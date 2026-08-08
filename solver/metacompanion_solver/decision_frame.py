from __future__ import annotations

import copy
import hashlib
import json
import re
from collections import Counter
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Mapping, Sequence

from .behavior import BehaviorRecord, BehaviorValidationError, public_behavior_state
from .errors import SchemaError
from .schemas import GameState


DECISION_FRAME_SCHEMA_ID = "advisor-decision-frame-v1"
DECISION_FRAME_CAPTURE_CONTRACT = "hdt_replay_options_send_option_v1"
DECISION_FRAME_CANDIDATE_SET_CONTRACT = "hdt_complete_main_action_options_v1"
DECISION_FRAME_SELECTION_CONTRACT = "hdt_send_option_root_exact_v1"
DECISION_FRAME_TRANSITION_CONTRACT = "hdt_replay_public_power_v1"

_ACTION_KINDS = {
    "play_card",
    "attack",
    "hero_power",
    "location_activate",
    "end_turn",
}
_TARGET_EVIDENCE = {
    "hdt_error_none",
    "hdt_no_legal_target",
    "not_applicable",
}
_POSITION_EVIDENCE = {"core_board_slots_v1", "not_applicable"}
_ACTION_KEYS = {
    "kind",
    "source_entity_id",
    "target_entity_id",
    "card_id",
    "board_position",
}
_CANDIDATE_INPUT_KEYS = {
    "option_id",
    "action",
    "target_evidence",
    "position_evidence",
}
_CANDIDATE_KEYS = _CANDIDATE_INPUT_KEYS | {"candidate_id"}
_CONTENT_KEYS = {
    "schema",
    "game_id",
    "decision_sequence",
    "observed_at_utc",
    "client_build",
    "mode",
    "actor_side",
    "actor_player_id",
    "selected_behavior_id",
    "hdt_frame_id",
    "capture_contract",
    "candidate_set_contract",
    "selection_contract",
    "transition_contract",
    "pre_state",
    "post_state",
    "selected_candidate_id",
    "selected_action",
    "legal_candidates",
    "candidate_set_complete",
    "selected_action_observed",
    "selected_action_in_candidates",
    "imitation_training_eligible",
    "optimality_verified",
    "rl_training_eligible",
    "outcome_used_as_action_optimality",
}
_TOP_LEVEL_KEYS = _CONTENT_KEYS | {"decision_frame_id", "content_sha256"}
_SAFE_TOKEN = re.compile(r"^[A-Za-z0-9_.:-]+$")
_ANONYMOUS_GAME_ID = re.compile(r"^anon-[0-9a-f]{16}$")
_BEHAVIOR_ID = re.compile(r"^behavior-[0-9a-f]{64}$")
_FRAME_ID = re.compile(r"^decision-frame-[0-9a-f]{64}$")
_HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_MAX_CANDIDATES = 512
_MAX_CORPUS_BYTES = 512 * 1024 * 1024
_MAX_LINE_BYTES = 16 * 1024 * 1024


class DecisionFrameValidationError(ValueError):
    def __init__(self, code: str, path: str = "decision_frame"):
        super().__init__(f"{path}: {code}")
        self.code = code
        self.path = path


