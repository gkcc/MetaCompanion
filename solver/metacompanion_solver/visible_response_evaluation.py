from __future__ import annotations

import hashlib
import json
import math
import time
from pathlib import Path
from typing import Any, Callable, Iterator, Mapping, Sequence

from .rust_worker_client import RustWorkerClient, sha256_file


VISIBLE_RESPONSE_SUITE_ID = "visible-response-v1"
VISIBLE_RESPONSE_SCHEMA_VERSION = 1
VISIBLE_RESPONSE_REPORT_SCHEMA = "visible-response-report-v1"


class VisibleResponseEvaluationError(ValueError):
    pass


def _mapping(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise VisibleResponseEvaluationError(f"{path} must be an object")
    return value


def _array(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list):
        raise VisibleResponseEvaluationError(f"{path} must be an array")
    return value


def _string_array(value: Any, path: str, *, nonempty: bool = False) -> tuple[str, ...]:
    items = _array(value, path)
    if any(not isinstance(item, str) or not item for item in items):
        raise VisibleResponseEvaluationError(f"{path} must contain non-empty strings")
    result = tuple(items)
    if nonempty and not result:
        raise VisibleResponseEvaluationError(f"{path} must not be empty")
    if len(result) != len(set(result)):
        raise VisibleResponseEvaluationError(f"{path} must not contain duplicates")
    return result


def _positive_integer(value: Any, path: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
        raise VisibleResponseEvaluationError(f"{path} must be a positive integer")
    return value


def _entity_id(value: Any) -> str:
    if isinstance(value, bool) or value is None:
        return ""
    if isinstance(value, (int, str)):
        return str(value).strip()
    return ""


def _canonical_hash(value: Any) -> str:
    payload = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _file_hash(path: Path) -> str:
    return sha256_file(path)


def _hidden_entity(value: Any) -> bool:
    raw = _mapping(value, "hidden entity")
    visibility = str(raw.get("visibility") or "").strip().lower()
    card_id = str(raw.get("card_id") or "").strip()
    card_type = str(raw.get("card_type") or "").strip().upper()
    return bool(
        "hidden" in visibility
        or raw.get("is_known") is False
        or raw.get("is_revealed") is False
        or not card_id
        or card_type in {"", "UNKNOWN", "INVALID"}
    )


def load_visible_response_suite(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    try:
        root = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise VisibleResponseEvaluationError(
            f"could not load visible-response fixture suite: {source}"
        ) from exc
    if not isinstance(root, dict):
        raise VisibleResponseEvaluationError("suite root must be an object")
    if root.get("schema_version") != VISIBLE_RESPONSE_SCHEMA_VERSION:
        raise VisibleResponseEvaluationError("unsupported visible-response schema_version")
    if root.get("suite_id") != VISIBLE_RESPONSE_SUITE_ID:
        raise VisibleResponseEvaluationError(
            f"suite_id must be {VISIBLE_RESPONSE_SUITE_ID!r}"
        )
    thresholds = _mapping(root.get("thresholds", {}), "suite.thresholds")
    minimum_fixture_count = _positive_integer(
        thresholds.get("min_fixture_count", 1), "suite.thresholds.min_fixture_count"
    )
    maximum_failures = thresholds.get("max_contract_failure_count", 0)
    if isinstance(maximum_failures, bool) or not isinstance(maximum_failures, int) or maximum_failures < 0:
        raise VisibleResponseEvaluationError(
            "suite.thresholds.max_contract_failure_count must be a non-negative integer"
        )
    fixtures = _array(root.get("fixtures"), "suite.fixtures")
    if len(fixtures) < minimum_fixture_count:
        raise VisibleResponseEvaluationError(
            "suite fixture count is below thresholds.min_fixture_count"
        )
    seen: set[str] = set()
    threat_fixture_count = 0
    unknown_source_fixture_count = 0
    approximation_fixture_count = 0
    for index, item in enumerate(fixtures):
        fixture = _mapping(item, f"suite.fixtures[{index}]")
        fixture_id = fixture.get("id")
        if not isinstance(fixture_id, str) or not fixture_id or fixture_id in seen:
            raise VisibleResponseEvaluationError("fixture IDs must be non-empty and unique")
        seen.add(fixture_id)
        request = _mapping(fixture.get("request"), f"fixture {fixture_id}.request")
        if request.get("api_version", "1.0") != "1.0":
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.request.api_version must be '1.0'"
            )
        if not isinstance(request.get("request_id"), str) or not request["request_id"]:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.request.request_id must be non-empty"
            )
        state = _mapping(request.get("state"), f"fixture {fixture_id}.request.state")
        if "friendly" in state or "player" not in state or "opponent" not in state:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} must contain a raw HDT player/opponent snapshot"
            )
        if not isinstance(state.get("state_id"), str) or not state["state_id"]:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.request.state.state_id must be non-empty"
            )
        expected = _mapping(fixture.get("expected"), f"fixture {fixture_id}.expected")
        if expected.get("status") != "partial":
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.expected.status must be 'partial'"
            )
        if expected.get("coverage_scope") != VISIBLE_RESPONSE_SUITE_ID:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.expected.coverage_scope must be "
                f"{VISIBLE_RESPONSE_SUITE_ID!r}"
            )
        if expected.get("score_kind") != "visible_response_heuristic_v1":
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.expected.score_kind must be "
                "'visible_response_heuristic_v1'"
            )
        minimum_recommendations = _positive_integer(
            expected.get("minimum_recommendation_count"),
            f"fixture {fixture_id}.expected.minimum_recommendation_count",
        )
        maximum_recommendations = _positive_integer(
            expected.get("maximum_recommendation_count"),
            f"fixture {fixture_id}.expected.maximum_recommendation_count",
        )
        if minimum_recommendations > maximum_recommendations:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} recommendation count bounds are inverted"
            )
        legal_ids = _string_array(
            expected.get("legal_first_action_ids"),
            f"fixture {fixture_id}.expected.legal_first_action_ids",
            nonempty=True,
        )
        top_ids = _string_array(
            expected.get("top_first_action_ids"),
            f"fixture {fixture_id}.expected.top_first_action_ids",
            nonempty=True,
        )
        allowed_ids = _string_array(
            expected.get("allowed_first_action_ids"),
            f"fixture {fixture_id}.expected.allowed_first_action_ids",
            nonempty=True,
        )
        if not set(top_ids).issubset(allowed_ids) or not set(allowed_ids).issubset(legal_ids):
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} top/allowed first actions must be subsets of legal actions"
            )
        prohibited_ids = _string_array(
            expected.get("prohibited_source_entity_ids"),
            f"fixture {fixture_id}.expected.prohibited_source_entity_ids",
        )
        player = _mapping(state["player"], f"fixture {fixture_id}.request.state.player")
        opponent = _mapping(state["opponent"], f"fixture {fixture_id}.request.state.opponent")
        player_hand = _array(player.get("hand", []), f"fixture {fixture_id}.player.hand")
        opponent_hand = _array(opponent.get("hand", []), f"fixture {fixture_id}.opponent.hand")
        if expected.get("requires_opponent_deck_nonempty") is not True:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} must require a non-empty opponent deck"
            )
        deck_size = opponent.get("deck_size", opponent.get("deck_count", 0))
        deck_count = opponent.get("deck_count", deck_size)
        if (
            isinstance(deck_size, bool)
            or not isinstance(deck_size, int)
            or deck_size <= 0
            or isinstance(deck_count, bool)
            or not isinstance(deck_count, int)
            or deck_count <= 0
        ):
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} must exercise opponent.deck_size/deck_count > 0"
            )
        if expected.get("requires_hidden_opponent_hand") is not True or not any(
            _hidden_entity(entity) for entity in opponent_hand
        ):
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} must exercise a hidden opponent hand entity"
            )
        unknown_friendly = {
            _entity_id(_mapping(entity, "friendly hand entity").get("entity_id"))
            for entity in player_hand
            if _hidden_entity(entity)
        }
        requires_unknown_source = expected.get(
            "requires_unknown_friendly_action_block"
        )
        if not isinstance(requires_unknown_source, bool):
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id}.expected.requires_unknown_friendly_action_block "
                "must be boolean"
            )
        if requires_unknown_source:
            unknown_source_fixture_count += 1
            if not prohibited_ids or not set(prohibited_ids).intersection(unknown_friendly):
                raise VisibleResponseEvaluationError(
                    f"fixture {fixture_id} must prohibit an unknown friendly hand entity"
                )
        elif prohibited_ids:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} has prohibited sources without enabling the unknown-source check"
            )
        if expected.get("requires_distinct_first_actions") is not True:
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} must require distinct returned first actions"
            )
        requires_threat = expected.get("requires_threat_priority")
        requires_approximation = expected.get("requires_vanilla_approximation")
        if not isinstance(requires_threat, bool) or not isinstance(
            requires_approximation, bool
        ):
            raise VisibleResponseEvaluationError(
                f"fixture {fixture_id} threat/approximation check flags must be boolean"
            )
        threat_fixture_count += int(requires_threat)
        approximation_fixture_count += int(requires_approximation)
    if threat_fixture_count < 1:
        raise VisibleResponseEvaluationError("suite must include a threat-priority fixture")
    if unknown_source_fixture_count < 1:
        raise VisibleResponseEvaluationError("suite must include an unknown-friendly-source fixture")
    if approximation_fixture_count < 1:
        raise VisibleResponseEvaluationError("suite must include a vanilla approximation fixture")
    return root


