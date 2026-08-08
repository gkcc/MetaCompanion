from __future__ import annotations

import copy
import hashlib
import json
import os
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from .decision_frame import (
    DecisionFrameRecord,
    DecisionFrameValidationError,
    _jsonl_values,
    _read_bounded,
    audit_decision_frame_file,
)
from .hdt_card_defs import (
    HdtCardDefsError,
    HdtCardDefsSnapshot,
    enrich_public_solver_state,
    iter_public_state_entities,
    load_hdt_card_defs,
    public_card_ids,
)
from .rust_worker_client import (
    RustWorkerClient,
    RustWorkerHttpError,
)


DECISION_SOLVER_EVALUATION_SCHEMA_ID = "advisor-decision-solver-evaluation-v1"
DECISION_SOLVER_SAMPLE_STRATEGY = "content_sha256_order_v1"
HDT_ROOT_CANDIDATE_CONTRACT = "hdt_complete_main_action_options_v1"
HISTORICAL_HDT_ADAPTER_CONTRACT = "decision_frame_legal_candidates_to_hdt_root_request_v1"
HISTORICAL_WEAPON_STATE_ADAPTER_CONTRACT = (
    "behavior_isolated_hero_attack_weapon_health_v1"
)
MIN_HISTORICAL_WEAPON_TRANSITIONS = 32

_HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_SAFE_LABEL = re.compile(r"^[A-Za-z0-9_.:-]{1,128}$")
_ABSOLUTE_WINDOWS_PATH = re.compile(r"^[A-Za-z]:[\\/]")
_RFC3339 = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")
_PRIVATE_KEYS = {
    "game_id",
    "state_id",
    "entity_id",
    "source_entity_id",
    "target_entity_id",
    "request_id",
    "decision_frame_id",
    "selected_behavior_id",
    "observed_at_utc",
    "hdt_frame_id",
    "session_token",
    "token",
    "url",
    "path",
}


class DecisionSolverEvaluationError(ValueError):
    pass


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _rate(numerator: int, denominator: int) -> float:
    return round(numerator / denominator, 6) if denominator else 0.0


def _average(values: Sequence[float]) -> float:
    return round(sum(values) / len(values), 6) if values else 0.0


def _safe_label(value: Any, fallback: str = "unknown") -> str:
    candidate = str(value or "").strip()
    return candidate if _SAFE_LABEL.fullmatch(candidate) else fallback


def _action_id(action: Mapping[str, Any]) -> str:
    kind = str(action.get("kind") or "")
    source = str(action.get("source_entity_id") or "")
    target = str(action.get("target_entity_id") or "")
    base = f"{kind}:{source}:{target}"
    position = int(action.get("board_position") or 0)
    return f"{base}:position={position}" if position > 0 else base


def _root_contract_id(action_id: str) -> str:
    # Turn-pair portfolios use a stable root sentinel for a line whose first
    # action is only the canonical wire action ``end_turn::``.
    return "end_turn" if action_id == "end_turn::" else action_id


def _historical_hdt_root_candidates(
    value: Mapping[str, Any], frame_index: int
) -> dict[str, Any]:
    """Adapt an already-audited historical decision frame for an offline solve.

    Older decision frames retain the HDT frame number and full legal candidate
    set, but predate collector epoch/watermark fields.  The positive adapter
    epoch/watermark below exist only to satisfy the live wire binding contract;
    the report explicitly records that they are not historical source evidence.
    """

    source_frame_id = value.get("hdt_frame_id")
    frame_id = (
        source_frame_id
        if isinstance(source_frame_id, int)
        and not isinstance(source_frame_id, bool)
        and source_frame_id > 0
        else frame_index + 1
    )
    decision_sequence = value.get("decision_sequence")
    frame_watermark = (
        decision_sequence
        if isinstance(decision_sequence, int)
        and not isinstance(decision_sequence, bool)
        and decision_sequence > 0
        else frame_index + 1
    )
    candidates = [
        {
            "option_id": int(candidate["option_id"]),
            "action": dict(candidate["action"]),
            "target_evidence": str(candidate["target_evidence"]),
            "position_evidence": str(candidate["position_evidence"]),
        }
        for candidate in value["legal_candidates"]
    ]
    return {
        "contract": HDT_ROOT_CANDIDATE_CONTRACT,
        "state_id": str(value["pre_state"]["state_id"]),
        "frame_id": frame_id,
        "collector_epoch": 1,
        "frame_watermark": frame_watermark,
        "candidate_set_complete": True,
        "candidates": candidates,
    }


def _nonnegative_integer(value: Any) -> int | None:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        return None
    return value


def _legacy_weapon_health_pair(value: Any) -> tuple[int, int] | None:
    """Return the historical max/current durability pair carried in health fields.

    Power-log snapshots produced before the explicit durability projection used
    ``HEALTH``/``DAMAGE`` for equipped weapons.  This helper deliberately refuses
    mixed or malformed representations; explicit durability always wins.
    """

    if not isinstance(value, Mapping) or str(value.get("card_type") or "").upper() != "WEAPON":
        return None
    if "durability" in value or "current_durability" in value:
        return None
    maximum = _nonnegative_integer(value.get("health"))
    current = _nonnegative_integer(value.get("current_health"))
    if maximum is None or current is None or maximum <= 0 or current <= 0 or current > maximum:
        return None
    return maximum, current


