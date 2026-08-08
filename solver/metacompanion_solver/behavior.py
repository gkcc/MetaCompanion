from __future__ import annotations

import copy
import hashlib
import json
import os
import re
import threading
from collections import Counter, defaultdict
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any, Mapping, Sequence


BEHAVIOR_SCHEMA_ID = "advisor-behavior-v1"
BEHAVIOR_CORPUS_FILENAME = "behavior-v1.jsonl"

_ACTOR_SIDES = {"local", "opponent", "unknown"}
_ACTOR_PLAYER_IDS = {"friendly", "opponent"}
_ACTOR_EVIDENCE = {
    "active_player",
    "source_owner",
    "hdt_player_event",
    "hdt_opponent_event",
    "hdt_power_log",
    "hdt_replay_power",
    "unknown",
}
_IDENTITY_STATUSES = {
    "exact_public_entity",
    "revealed_after_action",
    "event_only",
    "unknown",
}
_VISIBILITY_STATUSES = {
    "public_pre_state",
    "revealed_post_action",
    "hidden_source",
}
_BOUNDARY_STATUSES = {"isolated", "overlapped", "unstable", "unverified"}
_ACTION_KINDS = {
    "play_card",
    "attack",
    "hero_power",
    "location_activate",
    "end_turn",
}
_CARD_TYPES = {
    "HERO",
    "MINION",
    "SPELL",
    "WEAPON",
    "HERO_POWER",
    "LOCATION",
    "UNKNOWN",
}
_SOURCE_EVENTS: dict[str, tuple[str | None, str | None]] = {
    "hdt_power_log": ("local", None),
    "hdt_replay_power": (None, None),
    "player_play": ("local", "play_card"),
    "player_attack": ("local", "attack"),
    "player_hero_power": ("local", "hero_power"),
    "turn_passed_to_opponent": ("local", "end_turn"),
    "opponent_play": ("opponent", "play_card"),
    "opponent_attack": ("opponent", "attack"),
    "opponent_hero_power": ("opponent", "hero_power"),
    "turn_passed_to_player": ("opponent", "end_turn"),
    "unknown": (None, None),
}
_TOP_LEVEL_KEYS = {
    "schema",
    "behavior_id",
    "content_sha256",
    "game_id",
    "behavior_sequence",
    "observed_at_utc",
    "actor_side",
    "actor_player_id",
    "actor_evidence",
    "identity_status",
    "visibility_status",
    "boundary_status",
    "source_event",
    "action",
    "pre_state",
    "post_state",
    "behavior_eligible",
    "rl_training_eligible",
}
_CONTENT_KEYS = _TOP_LEVEL_KEYS - {"behavior_id", "content_sha256"}
_ACTION_REQUIRED_KEYS = {"kind", "source_entity_id", "target_entity_id", "card_id"}
_ACTION_KEYS = _ACTION_REQUIRED_KEYS | {
    "sub_option",
    "board_position",
    "choice_status",
    "choices",
}
_CHOICE_KEYS = {
    "choice_id",
    "choice_type",
    "source_entity_id",
    "option_entity_ids",
    "selected_entity_ids",
    "status",
}
_CHOICE_STATUSES = {"none", "selected", "unresolved", "not_observed"}
_CHOICE_ITEM_STATUSES = {"selected", "unresolved"}
_STATE_KEYS = {
    "state_id",
    "turn",
    "active_player_id",
    "perspective_player_id",
    "friendly",
    "opponent",
    "patch",
    "mode",
}
_PLAYER_REQUIRED_KEYS = {
    "player_id",
    "hero",
    "hero_power",
    "weapon",
    "hand",
    "board",
    "mana",
    "max_mana",
    "armor",
    "deck_size",
    "fatigue",
    "hero_power_available",
    "spell_power",
}
_PLAYER_KEYS = _PLAYER_REQUIRED_KEYS | {
    "public_rule_tags",
    "public_rule_tags_complete",
}
_PUBLIC_RULE_TAG_KEYS = {
    "STEADY_SHOT_CAN_TARGET",
    "CURRENT_HEROPOWER_DAMAGE_BONUS",
    "HERO_POWER_DOUBLE",
    "HEROPOWER_DAMAGE",
    "HERO_POWER_DISABLED",
}
_PUBLIC_ENTITY_KEYS = {
    "entity_id",
    "card_id",
    "card_type",
    "cost",
    "attack",
    "health",
    "current_health",
    "playable",
    "can_attack",
    "attacks_remaining",
    "current_health_known",
    "taunt",
    "divine_shield",
    "stealth",
    "poisonous",
    "lifesteal",
    "windfury",
    "mega_windfury",
    "rush",
    "charge",
    "reborn",
    "dormant",
    "immune",
    "summoned_this_turn",
    "frozen",
    "durability",
    "current_durability",
}
_HIDDEN_ENTITY_KEYS = {"entity_id", "visibility"}
_SAFE_TOKEN = re.compile(r"^[A-Za-z0-9_.:-]+$")
_ANONYMOUS_GAME_ID = re.compile(r"^anon-[0-9a-f]{16}$")
_HEX_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_BEHAVIOR_ID = re.compile(r"^behavior-[0-9a-f]{64}$")
_RESERVED_TRAJECTORY_FILENAMES = {
    "training.jsonl",
    "training-v2.jsonl",
    "trajectory.jsonl",
    "trajectory-v1.jsonl",
}


class BehaviorValidationError(ValueError):
    def __init__(self, code: str, path: str = "behavior"):
        super().__init__(f"{path}: {code}")
        self.code = code
        self.path = path


class BehaviorCorpusError(RuntimeError):
    def __init__(self, code: str, path: str = ""):
        super().__init__(f"{path}: {code}" if path else code)
        self.code = code
        self.path = path


