from __future__ import annotations

import copy
import hashlib
import json
import math
import statistics
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from .config import SolverConfig
from .logging_store import JsonlTrainingLogger
from .schemas import Action, ActionKind, Card, CardType, GameState, SearchResult, SolveRequest
from .service import SolverService


EVALUATION_SCHEMA_VERSION = 1
ORACLE_SUITE_ID = "oracle-turn-v1"
_SCOPES = {"exact", "approximate", "abstain"}
_SUPPORTED_EFFECT_KINDS = {"damage"}


class EvaluationError(ValueError):
    pass


@dataclass(frozen=True)
class OracleProof:
    has_lethal: bool
    winning_first_action_ids: tuple[str, ...]
    explored_state_count: int


@dataclass(frozen=True)
class LineAssessment:
    legal: bool
    lethal: bool
    error: str = ""


def _canonical_hash(value: Any) -> str:
    encoded = json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def load_evaluation_suite(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    raw = json.loads(source.read_text(encoding="utf-8"))
    if not isinstance(raw, Mapping):
        raise EvaluationError("evaluation suite root must be an object")
    suite = dict(raw)
    if suite.get("schema_version") != EVALUATION_SCHEMA_VERSION:
        raise EvaluationError(
            f"evaluation suite schema_version must be {EVALUATION_SCHEMA_VERSION}"
        )
    if suite.get("suite_id") != ORACLE_SUITE_ID:
        raise EvaluationError(f"evaluation suite_id must be {ORACLE_SUITE_ID!r}")
    seed = suite.get("seed")
    if isinstance(seed, bool) or not isinstance(seed, int):
        raise EvaluationError("evaluation suite seed must be an integer")
    fixtures = suite.get("fixtures")
    if not isinstance(fixtures, list) or not fixtures:
        raise EvaluationError("evaluation suite fixtures must be a non-empty array")
    fixture_ids: set[str] = set()
    for index, fixture in enumerate(fixtures):
        if not isinstance(fixture, Mapping):
            raise EvaluationError(f"fixtures[{index}] must be an object")
        fixture_id = fixture.get("id")
        if not isinstance(fixture_id, str) or not fixture_id.strip():
            raise EvaluationError(f"fixtures[{index}].id must be a non-empty string")
        if fixture_id in fixture_ids:
            raise EvaluationError(f"duplicate fixture id: {fixture_id}")
        fixture_ids.add(fixture_id)
        if fixture.get("scope") not in _SCOPES:
            raise EvaluationError(
                f"fixture {fixture_id!r} scope must be exact, approximate, or abstain"
            )
        if not isinstance(fixture.get("position"), Mapping):
            raise EvaluationError(f"fixture {fixture_id!r} position must be an object")
    thresholds = suite.get("thresholds", {})
    if not isinstance(thresholds, Mapping):
        raise EvaluationError("evaluation suite thresholds must be an object")
    return suite


def _card_payload(
    raw: Mapping[str, Any],
    *,
    fallback_id: str,
    card_type: str,
    can_attack_default: bool = False,
) -> dict[str, Any]:
    entity_id = raw.get("entity_id", fallback_id)
    if not isinstance(entity_id, str) or not entity_id:
        raise EvaluationError(f"card entity_id must be a non-empty string: {fallback_id}")
    health = raw.get("health", 0)
    if isinstance(health, bool) or not isinstance(health, int) or health < 0:
        raise EvaluationError(f"card health must be a non-negative integer: {entity_id}")
    payload: dict[str, Any] = {
        "entity_id": entity_id,
        "card_id": raw.get("card_id", entity_id.upper()),
        "name": raw.get("name", entity_id),
        "card_type": raw.get("card_type", card_type),
        "cost": raw.get("cost", 0),
        "attack": raw.get("attack", 0),
        "health": health,
        "current_health": raw.get("current_health", health),
        "playable": raw.get("playable", True),
        "can_attack": raw.get("can_attack", can_attack_default),
        "attacks_remaining": raw.get(
            "attacks_remaining",
            1 if raw.get("can_attack", can_attack_default) else 0,
        ),
        "taunt": raw.get("taunt", False),
        "divine_shield": raw.get("divine_shield", False),
        "stealth": raw.get("stealth", False),
        "poisonous": raw.get("poisonous", False),
        "lifesteal": raw.get("lifesteal", False),
        "effects": raw.get("effects", []),
        "effect_coverage": raw.get("effect_coverage", "exact"),
        "unsupported_effects": raw.get("unsupported_effects", []),
        "prior_weight": raw.get("prior_weight", 1.0),
        "tags": raw.get("tags", {}),
    }
    return payload


def _player_payload(
    raw: Mapping[str, Any],
    *,
    player_id: str,
    hero_id: str,
) -> dict[str, Any]:
    if raw.get("hero_power") is not None or raw.get("weapon") is not None:
        raise EvaluationError(
            f"{player_id} fixture cannot include hero_power or weapon in oracle-turn-v1"
        )
    hero_raw = raw.get("hero", {})
    if not isinstance(hero_raw, Mapping):
        raise EvaluationError(f"{player_id}.hero must be an object")
    hero_values = dict(hero_raw)
    hero_values.setdefault("health", 30)
    hero_values.setdefault("current_health", hero_values["health"])
    hand_raw = raw.get("hand", [])
    board_raw = raw.get("board", [])
    if not isinstance(hand_raw, list) or not isinstance(board_raw, list):
        raise EvaluationError(f"{player_id}.hand and board must be arrays")
    hand = [
        _card_payload(item, fallback_id=f"{player_id}-hand-{index}", card_type="SPELL")
        for index, item in enumerate(hand_raw)
        if isinstance(item, Mapping)
    ]
    if len(hand) != len(hand_raw):
        raise EvaluationError(f"{player_id}.hand entries must be objects")
    board = [
        _card_payload(
            item,
            fallback_id=f"{player_id}-board-{index}",
            card_type="MINION",
            can_attack_default=player_id == "friendly",
        )
        for index, item in enumerate(board_raw)
        if isinstance(item, Mapping)
    ]
    if len(board) != len(board_raw):
        raise EvaluationError(f"{player_id}.board entries must be objects")
    return {
        "player_id": player_id,
        "hero": _card_payload(
            hero_values,
            fallback_id=hero_id,
            card_type="HERO",
            can_attack_default=False,
        ),
        "mana": raw.get("mana", 0),
        "max_mana": raw.get("max_mana", raw.get("mana", 0)),
        "armor": raw.get("armor", 0),
        "hand": hand,
        "board": board,
        "deck_size": raw.get("deck_size", 20),
        "fatigue": raw.get("fatigue", 0),
        "hero_power": None,
        "hero_power_available": False,
        "weapon": None,
    }


def request_from_fixture(fixture: Mapping[str, Any], suite_seed: int) -> SolveRequest:
    fixture_id = str(fixture["id"])
    position = fixture["position"]
    friendly_raw = position.get("friendly", {})
    opponent_raw = position.get("opponent", {})
    if not isinstance(friendly_raw, Mapping) or not isinstance(opponent_raw, Mapping):
        raise EvaluationError(f"fixture {fixture_id!r} players must be objects")
    seed_offset = fixture.get("seed_offset", 0)
    if isinstance(seed_offset, bool) or not isinstance(seed_offset, int):
        raise EvaluationError(f"fixture {fixture_id!r} seed_offset must be an integer")
    options = fixture.get("options", {})
    if not isinstance(options, Mapping):
        raise EvaluationError(f"fixture {fixture_id!r} options must be an object")
    request = {
        "api_version": "1.0",
        "request_id": f"eval-{fixture_id}",
        "state": {
            "state_id": f"eval-state-{fixture_id}",
            "turn": position.get("turn", 5),
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": _player_payload(
                friendly_raw,
                player_id="friendly",
                hero_id="friendly-hero",
            ),
            "opponent": _player_payload(
                opponent_raw,
                player_id="opponent",
                hero_id="opponent-hero",
            ),
            "patch": "oracle-turn-v1",
            "mode": "deterministic_fixture",
            "rng_seed": suite_seed + seed_offset,
            "metadata": {
                "fixture_id": fixture_id,
                "oracle_scope": fixture["scope"],
                "environment_version": ORACLE_SUITE_ID,
            },
        },
        "options": {
            "time_budget_ms": options.get("time_budget_ms", 250),
            "max_iterations": options.get("max_iterations", 300),
            "max_depth": options.get("max_depth", 12),
            "top_k": options.get("top_k", 3),
            "search_seed": suite_seed + seed_offset,
            "allow_approximate_effects": True,
            "environment_version": ORACLE_SUITE_ID,
        },
    }
    return SolveRequest.from_dict(request)


