from __future__ import annotations

import hashlib
import html
import re
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import Any, Mapping, Sequence

from . import API_VERSION
from .errors import SchemaError


JSONScalar = str | int | float | bool | None


# A producer may attach a settled post-state candidate to an HDT callback without
# claiming that the callback identified one exact Hearthstone action.  These
# constants intentionally describe an *unverified* evidence tier.  Only the
# offline trajectory auditor can later produce a verified-transition manifest.
TRANSITION_CANDIDATE_CAPTURE_CONTRACT = "partial_hdt_transition_candidate_v1"
TRANSITION_CANDIDATE_STATUS = "post_state_candidate_unverified"
TRANSITION_CANDIDATE_VERIFICATION = "producer_candidate_unverified"
TRANSITION_CANDIDATE_COMPLETENESS = "partial_hdt_gameevents_v1"
POWER_ACTION_IDENTITY_CAPTURE_CONTRACT = "hdt_power_action_identity_v1"
POWER_ACTION_IDENTITY_COMPLETENESS = "exact_action_identity_unverified_transition_v1"
POWER_ACTION_IDENTITY_STATUS = "exact_hdt_power_v1"
POWER_ACTION_IDENTITY_CHOICE_STATUS = "none"
POWER_ACTION_IDENTITY_SIMULATOR_STATUS = "not_replayed"
TRANSITION_CANDIDATE_BOUNDARY_STATUSES = {
    "isolated",
    "overlapped",
    "unstable",
}
TRANSITION_CANDIDATE_ENVELOPE_FIELDS = (
    "pre_state_id",
    "post_state_id",
    "raw_pre_snapshot_hash",
    "raw_post_snapshot_hash",
    "pre_state_hash",
    "post_state_hash",
    "pre_snapshot_sequence",
    "post_snapshot_sequence",
    "boundary_status",
    "intervening_action_count",
    "capture_warning_count",
    "transition_verification",
    "action_identity_status",
    "choice_status",
    "simulator_status",
    "game_generation",
    "power_collector_epoch",
    "power_action_ordinal",
    "power_gap_count",
)


_HIDDEN_OPPONENT_ZONE_NAMES = {"HAND", "DECK", "SETASIDE", "SECRET"}
_HIDDEN_OPPONENT_ZONE_IDS = {2, 3, 6, 7}
_ZONE_CONTAINER_HINTS = {
    "hand": "HAND",
    "deck": "DECK",
    "setaside": "SETASIDE",
    "set_aside": "SETASIDE",
    "secret": "SECRET",
    "secrets": "SECRET",
}
_PUBLIC_ENTITY_LOCATION_KEYS = {
    "entity_id",
    "zone",
    "zone_id",
    "zone_position",
    "position",
    "controller",
    "controller_id",
    "visibility",
}
_PUBLIC_ENTITY_LOCATION_TAGS = {"ZONE", "ZONE_POSITION", "CONTROLLER"}


def _object(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise SchemaError(path, "must be an object")
    return value


def _sequence(value: Any, path: str) -> Sequence[Any]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise SchemaError(path, "must be an array")
    return value


def _str(raw: Mapping[str, Any], key: str, path: str, *, default: str | None = None) -> str:
    value = raw.get(key, default)
    if not isinstance(value, str) or not value.strip():
        raise SchemaError(f"{path}.{key}", "must be a non-empty string")
    return value.strip()


def _int(
    raw: Mapping[str, Any],
    key: str,
    path: str,
    *,
    default: int | None = None,
    minimum: int | None = None,
) -> int:
    value = raw.get(key, default)
    if isinstance(value, bool) or not isinstance(value, int):
        raise SchemaError(f"{path}.{key}", "must be an integer")
    if minimum is not None and value < minimum:
        raise SchemaError(f"{path}.{key}", f"must be at least {minimum}")
    return value


def _bool(raw: Mapping[str, Any], key: str, path: str, *, default: bool = False) -> bool:
    value = raw.get(key, default)
    if not isinstance(value, bool):
        raise SchemaError(f"{path}.{key}", "must be a boolean")
    return value


def _json_metadata(value: Any, path: str) -> dict[str, JSONScalar]:
    if value is None:
        return {}
    raw = _object(value, path)
    result: dict[str, JSONScalar] = {}
    for key, item in raw.items():
        if not isinstance(key, str):
            raise SchemaError(path, "metadata keys must be strings")
        if item is not None and not isinstance(item, (str, int, float, bool)):
            raise SchemaError(f"{path}.{key}", "metadata values must be JSON scalars")
        result[key] = item
    return result


def validate_result_metadata(
    metadata: Mapping[str, Any], path: str = "request.metadata"
) -> None:
    """Keep terminal-result content addressing stable across worker backends."""

    for key, item in metadata.items():
        if not isinstance(key, str):
            raise SchemaError(path, "metadata keys must be strings")
        if item is not None and not isinstance(item, (str, bool, int)):
            raise SchemaError(
                f"{path}.{key}",
                "must be a string, boolean, integer, or null for result observations",
            )


def _json_value(value: Any, path: str) -> Any:
    """Validate and detach an arbitrary JSON value.

    Observation choice evidence is intentionally richer than scalar request metadata.
    Keeping the validator here prevents a custom Mapping/Sequence object from leaking
    into the append-only logger while preserving the exact JSON structure supplied by
    the HDT PowerLog adapter.
    """

    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    if isinstance(value, Mapping):
        result: dict[str, Any] = {}
        for key, item in value.items():
            if not isinstance(key, str):
                raise SchemaError(path, "object keys must be strings")
            result[key] = _json_value(item, f"{path}.{key}")
        return result
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes, bytearray)):
        return [_json_value(item, f"{path}[{index}]") for index, item in enumerate(value)]
    raise SchemaError(path, "must contain only JSON values")


def _action_choice_evidence(value: Any, path: str) -> list[dict[str, Any]]:
    """Accept only normalized choice fields; raw PowerLog text is never wire data."""

    items = _sequence(value, path)
    allowed = {
        "choice_id",
        "choice_type",
        "source_entity_id",
        "option_entity_ids",
        "selected_entity_ids",
        "selected_index",
        "frame_id",
        "status",
    }
    result: list[dict[str, Any]] = []
    for index, item in enumerate(items):
        item_path = f"{path}[{index}]"
        raw = _object(item, item_path)
        unknown = sorted(set(raw) - allowed)
        if unknown:
            raise SchemaError(item_path, f"unknown fields: {', '.join(unknown)}")
        normalized: dict[str, Any] = {}
        for key, nested in raw.items():
            field_path = f"{item_path}.{key}"
            if key in {"choice_type", "status"}:
                if not isinstance(nested, str):
                    raise SchemaError(field_path, "must be a string")
                normalized[key] = nested
            elif key in {"selected_index"}:
                if nested is not None and (
                    isinstance(nested, bool) or not isinstance(nested, int)
                ):
                    raise SchemaError(field_path, "must be an integer or null")
                normalized[key] = nested
            elif key in {"option_entity_ids", "selected_entity_ids"}:
                entity_ids = _sequence(nested, field_path)
                normalized_ids: list[str | int] = []
                for entity_index, entity_id in enumerate(entity_ids):
                    if isinstance(entity_id, bool) or not isinstance(
                        entity_id, (str, int)
                    ):
                        raise SchemaError(
                            f"{field_path}[{entity_index}]",
                            "must be a string or integer entity ID",
                        )
                    normalized_ids.append(entity_id)
                normalized[key] = normalized_ids
            else:
                if nested is not None and (
                    isinstance(nested, bool) or not isinstance(nested, (str, int))
                ):
                    raise SchemaError(
                        field_path, "must be a string, integer, or null"
                    )
                normalized[key] = nested
        result.append(normalized)
    return result


def is_unverified_transition_candidate(metadata: Mapping[str, Any]) -> bool:
    """Return whether metadata carries any marker from the candidate tier.

    Testing all three markers is deliberate: accidentally changing one producer
    flag must not turn the same raw observation into an exact transition.
    """

    return any(
        str(metadata.get(key) or "").strip().lower() == expected
        for key, expected in (
            ("capture_contract", TRANSITION_CANDIDATE_CAPTURE_CONTRACT),
            ("transition_status", TRANSITION_CANDIDATE_STATUS),
            ("transition_verification", TRANSITION_CANDIDATE_VERIFICATION),
        )
    )


def is_power_action_identity_candidate(metadata: Mapping[str, Any]) -> bool:
    """Return whether the producer claims exact HDT input identity only.

    This evidence tier is deliberately not replayable or training eligible.  It
    becomes a verified transition only in a new immutable corpus after simulator
    replay matches the detached post-state.
    """

    return (
        str(metadata.get("capture_contract") or "").strip().lower()
        == POWER_ACTION_IDENTITY_CAPTURE_CONTRACT
    )


def _metadata_integer(
    metadata: Mapping[str, JSONScalar], key: str, path: str, *, minimum: int
) -> int:
    value = metadata.get(key)
    if isinstance(value, bool):
        raise SchemaError(f"{path}.{key}", "must be an integer")
    try:
        parsed = int(str(value))
    except (TypeError, ValueError) as exc:
        raise SchemaError(f"{path}.{key}", "must be an integer") from exc
    if str(value).strip() != str(parsed):
        raise SchemaError(f"{path}.{key}", "must be an integer")
    if parsed < minimum:
        raise SchemaError(f"{path}.{key}", f"must be at least {minimum}")
    return parsed


def _metadata_explicit_false(value: JSONScalar) -> bool:
    if value is False or (
        isinstance(value, int) and not isinstance(value, bool) and value == 0
    ):
        return True
    return isinstance(value, str) and value.strip().lower() in {"false", "0", "no"}


