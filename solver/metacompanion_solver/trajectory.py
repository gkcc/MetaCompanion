from __future__ import annotations

import copy
import hashlib
import json
import os
import re
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Sequence

from .errors import SolverError
from .logging_store import (
    TRAJECTORY_SCHEMA_ID,
    TRAINING_LOG_SCHEMA_ID,
    deterministic_game_split,
)
from .power_evidence import PowerEvidenceError, validate_power_identity_observation
from .schemas import (
    POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
    POWER_ACTION_IDENTITY_CHOICE_STATUS,
    POWER_ACTION_IDENTITY_COMPLETENESS,
    POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
    POWER_ACTION_IDENTITY_STATUS,
    TRANSITION_CANDIDATE_BOUNDARY_STATUSES,
    TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
    TRANSITION_CANDIDATE_COMPLETENESS,
    TRANSITION_CANDIDATE_ENVELOPE_FIELDS,
    TRANSITION_CANDIDATE_STATUS,
    TRANSITION_CANDIDATE_VERIFICATION,
    Action,
    GameState,
    is_unverified_transition_candidate,
    is_power_action_identity_candidate,
)
from .simulator import apply_action


TRAJECTORY_POLICY_SCHEMA_ID = "trajectory-readiness-policy-v1"
TRAJECTORY_REPORT_SCHEMA_ID = "trajectory-readiness-report-v1"
RUNTIME_TRAJECTORY_REPORT_SCHEMA_ID = "runtime-trajectory-readiness-report-v1"
DECISION_SNAPSHOT_CAPTURE_CONTRACT = "offline_power_decision_snapshot_v1"
MAX_INPUT_BYTES = 256 * 1024 * 1024
MAX_LINE_BYTES = 8 * 1024 * 1024
ISSUE_DETAIL_LIMIT = 100

SOURCE_KIND_DIRECT_AUDIT = "direct_audit"
SOURCE_KIND_SYNTHETIC_FIXTURE = "synthetic_fixture"
SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT = "live_runtime_snapshot"
_ALLOWED_SOURCE_KINDS = {
    SOURCE_KIND_DIRECT_AUDIT,
    SOURCE_KIND_SYNTHETIC_FIXTURE,
    SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
}

SOLVE_STATUS_SEMANTICS: dict[str, list[str] | str] = {
    "schema": "solve-status-semantics-v1",
    "policy_buckets": ["ok", "partial", "cancelled", "unsupported", "non_ok"],
    "non_ok_members": ["error", "other"],
    "unsuccessful_members": ["partial", "cancelled", "unsupported", "error", "other"],
}

DEFAULT_READINESS_POLICY: dict[str, float | int] = {
    "min_unique_games": 100,
    "min_canonical_decisions": 1000,
    "min_terminal_result_games": 95,
    "min_solve_result_join_rate": 0.95,
    "min_exact_action_rate": 0.90,
    "min_replayable_transition_rate": 0.95,
    "max_partial_action_rate": 0.10,
    "max_unsupported_solve_rate": 0.25,
    "max_cancelled_solve_rate": 0.10,
    "max_partial_solve_rate": 0.20,
    "max_non_ok_solve_rate": 0.30,
}

_ANONYMOUS_ID = re.compile(r"^anon-[0-9a-f]{16}$")
_EMAIL = re.compile(r"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b")
_BATTLE_TAG = re.compile(r"\b[^\s#]{2,32}#[0-9]{4,10}\b")
_BEARER = re.compile(r"(?i)\bBearer\s+[A-Za-z0-9._-]{16,}")
_JWT = re.compile(r"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")
_COOKIE = re.compile(r"(?i)\b(?:sessionid|csrftoken|cf_clearance|auth_token|access_token|refresh_token)\s*=\s*[^\s;]{8,}")
_FORBIDDEN_KEYS = {
    "account_id",
    "accountid",
    "battle_tag",
    "battletag",
    "player_name",
    "opponent_name",
    "email",
    "password",
    "cookie",
    "authorization",
    "session_token",
    "access_token",
    "refresh_token",
    "logged_at_utc",
    "observed_at_utc",
    "captured_at_utc",
    "current_deck",
    "deck_id",
    "deckid",
    "hearthstone_deck_id",
    "deck_name",
    "deckname",
}
_HIDDEN_IDENTITY_KEYS = {
    "card_id",
    "name",
    "card_text",
    "english_text",
    "dbf_id",
    "attack",
    "health",
    "current_health",
    "cost",
}
_EXACT_RESOLUTIONS = {"exact_entity_id", "not_applicable"}
_ALLOWED_SOLVE_STAGES = {"initial", "final", "single"}
_DERIVED_ENTITY_STATE_KEYS = {
    "effect_coverage",
    "effects",
    "rule_id",
    "rule_text_sha256",
    "rule_version",
    "unsupported_effects",
}


class TrajectoryAuditError(ValueError):
    pass


@dataclass(frozen=True)
class _Record:
    line: int
    value: Mapping[str, Any]


@dataclass(frozen=True)
class _Solve:
    line: int
    game_id: str
    decision_id: str
    state_id: str
    stage: str
    request: Mapping[str, Any]
    status: str


@dataclass(frozen=True)
class _DecisionSnapshot:
    line: int
    game_id: str
    decision_id: str
    state_id: str
    state: Mapping[str, Any]
    normalized_state_hash: str


@dataclass(frozen=True)
class _Observation:
    line: int
    game_id: str
    decision_id: str
    kind: str
    value: Mapping[str, Any]
    envelope: Mapping[str, Any]
    state_id: str


def _mapping(value: Any) -> Mapping[str, Any]:
    return value if isinstance(value, Mapping) else {}


def _text(value: Any) -> str:
    return value.strip() if isinstance(value, str) else ""