def _players(state: GameState) -> tuple[Any, Any]:
    actor = state.player(state.active_player_id)
    return actor, state.other_player(actor.player_id)


def _living(cards: Sequence[Card]) -> list[Card]:
    return [
        card
        for card in cards
        if card.card_type == CardType.LOCATION or card.current_health > 0
    ]


def _find_entity(state: GameState, entity_id: str) -> tuple[Any, Card] | None:
    for player in (state.friendly, state.opponent):
        for card in (player.hero, *player.hand, *player.board):
            if card.entity_id == entity_id:
                return player, card
        if player.hero_power and player.hero_power.entity_id == entity_id:
            return player, player.hero_power
    return None


def _target_candidates(state: GameState, mode: str) -> list[Card]:
    actor, enemy = _players(state)
    friendly_characters = [actor.hero, *_living(actor.board)]
    enemy_characters = [
        card for card in [enemy.hero, *_living(enemy.board)] if not card.stealth
    ]
    if mode == "enemy_character":
        return enemy_characters
    if mode == "friendly_character":
        return friendly_characters
    if mode == "any_character":
        return [*friendly_characters, *enemy_characters]
    if mode == "enemy_minion":
        return [card for card in _living(enemy.board) if not card.stealth]
    if mode == "friendly_minion":
        return _living(actor.board)
    if mode == "any_minion":
        return [*_living(actor.board), *[card for card in _living(enemy.board) if not card.stealth]]
    if mode == "enemy_hero":
        return [enemy.hero]
    if mode == "friendly_hero":
        return [actor.hero]
    return []