def _validate_transition_candidate_metadata(
    metadata: Mapping[str, JSONScalar], state_id: str, path: str
) -> None:
    if not is_unverified_transition_candidate(metadata):
        return

    power_identity = is_power_action_identity_candidate(metadata)
    expected_text = {
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
        expected_text.update(
            {
                "action_identity_status": POWER_ACTION_IDENTITY_STATUS,
                "choice_status": POWER_ACTION_IDENTITY_CHOICE_STATUS,
                "simulator_status": POWER_ACTION_IDENTITY_SIMULATOR_STATUS,
            }
        )
        game_generation = _metadata_integer(
            metadata, "game_generation", path, minimum=1
        )
        collector_epoch = _metadata_integer(
            metadata, "power_collector_epoch", path, minimum=1
        )
        action_ordinal = _metadata_integer(
            metadata, "power_action_ordinal", path, minimum=1
        )
        _metadata_integer(metadata, "power_gap_count", path, minimum=0)
        if collector_epoch != game_generation:
            raise SchemaError(
                f"{path}.power_collector_epoch",
                "must match game_generation",
            )
        if action_ordinal != _metadata_integer(
            metadata, "action_sequence", path, minimum=1
        ):
            raise SchemaError(
                f"{path}.power_action_ordinal",
                "must match action_sequence",
            )
    for key, expected in expected_text.items():
        actual = str(metadata.get(key) or "").strip().lower()
        if actual != expected:
            raise SchemaError(f"{path}.{key}", f"must be {expected!r}")
    if "training_eligible" not in metadata or not _metadata_explicit_false(
        metadata.get("training_eligible")
    ):
        raise SchemaError(
            f"{path}.training_eligible",
            "must be explicitly false for an unverified transition candidate",
        )

    pre_state_id = str(metadata.get("pre_state_id") or "").strip()
    post_state_id = str(metadata.get("post_state_id") or "").strip()
    if not pre_state_id:
        raise SchemaError(f"{path}.pre_state_id", "must be a non-empty string")
    if pre_state_id != state_id:
        raise SchemaError(f"{path}.pre_state_id", "must match request.state_id")
    if not post_state_id:
        raise SchemaError(f"{path}.post_state_id", "must be a non-empty string")
    if post_state_id == pre_state_id:
        raise SchemaError(f"{path}.post_state_id", "must differ from pre_state_id")

    for key in ("raw_pre_snapshot_hash", "raw_post_snapshot_hash"):
        value = metadata.get(key)
        if not isinstance(value, str) or len(value) != 64 or any(
            character not in "0123456789abcdef" for character in value
        ):
            raise SchemaError(f"{path}.{key}", "must be a lowercase SHA-256 hex digest")
    for key in ("pre_state_hash", "post_state_hash"):
        if metadata.get(key) not in (None, ""):
            raise SchemaError(
                f"{path}.{key}",
                "is logger-derived and must not be supplied by the producer",
            )

    action_sequence = _metadata_integer(metadata, "action_sequence", path, minimum=1)
    if action_sequence <= 0:  # Kept explicit for readability at the contract boundary.
        raise SchemaError(f"{path}.action_sequence", "must be positive")
    pre_sequence = _metadata_integer(metadata, "pre_snapshot_sequence", path, minimum=1)
    post_sequence = _metadata_integer(metadata, "post_snapshot_sequence", path, minimum=1)
    if post_sequence <= pre_sequence:
        raise SchemaError(
            f"{path}.post_snapshot_sequence",
            "must be greater than pre_snapshot_sequence",
        )
    intervening = _metadata_integer(
        metadata, "intervening_action_count", path, minimum=0
    )
    _metadata_integer(metadata, "capture_warning_count", path, minimum=0)
    boundary = str(metadata.get("boundary_status") or "").strip().lower()
    if boundary not in TRANSITION_CANDIDATE_BOUNDARY_STATUSES:
        raise SchemaError(
            f"{path}.boundary_status",
            "must be isolated, overlapped, or unstable",
        )
    if boundary == "isolated" and intervening != 0:
        raise SchemaError(
            f"{path}.intervening_action_count",
            "must be zero for an isolated boundary",
        )


def _optional_utc_timestamp(value: Any, path: str) -> str:
    if value in (None, ""):
        return ""
    if not isinstance(value, str):
        raise SchemaError(path, "must be an RFC 3339 timestamp string")
    text = value.strip()
    try:
        parsed = datetime.fromisoformat(text[:-1] + "+00:00" if text.endswith("Z") else text)
    except ValueError as exc:
        raise SchemaError(path, "must be a valid RFC 3339 timestamp") from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise SchemaError(path, "must include a UTC offset")
    return text


def _normalized_zone_name(value: Any) -> str:
    if not isinstance(value, str):
        return ""
    return value.strip().upper().replace("_", "").replace("-", "").replace(" ", "")


def _hidden_entity(
    raw: Mapping[str, Any], *, in_opponent: bool, zone_hint: str = ""
) -> bool:
    visibility = raw.get("visibility")
    if isinstance(visibility, str) and "hidden" in visibility.strip().lower():
        return True
    if not in_opponent:
        return False
    if _normalized_zone_name(zone_hint) in _HIDDEN_OPPONENT_ZONE_NAMES:
        return True
    if _normalized_zone_name(raw.get("zone")) in _HIDDEN_OPPONENT_ZONE_NAMES:
        return True
    zone_id = raw.get("zone_id")
    tags = raw.get("tags")
    if zone_id is None and isinstance(tags, Mapping):
        zone_id = tags.get("ZONE") if "ZONE" in tags else tags.get("zone")
    return isinstance(zone_id, int) and not isinstance(zone_id, bool) and zone_id in _HIDDEN_OPPONENT_ZONE_IDS


def _public_hidden_entity_location(raw: Mapping[str, Any]) -> dict[str, Any]:
    result = {
        str(key): item
        for key, item in raw.items()
        if str(key).lower() in _PUBLIC_ENTITY_LOCATION_KEYS
    }
    tags = raw.get("tags")
    if isinstance(tags, Mapping):
        safe_tags = {
            str(key): item
            for key, item in tags.items()
            if str(key).upper() in _PUBLIC_ENTITY_LOCATION_TAGS
            and (item is None or isinstance(item, (str, int, float, bool)))
        }
        if safe_tags:
            result["tags"] = safe_tags
    result["visibility"] = "hidden"
    return result


def redact_hidden_entities(value: Any) -> Any:
    """Remove exact hidden-card data while retaining public entity location fields.

    Opponent hand/deck/set-aside/secret containers are private regardless of whether
    an upstream caller accidentally marks their entities as known. An explicit
    ``visibility=hidden`` marker is also honored anywhere in a payload. The function
    returns a detached structure and never mutates the caller's object.
    """

    def visit(item: Any, *, in_opponent: bool = False, zone_hint: str = "") -> Any:
        if isinstance(item, Mapping):
            if _hidden_entity(item, in_opponent=in_opponent, zone_hint=zone_hint):
                return _public_hidden_entity_location(item)
            result: dict[str, Any] = {}
            for key, child in item.items():
                key_text = str(key)
                normalized_key = key_text.lower()
                child_in_opponent = in_opponent or normalized_key == "opponent"
                child_zone = (
                    _ZONE_CONTAINER_HINTS.get(normalized_key, "") if child_in_opponent else ""
                )
                result[key_text] = visit(
                    child,
                    in_opponent=child_in_opponent,
                    zone_hint=child_zone,
                )
            return result
        if isinstance(item, (list, tuple)):
            return [visit(child, in_opponent=in_opponent, zone_hint=zone_hint) for child in item]
        return item

    return visit(value)


class CardType(str, Enum):
    HERO = "HERO"
    MINION = "MINION"
    SPELL = "SPELL"
    WEAPON = "WEAPON"
    HERO_POWER = "HERO_POWER"
    LOCATION = "LOCATION"
    UNKNOWN = "UNKNOWN"


class ActionKind(str, Enum):
    PLAY_CARD = "play_card"
    ATTACK = "attack"
    HERO_POWER = "hero_power"
    LOCATION_ACTIVATE = "location_activate"
    END_TURN = "end_turn"


SUPPORTED_TARGET_MODES = {
    "none",
    "self",
    "enemy_character",
    "friendly_character",
    "any_character",
    "enemy_minion",
    "friendly_minion",
    "any_minion",
    "any_undamaged_minion",
    "damaged_enemy_minion",
    "enemy_hero",
    "friendly_hero",
    "all_enemy_characters",
    "all_friendly_characters",
    "all_enemy_minions",
    "all_friendly_minions",
    "all_minions",
    "all_characters",
    "all_other_minions",
    "all_other_friendly_minions",
}


@dataclass(frozen=True)
class Effect:
    kind: str
    trigger: str = "resolution"
    amount: int = 0
    target: str = "none"
    count: int = 1
    card_id: str = ""
    name: str = "Generated minion"
    attack: int = 0
    health: int = 1
    random: bool = False
    rush: bool = False
    taunt: bool = False
    divine_shield: bool = False
    stealth: bool = False
    poisonous: bool = False
    lifesteal: bool = False
    windfury: bool = False
    charge: bool = False
    reborn: bool = False
    hand_count_at_most: int | None = None
    summoned_card_effects_unmodeled: bool = False

    @classmethod
    def from_dict(cls, value: Any, path: str) -> "Effect":
        raw = _object(value, path)
        kind = _str(raw, "kind", path).lower()
        trigger = str(raw.get("trigger", "resolution")).strip().lower()
        if trigger not in {
            "resolution",
            "deathrattle",
            "after_spell_cast",
            "spellburst",
            "frenzy",
            "after_hero_attack",
            "after_hero_power",
            "turn_start",
            "turn_end",
        }:
            raise SchemaError(f"{path}.trigger", f"unsupported trigger {trigger!r}")
        target = str(raw.get("target", "none")).lower()
        if target not in SUPPORTED_TARGET_MODES:
            raise SchemaError(f"{path}.target", f"unsupported target mode {target!r}")
        card_id = raw.get("card_id", "")
        name = raw.get("name", "Generated minion")
        if not isinstance(card_id, str) or not isinstance(name, str):
            raise SchemaError(path, "card_id and name must be strings")
        hand_count_at_most = raw.get("hand_count_at_most")
        if hand_count_at_most is not None:
            hand_count_at_most = _int(
                raw, "hand_count_at_most", path, minimum=0
            )
        return cls(
            kind=kind,
            trigger=trigger,
            amount=_int(raw, "amount", path, default=0),
            target=target,
            count=_int(raw, "count", path, default=1, minimum=1),
            card_id=card_id,
            name=name,
            attack=_int(raw, "attack", path, default=0, minimum=0),
            health=_int(raw, "health", path, default=1, minimum=1),
            random=_bool(raw, "random", path),
            rush=_bool(raw, "rush", path),
            taunt=_bool(raw, "taunt", path),
            divine_shield=_bool(raw, "divine_shield", path),
            stealth=_bool(raw, "stealth", path),
            poisonous=_bool(raw, "poisonous", path),
            lifesteal=_bool(raw, "lifesteal", path),
            windfury=_bool(raw, "windfury", path),
            charge=_bool(raw, "charge", path),
            reborn=_bool(raw, "reborn", path),
            hand_count_at_most=hand_count_at_most,
            summoned_card_effects_unmodeled=_bool(
                raw, "summoned_card_effects_unmodeled", path
            ),
        )

    def to_dict(self) -> dict[str, Any]:
        result = {
            "kind": self.kind,
            "amount": self.amount,
            "target": self.target,
            "count": self.count,
            "card_id": self.card_id,
            "name": self.name,
            "attack": self.attack,
            "health": self.health,
            "random": self.random,
        }
        if self.trigger != "resolution":
            result["trigger"] = self.trigger
        for field_name in (
            "rush",
            "taunt",
            "divine_shield",
            "stealth",
            "poisonous",
            "lifesteal",
            "windfury",
            "charge",
            "reborn",
        ):
            if getattr(self, field_name):
                result[field_name] = True
        if self.hand_count_at_most is not None:
            result["hand_count_at_most"] = self.hand_count_at_most
        if self.summoned_card_effects_unmodeled:
            result["summoned_card_effects_unmodeled"] = True
        return result


@dataclass
class Card:
    entity_id: str
    card_id: str
    name: str
    card_type: CardType
    cost: int = 0
    attack: int = 0
    health: int = 0
    current_health: int = 0
    # Internal evidence bit used by conditional target filters such as Backstab.
    # Raw HDT adapters provide it explicitly, while hand-written canonical states
    # that omit current_health must not silently qualify as "undamaged".
    current_health_known: bool = False
    playable: bool = True
    can_attack: bool = False
    attacks_remaining: int = 0
    taunt: bool = False
    divine_shield: bool = False
    frozen: bool = False
    stealth: bool = False
    poisonous: bool = False
    lifesteal: bool = False
    windfury: bool = False
    mega_windfury: bool = False
    rush: bool = False
    charge: bool = False
    reborn: bool = False
    dormant: bool = False
    immune: bool = False
    summoned_this_turn: bool = False
    durability: int = 0
    current_durability: int = 0
    effects: tuple[Effect, ...] = ()
    effect_coverage: str = "generic"
    unsupported_effects: tuple[str, ...] = ()
    prior_weight: float = 1.0
    tags: dict[str, JSONScalar] = field(default_factory=dict)
    card_text: str = ""
    rule_id: str = ""
    rule_version: str = ""
    rule_text_sha256: str = ""

    @classmethod
    def from_dict(cls, value: Any, path: str) -> "Card":
        raw = _object(value, path)
        is_hidden = str(raw.get("visibility", "")).strip().lower() == "hidden"
        raw_type = _str(raw, "card_type", path, default="UNKNOWN").upper()
        try:
            card_type = CardType(raw_type)
        except ValueError as exc:
            raise SchemaError(f"{path}.card_type", f"unknown card type {raw_type!r}") from exc
        effects_raw = _sequence(raw.get("effects", []), f"{path}.effects")
        unsupported_raw = _sequence(raw.get("unsupported_effects", []), f"{path}.unsupported_effects")
        unsupported: list[str] = []
        for index, item in enumerate(unsupported_raw):
            if not isinstance(item, str) or not item.strip():
                raise SchemaError(f"{path}.unsupported_effects[{index}]", "must be a non-empty string")
            unsupported.append(item.strip())
        default_coverage = (
            "unsupported"
            if card_type in {CardType.SPELL, CardType.WEAPON, CardType.HERO_POWER, CardType.LOCATION}
            and not effects_raw
            else "generic"
        )
        coverage = raw.get("effect_coverage", default_coverage)
        if coverage not in {"exact", "generic", "unsupported"}:
            raise SchemaError(f"{path}.effect_coverage", "must be exact, generic, or unsupported")
        prior_weight = raw.get("prior_weight", 1.0)
        if isinstance(prior_weight, bool) or not isinstance(prior_weight, (int, float)) or prior_weight < 0:
            raise SchemaError(f"{path}.prior_weight", "must be a non-negative number")
        card_text = raw.get("english_text") or raw.get("card_text") or raw.get("text") or ""
        if not isinstance(card_text, str):
            raise SchemaError(f"{path}.card_text", "must be a string")
        provenance: dict[str, str] = {}
        for key in ("rule_id", "rule_version", "rule_text_sha256"):
            item = raw.get(key, "")
            if not isinstance(item, str):
                raise SchemaError(f"{path}.{key}", "must be a string")
            provenance[key] = item
        can_attack = _bool(raw, "can_attack", path)
        attacks_remaining = _int(
            raw,
            "attacks_remaining",
            path,
            default=1 if can_attack else 0,
            minimum=0,
        )
        health = _int(raw, "health", path, default=0, minimum=0)
        durability = _int(raw, "durability", path, default=0, minimum=0)
        current_health_known_raw = raw.get(
            "current_health_known", "current_health" in raw
        )
        if not isinstance(current_health_known_raw, bool):
            raise SchemaError(
                f"{path}.current_health_known", "must be a boolean when provided"
            )
        return cls(
            entity_id=_str(raw, "entity_id", path),
            card_id=_str(raw, "card_id", path, default="UNKNOWN"),
            name=_str(raw, "name", path, default="Unknown card"),
            card_type=card_type,
            cost=_int(raw, "cost", path, default=0, minimum=0),
            attack=_int(raw, "attack", path, default=0, minimum=0),
            health=health,
            current_health=_int(raw, "current_health", path, default=health, minimum=0),
            current_health_known=current_health_known_raw,
            playable=False if is_hidden else _bool(raw, "playable", path, default=True),
            can_attack=can_attack,
            attacks_remaining=attacks_remaining,
            taunt=_bool(raw, "taunt", path),
            divine_shield=_bool(raw, "divine_shield", path),
            frozen=_bool(raw, "frozen", path),
            stealth=_bool(raw, "stealth", path),
            poisonous=_bool(raw, "poisonous", path),
            lifesteal=_bool(raw, "lifesteal", path),
            windfury=_bool(raw, "windfury", path),
            mega_windfury=_bool(raw, "mega_windfury", path),
            rush=_bool(raw, "rush", path),
            charge=_bool(raw, "charge", path),
            reborn=_bool(raw, "reborn", path),
            dormant=_bool(raw, "dormant", path),
            immune=_bool(raw, "immune", path),
            summoned_this_turn=_bool(raw, "summoned_this_turn", path),
            durability=durability,
            current_durability=_int(
                raw,
                "current_durability",
                path,
                default=durability,
                minimum=0,
            ),
            effects=tuple(Effect.from_dict(item, f"{path}.effects[{i}]") for i, item in enumerate(effects_raw)),
            effect_coverage=coverage,
            unsupported_effects=tuple(unsupported),
            prior_weight=float(prior_weight),
            tags=_json_metadata(raw.get("tags"), f"{path}.tags"),
            card_text=card_text,
            rule_id=provenance["rule_id"],
            rule_version=provenance["rule_version"],
            rule_text_sha256=provenance["rule_text_sha256"],
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "entity_id": self.entity_id,
            "card_id": self.card_id,
            "name": self.name,
            "card_type": self.card_type.value,
            "cost": self.cost,
            "attack": self.attack,
            "health": self.health,
            "current_health": self.current_health,
            "current_health_known": self.current_health_known,
            "playable": self.playable,
            "can_attack": self.can_attack,
            "attacks_remaining": self.attacks_remaining,
            "taunt": self.taunt,
            "divine_shield": self.divine_shield,
            "frozen": self.frozen,
            "stealth": self.stealth,
            "poisonous": self.poisonous,
            "lifesteal": self.lifesteal,
            "windfury": self.windfury,
            "mega_windfury": self.mega_windfury,
            "rush": self.rush,
            "charge": self.charge,
            "reborn": self.reborn,
            "dormant": self.dormant,
            "immune": self.immune,
            "summoned_this_turn": self.summoned_this_turn,
            "durability": self.durability,
            "current_durability": self.current_durability,
            "effects": [effect.to_dict() for effect in self.effects],
            "effect_coverage": self.effect_coverage,
            "unsupported_effects": list(self.unsupported_effects),
            "prior_weight": self.prior_weight,
            "tags": dict(self.tags),
            "card_text": self.card_text,
            "rule_id": self.rule_id,
            "rule_version": self.rule_version,
            "rule_text_sha256": self.rule_text_sha256,
        }


@dataclass
class PlayerState:
    player_id: str
    hero: Card
    mana: int
    max_mana: int
    armor: int = 0
    hand: list[Card] = field(default_factory=list)
    board: list[Card] = field(default_factory=list)
    graveyard: list[Card] = field(default_factory=list)
    deck_size: int = 0
    fatigue: int = 0
    hero_power: Card | None = None
    hero_power_available: bool = False
    weapon: Card | None = None
    spell_power: int = 0
    public_rule_tags: dict[str, JSONScalar] = field(default_factory=dict)
    public_rule_tags_complete: bool = False

    @classmethod
    def from_dict(cls, value: Any, path: str) -> "PlayerState":
        raw = _object(value, path)
        hand_raw = _sequence(raw.get("hand", []), f"{path}.hand")
        board_raw = _sequence(raw.get("board", []), f"{path}.board")
        graveyard_raw = _sequence(raw.get("graveyard", []), f"{path}.graveyard")
        if len(hand_raw) > 10:
            raise SchemaError(f"{path}.hand", "may not contain more than 10 cards")
        if len(board_raw) > 7:
            raise SchemaError(f"{path}.board", "may not contain more than 7 minions")
        hero = Card.from_dict(raw.get("hero"), f"{path}.hero")
        hero_power = None
        if raw.get("hero_power") is not None:
            hero_power = Card.from_dict(raw["hero_power"], f"{path}.hero_power")
        weapon = None
        if raw.get("weapon") is not None:
            weapon = Card.from_dict(raw["weapon"], f"{path}.weapon")
        max_mana = _int(raw, "max_mana", path, default=0, minimum=0)
        mana = _int(raw, "mana", path, default=0, minimum=0)
        if mana > max_mana + 20:
            raise SchemaError(f"{path}.mana", "is implausibly larger than max_mana")
        return cls(
            player_id=_str(raw, "player_id", path),
            hero=hero,
            mana=mana,
            max_mana=max_mana,
            armor=_int(raw, "armor", path, default=0, minimum=0),
            hand=[Card.from_dict(item, f"{path}.hand[{i}]") for i, item in enumerate(hand_raw)],
            board=[Card.from_dict(item, f"{path}.board[{i}]") for i, item in enumerate(board_raw)],
            graveyard=[
                Card.from_dict(item, f"{path}.graveyard[{i}]")
                for i, item in enumerate(graveyard_raw)
            ],
            deck_size=_int(raw, "deck_size", path, default=0, minimum=0),
            fatigue=_int(raw, "fatigue", path, default=0, minimum=0),
            hero_power=hero_power,
            hero_power_available=_bool(raw, "hero_power_available", path),
            weapon=weapon,
            spell_power=_int(raw, "spell_power", path, default=0, minimum=0),
            public_rule_tags=_json_metadata(
                raw.get("public_rule_tags"), f"{path}.public_rule_tags"
            ),
            public_rule_tags_complete=_bool(
                raw, "public_rule_tags_complete", path
            ),
        )

    def to_dict(self) -> dict[str, Any]:
        result = {
            "player_id": self.player_id,
            "hero": self.hero.to_dict(),
            "mana": self.mana,
            "max_mana": self.max_mana,
            "armor": self.armor,
            "hand": [card.to_dict() for card in self.hand],
            "board": [card.to_dict() for card in self.board],
            "graveyard": [card.to_dict() for card in self.graveyard],
            "deck_size": self.deck_size,
            "fatigue": self.fatigue,
            "hero_power": self.hero_power.to_dict() if self.hero_power else None,
            "hero_power_available": self.hero_power_available,
            "weapon": self.weapon.to_dict() if self.weapon else None,
            "spell_power": self.spell_power,
        }
        if self.public_rule_tags or self.public_rule_tags_complete:
            result["public_rule_tags"] = dict(self.public_rule_tags)
            result["public_rule_tags_complete"] = self.public_rule_tags_complete
        return result


@dataclass(frozen=True)
class BeliefCandidate:
    card_id: str
    probability: float
    impact: float = 0.0

    @classmethod
    def from_dict(cls, value: Any, path: str) -> "BeliefCandidate":
        raw = _object(value, path)
        probability = raw.get("probability")
        impact = raw.get("impact", 0.0)
        if isinstance(probability, bool) or not isinstance(probability, (int, float)):
            raise SchemaError(f"{path}.probability", "must be a number")
        if not 0 <= probability <= 1:
            raise SchemaError(f"{path}.probability", "must be between 0 and 1")
        if isinstance(impact, bool) or not isinstance(impact, (int, float)):
            raise SchemaError(f"{path}.impact", "must be a number")
        return cls(_str(raw, "card_id", path), float(probability), float(impact))

    def to_dict(self) -> dict[str, Any]:
        return {"card_id": self.card_id, "probability": self.probability, "impact": self.impact}


@dataclass(frozen=True)
class BeliefState:
    opponent_hand_slots: int = 0
    candidates: tuple[BeliefCandidate, ...] = ()
    source_snapshot: str = ""
    confidence: float = 0.0

    @classmethod
    def from_dict(cls, value: Any, path: str = "state.belief") -> "BeliefState":
        raw = _object(value, path)
        candidates_raw = _sequence(raw.get("candidates", []), f"{path}.candidates")
        confidence = raw.get("confidence", 0.0)
        if isinstance(confidence, bool) or not isinstance(confidence, (int, float)) or not 0 <= confidence <= 1:
            raise SchemaError(f"{path}.confidence", "must be a number between 0 and 1")
        source = raw.get("source_snapshot", "")
        if not isinstance(source, str):
            raise SchemaError(f"{path}.source_snapshot", "must be a string")
        return cls(
            opponent_hand_slots=_int(raw, "opponent_hand_slots", path, default=0, minimum=0),
            candidates=tuple(
                BeliefCandidate.from_dict(item, f"{path}.candidates[{i}]")
                for i, item in enumerate(candidates_raw)
            ),
            source_snapshot=source,
            confidence=float(confidence),
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "opponent_hand_slots": self.opponent_hand_slots,
            "candidates": [candidate.to_dict() for candidate in self.candidates],
            "source_snapshot": self.source_snapshot,
            "confidence": self.confidence,
        }


@dataclass
class GameState:
    state_id: str
    turn: int
    active_player_id: str
    perspective_player_id: str
    friendly: PlayerState
    opponent: PlayerState
    patch: str = "unknown"
    mode: str = "unknown"
    rng_seed: int = 0
    belief: BeliefState = field(default_factory=BeliefState)
    metadata: dict[str, JSONScalar] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, value: Any, path: str = "state") -> "GameState":
        raw = _object(redact_hidden_entities(_object(value, path)), path)
        if "friendly" not in raw and "player" in raw and "opponent" in raw:
            return _game_state_from_advisor_snapshot(raw, path)
        friendly = PlayerState.from_dict(raw.get("friendly"), f"{path}.friendly")
        opponent = PlayerState.from_dict(raw.get("opponent"), f"{path}.opponent")
        active = _str(raw, "active_player_id", path)
        perspective = _str(raw, "perspective_player_id", path, default=friendly.player_id)
        ids = {friendly.player_id, opponent.player_id}
        if active not in ids:
            raise SchemaError(f"{path}.active_player_id", "must match friendly or opponent player_id")
        if perspective not in ids:
            raise SchemaError(f"{path}.perspective_player_id", "must match friendly or opponent player_id")
        belief = BeliefState()
        if raw.get("belief") is not None:
            belief = BeliefState.from_dict(raw["belief"], f"{path}.belief")
        patch = raw.get("patch", "unknown")
        mode = raw.get("mode", "unknown")
        if not isinstance(patch, str) or not isinstance(mode, str):
            raise SchemaError(path, "patch and mode must be strings")
        state = cls(
            state_id=_str(raw, "state_id", path),
            turn=_int(raw, "turn", path, minimum=1),
            active_player_id=active,
            perspective_player_id=perspective,
            friendly=friendly,
            opponent=opponent,
            patch=patch,
            mode=mode,
            rng_seed=_int(raw, "rng_seed", path, default=0),
            belief=belief,
            metadata=_json_metadata(raw.get("metadata"), f"{path}.metadata"),
        )
        state.validate_entity_ids()
        return state

    def validate_entity_ids(self) -> None:
        entities: list[str] = []
        for player in (self.friendly, self.opponent):
            entities.append(player.hero.entity_id)
            entities.extend(card.entity_id for card in player.hand)
            entities.extend(card.entity_id for card in player.board)
            entities.extend(card.entity_id for card in player.graveyard)
            if player.hero_power:
                entities.append(player.hero_power.entity_id)
            if player.weapon:
                entities.append(player.weapon.entity_id)
        duplicates = sorted({item for item in entities if entities.count(item) > 1})
        if duplicates:
            raise SchemaError("state", f"entity_id values must be unique: {', '.join(duplicates)}")

    def to_dict(self) -> dict[str, Any]:
        return {
            "state_id": self.state_id,
            "turn": self.turn,
            "active_player_id": self.active_player_id,
            "perspective_player_id": self.perspective_player_id,
            "friendly": self.friendly.to_dict(),
            "opponent": self.opponent.to_dict(),
            "patch": self.patch,
            "mode": self.mode,
            "rng_seed": self.rng_seed,
            "belief": self.belief.to_dict(),
            "metadata": dict(self.metadata),
        }

    def player(self, player_id: str) -> PlayerState:
        if self.friendly.player_id == player_id:
            return self.friendly
        if self.opponent.player_id == player_id:
            return self.opponent
        raise KeyError(player_id)

    def other_player(self, player_id: str) -> PlayerState:
        if self.friendly.player_id == player_id:
            return self.opponent
        if self.opponent.player_id == player_id:
            return self.friendly
        raise KeyError(player_id)