def _audit_historical_weapon_state_adapter(
    behavior_values: Sequence[Any],
) -> dict[str, Any]:
    """Prove the legacy weapon-field mapping from isolated observed transitions.

    Observed actions are not optimality labels.  They are used here only as
    transition evidence: an isolated hero attack with the same equipped weapon
    on both sides of the boundary must reduce ``current_health`` by exactly one.
    Exact multi-attack quota is intentionally not inferred from these records.
    """

    counters: Counter[str] = Counter()
    side_counts: Counter[str] = Counter()
    for raw in behavior_values:
        if not isinstance(raw, Mapping):
            continue
        pre_state = raw.get("pre_state")
        post_state = raw.get("post_state")
        if not isinstance(pre_state, Mapping) or not isinstance(post_state, Mapping):
            continue

        # Count the representations present in the hash-bound corpus, including
        # both sides and both transition boundaries.  No identifiers leave this
        # aggregate audit.
        for state in (pre_state, post_state):
            for role in ("friendly", "opponent"):
                player = state.get(role)
                weapon = player.get("weapon") if isinstance(player, Mapping) else None
                if not isinstance(weapon, Mapping):
                    continue
                counters["public_weapon_snapshot_count"] += 1
                if "durability" in weapon or "current_durability" in weapon:
                    counters["explicit_durability_snapshot_count"] += 1
                elif _legacy_weapon_health_pair(weapon) is not None:
                    counters["valid_legacy_weapon_snapshot_count"] += 1
                else:
                    counters["invalid_legacy_weapon_snapshot_count"] += 1

        if raw.get("behavior_eligible") is not True or raw.get("boundary_status") != "isolated":
            continue
        action = raw.get("action")
        if not isinstance(action, Mapping) or action.get("kind") != "attack":
            continue
        actor_side = str(raw.get("actor_side") or "")
        role = "friendly" if actor_side == "local" else "opponent" if actor_side == "opponent" else ""
        if not role:
            continue
        pre_player = pre_state.get(role)
        post_player = post_state.get(role)
        if not isinstance(pre_player, Mapping) or not isinstance(post_player, Mapping):
            continue
        hero = pre_player.get("hero")
        if not isinstance(hero, Mapping) or str(action.get("source_entity_id") or "") != str(
            hero.get("entity_id") or ""
        ):
            continue
        pre_weapon = pre_player.get("weapon")
        pre_pair = _legacy_weapon_health_pair(pre_weapon)
        if pre_pair is None:
            continue

        counters["eligible_isolated_hero_weapon_attack_count"] += 1
        side_counts[actor_side] += 1
        attacks_remaining = _nonnegative_integer(hero.get("attacks_remaining"))
        if hero.get("can_attack") is True and attacks_remaining is not None and attacks_remaining >= 1:
            counters["at_least_one_attack_readiness_count"] += 1

        post_weapon = post_player.get("weapon")
        if post_weapon is None:
            counters["weapon_removed_after_attack_count"] += 1
            if pre_pair[1] == 1:
                counters["weapon_removed_from_one_count"] += 1
            continue
        if not isinstance(post_weapon, Mapping):
            counters["conflicting_attack_transition_count"] += 1
            continue
        if str(post_weapon.get("entity_id") or "") != str(pre_weapon.get("entity_id") or ""):
            counters["weapon_replaced_after_attack_count"] += 1
            continue
        post_pair = _legacy_weapon_health_pair(post_weapon)
        counters["same_weapon_attack_transition_count"] += 1
        if (
            post_pair is not None
            and post_pair[0] == pre_pair[0]
            and post_pair[1] == pre_pair[1] - 1
        ):
            counters["same_weapon_decrement_by_one_count"] += 1
        else:
            counters["conflicting_attack_transition_count"] += 1

    comparable = counters["same_weapon_attack_transition_count"]
    confirmed = counters["same_weapon_decrement_by_one_count"]
    conflicts = counters["conflicting_attack_transition_count"]
    enabled = bool(
        comparable >= MIN_HISTORICAL_WEAPON_TRANSITIONS
        and confirmed == comparable
        and conflicts == 0
        and counters["invalid_legacy_weapon_snapshot_count"] == 0
    )
    return {
        "contract": HISTORICAL_WEAPON_STATE_ADAPTER_CONTRACT,
        "enabled": enabled,
        "offline_evaluation_only": True,
        "minimum_same_weapon_attack_transitions": MIN_HISTORICAL_WEAPON_TRANSITIONS,
        "source_fields": ["health", "current_health"],
        "mapped_fields": ["durability", "current_durability"],
        "source_behavior_hash_bound": True,
        "candidate_set_unchanged": True,
        "transition_state_evidence": enabled,
        "action_legality_evidence": False,
        "optimality_evidence": False,
        "training_label_evidence": False,
        "evidence": {
            "public_weapon_snapshot_count": counters["public_weapon_snapshot_count"],
            "valid_legacy_weapon_snapshot_count": counters[
                "valid_legacy_weapon_snapshot_count"
            ],
            "explicit_durability_snapshot_count": counters[
                "explicit_durability_snapshot_count"
            ],
            "invalid_legacy_weapon_snapshot_count": counters[
                "invalid_legacy_weapon_snapshot_count"
            ],
            "eligible_isolated_hero_weapon_attack_count": counters[
                "eligible_isolated_hero_weapon_attack_count"
            ],
            "local_attack_count": side_counts["local"],
            "opponent_attack_count": side_counts["opponent"],
            "same_weapon_attack_transition_count": comparable,
            "same_weapon_decrement_by_one_count": confirmed,
            "weapon_removed_after_attack_count": counters[
                "weapon_removed_after_attack_count"
            ],
            "weapon_removed_from_one_count": counters["weapon_removed_from_one_count"],
            "weapon_replaced_after_attack_count": counters[
                "weapon_replaced_after_attack_count"
            ],
            "conflicting_attack_transition_count": conflicts,
        },
        "attack_count_evidence": {
            "at_least_one_attack_readiness_count": counters[
                "at_least_one_attack_readiness_count"
            ],
            "exact_multi_attack_quota_available": False,
            "extra_attacks_synthesized": False,
        },
    }


def _apply_historical_weapon_state_adapter(
    state: dict[str, Any], *, enabled: bool
) -> int:
    if not enabled:
        return 0
    mapped = 0
    for role in ("friendly", "opponent"):
        player = state.get(role)
        weapon = player.get("weapon") if isinstance(player, Mapping) else None
        pair = _legacy_weapon_health_pair(weapon)
        if pair is None or not isinstance(weapon, dict):
            continue
        weapon["durability"] = pair[0]
        weapon["current_durability"] = pair[1]
        mapped += 1
    return mapped


def _number_matches(value: Any, expected: float) -> bool:
    return (
        isinstance(value, (int, float))
        and not isinstance(value, bool)
        and abs(float(value) - expected) <= 1e-9
    )


def _candidate_source_coverage(
    coverage: Mapping[str, Any],
    hdt_ids: set[str],
    binding: Mapping[str, Any],
) -> tuple[set[str], set[str], bool, bool]:
    independent = coverage.get("independent_generated_root_coverage")
    supplied = coverage.get("hdt_supplied_root_portfolio_coverage")
    if not isinstance(independent, Mapping) or not isinstance(supplied, Mapping):
        return set(), set(), False, False

    generated_ids, generated_ids_valid = _string_set(
        independent.get("generated_action_ids")
    )
    matched_ids, matched_ids_valid = _string_set(
        independent.get("matched_hdt_action_ids")
    )
    expected_matched = generated_ids & hdt_ids
    expected_recall = len(expected_matched) / len(hdt_ids) if hdt_ids else 0.0
    independent_valid = bool(
        generated_ids_valid
        and matched_ids_valid
        and independent.get("contract") == "solver_independent_root_generation_v1"
        and independent.get("available") is True
        and independent.get("generated_count") == len(generated_ids)
        and matched_ids == expected_matched
        and independent.get("matched_hdt_count") == len(expected_matched)
        and independent.get("hdt_candidate_count") == len(hdt_ids)
        and _number_matches(independent.get("hdt_recall"), expected_recall)
        and independent.get("exact_match") == (generated_ids == hdt_ids)
        and independent.get("false_exact") is False
        and independent.get("live_policy_eligible") is False
        and independent.get("rl_training_eligible") is False
        and independent.get("global_optimality_verified") is False
    )

    legal_ids, legal_ids_valid = _string_set(supplied.get("legal_action_ids"))
    evaluated_ids, evaluated_ids_valid = _string_set(
        supplied.get("evaluated_action_ids")
    )
    expected_evaluated_coverage = (
        len(evaluated_ids) / len(hdt_ids) if hdt_ids else 0.0
    )
    supplied_valid = bool(
        legal_ids_valid
        and evaluated_ids_valid
        and supplied.get("contract") == HDT_ROOT_CANDIDATE_CONTRACT
        and supplied.get("available") is True
        and supplied.get("state_bound") is True
        and supplied.get("frame_id") == binding["frame_id"]
        and supplied.get("collector_epoch") == binding["collector_epoch"]
        and supplied.get("candidate_set_complete") is True
        and supplied.get("candidate_count") == len(hdt_ids)
        and legal_ids == hdt_ids
        and evaluated_ids.issubset(hdt_ids)
        and supplied.get("evaluated_count") == len(evaluated_ids)
        and _number_matches(
            supplied.get("evaluated_coverage"), expected_evaluated_coverage
        )
        and supplied.get("effect_simulation_complete") is False
        and supplied.get("root_legality_source") == "hdt_debug_print_options"
        and supplied.get("hidden_response_generation_allowed") is False
        and supplied.get("live_policy_eligible") is False
        and supplied.get("rl_training_eligible") is False
        and supplied.get("global_optimality_verified") is False
    )
    return generated_ids, evaluated_ids, independent_valid, supplied_valid