def _primary_target_mode(card: Card) -> str:
    modes = [effect.target for effect in card.effects if effect.target not in {"none", "self"}]
    return modes[0] if modes else "none"


def _oracle_supported_card(card: Card, *, in_hand: bool) -> str:
    if card.unsupported_effects or card.effect_coverage == "unsupported":
        return f"{card.entity_id} has unsupported effects"
    if card.poisonous or card.lifesteal:
        return f"{card.entity_id} uses an unsupported combat keyword"
    if any(
        (
            card.windfury,
            card.mega_windfury,
            card.rush,
            card.charge,
            card.reborn,
            card.dormant,
            card.immune,
            card.durability > 0,
            card.current_durability > 0,
        )
    ):
        return f"{card.entity_id} is outside the deliberately small oracle combat subset"
    if in_hand and card.card_type not in {CardType.MINION, CardType.SPELL}:
        return f"{card.entity_id} has unsupported playable type {card.card_type.value}"
    if in_hand and card.card_type == CardType.SPELL and not card.effects:
        return f"{card.entity_id} is a spell without an exact effect"
    target_modes = {effect.target for effect in card.effects if effect.target not in {"none", "self"}}
    if len(target_modes) > 1:
        return f"{card.entity_id} has multiple target groups"
    for effect in card.effects:
        if effect.random or effect.kind not in _SUPPORTED_EFFECT_KINDS:
            return f"{card.entity_id} effect {effect.kind!r} is outside the oracle subset"
        if effect.target == "self":
            return f"{card.entity_id} uses unsupported self targeting"
    return ""


def assert_exact_oracle_state(state: GameState) -> None:
    if state.active_player_id != state.friendly.player_id:
        raise EvaluationError("oracle-turn-v1 fixtures must use the friendly active player")
    for player in (state.friendly, state.opponent):
        if player.weapon is not None or player.hero_power_available:
            raise EvaluationError("oracle-turn-v1 does not model weapons or active hero powers")
        for card in (player.hero, *player.board):
            reason = _oracle_supported_card(card, in_hand=False)
            if reason:
                raise EvaluationError(reason)
        for card in player.hand:
            reason = _oracle_supported_card(card, in_hand=True)
            if reason:
                raise EvaluationError(reason)