def _truthy(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        return value == 1
    if isinstance(value, str):
        return value.strip().lower() in {"1", "true", "yes"}
    return False


def _rate(numerator: int, denominator: int, *, empty: float = 0.0) -> float:
    return round(numerator / denominator, 6) if denominator else empty


def _canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _sha256_json(value: Any) -> str:
    return hashlib.sha256(_canonical_json(value).encode("utf-8")).hexdigest()


def trajectory_policy_sha256(policy: Mapping[str, float | int]) -> str:
    """Fingerprint the effective policy, including production defaults.

    Hashing the normalized effective policy keeps reports reproducible even when no
    policy file was supplied and protects against a sparse override silently inheriting
    different defaults in a later auditor build.
    """

    return _sha256_json(
        {
            "schema": TRAJECTORY_POLICY_SCHEMA_ID,
            "thresholds": dict(sorted(policy.items())),
        }
    )


def _state_identity_projection(value: Mapping[str, Any]) -> Mapping[str, Any]:
    """Return the producer state used to bind one state_id across log records.

    Snapshot sequence is capture ordering evidence and is audited separately.  Structured
    rule annotations are deterministic solver enrichments: solve records contain them after
    rule matching while C# transition candidates contain the same raw HDT entity before that
    matching pass.  Neither class of field belongs to the producer state identity.
    """

    def project(item: Any, *, root: bool = False) -> Any:
        if isinstance(item, Mapping):
            result: dict[str, Any] = {}
            for key, nested in item.items():
                name = str(key)
                if name in _DERIVED_ENTITY_STATE_KEYS:
                    continue
                projected = project(nested)
                if root and name == "metadata" and isinstance(projected, dict):
                    projected.pop("snapshot_sequence", None)
                result[name] = projected
            return result
        if isinstance(item, Sequence) and not isinstance(item, (str, bytes, bytearray)):
            return [project(nested) for nested in item]
        return item

    return project(value, root=True)


def _state_identity_hash(value: Mapping[str, Any]) -> str:
    return _sha256_json(_state_identity_projection(value))


def load_trajectory_policy(path: str | Path | None) -> dict[str, float | int]:
    policy = dict(DEFAULT_READINESS_POLICY)
    if path is None:
        return policy
    source = Path(path)
    try:
        raw = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise TrajectoryAuditError("invalid trajectory readiness policy") from exc
    if not isinstance(raw, Mapping):
        raise TrajectoryAuditError("trajectory policy root must be an object")
    if raw.get("schema") != TRAJECTORY_POLICY_SCHEMA_ID:
        raise TrajectoryAuditError(
            f"trajectory policy schema must be {TRAJECTORY_POLICY_SCHEMA_ID!r}"
        )
    thresholds = raw.get("thresholds")
    if not isinstance(thresholds, Mapping):
        raise TrajectoryAuditError("trajectory policy thresholds must be an object")
    unknown = sorted(set(thresholds) - set(policy))
    if unknown:
        raise TrajectoryAuditError(f"unknown trajectory thresholds: {', '.join(unknown)}")
    for key, value in thresholds.items():
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0:
            raise TrajectoryAuditError(f"trajectory threshold {key!r} must be non-negative")
        if key.startswith(("min_", "max_")) and key.endswith("_rate") and value > 1:
            raise TrajectoryAuditError(f"trajectory rate threshold {key!r} must be at most 1")
        policy[key] = value
    return policy


def _load_records(
    path: str | Path,
) -> tuple[list[_Record], list[dict[str, Any]], str, int]:
    source = Path(path)
    try:
        payload = source.read_bytes()
    except OSError as exc:
        raise TrajectoryAuditError(f"trajectory input was not found: {source}") from exc
    if len(payload) > MAX_INPUT_BYTES:
        raise TrajectoryAuditError("trajectory input exceeds the 256 MiB audit limit")
    input_sha256 = hashlib.sha256(payload).hexdigest()
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError as exc:
        raise TrajectoryAuditError("trajectory input could not be read as UTF-8 JSONL") from exc
    records: list[_Record] = []
    issues: list[dict[str, Any]] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        if not line.strip():
            continue
        if len(line.encode("utf-8")) > MAX_LINE_BYTES:
            issues.append({"line": line_number, "reason": "line_exceeds_8_mib"})
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            issues.append({"line": line_number, "reason": "invalid_json"})
            continue
        if not isinstance(value, Mapping):
            issues.append({"line": line_number, "reason": "record_not_object"})
            continue
        records.append(_Record(line_number, value))
    return records, issues, input_sha256, len(payload)


def _in_hidden_opponent_zone(path: str) -> bool:
    normalized = path.lower().replace("[", ".").replace("]", "")
    return any(
        marker in normalized
        for marker in (
            ".opponent.hand.",
            ".opponent.deck.",
            ".opponent.secrets.",
            ".opponent.set_aside.",
        )
    )


def _privacy_issues(value: Any, line: int, path: str = "record") -> list[dict[str, Any]]:
    issues: list[dict[str, Any]] = []
    if isinstance(value, Mapping):
        for key, item in value.items():
            name = str(key)
            normalized = name.lower()
            child_path = f"{path}.{name}"
            if normalized in _FORBIDDEN_KEYS and item not in (None, "", [], {}):
                issues.append(
                    {"line": line, "path": child_path, "reason": "forbidden_identity_key"}
                )
            if normalized in {"game_id", "match_id"} and item not in (None, ""):
                if not isinstance(item, str) or not _ANONYMOUS_ID.fullmatch(item):
                    issues.append(
                        {"line": line, "path": child_path, "reason": "raw_game_identifier"}
                    )
            if _in_hidden_opponent_zone(child_path) and normalized in _HIDDEN_IDENTITY_KEYS:
                if item not in (None, "", 0, False):
                    issues.append(
                        {"line": line, "path": child_path, "reason": "hidden_card_identity"}
                    )
            issues.extend(_privacy_issues(item, line, child_path))
        return issues
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes, bytearray)):
        for index, item in enumerate(value):
            issues.extend(_privacy_issues(item, line, f"{path}[{index}]"))
        return issues
    if isinstance(value, str):
        reason = ""
        if _EMAIL.search(value):
            reason = "email_like_value"
        elif _BATTLE_TAG.search(value):
            reason = "battle_tag_like_value"
        elif _BEARER.search(value) or _JWT.search(value) or _COOKIE.search(value):
            reason = "credential_like_value"
        if reason:
            issues.append({"line": line, "path": path, "reason": reason})
    return issues


def _trajectory_envelope(record: Mapping[str, Any]) -> Mapping[str, Any]:
    return _mapping(record.get("trajectory"))


def _solve_from_record(record: _Record) -> tuple[_Solve | None, list[str]]:
    root = record.value
    issues: list[str] = []
    request = _mapping(root.get("request"))
    state = _mapping(request.get("state"))
    result = _mapping(root.get("result"))
    request_metadata = _mapping(request.get("metadata"))
    state_metadata = _mapping(state.get("metadata"))
    envelope = _trajectory_envelope(root)
    if root.get("log_schema") != TRAINING_LOG_SCHEMA_ID:
        issues.append("missing_or_wrong_log_schema")
    if envelope.get("schema") != TRAJECTORY_SCHEMA_ID:
        issues.append("missing_or_wrong_trajectory_schema")
    envelope_game_id = _text(envelope.get("game_id"))
    state_game_id = _text(state_metadata.get("game_id"))
    game_id = envelope_game_id or state_game_id
    decision_id = (
        _text(envelope.get("decision_id"))
        or _text(request_metadata.get("decision_id"))
        or _text(state.get("state_id"))
    )
    state_id = _text(state.get("state_id"))
    stage = (
        _text(envelope.get("solve_stage"))
        or _text(request_metadata.get("solve_stage"))
    ).lower()
    if not game_id or not _ANONYMOUS_ID.fullmatch(game_id):
        issues.append("missing_or_non_anonymous_game_id")
    if not state_game_id or state_game_id != envelope_game_id:
        issues.append("solve_game_id_envelope_mismatch")
    if _text(request_metadata.get("trajectory_schema")) != TRAJECTORY_SCHEMA_ID:
        issues.append("missing_or_wrong_request_trajectory_schema")
    if not decision_id:
        issues.append("missing_decision_id")
    if not state_id or _text(envelope.get("state_id")) != state_id:
        issues.append("state_id_envelope_mismatch")
    request_decision_id = _text(request_metadata.get("decision_id"))
    if request_decision_id and request_decision_id != decision_id:
        issues.append("decision_id_envelope_mismatch")
    if stage not in _ALLOWED_SOLVE_STAGES:
        issues.append("invalid_solve_stage")
    request_stage = _text(request_metadata.get("solve_stage")).lower()
    if request_stage and request_stage != stage:
        issues.append("solve_stage_envelope_mismatch")
    request_capture = _text(request_metadata.get("capture_contract"))
    if not request_capture or request_capture != _text(envelope.get("capture_contract")):
        issues.append("solve_capture_contract_envelope_mismatch")
    if _text(envelope.get("patch")) != _text(state.get("patch")):
        issues.append("solve_patch_envelope_mismatch")
    if _text(envelope.get("mode")) != _text(state.get("mode")):
        issues.append("solve_mode_envelope_mismatch")
    normalized_state_hash = _text(envelope.get("normalized_state_hash"))
    if normalized_state_hash and normalized_state_hash != _sha256_json(state):
        issues.append("normalized_state_hash_mismatch")
    try:
        GameState.from_dict(state)
    except (SolverError, TypeError, ValueError):
        issues.append("invalid_solve_state")
    if issues:
        return None, issues
    return (
        _Solve(
            line=record.line,
            game_id=game_id,
            decision_id=decision_id,
            state_id=state_id,
            stage=stage,
            request=request,
            status=_text(result.get("status")).lower(),
        ),
        [],
    )


