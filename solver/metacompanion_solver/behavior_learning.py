from __future__ import annotations

import copy
import hashlib
import json
import os
import re
import tempfile
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path
from typing import Any, Mapping, Sequence

from .behavior import (
    BEHAVIOR_CORPUS_FILENAME,
    BehaviorRecord,
    BehaviorValidationError,
    _action,
    _entities,
    audit_behavior_corpus,
    public_behavior_state,
)
from .logging_store import deterministic_game_split
from .offline import load_records
from .trajectory import (
    SOURCE_KIND_DIRECT_AUDIT,
    SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
    SOURCE_KIND_SYNTHETIC_FIXTURE,
    audit_trajectory_file,
)


BEHAVIOR_LEARNING_POLICY_SCHEMA_ID = "behavior-learning-readiness-policy-v1"
BEHAVIOR_LEARNING_REPORT_SCHEMA_ID = "behavior-learning-readiness-report-v1"
RUNTIME_BEHAVIOR_LEARNING_REPORT_SCHEMA_ID = (
    "runtime-behavior-learning-readiness-report-v1"
)
BEHAVIOR_IMITATION_SCHEMA_ID = "behavior-imitation-example-v1"
BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID = "behavior-imitation-manifest-v1"

MAX_INPUT_BYTES = 256 * 1024 * 1024
ISSUE_DETAIL_LIMIT = 100

DEFAULT_BEHAVIOR_LEARNING_POLICY: dict[str, float | int] = {
    "min_unique_games": 50,
    "min_behavior_records": 500,
    "min_joined_result_games": 45,
    "min_joined_behavior_records": 450,
    "min_behavior_eligible_records": 250,
    "min_local_eligible_records": 100,
    "min_opponent_eligible_records": 100,
    "min_distinct_action_kinds": 3,
    "min_result_join_rate": 0.90,
    "min_both_side_game_rate": 0.90,
    "min_behavior_eligible_rate": 0.50,
    "max_unknown_actor_rate": 0.02,
    "max_unknown_identity_rate": 0.25,
    "min_train_games": 1,
    "min_validation_games": 1,
    "min_test_games": 1,
}

_ALLOWED_SOURCE_KINDS = {
    SOURCE_KIND_DIRECT_AUDIT,
    SOURCE_KIND_SYNTHETIC_FIXTURE,
    SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
}
_ANONYMOUS_GAME_ID = re.compile(r"^anon-[0-9a-f]{16}$")
_HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_BEHAVIOR_ID = re.compile(r"^behavior-[0-9a-f]{64}$")
_IMITATION_ID = re.compile(r"^imitation-[0-9a-f]{64}$")
_OUTCOMES = {"win", "loss", "tie"}
_IMITATION_CONTENT_KEYS = {
    "schema",
    "source_behavior_id",
    "source_content_sha256",
    "game_id",
    "behavior_sequence",
    "split",
    "actor_side",
    "actor_player_id",
    "action",
    "pre_state",
    "post_state",
    "local_outcome",
    "actor_outcome",
    "provenance",
    "imitation_training_eligible",
    "rl_training_eligible",
}
_IMITATION_TOP_LEVEL_KEYS = _IMITATION_CONTENT_KEYS | {"example_id"}
_IMITATION_PROVENANCE_KEYS = {
    "actor_evidence",
    "identity_status",
    "visibility_status",
    "boundary_status",
    "source_event",
    "observed_behavior",
    "optimality_verified",
}


class BehaviorLearningError(ValueError):
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


def behavior_learning_policy_sha256(policy: Mapping[str, float | int]) -> str:
    return _sha256_bytes(
        _canonical_json(
            {
                "schema": BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
                "thresholds": dict(sorted(policy.items())),
            }
        )
    )


def load_behavior_learning_policy(
    path: str | Path | None,
) -> dict[str, float | int]:
    policy = dict(DEFAULT_BEHAVIOR_LEARNING_POLICY)
    if path is None:
        return policy
    source = Path(path)
    try:
        raw = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise BehaviorLearningError("invalid behavior learning policy") from exc
    if not isinstance(raw, Mapping):
        raise BehaviorLearningError("behavior learning policy root must be an object")
    if raw.get("schema") != BEHAVIOR_LEARNING_POLICY_SCHEMA_ID:
        raise BehaviorLearningError(
            f"behavior learning policy schema must be "
            f"{BEHAVIOR_LEARNING_POLICY_SCHEMA_ID!r}"
        )
    thresholds = raw.get("thresholds")
    if not isinstance(thresholds, Mapping):
        raise BehaviorLearningError(
            "behavior learning policy thresholds must be an object"
        )
    unknown = sorted(set(thresholds) - set(policy))
    if unknown:
        raise BehaviorLearningError(
            "unknown behavior learning thresholds: " + ", ".join(unknown)
        )
    for key, value in thresholds.items():
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0:
            raise BehaviorLearningError(
                f"behavior learning threshold {key!r} must be non-negative"
            )
        if key.endswith("_rate"):
            if value > 1:
                raise BehaviorLearningError(
                    f"behavior learning rate threshold {key!r} must be at most 1"
                )
            policy[key] = float(value)
        else:
            if not isinstance(value, int):
                raise BehaviorLearningError(
                    f"behavior learning count threshold {key!r} must be an integer"
                )
            policy[key] = value
    return policy


def _rate(numerator: int, denominator: int, *, empty: float = 0.0) -> float:
    return round(numerator / denominator, 6) if denominator else empty