def _walk_key_values(value: Any, key: str) -> Iterator[Any]:
    if isinstance(value, Mapping):
        for child_key, child in value.items():
            if child_key == key:
                yield child
            yield from _walk_key_values(child, key)
    elif isinstance(value, list):
        for child in value:
            yield from _walk_key_values(child, key)


def _canonical_action_id(action: Mapping[str, Any]) -> str:
    action_id = str(action.get("action_id") or "").strip()
    kind = str(action.get("kind") or action.get("type") or "").strip().lower()
    source = _entity_id(action.get("source_entity_id"))
    target = _entity_id(action.get("target_entity_id"))
    if kind in {"end_turn", "end turn", "pass"}:
        expected = "end_turn::"
    elif kind and source:
        expected = f"{kind}:{source}:{target}"
    else:
        return ""
    return action_id if action_id == expected else ""


def _first_action_id(actions: Sequence[Mapping[str, Any]]) -> str:
    for action in actions:
        canonical = _canonical_action_id(action)
        if not canonical:
            return ""
        if canonical != "end_turn::":
            return canonical
    return "end_turn" if actions else ""


def _coverage_object(response: Mapping[str, Any]) -> Mapping[str, Any]:
    coverage = _mapping(response.get("coverage", {}), "response.coverage")
    details = coverage.get("details")
    if isinstance(details, Mapping) and isinstance(details.get("counterplay"), Mapping):
        return details["counterplay"]
    if isinstance(coverage.get("counterplay"), Mapping):
        return coverage["counterplay"]
    return {}