def _mapping(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise BehaviorValidationError("must_be_object", path)
    return value


def _strict_keys(
    raw: Mapping[str, Any], allowed: set[str], required: set[str], path: str
) -> None:
    keys = {str(key) for key in raw}
    unknown = sorted(keys - allowed)
    if unknown:
        raise BehaviorValidationError("unknown_field:" + unknown[0], path)
    missing = sorted(required - keys)
    if missing:
        raise BehaviorValidationError("missing_field:" + missing[0], path)


def _text(value: Any, path: str, *, allow_empty: bool = False, limit: int = 256) -> str:
    if not isinstance(value, str):
        raise BehaviorValidationError("must_be_string", path)
    result = value.strip()
    if (not result and not allow_empty) or len(result) > limit:
        raise BehaviorValidationError("invalid_length", path)
    return result


def _token(value: Any, path: str, *, allow_empty: bool = False, limit: int = 256) -> str:
    result = _text(value, path, allow_empty=allow_empty, limit=limit)
    if result and _SAFE_TOKEN.fullmatch(result) is None:
        raise BehaviorValidationError("unsafe_token", path)
    return result


def _entity_id(value: Any, path: str, *, allow_empty: bool = False) -> str:
    if value is None and allow_empty:
        return ""
    if isinstance(value, bool) or not isinstance(value, (str, int)):
        raise BehaviorValidationError("invalid_entity_id", path)
    return _token(str(value), path, allow_empty=allow_empty, limit=128)


def _integer(value: Any, path: str, *, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise BehaviorValidationError("must_be_integer", path)
    return value


def _rule_tag_integer(value: Any, path: str) -> int:
    if (
        isinstance(value, bool)
        or not isinstance(value, int)
        or value < -(2**31)
        or value > 2**31 - 1
    ):
        raise BehaviorValidationError("must_be_integer", path)
    return value


def _boolean(value: Any, path: str) -> bool:
    if not isinstance(value, bool):
        raise BehaviorValidationError("must_be_boolean", path)
    return value


def _enum(value: Any, allowed: set[str], path: str) -> str:
    result = _text(value, path).lower()
    if result not in allowed:
        raise BehaviorValidationError("invalid_value", path)
    return result


def _timestamp(value: Any, path: str) -> str:
    result = _text(value, path, limit=64)
    try:
        parsed = datetime.fromisoformat(
            result[:-1] + "+00:00" if result.endswith("Z") else result
        )
    except ValueError as exc:
        raise BehaviorValidationError("invalid_rfc3339_timestamp", path) from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise BehaviorValidationError("timestamp_requires_offset", path)
    return result


def anonymous_game_id(value: Any) -> str:
    if value is None:
        raise BehaviorValidationError("game_id_required", "game_id")
    text = _text(str(value), "game_id", limit=512)
    if _ANONYMOUS_GAME_ID.fullmatch(text):
        return text
    return "anon-" + hashlib.sha256(text.encode("utf-8")).hexdigest()[:16]


def _canonical_json(value: Mapping[str, Any]) -> str:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )


def _content_digest(value: Mapping[str, Any]) -> str:
    return hashlib.sha256(_canonical_json(value).encode("utf-8")).hexdigest()


def _public_entity(value: Any, path: str, *, hidden: bool, strict: bool) -> dict[str, Any]:
    raw = _mapping(value, path)
    if hidden:
        if strict:
            _strict_keys(raw, _HIDDEN_ENTITY_KEYS, _HIDDEN_ENTITY_KEYS, path)
            if raw.get("visibility") != "hidden":
                raise BehaviorValidationError("hidden_entity_visibility_required", path)
        return {
            "entity_id": _entity_id(
                raw.get("entity_id", ""), f"{path}.entity_id", allow_empty=True
            ),
            "visibility": "hidden",
        }

    if strict:
        _strict_keys(raw, _PUBLIC_ENTITY_KEYS, {"entity_id", "card_id", "card_type"}, path)
    card_type = _text(
        str(raw.get("card_type", "UNKNOWN")), f"{path}.card_type", limit=32
    ).upper()
    if card_type not in _CARD_TYPES:
        raise BehaviorValidationError("invalid_value", f"{path}.card_type")
    entity: dict[str, Any] = {
        "entity_id": _entity_id(raw.get("entity_id"), f"{path}.entity_id"),
        "card_id": _token(
            raw.get("card_id", ""), f"{path}.card_id", allow_empty=True, limit=128
        ),
        "card_type": card_type,
    }
    for key in (
        "cost",
        "attack",
        "health",
        "current_health",
        "attacks_remaining",
        "durability",
        "current_durability",
    ):
        if key in raw:
            entity[key] = _integer(raw[key], f"{path}.{key}")
    for key in (
        "playable",
        "can_attack",
        "current_health_known",
        "taunt",
        "divine_shield",
        "stealth",
        "poisonous",
        "lifesteal",
        "windfury",
        "mega_windfury",
        "rush",
        "charge",
        "reborn",
        "dormant",
        "immune",
        "summoned_this_turn",
        "frozen",
    ):
        if key in raw:
            entity[key] = _boolean(raw[key], f"{path}.{key}")
    return entity


def _entity_sequence(
    value: Any, path: str, *, hidden: bool, strict: bool
) -> list[dict[str, Any]]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise BehaviorValidationError("must_be_array", path)
    return [
        _public_entity(item, f"{path}[{index}]", hidden=hidden, strict=strict)
        for index, item in enumerate(value)
    ]


def _public_rule_tags(value: Any, path: str, *, strict: bool) -> dict[str, int]:
    raw = _mapping(value, path)
    if strict:
        _strict_keys(raw, _PUBLIC_RULE_TAG_KEYS, set(), path)
    return {
        key: _rule_tag_integer(raw[key], f"{path}.{key}")
        for key in sorted(_PUBLIC_RULE_TAG_KEYS)
        if key in raw
    }


def _public_player(value: Any, path: str, role: str, *, strict: bool) -> dict[str, Any]:
    raw = _mapping(value, path)
    if strict:
        _strict_keys(raw, _PLAYER_KEYS, _PLAYER_REQUIRED_KEYS, path)
        if raw.get("player_id") != role:
            raise BehaviorValidationError("player_role_mismatch", f"{path}.player_id")
    hero = raw.get("hero")
    if hero is None:
        raise BehaviorValidationError("hero_required", f"{path}.hero")
    hand = _entity_sequence(
        raw.get("hand", []),
        f"{path}.hand",
        hidden=role == "opponent",
        strict=strict,
    )
    board = _entity_sequence(
        raw.get("board", []), f"{path}.board", hidden=False, strict=strict
    )
    if len(hand) > 10:
        raise BehaviorValidationError("public_hand_capacity_exceeded", f"{path}.hand")
    if len(board) > 7:
        raise BehaviorValidationError("public_board_capacity_exceeded", f"{path}.board")
    result = {
        "player_id": role,
        "hero": _public_entity(hero, f"{path}.hero", hidden=False, strict=strict),
        "hero_power": None,
        "weapon": None,
        "hand": hand,
        "board": board,
        "mana": _integer(raw.get("mana", 0), f"{path}.mana"),
        "max_mana": _integer(raw.get("max_mana", 0), f"{path}.max_mana"),
        "armor": _integer(raw.get("armor", 0), f"{path}.armor"),
        "deck_size": _integer(raw.get("deck_size", 0), f"{path}.deck_size"),
        "fatigue": _integer(raw.get("fatigue", 0), f"{path}.fatigue"),
        "hero_power_available": _boolean(
            raw.get("hero_power_available", False), f"{path}.hero_power_available"
        ),
        "spell_power": _integer(raw.get("spell_power", 0), f"{path}.spell_power"),
    }
    for key in ("hero_power", "weapon"):
        if raw.get(key) is not None:
            result[key] = _public_entity(
                raw[key], f"{path}.{key}", hidden=False, strict=strict
            )
    if "public_rule_tags" in raw:
        result["public_rule_tags"] = _public_rule_tags(
            raw["public_rule_tags"], f"{path}.public_rule_tags", strict=strict
        )
    if "public_rule_tags_complete" in raw:
        result["public_rule_tags_complete"] = _boolean(
            raw["public_rule_tags_complete"],
            f"{path}.public_rule_tags_complete",
        )
    return result


def public_behavior_state(value: Any, *, strict: bool = False) -> dict[str, Any]:
    """Project a canonical game state onto the privacy-safe behavior allowlist.

    When ``strict`` is false, non-allowlisted state fields (names, card text, metadata,
    controller IDs, credentials, and raw logs) are deliberately not copied. Persisted
    corpus records are parsed with ``strict=True`` so smuggled fields fail closed.
    """

    raw = _mapping(value, "state")
    if strict:
        _strict_keys(raw, _STATE_KEYS, _STATE_KEYS, "state")
    active = _enum(
        raw.get("active_player_id"), _ACTOR_PLAYER_IDS, "state.active_player_id"
    )
    perspective = _enum(
        raw.get("perspective_player_id"),
        _ACTOR_PLAYER_IDS,
        "state.perspective_player_id",
    )
    if perspective != "friendly":
        raise BehaviorValidationError(
            "perspective_must_be_friendly", "state.perspective_player_id"
        )
    return {
        "state_id": _token(raw.get("state_id"), "state.state_id"),
        "turn": _integer(raw.get("turn"), "state.turn", minimum=1),
        "active_player_id": active,
        "perspective_player_id": perspective,
        "friendly": _public_player(
            raw.get("friendly"), "state.friendly", "friendly", strict=strict
        ),
        "opponent": _public_player(
            raw.get("opponent"), "state.opponent", "opponent", strict=strict
        ),
        "patch": _token(
            raw.get("patch", ""), "state.patch", allow_empty=True, limit=128
        ),
        "mode": _token(raw.get("mode", ""), "state.mode", allow_empty=True, limit=128),
    }


def _optional_integer(value: Any, path: str, *, minimum: int) -> int | None:
    if value is None:
        return None
    return _integer(value, path, minimum=minimum)


def _positive_choice_entity_id(value: Any, path: str) -> str:
    return _entity_id(value, path)


def _choice_entity_ids(value: Any, path: str) -> list[str]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise BehaviorValidationError("must_be_array", path)
    result = [
        _positive_choice_entity_id(item, f"{path}[{index}]")
        for index, item in enumerate(value)
    ]
    if len(set(result)) != len(result):
        raise BehaviorValidationError("duplicate_entity_id", path)
    return result


def _choice(value: Any, path: str) -> dict[str, Any]:
    raw = _mapping(value, path)
    _strict_keys(raw, _CHOICE_KEYS, _CHOICE_KEYS, path)
    choice_id_raw = raw.get("choice_id")
    choice_id = (
        None
        if choice_id_raw is None
        else _integer(choice_id_raw, f"{path}.choice_id", minimum=1)
    )
    return {
        "choice_id": choice_id,
        "choice_type": _token(raw.get("choice_type"), f"{path}.choice_type", limit=64),
        "source_entity_id": _positive_choice_entity_id(
            raw.get("source_entity_id"), f"{path}.source_entity_id"
        ),
        "option_entity_ids": _choice_entity_ids(
            raw.get("option_entity_ids"), f"{path}.option_entity_ids"
        ),
        "selected_entity_ids": _choice_entity_ids(
            raw.get("selected_entity_ids"), f"{path}.selected_entity_ids"
        ),
        "status": _enum(raw.get("status"), _CHOICE_ITEM_STATUSES, f"{path}.status"),
    }


def _action(value: Any, *, strict: bool) -> dict[str, Any]:
    raw = _mapping(value, "behavior.action")
    if strict:
        _strict_keys(raw, _ACTION_KEYS, _ACTION_REQUIRED_KEYS, "behavior.action")
    else:
        unknown = sorted({str(key) for key in raw} - _ACTION_KEYS)
        if unknown:
            raise BehaviorValidationError(
                "unknown_field:" + unknown[0], "behavior.action"
            )
    result: dict[str, Any] = {
        "kind": _enum(raw.get("kind"), _ACTION_KINDS, "behavior.action.kind"),
        "source_entity_id": _entity_id(
            raw.get("source_entity_id", ""),
            "behavior.action.source_entity_id",
            allow_empty=True,
        ),
        "target_entity_id": _entity_id(
            raw.get("target_entity_id", ""),
            "behavior.action.target_entity_id",
            allow_empty=True,
        ),
        "card_id": _token(
            raw.get("card_id", ""),
            "behavior.action.card_id",
            allow_empty=True,
            limit=128,
        ),
    }
    if "sub_option" in raw:
        result["sub_option"] = _optional_integer(
            raw.get("sub_option"), "behavior.action.sub_option", minimum=-1
        )
    if "board_position" in raw:
        result["board_position"] = _optional_integer(
            raw.get("board_position"), "behavior.action.board_position", minimum=0
        )
    if "choice_status" in raw:
        result["choice_status"] = _enum(
            raw.get("choice_status"), _CHOICE_STATUSES, "behavior.action.choice_status"
        )
    if "choices" in raw:
        choices_raw = raw.get("choices")
        if not isinstance(choices_raw, Sequence) or isinstance(
            choices_raw, (str, bytes, bytearray)
        ):
            raise BehaviorValidationError("must_be_array", "behavior.action.choices")
        result["choices"] = [
            _choice(item, f"behavior.action.choices[{index}]")
            for index, item in enumerate(choices_raw)
        ]
    return result


def _entities(state: Mapping[str, Any]) -> dict[str, tuple[str, str, Mapping[str, Any]]]:
    result: dict[str, tuple[str, str, Mapping[str, Any]]] = {}
    for role in ("friendly", "opponent"):
        player = state[role]
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping) and entity.get("entity_id"):
                entity_id = str(entity["entity_id"])
                if entity_id in result:
                    raise BehaviorValidationError(
                        "duplicate_entity_id", f"behavior.pre_state.{role}.{zone}"
                    )
                result[entity_id] = (role, zone, entity)
        for zone in ("hand", "board"):
            for index, entity in enumerate(player.get(zone, [])):
                entity_id = str(entity.get("entity_id") or "")
                if not entity_id:
                    continue
                if entity_id in result:
                    raise BehaviorValidationError(
                        "duplicate_entity_id",
                        f"behavior.pre_state.{role}.{zone}[{index}]",
                    )
                result[entity_id] = (role, zone, entity)
    return result