def _check(name: str, actual: int | float, operator: str, expected: int | float) -> dict[str, Any]:
    if operator == ">=":
        passed = actual >= expected
    elif operator == "<=":
        passed = actual <= expected
    elif operator == "==":
        passed = actual == expected
    else:  # pragma: no cover - all callers use the fixed operators above.
        raise AssertionError(operator)
    return {
        "name": name,
        "actual": actual,
        "operator": operator,
        "expected": expected,
        "passed": passed,
    }


def _read_input(path: str | Path, label: str) -> tuple[Path, bytes]:
    source = Path(path)
    try:
        payload = source.read_bytes()
    except OSError as exc:
        raise BehaviorLearningError(f"{label} could not be read") from exc
    if len(payload) > MAX_INPUT_BYTES:
        raise BehaviorLearningError(f"{label} exceeds the 256 MiB audit limit")
    return source, payload


def _behavior_records(path: Path) -> list[BehaviorRecord]:
    records: list[BehaviorRecord] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        try:
            records.append(BehaviorRecord.from_dict(json.loads(line)))
        except (json.JSONDecodeError, BehaviorValidationError):
            # The single-file audit reports the exact line and reason.  Keeping only
            # valid records here lets the report expose useful partial metrics while
            # contract_passed still fails closed.
            continue
    return records


def _terminal_results(path: Path) -> dict[str, set[str]]:
    results: dict[str, set[str]] = defaultdict(set)
    for record in load_records(path):
        if record.get("kind") != "observation":
            continue
        observation = record.get("observation")
        trajectory = record.get("trajectory")
        if not isinstance(observation, Mapping) or not isinstance(trajectory, Mapping):
            continue
        if str(observation.get("kind") or "").strip().lower() != "result":
            continue
        game_id = str(observation.get("game_id") or "").strip()
        if game_id != str(trajectory.get("game_id") or "").strip():
            continue
        outcome = str(observation.get("result") or "").strip().lower()
        if _ANONYMOUS_GAME_ID.fullmatch(game_id) and outcome in _OUTCOMES:
            results[game_id].add(outcome)
    return results


def _nested_counts(
    counts: Mapping[tuple[str, str], int],
) -> dict[str, dict[str, int]]:
    result: dict[str, dict[str, int]] = defaultdict(dict)
    for (outer, inner), value in sorted(counts.items()):
        result[outer][inner] = value
    return dict(result)


def _timestamp(value: str) -> datetime:
    return datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)


