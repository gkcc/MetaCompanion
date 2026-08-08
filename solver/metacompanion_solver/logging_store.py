from __future__ import annotations

import hashlib
import json
import os
import re
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

from .schemas import (
    TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
    TRANSITION_CANDIDATE_COMPLETENESS,
    TRANSITION_CANDIDATE_ENVELOPE_FIELDS,
    TRANSITION_CANDIDATE_STATUS,
    TRANSITION_CANDIDATE_VERIFICATION,
    POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
    POWER_ACTION_IDENTITY_COMPLETENESS,
    POWER_ACTION_IDENTITY_STATUS,
    POWER_ACTION_IDENTITY_CHOICE_STATUS,
    POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
    Observation,
    SearchResult,
    SolveRequest,
    is_unverified_transition_candidate,
    is_power_action_identity_candidate,
    redact_hidden_entities,
    validate_result_metadata,
)
from .errors import ResultObservationConflictError


_DROP_KEYS = {
    "account_id",
    "accountid",
    "battle_tag",
    "battletag",
    "opponent_name",
    "player_name",
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
    "snapshot_state_hash",
    "current_deck",
    "deck_id",
    "deckid",
    "hearthstone_deck_id",
    "deck_name",
    "deckname",
}
_HASH_KEYS = {"game_id", "match_id"}
_ANONYMOUS_ID = re.compile(r"^anon-[0-9a-f]{16}$")
TRAJECTORY_SCHEMA_ID = "trajectory-readiness-v1"
TRAINING_LOG_SCHEMA_ID = "advisor-training-log-v2"


@dataclass(frozen=True)
class ObservationAppendResult:
    kind: str
    state_id: str
    logged: bool
    duplicate: bool = False
    result_id: str = ""
    game_id: str = ""
    result: str = ""


def _anonymous(value: Any) -> str:
    text = str(value)
    if _ANONYMOUS_ID.fullmatch(text):
        return text
    return "anon-" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def deterministic_game_split(game_id: str) -> str:
    """Assign an already-anonymized game to one stable, game-level data split."""

    bucket = int(hashlib.sha256(game_id.encode("utf-8")).hexdigest()[:8], 16) % 100
    if bucket < 80:
        return "train"
    if bucket < 90:
        return "validation"
    return "test"


def _sanitize_identity(value: Any) -> Any:
    if isinstance(value, Mapping):
        result: dict[str, Any] = {}
        for key, item in value.items():
            normalized = str(key).lower()
            if normalized in _DROP_KEYS:
                continue
            if normalized in _HASH_KEYS:
                result[str(key)] = "" if item is None or item == "" else _anonymous(item)
            else:
                result[str(key)] = _sanitize_identity(item)
        return result
    if isinstance(value, list):
        return [_sanitize_identity(item) for item in value]
    if isinstance(value, tuple):
        return [_sanitize_identity(item) for item in value]
    return value


def sanitize_for_training(value: Any) -> Any:
    """Remove identity and exact hidden-card data before any training-data write."""

    return _sanitize_identity(redact_hidden_entities(value))


def _normalized_state_payload(value: Mapping[str, Any]) -> dict[str, Any]:
    payload = sanitize_for_training(dict(value))
    if not isinstance(payload, dict):  # Defensive; callers always pass a state object.
        raise TypeError("normalized state must be an object")
    return payload