def _decision_snapshot_from_record(
    record: _Record,
) -> tuple[_DecisionSnapshot | None, list[str]]:
    root = record.value
    issues: list[str] = []
    allowed_root = {"kind", "log_schema", "trajectory", "state", "provenance"}
    if set(root) - allowed_root:
        issues.append("decision_snapshot_unknown_root_field")
    envelope = _trajectory_envelope(root)
    state = _mapping(root.get("state"))
    state_metadata = _mapping(state.get("metadata"))
    provenance = _mapping(root.get("provenance"))
    allowed_envelope = {
        "schema",
        "game_id",
        "split",
        "decision_id",
        "state_id",
        "capture_contract",
        "normalized_state_hash",
    }
    allowed_provenance = {
        "source_capture_contract",
        "source_action_sequence",
        "state_role",
        "simulator_verification",
        "source_observation_sha256",
    }
    if set(envelope) - allowed_envelope:
        issues.append("decision_snapshot_unknown_trajectory_field")
    if set(provenance) - allowed_provenance:
        issues.append("decision_snapshot_unknown_provenance_field")
    if root.get("log_schema") != TRAINING_LOG_SCHEMA_ID:
        issues.append("missing_or_wrong_log_schema")
    if envelope.get("schema") != TRAJECTORY_SCHEMA_ID:
        issues.append("missing_or_wrong_trajectory_schema")
    game_id = _text(envelope.get("game_id"))
    state_game_id = _text(state_metadata.get("game_id"))
    decision_id = _text(envelope.get("decision_id"))
    state_id = _text(state.get("state_id"))
    if not game_id or not _ANONYMOUS_ID.fullmatch(game_id):
        issues.append("missing_or_non_anonymous_game_id")
    if state_game_id != game_id:
        issues.append("decision_snapshot_game_id_mismatch")
    if not decision_id or decision_id != state_id:
        issues.append("decision_snapshot_id_mismatch")
    if _text(envelope.get("state_id")) != state_id:
        issues.append("decision_snapshot_state_envelope_mismatch")
    if _text(envelope.get("capture_contract")) != DECISION_SNAPSHOT_CAPTURE_CONTRACT:
        issues.append("invalid_decision_snapshot_capture_contract")
    normalized_state_hash = _text(envelope.get("normalized_state_hash"))
    if normalized_state_hash != _sha256_json(state):
        issues.append("decision_snapshot_hash_mismatch")
    if _text(provenance.get("source_capture_contract")) != POWER_ACTION_IDENTITY_CAPTURE_CONTRACT:
        issues.append("decision_snapshot_source_contract_mismatch")
    if _metadata_integer(provenance.get("source_action_sequence"), minimum=1) is None:
        issues.append("decision_snapshot_action_sequence_invalid")
    if _text(provenance.get("state_role")) not in {"pre_action", "post_action"}:
        issues.append("decision_snapshot_state_role_invalid")
    if _text(provenance.get("simulator_verification")) != "offline_simulator_verified_v1":
        issues.append("decision_snapshot_simulator_verification_invalid")
    source_digest = _text(provenance.get("source_observation_sha256"))
    if len(source_digest) != 64 or any(
        character not in "0123456789abcdef" for character in source_digest
    ):
        issues.append("decision_snapshot_source_digest_invalid")
    try:
        GameState.from_dict(state)
    except (SolverError, TypeError, ValueError):
        issues.append("invalid_decision_snapshot_state")
    if issues:
        return None, issues
    return (
        _DecisionSnapshot(
            line=record.line,
            game_id=game_id,
            decision_id=decision_id,
            state_id=state_id,
            state=state,
            normalized_state_hash=normalized_state_hash,
        ),
        [],
    )


def _metadata_integer(value: Any, *, minimum: int = 0) -> int | None:
    if isinstance(value, bool):
        return None
    try:
        parsed = int(str(value))
    except (TypeError, ValueError):
        return None
    if str(value).strip() != str(parsed) or parsed < minimum:
        return None
    return parsed


def _explicit_false(value: Any) -> bool:
    if value is False or (isinstance(value, int) and not isinstance(value, bool) and value == 0):
        return True
    return isinstance(value, str) and value.strip().lower() in {"false", "0", "no"}


def _candidate_transition_issues(
    observation: Mapping[str, Any],
    metadata: Mapping[str, Any],
    envelope: Mapping[str, Any],
    state_id: str,
) -> list[str]:
    if not is_unverified_transition_candidate(metadata):
        return []
    issues: list[str] = []
    power_identity = is_power_action_identity_candidate(metadata)
    expected = {
        "capture_contract": (
            POWER_ACTION_IDENTITY_CAPTURE_CONTRACT
            if power_identity
            else TRANSITION_CANDIDATE_CAPTURE_CONTRACT
        ),
        "transition_status": TRANSITION_CANDIDATE_STATUS,
        "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
        "completeness": (
            POWER_ACTION_IDENTITY_COMPLETENESS
            if power_identity
            else TRANSITION_CANDIDATE_COMPLETENESS
        ),
    }
    if power_identity:
        expected.update(
            {
                "action_identity_status": POWER_ACTION_IDENTITY_STATUS,
                "choice_status": POWER_ACTION_IDENTITY_CHOICE_STATUS,
                "simulator_status": POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
            }
        )
    for field, required in expected.items():
        if _text(metadata.get(field)).lower() != required:
            issues.append(f"invalid_candidate_{field}")
    if "training_eligible" not in metadata or not _explicit_false(
        metadata.get("training_eligible")
    ):
        issues.append("candidate_transition_marked_training_eligible")

    pre_state_id = _text(metadata.get("pre_state_id"))
    post_state_id = _text(metadata.get("post_state_id"))
    if not pre_state_id or pre_state_id != state_id:
        issues.append("candidate_pre_state_id_mismatch")
    if not post_state_id:
        issues.append("candidate_missing_post_state_id")
    elif post_state_id == pre_state_id:
        issues.append("candidate_pre_post_state_id_reuse")
    for field in (
        "raw_pre_snapshot_hash",
        "raw_post_snapshot_hash",
        "pre_state_hash",
        "post_state_hash",
    ):
        digest = _text(metadata.get(field))
        if len(digest) != 64 or any(character not in "0123456789abcdef" for character in digest):
            issues.append(f"invalid_candidate_{field}")

    for field, expected_state_id in (
        ("pre_state", pre_state_id),
        ("post_state", post_state_id),
    ):
        state = _mapping(observation.get(field))
        if not state:
            issues.append(f"candidate_missing_{field}")
            continue
        if _text(state.get("state_id")) != expected_state_id:
            issues.append(f"candidate_{field}_id_mismatch")
        try:
            GameState.from_dict(state)
        except (SolverError, TypeError, ValueError):
            issues.append(f"candidate_invalid_{field}")
        expected_hash = _text(
            metadata.get("pre_state_hash" if field == "pre_state" else "post_state_hash")
        )
        if expected_hash and _sha256_json(state) != expected_hash:
            issues.append(f"candidate_{field}_hash_mismatch")

    if _metadata_integer(metadata.get("action_sequence"), minimum=1) is None:
        issues.append("invalid_candidate_action_sequence")
    pre_sequence = _metadata_integer(metadata.get("pre_snapshot_sequence"), minimum=1)
    post_sequence = _metadata_integer(metadata.get("post_snapshot_sequence"), minimum=1)
    if pre_sequence is None:
        issues.append("invalid_candidate_pre_snapshot_sequence")
    if post_sequence is None:
        issues.append("invalid_candidate_post_snapshot_sequence")
    if pre_sequence is not None and post_sequence is not None and post_sequence <= pre_sequence:
        issues.append("candidate_snapshot_sequence_not_increasing")
    intervening = _metadata_integer(metadata.get("intervening_action_count"), minimum=0)
    if intervening is None:
        issues.append("invalid_candidate_intervening_action_count")
    if _metadata_integer(metadata.get("capture_warning_count"), minimum=0) is None:
        issues.append("invalid_candidate_capture_warning_count")
    boundary = _text(metadata.get("boundary_status")).lower()
    if boundary not in TRANSITION_CANDIDATE_BOUNDARY_STATUSES:
        issues.append("invalid_candidate_boundary_status")
    elif boundary == "isolated" and intervening not in (None, 0):
        issues.append("isolated_candidate_has_intervening_action")

    for field in TRANSITION_CANDIDATE_ENVELOPE_FIELDS:
        if str(envelope.get(field, "")) != str(metadata.get(field, "")):
            issues.append(f"candidate_{field}_envelope_mismatch")
    if power_identity:
        try:
            validate_power_identity_observation(observation)
        except PowerEvidenceError as exc:
            issues.append(exc.code)
    return issues