def oracle_legal_actions(state: GameState) -> list[Action]:
    actor, enemy = _players(state)
    if actor.hero.current_health <= 0 or enemy.hero.current_health <= 0:
        return []
    actions: list[Action] = []
    taunts = [card for card in _living(enemy.board) if card.taunt and not card.stealth]
    targets = taunts or [
        card for card in [*_living(enemy.board), enemy.hero] if not card.stealth
    ]
    for attacker in [actor.hero, *_living(actor.board)]:
        if attacker.attack <= 0 or not attacker.can_attack or attacker.attacks_remaining <= 0:
            continue
        for target in targets:
            actions.append(
                Action(
                    ActionKind.ATTACK,
                    attacker.entity_id,
                    target.entity_id,
                    attacker.card_id,
                )
            )
    for card in actor.hand:
        if not card.playable or card.cost > actor.mana:
            continue
        if card.card_type == CardType.MINION and len(actor.board) >= 7:
            continue
        board_positions = range(1, len(actor.board) + 2) if card.card_type == CardType.MINION else (0,)
        target_mode = _primary_target_mode(card)
        if target_mode == "none":
            target_ids = ("",)
        else:
            target_ids = tuple(
                target.entity_id for target in _target_candidates(state, target_mode)
            )
        for target_id in target_ids:
            for board_position in board_positions:
                actions.append(
                    Action(
                        ActionKind.PLAY_CARD,
                        card.entity_id,
                        target_id,
                        card.card_id,
                        board_position=board_position,
                    )
                )
    actions.append(Action(ActionKind.END_TURN))
    return actions


def _damage(owner: Any, target: Card, amount: int) -> None:
    remaining = max(0, amount)
    if remaining == 0:
        return
    if target.divine_shield:
        target.divine_shield = False
        return
    if target.card_type == CardType.HERO and owner.armor > 0:
        absorbed = min(owner.armor, remaining)
        owner.armor -= absorbed
        remaining -= absorbed
    target.current_health = max(0, target.current_health - remaining)


def _remove_dead(state: GameState) -> None:
    for player in (state.friendly, state.opponent):
        dead = [
            card
            for card in player.board
            if card.card_type != CardType.LOCATION and card.current_health <= 0
        ]
        player.board = _living(player.board)
        player.graveyard.extend(dead)


def _apply_effects(state: GameState, source: Card, target_id: str) -> None:
    for effect in source.effects:
        if effect.kind != "damage":
            raise EvaluationError(f"oracle cannot apply effect {effect.kind!r}")
        if effect.target == "none":
            continue
        found = _find_entity(state, target_id)
        if found is None:
            raise EvaluationError(f"oracle target no longer exists: {target_id}")
        owner, target = found
        _damage(owner, target, effect.amount)
        _remove_dead(state)


def oracle_apply_action(state: GameState, action: Action) -> tuple[GameState, bool]:
    legal_ids = {candidate.action_id for candidate in oracle_legal_actions(state)}
    if action.action_id not in legal_ids:
        raise EvaluationError(f"independent oracle rejected action {action.action_id}")
    next_state = copy.deepcopy(state)
    actor, enemy = _players(next_state)
    if action.kind == ActionKind.END_TURN:
        next_state.active_player_id = enemy.player_id
        next_state.turn += 1
        return next_state, True
    if action.kind == ActionKind.ATTACK:
        source_found = _find_entity(next_state, action.source_entity_id)
        target_found = _find_entity(next_state, action.target_entity_id)
        if source_found is None or target_found is None:
            raise EvaluationError("independent oracle attack entity disappeared")
        _, attacker = source_found
        target_owner, target = target_found
        retaliation = target.attack if target.card_type != CardType.HERO else 0
        _damage(target_owner, target, attacker.attack)
        _damage(actor, attacker, retaliation)
        attacker.attacks_remaining = max(0, attacker.attacks_remaining - 1)
        attacker.can_attack = attacker.attacks_remaining > 0
        _remove_dead(next_state)
        return next_state, False
    if action.kind == ActionKind.PLAY_CARD:
        card = next(
            (item for item in actor.hand if item.entity_id == action.source_entity_id),
            None,
        )
        if card is None:
            raise EvaluationError("independent oracle play source is not in hand")
        actor.hand.remove(card)
        actor.mana -= card.cost
        if card.card_type == CardType.MINION:
            if not 1 <= action.board_position <= len(actor.board) + 1:
                raise EvaluationError("independent oracle board position is invalid")
            actor.board.insert(action.board_position - 1, card)
            card.can_attack = False
            card.attacks_remaining = 0
        _apply_effects(next_state, card, action.target_entity_id)
        if card.card_type == CardType.SPELL:
            actor.graveyard.append(card)
        return next_state, False
    raise EvaluationError(f"independent oracle cannot apply {action.kind.value}")