def _sorted_distinct_strings(value: Any, path: str, errors: list[str]) -> tuple[str, ...]:
    if not isinstance(value, list) or any(not isinstance(item, str) or not item for item in value):
        errors.append(f"{path} must be an array of non-empty strings")
        return ()
    result = tuple(value)
    if result != tuple(sorted(set(result))):
        errors.append(f"{path} must be sorted and distinct")
    return result


def compare_visible_response(
    fixture: Mapping[str, Any], response: Mapping[str, Any]
) -> tuple[list[str], dict[str, Any]]:
    fixture_id = str(fixture["id"])
    request = _mapping(fixture["request"], f"fixture {fixture_id}.request")
    expected = _mapping(fixture["expected"], f"fixture {fixture_id}.expected")
    errors: list[str] = []
    if response.get("api_version") != "1.0":
        errors.append("response.api_version must be '1.0'")
    if response.get("request_id") != request.get("request_id"):
        errors.append("response.request_id does not match the raw HDT request")
    state = _mapping(request["state"], f"fixture {fixture_id}.request.state")
    if response.get("state_id") != state.get("state_id"):
        errors.append("response.state_id does not match the raw HDT request")
    if response.get("status") != expected.get("status"):
        errors.append("visible-response fallback must return status='partial'")
    if response.get("is_final") is not True:
        errors.append("the completed partial fallback must explicitly return is_final=true")

    coverage = response.get("coverage")
    if not isinstance(coverage, Mapping):
        errors.append("response.coverage must be an object")
        coverage = {}
    if coverage.get("exact") is not False:
        errors.append("partial fallback must explicitly report coverage.exact=false")
    exact_scope = str(coverage.get("exact_scope") or "").strip().lower()
    if exact_scope != str(expected.get("coverage_scope") or "").strip().lower():
        errors.append("partial fallback must report the fixture-declared visible-response scope")
    if exact_scope in {"exact", "visible_generic_turnpair_v1", "oracle-turnpair-v1"}:
        errors.append("partial fallback exact_scope must not name an exact proof scope")

    forbidden_true_keys = (
        "exact",
        "is_response_verified",
        "response_search_complete",
        "search_complete",
        "response_line_complete",
        "root_action_coverage_complete",
        "portfolio_optimality_proven",
        "is_proven_lethal",
        "response_is_proven_lethal",
    )
    false_claim_count = 0
    for key in forbidden_true_keys:
        for value in _walk_key_values(response, key):
            if value is True:
                false_claim_count += 1
                errors.append(f"partial fallback must not claim {key}=true")
    for value in _walk_key_values(response, "verified_portfolio_regret"):
        if value is not None:
            false_claim_count += 1
            errors.append("partial fallback must not report verified_portfolio_regret")
    for value in _walk_key_values(response, "is_safe_after_response"):
        if value is not None:
            false_claim_count += 1
            errors.append("partial fallback must not report an after-response safety verdict")
    for value in _walk_key_values(response, "minimax_value"):
        if value is not None:
            false_claim_count += 1
            errors.append("partial fallback must not report a verified minimax value")

    counterplay = _coverage_object(response)
    expected_legal = tuple(sorted(_string_array(
        expected["legal_first_action_ids"],
        f"fixture {fixture_id}.expected.legal_first_action_ids",
    )))
    legal = _sorted_distinct_strings(
        counterplay.get("legal_first_action_ids"),
        "coverage.details.counterplay.legal_first_action_ids",
        errors,
    )
    generated = _sorted_distinct_strings(
        counterplay.get("generated_first_action_ids"),
        "coverage.details.counterplay.generated_first_action_ids",
        errors,
    )
    verified = _sorted_distinct_strings(
        counterplay.get("response_verified_first_action_ids"),
        "coverage.details.counterplay.response_verified_first_action_ids",
        errors,
    )
    missing = _sorted_distinct_strings(
        counterplay.get("missing_first_action_ids"),
        "coverage.details.counterplay.missing_first_action_ids",
        errors,
    )
    if legal != expected_legal:
        errors.append("reported legal first-action IDs differ from fixture expected")
    if not set(generated).issubset(set(legal)):
        errors.append("generated first-action IDs must be a subset of fixture legal IDs")
    if verified:
        errors.append("partial visible-response fallback must verify zero first actions")
    if missing != expected_legal:
        errors.append("missing first-action IDs must contain every unverified legal root")
    for prefix, values in (("legal", legal), ("generated", generated), ("response_verified", verified)):
        count = counterplay.get(f"{prefix}_first_action_count")
        if isinstance(count, bool) or not isinstance(count, int) or count != len(values):
            errors.append(f"{prefix}_first_action_count must match its canonical ID array")
    if counterplay.get("root_action_coverage_complete") is not False:
        errors.append("partial fallback must explicitly report incomplete root coverage")
    if counterplay.get("portfolio_optimality_proven") is not False:
        errors.append("partial fallback must explicitly deny portfolio optimality")
    if counterplay.get("search_complete") is not False:
        errors.append("partial fallback must explicitly report response search incomplete")

    recommendations_raw = response.get("recommendations")
    if not isinstance(recommendations_raw, list):
        errors.append("response.recommendations must be an array")
        recommendations_raw = []
    minimum = int(expected["minimum_recommendation_count"])
    maximum = int(expected["maximum_recommendation_count"])
    if not minimum <= len(recommendations_raw) <= maximum:
        errors.append(
            f"recommendation count {len(recommendations_raw)} is outside fixture bounds {minimum}..{maximum}"
        )
    allowed_first = set(_string_array(
        expected["allowed_first_action_ids"],
        f"fixture {fixture_id}.expected.allowed_first_action_ids",
    ))
    top_first = set(_string_array(
        expected["top_first_action_ids"],
        f"fixture {fixture_id}.expected.top_first_action_ids",
    ))
    prohibited_sources = set(_string_array(
        expected["prohibited_source_entity_ids"],
        f"fixture {fixture_id}.expected.prohibited_source_entity_ids",
    ))
    first_action_ids: list[str] = []
    unknown_source_violation = False
    for index, item in enumerate(recommendations_raw):
        if not isinstance(item, Mapping):
            errors.append(f"recommendations[{index}] must be an object")
            continue
        if item.get("rank") != index + 1:
            errors.append(f"recommendations[{index}].rank must be {index + 1}")
        actions_raw = item.get("actions")
        if not isinstance(actions_raw, list) or not actions_raw:
            errors.append(f"recommendations[{index}].actions must be non-empty")
            continue
        actions: list[Mapping[str, Any]] = []
        for action_index, action in enumerate(actions_raw):
            if not isinstance(action, Mapping):
                errors.append(
                    f"recommendations[{index}].actions[{action_index}] must be an object"
                )
                continue
            actions.append(action)
            if action.get("index") != action_index + 1:
                errors.append(
                    f"recommendations[{index}].actions must use contiguous one-based indices"
                )
            canonical = _canonical_action_id(action)
            if not canonical:
                errors.append(
                    f"recommendations[{index}].actions[{action_index}] has an invalid canonical action_id"
                )
            source = _entity_id(action.get("source_entity_id"))
            if source and source in prohibited_sources:
                unknown_source_violation = True
                errors.append(
                    f"unknown friendly entity {source!r} generated an action"
                )
        first_action_id = _first_action_id(actions)
        if not first_action_id:
            errors.append(f"recommendations[{index}] has no canonical first action")
        else:
            first_action_ids.append(first_action_id)
            if first_action_id not in allowed_first:
                errors.append(
                    f"recommendations[{index}] first action {first_action_id!r} is not fixture-allowed"
                )
        if item.get("alternative_kind") != "fallback":
            errors.append(
                f"recommendations[{index}] must be classified as unverified fallback"
            )
        if item.get("score_kind") != expected.get("score_kind"):
            errors.append(
                f"recommendations[{index}] must use the fixture-declared heuristic score kind"
            )
        if item.get("verified_portfolio_regret") is not None:
            errors.append(
                f"recommendations[{index}] must not carry verified portfolio regret"
            )
        if item.get("is_response_verified") not in (None, False):
            errors.append(f"recommendations[{index}] must remain response-unverified")
        if item.get("response_search_complete") not in (None, False):
            errors.append(f"recommendations[{index}] must not claim complete response search")
        if item.get("is_safe_after_response") is not None:
            errors.append(f"recommendations[{index}] must not carry a safety verdict")
        if item.get("opponent_response") is not None:
            errors.append(
                f"recommendations[{index}] must not fabricate a canonical opponent response"
            )
        if str(item.get("response_scope") or "").strip() or str(
            item.get("response_kind") or ""
        ).strip():
            errors.append(
                f"recommendations[{index}] must not carry verified-response scope metadata"
            )
        if item.get("counterplay") is not None:
            errors.append(
                f"recommendations[{index}] must not carry a verified counterplay object"
            )
        if str(item.get("proof_kind") or "").strip() or str(item.get("proof_scope") or "").strip():
            errors.append(f"recommendations[{index}] must not carry proof metadata")
    requires_threat = expected.get("requires_threat_priority") is True
    top_expected_passed = bool(first_action_ids and first_action_ids[0] in top_first)
    if first_action_ids and not top_expected_passed:
        errors.append(
            "fixture-declared top first action was not prioritized by the approximate ranker"
        )
    duplicate_first_action_count = len(first_action_ids) - len(set(first_action_ids))
    if duplicate_first_action_count:
        errors.append("returned recommendations contain duplicate first actions")
    if not set(first_action_ids).issubset(set(generated)):
        errors.append("every returned first action must be listed as generated coverage")

    warnings = response.get("warnings")
    warning_text = " ".join(str(value).lower() for value in warnings) if isinstance(warnings, list) else ""
    if not warning_text or not any(token in warning_text for token in ("隐藏", "未知", "hidden", "unknown")):
        errors.append("partial fallback must include an explicit hidden-information warning")
    approximation_passed = bool(
        response.get("status") == "partial"
        and coverage.get("exact") is False
        and recommendations_raw
        and all(
            isinstance(item, Mapping)
            and item.get("score_kind") == expected.get("score_kind")
            for item in recommendations_raw
        )
    )
    return errors, {
        "partial_status_passed": response.get("status") == expected.get("status"),
        "false_claim_count": false_claim_count,
        "first_action_ids": first_action_ids,
        "requires_threat_priority": requires_threat,
        "top_threat_priority_passed": top_expected_passed,
        "requires_unknown_friendly_action_block": expected.get(
            "requires_unknown_friendly_action_block"
        )
        is True,
        "distinct_first_actions_passed": len(first_action_ids) == len(set(first_action_ids)),
        "unknown_friendly_action_blocked": not unknown_source_violation,
        "requires_vanilla_approximation": expected.get(
            "requires_vanilla_approximation"
        )
        is True,
        "vanilla_approximation_passed": approximation_passed,
        "duplicate_first_action_count": duplicate_first_action_count,
    }