GAME_TAG_ENUM_IDS = {
    "TRIGGER_VISUAL": 32,
    "AURA": 362,
    "EXHAUSTED": 43,
    "HERO_POWER_DOUBLE": 366,
    "STEADY_SHOT_CAN_TARGET": 383,
    "CURRENT_HEROPOWER_DAMAGE_BONUS": 395,
    "HEROPOWER_DAMAGE": 396,
    "LIFESTEAL": 685,
    "HERO_POWER_DISABLED": 777,
    "HAS_ACTIVATE_POWER": 2840,
}
_PUBLIC_PLAYER_RULE_TAGS = (
    "STEADY_SHOT_CAN_TARGET",
    "CURRENT_HEROPOWER_DAMAGE_BONUS",
    "HERO_POWER_DOUBLE",
    "HEROPOWER_DAMAGE",
    "HERO_POWER_DISABLED",
)


def game_tag_int(tags: Mapping[str, JSONScalar], name: str) -> int | None:
    """Read a public GameTag by stable name or enum ID.

    HDT normally serializes known enum names, but an older enum table may emit the
    numeric ID. Accepting both keeps rule/availability evidence fail-closed across
    that presentation difference without interpreting arbitrary tags.
    """

    canonical = name.strip().upper()
    aliases = {canonical, str(GAME_TAG_ENUM_IDS.get(canonical, ""))}
    if canonical == "HERO_POWER_DOUBLE":
        aliases.add("TAG_HERO_POWER_DOUBLE")
    aliases.discard("")
    for key, raw_value in tags.items():
        if str(key).strip().upper() not in aliases:
            continue
        if isinstance(raw_value, bool):
            return int(raw_value)
        if isinstance(raw_value, int):
            return raw_value
        if isinstance(raw_value, float) and raw_value.is_integer():
            return int(raw_value)
        if isinstance(raw_value, str):
            try:
                return int(raw_value.strip())
            except ValueError:
                return None
        return None
    return None