def _validate_source_event(source_event: str, actor_side: str, kind: str) -> None:
    expected_side, expected_kind = _SOURCE_EVENTS[source_event]
    if actor_side != "unknown" and expected_side is not None and actor_side != expected_side:
        raise BehaviorValidationError("source_event_actor_mismatch", "behavior.source_event")
    if expected_kind is not None and kind != expected_kind:
        raise BehaviorValidationError("source_event_action_mismatch", "behavior.source_event")
    if source_event == "unknown" and actor_side != "unknown":
        raise BehaviorValidationError("known_actor_requires_source_event", "behavior.source_event")


def _validate_actor_evidence(actor_side: str, evidence: str, source_event: str) -> None:
    if actor_side == "unknown":
        if evidence != "unknown" or source_event != "unknown":
            raise BehaviorValidationError("unknown_actor_evidence_mismatch")
        return
    if evidence == "unknown":
        raise BehaviorValidationError("known_actor_requires_evidence")
    if evidence == "hdt_player_event" and actor_side != "local":
        raise BehaviorValidationError("actor_evidence_side_mismatch")
    if evidence == "hdt_opponent_event" and actor_side != "opponent":
        raise BehaviorValidationError("actor_evidence_side_mismatch")
    if evidence == "hdt_power_log" and actor_side != "local":
        raise BehaviorValidationError("power_evidence_must_be_local")
    if source_event == "hdt_power_log" and evidence != "hdt_power_log":
        raise BehaviorValidationError("power_source_requires_power_evidence")
    if source_event == "hdt_replay_power" and evidence != "hdt_replay_power":
        raise BehaviorValidationError("replay_source_requires_replay_evidence")
    if evidence == "hdt_replay_power" and source_event != "hdt_replay_power":
        raise BehaviorValidationError("replay_evidence_requires_replay_source")
    if source_event.startswith("player_") and evidence not in {
        "hdt_player_event",
        "source_owner",
    }:
        raise BehaviorValidationError("player_event_evidence_mismatch")
    if source_event.startswith("opponent_") and evidence not in {
        "hdt_opponent_event",
        "source_owner",
    }:
        raise BehaviorValidationError("opponent_event_evidence_mismatch")
    if source_event.startswith("turn_passed_") and evidence != "active_player":
        raise BehaviorValidationError("turn_event_requires_active_player_evidence")


