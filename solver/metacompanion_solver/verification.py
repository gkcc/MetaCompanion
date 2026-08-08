from __future__ import annotations

import copy
import hashlib
import json
import tempfile
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Mapping, Sequence

from .logging_store import TRAJECTORY_SCHEMA_ID, TRAINING_LOG_SCHEMA_ID, sanitize_for_training
from .offline import load_records
from .power_evidence import PowerEvidenceError, verify_power_identity_transition
from .schemas import (
    POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
    TRANSITION_CANDIDATE_ENVELOPE_FIELDS,
)
from .trajectory import DECISION_SNAPSHOT_CAPTURE_CONTRACT, audit_trajectory_file


VERIFICATION_MANIFEST_SCHEMA_ID = "trajectory-verification-manifest-v1"
POWER_SIMULATOR_VERIFIED_STATUS = "exact_post_state_match_v1"
POWER_SIMULATOR_VERIFICATION = "offline_simulator_verified_v1"


class TrajectoryVerificationError(ValueError):
    """Raised when a corpus cannot be promoted or its verification is stale."""


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _atomic_write_bytes(value: bytes, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    temporary.write_bytes(value)
    temporary.replace(destination)


def _atomic_write_json(value: Mapping[str, Any], destination: Path) -> None:
    _atomic_write_bytes(
        json.dumps(value, ensure_ascii=False, indent=2).encode("utf-8") + b"\n",
        destination,
    )


def _normalized_verified_transitions(
    value: Any,
) -> list[dict[str, Any]]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise TrajectoryVerificationError("verification manifest is missing verified transitions")
    result: list[dict[str, Any]] = []
    required = {
        "game_id",
        "action_sequence",
        "pre_state_id",
        "post_state_id",
        "normalized_pre_state_hash",
        "normalized_post_state_hash",
    }
    for index, item in enumerate(value):
        if not isinstance(item, Mapping):
            raise TrajectoryVerificationError(
                f"verified transition {index} must be an object"
            )
        normalized = {str(key): item[key] for key in sorted(item)}
        if not required.issubset(normalized):
            missing = ", ".join(sorted(required - set(normalized)))
            raise TrajectoryVerificationError(
                f"verified transition {index} is missing: {missing}"
            )
        result.append(normalized)
    result.sort(key=lambda item: _canonical_json(item))
    return result


def _simulator_provenance() -> dict[str, str]:
    simulator = Path(__file__).with_name("simulator.py")
    return {
        "id": "metacompanion_solver.simulator.apply_action",
        "sha256": _sha256_bytes(simulator.read_bytes()),
    }


def validate_trajectory_verification(
    manifest: Mapping[str, Any],
    *,
    dataset_bytes: bytes,
    audit_report: Mapping[str, Any],
) -> dict[str, Any]:
    """Bind a per-transition verification manifest to one immutable corpus snapshot."""

    if manifest.get("schema") != VERIFICATION_MANIFEST_SCHEMA_ID:
        raise TrajectoryVerificationError("unsupported trajectory verification manifest")
    if manifest.get("trajectory_schema") != TRAJECTORY_SCHEMA_ID:
        raise TrajectoryVerificationError("verification trajectory schema does not match")
    if manifest.get("training_log_schema") != TRAINING_LOG_SCHEMA_ID:
        raise TrajectoryVerificationError("verification training-log schema does not match")
    if manifest.get("training_ready") is not True:
        raise TrajectoryVerificationError("verification manifest is not training-ready")

    dataset = manifest.get("verified_dataset")
    if not isinstance(dataset, Mapping):
        raise TrajectoryVerificationError("verification manifest is missing dataset provenance")
    dataset_sha256 = _sha256_bytes(dataset_bytes)
    if dataset.get("sha256") != dataset_sha256:
        raise TrajectoryVerificationError("verified dataset hash does not match the input")
    if audit_report.get("input_sha256") != dataset_sha256:
        raise TrajectoryVerificationError("fresh audit did not inspect the training snapshot")
    if audit_report.get("training_ready") is not True:
        raise TrajectoryVerificationError("fresh trajectory audit is not training-ready")

    audit = manifest.get("audit")
    if not isinstance(audit, Mapping):
        raise TrajectoryVerificationError("verification manifest is missing audit provenance")
    if audit.get("schema") != audit_report.get("schema"):
        raise TrajectoryVerificationError("verification audit schema does not match")
    if audit.get("policy") != audit_report.get("policy"):
        raise TrajectoryVerificationError("verification policy does not match the fresh audit")
    if audit.get("input_sha256") != dataset_sha256:
        raise TrajectoryVerificationError("verification audit hash is stale")

    expected = _normalized_verified_transitions(
        audit_report.get("verified_transitions")
    )
    declared = _normalized_verified_transitions(manifest.get("verified_transitions"))
    if not expected:
        raise TrajectoryVerificationError("fresh audit verified no transitions")
    if declared != expected:
        raise TrajectoryVerificationError("verified transition allowlist is stale or incomplete")

    simulator = manifest.get("simulator")
    current_simulator = _simulator_provenance()
    if not isinstance(simulator, Mapping) or dict(simulator) != current_simulator:
        raise TrajectoryVerificationError("simulator provenance does not match this build")
    return {
        "schema": VERIFICATION_MANIFEST_SCHEMA_ID,
        "dataset_sha256": dataset_sha256,
        "verified_transition_count": len(expected),
        "simulator": current_simulator,
    }


def load_and_validate_trajectory_verification(
    path: str | Path,
    *,
    dataset_bytes: bytes,
    audit_report: Mapping[str, Any],
) -> dict[str, Any]:
    try:
        raw = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise TrajectoryVerificationError(
            "trajectory verification manifest could not be read"
        ) from exc
    if not isinstance(raw, Mapping):
        raise TrajectoryVerificationError("trajectory verification manifest must be an object")
    return validate_trajectory_verification(
        raw,
        dataset_bytes=dataset_bytes,
        audit_report=audit_report,
    )


def _jsonl_bytes(records: Sequence[Mapping[str, Any]]) -> bytes:
    lines = [
        json.dumps(
            sanitize_for_training(record),
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
        for record in records
    ]
    return (("\n".join(lines) + "\n") if lines else "").encode("utf-8")


def _record_game_id(record: Mapping[str, Any]) -> str:
    trajectory = record.get("trajectory")
    if not isinstance(trajectory, Mapping):
        return ""
    value = trajectory.get("game_id")
    return value.strip() if isinstance(value, str) else ""


def _strict_integer(value: Any, *, minimum: int = 0) -> int | None:
    if isinstance(value, bool):
        return None
    try:
        parsed = int(str(value))
    except (TypeError, ValueError):
        return None
    if str(value).strip() != str(parsed) or parsed < minimum:
        return None
    return parsed


def _decision_snapshot_record(
    *,
    game_id: str,
    split: str,
    state: Mapping[str, Any],
    normalized_state_hash: str,
    action_sequence: int,
    state_role: str,
    source_observation: Mapping[str, Any],
) -> Mapping[str, Any]:
    state_id = str(state.get("state_id") or "").strip()
    return {
        "kind": "decision_snapshot",
        "log_schema": TRAINING_LOG_SCHEMA_ID,
        "trajectory": {
            "schema": TRAJECTORY_SCHEMA_ID,
            "game_id": game_id,
            "split": split,
            "decision_id": state_id,
            "state_id": state_id,
            "capture_contract": DECISION_SNAPSHOT_CAPTURE_CONTRACT,
            "normalized_state_hash": normalized_state_hash,
        },
        "state": copy.deepcopy(state),
        "provenance": {
            "source_capture_contract": POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
            "source_action_sequence": action_sequence,
            "state_role": state_role,
            "simulator_verification": POWER_SIMULATOR_VERIFICATION,
            "source_observation_sha256": _sha256_bytes(
                _canonical_json(source_observation)
            ),
        },
    }


def _power_verified_records(
    records: Sequence[Mapping[str, Any]],
) -> tuple[list[Mapping[str, Any]], dict[str, Any]]:
    """Promote only complete games whose every action has matching Power evidence."""

    by_game: dict[str, list[tuple[int, Mapping[str, Any]]]] = defaultdict(list)
    for index, record in enumerate(records):
        game_id = _record_game_id(record)
        if game_id:
            by_game[game_id].append((index, record))

    rejected = Counter()
    source_solve_status_counts: Counter[str] = Counter()
    for record in records:
        if record.get("kind") != "solve":
            continue
        result = record.get("result")
        status = (
            str(result.get("status") or "missing").strip().lower()
            if isinstance(result, Mapping)
            else "missing"
        )
        source_solve_status_counts[status] += 1
    promoted_by_index: dict[int, Mapping[str, Any]] = {}
    snapshot_sources_by_index: dict[
        int,
        tuple[
            str,
            int,
            Mapping[str, Any],
            str,
            Mapping[str, Any],
            str,
            Mapping[str, Any],
        ],
    ] = {}
    accepted_games: set[str] = set()
    verified_transition_count = 0
    for game_id, items in by_game.items():
        actions = [
            (index, record)
            for index, record in items
            if record.get("kind") == "observation"
            and isinstance(record.get("observation"), Mapping)
            and record["observation"].get("kind") == "action"
        ]
        results = [
            (index, record)
            for index, record in items
            if record.get("kind") == "observation"
            and isinstance(record.get("observation"), Mapping)
            and record["observation"].get("kind") == "result"
        ]
        if not actions:
            rejected["game_without_actions"] += 1
            continue
        if len(results) != 1:
            rejected["game_without_one_terminal_result"] += 1
            continue

        terminal_index, terminal_record = results[0]
        terminal_observation = terminal_record.get("observation")
        terminal_metadata = (
            terminal_observation.get("metadata")
            if isinstance(terminal_observation, Mapping)
            else None
        )
        if not isinstance(terminal_metadata, Mapping):
            rejected["terminal_power_trace_metadata_missing"] += 1
            continue
        if terminal_index <= actions[-1][0]:
            rejected["terminal_result_before_last_power_action"] += 1
            continue

        game_promoted: dict[int, Mapping[str, Any]] = {}
        game_snapshot_sources: dict[
            int,
            tuple[
                str,
                int,
                Mapping[str, Any],
                str,
                Mapping[str, Any],
                str,
                Mapping[str, Any],
            ],
        ] = {}
        game_state_bindings: dict[str, str] = {}
        sequences: list[int] = []
        game_generation: int | None = None
        collector_epoch: int | None = None
        previous_end_cursor: int | None = None
        previous_frame_id: int | None = None
        game_failure = ""
        for index, record in actions:
            observation = record["observation"]
            metadata = observation.get("metadata")
            if not isinstance(metadata, Mapping) or str(
                metadata.get("capture_contract") or ""
            ).strip().lower() != POWER_ACTION_IDENTITY_CAPTURE_CONTRACT:
                game_failure = "game_contains_non_power_action"
                break
            try:
                evidence = verify_power_identity_transition(observation)
            except PowerEvidenceError as exc:
                game_failure = exc.code
                break
            try:
                sequence = int(str(metadata.get("action_sequence")))
            except (TypeError, ValueError):
                game_failure = "power_action_sequence_invalid"
                break
            decision_id = str(
                metadata.get("decision_id")
                or metadata.get("pre_state_id")
                or observation.get("state_id")
                or ""
            ).strip()
            if decision_id != evidence.pre_state.state_id:
                game_failure = "power_decision_id_pre_state_mismatch"
                break
            for state_id, digest in (
                (evidence.pre_state.state_id, evidence.normalized_pre_state_hash),
                (evidence.post_state.state_id, evidence.normalized_post_state_hash),
            ):
                existing_digest = game_state_bindings.get(state_id)
                if existing_digest is not None and existing_digest != digest:
                    game_failure = "power_state_id_hash_conflict"
                    break
                game_state_bindings[state_id] = digest
            if game_failure:
                break
            sequences.append(sequence)
            if sequence != len(sequences):
                game_failure = "power_action_sequence_not_contiguous"
                break
            if game_generation is None:
                game_generation = evidence.game_generation
                collector_epoch = _strict_integer(
                    metadata.get("power_collector_epoch"), minimum=1
                )
            elif evidence.game_generation != game_generation or _strict_integer(
                metadata.get("power_collector_epoch"), minimum=1
            ) != collector_epoch:
                game_failure = "power_collector_epoch_changed_within_game"
                break
            if previous_end_cursor is not None and evidence.start_cursor <= previous_end_cursor:
                game_failure = "power_watermark_not_strictly_non_overlapping"
                break
            if previous_frame_id is not None and evidence.frame_id <= previous_frame_id:
                game_failure = "power_frame_id_not_strictly_increasing"
                break
            previous_end_cursor = evidence.end_cursor
            previous_frame_id = evidence.frame_id

            promoted = copy.deepcopy(record)
            promoted_observation = promoted["observation"]
            promoted_metadata = promoted_observation["metadata"]
            promoted_metadata.update(
                {
                    "capture_contract": TRAJECTORY_SCHEMA_ID,
                    "completeness": "complete_action_trace_v1",
                    "transition_status": "replayable_exact",
                    "transition_verification": POWER_SIMULATOR_VERIFICATION,
                    "simulator_status": POWER_SIMULATOR_VERIFIED_STATUS,
                    "training_eligible": True,
                    "verified_pre_state_hash": evidence.normalized_pre_state_hash,
                    "verified_post_state_hash": evidence.normalized_post_state_hash,
                }
            )
            promoted_trajectory = promoted["trajectory"]
            promoted_trajectory.update(
                {
                    "capture_contract": TRAJECTORY_SCHEMA_ID,
                    "completeness": "complete_action_trace_v1",
                    "transition_status": "replayable_exact",
                }
            )
            for field in TRANSITION_CANDIDATE_ENVELOPE_FIELDS:
                if field in promoted_metadata:
                    promoted_trajectory[field] = promoted_metadata[field]
            game_promoted[index] = promoted
            split = str(promoted_trajectory.get("split") or "").strip().lower()
            game_snapshot_sources[index] = (
                split,
                sequence,
                copy.deepcopy(observation["pre_state"]),
                evidence.normalized_pre_state_hash,
                copy.deepcopy(observation["post_state"]),
                evidence.normalized_post_state_hash,
                observation,
            )

        if game_failure:
            rejected[game_failure] += 1
            continue
        if sequences != list(range(1, len(sequences) + 1)):
            rejected["power_action_sequence_not_contiguous"] += 1
            continue
        terminal_generation = _strict_integer(
            terminal_metadata.get("game_generation"), minimum=1
        )
        terminal_epoch = _strict_integer(
            terminal_metadata.get("power_collector_epoch"), minimum=1
        )
        committed_count = _strict_integer(
            terminal_metadata.get("power_committed_action_count"), minimum=0
        )
        recorded_count = _strict_integer(
            terminal_metadata.get("power_recorded_action_count"), minimum=0
        )
        terminal_gap_count = _strict_integer(
            terminal_metadata.get("power_gap_count"), minimum=0
        )
        if str(terminal_metadata.get("power_trace_status") or "").strip().lower() != "complete":
            rejected["terminal_power_trace_not_complete"] += 1
            continue
        if terminal_generation != game_generation or terminal_epoch != collector_epoch:
            rejected["terminal_power_collector_epoch_mismatch"] += 1
            continue
        if committed_count != len(actions) or recorded_count != len(actions):
            rejected["terminal_power_action_count_mismatch"] += 1
            continue
        if terminal_gap_count != 0:
            rejected["terminal_power_gap_count_nonzero"] += 1
            continue
        accepted_games.add(game_id)
        promoted_by_index.update(game_promoted)
        snapshot_sources_by_index.update(game_snapshot_sources)
        verified_transition_count += len(game_promoted)

    output: list[Mapping[str, Any]] = []
    emitted_snapshots: set[tuple[str, str]] = set()
    for index, record in enumerate(records):
        game_id = _record_game_id(record)
        if game_id not in accepted_games:
            continue
        if record.get("kind") == "solve":
            # Solve outcomes remain summarized in the manifest as operational
            # telemetry; they are not state-provenance records in the verified corpus.
            continue
        promoted = promoted_by_index.get(index)
        source = snapshot_sources_by_index.get(index)
        if promoted is None or source is None:
            output.append(record)
            continue
        (
            split,
            sequence,
            pre_state,
            pre_hash,
            post_state,
            post_hash,
            source_observation,
        ) = source
        pre_key = (game_id, str(pre_state.get("state_id") or ""))
        if pre_key not in emitted_snapshots:
            output.append(
                _decision_snapshot_record(
                    game_id=game_id,
                    split=split,
                    state=pre_state,
                    normalized_state_hash=pre_hash,
                    action_sequence=sequence,
                    state_role="pre_action",
                    source_observation=source_observation,
                )
            )
            emitted_snapshots.add(pre_key)
        output.append(promoted)
        post_key = (game_id, str(post_state.get("state_id") or ""))
        if post_key not in emitted_snapshots:
            output.append(
                _decision_snapshot_record(
                    game_id=game_id,
                    split=split,
                    state=post_state,
                    normalized_state_hash=post_hash,
                    action_sequence=sequence,
                    state_role="post_action",
                    source_observation=source_observation,
                )
            )
            emitted_snapshots.add(post_key)
    return output, {
        "source_game_count": len(by_game),
        "accepted_game_count": len(accepted_games),
        "rejected_game_count": len(by_game) - len(accepted_games),
        "verified_transition_count": verified_transition_count,
        "decision_snapshot_count": len(emitted_snapshots),
        "source_solve_status_counts": dict(sorted(source_solve_status_counts.items())),
        "rejection_reasons": dict(sorted(rejected.items())),
    }


def promote_trajectory_file(
    input_path: str | Path,
    output_path: str | Path,
    manifest_path: str | Path,
    *,
    policy_path: str | Path | None = None,
) -> dict[str, Any]:
    """Create a new, hash-bound verified corpus; never mutate the production log."""

    source = Path(input_path).resolve()
    output = Path(output_path).resolve()
    manifest_output = Path(manifest_path).resolve()
    if source in {output, manifest_output} or output == manifest_output:
        raise TrajectoryVerificationError(
            "input, verified output, and manifest must be three different files"
        )
    try:
        source_bytes = source.read_bytes()
    except OSError as exc:
        raise TrajectoryVerificationError("trajectory input could not be read") from exc

    with tempfile.TemporaryDirectory(prefix="metacompanion-verify-") as directory:
        snapshot = Path(directory) / "source.jsonl"
        snapshot.write_bytes(source_bytes)
        source_audit = audit_trajectory_file(snapshot, policy_path=policy_path)
        if source_audit.get("contract_passed") is not True:
            raise TrajectoryVerificationError(
                "trajectory input failed the production privacy or capture contract"
            )
        records = load_records(snapshot)
        promotion: dict[str, Any] | None = None
        if source_audit.get("training_ready") is True:
            verified_records = records
        else:
            verified_records, promotion = _power_verified_records(records)
            if not verified_records:
                raise TrajectoryVerificationError(
                    "trajectory input contains no complete simulator-verified Power games"
                )
        verified_bytes = _jsonl_bytes(verified_records)
        verified_snapshot = Path(directory) / "verified.jsonl"
        verified_snapshot.write_bytes(verified_bytes)
        verified_audit = audit_trajectory_file(
            verified_snapshot,
            policy_path=policy_path,
        )
        if verified_audit.get("training_ready") is not True:
            raise TrajectoryVerificationError(
                "sanitized verified corpus did not pass a fresh audit"
            )

    verified_transitions = _normalized_verified_transitions(
        verified_audit.get("verified_transitions")
    )
    if not verified_transitions:
        raise TrajectoryVerificationError("trajectory audit verified no transitions")
    verified_sha256 = _sha256_bytes(verified_bytes)
    manifest: dict[str, Any] = {
        "schema": VERIFICATION_MANIFEST_SCHEMA_ID,
        "trajectory_schema": TRAJECTORY_SCHEMA_ID,
        "training_log_schema": TRAINING_LOG_SCHEMA_ID,
        "training_ready": True,
        "source": {
            "name": source.name,
            "sha256": _sha256_bytes(source_bytes),
        },
        "verified_dataset": {
            "name": output.name,
            "sha256": verified_sha256,
            "record_count": verified_audit.get("metrics", {}).get("record_count", 0),
        },
        "audit": {
            "schema": verified_audit.get("schema"),
            "input_sha256": verified_sha256,
            "policy": verified_audit.get("policy"),
            "metrics": verified_audit.get("metrics"),
        },
        "simulator": _simulator_provenance(),
        "verified_transitions": verified_transitions,
        "caveat": (
            "This manifest proves only a versioned privacy, join, exact-action, and "
            "replay contract. It does not prove optimal play or an unbiased policy label."
        ),
    }
    if promotion is not None:
        manifest["power_identity_promotion"] = promotion
    _atomic_write_bytes(verified_bytes, output)
    _atomic_write_json(manifest, manifest_output)
    return manifest