def _observation_from_record(record: _Record) -> tuple[_Observation | None, list[str]]:
    root = record.value
    issues: list[str] = []
    observation = _mapping(root.get("observation"))
    metadata = _mapping(observation.get("metadata"))
    envelope = _trajectory_envelope(root)
    if root.get("log_schema") != TRAINING_LOG_SCHEMA_ID:
        issues.append("missing_or_wrong_log_schema")
    if envelope.get("schema") != TRAJECTORY_SCHEMA_ID:
        issues.append("missing_or_wrong_trajectory_schema")
    envelope_game_id = _text(envelope.get("game_id"))
    observation_game_id = _text(observation.get("game_id"))
    game_id = envelope_game_id or observation_game_id
    decision_id = (
        _text(envelope.get("decision_id"))
        or _text(metadata.get("decision_id"))
        or _text(metadata.get("pre_state_id"))
        or _text(observation.get("state_id"))
    )
    kind = _text(observation.get("kind")).lower()
    state_id = _text(observation.get("state_id"))
    if not game_id or not _ANONYMOUS_ID.fullmatch(game_id):
        issues.append("missing_or_non_anonymous_game_id")
    if not observation_game_id or observation_game_id != envelope_game_id:
        issues.append("observation_game_id_envelope_mismatch")
    if _text(metadata.get("trajectory_schema")) != TRAJECTORY_SCHEMA_ID:
        issues.append("missing_or_wrong_observation_trajectory_schema")
    if kind not in {"action", "result"}:
        issues.append("invalid_observation_kind")
    if _text(envelope.get("observation_kind")).lower() != kind:
        issues.append("observation_kind_envelope_mismatch")
    if not state_id:
        issues.append("missing_observation_state_id")
    if state_id and _text(envelope.get("state_id")) != state_id:
        issues.append("observation_state_id_envelope_mismatch")
    if kind == "result":
        if _text(observation.get("result")).lower() not in {"win", "loss", "tie"}:
            issues.append("invalid_terminal_result")
        if _text(metadata.get("completeness")).lower() != "terminal_result":
            issues.append("invalid_result_completeness")
        if not _truthy(metadata.get("training_eligible")):
            issues.append("result_not_explicitly_eligible")
        if _text(metadata.get("capture_contract")).lower() != "terminal_result_v1":
            issues.append("invalid_result_capture_contract")
        if _text(envelope.get("completeness")).lower() != "terminal_result":
            issues.append("result_completeness_envelope_mismatch")
        if _text(metadata.get("capture_contract")) != _text(
            envelope.get("capture_contract")
        ):
            issues.append("result_capture_contract_envelope_mismatch")
    else:
        if not isinstance(observation.get("action"), Mapping):
            issues.append("missing_action")
        try:
            action_sequence = int(str(metadata.get("action_sequence", "")))
        except ValueError:
            action_sequence = 0
        if action_sequence <= 0:
            issues.append("invalid_action_sequence")
        if str(envelope.get("action_sequence", "")) != str(
            metadata.get("action_sequence", "")
        ):
            issues.append("action_sequence_envelope_mismatch")
        pre_state_id = _text(metadata.get("pre_state_id"))
        if not pre_state_id:
            issues.append("missing_pre_state_id")
        if decision_id and _text(envelope.get("decision_id")) != decision_id:
            issues.append("decision_id_envelope_mismatch")
        metadata_decision_id = _text(metadata.get("decision_id"))
        if metadata_decision_id and metadata_decision_id != decision_id:
            issues.append("observation_decision_id_envelope_mismatch")
        for field in ("completeness", "capture_contract", "transition_status"):
            if _text(metadata.get(field)).lower() != _text(envelope.get(field)).lower():
                issues.append(f"action_{field}_envelope_mismatch")
        issues.extend(
            _candidate_transition_issues(
                observation,
                metadata,
                envelope,
                state_id,
            )
        )
    if issues:
        return None, issues
    return _Observation(
        record.line,
        game_id,
        decision_id,
        kind,
        observation,
        envelope,
        state_id,
    ), []


def _state_projection(state: GameState) -> Mapping[str, Any]:
    value = copy.deepcopy(state.to_dict())
    value["state_id"] = ""
    value["rng_seed"] = 0
    value["metadata"] = {}
    value["belief"] = {}
    return value


def _exact_action_contract(observation: _Observation) -> bool:
    value = observation.value
    metadata = _mapping(value.get("metadata"))
    action = _mapping(value.get("action"))
    kind = _text(action.get("kind") or action.get("type")).lower()
    # Producer-side post-state candidates are evidence only.  Flipping one of
    # their other string flags must never make the raw record exact.
    if is_unverified_transition_candidate(metadata):
        return False
    if _text(metadata.get("completeness")).lower() != "complete_action_trace_v1":
        return False
    if _text(metadata.get("capture_contract")).lower() != TRAJECTORY_SCHEMA_ID:
        return False
    if _text(metadata.get("transition_status")).lower() != "replayable_exact":
        return False
    if not _truthy(metadata.get("training_eligible")):
        return False
    if not _text(metadata.get("pre_state_id")) or not _text(metadata.get("post_state_id")):
        return False
    try:
        sequence = int(str(metadata.get("action_sequence", "")))
    except ValueError:
        return False
    if sequence <= 0 or kind not in {"play_card", "attack", "hero_power", "end_turn"}:
        return False
    source_resolution = _text(metadata.get("source_entity_resolution")).lower()
    target_resolution = _text(metadata.get("target_entity_resolution")).lower()
    if source_resolution not in _EXACT_RESOLUTIONS or target_resolution not in _EXACT_RESOLUTIONS:
        return False
    source = action.get("source_entity_id")
    target = action.get("target_entity_id")
    card_id = _text(action.get("card_id"))
    if observation.state_id != _text(metadata.get("pre_state_id")):
        return False
    if str(observation.envelope.get("action_sequence", "")) != str(
        metadata.get("action_sequence", "")
    ):
        return False
    if kind == "end_turn":
        return (
            source in (None, "")
            and target in (None, "")
            and not card_id
            and source_resolution == "not_applicable"
            and target_resolution == "not_applicable"
        )
    if source in (None, "") or source_resolution != "exact_entity_id" or not card_id:
        return False
    if kind == "attack" and (
        target in (None, "") or target_resolution != "exact_entity_id"
    ):
        return False
    if target_resolution == "exact_entity_id" and target in (None, ""):
        return False
    if target_resolution == "not_applicable" and target not in (None, ""):
        return False
    return True


def _replay_action(
    observation: _Observation,
    states: Mapping[tuple[str, str], Mapping[str, Any]],
) -> tuple[bool, str]:
    metadata = _mapping(observation.value.get("metadata"))
    pre_id = _text(metadata.get("pre_state_id"))
    post_id = _text(metadata.get("post_state_id"))
    pre_raw = states.get((observation.game_id, pre_id))
    post_raw = states.get((observation.game_id, post_id))
    if pre_raw is None or post_raw is None:
        return False, "missing_pre_or_post_state"
    try:
        pre = GameState.from_dict(pre_raw)
        post = GameState.from_dict(post_raw)
        action_raw = dict(_mapping(observation.value.get("action")))
        for key in ("source_entity_id", "target_entity_id"):
            value = action_raw.get(key)
            if value is None:
                # Action.to_dict() deliberately emits JSON null for an absent entity.
                # The wire parser uses an empty string internally, so normalize the
                # logger representation before replaying it.
                action_raw[key] = ""
            elif isinstance(value, int) and not isinstance(value, bool):
                action_raw[key] = str(value)
        action = Action.from_dict(action_raw, "trajectory.action")
        if action.kind.value != "end_turn":
            actor = pre.player(pre.active_player_id)
            actor_entities = [
                actor.hero,
                *actor.hand,
                *actor.board,
                *([actor.hero_power] if actor.hero_power else []),
                *([actor.weapon] if actor.weapon else []),
            ]
            source = next(
                (item for item in actor_entities if item.entity_id == action.source_entity_id),
                None,
            )
            if source is None:
                return False, "source_not_owned_by_active_player"
            if not action.card_id or action.card_id != source.card_id:
                return False, "source_card_id_mismatch"
        outcome = apply_action(pre, action)
    except (SolverError, TypeError, ValueError, RuntimeError) as exc:
        return False, type(exc).__name__
    if outcome.annotations:
        return False, "simulation_annotations"
    if _state_projection(outcome.state) != _state_projection(post):
        return False, "post_state_mismatch"
    return True, ""


def _check(name: str, value: float | int, operator: str, threshold: float | int) -> dict[str, Any]:
    passed = value >= threshold if operator == ">=" else value <= threshold
    return {
        "name": name,
        "value": value,
        "operator": operator,
        "threshold": threshold,
        "passed": bool(passed),
    }


def _solve_status_bucket(status: str) -> str:
    normalized = status.strip().lower()
    if normalized in {"ok", "partial", "cancelled", "unsupported"}:
        return normalized
    if normalized in {"error", "unavailable"}:
        return "error"
    return "other"


def _issue_reason_counts(items: Sequence[Mapping[str, Any]]) -> dict[str, int]:
    counts: Counter[str] = Counter()
    for item in items:
        reasons: list[str] = []
        reason = _text(item.get("reason"))
        if reason:
            reasons.append(reason)
        raw_reasons = item.get("reasons")
        if isinstance(raw_reasons, Sequence) and not isinstance(
            raw_reasons, (str, bytes, bytearray)
        ):
            reasons.extend(
                normalized
                for value in raw_reasons
                if (normalized := _text(value))
            )
        counts.update(reasons or ("unspecified",))
    return dict(sorted(counts.items()))


