from __future__ import annotations

import copy
import hashlib
import io
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterable, Mapping


MAX_CARD_DEFS_BYTES = 128 * 1024 * 1024
MAX_CARD_TEXT_CHARS = 16_384
MAX_CARD_NAME_CHARS = 512

_SAFE_BUILD = re.compile(r"^[0-9]+$")
_SAFE_CARD_ID = re.compile(r"^[A-Za-z0-9_.:-]{1,128}$")
_CARD_TYPES = {
    3: "HERO",
    4: "MINION",
    5: "SPELL",
    7: "WEAPON",
    10: "HERO_POWER",
    39: "LOCATION",
}
_BASE_INTEGER_TAGS = {
    "COST": "base_cost",
    "ATK": "base_attack",
    "HEALTH": "base_health",
    "DURABILITY": "base_durability",
}
_INTRINSIC_BOOLEAN_TAGS = {
    "TAUNT": "taunt",
    "DIVINE_SHIELD": "divine_shield",
    "STEALTH": "stealth",
    "POISONOUS": "poisonous",
    "LIFESTEAL": "lifesteal",
    "WINDFURY": "windfury",
    "MEGA_WINDFURY": "mega_windfury",
    "RUSH": "rush",
    "CHARGE": "charge",
    "REBORN": "reborn",
    "DORMANT": "dormant",
    "IMMUNE": "immune",
}


class HdtCardDefsError(ValueError):
    def __init__(self, code: str, detail: str = "") -> None:
        super().__init__(f"{code}: {detail}" if detail else code)
        self.code = code
        self.detail = detail


@dataclass(frozen=True)
class HdtCardDefinition:
    card_id: str
    card_type: str
    english_name: str
    english_text: str
    base_cost: int | None = None
    base_attack: int | None = None
    base_health: int | None = None
    base_durability: int | None = None
    intrinsic_true_fields: tuple[str, ...] = ()

    @property
    def english_text_sha256(self) -> str:
        return hashlib.sha256(self.english_text.encode("utf-8")).hexdigest()


@dataclass(frozen=True)
class HdtCardDefsSnapshot:
    build: str
    file_name: str
    byte_count: int
    sha256: str
    requested_card_id_count: int
    cards: Mapping[str, HdtCardDefinition] = field(default_factory=dict)

    def manifest_summary(self) -> dict[str, Any]:
        text_count = sum(bool(card.english_text.strip()) for card in self.cards.values())
        type_count = sum(card.card_type != "UNKNOWN" for card in self.cards.values())
        return {
            "file_name": self.file_name,
            "build": self.build,
            "bytes": self.byte_count,
            "sha256": self.sha256,
            "requested_public_card_id_count": self.requested_card_id_count,
            "matched_public_card_id_count": len(self.cards),
            "missing_public_card_id_count": max(
                0, self.requested_card_id_count - len(self.cards)
            ),
            "card_type_available_count": type_count,
            "english_text_available_count": text_count,
        }


def _bounded_bytes(path: Path) -> bytes:
    try:
        size = path.stat().st_size
    except OSError as exc:
        raise HdtCardDefsError("card_defs_missing") from exc
    if not path.is_file() or size <= 0 or size > MAX_CARD_DEFS_BYTES:
        raise HdtCardDefsError("card_defs_size_invalid")
    try:
        payload = path.read_bytes()
    except OSError as exc:
        raise HdtCardDefsError("card_defs_unreadable") from exc
    if len(payload) != size:
        raise HdtCardDefsError("card_defs_changed_during_read")
    upper_prefix = payload[: 64 * 1024].upper()
    if b"<!DOCTYPE" in upper_prefix or b"<!ENTITY" in upper_prefix:
        raise HdtCardDefsError("card_defs_unsafe_xml")
    return payload


def _integer_tag(tag: ET.Element) -> int | None:
    raw = str(tag.attrib.get("value") or "").strip()
    if not raw:
        return None
    try:
        value = int(raw)
    except ValueError:
        return None
    return value if value >= 0 else None


def _localized_text(tag: ET.Element, locale: str = "enUS") -> str:
    child = next((item for item in tag if item.tag == locale), None)
    if child is None:
        return ""
    return "".join(child.itertext())


def _definition(entity: ET.Element, card_id: str) -> HdtCardDefinition:
    card_type = "UNKNOWN"
    english_name = ""
    english_text = ""
    integers: dict[str, int] = {}
    mechanics: set[str] = set()
    for tag in entity:
        if tag.tag != "Tag":
            continue
        name = str(tag.attrib.get("name") or "").strip().upper()
        if name == "CARDNAME":
            english_name = _localized_text(tag)
        elif name == "CARDTEXT":
            english_text = _localized_text(tag)
        elif name == "CARDTYPE":
            value = _integer_tag(tag)
            card_type = _CARD_TYPES.get(value or -1, "UNKNOWN")
        elif name in _BASE_INTEGER_TAGS:
            value = _integer_tag(tag)
            if value is not None:
                integers[_BASE_INTEGER_TAGS[name]] = value
        elif name in _INTRINSIC_BOOLEAN_TAGS:
            value = _integer_tag(tag)
            if value:
                mechanics.add(_INTRINSIC_BOOLEAN_TAGS[name])
    if len(english_name) > MAX_CARD_NAME_CHARS:
        raise HdtCardDefsError("card_defs_card_name_too_long", card_id)
    if len(english_text) > MAX_CARD_TEXT_CHARS:
        raise HdtCardDefsError("card_defs_card_text_too_long", card_id)
    return HdtCardDefinition(
        card_id=card_id,
        card_type=card_type,
        english_name=english_name,
        english_text=english_text,
        base_cost=integers.get("base_cost"),
        base_attack=integers.get("base_attack"),
        base_health=integers.get("base_health"),
        base_durability=integers.get("base_durability"),
        intrinsic_true_fields=tuple(sorted(mechanics)),
    )


