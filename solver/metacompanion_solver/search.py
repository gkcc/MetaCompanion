from __future__ import annotations

import math
import random
import json
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Mapping, Sequence

from .counterplay import COUNTERPLAY_SCOPE, evaluate_counterplay
from .models import ActionPrior, HeuristicActionPrior, StateEvaluator
from .schemas import (
    Action,
    ActionKind,
    Annotation,
    GameState,
    Recommendation,
    SearchResult,
)
from .simulator import (
    SUPPORTED_EFFECTS,
    advance_to_start_of_turn,
    apply_action,
    enumerate_legal_actions,
    scan_state_coverage,
)


@dataclass(frozen=True)
class SearchLimits:
    time_budget_ms: int
    max_iterations: int
    max_depth: int
    top_k: int = 3
    exploration_constant: float = 1.35


@dataclass
class _Node:
    state: GameState
    parent: "_Node | None" = None
    action: Action | None = None
    prior: float = 1.0
    depth: int = 0
    ended: bool = False
    edge_annotations: tuple[Annotation, ...] = ()
    visits: int = 0
    value_sum: float = 0.0
    children: dict[str, "_Node"] = field(default_factory=dict)
    unexpanded: list[tuple[Action, float]] | None = None

    @property
    def mean_value(self) -> float:
        return self.value_sum / self.visits if self.visits else 0.5


@dataclass
class _LineStats:
    actions: tuple[Action, ...]
    visits: int = 0
    value_sum: float = 0.0
    annotations: dict[tuple[str, str, str], Annotation] = field(default_factory=dict)
    terminal_state_key: str = ""
    proof_kind: str = ""
    proof_scope: str = ""
    opponent_reply: tuple[Action, ...] = ()
    worst_case_score: float | None = None
    response_scope: str = ""
    response_search_complete: bool = False
    response_is_proven_lethal: bool = False
    response_nodes_expanded: int = 0
    response_searched_depth: int = 0
    response_transposition_hits: int = 0
    score_components: dict[str, float] = field(default_factory=dict)
    response_evaluated: bool = False

    @property
    def mean_value(self) -> float:
        return self.value_sum / self.visits if self.visits else 0.5

    @property
    def ranking_value(self) -> float:
        return self.mean_value if self.worst_case_score is None else self.worst_case_score

    @property
    def ordering_value(self) -> float:
        return self.score_components.get("minimax_value", self.ranking_value)

    @property
    def response_rank(self) -> int:
        """Conservative ordering tier for opponent-response evidence."""

        if self.response_is_proven_lethal:
            return 0
        if not self.response_evaluated:
            return 1
        if not self.response_search_complete:
            return 2
        return 3

    @property
    def is_proven_lethal(self) -> bool:
        return self.proof_kind == "modeled_lethal"


@dataclass(frozen=True)
class _LethalPlan:
    actions: tuple[Action, ...] = ()
    annotations: tuple[Annotation, ...] = ()
    terminal_state_key: str = ""
    nodes_expanded: int = 0
    transposition_hits: int = 0
    searched_depth: int = 0
    search_complete: bool = False


def _is_terminal(state: GameState) -> bool:
    return state.friendly.hero.current_health <= 0 or state.opponent.hero.current_health <= 0


def _deduplicate_annotations(items: Sequence[Annotation]) -> tuple[Annotation, ...]:
    unique: dict[tuple[str, str, str], Annotation] = {}
    for item in items:
        unique[(item.code, item.entity_id, item.detail)] = item
    return tuple(unique.values())


def _affects_rule_coverage(annotation: Annotation) -> bool:
    return annotation.code.startswith(
        ("unsupported", "approximate", "hidden", "multiple", "missing", "unknown")
    )


def _blocks_response_verification(annotation: Annotation) -> bool:
    # The turn boundary itself is deliberately scoped and deterministic for the
    # visible-combat subset. Every other rule/data gap invalidates a "verified"
    # response claim, even when the bounded graph search was structurally exhausted.
    if annotation.code in {"approximate_turn_refresh", "counterplay_scope"}:
        return False
    return _affects_rule_coverage(annotation)


def _playable_unsupported_annotations(state: GameState) -> tuple[Annotation, ...]:
    """Mark playable rules that force compatibility advice into best-effort mode.

    The generic simulator can still produce legal, useful routes when one available
    card has unparsed text.  That card can change their relative value, though, so the
    routes must remain partial and must not carry exact, response-verification, safety,
    or optimality claims.
    """

    actor = state.player(state.active_player_id)
    sources = [
        card
        for card in actor.hand
        if card.playable and card.cost <= actor.mana
    ]
    if (
        actor.hero_power
        and actor.hero_power_available
        and actor.hero_power.cost <= actor.mana
    ):
        sources.append(actor.hero_power)
    state_coverage = scan_state_coverage(state)
    annotations: list[Annotation] = []
    for card in sources:
        unsupported_effects = [
            effect
            for effect in card.effects
            if effect.kind not in SUPPORTED_EFFECTS or effect.random
        ]
        hard_mechanics = list(card.unsupported_effects)
        coverage_codes = sorted(
            {
                item.code
                for item in state_coverage
                if item.entity_id == card.entity_id and _affects_rule_coverage(item)
            }
        )
        if (
            card.effect_coverage != "unsupported"
            and not unsupported_effects
            and not hard_mechanics
            and not coverage_codes
        ):
            continue
        details = [
            *(
                ("coverage:unsupported",)
                if card.effect_coverage == "unsupported"
                else ()
            ),
            *(f"effect:{effect.kind}" for effect in unsupported_effects),
            *(f"mechanic:{mechanic}" for mechanic in hard_mechanics),
            *(f"coverage:{code}" for code in coverage_codes),
        ]
        annotations.append(
            Annotation(
                code="approximate_playable_unsupported_rule",
                detail=(
                    f"{card.name} has a currently playable unsupported rule "
                    f"({', '.join(dict.fromkeys(details))}); recommendations are "
                    "best-effort only and are not exact, response-verified, safe, "
                    "or optimal."
                ),
                entity_id=card.entity_id,
                severity="warning",
            )
        )
    return tuple(annotations)


def _state_key(state: GameState) -> str:
    payload = state.to_dict()
    for key in ("state_id", "patch", "mode", "rng_seed", "belief", "metadata"):
        payload.pop(key, None)
    return json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def _first_action_id(actions: Sequence[Action]) -> str:
    first = next((action for action in actions if action.kind != ActionKind.END_TURN), None)
    return first.action_id if first else "end_turn"