def _load_records(path: str | Path) -> tuple[list[DecisionFrameRecord], bytes]:
    payload = _read_bounded(path, label="decision_frame_input")
    values = _jsonl_values(payload, label="decision_frame_input")
    records: list[DecisionFrameRecord] = []
    for index, value in enumerate(values, start=1):
        try:
            records.append(DecisionFrameRecord.from_dict(value))
        except DecisionFrameValidationError as exc:
            raise DecisionSolverEvaluationError(
                f"决策帧第 {index} 行未通过合同校验（{exc.code}）"
            ) from exc
    if not records:
        raise DecisionSolverEvaluationError("决策帧文件为空。")
    return records, payload


def _sample_records(
    records: Sequence[DecisionFrameRecord], max_frames: int
) -> tuple[list[DecisionFrameRecord], str]:
    ordered = sorted(records, key=lambda record: str(record.value["content_sha256"]))
    sampled = ordered if max_frames == 0 else ordered[:max_frames]
    material = {
        "strategy": DECISION_SOLVER_SAMPLE_STRATEGY,
        "source_record_count": len(records),
        "requested_max_frames": max_frames,
        "selected_content_sha256": [
            str(record.value["content_sha256"]) for record in sampled
        ],
    }
    return list(sampled), _sha256_bytes(_canonical_json(material))


def _string_set(value: Any) -> tuple[set[str], bool]:
    if not isinstance(value, list):
        return set(), False
    result: set[str] = set()
    for item in value:
        if not isinstance(item, str) or not item or len(item) > 256:
            return set(), False
        result.add(_root_contract_id(item))
    return result, len(result) == len(value)


def _counterplay(response: Mapping[str, Any]) -> tuple[Mapping[str, Any] | None, bool]:
    coverage = response.get("coverage")
    if not isinstance(coverage, Mapping):
        return None, False
    direct = coverage.get("counterplay")
    details = coverage.get("details")
    nested = details.get("counterplay") if isinstance(details, Mapping) else None
    candidates = [item for item in (direct, nested) if isinstance(item, Mapping)]
    if not candidates:
        return None, False
    consistent = len(candidates) == 1 or candidates[0] == candidates[1]
    return candidates[0], consistent


def _recommendation_roots(
    response: Mapping[str, Any],
) -> tuple[list[str], list[int | None], bool]:
    raw = response.get("recommendations")
    if not isinstance(raw, list):
        return [], [], False
    roots: list[str] = []
    regrets: list[int | None] = []
    valid = True
    for recommendation in raw:
        if not isinstance(recommendation, Mapping):
            valid = False
            continue
        actions = recommendation.get("actions")
        if not isinstance(actions, list) or not actions or not isinstance(actions[0], Mapping):
            valid = False
            continue
        action_id = actions[0].get("action_id")
        if not isinstance(action_id, str) or not action_id or len(action_id) > 256:
            valid = False
            continue
        roots.append(_root_contract_id(action_id))
        regret = recommendation.get("verified_portfolio_regret")
        regrets.append(regret if isinstance(regret, int) and not isinstance(regret, bool) else None)
    if len(roots) != len(set(roots)):
        valid = False
    return roots, regrets, valid


def _privacy_violation_count(value: Any, *, key: str = "") -> int:
    violations = 0
    if key.lower() in _PRIVATE_KEYS:
        violations += 1
    if isinstance(value, Mapping):
        for child_key, child in value.items():
            violations += _privacy_violation_count(child, key=str(child_key))
    elif isinstance(value, list):
        for child in value:
            violations += _privacy_violation_count(child)
    elif isinstance(value, str):
        if _ABSOLUTE_WINDOWS_PATH.match(value) or value.startswith(("/", "file://", "http://", "https://")):
            violations += 1
        if _RFC3339.match(value):
            violations += 1
    return violations


def _source_identity(payload: bytes) -> dict[str, Any]:
    return {"bytes": len(payload), "sha256": _sha256_bytes(payload)}


def _state_entity_index(
    state: Mapping[str, Any],
) -> dict[str, tuple[str, str, Mapping[str, Any]]]:
    result: dict[str, tuple[str, str, Mapping[str, Any]]] = {}
    for role, zone, entity in iter_public_state_entities(state):
        entity_id = str(entity.get("entity_id") or "")
        if entity_id:
            result[entity_id] = (role, zone, entity)
    return result


def _structured_rule_entity_status(
    response: Mapping[str, Any], entity_id: str
) -> tuple[bool, str]:
    coverage = response.get("coverage")
    rules = coverage.get("structured_card_rules") if isinstance(coverage, Mapping) else None
    if not isinstance(rules, Mapping):
        return False, ""
    matched = rules.get("matched")
    if isinstance(matched, list):
        for item in matched:
            if isinstance(item, Mapping) and str(item.get("entity_id") or "") == entity_id:
                return True, ""
    mismatches = rules.get("mismatches")
    if isinstance(mismatches, list):
        for item in mismatches:
            if isinstance(item, Mapping) and str(item.get("entity_id") or "") == entity_id:
                return False, _safe_label(item.get("reason"), "structured_rule_mismatch")
    return False, ""


def _structured_rule_payload(
    response: Mapping[str, Any],
) -> tuple[bool, list[Mapping[str, Any]], list[Mapping[str, Any]], bool]:
    """Read the public, privacy-safe portion of one worker rule assessment.

    Entity IDs are intentionally consumed only for per-action diagnosis elsewhere
    and are never returned from this helper or written into the aggregate report.
    """

    coverage = response.get("coverage")
    rules = coverage.get("structured_card_rules") if isinstance(coverage, Mapping) else None
    if not isinstance(rules, Mapping):
        return False, [], [], False
    available = rules.get("available")
    matched = rules.get("matched")
    mismatches = rules.get("mismatches")
    valid = bool(
        isinstance(available, bool)
        and isinstance(matched, list)
        and isinstance(mismatches, list)
        and all(isinstance(item, Mapping) for item in matched)
        and all(isinstance(item, Mapping) for item in mismatches)
    )
    if not valid:
        return False, [], [], False
    return bool(available), list(matched), list(mismatches), True


def _complex_character_reason(entity: Mapping[str, Any], prefix: str) -> str:
    for field_name in ("reborn", "dormant", "immune", "stealth"):
        if entity.get(field_name) is True:
            return f"{prefix}_{field_name}_not_modeled"
    durability = entity.get("current_durability", entity.get("durability", 0))
    if isinstance(durability, int) and not isinstance(durability, bool) and durability > 0:
        return f"{prefix}_durability_not_modeled"
    return ""