def _player_rule_tag_evidence(raw_player: Mapping[str, Any]) -> tuple[dict[str, int], bool]:
    player_entity = raw_player.get("player_entity")
    if not isinstance(player_entity, Mapping):
        return {}, False
    raw_tags = player_entity.get("tags")
    if not isinstance(raw_tags, Mapping):
        return {}, False
    tags: dict[str, JSONScalar] = {
        str(key): item
        for key, item in raw_tags.items()
        if isinstance(key, str)
        and (item is None or isinstance(item, (str, int, float, bool)))
    }
    selected: dict[str, int] = {}
    for name in _PUBLIC_PLAYER_RULE_TAGS:
        value = game_tag_int(tags, name)
        if value is not None:
            selected[name] = value
    return selected, True


def _hdt_hero_power_available(
    hero_power: Card | None,
    raw_power: Any,
    public_player_tags: Mapping[str, JSONScalar],
) -> bool:
    if hero_power is None or not isinstance(raw_power, Mapping):
        return False
    raw_tags = raw_power.get("tags")
    if not isinstance(raw_tags, Mapping):
        return False
    power_tags: dict[str, JSONScalar] = {
        str(key): item
        for key, item in raw_tags.items()
        if isinstance(key, str)
        and (item is None or isinstance(item, (str, int, float, bool)))
    }
    if not game_tag_int(power_tags, "HAS_ACTIVATE_POWER"):
        return False
    if bool(raw_power.get("is_exhausted", False)) or game_tag_int(
        power_tags, "EXHAUSTED"
    ):
        return False
    if game_tag_int(power_tags, "HERO_POWER_DISABLED") or game_tag_int(
        public_player_tags, "HERO_POWER_DISABLED"
    ):
        return False
    # This is the public ready/not-exhausted state. Affordability remains a
    # separate legal-action check so a searched card play can make the power
    # free later in the same line without leaving it permanently unavailable.
    return True