def _audit_snapshots(
    behavior_snapshot: Path,
    trajectory_snapshot: Path,
    *,
    behavior_name: str,
    trajectory_name: str,
    behavior_bytes: bytes,
    trajectory_bytes: bytes,
    policy_path: str | Path | None,
    source_kind: str,
) -> dict[str, Any]:
    if source_kind not in _ALLOWED_SOURCE_KINDS:
        raise BehaviorLearningError(
            f"unsupported behavior learning source kind: {source_kind}"
        )
    policy = load_behavior_learning_policy(policy_path)
    policy_sha256 = behavior_learning_policy_sha256(policy)
    behavior_audit = audit_behavior_corpus(behavior_snapshot)
    trajectory_audit = audit_trajectory_file(
        trajectory_snapshot,
        source_kind=source_kind,
    )
    records = _behavior_records(behavior_snapshot)
    result_sets = _terminal_results(trajectory_snapshot)
    unique_results = {
        game_id: next(iter(outcomes))
        for game_id, outcomes in result_sets.items()
        if len(outcomes) == 1
    }

    side_counts: Counter[str] = Counter()
    identity_counts: Counter[str] = Counter()
    boundary_counts: Counter[str] = Counter()
    action_counts: Counter[str] = Counter()
    choice_status_counts: Counter[str] = Counter()
    side_action_counts: Counter[tuple[str, str]] = Counter()
    eligible_side_action_counts: Counter[tuple[str, str]] = Counter()
    mode_counts: Counter[str] = Counter()
    patch_counts: Counter[str] = Counter()
    game_sides: dict[str, set[str]] = defaultdict(set)
    game_records: dict[str, list[BehaviorRecord]] = defaultdict(list)
    last_sequence: dict[str, int] = {}
    last_timestamp: dict[str, datetime] = {}
    sequence_order_violation_count = 0
    timestamp_regression_count = 0
    post_state_present_count = 0
    behavior_eligible_count = 0
    local_eligible_count = 0
    opponent_eligible_count = 0
    board_position_record_count = 0
    choice_item_count = 0
    offered_choice_entity_count = 0
    selected_choice_entity_count = 0
    replay_behavior_record_count = 0
    replay_play_card_record_count = 0
    replay_play_source_still_actor_hand_post_count = 0
    replay_play_source_still_actor_hand_post_games: set[str] = set()
    replay_attack_record_count = 0
    replay_attack_source_readiness_explicit_count = 0
    replay_end_turn_record_count = 0
    replay_end_turn_active_player_unchanged_count = 0

    for record in records:
        value = record.value
        game_id = record.game_id
        side = str(value["actor_side"])
        identity = str(value["identity_status"])
        boundary = str(value["boundary_status"])
        action_kind = str(value["action"]["kind"])
        action = value["action"]
        choice_status_counts[str(action.get("choice_status", "not_observed"))] += 1
        if action.get("board_position") is not None:
            board_position_record_count += 1
        choices = action.get("choices", [])
        choice_item_count += len(choices)
        offered_choice_entity_count += sum(
            len(item.get("option_entity_ids", [])) for item in choices
        )
        selected_choice_entity_count += sum(
            len(item.get("selected_entity_ids", [])) for item in choices
        )
        side_counts[side] += 1
        identity_counts[identity] += 1
        boundary_counts[boundary] += 1
        action_counts[action_kind] += 1
        side_action_counts[(side, action_kind)] += 1
        game_sides[game_id].add(side)
        game_records[game_id].append(record)
        mode_counts[str(value["pre_state"].get("mode") or "unknown")] += 1
        patch_counts[str(value["pre_state"].get("patch") or "unknown")] += 1
        if value.get("post_state") is not None:
            post_state_present_count += 1
        if value["behavior_eligible"] is True:
            behavior_eligible_count += 1
            eligible_side_action_counts[(side, action_kind)] += 1
            if side == "local":
                local_eligible_count += 1
            elif side == "opponent":
                opponent_eligible_count += 1

        if value["source_event"] == "hdt_replay_power":
            replay_behavior_record_count += 1
            post_state = value.get("post_state")
            if action_kind == "play_card":
                replay_play_card_record_count += 1
                if isinstance(post_state, Mapping):
                    post_entities = _entities(post_state)
                    post_source = post_entities.get(
                        str(action.get("source_entity_id") or "")
                    )
                    if post_source is not None and post_source[:2] == (
                        value["actor_player_id"],
                        "hand",
                    ):
                        replay_play_source_still_actor_hand_post_count += 1
                        replay_play_source_still_actor_hand_post_games.add(game_id)
            elif action_kind == "attack":
                replay_attack_record_count += 1
                pre_source = _entities(value["pre_state"]).get(
                    str(action.get("source_entity_id") or "")
                )
                if (
                    pre_source is not None
                    and pre_source[2].get("can_attack") is True
                    and int(pre_source[2].get("attacks_remaining") or 0) >= 1
                ):
                    replay_attack_source_readiness_explicit_count += 1
            elif action_kind == "end_turn":
                replay_end_turn_record_count += 1
                if (
                    isinstance(post_state, Mapping)
                    and post_state.get("active_player_id")
                    == value["actor_player_id"]
                ):
                    replay_end_turn_active_player_unchanged_count += 1

        previous_sequence = last_sequence.get(game_id)
        if previous_sequence is not None and record.behavior_sequence <= previous_sequence:
            sequence_order_violation_count += 1
        last_sequence[game_id] = record.behavior_sequence
        observed = _timestamp(str(value["observed_at_utc"]))
        previous_timestamp = last_timestamp.get(game_id)
        if previous_timestamp is not None and observed < previous_timestamp:
            timestamp_regression_count += 1
        last_timestamp[game_id] = observed

    behavior_games = set(game_records)
    result_games = set(unique_results)
    joined_games = behavior_games & result_games
    both_side_games = {
        game_id
        for game_id, sides in game_sides.items()
        if "local" in sides and "opponent" in sides
    }
    joined_record_count = sum(len(game_records[game_id]) for game_id in joined_games)
    joined_eligible_count = sum(
        1
        for game_id in joined_games
        for record in game_records[game_id]
        if record.value["behavior_eligible"] is True
    )
    split_game_counts = Counter(
        deterministic_game_split(game_id) for game_id in joined_games
    )
    split_eligible_record_counts = Counter(
        deterministic_game_split(game_id)
        for game_id in joined_games
        for record in game_records[game_id]
        if record.value["behavior_eligible"] is True
    )
    outcome_counts = Counter(unique_results[game_id] for game_id in joined_games)

    adjacent_state_pair_count = 0
    adjacent_state_match_count = 0
    for items in game_records.values():
        ordered = sorted(items, key=lambda item: item.behavior_sequence)
        for previous, current in zip(ordered, ordered[1:]):
            post = previous.value.get("post_state")
            pre = current.value.get("pre_state")
            if not isinstance(post, Mapping) or not isinstance(pre, Mapping):
                continue
            adjacent_state_pair_count += 1
            if str(post.get("state_id") or "") == str(pre.get("state_id") or ""):
                adjacent_state_match_count += 1

    unknown_actor_count = side_counts["unknown"]
    unknown_identity_count = identity_counts["unknown"]
    trajectory_metrics = trajectory_audit.get("metrics", {})
    metrics: dict[str, Any] = {
        "behavior_record_count": len(records),
        "behavior_invalid_record_count": int(behavior_audit["record_count"])
        - len(records),
        "unique_behavior_game_count": len(behavior_games),
        "behavior_eligible_record_count": behavior_eligible_count,
        "local_eligible_record_count": local_eligible_count,
        "opponent_eligible_record_count": opponent_eligible_count,
        "post_state_present_count": post_state_present_count,
        "board_position_record_count": board_position_record_count,
        "choice_item_count": choice_item_count,
        "offered_choice_entity_count": offered_choice_entity_count,
        "selected_choice_entity_count": selected_choice_entity_count,
        "replay_behavior_record_count": replay_behavior_record_count,
        "replay_play_card_record_count": replay_play_card_record_count,
        "replay_play_source_still_actor_hand_post_count": (
            replay_play_source_still_actor_hand_post_count
        ),
        "replay_play_source_still_actor_hand_post_affected_game_count": len(
            replay_play_source_still_actor_hand_post_games
        ),
        "replay_play_source_left_actor_hand_post_rate": _rate(
            replay_play_card_record_count
            - replay_play_source_still_actor_hand_post_count,
            replay_play_card_record_count,
        ),
        "replay_attack_record_count": replay_attack_record_count,
        "replay_attack_source_readiness_explicit_count": (
            replay_attack_source_readiness_explicit_count
        ),
        "replay_attack_source_readiness_explicit_rate": _rate(
            replay_attack_source_readiness_explicit_count,
            replay_attack_record_count,
        ),
        "replay_end_turn_record_count": replay_end_turn_record_count,
        "replay_end_turn_active_player_unchanged_count": (
            replay_end_turn_active_player_unchanged_count
        ),
        "unknown_actor_count": unknown_actor_count,
        "unknown_identity_count": unknown_identity_count,
        "distinct_action_kind_count": len(action_counts),
        "both_side_game_count": len(both_side_games),
        "terminal_result_game_count": len(result_games),
        "joined_result_game_count": len(joined_games),
        "joined_behavior_record_count": joined_record_count,
        "joined_behavior_eligible_record_count": joined_eligible_count,
        "behavior_without_result_game_count": len(behavior_games - result_games),
        "result_without_behavior_game_count": len(result_games - behavior_games),
        "conflicting_result_game_count": sum(
            1 for outcomes in result_sets.values() if len(outcomes) > 1
        ),
        "duplicate_result_observation_count": int(
            trajectory_metrics.get("duplicate_result_observation_count", 0)
        ),
        "sequence_order_violation_count": sequence_order_violation_count,
        "timestamp_regression_count": timestamp_regression_count,
        "adjacent_state_pair_count": adjacent_state_pair_count,
        "adjacent_state_match_count": adjacent_state_match_count,
        "result_join_rate": _rate(len(joined_games), len(behavior_games)),
        "both_side_game_rate": _rate(len(both_side_games), len(behavior_games)),
        "behavior_eligible_rate": _rate(behavior_eligible_count, len(records)),
        "joined_behavior_eligible_rate": _rate(
            joined_eligible_count, behavior_eligible_count
        ),
        "post_state_present_rate": _rate(post_state_present_count, len(records)),
        "unknown_actor_rate": _rate(unknown_actor_count, len(records)),
        "unknown_identity_rate": _rate(unknown_identity_count, len(records)),
        "adjacent_state_match_rate": _rate(
            adjacent_state_match_count, adjacent_state_pair_count
        ),
        "actor_side_counts": dict(sorted(side_counts.items())),
        "identity_status_counts": dict(sorted(identity_counts.items())),
        "boundary_status_counts": dict(sorted(boundary_counts.items())),
        "action_kind_counts": dict(sorted(action_counts.items())),
        "choice_status_counts": dict(sorted(choice_status_counts.items())),
        "side_action_kind_counts": _nested_counts(side_action_counts),
        "eligible_side_action_kind_counts": _nested_counts(
            eligible_side_action_counts
        ),
        "mode_counts": dict(sorted(mode_counts.items())),
        "patch_counts": dict(sorted(patch_counts.items())),
        "outcome_counts": dict(sorted(outcome_counts.items())),
        "split_game_counts": dict(sorted(split_game_counts.items())),
        "split_eligible_record_counts": dict(
            sorted(split_eligible_record_counts.items())
        ),
    }
    contract_checks = [
        _check("behavior_corpus_valid", int(bool(behavior_audit["valid"])), "==", 1),
        _check(
            "trajectory_contract_passed",
            int(bool(trajectory_audit.get("contract_passed"))),
            "==",
            1,
        ),
        _check("sequence_order_violation_count", sequence_order_violation_count, "<=", 0),
        _check("timestamp_regression_count", timestamp_regression_count, "<=", 0),
        _check(
            "conflicting_result_game_count",
            metrics["conflicting_result_game_count"],
            "<=",
            0,
        ),
        _check(
            "duplicate_result_observation_count",
            metrics["duplicate_result_observation_count"],
            "<=",
            0,
        ),
        _check(
            "replay_play_source_still_actor_hand_post_count",
            replay_play_source_still_actor_hand_post_count,
            "<=",
            0,
        ),
        _check(
            "replay_attack_source_readiness_missing_count",
            replay_attack_record_count
            - replay_attack_source_readiness_explicit_count,
            "<=",
            0,
        ),
        _check(
            "replay_end_turn_active_player_unchanged_count",
            replay_end_turn_active_player_unchanged_count,
            "<=",
            0,
        ),
    ]
    readiness_checks = [
        _check("has_local_behavior", side_counts["local"], ">=", 1),
        _check("has_opponent_behavior", side_counts["opponent"], ">=", 1),
        _check("has_joined_result_game", len(joined_games), ">=", 1),
        _check("unique_game_count", len(behavior_games), ">=", policy["min_unique_games"]),
        _check("behavior_record_count", len(records), ">=", policy["min_behavior_records"]),
        _check(
            "joined_result_game_count",
            len(joined_games),
            ">=",
            policy["min_joined_result_games"],
        ),
        _check(
            "joined_behavior_record_count",
            joined_record_count,
            ">=",
            policy["min_joined_behavior_records"],
        ),
        _check(
            "behavior_eligible_record_count",
            behavior_eligible_count,
            ">=",
            policy["min_behavior_eligible_records"],
        ),
        _check(
            "local_eligible_record_count",
            local_eligible_count,
            ">=",
            policy["min_local_eligible_records"],
        ),
        _check(
            "opponent_eligible_record_count",
            opponent_eligible_count,
            ">=",
            policy["min_opponent_eligible_records"],
        ),
        _check(
            "distinct_action_kind_count",
            len(action_counts),
            ">=",
            policy["min_distinct_action_kinds"],
        ),
        _check(
            "result_join_rate",
            metrics["result_join_rate"],
            ">=",
            policy["min_result_join_rate"],
        ),
        _check(
            "both_side_game_rate",
            metrics["both_side_game_rate"],
            ">=",
            policy["min_both_side_game_rate"],
        ),
        _check(
            "behavior_eligible_rate",
            metrics["behavior_eligible_rate"],
            ">=",
            policy["min_behavior_eligible_rate"],
        ),
        _check(
            "unknown_actor_rate",
            metrics["unknown_actor_rate"],
            "<=",
            policy["max_unknown_actor_rate"],
        ),
        _check(
            "unknown_identity_rate",
            metrics["unknown_identity_rate"],
            "<=",
            policy["max_unknown_identity_rate"],
        ),
        _check(
            "train_game_count",
            split_game_counts["train"],
            ">=",
            policy["min_train_games"],
        ),
        _check(
            "validation_game_count",
            split_game_counts["validation"],
            ">=",
            policy["min_validation_games"],
        ),
        _check(
            "test_game_count",
            split_game_counts["test"],
            ">=",
            policy["min_test_games"],
        ),
    ]
    contract_passed = all(item["passed"] for item in contract_checks)
    imitation_ready = contract_passed and all(
        item["passed"] for item in readiness_checks
    )
    return {
        "schema": BEHAVIOR_LEARNING_REPORT_SCHEMA_ID,
        "behavior_schema": "advisor-behavior-v1",
        "trajectory_schema": "trajectory-readiness-v1",
        "source_kind": source_kind,
        "behavior_input": behavior_name,
        "behavior_input_sha256": _sha256_bytes(behavior_bytes),
        "behavior_input_bytes": len(behavior_bytes),
        "trajectory_input": trajectory_name,
        "trajectory_input_sha256": _sha256_bytes(trajectory_bytes),
        "trajectory_input_bytes": len(trajectory_bytes),
        "policy": policy,
        "policy_sha256": policy_sha256,
        "metrics": metrics,
        "contract_checks": contract_checks,
        "readiness_checks": readiness_checks,
        "contract_passed": contract_passed,
        "imitation_ready": imitation_ready,
        "rl_training_ready": False,
        "passed": imitation_ready,
        "behavior_audit": {
            key: copy.deepcopy(value)
            for key, value in behavior_audit.items()
            if key != "path"
        },
        "trajectory_contract_audit": {
            "schema": trajectory_audit.get("schema"),
            "input_sha256": trajectory_audit.get("input_sha256"),
            "input_bytes": trajectory_audit.get("input_bytes"),
            "contract_passed": trajectory_audit.get("contract_passed"),
            "metrics": {
                key: trajectory_metrics.get(key, 0)
                for key in (
                    "record_count",
                    "invalid_json_or_record_count",
                    "contract_issue_count",
                    "privacy_violation_count",
                    "terminal_result_observation_count",
                    "terminal_result_game_count",
                    "conflicting_result_game_count",
                    "duplicate_result_observation_count",
                    "split_assignment_mismatch_count",
                    "cross_split_leakage_count",
                )
            },
            "issues": copy.deepcopy(trajectory_audit.get("issues", {})),
        },
        "caveat": (
            "就绪只表示双方公开行为、终局关联、拆分和隐私合同达到本版模仿学习门槛；"
            "观察到的动作不等于最优动作，输出不得直接冒充强化学习轨迹或最优策略标签。"
        ),
    }


