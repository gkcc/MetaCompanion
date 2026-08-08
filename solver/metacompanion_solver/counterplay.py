from __future__ import annotations

import json
import math
import threading
import time
from dataclasses import dataclass
from typing import Any, Protocol, Sequence

from .schemas import Action, ActionKind, Annotation, GameState
from .simulator import advance_to_start_of_turn, apply_action, enumerate_legal_actions, scan_state_coverage


COUNTERPLAY_SCOPE = "visible_generic_turnpair_v1"


class CounterplayEvaluator(Protocol):
    def evaluate(self, state: GameState, perspective_player_id: str) -> float:
        """Return a lower-is-worse value for the supplied perspective."""


@dataclass(frozen=True)
class CounterplayResult:
    response_actions: tuple[Action, ...]
    worst_case_value: float
    search_complete: bool
    nodes_expanded: int
    searched_depth: int
    transposition_hits: int
    annotations: tuple[Annotation, ...]
    modeled_counter_lethal: bool = False
    scope: str = COUNTERPLAY_SCOPE
    perspective_player_id: str = ""
    responder_player_id: str = ""
    stop_reason: str = ""

    @property
    def actions(self) -> tuple[Action, ...]:
        return self.response_actions

    @property
    def opponent_reply(self) -> tuple[Action, ...]:
        return self.response_actions

    @property
    def worst_case_score(self) -> float:
        return self.worst_case_value

    @property
    def is_proven_lethal(self) -> bool:
        return self.modeled_counter_lethal

    @property
    def response_is_proven_lethal(self) -> bool:
        return self.modeled_counter_lethal

    @property
    def nodes(self) -> int:
        return self.nodes_expanded

    @property
    def depth(self) -> int:
        return self.searched_depth

    @property
    def tt_hits(self) -> int:
        return self.transposition_hits

    def to_dict(self) -> dict[str, Any]:
        return {
            "scope": self.scope,
            "perspective_player_id": self.perspective_player_id,
            "responder_player_id": self.responder_player_id,
            "response_actions": [action.to_dict() for action in self.response_actions],
            "worst_case_value": round(self.worst_case_value, 6),
            "modeled_counter_lethal": self.modeled_counter_lethal,
            "search_complete": self.search_complete,
            "nodes_expanded": self.nodes_expanded,
            "searched_depth": self.searched_depth,
            "transposition_hits": self.transposition_hits,
            "annotations": [annotation.to_dict() for annotation in self.annotations],
            "stop_reason": self.stop_reason,
        }


@dataclass(frozen=True)
class _ResponseLine:
    actions: tuple[Action, ...]
    value: float
    annotations: tuple[Annotation, ...] = ()
    modeled_counter_lethal: bool = False
    search_complete: bool = True
    turn_complete: bool = True


@dataclass
class _SearchBudget:
    deadline: float
    cancel_event: threading.Event
    max_nodes: int
    nodes_expanded: int = 0
    searched_depth: int = 0
    transposition_hits: int = 0
    stop_reason: str = ""

    def can_expand(self) -> bool:
        if self.cancel_event.is_set():
            self.stop_reason = "cancelled"
            return False
        if time.monotonic() >= self.deadline:
            self.stop_reason = "deadline"
            return False
        if self.nodes_expanded >= self.max_nodes:
            self.stop_reason = "node_limit"
            return False
        return True

    def record_expansion(self, depth: int) -> None:
        self.nodes_expanded += 1
        self.searched_depth = max(self.searched_depth, depth)


def _deduplicate_annotations(items: Sequence[Annotation]) -> tuple[Annotation, ...]:
    unique: dict[tuple[str, str, str, str], Annotation] = {}
    for item in items:
        unique[(item.code, item.entity_id, item.detail, item.severity)] = item
    return tuple(unique.values())


def _annotation(code: str, detail: str, severity: str = "warning") -> Annotation:
    return Annotation(code=code, detail=detail, severity=severity)


def _state_key(state: GameState) -> str:
    payload = state.to_dict()
    payload.pop("state_id", None)
    return json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def _evaluate(
    evaluator: CounterplayEvaluator,
    state: GameState,
    perspective_player_id: str,
) -> float:
    value = float(evaluator.evaluate(state, perspective_player_id))
    if not math.isfinite(value):
        raise ValueError("counterplay evaluator must return a finite value")
    return value


def _is_terminal(state: GameState) -> bool:
    return state.friendly.hero.current_health <= 0 or state.opponent.hero.current_health <= 0