def _advisor_entity(value: Any, path: str, fallback_entity_id: str) -> Card:
    raw = _object(value, path)
    is_hidden = str(raw.get("visibility", "")).strip().lower() == "hidden"
    raw_entity_id = raw.get("entity_id", fallback_entity_id)
    if isinstance(raw_entity_id, bool) or not isinstance(raw_entity_id, (str, int)):
        raise SchemaError(f"{path}.entity_id", "must be an integer or string")
    entity_id = str(raw_entity_id) if str(raw_entity_id) != "0" else fallback_entity_id
    raw_type = str(raw.get("card_type", "UNKNOWN")).upper()
    card_type = CardType(raw_type) if raw_type in {item.value for item in CardType} else CardType.UNKNOWN
    health_raw = raw.get("health", 0)
    damage_raw = raw.get("damage", 0)
    if isinstance(health_raw, bool) or not isinstance(health_raw, int):
        health_raw = 0
    if isinstance(damage_raw, bool) or not isinstance(damage_raw, int):
        damage_raw = 0
    health = max(0, health_raw)
    current_health = max(0, health - max(0, damage_raw))
    mechanics_raw = raw.get("mechanics", [])
    mechanics: list[str] = []
    if isinstance(mechanics_raw, Sequence) and not isinstance(mechanics_raw, (str, bytes, bytearray)):
        mechanics = [
            str(item).strip().lower().replace("-", "_").replace(" ", "_")
            for item in mechanics_raw
        ]
    text = raw.get("english_text") or raw.get("card_text") or ""
    if not isinstance(text, str):
        text = ""
    tags_raw = raw.get("tags")
    tags: dict[str, JSONScalar] = {}
    if isinstance(tags_raw, Mapping):
        for key, item in tags_raw.items():
            if isinstance(key, str) and (item is None or isinstance(item, (str, int, float, bool))):
                tags[key] = item

    def tag_int(name: str, default: int = 0) -> int:
        value = next((item for key, item in tags.items() if key.upper() == name), default)
        return value if isinstance(value, int) and not isinstance(value, bool) else default

    def tag_flag(name: str) -> bool:
        value = game_tag_int(tags, name)
        return value is not None and value != 0

    def raw_flag_or_tag(raw_key: str, tag_name: str) -> bool:
        return bool(raw.get(raw_key, False)) or tag_flag(tag_name)

    def elusive_evidence() -> bool:
        return tag_flag("ELUSIVE") or (
            tag_flag("CANT_BE_TARGETED_BY_SPELLS")
            and tag_flag("CANT_BE_TARGETED_BY_HERO_POWERS")
        )

    normalized_rule_text = html.unescape(text).replace("[x]", " ").replace("\u00a0", " ")
    normalized_rule_text = re.sub(r"<[^>]*>", " ", normalized_rule_text)
    normalized_rule_text = re.sub(r"[$#](?=\d)", "", normalized_rule_text)
    normalized_rule_text = " ".join(normalized_rule_text.split())
    normalized_keyword_text = normalized_rule_text
    normalized_keyword_text = re.sub(
        r"[,.;:/]", " ", normalized_keyword_text.lower()
    )
    keyword_tokens = normalized_keyword_text.split()
    keyword_text_covered = bool(keyword_tokens) and card_type != CardType.HERO
    index = 0
    while keyword_text_covered and index < len(keyword_tokens):
        if keyword_tokens[index : index + 2] == ["divine", "shield"]:
            keyword, width = "divine_shield", 2
        elif keyword_tokens[index : index + 2] == ["mega", "windfury"]:
            keyword, width = "mega_windfury", 2
        else:
            keyword, width = keyword_tokens[index].strip("-"), 1
        evidence = {
            "taunt": lambda: raw_flag_or_tag("has_taunt", "TAUNT"),
            "divine_shield": lambda: raw_flag_or_tag(
                "has_divine_shield", "DIVINE_SHIELD"
            ),
            "stealth": lambda: raw_flag_or_tag("has_stealth", "STEALTH"),
            "lifesteal": lambda: raw_flag_or_tag("has_lifesteal", "LIFESTEAL"),
            "poisonous": lambda: raw_flag_or_tag("has_poisonous", "POISONOUS"),
            "windfury": lambda: raw_flag_or_tag("has_windfury", "WINDFURY"),
            "mega-windfury": lambda: raw_flag_or_tag(
                "has_mega_windfury", "MEGA_WINDFURY"
            ),
            "mega_windfury": lambda: raw_flag_or_tag(
                "has_mega_windfury", "MEGA_WINDFURY"
            ),
            "rush": lambda: raw_flag_or_tag("has_rush", "RUSH"),
            "charge": lambda: raw_flag_or_tag("has_charge", "CHARGE"),
            "reborn": lambda: raw_flag_or_tag("has_reborn", "REBORN"),
            "immune": lambda: raw_flag_or_tag("is_immune", "IMMUNE"),
            "elusive": elusive_evidence,
        }.get(keyword)
        if evidence is None or not evidence():
            keyword_text_covered = False
            break
        index += width
    generic_keywords = {
        "taunt",
        "divine_shield",
        "stealth",
        "lifesteal",
        "poisonous",
        "windfury",
        "mega_windfury",
        "rush",
        "charge",
        "reborn",
        "dormant",
        "immune",
    }
    elusive_mechanics = {
        "elusive",
        "cant_be_targeted_by_spells",
        "cant_be_targeted_by_hero_powers",
    }
    unsupported = [
        item
        for item in mechanics
        if item not in generic_keywords
        and not (item in elusive_mechanics and elusive_evidence())
    ]
    if text.strip() and card_type != CardType.HERO and not keyword_text_covered:
        unsupported.append("card_text_not_parsed")

    is_exhausted = bool(raw.get("is_exhausted", False))
    is_frozen = bool(raw.get("is_frozen", False))
    attack_raw = raw.get("attack", 0)
    cost_raw = raw.get("cost", 0)
    attack = attack_raw if isinstance(attack_raw, int) and not isinstance(attack_raw, bool) else 0
    cost = cost_raw if isinstance(cost_raw, int) and not isinstance(cost_raw, bool) else 0
    mega_windfury = bool(raw.get("has_mega_windfury", False)) or tag_flag("MEGA_WINDFURY")
    windfury = mega_windfury or bool(raw.get("has_windfury", False)) or tag_flag("WINDFURY")
    dormant = bool(raw.get("is_dormant", False)) or tag_flag("DORMANT")
    immune = bool(raw.get("is_immune", False)) or tag_flag("IMMUNE")
    attack_limit = 4 if mega_windfury else (2 if windfury else 1)
    attack_limit += max(0, tag_int("EXTRA_ATTACKS_THIS_TURN"))
    attacks_used = max(0, tag_int("NUM_ATTACKS_THIS_TURN"))
    can_attack = (
        card_type in {CardType.HERO, CardType.MINION}
        and attack > 0
        and not is_exhausted
        and not is_frozen
        and not dormant
        and attack_limit > attacks_used
    )
    attacks_remaining = max(0, attack_limit - attacks_used) if can_attack else 0
    durability_raw = raw.get("durability", 0)
    durability = (
        max(0, durability_raw)
        if isinstance(durability_raw, int) and not isinstance(durability_raw, bool)
        else 0
    )
    current_durability = max(0, durability - max(0, damage_raw))
    zone = str(raw.get("zone", "")).strip().upper()
    turns_in_play = tag_int("NUM_TURNS_IN_PLAY", -1)
    summoned_this_turn = card_type == CardType.MINION and zone == "PLAY" and turns_in_play == 0
    name = raw.get("name") or "Unknown card"
    card_id = raw.get("card_id") or "UNKNOWN"
    return Card(
        entity_id=entity_id,
        card_id=str(card_id),
        name=str(name),
        card_type=card_type,
        cost=max(0, cost),
        attack=max(0, attack),
        health=health,
        current_health=current_health,
        current_health_known=(
            "health" in raw
            and "damage" in raw
            and isinstance(raw.get("health"), int)
            and not isinstance(raw.get("health"), bool)
            and isinstance(raw.get("damage"), int)
            and not isinstance(raw.get("damage"), bool)
        ),
        playable=False if is_hidden else bool(raw.get("is_playable_card", True)),
        can_attack=can_attack,
        attacks_remaining=attacks_remaining,
        taunt=bool(raw.get("has_taunt", False)),
        divine_shield=bool(raw.get("has_divine_shield", False)),
        frozen=is_frozen,
        stealth=bool(raw.get("has_stealth", False)),
        poisonous=bool(raw.get("has_poisonous", False)),
        lifesteal=bool(raw.get("has_lifesteal", False))
        or bool(game_tag_int(tags, "LIFESTEAL")),
        windfury=windfury,
        mega_windfury=mega_windfury,
        rush=bool(raw.get("has_rush", False)) or tag_flag("RUSH"),
        charge=bool(raw.get("has_charge", False)) or tag_flag("CHARGE"),
        reborn=bool(raw.get("has_reborn", False)) or tag_flag("REBORN"),
        dormant=dormant,
        immune=immune,
        summoned_this_turn=summoned_this_turn,
        durability=durability,
        current_durability=current_durability,
        effects=(),
        effect_coverage="unsupported" if unsupported else "generic",
        unsupported_effects=tuple(dict.fromkeys(unsupported)),
        tags=tags,
        card_text=text,
        rule_id="hdt-intrinsic-keywords-v1" if keyword_text_covered else "",
        rule_version="hdt-intrinsic-keywords-v1" if keyword_text_covered else "",
        rule_text_sha256=(
            hashlib.sha256(normalized_rule_text.encode("utf-8")).hexdigest()
            if keyword_text_covered
            else ""
        ),
    )


def _advisor_player(value: Any, path: str, fallback_id: str) -> PlayerState:
    raw = _object(value, path)
    public_rule_tags, public_rule_tags_complete = _player_rule_tag_evidence(raw)
    player_id_raw = raw.get("player_id", fallback_id)
    player_id = str(player_id_raw) if str(player_id_raw) not in {"", "0"} else fallback_id
    if raw.get("hero") is None:
        raise SchemaError(f"{path}.hero", "a public hero entity is required for solving")
    hero = _advisor_entity(raw["hero"], f"{path}.hero", f"{fallback_id}-hero")
    hero.card_type = CardType.HERO
    hand_raw = _sequence(raw.get("hand", []), f"{path}.hand")
    board_raw = _sequence(raw.get("board", []), f"{path}.board")
    hero_power = (
        _advisor_entity(raw["hero_power"], f"{path}.hero_power", f"{fallback_id}-hero-power")
        if raw.get("hero_power") is not None
        else None
    )
    if hero_power:
        hero_power.card_type = CardType.HERO_POWER
    weapon = (
        _advisor_entity(raw["weapon"], f"{path}.weapon", f"{fallback_id}-weapon")
        if raw.get("weapon") is not None
        else None
    )
    resources = raw.get("resources") if isinstance(raw.get("resources"), Mapping) else {}
    available = resources.get("available", 0)
    if isinstance(available, bool) or not isinstance(available, int):
        available = 0
    # HDT Player.MaxMana is normally the rules cap (10), whereas RESOURCES is
    # the number of permanent crystals actually unlocked.  An explicit zero is
    # meaningful before a player has taken their first turn and must not fall
    # back to the cap.  The top-level value remains only for legacy snapshots
    # that omit resources.total entirely.
    resource_total = resources.get("total")
    if isinstance(resource_total, int) and not isinstance(resource_total, bool):
        max_mana = resource_total
    else:
        max_mana = raw.get("max_mana", 0)
        if isinstance(max_mana, bool) or not isinstance(max_mana, int):
            max_mana = 0
    deck_count = raw.get("deck_count", 0)
    fatigue = raw.get("fatigue", 0)
    spell_power = resources.get("spell_power", 0)
    if isinstance(deck_count, bool) or not isinstance(deck_count, int):
        deck_count = 0
    if isinstance(fatigue, bool) or not isinstance(fatigue, int):
        fatigue = 0
    if isinstance(spell_power, bool) or not isinstance(spell_power, int):
        spell_power = 0
    armor_raw = raw["hero"].get("armor", 0) if isinstance(raw["hero"], Mapping) else 0
    armor = armor_raw if isinstance(armor_raw, int) and not isinstance(armor_raw, bool) else 0
    if weapon and hero.attack > 0 and (weapon.windfury or weapon.mega_windfury):
        attacks_used_raw = next(
            (item for key, item in hero.tags.items() if key.upper() == "NUM_ATTACKS_THIS_TURN"),
            0,
        )
        attacks_used = (
            max(0, attacks_used_raw)
            if isinstance(attacks_used_raw, int) and not isinstance(attacks_used_raw, bool)
            else 0
        )
        attack_limit = 4 if weapon.mega_windfury else 2
        hero.windfury = weapon.windfury
        hero.mega_windfury = weapon.mega_windfury
        hero.attacks_remaining = max(0, attack_limit - attacks_used)
        hero_frozen = next(
            (bool(item) for key, item in hero.tags.items() if key.upper() == "FROZEN"),
            False,
        )
        hero_exhausted = next(
            (bool(item) for key, item in hero.tags.items() if key.upper() == "EXHAUSTED"),
            False,
        )
        hero.can_attack = bool(
            hero.attacks_remaining > 0 and not hero_frozen and not hero_exhausted
        )
    return PlayerState(
        player_id=player_id,
        hero=hero,
        mana=max(0, available),
        max_mana=max(0, max_mana),
        armor=max(0, armor),
        hand=[
            _advisor_entity(item, f"{path}.hand[{i}]", f"{fallback_id}-hand-{i}")
            for i, item in enumerate(hand_raw)
        ],
        board=[
            _advisor_entity(item, f"{path}.board[{i}]", f"{fallback_id}-board-{i}")
            for i, item in enumerate(board_raw)
        ],
        deck_size=max(0, deck_count),
        fatigue=max(0, fatigue),
        hero_power=hero_power,
        hero_power_available=_hdt_hero_power_available(
            hero_power,
            raw.get("hero_power"),
            public_rule_tags,
        ),
        weapon=weapon,
        spell_power=max(0, spell_power),
        public_rule_tags=public_rule_tags,
        public_rule_tags_complete=public_rule_tags_complete,
    )