def _hdt_omission_reason(
    action: Mapping[str, Any],
    state: Mapping[str, Any],
    response: Mapping[str, Any] | None,
) -> str:
    if response is None:
        return "solver_request_failed"
    kind = str(action.get("kind") or "unknown")
    if kind == "location_activate":
        return "location_activation_not_modeled"
    if kind == "end_turn":
        return "unexpected_end_turn_omission"
    entities = _state_entity_index(state)
    source_id = str(action.get("source_entity_id") or "")
    source_binding = entities.get(source_id)
    if source_binding is None:
        return "public_source_missing"
    _role, zone, source = source_binding
    card_type = str(source.get("card_type") or "UNKNOWN").upper()
    player = state.get("friendly")
    mana = player.get("mana", 0) if isinstance(player, Mapping) else 0
    cost = source.get("cost", 0)
    if (
        kind in {"play_card", "hero_power"}
        and isinstance(cost, int)
        and not isinstance(cost, bool)
        and isinstance(mana, int)
        and not isinstance(mana, bool)
        and cost > mana
    ):
        return "public_cost_exceeds_available_mana"
    target_id = str(action.get("target_entity_id") or "")
    target = entities.get(target_id)[2] if target_id in entities else None
    if isinstance(target, Mapping):
        reason = _complex_character_reason(target, "target")
        if reason:
            return reason
    if kind == "attack":
        reason = _complex_character_reason(source, "attack_source")
        if reason:
            return reason
        friendly = state.get("friendly")
        if zone == "hero" and isinstance(friendly, Mapping) and friendly.get("weapon"):
            weapon = friendly.get("weapon")
            current_durability = (
                weapon.get("current_durability")
                if isinstance(weapon, Mapping)
                else None
            )
            if (
                isinstance(current_durability, bool)
                or not isinstance(current_durability, int)
                or current_durability <= 0
            ):
                return "hero_weapon_attack_state_not_modeled"
            return "hero_weapon_effect_transition_not_modeled"
        return "attack_state_not_modeled"
    if kind == "play_card":
        if card_type == "WEAPON":
            return "weapon_play_not_modeled"
        if card_type == "LOCATION":
            return "location_play_not_modeled"
        if card_type in {"HERO", "HERO_POWER", "UNKNOWN"}:
            return "play_card_type_not_modeled"
    matched, mismatch = _structured_rule_entity_status(response, source_id)
    if mismatch:
        return "structured_rule_" + mismatch
    if matched:
        return "matched_effect_transition_not_modeled"
    english_text = str(
        source.get("english_text") or source.get("card_text") or ""
    ).strip()
    if kind == "hero_power":
        return (
            "hero_power_effect_not_structured"
            if english_text
            else "hero_power_english_text_missing"
        )
    if card_type == "SPELL":
        return "spell_effect_not_structured" if english_text else "spell_english_text_missing"
    if card_type == "MINION":
        return "minion_effect_not_structured" if english_text else "minion_transition_not_modeled"
    return "action_transition_not_modeled"


def _validated_binary_identity(value: Mapping[str, Any]) -> dict[str, Any]:
    sha256 = str(value.get("sha256") or "")
    size = value.get("bytes")
    if _HEX_SHA256.fullmatch(sha256) is None:
        raise DecisionSolverEvaluationError("Rust 求解器文件哈希无效。")
    if isinstance(size, bool) or not isinstance(size, int) or size <= 0:
        raise DecisionSolverEvaluationError("Rust 求解器文件大小无效。")
    return {"bytes": size, "sha256": sha256}


