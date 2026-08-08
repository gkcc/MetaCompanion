from __future__ import annotations

import copy
import hashlib
import json
import math
import os
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from .behavior_learning import (
    BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID,
    BEHAVIOR_IMITATION_SCHEMA_ID,
    BEHAVIOR_LEARNING_REPORT_SCHEMA_ID,
    _validate_imitation_record,
    behavior_learning_policy_sha256,
)


BEHAVIOR_PRIOR_POLICY_SCHEMA_ID = "behavior-imitation-prior-policy-v1"
BEHAVIOR_PRIOR_SCHEMA_ID = "behavior-imitation-prior-v2"
BEHAVIOR_PRIOR_MODEL_ID = "hierarchical-behavior-frequency-v1"

MAX_INPUT_BYTES = 256 * 1024 * 1024
OTHER_TEMPLATE = "__other__"
ACTION_KINDS = (
    "attack",
    "end_turn",
    "hero_power",
    "location_activate",
    "play_card",
)
TEMPLATE_ACTION_KINDS = (
    "attack",
    "hero_power",
    "location_activate",
    "play_card",
)
CONTEXT_LEVELS = (
    "global",
    "actor",
    "mode",
    "patch",
    "hero_pair",
    "public_state",
)

DEFAULT_BEHAVIOR_PRIOR_POLICY: dict[str, float | int] = {
    "min_train_games": 30,
    "min_validation_games": 10,
    "min_test_games": 10,
    "min_train_records": 250,
    "min_validation_records": 50,
    "min_test_records": 50,
    "min_validation_seen_template_records": 25,
    "max_validation_kind_log_loss_excess": 0.02,
    "max_validation_seen_template_log_loss_excess": 0.05,
    "max_validation_unseen_template_rate": 0.50,
}

_INTEGER_POLICY_KEYS = {
    "min_train_games",
    "min_validation_games",
    "min_test_games",
    "min_train_records",
    "min_validation_records",
    "min_test_records",
    "min_validation_seen_template_records",
}
_RATE_POLICY_KEYS = {
    "max_validation_kind_log_loss_excess",
    "max_validation_seen_template_log_loss_excess",
    "max_validation_unseen_template_rate",
}
_HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_SAFE_NAME = re.compile(r"^[^/\\\x00]{1,255}$")

_MANIFEST_KEYS = {
    "schema",
    "imitation_schema",
    "imitation_ready",
    "rl_training_ready",
    "source_behavior",
    "source_trajectory_results",
    "imitation_dataset",
    "audit",
    "approved_uses",
    "prohibited_uses",
    "caveat",
}
_SOURCE_KEYS = {"name", "sha256", "bytes"}
_DATASET_KEYS = {
    "name",
    "sha256",
    "bytes",
    "record_count",
    "game_count",
    "split_record_counts",
}
_AUDIT_KEYS = {
    "schema",
    "behavior_input_sha256",
    "trajectory_input_sha256",
    "policy",
    "policy_sha256",
    "metrics",
    "contract_passed",
    "imitation_ready",
}
_APPROVED_USES = [
    "behavior_cloning",
    "opponent_behavior_modeling",
    "search_ordering_prior",
]
_PROHIBITED_USES = [
    "direct_rl_trajectory",
    "optimal_action_ground_truth",
    "hidden_opponent_card_reconstruction",
]

_ARTIFACT_KEYS = {
    "schema",
    "model_type",
    "source_dataset",
    "source_manifest",
    "policy",
    "policy_sha256",
    "training",
    "evaluation",
    "quality_checks",
    "imitation_training_complete",
    "search_ordering_prior_ready",
    "live_policy_eligible",
    "rl_training_eligible",
    "optimality_verified",
    "candidate_generation_allowed",
    "outcome_used_for_training",
    "models",
    "approved_uses",
    "prohibited_uses",
    "caveat",
}
_MODEL_KEYS = {
    "labels",
    "other_label",
    "alpha",
    "prior_strength",
    "context_levels",
    "counts_by_level",
}
_TRAINING_KEYS = {
    "split",
    "record_count",
    "game_count",
    "actor_side_record_counts",
    "action_kind_record_counts",
    "supported_modes",
    "supported_patches",
    "unit_of_analysis",
    "game_level_split",
    "actor_outcome_used",
    "local_outcome_used",
}
_QUALITY_CHECK_SPECS = (
    ("train_game_count", ">=", "min_train_games"),
    ("validation_game_count", ">=", "min_validation_games"),
    ("test_game_count", ">=", "min_test_games"),
    ("train_record_count", ">=", "min_train_records"),
    ("validation_record_count", ">=", "min_validation_records"),
    ("test_record_count", ">=", "min_test_records"),
    (
        "validation_seen_template_record_count",
        ">=",
        "min_validation_seen_template_records",
    ),
    (
        "validation_kind_log_loss_excess",
        "<=",
        "max_validation_kind_log_loss_excess",
    ),
    (
        "validation_seen_template_log_loss_excess",
        "<=",
        "max_validation_seen_template_log_loss_excess",
    ),
    (
        "validation_unseen_template_rate",
        "<=",
        "max_validation_unseen_template_rate",
    ),
)


class BehaviorPriorError(ValueError):
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


def behavior_prior_policy_sha256(policy: Mapping[str, float | int]) -> str:
    return _sha256_bytes(
        _canonical_json(
            {
                "schema": BEHAVIOR_PRIOR_POLICY_SCHEMA_ID,
                "thresholds": dict(policy),
            }
        )
    )