def _validate_action_selection(
    action: Mapping[str, Any], actor_side: str, source_event: str
) -> str:
    choice_status = str(action.get("choice_status", "not_observed"))
    sub_option = action.get("sub_option")
    board_position = action.get("board_position")
    choices = action.get("choices", [])
    if action["kind"] == "end_turn":
        if choice_status not in {"none", "not_observed"}:
            raise BehaviorValidationError(
                "end_turn_choice_status_invalid", "behavior.action.choice_status"
            )
        if sub_option not in {None, -1} or board_position not in {None, 0} or choices:
            raise BehaviorValidationError("end_turn_has_selection", "behavior.action")
    if choice_status == "not_observed":
        if sub_option is not None or board_position is not None or choices:
            raise BehaviorValidationError(
                "unobserved_choice_has_power_fields", "behavior.action"
            )
        return choice_status
    if choice_status == "none":
        if sub_option not in {None, -1} or choices:
            raise BehaviorValidationError(
                "choice_none_has_selection", "behavior.action"
            )
        return choice_status
    if choice_status == "selected" and (
        actor_side != "local" or source_event != "hdt_power_log"
    ):
        raise BehaviorValidationError(
            "selected_choice_requires_local_power", "behavior.action.choice_status"
        )
    if choice_status == "selected" and not choices:
        raise BehaviorValidationError(
            "selected_choice_missing", "behavior.action.choices"
        )
    source_id = str(action.get("source_entity_id") or "")
    for index, choice in enumerate(choices):
        path = f"behavior.action.choices[{index}]"
        if choice["source_entity_id"] != source_id:
            raise BehaviorValidationError("choice_source_mismatch", path)
        options = choice["option_entity_ids"]
        selected = choice["selected_entity_ids"]
        if any(entity_id not in options for entity_id in selected):
            raise BehaviorValidationError("selected_entity_not_offered", path)
        if choice_status == "selected" and (
            choice["status"] != "selected" or not options or not selected
        ):
            raise BehaviorValidationError("selected_choice_incomplete", path)
    return choice_status