def _game_state_from_advisor_snapshot(raw: Mapping[str, Any], path: str) -> GameState:
    player = _advisor_player(raw.get("player"), f"{path}.player", "player")
    opponent = _advisor_player(raw.get("opponent"), f"{path}.opponent", "opponent")
    local_turn = raw.get("is_local_player_turn")
    active_label = str(raw.get("active_player", "")).lower()
    if isinstance(local_turn, bool):
        active_id = player.player_id if local_turn else opponent.player_id
    elif active_label in {"player", "friendly", "local", player.player_id.lower()}:
        active_id = player.player_id
    elif active_label in {"opponent", "enemy", opponent.player_id.lower()}:
        active_id = opponent.player_id
    else:
        raise SchemaError(f"{path}.active_player", "could not determine the active player")
    turn = raw.get("turn_number", 1)
    if isinstance(turn, bool) or not isinstance(turn, int):
        turn = 1
    state_id = raw.get("state_id")
    if not isinstance(state_id, str) or not state_id:
        raise SchemaError(f"{path}.state_id", "must be a non-empty string")
    metadata_raw = raw.get("metadata") if isinstance(raw.get("metadata"), Mapping) else {}
    metadata: dict[str, JSONScalar] = {
        str(key): item
        for key, item in metadata_raw.items()
        if item is None or isinstance(item, (str, int, float, bool))
    }
    metadata.update(
        {
            "adapter": "hdt-snapshot-v1",
            "format": str(raw.get("format") or ""),
            "format_type": str(raw.get("format_type") or ""),
            "game_mode": str(raw.get("game_mode") or ""),
            "game_type": str(raw.get("game_type") or ""),
            "environment_version": str(raw.get("environment_version", "")),
            "snapshot_schema_version": raw.get("schema_version", 1),
            "hdt_version": str(raw.get("hdt_version", "")),
            "game_id": str(raw.get("game_id", "")),
            "snapshot_sequence": raw.get("snapshot_sequence", 0)
            if isinstance(raw.get("snapshot_sequence", 0), int)
            and not isinstance(raw.get("snapshot_sequence", 0), bool)
            else 0,
            "snapshot_state_hash": str(raw.get("state_hash", "")),
            "captured_at_utc": str(raw.get("captured_at_utc", "")),
            "unsupported_feature_count": len(raw.get("unsupported_features", []))
            if isinstance(raw.get("unsupported_features"), list)
            else 0,
            "unknown_data_count": len(raw.get("unknown_data", []))
            if isinstance(raw.get("unknown_data"), list)
            else 0,
        }
    )
    game_mode = raw.get("game_mode") or raw.get("format") or "unknown"
    state = GameState(
        state_id=state_id,
        turn=max(1, turn),
        active_player_id=active_id,
        perspective_player_id=player.player_id,
        friendly=player,
        opponent=opponent,
        patch=str(raw.get("hearthstone_build") or "unknown"),
        mode=str(game_mode),
        rng_seed=0,
        belief=BeliefState(),
        metadata=metadata,
    )
    state.validate_entity_ids()
    return state


@dataclass(frozen=True)
class Action:
    kind: ActionKind
    source_entity_id: str = ""
    target_entity_id: str = ""
    card_id: str = ""
    text: str = ""
    board_position: int = 0

    @property
    def action_id(self) -> str:
        base = f"{self.kind.value}:{self.source_entity_id}:{self.target_entity_id}"
        return (
            f"{base}:position={self.board_position}"
            if self.board_position > 0
            else base
        )

    @classmethod
    def from_dict(cls, value: Any, path: str = "action") -> "Action":
        raw = _object(value, path)
        kind_raw = _str(raw, "kind", path).lower()
        try:
            kind = ActionKind(kind_raw)
        except ValueError as exc:
            raise SchemaError(f"{path}.kind", f"unknown action kind {kind_raw!r}") from exc
        source = raw.get("source_entity_id", "")
        target = raw.get("target_entity_id", "")
        if not isinstance(source, str) or not isinstance(target, str):
            raise SchemaError(path, "source_entity_id and target_entity_id must be strings")
        card_id = raw.get("card_id", "")
        text = raw.get("text", "")
        if not isinstance(card_id, str) or not isinstance(text, str):
            raise SchemaError(path, "card_id and text must be strings")
        board_position = raw.get("board_position", 0)
        if board_position is None:
            board_position = 0
        if (
            isinstance(board_position, bool)
            or not isinstance(board_position, int)
            or not 0 <= board_position <= 7
        ):
            raise SchemaError(
                f"{path}.board_position",
                "must be an integer between 0 and 7",
            )
        return cls(kind, source, target, card_id, text, board_position)

    def to_dict(self) -> dict[str, Any]:
        def wire_entity_id(value: str) -> str | int | None:
            if not value:
                return None
            return int(value) if value.isdigit() else value

        result = {
            "action_id": self.action_id,
            "kind": self.kind.value,
            "source_entity_id": wire_entity_id(self.source_entity_id),
            "target_entity_id": wire_entity_id(self.target_entity_id),
            "card_id": self.card_id,
            "text": self.text,
        }
        if self.board_position > 0:
            result["board_position"] = self.board_position
        return result


@dataclass(frozen=True)
class SolveOptions:
    time_budget_ms: int | None = None
    max_iterations: int | None = None
    max_depth: int | None = None
    top_k: int | None = None
    search_seed: int | None = None
    allow_approximate_effects: bool = True
    environment_version: str = ""

    @classmethod
    def from_dict(cls, value: Any, path: str = "options") -> "SolveOptions":
        if value is None:
            return cls()
        raw = _object(value, path)
        allowed = {
            "time_budget_ms",
            "max_iterations",
            "max_depth",
            "top_k",
            "time_budget_milliseconds",
            "initial_budget_milliseconds",
            "max_recommendations",
            "search_seed",
            "allow_approximate_effects",
            "environment_version",
        }
        unknown = sorted(set(raw) - allowed)
        if unknown:
            raise SchemaError(path, f"unknown fields: {', '.join(unknown)}")

        def optional_positive(key: str) -> int | None:
            if key not in raw:
                return None
            return _int(raw, key, path, minimum=1)

        time_budget_ms = optional_positive("time_budget_ms")
        if time_budget_ms is None and "time_budget_milliseconds" in raw:
            time_budget_ms = _int(raw, "time_budget_milliseconds", path, minimum=1)
        top_k = optional_positive("top_k")
        if top_k is None and "max_recommendations" in raw:
            top_k = _int(raw, "max_recommendations", path, minimum=1)
        search_seed = None
        if "search_seed" in raw and raw["search_seed"] is not None:
            search_seed = _int(raw, "search_seed", path)
        allow_approximate = raw.get("allow_approximate_effects", True)
        if not isinstance(allow_approximate, bool):
            raise SchemaError(f"{path}.allow_approximate_effects", "must be a boolean")
        environment = raw.get("environment_version", "")
        if not isinstance(environment, str):
            raise SchemaError(f"{path}.environment_version", "must be a string")
        if "initial_budget_milliseconds" in raw:
            _int(raw, "initial_budget_milliseconds", path, minimum=1)
        return cls(
            time_budget_ms=time_budget_ms,
            max_iterations=optional_positive("max_iterations"),
            max_depth=optional_positive("max_depth"),
            top_k=top_k,
            search_seed=search_seed,
            allow_approximate_effects=allow_approximate,
            environment_version=environment,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            key: value
            for key, value in {
                "time_budget_ms": self.time_budget_ms,
                "max_iterations": self.max_iterations,
                "max_depth": self.max_depth,
                "top_k": self.top_k,
                "search_seed": self.search_seed,
                "allow_approximate_effects": self.allow_approximate_effects,
                "environment_version": self.environment_version,
            }.items()
            if value is not None
        }