def audit_behavior_learning_files(
    behavior_path: str | Path,
    trajectory_path: str | Path,
    *,
    policy_path: str | Path | None = None,
    source_kind: str = SOURCE_KIND_DIRECT_AUDIT,
) -> dict[str, Any]:
    behavior_source, behavior_bytes = _read_input(
        behavior_path, "behavior corpus input"
    )
    trajectory_source, trajectory_bytes = _read_input(
        trajectory_path, "trajectory result input"
    )
    if behavior_source.resolve() == trajectory_source.resolve():
        raise BehaviorLearningError(
            "behavior corpus and trajectory result input must be different files"
        )
    with tempfile.TemporaryDirectory(prefix="metacompanion-behavior-audit-") as directory:
        root = Path(directory)
        behavior_snapshot = root / BEHAVIOR_CORPUS_FILENAME
        trajectory_snapshot = root / "training-v2.jsonl"
        behavior_snapshot.write_bytes(behavior_bytes)
        trajectory_snapshot.write_bytes(trajectory_bytes)
        return _audit_snapshots(
            behavior_snapshot,
            trajectory_snapshot,
            behavior_name=behavior_source.name,
            trajectory_name=trajectory_source.name,
            behavior_bytes=behavior_bytes,
            trajectory_bytes=trajectory_bytes,
            policy_path=policy_path,
            source_kind=source_kind,
        )


