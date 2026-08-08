"""Fail-closed Python-oracle parity and timing gate for the Rust solver.

The Rust process never receives a fixture wrapper or an expected answer.  Combat
and turn-pair fixtures send their canonical ``SolveRequest``; HDT fixtures keep a
separate raw Advisor wire payload and use an independently constructed oracle
request only to compute expected truth.  That keeps fixture interpretation and
expected truth out of the implementation under test while exercising the real HDT
adapter boundary.

Profiles are fixed here rather than self-declared by the Rust binary:

``combat-v1``
    Every exact fixture in ``oracle-turn-v1``.  This is the first migration gate.

``full``
    Every exact fixture in ``oracle-turnpair-v1`` plus every exact/scoped-lethal
    fixture in ``oracle-hdt-cardrules-v1``.  This is the future worker-promotion
    gate and intentionally fails while those capabilities are absent.

Exit codes are 0 for parity, 2 for a gate/contract error, 3 for a parity mismatch,
and 4 when the Rust binary is missing.  Missing binaries are never a passing CLI
result; only the unittest integration wrapper is allowed to report a clear skip.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import math
import os
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence


SOLVER_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = SOLVER_ROOT.parent
if str(SOLVER_ROOT) not in sys.path:
    sys.path.insert(0, str(SOLVER_ROOT))

from metacompanion_solver.evaluation import (  # noqa: E402
    load_evaluation_suite,
    oracle_apply_action,
    oracle_legal_actions,
    prove_lethal,
    request_from_fixture,
)
from metacompanion_solver.hdt_rule_evaluation import (  # noqa: E402
    candidate_wire_request_from_fixture,
    load_hdt_rule_suite,
    oracle_request_from_fixture,
)
from metacompanion_solver.schemas import (  # noqa: E402
    Action,
    GameState,
    SolveRequest,
)
from metacompanion_solver.turnpair_evaluation import (  # noqa: E402
    MAX_ENUMERATED_NODES,
    MAX_LINE_DEPTH,
    TurnPairEvaluationError,
    assess_oracle_actions,
    assess_turnpair_line,
    enumerate_oracle_actions,
    load_turnpair_suite,
    prove_turnpair,
    request_from_turnpair_fixture,
    tactical_utility,
)


REQUEST_SCHEMA = "metacompanion-rust-parity-request-v1"
RESULT_SCHEMA = "metacompanion-rust-parity-result-v1"
ROOT_ACTION_PORTFOLIO_MODEL = "root-action-portfolio-v1"
NEAR_OPTIMAL_REGRET_THRESHOLD = 100
REPORT_SCHEMA = "metacompanion-rust-parity-report-v1"
PROFILE_COMBAT = "combat-v1"
PROFILE_FULL = "full"
SUPPORTED_PROFILES = (PROFILE_COMBAT, PROFILE_FULL)

TURN_FIXTURES = SOLVER_ROOT / "fixtures" / "oracle-turn-v1.json"
TURNPAIR_FIXTURES = SOLVER_ROOT / "fixtures" / "oracle-turnpair-v1.json"
HDT_RULE_FIXTURES = SOLVER_ROOT / "fixtures" / "oracle-hdt-cardrules-v1.json"

_HDT_NON_SEMANTIC_STATE_FIELDS = frozenset({"state_id", "patch", "mode", "metadata"})
_HDT_RULE_PROVENANCE_FIELDS = frozenset(
    {"rule_id", "rule_version", "rule_text_sha256"}
)
_HDT_DERIVATION_TAGS = frozenset(
    {
        "EXHAUSTED",
        "HAS_ACTIVATE_POWER",
        "NUM_ATTACKS_THIS_TURN",
        "NUM_TURNS_IN_PLAY",
    }
)


class RustParityError(RuntimeError):
    """Raised for a malformed profile, oracle result, or Rust wire response."""


@dataclass(frozen=True)
class TerminalVariant:
    friendly_action_ids: tuple[str, ...]
    opponent_action_ids: tuple[str, ...]
    state: dict[str, Any]
    state_sha256: str
    friendly_health: int
    opponent_health: int
    friendly_armor: int
    opponent_armor: int


@dataclass(frozen=True)
class ParityCase:
    case_id: str
    suite_id: str
    fixture_id: str
    profile: str
    request: SolveRequest
    legal_action_ids: tuple[str, ...]
    optimal_first_action_ids: tuple[str, ...]
    top1_action_id: str
    utility_kind: str
    utility: int
    terminal_variants: tuple[TerminalVariant, ...]
    python_wall_time_ms: float
    request_wire_payload: dict[str, Any] | None = None
    has_lethal: bool | None = None
    winning_first_action_ids: tuple[str, ...] = ()
    explored_state_count: int | None = None
    ignored_unsupported_hand_entity_ids: tuple[str, ...] = ()
    first_action_values: tuple[tuple[str, int], ...] = ()
    required_portfolio_first_action_ids: tuple[str, ...] = ()
    legal_root_action_ids: tuple[str, ...] = ()
    safe_first_action_ids: tuple[str, ...] = ()
    max_portfolio_first_action_minimax_regret: int | None = None

    def request_envelope(self) -> dict[str, Any]:
        request_payload = (
            self.request.to_dict()
            if self.request_wire_payload is None
            else copy.deepcopy(self.request_wire_payload)
        )
        return {
            "schema": REQUEST_SCHEMA,
            "case_id": self.case_id,
            "suite_id": self.suite_id,
            "request": request_payload,
        }


@dataclass(frozen=True)
class _CompleteLine:
    actions: tuple[Action, ...]
    state: GameState


@dataclass(frozen=True)
class RustInvocation:
    payload: dict[str, Any]
    process_wall_time_ms: float
    stderr: str


def _canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def _canonical_hash(value: Any) -> str:
    return hashlib.sha256(_canonical_json(value).encode("utf-8")).hexdigest()


def _zero_rule_tag(value: Any) -> bool:
    """Return whether a raw HDT rule tag carries the canonical zero value."""

    if isinstance(value, bool):
        return not value
    if isinstance(value, (int, float)):
        return value == 0
    if isinstance(value, str):
        return value.strip() == "0"
    return False


def _hdt_semantic_state(case: ParityCase, state: Mapping[str, Any]) -> dict[str, Any]:
    """Project a raw-HDT terminal onto independently comparable game semantics.

    The allowlist is intentionally narrow.  It removes only adapter identity,
    diagnostic/provenance fields, and raw HDT tags whose derived canonical fields
    remain in the state.  Health, armor, mana, fatigue, attack availability/counts,
    card combat state, effects, turn ownership, action lines, and utility are never
    normalized away.
    """

    normalized = copy.deepcopy(dict(state))
    for field in _HDT_NON_SEMANTIC_STATE_FIELDS:
        normalized.pop(field, None)

    friendly = _mapping(normalized.get("friendly"), "semantic_state.friendly")
    opponent = _mapping(normalized.get("opponent"), "semantic_state.opponent")
    friendly_id = str(friendly.get("player_id", ""))
    opponent_id = str(opponent.get("player_id", ""))

    def player_role(value: Any) -> Any:
        if str(value) == friendly_id:
            return "friendly"
        if str(value) == opponent_id:
            return "opponent"
        return value

    normalized["active_player_id"] = player_role(normalized.get("active_player_id"))
    normalized["perspective_player_id"] = player_role(
        normalized.get("perspective_player_id")
    )
    friendly["player_id"] = "friendly"
    opponent["player_id"] = "opponent"

    ignored_ids = set(case.ignored_unsupported_hand_entity_ids)
    for player in (friendly, opponent):
        player.pop("public_rule_tags_complete", None)
        public_rule_tags = player.get("public_rule_tags")
        if isinstance(public_rule_tags, dict):
            player["public_rule_tags"] = {
                key: value
                for key, value in public_rule_tags.items()
                if not _zero_rule_tag(value)
            }
        if not player.get("public_rule_tags"):
            player.pop("public_rule_tags", None)

        hand = player.get("hand")
        if isinstance(hand, list) and ignored_ids:
            player["hand"] = [
                card
                for card in hand
                if not (
                    isinstance(card, Mapping)
                    and str(card.get("entity_id", "")) in ignored_ids
                    and card.get("effect_coverage") == "unsupported"
                    and bool(card.get("unsupported_effects"))
                )
            ]

        cards: list[Mapping[str, Any]] = []
        for singleton in (player.get("hero"), player.get("hero_power"), player.get("weapon")):
            if isinstance(singleton, Mapping):
                cards.append(singleton)
        for collection_name in ("hand", "board", "graveyard"):
            collection = player.get(collection_name)
            if isinstance(collection, list):
                cards.extend(card for card in collection if isinstance(card, Mapping))

        for card in cards:
            for field in _HDT_RULE_PROVENANCE_FIELDS:
                card.pop(field, None)
            if card.get("visibility") == "public":
                card.pop("visibility", None)
            tags = card.get("tags")
            if isinstance(tags, dict):
                for tag in _HDT_DERIVATION_TAGS:
                    tags.pop(tag, None)
            if not card.get("effects") and not card.get("unsupported_effects"):
                card["effect_coverage"] = "no_modeled_effect"

    return normalized


def _semantic_state_hash(case: ParityCase, state: Mapping[str, Any]) -> str:
    if case.suite_id == "oracle-hdt-cardrules-v1":
        return _canonical_hash(_hdt_semantic_state(case, state))
    return _canonical_hash(state)


def _file_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _numeric(value: Any) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and math.isfinite(float(value))
    )


def _percentile(values: Sequence[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(float(value) for value in values)
    index = max(0, min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1))
    return round(ordered[index], 3)


def _terminal_variant(
    state: GameState,
    friendly_actions: Sequence[Action],
    opponent_actions: Sequence[Action] = (),
) -> TerminalVariant:
    normalized = state.to_dict()
    return TerminalVariant(
        friendly_action_ids=tuple(action.action_id for action in friendly_actions),
        opponent_action_ids=tuple(action.action_id for action in opponent_actions),
        state=normalized,
        state_sha256=_canonical_hash(normalized),
        friendly_health=state.friendly.hero.current_health,
        opponent_health=state.opponent.hero.current_health,
        friendly_armor=state.friendly.armor,
        opponent_armor=state.opponent.armor,
    )


def _deduplicate_variants(values: Iterable[TerminalVariant]) -> tuple[TerminalVariant, ...]:
    unique: dict[tuple[Any, ...], TerminalVariant] = {}
    for value in values:
        key = (
            value.friendly_action_ids,
            value.opponent_action_ids,
            value.state_sha256,
        )
        unique.setdefault(key, value)
    return tuple(
        unique[key]
        for key in sorted(
            unique,
            key=lambda item: (len(item[0]), item[0], len(item[1]), item[1], item[2]),
        )
    )


def _state_is_terminal(state: GameState) -> bool:
    return (
        state.friendly.hero.current_health <= 0
        or state.opponent.hero.current_health <= 0
    )


def _enumerate_current_turn_lines(
    state: GameState,
    *,
    max_depth: int = MAX_LINE_DEPTH,
    max_nodes: int = MAX_ENUMERATED_NODES,
) -> tuple[tuple[_CompleteLine, ...], int]:
    """Exhaustively enumerate the independent oracle-turn-v1 action leaves."""

    lines: dict[tuple[str, ...], _CompleteLine] = {}
    explored = 0

    def visit(current: GameState, actions: tuple[Action, ...], depth: int) -> None:
        nonlocal explored
        if explored >= max_nodes:
            raise RustParityError(
                f"current-turn parity oracle exceeded max_nodes={max_nodes}"
            )
        if _state_is_terminal(current):
            lines.setdefault(
                tuple(action.action_id for action in actions),
                _CompleteLine(actions, current),
            )
            return
        if depth >= max_depth:
            raise RustParityError(
                f"current-turn parity oracle exceeded max_depth={max_depth}"
            )
        for action in oracle_legal_actions(current):
            explored += 1
            next_state, ended = oracle_apply_action(current, action)
            next_actions = (*actions, action)
            if ended or _state_is_terminal(next_state):
                lines.setdefault(
                    tuple(item.action_id for item in next_actions),
                    _CompleteLine(next_actions, next_state),
                )
            else:
                visit(next_state, next_actions, depth + 1)

    visit(copy.deepcopy(state), (), 0)
    if not lines:
        raise RustParityError("current-turn parity oracle produced no complete lines")
    ordered = tuple(
        lines[key]
        for key in sorted(lines, key=lambda item: (len(item), item))
    )
    return ordered, explored


def _build_combat_case(
    fixture: Mapping[str, Any], suite_seed: int, suite_id: str
) -> ParityCase:
    request = request_from_fixture(fixture, suite_seed)
    # Match the Rust-reported interval: both timings begin after the canonical
    # request has been decoded and cover oracle/planning plus terminal assembly.
    started = time.perf_counter()
    proof = prove_lethal(request.state)
    lines, _ = _enumerate_current_turn_lines(request.state)
    scored = [
        (tactical_utility(line.state, request.state.perspective_player_id), line)
        for line in lines
    ]
    best_utility = max(score for score, _ in scored)
    optimal_lines = [line for score, line in scored if score == best_utility]
    optimal_first = tuple(
        sorted({line.actions[0].action_id for line in optimal_lines})
    )
    if not optimal_first:
        raise RustParityError(f"fixture {fixture['id']} produced no optimal first action")
    # The lethal contract deliberately chooses the lexicographically first winning
    # first action.  For a non-lethal tactical tie, the Rust oracle's stable rule is
    # shortest complete line and then the complete action-id sequence.
    if proof.has_lethal:
        top1 = proof.winning_first_action_ids[0]
    else:
        chosen = min(
            optimal_lines,
            key=lambda line: (
                len(line.actions),
                tuple(action.action_id for action in line.actions),
            ),
        )
        top1 = chosen.actions[0].action_id
    variants = _deduplicate_variants(
        _terminal_variant(line.state, line.actions)
        for line in optimal_lines
        if line.actions[0].action_id == top1
    )
    elapsed = (time.perf_counter() - started) * 1000.0
    return ParityCase(
        case_id=f"{suite_id}/{fixture['id']}",
        suite_id=suite_id,
        fixture_id=str(fixture["id"]),
        profile=PROFILE_COMBAT,
        request=request,
        legal_action_ids=tuple(
            sorted(action.action_id for action in oracle_legal_actions(request.state))
        ),
        optimal_first_action_ids=optimal_first,
        top1_action_id=top1,
        utility_kind="current_turn_tactical_utility",
        utility=best_utility,
        terminal_variants=variants,
        python_wall_time_ms=elapsed,
        has_lethal=proof.has_lethal,
        winning_first_action_ids=proof.winning_first_action_ids,
        explored_state_count=proof.explored_state_count,
    )


def _turnpair_final_variant(state: GameState, line: Any) -> TerminalVariant:
    assessment = assess_turnpair_line(state, line.actions)
    if not assessment.action_assessment.legal or not assessment.action_assessment.complete:
        raise RustParityError(
            "independent turn-pair proof yielded an illegal or incomplete friendly line"
        )
    if assessment.minimax_value != line.minimax_value:
        raise RustParityError(
            "independent turn-pair proof and line reassessment disagree on utility"
        )
    final_state = assessment.action_assessment.state
    response_actions: tuple[Action, ...] = ()
    if assessment.response_start is not None:
        response_actions = assessment.worst_response
        response = assess_oracle_actions(assessment.response_start, response_actions)
        if not response.legal or not response.complete:
            raise RustParityError(
                "independent turn-pair proof yielded an illegal or incomplete response"
            )
        final_state = response.state
    if tactical_utility(final_state, state.perspective_player_id) != line.minimax_value:
        raise RustParityError(
            "independent turn-pair terminal state does not reproduce minimax utility"
        )
    return _terminal_variant(final_state, line.actions, response_actions)


def _turnpair_first_action_values(proof: Any) -> tuple[tuple[str, int], ...]:
    values: dict[str, int] = {}
    for line in proof.lines:
        first_action_id = str(line.first_action_id)
        current = values.get(first_action_id)
        if current is None or int(line.minimax_value) > current:
            values[first_action_id] = int(line.minimax_value)
    return tuple(sorted(values.items()))


def _turnpair_safe_first_action_ids(proof: Any) -> tuple[str, ...]:
    best: dict[str, Any] = {}
    for line in proof.lines:
        current = best.get(line.first_action_id)
        line_key = (
            int(line.minimax_value),
            tuple(action.action_id for action in line.actions),
        )
        current_key = (
            int(current.minimax_value),
            tuple(action.action_id for action in current.actions),
        ) if current is not None else None
        if current_key is None or line_key > current_key:
            best[line.first_action_id] = line
    return tuple(
        sorted(
            first_action_id
            for first_action_id, line in best.items()
            if line.immediate_lethal or line.safe_after_response
        )
    )


def _max_portfolio_regret(fixture: Mapping[str, Any]) -> int | None:
    expected = _mapping(
        fixture.get("expected", {}),
        f"fixture {fixture.get('id', 'unknown')}.expected",
    )
    canonical_key = "max_returned_alternative_regret"
    legacy_key = "max_portfolio_first_action_minimax_regret"
    if (
        canonical_key in expected
        and legacy_key in expected
        and expected.get(canonical_key) != expected.get(legacy_key)
    ):
        raise RustParityError(
            "conflicting canonical and legacy returned-alternative regret limits"
        )
    key = canonical_key if canonical_key in expected else legacy_key
    value = expected.get(key)
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise RustParityError(
            f"{key} must be a non-negative integer"
        )
    return value


def _required_portfolio_first_actions(
    fixture: Mapping[str, Any],
    optimal_first_action_ids: tuple[str, ...],
    request: SolveRequest,
) -> tuple[str, ...]:
    expected = _mapping(
        fixture.get("expected", {}),
        f"fixture {fixture.get('id', 'unknown')}.expected",
    )
    explicit = expected.get("required_portfolio_first_action_ids")
    if explicit is not None:
        required = _string_array(
            explicit,
            f"fixture {fixture.get('id', 'unknown')}.expected.required_portfolio_first_action_ids",
        )
        if len(required) != len(set(required)):
            raise RustParityError("required portfolio first actions must be distinct")
        unknown = sorted(set(required) - set(optimal_first_action_ids))
        if unknown:
            raise RustParityError(
                f"required portfolio first actions are not oracle-optimal: {unknown}"
            )
        return required
    top_k = request.options.top_k or 3
    return optimal_first_action_ids[:top_k]


def _build_turnpair_case(
    fixture: Mapping[str, Any], suite_seed: int, suite_id: str
) -> ParityCase:
    request = request_from_turnpair_fixture(fixture, suite_seed)
    started = time.perf_counter()
    proof = prove_turnpair(request.state)
    if proof.abstained:
        raise RustParityError(
            f"exact fixture {fixture['id']} caused the turn-pair oracle to abstain: "
            + "; ".join(proof.reasons)
        )
    optimal = tuple(sorted(proof.optimal_first_action_ids))
    if not optimal:
        raise RustParityError(f"fixture {fixture['id']} has no turn-pair optimum")
    top1 = optimal[0]
    variants = _deduplicate_variants(
        _turnpair_final_variant(request.state, line)
        for line in proof.lines
        if line.minimax_value == proof.optimal_value and line.first_action_id == top1
    )
    elapsed = (time.perf_counter() - started) * 1000.0
    first_action_values = _turnpair_first_action_values(proof)
    safe_first_action_ids = _turnpair_safe_first_action_ids(proof)
    required_portfolio = _required_portfolio_first_actions(fixture, optimal, request)
    legal_action_ids = tuple(
        sorted(action.action_id for action in enumerate_oracle_actions(request.state))
    )
    legal_root_action_ids = tuple(
        sorted({_root_action_contract_id(action_id) for action_id in legal_action_ids})
    )
    return ParityCase(
        case_id=f"{suite_id}/{fixture['id']}",
        suite_id=suite_id,
        fixture_id=str(fixture["id"]),
        profile=PROFILE_FULL,
        request=request,
        legal_action_ids=legal_action_ids,
        optimal_first_action_ids=optimal,
        top1_action_id=top1,
        utility_kind="turnpair_minimax_utility",
        utility=proof.optimal_value,
        terminal_variants=variants,
        python_wall_time_ms=elapsed,
        first_action_values=first_action_values,
        required_portfolio_first_action_ids=required_portfolio,
        legal_root_action_ids=legal_root_action_ids,
        safe_first_action_ids=safe_first_action_ids,
        max_portfolio_first_action_minimax_regret=_max_portfolio_regret(fixture),
    )


def _oracle_omitted_hand_entity_ids(fixture: Mapping[str, Any]) -> tuple[str, ...]:
    """Return only explicitly fixture-owned, oracle-omitted hand entity IDs."""

    fixture_id = str(fixture.get("id") or "fixture")
    position = _mapping(fixture.get("position"), f"fixture {fixture_id}.position")
    omitted: set[str] = set()
    for label in ("friendly", "opponent"):
        player = _mapping(
            position.get(label, {}), f"fixture {fixture_id}.position.{label}"
        )
        hand = player.get("hand", [])
        if not isinstance(hand, Sequence) or isinstance(hand, (str, bytes, bytearray)):
            raise RustParityError(
                f"fixture {fixture_id}.position.{label}.hand must be an array"
            )
        for index, value in enumerate(hand):
            card = _mapping(
                value, f"fixture {fixture_id}.position.{label}.hand[{index}]"
            )
            if card.get("oracle_omit") is not True:
                continue
            entity_id = card.get("entity_id")
            if isinstance(entity_id, bool) or not isinstance(entity_id, (str, int)):
                raise RustParityError(
                    f"oracle_omit card in fixture {fixture_id} must have an explicit entity_id"
                )
            omitted.add(str(entity_id))
    return tuple(sorted(omitted))


def _build_hdt_case(
    fixture: Mapping[str, Any], suite_seed: int, suite_id: str
) -> ParityCase:
    request = oracle_request_from_fixture(fixture, suite_seed)
    request_wire_payload = candidate_wire_request_from_fixture(fixture, suite_seed)
    started = time.perf_counter()
    proof = prove_turnpair(request.state, allow_point_effects=True)
    if proof.abstained:
        raise RustParityError(
            f"scoped fixture {fixture['id']} caused the point-effect oracle to abstain: "
            + "; ".join(proof.reasons)
        )
    optimal = tuple(sorted(proof.optimal_first_action_ids))
    if not optimal:
        raise RustParityError(f"fixture {fixture['id']} has no HDT point-effect optimum")
    top1 = optimal[0]
    variants = _deduplicate_variants(
        _turnpair_final_variant(request.state, line)
        for line in proof.lines
        if line.minimax_value == proof.optimal_value and line.first_action_id == top1
    )
    elapsed = (time.perf_counter() - started) * 1000.0
    first_action_values = _turnpair_first_action_values(proof)
    safe_first_action_ids = _turnpair_safe_first_action_ids(proof)
    required_portfolio = _required_portfolio_first_actions(fixture, optimal, request)
    ignored_unsupported_hand_entity_ids = _oracle_omitted_hand_entity_ids(fixture)
    legal_action_ids = tuple(
        sorted(action.action_id for action in enumerate_oracle_actions(request.state))
    )
    legal_root_action_ids = tuple(
        sorted(
            {_root_action_contract_id(action_id) for action_id in legal_action_ids}
            | {
                f"play_card:{entity_id}:"
                for entity_id in ignored_unsupported_hand_entity_ids
            }
        )
    )
    return ParityCase(
        case_id=f"{suite_id}/{fixture['id']}",
        suite_id=suite_id,
        fixture_id=str(fixture["id"]),
        profile=PROFILE_FULL,
        request=request,
        legal_action_ids=legal_action_ids,
        optimal_first_action_ids=optimal,
        top1_action_id=top1,
        utility_kind="turnpair_minimax_utility",
        utility=proof.optimal_value,
        terminal_variants=variants,
        python_wall_time_ms=elapsed,
        request_wire_payload=request_wire_payload,
        ignored_unsupported_hand_entity_ids=ignored_unsupported_hand_entity_ids,
        first_action_values=first_action_values,
        required_portfolio_first_action_ids=required_portfolio,
        legal_root_action_ids=legal_root_action_ids,
        safe_first_action_ids=safe_first_action_ids,
        max_portfolio_first_action_minimax_regret=_max_portfolio_regret(fixture),
    )


def build_profile_cases(profile: str) -> tuple[tuple[ParityCase, ...], tuple[Path, ...]]:
    """Build the immutable fixture set and independent expectations for a profile."""

    if profile == PROFILE_COMBAT:
        suite = load_evaluation_suite(TURN_FIXTURES)
        fixtures = [fixture for fixture in suite["fixtures"] if fixture["scope"] == "exact"]
        cases = tuple(
            _build_combat_case(fixture, int(suite["seed"]), str(suite["suite_id"]))
            for fixture in fixtures
        )
        return cases, (TURN_FIXTURES,)
    if profile == PROFILE_FULL:
        turnpair = load_turnpair_suite(TURNPAIR_FIXTURES)
        hdt = load_hdt_rule_suite(HDT_RULE_FIXTURES)
        turnpair_cases = tuple(
            _build_turnpair_case(
                fixture,
                int(turnpair["seed"]),
                str(turnpair["suite_id"]),
            )
            for fixture in turnpair["fixtures"]
            if fixture["scope"] == "exact"
        )
        hdt_cases = tuple(
            _build_hdt_case(fixture, int(hdt["seed"]), str(hdt["suite_id"]))
            for fixture in hdt["fixtures"]
            if fixture["scope"] in {"exact", "scoped_lethal"}
        )
        return (*turnpair_cases, *hdt_cases), (TURNPAIR_FIXTURES, HDT_RULE_FIXTURES)
    raise RustParityError(
        f"unknown parity profile {profile!r}; expected one of {', '.join(SUPPORTED_PROFILES)}"
    )


def discover_rust_binary(explicit: str | Path | None = None) -> Path | None:
    """Resolve only explicit/env paths or Rust build outputs, never a Python worker."""

    if explicit is not None:
        path = Path(explicit).expanduser()
        return path.resolve() if path.is_file() else None
    for name in ("METACOMPANION_RUST_SOLVER", "METACOMPANION_RUST_SOLVER_PATH"):
        raw = os.environ.get(name, "").strip()
        if raw:
            path = Path(raw).expanduser()
            return path.resolve() if path.is_file() else None
    binary_names = ("metacompanion-solver.exe", "metacompanion-solver")
    for configuration in ("release", "debug"):
        for name in binary_names:
            candidate = REPO_ROOT / "solver-rust" / "target" / configuration / name
            if candidate.is_file():
                return candidate.resolve()
    return None


def invoke_rust_case(
    binary: Path,
    case: ParityCase,
    *,
    timeout_seconds: float = 30.0,
    subcommand: str = "parity-one",
) -> RustInvocation:
    command = [str(binary), subcommand, "--profile", case.profile]
    payload = _canonical_json(case.request_envelope()) + "\n"
    started = time.perf_counter()
    try:
        completed = subprocess.run(
            command,
            input=payload,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_seconds,
            check=False,
            shell=False,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise RustParityError(f"could not execute Rust parity case {case.case_id}: {exc}") from exc
    process_wall = (time.perf_counter() - started) * 1000.0
    stderr = completed.stderr.strip()
    if completed.returncode != 0:
        suffix = f": {stderr[:2000]}" if stderr else ""
        raise RustParityError(
            f"Rust parity case {case.case_id} exited {completed.returncode}{suffix}"
        )
    stdout = completed.stdout.strip()
    if not stdout:
        raise RustParityError(f"Rust parity case {case.case_id} returned empty stdout")
    try:
        result = json.loads(stdout)
    except json.JSONDecodeError as exc:
        raise RustParityError(
            f"Rust parity case {case.case_id} returned non-JSON stdout"
        ) from exc
    if not isinstance(result, dict):
        raise RustParityError(f"Rust parity case {case.case_id} result must be an object")
    return RustInvocation(result, process_wall, stderr)


def _string_array(value: Any, path: str) -> tuple[str, ...]:
    if not isinstance(value, list) or any(not isinstance(item, str) for item in value):
        raise RustParityError(f"{path} must be an array of strings")
    return tuple(value)


def _mapping(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise RustParityError(f"{path} must be an object")
    return value


def _integer(value: Any, path: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise RustParityError(f"{path} must be an integer")
    return value


def _root_action_contract_id(action_id: str) -> str:
    return "end_turn" if action_id == "end_turn::" else action_id


def _compare_full_portfolio(
    case: ParityCase,
    result: Mapping[str, Any],
) -> list[str]:
    errors: list[str] = []
    try:
        portfolio = _mapping(result.get("portfolio"), "portfolio")
    except RustParityError as exc:
        return [str(exc)]
    if portfolio.get("model") != ROOT_ACTION_PORTFOLIO_MODEL:
        errors.append(
            f"portfolio.model={portfolio.get('model')!r}, "
            f"expected {ROOT_ACTION_PORTFOLIO_MODEL!r}"
        )

    root_ids: dict[str, tuple[str, ...]] = {}
    for prefix in ("legal", "generated", "response_verified"):
        field_name = f"{prefix}_first_action_ids"
        try:
            values = _string_array(portfolio.get(field_name), f"portfolio.{field_name}")
            if values != tuple(sorted(set(values))):
                errors.append(f"portfolio.{field_name} must be sorted and distinct")
            root_ids[prefix] = values
        except RustParityError as exc:
            errors.append(str(exc))
            root_ids[prefix] = ()
    counts: dict[str, int] = {}
    for prefix in ("legal", "generated", "response_verified"):
        field_name = f"{prefix}_first_action_count"
        try:
            value = _integer(portfolio.get(field_name), f"portfolio.{field_name}")
            if value < 0:
                errors.append(f"portfolio.{field_name} must be non-negative")
            counts[prefix] = value
            if value != len(root_ids[prefix]):
                errors.append(
                    f"portfolio.{field_name}={value} does not equal "
                    f"len({prefix}_first_action_ids)={len(root_ids[prefix])}"
                )
        except RustParityError as exc:
            errors.append(str(exc))
    try:
        missing = _string_array(
            portfolio.get("missing_first_action_ids"),
            "portfolio.missing_first_action_ids",
        )
    except RustParityError as exc:
        errors.append(str(exc))
        missing = ()
    if missing != tuple(sorted(set(missing))):
        errors.append("portfolio.missing_first_action_ids must be sorted and distinct")
    complete = portfolio.get("root_action_coverage_complete")
    if not isinstance(complete, bool):
        errors.append("portfolio.root_action_coverage_complete must be boolean")
    optimality_proven = portfolio.get("portfolio_optimality_proven")
    if not isinstance(optimality_proven, bool):
        errors.append("portfolio.portfolio_optimality_proven must be boolean")

    legal_ids = set(root_ids["legal"])
    generated_ids = set(root_ids["generated"])
    verified_ids = set(root_ids["response_verified"])
    if tuple(root_ids["legal"]) != case.legal_root_action_ids:
        errors.append(
            "portfolio legal_first_action_ids differ from independent oracle roots: "
            f"expected={list(case.legal_root_action_ids)!r}, "
            f"actual={list(root_ids['legal'])!r}"
        )
    if not generated_ids.issubset(legal_ids):
        errors.append("portfolio generated_first_action_ids are not a subset of legal roots")
    if not verified_ids.issubset(generated_ids):
        errors.append(
            "portfolio response_verified_first_action_ids are not a subset of generated roots"
        )
    expected_missing = tuple(sorted(legal_ids - verified_ids))
    if missing != expected_missing:
        errors.append(
            "portfolio missing_first_action_ids must equal legal roots minus "
            f"response-verified roots: expected={list(expected_missing)!r}, "
            f"actual={list(missing)!r}"
        )
    derived_complete = bool(
        generated_ids == legal_ids and verified_ids == legal_ids and not missing
    )
    if isinstance(complete, bool) and complete != derived_complete:
        errors.append("portfolio root_action_coverage_complete is inconsistent with root IDs")

    scoped = bool(case.ignored_unsupported_hand_entity_ids)
    if not scoped:
        if generated_ids != legal_ids or verified_ids != legal_ids:
            errors.append("exact portfolio must generate and response-verify every legal root")
        if missing:
            errors.append("exact portfolio must not report missing root actions")
        if complete is not True:
            errors.append("exact portfolio root coverage is not complete")
        if optimality_proven is not True:
            errors.append("exact portfolio optimality is not proven")
    else:
        if complete is not False:
            errors.append("scoped portfolio with an unsupported alternative must be incomplete")
        if optimality_proven is not False:
            errors.append("scoped portfolio must not claim proven portfolio optimality")

    alternatives_raw = portfolio.get("alternatives")
    if not isinstance(alternatives_raw, list):
        errors.append("portfolio.alternatives must be an array")
        alternatives_raw = []
    top_k = case.request.options.top_k or 3
    if len(alternatives_raw) > top_k:
        errors.append(f"portfolio has {len(alternatives_raw)} alternatives, top_k is {top_k}")
    values = dict(case.first_action_values)
    seen: set[str] = set()
    alternative_contracts: dict[str, tuple[Any, Any]] = {}
    for index, raw in enumerate(alternatives_raw):
        try:
            alternative = _mapping(raw, f"portfolio.alternatives[{index}]")
        except RustParityError as exc:
            errors.append(str(exc))
            continue
        first_action_id = alternative.get("first_action_id")
        if not isinstance(first_action_id, str) or not first_action_id:
            errors.append(f"portfolio.alternatives[{index}].first_action_id must be non-empty")
            continue
        if first_action_id in seen:
            errors.append(f"portfolio contains duplicate first action {first_action_id!r}")
        seen.add(first_action_id)
        if first_action_id not in values:
            errors.append(
                f"portfolio first action {first_action_id!r} is absent from independent oracle roots"
            )
            continue
        regret = alternative.get("verified_portfolio_regret")
        kind = alternative.get("alternative_kind")
        if kind not in {"co_optimal", "near_optimal", "best_found", "backup", "fallback"}:
            errors.append(
                f"portfolio.alternatives[{index}].alternative_kind={kind!r} is invalid"
            )
        if scoped:
            if regret is not None:
                errors.append("scoped best-found alternative must report null verified regret")
            if kind != "best_found":
                errors.append("scoped proven lethal must be classified as best_found")
        else:
            expected_regret = case.utility - values[first_action_id]
            if isinstance(regret, bool) or not isinstance(regret, int):
                errors.append(
                    f"portfolio.alternatives[{index}].verified_portfolio_regret must be an integer"
                )
            elif regret != expected_regret:
                errors.append(
                    f"portfolio first action {first_action_id!r} regret={regret}, "
                    f"expected {expected_regret}"
                )
            expected_kind = (
                "co_optimal"
                if optimality_proven is True and complete is True and expected_regret == 0
                else "near_optimal"
                if (
                    optimality_proven is True
                    and complete is True
                    and expected_regret <= NEAR_OPTIMAL_REGRET_THRESHOLD
                )
                else "best_found"
                if expected_regret == 0
                else "backup"
            )
            if kind != expected_kind:
                errors.append(
                    f"portfolio first action {first_action_id!r} kind={kind!r}, "
                    f"expected {expected_kind!r}"
                )
            maximum_regret = case.max_portfolio_first_action_minimax_regret
            if maximum_regret is not None and expected_regret > maximum_regret:
                errors.append(
                    f"portfolio first action {first_action_id!r} regret={expected_regret} "
                    f"exceeds fixture maximum {maximum_regret}"
                )
        if kind == "co_optimal" and not (
            complete is True and optimality_proven is True and regret == 0
        ):
            errors.append(
                "co_optimal requires complete root coverage, proven portfolio optimality, "
                "and zero regret"
            )
        alternative_contracts[first_action_id] = (regret, kind)

    missing_required = sorted(
        set(case.required_portfolio_first_action_ids) - set(alternative_contracts)
    )
    if missing_required:
        errors.append(f"portfolio misses required co-optimal first actions: {missing_required}")
    if not scoped:
        for first_action_id in case.required_portfolio_first_action_ids:
            if alternative_contracts.get(first_action_id) != (0, "co_optimal"):
                errors.append(
                    f"required first action {first_action_id!r} is not a zero-regret co_optimal"
                )
    if case.safe_first_action_ids:
        unsafe_returned = sorted(set(alternative_contracts) - set(case.safe_first_action_ids))
        if unsafe_returned:
            errors.append(
                f"portfolio returns known counterlethal roots despite safe alternatives: "
                f"{unsafe_returned}"
            )
    elif not alternative_contracts:
        errors.append("all roots are counterlethal but portfolio returned no least-bad choice")
    return errors


def compare_case_result(case: ParityCase, result: Mapping[str, Any]) -> list[str]:
    """Return every semantic parity mismatch; malformed contracts fail closed."""

    errors: list[str] = []
    if result.get("schema") != RESULT_SCHEMA:
        errors.append(f"schema={result.get('schema')!r}, expected {RESULT_SCHEMA!r}")
    if result.get("case_id") != case.case_id:
        errors.append(f"case_id={result.get('case_id')!r}, expected {case.case_id!r}")
    if result.get("status") != "ok":
        errors.append(f"status={result.get('status')!r}, expected 'ok'")

    try:
        legal = _string_array(result.get("legal_action_ids"), "legal_action_ids")
        if legal != tuple(sorted(legal)):
            errors.append("legal_action_ids are not sorted")
        if legal != case.legal_action_ids:
            errors.append(
                "legal_action_ids differ: "
                f"expected={list(case.legal_action_ids)!r}, actual={list(legal)!r}"
            )
    except RustParityError as exc:
        errors.append(str(exc))

    top1 = result.get("top1_action_id")
    if top1 != case.top1_action_id:
        errors.append(f"top1_action_id={top1!r}, expected {case.top1_action_id!r}")
    utility = result.get("minimax_utility")
    if utility != case.utility or isinstance(utility, bool):
        errors.append(f"minimax_utility={utility!r}, expected {case.utility!r}")
    if case.profile == PROFILE_FULL:
        errors.extend(_compare_full_portfolio(case, result))

    friendly_actions: tuple[str, ...] = ()
    opponent_actions: tuple[str, ...] = ()
    try:
        friendly_actions = _string_array(result.get("action_ids"), "action_ids")
    except RustParityError as exc:
        errors.append(str(exc))
    if "opponent_action_ids" in result:
        try:
            opponent_actions = _string_array(
                result.get("opponent_action_ids"), "opponent_action_ids"
            )
        except RustParityError as exc:
            errors.append(str(exc))
    if (
        friendly_actions
        and friendly_actions[0] != case.top1_action_id
        and _root_action_contract_id(friendly_actions[0]) != case.top1_action_id
    ):
        errors.append(
            f"action_ids first action={friendly_actions[0]!r}, expected Top1 {case.top1_action_id!r}"
        )

    actual_state: dict[str, Any] | None = None
    actual_hash = ""
    actual_health: tuple[int, int] | None = None
    actual_armor: tuple[int, int] | None = None
    try:
        terminal = _mapping(result.get("terminal"), "terminal")
        state = GameState.from_dict(terminal.get("state"), "terminal.state")
        actual_state = state.to_dict()
        actual_hash = _semantic_state_hash(case, actual_state)
        health = _mapping(terminal.get("hero_health"), "terminal.hero_health")
        armor = _mapping(terminal.get("hero_armor"), "terminal.hero_armor")
        actual_health = (
            _integer(health.get("friendly"), "terminal.hero_health.friendly"),
            _integer(health.get("opponent"), "terminal.hero_health.opponent"),
        )
        actual_armor = (
            _integer(armor.get("friendly"), "terminal.hero_armor.friendly"),
            _integer(armor.get("opponent"), "terminal.hero_armor.opponent"),
        )
        state_health = (
            state.friendly.hero.current_health,
            state.opponent.hero.current_health,
        )
        state_armor = (state.friendly.armor, state.opponent.armor)
        if actual_health != state_health:
            errors.append(
                f"terminal hero_health={actual_health!r} disagrees with terminal state={state_health!r}"
            )
        if actual_armor != state_armor:
            errors.append(
                f"terminal hero_armor={actual_armor!r} disagrees with terminal state={state_armor!r}"
            )
    except (RustParityError, ValueError) as exc:
        errors.append(str(exc))

    allowed = [
        variant
        for variant in case.terminal_variants
        if variant.friendly_action_ids == friendly_actions
        and variant.opponent_action_ids == opponent_actions
    ]
    if not allowed:
        errors.append(
            "action line is not an independently optimal terminal variant: "
            f"friendly={list(friendly_actions)!r}, opponent={list(opponent_actions)!r}"
        )
        allowed = list(case.terminal_variants)
    if actual_state is not None and all(
        _semantic_state_hash(case, variant.state) != actual_hash for variant in allowed
    ):
        expected_health = sorted(
            {
                (variant.friendly_health, variant.opponent_health)
                for variant in allowed
            }
        )
        expected_hashes = sorted(
            {_semantic_state_hash(case, variant.state) for variant in allowed}
        )
        errors.append(
            "terminal state differs: "
            f"actual_sha256={actual_hash}, expected_sha256={expected_hashes!r}, "
            f"actual_health={actual_health!r}, expected_health={expected_health!r}"
        )

    if case.profile == PROFILE_COMBAT:
        try:
            proof = _mapping(result.get("proof"), "proof")
            if proof.get("has_lethal") is not case.has_lethal:
                errors.append(
                    f"proof.has_lethal={proof.get('has_lethal')!r}, expected {case.has_lethal!r}"
                )
            winning = _string_array(
                proof.get("winning_first_action_ids"),
                "proof.winning_first_action_ids",
            )
            if winning != tuple(sorted(winning)):
                errors.append("proof.winning_first_action_ids are not sorted")
            if winning != case.winning_first_action_ids:
                errors.append(
                    "proof.winning_first_action_ids differ: "
                    f"expected={list(case.winning_first_action_ids)!r}, actual={list(winning)!r}"
                )
            explored = proof.get("explored_state_count")
            if isinstance(explored, bool) or not isinstance(explored, int) or explored < 0:
                errors.append("proof.explored_state_count must be a non-negative integer")
        except RustParityError as exc:
            errors.append(str(exc))

    reported_wall = result.get("wall_time_ms")
    if not _numeric(reported_wall) or float(reported_wall) < 0:
        errors.append("wall_time_ms must be a finite non-negative number")
    return errors


def _binary_descriptor(path: Path | None) -> dict[str, Any]:
    if path is None:
        return {"available": False, "path": "", "sha256": ""}
    return {
        "available": True,
        "path": str(path),
        "sha256": _file_sha256(path),
    }


def _base_report(
    profile: str,
    binary: Path | None,
    fixture_paths: Sequence[Path],
) -> dict[str, Any]:
    return {
        "schema": REPORT_SCHEMA,
        "profile": profile,
        "passed": False,
        "status": "not_run",
        "binary": _binary_descriptor(binary),
        "fixture_files": [
            {"path": str(path), "sha256": _file_sha256(path)}
            for path in fixture_paths
        ],
        "metrics": {},
        "cases": [],
        "caveat": (
            "Parity proves only the fixed versioned fixture profile. It does not prove "
            "complete Hearthstone rules, calibrated win probability, or globally optimal play."
        ),
        "timing_note": (
            "Rust-reported engine time excludes process startup. Python oracle construction "
            "and Rust planning enforce the same outcomes but may traverse states differently; "
            "the displayed speedup is diagnostic and is not a release threshold."
        ),
    }


def run_gate(
    profile: str,
    binary: Path | None,
    *,
    timeout_seconds: float = 30.0,
    subcommand: str = "parity-one",
) -> dict[str, Any]:
    cases, fixture_paths = build_profile_cases(profile)
    report = _base_report(profile, binary, fixture_paths)
    if binary is None or not binary.is_file():
        report["status"] = "missing_binary"
        report["error"] = (
            "Rust solver binary was not found. Set METACOMPANION_RUST_SOLVER or pass --binary."
        )
        report["metrics"] = {
            "fixture_count": len(cases),
            "passed_fixture_count": 0,
            "failed_fixture_count": len(cases),
        }
        return report

    case_reports: list[dict[str, Any]] = []
    process_walls: list[float] = []
    reported_walls: list[float] = []
    for case in cases:
        item: dict[str, Any] = {
            "case_id": case.case_id,
            "suite_id": case.suite_id,
            "fixture_id": case.fixture_id,
            "python_wall_time_ms": round(case.python_wall_time_ms, 3),
            "utility_kind": case.utility_kind,
            "expected_utility": case.utility,
            "expected_top1_action_id": case.top1_action_id,
            "expected_legal_action_count": len(case.legal_action_ids),
            "expected_terminal_variant_count": len(case.terminal_variants),
        }
        try:
            invocation = invoke_rust_case(
                binary,
                case,
                timeout_seconds=timeout_seconds,
                subcommand=subcommand,
            )
            errors = compare_case_result(case, invocation.payload)
            reported_wall = invocation.payload.get("wall_time_ms")
            if _numeric(reported_wall) and float(reported_wall) >= 0:
                reported_walls.append(float(reported_wall))
                item["rust_reported_wall_time_ms"] = round(float(reported_wall), 3)
            item["rust_process_wall_time_ms"] = round(invocation.process_wall_time_ms, 3)
            process_walls.append(invocation.process_wall_time_ms)
            item["passed"] = not errors
            item["errors"] = errors
        except RustParityError as exc:
            item["passed"] = False
            item["errors"] = [str(exc)]
        case_reports.append(item)

    passed_count = sum(1 for item in case_reports if item["passed"])
    failed_count = len(case_reports) - passed_count
    python_walls = [case.python_wall_time_ms for case in cases]
    engine_speedup = None
    if reported_walls and sum(reported_walls) > 0:
        engine_speedup = round(sum(python_walls) / sum(reported_walls), 6)
    report["passed"] = failed_count == 0 and len(case_reports) == len(cases)
    report["status"] = "passed" if report["passed"] else "parity_mismatch"
    report["metrics"] = {
        "fixture_count": len(cases),
        "passed_fixture_count": passed_count,
        "failed_fixture_count": failed_count,
        "python_oracle_wall_time_ms_total": round(sum(python_walls), 3),
        "python_oracle_wall_time_ms_p50": _percentile(python_walls, 0.5),
        "python_oracle_wall_time_ms_p95": _percentile(python_walls, 0.95),
        "rust_process_wall_time_ms_total": round(sum(process_walls), 3),
        "rust_process_wall_time_ms_p50": _percentile(process_walls, 0.5),
        "rust_process_wall_time_ms_p95": _percentile(process_walls, 0.95),
        "rust_reported_wall_time_ms_total": round(sum(reported_walls), 3),
        "rust_reported_wall_time_ms_p50": _percentile(reported_walls, 0.5),
        "rust_reported_wall_time_ms_p95": _percentile(reported_walls, 0.95),
        "engine_wall_time_speedup": engine_speedup,
        "timing_threshold_enforced": False,
    }
    report["cases"] = case_reports
    return report


def write_report(report: Mapping[str, Any], output: str | Path) -> None:
    path = Path(output)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Compare a Rust solver against fixed independent Python-oracle profiles."
    )
    parser.add_argument("--profile", choices=SUPPORTED_PROFILES, required=True)
    parser.add_argument("--binary", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--timeout-seconds", type=float, default=30.0)
    parser.add_argument("--subcommand", default="parity-one")
    parser.add_argument(
        "--require-binary",
        action="store_true",
        help=(
            "Explicit release-gate marker. Missing binaries are non-zero even without "
            "this flag; it exists so release scripts document their intent."
        ),
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
        print("error: --timeout-seconds must be a finite positive number", file=sys.stderr)
        return 2
    explicit_missing = args.binary is not None and not args.binary.is_file()
    binary = discover_rust_binary(args.binary)
    try:
        report = run_gate(
            args.profile,
            binary,
            timeout_seconds=args.timeout_seconds,
            subcommand=args.subcommand,
        )
    except (OSError, ValueError, TurnPairEvaluationError, RustParityError) as exc:
        report = {
            "schema": REPORT_SCHEMA,
            "profile": args.profile,
            "passed": False,
            "status": "gate_error",
            "error": str(exc),
            "binary": _binary_descriptor(binary),
            "metrics": {},
            "cases": [],
        }
        if args.output is not None:
            write_report(report, args.output)
        print(json.dumps(report, ensure_ascii=False, indent=2))
        return 2
    if explicit_missing:
        report["error"] = f"explicit Rust solver binary does not exist: {args.binary}"
    if args.output is not None:
        write_report(report, args.output)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    if report["passed"]:
        return 0
    if report["status"] == "missing_binary":
        return 4
    return 3


if __name__ == "__main__":
    raise SystemExit(main())