def _strict_json_object(payload: bytes, label: str) -> Mapping[str, Any]:
    def pairs_hook(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise BehaviorPriorError(f"{label} contains duplicate JSON key: {key}")
            result[key] = value
        return result

    try:
        value = json.loads(payload.decode("utf-8"), object_pairs_hook=pairs_hook)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise BehaviorPriorError(f"{label} is not valid UTF-8 JSON") from exc
    if not isinstance(value, Mapping):
        raise BehaviorPriorError(f"{label} root must be an object")
    return value


def _read_input(path: str | Path, label: str) -> tuple[Path, bytes]:
    source = Path(path)
    try:
        payload = source.read_bytes()
    except OSError as exc:
        raise BehaviorPriorError(f"{label} could not be read") from exc
    if len(payload) > MAX_INPUT_BYTES:
        raise BehaviorPriorError(f"{label} exceeds the 256 MiB limit")
    return source, payload


def _positive_integer(value: Any, label: str, *, allow_zero: bool = False) -> int:
    minimum = 0 if allow_zero else 1
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        qualifier = "non-negative" if allow_zero else "positive"
        raise BehaviorPriorError(f"{label} must be a {qualifier} integer")
    return value


def _finite_number(value: Any, label: str, *, minimum: float = 0.0) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise BehaviorPriorError(f"{label} must be numeric")
    result = float(value)
    if not math.isfinite(result) or result < minimum:
        raise BehaviorPriorError(f"{label} is outside the supported range")
    return result


def load_behavior_prior_policy(
    path: str | Path | None = None,
) -> dict[str, float | int]:
    if path is None:
        return dict(DEFAULT_BEHAVIOR_PRIOR_POLICY)
    _, payload = _read_input(path, "behavior prior policy")
    raw = _strict_json_object(payload, "behavior prior policy")
    if set(raw) != {"schema", "thresholds"}:
        raise BehaviorPriorError("behavior prior policy fields do not match the contract")
    if raw.get("schema") != BEHAVIOR_PRIOR_POLICY_SCHEMA_ID:
        raise BehaviorPriorError("unsupported behavior prior policy schema")
    thresholds = raw.get("thresholds")
    if not isinstance(thresholds, Mapping) or set(thresholds) != set(
        DEFAULT_BEHAVIOR_PRIOR_POLICY
    ):
        raise BehaviorPriorError("behavior prior policy thresholds do not match the contract")
    result: dict[str, float | int] = {}
    for key in sorted(DEFAULT_BEHAVIOR_PRIOR_POLICY):
        value = thresholds[key]
        if key in _INTEGER_POLICY_KEYS:
            result[key] = _positive_integer(value, f"policy.{key}")
        elif key in _RATE_POLICY_KEYS:
            number = _finite_number(value, f"policy.{key}")
            if key == "max_validation_unseen_template_rate" and number > 1:
                raise BehaviorPriorError(f"policy.{key} must be between 0 and 1")
            result[key] = number
        else:  # pragma: no cover - the exact-key check makes this unreachable.
            raise BehaviorPriorError(f"unsupported behavior prior policy field: {key}")
    return result


def _parse_imitation_records(payload: bytes) -> list[dict[str, Any]]:
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise BehaviorPriorError("imitation dataset is not valid UTF-8") from exc
    records: list[dict[str, Any]] = []
    seen_examples: set[str] = set()
    seen_sources: set[str] = set()
    seen_sequences: set[tuple[str, int]] = set()
    last_sequence: dict[str, int] = {}
    for line_number, line in enumerate(text.splitlines(), start=1):
        if not line.strip():
            raise BehaviorPriorError(
                f"imitation dataset contains a blank line at {line_number}"
            )
        try:
            raw = json.loads(line)
            record = _validate_imitation_record(raw)
        except (json.JSONDecodeError, ValueError) as exc:
            raise BehaviorPriorError(
                f"invalid imitation example at line {line_number}"
            ) from exc
        if line.encode("utf-8") != _canonical_json(record):
            raise BehaviorPriorError(
                f"imitation example at line {line_number} is not canonical JSON"
            )
        example_id = str(record["example_id"])
        source_id = str(record["source_behavior_id"])
        game_id = str(record["game_id"])
        sequence = int(record["behavior_sequence"])
        if example_id in seen_examples:
            raise BehaviorPriorError("duplicate imitation example ID")
        if source_id in seen_sources:
            raise BehaviorPriorError("duplicate source behavior ID")
        if (game_id, sequence) in seen_sequences:
            raise BehaviorPriorError("duplicate game behavior sequence")
        if sequence <= last_sequence.get(game_id, 0):
            raise BehaviorPriorError("imitation sequence order regressed")
        seen_examples.add(example_id)
        seen_sources.add(source_id)
        seen_sequences.add((game_id, sequence))
        last_sequence[game_id] = sequence
        records.append(record)
    if not records:
        raise BehaviorPriorError("imitation dataset is empty")
    return records


def _validate_name(value: Any, label: str) -> str:
    if not isinstance(value, str) or _SAFE_NAME.fullmatch(value) is None:
        raise BehaviorPriorError(f"{label} must be a plain file name")
    return value


def _validate_sha256(value: Any, label: str) -> str:
    if not isinstance(value, str) or _HEX_SHA256.fullmatch(value) is None:
        raise BehaviorPriorError(f"{label} must be a lowercase SHA-256")
    return value


def _validate_source_identity(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, Mapping) or set(value) != _SOURCE_KEYS:
        raise BehaviorPriorError(f"{label} fields do not match the contract")
    return {
        "name": _validate_name(value["name"], f"{label}.name"),
        "sha256": _validate_sha256(value["sha256"], f"{label}.sha256"),
        "bytes": _positive_integer(value["bytes"], f"{label}.bytes"),
    }


def _split_counts(records: Sequence[Mapping[str, Any]], field: str) -> dict[str, int]:
    if field == "record":
        counts = Counter(str(record["split"]) for record in records)
    else:
        games: dict[str, str] = {}
        for record in records:
            games[str(record["game_id"])] = str(record["split"])
        counts = Counter(games.values())
    return {key: counts.get(key, 0) for key in ("train", "validation", "test")}


def _validate_manifest(
    manifest: Mapping[str, Any],
    *,
    dataset_name: str,
    dataset_bytes: bytes,
    records: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    if set(manifest) != _MANIFEST_KEYS:
        raise BehaviorPriorError("imitation manifest fields do not match the contract")
    if manifest.get("schema") != BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID:
        raise BehaviorPriorError("unsupported imitation manifest schema")
    if manifest.get("imitation_schema") != BEHAVIOR_IMITATION_SCHEMA_ID:
        raise BehaviorPriorError("imitation manifest dataset schema mismatch")
    if manifest.get("imitation_ready") is not True:
        raise BehaviorPriorError("imitation manifest is not ready")
    if manifest.get("rl_training_ready") is not False:
        raise BehaviorPriorError("imitation manifest cannot be RL-ready")
    source_behavior = _validate_source_identity(
        manifest.get("source_behavior"), "manifest.source_behavior"
    )
    source_trajectory = _validate_source_identity(
        manifest.get("source_trajectory_results"),
        "manifest.source_trajectory_results",
    )
    dataset = manifest.get("imitation_dataset")
    if not isinstance(dataset, Mapping) or set(dataset) != _DATASET_KEYS:
        raise BehaviorPriorError("manifest.imitation_dataset fields do not match")
    dataset_identity = {
        "name": _validate_name(dataset["name"], "manifest.imitation_dataset.name"),
        "sha256": _validate_sha256(
            dataset["sha256"], "manifest.imitation_dataset.sha256"
        ),
        "bytes": _positive_integer(
            dataset["bytes"], "manifest.imitation_dataset.bytes"
        ),
        "record_count": _positive_integer(
            dataset["record_count"], "manifest.imitation_dataset.record_count"
        ),
        "game_count": _positive_integer(
            dataset["game_count"], "manifest.imitation_dataset.game_count"
        ),
    }
    reported_splits = dataset.get("split_record_counts")
    if not isinstance(reported_splits, Mapping):
        raise BehaviorPriorError("manifest split record counts must be an object")
    normalized_reported_splits: dict[str, int] = {}
    for key, value in reported_splits.items():
        if key not in {"train", "validation", "test"}:
            raise BehaviorPriorError("manifest contains an unknown dataset split")
        normalized_reported_splits[str(key)] = _positive_integer(
            value, f"manifest.split_record_counts.{key}"
        )
    actual_split_records = Counter(str(record["split"]) for record in records)
    expected_reported_splits = {
        key: actual_split_records[key]
        for key in ("train", "validation", "test")
        if actual_split_records[key] > 0
    }
    actual_games = {str(record["game_id"]) for record in records}
    if (
        dataset_identity["name"] != dataset_name
        or dataset_identity["sha256"] != _sha256_bytes(dataset_bytes)
        or dataset_identity["bytes"] != len(dataset_bytes)
        or dataset_identity["record_count"] != len(records)
        or dataset_identity["game_count"] != len(actual_games)
        or normalized_reported_splits != expected_reported_splits
    ):
        raise BehaviorPriorError("imitation manifest does not bind the dataset bytes")
    audit = manifest.get("audit")
    if not isinstance(audit, Mapping) or set(audit) != _AUDIT_KEYS:
        raise BehaviorPriorError("manifest audit fields do not match the contract")
    if (
        audit.get("schema") != BEHAVIOR_LEARNING_REPORT_SCHEMA_ID
        or audit.get("behavior_input_sha256") != source_behavior["sha256"]
        or audit.get("trajectory_input_sha256") != source_trajectory["sha256"]
        or not isinstance(audit.get("policy"), Mapping)
        or not isinstance(audit.get("metrics"), Mapping)
        or audit.get("contract_passed") is not True
        or audit.get("imitation_ready") is not True
    ):
        raise BehaviorPriorError("manifest audit binding is invalid")
    replay_metrics = audit["metrics"]
    replay_count_fields = (
        "replay_behavior_record_count",
        "replay_play_card_record_count",
        "replay_play_source_still_actor_hand_post_count",
        "replay_attack_record_count",
        "replay_attack_source_readiness_explicit_count",
        "replay_end_turn_record_count",
        "replay_end_turn_active_player_unchanged_count",
    )
    normalized_replay_counts: dict[str, int] = {}
    for field in replay_count_fields:
        if field not in replay_metrics:
            raise BehaviorPriorError(
                "manifest audit is missing the replay transition contract"
            )
        normalized_replay_counts[field] = _positive_integer(
            replay_metrics[field],
            f"manifest.audit.metrics.{field}",
            allow_zero=True,
        )
    replay_action_subset = sum(
        normalized_replay_counts[field]
        for field in (
            "replay_play_card_record_count",
            "replay_attack_record_count",
            "replay_end_turn_record_count",
        )
    )
    if (
        replay_action_subset
        > normalized_replay_counts["replay_behavior_record_count"]
        or normalized_replay_counts[
            "replay_play_source_still_actor_hand_post_count"
        ]
        != 0
        or normalized_replay_counts[
            "replay_attack_source_readiness_explicit_count"
        ]
        != normalized_replay_counts["replay_attack_record_count"]
        or normalized_replay_counts[
            "replay_end_turn_active_player_unchanged_count"
        ]
        != 0
    ):
        raise BehaviorPriorError("manifest replay transition evidence did not pass")
    audit_policy_sha256 = _validate_sha256(
        audit.get("policy_sha256"), "manifest.audit.policy_sha256"
    )
    try:
        computed_audit_policy_sha256 = behavior_learning_policy_sha256(audit["policy"])
    except (TypeError, ValueError) as exc:
        raise BehaviorPriorError("manifest audit policy is invalid") from exc
    if audit_policy_sha256 != computed_audit_policy_sha256:
        raise BehaviorPriorError("manifest audit policy SHA-256 mismatch")
    if manifest.get("approved_uses") != _APPROVED_USES:
        raise BehaviorPriorError("imitation manifest approved uses drifted")
    if manifest.get("prohibited_uses") != _PROHIBITED_USES:
        raise BehaviorPriorError("imitation manifest prohibited uses drifted")
    caveat = manifest.get("caveat")
    if (
        not isinstance(caveat, str)
        or "不是最优动作证明" not in caveat
        or "rl_training_eligible=false" not in caveat
    ):
        raise BehaviorPriorError("imitation manifest caveat is missing")
    return {
        "source_behavior": source_behavior,
        "source_trajectory_results": source_trajectory,
        "imitation_dataset": {
            **dataset_identity,
            "split_record_counts": expected_reported_splits,
        },
        "audit": copy.deepcopy(dict(audit)),
    }


def load_and_validate_behavior_imitation(
    dataset_path: str | Path,
    manifest_path: str | Path,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    dataset_source, dataset_bytes = _read_input(
        dataset_path, "behavior imitation dataset"
    )
    manifest_source, manifest_bytes = _read_input(
        manifest_path, "behavior imitation manifest"
    )
    if dataset_source.resolve() == manifest_source.resolve():
        raise BehaviorPriorError("imitation dataset and manifest must differ")
    records = _parse_imitation_records(dataset_bytes)
    manifest = _strict_json_object(manifest_bytes, "behavior imitation manifest")
    validated_manifest = _validate_manifest(
        manifest,
        dataset_name=dataset_source.name,
        dataset_bytes=dataset_bytes,
        records=records,
    )
    return records, {
        "dataset_path": dataset_source,
        "dataset_bytes": dataset_bytes,
        "dataset_sha256": _sha256_bytes(dataset_bytes),
        "manifest_path": manifest_source,
        "manifest_bytes": manifest_bytes,
        "manifest_sha256": _sha256_bytes(manifest_bytes),
        "manifest": validated_manifest,
    }


def _mode(value: Any) -> str:
    text = str(value or "").strip().lower()
    if "arena" in text:
        return "arena"
    if "standard" in text or "ranked" in text:
        return "standard"
    return text or "unknown"


def _card_id(value: Any) -> str:
    if not isinstance(value, Mapping):
        return ""
    result = value.get("card_id")
    return result if isinstance(result, str) else ""


def _bucket(value: Any, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        return -1
    return min(maximum, max(0, value))


def _context_keys(record: Mapping[str, Any]) -> dict[str, str]:
    state = record["pre_state"]
    actor_player_id = str(record["actor_player_id"])
    other_player_id = "opponent" if actor_player_id == "friendly" else "friendly"
    actor = state[actor_player_id]
    other = state[other_player_id]
    actor_side = str(record["actor_side"])
    mode = _mode(state.get("mode"))
    patch = str(state.get("patch") or "unknown")
    actor_hero = _card_id(actor.get("hero")) or "unknown"
    other_hero = _card_id(other.get("hero")) or "unknown"
    turn = _bucket(state.get("turn"), 60)
    turn_bucket = -1 if turn < 0 else min(15, turn // 2)
    public_features = [
        _bucket(actor.get("mana"), 20),
        _bucket(actor.get("max_mana"), 20),
        _bucket(len(actor.get("hand", [])), 12),
        _bucket(len(actor.get("board", [])), 7),
        _bucket(len(other.get("board", [])), 7),
        int(bool(actor.get("hero_power_available"))),
        turn_bucket,
    ]
    values: dict[str, list[Any]] = {
        "global": [],
        "actor": [actor_side],
        "mode": [actor_side, mode],
        "patch": [actor_side, mode, patch],
        "hero_pair": [actor_side, mode, patch, actor_hero, other_hero],
        "public_state": [
            actor_side,
            mode,
            patch,
            actor_hero,
            other_hero,
            *public_features,
        ],
    }
    return {
        level: _canonical_json(values[level]).decode("utf-8")
        for level in CONTEXT_LEVELS
    }


def _find_entity_role(
    state: Mapping[str, Any], actor_player_id: str, entity_id: str
) -> str:
    if not entity_id:
        return "none"
    for player_id in ("friendly", "opponent"):
        player = state[player_id]
        relation = "self" if player_id == actor_player_id else "enemy"
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping) and str(entity.get("entity_id") or "") == entity_id:
                return f"{relation}_{zone}"
        for zone in ("hand", "board"):
            for entity in player.get(zone, []):
                if str(entity.get("entity_id") or "") == entity_id:
                    return f"{relation}_{zone}"
    return "unknown"


def _source_card_id(state: Mapping[str, Any], entity_id: str) -> str:
    if not entity_id:
        return ""
    for player_id in ("friendly", "opponent"):
        player = state[player_id]
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping) and str(entity.get("entity_id") or "") == entity_id:
                return _card_id(entity)
        for zone in ("hand", "board"):
            for entity in player.get(zone, []):
                if str(entity.get("entity_id") or "") == entity_id:
                    return _card_id(entity)
    return ""


def _template_label(record: Mapping[str, Any]) -> str:
    action = record["action"]
    state = record["pre_state"]
    card_id = str(action.get("card_id") or "") or _source_card_id(
        state, str(action.get("source_entity_id") or "")
    )
    target_role = _find_entity_role(
        state,
        str(record["actor_player_id"]),
        str(action.get("target_entity_id") or ""),
    )
    template: list[Any] = [card_id or "unknown", target_role]
    board_position = action.get("board_position", 0)
    if (
        isinstance(board_position, int)
        and not isinstance(board_position, bool)
        and board_position > 0
    ):
        template.append(board_position)
    return _canonical_json(template).decode("utf-8")


def _hierarchical_prior_strength(label_count: int, alpha: float) -> float:
    # A fixed strength of eight is adequate for the five action kinds but badly
    # overfits sparse card/target templates with hundreds of labels. Give the
    # parent distribution at least the same total pseudo-count mass as the
    # global symmetric Dirichlet prior, while preserving the old small-model
    # behavior. This is label-only and never inspects validation/test outcomes.
    return max(8.0, float(label_count) * alpha)


def _build_count_model(
    records: Sequence[Mapping[str, Any]],
    label: Callable[[Mapping[str, Any]], str],
    *,
    labels: Sequence[str] | None = None,
    other_label: str = "",
    alpha: float = 0.5,
    prior_strength: float | None = None,
) -> dict[str, Any]:
    learned_labels = sorted(set(labels or (label(record) for record in records)))
    if other_label and other_label not in learned_labels:
        learned_labels.append(other_label)
    if not learned_labels:
        learned_labels = [other_label] if other_label else []
    if not learned_labels:
        raise BehaviorPriorError("count model has no labels")
    effective_prior_strength = (
        _hierarchical_prior_strength(len(learned_labels), alpha)
        if prior_strength is None
        else float(prior_strength)
    )
    counts: dict[str, dict[str, Counter[str]]] = {
        level: defaultdict(Counter) for level in CONTEXT_LEVELS
    }
    # Keep a valid smoothed global bucket even when a particular non-end action kind
    # has not appeared in the train split yet.
    counts["global"]["[]"]
    for record in records:
        value = label(record)
        contexts = _context_keys(record)
        for level in CONTEXT_LEVELS:
            counts[level][contexts[level]][value] += 1
    serialized: dict[str, dict[str, dict[str, Any]]] = {}
    for level in CONTEXT_LEVELS:
        serialized[level] = {}
        for key in sorted(counts[level]):
            bucket = counts[level][key]
            serialized[level][key] = {
                "total": sum(bucket.values()),
                "counts": {name: bucket[name] for name in sorted(bucket)},
            }
    return {
        "labels": learned_labels,
        "other_label": other_label,
        "alpha": alpha,
        "prior_strength": effective_prior_strength,
        "context_levels": list(CONTEXT_LEVELS),
        "counts_by_level": serialized,
    }


def _model_probabilities(
    model: Mapping[str, Any],
    contexts: Mapping[str, str],
    *,
    global_only: bool = False,
) -> dict[str, float]:
    labels = [str(value) for value in model["labels"]]
    alpha = float(model["alpha"])
    strength = float(model["prior_strength"])
    probabilities = {label: 1.0 / len(labels) for label in labels}
    for level in model["context_levels"]:
        if global_only and level != "global":
            break
        bucket = model["counts_by_level"].get(level, {}).get(contexts[level])
        if not isinstance(bucket, Mapping):
            continue
        total = float(bucket["total"])
        bucket_counts = bucket["counts"]
        if level == "global":
            denominator = total + alpha * len(labels)
            probabilities = {
                label: (float(bucket_counts.get(label, 0)) + alpha) / denominator
                for label in labels
            }
        else:
            denominator = total + strength
            probabilities = {
                label: (
                    float(bucket_counts.get(label, 0))
                    + strength * probabilities[label]
                )
                / denominator
                for label in labels
            }
    return probabilities


def _average(values: Sequence[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def _game_macro(values: Mapping[str, Sequence[float]]) -> float:
    return _average([_average(items) for items in values.values() if items])


def _evaluate_split(
    records: Sequence[Mapping[str, Any]], models: Mapping[str, Any]
) -> dict[str, Any]:
    if not records:
        return {
            "status": "NO_DATA",
            "record_count": 0,
            "game_count": 0,
            "seen_template_record_count": 0,
            "unseen_template_count": 0,
            "unseen_template_rate": 0.0,
        }
    kind_model = models["action_kind"]
    template_models = models["action_template_by_kind"]
    kind_losses: list[float] = []
    global_kind_losses: list[float] = []
    kind_top1: list[float] = []
    global_kind_top1: list[float] = []
    template_losses: list[float] = []
    global_template_losses: list[float] = []
    template_top1: list[float] = []
    global_template_top1: list[float] = []
    game_kind_losses: dict[str, list[float]] = defaultdict(list)
    game_global_kind_losses: dict[str, list[float]] = defaultdict(list)
    game_template_losses: dict[str, list[float]] = defaultdict(list)
    game_global_template_losses: dict[str, list[float]] = defaultdict(list)
    unseen_templates = 0
    template_records = 0
    seen_template_records = 0
    for record in records:
        game_id = str(record["game_id"])
        contexts = _context_keys(record)
        kind = str(record["action"]["kind"])
        contextual_kind = _model_probabilities(kind_model, contexts)
        global_kind = _model_probabilities(kind_model, contexts, global_only=True)
        contextual_kind_loss = -math.log(max(1e-12, contextual_kind[kind]))
        global_kind_loss = -math.log(max(1e-12, global_kind[kind]))
        kind_losses.append(contextual_kind_loss)
        global_kind_losses.append(global_kind_loss)
        game_kind_losses[game_id].append(contextual_kind_loss)
        game_global_kind_losses[game_id].append(global_kind_loss)
        kind_top1.append(
            float(max(contextual_kind, key=lambda key: (contextual_kind[key], key)) == kind)
        )
        global_kind_top1.append(
            float(max(global_kind, key=lambda key: (global_kind[key], key)) == kind)
        )
        if kind == "end_turn":
            continue
        template_records += 1
        template = _template_label(record)
        model = template_models[kind]
        known_labels = set(model["labels"]) - {OTHER_TEMPLATE}
        if template not in known_labels:
            unseen_templates += 1
            continue
        seen_template_records += 1
        contextual_template = _model_probabilities(model, contexts)
        global_template = _model_probabilities(model, contexts, global_only=True)
        contextual_template_loss = -math.log(
            max(1e-12, contextual_template[template])
        )
        global_template_loss = -math.log(max(1e-12, global_template[template]))
        template_losses.append(contextual_template_loss)
        global_template_losses.append(global_template_loss)
        game_template_losses[game_id].append(contextual_template_loss)
        game_global_template_losses[game_id].append(global_template_loss)
        contextual_known = {key: contextual_template[key] for key in known_labels}
        global_known = {key: global_template[key] for key in known_labels}
        template_top1.append(
            float(
                max(contextual_known, key=lambda key: (contextual_known[key], key))
                == template
            )
        )
        global_template_top1.append(
            float(max(global_known, key=lambda key: (global_known[key], key)) == template)
        )
    return {
        "status": "EVALUATED",
        "record_count": len(records),
        "game_count": len({str(record["game_id"]) for record in records}),
        "actor_side_record_counts": dict(
            sorted(Counter(str(record["actor_side"]) for record in records).items())
        ),
        "action_kind_record_counts": dict(
            sorted(Counter(str(record["action"]["kind"]) for record in records).items())
        ),
        "kind_log_loss": _average(kind_losses),
        "global_kind_log_loss": _average(global_kind_losses),
        "kind_log_loss_excess": _average(kind_losses) - _average(global_kind_losses),
        "kind_top1_accuracy": _average(kind_top1),
        "global_kind_top1_accuracy": _average(global_kind_top1),
        "game_macro_kind_log_loss": _game_macro(game_kind_losses),
        "game_macro_global_kind_log_loss": _game_macro(game_global_kind_losses),
        "template_record_count": template_records,
        "seen_template_record_count": seen_template_records,
        "unseen_template_count": unseen_templates,
        "unseen_template_rate": (
            unseen_templates / template_records if template_records else 0.0
        ),
        "seen_template_log_loss": _average(template_losses),
        "global_seen_template_log_loss": _average(global_template_losses),
        "seen_template_log_loss_excess": _average(template_losses)
        - _average(global_template_losses),
        "seen_template_top1_accuracy": _average(template_top1),
        "global_seen_template_top1_accuracy": _average(global_template_top1),
        "game_macro_seen_template_log_loss": _game_macro(game_template_losses),
        "game_macro_global_seen_template_log_loss": _game_macro(
            game_global_template_losses
        ),
        "caveat": (
            "Held-out labels are observed actions. Metrics measure behavior prediction, "
            "not legal-action coverage, reward, or optimality."
        ),
    }


def _check(
    name: str, actual: int | float, operator: str, expected: int | float
) -> dict[str, Any]:
    passed = actual >= expected if operator == ">=" else actual <= expected
    return {
        "name": name,
        "actual": actual,
        "operator": operator,
        "expected": expected,
        "passed": passed,
    }


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


def _validate_count_model(
    value: Any, label: str, *, expected_labels: Sequence[str] | None = None
) -> dict[str, Any]:
    if not isinstance(value, Mapping) or set(value) != _MODEL_KEYS:
        raise BehaviorPriorError(f"{label} fields do not match the model contract")
    labels = value.get("labels")
    if (
        not isinstance(labels, list)
        or not labels
        or any(not isinstance(item, str) or not item for item in labels)
        or len(set(labels)) != len(labels)
    ):
        raise BehaviorPriorError(f"{label}.labels are invalid")
    if expected_labels is not None and labels != list(expected_labels):
        raise BehaviorPriorError(f"{label}.labels drifted")
    other_label = value.get("other_label")
    if not isinstance(other_label, str) or (other_label and other_label not in labels):
        raise BehaviorPriorError(f"{label}.other_label is invalid")
    alpha = _finite_number(value.get("alpha"), f"{label}.alpha", minimum=1e-12)
    strength = _finite_number(
        value.get("prior_strength"), f"{label}.prior_strength", minimum=1e-12
    )
    if value.get("context_levels") != list(CONTEXT_LEVELS):
        raise BehaviorPriorError(f"{label}.context_levels drifted")
    raw_levels = value.get("counts_by_level")
    if not isinstance(raw_levels, Mapping) or set(raw_levels) != set(CONTEXT_LEVELS):
        raise BehaviorPriorError(f"{label}.counts_by_level is invalid")
    normalized_levels: dict[str, dict[str, dict[str, Any]]] = {}
    for level in CONTEXT_LEVELS:
        raw_contexts = raw_levels[level]
        if not isinstance(raw_contexts, Mapping):
            raise BehaviorPriorError(f"{label}.{level} contexts must be an object")
        normalized_levels[level] = {}
        for context_key, bucket in raw_contexts.items():
            if not isinstance(context_key, str) or not isinstance(bucket, Mapping):
                raise BehaviorPriorError(f"{label}.{level} contains an invalid bucket")
            if set(bucket) != {"total", "counts"} or not isinstance(
                bucket.get("counts"), Mapping
            ):
                raise BehaviorPriorError(f"{label}.{level} bucket fields are invalid")
            total = _positive_integer(
                bucket.get("total"), f"{label}.{level}.total", allow_zero=True
            )
            normalized_counts: dict[str, int] = {}
            for name, count in bucket["counts"].items():
                if name not in labels:
                    raise BehaviorPriorError(f"{label}.{level} has an unknown label")
                normalized_counts[str(name)] = _positive_integer(
                    count, f"{label}.{level}.counts.{name}"
                )
            if sum(normalized_counts.values()) != total:
                raise BehaviorPriorError(f"{label}.{level} bucket total is inconsistent")
            normalized_levels[level][context_key] = {
                "total": total,
                "counts": normalized_counts,
            }
    if set(normalized_levels["global"]) != {"[]"}:
        raise BehaviorPriorError(f"{label} must contain exactly one global bucket")
    global_bucket = normalized_levels["global"]["[]"]
    global_counts = Counter(global_bucket["counts"])
    for level in CONTEXT_LEVELS[1:]:
        level_total = sum(
            bucket["total"] for bucket in normalized_levels[level].values()
        )
        level_counts: Counter[str] = Counter()
        for bucket in normalized_levels[level].values():
            level_counts.update(bucket["counts"])
        if level_total != global_bucket["total"] or level_counts != global_counts:
            raise BehaviorPriorError(
                f"{label}.{level} counts do not reconcile with the global bucket"
            )
    return {
        "labels": list(labels),
        "other_label": other_label,
        "alpha": alpha,
        "prior_strength": strength,
        "context_levels": list(CONTEXT_LEVELS),
        "counts_by_level": normalized_levels,
    }


def _validate_split_count_mapping(
    value: Any, label: str, *, require_sum: int
) -> dict[str, int]:
    if not isinstance(value, Mapping) or set(value) != {"train", "validation", "test"}:
        raise BehaviorPriorError(f"{label} must contain all three game-level splits")
    result = {
        split: _positive_integer(
            value[split], f"{label}.{split}", allow_zero=True
        )
        for split in ("train", "validation", "test")
    }
    if sum(result.values()) != require_sum:
        raise BehaviorPriorError(f"{label} total is inconsistent")
    return result


def _validate_string_list(value: Any, label: str) -> list[str]:
    if (
        not isinstance(value, list)
        or not value
        or any(not isinstance(item, str) or not item for item in value)
        or value != sorted(set(value))
    ):
        raise BehaviorPriorError(f"{label} must be a sorted non-empty unique list")
    return list(value)


def _validate_evaluation_split(
    value: Any,
    label: str,
    *,
    expected_records: int,
    expected_games: int,
) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise BehaviorPriorError(f"{label} must be an object")
    if value.get("status") == "NO_DATA":
        expected_keys = {
            "status",
            "record_count",
            "game_count",
            "seen_template_record_count",
            "unseen_template_count",
            "unseen_template_rate",
        }
        if set(value) != expected_keys or expected_records != 0 or expected_games != 0:
            raise BehaviorPriorError(f"{label} NO_DATA fields are inconsistent")
        for field in (
            "record_count",
            "game_count",
            "seen_template_record_count",
            "unseen_template_count",
        ):
            if value.get(field) != 0:
                raise BehaviorPriorError(f"{label}.{field} must be zero")
        if value.get("unseen_template_rate") != 0.0:
            raise BehaviorPriorError(f"{label}.unseen_template_rate must be zero")
        return copy.deepcopy(dict(value))
    expected_keys = {
        "status",
        "record_count",
        "game_count",
        "actor_side_record_counts",
        "action_kind_record_counts",
        "kind_log_loss",
        "global_kind_log_loss",
        "kind_log_loss_excess",
        "kind_top1_accuracy",
        "global_kind_top1_accuracy",
        "game_macro_kind_log_loss",
        "game_macro_global_kind_log_loss",
        "template_record_count",
        "seen_template_record_count",
        "unseen_template_count",
        "unseen_template_rate",
        "seen_template_log_loss",
        "global_seen_template_log_loss",
        "seen_template_log_loss_excess",
        "seen_template_top1_accuracy",
        "global_seen_template_top1_accuracy",
        "game_macro_seen_template_log_loss",
        "game_macro_global_seen_template_log_loss",
        "caveat",
    }
    if value.get("status") != "EVALUATED" or set(value) != expected_keys:
        raise BehaviorPriorError(f"{label} evaluated fields do not match the contract")
    if value.get("record_count") != expected_records or value.get("game_count") != expected_games:
        raise BehaviorPriorError(f"{label} does not match its held-out split")
    actor_counts = value.get("actor_side_record_counts")
    if not isinstance(actor_counts, Mapping) or any(
        key not in {"local", "opponent"} for key in actor_counts
    ):
        raise BehaviorPriorError(f"{label}.actor_side_record_counts are invalid")
    normalized_actor_counts = {
        str(key): _positive_integer(count, f"{label}.actor_side.{key}")
        for key, count in actor_counts.items()
    }
    if sum(normalized_actor_counts.values()) != expected_records:
        raise BehaviorPriorError(f"{label} actor-side counts are inconsistent")
    action_counts = value.get("action_kind_record_counts")
    if not isinstance(action_counts, Mapping) or any(
        key not in ACTION_KINDS for key in action_counts
    ):
        raise BehaviorPriorError(f"{label}.action_kind_record_counts are invalid")
    normalized_action_counts = {
        str(key): _positive_integer(count, f"{label}.action_kind.{key}")
        for key, count in action_counts.items()
    }
    if sum(normalized_action_counts.values()) != expected_records:
        raise BehaviorPriorError(f"{label} action-kind counts are inconsistent")
    template_records = _positive_integer(
        value.get("template_record_count"),
        f"{label}.template_record_count",
        allow_zero=True,
    )
    seen_templates = _positive_integer(
        value.get("seen_template_record_count"),
        f"{label}.seen_template_record_count",
        allow_zero=True,
    )
    unseen_templates = _positive_integer(
        value.get("unseen_template_count"),
        f"{label}.unseen_template_count",
        allow_zero=True,
    )
    if (
        template_records != expected_records - normalized_action_counts.get("end_turn", 0)
        or seen_templates + unseen_templates != template_records
    ):
        raise BehaviorPriorError(f"{label} template counts are inconsistent")
    rate_fields = {
        "kind_top1_accuracy",
        "global_kind_top1_accuracy",
        "unseen_template_rate",
        "seen_template_top1_accuracy",
        "global_seen_template_top1_accuracy",
    }
    numeric_fields = expected_keys - {
        "status",
        "record_count",
        "game_count",
        "actor_side_record_counts",
        "action_kind_record_counts",
        "template_record_count",
        "seen_template_record_count",
        "unseen_template_count",
        "caveat",
    }
    normalized = copy.deepcopy(dict(value))
    for field in numeric_fields:
        if field.endswith("_excess"):
            raw_number = value.get(field)
            if isinstance(raw_number, bool) or not isinstance(raw_number, (int, float)):
                raise BehaviorPriorError(f"{label}.{field} must be numeric")
            number = float(raw_number)
            if not math.isfinite(number):
                raise BehaviorPriorError(f"{label}.{field} must be finite")
        else:
            number = _finite_number(value.get(field), f"{label}.{field}")
        if field in rate_fields and number > 1:
            raise BehaviorPriorError(f"{label}.{field} must be between 0 and 1")
        normalized[field] = number
    expected_unseen_rate = (
        unseen_templates / template_records if template_records else 0.0
    )
    if not math.isclose(
        float(normalized["unseen_template_rate"]),
        expected_unseen_rate,
        rel_tol=0.0,
        abs_tol=1e-12,
    ):
        raise BehaviorPriorError(f"{label} unseen-template rate is inconsistent")
    if not isinstance(value.get("caveat"), str) or "not" not in value["caveat"]:
        raise BehaviorPriorError(f"{label} caveat is missing")
    normalized["actor_side_record_counts"] = normalized_actor_counts
    normalized["action_kind_record_counts"] = normalized_action_counts
    return normalized


def validate_behavior_prior_artifact(value: Any) -> dict[str, Any]:
    if not isinstance(value, Mapping) or set(value) != _ARTIFACT_KEYS:
        raise BehaviorPriorError("behavior prior artifact fields do not match the contract")
    if value.get("schema") != BEHAVIOR_PRIOR_SCHEMA_ID:
        raise BehaviorPriorError("unsupported behavior prior artifact schema")
    if value.get("model_type") != BEHAVIOR_PRIOR_MODEL_ID:
        raise BehaviorPriorError("unsupported behavior prior model type")
    for field in (
        "imitation_training_complete",
        "search_ordering_prior_ready",
        "live_policy_eligible",
        "rl_training_eligible",
        "optimality_verified",
        "candidate_generation_allowed",
        "outcome_used_for_training",
    ):
        if not isinstance(value.get(field), bool):
            raise BehaviorPriorError(f"artifact.{field} must be boolean")
    if value.get("imitation_training_complete") is not True:
        raise BehaviorPriorError("behavior prior training is not complete")
    for field in (
        "live_policy_eligible",
        "rl_training_eligible",
        "optimality_verified",
        "candidate_generation_allowed",
        "outcome_used_for_training",
    ):
        if value.get(field) is not False:
            raise BehaviorPriorError(f"artifact.{field} must remain false")
    source_dataset = value.get("source_dataset")
    if not isinstance(source_dataset, Mapping) or set(source_dataset) != {
        "name",
        "sha256",
        "bytes",
        "record_count",
        "game_count",
        "split_record_counts",
        "split_game_counts",
    }:
        raise BehaviorPriorError("artifact source dataset identity is invalid")
    source_name = _validate_name(
        source_dataset.get("name"), "artifact.source_dataset.name"
    )
    source_sha256 = _validate_sha256(
        source_dataset.get("sha256"), "artifact.source_dataset.sha256"
    )
    source_bytes = _positive_integer(
        source_dataset.get("bytes"), "artifact.source_dataset.bytes"
    )
    source_record_count = _positive_integer(
        source_dataset.get("record_count"), "artifact.source_dataset.record_count"
    )
    source_game_count = _positive_integer(
        source_dataset.get("game_count"), "artifact.source_dataset.game_count"
    )
    split_record_counts = _validate_split_count_mapping(
        source_dataset.get("split_record_counts"),
        "artifact.source_dataset.split_record_counts",
        require_sum=source_record_count,
    )
    split_game_counts = _validate_split_count_mapping(
        source_dataset.get("split_game_counts"),
        "artifact.source_dataset.split_game_counts",
        require_sum=source_game_count,
    )
    source_manifest = value.get("source_manifest")
    if not isinstance(source_manifest, Mapping) or set(source_manifest) != {
        "name",
        "sha256",
        "schema",
    }:
        raise BehaviorPriorError("artifact source manifest identity is invalid")
    _validate_name(source_manifest.get("name"), "artifact.source_manifest.name")
    _validate_sha256(source_manifest.get("sha256"), "artifact.source_manifest.sha256")
    if source_manifest.get("schema") != BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID:
        raise BehaviorPriorError("artifact source manifest schema mismatch")
    policy = value.get("policy")
    if not isinstance(policy, Mapping) or set(policy) != set(
        DEFAULT_BEHAVIOR_PRIOR_POLICY
    ):
        raise BehaviorPriorError("artifact policy fields drifted")
    expected_policy = load_behavior_prior_policy_from_mapping(policy)
    if value.get("policy_sha256") != behavior_prior_policy_sha256(expected_policy):
        raise BehaviorPriorError("artifact policy SHA-256 mismatch")
    models = value.get("models")
    if not isinstance(models, Mapping) or set(models) != {
        "action_kind",
        "action_template_by_kind",
    }:
        raise BehaviorPriorError("artifact models are invalid")
    action_kind_model = _validate_count_model(
        models["action_kind"], "artifact.models.action_kind", expected_labels=ACTION_KINDS
    )
    template_models = models["action_template_by_kind"]
    if not isinstance(template_models, Mapping) or set(template_models) != set(
        TEMPLATE_ACTION_KINDS
    ):
        raise BehaviorPriorError("artifact template models are invalid")
    normalized_templates = {
        kind: _validate_count_model(
            template_models[kind], f"artifact.models.action_template_by_kind.{kind}"
        )
        for kind in TEMPLATE_ACTION_KINDS
    }
    training = value.get("training")
    if not isinstance(training, Mapping) or set(training) != _TRAINING_KEYS:
        raise BehaviorPriorError("artifact training fields do not match the contract")
    if (
        training.get("split") != "train"
        or training.get("unit_of_analysis") != "observed_action"
        or training.get("game_level_split") is not True
        or training.get("actor_outcome_used") is not False
        or training.get("local_outcome_used") is not False
    ):
        raise BehaviorPriorError("artifact training semantics drifted")
    if (
        training.get("record_count") != split_record_counts["train"]
        or training.get("game_count") != split_game_counts["train"]
    ):
        raise BehaviorPriorError("artifact training counts do not match the train split")
    actor_counts = training.get("actor_side_record_counts")
    if not isinstance(actor_counts, Mapping) or any(
        key not in {"local", "opponent"} for key in actor_counts
    ):
        raise BehaviorPriorError("artifact training actor-side counts are invalid")
    normalized_actor_counts = {
        str(key): _positive_integer(count, f"artifact.training.actor_side.{key}")
        for key, count in actor_counts.items()
    }
    if sum(normalized_actor_counts.values()) != split_record_counts["train"]:
        raise BehaviorPriorError("artifact training actor-side counts are inconsistent")
    action_counts = training.get("action_kind_record_counts")
    if not isinstance(action_counts, Mapping) or any(
        key not in ACTION_KINDS for key in action_counts
    ):
        raise BehaviorPriorError("artifact training action-kind counts are invalid")
    normalized_action_counts = {
        str(key): _positive_integer(count, f"artifact.training.action_kind.{key}")
        for key, count in action_counts.items()
    }
    if sum(normalized_action_counts.values()) != split_record_counts["train"]:
        raise BehaviorPriorError("artifact training action-kind counts are inconsistent")
    supported_modes = _validate_string_list(
        training.get("supported_modes"), "artifact.training.supported_modes"
    )
    supported_patches = _validate_string_list(
        training.get("supported_patches"), "artifact.training.supported_patches"
    )
    if (
        action_kind_model["counts_by_level"]["global"]["[]"]["total"]
        != split_record_counts["train"]
    ):
        raise BehaviorPriorError("artifact action-kind model did not use only train records")
    for kind in TEMPLATE_ACTION_KINDS:
        template_total = normalized_templates[kind]["counts_by_level"]["global"][
            "[]"
        ]["total"]
        if template_total != normalized_action_counts.get(kind, 0):
            raise BehaviorPriorError(
                f"artifact {kind} template model count does not match training"
            )
    evaluation = value.get("evaluation")
    if not isinstance(evaluation, Mapping) or set(evaluation) != {"validation", "test"}:
        raise BehaviorPriorError("artifact evaluation fields do not match the contract")
    normalized_validation = _validate_evaluation_split(
        evaluation["validation"],
        "artifact.evaluation.validation",
        expected_records=split_record_counts["validation"],
        expected_games=split_game_counts["validation"],
    )
    normalized_test = _validate_evaluation_split(
        evaluation["test"],
        "artifact.evaluation.test",
        expected_records=split_record_counts["test"],
        expected_games=split_game_counts["test"],
    )
    actual_quality_values: dict[str, int | float] = {
        "train_game_count": split_game_counts["train"],
        "validation_game_count": split_game_counts["validation"],
        "test_game_count": split_game_counts["test"],
        "train_record_count": split_record_counts["train"],
        "validation_record_count": split_record_counts["validation"],
        "test_record_count": split_record_counts["test"],
        "validation_seen_template_record_count": int(
            normalized_validation["seen_template_record_count"]
        ),
        "validation_kind_log_loss_excess": float(
            normalized_validation.get("kind_log_loss_excess", 1_000_000.0)
        ),
        "validation_seen_template_log_loss_excess": float(
            normalized_validation.get(
                "seen_template_log_loss_excess", 1_000_000.0
            )
        ),
        "validation_unseen_template_rate": float(
            normalized_validation["unseen_template_rate"]
        ),
    }
    quality_checks = value.get("quality_checks")
    if not isinstance(quality_checks, list) or len(quality_checks) != len(
        _QUALITY_CHECK_SPECS
    ):
        raise BehaviorPriorError("artifact quality checks are incomplete")
    normalized_checks: list[dict[str, Any]] = []
    for raw_check, (name, operator, policy_key) in zip(
        quality_checks, _QUALITY_CHECK_SPECS
    ):
        if not isinstance(raw_check, Mapping) or set(raw_check) != {
            "name",
            "actual",
            "operator",
            "expected",
            "passed",
        }:
            raise BehaviorPriorError("artifact quality check fields are invalid")
        actual = actual_quality_values[name]
        expected = expected_policy[policy_key]
        raw_actual = raw_check.get("actual")
        raw_expected = raw_check.get("expected")
        if (
            isinstance(raw_actual, bool)
            or not isinstance(raw_actual, (int, float))
            or not math.isfinite(float(raw_actual))
            or isinstance(raw_expected, bool)
            or not isinstance(raw_expected, (int, float))
            or not math.isfinite(float(raw_expected))
        ):
            raise BehaviorPriorError(
                f"artifact quality check contains a non-finite number: {name}"
            )
        if (
            raw_check.get("name") != name
            or raw_check.get("operator") != operator
            or isinstance(raw_check.get("passed"), bool) is False
            or not math.isclose(
                float(raw_actual),
                float(actual),
                rel_tol=0.0,
                abs_tol=1e-12,
            )
            or not math.isclose(
                float(raw_expected),
                float(expected),
                rel_tol=0.0,
                abs_tol=1e-12,
            )
        ):
            raise BehaviorPriorError(f"artifact quality check drifted: {name}")
        passed = actual >= expected if operator == ">=" else actual <= expected
        if raw_check["passed"] is not passed:
            raise BehaviorPriorError(f"artifact quality result drifted: {name}")
        normalized_checks.append(_check(name, actual, operator, expected))
    computed_ready = all(check["passed"] for check in normalized_checks)
    if value.get("search_ordering_prior_ready") is not computed_ready:
        raise BehaviorPriorError("artifact readiness contradicts its quality checks")
    if value.get("approved_uses") != [
        "offline_behavior_cloning_baseline",
        "opponent_behavior_modeling",
        "legal_action_search_ordering_prior",
    ]:
        raise BehaviorPriorError("artifact approved uses drifted")
    if value.get("prohibited_uses") != [
        "action_generation",
        "direct_live_policy",
        "direct_rl_trajectory",
        "optimal_action_ground_truth",
        "hidden_opponent_card_reconstruction",
    ]:
        raise BehaviorPriorError("artifact prohibited uses drifted")
    caveat = value.get("caveat")
    if not isinstance(caveat, str) or "不等于最优动作" not in caveat:
        raise BehaviorPriorError("artifact caveat is missing")
    normalized = copy.deepcopy(dict(value))
    normalized["source_dataset"] = {
        "name": source_name,
        "sha256": source_sha256,
        "bytes": source_bytes,
        "record_count": source_record_count,
        "game_count": source_game_count,
        "split_record_counts": split_record_counts,
        "split_game_counts": split_game_counts,
    }
    normalized["policy"] = expected_policy
    normalized["training"] = {
        **copy.deepcopy(dict(training)),
        "actor_side_record_counts": normalized_actor_counts,
        "action_kind_record_counts": normalized_action_counts,
        "supported_modes": supported_modes,
        "supported_patches": supported_patches,
    }
    normalized["evaluation"] = {
        "validation": normalized_validation,
        "test": normalized_test,
    }
    normalized["quality_checks"] = normalized_checks
    normalized["models"] = {
        "action_kind": action_kind_model,
        "action_template_by_kind": normalized_templates,
    }
    if b"anon-" in _canonical_json(normalized):
        raise BehaviorPriorError("behavior prior artifact leaked an anonymous game ID")
    return normalized


def load_behavior_prior_policy_from_mapping(
    value: Mapping[str, Any],
) -> dict[str, float | int]:
    result: dict[str, float | int] = {}
    for key in sorted(DEFAULT_BEHAVIOR_PRIOR_POLICY):
        raw = value[key]
        if key in _INTEGER_POLICY_KEYS:
            result[key] = _positive_integer(raw, f"policy.{key}")
        else:
            number = _finite_number(raw, f"policy.{key}")
            if key == "max_validation_unseen_template_rate" and number > 1:
                raise BehaviorPriorError(f"policy.{key} must be between 0 and 1")
            result[key] = number
    return result


def train_behavior_prior_file(
    dataset_path: str | Path,
    manifest_path: str | Path,
    output_path: str | Path,
    *,
    policy_path: str | Path | None = None,
) -> dict[str, Any]:
    output = Path(output_path)
    identity_paths = [
        Path(dataset_path).resolve(),
        Path(manifest_path).resolve(),
        output.resolve(),
    ]
    if policy_path is not None:
        identity_paths.append(Path(policy_path).resolve())
    if len(set(identity_paths)) != len(identity_paths):
        raise BehaviorPriorError(
            "dataset, manifest, policy, and prior output must differ"
        )
    records, identity = load_and_validate_behavior_imitation(
        dataset_path, manifest_path
    )
    policy = load_behavior_prior_policy(policy_path)
    split_records = {
        split: [record for record in records if record["split"] == split]
        for split in ("train", "validation", "test")
    }
    train_records = split_records["train"]
    if not train_records:
        raise BehaviorPriorError("imitation dataset contains no train split")
    action_kind_model = _build_count_model(
        train_records,
        lambda record: str(record["action"]["kind"]),
        labels=ACTION_KINDS,
    )
    template_models = {
        kind: _build_count_model(
            [record for record in train_records if record["action"]["kind"] == kind],
            _template_label,
            other_label=OTHER_TEMPLATE,
        )
        for kind in TEMPLATE_ACTION_KINDS
    }
    models = {
        "action_kind": action_kind_model,
        "action_template_by_kind": template_models,
    }
    validation = _evaluate_split(split_records["validation"], models)
    test = _evaluate_split(split_records["test"], models)
    split_record_counts = _split_counts(records, "record")
    split_game_counts = _split_counts(records, "game")
    checks = [
        _check(
            "train_game_count",
            split_game_counts["train"],
            ">=",
            int(policy["min_train_games"]),
        ),
        _check(
            "validation_game_count",
            split_game_counts["validation"],
            ">=",
            int(policy["min_validation_games"]),
        ),
        _check(
            "test_game_count",
            split_game_counts["test"],
            ">=",
            int(policy["min_test_games"]),
        ),
        _check(
            "train_record_count",
            split_record_counts["train"],
            ">=",
            int(policy["min_train_records"]),
        ),
        _check(
            "validation_record_count",
            split_record_counts["validation"],
            ">=",
            int(policy["min_validation_records"]),
        ),
        _check(
            "test_record_count",
            split_record_counts["test"],
            ">=",
            int(policy["min_test_records"]),
        ),
        _check(
            "validation_seen_template_record_count",
            int(validation["seen_template_record_count"]),
            ">=",
            int(policy["min_validation_seen_template_records"]),
        ),
        _check(
            "validation_kind_log_loss_excess",
            float(validation.get("kind_log_loss_excess", 1_000_000.0)),
            "<=",
            float(policy["max_validation_kind_log_loss_excess"]),
        ),
        _check(
            "validation_seen_template_log_loss_excess",
            float(
                validation.get("seen_template_log_loss_excess", 1_000_000.0)
            ),
            "<=",
            float(policy["max_validation_seen_template_log_loss_excess"]),
        ),
        _check(
            "validation_unseen_template_rate",
            float(validation["unseen_template_rate"]),
            "<=",
            float(policy["max_validation_unseen_template_rate"]),
        ),
    ]
    ready = all(check["passed"] for check in checks)
    dataset_identity = identity["manifest"]["imitation_dataset"]
    artifact: dict[str, Any] = {
        "schema": BEHAVIOR_PRIOR_SCHEMA_ID,
        "model_type": BEHAVIOR_PRIOR_MODEL_ID,
        "source_dataset": {
            "name": Path(dataset_path).name,
            "sha256": identity["dataset_sha256"],
            "bytes": len(identity["dataset_bytes"]),
            "record_count": dataset_identity["record_count"],
            "game_count": dataset_identity["game_count"],
            "split_record_counts": split_record_counts,
            "split_game_counts": split_game_counts,
        },
        "source_manifest": {
            "name": Path(manifest_path).name,
            "sha256": identity["manifest_sha256"],
            "schema": BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID,
        },
        "policy": policy,
        "policy_sha256": behavior_prior_policy_sha256(policy),
        "training": {
            "split": "train",
            "record_count": len(train_records),
            "game_count": len({str(record["game_id"]) for record in train_records}),
            "actor_side_record_counts": dict(
                sorted(
                    Counter(str(record["actor_side"]) for record in train_records).items()
                )
            ),
            "action_kind_record_counts": dict(
                sorted(
                    Counter(
                        str(record["action"]["kind"]) for record in train_records
                    ).items()
                )
            ),
            "supported_modes": sorted(
                {_mode(record["pre_state"].get("mode")) for record in train_records}
            ),
            "supported_patches": sorted(
                {str(record["pre_state"].get("patch") or "unknown") for record in train_records}
            ),
            "unit_of_analysis": "observed_action",
            "game_level_split": True,
            "actor_outcome_used": False,
            "local_outcome_used": False,
        },
        "evaluation": {"validation": validation, "test": test},
        "quality_checks": checks,
        "imitation_training_complete": True,
        "search_ordering_prior_ready": ready,
        "live_policy_eligible": False,
        "rl_training_eligible": False,
        "optimality_verified": False,
        "candidate_generation_allowed": False,
        "outcome_used_for_training": False,
        "models": models,
        "approved_uses": [
            "offline_behavior_cloning_baseline",
            "opponent_behavior_modeling",
            "legal_action_search_ordering_prior",
        ],
        "prohibited_uses": [
            "action_generation",
            "direct_live_policy",
            "direct_rl_trajectory",
            "optimal_action_ground_truth",
            "hidden_opponent_card_reconstruction",
        ],
        "caveat": (
            "该产物只学习双方在公开局面中实际做过什么，并只允许给外部已确认合法的动作排序；"
            "观察行为不等于最优动作，验证/测试指标也不证明对局收益或全局最优性。"
        ),
    }
    validated = validate_behavior_prior_artifact(artifact)
    payload = json.dumps(validated, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    reparsed = _strict_json_object(payload, "behavior prior output")
    validate_behavior_prior_artifact(reparsed)
    _atomic_write_bytes(payload, output)
    return validated


def load_behavior_prior(path: str | Path, *, require_ready: bool = True) -> dict[str, Any]:
    _, payload = _read_input(path, "behavior prior artifact")
    artifact = validate_behavior_prior_artifact(
        _strict_json_object(payload, "behavior prior artifact")
    )
    if require_ready and artifact["search_ordering_prior_ready"] is not True:
        raise BehaviorPriorError("behavior prior did not pass its search-ordering gate")
    return artifact


def score_legal_behavior_candidates(
    artifact: Mapping[str, Any],
    *,
    pre_state: Mapping[str, Any],
    actor_side: str,
    actor_player_id: str,
    actions: Sequence[Mapping[str, Any]],
) -> list[float]:
    """Score only caller-supplied legal actions; this function never creates actions."""

    model = validate_behavior_prior_artifact(artifact)
    if model["search_ordering_prior_ready"] is not True:
        raise BehaviorPriorError("behavior prior is not ready for search ordering")
    if not actions:
        return []
    if actor_side not in {"local", "opponent"}:
        raise BehaviorPriorError("candidate actor side must be known")
    if actor_player_id not in {"friendly", "opponent"}:
        raise BehaviorPriorError("candidate actor player is invalid")
    if pre_state.get("active_player_id") != actor_player_id:
        raise BehaviorPriorError("candidate actor is not active in the supplied state")
    expected_side = (
        "local"
        if actor_player_id == pre_state.get("perspective_player_id")
        else "opponent"
    )
    if actor_side != expected_side:
        raise BehaviorPriorError("candidate actor side does not match the state")
    mode = _mode(pre_state.get("mode"))
    patch = str(pre_state.get("patch") or "unknown")
    training = model["training"]
    if mode not in training["supported_modes"] or patch not in training["supported_patches"]:
        return [1.0 / len(actions)] * len(actions)
    base = {
        "pre_state": pre_state,
        "actor_side": actor_side,
        "actor_player_id": actor_player_id,
    }
    contexts = _context_keys(base)
    kind_probabilities = _model_probabilities(model["models"]["action_kind"], contexts)
    raw_scores: list[float] = []
    for index, action in enumerate(actions):
        if not isinstance(action, Mapping):
            raise BehaviorPriorError(f"candidate action {index} must be an object")
        kind = str(action.get("kind") or "")
        if kind not in ACTION_KINDS:
            raise BehaviorPriorError(f"candidate action {index} has an unknown kind")
        score = kind_probabilities[kind]
        if kind != "end_turn":
            candidate_record = {
                **base,
                "action": {
                    "kind": kind,
                    "source_entity_id": str(action.get("source_entity_id") or ""),
                    "target_entity_id": str(action.get("target_entity_id") or ""),
                    "card_id": str(action.get("card_id") or ""),
                    "board_position": action.get("board_position", 0),
                },
            }
            template = _template_label(candidate_record)
            template_model = model["models"]["action_template_by_kind"][kind]
            template_probabilities = _model_probabilities(template_model, contexts)
            label = template if template in template_probabilities else OTHER_TEMPLATE
            score *= template_probabilities[label]
        raw_scores.append(max(1e-12, score))
    total = sum(raw_scores)
    return [score / total for score in raw_scores]