def _is_counter_lethal(
    state: GameState,
    perspective_player_id: str,
    responder_player_id: str,
) -> bool:
    return (
        state.player(perspective_player_id).hero.current_health <= 0
        and state.player(responder_player_id).hero.current_health > 0
    )


def _action_order(state: GameState, perspective_player_id: str, action: Action) -> tuple[int, int, str]:
    perspective_hero_id = state.player(perspective_player_id).hero.entity_id
    targets_perspective_hero = action.target_entity_id == perspective_hero_id
    kind_order = {
        ActionKind.ATTACK: 0,
        ActionKind.HERO_POWER: 1,
        ActionKind.PLAY_CARD: 2,
    }.get(action.kind, 3)
    return (0 if targets_perspective_hero else 1, kind_order, action.action_id)


def _line_order(line: _ResponseLine) -> tuple[int, tuple[str, ...]]:
    return (len(line.actions), tuple(action.action_id for action in line.actions))


def _is_worse(candidate: _ResponseLine, current: _ResponseLine | None) -> bool:
    if current is None:
        return True
    if candidate.modeled_counter_lethal != current.modeled_counter_lethal:
        return candidate.modeled_counter_lethal
    if candidate.value < current.value - 1e-12:
        return True
    if candidate.value > current.value + 1e-12:
        return False
    return _line_order(candidate) < _line_order(current)