def _validate_action_binding(
    action: Mapping[str, Any],
    pre_state: Mapping[str, Any],
    actor_side: str,
    actor_player_id: str,
    identity_status: str,
    visibility_status: str,
) -> None:
    kind = action["kind"]
    source_id = action["source_entity_id"]
    target_id = action["target_entity_id"]
    card_id = action["card_id"]
    entities = _entities(pre_state)

    if actor_side == "unknown":
        if identity_status != "unknown" or visibility_status != "hidden_source":
            raise BehaviorValidationError("unknown_actor_tier_mismatch")
        if source_id and (source_id not in entities or entities[source_id][0] != actor_player_id):
            raise BehaviorValidationError("source_owner_mismatch", "behavior.action")
        return

    if kind == "end_turn":
        if source_id or target_id or card_id:
            raise BehaviorValidationError("end_turn_must_not_have_entities", "behavior.action")
        if identity_status != "event_only" or visibility_status != "public_pre_state":
            raise BehaviorValidationError("end_turn_tier_mismatch", "behavior.action")
        return

    # HDT's public GameEvents callbacks sometimes prove which side acted and which
    # action kind occurred without exposing a unique entity binding (for example,
    # two identical minions or a card leaving the opponent's hidden hand). Preserve
    # that behavior evidence at an explicitly ineligible tier instead of dropping it.
    if identity_status == "unknown":
        source = entities.get(source_id) if source_id else None
        target = entities.get(target_id) if target_id else None
        if source_id and source is None:
            raise BehaviorValidationError("source_not_in_pre_state", "behavior.action")
        if source is not None:
            source_role, source_zone, source_entity = source
            if source_role != actor_player_id:
                raise BehaviorValidationError(
                    "source_owner_mismatch", "behavior.action"
                )
            if kind == "play_card" and source_zone != "hand":
                raise BehaviorValidationError(
                    "play_source_not_in_hand", "behavior.action"
                )
            if kind == "attack" and source_zone not in {"hero", "board"}:
                raise BehaviorValidationError(
                    "attack_source_not_character", "behavior.action"
                )
            if kind == "hero_power" and source_zone != "hero_power":
                raise BehaviorValidationError(
                    "hero_power_source_mismatch", "behavior.action"
                )
            if kind == "location_activate":
                if source_zone != "board":
                    raise BehaviorValidationError(
                        "location_source_not_on_board", "behavior.action"
                    )
                if source_entity.get("card_type") != "LOCATION":
                    raise BehaviorValidationError(
                        "location_source_not_location", "behavior.action"
                    )
            public_card_id = str(source_entity.get("card_id") or "")
            if public_card_id and card_id and public_card_id != card_id:
                raise BehaviorValidationError(
                    "source_card_id_mismatch", "behavior.action"
                )
        if target_id and target is None:
            raise BehaviorValidationError("target_not_in_pre_state", "behavior.action")
        if kind == "attack" and target is not None:
            target_role, target_zone, _ = target
            if target_role == actor_player_id or target_zone not in {"hero", "board"}:
                raise BehaviorValidationError(
                    "attack_target_not_enemy_character", "behavior.action"
                )
        return

    if not source_id or source_id not in entities:
        raise BehaviorValidationError("source_not_in_pre_state", "behavior.action")
    source_role, source_zone, source = entities[source_id]
    if source_role != actor_player_id:
        raise BehaviorValidationError("source_owner_mismatch", "behavior.action")
    if target_id and target_id not in entities:
        raise BehaviorValidationError("target_not_in_pre_state", "behavior.action")

    if kind == "play_card":
        if source_zone != "hand":
            raise BehaviorValidationError("play_source_not_in_hand", "behavior.action")
        if not card_id:
            raise BehaviorValidationError("play_card_id_required", "behavior.action")
        if actor_side == "opponent":
            if source.get("visibility") != "hidden":
                raise BehaviorValidationError("opponent_hand_source_must_be_hidden")
            if (
                identity_status != "revealed_after_action"
                or visibility_status != "revealed_post_action"
            ):
                raise BehaviorValidationError("opponent_hidden_play_tier_mismatch")
        else:
            if (
                identity_status != "exact_public_entity"
                or visibility_status != "public_pre_state"
            ):
                raise BehaviorValidationError("local_play_tier_mismatch")
            if card_id != source.get("card_id"):
                raise BehaviorValidationError("source_card_id_mismatch", "behavior.action")
        return

    if identity_status != "exact_public_entity" or visibility_status != "public_pre_state":
        raise BehaviorValidationError("public_action_tier_mismatch", "behavior.action")
    if not card_id or card_id != source.get("card_id"):
        raise BehaviorValidationError("source_card_id_mismatch", "behavior.action")
    if kind == "attack":
        if source_zone not in {"hero", "board"}:
            raise BehaviorValidationError("attack_source_not_character", "behavior.action")
        if not target_id:
            raise BehaviorValidationError("attack_target_required", "behavior.action")
        target_role, target_zone, _ = entities[target_id]
        if target_role == actor_player_id or target_zone not in {"hero", "board"}:
            raise BehaviorValidationError("attack_target_not_enemy_character", "behavior.action")
    elif kind == "hero_power" and source_zone != "hero_power":
        raise BehaviorValidationError("hero_power_source_mismatch", "behavior.action")
    elif kind == "location_activate":
        if source_zone != "board":
            raise BehaviorValidationError(
                "location_source_not_on_board", "behavior.action"
            )
        if source.get("card_type") != "LOCATION":
            raise BehaviorValidationError(
                "location_source_not_location", "behavior.action"
            )


def _computed_behavior_eligible(
    *,
    actor_side: str,
    actor_evidence: str,
    identity_status: str,
    visibility_status: str,
    boundary_status: str,
    action_kind: str,
    choice_status: str,
    post_state_present: bool,
) -> bool:
    if (
        actor_side == "unknown"
        or actor_evidence == "unknown"
        or boundary_status != "isolated"
        or choice_status == "unresolved"
        or not post_state_present
    ):
        return False
    if action_kind == "end_turn":
        return identity_status == "event_only" and visibility_status == "public_pre_state"
    if identity_status == "exact_public_entity":
        return visibility_status == "public_pre_state"
    return (
        actor_side == "opponent"
        and action_kind == "play_card"
        and identity_status == "revealed_after_action"
        and visibility_status == "revealed_post_action"
    )


def _normalized_content(value: Mapping[str, Any], *, strict: bool) -> dict[str, Any]:
    if strict:
        _strict_keys(value, _CONTENT_KEYS, _CONTENT_KEYS, "behavior")
    schema = _text(value.get("schema"), "behavior.schema")
    if schema != BEHAVIOR_SCHEMA_ID:
        raise BehaviorValidationError("wrong_schema", "behavior.schema")
    game_id = _text(value.get("game_id"), "behavior.game_id")
    if _ANONYMOUS_GAME_ID.fullmatch(game_id) is None:
        raise BehaviorValidationError("game_id_not_anonymous", "behavior.game_id")
    actor_side = _enum(value.get("actor_side"), _ACTOR_SIDES, "behavior.actor_side")
    actor_player_id = _enum(
        value.get("actor_player_id"), _ACTOR_PLAYER_IDS, "behavior.actor_player_id"
    )
    actor_evidence = _enum(
        value.get("actor_evidence"), _ACTOR_EVIDENCE, "behavior.actor_evidence"
    )
    identity_status = _enum(
        value.get("identity_status"), _IDENTITY_STATUSES, "behavior.identity_status"
    )
    visibility_status = _enum(
        value.get("visibility_status"),
        _VISIBILITY_STATUSES,
        "behavior.visibility_status",
    )
    boundary_status = _enum(
        value.get("boundary_status"), _BOUNDARY_STATUSES, "behavior.boundary_status"
    )
    source_event = _enum(
        value.get("source_event"), set(_SOURCE_EVENTS), "behavior.source_event"
    )
    action = _action(value.get("action"), strict=strict)
    pre_state = public_behavior_state(value.get("pre_state"), strict=strict)
    post_raw = value.get("post_state")
    post_state = (
        None if post_raw is None else public_behavior_state(post_raw, strict=strict)
    )
    if actor_player_id != pre_state["active_player_id"]:
        raise BehaviorValidationError(
            "actor_not_active_player", "behavior.actor_player_id"
        )
    computed_side = (
        "local"
        if actor_player_id == pre_state["perspective_player_id"]
        else "opponent"
    )
    if actor_side != "unknown" and actor_side != computed_side:
        raise BehaviorValidationError("actor_side_mismatch", "behavior.actor_side")
    _validate_source_event(source_event, actor_side, action["kind"])
    _validate_actor_evidence(actor_side, actor_evidence, source_event)
    choice_status = _validate_action_selection(action, actor_side, source_event)
    _validate_action_binding(
        action,
        pre_state,
        actor_side,
        actor_player_id,
        identity_status,
        visibility_status,
    )
    behavior_eligible = _boolean(
        value.get("behavior_eligible"), "behavior.behavior_eligible"
    )
    expected_eligibility = _computed_behavior_eligible(
        actor_side=actor_side,
        actor_evidence=actor_evidence,
        identity_status=identity_status,
        visibility_status=visibility_status,
        boundary_status=boundary_status,
        action_kind=action["kind"],
        choice_status=choice_status,
        post_state_present=post_state is not None,
    )
    if behavior_eligible != expected_eligibility:
        raise BehaviorValidationError(
            "behavior_eligibility_mismatch", "behavior.behavior_eligible"
        )
    if value.get("rl_training_eligible") is not False:
        raise BehaviorValidationError(
            "rl_training_eligible_must_be_false",
            "behavior.rl_training_eligible",
        )
    return {
        "schema": schema,
        "game_id": game_id,
        "behavior_sequence": _integer(
            value.get("behavior_sequence"), "behavior.behavior_sequence", minimum=1
        ),
        "observed_at_utc": _timestamp(
            value.get("observed_at_utc"), "behavior.observed_at_utc"
        ),
        "actor_side": actor_side,
        "actor_player_id": actor_player_id,
        "actor_evidence": actor_evidence,
        "identity_status": identity_status,
        "visibility_status": visibility_status,
        "boundary_status": boundary_status,
        "source_event": source_event,
        "action": action,
        "pre_state": pre_state,
        "post_state": post_state,
        "behavior_eligible": behavior_eligible,
        "rl_training_eligible": False,
    }