def _mapping(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise DecisionFrameValidationError("must_be_object", path)
    return value


def _sequence(value: Any, path: str) -> Sequence[Any]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise DecisionFrameValidationError("must_be_array", path)
    return value


def _strict_keys(
    value: Mapping[str, Any], allowed: set[str], required: set[str], path: str
) -> None:
    keys = {str(key) for key in value}
    unknown = sorted(keys - allowed)
    if unknown:
        raise DecisionFrameValidationError("unknown_field:" + unknown[0], path)
    missing = sorted(required - keys)
    if missing:
        raise DecisionFrameValidationError("missing_field:" + missing[0], path)


def _text(value: Any, path: str, *, allow_empty: bool = False, limit: int = 256) -> str:
    if not isinstance(value, str):
        raise DecisionFrameValidationError("must_be_string", path)
    result = value.strip()
    if (not result and not allow_empty) or len(result) > limit:
        raise DecisionFrameValidationError("invalid_length", path)
    return result


def _token(value: Any, path: str, *, allow_empty: bool = False, limit: int = 256) -> str:
    result = _text(value, path, allow_empty=allow_empty, limit=limit)
    if result and _SAFE_TOKEN.fullmatch(result) is None:
        raise DecisionFrameValidationError("unsafe_token", path)
    return result


def _integer(value: Any, path: str, *, minimum: int = 0, maximum: int | None = None) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise DecisionFrameValidationError("must_be_integer", path)
    if maximum is not None and value > maximum:
        raise DecisionFrameValidationError("integer_out_of_range", path)
    return value


def _false(value: Any, path: str) -> bool:
    if value is not False:
        raise DecisionFrameValidationError("must_be_false", path)
    return False


def _true(value: Any, path: str) -> bool:
    if value is not True:
        raise DecisionFrameValidationError("must_be_true", path)
    return True


def _timestamp(value: Any, path: str) -> str:
    result = _text(value, path, limit=64)
    try:
        parsed = datetime.fromisoformat(
            result[:-1] + "+00:00" if result.endswith("Z") else result
        )
    except ValueError as exc:
        raise DecisionFrameValidationError("invalid_rfc3339_timestamp", path) from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise DecisionFrameValidationError("timestamp_requires_offset", path)
    return result


def _canonical_json(value: Mapping[str, Any]) -> str:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )


def _content_digest(value: Mapping[str, Any]) -> str:
    return hashlib.sha256(_canonical_json(value).encode("utf-8")).hexdigest()


def _state_entities(
    state: Mapping[str, Any],
) -> dict[str, tuple[str, str, Mapping[str, Any]]]:
    result: dict[str, tuple[str, str, Mapping[str, Any]]] = {}
    for role in ("friendly", "opponent"):
        player = state[role]
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping) and entity.get("entity_id"):
                result[str(entity["entity_id"])] = (role, zone, entity)
        for zone in ("hand", "board"):
            for entity in player.get(zone, []):
                entity_id = str(entity.get("entity_id") or "")
                if entity_id:
                    result[entity_id] = (role, zone, entity)
    return result


def _action(value: Any, path: str, pre_state: Mapping[str, Any]) -> dict[str, Any]:
    raw = _mapping(value, path)
    _strict_keys(raw, _ACTION_KEYS, _ACTION_KEYS, path)
    kind = _text(raw.get("kind"), f"{path}.kind").lower()
    if kind not in _ACTION_KINDS:
        raise DecisionFrameValidationError("invalid_value", f"{path}.kind")
    source = _token(
        raw.get("source_entity_id"),
        f"{path}.source_entity_id",
        allow_empty=True,
        limit=32,
    )
    target = _token(
        raw.get("target_entity_id"),
        f"{path}.target_entity_id",
        allow_empty=True,
        limit=32,
    )
    card_id = _token(
        raw.get("card_id"), f"{path}.card_id", allow_empty=True, limit=128
    )
    position = _integer(
        raw.get("board_position"), f"{path}.board_position", maximum=7
    )
    entities = _state_entities(pre_state)
    if kind == "end_turn":
        if source or target or card_id or position:
            raise DecisionFrameValidationError("end_turn_must_be_empty", path)
        return {
            "kind": kind,
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": "",
            "board_position": 0,
        }
    located = entities.get(source)
    if located is None:
        raise DecisionFrameValidationError("source_not_in_pre_state", path)
    role, zone, entity = located
    if role != "friendly":
        raise DecisionFrameValidationError("source_not_friendly", path)
    if not card_id or card_id != str(entity.get("card_id") or ""):
        raise DecisionFrameValidationError("source_card_id_mismatch", path)
    target_entity = entities.get(target) if target else None
    if target and target_entity is None:
        raise DecisionFrameValidationError("target_not_in_pre_state", path)
    card_type = str(entity.get("card_type") or "UNKNOWN").upper()
    if kind == "play_card":
        if zone != "hand":
            raise DecisionFrameValidationError("play_source_not_in_hand", path)
        placement = card_type in {"MINION", "LOCATION"}
        if placement:
            maximum = min(7, len(pre_state["friendly"].get("board", [])) + 1)
            if not 1 <= position <= maximum:
                raise DecisionFrameValidationError("board_position_invalid", path)
        elif position != 0:
            raise DecisionFrameValidationError("board_position_not_applicable", path)
    elif kind == "attack":
        if zone not in {"hero", "board"} or card_type not in {"HERO", "MINION"}:
            raise DecisionFrameValidationError("attack_source_not_character", path)
        if target_entity is None:
            raise DecisionFrameValidationError("attack_target_required", path)
        target_role, target_zone, _ = target_entity
        if target_role != "opponent" or target_zone not in {"hero", "board"}:
            raise DecisionFrameValidationError("attack_target_not_enemy_character", path)
        if position != 0:
            raise DecisionFrameValidationError("board_position_not_applicable", path)
    elif kind == "hero_power":
        if zone != "hero_power" or card_type != "HERO_POWER":
            raise DecisionFrameValidationError("hero_power_source_mismatch", path)
        if position != 0:
            raise DecisionFrameValidationError("board_position_not_applicable", path)
    elif kind == "location_activate":
        if zone != "board" or card_type != "LOCATION":
            raise DecisionFrameValidationError("location_source_mismatch", path)
        if position != 0:
            raise DecisionFrameValidationError("board_position_not_applicable", path)
    return {
        "kind": kind,
        "source_entity_id": source,
        "target_entity_id": target,
        "card_id": card_id,
        "board_position": position,
    }