def _issue_report(
    groups: Mapping[str, Sequence[Mapping[str, Any]]],
) -> dict[str, Any]:
    all_counts: Counter[str] = Counter()
    reason_counts: dict[str, dict[str, int]] = {}
    truncated_counts: dict[str, int] = {}
    report: dict[str, Any] = {}
    for category, items in groups.items():
        category_counts = _issue_reason_counts(items)
        reason_counts[category] = category_counts
        all_counts.update(category_counts)
        report[category] = list(items[:ISSUE_DETAIL_LIMIT])
        truncated_counts[category] = max(0, len(items) - ISSUE_DETAIL_LIMIT)
    report["reason_counts"] = reason_counts
    report["all_reason_counts"] = dict(sorted(all_counts.items()))
    report["detail_limit"] = ISSUE_DETAIL_LIMIT
    report["truncated_counts"] = truncated_counts
    return report


def audit_trajectory_file(
    input_path: str | Path,
    *,
    policy_path: str | Path | None = None,
    source_kind: str = SOURCE_KIND_DIRECT_AUDIT,
) -> dict[str, Any]:
    if source_kind not in _ALLOWED_SOURCE_KINDS:
        raise TrajectoryAuditError(f"unsupported trajectory source kind: {source_kind}")
    records, parse_issues, input_sha256, input_bytes = _load_records(input_path)
    policy = load_trajectory_policy(policy_path)
    policy_sha256 = trajectory_policy_sha256(policy)
    privacy_issues: list[dict[str, Any]] = []
    contract_issues: list[dict[str, Any]] = []
    solves: list[_Solve] = []
    decision_snapshots: list[_DecisionSnapshot] = []
    observations: list[_Observation] = []
    declared_splits: dict[str, set[str]] = defaultdict(set)

    for record in records:
        privacy_issues.extend(_privacy_issues(record.value, record.line))
        envelope = _trajectory_envelope(record.value)
        game_id = _text(envelope.get("game_id"))
        declared_split = _text(envelope.get("split")).lower()
        if declared_split not in {"train", "validation", "test"}:
            contract_issues.append(
                {"line": record.line, "reason": "missing_or_invalid_game_split"}
            )
        elif game_id:
            declared_splits[game_id].add(declared_split)
        if record.value.get("kind") == "solve":
            solve, issues = _solve_from_record(record)
            if solve is not None:
                solves.append(solve)
            for reason in issues:
                contract_issues.append({"line": record.line, "reason": reason})
        elif record.value.get("kind") == "decision_snapshot":
            snapshot, issues = _decision_snapshot_from_record(record)
            if snapshot is not None:
                decision_snapshots.append(snapshot)
            for reason in issues:
                contract_issues.append({"line": record.line, "reason": reason})
        elif record.value.get("kind") == "observation":
            observation, issues = _observation_from_record(record)
            if observation is not None:
                observations.append(observation)
            for reason in issues:
                contract_issues.append({"line": record.line, "reason": reason})
        else:
            contract_issues.append({"line": record.line, "reason": "unsupported_record_kind"})

    solve_groups: dict[tuple[str, str], list[_Solve]] = defaultdict(list)
    for solve in solves:
        solve_groups[(solve.game_id, solve.decision_id)].append(solve)
    canonical: dict[tuple[str, str], _Solve | _DecisionSnapshot] = {}
    duplicate_solve_count = 0
    conflicting_final_solve_count = 0
    superseded_initial_count = 0
    for key, items in solve_groups.items():
        by_stage: dict[str, list[_Solve]] = defaultdict(list)
        for item in items:
            by_stage[item.stage].append(item)
        duplicate_solve_count += sum(max(0, len(stage_items) - 1) for stage_items in by_stage.values())
        terminal = by_stage.get("final", []) + by_stage.get("single", [])
        if len(terminal) > 1:
            conflicting_final_solve_count += 1
            continue
        if terminal:
            canonical[key] = terminal[0]
            superseded_initial_count += len(by_stage.get("initial", []))

    snapshot_groups: dict[tuple[str, str], list[_DecisionSnapshot]] = defaultdict(list)
    for snapshot in decision_snapshots:
        snapshot_groups[(snapshot.game_id, snapshot.decision_id)].append(snapshot)
    duplicate_decision_snapshot_count = 0
    conflicting_decision_snapshot_count = 0
    for key, items in snapshot_groups.items():
        identities = {
            (item.state_id, item.normalized_state_hash)
            for item in items
        }
        if len(items) > 1:
            duplicate_decision_snapshot_count += len(items) - 1
        if len(identities) != 1:
            conflicting_decision_snapshot_count += 1
            canonical.pop(key, None)
            continue
        snapshot = items[0]
        existing = canonical.get(key)
        if existing is not None and existing.state_id != snapshot.state_id:
            conflicting_decision_snapshot_count += 1
            canonical.pop(key, None)
            continue
        canonical[key] = snapshot

    states: dict[tuple[str, str], Mapping[str, Any]] = {}
    state_hashes: dict[tuple[str, str], str] = {}
    state_id_games: dict[str, set[str]] = defaultdict(set)
    state_content_conflict_count = 0
    for solve in solves:
        state = _mapping(solve.request.get("state"))
        digest = _state_identity_hash(state)
        state_key = (solve.game_id, solve.state_id)
        state_id_games[solve.state_id].add(solve.game_id)
        if state_key in state_hashes and state_hashes[state_key] != digest:
            state_content_conflict_count += 1
        else:
            state_hashes[state_key] = digest
            states[state_key] = state
    for snapshot in decision_snapshots:
        state = snapshot.state
        digest = _state_identity_hash(state)
        state_key = (snapshot.game_id, snapshot.state_id)
        state_id_games[snapshot.state_id].add(snapshot.game_id)
        if state_key in state_hashes and state_hashes[state_key] != digest:
            state_content_conflict_count += 1
        else:
            state_hashes[state_key] = digest
            states[state_key] = state
    for observation in observations:
        if observation.kind != "action":
            continue
        for field in ("pre_state", "post_state"):
            state = _mapping(observation.value.get(field))
            state_id = _text(state.get("state_id"))
            if not state_id:
                continue
            digest = _state_identity_hash(state)
            state_key = (observation.game_id, state_id)
            state_id_games[state_id].add(observation.game_id)
            if state_key in state_hashes and state_hashes[state_key] != digest:
                state_content_conflict_count += 1
            else:
                state_hashes[state_key] = digest
                states[state_key] = state
    cross_game_state_id_reuse_count = sum(
        1 for game_ids in state_id_games.values() if len(game_ids) > 1
    )

    result_by_game: dict[str, set[str]] = defaultdict(set)
    result_records_by_game: dict[str, list[_Observation]] = defaultdict(list)
    actions = [item for item in observations if item.kind == "action"]
    results = [item for item in observations if item.kind == "result"]
    for observation in results:
        result_records_by_game[observation.game_id].append(observation)
        result_by_game[observation.game_id].add(
            _text(observation.value.get("result")).lower()
        )
    conflicting_result_game_count = sum(1 for values in result_by_game.values() if len(values) > 1)
    duplicate_result_observation_count = sum(
        max(0, len(items) - 1) for items in result_records_by_game.values()
    )

    exact_actions = [item for item in actions if _exact_action_contract(item)]
    candidate_actions = [
        item
        for item in actions
        if is_unverified_transition_candidate(_mapping(item.value.get("metadata")))
    ]
    exact_action_lines = {item.line for item in exact_actions}
    for observation in actions:
        metadata = _mapping(observation.value.get("metadata"))
        if _truthy(metadata.get("training_eligible")) and observation.line not in exact_action_lines:
            contract_issues.append(
                {
                    "line": observation.line,
                    "reason": "non_exact_action_marked_training_eligible",
                }
            )
    replayable_count = 0
    replay_failures: list[dict[str, Any]] = []
    verified_transitions: list[dict[str, Any]] = []
    for observation in exact_actions:
        replayed, reason = _replay_action(observation, states)
        if replayed:
            replayable_count += 1
            metadata = _mapping(observation.value.get("metadata"))
            pre_state_id = _text(metadata.get("pre_state_id"))
            post_state_id = _text(metadata.get("post_state_id"))
            verified_transitions.append(
                {
                    "game_id": observation.game_id,
                    "action_sequence": int(str(metadata.get("action_sequence"))),
                    "pre_state_id": pre_state_id,
                    "post_state_id": post_state_id,
                    "observation_line": observation.line,
                    "normalized_pre_state_hash": state_hashes[
                        (observation.game_id, pre_state_id)
                    ],
                    "normalized_post_state_hash": state_hashes[
                        (observation.game_id, post_state_id)
                    ],
                }
            )
        else:
            replay_failures.append({"line": observation.line, "reason": reason})

    candidate_issues: list[dict[str, Any]] = []
    candidate_boundary_failure_count = 0
    candidate_state_binding_failure_count = 0
    candidate_state_hash_mismatch_count = 0
    candidate_snapshot_sequence_mismatch_count = 0
    candidate_state_order_failure_count = 0
    candidate_evidence_consistent_count = 0
    state_lines: dict[tuple[str, str], list[int]] = defaultdict(list)
    for solve in solves:
        state_lines[(solve.game_id, solve.state_id)].append(solve.line)
    for observation in candidate_actions:
        metadata = _mapping(observation.value.get("metadata"))
        pre_state_id = _text(metadata.get("pre_state_id"))
        post_state_id = _text(metadata.get("post_state_id"))
        pre_key = (observation.game_id, pre_state_id)
        post_key = (observation.game_id, post_state_id)
        pre_state = _mapping(observation.value.get("pre_state"))
        post_state = _mapping(observation.value.get("post_state"))
        local_failures: list[str] = []

        boundary = _text(metadata.get("boundary_status")).lower()
        intervening = _metadata_integer(metadata.get("intervening_action_count"), minimum=0)
        warning_count = _metadata_integer(metadata.get("capture_warning_count"), minimum=0)
        if boundary != "isolated" or intervening != 0 or warning_count != 0:
            candidate_boundary_failure_count += 1
            local_failures.append("candidate_boundary_not_isolated_and_clean")
        if pre_state is None or post_state is None:
            candidate_state_binding_failure_count += 1
            local_failures.append("candidate_missing_pre_or_post_state")
        else:
            pre_metadata = _mapping(pre_state.get("metadata"))
            post_metadata = _mapping(post_state.get("metadata"))
            if (
                state_hashes.get(pre_key) != _state_identity_hash(pre_state)
                or state_hashes.get(post_key) != _state_identity_hash(post_state)
            ):
                candidate_state_hash_mismatch_count += 1
                local_failures.append("candidate_snapshot_hash_mismatch")
            pre_sequence = _metadata_integer(
                pre_metadata.get("snapshot_sequence"), minimum=1
            )
            post_sequence = _metadata_integer(
                post_metadata.get("snapshot_sequence"), minimum=1
            )
            if (
                pre_sequence
                != _metadata_integer(metadata.get("pre_snapshot_sequence"), minimum=1)
                or post_sequence
                != _metadata_integer(metadata.get("post_snapshot_sequence"), minimum=1)
            ):
                candidate_snapshot_sequence_mismatch_count += 1
                local_failures.append("candidate_snapshot_sequence_mismatch")
        if (
            not any(line < observation.line for line in state_lines.get(pre_key, []))
            or (
                not _mapping(observation.value.get("post_state"))
                and not any(line > observation.line for line in state_lines.get(post_key, []))
            )
        ):
            candidate_state_order_failure_count += 1
            local_failures.append("candidate_state_log_order_mismatch")
        if local_failures:
            candidate_issues.append(
                {"line": observation.line, "reasons": local_failures}
            )
        else:
            candidate_evidence_consistent_count += 1

    verified_transitions.sort(
        key=lambda item: (
            item["game_id"], item["action_sequence"], item["observation_line"]
        )
    )

    chain_issues: list[dict[str, Any]] = []
    action_records_by_game: dict[str, list[_Observation]] = defaultdict(list)
    for observation in actions:
        action_records_by_game[observation.game_id].append(observation)
    duplicate_action_sequence_count = 0
    non_contiguous_action_sequence_game_count = 0
    action_order_violation_count = 0
    action_chain_break_count = 0
    for game_id, game_actions in action_records_by_game.items():
        ordered_by_line = sorted(game_actions, key=lambda item: item.line)
        sequences = [
            int(str(_mapping(item.value.get("metadata")).get("action_sequence")))
            for item in ordered_by_line
        ]
        duplicate_action_sequence_count += len(sequences) - len(set(sequences))
        if sequences != sorted(sequences):
            action_order_violation_count += 1
            chain_issues.append(
                {"game_id": game_id, "reason": "action_sequence_out_of_log_order"}
            )
        if sequences != list(range(1, len(sequences) + 1)):
            non_contiguous_action_sequence_game_count += 1
            chain_issues.append(
                {"game_id": game_id, "reason": "non_contiguous_action_sequence"}
            )
        for previous, current in zip(ordered_by_line, ordered_by_line[1:]):
            if previous.line not in exact_action_lines or current.line not in exact_action_lines:
                continue
            previous_action = _mapping(previous.value.get("action"))
            if _text(previous_action.get("kind") or previous_action.get("type")).lower() == "end_turn":
                # End-turn closes the local-player segment.  The next recorded local
                # action may follow a whole opponent turn and is not state-adjacent.
                continue
            previous_post = _text(
                _mapping(previous.value.get("metadata")).get("post_state_id")
            )
            current_pre = _text(
                _mapping(current.value.get("metadata")).get("pre_state_id")
            )
            if previous_post and current_pre and previous_post != current_pre:
                action_chain_break_count += 1
                chain_issues.append(
                    {"line": current.line, "reason": "pre_post_chain_break"}
                )

    state_provenance: dict[
        tuple[str, str], list[_Solve | _DecisionSnapshot]
    ] = defaultdict(list)
    for solve in solves:
        state_provenance[(solve.game_id, solve.state_id)].append(solve)
    for snapshot in decision_snapshots:
        state_provenance[(snapshot.game_id, snapshot.state_id)].append(snapshot)
    action_decision_join_failure_count = 0
    pre_state_order_violation_count = 0
    post_state_order_violation_count = 0
    attached_decision_keys: set[tuple[str, str]] = set()
    for observation in exact_actions:
        metadata = _mapping(observation.value.get("metadata"))
        pre_id = _text(metadata.get("pre_state_id"))
        post_id = _text(metadata.get("post_state_id"))
        decision = canonical.get((observation.game_id, observation.decision_id))
        attached_pre = isinstance(observation.value.get("pre_state"), Mapping)
        if attached_pre:
            attached_decision_keys.add((observation.game_id, observation.decision_id))
        if (decision is None and not attached_pre) or (
            decision is not None and decision.state_id != pre_id
        ):
            action_decision_join_failure_count += 1
            chain_issues.append(
                {"line": observation.line, "reason": "action_decision_join_failure"}
            )
        pre_candidates = state_provenance.get((observation.game_id, pre_id), [])
        if (
            pre_candidates
            and all(item.line >= observation.line for item in pre_candidates)
        ) or (not pre_candidates and not attached_pre):
            pre_state_order_violation_count += 1
            chain_issues.append(
                {"line": observation.line, "reason": "pre_state_not_logged_before_action"}
            )
        post_candidates = state_provenance.get((observation.game_id, post_id), [])
        attached_post = isinstance(observation.value.get("post_state"), Mapping)
        if (
            post_candidates
            and all(item.line <= observation.line for item in post_candidates)
        ) or (not post_candidates and not attached_post):
            post_state_order_violation_count += 1
            chain_issues.append(
                {"line": observation.line, "reason": "post_state_not_logged_after_action"}
            )

    terminal_before_last_action_count = 0
    terminal_state_mismatch_count = 0
    for game_id, game_results in result_records_by_game.items():
        game_actions = sorted(action_records_by_game.get(game_id, []), key=lambda item: item.line)
        if not game_actions:
            continue
        last_action = game_actions[-1]
        last_post_id = _text(
            _mapping(last_action.value.get("metadata")).get("post_state_id")
        )
        for result in game_results:
            if result.line <= last_action.line:
                terminal_before_last_action_count += 1
                chain_issues.append(
                    {"line": result.line, "reason": "terminal_result_before_last_action"}
                )
            result_metadata = _mapping(result.value.get("metadata"))
            if (
                _text(result_metadata.get("terminal_adjacency")).lower() == "immediate"
                and (not last_post_id or result.state_id != last_post_id)
            ):
                terminal_state_mismatch_count += 1
                chain_issues.append(
                    {"line": result.line, "reason": "terminal_state_mismatch"}
                )

    decision_provenance_keys = set(canonical) | attached_decision_keys
    canonical_games = {key[0] for key in decision_provenance_keys}
    terminal_games = {game_id for game_id, values in result_by_game.items() if len(values) == 1}
    joined_games = canonical_games & terminal_games
    joined_decisions = sum(
        1 for key in decision_provenance_keys if key[0] in terminal_games
    )
    solve_status_counts = Counter(_solve_status_bucket(solve.status) for solve in solves)
    ok_solves = solve_status_counts["ok"]
    partial_solves = solve_status_counts["partial"]
    cancelled_solves = solve_status_counts["cancelled"]
    unsupported_solves = solve_status_counts["unsupported"]
    error_solves = solve_status_counts["error"]
    other_solves = solve_status_counts["other"]
    # Keep the operational outcome buckets disjoint.  In particular, an honest
    # ``unsupported`` abstention is not an execution failure, and partial or
    # cancelled searches already have their own policy thresholds.  Preserve the
    # broad "anything other than ok" diagnostic under an unambiguous name.
    non_ok_solves = error_solves + other_solves
    unsuccessful_solves = len(solves) - ok_solves
    partial_actions = len(actions) - len(exact_actions)
    all_game_ids = (
        {item.game_id for item in solves}
        | set(result_by_game)
        | {item.game_id for item in observations}
    )
    split_counts = Counter(deterministic_game_split(game_id) for game_id in all_game_ids)
    split_assignment_mismatch_count = sum(
        1
        for game_id, values in declared_splits.items()
        if any(value != deterministic_game_split(game_id) for value in values)
    )
    cross_split_leakage_count = sum(1 for values in declared_splits.values() if len(values) > 1)

    metrics = {
        "record_count": len(records),
        "invalid_json_or_record_count": len(parse_issues),
        "contract_issue_count": len(contract_issues),
        "privacy_violation_count": len(privacy_issues),
        "solve_record_count": len(solves),
        "decision_snapshot_record_count": len(decision_snapshots),
        "ok_solve_count": ok_solves,
        "partial_solve_count": partial_solves,
        "cancelled_solve_count": cancelled_solves,
        "unsupported_solve_count": unsupported_solves,
        "error_solve_count": error_solves,
        "other_solve_count": other_solves,
        "non_ok_solve_count": non_ok_solves,
        "unsuccessful_solve_count": unsuccessful_solves,
        "initial_solve_count": sum(1 for item in solves if item.stage == "initial"),
        "final_solve_count": sum(1 for item in solves if item.stage == "final"),
        "single_solve_count": sum(1 for item in solves if item.stage == "single"),
        "unique_game_count": len(all_game_ids),
        "unique_decision_count": len(
            set(solve_groups) | set(snapshot_groups) | attached_decision_keys
        ),
        "canonical_decision_count": len(decision_provenance_keys),
        "superseded_initial_solve_count": superseded_initial_count,
        "duplicate_solve_count": duplicate_solve_count,
        "conflicting_final_solve_count": conflicting_final_solve_count,
        "duplicate_decision_snapshot_count": duplicate_decision_snapshot_count,
        "conflicting_decision_snapshot_count": conflicting_decision_snapshot_count,
        "state_content_conflict_count": state_content_conflict_count,
        "cross_game_state_id_reuse_count": cross_game_state_id_reuse_count,
        "action_observation_count": len(actions),
        "exact_action_count": len(exact_actions),
        "replayable_transition_count": replayable_count,
        "candidate_transition_count": len(candidate_actions),
        "candidate_evidence_consistent_count": candidate_evidence_consistent_count,
        "candidate_boundary_failure_count": candidate_boundary_failure_count,
        "candidate_state_binding_failure_count": candidate_state_binding_failure_count,
        "candidate_state_hash_mismatch_count": candidate_state_hash_mismatch_count,
        "candidate_snapshot_sequence_mismatch_count": candidate_snapshot_sequence_mismatch_count,
        "candidate_state_order_failure_count": candidate_state_order_failure_count,
        "partial_action_count": partial_actions,
        "terminal_result_observation_count": len(results),
        "terminal_result_game_count": len(terminal_games),
        "conflicting_result_game_count": conflicting_result_game_count,
        "duplicate_result_observation_count": duplicate_result_observation_count,
        "joined_game_count": len(joined_games),
        "joined_decision_count": joined_decisions,
        "solve_result_join_rate": _rate(joined_decisions, len(decision_provenance_keys)),
        "exact_action_rate": _rate(len(exact_actions), len(actions)),
        "replayable_transition_rate": _rate(replayable_count, len(exact_actions)),
        "partial_action_rate": _rate(partial_actions, len(actions)),
        "ok_solve_rate": _rate(ok_solves, len(solves)),
        "partial_solve_rate": _rate(partial_solves, len(solves)),
        "cancelled_solve_rate": _rate(cancelled_solves, len(solves)),
        "unsupported_solve_rate": _rate(unsupported_solves, len(solves)),
        "error_solve_rate": _rate(error_solves, len(solves)),
        "other_solve_rate": _rate(other_solves, len(solves)),
        "non_ok_solve_rate": _rate(non_ok_solves, len(solves)),
        "unsuccessful_solve_rate": _rate(unsuccessful_solves, len(solves)),
        "solve_status_counts": dict(sorted(solve_status_counts.items())),
        "replay_failure_count": len(replay_failures),
        "duplicate_action_sequence_count": duplicate_action_sequence_count,
        "non_contiguous_action_sequence_game_count": non_contiguous_action_sequence_game_count,
        "action_order_violation_count": action_order_violation_count,
        "action_chain_break_count": action_chain_break_count,
        "action_decision_join_failure_count": action_decision_join_failure_count,
        "pre_state_order_violation_count": pre_state_order_violation_count,
        "post_state_order_violation_count": post_state_order_violation_count,
        "terminal_before_last_action_count": terminal_before_last_action_count,
        "terminal_state_mismatch_count": terminal_state_mismatch_count,
        "split_assignment_mismatch_count": split_assignment_mismatch_count,
        "cross_split_leakage_count": cross_split_leakage_count,
        "split_game_counts": dict(sorted(split_counts.items())),
    }

    contract_checks = [
        _check("invalid_json_or_record_count", metrics["invalid_json_or_record_count"], "<=", 0),
        _check("contract_issue_count", metrics["contract_issue_count"], "<=", 0),
        _check("privacy_violation_count", metrics["privacy_violation_count"], "<=", 0),
        _check("duplicate_solve_count", metrics["duplicate_solve_count"], "<=", 0),
        _check("conflicting_final_solve_count", metrics["conflicting_final_solve_count"], "<=", 0),
        _check(
            "duplicate_decision_snapshot_count",
            metrics["duplicate_decision_snapshot_count"],
            "<=",
            0,
        ),
        _check(
            "conflicting_decision_snapshot_count",
            metrics["conflicting_decision_snapshot_count"],
            "<=",
            0,
        ),
        _check("state_content_conflict_count", metrics["state_content_conflict_count"], "<=", 0),
        _check("cross_game_state_id_reuse_count", metrics["cross_game_state_id_reuse_count"], "<=", 0),
        _check("conflicting_result_game_count", metrics["conflicting_result_game_count"], "<=", 0),
        _check("duplicate_result_observation_count", metrics["duplicate_result_observation_count"], "<=", 0),
        _check("replay_failure_count", metrics["replay_failure_count"], "<=", 0),
        _check("duplicate_action_sequence_count", metrics["duplicate_action_sequence_count"], "<=", 0),
        _check(
            "non_contiguous_action_sequence_game_count",
            metrics["non_contiguous_action_sequence_game_count"],
            "<=",
            0,
        ),
        _check("action_order_violation_count", metrics["action_order_violation_count"], "<=", 0),
        _check("action_chain_break_count", metrics["action_chain_break_count"], "<=", 0),
        _check(
            "action_decision_join_failure_count",
            metrics["action_decision_join_failure_count"],
            "<=",
            0,
        ),
        _check("pre_state_order_violation_count", metrics["pre_state_order_violation_count"], "<=", 0),
        _check("post_state_order_violation_count", metrics["post_state_order_violation_count"], "<=", 0),
        _check("terminal_before_last_action_count", metrics["terminal_before_last_action_count"], "<=", 0),
        _check("terminal_state_mismatch_count", metrics["terminal_state_mismatch_count"], "<=", 0),
        _check("split_assignment_mismatch_count", metrics["split_assignment_mismatch_count"], "<=", 0),
        _check("cross_split_leakage_count", metrics["cross_split_leakage_count"], "<=", 0),
    ]
    readiness_checks = [
        # Non-overridable readiness invariants. Contract validity describes whether
        # present records are internally honest; it may still be true for a partial-only
        # or empty capture. Such a capture can never become training-ready.
        _check("has_canonical_decision", metrics["canonical_decision_count"], ">=", 1),
        _check("has_terminal_result_game", metrics["terminal_result_game_count"], ">=", 1),
        _check("has_joined_game", metrics["joined_game_count"], ">=", 1),
        _check("has_exact_action", metrics["exact_action_count"], ">=", 1),
        _check("has_replayable_transition", metrics["replayable_transition_count"], ">=", 1),
        _check("unique_game_count", metrics["unique_game_count"], ">=", policy["min_unique_games"]),
        _check(
            "canonical_decision_count",
            metrics["canonical_decision_count"],
            ">=",
            policy["min_canonical_decisions"],
        ),
        _check(
            "terminal_result_game_count",
            metrics["terminal_result_game_count"],
            ">=",
            policy["min_terminal_result_games"],
        ),
        _check(
            "solve_result_join_rate",
            metrics["solve_result_join_rate"],
            ">=",
            policy["min_solve_result_join_rate"],
        ),
        _check(
            "exact_action_rate",
            metrics["exact_action_rate"],
            ">=",
            policy["min_exact_action_rate"],
        ),
        _check(
            "replayable_transition_rate",
            metrics["replayable_transition_rate"],
            ">=",
            policy["min_replayable_transition_rate"],
        ),
        _check(
            "partial_action_rate",
            metrics["partial_action_rate"],
            "<=",
            policy["max_partial_action_rate"],
        ),
    ]
    operational_checks = [
        # Online solve outcomes describe live-advisor health, not whether an
        # independently replayed state/action/result trajectory is valid training data.
        _check("has_solve_record", metrics["solve_record_count"], ">=", 1),
        _check(
            "unsupported_solve_rate",
            metrics["unsupported_solve_rate"],
            "<=",
            policy["max_unsupported_solve_rate"],
        ),
        _check(
            "cancelled_solve_rate",
            metrics["cancelled_solve_rate"],
            "<=",
            policy["max_cancelled_solve_rate"],
        ),
        _check(
            "partial_solve_rate",
            metrics["partial_solve_rate"],
            "<=",
            policy["max_partial_solve_rate"],
        ),
        _check(
            "non_ok_solve_rate",
            metrics["non_ok_solve_rate"],
            "<=",
            policy["max_non_ok_solve_rate"],
        ),
    ]
    contract_passed = all(item["passed"] for item in contract_checks)
    training_ready = contract_passed and all(item["passed"] for item in readiness_checks)
    solver_runtime_ready = contract_passed and all(
        item["passed"] for item in operational_checks
    )
    source = Path(input_path)
    return {
        "schema": TRAJECTORY_REPORT_SCHEMA_ID,
        "trajectory_schema": TRAJECTORY_SCHEMA_ID,
        "source_kind": source_kind,
        "input": source.name,
        "input_sha256": input_sha256,
        "input_bytes": input_bytes,
        "policy": policy,
        "policy_sha256": policy_sha256,
        "solve_status_semantics": copy.deepcopy(SOLVE_STATUS_SEMANTICS),
        "metrics": metrics,
        "contract_checks": contract_checks,
        "readiness_checks": readiness_checks,
        "operational_checks": operational_checks,
        "contract_passed": contract_passed,
        "training_ready": training_ready,
        "solver_runtime_ready": solver_runtime_ready,
        "passed": training_ready,
        "verified_transitions": verified_transitions,
        "issues": _issue_report(
            {
                "parse": parse_issues,
                "contract": contract_issues,
                "privacy": privacy_issues,
                "replay": replay_failures,
                "candidate": candidate_issues,
                "chain": chain_issues,
            }
        ),
        "caveat": (
            "Training-ready means only that anonymized trajectories satisfy this versioned "
            "join, exact-action, replay, split, and privacy contract. It does not prove that "
            "the online solver is healthy or optimal, that labels are unbiased, or that an "
            "RL policy exists; solver_runtime_ready reports online solve health separately."
        ),
    }