def evaluate_decision_solver_coverage_files(
    decision_frame_path: str | Path,
    behavior_path: str | Path,
    solve: Callable[[Mapping[str, Any]], Mapping[str, Any]],
    *,
    binary_identity: Mapping[str, Any],
    max_frames: int = 256,
    time_budget_ms: int = 250,
    max_iterations: int = 100_000,
    max_depth: int = 8,
    top_k: int = 10,
    worker_capabilities: Mapping[str, Any] | None = None,
    card_defs_path: str | Path | None = None,
) -> dict[str, Any]:
    limits = {
        "max_frames": max_frames,
        "time_budget_ms": time_budget_ms,
        "max_iterations": max_iterations,
        "max_depth": max_depth,
        "top_k": top_k,
    }
    for name, value in limits.items():
        if isinstance(value, bool) or not isinstance(value, int):
            raise DecisionSolverEvaluationError(f"{name} 必须是整数。")
    if max_frames < 0:
        raise DecisionSolverEvaluationError("max_frames 不能小于 0。")
    if min(time_budget_ms, max_iterations, max_depth, top_k) <= 0:
        raise DecisionSolverEvaluationError("求解预算必须全部大于 0。")
    if top_k > 255 or max_depth > 65_535 or max_iterations > 4_294_967_295:
        raise DecisionSolverEvaluationError("求解预算超出 Rust 协议范围。")

    decision_audit = audit_decision_frame_file(
        decision_frame_path, behavior_path=behavior_path
    )
    if decision_audit.get("passed") is not True:
        raise DecisionSolverEvaluationError("决策帧与双方行为语料联审未通过。")
    records, frame_payload = _load_records(decision_frame_path)
    behavior_payload = _read_bounded(behavior_path, label="behavior_input")
    behavior_identity = decision_audit.get("behavior_input")
    if not isinstance(behavior_identity, Mapping) or (
        behavior_identity.get("sha256") != _sha256_bytes(behavior_payload)
    ):
        raise DecisionSolverEvaluationError("双方行为语料哈希绑定不一致。")
    behavior_values = _jsonl_values(behavior_payload, label="behavior_input")
    historical_weapon_adapter = _audit_historical_weapon_state_adapter(
        behavior_values
    )
    sampled, sample_sha256 = _sample_records(records, max_frames)
    binary = _validated_binary_identity(binary_identity)
    card_defs_snapshot: HdtCardDefsSnapshot | None = None
    if card_defs_path is not None:
        expected_builds = {
            str(record.value.get("client_build") or "") for record in records
        }
        requested_cards = public_card_ids(
            record.value["pre_state"] for record in records
        )
        try:
            card_defs_snapshot = load_hdt_card_defs(
                card_defs_path,
                requested_card_ids=requested_cards,
                expected_builds=expected_builds,
            )
        except HdtCardDefsError as exc:
            detail = f"（{exc.code}）"
            raise DecisionSolverEvaluationError(
                "CardDefs 未通过同版本、大小、哈希或 XML 校验" + detail
            ) from exc

    outcomes: Counter[str] = Counter()
    response_statuses: Counter[str] = Counter()
    http_statuses: Counter[str] = Counter()
    solver_error_codes: Counter[str] = Counter()
    false_exact_reasons: Counter[str] = Counter()
    missing_action_kinds: Counter[str] = Counter()
    extra_action_kinds: Counter[str] = Counter()
    selected_action_kinds: Counter[str] = Counter()
    modes: Counter[str] = Counter()
    public_card_missing_candidates: Counter[str] = Counter()
    public_card_missing_frames: dict[str, set[int]] = defaultdict(set)
    public_card_action_kinds: dict[str, Counter[str]] = defaultdict(Counter)
    hdt_omitted_action_kinds: Counter[str] = Counter()
    hdt_omitted_reasons: Counter[str] = Counter()
    public_card_hdt_omitted_candidates: Counter[str] = Counter()
    public_card_hdt_omitted_frames: dict[str, set[int]] = defaultdict(set)
    public_card_hdt_omitted_kinds: dict[str, Counter[str]] = defaultdict(Counter)
    public_card_hdt_omitted_reasons: dict[str, Counter[str]] = defaultdict(Counter)
    structured_rule_match_counts: Counter[tuple[str, str]] = Counter()
    structured_rule_match_frames: dict[tuple[str, str], set[int]] = defaultdict(set)
    structured_rule_mismatch_counts: Counter[tuple[str, str, str, str]] = Counter()
    structured_rule_mismatch_frames: dict[
        tuple[str, str, str, str], set[int]
    ] = defaultdict(set)
    card_defs_overlay_metrics: Counter[str] = Counter()
    historical_weapon_adapter_metrics: Counter[str] = Counter()

    hdt_candidate_count = 0
    rust_root_count = 0
    matched_candidate_count = 0
    missing_candidate_count = 0
    extra_root_count = 0
    complete_match_count = 0
    exact_claim_count = 0
    scoped_lethal_count = 0
    false_exact_count = 0
    root_complete_count = 0
    portfolio_proven_count = 0
    solver_scope_verified_count = 0
    solver_scope_verified_candidate_count = 0
    verified_multi_alternative_count = 0
    verified_cooptimal_count = 0
    observed_choice_evaluated_count = 0
    observed_choice_top1_count = 0
    observed_choice_top3_count = 0
    observed_choice_in_solver_roots_count = 0
    observed_choice_in_hdt_evaluated_roots_count = 0
    hdt_request_structurally_valid_count = 0
    hdt_response_contract_valid_count = 0
    hdt_supplied_evaluated_count = 0
    hdt_supplied_omitted_count = 0
    hdt_supplied_fully_modeled_frame_count = 0
    structured_rule_assessment_available_frame_count = 0
    structured_rule_assessment_unavailable_frame_count = 0
    structured_rule_assessment_invalid_frame_count = 0
    protocol_error_count = 0
    per_frame_recall: list[float] = []
    per_frame_precision: list[float] = []
    per_frame_hdt_evaluated_coverage: list[float] = []

    def record_missing(frame_index: int, candidates: Sequence[Mapping[str, Any]]) -> None:
        for candidate in candidates:
            action = candidate["action"]
            kind = _safe_label(action.get("kind"))
            missing_action_kinds[kind] += 1
            card_id = _safe_label(action.get("card_id"), fallback="")
            if not card_id:
                continue
            public_card_missing_candidates[card_id] += 1
            public_card_missing_frames[card_id].add(frame_index)
            public_card_action_kinds[card_id][kind] += 1

    def record_hdt_omitted(
        frame_index: int,
        candidates: Sequence[Mapping[str, Any]],
        state: Mapping[str, Any],
        response: Mapping[str, Any] | None,
        *,
        forced_reason: str = "",
    ) -> None:
        for candidate in candidates:
            action = candidate["action"]
            kind = _safe_label(action.get("kind"))
            reason = forced_reason or _hdt_omission_reason(action, state, response)
            reason = _safe_label(reason, "unclassified_omission")
            hdt_omitted_action_kinds[kind] += 1
            hdt_omitted_reasons[reason] += 1
            card_id = _safe_label(action.get("card_id"), fallback="")
            if not card_id:
                continue
            public_card_hdt_omitted_candidates[card_id] += 1
            public_card_hdt_omitted_frames[card_id].add(frame_index)
            public_card_hdt_omitted_kinds[card_id][kind] += 1
            public_card_hdt_omitted_reasons[card_id][reason] += 1

    def record_structured_rules(
        frame_index: int, response: Mapping[str, Any]
    ) -> None:
        nonlocal structured_rule_assessment_available_frame_count
        nonlocal structured_rule_assessment_unavailable_frame_count
        nonlocal structured_rule_assessment_invalid_frame_count
        available, matched, mismatches, valid = _structured_rule_payload(response)
        if not valid:
            structured_rule_assessment_invalid_frame_count += 1
            return
        if not available:
            structured_rule_assessment_unavailable_frame_count += 1
            return
        structured_rule_assessment_available_frame_count += 1
        for item in matched:
            card_id = _safe_label(item.get("card_id"), fallback="")
            rule_id = _safe_label(item.get("rule_id"), fallback="")
            if not card_id or not rule_id:
                continue
            key = (card_id, rule_id)
            structured_rule_match_counts[key] += 1
            structured_rule_match_frames[key].add(frame_index)
        for item in mismatches:
            card_id = _safe_label(item.get("card_id"), fallback="")
            rule_id = _safe_label(item.get("rule_id"), fallback="")
            reason = _safe_label(item.get("reason"), "structured_rule_mismatch")
            actual_hash = str(item.get("actual_text_sha256") or "").lower()
            if not card_id or not rule_id or _HEX_SHA256.fullmatch(actual_hash) is None:
                continue
            key = (card_id, rule_id, reason, actual_hash)
            structured_rule_mismatch_counts[key] += 1
            structured_rule_mismatch_frames[key].add(frame_index)

    for frame_index, record in enumerate(sampled):
        value = record.value
        candidates = list(value["legal_candidates"])
        hdt_ids = {
            _root_contract_id(_action_id(candidate["action"]))
            for candidate in candidates
        }
        selected_id = _root_contract_id(_action_id(value["selected_action"]))
        hdt_candidate_count += len(hdt_ids)
        selected_action_kinds[_safe_label(value["selected_action"]["kind"])] += 1
        modes[_safe_label(value["mode"])] += 1
        hdt_binding = _historical_hdt_root_candidates(value, frame_index)
        hdt_request_structurally_valid_count += 1
        request_state = copy.deepcopy(value["pre_state"])
        mapped_weapon_count = _apply_historical_weapon_state_adapter(
            request_state,
            enabled=historical_weapon_adapter["enabled"] is True,
        )
        historical_weapon_adapter_metrics["mapped_weapon_state_count"] += (
            mapped_weapon_count
        )
        historical_weapon_adapter_metrics["mapped_frame_count"] += int(
            mapped_weapon_count > 0
        )
        if card_defs_snapshot is not None:
            request_state, overlay_metrics = enrich_public_solver_state(
                request_state, card_defs_snapshot
            )
            card_defs_overlay_metrics.update(overlay_metrics)
        request = {
            "api_version": "1.0",
            "request_id": f"coverage-{frame_index + 1:08d}",
            "state": request_state,
            "options": {
                "time_budget_ms": time_budget_ms,
                "max_iterations": max_iterations,
                "max_depth": max_depth,
                "top_k": top_k,
                "allow_approximate_effects": True,
            },
            "hdt_root_candidates": hdt_binding,
        }
        try:
            response = solve(request)
        except RustWorkerHttpError as exc:
            outcome = "unsupported" if exc.error_code == "unsupported_scope" else "error"
            outcomes[outcome] += 1
            http_statuses[str(exc.status_code)] += 1
            solver_error_codes[_safe_label(exc.error_code)] += 1
            missing_candidate_count += len(hdt_ids)
            hdt_supplied_omitted_count += len(hdt_ids)
            per_frame_recall.append(0.0)
            per_frame_precision.append(0.0)
            per_frame_hdt_evaluated_coverage.append(0.0)
            record_missing(frame_index, candidates)
            record_hdt_omitted(
                frame_index,
                candidates,
                request_state,
                None,
            )
            continue
        if not isinstance(response, Mapping):
            raise DecisionSolverEvaluationError("Rust 求解器回调没有返回 JSON 对象。")
        record_structured_rules(frame_index, response)

        status = _safe_label(response.get("status"))
        response_statuses[status] += 1
        coverage = response.get("coverage")
        exact_claim = isinstance(coverage, Mapping) and coverage.get("exact") is True
        scoped_lethal = bool(
            isinstance(coverage, Mapping) and coverage.get("scoped_lethal") is True
        )
        if exact_claim:
            exact_claim_count += 1
        if scoped_lethal:
            scoped_lethal_count += 1
        counterplay, counterplay_consistent = _counterplay(response)
        portfolio_root_ids: set[str] = set()
        root_ids_valid = False
        root_complete = False
        portfolio_proven = False
        search_complete = False
        counterplay_generated_ids: set[str] = set()
        counterplay_generated_ids_valid = False
        if counterplay is not None:
            portfolio_root_ids, root_ids_valid = _string_set(
                counterplay.get("legal_first_action_ids")
            )
            counterplay_generated_ids, counterplay_generated_ids_valid = _string_set(
                counterplay.get("generated_first_action_ids")
            )
            root_complete = counterplay.get("root_action_coverage_complete") is True
            portfolio_proven = counterplay.get("portfolio_optimality_proven") is True
            search_complete = counterplay.get("search_complete") is True
        if root_complete:
            root_complete_count += 1
        if portfolio_proven:
            portfolio_proven_count += 1
        recommendation_roots, regrets, recommendations_valid = _recommendation_roots(
            response
        )
        (
            independent_root_ids,
            evaluated_hdt_ids,
            independent_coverage_valid,
            hdt_coverage_valid,
        ) = (
            _candidate_source_coverage(coverage, hdt_ids, hdt_binding)
            if isinstance(coverage, Mapping)
            else (set(), set(), False, False)
        )
        if not independent_coverage_valid:
            independent_root_ids = set()
        if hdt_coverage_valid:
            hdt_response_contract_valid_count += 1
        else:
            evaluated_hdt_ids = set()
        hdt_supplied_evaluated_count += len(evaluated_hdt_ids)
        omitted_hdt_ids = hdt_ids - evaluated_hdt_ids
        hdt_supplied_omitted_count += len(omitted_hdt_ids)
        omitted_hdt_candidates = [
            candidate
            for candidate in candidates
            if _root_contract_id(_action_id(candidate["action"])) in omitted_hdt_ids
        ]
        record_hdt_omitted(
            frame_index,
            omitted_hdt_candidates,
            request_state,
            response,
            forced_reason=("" if hdt_coverage_valid else "hdt_coverage_contract_invalid"),
        )
        hdt_evaluated_coverage = (
            len(evaluated_hdt_ids) / len(hdt_ids) if hdt_ids else 0.0
        )
        per_frame_hdt_evaluated_coverage.append(hdt_evaluated_coverage)
        if hdt_coverage_valid and not omitted_hdt_ids:
            hdt_supplied_fully_modeled_frame_count += 1
        protocol_ok = bool(
            isinstance(coverage, Mapping)
            and counterplay is not None
            and counterplay_consistent
            and root_ids_valid
            and counterplay_generated_ids_valid
            and independent_coverage_valid
            and hdt_coverage_valid
            and portfolio_root_ids == hdt_ids
            and counterplay.get("legal_first_action_count") == len(hdt_ids)
            and counterplay_generated_ids == evaluated_hdt_ids
            and counterplay.get("generated_first_action_count")
            == len(evaluated_hdt_ids)
            and recommendations_valid
            and set(recommendation_roots).issubset(evaluated_hdt_ids)
        )
        if not protocol_ok:
            protocol_error_count += 1

        # These remain the independent generator metrics.  The HDT-supplied
        # portfolio must never be fed back into recall/precision and make the
        # solver's own legal-action generation look better than it is.
        matched = hdt_ids & independent_root_ids
        missing = hdt_ids - independent_root_ids
        extra = independent_root_ids - hdt_ids
        rust_root_count += len(independent_root_ids)
        matched_candidate_count += len(matched)
        missing_candidate_count += len(missing)
        extra_root_count += len(extra)
        recall = len(matched) / len(hdt_ids) if hdt_ids else 0.0
        precision = (
            len(matched) / len(independent_root_ids)
            if independent_root_ids
            else 0.0
        )
        per_frame_recall.append(recall)
        per_frame_precision.append(precision)
        if hdt_ids == independent_root_ids:
            complete_match_count += 1
        missing_candidates = [
            candidate
            for candidate in candidates
            if _root_contract_id(_action_id(candidate["action"])) in missing
        ]
        record_missing(frame_index, missing_candidates)
        for action_id in extra:
            extra_action_kinds[_safe_label(action_id.split(":", 1)[0])] += 1

        false_reasons: list[str] = []
        if exact_claim:
            if status != "ok":
                false_reasons.append("exact_status_not_ok")
            if not protocol_ok:
                false_reasons.append("exact_protocol_incomplete")
            if not root_complete:
                false_reasons.append("exact_root_coverage_incomplete")
            if not portfolio_proven or not search_complete:
                false_reasons.append("exact_portfolio_unproven")
            if hdt_ids != independent_root_ids:
                false_reasons.append("exact_hdt_candidate_mismatch")
            if hdt_ids != evaluated_hdt_ids:
                false_reasons.append("exact_hdt_portfolio_not_fully_evaluated")
            if not str(coverage.get("exact_scope") or "").strip():
                false_reasons.append("exact_scope_missing")
        if false_reasons:
            false_exact_count += 1
            false_exact_reasons.update(false_reasons)

        solver_scope_verified = bool(
            exact_claim
            and not false_reasons
            and status == "ok"
            and protocol_ok
            and root_complete
            and portfolio_proven
            and search_complete
            and hdt_ids == independent_root_ids
            and hdt_ids == evaluated_hdt_ids
        )
        if solver_scope_verified:
            outcomes["exact"] += 1
            solver_scope_verified_count += 1
            solver_scope_verified_candidate_count += len(hdt_ids)
            verified_regrets = [value for value in regrets if value is not None]
            if len(verified_regrets) >= 2:
                verified_multi_alternative_count += 1
            if sum(value == 0 for value in verified_regrets) >= 2:
                verified_cooptimal_count += 1
        elif exact_claim:
            outcomes["false_exact"] += 1
        elif status == "partial" or scoped_lethal:
            outcomes["partial"] += 1
        elif status == "unsupported":
            outcomes["unsupported"] += 1
        elif not protocol_ok:
            outcomes["protocol_error"] += 1
        else:
            outcomes["error"] += 1

        if recommendation_roots:
            observed_choice_evaluated_count += 1
            observed_choice_top1_count += int(recommendation_roots[0] == selected_id)
            observed_choice_top3_count += int(selected_id in recommendation_roots[:3])
        observed_choice_in_solver_roots_count += int(
            selected_id in independent_root_ids
        )
        observed_choice_in_hdt_evaluated_roots_count += int(
            selected_id in evaluated_hdt_ids
        )

    top_uncovered_cards = [
        {
            "card_id": card_id,
            "missing_candidate_count": count,
            "frame_count": len(public_card_missing_frames[card_id]),
            "action_kind_counts": dict(sorted(public_card_action_kinds[card_id].items())),
        }
        for card_id, count in sorted(
            public_card_missing_candidates.items(),
            key=lambda item: (
                -item[1],
                -len(public_card_missing_frames[item[0]]),
                item[0],
            ),
        )[:25]
    ]
    top_hdt_supplied_omitted_cards = [
        {
            "card_id": card_id,
            "omitted_candidate_count": count,
            "frame_count": len(public_card_hdt_omitted_frames[card_id]),
            "action_kind_counts": dict(
                sorted(public_card_hdt_omitted_kinds[card_id].items())
            ),
            "omission_reason_counts": dict(
                sorted(public_card_hdt_omitted_reasons[card_id].items())
            ),
        }
        for card_id, count in sorted(
            public_card_hdt_omitted_candidates.items(),
            key=lambda item: (
                -item[1],
                -len(public_card_hdt_omitted_frames[item[0]]),
                item[0],
            ),
        )[:25]
    ]
    top_structured_rule_matches = [
        {
            "card_id": key[0],
            "rule_id": key[1],
            "match_count": count,
            "frame_count": len(structured_rule_match_frames[key]),
        }
        for key, count in sorted(
            structured_rule_match_counts.items(),
            key=lambda item: (
                -item[1],
                -len(structured_rule_match_frames[item[0]]),
                item[0],
            ),
        )[:25]
    ]
    top_structured_rule_mismatches = [
        {
            "card_id": key[0],
            "rule_id": key[1],
            "reason": key[2],
            "actual_text_sha256": key[3],
            "mismatch_count": count,
            "frame_count": len(structured_rule_mismatch_frames[key]),
        }
        for key, count in sorted(
            structured_rule_mismatch_counts.items(),
            key=lambda item: (
                -item[1],
                -len(structured_rule_mismatch_frames[item[0]]),
                item[0],
            ),
        )[:25]
    ]
    capabilities = worker_capabilities or {}
    capability_contract = bool(
        capabilities.get("root_action_portfolio_v1") is True
        if capabilities
        else True
    )
    metrics = {
        "source_frame_count": len(records),
        "sampled_frame_count": len(sampled),
        "sampled_mode_counts": dict(sorted(modes.items())),
        "sampled_selected_action_kind_counts": dict(sorted(selected_action_kinds.items())),
        "hdt_candidate_count": hdt_candidate_count,
        "hdt_supplied_request_structurally_valid_frame_count": (
            hdt_request_structurally_valid_count
        ),
        "hdt_supplied_request_structurally_valid_frame_rate": _rate(
            hdt_request_structurally_valid_count, len(sampled)
        ),
        "hdt_supplied_response_contract_valid_frame_count": (
            hdt_response_contract_valid_count
        ),
        "hdt_supplied_response_contract_valid_frame_rate": _rate(
            hdt_response_contract_valid_count, len(sampled)
        ),
        "hdt_supplied_candidate_count": hdt_candidate_count,
        "hdt_supplied_evaluated_count": hdt_supplied_evaluated_count,
        "hdt_supplied_omitted_count": hdt_supplied_omitted_count,
        "hdt_supplied_omitted_action_kind_counts": dict(
            sorted(hdt_omitted_action_kinds.items())
        ),
        "hdt_supplied_omitted_reason_counts": dict(
            sorted(hdt_omitted_reasons.items())
        ),
        "hdt_supplied_evaluated_coverage": _rate(
            hdt_supplied_evaluated_count, hdt_candidate_count
        ),
        "mean_frame_hdt_supplied_evaluated_coverage": _average(
            per_frame_hdt_evaluated_coverage
        ),
        "hdt_supplied_root_portfolio_fully_modeled_frame_count": (
            hdt_supplied_fully_modeled_frame_count
        ),
        "hdt_supplied_root_portfolio_fully_modeled_frame_rate": _rate(
            hdt_supplied_fully_modeled_frame_count, len(sampled)
        ),
        "structured_rule_assessment_available_frame_count": (
            structured_rule_assessment_available_frame_count
        ),
        "structured_rule_assessment_unavailable_frame_count": (
            structured_rule_assessment_unavailable_frame_count
        ),
        "structured_rule_assessment_invalid_frame_count": (
            structured_rule_assessment_invalid_frame_count
        ),
        "structured_rule_match_count": sum(structured_rule_match_counts.values()),
        "structured_rule_mismatch_count": sum(
            structured_rule_mismatch_counts.values()
        ),
        "rust_root_action_count": rust_root_count,
        "independent_generated_root_action_count": rust_root_count,
        "matched_candidate_count": matched_candidate_count,
        "independent_matched_hdt_candidate_count": matched_candidate_count,
        "missing_candidate_count": missing_candidate_count,
        "independent_missing_hdt_candidate_count": missing_candidate_count,
        "extra_root_action_count": extra_root_count,
        "independent_extra_root_action_count": extra_root_count,
        "hdt_candidate_recall": _rate(matched_candidate_count, hdt_candidate_count),
        "independent_hdt_candidate_recall": _rate(
            matched_candidate_count, hdt_candidate_count
        ),
        "rust_root_precision": _rate(matched_candidate_count, rust_root_count),
        "independent_root_precision": _rate(matched_candidate_count, rust_root_count),
        "mean_frame_candidate_recall": _average(per_frame_recall),
        "mean_frame_independent_candidate_recall": _average(per_frame_recall),
        "mean_frame_root_precision": _average(per_frame_precision),
        "mean_frame_independent_root_precision": _average(per_frame_precision),
        "complete_candidate_set_match_count": complete_match_count,
        "independent_complete_candidate_set_match_count": complete_match_count,
        "complete_candidate_set_match_rate": _rate(complete_match_count, len(sampled)),
        "independent_complete_candidate_set_match_rate": _rate(
            complete_match_count, len(sampled)
        ),
        "response_status_counts": dict(sorted(response_statuses.items())),
        "frame_outcome_counts": dict(sorted(outcomes.items())),
        "http_status_counts": dict(sorted(http_statuses.items())),
        "solver_error_code_counts": dict(sorted(solver_error_codes.items())),
        "protocol_error_count": protocol_error_count,
        "exact_claim_count": exact_claim_count,
        "scoped_lethal_count": scoped_lethal_count,
        "false_exact_count": false_exact_count,
        "false_exact_reason_counts": dict(sorted(false_exact_reasons.items())),
        "root_action_coverage_complete_count": root_complete_count,
        "portfolio_optimality_proven_count": portfolio_proven_count,
        "solver_scope_verified_frame_count": solver_scope_verified_count,
        "solver_scope_verified_frame_rate": _rate(solver_scope_verified_count, len(sampled)),
        "solver_scope_verified_candidate_count": solver_scope_verified_candidate_count,
        "verified_multi_alternative_frame_count": verified_multi_alternative_count,
        "verified_cooptimal_frame_count": verified_cooptimal_count,
        "observed_choice_evaluated_count": observed_choice_evaluated_count,
        "observed_choice_top1_agreement_count": observed_choice_top1_count,
        "observed_choice_top1_agreement_rate": _rate(
            observed_choice_top1_count, observed_choice_evaluated_count
        ),
        "observed_choice_top3_agreement_count": observed_choice_top3_count,
        "observed_choice_top3_agreement_rate": _rate(
            observed_choice_top3_count, observed_choice_evaluated_count
        ),
        "observed_choice_in_solver_roots_count": observed_choice_in_solver_roots_count,
        "observed_choice_in_solver_roots_rate": _rate(
            observed_choice_in_solver_roots_count, len(sampled)
        ),
        "observed_choice_in_hdt_evaluated_roots_count": (
            observed_choice_in_hdt_evaluated_roots_count
        ),
        "observed_choice_in_hdt_evaluated_roots_rate": _rate(
            observed_choice_in_hdt_evaluated_roots_count, len(sampled)
        ),
        "missing_candidate_action_kind_counts": dict(sorted(missing_action_kinds.items())),
        "extra_root_action_kind_counts": dict(sorted(extra_action_kinds.items())),
    }
    report: dict[str, Any] = {
        "schema": DECISION_SOLVER_EVALUATION_SCHEMA_ID,
        "source_decision_frames": {
            **_source_identity(frame_payload),
            "record_count": len(records),
        },
        "source_behavior": {
            **_source_identity(behavior_payload),
            "record_count": int(behavior_identity.get("valid_record_count") or 0),
        },
        "source_rust_binary": binary,
        "sample": {
            "strategy": DECISION_SOLVER_SAMPLE_STRATEGY,
            "requested_max_frames": max_frames,
            "sha256": sample_sha256,
        },
        "solve_options": {
            "time_budget_ms": time_budget_ms,
            "max_iterations": max_iterations,
            "max_depth": max_depth,
            "top_k": top_k,
            "allow_approximate_effects": True,
        },
        "historical_candidate_adapter": {
            "contract": HISTORICAL_HDT_ADAPTER_CONTRACT,
            "offline_evaluation_only": True,
            "source_candidate_set_preserved": True,
            "source_epoch_available": False,
            "source_watermark_available": False,
            "adapter_identity_used_as_training_evidence": False,
        },
        "historical_weapon_state_adapter": {
            **historical_weapon_adapter,
            "adapter_metrics": dict(
                sorted(historical_weapon_adapter_metrics.items())
            ),
        },
        "decision_frame_contract_passed": True,
        "source_binding_passed": True,
        "worker_backend_ready": True,
        "root_action_portfolio_capability": capability_contract,
        "public_card_defs_overlay": {
            "enabled": card_defs_snapshot is not None,
            "contract": "same_build_public_card_defs_overlay_v1",
            "card_defs": (
                card_defs_snapshot.manifest_summary()
                if card_defs_snapshot is not None
                else None
            ),
            "selected_frame_build_match_required": True,
            "decision_frame_payload_unchanged": True,
            "public_card_ids_only": True,
            "hidden_opponent_hand_identity_enriched": False,
            "dynamic_cost_or_stats_overwritten": False,
            "action_legality_evidence": False,
            "optimality_evidence": False,
            "overlay_metrics": dict(sorted(card_defs_overlay_metrics.items())),
        },
        "metrics": metrics,
        "top_uncovered_public_cards": top_uncovered_cards,
        "top_hdt_supplied_omitted_public_cards": top_hdt_supplied_omitted_cards,
        "top_structured_rule_matches": top_structured_rule_matches,
        "top_structured_rule_mismatches": top_structured_rule_mismatches,
        "solver_scope_counterfactual_evidence_count": solver_scope_verified_count,
        "counterfactual_dataset_written": False,
        "observed_choice_used_as_optimality_label": False,
        "outcome_used_as_action_optimality": False,
        "candidate_generation_allowed": False,
        "live_policy_eligible": False,
        "rl_training_eligible": False,
        "global_optimality_verified": False,
        "approved_uses": [
            "measure_independent_solver_generation_against_hdt_legal_candidates",
            "measure_hdt_supplied_root_portfolio_evaluation_coverage",
            "prioritize_public_card_and_action_rule_coverage",
            "diagnose_hdt_supplied_omissions_by_public_card_and_reason",
            "diagnose_structured_rule_matches_and_hash_or_context_mismatches",
            "measure_observed_choice_agreement_without_optimality_claims",
            "identify_solver_scope_counterfactual_evidence",
        ],
        "prohibited_uses": [
            "global_optimal_action_ground_truth",
            "automatic_rl_promotion",
            "hidden_opponent_card_reconstruction",
            "live_policy_installation",
        ],
        "caveats_zh": [
            "你的实际选择只用于衡量一致性，不会被当作最优动作真值。",
            "对手公开动作继续用于对手行为模型；本审计不伪造对手 Options 候选集。",
            "独立生成召回率和精确率只读取 Rust 自己生成的根动作，不会用 HDT 候选集反向美化。",
            "历史决策帧没有保存采集器 epoch/watermark；离线适配标识只用于本次请求校验，不会写成训练证据。",
            "旧回放仅在双方隔离英雄攻击前后状态达到最低样本数、同一武器每次都恰好减少 1 且零冲突时，才把武器 health/current_health 兼容映射为总耐久/剩余耐久；该映射不改变候选集，也不是最优动作标签。",
            "历史动作只能证明当下至少还有一次攻击，不能证明风怒等情况下的完整攻击额度；本审计不会凭动作轨迹合成额外攻击。",
            "可选 CardDefs 补全只接受与全部决策帧 build 完全相同的文件，并把大小与 SHA-256 写入报告；它只补公开卡牌定义，不改变原决策帧、合法候选或行为标签。",
            "只有 Rust 明确 exact、根动作全集完整、组合最优性已证明且与 HDT 候选全集一致的帧，才计入求解器范围内可复核证据。",
            "即使单帧通过，也只证明当前公开规则与最坏可见回应范围，不证明完整炉石全局最优，更不会自动晋升强化学习。",
        ],
    }
    privacy_violations = _privacy_violation_count(report)
    report["privacy_contract_passed"] = privacy_violations == 0
    report["privacy_violation_count"] = privacy_violations
    report["passed"] = bool(
        privacy_violations == 0
        and capability_contract
        and protocol_error_count == 0
        and false_exact_count == 0
    )
    report["status"] = "AUDITED" if report["passed"] else "REVIEW_REQUIRED"
    return report