def _visible_response_pressure(terminal_state_key: str) -> tuple[int, float, int]:
    """Return a cheap, non-proving priority for counterplay candidate scheduling.

    Current-turn evaluation naturally favors face damage.  If response verification
    follows that order unchanged, a tight deadline can be spent proving several
    obviously counter-lethal continuations while a defensive continuation under the
    same root action remains unchecked.  The serialized terminal state is already
    available on every line, so prefer states with less visible ready-board pressure
    before falling back to the normal current-turn score.

    This is only a work-order hint.  Safety and ranking still come exclusively from
    the bounded response search; unknown effects are not treated as safe here.
    """

    try:
        payload = json.loads(terminal_state_key)
        players = [payload["friendly"], payload["opponent"]]
        perspective_id = str(payload["perspective_player_id"])
        responder_id = str(payload["active_player_id"])
        perspective = next(
            player for player in players if str(player["player_id"]) == perspective_id
        )
        responder = next(
            player for player in players if str(player["player_id"]) == responder_id
        )

        visible_attack = 0.0
        visible_attackers = 0
        for card in responder.get("board", []):
            if float(card.get("current_health", 0)) <= 0 or bool(card.get("dormant", False)):
                continue
            attack = max(0.0, float(card.get("attack", 0)))
            if attack <= 0:
                continue
            visible_attack += attack
            visible_attackers += 1

        weapon = responder.get("weapon")
        if isinstance(weapon, Mapping) and float(weapon.get("current_durability", 0)) > 0:
            weapon_attack = max(0.0, float(weapon.get("attack", 0)))
            if weapon_attack > 0:
                visible_attack += weapon_attack
                visible_attackers += 1

        hero = perspective["hero"]
        effective_health = max(0.0, float(hero.get("current_health", 0))) + max(
            0.0,
            float(perspective.get("armor", 0)),
        )
        has_taunt = any(
            float(card.get("current_health", 0)) > 0
            and bool(card.get("taunt", False))
            and not bool(card.get("stealth", False))
            for card in perspective.get("board", [])
        )
        immediate_board_lethal = int(
            not has_taunt
            and effective_health > 0
            and visible_attack >= effective_health
        )
        return immediate_board_lethal, visible_attack, visible_attackers
    except (KeyError, StopIteration, TypeError, ValueError, json.JSONDecodeError):
        # Internal state keys should always parse.  If one does not, schedule it last
        # and let the normal replay/verification path report the actual problem.
        return 1, math.inf, 2**31 - 1