def _hdt_root_candidate_set(
    value: Any, state: GameState, path: str = "request.hdt_root_candidates"
) -> dict[str, Any] | None:
    if value is None:
        return None
    raw = _object(value, path)
    allowed = {
        "contract",
        "state_id",
        "frame_id",
        "collector_epoch",
        "frame_watermark",
        "candidate_set_complete",
        "candidates",
    }
    unknown = sorted(set(raw) - allowed)
    if unknown:
        raise SchemaError(path, f"unknown fields: {', '.join(unknown)}")
    if raw.get("contract") != "hdt_complete_main_action_options_v1":
        raise SchemaError(f"{path}.contract", "has an unsupported contract")
    if raw.get("state_id") != state.state_id:
        raise SchemaError(f"{path}.state_id", "must match request.state.state_id")
    for key in ("frame_id", "collector_epoch", "frame_watermark"):
        _int(raw, key, path, minimum=1)
    if raw.get("candidate_set_complete") is not True:
        raise SchemaError(f"{path}.candidate_set_complete", "must be true")
    candidates = _sequence(raw.get("candidates"), f"{path}.candidates")
    if not 1 <= len(candidates) <= 512:
        raise SchemaError(f"{path}.candidates", "must contain between 1 and 512 actions")
    normalized: list[dict[str, Any]] = []
    action_ids: set[tuple[Any, ...]] = set()
    end_turn_count = 0
    for index, candidate_value in enumerate(candidates):
        candidate_path = f"{path}.candidates[{index}]"
        candidate = _object(candidate_value, candidate_path)
        if set(candidate) != {
            "option_id",
            "action",
            "target_evidence",
            "position_evidence",
        }:
            raise SchemaError(candidate_path, "candidate fields do not match the contract")
        option_id = _int(candidate, "option_id", candidate_path, minimum=0)
        action_path = f"{candidate_path}.action"
        action = _object(candidate.get("action"), action_path)
        unknown_action = sorted(
            set(action)
            - {
                "kind",
                "source_entity_id",
                "target_entity_id",
                "card_id",
                "board_position",
            }
        )
        if unknown_action:
            raise SchemaError(action_path, f"unknown fields: {', '.join(unknown_action)}")
        kind = _str(action, "kind", action_path).lower()
        if kind not in {
            "play_card",
            "attack",
            "hero_power",
            "location_activate",
            "end_turn",
        }:
            raise SchemaError(f"{action_path}.kind", "has an unsupported action kind")
        source = action.get("source_entity_id", "")
        target = action.get("target_entity_id", "")
        card_id = action.get("card_id", "")
        if not all(isinstance(item, str) for item in (source, target, card_id)):
            raise SchemaError(action_path, "entity and card identities must be strings")
        position = action.get("board_position", 0)
        if isinstance(position, bool) or not isinstance(position, int) or not 0 <= position <= 7:
            raise SchemaError(f"{action_path}.board_position", "must be between 0 and 7")
        target_evidence = candidate.get("target_evidence")
        position_evidence = candidate.get("position_evidence")
        if target_evidence not in {
            "hdt_error_none",
            "hdt_no_legal_target",
            "not_applicable",
        }:
            raise SchemaError(f"{candidate_path}.target_evidence", "is invalid")
        if position_evidence not in {"core_board_slots_v1", "not_applicable"}:
            raise SchemaError(f"{candidate_path}.position_evidence", "is invalid")
        identity = (kind, source, target, card_id, position)
        if identity in action_ids:
            raise SchemaError(f"{path}.candidates", "candidate actions must be unique")
        action_ids.add(identity)
        if kind == "end_turn":
            end_turn_count += 1
            if (
                option_id != 0
                or source
                or target
                or card_id
                or position
                or target_evidence != "not_applicable"
                or position_evidence != "not_applicable"
            ):
                raise SchemaError(candidate_path, "end-turn evidence is inconsistent")
        elif option_id == 0:
            raise SchemaError(f"{candidate_path}.option_id", "must be positive")
        normalized.append(
            {
                "option_id": option_id,
                "action": {
                    "kind": kind,
                    "source_entity_id": source,
                    "target_entity_id": target,
                    "card_id": card_id,
                    "board_position": position,
                },
                "target_evidence": target_evidence,
                "position_evidence": position_evidence,
            }
        )
    if end_turn_count != 1:
        raise SchemaError(f"{path}.candidates", "must contain exactly one end-turn action")
    return {
        "contract": "hdt_complete_main_action_options_v1",
        "state_id": state.state_id,
        "frame_id": int(raw["frame_id"]),
        "collector_epoch": int(raw["collector_epoch"]),
        "frame_watermark": int(raw["frame_watermark"]),
        "candidate_set_complete": True,
        "candidates": normalized,
    }


@dataclass(frozen=True)
class SolveRequest:
    request_id: str
    state: GameState
    options: SolveOptions = field(default_factory=SolveOptions)
    metadata: dict[str, JSONScalar] = field(default_factory=dict)
    api_version: str = API_VERSION
    # Appended after the original positional fields so older offline callers
    # that pass api_version as the fifth argument keep the same contract.
    hdt_root_candidates: dict[str, Any] | None = None

    @classmethod
    def from_dict(cls, value: Any) -> "SolveRequest":
        raw = _object(value, "request")
        version = raw.get("api_version", API_VERSION)
        if version != API_VERSION:
            raise SchemaError("request.api_version", f"expected {API_VERSION!r}")
        state = GameState.from_dict(raw.get("state"))
        options = SolveOptions.from_dict(raw.get("options"))
        if options.search_seed is not None:
            state.rng_seed = options.search_seed
        if options.environment_version:
            state.metadata["environment_version"] = options.environment_version
        return cls(
            request_id=_str(raw, "request_id", "request"),
            state=state,
            options=options,
            metadata=_json_metadata(raw.get("metadata"), "request.metadata"),
            hdt_root_candidates=_hdt_root_candidate_set(
                raw.get("hdt_root_candidates"), state
            ),
            api_version=version,
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "api_version": self.api_version,
            "request_id": self.request_id,
            "state": self.state.to_dict(),
            "options": self.options.to_dict(),
            "metadata": dict(self.metadata),
            **(
                {"hdt_root_candidates": self.hdt_root_candidates}
                if self.hdt_root_candidates is not None
                else {}
            ),
        }


