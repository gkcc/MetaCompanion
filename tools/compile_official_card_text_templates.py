#!/usr/bin/env python3
"""Compile closed-grammar Hearthstone text into hash-bound generic effects.

This compiler is deliberately narrower than a natural-language model.  It only
accepts text that is consumed in full by one of the reviewed templates below.
The resulting rules are executable *generic approximations*: they may take part
in Rust visible-state search, but they never claim exact transitions or global
optimality.  Every current Standard/Arena card is retained in the audit report,
including a stable reason when no template can be compiled.
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Mapping, NamedTuple, Sequence

TOOLS_DIR = Path(__file__).resolve().parent
ROOT = TOOLS_DIR.parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from classify_mainstream_card_effects import classify_card as classify_semantic_card


DEFAULT_POOL_ROOT = (
    Path.home()
    / "AppData"
    / "Roaming"
    / "HearthstoneDeckTracker"
    / "MetaCompanion"
    / "AdvisorData"
    / "OfficialCardPools"
    / "latest"
)
DEFAULT_EXACT_RULES = (
    ROOT
    / "solver"
    / "metacompanion_solver"
    / "rules_data"
    / "hdt-visible-point-effects-v1.json"
)
DEFAULT_OUTPUT = (
    ROOT
    / "solver"
    / "metacompanion_solver"
    / "rules_data"
    / "hdt-text-template-effects-v1.json"
)
DEFAULT_REPORT = (
    ROOT
    / "artifacts"
    / "card-modeling"
    / "official-current"
    / "card-text-compilation-report.json"
)
DEFAULT_CARD_DEFS = (
    Path.home()
    / "AppData"
    / "Roaming"
    / "HearthstoneDeckTracker"
    / "CardDefs"
    / "CardDefs.base.xml"
)

SCHEMA_VERSION = 1
RULESET_ID = "hdt-text-template-effects-v1"
MATCHING_CONTRACT = (
    "card_id+normalized_english_text_sha256+card_type+closed_grammar_v1"
)

TAG_RE = re.compile(r"<[^>]*>")
VARIABLE_RE = re.compile(r"[$#](?=\d)")
SPACE_RE = re.compile(r"\s+")

CARD_TYPES = {
    4: "MINION",
    5: "SPELL",
    7: "WEAPON",
    10: "HERO_POWER",
    39: "LOCATION",
}

TARGETS = {
    "": "any_character",
    "a character": "any_character",
    "a friendly character": "friendly_character",
    "a friendly minion": "friendly_minion",
    "a minion": "any_minion",
    "this minion": "self",
    "an undamaged minion": "any_undamaged_minion",
    "a damaged enemy minion": "damaged_enemy_minion",
    "an enemy": "enemy_character",
    "an enemy minion": "enemy_minion",
    "the enemy hero": "enemy_hero",
    "your hero": "friendly_hero",
    "all enemies": "all_enemy_characters",
    "all enemy characters": "all_enemy_characters",
    "all enemy minions": "all_enemy_minions",
    "all friendly minions": "all_friendly_minions",
    "your minions": "all_friendly_minions",
    "your other minions": "all_other_friendly_minions",
    "all friendly characters": "all_friendly_characters",
    "all minions": "all_minions",
    "all characters": "all_characters",
    "all other minions": "all_other_minions",
    "all other friendly minions": "all_other_friendly_minions",
    "this": "self",
}

TARGET_PATTERN = "|".join(
    sorted((re.escape(value) for value in TARGETS if value), key=len, reverse=True)
)

DAMAGE_RE = re.compile(
    rf"Deal (?P<amount>\d+) damage(?: to (?P<target>{TARGET_PATTERN}))?",
    re.IGNORECASE,
)
RANDOM_TARGETS = {
    "a random enemy": "enemy_character",
    "a random enemy minion": "enemy_minion",
    "a random friendly minion": "friendly_minion",
    "a random minion": "any_minion",
}
RANDOM_TARGET_PATTERN = "|".join(
    sorted((re.escape(value) for value in RANDOM_TARGETS), key=len, reverse=True)
)
RANDOM_DAMAGE_RE = re.compile(
    rf"Deal (?P<amount>\d+) damage to (?P<target>{RANDOM_TARGET_PATTERN})",
    re.IGNORECASE,
)
HEAL_RE = re.compile(
    rf"Restore (?P<amount>\d+) Health(?: to (?P<target>{TARGET_PATTERN}))?",
    re.IGNORECASE,
)
RANDOM_HEAL_RE = re.compile(
    rf"Restore (?P<amount>\d+) Health to (?P<target>{RANDOM_TARGET_PATTERN})",
    re.IGNORECASE,
)
ARMOR_RE = re.compile(r"Gain (?P<amount>\d+) Armor", re.IGNORECASE)
FREEZE_RE = re.compile(
    rf"Freeze (?P<target>{TARGET_PATTERN})",
    re.IGNORECASE,
)
RANDOM_FREEZE_RE = re.compile(
    rf"Freeze (?P<target>{RANDOM_TARGET_PATTERN})",
    re.IGNORECASE,
)
BUFF_RE = re.compile(
    rf"Give (?P<target>{TARGET_PATTERN}) \+(?P<attack>\d+)/\+(?P<health>\d+)",
    re.IGNORECASE,
)
HERO_ATTACK_RE = re.compile(
    r"(?:Give your hero \+|Your hero gains? )(?P<amount>\d+) Attack(?: this turn)?",
    re.IGNORECASE,
)
DRAW_RE = re.compile(
    r"Draw (?:(?:a|one|1) card|(?P<count>\d+) cards)",
    re.IGNORECASE,
)
FILTERED_DRAW_DESCRIPTOR_PATTERN = (
    r"(?:Deathrattle|Taunt|Outcast) (?:card|minion)s?|"
    r"(?:Arcane|Fire|Frost|Nature|Holy|Shadow|Fel) spells?|"
    r"(?:Murloc|Demon|Mech|Elemental|Beast|Totem|Pirate|Dragon|Quilboar|Undead|Naga)s?|"
    r"(?:minion|spell)s?"
)
FILTERED_DRAW_RE = re.compile(
    r"Draw (?P<count>a|an|one|two|three|four|five|six|seven|\d+) "
    rf"(?P<descriptor>{FILTERED_DRAW_DESCRIPTOR_PATTERN})(?: from your deck)?",
    re.IGNORECASE,
)
COST_FILTERED_DRAW_RE = re.compile(
    r"Draw (?P<count>a|an|one|two|three|four|five|six|seven|\d+) "
    rf"(?P<descriptor>{FILTERED_DRAW_DESCRIPTOR_PATTERN}) that costs "
    r"\((?P<cost>\d+)\) or (?P<bound>more|less)(?: from your deck)?",
    re.IGNORECASE,
)
COST_SERIES_DRAW_RE = re.compile(
    r"Draw a (?P<costs>\d+(?:,\s*\d+)*(?:,\s*and\s*\d+|\s+and\s+\d+)?)"
    r"-Cost (?P<descriptor>minion|spell)(?: from your deck)?",
    re.IGNORECASE,
)
DRAW_UNTIL_HAND_RE = re.compile(
    r"Draw until you have (?P<count>\d+) cards", re.IGNORECASE
)
DRAW_OPPONENT_RE = re.compile(
    r"Your opponent draws (?:(?:a|one) card|(?P<count>\d+) cards)",
    re.IGNORECASE,
)
DRAW_EACH_PLAYER_RE = re.compile(
    r"(?:Each player draws|Both players draw) "
    r"(?:(?:a|one) card|(?P<count>\d+) cards)",
    re.IGNORECASE,
)
BUFF_ATTACK_ONLY_RE = re.compile(
    rf"Give (?P<target>{TARGET_PATTERN}) \+(?P<amount>\d+) Attack",
    re.IGNORECASE,
)
RANDOM_BUFF_ATTACK_ONLY_RE = re.compile(
    rf"Give (?P<target>{RANDOM_TARGET_PATTERN}) \+(?P<amount>\d+) Attack",
    re.IGNORECASE,
)
BUFF_HEALTH_ONLY_RE = re.compile(
    rf"Give (?P<target>{TARGET_PATTERN}) \+(?P<amount>\d+) Health",
    re.IGNORECASE,
)
RANDOM_BUFF_HEALTH_ONLY_RE = re.compile(
    rf"Give (?P<target>{RANDOM_TARGET_PATTERN}) \+(?P<amount>\d+) Health",
    re.IGNORECASE,
)
GAIN_STATS_RE = re.compile(
    r"Gain \+(?P<attack>\d+)/\+(?P<health>\d+)", re.IGNORECASE
)
GAIN_ATTACK_SELF_RE = re.compile(r"Gain \+(?P<amount>\d+) Attack", re.IGNORECASE)
GAIN_HEALTH_SELF_RE = re.compile(r"Gain \+(?P<amount>\d+) Health", re.IGNORECASE)
BUFF_WEAPON_ATTACK_RE = re.compile(
    r"Give your weapon \+(?P<amount>\d+) Attack(?: this turn)?", re.IGNORECASE
)
SUMMON_RE = re.compile(
    r"Summon (?P<count>a|an|one|two|three|four|five|six|seven|\d+) "
    r"(?P<attack>\d+)/(?P<health>\d+) (?P<description>.+)",
    re.IGNORECASE,
)
NAMED_SUMMON_RE = re.compile(
    r"Summon (?P<count>a|an|one|two|three|four|five|six|seven|\d+) "
    r"(?P<description>.+)",
    re.IGNORECASE,
)
DESTROY_RE = re.compile(
    rf"Destroy (?P<target>a minion|an enemy minion|a friendly minion|a damaged enemy minion|all enemy minions|all friendly minions|all minions)",
    re.IGNORECASE,
)
DESTROY_BOARD_RE = re.compile(
    r"Destroy all minions and locations",
    re.IGNORECASE,
)
TRANSFORM_RE = re.compile(
    r"Transform (?P<target>a minion|an enemy minion|a friendly minion) into an? "
    r"(?P<attack>\d+)/(?P<health>\d+) (?P<description>.+)",
    re.IGNORECASE,
)
SELF_TRANSFORM_RE = re.compile(
    r"Transform into an? (?P<attack>\d+)/(?P<health>\d+) (?P<description>.+)",
    re.IGNORECASE,
)
EQUIP_RE = re.compile(
    r"Equip an? (?P<attack>\d+)/(?P<durability>\d+) (?P<description>.+)",
    re.IGNORECASE,
)
SET_STATS_RE = re.compile(
    r"Set (?P<target>a minion|an enemy minion|a friendly minion)'s stats to "
    r"(?P<attack>\d+)/(?P<health>\d+)",
    re.IGNORECASE,
)
SET_ATTACK_HEALTH_RE = re.compile(
    r"Set (?:the )?(?P<target>a minion|an enemy minion|a friendly minion)'s "
    r"Attack and Health to (?P<amount>\d+)",
    re.IGNORECASE,
)
SET_HEALTH_RE = re.compile(
    rf"Set the Health of (?P<target>{TARGET_PATTERN}) to (?P<amount>\d+)",
    re.IGNORECASE,
)
RANDOM_SET_HEALTH_RE = re.compile(
    rf"Set the Health of (?P<target>{RANDOM_TARGET_PATTERN}) to (?P<amount>\d+)",
    re.IGNORECASE,
)
FIXED_DAMAGE_REPEAT_RE = re.compile(
    rf"(?P<clause>Deal \d+ damage(?: to (?:{TARGET_PATTERN}))?), "
    r"(?P<repetitions>twice|three times)",
    re.IGNORECASE,
)
GRANT_KEYWORDS_RE = re.compile(
    rf"Give (?P<target>{TARGET_PATTERN}) (?P<keywords>(?:(?:Taunt|Rush|Charge|"
    r"Lifesteal|Divine Shield|Reborn|Windfury|Stealth|Poisonous)"
    r"(?:\s*(?:,|and)\s*|\s+)?)+)",
    re.IGNORECASE,
)
GAIN_KEYWORDS_RE = re.compile(
    r"Gain (?P<keywords>(?:(?:Taunt|Rush|Charge|Lifesteal|Divine Shield|Reborn|"
    r"Windfury|Stealth|Poisonous)(?:\s*(?:,|and)\s*|\s+)?)+)",
    re.IGNORECASE,
)
REFRESH_MANA_RE = re.compile(
    r"Refresh (?:(?P<count>\d+)|(?P<count_word>a|one|two|three|four|five|six|seven)) "
    r"Mana Crystals?",
    re.IGNORECASE,
)
REFRESH_ALL_MANA_RE = re.compile(r"Refresh your Mana Crystals", re.IGNORECASE)
GAIN_MANA_RE = re.compile(
    r"Gain (?:(?P<count>\d+)|(?P<count_word>a|one|two|three|four|five|six|seven)) "
    r"(?P<empty>empty )?Mana Crystals?(?P<temporary> this turn only)?",
    re.IGNORECASE,
)
GAIN_TEMPORARY_MANA_RE = re.compile(
    r"Gain (?:a|one|1) temporary Mana Crystal",
    re.IGNORECASE,
)

COUNT_WORDS = {
    "a": 1,
    "an": 1,
    "one": 1,
    "two": 2,
    "three": 3,
    "four": 4,
    "five": 5,
    "six": 6,
    "seven": 7,
}

SPELL_SCHOOL_IDS = {
    "arcane": 1,
    "fire": 2,
    "frost": 3,
    "nature": 4,
    "holy": 5,
    "shadow": 6,
    "fel": 7,
}

MINION_TYPE_IDS = {
    "murloc": 14,
    "demon": 15,
    "mech": 17,
    "elemental": 18,
    "beast": 20,
    "totem": 21,
    "pirate": 23,
    "dragon": 24,
    "quilboar": 43,
    "undead": 77,
    "naga": 92,
}

TRAILING_PARSER_REMINDER_RE = re.compile(
    r"\s*\((?:Upgrades when you have \d+ Mana\.|\+\d+ Attack/\+\d+ Health)\)\s*$",
    re.IGNORECASE,
)

TOKEN_KEYWORD_FIELDS = {
    "taunt": "taunt",
    "divine shield": "divine_shield",
    "stealth": "stealth",
    "poisonous": "poisonous",
    "lifesteal": "lifesteal",
    "windfury": "windfury",
    "rush": "rush",
    "charge": "charge",
    "reborn": "reborn",
}
TOKEN_KEYWORD_TAGS = {
    "TAUNT": "taunt",
    "DIVINE_SHIELD": "divine_shield",
    "STEALTH": "stealth",
    "POISONOUS": "poisonous",
    "LIFESTEAL": "lifesteal",
    "WINDFURY": "windfury",
    "RUSH": "rush",
    "CHARGE": "charge",
    "REBORN": "reborn",
}
TOKEN_KEYWORD_PATTERN = re.compile(
    "|".join(
        sorted((re.escape(value) for value in TOKEN_KEYWORD_FIELDS), key=len, reverse=True)
    ),
    re.IGNORECASE,
)

LEADING_INTRINSIC_RE = re.compile(
    r"^(?:(?:Taunt|Rush|Charge|Lifesteal|Divine Shield|Reborn|Windfury|"
    r"Elusive|Stealth|Poisonous)(?:\s*[,.;]\s*|\s+))+",
    re.IGNORECASE,
)


class CardDef(NamedTuple):
    card_id: str
    dbf_id: int
    name: str
    text: str
    card_type_id: int
    attack: int
    health: int
    durability: int
    related_dbf_ids: tuple[int, ...]
    keywords: frozenset[str]


class CardDefIndex(NamedTuple):
    by_card_id: Mapping[str, CardDef]
    by_dbf_id: Mapping[int, CardDef]


def normalize_card_text(value: Any) -> str:
    text = html.unescape(str(value or "")).replace("[x]", " ").replace("\u00a0", " ")
    text = TAG_RE.sub(" ", text)
    text = VARIABLE_RE.sub("", text)
    return SPACE_RE.sub(" ", text).strip()


def normalized_text_sha256(value: str) -> str:
    return hashlib.sha256(normalize_card_text(value).encode("utf-8")).hexdigest()


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _integer(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _localized_tag_text(tag: ET.Element) -> str:
    value = tag.find("enUS")
    return "" if value is None else "".join(value.itertext())


def load_card_defs(path: Path) -> CardDefIndex:
    """Load the small CardDefs subset needed for reviewed token resolution."""

    if not path.is_file():
        raise FileNotFoundError(f"CardDefs file does not exist: {path}")
    by_card_id: dict[str, CardDef] = {}
    by_dbf_id: dict[int, CardDef] = {}
    tree = ET.parse(path)
    for entity in tree.getroot().iter("Entity"):
        card_id = str(entity.get("CardID", "")).strip()
        dbf_id = _integer(entity.get("ID"))
        if not card_id or dbf_id <= 0:
            continue
        name = ""
        text = ""
        card_type_id = 0
        attack = 0
        health = 0
        durability = 0
        related: list[int] = []
        keywords: set[str] = set()
        for tag in entity.findall("Tag"):
            tag_name = str(tag.get("name", "")).upper()
            if tag_name == "CARDNAME":
                name = _localized_tag_text(tag)
            elif tag_name == "CARDTEXT":
                text = _localized_tag_text(tag)
            elif tag_name == "CARDTYPE":
                card_type_id = _integer(tag.get("value"))
            elif tag_name == "ATK":
                attack = _integer(tag.get("value"))
            elif tag_name == "HEALTH":
                health = _integer(tag.get("value"))
            elif tag_name == "DURABILITY":
                durability = _integer(tag.get("value"))
            elif tag_name == "COLLECTION_RELATED_CARD_DATABASE_ID":
                related_id = _integer(tag.get("value"))
                if related_id > 0:
                    related.append(related_id)
            elif tag_name in TOKEN_KEYWORD_TAGS and _integer(tag.get("value")) > 0:
                keywords.add(TOKEN_KEYWORD_TAGS[tag_name])
        # CardDefs exposes a weapon's durability through HEALTH on current
        # builds, while live HDT normalizes the same value as durability.
        if card_type_id == 7 and durability == 0 and health > 0:
            durability = health
            health = 0
        definition = CardDef(
            card_id=card_id,
            dbf_id=dbf_id,
            name=name,
            text=text,
            card_type_id=card_type_id,
            attack=attack,
            health=health,
            durability=durability,
            related_dbf_ids=tuple(related),
            keywords=frozenset(keywords),
        )
        by_card_id[card_id] = definition
        by_dbf_id[dbf_id] = definition
    return CardDefIndex(by_card_id=by_card_id, by_dbf_id=by_dbf_id)


def _parse_token_keywords(value: str) -> set[str] | None:
    text = normalize_card_text(value).strip().rstrip(".").strip()
    if not text:
        return set()
    matches = TOKEN_KEYWORD_PATTERN.findall(text)
    remainder = TOKEN_KEYWORD_PATTERN.sub(" ", text)
    remainder = re.sub(r"[\s,./&+]+|\band\b", "", remainder, flags=re.IGNORECASE)
    if remainder:
        return None
    return {TOKEN_KEYWORD_FIELDS[item.lower()] for item in matches}


def _definition_token_keywords(definition: CardDef) -> set[str] | None:
    parsed = _parse_token_keywords(definition.text)
    if parsed is None:
        return None
    return set(definition.keywords).union(parsed)


def _descriptor_name_and_keywords(value: str) -> tuple[str, set[str]] | None:
    description = value.strip().rstrip(".").strip()
    name = description
    explicit: set[str] = set()
    if " with " in description.lower():
        start = description.lower().rfind(" with ")
        name = description[:start].strip()
        parsed = _parse_token_keywords(description[start + len(" with ") :])
        if parsed is None:
            return None
        explicit = parsed
    name = re.sub(r"^another\s+", "", name, flags=re.IGNORECASE).strip()
    if not name or any(
        marker in name.lower()
        for marker in (
            " random ",
            " copy of ",
            " from ",
            " for ",
            " equal to ",
            " that ",
            '"',
        )
    ):
        return None
    return name, explicit


def _choose_token_definition(
    card_defs: CardDefIndex,
    source_card_id: str,
    card_type_id: int,
    attack: int,
    health: int,
    durability: int,
    descriptor_name: str,
) -> CardDef | None:
    source = card_defs.by_card_id.get(source_card_id)
    related = [] if source is None else [
        card_defs.by_dbf_id[dbf_id]
        for dbf_id in source.related_dbf_ids
        if dbf_id in card_defs.by_dbf_id
    ]

    def eligible(definition: CardDef) -> bool:
        return (
            definition.card_type_id == card_type_id
            and definition.attack == attack
            and definition.health == health
            and definition.durability == durability
            and _definition_token_keywords(definition) is not None
        )

    related = [definition for definition in related if eligible(definition)]
    if len(related) == 1:
        return related[0]

    prefixed = [
        definition
        for card_id, definition in card_defs.by_card_id.items()
        if card_id != source_card_id
        and card_id.lower().startswith(source_card_id.lower())
        and eligible(definition)
    ]
    if len(prefixed) == 1:
        return prefixed[0]

    normalized_descriptor = re.sub(
        r"[^a-z0-9]+", "", descriptor_name.lower().rstrip("s")
    )
    local_candidates = {
        definition.card_id: definition
        for definition in related + prefixed
        if re.sub(r"[^a-z0-9]+", "", definition.name.lower().rstrip("s"))
        == normalized_descriptor
    }
    if len(local_candidates) == 1:
        return next(iter(local_candidates.values()))

    global_candidates = {
        definition.card_id: definition
        for definition in card_defs.by_card_id.values()
        if eligible(definition)
        and re.sub(r"[^a-z0-9]+", "", definition.name.lower().rstrip("s"))
        == normalized_descriptor
    }
    return next(iter(global_candidates.values())) if len(global_candidates) == 1 else None


def _choose_named_minion_definition(
    card_defs: CardDefIndex,
    source_card_id: str,
    descriptor_name: str,
) -> CardDef | None:
    source = card_defs.by_card_id.get(source_card_id)
    related = [] if source is None else [
        card_defs.by_dbf_id[dbf_id]
        for dbf_id in source.related_dbf_ids
        if dbf_id in card_defs.by_dbf_id
    ]
    prefixed = [
        definition
        for card_id, definition in card_defs.by_card_id.items()
        if card_id != source_card_id and card_id.lower().startswith(source_card_id.lower())
    ]
    normalized_descriptor = re.sub(
        r"[^a-z0-9]+", "", descriptor_name.lower().rstrip("s")
    )

    def eligible(definition: CardDef) -> bool:
        return (
            definition.card_type_id == 4
            and definition.health > 0
            and _definition_token_keywords(definition) is not None
            and re.sub(r"[^a-z0-9]+", "", definition.name.lower().rstrip("s"))
            == normalized_descriptor
        )

    local = {
        definition.card_id: definition
        for definition in related + prefixed
        if eligible(definition)
    }
    if len(local) == 1:
        return next(iter(local.values()))
    global_matches = {
        definition.card_id: definition
        for definition in card_defs.by_card_id.values()
        if eligible(definition)
    }
    return next(iter(global_matches.values())) if len(global_matches) == 1 else None


def _summon_effect(
    match: re.Match[str],
    source_card_id: str,
    card_defs: CardDefIndex | None,
) -> dict[str, Any] | None:
    if card_defs is None:
        return None
    count_text = match.group("count").lower()
    count = COUNT_WORDS.get(count_text, _integer(count_text))
    attack = int(match.group("attack"))
    health = int(match.group("health"))
    if not 1 <= count <= 7 or health <= 0:
        return None
    descriptor = _descriptor_name_and_keywords(match.group("description"))
    if descriptor is None:
        return None
    descriptor_name, explicit_keywords = descriptor
    definition = _choose_token_definition(
        card_defs, source_card_id, 4, attack, health, 0, descriptor_name
    )
    if definition is None:
        return None
    definition_keywords = _definition_token_keywords(definition)
    if definition_keywords is None:
        return None
    keywords = definition_keywords.union(explicit_keywords)
    effect: dict[str, Any] = {
        "kind": "summon",
        "target": "none",
        "count": count,
        "card_id": definition.card_id,
        "name": definition.name or descriptor_name,
        "attack": attack,
        "health": health,
    }
    for keyword in sorted(keywords):
        effect[keyword] = True
    return effect


def _named_summon_effect(
    match: re.Match[str],
    source_card_id: str,
    card_defs: CardDefIndex | None,
) -> dict[str, Any] | None:
    if card_defs is None:
        return None
    count_text = match.group("count").lower()
    count = COUNT_WORDS.get(count_text, _integer(count_text))
    if not 1 <= count <= 7:
        return None
    descriptor = _descriptor_name_and_keywords(match.group("description"))
    if descriptor is None:
        return None
    descriptor_name, explicit_keywords = descriptor
    definition = _choose_named_minion_definition(
        card_defs, source_card_id, descriptor_name
    )
    if definition is None:
        return None
    definition_keywords = _definition_token_keywords(definition)
    if definition_keywords is None:
        return None
    effect: dict[str, Any] = {
        "kind": "summon",
        "target": "none",
        "count": count,
        "card_id": definition.card_id,
        "name": definition.name or descriptor_name,
        "attack": definition.attack,
        "health": definition.health,
    }
    for keyword in sorted(definition_keywords.union(explicit_keywords)):
        effect[keyword] = True
    return effect


def _transform_effect(
    match: re.Match[str],
    source_card_id: str,
    card_defs: CardDefIndex | None,
    target: str | None = None,
) -> dict[str, Any] | None:
    if card_defs is None:
        return None
    attack = int(match.group("attack"))
    health = int(match.group("health"))
    if health <= 0:
        return None
    descriptor = _descriptor_name_and_keywords(match.group("description"))
    if descriptor is None:
        return None
    descriptor_name, explicit_keywords = descriptor
    definition = _choose_token_definition(
        card_defs, source_card_id, 4, attack, health, 0, descriptor_name
    )
    if definition is None:
        return None
    definition_keywords = _definition_token_keywords(definition)
    if definition_keywords is None:
        return None
    effect: dict[str, Any] = {
        "kind": "transform",
        "target": target or _canonical_target(match.group("target")),
        "card_id": definition.card_id,
        "name": definition.name or descriptor_name,
        "attack": attack,
        "health": health,
    }
    for keyword in sorted(definition_keywords.union(explicit_keywords)):
        effect[keyword] = True
    return effect


def _equip_effect(
    match: re.Match[str],
    source_card_id: str,
    card_defs: CardDefIndex | None,
) -> dict[str, Any] | None:
    if card_defs is None:
        return None
    attack = int(match.group("attack"))
    durability = int(match.group("durability"))
    if attack <= 0 or durability <= 0:
        return None
    descriptor = _descriptor_name_and_keywords(match.group("description"))
    if descriptor is None:
        return None
    descriptor_name, explicit_keywords = descriptor
    definition = _choose_token_definition(
        card_defs, source_card_id, 7, attack, 0, durability, descriptor_name
    )
    if definition is None:
        return None
    definition_keywords = _definition_token_keywords(definition)
    if definition_keywords is None:
        return None
    effect: dict[str, Any] = {
        "kind": "equip_weapon",
        "target": "none",
        "card_id": definition.card_id,
        "name": definition.name or descriptor_name,
        "attack": attack,
        "durability": durability,
    }
    for keyword in sorted(definition_keywords.union(explicit_keywords)):
        effect[keyword] = True
    return effect


def _canonical_target(value: str | None) -> str:
    key = (value or "").strip().lower()
    try:
        return TARGETS[key]
    except KeyError as exc:  # pragma: no cover - guarded by the regex grammar
        raise ValueError(f"unsupported target phrase: {value!r}") from exc


def _canonical_random_target(value: str | None) -> str:
    key = (value or "").strip().lower()
    try:
        return RANDOM_TARGETS[key]
    except KeyError as exc:  # pragma: no cover - guarded by the regex grammar
        raise ValueError(f"unsupported random target phrase: {value!r}") from exc


def _point_effect(kind: str, match: re.Match[str]) -> dict[str, Any]:
    return {
        "kind": kind,
        "amount": int(match.group("amount")),
        "target": _canonical_target(match.groupdict().get("target")),
    }


def _random_point_effect(kind: str, match: re.Match[str]) -> dict[str, Any]:
    return {
        "kind": kind,
        "amount": int(match.group("amount")),
        "target": _canonical_random_target(match.group("target")),
        "random": True,
    }


def _count_value(match: re.Match[str]) -> int:
    numeric = match.groupdict().get("count")
    word = match.groupdict().get("count_word")
    if numeric:
        return int(numeric)
    return COUNT_WORDS.get(str(word or "").lower(), 0)


def _keyword_effect(target: str, value: str) -> dict[str, Any] | None:
    keywords = _parse_token_keywords(value)
    if not keywords:
        return None
    effect: dict[str, Any] = {"kind": "grant_keywords", "target": target}
    for keyword in sorted(keywords):
        effect[keyword] = True
    return effect


def _filtered_draw_effect(
    count_text: str,
    descriptor_text: str,
    *,
    cost_min: int | None = None,
    cost_max: int | None = None,
) -> dict[str, Any] | None:
    count_text = count_text.lower()
    count = COUNT_WORDS.get(count_text, _integer(count_text))
    if not 1 <= count <= 7:
        return None
    if cost_min is not None and cost_min < 0:
        return None
    if cost_max is not None and cost_max < 0:
        return None
    if cost_min is not None and cost_max is not None and cost_min > cost_max:
        return None
    descriptor = descriptor_text.lower().strip()
    descriptor = re.sub(r"s$", "", descriptor)
    card_types: list[str] = []
    spell_school_ids: list[int] = []
    minion_type_ids: list[int] = []
    required_keywords: list[str] = []
    if descriptor in {"minion", "spell"}:
        card_types = [descriptor.upper()]
    elif descriptor.endswith(" spell"):
        school = descriptor.removesuffix(" spell")
        school_id = SPELL_SCHOOL_IDS.get(school)
        if school_id is None:
            return None
        card_types = ["SPELL"]
        spell_school_ids = [school_id]
    elif descriptor in MINION_TYPE_IDS:
        card_types = ["MINION"]
        minion_type_ids = [MINION_TYPE_IDS[descriptor]]
    elif descriptor in {"deathrattle minion", "taunt minion"}:
        card_types = ["MINION"]
        required_keywords = [descriptor.removesuffix(" minion")]
    elif descriptor == "outcast card":
        required_keywords = ["outcast"]
    else:
        return None
    pool: dict[str, Any] = {
        "source": "owner_deck",
        "collectible": True,
        "card_types": card_types,
        "class_mode": "any",
        "spell_school_ids": spell_school_ids,
        "minion_type_ids": minion_type_ids,
        "required_keywords": required_keywords,
    }
    if cost_min is not None:
        pool["cost_min"] = cost_min
    if cost_max is not None:
        pool["cost_max"] = cost_max
    return {
        "kind": "draw_from_pool",
        "target": "none",
        "count": count,
        "random": True,
        "pool_selection": "uniform_random",
        "pool_destination": "hand",
        "offer_count": 1,
        "with_replacement": False,
        "pool": pool,
    }


def _parse_atomic_clause(
    value: str,
    source_card_id: str,
    card_defs: CardDefIndex | None,
) -> list[dict[str, Any]] | None:
    text = value.strip().rstrip(".").strip()
    if not text:
        return []

    match = RANDOM_DAMAGE_RE.fullmatch(text)
    if match:
        return [_random_point_effect("damage", match)]
    match = DAMAGE_RE.fullmatch(text)
    if match:
        return [_point_effect("damage", match)]
    match = RANDOM_HEAL_RE.fullmatch(text)
    if match:
        return [_random_point_effect("heal", match)]
    match = HEAL_RE.fullmatch(text)
    if match:
        return [_point_effect("heal", match)]
    match = ARMOR_RE.fullmatch(text)
    if match:
        return [{"kind": "armor", "amount": int(match.group("amount")), "target": "none"}]
    match = FREEZE_RE.fullmatch(text)
    if match:
        return [{"kind": "freeze", "target": _canonical_target(match.group("target"))}]
    match = RANDOM_FREEZE_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "freeze",
                "target": _canonical_random_target(match.group("target")),
                "random": True,
            }
        ]
    match = BUFF_RE.fullmatch(text)
    if match:
        target = _canonical_target(match.group("target"))
        effects: list[dict[str, Any]] = []
        attack = int(match.group("attack"))
        health = int(match.group("health"))
        if attack:
            effects.append({"kind": "buff_attack", "amount": attack, "target": target})
        if health:
            effects.append({"kind": "buff_health", "amount": health, "target": target})
        return effects
    match = HERO_ATTACK_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "gain_hero_attack",
                "amount": int(match.group("amount")),
                "target": "none",
            }
        ]
    match = DRAW_RE.fullmatch(text)
    if match:
        count = int(match.group("count") or 1)
        if 1 <= count <= 10:
            return [{"kind": "draw", "target": "none", "count": count}]
        return None
    match = FILTERED_DRAW_RE.fullmatch(text)
    if match:
        effect = _filtered_draw_effect(match.group("count"), match.group("descriptor"))
        return None if effect is None else [effect]
    match = COST_FILTERED_DRAW_RE.fullmatch(text)
    if match:
        cost = int(match.group("cost"))
        cost_min = cost if match.group("bound").lower() == "more" else None
        cost_max = cost if match.group("bound").lower() == "less" else None
        effect = _filtered_draw_effect(
            match.group("count"),
            match.group("descriptor"),
            cost_min=cost_min,
            cost_max=cost_max,
        )
        return None if effect is None else [effect]
    match = COST_SERIES_DRAW_RE.fullmatch(text)
    if match:
        costs = [int(value) for value in re.findall(r"\d+", match.group("costs"))]
        if not 2 <= len(costs) <= 7 or len(set(costs)) != len(costs):
            return None
        if costs != sorted(costs) or any(cost > 30 for cost in costs):
            return None
        effects = [
            _filtered_draw_effect(
                "a",
                match.group("descriptor"),
                cost_min=cost,
                cost_max=cost,
            )
            for cost in costs
        ]
        return None if any(effect is None for effect in effects) else [
            effect for effect in effects if effect is not None
        ]
    match = DRAW_UNTIL_HAND_RE.fullmatch(text)
    if match:
        count = int(match.group("count"))
        return None if not 1 <= count <= 10 else [
            {"kind": "draw_until_hand_count", "target": "none", "count": count}
        ]
    match = DRAW_OPPONENT_RE.fullmatch(text)
    if match:
        count = int(match.group("count") or 1)
        return None if not 1 <= count <= 10 else [
            {"kind": "draw_opponent", "target": "none", "count": count}
        ]
    match = DRAW_EACH_PLAYER_RE.fullmatch(text)
    if match:
        count = int(match.group("count") or 1)
        return None if not 1 <= count <= 10 else [
            {"kind": "draw_both_players", "target": "none", "count": count}
        ]
    match = RANDOM_BUFF_ATTACK_ONLY_RE.fullmatch(text)
    if match:
        return [_random_point_effect("buff_attack", match)]
    match = BUFF_ATTACK_ONLY_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "buff_attack",
                "amount": int(match.group("amount")),
                "target": _canonical_target(match.group("target")),
            }
        ]
    match = RANDOM_BUFF_HEALTH_ONLY_RE.fullmatch(text)
    if match:
        return [_random_point_effect("buff_health", match)]
    match = BUFF_HEALTH_ONLY_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "buff_health",
                "amount": int(match.group("amount")),
                "target": _canonical_target(match.group("target")),
            }
        ]
    match = GAIN_STATS_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "buff_attack",
                "amount": int(match.group("attack")),
                "target": "self",
            },
            {
                "kind": "buff_health",
                "amount": int(match.group("health")),
                "target": "self",
            },
        ]
    match = GAIN_ATTACK_SELF_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "buff_attack",
                "amount": int(match.group("amount")),
                "target": "self",
            }
        ]
    match = GAIN_HEALTH_SELF_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "buff_health",
                "amount": int(match.group("amount")),
                "target": "self",
            }
        ]
    match = BUFF_WEAPON_ATTACK_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "buff_weapon_attack",
                "amount": int(match.group("amount")),
                "target": "none",
            }
        ]
    match = SUMMON_RE.fullmatch(text)
    if match:
        effect = _summon_effect(match, source_card_id, card_defs)
        return None if effect is None else [effect]
    match = NAMED_SUMMON_RE.fullmatch(text)
    if match:
        effect = _named_summon_effect(match, source_card_id, card_defs)
        return None if effect is None else [effect]
    match = DESTROY_BOARD_RE.fullmatch(text)
    if match:
        return [{"kind": "destroy_all_minions_and_locations", "target": "none"}]
    match = DESTROY_RE.fullmatch(text)
    if match:
        return [{"kind": "destroy", "target": _canonical_target(match.group("target"))}]
    match = TRANSFORM_RE.fullmatch(text)
    if match:
        effect = _transform_effect(match, source_card_id, card_defs)
        return None if effect is None else [effect]
    match = SELF_TRANSFORM_RE.fullmatch(text)
    if match:
        effect = _transform_effect(match, source_card_id, card_defs, "self")
        return None if effect is None else [effect]
    match = EQUIP_RE.fullmatch(text)
    if match:
        effect = _equip_effect(match, source_card_id, card_defs)
        return None if effect is None else [effect]
    match = SET_STATS_RE.fullmatch(text)
    if match:
        target = _canonical_target(match.group("target"))
        return [
            {"kind": "set_attack", "amount": int(match.group("attack")), "target": target},
            {"kind": "set_health", "amount": int(match.group("health")), "target": target},
        ]
    match = SET_ATTACK_HEALTH_RE.fullmatch(text)
    if match:
        target = _canonical_target(match.group("target"))
        amount = int(match.group("amount"))
        return [
            {"kind": "set_attack", "amount": amount, "target": target},
            {"kind": "set_health", "amount": amount, "target": target},
        ]
    match = RANDOM_SET_HEALTH_RE.fullmatch(text)
    if match:
        return [_random_point_effect("set_health", match)]
    match = SET_HEALTH_RE.fullmatch(text)
    if match:
        return [
            {
                "kind": "set_health",
                "amount": int(match.group("amount")),
                "target": _canonical_target(match.group("target")),
            }
        ]
    match = GRANT_KEYWORDS_RE.fullmatch(text)
    if match:
        effect = _keyword_effect(
            _canonical_target(match.group("target")), match.group("keywords")
        )
        return None if effect is None else [effect]
    match = GAIN_KEYWORDS_RE.fullmatch(text)
    if match:
        effect = _keyword_effect("self", match.group("keywords"))
        return None if effect is None else [effect]
    match = REFRESH_MANA_RE.fullmatch(text)
    if match:
        count = _count_value(match)
        return None if not 1 <= count <= 10 else [
            {"kind": "refresh_mana", "amount": count, "target": "none"}
        ]
    if REFRESH_ALL_MANA_RE.fullmatch(text):
        return [{"kind": "refresh_mana", "amount": 10, "target": "none"}]
    if GAIN_TEMPORARY_MANA_RE.fullmatch(text):
        return [{"kind": "gain_mana", "amount": 1, "target": "none"}]
    match = GAIN_MANA_RE.fullmatch(text)
    if match:
        count = _count_value(match)
        if not 1 <= count <= 10:
            return None
        if match.group("temporary"):
            kind = "gain_mana"
        elif match.group("empty"):
            kind = "gain_empty_mana_crystals"
        else:
            kind = "gain_mana_crystals"
        return [{"kind": kind, "amount": count, "target": "none"}]
    return None


def _parse_compound_clause(
    value: str,
    source_card_id: str,
    card_defs: CardDefIndex | None,
) -> list[dict[str, Any]] | None:
    text = value.strip().rstrip(".").strip()

    fixed_repeat = FIXED_DAMAGE_REPEAT_RE.fullmatch(text)
    if fixed_repeat:
        damage_match = DAMAGE_RE.fullmatch(fixed_repeat.group("clause"))
        if damage_match is None:  # pragma: no cover - constrained by outer regex
            return None
        repetitions = 2 if fixed_repeat.group("repetitions").lower() == "twice" else 3
        damage = _point_effect("damage", damage_match)
        return [dict(damage) for _ in range(repetitions)]

    # The pronoun is unambiguous because both operations use the same automatic
    # target group.  Player-selected singular targets are also preserved.
    freeze_suffix = re.fullmatch(
        rf"(?P<damage>Deal \d+ damage(?: to (?:{TARGET_PATTERN}))?) and Freeze (?:it|them)",
        text,
        re.IGNORECASE,
    )
    if freeze_suffix:
        damage_match = DAMAGE_RE.fullmatch(freeze_suffix.group("damage"))
        if damage_match is None:  # pragma: no cover - constrained by outer regex
            return None
        damage = _point_effect("damage", damage_match)
        return [damage, {"kind": "freeze", "target": damage["target"]}]

    damage_buff = re.fullmatch(
        rf"(?P<damage>Deal \d+ damage(?: to (?:{TARGET_PATTERN}))?) and give it "
        r"\+(?P<amount>\d+) Attack",
        text,
        re.IGNORECASE,
    )
    if damage_buff:
        damage_match = DAMAGE_RE.fullmatch(damage_buff.group("damage"))
        if damage_match is None:  # pragma: no cover - constrained by outer regex
            return None
        damage = _point_effect("damage", damage_match)
        return [
            damage,
            {
                "kind": "buff_attack",
                "amount": int(damage_buff.group("amount")),
                "target": damage["target"],
            },
        ]

    self_damage = re.fullmatch(
        r"Deal (?P<amount>\d+) damage to a minion and your hero",
        text,
        re.IGNORECASE,
    )
    if self_damage:
        amount = int(self_damage.group("amount"))
        return [
            {"kind": "damage", "amount": amount, "target": "any_minion"},
            {"kind": "damage", "amount": amount, "target": "friendly_hero"},
        ]

    set_then_keyword = re.fullmatch(
        r"Set (?P<target>a minion|an enemy minion|a friendly minion)'s stats to "
        r"(?P<attack>\d+)/(?P<health>\d+) and give it (?P<keywords>[^.]+)",
        text,
        re.IGNORECASE,
    )
    if set_then_keyword:
        target = _canonical_target(set_then_keyword.group("target"))
        keyword = _keyword_effect(target, set_then_keyword.group("keywords"))
        if keyword is None:
            return None
        return [
            {
                "kind": "set_attack",
                "amount": int(set_then_keyword.group("attack")),
                "target": target,
            },
            {
                "kind": "set_health",
                "amount": int(set_then_keyword.group("health")),
                "target": target,
            },
            keyword,
        ]

    stats_then_keyword = re.fullmatch(
        rf"Give (?P<target>{TARGET_PATTERN}) \+(?P<attack>\d+)/\+(?P<health>\d+) "
        r"and (?P<keywords>[^.]+)",
        text,
        re.IGNORECASE,
    )
    if stats_then_keyword:
        target = _canonical_target(stats_then_keyword.group("target"))
        keyword = _keyword_effect(target, stats_then_keyword.group("keywords"))
        if keyword is None:
            return None
        return [
            {
                "kind": "buff_attack",
                "amount": int(stats_then_keyword.group("attack")),
                "target": target,
            },
            {
                "kind": "buff_health",
                "amount": int(stats_then_keyword.group("health")),
                "target": target,
            },
            keyword,
        ]

    keyword_then_stats = re.fullmatch(
        rf"Give (?P<target>{TARGET_PATTERN}) (?P<keywords>[^.]+) and "
        r"\+(?P<attack>\d+)/\+(?P<health>\d+)",
        text,
        re.IGNORECASE,
    )
    if keyword_then_stats:
        target = _canonical_target(keyword_then_stats.group("target"))
        keyword = _keyword_effect(target, keyword_then_stats.group("keywords"))
        if keyword is None:
            return None
        return [
            keyword,
            {
                "kind": "buff_attack",
                "amount": int(keyword_then_stats.group("attack")),
                "target": target,
            },
            {
                "kind": "buff_health",
                "amount": int(keyword_then_stats.group("health")),
                "target": target,
            },
        ]

    heal_then_keyword = re.fullmatch(
        r"Restore (?P<amount>\d+) Health to your hero and give them (?P<keywords>[^.]+)",
        text,
        re.IGNORECASE,
    )
    if heal_then_keyword:
        keyword = _keyword_effect("friendly_hero", heal_then_keyword.group("keywords"))
        if keyword is None:
            return None
        return [
            {
                "kind": "heal",
                "amount": int(heal_then_keyword.group("amount")),
                "target": "friendly_hero",
            },
            keyword,
        ]

    both_heroes_heal = re.fullmatch(
        r"Restore (?P<amount>\d+) Health to each hero",
        text,
        re.IGNORECASE,
    )
    if both_heroes_heal:
        amount = int(both_heroes_heal.group("amount"))
        return [
            {"kind": "heal", "amount": amount, "target": "friendly_hero"},
            {"kind": "heal", "amount": amount, "target": "enemy_hero"},
        ]

    # Split only when every side is independently valid closed grammar. This
    # deliberately rejects shared pronouns, dynamic quantities, and clauses
    # whose second half depends on the first result.
    conjunctions = [item.strip() for item in re.split(r"\s+and\s+", text, flags=re.IGNORECASE)]
    if len(conjunctions) > 1:
        effects: list[dict[str, Any]] = []
        for clause in conjunctions:
            parsed = _parse_atomic_clause(clause, source_card_id, card_defs)
            if not parsed:
                break
            effects.extend(parsed)
        else:
            return effects

    # Two independent fully parsed clauses may be separated by a sentence.  No
    # conjunction splitting is attempted because Hearthstone's "and" often
    # carries shared targets, conditions, or replacement semantics.
    sentences = [item.strip() for item in re.split(r"\.\s+", text) if item.strip()]
    if len(sentences) <= 1:
        return _parse_atomic_clause(text, source_card_id, card_defs)
    effects: list[dict[str, Any]] = []
    shared_target = ""
    for sentence in sentences:
        pronoun_keyword = re.fullmatch(
            r"Give it (?P<keywords>.+)", sentence, re.IGNORECASE
        )
        if pronoun_keyword and shared_target:
            effect = _keyword_effect(shared_target, pronoun_keyword.group("keywords"))
            parsed = None if effect is None else [effect]
        else:
            parsed = _parse_compound_clause(sentence, source_card_id, card_defs)
        if parsed is None:
            return None
        effects.extend(parsed)
        targets = {
            str(effect.get("target", ""))
            for effect in parsed
            if str(effect.get("target", ""))
            not in {"", "none", "self", "friendly_hero", "enemy_hero"}
        }
        if len(targets) == 1:
            shared_target = next(iter(targets))
    return effects or None


EXPLICIT_TRIGGER_SECTION_RE = re.compile(
    r"(?:^|(?<=\.\s))(?P<label>Battlecry:|Deathrattle:|Frenzy:|After you cast a spell,|"
    r"At the start of your turn,|At the end of your turn,)",
    re.IGNORECASE,
)

EXPLICIT_TRIGGER_LABELS = {
    "battlecry:": "resolution",
    "deathrattle:": "deathrattle",
    "frenzy:": "frenzy",
    "after you cast a spell,": "after_spell_cast",
    "at the start of your turn,": "turn_start",
    "at the end of your turn,": "turn_end",
}


def _parse_explicit_trigger_sections(
    value: str,
    card_type: str,
    source_card_id: str,
    card_defs: CardDefIndex | None,
) -> list[dict[str, Any]] | None:
    matches = list(EXPLICIT_TRIGGER_SECTION_RE.finditer(value))
    if len(matches) < 2 or matches[0].start() != 0:
        return None
    effects: list[dict[str, Any]] = []
    for index, match in enumerate(matches):
        label = match.group("label").lower()
        trigger = EXPLICIT_TRIGGER_LABELS[label]
        if trigger in {"resolution", "deathrattle"} and card_type not in {"MINION", "WEAPON"}:
            return None
        if trigger == "frenzy" and card_type != "MINION":
            return None
        if trigger in {"after_spell_cast", "turn_start", "turn_end"} and card_type not in {
            "MINION",
            "WEAPON",
            "LOCATION",
        }:
            return None
        end = matches[index + 1].start() if index + 1 < len(matches) else len(value)
        section = value[match.end() : end].strip().rstrip(".").strip()
        parsed = _parse_compound_clause(section, source_card_id, card_defs)
        if not parsed:
            return None
        for effect in parsed:
            triggered = dict(effect)
            if trigger != "resolution":
                triggered["trigger"] = trigger
            effects.append(triggered)
    return effects or None


def compile_card(
    card: Mapping[str, Any],
    card_defs: CardDefIndex | None = None,
) -> tuple[dict[str, Any] | None, str]:
    card_type = CARD_TYPES.get(int(card.get("card_type_id", 0) or 0))
    if card_type is None:
        return None, "unsupported_card_type"
    normalized = normalize_card_text(card.get("text"))
    if not normalized:
        return None, "empty_text"

    body = LEADING_INTRINSIC_RE.sub("", normalized).strip()
    body = TRAILING_PARSER_REMINDER_RE.sub("", body).strip()
    card_id = str(card.get("card_id", "")).strip()
    if not card_id:
        return None, "missing_card_id"

    trigger = ""
    effect_triggers = ["resolution"]
    multi_event_effects = _parse_explicit_trigger_sections(
        body, card_type, card_id, card_defs
    )
    lower_body = body.lower()
    if multi_event_effects is not None:
        trigger = "multi_event"
    elif lower_body.startswith("battlecry and frenzy:"):
        trigger = "battlecry_and_frenzy"
        effect_triggers = ["resolution", "frenzy"]
        body = body[len("Battlecry and Frenzy:") :].strip()
        if card_type != "MINION":
            return None, "battlecry_card_type_mismatch"
    elif lower_body.startswith("battlecry and deathrattle:"):
        trigger = "battlecry_and_deathrattle"
        effect_triggers = ["resolution", "deathrattle"]
        body = body[len("Battlecry and Deathrattle:") :].strip()
        if card_type not in {"MINION", "WEAPON"}:
            return None, "battlecry_card_type_mismatch"
    elif lower_body.startswith("battlecry:"):
        trigger = "battlecry"
        body = body[len("Battlecry:") :].strip()
        if card_type not in {"MINION", "WEAPON"}:
            return None, "battlecry_card_type_mismatch"
    elif lower_body.startswith("deathrattle:"):
        trigger = "deathrattle"
        effect_triggers = ["deathrattle"]
        body = body[len("Deathrattle:") :].strip()
        if card_type not in {"MINION", "WEAPON"}:
            return None, "deathrattle_card_type_mismatch"
    elif lower_body.startswith("after you cast a spell,"):
        trigger = "after_spell_cast"
        effect_triggers = ["after_spell_cast"]
        body = body[len("After you cast a spell,") :].strip()
        if card_type != "MINION":
            return None, "board_trigger_card_type_mismatch"
    elif lower_body.startswith("spellburst:"):
        trigger = "spellburst"
        effect_triggers = ["spellburst"]
        body = body[len("Spellburst:") :].strip()
        if card_type != "MINION":
            return None, "board_trigger_card_type_mismatch"
    elif lower_body.startswith("frenzy:"):
        trigger = "frenzy"
        effect_triggers = ["frenzy"]
        body = body[len("Frenzy:") :].strip()
        if card_type != "MINION":
            return None, "board_trigger_card_type_mismatch"
    elif lower_body.startswith("after your hero attacks,"):
        trigger = "after_hero_attack"
        effect_triggers = ["after_hero_attack"]
        body = body[len("After your hero attacks,") :].strip()
        if card_type not in {"MINION", "WEAPON"}:
            return None, "board_trigger_card_type_mismatch"
    elif lower_body.startswith("after you use your hero power,"):
        trigger = "after_hero_power"
        effect_triggers = ["after_hero_power"]
        body = body[len("After you use your Hero Power,") :].strip()
        if card_type != "MINION":
            return None, "board_trigger_card_type_mismatch"
    elif lower_body.startswith("at the end of your turn,"):
        trigger = "turn_end"
        effect_triggers = ["turn_end"]
        body = body[len("At the end of your turn,") :].strip()
        if card_type not in {"MINION", "WEAPON", "LOCATION"}:
            return None, "board_trigger_card_type_mismatch"
    elif lower_body.startswith("at the start of your turn,"):
        trigger = "turn_start"
        effect_triggers = ["turn_start"]
        body = body[len("At the start of your turn,") :].strip()
        if card_type not in {"MINION", "WEAPON", "LOCATION"}:
            return None, "board_trigger_card_type_mismatch"
    elif card_type in {"SPELL", "HERO_POWER", "LOCATION"}:
        trigger = "location_activation" if card_type == "LOCATION" else "play_resolution"
    else:
        return None, "unsupported_trigger"

    if any(
        marker in body.lower()
        for marker in (
            " if ",
            " whenever ",
            " after ",
            " before ",
            " for the rest of",
            "discover",
            "instead",
            "overload",
            "choose one",
            "combo:",
        )
    ):
        return None, "outside_closed_grammar"

    effects = multi_event_effects or _parse_compound_clause(body, card_id, card_defs)
    if not effects:
        return None, "no_full_template_match"
    if any(effect.get("random") for effect in effects) and (
        card_type in {"HERO_POWER", "LOCATION"}
        or any(effect_trigger != "resolution" for effect_trigger in effect_triggers)
        or multi_event_effects is not None
    ):
        return None, "chance_trigger_not_executable"
    if multi_event_effects is not None:
        triggered_effects = effects
    else:
        triggered_effects: list[dict[str, Any]] = []
        for effect_trigger in effect_triggers:
            for effect in effects:
                triggered = dict(effect)
                if effect_trigger != "resolution":
                    triggered["trigger"] = effect_trigger
                triggered_effects.append(triggered)

    safe_id = re.sub(r"[^a-z0-9]+", "-", card_id.lower()).strip("-")
    return (
        {
            "rule_id": f"text-template-{safe_id}-v1",
            "card_ids": [card_id],
            "card_type": card_type,
            "trigger": trigger,
            "accepted_texts": [
                {"normalized": normalized, "sha256": normalized_text_sha256(normalized)}
            ],
            "effects": triggered_effects,
        },
        "compiled_generic_template",
    )


def _load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _unique_cards(pool_paths: Sequence[Path]) -> tuple[list[dict[str, Any]], dict[str, list[str]]]:
    cards: dict[str, dict[str, Any]] = {}
    formats: dict[str, set[str]] = {}
    for path in pool_paths:
        root = _load_json(path)
        format_name = str(root.get("format", path.stem)).lower()
        for raw in root.get("cards", []):
            card_id = str(raw.get("card_id", "")).strip()
            if not card_id:
                raise ValueError(f"{path.name} contains a card without card_id")
            previous = cards.get(card_id)
            if previous is not None and previous != raw:
                raise ValueError(f"official pools disagree for card_id {card_id}")
            cards[card_id] = dict(raw)
            formats.setdefault(card_id, set()).add(format_name)
    return (
        [cards[card_id] for card_id in sorted(cards)],
        {card_id: sorted(values) for card_id, values in formats.items()},
    )


def _exact_card_ids(path: Path) -> set[str]:
    root = _load_json(path)
    return {
        str(card_id)
        for rule in root.get("rules", [])
        for card_id in rule.get("card_ids", [])
    }


def compile_bundle(
    pool_root: Path,
    exact_rules_path: Path,
    card_defs_path: Path,
) -> tuple[dict[str, Any], dict[str, Any]]:
    manifest_path = pool_root / "manifest.json"
    standard_path = pool_root / "standard.json"
    arena_path = pool_root / "arena.json"
    manifest = _load_json(manifest_path)
    expected_card_defs_sha256 = str(
        manifest.get("card_defs", {}).get("sha256", "")
    ).upper()
    actual_card_defs_sha256 = file_sha256(card_defs_path)
    if not expected_card_defs_sha256 or actual_card_defs_sha256 != expected_card_defs_sha256:
        raise ValueError(
            "CardDefs does not match the official pool manifest: "
            f"expected {expected_card_defs_sha256 or '<missing>'}, "
            f"got {actual_card_defs_sha256}"
        )
    card_defs = load_card_defs(card_defs_path)
    cards, formats = _unique_cards((standard_path, arena_path))
    exact_ids = _exact_card_ids(exact_rules_path)

    rules: list[dict[str, Any]] = []
    audit_rows: list[dict[str, Any]] = []
    reasons: Counter[str] = Counter()
    semantic_operations: Counter[str] = Counter()
    semantic_triggers: Counter[str] = Counter()
    semantic_families: Counter[str] = Counter()
    semantic_readiness: Counter[str] = Counter()
    for card in cards:
        card_id = str(card["card_id"])
        if card_id in exact_ids:
            rule = None
            reason = "already_exact"
        else:
            rule, reason = compile_card(card, card_defs)
        reasons[reason] += 1
        if rule is not None:
            rules.append(rule)
        definition = card_defs.by_card_id.get(card_id)
        semantic = classify_semantic_card(
            {
                "card_id": card_id,
                "dbf_id": int(card.get("dbf_id", 0) or 0),
                "name": str(card.get("name", "")),
                "card_type": CARD_TYPES.get(int(card.get("card_type_id", 0) or 0), "OTHER"),
                "card_class": str(card.get("class_id", "")),
                "cost": int(card.get("mana_cost", 0) or 0),
                "official_text": str(card.get("text", "")),
                "runtime_text": "",
                "mechanics": [] if definition is None else sorted(definition.keywords),
                "existing_rule_coverage": reason == "already_exact",
                "existing_rules": [],
            }
        )
        readiness = semantic["modeling"]["execution_readiness"]
        if reason == "already_exact":
            readiness = "existing_exact_rule"
        elif rule is not None:
            readiness = "compiled_generic_rule"
        elif semantic["semantic_inventory"]["operations"] == [
            "intrinsic_or_continuous_keyword"
        ]:
            readiness = "intrinsic_keyword_only"
        semantic["modeling"]["execution_readiness"] = readiness
        semantic_operations.update(semantic["semantic_inventory"]["operations"])
        semantic_triggers.update(semantic["semantic_inventory"]["triggers"])
        semantic_families.update(semantic["modeling"]["families"])
        semantic_readiness.update([readiness])
        audit_rows.append(
            {
                "card_id": card_id,
                "dbf_id": int(card.get("dbf_id", 0) or 0),
                "name": str(card.get("name", "")),
                "formats": formats[card_id],
                "card_type": CARD_TYPES.get(int(card.get("card_type_id", 0) or 0), "OTHER"),
                "normalized_text": normalize_card_text(card.get("text")),
                "text_sha256": normalized_text_sha256(str(card.get("text", ""))),
                "compilation_status": reason,
                "compiled_rule_id": "" if rule is None else rule["rule_id"],
                "semantic_inventory": semantic["semantic_inventory"],
                "modeling": semantic["modeling"],
            }
        )

    source = {
        "official_pool_run_id": str(manifest.get("run_id", "")),
        "card_defs_build": str(manifest.get("card_defs", {}).get("build", "")),
        "card_defs_sha256": actual_card_defs_sha256,
        "official_manifest_sha256": file_sha256(manifest_path),
        "standard_pool_sha256": file_sha256(standard_path),
        "arena_pool_sha256": file_sha256(arena_path),
        "rules_generated_from_free_text": True,
        "exact_claim_allowed": False,
    }
    bundle = {
        "schema_version": SCHEMA_VERSION,
        "ruleset_id": RULESET_ID,
        "status": "complete",
        "matching_contract": MATCHING_CONTRACT,
        "runtime_effect_coverage": "generic",
        "source": source,
        "counts": {
            "unique_official_cards": len(cards),
            "compiled_generic_rules": len(rules),
            "already_exact_cards": reasons["already_exact"],
            "uncompiled_cards": len(cards) - len(rules) - reasons["already_exact"],
        },
        "rules": sorted(rules, key=lambda item: item["rule_id"]),
    }
    report = {
        "schema_version": 1,
        "artifact_kind": "official-card-text-compilation-audit-v1",
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "scope": {
            "formats": ["standard", "arena"],
            "collectible_pool_union": True,
            "all_source_cards_retained": True,
            "runtime_output_is_generic_only": True,
            "user_gameplay_required": False,
        },
        "source": source,
        "counts": bundle["counts"],
        "reason_counts": dict(sorted(reasons.items())),
        "semantic_coverage": {
            "all_cards_indexed": len(audit_rows) == len(cards),
            "indexed_cards": len(audit_rows),
            "classifier_purpose": "offline effect-family inventory; never executable by itself",
            "execution_readiness": dict(sorted(semantic_readiness.items())),
            "effect_families": dict(
                sorted(semantic_operations.items(), key=lambda item: (-item[1], item[0]))
            ),
            "trigger_families": dict(
                sorted(semantic_triggers.items(), key=lambda item: (-item[1], item[0]))
            ),
            "modeling_families": dict(
                sorted(semantic_families.items(), key=lambda item: (-item[1], item[0]))
            ),
        },
        "cards": audit_rows,
    }
    return bundle, report


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pool-root", type=Path, default=DEFAULT_POOL_ROOT)
    parser.add_argument("--exact-rules", type=Path, default=DEFAULT_EXACT_RULES)
    parser.add_argument("--card-defs", type=Path, default=DEFAULT_CARD_DEFS)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--report", type=Path, default=DEFAULT_REPORT)
    parser.add_argument("--check", action="store_true", help="verify outputs are current")
    return parser.parse_args()


def _canonical_json(value: Mapping[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2) + "\n"


def _write_or_check(path: Path, content: str, check: bool) -> None:
    if check:
        if not path.is_file() or path.read_text(encoding="utf-8-sig") != content:
            raise SystemExit(f"generated artifact is stale: {path}")
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")


def main() -> int:
    args = parse_args()
    bundle, report = compile_bundle(args.pool_root, args.exact_rules, args.card_defs)
    _write_or_check(args.output, _canonical_json(bundle), args.check)
    # The report timestamp is intentionally omitted during --check because an
    # audit report records when the scan ran, while the embedded bundle is fully
    # reproducible.  Normal generation writes both outputs.
    if not args.check:
        _write_or_check(args.report, _canonical_json(report), False)
    print(
        "Compiled "
        f"{bundle['counts']['compiled_generic_rules']} generic rules from "
        f"{bundle['counts']['unique_official_cards']} Standard/Arena cards; "
        f"{bundle['counts']['already_exact_cards']} already have exact rules."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