class PuctTurnSearcher:
    """Time-bounded PUCT search over the currently active player's remaining turn.

    The tree is only as accurate as the generic simulator. Unsupported effects are
    retained on each candidate line so a UI cannot mistake an approximation for an
    exact Hearthstone rules result.
    """

    def __init__(
        self,
        prior: ActionPrior | None = None,
        evaluator: StateEvaluator | None = None,
    ) -> None:
        self.prior = prior or HeuristicActionPrior()
        self.evaluator = evaluator or StateEvaluator()

    @staticmethod
    def _lethal_action_order(state: GameState, action: Action) -> tuple[int, int, str]:
        enemy = state.other_player(state.active_player_id)
        targets_hero = action.target_entity_id == enemy.hero.entity_id
        kind_order = {
            ActionKind.ATTACK: 0,
            ActionKind.HERO_POWER: 1,
            ActionKind.PLAY_CARD: 2,
        }.get(action.kind, 3)
        return (0 if targets_hero else 1, kind_order, action.action_id)

    def _find_modeled_lethal(
        self,
        state: GameState,
        limits: SearchLimits,
        cancel_event: threading.Event,
        deadline: float,
    ) -> _LethalPlan:
        """Find a shortest lethal in the deterministic visible generic model.

        This is deliberately a scoped model proof, not a claim about hidden cards,
        secrets, or unsupported triggers. Iterative deepening makes the first result
        stable for a fixed state, while the node/depth/deadline checks keep it bounded.
        """

        enemy_id = state.other_player(state.active_player_id).player_id
        node_budget = max(1, min(2048, limits.max_iterations))
        nodes_expanded = 0
        transposition_hits = 0
        searched_depth = 0
        stopped_early = False

        def should_stop() -> bool:
            return (
                cancel_event.is_set()
                or time.monotonic() >= deadline
                or nodes_expanded >= node_budget
            )

        def dfs(
            current: GameState,
            remaining: int,
            actions: list[Action],
            annotations: list[Annotation],
            seen: dict[str, int],
        ) -> tuple[tuple[Action, ...], tuple[Annotation, ...], str] | None:
            nonlocal nodes_expanded, transposition_hits, stopped_early
            if should_stop():
                stopped_early = True
                return None
            if current.player(enemy_id).hero.current_health <= 0:
                clean_annotations = _deduplicate_annotations(annotations)
                if any(_affects_rule_coverage(item) for item in clean_annotations):
                    return None
                return tuple(actions), clean_annotations, _state_key(current)
            if remaining <= 0:
                return None

            key = _state_key(current)
            previous_remaining = seen.get(key)
            if previous_remaining is not None and previous_remaining >= remaining:
                transposition_hits += 1
                return None
            seen[key] = remaining

            legal = [
                action
                for action in enumerate_legal_actions(current)
                if action.kind != ActionKind.END_TURN
            ]
            legal.sort(key=lambda action: self._lethal_action_order(current, action))
            for action in legal:
                if should_stop():
                    stopped_early = True
                    return None
                outcome = apply_action(current, action)
                nodes_expanded += 1
                next_actions = [*actions, action]
                next_annotations = [*annotations, *outcome.annotations]
                if outcome.state.player(enemy_id).hero.current_health <= 0:
                    clean_annotations = _deduplicate_annotations(next_annotations)
                    if not any(_affects_rule_coverage(item) for item in clean_annotations):
                        return (
                            tuple(next_actions),
                            clean_annotations,
                            _state_key(outcome.state),
                        )
                    continue
                found = dfs(
                    outcome.state,
                    remaining - 1,
                    next_actions,
                    next_annotations,
                    seen,
                )
                if found:
                    return found
            return None

        found: tuple[tuple[Action, ...], tuple[Annotation, ...], str] | None = None
        for depth in range(1, limits.max_depth + 1):
            if should_stop():
                stopped_early = True
                break
            searched_depth = depth
            found = dfs(state, depth, [], [], {})
            if found:
                break

        if found:
            actions, annotations, terminal_state_key = found
            scope_annotation = Annotation(
                code="modeled_lethal_scope",
                detail=(
                    "Lethal is proven only in the visible generic-v2 combat model; "
                    "hidden information and unsupported triggers remain outside this proof."
                ),
                severity="warning",
            )
            return _LethalPlan(
                actions=actions,
                annotations=_deduplicate_annotations([*annotations, scope_annotation]),
                terminal_state_key=terminal_state_key,
                nodes_expanded=nodes_expanded,
                transposition_hits=transposition_hits,
                searched_depth=searched_depth,
                search_complete=False,
            )
        return _LethalPlan(
            nodes_expanded=nodes_expanded,
            transposition_hits=transposition_hits,
            searched_depth=searched_depth,
            search_complete=not stopped_early and searched_depth >= limits.max_depth,
        )

    def _initialize_node(self, node: _Node) -> None:
        if node.unexpanded is not None:
            return
        if node.ended or _is_terminal(node.state):
            node.unexpanded = []
            return
        actions = enumerate_legal_actions(node.state)
        priors = self.prior.probabilities(node.state, actions)
        node.unexpanded = sorted(
            [(action, max(0.000001, float(priors.get(action.action_id, 0.0)))) for action in actions],
            key=lambda item: (-item[1], item[0].action_id),
        )

    @staticmethod
    def _select_child(node: _Node, exploration: float) -> _Node:
        parent_visits = max(1, node.visits)

        def score(child: _Node) -> tuple[float, str]:
            bonus = exploration * child.prior * math.sqrt(parent_visits) / (1 + child.visits)
            return child.mean_value + bonus, child.action.action_id if child.action else ""

        return max(node.children.values(), key=score)

    def _weighted_action(
        self,
        state: GameState,
        actions: Sequence[Action],
        rng: random.Random,
    ) -> Action:
        priors = self.prior.probabilities(state, actions)
        # The policy prior intentionally remains stochastic during rollouts. A fixed
        # state rng_seed still makes the entire solve reproducible by iteration count.
        total = sum(max(0.0, float(priors.get(action.action_id, 0.0))) for action in actions)
        if total <= 0:
            return actions[0]
        threshold = rng.random() * total
        cumulative = 0.0
        for action in actions:
            cumulative += max(0.0, float(priors.get(action.action_id, 0.0)))
            if cumulative >= threshold:
                return action
        return actions[-1]

    def _rollout(
        self,
        node: _Node,
        limits: SearchLimits,
        rng: random.Random,
        cancel_event: threading.Event,
        deadline: float,
    ) -> tuple[list[Action], list[Annotation], GameState]:
        state = node.state
        ended = node.ended
        actions: list[Action] = []
        annotations: list[Annotation] = []
        depth = node.depth
        while (
            not ended
            and not _is_terminal(state)
            and depth < limits.max_depth
            and not cancel_event.is_set()
            and time.monotonic() < deadline
        ):
            legal = enumerate_legal_actions(state)
            if not legal:
                break
            action = self._weighted_action(state, legal, rng)
            outcome = apply_action(state, action)
            actions.append(action)
            annotations.extend(outcome.annotations)
            state = outcome.state
            ended = outcome.ended_turn
            depth += 1

        if not ended and not _is_terminal(state):
            end_turn = next(
                (item for item in enumerate_legal_actions(state) if item.kind == ActionKind.END_TURN),
                None,
            )
            if end_turn:
                outcome = apply_action(state, end_turn)
                actions.append(end_turn)
                annotations.extend(outcome.annotations)
                state = outcome.state
        return actions, annotations, state

    @staticmethod
    def _path_actions_and_annotations(node: _Node) -> tuple[list[Action], list[Annotation]]:
        actions: list[Action] = []
        annotations: list[Annotation] = []
        cursor: _Node | None = node
        while cursor and cursor.parent is not None:
            if cursor.action:
                actions.append(cursor.action)
            annotations.extend(cursor.edge_annotations)
            cursor = cursor.parent
        actions.reverse()
        annotations.reverse()
        return actions, annotations

    @staticmethod
    def _replay_line(
        state: GameState,
        actions: Sequence[Action],
    ) -> tuple[GameState, tuple[Annotation, ...], bool]:
        current = state
        annotations: list[Annotation] = []
        ended_turn = False
        for action in actions:
            outcome = apply_action(current, action)
            current = outcome.state
            annotations.extend(outcome.annotations)
            ended_turn = outcome.ended_turn
            if ended_turn or _is_terminal(current):
                break
        return current, _deduplicate_annotations(annotations), ended_turn

    def _score_components(
        self,
        state: GameState,
        perspective_player_id: str,
        fallback_value: float,
    ) -> dict[str, float]:
        component_evaluator = getattr(self.evaluator, "evaluate_components", None)
        if not callable(component_evaluator):
            return {"tactical_state_value": fallback_value}
        raw = component_evaluator(state, perspective_player_id)
        if not isinstance(raw, Mapping):
            return {"tactical_state_value": fallback_value}
        components = {
            str(key): float(value)
            for key, value in raw.items()
            if isinstance(value, (int, float))
            and not isinstance(value, bool)
            and math.isfinite(float(value))
        }
        components["tactical_state_value"] = fallback_value
        return components

    def _response_score_components(
        self,
        state_after_end_turn: GameState,
        response_actions: Sequence[Action],
        perspective_player_id: str,
        fallback_value: float,
    ) -> dict[str, float]:
        completed_player_id = state_after_end_turn.other_player(
            state_after_end_turn.active_player_id
        ).player_id
        boundary = advance_to_start_of_turn(state_after_end_turn)
        current = boundary.state
        current.player(completed_player_id).mana = 0
        for action in response_actions:
            completed_player_id = current.active_player_id
            outcome = apply_action(current, action)
            current = outcome.state
            if outcome.ended_turn:
                current.player(completed_player_id).mana = 0
        return self._score_components(
            current,
            perspective_player_id,
            fallback_value,
        )

    def _assess_counterplay(
        self,
        state: GameState,
        line_stats: Mapping[tuple[str, ...], _LineStats],
        limits: SearchLimits,
        cancel_event: threading.Event,
        deadline: float,
        legal_first_action_ids: Sequence[str] | None = None,
    ) -> dict[str, Any]:
        """Attach one bounded visible opponent response to each non-lethal line.

        Candidate generation remains the existing current-turn PUCT search. This
        deterministic post-pass changes ranking only after a complete END_TURN line
        exists; it never weakens the modeled-lethal planner's precedence.
        """

        max_depth = max(1, min(12, limits.max_depth))
        node_budget = max(1, min(4096, limits.max_iterations))
        nodes_remaining = node_budget
        assessed = 0
        complete = 0
        structurally_complete = 0
        skipped = 0
        modeled_counter_lethals = 0
        stop_reasons: dict[str, int] = {}
        legal_first_actions = set(
            legal_first_action_ids
            if legal_first_action_ids is not None
            else (
                _first_action_id((action,))
                for action in enumerate_legal_actions(state)
            )
        )
        generated_first_actions = {
            _first_action_id(item.actions) for item in line_stats.values()
        }
        proven_lethal_first_actions = {
            _first_action_id(item.actions)
            for item in line_stats.values()
            if item.is_proven_lethal
        }
        assessed_first_actions: set[str] = set(proven_lethal_first_actions)
        structurally_complete_first_actions: set[str] = set(
            proven_lethal_first_actions
        )
        response_verified_first_actions: set[str] = set(
            proven_lethal_first_actions
        )

        all_candidates = sorted(
            (item for item in line_stats.values() if not item.is_proven_lethal),
            key=lambda item: (
                item.mean_value,
                item.visits,
                tuple(action.action_id for action in item.actions),
            ),
            reverse=True,
        )
        # Counterplay coverage must not be coupled to the number of lines rendered
        # to the user. A superficially attractive first turn can otherwise consume
        # every response slot while the only safe first action is never assessed.
        # Keep one representative per (first action, terminal state), then visit
        # every generated first-action group round-robin before taking a second
        # continuation from any group.
        groups: dict[str, list[_LineStats]] = {}
        unique_candidates: list[_LineStats] = []
        seen_first_action_terminals: set[tuple[str, str]] = set()
        for item in all_candidates:
            first_action = _first_action_id(item.actions)
            terminal_identity = item.terminal_state_key or "|".join(
                action.action_id for action in item.actions
            )
            identity = (first_action, terminal_identity)
            if identity in seen_first_action_terminals:
                continue
            seen_first_action_terminals.add(identity)
            groups.setdefault(first_action, []).append(item)
            unique_candidates.append(item)
        for group in groups.values():
            group.sort(
                key=lambda item: (
                    *_visible_response_pressure(item.terminal_state_key),
                    -item.mean_value,
                    -item.visits,
                    tuple(action.action_id for action in item.actions),
                )
            )
        group_order = list(groups)
        shortlist_limit = min(node_budget, len(unique_candidates))
        candidates: list[_LineStats] = []
        round_index = 0
        while len(candidates) < shortlist_limit:
            added = False
            for first_action in group_order:
                group = groups[first_action]
                if round_index < len(group):
                    candidates.append(group[round_index])
                    added = True
                    if len(candidates) >= shortlist_limit:
                        break
            if not added:
                break
            round_index += 1
        shortlisted_first_actions = {
            _first_action_id(item.actions) for item in candidates
        }
        per_line_node_budget = max(1, node_budget // max(1, len(candidates)))
        maximum_line_node_budget = per_line_node_budget
        first_pass_candidate_count = min(len(group_order), len(candidates))

        for candidate_index, stats in enumerate(candidates):
            terminal_state, replay_annotations, ended_turn = self._replay_line(
                state,
                stats.actions,
            )
            for annotation in replay_annotations:
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation

            if _is_terminal(terminal_state):
                # A terminal line found outside the dedicated lethal proof still has
                # no legal opponent response, but it is not promoted to a proof.
                value = self.evaluator.evaluate(
                    terminal_state,
                    state.perspective_player_id,
                )
                stats.worst_case_score = value
                stats.score_components = self._score_components(
                    terminal_state,
                    state.perspective_player_id,
                    value,
                )
                stats.response_scope = COUNTERPLAY_SCOPE
                terminal_verified = not any(
                    _blocks_response_verification(annotation)
                    for annotation in replay_annotations
                )
                stats.response_search_complete = terminal_verified
                stats.response_is_proven_lethal = bool(
                    terminal_state.player(state.perspective_player_id).hero.current_health <= 0
                )
                stats.response_evaluated = True
                first_action = _first_action_id(stats.actions)
                assessed_first_actions.add(first_action)
                assessed += 1
                structurally_complete += 1
                structurally_complete_first_actions.add(first_action)
                if terminal_verified:
                    complete += 1
                    response_verified_first_actions.add(first_action)
                else:
                    annotation = Annotation(
                        code="unsupported_counterplay_verification",
                        detail=(
                            "The terminal state depends on unsupported or unknown "
                            "mechanics, so no verified response claim is made."
                        ),
                        severity="warning",
                    )
                    stats.annotations[
                        (annotation.code, annotation.entity_id, annotation.detail)
                    ] = annotation
                continue

            if not ended_turn or terminal_state.active_player_id == state.perspective_player_id:
                annotation = Annotation(
                    code="unsupported_counterplay_incomplete_candidate",
                    detail=(
                        "Counterplay was not evaluated because the candidate did not "
                        "end the perspective player's turn."
                    ),
                    severity="warning",
                )
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
                stats.response_scope = COUNTERPLAY_SCOPE
                skipped += 1
                stop_reasons["incomplete_candidate"] = (
                    stop_reasons.get("incomplete_candidate", 0) + 1
                )
                continue

            if cancel_event.is_set():
                annotation = Annotation(
                    code="counterplay_cancelled",
                    detail="Counterplay was not evaluated because the solve was cancelled.",
                    severity="warning",
                )
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
                stats.response_scope = COUNTERPLAY_SCOPE
                skipped += 1
                stop_reasons["cancelled"] = stop_reasons.get("cancelled", 0) + 1
                continue
            if time.monotonic() >= deadline:
                annotation = Annotation(
                    code="approximate_counterplay_deadline_reached",
                    detail=(
                        "No opponent response was attached because the bounded "
                        "counterplay deadline was exhausted."
                    ),
                    severity="warning",
                )
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
                stats.response_scope = COUNTERPLAY_SCOPE
                skipped += 1
                stop_reasons["deadline"] = stop_reasons.get("deadline", 0) + 1
                continue
            if nodes_remaining <= 0:
                annotation = Annotation(
                    code="approximate_counterplay_node_budget_exhausted",
                    detail=(
                        "No opponent response was attached because the shared "
                        "counterplay node budget was exhausted."
                    ),
                    severity="warning",
                )
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
                stats.response_scope = COUNTERPLAY_SCOPE
                skipped += 1
                stop_reasons["node_limit"] = stop_reasons.get("node_limit", 0) + 1
                continue

            remaining_candidate_count = max(1, len(candidates) - candidate_index)
            line_node_budget = max(1, nodes_remaining // remaining_candidate_count)
            maximum_line_node_budget = max(maximum_line_node_budget, line_node_budget)
            line_deadline = deadline
            if candidate_index < first_pass_candidate_count:
                # Divide the remaining wall-clock window among the as-yet-unseen
                # first actions. A complex response for the first heuristic group
                # must not prevent later root actions from receiving any safety scan.
                line_started = time.monotonic()
                remaining_first_actions = max(
                    1,
                    first_pass_candidate_count - candidate_index,
                )
                line_deadline = min(
                    deadline,
                    line_started
                    + max(0.0, deadline - line_started) / remaining_first_actions,
                )
            result = evaluate_counterplay(
                terminal_state,
                evaluator=self.evaluator,
                deadline=line_deadline,
                cancel_event=cancel_event,
                max_nodes=min(nodes_remaining, line_node_budget),
                max_depth=max_depth,
                perspective_player_id=state.perspective_player_id,
            )
            nodes_remaining = max(0, nodes_remaining - result.nodes_expanded)
            verification_blockers = [
                annotation
                for annotation in [*replay_annotations, *result.annotations]
                if _blocks_response_verification(annotation)
            ]
            response_verified = result.search_complete and not verification_blockers
            stats.opponent_reply = result.response_actions
            stats.worst_case_score = result.worst_case_value
            stats.response_scope = result.scope
            stats.response_search_complete = response_verified
            stats.response_is_proven_lethal = result.modeled_counter_lethal
            stats.response_nodes_expanded = result.nodes_expanded
            stats.response_searched_depth = result.searched_depth
            stats.response_transposition_hits = result.transposition_hits
            stats.response_evaluated = True
            first_action = _first_action_id(stats.actions)
            assessed_first_actions.add(first_action)
            stats.score_components = self._response_score_components(
                terminal_state,
                result.response_actions,
                state.perspective_player_id,
                result.worst_case_value,
            )
            for annotation in result.annotations:
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
            if result.search_complete and verification_blockers:
                annotation = Annotation(
                    code="unsupported_counterplay_verification",
                    detail=(
                        "The bounded response graph was exhausted, but unsupported or "
                        "unknown mechanics prevent a verified best-response claim."
                    ),
                    severity="warning",
                )
                stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
            assessed += 1
            if response_verified:
                complete += 1
                response_verified_first_actions.add(first_action)
            if result.search_complete:
                structurally_complete += 1
                structurally_complete_first_actions.add(first_action)
            if result.modeled_counter_lethal:
                modeled_counter_lethals += 1
            if result.stop_reason:
                stop_reasons[result.stop_reason] = (
                    stop_reasons.get(result.stop_reason, 0) + 1
                )

        unassessed_unique_line_count = max(0, len(unique_candidates) - assessed)
        missing_generated_first_actions = (
            legal_first_actions - generated_first_actions
        )
        missing_response_verified_first_actions = (
            legal_first_actions - response_verified_first_actions
        )
        unassessed_first_actions = legal_first_actions - assessed_first_actions
        legal_first_action_count = len(legal_first_actions)
        root_action_generation_coverage_rate = (
            len(legal_first_actions & generated_first_actions)
            / legal_first_action_count
            if legal_first_action_count
            else 1.0
        )
        root_action_response_coverage_rate = (
            len(legal_first_actions & response_verified_first_actions)
            / legal_first_action_count
            if legal_first_action_count
            else 1.0
        )
        root_action_coverage_complete = bool(
            not missing_generated_first_actions
            and not missing_response_verified_first_actions
        )
        return {
            "planner_model": "counterplay-turnpair-v1",
            "portfolio_model": "root-action-portfolio-v1",
            # Root coverage proves that every legal first action received at
            # least one verified line. Bounded PUCT does not exhaust every
            # friendly continuation below those roots, so it cannot prove the
            # minimax-optimal portfolio.
            "portfolio_optimality_proven": False,
            "response_scope": COUNTERPLAY_SCOPE,
            "score_kind": "counterplay_tactical_state_value",
            "seed": state.rng_seed,
            "deterministic_for_fixed_state_and_limits": True,
            "candidate_line_count": len(all_candidates),
            "unique_candidate_line_count": len(unique_candidates),
            "deduplicated_candidate_line_count": len(all_candidates) - len(unique_candidates),
            "shortlisted_line_count": len(candidates),
            "shortlisted_first_action_count": len(shortlisted_first_actions),
            "legal_first_action_count": legal_first_action_count,
            "legal_first_action_ids": sorted(legal_first_actions),
            "generated_first_action_count": len(generated_first_actions),
            "generated_first_action_ids": sorted(generated_first_actions),
            "assessed_first_action_count": len(assessed_first_actions),
            "structurally_complete_first_action_count": len(
                structurally_complete_first_actions
            ),
            "response_verified_first_action_count": len(
                response_verified_first_actions
            ),
            "response_verified_first_action_ids": sorted(
                response_verified_first_actions
            ),
            "unassessed_first_action_count": len(unassessed_first_actions),
            "missing_first_action_ids": sorted(
                missing_response_verified_first_actions
            ),
            "missing_generated_first_action_ids": sorted(
                missing_generated_first_actions
            ),
            "missing_response_verified_first_action_ids": sorted(
                missing_response_verified_first_actions
            ),
            "root_action_generation_coverage_rate": round(
                root_action_generation_coverage_rate,
                6,
            ),
            "root_action_response_coverage_rate": round(
                root_action_response_coverage_rate,
                6,
            ),
            "root_action_coverage_complete": root_action_coverage_complete,
            "unassessed_line_count": max(0, len(all_candidates) - assessed),
            "unassessed_unique_line_count": unassessed_unique_line_count,
            "assessed_line_count": assessed,
            "complete_response_count": complete,
            "structurally_complete_response_count": structurally_complete,
            "skipped_line_count": skipped,
            "modeled_counter_lethal_count": modeled_counter_lethals,
            "node_budget": node_budget,
            "per_line_node_budget": per_line_node_budget,
            "maximum_line_node_budget": maximum_line_node_budget,
            "nodes_expanded": node_budget - nodes_remaining,
            "max_depth": max_depth,
            "search_complete": (
                structurally_complete == len(unique_candidates)
                and root_action_coverage_complete
            ),
            "stop_reasons": dict(sorted(stop_reasons.items())),
        }

    @staticmethod
    def _record_progress(
        line_stats: Mapping[tuple[str, ...], _LineStats],
        elapsed_ms: int,
        iterations: int,
        fraction: float,
    ) -> dict[str, Any]:
        leaders = sorted(
            line_stats.values(),
            key=lambda item: (item.ranking_value, item.visits),
            reverse=True,
        )[:3]
        return {
            "elapsed_ms": elapsed_ms,
            "iterations": iterations,
            "fraction": round(min(1.0, max(0.0, fraction)), 6),
            "top_expected_win_rates": [round(item.ranking_value, 6) for item in leaders],
        }

    @staticmethod
    def _rationale(
        state: GameState,
        actions: Sequence[Action],
        annotations: Sequence[Annotation],
        *,
        response_evaluated: bool = False,
        response_is_proven_lethal: bool = False,
    ) -> str:
        first = next((action for action in actions if action.kind != ActionKind.END_TURN), None)
        if first is None:
            text = "End the turn without another modeled action."
        elif first.kind == ActionKind.ATTACK:
            enemy = state.other_player(state.active_player_id)
            text = (
                "Start by attacking the opposing hero."
                if first.target_entity_id == enemy.hero.entity_id
                else "Start with the listed trade."
            )
        elif first.kind == ActionKind.PLAY_CARD:
            text = "Start by playing the listed card, then follow the modeled sequence."
        else:
            text = "Start with the hero power, then follow the modeled sequence."
        if response_is_proven_lethal:
            text += " The visible opponent response contains a modeled counter-lethal."
        elif response_evaluated:
            text += " It is ranked by the worst bounded visible opponent response found."
        else:
            text += " No opponent response was evaluated for this fallback line."
        if annotations:
            text += " This line contains approximated mechanics; review its warnings."
        return text

    def _recommendations(
        self,
        state: GameState,
        line_stats: Mapping[tuple[str, ...], _LineStats],
        top_k: int,
        root_action_coverage_complete: bool = False,
        portfolio_optimality_proven: bool = False,
        approximation_annotations: Sequence[Annotation] = (),
    ) -> tuple[Recommendation, ...]:
        claims_allowed = not approximation_annotations
        assessed = [
            item
            for item in line_stats.values()
            if item.is_proven_lethal or item.response_evaluated
        ]
        verified = (
            [
                item
                for item in assessed
                if item.is_proven_lethal
                or (item.response_evaluated and item.response_search_complete)
            ]
            if claims_allowed
            else []
        )
        verified_non_counterlethal = [
            item
            for item in verified
            if item.is_proven_lethal
            or not item.response_is_proven_lethal
        ]
        # A deadline-limited line that has not found a counter-lethal is not thereby
        # safe.  Keep incomplete response searches out of the normal recommendation
        # pool: returning fewer than top_k is safer than padding the list with an
        # unverified line.  Cancellation or a zero-time solve can leave no verified
        # candidate at all; in that exceptional case retain one explicit fallback so
        # callers still receive a useful, clearly unverified baseline.
        recommendation_pool = (
            verified_non_counterlethal
            or verified
            or assessed
            or list(line_stats.values())
        )
        recommendation_limit = top_k if verified else 1
        ordered = sorted(
            recommendation_pool,
            key=lambda item: (
                item.is_proven_lethal,
                item.response_rank,
                item.ordering_value,
                item.visits,
                tuple(a.action_id for a in item.actions),
            ),
            reverse=True,
        )
        # A portfolio alternative is a root decision, not a different continuation
        # of the same root decision. Since ``ordered`` is best-first, retaining its
        # first entry for each root action also keeps that root's best verified line.
        portfolio_winners: list[_LineStats] = []
        seen_first_actions: set[str] = set()
        for item in ordered:
            first_action = _first_action_id(item.actions)
            if first_action in seen_first_actions:
                continue
            seen_first_actions.add(first_action)
            portfolio_winners.append(item)
        best_verified_value = (
            max(item.ordering_value for item in portfolio_winners)
            if verified and portfolio_winners
            else None
        )
        co_optimal_winners = (
            [
                item
                for item in portfolio_winners
                if math.isclose(
                    item.ordering_value,
                    best_verified_value,
                    abs_tol=1e-9,
                )
            ]
            if best_verified_value is not None
            else []
        )
        # Once two or more distinct, fully verified roots tie for the best value,
        # those are already the useful portfolio. Do not pad Top-K with a clearly
        # inferior root merely to fill a display slot. With only one best root,
        # explicit near-optimal/backup choices remain useful and retain their regret.
        leaders = (
            co_optimal_winners[:recommendation_limit]
            if root_action_coverage_complete and len(co_optimal_winners) >= 2
            else portfolio_winners[:recommendation_limit]
        )
        near_optimal_regret_threshold = 100.0
        recommendations: list[Recommendation] = []
        for rank, stats in enumerate(leaders, start=1):
            probability = min(1.0, max(0.0, stats.ranking_value))
            standard_error = math.sqrt(max(0.000001, probability * (1 - probability)) / max(1, stats.visits))
            margin = max(0.08, min(0.49, 1.96 * standard_error))
            interval = (max(0.0, probability - margin), min(1.0, probability + margin))
            annotations = _deduplicate_annotations(
                [*stats.annotations.values(), *approximation_annotations]
            )
            verified_portfolio_regret = (
                max(0.0, best_verified_value - stats.ordering_value)
                if best_verified_value is not None
                else None
            )
            if verified_portfolio_regret is None:
                alternative_kind = "fallback"
            elif math.isclose(verified_portfolio_regret, 0.0, abs_tol=1e-9):
                alternative_kind = (
                    "co_optimal"
                    if (
                        root_action_coverage_complete
                        and portfolio_optimality_proven
                    )
                    else "best_found"
                )
            elif (
                root_action_coverage_complete
                and portfolio_optimality_proven
                and verified_portfolio_regret <= near_optimal_regret_threshold
            ):
                alternative_kind = "near_optimal"
            else:
                alternative_kind = "backup"
            recommendations.append(
                Recommendation(
                    rank=rank,
                    actions=stats.actions,
                    expected_win_probability=probability,
                    confidence_interval=interval,
                    visits=stats.visits,
                    rationale=self._rationale(
                        state,
                        stats.actions,
                        annotations,
                        response_evaluated=stats.response_evaluated,
                        response_is_proven_lethal=stats.response_is_proven_lethal,
                    ),
                    annotations=annotations,
                    proof_kind=stats.proof_kind if claims_allowed else "",
                    proof_scope=stats.proof_scope if claims_allowed else "",
                    is_proven_lethal=stats.is_proven_lethal if claims_allowed else False,
                    opponent_reply=stats.opponent_reply if claims_allowed else (),
                    worst_case_score=stats.worst_case_score if claims_allowed else None,
                    response_scope=stats.response_scope if claims_allowed else "",
                    response_search_complete=(
                        stats.response_search_complete if claims_allowed else False
                    ),
                    response_is_proven_lethal=(
                        stats.response_is_proven_lethal if claims_allowed else False
                    ),
                    response_nodes_expanded=(
                        stats.response_nodes_expanded if claims_allowed else 0
                    ),
                    response_searched_depth=(
                        stats.response_searched_depth if claims_allowed else 0
                    ),
                    response_transposition_hits=(
                        stats.response_transposition_hits if claims_allowed else 0
                    ),
                    score_components=(
                        dict(stats.score_components) if claims_allowed else {}
                    ),
                    verified_portfolio_regret=(
                        verified_portfolio_regret if claims_allowed else None
                    ),
                    alternative_kind=alternative_kind if claims_allowed else "fallback",
                )
            )
        return tuple(recommendations)

    def search(
        self,
        request_id: str,
        state: GameState,
        limits: SearchLimits,
        cancel_event: threading.Event | None = None,
        approximation_annotations: Sequence[Annotation] = (),
    ) -> SearchResult:
        cancel_event = cancel_event or threading.Event()
        started = time.monotonic()
        deadline = started + limits.time_budget_ms / 1000.0
        # Keep the deterministic lethal proof from consuming the entire current-turn
        # planning share on a wide non-lethal board. Tight solves still need time to
        # generate at least one complete continuation for each root action before the
        # opponent-response post-pass.
        lethal_deadline = started + (limits.time_budget_ms / 1000.0) * 0.3
        turn_deadline = started + (limits.time_budget_ms / 1000.0) * 0.7
        rng = random.Random(state.rng_seed)
        root_legal_actions = tuple(enumerate_legal_actions(state))
        legal_first_action_ids = tuple(
            sorted(
                {
                    _first_action_id((action,))
                    for action in root_legal_actions
                }
            )
        )
        root = _Node(state=state)
        line_stats: dict[tuple[str, ...], _LineStats] = {}
        progress: list[dict[str, Any]] = []
        milestones = [0.25, 0.5, 0.75]
        iterations = 0

        lethal_plan = self._find_modeled_lethal(
            state,
            limits,
            cancel_event,
            lethal_deadline,
        )
        if lethal_plan.actions:
            lethal_key = tuple(action.action_id for action in lethal_plan.actions)
            lethal_terminal_state, _, _ = self._replay_line(
                state,
                lethal_plan.actions,
            )
            lethal_stats = _LineStats(
                actions=lethal_plan.actions,
                visits=1,
                value_sum=1.0,
                terminal_state_key=lethal_plan.terminal_state_key,
                proof_kind="modeled_lethal",
                proof_scope="visible_generic_v2",
                opponent_reply=(),
                worst_case_score=1.0,
                response_scope=COUNTERPLAY_SCOPE,
                response_search_complete=True,
                response_is_proven_lethal=False,
                score_components=self._score_components(
                    lethal_terminal_state,
                    state.perspective_player_id,
                    1.0,
                ),
                response_evaluated=True,
            )
            for annotation in lethal_plan.annotations:
                lethal_stats.annotations[
                    (annotation.code, annotation.entity_id, annotation.detail)
                ] = annotation
            line_stats[lethal_key] = lethal_stats

        def record_iteration(node: _Node, path: Sequence[_Node]) -> None:
            nonlocal iterations

            prefix_actions, prefix_annotations = self._path_actions_and_annotations(node)
            suffix_actions, suffix_annotations, terminal_state = self._rollout(
                node,
                limits,
                rng,
                cancel_event,
                turn_deadline,
            )
            actions = tuple(prefix_actions + suffix_actions)
            annotations = _deduplicate_annotations(
                prefix_annotations + suffix_annotations
            )
            value = self.evaluator.evaluate(
                terminal_state,
                state.perspective_player_id,
            )

            for visited in path:
                visited.visits += 1
                visited.value_sum += value

            line_complete = _is_terminal(terminal_state) or bool(
                actions and actions[-1].kind == ActionKind.END_TURN
            )
            if line_complete:
                key = tuple(action.action_id for action in actions)
                stats = line_stats.setdefault(
                    key,
                    _LineStats(
                        actions=actions,
                        terminal_state_key=_state_key(terminal_state),
                    ),
                )
                stats.visits += 1
                stats.value_sum += value
                for annotation in annotations:
                    stats.annotations[
                        (annotation.code, annotation.entity_id, annotation.detail)
                    ] = annotation

            iterations += 1
            elapsed_fraction = (time.monotonic() - started) / max(
                0.001,
                limits.time_budget_ms / 1000.0,
            )
            if milestones and elapsed_fraction >= milestones[0]:
                milestone = milestones.pop(0)
                progress.append(
                    self._record_progress(
                        line_stats,
                        int((time.monotonic() - started) * 1000),
                        iterations,
                        milestone,
                    )
                )

        # Seed the root portfolio before PUCT exploitation. Root priors still guide
        # later visits, while a stable action-id pass prevents a low-prior legal root
        # from being invisible merely because higher-prior continuations expanded first.
        self._initialize_node(root)
        root_seed_actions = sorted(
            tuple(root.unexpanded or ()),
            key=lambda item: item[0].action_id,
        )
        for action, prior in root_seed_actions:
            if (
                iterations >= limits.max_iterations
                or cancel_event.is_set()
                or time.monotonic() >= turn_deadline
            ):
                break
            root.unexpanded = [
                item
                for item in (root.unexpanded or [])
                if item[0].action_id != action.action_id
            ]
            outcome = apply_action(root.state, action)
            child = _Node(
                state=outcome.state,
                parent=root,
                action=action,
                prior=prior,
                depth=1,
                ended=outcome.ended_turn,
                edge_annotations=outcome.annotations,
            )
            root.children[action.action_id] = child
            record_iteration(child, (root, child))

        while iterations < limits.max_iterations and time.monotonic() < turn_deadline:
            if cancel_event.is_set():
                break
            node = root
            path = [root]
            self._initialize_node(node)

            while (
                not node.ended
                and not _is_terminal(node.state)
                and node.depth < limits.max_depth
                and not cancel_event.is_set()
                and time.monotonic() < turn_deadline
            ):
                self._initialize_node(node)
                if node.unexpanded:
                    action, prior = node.unexpanded.pop(0)
                    outcome = apply_action(node.state, action)
                    child = _Node(
                        state=outcome.state,
                        parent=node,
                        action=action,
                        prior=prior,
                        depth=node.depth + 1,
                        ended=outcome.ended_turn,
                        edge_annotations=outcome.annotations,
                    )
                    node.children[action.action_id] = child
                    node = child
                    path.append(node)
                    break
                if not node.children:
                    break
                node = self._select_child(node, limits.exploration_constant)
                path.append(node)
            record_iteration(node, path)

        # A cancellation before the first rollout still gets a safe, explicit baseline.
        if not line_stats:
            end_turn = next(
                (
                    action
                    for action in root_legal_actions
                    if action.kind == ActionKind.END_TURN
                ),
                None,
            )
            if end_turn:
                outcome = apply_action(state, end_turn)
                value = self.evaluator.evaluate(outcome.state, state.perspective_player_id)
                stats = _LineStats(
                    actions=(end_turn,),
                    visits=1,
                    value_sum=value,
                    terminal_state_key=_state_key(outcome.state),
                )
                line_stats[(end_turn.action_id,)] = stats

        best_effort_annotations = _deduplicate_annotations(
            [
                *approximation_annotations,
                *_playable_unsupported_annotations(state),
            ]
        )
        if best_effort_annotations:
            legal_first_actions = set(legal_first_action_ids)
            generated_first_actions = {
                _first_action_id(item.actions) for item in line_stats.values()
            }
            response_verified_first_actions: set[str] = set()
            missing_generated_first_actions = (
                legal_first_actions - generated_first_actions
            )
            missing_response_verified_first_actions = (
                legal_first_actions - response_verified_first_actions
            )
            legal_first_action_count = len(legal_first_actions)
            counterplay_diagnostics = {
                "planner_model": "counterplay-turnpair-v1",
                "portfolio_model": "root-action-portfolio-v1",
                "portfolio_optimality_proven": False,
                "response_scope": COUNTERPLAY_SCOPE,
                "score_kind": "counterplay_tactical_state_value",
                "seed": state.rng_seed,
                "abstained": False,
                "best_effort": True,
                "approximation_reason_count": len(best_effort_annotations),
                "candidate_line_count": len(line_stats),
                "legal_first_action_count": legal_first_action_count,
                "legal_first_action_ids": sorted(legal_first_actions),
                "generated_first_action_count": len(generated_first_actions),
                "generated_first_action_ids": sorted(generated_first_actions),
                "assessed_first_action_count": len(
                    response_verified_first_actions
                ),
                "structurally_complete_first_action_count": len(
                    response_verified_first_actions
                ),
                "response_verified_first_action_count": len(
                    response_verified_first_actions
                ),
                "response_verified_first_action_ids": sorted(
                    response_verified_first_actions
                ),
                "unassessed_first_action_count": len(
                    legal_first_actions - response_verified_first_actions
                ),
                "missing_first_action_ids": sorted(
                    missing_response_verified_first_actions
                ),
                "missing_generated_first_action_ids": sorted(
                    missing_generated_first_actions
                ),
                "missing_response_verified_first_action_ids": sorted(
                    missing_response_verified_first_actions
                ),
                "root_action_generation_coverage_rate": round(
                    len(legal_first_actions & generated_first_actions)
                    / legal_first_action_count
                    if legal_first_action_count
                    else 1.0,
                    6,
                ),
                "root_action_response_coverage_rate": round(
                    len(legal_first_actions & response_verified_first_actions)
                    / legal_first_action_count
                    if legal_first_action_count
                    else 1.0,
                    6,
                ),
                "root_action_coverage_complete": bool(
                    not missing_generated_first_actions
                    and not missing_response_verified_first_actions
                ),
                "assessed_line_count": 0,
                "search_complete": False,
            }
        else:
            counterplay_diagnostics = self._assess_counterplay(
                state,
                line_stats,
                limits,
                cancel_event,
                deadline,
                legal_first_action_ids=legal_first_action_ids,
            )
        elapsed_ms = int((time.monotonic() - started) * 1000)
        progress.append(self._record_progress(line_stats, elapsed_ms, iterations, 1.0))
        recommendations = self._recommendations(
            state,
            line_stats,
            limits.top_k,
            root_action_coverage_complete=bool(
                counterplay_diagnostics.get(
                    "root_action_coverage_complete",
                    False,
                )
            ),
            portfolio_optimality_proven=bool(
                counterplay_diagnostics.get(
                    "portfolio_optimality_proven",
                    False,
                )
            ),
            approximation_annotations=best_effort_annotations,
        )
        state_annotations = _deduplicate_annotations(
            [
                *scan_state_coverage(state),
                *best_effort_annotations,
            ]
        )
        # Coverage describes the bounded search that was actually run, not only the
        # subset safe enough to render. Withholding a known-losing line must not also
        # hide the approximation evidence discovered while assessing it.
        line_annotations = [
            annotation
            for stats in line_stats.values()
            if stats.response_evaluated
            for annotation in stats.annotations.values()
        ]
        coverage_annotations = _deduplicate_annotations(
            [
                annotation
                for annotation in [*state_annotations, *line_annotations]
                if _affects_rule_coverage(annotation)
            ]
        )
        actor = state.player(state.active_player_id)
        enemy = state.other_player(actor.player_id)
        actionable_cards = [*actor.hand, *actor.board, *enemy.board]
        if actor.hero_power:
            actionable_cards.append(actor.hero_power)
        if enemy.hero_power:
            actionable_cards.append(enemy.hero_power)
        if actor.weapon:
            actionable_cards.append(actor.weapon)
        if enemy.weapon:
            actionable_cards.append(enemy.weapon)
        coverage_entity_ids = {card.entity_id for card in actionable_cards}
        unknown_entities = {
            item.entity_id
            for item in coverage_annotations
            if item.entity_id and item.entity_id in coverage_entity_ids
        }
        total_cards = len(coverage_entity_ids)
        exact_cards = max(0, total_cards - len(unknown_entities))
        overall = exact_cards / total_cards if total_cards else 1.0
        planner_diagnostics = {
            "planner_version": "counterplay-turnpair-v1",
            "planner_model": "counterplay-turnpair-v1",
            "proof_scope": "visible_generic_v2",
            "modeled_lethal_found": bool(lethal_plan.actions) and not best_effort_annotations,
            "modeled_lethal_candidate_found": bool(lethal_plan.actions),
            "lethal_nodes_expanded": lethal_plan.nodes_expanded,
            "lethal_transposition_hits": lethal_plan.transposition_hits,
            "lethal_searched_depth": lethal_plan.searched_depth,
            "lethal_search_complete": lethal_plan.search_complete,
            "candidate_line_count": len(line_stats),
            "unique_terminal_state_count": len(
                {item.terminal_state_key for item in line_stats.values() if item.terminal_state_key}
            ),
            "max_depth": limits.max_depth,
            "abstained": False,
            "best_effort_due_to_approximation": bool(best_effort_annotations),
            "lethal_only_due_to_unsupported_alternatives": False,
            "counterplay": counterplay_diagnostics,
        }
        coverage = {
            "rules_model": "visible-combat-v2",
            "planner_model": "counterplay-turnpair-v1",
            "exact": not coverage_annotations,
            "exact_scope": "" if best_effort_annotations else "visible_generic_v2",
            "unsupported_count": len(coverage_annotations),
            "approximate_effects": [item.to_dict() for item in coverage_annotations],
            "environment_version": state.metadata.get("environment_version", ""),
            "overall": round(overall, 6),
            "card_coverage": round(overall, 6),
            "rule_coverage": 1.0 if not coverage_annotations else 0.0,
            "exact_card_count": exact_cards,
            "approximate_card_count": len(unknown_entities),
            "unknown_card_count": sum(1 for card in actionable_cards if card.card_id.startswith("UNKNOWN")),
            "summary": (
                "Bounded turn-pair simulation only; one or more mechanics are approximated."
                if coverage_annotations
                else "All supplied generic effects in the modeled turn-pair scope were simulated."
            ),
            "details": {
                "rules_model": "visible-combat-v2",
                "planner_model": "counterplay-turnpair-v1",
                "planner": planner_diagnostics,
                "counterplay": counterplay_diagnostics,
            },
        }
        return SearchResult(
            request_id=request_id,
            state_id=state.state_id,
            status=(
                "cancelled"
                if cancel_event.is_set()
                else (
                    "partial" if coverage_annotations else "ok"
                )
            ),
            elapsed_ms=elapsed_ms,
            iterations=iterations,
            recommendations=recommendations,
            progress=tuple(progress),
            coverage=coverage,
        )