@dataclass(frozen=True)
class BehaviorRecord:
    value: dict[str, Any]

    @classmethod
    def from_dict(cls, value: Any) -> "BehaviorRecord":
        raw = _mapping(value, "behavior")
        _strict_keys(raw, _TOP_LEVEL_KEYS, _TOP_LEVEL_KEYS, "behavior")
        content = _normalized_content(
            {key: raw[key] for key in _CONTENT_KEYS}, strict=True
        )
        digest = _content_digest(content)
        content_sha256 = _text(
            raw.get("content_sha256"), "behavior.content_sha256", limit=64
        )
        behavior_id = _text(raw.get("behavior_id"), "behavior.behavior_id", limit=73)
        if _HEX_SHA256.fullmatch(content_sha256) is None or content_sha256 != digest:
            raise BehaviorValidationError(
                "content_sha256_mismatch", "behavior.content_sha256"
            )
        if _BEHAVIOR_ID.fullmatch(behavior_id) is None or behavior_id != "behavior-" + digest:
            raise BehaviorValidationError("behavior_id_mismatch", "behavior.behavior_id")
        result = dict(content)
        result["behavior_id"] = behavior_id
        result["content_sha256"] = content_sha256
        ordered = {key: result[key] for key in sorted(_TOP_LEVEL_KEYS)}
        return cls(ordered)

    @property
    def behavior_id(self) -> str:
        return str(self.value["behavior_id"])

    @property
    def content_sha256(self) -> str:
        return str(self.value["content_sha256"])

    @property
    def game_id(self) -> str:
        return str(self.value["game_id"])

    @property
    def behavior_sequence(self) -> int:
        return int(self.value["behavior_sequence"])

    def to_dict(self) -> dict[str, Any]:
        return copy.deepcopy(self.value)


def create_behavior_record(
    *,
    game_id: Any,
    behavior_sequence: int,
    observed_at_utc: str,
    actor_side: str,
    actor_player_id: str,
    actor_evidence: str,
    identity_status: str,
    visibility_status: str,
    boundary_status: str,
    source_event: str,
    action: Mapping[str, Any],
    pre_state: Mapping[str, Any],
    post_state: Mapping[str, Any] | None = None,
    behavior_eligible: bool | None = None,
    rl_training_eligible: bool = False,
) -> BehaviorRecord:
    public_pre = public_behavior_state(pre_state)
    public_post = None if post_state is None else public_behavior_state(post_state)
    normalized_action = _action(action, strict=False)
    normalized_side = _enum(actor_side, _ACTOR_SIDES, "behavior.actor_side")
    normalized_evidence = _enum(
        actor_evidence, _ACTOR_EVIDENCE, "behavior.actor_evidence"
    )
    normalized_identity = _enum(
        identity_status, _IDENTITY_STATUSES, "behavior.identity_status"
    )
    normalized_visibility = _enum(
        visibility_status, _VISIBILITY_STATUSES, "behavior.visibility_status"
    )
    normalized_boundary = _enum(
        boundary_status, _BOUNDARY_STATUSES, "behavior.boundary_status"
    )
    expected_eligible = _computed_behavior_eligible(
        actor_side=normalized_side,
        actor_evidence=normalized_evidence,
        identity_status=normalized_identity,
        visibility_status=normalized_visibility,
        boundary_status=normalized_boundary,
        action_kind=normalized_action["kind"],
        choice_status=str(normalized_action.get("choice_status", "not_observed")),
        post_state_present=public_post is not None,
    )
    declared_eligible = expected_eligible if behavior_eligible is None else behavior_eligible
    content = _normalized_content(
        {
            "schema": BEHAVIOR_SCHEMA_ID,
            "game_id": anonymous_game_id(game_id),
            "behavior_sequence": behavior_sequence,
            "observed_at_utc": observed_at_utc,
            "actor_side": normalized_side,
            "actor_player_id": actor_player_id,
            "actor_evidence": normalized_evidence,
            "identity_status": normalized_identity,
            "visibility_status": normalized_visibility,
            "boundary_status": normalized_boundary,
            "source_event": source_event,
            "action": normalized_action,
            "pre_state": public_pre,
            "post_state": public_post,
            "behavior_eligible": declared_eligible,
            "rl_training_eligible": rl_training_eligible,
        },
        strict=True,
    )
    digest = _content_digest(content)
    payload = dict(content)
    payload["behavior_id"] = "behavior-" + digest
    payload["content_sha256"] = digest
    return BehaviorRecord.from_dict(payload)


_PATH_LOCKS_GUARD = threading.Lock()
_PATH_LOCKS: dict[str, threading.RLock] = {}


def _normalized_path_key(path: Path) -> str:
    return str(path.resolve(strict=False)).lower()


def _path_lock(path: Path) -> threading.RLock:
    key = _normalized_path_key(path)
    with _PATH_LOCKS_GUARD:
        lock = _PATH_LOCKS.get(key)
        if lock is None:
            lock = threading.RLock()
            _PATH_LOCKS[key] = lock
        return lock


def behavior_path_for_training_log(path: str | Path | None) -> Path | None:
    """Derive the behavior corpus beside a distinct trajectory log path."""

    if path is None:
        return None
    training_path = Path(path)
    try:
        behavior_path = training_path.with_name(BEHAVIOR_CORPUS_FILENAME)
    except ValueError as exc:
        raise BehaviorCorpusError(
            "behavior_corpus_path_must_be_independent", str(training_path)
        ) from exc
    if _normalized_path_key(training_path) == _normalized_path_key(behavior_path):
        raise BehaviorCorpusError(
            "behavior_corpus_path_must_be_independent", str(training_path)
        )
    return behavior_path