def load_hdt_card_defs(
    path: str | Path,
    *,
    requested_card_ids: Iterable[str],
    expected_builds: Iterable[str] = (),
) -> HdtCardDefsSnapshot:
    """Load only requested public cards from one immutable CardDefs snapshot.

    The caller supplies the public CardIDs.  This function never enumerates a
    hidden opponent zone and never treats CardDefs as action-legality or
    optimality evidence.
    """

    source = Path(path)
    payload = _bounded_bytes(source)
    requested = {
        str(card_id).strip()
        for card_id in requested_card_ids
        if _SAFE_CARD_ID.fullmatch(str(card_id).strip()) is not None
    }
    expected = {
        str(build).strip()
        for build in expected_builds
        if str(build).strip()
    }
    if any(_SAFE_BUILD.fullmatch(build) is None for build in expected):
        raise HdtCardDefsError("card_defs_expected_build_invalid")

    build = ""
    cards: dict[str, HdtCardDefinition] = {}
    try:
        iterator = ET.iterparse(io.BytesIO(payload), events=("start", "end"))
        for event, element in iterator:
            if event == "start" and not build:
                if element.tag != "CardDefs":
                    raise HdtCardDefsError("card_defs_root_invalid")
                build = str(element.attrib.get("build") or "").strip()
                if _SAFE_BUILD.fullmatch(build) is None:
                    raise HdtCardDefsError("card_defs_build_invalid")
                if expected and expected != {build}:
                    raise HdtCardDefsError(
                        "card_defs_build_mismatch",
                        f"expected={','.join(sorted(expected))};actual={build}",
                    )
            if event != "end" or element.tag != "Entity":
                continue
            card_id = str(element.attrib.get("CardID") or "").strip()
            if card_id in requested:
                if card_id in cards:
                    raise HdtCardDefsError("card_defs_duplicate_card_id", card_id)
                cards[card_id] = _definition(element, card_id)
            element.clear()
    except HdtCardDefsError:
        raise
    except (ET.ParseError, OSError, ValueError) as exc:
        raise HdtCardDefsError("card_defs_xml_invalid") from exc
    if not build:
        raise HdtCardDefsError("card_defs_build_invalid")
    return HdtCardDefsSnapshot(
        build=build,
        file_name=source.name,
        byte_count=len(payload),
        sha256=hashlib.sha256(payload).hexdigest(),
        requested_card_id_count=len(requested),
        cards=cards,
    )


def iter_public_state_entities(
    state: Mapping[str, Any],
) -> Iterable[tuple[str, str, Mapping[str, Any]]]:
    """Yield only public entity slots from a canonical friendly perspective."""

    for role in ("friendly", "opponent"):
        player = state.get(role)
        if not isinstance(player, Mapping):
            continue
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping):
                yield role, zone, entity
        if role == "friendly":
            hand = player.get("hand")
            if isinstance(hand, list):
                for entity in hand:
                    if isinstance(entity, Mapping) and entity.get("visibility") != "hidden":
                        yield role, "hand", entity
        board = player.get("board")
        if isinstance(board, list):
            for entity in board:
                if isinstance(entity, Mapping) and entity.get("visibility") != "hidden":
                    yield role, "board", entity


def public_card_ids(states: Iterable[Mapping[str, Any]]) -> set[str]:
    result: set[str] = set()
    for state in states:
        for _role, _zone, entity in iter_public_state_entities(state):
            card_id = str(entity.get("card_id") or "").strip()
            if _SAFE_CARD_ID.fullmatch(card_id) is not None:
                result.add(card_id)
    return result


def enrich_public_solver_state(
    state: Mapping[str, Any], snapshot: HdtCardDefsSnapshot
) -> tuple[dict[str, Any], dict[str, int]]:
    """Overlay immutable public card definition fields on a detached solve state.

    Dynamic costs, stats, zones and readiness always remain those observed in
    the replay.  Intrinsic mechanic flags are filled only for friendly hand
    cards and hero powers, where a matching public CardID identifies the card
    definition without guessing a mutable board enchantment.
    """

    enriched = copy.deepcopy(dict(state))
    metrics = {
        "public_entity_count": 0,
        "card_id_matched_entity_count": 0,
        "card_type_mismatch_entity_count": 0,
        "english_text_injected_entity_count": 0,
        "english_name_injected_entity_count": 0,
        "intrinsic_field_injected_count": 0,
        "hidden_opponent_hand_entity_touched_count": 0,
    }
    for role, zone, entity_value in iter_public_state_entities(enriched):
        if not isinstance(entity_value, dict):
            continue
        metrics["public_entity_count"] += 1
        card_id = str(entity_value.get("card_id") or "").strip()
        definition = snapshot.cards.get(card_id)
        if definition is None:
            continue
        metrics["card_id_matched_entity_count"] += 1
        observed_type = str(entity_value.get("card_type") or "UNKNOWN").upper()
        if (
            definition.card_type != "UNKNOWN"
            and observed_type != definition.card_type
        ):
            metrics["card_type_mismatch_entity_count"] += 1
            continue
        if definition.english_text:
            entity_value["english_text"] = definition.english_text
            metrics["english_text_injected_entity_count"] += 1
        if definition.english_name:
            entity_value["name"] = definition.english_name
            metrics["english_name_injected_entity_count"] += 1
        if role == "friendly" and zone in {"hand", "hero_power"}:
            for field_name in definition.intrinsic_true_fields:
                if field_name not in entity_value:
                    entity_value[field_name] = True
                    metrics["intrinsic_field_injected_count"] += 1
    return enriched, metrics