def _percentile(values: Sequence[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(float(value) for value in values)
    index = min(len(ordered) - 1, max(0, math.ceil((len(ordered) - 1) * fraction)))
    return round(ordered[index], 3)


def evaluate_visible_response_suite(
    fixture_path: str | Path,
    solve_wire: Callable[[Mapping[str, Any]], Mapping[str, Any]],
    *,
    binary: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    suite = load_visible_response_suite(fixture_path)
    details: list[dict[str, Any]] = []
    latencies: list[float] = []
    passed_count = 0
    contract_failure_count = 0
    false_claim_count = 0
    threat_fixture_count = 0
    threat_priority_count = 0
    distinct_fixture_count = 0
    distinct_count = 0
    unknown_source_fixture_count = 0
    unknown_blocked_count = 0
    approximation_fixture_count = 0
    approximation_passed_count = 0
    duplicate_first_action_count = 0
    partial_status_count = 0
    for fixture in suite["fixtures"]:
        started = time.perf_counter()
        errors: list[str] = []
        assessment: dict[str, Any] = {
            "partial_status_passed": False,
            "false_claim_count": 0,
            "first_action_ids": [],
            "requires_threat_priority": False,
            "top_threat_priority_passed": False,
            "requires_unknown_friendly_action_block": False,
            "distinct_first_actions_passed": False,
            "unknown_friendly_action_blocked": False,
            "requires_vanilla_approximation": False,
            "vanilla_approximation_passed": False,
            "duplicate_first_action_count": 0,
        }
        try:
            response = solve_wire(fixture["request"])
            if not isinstance(response, Mapping):
                raise VisibleResponseEvaluationError("worker response must be an object")
            errors, assessment = compare_visible_response(fixture, response)
        except (OSError, RuntimeError, ValueError) as exc:
            errors = [f"worker invocation failed: {exc}"]
        elapsed = (time.perf_counter() - started) * 1000.0
        latencies.append(elapsed)
        passed = not errors
        if passed:
            passed_count += 1
        else:
            contract_failure_count += 1
        false_claim_count += int(assessment["false_claim_count"])
        requires_threat = bool(assessment["requires_threat_priority"])
        requires_unknown = bool(assessment["requires_unknown_friendly_action_block"])
        requires_approximation = bool(assessment["requires_vanilla_approximation"])
        threat_fixture_count += int(requires_threat)
        threat_priority_count += int(
            requires_threat and bool(assessment["top_threat_priority_passed"])
        )
        distinct_fixture_count += 1
        distinct_count += int(bool(assessment["distinct_first_actions_passed"]))
        unknown_source_fixture_count += int(requires_unknown)
        unknown_blocked_count += int(
            requires_unknown and bool(assessment["unknown_friendly_action_blocked"])
        )
        approximation_fixture_count += int(requires_approximation)
        approximation_passed_count += int(
            requires_approximation and bool(assessment["vanilla_approximation_passed"])
        )
        duplicate_first_action_count += int(assessment["duplicate_first_action_count"])
        partial_status_count += int(bool(assessment["partial_status_passed"]))
        details.append(
            {
                "id": fixture["id"],
                "fixture_sha256": _canonical_hash(fixture),
                "passed": passed,
                "latency_ms": round(elapsed, 3),
                "first_action_ids": assessment["first_action_ids"],
                "requires_threat_priority": requires_threat,
                "top_threat_priority_passed": assessment["top_threat_priority_passed"],
                "distinct_first_actions_passed": assessment["distinct_first_actions_passed"],
                "requires_unknown_friendly_action_block": requires_unknown,
                "unknown_friendly_action_blocked": assessment[
                    "unknown_friendly_action_blocked"
                ],
                "requires_vanilla_approximation": requires_approximation,
                "vanilla_approximation_passed": assessment[
                    "vanilla_approximation_passed"
                ],
                "duplicate_first_action_count": assessment[
                    "duplicate_first_action_count"
                ],
                "false_claim_count": assessment["false_claim_count"],
                "contract_errors": errors,
            }
        )
    fixture_count = len(suite["fixtures"])
    maximum_failures = int(suite.get("thresholds", {}).get("max_contract_failure_count", 0))
    passed = contract_failure_count <= maximum_failures and passed_count == fixture_count
    return {
        "schema": VISIBLE_RESPONSE_REPORT_SCHEMA,
        "suite_id": VISIBLE_RESPONSE_SUITE_ID,
        "passed": passed,
        "status": "passed" if passed else "failed",
        "binary": dict(binary or {"available": False, "path": "", "sha256": ""}),
        "fixture_file": str(Path(fixture_path).resolve()),
        "fixture_file_sha256": _file_hash(Path(fixture_path)),
        "metrics": {
            "fixture_count": fixture_count,
            "passed_fixture_count": passed_count,
            "failed_fixture_count": fixture_count - passed_count,
            "contract_failure_count": contract_failure_count,
            "false_claim_count": false_claim_count,
            "partial_status_count": partial_status_count,
            "threat_fixture_count": threat_fixture_count,
            "threat_priority_passed_count": threat_priority_count,
            "distinct_first_actions_fixture_count": distinct_fixture_count,
            "distinct_first_actions_passed_count": distinct_count,
            "duplicate_first_action_count": duplicate_first_action_count,
            "unknown_source_fixture_count": unknown_source_fixture_count,
            "unknown_friendly_action_blocked_count": unknown_blocked_count,
            "approximation_fixture_count": approximation_fixture_count,
            "approximation_passed_count": approximation_passed_count,
            "latency_p95_ms": _percentile(latencies, 0.95),
        },
        "fixtures": details,
        "caveat": (
            "This gate proves only honest ranking behavior for fixed visible-response-v1 "
            "fixtures. It does not prove exact counterplay, safety, portfolio optimality, "
            "calibrated win probability, complete Hearthstone rules, or globally optimal play."
        ),
    }


def evaluate_visible_response_binary(
    fixture_path: str | Path,
    binary_path: str | Path,
    *,
    startup_timeout_seconds: float = 10.0,
) -> dict[str, Any]:
    binary = Path(binary_path).resolve()
    if not binary.is_file():
        raise VisibleResponseEvaluationError(f"Rust solver binary was not found: {binary}")
    with RustWorkerClient(
        binary,
        startup_timeout_seconds=startup_timeout_seconds,
        data_prefix="metacompanion-visible-response-",
    ) as worker:

        def solve(request: Mapping[str, Any]) -> Mapping[str, Any]:
            return worker.solve(request, timeout=15.0)

        return evaluate_visible_response_suite(
            fixture_path,
            solve,
            binary={
                "available": True,
                "path": str(binary),
                "sha256": worker.binary_identity["sha256"],
            },
        )


def write_visible_response_report(report: Mapping[str, Any], path: str | Path) -> None:
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