def _state_key(state: GameState) -> str:
    return _canonical_hash(state.to_dict())


def prove_lethal(state: GameState, *, maximum_states: int = 100_000) -> OracleProof:
    assert_exact_oracle_state(state)
    memo: dict[str, bool] = {}
    explored = 0

    def can_lethal(current: GameState) -> bool:
        nonlocal explored
        _, enemy = _players(current)
        if enemy.hero.current_health <= 0:
            return True
        key = _state_key(current)
        if key in memo:
            return memo[key]
        explored += 1
        if explored > maximum_states:
            raise EvaluationError(
                f"oracle exceeded maximum_states={maximum_states}; fixture is not bounded"
            )
        memo[key] = False
        for action in oracle_legal_actions(current):
            if action.kind == ActionKind.END_TURN:
                continue
            child, _ = oracle_apply_action(current, action)
            if can_lethal(child):
                memo[key] = True
                return True
        return False

    winning: list[str] = []
    for action in oracle_legal_actions(state):
        if action.kind == ActionKind.END_TURN:
            continue
        child, _ = oracle_apply_action(state, action)
        if can_lethal(child):
            winning.append(action.action_id)
    return OracleProof(bool(winning), tuple(sorted(winning)), explored)


def assess_line(state: GameState, actions: Sequence[Action]) -> LineAssessment:
    current = copy.deepcopy(state)
    ended = False
    try:
        for action in actions:
            if ended:
                raise EvaluationError("line contains actions after end_turn")
            current, ended = oracle_apply_action(current, action)
            _, enemy = _players(current)
            if enemy.hero.current_health <= 0:
                ended = True
        _, enemy = _players(current)
        return LineAssessment(True, enemy.hero.current_health <= 0)
    except EvaluationError as exc:
        return LineAssessment(False, False, str(exc))


def _percentile(values: Sequence[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1))
    return round(float(ordered[index]), 3)


def _rate(numerator: int, denominator: int, *, empty: float = 0.0) -> float:
    return round(numerator / denominator, 6) if denominator else empty


def _metric_check(
    name: str,
    value: float | int,
    operator: str,
    threshold: float | int,
) -> dict[str, Any]:
    passed = value >= threshold if operator == ">=" else value <= threshold
    return {
        "name": name,
        "value": value,
        "operator": operator,
        "threshold": threshold,
        "passed": passed,
    }


def _threshold_checks(metrics: Mapping[str, Any], thresholds: Mapping[str, Any]) -> list[dict[str, Any]]:
    defaults: dict[str, tuple[str, float | int, str]] = {
        "min_proven_top1_rate": (">=", 1.0, "proven_top1_rate"),
        "min_proven_top3_rate": (">=", 1.0, "proven_top3_rate"),
        "max_false_lethal_rate": ("<=", 0.0, "false_lethal_rate"),
        "min_legality_rate": (">=", 1.0, "legality_rate"),
        "min_exact_fixture_count": (">=", 1, "exact_fixture_count"),
        "max_approximate_rate": ("<=", 1.0, "approximate_fixture_rate"),
        "max_abstain_rate": ("<=", 1.0, "abstain_fixture_rate"),
        "max_latency_p95_ms": ("<=", 10_000.0, "latency_p95_ms"),
    }
    checks: list[dict[str, Any]] = []
    for threshold_name, (operator, default, metric_name) in defaults.items():
        raw_threshold = thresholds.get(threshold_name, default)
        if isinstance(raw_threshold, bool) or not isinstance(raw_threshold, (int, float)):
            raise EvaluationError(f"threshold {threshold_name} must be numeric")
        checks.append(
            _metric_check(metric_name, metrics[metric_name], operator, raw_threshold)
        )
    return checks