def evaluate_counterplay(
    state_after_end_turn: GameState,
    *,
    evaluator: CounterplayEvaluator,
    deadline: float,
    cancel_event: threading.Event,
    max_nodes: int,
    max_depth: int,
    perspective_player_id: str | None = None,
) -> CounterplayResult:
    """Evaluate one explicit visible opponent turn after an already-applied END_TURN.

    ``max_depth`` counts responder actions other than END_TURN, so every accepted
    non-terminal response can still be a complete turn. ``search_complete`` means the
    bounded response space was exhausted, or a modeled counter-lethal established the
    terminal lower bound. Between-turn refresh and unknown draws retain the production
    simulator's explicit approximation annotations.
    """

    if isinstance(max_nodes, bool) or not isinstance(max_nodes, int) or max_nodes < 0:
        raise ValueError("max_nodes must be a non-negative integer")
    if isinstance(max_depth, bool) or not isinstance(max_depth, int) or max_depth < 0:
        raise ValueError("max_depth must be a non-negative integer")
    if isinstance(deadline, bool) or not isinstance(deadline, (int, float)) or math.isnan(deadline):
        raise ValueError("deadline must be an absolute monotonic timestamp")

    perspective = perspective_player_id or state_after_end_turn.perspective_player_id
    player_ids = {
        state_after_end_turn.friendly.player_id,
        state_after_end_turn.opponent.player_id,
    }
    if perspective not in player_ids:
        raise ValueError("perspective_player_id must identify a player in the state")
    responder = state_after_end_turn.active_player_id
    if responder == perspective:
        raise ValueError("state_after_end_turn must have the opposing player active")

    # The generic replay simulator preserves raw boundary snapshots, including
    # the previous player's stale mana.  Counterplay scoring must instead expire
    # that turn-local resource without mutating replay evidence.
    completed_player_id = state_after_end_turn.other_player(
        state_after_end_turn.active_player_id
    ).player_id
    boundary = advance_to_start_of_turn(state_after_end_turn)
    boundary_state = boundary.state
    boundary_state.player(completed_player_id).mana = 0
    base_annotations = [
        *boundary.annotations,
        *scan_state_coverage(boundary_state),
        _annotation(
            "counterplay_scope",
            "Counterplay is evaluated only in the deterministic visible generic turn-pair model; "
            "hidden identities and unsupported turn triggers remain outside this scope.",
        ),
    ]
    budget = _SearchBudget(
        deadline=float(deadline),
        cancel_event=cancel_event,
        max_nodes=max_nodes,
    )
    memo: dict[tuple[str, int], _ResponseLine] = {}

    def explore(current: GameState, remaining_depth: int, depth: int) -> _ResponseLine:
        if _is_terminal(current):
            return _ResponseLine(
                actions=(),
                value=_evaluate(evaluator, current, perspective),
                modeled_counter_lethal=_is_counter_lethal(current, perspective, responder),
            )

        key = (_state_key(current), remaining_depth)
        cached = memo.get(key)
        if cached is not None:
            budget.transposition_hits += 1
            return cached

        legal = enumerate_legal_actions(current)
        end_turn = next((action for action in legal if action.kind == ActionKind.END_TURN), None)
        non_end = sorted(
            (action for action in legal if action.kind != ActionKind.END_TURN),
            key=lambda action: _action_order(current, perspective, action),
        )
        best: _ResponseLine | None = None
        search_complete = True

        # Establish a complete-turn baseline before spending budget on longer replies.
        if end_turn is not None and budget.can_expand():
            completed_player_id = current.active_player_id
            outcome = apply_action(current, end_turn)
            scoring_state = outcome.state
            scoring_state.player(completed_player_id).mana = 0
            budget.record_expansion(depth)
            best = _ResponseLine(
                actions=(end_turn,),
                value=_evaluate(evaluator, scoring_state, perspective),
                annotations=outcome.annotations,
            )
        else:
            search_complete = False

        if remaining_depth <= 0 and non_end:
            search_complete = False
            if not budget.stop_reason:
                budget.stop_reason = "depth_limit"
        elif remaining_depth > 0:
            for action in non_end:
                if not budget.can_expand():
                    search_complete = False
                    break
                outcome = apply_action(current, action)
                budget.record_expansion(depth + 1)
                if _is_counter_lethal(outcome.state, perspective, responder):
                    lethal = _ResponseLine(
                        actions=(action,),
                        value=_evaluate(evaluator, outcome.state, perspective),
                        annotations=outcome.annotations,
                        modeled_counter_lethal=True,
                        search_complete=True,
                    )
                    memo[key] = lethal
                    return lethal
                if _is_terminal(outcome.state):
                    child = _ResponseLine(
                        actions=(),
                        value=_evaluate(evaluator, outcome.state, perspective),
                    )
                else:
                    child = explore(outcome.state, remaining_depth - 1, depth + 1)
                candidate = _ResponseLine(
                    actions=(action, *child.actions),
                    value=child.value,
                    annotations=_deduplicate_annotations([*outcome.annotations, *child.annotations]),
                    modeled_counter_lethal=child.modeled_counter_lethal,
                    search_complete=child.search_complete,
                    turn_complete=child.turn_complete,
                )
                if candidate.modeled_counter_lethal:
                    candidate = _ResponseLine(
                        actions=candidate.actions,
                        value=candidate.value,
                        annotations=candidate.annotations,
                        modeled_counter_lethal=True,
                        search_complete=True,
                        turn_complete=True,
                    )
                    memo[key] = candidate
                    return candidate
                search_complete = search_complete and child.search_complete
                if candidate.turn_complete and _is_worse(candidate, best):
                    best = candidate

        if best is None:
            best = _ResponseLine(
                actions=(),
                value=_evaluate(evaluator, current, perspective),
                search_complete=False,
                turn_complete=False,
            )
        result = _ResponseLine(
            actions=best.actions,
            value=best.value,
            annotations=best.annotations,
            modeled_counter_lethal=best.modeled_counter_lethal,
            search_complete=search_complete and best.search_complete,
            turn_complete=best.turn_complete,
        )
        if result.search_complete:
            memo[key] = result
        return result

    line = explore(boundary_state, max_depth, 0)
    annotations = [*base_annotations, *line.annotations]
    if line.modeled_counter_lethal:
        annotations.append(
            _annotation(
                "modeled_counter_lethal_scope",
                "The responder has lethal in the visible generic turn-pair model; hidden information "
                "and unsupported triggers remain outside this proof.",
            )
        )
    elif not line.search_complete:
        stop_annotations = {
            "cancelled": (
                "counterplay_cancelled",
                "Counterplay search was cancelled before the bounded response space was resolved.",
            ),
            "deadline": (
                "counterplay_deadline_reached",
                "Counterplay search reached its deadline before the bounded response space was resolved.",
            ),
            "node_limit": (
                "counterplay_node_limit",
                "Counterplay search reached its node limit before the bounded response space was resolved.",
            ),
            "depth_limit": (
                "counterplay_depth_limit",
                "Counterplay search omitted one or more responder continuations beyond max_depth.",
            ),
        }
        code, detail = stop_annotations.get(
            budget.stop_reason,
            (
                "counterplay_incomplete",
                "Counterplay search did not resolve the bounded response space.",
            ),
        )
        annotations.append(_annotation(code, detail))

    return CounterplayResult(
        response_actions=line.actions,
        worst_case_value=line.value,
        search_complete=line.search_complete,
        nodes_expanded=budget.nodes_expanded,
        searched_depth=budget.searched_depth,
        transposition_hits=budget.transposition_hits,
        annotations=_deduplicate_annotations(annotations),
        modeled_counter_lethal=line.modeled_counter_lethal,
        perspective_player_id=perspective,
        responder_player_id=responder,
        stop_reason="" if line.search_complete else budget.stop_reason,
    )


evaluate_visible_counterplay = evaluate_counterplay