def decision_candidate_id(option_id: int, action: Mapping[str, Any]) -> str:
    material = json.dumps(
        {"option_id": option_id, "action": dict(action)},
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return f"candidate-{option_id}-{hashlib.sha256(material).hexdigest()[:24]}"


def _candidate(
    value: Any,
    path: str,
    pre_state: Mapping[str, Any],
    *,
    input_form: bool,
) -> dict[str, Any]:
    raw = _mapping(value, path)
    keys = _CANDIDATE_INPUT_KEYS if input_form else _CANDIDATE_KEYS
    _strict_keys(raw, keys, keys, path)
    option_id = _integer(raw.get("option_id"), f"{path}.option_id")
    action = _action(raw.get("action"), f"{path}.action", pre_state)
    target_evidence = _text(
        raw.get("target_evidence"), f"{path}.target_evidence", limit=64
    ).lower()
    if target_evidence not in _TARGET_EVIDENCE:
        raise DecisionFrameValidationError("invalid_value", f"{path}.target_evidence")
    position_evidence = _text(
        raw.get("position_evidence"), f"{path}.position_evidence", limit=64
    ).lower()
    if position_evidence not in _POSITION_EVIDENCE:
        raise DecisionFrameValidationError("invalid_value", f"{path}.position_evidence")
    if action["kind"] == "end_turn":
        if option_id != 0 or target_evidence != "not_applicable":
            raise DecisionFrameValidationError("end_turn_evidence_mismatch", path)
    elif option_id <= 0:
        raise DecisionFrameValidationError("power_option_id_required", path)
    elif bool(action["target_entity_id"]) != (target_evidence == "hdt_error_none"):
        raise DecisionFrameValidationError("target_evidence_mismatch", path)
    expected_position_evidence = (
        "core_board_slots_v1" if action["board_position"] > 0 else "not_applicable"
    )
    if position_evidence != expected_position_evidence:
        raise DecisionFrameValidationError("position_evidence_mismatch", path)
    expected_id = decision_candidate_id(option_id, action)
    if not input_form:
        candidate_id = _token(raw.get("candidate_id"), f"{path}.candidate_id", limit=96)
        if candidate_id != expected_id:
            raise DecisionFrameValidationError("candidate_id_mismatch", path)
    return {
        "candidate_id": expected_id,
        "option_id": option_id,
        "action": action,
        "target_evidence": target_evidence,
        "position_evidence": position_evidence,
    }


def _normalized_content(value: Mapping[str, Any], *, strict: bool) -> dict[str, Any]:
    if strict:
        _strict_keys(value, _CONTENT_KEYS, _CONTENT_KEYS, "decision_frame")
    schema = _text(value.get("schema"), "decision_frame.schema")
    if schema != DECISION_FRAME_SCHEMA_ID:
        raise DecisionFrameValidationError("wrong_schema", "decision_frame.schema")
    game_id = _text(value.get("game_id"), "decision_frame.game_id", limit=32)
    if _ANONYMOUS_GAME_ID.fullmatch(game_id) is None:
        raise DecisionFrameValidationError("game_id_not_anonymous", "decision_frame.game_id")
    behavior_id = _text(
        value.get("selected_behavior_id"), "decision_frame.selected_behavior_id", limit=80
    )
    if _BEHAVIOR_ID.fullmatch(behavior_id) is None:
        raise DecisionFrameValidationError(
            "selected_behavior_id_invalid", "decision_frame.selected_behavior_id"
        )
    try:
        pre_state = public_behavior_state(value.get("pre_state"), strict=True)
        post_state = public_behavior_state(value.get("post_state"), strict=True)
        GameState.from_dict(pre_state, "decision_frame.pre_state")
        GameState.from_dict(post_state, "decision_frame.post_state")
    except (BehaviorValidationError, SchemaError) as exc:
        raise DecisionFrameValidationError("state_contract_invalid", "decision_frame") from exc
    if pre_state["active_player_id"] != "friendly":
        raise DecisionFrameValidationError("actor_not_active", "decision_frame.pre_state")
    client_build = _token(
        value.get("client_build"), "decision_frame.client_build", limit=32
    )
    mode = _token(value.get("mode"), "decision_frame.mode", limit=64)
    if pre_state["patch"] != client_build or post_state["patch"] != client_build:
        raise DecisionFrameValidationError("build_state_mismatch", "decision_frame.client_build")
    if pre_state["mode"] != mode or post_state["mode"] != mode:
        raise DecisionFrameValidationError("mode_state_mismatch", "decision_frame.mode")
    raw_candidates = _sequence(value.get("legal_candidates"), "decision_frame.legal_candidates")
    if not 1 <= len(raw_candidates) <= _MAX_CANDIDATES:
        raise DecisionFrameValidationError(
            "candidate_count_invalid", "decision_frame.legal_candidates"
        )
    candidates = [
        _candidate(
            item,
            f"decision_frame.legal_candidates[{index}]",
            pre_state,
            input_form=not strict,
        )
        for index, item in enumerate(raw_candidates)
    ]
    candidates.sort(key=lambda item: (item["option_id"], item["candidate_id"]))
    candidate_ids = [str(item["candidate_id"]) for item in candidates]
    action_keys = [_canonical_json(item["action"]) for item in candidates]
    if len(set(candidate_ids)) != len(candidate_ids):
        raise DecisionFrameValidationError("duplicate_candidate_id", "decision_frame.legal_candidates")
    if len(set(action_keys)) != len(action_keys):
        raise DecisionFrameValidationError("duplicate_candidate_action", "decision_frame.legal_candidates")
    end_turn = [item for item in candidates if item["action"]["kind"] == "end_turn"]
    if len(end_turn) != 1:
        raise DecisionFrameValidationError("single_end_turn_required", "decision_frame.legal_candidates")
    selected_action = _action(
        value.get("selected_action"), "decision_frame.selected_action", pre_state
    )
    selected_candidate_id = _token(
        value.get("selected_candidate_id"),
        "decision_frame.selected_candidate_id",
        limit=96,
    )
    selected = next(
        (item for item in candidates if item["candidate_id"] == selected_candidate_id),
        None,
    )
    if selected is None or selected["action"] != selected_action:
        raise DecisionFrameValidationError(
            "selected_candidate_mismatch", "decision_frame.selected_candidate_id"
        )
    fixed_contracts = {
        "capture_contract": DECISION_FRAME_CAPTURE_CONTRACT,
        "candidate_set_contract": DECISION_FRAME_CANDIDATE_SET_CONTRACT,
        "selection_contract": DECISION_FRAME_SELECTION_CONTRACT,
        "transition_contract": DECISION_FRAME_TRANSITION_CONTRACT,
    }
    for key, expected in fixed_contracts.items():
        if value.get(key) != expected:
            raise DecisionFrameValidationError("contract_mismatch", f"decision_frame.{key}")
    if value.get("actor_side") != "local" or value.get("actor_player_id") != "friendly":
        raise DecisionFrameValidationError("actor_mismatch", "decision_frame.actor_side")
    return {
        "schema": schema,
        "game_id": game_id,
        "decision_sequence": _integer(
            value.get("decision_sequence"), "decision_frame.decision_sequence", minimum=1
        ),
        "observed_at_utc": _timestamp(
            value.get("observed_at_utc"), "decision_frame.observed_at_utc"
        ),
        "client_build": client_build,
        "mode": mode,
        "actor_side": "local",
        "actor_player_id": "friendly",
        "selected_behavior_id": behavior_id,
        "hdt_frame_id": _integer(value.get("hdt_frame_id"), "decision_frame.hdt_frame_id"),
        **fixed_contracts,
        "pre_state": pre_state,
        "post_state": post_state,
        "selected_candidate_id": selected_candidate_id,
        "selected_action": selected_action,
        "legal_candidates": candidates,
        "candidate_set_complete": _true(
            value.get("candidate_set_complete"), "decision_frame.candidate_set_complete"
        ),
        "selected_action_observed": _true(
            value.get("selected_action_observed"), "decision_frame.selected_action_observed"
        ),
        "selected_action_in_candidates": _true(
            value.get("selected_action_in_candidates"),
            "decision_frame.selected_action_in_candidates",
        ),
        "imitation_training_eligible": _true(
            value.get("imitation_training_eligible"),
            "decision_frame.imitation_training_eligible",
        ),
        "optimality_verified": _false(
            value.get("optimality_verified"), "decision_frame.optimality_verified"
        ),
        "rl_training_eligible": _false(
            value.get("rl_training_eligible"), "decision_frame.rl_training_eligible"
        ),
        "outcome_used_as_action_optimality": _false(
            value.get("outcome_used_as_action_optimality"),
            "decision_frame.outcome_used_as_action_optimality",
        ),
    }


@dataclass(frozen=True)
class DecisionFrameRecord:
    value: dict[str, Any]

    @classmethod
    def from_dict(cls, value: Any) -> "DecisionFrameRecord":
        raw = _mapping(value, "decision_frame")
        _strict_keys(raw, _TOP_LEVEL_KEYS, _TOP_LEVEL_KEYS, "decision_frame")
        content = _normalized_content(
            {key: raw[key] for key in _CONTENT_KEYS}, strict=True
        )
        digest = _content_digest(content)
        content_sha256 = _text(
            raw.get("content_sha256"), "decision_frame.content_sha256", limit=64
        )
        frame_id = _text(
            raw.get("decision_frame_id"), "decision_frame.decision_frame_id", limit=80
        )
        if _HEX_SHA256.fullmatch(content_sha256) is None or content_sha256 != digest:
            raise DecisionFrameValidationError(
                "content_sha256_mismatch", "decision_frame.content_sha256"
            )
        if _FRAME_ID.fullmatch(frame_id) is None or frame_id != "decision-frame-" + digest:
            raise DecisionFrameValidationError(
                "decision_frame_id_mismatch", "decision_frame.decision_frame_id"
            )
        result = dict(content)
        result["content_sha256"] = digest
        result["decision_frame_id"] = frame_id
        return cls({key: result[key] for key in sorted(_TOP_LEVEL_KEYS)})

    @property
    def decision_frame_id(self) -> str:
        return str(self.value["decision_frame_id"])

    @property
    def game_id(self) -> str:
        return str(self.value["game_id"])

    def to_dict(self) -> dict[str, Any]:
        return copy.deepcopy(self.value)


def create_decision_frame_record(
    *,
    game_id: str,
    decision_sequence: int,
    observed_at_utc: str,
    client_build: str,
    mode: str,
    selected_behavior_id: str,
    hdt_frame_id: int,
    pre_state: Mapping[str, Any],
    post_state: Mapping[str, Any],
    selected_action: Mapping[str, Any],
    legal_candidates: Sequence[Mapping[str, Any]],
) -> DecisionFrameRecord:
    normalized_candidates = [
        _candidate(
            item,
            f"decision_frame.legal_candidates[{index}]",
            pre_state,
            input_form=True,
        )
        for index, item in enumerate(legal_candidates)
    ]
    normalized_candidates.sort(key=lambda item: (item["option_id"], item["candidate_id"]))
    normalized_action = _action(selected_action, "decision_frame.selected_action", pre_state)
    matches = [
        item for item in normalized_candidates if item["action"] == normalized_action
    ]
    if len(matches) != 1:
        raise DecisionFrameValidationError(
            "selected_action_candidate_count_mismatch",
            "decision_frame.selected_action",
        )
    content = {
        "schema": DECISION_FRAME_SCHEMA_ID,
        "game_id": game_id,
        "decision_sequence": decision_sequence,
        "observed_at_utc": observed_at_utc,
        "client_build": client_build,
        "mode": mode,
        "actor_side": "local",
        "actor_player_id": "friendly",
        "selected_behavior_id": selected_behavior_id,
        "hdt_frame_id": hdt_frame_id,
        "capture_contract": DECISION_FRAME_CAPTURE_CONTRACT,
        "candidate_set_contract": DECISION_FRAME_CANDIDATE_SET_CONTRACT,
        "selection_contract": DECISION_FRAME_SELECTION_CONTRACT,
        "transition_contract": DECISION_FRAME_TRANSITION_CONTRACT,
        "pre_state": copy.deepcopy(dict(pre_state)),
        "post_state": copy.deepcopy(dict(post_state)),
        "selected_candidate_id": matches[0]["candidate_id"],
        "selected_action": normalized_action,
        "legal_candidates": normalized_candidates,
        "candidate_set_complete": True,
        "selected_action_observed": True,
        "selected_action_in_candidates": True,
        "imitation_training_eligible": True,
        "optimality_verified": False,
        "rl_training_eligible": False,
        "outcome_used_as_action_optimality": False,
    }
    normalized = _normalized_content(content, strict=True)
    digest = _content_digest(normalized)
    raw = dict(normalized)
    raw["content_sha256"] = digest
    raw["decision_frame_id"] = "decision-frame-" + digest
    return DecisionFrameRecord.from_dict(raw)


def _read_bounded(path: str | Path, *, label: str) -> bytes:
    source = Path(path)
    try:
        size = source.stat().st_size
    except OSError as exc:
        raise DecisionFrameValidationError("file_unreadable", label) from exc
    if not source.is_file() or size > _MAX_CORPUS_BYTES:
        raise DecisionFrameValidationError("file_size_invalid", label)
    try:
        return source.read_bytes()
    except OSError as exc:
        raise DecisionFrameValidationError("file_unreadable", label) from exc


def _jsonl_values(payload: bytes, *, label: str) -> list[Any]:
    try:
        text = payload.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise DecisionFrameValidationError("file_not_utf8", label) from exc
    values: list[Any] = []
    for index, line in enumerate(text.splitlines(), 1):
        if not line.strip():
            raise DecisionFrameValidationError("blank_line", f"{label}[{index}]")
        if len(line.encode("utf-8")) > _MAX_LINE_BYTES:
            raise DecisionFrameValidationError("line_too_large", f"{label}[{index}]")
        try:
            values.append(json.loads(line))
        except json.JSONDecodeError as exc:
            raise DecisionFrameValidationError(
                "invalid_json", f"{label}[{index}]"
            ) from exc
    return values


def _behavior_action_core(value: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "kind": str(value.get("kind") or ""),
        "source_entity_id": str(value.get("source_entity_id") or ""),
        "target_entity_id": str(value.get("target_entity_id") or ""),
        "card_id": str(value.get("card_id") or ""),
        "board_position": int(value.get("board_position") or 0),
    }


def audit_decision_frame_file(
    path: str | Path,
    *,
    behavior_path: str | Path | None = None,
) -> dict[str, Any]:
    payload = _read_bounded(path, label="decision_frame_input")
    values = _jsonl_values(payload, label="decision_frame_input")
    records: list[DecisionFrameRecord] = []
    invalid_reasons: Counter[str] = Counter()
    for value in values:
        try:
            records.append(DecisionFrameRecord.from_dict(value))
        except DecisionFrameValidationError as exc:
            invalid_reasons[exc.code] += 1
    ids = [record.decision_frame_id for record in records]
    selected_behavior_ids = [
        str(record.value["selected_behavior_id"]) for record in records
    ]
    sequence_errors = 0
    previous_sequence: dict[str, int] = {}
    for record in records:
        game_id = record.game_id
        sequence = int(record.value["decision_sequence"])
        expected = previous_sequence.get(game_id, 0) + 1
        if sequence != expected:
            sequence_errors += 1
        previous_sequence[game_id] = sequence
    action_kinds = Counter(
        str(record.value["selected_action"]["kind"]) for record in records
    )
    candidate_count = sum(
        len(record.value["legal_candidates"]) for record in records
    )
    behavior_input: dict[str, Any] | None = None
    join_reasons: Counter[str] = Counter()
    joined_count = 0
    if behavior_path is not None:
        behavior_payload = _read_bounded(behavior_path, label="behavior_input")
        behavior_values = _jsonl_values(behavior_payload, label="behavior_input")
        behaviors: dict[str, BehaviorRecord] = {}
        for value in behavior_values:
            try:
                behavior = BehaviorRecord.from_dict(value)
            except BehaviorValidationError as exc:
                join_reasons["behavior_contract:" + exc.code] += 1
                continue
            if behavior.behavior_id in behaviors:
                join_reasons["duplicate_behavior_id"] += 1
                continue
            behaviors[behavior.behavior_id] = behavior
        for record in records:
            behavior = behaviors.get(str(record.value["selected_behavior_id"]))
            if behavior is None:
                join_reasons["selected_behavior_missing"] += 1
                continue
            if behavior.game_id != record.game_id:
                join_reasons["selected_behavior_game_mismatch"] += 1
                continue
            if behavior.value["actor_side"] != "local":
                join_reasons["selected_behavior_not_local"] += 1
                continue
            if _behavior_action_core(behavior.value["action"]) != record.value["selected_action"]:
                join_reasons["selected_behavior_action_mismatch"] += 1
                continue
            if (
                behavior.value["pre_state"] != record.value["pre_state"]
                or behavior.value["post_state"] != record.value["post_state"]
            ):
                join_reasons["selected_behavior_state_mismatch"] += 1
                continue
            joined_count += 1
        behavior_input = {
            "bytes": len(behavior_payload),
            "sha256": hashlib.sha256(behavior_payload).hexdigest(),
            "valid_record_count": len(behaviors),
        }
    metrics = {
        "input_line_count": len(values),
        "valid_record_count": len(records),
        "invalid_record_count": sum(invalid_reasons.values()),
        "unique_record_count": len(set(ids)),
        "duplicate_record_id_count": len(ids) - len(set(ids)),
        "game_count": len({record.game_id for record in records}),
        "candidate_count": candidate_count,
        "average_candidate_count": (
            round(candidate_count / len(records), 6) if records else 0.0
        ),
        "selected_action_kind_counts": dict(sorted(action_kinds.items())),
        "sequence_error_count": sequence_errors,
        "duplicate_selected_behavior_id_count": (
            len(selected_behavior_ids) - len(set(selected_behavior_ids))
        ),
        "behavior_joined_count": joined_count,
        "behavior_join_error_count": sum(join_reasons.values()),
        "rl_training_eligible_count": sum(
            record.value["rl_training_eligible"] is True for record in records
        ),
        "optimality_verified_count": sum(
            record.value["optimality_verified"] is True for record in records
        ),
    }
    contract_passed = bool(records)
    contract_passed = contract_passed and metrics["invalid_record_count"] == 0
    contract_passed = contract_passed and metrics["duplicate_record_id_count"] == 0
    contract_passed = contract_passed and metrics["sequence_error_count"] == 0
    contract_passed = (
        contract_passed and metrics["duplicate_selected_behavior_id_count"] == 0
    )
    contract_passed = contract_passed and metrics["rl_training_eligible_count"] == 0
    contract_passed = contract_passed and metrics["optimality_verified_count"] == 0
    join_passed = bool(
        behavior_path is not None
        and joined_count == len(records)
        and not join_reasons
    )
    ready = contract_passed and join_passed
    return {
        "schema": "advisor-decision-frame-audit-v1",
        "status": "READY" if ready else "NOT_READY",
        "passed": ready,
        "contract_passed": contract_passed,
        "candidate_imitation_ready": ready,
        "rl_training_ready": False,
        "optimality_verified": False,
        "input": {
            "bytes": len(payload),
            "sha256": hashlib.sha256(payload).hexdigest(),
        },
        "behavior_input": behavior_input,
        "metrics": metrics,
        "invalid_reason_counts": dict(sorted(invalid_reasons.items())),
        "behavior_join_error_counts": dict(sorted(join_reasons.items())),
        "caveats_zh": [
            "READY 只批准候选集模仿和离线排序评估，不证明玩家选择最优。",
            "该合同固定禁止强化学习真值与最优动作标签。",
            "对手行为没有本机 Options 候选集，只能通过 game_id 做局级行为分析。",
        ],
    }