def _promotion_checks(
    report: Mapping[str, Any], baseline: Mapping[str, Any]
) -> tuple[list[dict[str, Any]], bool]:
    checks: list[dict[str, Any]] = []
    if baseline.get("suite_hash") != report.get("suite_hash"):
        return [
            {
                "name": "suite_hash",
                "value": report.get("suite_hash", ""),
                "operator": "==",
                "threshold": baseline.get("suite_hash", ""),
                "passed": False,
            }
        ], False
    candidate_metrics = report.get("metrics")
    baseline_metrics = baseline.get("metrics")
    if not isinstance(candidate_metrics, Mapping) or not isinstance(baseline_metrics, Mapping):
        raise EvaluationError("baseline and candidate reports must contain metrics objects")
    for name in ("proven_top1_rate", "proven_top3_rate", "legality_rate"):
        checks.append(
            _metric_check(name, candidate_metrics[name], ">=", baseline_metrics[name])
        )
    checks.append(
        _metric_check(
            "false_lethal_rate",
            candidate_metrics["false_lethal_rate"],
            "<=",
            baseline_metrics["false_lethal_rate"],
        )
    )
    baseline_latency = float(baseline_metrics.get("latency_p95_ms", 0.0))
    latency_limit = round(max(baseline_latency * 1.10, baseline_latency + 25.0), 3)
    checks.append(
        _metric_check(
            "latency_p95_ms",
            candidate_metrics["latency_p95_ms"],
            "<=",
            latency_limit,
        )
    )
    quality_improved = bool(
        candidate_metrics["proven_top1_rate"] > baseline_metrics["proven_top1_rate"]
        or candidate_metrics["proven_top3_rate"] > baseline_metrics["proven_top3_rate"]
        or candidate_metrics["false_lethal_rate"] < baseline_metrics["false_lethal_rate"]
    )
    return checks, quality_improved