def evaluate_decision_solver_binary(
    decision_frame_path: str | Path,
    behavior_path: str | Path,
    binary_path: str | Path,
    *,
    max_frames: int = 256,
    time_budget_ms: int = 250,
    max_iterations: int = 100_000,
    max_depth: int = 8,
    top_k: int = 10,
    startup_timeout_seconds: float = 10.0,
    card_defs_path: str | Path | None = None,
) -> dict[str, Any]:
    timeout = max(15.0, time_budget_ms / 1000.0 + 5.0)
    with RustWorkerClient(
        binary_path,
        startup_timeout_seconds=startup_timeout_seconds,
        data_prefix="metacompanion-decision-coverage-",
    ) as worker:
        health = worker.health or {}
        capabilities = health.get("capabilities")
        return evaluate_decision_solver_coverage_files(
            decision_frame_path,
            behavior_path,
            lambda request: worker.solve(request, timeout=timeout),
            binary_identity=worker.binary_identity,
            max_frames=max_frames,
            time_budget_ms=time_budget_ms,
            max_iterations=max_iterations,
            max_depth=max_depth,
            top_k=top_k,
            worker_capabilities=(
                capabilities if isinstance(capabilities, Mapping) else {}
            ),
            card_defs_path=card_defs_path,
        )


def write_decision_solver_evaluation(
    report: Mapping[str, Any], path: str | Path
) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(report, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    try:
        with temporary.open("wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, destination)
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