def default_runtime_trajectory_path() -> Path | None:
    app_data = os.environ.get("APPDATA", "").strip()
    if not app_data:
        return None
    return (
        Path(app_data)
        / "HearthstoneDeckTracker"
        / "MetaCompanion"
        / "AdvisorWorker"
        / "training-v2.jsonl"
    )


def _write_content_addressed_snapshot(
    payload: bytes,
    source_name: str,
    snapshot_directory: str | Path,
) -> tuple[Path, str]:
    digest = hashlib.sha256(payload).hexdigest()
    safe_stem = Path(source_name).stem or "training-v2"
    target_directory = Path(snapshot_directory)
    target_directory.mkdir(parents=True, exist_ok=True)
    target = target_directory / f"{safe_stem}.{digest}.jsonl"
    try:
        with target.open("xb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
    except FileExistsError:
        try:
            existing = target.read_bytes()
        except OSError as exc:
            raise TrajectoryAuditError(
                "content-addressed trajectory snapshot could not be verified"
            ) from exc
        if existing != payload:
            raise TrajectoryAuditError(
                "content-addressed trajectory snapshot hash collision or overwrite detected"
            )
    return target, digest


def audit_runtime_trajectory(
    *,
    input_path: str | Path | None = None,
    snapshot_directory: str | Path,
    policy_path: str | Path | None = None,
) -> dict[str, Any]:
    """Snapshot and audit the real runtime JSONL without rereading the live file.

    The mutable runtime file is read once into bytes, then persisted under a content-addressed,
    exclusive-create name. The ordinary auditor subsequently reads only that immutable-by-
    contract snapshot. A concurrently appended partial line therefore fails closed inside the
    captured snapshot instead of producing a time-of-check/time-of-use mismatch.
    """

    policy = load_trajectory_policy(policy_path)
    policy_sha256 = trajectory_policy_sha256(policy)
    source = Path(input_path) if input_path is not None else default_runtime_trajectory_path()
    base: dict[str, Any] = {
        "schema": RUNTIME_TRAJECTORY_REPORT_SCHEMA_ID,
        "source_kind": SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
        "input": source.name if source is not None else "training-v2.jsonl",
        "input_sha256": "",
        "input_bytes": 0,
        "policy_sha256": policy_sha256,
        "snapshot": "",
        "snapshot_content_addressed": False,
        "contract_passed": False,
        "training_ready": False,
        "status": "NO_DATA",
        "audit": None,
    }
    if source is None or not source.is_file():
        base["reason"] = "runtime_training_log_not_found"
        return base
    try:
        payload = source.read_bytes()
    except FileNotFoundError:
        base["reason"] = "runtime_training_log_not_found"
        return base
    except OSError as exc:
        raise TrajectoryAuditError("runtime trajectory input could not be snapshotted") from exc
    if not payload:
        base["reason"] = "runtime_training_log_empty"
        return base
    if len(payload) > MAX_INPUT_BYTES:
        raise TrajectoryAuditError("trajectory input exceeds the 256 MiB audit limit")

    snapshot, digest = _write_content_addressed_snapshot(
        payload,
        source.name,
        snapshot_directory,
    )
    audit = audit_trajectory_file(
        snapshot,
        policy_path=policy_path,
        source_kind=SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
    )
    if audit["input_sha256"] != digest or audit["input_bytes"] != len(payload):
        raise TrajectoryAuditError("runtime trajectory snapshot identity changed during audit")
    ready = bool(audit["training_ready"])
    base.update(
        {
            "input_sha256": digest,
            "input_bytes": len(payload),
            "policy_sha256": audit["policy_sha256"],
            "snapshot": snapshot.name,
            "snapshot_content_addressed": True,
            "contract_passed": bool(audit["contract_passed"]),
            "training_ready": ready,
            "status": "READY" if ready else "NOT_READY",
            "reason": "production_policy_passed" if ready else "production_policy_failed",
            "audit": audit,
        }
    )
    return base


def write_trajectory_report(report: Mapping[str, Any], path: str | Path) -> None:
    output = Path(path)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    temporary.replace(output)