def evaluate_suite(
    fixture_path: str | Path,
    config: SolverConfig,
    *,
    baseline_path: str | Path | None = None,
    seed_override: int | None = None,
    solve: Callable[[SolveRequest], SearchResult] | None = None,
) -> dict[str, Any]:
    suite = load_evaluation_suite(fixture_path)
    fixtures = suite["fixtures"]
    suite_seed = suite["seed"] if seed_override is None else seed_override
    if isinstance(suite_seed, bool) or not isinstance(suite_seed, int):
        raise EvaluationError("seed_override must be an integer")
    service = None
    if solve is None:
        service = SolverService(config, logger=JsonlTrainingLogger(None))
        solve = service.solve
    fixture_hashes = [
        {"id": fixture["id"], "sha256": _canonical_hash(fixture)}
        for fixture in fixtures
    ]
    suite_hash = _canonical_hash(suite)
    details: list[dict[str, Any]] = []
    latencies: list[float] = []
    exact_count = 0
    approximate_count = 0
    abstain_count = 0
    solver_exact_count = 0
    solver_approximate_count = 0
    solver_abstain_count = 0
    proven_count = 0
    top1_count = 0
    top3_count = 0
    lethal_claim_count = 0
    false_lethal_count = 0
    proof_contract_failures = 0
    assessed_lines = 0
    legal_lines = 0
    contract_failures = 0

    for fixture in fixtures:
        fixture_id = str(fixture["id"])
        scope = str(fixture["scope"])
        request = request_from_fixture(fixture, suite_seed)
        proof: OracleProof | None = None
        contract_errors: list[str] = []
        if scope == "exact":
            exact_count += 1
            proof = prove_lethal(request.state)
            expected = fixture.get("expected", {})
            if not isinstance(expected, Mapping):
                raise EvaluationError(f"fixture {fixture_id!r} expected must be an object")
            expected_lethal = expected.get("has_lethal")
            if not isinstance(expected_lethal, bool):
                raise EvaluationError(
                    f"fixture {fixture_id!r} expected.has_lethal must be boolean"
                )
            if proof.has_lethal != expected_lethal:
                contract_errors.append(
                    f"oracle has_lethal={proof.has_lethal} != expected {expected_lethal}"
                )
            expected_first_count = expected.get("winning_first_action_count")
            if expected_first_count is not None:
                if isinstance(expected_first_count, bool) or not isinstance(expected_first_count, int):
                    raise EvaluationError(
                        f"fixture {fixture_id!r} winning_first_action_count must be integer"
                    )
                if len(proof.winning_first_action_ids) != expected_first_count:
                    contract_errors.append(
                        "oracle winning_first_action_count="
                        f"{len(proof.winning_first_action_ids)} != expected {expected_first_count}"
                    )
        elif scope == "approximate":
            approximate_count += 1
        else:
            abstain_count += 1

        started = time.perf_counter()
        result = solve(request)
        wall_ms = (time.perf_counter() - started) * 1000.0
        latencies.append(wall_ms)
        if not result.recommendations:
            solver_abstain_count += 1
            solver_scope = "abstain"
        elif bool(result.coverage.get("exact", False)):
            solver_exact_count += 1
            solver_scope = "exact"
        else:
            solver_approximate_count += 1
            solver_scope = "approximate"

        assessments: list[LineAssessment] = []
        proof_claims: list[bool] = []
        for recommendation_index, recommendation in enumerate(result.recommendations[:3], start=1):
            has_proof_signal = bool(
                recommendation.is_proven_lethal
                or recommendation.proof_kind
                or recommendation.proof_scope
            )
            valid_modeled_lethal_claim = bool(
                recommendation.is_proven_lethal
                and recommendation.proof_kind == "modeled_lethal"
                and recommendation.proof_scope == "visible_generic_v2"
            )
            proof_claims.append(has_proof_signal)
            if has_proof_signal and not valid_modeled_lethal_claim:
                proof_contract_failures += 1
                contract_errors.append(
                    f"recommendation {recommendation_index} has inconsistent lethal proof fields"
                )
        if scope == "exact":
            for recommendation, has_proof_claim in zip(
                result.recommendations[:3], proof_claims
            ):
                assessment = assess_line(request.state, recommendation.actions)
                assessments.append(assessment)
                assessed_lines += 1
                if assessment.legal:
                    legal_lines += 1
                if has_proof_claim:
                    lethal_claim_count += 1
                    if not assessment.lethal:
                        false_lethal_count += 1
            if proof and proof.has_lethal:
                proven_count += 1
                if assessments and assessments[0].legal and assessments[0].lethal:
                    top1_count += 1
                if any(item.legal and item.lethal for item in assessments[:3]):
                    top3_count += 1

        if contract_errors:
            contract_failures += 1
        details.append(
            {
                "id": fixture_id,
                "category": fixture.get("category", ""),
                "scope": scope,
                "fixture_sha256": _canonical_hash(fixture),
                "oracle": (
                    {
                        "has_lethal": proof.has_lethal,
                        "winning_first_action_ids": list(proof.winning_first_action_ids),
                        "explored_state_count": proof.explored_state_count,
                    }
                    if proof
                    else {"abstained": True}
                ),
                "contract_errors": contract_errors,
                "solver_status": result.status,
                "solver_scope": solver_scope,
                "recommendation_count": len(result.recommendations),
                "line_assessments": [
                    {"legal": item.legal, "lethal": item.lethal, "error": item.error}
                    for item in assessments
                ],
                "latency_ms": round(wall_ms, 3),
            }
        )

    fixture_count = len(fixtures)
    metrics: dict[str, Any] = {
        "fixture_count": fixture_count,
        "exact_fixture_count": exact_count,
        "approximate_fixture_count": approximate_count,
        "abstain_fixture_count": abstain_count,
        "exact_fixture_rate": _rate(exact_count, fixture_count),
        "approximate_fixture_rate": _rate(approximate_count, fixture_count),
        "abstain_fixture_rate": _rate(abstain_count, fixture_count),
        "solver_exact_count": solver_exact_count,
        "solver_approximate_count": solver_approximate_count,
        "solver_abstain_count": solver_abstain_count,
        "proven_lethal_fixture_count": proven_count,
        "proven_top1_count": top1_count,
        "proven_top3_count": top3_count,
        "proven_top1_rate": _rate(top1_count, proven_count, empty=1.0),
        "proven_top3_rate": _rate(top3_count, proven_count, empty=1.0),
        "lethal_claim_count": lethal_claim_count,
        "false_lethal_count": false_lethal_count,
        "false_lethal_rate": _rate(false_lethal_count, lethal_claim_count),
        "proof_contract_failure_count": proof_contract_failures,
        "assessed_line_count": assessed_lines,
        "legal_line_count": legal_lines,
        "legality_rate": _rate(legal_lines, assessed_lines, empty=1.0),
        "fixture_contract_failure_count": contract_failures,
        "latency_mean_ms": round(statistics.mean(latencies), 3) if latencies else 0.0,
        "latency_p50_ms": _percentile(latencies, 0.50),
        "latency_p95_ms": _percentile(latencies, 0.95),
    }
    threshold_checks = _threshold_checks(metrics, suite.get("thresholds", {}))
    contract_check = _metric_check(
        "fixture_contract_failure_count",
        contract_failures,
        "<=",
        0,
    )
    threshold_checks.append(contract_check)
    threshold_checks.append(
        _metric_check(
            "proof_contract_failure_count",
            proof_contract_failures,
            "<=",
            0,
        )
    )
    gate_passed = all(item["passed"] for item in threshold_checks)
    report: dict[str, Any] = {
        "schema_version": EVALUATION_SCHEMA_VERSION,
        "kind": "advisor_eval_report_v1",
        "suite_id": ORACLE_SUITE_ID,
        "suite_hash": suite_hash,
        "fixture_file": str(Path(fixture_path)),
        "fixture_hashes": fixture_hashes,
        "seed": suite_seed,
        "config_hash": _canonical_hash(asdict(config)),
        "model_version": (
            service.health().get("model_version", "generic-v1") if service else "injected-solver"
        ),
        "metrics": metrics,
        "metric_definitions": {
            "proven_top1_rate": (
                "Share of exact fixtures with an independently proven lethal where the "
                "candidate's first recommendation is independently legal and lethal."
            ),
            "proven_top3_rate": (
                "Share of exact fixtures with an independently proven lethal where at least "
                "one of the first three recommendations is independently legal and lethal."
            ),
            "false_lethal_rate": (
                "Share of recommendations carrying modeled-lethal proof fields that the "
                "independent oracle cannot execute to lethal. Heuristic score=1 alone is "
                "not a proof claim."
            ),
            "legality_rate": (
                "Share of recommendation lines in exact fixtures accepted action-by-action "
                "by the independent oracle."
            ),
            "exact_approximate_abstain": (
                "Fixture-declared oracle scope counts; only exact fixtures enter proof and "
                "legality promotion metrics."
            ),
        },
        "gate": {"passed": gate_passed, "checks": threshold_checks},
        "fixtures": details,
        "promotion": {
            "baseline_provided": False,
            "passed": gate_passed,
            "quality_improvement_proven": False,
            "checks": [],
        },
        "caveat": (
            "This gate covers only the deterministic oracle-turn-v1 subset. "
            "It does not prove complete Hearthstone optimal play or calibrated win rates."
        ),
    }
    if baseline_path is not None:
        baseline_raw = json.loads(Path(baseline_path).read_text(encoding="utf-8"))
        if not isinstance(baseline_raw, Mapping):
            raise EvaluationError("baseline report root must be an object")
        promotion_checks, quality_improved = _promotion_checks(report, baseline_raw)
        promotion_passed = gate_passed and all(item["passed"] for item in promotion_checks)
        report["promotion"] = {
            "baseline_provided": True,
            "baseline_file": str(Path(baseline_path)),
            "passed": promotion_passed,
            "quality_improvement_proven": quality_improved,
            "checks": promotion_checks,
        }
    report["passed"] = bool(report["gate"]["passed"] and report["promotion"]["passed"])
    return report


def write_evaluation_report(report: Mapping[str, Any], path: str | Path) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