def _behavior_file_stamp(path: Path) -> tuple[int, int, int, int, int]:
    """Return cheap identity/change metadata for the active corpus path."""

    try:
        stat = path.stat()
    except FileNotFoundError:
        return (0, 0, 0, 0, 0)
    return (
        stat.st_size,
        stat.st_mtime_ns,
        stat.st_ctime_ns,
        stat.st_dev,
        stat.st_ino,
    )


class BehaviorCorpus:
    """Content-addressed, idempotent JSONL store kept separate from trajectory logs."""

    def __init__(self, path: str | Path):
        self.path = Path(path)
        if self.path.name.lower() in _RESERVED_TRAJECTORY_FILENAMES:
            raise BehaviorCorpusError(
                "behavior_corpus_path_must_be_independent", str(self.path)
            )
        self._lock = _path_lock(self.path)
        self._known_stamp: tuple[int, int, int, int, int] | None = None
        self._by_id: dict[str, str] = {}
        self._by_sequence: dict[tuple[str, int], str] = {}
        self._max_sequence: dict[str, int] = {}
        self._startup_error = ""
        with self._lock:
            try:
                self._reload_locked()
            except BehaviorCorpusError as exc:
                # Keep the service available so /v1/health can report the existing corpus
                # as unhealthy before any new append is attempted. The next append reloads
                # the path again and can recover after an operator repairs the file.
                self._invalidate_index_locked()
                self._startup_error = exc.code

    @property
    def startup_error(self) -> str:
        """Return a bounded, non-sensitive code from the one-time startup validation."""

        return self._startup_error

    @classmethod
    def for_data_directory(cls, directory: str | Path) -> "BehaviorCorpus":
        return cls(Path(directory) / BEHAVIOR_CORPUS_FILENAME)

    def _reload_locked(self) -> None:
        if not self.path.exists():
            self._by_id.clear()
            self._by_sequence.clear()
            self._max_sequence.clear()
            self._known_stamp = _behavior_file_stamp(self.path)
            return
        by_id: dict[str, str] = {}
        by_sequence: dict[tuple[str, int], str] = {}
        sequences: dict[str, set[int]] = defaultdict(set)
        try:
            self._repair_torn_tail_locked()
            expected_stamp = _behavior_file_stamp(self.path)
            expected_size = expected_stamp[0]
            with self.path.open("r", encoding="utf-8") as handle:
                for line_number, line in enumerate(handle, 1):
                    if not line.strip():
                        raise BehaviorCorpusError(
                            "blank_jsonl_line", f"{self.path}:{line_number}"
                        )
                    try:
                        raw = json.loads(line)
                        record = BehaviorRecord.from_dict(raw)
                    except (json.JSONDecodeError, BehaviorValidationError) as exc:
                        raise BehaviorCorpusError(
                            "existing_corpus_invalid", f"{self.path}:{line_number}"
                        ) from exc
                    key = (record.game_id, record.behavior_sequence)
                    if record.behavior_id in by_id or key in by_sequence:
                        raise BehaviorCorpusError(
                            "existing_corpus_duplicate", f"{self.path}:{line_number}"
                        )
                    by_id[record.behavior_id] = record.content_sha256
                    by_sequence[key] = record.content_sha256
                    sequences[record.game_id].add(record.behavior_sequence)
            max_sequence: dict[str, int] = {}
            for game_id, values in sequences.items():
                maximum = max(values, default=0)
                if values != set(range(1, maximum + 1)):
                    raise BehaviorCorpusError(
                        "existing_behavior_sequence_not_contiguous", game_id
                    )
                max_sequence[game_id] = maximum
            # Rebuilding the content-addressed index is a new durability trust
            # boundary, including after a worker restart where an earlier fsync
            # failure cannot be remembered.  Sync the active corpus before any
            # record discovered here may receive a duplicate ACK.
            with self.path.open("r+b") as handle:
                handle.flush()
                os.fsync(handle.fileno())
                size = handle.seek(0, os.SEEK_END)
            current_stamp = _behavior_file_stamp(self.path)
            if size != expected_size or current_stamp != expected_stamp:
                raise OSError("behavior corpus changed during reload")
            self._by_id = by_id
            self._by_sequence = by_sequence
            self._max_sequence = max_sequence
            self._known_stamp = current_stamp
        except (OSError, UnicodeError) as exc:
            raise BehaviorCorpusError("corpus_read_failed", str(self.path)) from exc

    def _ensure_current_locked(self) -> None:
        try:
            stamp = _behavior_file_stamp(self.path)
        except OSError as exc:
            self._invalidate_index_locked()
            raise BehaviorCorpusError("corpus_stat_failed", str(self.path)) from exc
        if stamp != self._known_stamp:
            try:
                self._reload_locked()
            except BehaviorCorpusError:
                self._invalidate_index_locked()
                raise

    def _invalidate_index_locked(self) -> None:
        self._known_stamp = None
        self._by_id.clear()
        self._by_sequence.clear()
        self._max_sequence.clear()

    def append(self, value: BehaviorRecord | Mapping[str, Any]) -> bool:
        record = value if isinstance(value, BehaviorRecord) else BehaviorRecord.from_dict(value)
        line = (_canonical_json(record.to_dict()) + "\n").encode("utf-8")
        with self._lock:
            self._ensure_current_locked()
            key = (record.game_id, record.behavior_sequence)
            existing_id = self._by_id.get(record.behavior_id)
            existing_sequence = self._by_sequence.get(key)
            if existing_id is not None:
                if existing_id != record.content_sha256:
                    raise BehaviorCorpusError("behavior_id_conflict", record.behavior_id)
                if existing_sequence == record.content_sha256:
                    return False
                raise BehaviorCorpusError("behavior_id_sequence_conflict", record.behavior_id)
            if existing_sequence is not None:
                if existing_sequence == record.content_sha256:
                    return False
                raise BehaviorCorpusError(
                    "behavior_sequence_conflict",
                    f"{record.game_id}:{record.behavior_sequence}",
                )
            expected_sequence = self._max_sequence.get(record.game_id, 0) + 1
            if record.behavior_sequence != expected_sequence:
                raise BehaviorCorpusError(
                    "behavior_sequence_out_of_order",
                    f"{record.game_id}:{record.behavior_sequence}",
                )
            try:
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with self.path.open("ab", buffering=0) as handle:
                    written = handle.write(line)
                    if written != len(line):
                        raise OSError("short JSONL write")
                    handle.flush()
                    os.fsync(handle.fileno())
                self._known_stamp = _behavior_file_stamp(self.path)
            except OSError as exc:
                self._invalidate_index_locked()
                raise BehaviorCorpusError("corpus_append_failed", str(self.path)) from exc
            self._by_id[record.behavior_id] = record.content_sha256
            self._by_sequence[key] = record.content_sha256
            self._max_sequence[record.game_id] = record.behavior_sequence
            return True

    def _repair_torn_tail_locked(self) -> bool:
        if not self.path.exists():
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
            # The JSON object is complete and only its JSONL delimiter is
            # missing. Preserve it so the rebuilt index can recognize a retry.
            with self.path.open("r+b") as active:
                self._verify_tail_unchanged_locked(
                    active, original_size, cutoff, fragment
                )
                active.seek(0, os.SEEK_END)
                active.write(b"\n")
                active.flush()
                os.fsync(active.fileno())
            return True

        self._archive_torn_fragment_locked(fragment)
        with self.path.open("r+b") as active:
            self._verify_tail_unchanged_locked(active, original_size, cutoff, fragment)
            active.truncate(cutoff)
            active.flush()
            os.fsync(active.fileno())
        return True

    @staticmethod
    def _verify_tail_unchanged_locked(
        active: Any, original_size: int, cutoff: int, fragment: bytes
    ) -> None:
        active.seek(0, os.SEEK_END)
        if active.tell() != original_size:
            raise OSError("behavior corpus changed during tail recovery")
        active.seek(cutoff)
        if active.read() != fragment:
            raise OSError("behavior tail changed during recovery")

    def _archive_torn_fragment_locked(self, fragment: bytes) -> Path:
        digest = hashlib.sha256(fragment).hexdigest()
        archive = self.path.with_name(
            f"{self.path.name}.torn-tail.{digest}.fragment"
        )
        if archive.exists():
            if archive.read_bytes() != fragment:
                raise OSError("behavior tail archive content mismatch")
            return archive

        temporary = archive.with_name(
            f".{archive.name}.{os.getpid()}.{threading.get_ident()}.tmp"
        )
        try:
            with temporary.open("xb") as handle:
                written = handle.write(fragment)
                if written != len(fragment):
                    raise OSError("short behavior tail archive write")
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