def default_runtime_behavior_learning_paths() -> tuple[Path | None, Path | None]:
    app_data = os.environ.get("APPDATA", "").strip()
    if not app_data:
        return None, None
    root = (
        Path(app_data)
        / "HearthstoneDeckTracker"
        / "MetaCompanion"
        / "AdvisorWorker"
    )
    return root / BEHAVIOR_CORPUS_FILENAME, root / "training-v2.jsonl"


def _write_content_addressed_snapshot(
    payload: bytes,
    source_name: str,
    snapshot_directory: str | Path,
) -> tuple[Path, str]:
    digest = _sha256_bytes(payload)
    stem = Path(source_name).stem or "behavior-learning"
    directory = Path(snapshot_directory)
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"{stem}.{digest}.jsonl"
    try:
        with target.open("xb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
    except FileExistsError:
        try:
            existing = target.read_bytes()
        except OSError as exc:
            raise BehaviorLearningError(
                "content-addressed behavior learning snapshot could not be verified"
            ) from exc
        if existing != payload:
            raise BehaviorLearningError(
                "content-addressed behavior learning snapshot collision detected"
            )
    return target, digest


def audit_runtime_behavior_learning(
    *,
    behavior_path: str | Path | None = None,
    trajectory_path: str | Path | None = None,
    snapshot_directory: str | Path,
    policy_path: str | Path | None = None,
) -> dict[str, Any]:
    default_behavior, default_trajectory = default_runtime_behavior_learning_paths()
    behavior_source = Path(behavior_path) if behavior_path is not None else default_behavior
    trajectory_source = (
        Path(trajectory_path) if trajectory_path is not None else default_trajectory
    )
    policy = load_behavior_learning_policy(policy_path)
    base: dict[str, Any] = {
        "schema": RUNTIME_BEHAVIOR_LEARNING_REPORT_SCHEMA_ID,
        "source_kind": SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
        "status": "NO_DATA",
        "behavior_input": (
            behavior_source.name if behavior_source is not None else BEHAVIOR_CORPUS_FILENAME
        ),
        "behavior_input_sha256": "",
        "behavior_input_bytes": 0,
        "behavior_snapshot": "",
        "trajectory_input": (
            trajectory_source.name if trajectory_source is not None else "training-v2.jsonl"
        ),
        "trajectory_input_sha256": "",
        "trajectory_input_bytes": 0,
        "trajectory_snapshot": "",
        "snapshots_content_addressed": False,
        "policy_sha256": behavior_learning_policy_sha256(policy),
        "contract_passed": False,
        "imitation_ready": False,
        "rl_training_ready": False,
        "audit": None,
    }
    if behavior_source is None or not behavior_source.is_file():
        base["reason"] = "runtime_behavior_log_not_found"
        return base
    try:
        behavior_bytes = behavior_source.read_bytes()
    except FileNotFoundError:
        base["reason"] = "runtime_behavior_log_not_found"
        return base
    except OSError as exc:
        raise BehaviorLearningError(
            "runtime behavior input could not be snapshotted"
        ) from exc
    if not behavior_bytes:
        base["reason"] = "runtime_behavior_log_empty"
        return base
    if len(behavior_bytes) > MAX_INPUT_BYTES:
        raise BehaviorLearningError(
            "runtime behavior input exceeds the 256 MiB audit limit"
        )
    behavior_snapshot, behavior_digest = _write_content_addressed_snapshot(
        behavior_bytes,
        behavior_source.name,
        snapshot_directory,
    )
    base.update(
        {
            "behavior_input_sha256": behavior_digest,
            "behavior_input_bytes": len(behavior_bytes),
            "behavior_snapshot": behavior_snapshot.name,
            "status": "NOT_READY",
        }
    )
    if trajectory_source is None or not trajectory_source.is_file():
        base["reason"] = "runtime_trajectory_result_log_not_found"
        return base
    try:
        trajectory_bytes = trajectory_source.read_bytes()
    except FileNotFoundError:
        base["reason"] = "runtime_trajectory_result_log_not_found"
        return base
    except OSError as exc:
        raise BehaviorLearningError(
            "runtime trajectory result input could not be snapshotted"
        ) from exc
    if len(trajectory_bytes) > MAX_INPUT_BYTES:
        raise BehaviorLearningError(
            "runtime trajectory result input exceeds the 256 MiB audit limit"
        )
    trajectory_snapshot, trajectory_digest = _write_content_addressed_snapshot(
        trajectory_bytes,
        trajectory_source.name,
        snapshot_directory,
    )
    audit = _audit_snapshots(
        behavior_snapshot,
        trajectory_snapshot,
        behavior_name=behavior_source.name,
        trajectory_name=trajectory_source.name,
        behavior_bytes=behavior_bytes,
        trajectory_bytes=trajectory_bytes,
        policy_path=policy_path,
        source_kind=SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
    )
    if (
        audit["behavior_input_sha256"] != behavior_digest
        or audit["behavior_input_bytes"] != len(behavior_bytes)
        or audit["trajectory_input_sha256"] != trajectory_digest
        or audit["trajectory_input_bytes"] != len(trajectory_bytes)
    ):
        raise BehaviorLearningError(
            "runtime behavior learning snapshot identity changed during audit"
        )
    ready = bool(audit["imitation_ready"])
    base.update(
        {
            "trajectory_input_sha256": trajectory_digest,
            "trajectory_input_bytes": len(trajectory_bytes),
            "trajectory_snapshot": trajectory_snapshot.name,
            "snapshots_content_addressed": True,
            "policy_sha256": audit["policy_sha256"],
            "contract_passed": bool(audit["contract_passed"]),
            "imitation_ready": ready,
            "status": "READY" if ready else "NOT_READY",
            "reason": (
                "production_behavior_policy_passed"
                if ready
                else "production_behavior_policy_failed"
            ),
            "audit": audit,
        }
    )
    return base


def _actor_outcome(local_outcome: str, actor_side: str) -> str:
    if actor_side == "local" or local_outcome == "tie":
        return local_outcome
    return "loss" if local_outcome == "win" else "win"


def _imitation_record(
    record: BehaviorRecord,
    local_outcome: str,
) -> dict[str, Any]:
    value = record.value
    content: dict[str, Any] = {
        "schema": BEHAVIOR_IMITATION_SCHEMA_ID,
        "source_behavior_id": record.behavior_id,
        "source_content_sha256": record.content_sha256,
        "game_id": record.game_id,
        "behavior_sequence": record.behavior_sequence,
        "split": deterministic_game_split(record.game_id),
        "actor_side": value["actor_side"],
        "actor_player_id": value["actor_player_id"],
        "action": copy.deepcopy(value["action"]),
        "pre_state": copy.deepcopy(value["pre_state"]),
        "post_state": copy.deepcopy(value["post_state"]),
        "local_outcome": local_outcome,
        "actor_outcome": _actor_outcome(local_outcome, str(value["actor_side"])),
        "provenance": {
            "actor_evidence": value["actor_evidence"],
            "identity_status": value["identity_status"],
            "visibility_status": value["visibility_status"],
            "boundary_status": value["boundary_status"],
            "source_event": value["source_event"],
            "observed_behavior": True,
            "optimality_verified": False,
        },
        "imitation_training_eligible": True,
        "rl_training_eligible": False,
    }
    payload = dict(content)
    payload["example_id"] = "imitation-" + _sha256_bytes(_canonical_json(content))
    return _validate_imitation_record(payload)


def _validate_imitation_record(value: Any) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise BehaviorLearningError("imitation example must be an object")
    keys = {str(key) for key in value}
    if keys != _IMITATION_TOP_LEVEL_KEYS:
        raise BehaviorLearningError("imitation example fields do not match the contract")
    content = {key: copy.deepcopy(value[key]) for key in _IMITATION_CONTENT_KEYS}
    if content["schema"] != BEHAVIOR_IMITATION_SCHEMA_ID:
        raise BehaviorLearningError("unsupported imitation example schema")
    digest = _sha256_bytes(_canonical_json(content))
    example_id = value["example_id"]
    if (
        not isinstance(example_id, str)
        or _IMITATION_ID.fullmatch(example_id) is None
        or example_id != "imitation-" + digest
    ):
        raise BehaviorLearningError("imitation example ID does not match its content")
    source_behavior_id = content["source_behavior_id"]
    source_sha256 = content["source_content_sha256"]
    if (
        not isinstance(source_behavior_id, str)
        or _BEHAVIOR_ID.fullmatch(source_behavior_id) is None
        or not isinstance(source_sha256, str)
        or _HEX_SHA256.fullmatch(source_sha256) is None
        or source_behavior_id != "behavior-" + source_sha256
    ):
        raise BehaviorLearningError("imitation source behavior identity is invalid")
    game_id = content["game_id"]
    if not isinstance(game_id, str) or _ANONYMOUS_GAME_ID.fullmatch(game_id) is None:
        raise BehaviorLearningError("imitation game ID is not anonymous")
    if content["split"] != deterministic_game_split(game_id):
        raise BehaviorLearningError("imitation game split is invalid")
    sequence = content["behavior_sequence"]
    if isinstance(sequence, bool) or not isinstance(sequence, int) or sequence <= 0:
        raise BehaviorLearningError("imitation behavior sequence is invalid")
    side = content["actor_side"]
    player_id = content["actor_player_id"]
    if side not in {"local", "opponent"}:
        raise BehaviorLearningError("imitation actor side must be known")
    expected_player = "friendly" if side == "local" else "opponent"
    if player_id != expected_player:
        raise BehaviorLearningError("imitation actor player does not match its side")
    _action(content["action"], strict=True)
    pre_state = public_behavior_state(content["pre_state"], strict=True)
    post_state = public_behavior_state(content["post_state"], strict=True)
    if pre_state["active_player_id"] != player_id:
        raise BehaviorLearningError("imitation actor is not active in the pre-state")
    if not post_state:
        raise BehaviorLearningError("imitation post-state is required")
    local_outcome = content["local_outcome"]
    actor_outcome = content["actor_outcome"]
    if local_outcome not in _OUTCOMES or actor_outcome != _actor_outcome(
        local_outcome, side
    ):
        raise BehaviorLearningError("imitation outcome perspective is invalid")
    provenance = content["provenance"]
    if not isinstance(provenance, Mapping) or set(provenance) != _IMITATION_PROVENANCE_KEYS:
        raise BehaviorLearningError("imitation provenance fields are invalid")
    if provenance.get("observed_behavior") is not True:
        raise BehaviorLearningError("imitation example must remain observed behavior")
    if provenance.get("optimality_verified") is not False:
        raise BehaviorLearningError("observed behavior cannot claim optimality")
    if content["imitation_training_eligible"] is not True:
        raise BehaviorLearningError("imitation example is not marked for imitation use")
    if content["rl_training_eligible"] is not False:
        raise BehaviorLearningError("imitation example cannot be marked for RL use")
    normalized = dict(content)
    normalized["example_id"] = example_id
    return {key: normalized[key] for key in sorted(_IMITATION_TOP_LEVEL_KEYS)}


def _imitation_jsonl_bytes(records: Sequence[Mapping[str, Any]]) -> bytes:
    seen_ids: set[str] = set()
    lines: list[str] = []
    for value in records:
        record = _validate_imitation_record(value)
        example_id = str(record["example_id"])
        if example_id in seen_ids:
            raise BehaviorLearningError("duplicate imitation example ID")
        seen_ids.add(example_id)
        lines.append(_canonical_json(record).decode("utf-8"))
    return (("\n".join(lines) + "\n") if lines else "").encode("utf-8")


def _atomic_write_bytes(payload: bytes, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
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


def write_behavior_learning_report(
    report: Mapping[str, Any], path: str | Path
) -> None:
    payload = json.dumps(report, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    _atomic_write_bytes(payload, Path(path))


def promote_behavior_imitation_file(
    behavior_path: str | Path,
    trajectory_path: str | Path,
    output_path: str | Path,
    manifest_path: str | Path,
    *,
    policy_path: str | Path | None = None,
) -> dict[str, Any]:
    behavior_source, behavior_bytes = _read_input(
        behavior_path, "behavior corpus input"
    )
    trajectory_source, trajectory_bytes = _read_input(
        trajectory_path, "trajectory result input"
    )
    output = Path(output_path).resolve()
    manifest_output = Path(manifest_path).resolve()
    identities = {
        behavior_source.resolve(),
        trajectory_source.resolve(),
        output,
        manifest_output,
    }
    if len(identities) != 4:
        raise BehaviorLearningError(
            "behavior input, result input, imitation output, and manifest must differ"
        )
    with tempfile.TemporaryDirectory(prefix="metacompanion-behavior-promote-") as directory:
        root = Path(directory)
        behavior_snapshot = root / BEHAVIOR_CORPUS_FILENAME
        trajectory_snapshot = root / "training-v2.jsonl"
        behavior_snapshot.write_bytes(behavior_bytes)
        trajectory_snapshot.write_bytes(trajectory_bytes)
        audit = _audit_snapshots(
            behavior_snapshot,
            trajectory_snapshot,
            behavior_name=behavior_source.name,
            trajectory_name=trajectory_source.name,
            behavior_bytes=behavior_bytes,
            trajectory_bytes=trajectory_bytes,
            policy_path=policy_path,
            source_kind=SOURCE_KIND_DIRECT_AUDIT,
        )
        if audit.get("imitation_ready") is not True:
            raise BehaviorLearningError(
                "behavior corpus is not ready for imitation promotion"
            )
        records = _behavior_records(behavior_snapshot)
        result_sets = _terminal_results(trajectory_snapshot)
        outcomes = {
            game_id: next(iter(values))
            for game_id, values in result_sets.items()
            if len(values) == 1
        }
        promoted = [
            _imitation_record(record, outcomes[record.game_id])
            for record in records
            if record.value["behavior_eligible"] is True
            and record.game_id in outcomes
        ]
        imitation_bytes = _imitation_jsonl_bytes(promoted)
        # Parse the final bytes again so output construction and output validation are
        # independent operations before either durable artifact is replaced.
        reparsed = [
            _validate_imitation_record(json.loads(line))
            for line in imitation_bytes.decode("utf-8").splitlines()
            if line.strip()
        ]
        if len(reparsed) != len(promoted) or not reparsed:
            raise BehaviorLearningError("imitation output self-verification failed")

    dataset_sha256 = _sha256_bytes(imitation_bytes)
    split_counts = Counter(str(item["split"]) for item in promoted)
    game_count = len({str(item["game_id"]) for item in promoted})
    manifest: dict[str, Any] = {
        "schema": BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID,
        "imitation_schema": BEHAVIOR_IMITATION_SCHEMA_ID,
        "imitation_ready": True,
        "rl_training_ready": False,
        "source_behavior": {
            "name": behavior_source.name,
            "sha256": _sha256_bytes(behavior_bytes),
            "bytes": len(behavior_bytes),
        },
        "source_trajectory_results": {
            "name": trajectory_source.name,
            "sha256": _sha256_bytes(trajectory_bytes),
            "bytes": len(trajectory_bytes),
        },
        "imitation_dataset": {
            "name": output.name,
            "sha256": dataset_sha256,
            "bytes": len(imitation_bytes),
            "record_count": len(promoted),
            "game_count": game_count,
            "split_record_counts": dict(sorted(split_counts.items())),
        },
        "audit": {
            "schema": audit["schema"],
            "behavior_input_sha256": audit["behavior_input_sha256"],
            "trajectory_input_sha256": audit["trajectory_input_sha256"],
            "policy": audit["policy"],
            "policy_sha256": audit["policy_sha256"],
            "metrics": audit["metrics"],
            "contract_passed": audit["contract_passed"],
            "imitation_ready": audit["imitation_ready"],
        },
        "approved_uses": [
            "behavior_cloning",
            "opponent_behavior_modeling",
            "search_ordering_prior",
        ],
        "prohibited_uses": [
            "direct_rl_trajectory",
            "optimal_action_ground_truth",
            "hidden_opponent_card_reconstruction",
        ],
        "caveat": (
            "该清单证明的只是公开行为语料通过了版本化联审；动作仍是观察样本，"
            "不是最优动作证明，且所有样本继续固定 rl_training_eligible=false。"
        ),
    }
    _atomic_write_bytes(imitation_bytes, output)
    _atomic_write_bytes(
        json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8") + b"\n",
        manifest_output,
    )
    return manifest