def _canonical_sha256(value: Mapping[str, Any]) -> str:
    return hashlib.sha256(
        json.dumps(
            value,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    ).hexdigest()


class JsonlTrainingLogger:
    def __init__(self, path: str | Path | None):
        self.path = Path(path) if path else None
        # Cache mutations and their matching JSONL append must be one ordered
        # transaction.  ``append`` also takes this lock, so it must be re-entrant.
        self._lock = threading.RLock()
        self._state_cache: dict[
            tuple[str, str], tuple[dict[str, Any], str, int]
        ] = {}
        # Loaded lazily so ordinary action observations retain their existing cost and behavior.
        self._terminal_results_loaded = False
        self._terminal_index_error = False
        self._terminal_result_ids: dict[str, str] = {}
        self.last_error: str = ""

    @property
    def enabled(self) -> bool:
        return self.path is not None

    @property
    def healthy(self) -> bool:
        if self.path is None:
            return True
        with self._lock:
            # Keep ordinary action appends cheap, but let health discover damage in
            # an existing corpus before the first terminal result is received.
            if not self._terminal_results_loaded and (
                not self.last_error or self._terminal_index_error
            ):
                try:
                    self._load_terminal_result_index_locked()
                except Exception:
                    pass
            return not bool(self.last_error)

    def append(self, record: Mapping[str, Any]) -> bool:
        if self.path is None:
            return False
        payload = sanitize_for_training(dict(record))
        line = json.dumps(payload, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        with self._lock:
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with self.path.open("a", encoding="utf-8", newline="\n") as handle:
                    handle.write(line)
                    handle.write("\n")
                self.last_error = ""
                return True
            except OSError as exc:
                self.last_error = type(exc).__name__
                return False

    def append_solve(self, request: SolveRequest, result: SearchResult) -> bool:
        with self._lock:
            return self._append_solve_locked(request, result)

    def _append_solve_locked(
        self, request: SolveRequest, result: SearchResult
    ) -> bool:
        raw_game_id = request.state.metadata.get("game_id", "")
        game_id = "" if raw_game_id in (None, "") else _anonymous(raw_game_id)
        decision_id = request.metadata.get("decision_id") or request.state.state_id
        solve_stage = str(request.metadata.get("solve_stage") or "legacy").strip().lower()
        raw_snapshot_hash = str(request.state.metadata.get("snapshot_state_hash", ""))
        snapshot_sequence_raw = request.state.metadata.get("snapshot_sequence", 0)
        snapshot_sequence = (
            snapshot_sequence_raw
            if isinstance(snapshot_sequence_raw, int)
            and not isinstance(snapshot_sequence_raw, bool)
            else 0
        )
        normalized_state = _normalized_state_payload(request.state.to_dict())
        if game_id:
            with self._lock:
                self._state_cache[(game_id, request.state.state_id)] = (
                    normalized_state,
                    raw_snapshot_hash,
                    snapshot_sequence,
                )
        return self.append(
            {
                "kind": "solve",
                "log_schema": TRAINING_LOG_SCHEMA_ID,
                "trajectory": {
                    "schema": str(
                        request.metadata.get("trajectory_schema") or TRAJECTORY_SCHEMA_ID
                    ),
                    "game_id": game_id,
                    "split": deterministic_game_split(game_id) if game_id else "",
                    "decision_id": str(decision_id),
                    "state_id": request.state.state_id,
                    "solve_stage": solve_stage,
                    "snapshot_sequence": request.metadata.get(
                        "snapshot_sequence",
                        request.state.metadata.get("snapshot_sequence"),
                    ),
                    "capture_contract": str(
                        request.metadata.get("capture_contract") or "unknown"
                    ),
                    "patch": request.state.patch,
                    "mode": request.state.mode,
                    "planner_model": str(
                        result.coverage.get("planner_model", "unknown")
                    ),
                    "rules_model": str(result.coverage.get("rules_model", "unknown")),
                    "adapter": str(request.state.metadata.get("adapter", "native-v1")),
                    "raw_snapshot_hash": raw_snapshot_hash,
                    "normalized_state_hash": _canonical_sha256(normalized_state),
                },
                "request": request.to_dict(),
                "result": result.to_dict(),
            }
        )

    def append_observation(self, observation: Observation) -> bool:
        return self.append_observation_with_ack(observation).logged

    def append_observation_with_ack(
        self, observation: Observation
    ) -> ObservationAppendResult:
        with self._lock:
            if observation.kind == "result":
                validate_result_metadata(observation.metadata)
            record = self._build_observation_record_locked(observation)
            if observation.kind == "result":
                return self._append_terminal_result_locked(record)
            return ObservationAppendResult(
                kind=observation.kind,
                state_id=observation.state_id,
                logged=self.append(record),
            )

    def _build_observation_record_locked(
        self, observation: Observation
    ) -> dict[str, Any]:
        metadata = dict(observation.metadata)
        game_id = "" if not observation.game_id else _anonymous(observation.game_id)
        observation_payload = observation.to_dict()
        observation_payload["game_id"] = game_id
        # Candidate post-state evidence is producer evidence, never an eligibility
        # decision.  Clamp the fail-closed markers even for internal callers that
        # constructed Observation directly instead of using from_dict().
        candidate = is_unverified_transition_candidate(metadata)
        if candidate:
            power_identity = is_power_action_identity_candidate(metadata)
            if power_identity:
                metadata.update(
                    {
                        "capture_contract": POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
                        "transition_status": TRANSITION_CANDIDATE_STATUS,
                        "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
                        "completeness": POWER_ACTION_IDENTITY_COMPLETENESS,
                        "action_identity_status": POWER_ACTION_IDENTITY_STATUS,
                        "choice_status": POWER_ACTION_IDENTITY_CHOICE_STATUS,
                        "simulator_status": POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
                        "training_eligible": False,
                    }
                )
            else:
                metadata.update(
                    {
                        "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
                        "transition_status": TRANSITION_CANDIDATE_STATUS,
                        "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
                        "completeness": TRANSITION_CANDIDATE_COMPLETENESS,
                        "training_eligible": False,
                    }
                )
            candidate_failure = ""
            cached_pre = None
            if observation.pre_state is not None:
                cached_pre = (
                    _normalized_state_payload(observation.pre_state.to_dict()),
                    str(observation.pre_state.metadata.get("snapshot_state_hash", "")),
                    observation.pre_state.metadata.get("snapshot_sequence", 0),
                )
            else:
                with self._lock:
                    cached_pre = self._state_cache.get((game_id, observation.state_id))
            if cached_pre is None:
                candidate_failure = "missing_normalized_pre_state"
            elif observation.post_state is None:
                candidate_failure = "missing_normalized_post_state"
            else:
                pre_state, raw_pre_hash, pre_sequence = cached_pre
                if isinstance(pre_sequence, bool) or not isinstance(pre_sequence, int):
                    pre_sequence = 0
                raw_post_hash = str(
                    observation.post_state.metadata.get("snapshot_state_hash", "")
                )
                post_sequence_raw = observation.post_state.metadata.get(
                    "snapshot_sequence", 0
                )
                post_sequence = (
                    post_sequence_raw
                    if isinstance(post_sequence_raw, int)
                    and not isinstance(post_sequence_raw, bool)
                    else 0
                )
                if str(metadata.get("raw_pre_snapshot_hash") or "") != raw_pre_hash:
                    candidate_failure = "raw_pre_snapshot_hash_mismatch"
                elif str(metadata.get("raw_post_snapshot_hash") or "") != raw_post_hash:
                    candidate_failure = "raw_post_snapshot_hash_mismatch"
                elif str(metadata.get("pre_snapshot_sequence") or "") != str(
                    pre_sequence
                ):
                    candidate_failure = "pre_snapshot_sequence_mismatch"
                elif str(metadata.get("post_snapshot_sequence") or "") != str(
                    post_sequence
                ):
                    candidate_failure = "post_snapshot_sequence_mismatch"
                elif observation.post_state.state_id != str(
                    metadata.get("post_state_id") or ""
                ):
                    candidate_failure = "post_state_id_mismatch"
                else:
                    post_state = _normalized_state_payload(
                        observation.post_state.to_dict()
                    )
                    metadata["pre_state_hash"] = _canonical_sha256(pre_state)
                    metadata["post_state_hash"] = _canonical_sha256(post_state)
                    observation_payload["pre_state"] = pre_state
                    observation_payload["post_state"] = post_state
                    with self._lock:
                        self._state_cache[(game_id, observation.post_state.state_id)] = (
                            post_state,
                            raw_post_hash,
                            post_sequence,
                        )
            if candidate_failure:
                metadata.update(
                    {
                        "capture_contract": "partial_hdt_gameevents_v1",
                        "transition_status": "not_replayable",
                        "transition_verification": "candidate_rejected",
                        "completeness": TRANSITION_CANDIDATE_COMPLETENESS,
                        "training_eligible": False,
                        "candidate_failure_reason": candidate_failure,
                    }
                )
                metadata.pop("pre_state_hash", None)
                metadata.pop("post_state_hash", None)
        observation_payload["metadata"] = metadata
        trajectory = {
            "schema": str(metadata.get("trajectory_schema") or TRAJECTORY_SCHEMA_ID),
            "game_id": game_id,
            "split": deterministic_game_split(game_id) if game_id else "",
            "decision_id": str(
                metadata.get("decision_id")
                or metadata.get("pre_state_id")
                or observation.state_id
            ),
            "state_id": observation.state_id,
            "observation_kind": observation.kind,
            "action_sequence": metadata.get("action_sequence"),
            "completeness": metadata.get("completeness"),
            "capture_contract": metadata.get("capture_contract"),
            "transition_status": metadata.get("transition_status"),
        }
        for field in TRANSITION_CANDIDATE_ENVELOPE_FIELDS:
            if field in metadata:
                trajectory[field] = metadata[field]
        return {
            "kind": "observation",
            "log_schema": TRAINING_LOG_SCHEMA_ID,
            "trajectory": trajectory,
            "observation": observation_payload,
        }

    def _append_terminal_result_locked(
        self,
        record: dict[str, Any],
    ) -> ObservationAppendResult:
        payload = sanitize_for_training(record["observation"])
        if not isinstance(payload, dict):
            raise TypeError("terminal observation must be an object")
        game_id = str(payload.get("game_id") or "")
        state_id = str(payload.get("state_id") or "")
        result = str(payload.get("result") or "")
        result_id = "result-" + _canonical_sha256(payload)
        key = self._terminal_result_key(game_id, state_id)
        if self.path is None:
            return ObservationAppendResult(
                kind="result",
                state_id=state_id,
                logged=False,
                result_id=result_id,
                game_id=game_id,
                result=result,
            )
        self._load_terminal_result_index_locked()
        existing = self._terminal_result_ids.get(key)
        if existing is not None:
            if existing != result_id:
                raise ResultObservationConflictError(
                    "terminal result conflicts with durable content for this game"
                )
            return ObservationAppendResult(
                kind="result",
                state_id=state_id,
                logged=False,
                duplicate=True,
                result_id=result_id,
                game_id=game_id,
                result=result,
            )
        logged = self._append_terminal_record_durable_locked(record)
        if logged:
            self._terminal_result_ids[key] = result_id
        else:
            # A complete write followed by a failed fsync is ambiguous.  Force the
            # next retry to rebuild the index from disk instead of appending again.
            self._terminal_results_loaded = False
            self._terminal_index_error = False
            self._terminal_result_ids.clear()
        return ObservationAppendResult(
            kind="result",
            state_id=state_id,
            logged=logged,
            result_id=result_id,
            game_id=game_id,
            result=result,
        )

    @staticmethod
    def _terminal_result_key(game_id: str, state_id: str) -> str:
        return "game:" + game_id if game_id else "state:" + state_id

    def _append_terminal_record_durable_locked(
        self, record: Mapping[str, Any]
    ) -> bool:
        if self.path is None:
            return False
        payload = sanitize_for_training(dict(record))
        line = json.dumps(
            payload, ensure_ascii=False, separators=(",", ":"), sort_keys=True
        )
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            with self.path.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(line)
                handle.write("\n")
                self._durable_terminal_barrier(handle)
            self.last_error = ""
            return True
        except OSError as exc:
            self.last_error = type(exc).__name__
            return False

    @staticmethod
    def _durable_terminal_barrier(handle: Any) -> None:
        handle.flush()
        os.fsync(handle.fileno())

    def _load_terminal_result_index_locked(self) -> None:
        if self._terminal_results_loaded:
            return
        terminal_result_ids: dict[str, str] = {}
        try:
            self._repair_torn_training_tail_locked()
            if self.path is not None and self.path.exists():
                with self.path.open("r", encoding="utf-8") as handle:
                    for line_number, line in enumerate(handle, 1):
                        if not line.strip():
                            continue
                        record = json.loads(line)
                        if not isinstance(record, dict):
                            raise ValueError(
                                f"training log line {line_number} must be an object"
                            )
                        if record.get("kind") != "observation":
                            continue
                        payload = record.get("observation")
                        if (
                            not isinstance(payload, Mapping)
                            or payload.get("kind") != "result"
                        ):
                            continue
                        content = sanitize_for_training(dict(payload))
                        if not isinstance(content, dict):
                            continue
                        game_id = str(content.get("game_id") or "")
                        state_id = str(content.get("state_id") or "")
                        key = self._terminal_result_key(game_id, state_id)
                        result_id = "result-" + _canonical_sha256(content)
                        existing = terminal_result_ids.get(key)
                        if existing is not None and existing != result_id:
                            raise ResultObservationConflictError(
                                "conflicting terminal results already exist "
                                f"at line {line_number}"
                            )
                        terminal_result_ids[key] = result_id
            # A rebuilt index is a fresh durability trust boundary.  A new worker
            # cannot know whether an earlier process wrote this complete line but
            # failed fsync, so every disk rebuild must sync before duplicate ACKs.
            if self.path is not None and self.path.exists():
                with self.path.open("r+b") as handle:
                    handle.flush()
                    os.fsync(handle.fileno())
        except Exception as exc:
            self._terminal_results_loaded = False
            self._terminal_index_error = True
            self._terminal_result_ids.clear()
            self.last_error = (
                "ResultObservationConflict"
                if isinstance(exc, ResultObservationConflictError)
                else "TrainingLogIndexLoadFailed"
            )
            raise
        self._terminal_result_ids = terminal_result_ids
        self._terminal_results_loaded = True
        self._terminal_index_error = False
        self.last_error = ""

    def _repair_torn_training_tail_locked(self) -> bool:
        if self.path is None or not self.path.exists():
            return False
        with self.path.open("rb") as source:
            source.seek(0, os.SEEK_END)
            original_size = source.tell()
            if original_size == 0:
                return False
            source.seek(-1, os.SEEK_END)
            if source.read(1) == b"\n":
                return False

            cutoff = 0
            cursor = original_size
            while cursor > 0:
                chunk_size = min(cursor, 8192)
                start = cursor - chunk_size
                source.seek(start)
                chunk = source.read(chunk_size)
                position = chunk.rfind(b"\n")
                if position >= 0:
                    cutoff = start + position + 1
                    break
                cursor = start
            source.seek(cutoff)
            fragment = source.read()

        try:
            complete_record = json.loads(fragment)
        except (UnicodeDecodeError, json.JSONDecodeError):
            complete_record = None
        if isinstance(complete_record, dict):
            # The JSON object is complete and only its JSONL delimiter is missing.
            # Preserve it in place so a restart can index and de-duplicate it.
            with self.path.open("r+b") as active:
                active.seek(0, os.SEEK_END)
                if active.tell() != original_size:
                    raise OSError("training log changed during tail recovery")
                active.seek(cutoff)
                if active.read() != fragment:
                    raise OSError("training tail changed during recovery")
                active.seek(0, os.SEEK_END)
                active.write(b"\n")
                active.flush()
                os.fsync(active.fileno())
            return True

        self._archive_torn_fragment_locked(fragment)

        # With no complete newline, the entire file is an unverifiable fragment:
        # archive it first, then truncate the active corpus to zero.  Otherwise
        # preserve every byte through the last complete JSONL record.
        with self.path.open("r+b") as active:
            active.seek(0, os.SEEK_END)
            if active.tell() != original_size:
                raise OSError("training log changed during tail recovery")
            active.seek(cutoff)
            if active.read() != fragment:
                raise OSError("training tail changed during recovery")
            active.truncate(cutoff)
            active.flush()
            os.fsync(active.fileno())
        return True

    def _archive_torn_fragment_locked(self, fragment: bytes) -> Path:
        if self.path is None:  # Defensive; caller already checked.
            raise OSError("training log is disabled")
        digest = hashlib.sha256(fragment).hexdigest()
        archive = self.path.with_name(
            f"{self.path.name}.torn-tail.{digest}.fragment"
        )
        if archive.exists():
            if archive.read_bytes() != fragment:
                raise OSError("training tail archive content mismatch")
            return archive

        temporary = archive.with_name(
            f".{archive.name}.{os.getpid()}.{threading.get_ident()}.tmp"
        )
        try:
            with temporary.open("xb") as handle:
                handle.write(fragment)
                handle.flush()
                os.fsync(handle.fileno())
            temporary.chmod(0o444)
            os.replace(temporary, archive)
        except Exception:
            try:
                temporary.chmod(0o666)
                temporary.unlink(missing_ok=True)
            except OSError:
                pass
            if archive.exists() and archive.read_bytes() == fragment:
                return archive
            raise
        return archive