def audit_behavior_corpus(path: str | Path) -> dict[str, Any]:
    corpus_path = Path(path)
    issues: list[dict[str, Any]] = []
    side_counts: Counter[str] = Counter()
    identity_counts: Counter[str] = Counter()
    boundary_counts: Counter[str] = Counter()
    choice_status_counts: Counter[str] = Counter()
    sequences: dict[str, set[int]] = defaultdict(set)
    seen_ids: dict[str, str] = {}
    seen_sequences: dict[tuple[str, int], str] = {}
    record_count = 0
    valid_record_count = 0
    behavior_eligible_count = 0
    duplicate_behavior_id_count = 0
    duplicate_sequence_count = 0
    privacy_violation_count = 0
    board_position_record_count = 0
    choice_item_count = 0
    offered_choice_entity_count = 0
    selected_choice_entity_count = 0

    if corpus_path.exists():
        try:
            lines = corpus_path.read_text(encoding="utf-8").splitlines()
        except (OSError, UnicodeError) as exc:
            return {
                "schema": BEHAVIOR_SCHEMA_ID,
                "path": str(corpus_path),
                "valid": False,
                "record_count": 0,
                "valid_record_count": 0,
                "behavior_eligible_count": 0,
                "duplicate_behavior_id_count": 0,
                "duplicate_sequence_count": 0,
                "non_contiguous_game_count": 0,
                "privacy_violation_count": 0,
                "actor_side_counts": {},
                "identity_status_counts": {},
                "boundary_status_counts": {},
                "choice_status_counts": {},
                "board_position_record_count": 0,
                "choice_item_count": 0,
                "offered_choice_entity_count": 0,
                "selected_choice_entity_count": 0,
                "issues": [{"line": 0, "reason": type(exc).__name__}],
            }
    else:
        lines = []

    for line_number, line in enumerate(lines, 1):
        record_count += 1
        if not line.strip():
            issues.append({"line": line_number, "reason": "blank_jsonl_line"})
            continue
        try:
            record = BehaviorRecord.from_dict(json.loads(line))
        except json.JSONDecodeError:
            issues.append({"line": line_number, "reason": "invalid_json"})
            continue
        except BehaviorValidationError as exc:
            issues.append({"line": line_number, "reason": exc.code, "path": exc.path})
            if any(
                token in exc.code or token in exc.path.lower()
                for token in ("unknown_field", "unsafe_token", "credential", "name", "controller", "power_log")
            ):
                privacy_violation_count += 1
            continue

        valid_record_count += 1
        side_counts[str(record.value["actor_side"])] += 1
        identity_counts[str(record.value["identity_status"])] += 1
        boundary_counts[str(record.value["boundary_status"])] += 1
        action = record.value["action"]
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
        if record.value["behavior_eligible"] is True:
            behavior_eligible_count += 1
        key = (record.game_id, record.behavior_sequence)
        if record.behavior_id in seen_ids:
            duplicate_behavior_id_count += 1
            issues.append({"line": line_number, "reason": "duplicate_behavior_id"})
        else:
            seen_ids[record.behavior_id] = record.content_sha256
        if key in seen_sequences:
            duplicate_sequence_count += 1
            issues.append({"line": line_number, "reason": "duplicate_behavior_sequence"})
        else:
            seen_sequences[key] = record.content_sha256
        sequences[record.game_id].add(record.behavior_sequence)

    non_contiguous_games: list[str] = []
    for game_id, values in sequences.items():
        if values and values != set(range(1, max(values) + 1)):
            non_contiguous_games.append(game_id)
            issues.append({"line": 0, "reason": "non_contiguous_behavior_sequence", "game_id": game_id})

    return {
        "schema": BEHAVIOR_SCHEMA_ID,
        "path": str(corpus_path),
        "valid": not issues,
        "record_count": record_count,
        "valid_record_count": valid_record_count,
        "behavior_eligible_count": behavior_eligible_count,
        "duplicate_behavior_id_count": duplicate_behavior_id_count,
        "duplicate_sequence_count": duplicate_sequence_count,
        "non_contiguous_game_count": len(non_contiguous_games),
        "privacy_violation_count": privacy_violation_count,
        "actor_side_counts": dict(sorted(side_counts.items())),
        "identity_status_counts": dict(sorted(identity_counts.items())),
        "boundary_status_counts": dict(sorted(boundary_counts.items())),
        "choice_status_counts": dict(sorted(choice_status_counts.items())),
        "board_position_record_count": board_position_record_count,
        "choice_item_count": choice_item_count,
        "offered_choice_entity_count": offered_choice_entity_count,
        "selected_choice_entity_count": selected_choice_entity_count,
        "issues": issues,
    }