@dataclass(frozen=True)
class Observation:
    kind: str
    state_id: str
    game_id: str = ""
    observed_at_utc: str = ""
    action: Action | None = None
    pre_state: GameState | None = None
    post_state: GameState | None = None
    result: str = ""
    metadata: dict[str, JSONScalar] = field(default_factory=dict)
    api_version: str = API_VERSION
    # PowerLog-only fields that identify the actual input without changing the
    # simulator's canonical Action equality/hash semantics. Appended to preserve
    # the positional constructor contract used by older offline callers.
    action_evidence: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, value: Any) -> "Observation":
        raw = _object(value, "request")
        allowed = {
            "api_version",
            "kind",
            "state_id",
            "game_id",
            "observed_at_utc",
            "action",
            "pre_state",
            "post_state",
            "result",
            "metadata",
        }
        unknown = sorted(set(raw) - allowed)
        if unknown:
            raise SchemaError("request", f"unknown fields: {', '.join(unknown)}")
        version = raw.get("api_version", API_VERSION)
        if version != API_VERSION:
            raise SchemaError("request.api_version", f"expected {API_VERSION!r}")
        kind = _str(raw, "kind", "request").lower()
        if kind not in {"action", "result"}:
            raise SchemaError("request.kind", "must be 'action' or 'result'")
        game_id = raw.get("game_id", "")
        observed = _optional_utc_timestamp(
            raw.get("observed_at_utc", ""), "request.observed_at_utc"
        )
        if not isinstance(game_id, str):
            raise SchemaError("request.game_id", "must be a string")
        action = None
        action_evidence: dict[str, Any] = {}
        pre_state = None
        post_state = None
        result = raw.get("result", "")
        state_id = _str(raw, "state_id", "request")
        metadata = _json_metadata(raw.get("metadata"), "request.metadata")
        if kind == "action":
            action_raw = _object(raw.get("action"), "request.action")
            allowed_action_fields = {
                "action_id",
                "kind",
                "source_entity_id",
                "target_entity_id",
                "card_id",
                "text",
                "sub_option",
                "board_position",
                "option_id",
                "frame_id",
                "power_start_watermark",
                "power_end_watermark",
                "hdt_root_candidates",
                "choices",
            }
            unknown_action_fields = sorted(set(action_raw) - allowed_action_fields)
            if unknown_action_fields:
                raise SchemaError(
                    "request.action",
                    f"unknown fields: {', '.join(unknown_action_fields)}",
                )
            normalized_action = dict(action_raw)
            for key in ("source_entity_id", "target_entity_id"):
                if normalized_action.get(key) is None:
                    normalized_action[key] = ""
                elif isinstance(normalized_action.get(key), int) and not isinstance(normalized_action[key], bool):
                    normalized_action[key] = str(normalized_action[key])
            action = Action.from_dict(normalized_action, "request.action")
            if "action_id" in action_raw:
                action_id = action_raw["action_id"]
                if not isinstance(action_id, str) or action_id != action.action_id:
                    raise SchemaError(
                        "request.action.action_id",
                        "must exactly match kind:source_entity_id:target_entity_id",
                    )
            for key in ("sub_option", "board_position"):
                if key not in action_raw or action_raw[key] is None:
                    continue
                value = action_raw[key]
                if isinstance(value, bool) or not isinstance(value, int):
                    raise SchemaError(f"request.action.{key}", "must be an integer or null")
                action_evidence[key] = value
            for key in ("option_id", "frame_id"):
                if key not in action_raw or action_raw[key] is None:
                    continue
                value = action_raw[key]
                if isinstance(value, bool) or not isinstance(value, (str, int)):
                    raise SchemaError(
                        f"request.action.{key}",
                        "must be a string, integer, or null",
                    )
                action_evidence[key] = str(value)
            for key in ("power_start_watermark", "power_end_watermark"):
                if key not in action_raw or action_raw[key] is None:
                    continue
                value = action_raw[key]
                if not isinstance(value, str):
                    raise SchemaError(f"request.action.{key}", "must be a string or null")
                action_evidence[key] = value
            if "choices" in action_raw:
                choices = action_raw["choices"]
                action_evidence["choices"] = _action_choice_evidence(
                    choices,
                    "request.action.choices",
                )
            if "hdt_root_candidates" in action_raw:
                action_evidence["hdt_root_candidates"] = action_raw[
                    "hdt_root_candidates"
                ]
            if "result" in raw and result not in {"", None}:
                raise SchemaError("request.result", "is only valid for result observations")
            result = ""
            _validate_transition_candidate_metadata(
                metadata,
                state_id,
                "request.metadata",
            )
            if is_unverified_transition_candidate(metadata):
                parsed_states: dict[str, GameState] = {}
                for label in ("pre", "post"):
                    field = f"{label}_state"
                    parsed = GameState.from_dict(raw.get(field), f"request.{field}")
                    expected_id = str(metadata.get(f"{label}_state_id") or "").strip()
                    if parsed.state_id != expected_id:
                        raise SchemaError(
                            f"request.{field}.state_id",
                            f"must match request.metadata.{label}_state_id",
                        )
                    parsed_metadata = parsed.metadata
                    if str(parsed_metadata.get("game_id") or "") != game_id:
                        raise SchemaError(
                            f"request.{field}.metadata.game_id",
                            "must match request.game_id",
                        )
                    if str(parsed_metadata.get("snapshot_state_hash") or "") != str(
                        metadata.get(f"raw_{label}_snapshot_hash") or ""
                    ):
                        raise SchemaError(
                            f"request.metadata.raw_{label}_snapshot_hash",
                            f"must match the {label}-state snapshot hash",
                        )
                    if _metadata_integer(
                        parsed_metadata,
                        "snapshot_sequence",
                        f"request.{field}.metadata",
                        minimum=1,
                    ) != _metadata_integer(
                        metadata,
                        f"{label}_snapshot_sequence",
                        "request.metadata",
                        minimum=1,
                    ):
                        raise SchemaError(
                            f"request.metadata.{label}_snapshot_sequence",
                            f"must match the {label}-state snapshot sequence",
                        )
                    parsed_states[label] = parsed
                pre_state = parsed_states["pre"]
                post_state = parsed_states["post"]
                if "hdt_root_candidates" in action_evidence:
                    action_evidence["hdt_root_candidates"] = _hdt_root_candidate_set(
                        action_evidence["hdt_root_candidates"],
                        pre_state,
                        "request.action.hdt_root_candidates",
                    )
                if is_power_action_identity_candidate(metadata):
                    required_evidence = {
                        "sub_option",
                        "board_position",
                        "option_id",
                        "frame_id",
                        "power_start_watermark",
                        "power_end_watermark",
                        "choices",
                    }
                    missing_evidence = sorted(required_evidence - set(action_evidence))
                    if missing_evidence:
                        raise SchemaError(
                            "request.action",
                            "missing HDT Power evidence: " + ", ".join(missing_evidence),
                        )
                    if action_evidence["sub_option"] != -1:
                        raise SchemaError(
                            "request.action.sub_option",
                            "must be -1 for an exact action without unresolved choices",
                        )
                    if action_evidence["choices"]:
                        raise SchemaError(
                            "request.action.choices",
                            "must be empty when choice_status is none",
                        )
                    for evidence_key in (
                        "option_id",
                        "frame_id",
                        "power_start_watermark",
                        "power_end_watermark",
                    ):
                        if not str(action_evidence[evidence_key]).strip():
                            raise SchemaError(
                                f"request.action.{evidence_key}",
                                "must be non-empty for exact HDT Power evidence",
                            )
                    if not str(action_evidence["option_id"]).isdigit():
                        raise SchemaError(
                            "request.action.option_id", "must identify a numeric option"
                        )
                    if not str(action_evidence["frame_id"]).isdigit():
                        raise SchemaError(
                            "request.action.frame_id", "must identify a numeric option frame"
                        )
                    if pre_state.active_player_id != pre_state.perspective_player_id:
                        raise SchemaError(
                            "request.pre_state.active_player_id",
                            "must be the local player for exact local action evidence",
                        )
                    actor = pre_state.player(pre_state.perspective_player_id)
                    source = None
                    if action.kind == ActionKind.PLAY_CARD:
                        source = next(
                            (
                                card
                                for card in actor.hand
                                if card.entity_id == action.source_entity_id
                            ),
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
                    elif action.kind == ActionKind.END_TURN:
                        if (
                            action.source_entity_id
                            or action.target_entity_id
                            or action.card_id
                            or action_evidence["option_id"] != "0"
                        ):
                            raise SchemaError(
                                "request.action",
                                "end_turn must use option 0 without source, target, or card",
                            )
                    if action.kind != ActionKind.END_TURN:
                        if source is None:
                            raise SchemaError(
                                "request.action.source_entity_id",
                                "must resolve to the local action source in pre_state",
                            )
                        if not action.card_id or action.card_id != source.card_id:
                            raise SchemaError(
                                "request.action.card_id",
                                "must match the exact pre_state source card",
                            )
                    if action.kind == ActionKind.ATTACK and not action.target_entity_id:
                        raise SchemaError(
                            "request.action.target_entity_id",
                            "is required for an exact attack",
                        )
                    if action.target_entity_id:
                        public_entity_ids = {
                            card.entity_id
                            for player in (pre_state.friendly, pre_state.opponent)
                            for card in (
                                player.hero,
                                *player.hand,
                                *player.board,
                                *([player.hero_power] if player.hero_power else []),
                                *([player.weapon] if player.weapon else []),
                            )
                        }
                        if action.target_entity_id not in public_entity_ids:
                            raise SchemaError(
                                "request.action.target_entity_id",
                                "must resolve to a pre_state entity",
                            )
            elif raw.get("pre_state") is not None or raw.get("post_state") is not None:
                raise SchemaError(
                    "request.pre_state",
                    "pre_state and post_state are only valid for an unverified transition candidate",
                )
        else:
            if not isinstance(result, str) or result.lower() not in {"win", "loss", "tie", "unknown"}:
                raise SchemaError("request.result", "must be win, loss, tie, or unknown")
            result = result.lower()
            validate_result_metadata(metadata)
            if raw.get("action") is not None:
                raise SchemaError("request.action", "is only valid for action observations")
            if raw.get("pre_state") is not None or raw.get("post_state") is not None:
                raise SchemaError(
                    "request.pre_state", "is only valid for action observations"
                )
        return cls(
            kind=kind,
            state_id=state_id,
            game_id=game_id,
            observed_at_utc=observed,
            action=action,
            action_evidence=action_evidence,
            pre_state=pre_state,
            post_state=post_state,
            result=result,
            metadata=metadata,
            api_version=version,
        )

    def to_dict(self) -> dict[str, Any]:
        action = self.action.to_dict() if self.action else None
        if action is not None:
            action.update(_json_value(self.action_evidence, "observation.action_evidence"))
        return {
            "api_version": self.api_version,
            "kind": self.kind,
            "state_id": self.state_id,
            "game_id": self.game_id,
            "observed_at_utc": self.observed_at_utc,
            "action": action,
            "pre_state": self.pre_state.to_dict() if self.pre_state else None,
            "post_state": self.post_state.to_dict() if self.post_state else None,
            "result": self.result,
            "metadata": dict(self.metadata),
        }


@dataclass(frozen=True)
class Annotation:
    code: str
    detail: str
    entity_id: str = ""
    severity: str = "warning"

    def to_dict(self) -> dict[str, Any]:
        return {
            "code": self.code,
            "detail": self.detail,
            "entity_id": self.entity_id,
            "severity": self.severity,
        }


@dataclass(frozen=True)
class Recommendation:
    rank: int
    actions: tuple[Action, ...]
    expected_win_probability: float
    confidence_interval: tuple[float, float]
    visits: int
    rationale: str
    annotations: tuple[Annotation, ...] = ()
    proof_kind: str = ""
    proof_scope: str = ""
    is_proven_lethal: bool = False
    opponent_reply: tuple[Action, ...] = ()
    worst_case_score: float | None = None
    response_scope: str = ""
    response_search_complete: bool = False
    response_is_proven_lethal: bool = False
    response_nodes_expanded: int = 0
    response_searched_depth: int = 0
    response_transposition_hits: int = 0
    score_components: dict[str, float] = field(default_factory=dict)
    verified_portfolio_regret: float | None = None
    alternative_kind: str = ""

    def to_dict(self) -> dict[str, Any]:
        line_id = hashlib.sha256(
            "|".join(action.action_id for action in self.actions).encode("utf-8")
        ).hexdigest()[:16]
        action_items = []
        for index, action in enumerate(self.actions, start=1):
            item = action.to_dict()
            item.update(
                {
                    "index": index,
                    "type": action.kind.value,
                    "card_id": action.card_id,
                    "text": action.text or action.kind.value.replace("_", " "),
                }
            )
            action_items.append(item)
        response_items = []
        for index, action in enumerate(self.opponent_reply, start=1):
            item = action.to_dict()
            item.update(
                {
                    "index": index,
                    "type": action.kind.value,
                    "card_id": action.card_id,
                    "text": action.text or action.kind.value.replace("_", " "),
                }
            )
            response_items.append(item)
        low, high = self.confidence_interval
        minimax_value = self.score_components.get("minimax_value")
        response_verified = bool(
            self.response_search_complete and self.response_scope
        )
        safe_after_response = (
            not self.response_is_proven_lethal if response_verified else None
        )
        approximate = [
            annotation.detail
            for annotation in self.annotations
            if annotation.code.startswith(("unsupported", "approximate", "hidden", "multiple"))
        ]
        risks = [annotation.detail for annotation in self.annotations]
        return {
            "rank": self.rank,
            "line_id": line_id,
            "actions": action_items,
            "expected_win_probability": round(self.expected_win_probability, 6),
            "expected_win_rate": round(self.expected_win_probability, 6),
            "score_kind": "counterplay_tactical_state_value",
            "confidence_interval": [round(low, 6), round(high, 6)],
            "win_rate_low": round(low, 6),
            "win_rate_high": round(high, 6),
            "confidence": round(max(0.0, min(1.0, 1.0 - (high - low))), 6),
            "visits": self.visits,
            "rationale": self.rationale,
            "summary": self.rationale,
            "risks": risks,
            "approximate_effects": approximate,
            "annotations": [annotation.to_dict() for annotation in self.annotations],
            "proof_kind": self.proof_kind,
            "proof_scope": self.proof_scope,
            "is_proven_lethal": self.is_proven_lethal,
            "opponent_reply": response_items,
            "worst_case_score": round(
                self.expected_win_probability
                if self.worst_case_score is None
                else self.worst_case_score,
                6,
            ),
            "response_scope": self.response_scope,
            "response_search_complete": self.response_search_complete,
            "response_is_proven_lethal": self.response_is_proven_lethal,
            "response_nodes_expanded": self.response_nodes_expanded,
            "response_searched_depth": self.response_searched_depth,
            "response_transposition_hits": self.response_transposition_hits,
            "verified_portfolio_regret": (
                round(float(self.verified_portfolio_regret), 6)
                if self.verified_portfolio_regret is not None
                else None
            ),
            "alternative_kind": self.alternative_kind,
            "is_response_verified": response_verified,
            "response_kind": "minimax_best_response" if response_verified else "",
            "minimax_value": (
                round(float(minimax_value), 6)
                if minimax_value is not None
                else None
            ),
            "is_safe_after_response": safe_after_response,
            "opponent_response": {
                "actions": response_items,
                "tactical_value": (
                    round(float(minimax_value), 6)
                    if minimax_value is not None
                    else None
                ),
            },
            "score_components": {
                key: round(float(value), 6)
                for key, value in sorted(self.score_components.items())
            },
            "counterplay": {
                "scope": self.response_scope,
                "search_complete": self.response_search_complete,
                "is_proven_lethal": self.response_is_proven_lethal,
                "worst_case_score": round(
                    self.expected_win_probability
                    if self.worst_case_score is None
                    else self.worst_case_score,
                    6,
                ),
                "nodes_expanded": self.response_nodes_expanded,
                "searched_depth": self.response_searched_depth,
                "transposition_hits": self.response_transposition_hits,
                "actions": response_items,
                "score_components": {
                    key: round(float(value), 6)
                    for key, value in sorted(self.score_components.items())
                },
            },
        }


@dataclass(frozen=True)
class SearchResult:
    request_id: str
    state_id: str
    status: str
    elapsed_ms: int
    iterations: int
    recommendations: tuple[Recommendation, ...]
    progress: tuple[dict[str, Any], ...]
    coverage: dict[str, Any]

    def to_dict(self) -> dict[str, Any]:
        return {
            "api_version": API_VERSION,
            "schema_version": 1,
            "request_id": self.request_id,
            "state_id": self.state_id,
            "status": self.status,
            "elapsed_ms": self.elapsed_ms,
            "iterations": self.iterations,
            "recommendations": [item.to_dict() for item in self.recommendations],
            "progress": list(self.progress),
            "coverage": self.coverage,
            "warnings": [
                item.get("detail", "") for item in self.coverage.get("approximate_effects", [])
            ],
            "model_version": self.coverage.get(
                "planner_model",
                self.coverage.get("rules_model", "counterplay-turnpair-v1"),
            ),
            "environment_version": self.coverage.get("environment_version", ""),
            "is_final": True,
            "generated_at_utc": datetime.now(timezone.utc).isoformat(),
            "message": (
                "Recommendations include approximated mechanics."
                if not self.coverage.get("exact", False)
                else "Counterplay turn-pair solve completed."
            ),
        }
