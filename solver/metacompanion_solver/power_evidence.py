from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from typing import Any, Mapping, Sequence

from .errors import SolverError
from .schemas import (
    POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
    POWER_ACTION_IDENTITY_CHOICE_STATUS,
    POWER_ACTION_IDENTITY_COMPLETENESS,
    POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
    POWER_ACTION_IDENTITY_STATUS,
    TRANSITION_CANDIDATE_BOUNDARY_STATUSES,
    TRANSITION_CANDIDATE_STATUS,
    TRANSITION_CANDIDATE_VERIFICATION,
    Action,
    ActionKind,
    GameState,
)
from .simulator import apply_action


class PowerEvidenceError(ValueError):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


@dataclass(frozen=True)
class VerifiedPowerTransition:
    action: Action
    pre_state: GameState
    post_state: GameState
    normalized_pre_state_hash: str
    normalized_post_state_hash: str
    game_generation: int
    frame_id: int
    option_id: int
    start_cursor: int
    end_cursor: int


_POWER_WATERMARK = re.compile(r"^g([1-9][0-9]*):([1-9][0-9]*)$")


def _mapping(value: Any, code: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise PowerEvidenceError(code)
    return value


def _text(value: Any) -> str:
    return value.strip() if isinstance(value, str) else ""


def _integer(value: Any, code: str, *, minimum: int | None = None) -> int:
    if isinstance(value, bool):
        raise PowerEvidenceError(code)
    try:
        parsed = int(str(value))
    except (TypeError, ValueError) as exc:
        raise PowerEvidenceError(code) from exc
    if str(value).strip() != str(parsed) or (minimum is not None and parsed < minimum):
        raise PowerEvidenceError(code)
    return parsed


def _false(value: Any) -> bool:
    if value is False or (
        isinstance(value, int) and not isinstance(value, bool) and value == 0
    ):
        return True
    return isinstance(value, str) and value.strip().lower() in {"false", "0", "no"}


def _sha256_json(value: Mapping[str, Any]) -> str:
    payload = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def _digest(value: Any, code: str) -> str:
    digest = _text(value)
    if len(digest) != 64 or any(item not in "0123456789abcdef" for item in digest):
        raise PowerEvidenceError(code)
    return digest


def _state_projection(state: GameState) -> Mapping[str, Any]:
    value = state.to_dict()
    value["state_id"] = ""
    value["rng_seed"] = 0
    value["metadata"] = {}
    value["belief"] = {}
    return value


def validate_power_identity_observation(
    observation_value: Any,
    *,
    require_isolated: bool = False,
) -> VerifiedPowerTransition:
    """Validate producer evidence without treating it as a replayed transition."""

    observation = _mapping(observation_value, "power_observation_not_object")
    metadata = _mapping(
        observation.get("metadata"), "power_metadata_not_object"
    )
    expected = {
        "capture_contract": POWER_ACTION_IDENTITY_CAPTURE_CONTRACT,
        "completeness": POWER_ACTION_IDENTITY_COMPLETENESS,
        "action_identity_status": POWER_ACTION_IDENTITY_STATUS,
        "choice_status": POWER_ACTION_IDENTITY_CHOICE_STATUS,
        "simulator_status": POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
        "transition_status": TRANSITION_CANDIDATE_STATUS,
        "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
    }
    for key, required in expected.items():
        if _text(metadata.get(key)).lower() != required:
            raise PowerEvidenceError(f"invalid_power_{key}")
    if "training_eligible" not in metadata or not _false(
        metadata.get("training_eligible")
    ):
        raise PowerEvidenceError("power_identity_marked_training_eligible")

    state_id = _text(observation.get("state_id"))
    pre_state_id = _text(metadata.get("pre_state_id"))
    post_state_id = _text(metadata.get("post_state_id"))
    if not state_id or state_id != pre_state_id:
        raise PowerEvidenceError("power_pre_state_id_mismatch")
    if not post_state_id or post_state_id == pre_state_id:
        raise PowerEvidenceError("power_post_state_id_invalid")
    _integer(metadata.get("action_sequence"), "power_action_sequence_invalid", minimum=1)
    pre_sequence = _integer(
        metadata.get("pre_snapshot_sequence"),
        "power_pre_snapshot_sequence_invalid",
        minimum=1,
    )
    post_sequence = _integer(
        metadata.get("post_snapshot_sequence"),
        "power_post_snapshot_sequence_invalid",
        minimum=1,
    )
    if post_sequence <= pre_sequence:
        raise PowerEvidenceError("power_snapshot_sequence_not_increasing")
    intervening = _integer(
        metadata.get("intervening_action_count"),
        "power_intervening_action_count_invalid",
        minimum=0,
    )
    warnings = _integer(
        metadata.get("capture_warning_count"),
        "power_capture_warning_count_invalid",
        minimum=0,
    )
    boundary = _text(metadata.get("boundary_status")).lower()
    if boundary not in TRANSITION_CANDIDATE_BOUNDARY_STATUSES:
        raise PowerEvidenceError("power_boundary_status_invalid")
    if boundary == "isolated" and intervening != 0:
        raise PowerEvidenceError("power_isolated_boundary_has_intervening_action")
    if require_isolated and (boundary != "isolated" or intervening != 0 or warnings != 0):
        raise PowerEvidenceError("power_transition_not_isolated")
    game_generation = _integer(
        metadata.get("game_generation"),
        "power_game_generation_invalid",
        minimum=1,
    )
    collector_epoch = _integer(
        metadata.get("power_collector_epoch"),
        "power_collector_epoch_invalid",
        minimum=1,
    )
    action_ordinal = _integer(
        metadata.get("power_action_ordinal"),
        "power_action_ordinal_invalid",
        minimum=1,
    )
    gap_count = _integer(
        metadata.get("power_gap_count"),
        "power_gap_count_invalid",
        minimum=0,
    )
    if collector_epoch != game_generation:
        raise PowerEvidenceError("power_collector_epoch_generation_mismatch")
    if action_ordinal != _integer(
        metadata.get("action_sequence"),
        "power_action_sequence_invalid",
        minimum=1,
    ):
        raise PowerEvidenceError("power_action_ordinal_sequence_mismatch")
    if require_isolated and gap_count != 0:
        raise PowerEvidenceError("power_collector_trace_tainted")

    _digest(metadata.get("raw_pre_snapshot_hash"), "power_raw_pre_hash_invalid")
    _digest(metadata.get("raw_post_snapshot_hash"), "power_raw_post_hash_invalid")
    normalized_pre_hash = _digest(
        metadata.get("pre_state_hash"), "power_pre_state_hash_invalid"
    )
    normalized_post_hash = _digest(
        metadata.get("post_state_hash"), "power_post_state_hash_invalid"
    )
    pre_raw = _mapping(observation.get("pre_state"), "power_pre_state_missing")
    post_raw = _mapping(observation.get("post_state"), "power_post_state_missing")
    if _sha256_json(pre_raw) != normalized_pre_hash:
        raise PowerEvidenceError("power_pre_state_hash_mismatch")
    if _sha256_json(post_raw) != normalized_post_hash:
        raise PowerEvidenceError("power_post_state_hash_mismatch")
    try:
        pre = GameState.from_dict(pre_raw)
        post = GameState.from_dict(post_raw)
    except (SolverError, TypeError, ValueError) as exc:
        raise PowerEvidenceError("power_state_invalid") from exc
    if pre.state_id != pre_state_id or post.state_id != post_state_id:
        raise PowerEvidenceError("power_detached_state_id_mismatch")
    game_id = _text(observation.get("game_id"))
    if not game_id or any(
        _text(state.metadata.get("game_id")) not in {"", game_id}
        for state in (pre, post)
    ):
        raise PowerEvidenceError("power_detached_state_game_mismatch")
    if pre.active_player_id != pre.perspective_player_id:
        raise PowerEvidenceError("power_action_not_local_turn")

    raw_action = _mapping(observation.get("action"), "power_action_missing")
    required_action_fields = {
        "sub_option",
        "board_position",
        "option_id",
        "frame_id",
        "power_start_watermark",
        "power_end_watermark",
        "choices",
    }
    if not required_action_fields.issubset(raw_action):
        raise PowerEvidenceError("power_action_evidence_missing")
    if _integer(raw_action.get("sub_option"), "power_sub_option_invalid") != -1:
        raise PowerEvidenceError("power_sub_option_unresolved")
    _integer(raw_action.get("board_position"), "power_board_position_invalid", minimum=0)
    option_id_text = _text(raw_action.get("option_id"))
    frame_id_text = _text(raw_action.get("frame_id"))
    if not option_id_text.isdigit() or not frame_id_text.isdigit():
        raise PowerEvidenceError("power_option_identity_invalid")
    option_id = int(option_id_text)
    frame_id = int(frame_id_text)
    if frame_id <= 0:
        raise PowerEvidenceError("power_frame_id_invalid")
    start_watermark = _text(raw_action.get("power_start_watermark"))
    end_watermark = _text(raw_action.get("power_end_watermark"))
    start_match = _POWER_WATERMARK.fullmatch(start_watermark)
    end_match = _POWER_WATERMARK.fullmatch(end_watermark)
    if start_match is None or end_match is None:
        raise PowerEvidenceError("power_watermark_invalid")
    start_generation, start_cursor = (int(item) for item in start_match.groups())
    end_generation, end_cursor = (int(item) for item in end_match.groups())
    if (
        start_generation != game_generation
        or end_generation != game_generation
        or end_cursor <= start_cursor
    ):
        raise PowerEvidenceError("power_watermark_order_invalid")
    choices = raw_action.get("choices")
    if not isinstance(choices, Sequence) or isinstance(
        choices, (str, bytes, bytearray)
    ):
        raise PowerEvidenceError("power_choices_not_array")
    if choices:
        raise PowerEvidenceError("power_choice_unresolved")

    normalized_action = dict(raw_action)
    for key in ("source_entity_id", "target_entity_id"):
        value = normalized_action.get(key)
        if value is None:
            normalized_action[key] = ""
        elif isinstance(value, int) and not isinstance(value, bool):
            normalized_action[key] = str(value)
    try:
        action = Action.from_dict(normalized_action, "power.action")
    except (SolverError, TypeError, ValueError) as exc:
        raise PowerEvidenceError("power_action_invalid") from exc
    declared_action_id = raw_action.get("action_id")
    if declared_action_id is not None and declared_action_id != action.action_id:
        raise PowerEvidenceError("power_action_id_mismatch")

    actor = pre.player(pre.perspective_player_id)
    source = None
    if action.kind == ActionKind.PLAY_CARD:
        source = next(
            (card for card in actor.hand if card.entity_id == action.source_entity_id),
            None,
        )
    elif action.kind == ActionKind.ATTACK:
        source = next(
            (
                card
                for card in (actor.hero, *actor.board)
                if card.entity_id == action.source_entity_id
            ),
            None,
        )
    elif action.kind == ActionKind.HERO_POWER:
        source = (
            actor.hero_power
            if actor.hero_power is not None
            and actor.hero_power.entity_id == action.source_entity_id
            else None
        )
    elif action.kind == ActionKind.LOCATION_ACTIVATE:
        source = next(
            (
                card
                for card in actor.board
                if card.entity_id == action.source_entity_id
                and card.card_type.value == "LOCATION"
            ),
            None,
        )
    elif action.kind == ActionKind.END_TURN:
        if (
            action.source_entity_id
            or action.target_entity_id
            or action.card_id
            or option_id != 0
        ):
            raise PowerEvidenceError("power_end_turn_identity_invalid")
    else:
        raise PowerEvidenceError("power_action_kind_unsupported")
    if action.kind != ActionKind.END_TURN:
        if option_id <= 0:
            raise PowerEvidenceError("power_non_end_option_id_invalid")
        if source is None:
            raise PowerEvidenceError("power_source_not_local_pre_state_entity")
        if not action.card_id or action.card_id != source.card_id:
            raise PowerEvidenceError("power_source_card_id_mismatch")
        if not action.source_entity_id.isdigit() or int(action.source_entity_id) <= 0:
            raise PowerEvidenceError("power_source_entity_id_not_numeric")

    board_position = _integer(
        raw_action.get("board_position"), "power_board_position_invalid", minimum=0
    )
    if action.kind == ActionKind.PLAY_CARD and source is not None:
        if source.card_type.value in {"MINION", "LOCATION"}:
            if not 1 <= board_position <= len(actor.board) + 1:
                raise PowerEvidenceError("power_board_position_not_replayable")
        elif board_position != 0:
            raise PowerEvidenceError("power_non_minion_board_position_invalid")
    elif board_position != 0:
        raise PowerEvidenceError("power_non_play_board_position_invalid")

    source_resolution = _text(metadata.get("source_entity_resolution")).lower()
    target_resolution = _text(metadata.get("target_entity_resolution")).lower()
    expected_source_resolution = (
        "not_applicable" if action.kind == ActionKind.END_TURN else "exact_entity_id"
    )
    expected_target_resolution = (
        "exact_entity_id" if action.target_entity_id else "not_applicable"
    )
    if source_resolution != expected_source_resolution:
        raise PowerEvidenceError("power_source_resolution_invalid")
    if target_resolution != expected_target_resolution:
        raise PowerEvidenceError("power_target_resolution_invalid")
    if action.kind == ActionKind.ATTACK and not action.target_entity_id:
        raise PowerEvidenceError("power_attack_target_missing")
    if action.target_entity_id:
        if not action.target_entity_id.isdigit() or int(action.target_entity_id) <= 0:
            raise PowerEvidenceError("power_target_entity_id_not_numeric")
        public_ids = {
            card.entity_id
            for player in (pre.friendly, pre.opponent)
            for card in (
                player.hero,
                *player.hand,
                *player.board,
                *([player.hero_power] if player.hero_power else []),
                *([player.weapon] if player.weapon else []),
            )
        }
        if action.target_entity_id not in public_ids:
            raise PowerEvidenceError("power_target_not_pre_state_entity")

    return VerifiedPowerTransition(
        action=action,
        pre_state=pre,
        post_state=post,
        normalized_pre_state_hash=normalized_pre_hash,
        normalized_post_state_hash=normalized_post_hash,
        game_generation=game_generation,
        frame_id=frame_id,
        option_id=option_id,
        start_cursor=start_cursor,
        end_cursor=end_cursor,
    )


def verify_power_identity_transition(observation_value: Any) -> VerifiedPowerTransition:
    evidence = validate_power_identity_observation(
        observation_value,
        require_isolated=True,
    )
    try:
        outcome = apply_action(
            evidence.pre_state,
            evidence.action,
            validate=evidence.action.kind != ActionKind.LOCATION_ACTIVATE,
        )
    except (SolverError, TypeError, ValueError, RuntimeError) as exc:
        raise PowerEvidenceError("power_simulator_replay_failed") from exc
    if outcome.annotations:
        raise PowerEvidenceError("power_simulator_replay_has_annotations")
    if _state_projection(outcome.state) != _state_projection(evidence.post_state):
        raise PowerEvidenceError("power_simulator_post_state_mismatch")
    return evidence
