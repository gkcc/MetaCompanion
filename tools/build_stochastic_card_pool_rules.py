from __future__ import annotations

import argparse
import copy
import hashlib
import html
import json
import os
import re
from collections import Counter
from pathlib import Path
from typing import Any, Iterable, Mapping


RULESET_ID = "card-generation-pools-v1"

CARD_TYPE_IDS = {
    "card": (),
    "cards": (),
    "minion": ("MINION",),
    "minions": ("MINION",),
    "spell": ("SPELL",),
    "spells": ("SPELL",),
    "weapon": ("WEAPON",),
    "weapons": ("WEAPON",),
    "location": ("LOCATION",),
    "locations": ("LOCATION",),
    "hero power": ("HERO_POWER",),
    "hero powers": ("HERO_POWER",),
}

CLASS_IDS = {
    "death knight": 1,
    "druid": 2,
    "hunter": 3,
    "mage": 4,
    "paladin": 5,
    "priest": 6,
    "rogue": 7,
    "shaman": 8,
    "warlock": 9,
    "warrior": 10,
    "demon hunter": 14,
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

# Blizzard Card Library minionTypeId values. Names are retained beside IDs in
# the audit artifact so changes can be reviewed instead of silently remapped.
MINION_TYPE_IDS = {
    "murloc": 14,
    "demon": 15,
    "mech": 17,
    "elemental": 18,
    "beast": 20,
    "totem": 21,
    "pirate": 23,
    "dragon": 24,
    "all": 26,
    "quilboar": 43,
    "undead": 77,
    "naga": 92,
}

RARITY_IDS = {
    "common": 1,
    "free": 2,
    "rare": 3,
    "epic": 4,
    "legendary": 5,
}

POOL_KEYWORDS = (
    "Battlecry",
    "Charge",
    "Choose One",
    "Combo",
    "Deathrattle",
    "Divine Shield",
    "Dormant",
    "Elusive",
    "Lifesteal",
    "Outcast",
    "Poisonous",
    "Quickdraw",
    "Reborn",
    "Rewind",
    "Rush",
    "Secret",
    "Spell Damage",
    "Stealth",
    "Taunt",
    "Temporary",
    "Windfury",
)

ENTOURAGE_PHRASES = (
    "Animal Companion",
    "Beast Companion",
    "Bonus Effect",
    "Bulb",
    "Colossal minion",
    "Contraband",
    "Dark Gift",
    "Demon Companion",
    "Dream card",
    "Dreadseed",
    "Elemental Companion",
    "Leyline",
    "Paladin Aura",
    "Poison",
    "Shatter card",
    "Wild God",
)

NUMBER_WORDS = {
    "a": 1,
    "an": 1,
    "one": 1,
    "two": 2,
    "three": 3,
    "four": 4,
    "five": 5,
}

HTML_TAG = re.compile(r"<[^>]+>")
SPACE = re.compile(r"\s+")
RANDOM_OR_DISCOVER = re.compile(r"\b(?:random(?:ly)?|Discover)\b", re.IGNORECASE)


def normalize_text(value: Any) -> str:
    decoded = html.unescape(str(value or "")).replace("[x]", " ").replace("\xa0", " ")
    without_tags = HTML_TAG.sub(" ", decoded)
    without_variables = re.sub(r"[$#](?=\d)", "", without_tags)
    return SPACE.sub(" ", without_variables).strip()


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def load_json(path: Path) -> Mapping[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, Mapping):
        raise ValueError(f"JSON root must be an object: {path}")
    return value


def load_cards(pool_root: Path) -> tuple[Mapping[str, Any], list[dict[str, Any]]]:
    manifest = load_json(pool_root / "manifest.json")
    records = {str(row.get("format") or "").lower(): row for row in manifest.get("pools", [])}
    cards_by_id: dict[str, dict[str, Any]] = {}
    for format_name in ("standard", "arena"):
        page = load_json(pool_root / f"{format_name}.json")
        for raw in page.get("cards", []):
            card_id = str(raw.get("card_id") or "").strip()
            if not card_id:
                continue
            row = cards_by_id.setdefault(card_id, dict(raw))
            row.setdefault("formats", [])
            if format_name not in row["formats"]:
                row["formats"].append(format_name)
    for row in cards_by_id.values():
        row["formats"] = sorted(row["formats"])
    manifest = dict(manifest)
    manifest["pool_sha256"] = {
        name: str(records.get(name, {}).get("sha256") or "")
        for name in ("standard", "arena")
    }
    return manifest, sorted(cards_by_id.values(), key=lambda row: str(row["card_id"]))


def trigger_for(card_type_id: Any, text: str) -> str:
    lowered = text.lower()
    if "deathrattle:" in lowered:
        return "deathrattle"
    if "at the end of" in lowered:
        return "end_of_turn"
    if "at the start of" in lowered:
        return "start_of_turn"
    if re.search(r"\b(?:after|whenever|when)\b", lowered):
        return "event_trigger"
    for keyword, trigger in (
        ("frenzy", "frenzy"),
        ("spellburst", "spellburst"),
        ("overheal", "overheal"),
        ("honorable kill", "honorable_kill"),
        ("quickdraw", "quickdraw"),
        ("finale", "finale"),
        ("combo", "combo"),
        ("outcast", "outcast"),
        ("corrupt", "corrupt"),
    ):
        if re.search(rf"\b{re.escape(keyword)}\s*:", lowered):
            return trigger
    if re.search(r"\bbattlecry\s*:", lowered):
        return "battlecry"
    if re.search(r"\b(?:quest|sidequest)\s*:", lowered):
        return "quest_progress"
    if int(card_type_id or 0) == 39:
        return "location_activation"
    return "play_resolution"


def count_from_prefix(prefix: str, *, default: int = 1) -> int:
    tokens = re.findall(r"[A-Za-z]+|\d+", prefix.lower())
    for token in reversed(tokens[-4:]):
        if token.isdigit():
            return max(1, int(token))
        if token in NUMBER_WORDS:
            return NUMBER_WORDS[token]
    return default


def sentence_around(text: str, start: int) -> str:
    left = max(text.rfind(".", 0, start), text.rfind("!", 0, start), text.rfind("?", 0, start))
    ends = [position for position in (text.find(".", start), text.find("!", start), text.find("?", start)) if position >= 0]
    right = min(ends) + 1 if ends else len(text)
    return text[left + 1 : right].strip()


def source_for(fragment: str) -> str:
    lowered = fragment.lower()
    if re.search(r"from your hand\s+and\s+(?:your )?deck", lowered):
        return "owner_hand_and_deck"
    if re.search(r"from your deck\s+and\s+(?:your )?hand", lowered):
        return "owner_hand_and_deck"
    if "opponent's hand" in lowered:
        return "opponent_hand"
    if "opponent's deck" in lowered:
        return "opponent_deck"
    if "from your hand" in lowered and "deck" not in lowered:
        return "owner_hand"
    if "from your deck" in lowered or "in your deck" in lowered:
        return "owner_deck"
    if "died this game" in lowered or "graveyard" in lowered:
        return "graveyard"
    if "from the past" in lowered or "any " in lowered and " from the past" in lowered:
        return "historical"
    if any(phrase.lower() in lowered for phrase in ENTOURAGE_PHRASES):
        return "entourage"
    return "current_format"


def cost_constraint(fragment: str) -> tuple[dict[str, int], list[str]]:
    values: dict[str, int] = {}
    dynamic: list[str] = []
    exact = re.search(r"\b(\d+)-Cost\b", fragment, re.IGNORECASE)
    if exact:
        values = {"min": int(exact.group(1)), "max": int(exact.group(1))}
    lower = re.search(r"costs? \(?([0-9]+)\)? or less", fragment, re.IGNORECASE)
    if lower:
        values["max"] = int(lower.group(1))
    upper = re.search(r"costs? \(?([0-9]+)\)? or more", fragment, re.IGNORECASE)
    if upper:
        values["min"] = int(upper.group(1))
    for phrase, label in (
        ("same Cost", "same_cost"),
        ("that Cost", "resolved_resource_cost"),
        ("remaining Mana", "remaining_mana"),
        ("hand size", "hand_size"),
        ("weapon's Attack", "weapon_attack"),
        ("this minion's Attack", "source_attack"),
        ("those Costs", "prior_random_costs"),
    ):
        if phrase.lower() in fragment.lower():
            dynamic.append(label)
    return values, dynamic


def candidate_card_types(fragment: str, minion_types: Iterable[int]) -> list[str]:
    """Infer the candidate's noun, not source-card keywords such as Spell Damage."""

    lowered = fragment.lower()
    if re.search(r"\bminions?\b", lowered) or any(True for _ in minion_types):
        return ["MINION"]
    if re.search(r"\bspells?\b", lowered):
        return ["SPELL"]
    if re.search(r"\bweapons?\b", lowered):
        return ["WEAPON"]
    if re.search(r"\blocations?\b", lowered):
        return ["LOCATION"]
    if re.search(r"\bhero powers?\b", lowered):
        return ["HERO_POWER"]
    return []


def distinct_constraint_blockers(fragment: str, pool: Mapping[str, Any]) -> list[str]:
    """Detect pools that need a union/product model rather than one AND-query."""

    lowered = fragment.lower()
    blockers: list[str] = []
    costs = {int(value) for value in re.findall(r"\b(\d+)\s*-?\s*Cost\b", fragment, re.IGNORECASE)}
    if len(costs) > 1 or re.search(
        r"^\s*\d+(?:\s*,\s*\d+)*\s*,?\s*and\s*\d+\s*-\s*Cost\b",
        fragment,
        re.IGNORECASE,
    ):
        blockers.append("multiple_distinct_costs")
    rarity_count = len(pool.get("rarity_ids") or [])
    minion_type_count = len(pool.get("minion_type_ids") or [])
    if " and " in lowered and rarity_count > 1:
        blockers.append("multiple_distinct_rarities")
    if " and " in lowered and minion_type_count > 1:
        blockers.append("multiple_distinct_minion_types")
    if " or " in lowered:
        dimensions = sum(
            bool(pool.get(field))
            for field in ("card_types", "minion_type_ids", "rarity_ids", "required_keywords")
        )
        if dimensions > 1:
            blockers.append("union_pool")
    return blockers


def pool_constraints(fragment: str, selection: str) -> dict[str, Any]:
    lowered = fragment.lower()
    spell_schools = sorted(
        value for name, value in SPELL_SCHOOL_IDS.items() if re.search(rf"\b{name}\b", lowered)
    )
    minion_types = sorted(
        value for name, value in MINION_TYPE_IDS.items() if re.search(rf"\b{name}s?\b", lowered)
    )
    minion_type_names = sorted(
        name.upper() for name in MINION_TYPE_IDS if re.search(rf"\b{name}s?\b", lowered)
    )
    card_types = candidate_card_types(fragment, minion_types)
    rarities = sorted(
        value for name, value in RARITY_IDS.items() if re.search(rf"\b{name}\b", lowered)
    )
    required_keywords = sorted(
        keyword.lower().replace(" ", "_")
        for keyword in POOL_KEYWORDS
        if re.search(rf"\b{re.escape(keyword)}\b", fragment, re.IGNORECASE)
    )
    # The operation keyword itself is not a candidate-card requirement.
    required_keywords = [value for value in required_keywords if value != "rewind" or "Rewind card" in fragment]
    cost, dynamic = cost_constraint(fragment)

    explicit_classes = sorted(
        class_id
        for name, class_id in CLASS_IDS.items()
        if re.search(rf"\b{name}\b", lowered)
    )
    if "any class" in lowered:
        class_mode = "any"
        explicit_classes = []
    elif "another class" in lowered or "other class" in lowered:
        class_mode = "another_class"
        explicit_classes = []
    elif "your class" in lowered:
        class_mode = "controller"
        explicit_classes = []
    elif explicit_classes:
        class_mode = "specific"
    elif selection == "discover":
        class_mode = "controller_or_neutral"
    else:
        class_mode = "any"

    return {
        "source": source_for(fragment),
        "collectible": True,
        "cost": cost,
        "card_types": card_types,
        "class_mode": class_mode,
        "class_ids": explicit_classes,
        "spell_school_ids": spell_schools,
        "minion_type_ids": minion_types,
        "minion_type_names": minion_type_names,
        "card_set_ids": [],
        "rarity_ids": rarities,
        "keyword_ids": [],
        "required_keywords": required_keywords,
        "exclude_self": "another card" in lowered,
        "exclude_card_ids": [],
        "dynamic_constraints": sorted(set(dynamic)),
        "named_pool_constraints": sorted(
            phrase for phrase in ENTOURAGE_PHRASES if phrase.lower() in lowered
        ),
    }


def detected_operations(text: str) -> list[dict[str, Any]]:
    operations: list[dict[str, Any]] = []
    occupied: list[tuple[int, int]] = []

    for match in re.finditer(r"\bDiscover\b", text, re.IGNORECASE):
        fragment = sentence_around(text, match.start())
        prefix = fragment[: fragment.lower().find("discover")]
        candidate_fragment = fragment[fragment.lower().find("discover") + len("discover") :].strip()
        pool = pool_constraints(candidate_fragment, "discover")
        count = count_from_prefix(prefix)
        operations.append(
            {
                "kind": "discover_to_hand",
                "selection": "discover",
                "count": count,
                "offer_count": 3,
                "with_replacement": False,
                "fragment": fragment,
                "pool": pool,
                "pool_blockers": distinct_constraint_blockers(candidate_fragment, pool),
                "created_card_cost_delta": created_cost_delta(text),
            }
        )
        occupied.append(match.span())

    random_pattern = re.compile(
        r"\b(?P<verb>Get|Add|Summon|Cast|Shuffle|Fill your hand with|Put)\b[^.!?]{0,90}?\brandom\b",
        re.IGNORECASE,
    )
    for match in random_pattern.finditer(text):
        random_position = text.lower().find("random", match.start(), match.end())
        if any(start <= random_position < end for start, end in occupied):
            continue
        verb = match.group("verb").lower()
        fragment = sentence_around(text, match.start())
        before_random = fragment[: fragment.lower().find("random")]
        candidate_fragment = fragment[fragment.lower().find("random") + len("random") :].strip()
        pool = pool_constraints(candidate_fragment, "uniform_random")
        count = count_from_prefix(before_random)
        if verb in {"get", "add", "fill your hand with"}:
            kind = "generate_to_hand"
        elif verb == "summon":
            kind = "summon_from_pool"
        elif verb == "cast":
            kind = "cast_from_pool"
        elif verb in {"shuffle", "put"}:
            kind = "shuffle_from_pool"
        else:
            kind = "unresolved_random_operation"
        operations.append(
            {
                "kind": kind,
                "selection": "uniform_random",
                "count": count,
                "offer_count": 1,
                "with_replacement": True,
                "fragment": fragment,
                "pool": pool,
                "pool_blockers": distinct_constraint_blockers(candidate_fragment, pool),
                "created_card_cost_delta": created_cost_delta(text),
            }
        )

    if not operations and RANDOM_OR_DISCOVER.search(text):
        operations.append(
            {
                "kind": "random_target_or_modifier",
                "selection": "uniform_random",
                "count": 1,
                "offer_count": 1,
                "with_replacement": True,
                "fragment": text,
                "pool": None,
                "created_card_cost_delta": 0,
            }
        )
    return operations


def created_cost_delta(text: str) -> int:
    patterns = (
        r"(?:It|They) costs? \(([0-9]+)\) less",
        r"Reduce (?:its|their) Costs? by \(([0-9]+)\)",
        r"Reduce (?:its|their) Cost by \(([0-9]+)\)",
    )
    for pattern in patterns:
        match = re.search(pattern, text, re.IGNORECASE)
        if match:
            return -int(match.group(1))
    return 0


def conditional_blockers(text: str, trigger: str) -> list[str]:
    lowered = text.lower()
    blockers: list[str] = []
    if trigger not in {"play_resolution", "battlecry"}:
        blockers.append(f"trigger_engine:{trigger}")
    for phrase, reason in (
        (" if ", "conditional_if"),
        ("choose one", "choose_branch"),
        ("choose a ", "choose_target_or_branch"),
        ("combo:", "combo_condition"),
        ("prepare", "prepare_condition"),
        ("kindred", "kindred_condition"),
        ("rewind", "rewind_state"),
        ("this game", "game_history"),
        ("this turn", "turn_history"),
        ("for each", "repeat_dynamic"),
        ("up to", "dynamic_count"),
        ("instead", "replacement_condition"),
        ("your next", "persistent_next_card_modifier"),
        ("future", "persistent_future_modifier"),
        ("while building your deck", "deckbuilding_pool"),
        ("playable", "runtime_playability_pool"),
        ("crew", "entourage_destination"),
        ("reward:", "quest_reward"),
        ("overload:", "overload_unmodeled"),
        ("scramble", "stat_scramble"),
        ("spell damage +", "persistent_source_spell_damage"),
        ("set its stats", "created_card_stat_override"),
        ("set their stats", "created_card_stat_override"),
        ("same cost", "dynamic_cost"),
        ("that cost", "dynamic_cost"),
        ("from the past", "historical_pool"),
        ("from your deck", "owner_deck_pool"),
        ("opponent's", "opponent_hidden_zone"),
    ):
        if phrase in lowered:
            blockers.append(reason)
    return sorted(set(blockers))


def strip_simple_prefix(text: str) -> str:
    if "Battlecry:" in text:
        return text.split("Battlecry:", 1)[1].strip()
    return text


def is_simple_complete_effect(text: str, operation: Mapping[str, Any], trigger: str) -> bool:
    if trigger not in {"play_resolution", "battlecry"}:
        return False
    if operation["kind"] not in {
        "generate_to_hand",
        "discover_to_hand",
        "summon_from_pool",
    }:
        return False
    pool = operation.get("pool") or {}
    if pool.get("source") != "current_format":
        return False
    if pool.get("dynamic_constraints") or pool.get("named_pool_constraints"):
        return False
    if operation.get("pool_blockers"):
        return False
    body = strip_simple_prefix(text)
    forbidden = re.compile(
        r"\b(?:Deal|Draw|Destroy|Restore|Gain|Spend|Freeze|Attack|Copy|Give|Double|"
        r"Shuffle|Put|Swap|Transform|Refresh|Equip|Discard|Return|Trigger|Reduce|"
        r"Cast|Set|Improve|Lasts?|Upgrade)\b",
        re.IGNORECASE,
    )
    # A generated-card cost adjustment is modeled on the created card.
    body_without_cost = re.sub(
        r"(?:It|They) costs? \([0-9]+\) less\.?|Reduce (?:its|their) Costs? by \([0-9]+\)\.?",
        "",
        body,
        flags=re.IGNORECASE,
    )
    return forbidden.search(body_without_cost) is None


def rust_pool_query(pool: Mapping[str, Any]) -> dict[str, Any]:
    cost = pool.get("cost") or {}
    value: dict[str, Any] = {
        "source": pool["source"],
        "collectible": bool(pool.get("collectible", True)),
        "card_types": list(pool.get("card_types") or []),
        "class_mode": pool.get("class_mode", "any"),
        "class_ids": list(pool.get("class_ids") or []),
        "spell_school_ids": list(pool.get("spell_school_ids") or []),
        "minion_type_ids": list(pool.get("minion_type_ids") or []),
        "card_set_ids": list(pool.get("card_set_ids") or []),
        "rarity_ids": list(pool.get("rarity_ids") or []),
        "keyword_ids": list(pool.get("keyword_ids") or []),
        "required_keywords": list(pool.get("required_keywords") or []),
        "exclude_self": bool(pool.get("exclude_self", False)),
        "exclude_card_ids": list(pool.get("exclude_card_ids") or []),
    }
    if "min" in cost:
        value["cost_min"] = int(cost["min"])
    if "max" in cost:
        value["cost_max"] = int(cost["max"])
    return value


def runtime_effect(operation: Mapping[str, Any]) -> dict[str, Any]:
    destination = {
        "generate_to_hand": "hand",
        "discover_to_hand": "hand",
        "summon_from_pool": "battlefield",
    }[str(operation["kind"])]
    kind = {
        "generate_to_hand": "generate_from_pool",
        "discover_to_hand": "discover_from_pool",
        "summon_from_pool": "summon_from_pool",
    }[str(operation["kind"])]
    return {
        "kind": kind,
        "target": "none",
        "count": int(operation["count"]),
        "random": True,
        "pool_selection": operation["selection"],
        "pool_destination": destination,
        "offer_count": int(operation["offer_count"]),
        "with_replacement": bool(operation["with_replacement"]),
        "created_card_cost_delta": int(operation.get("created_card_cost_delta") or 0),
        "pool": rust_pool_query(operation["pool"]),
    }


def explicit_cost_values(fragment: str) -> list[int]:
    grouped = re.search(
        r"\b(?P<values>\d+(?:\s*,\s*\d+)*\s*,?\s*and\s*\d+)\s*-\s*Cost\b",
        fragment,
        re.IGNORECASE,
    )
    if grouped:
        return [int(value) for value in re.findall(r"\d+", grouped.group("values"))]
    return list(
        dict.fromkeys(
            int(value)
            for value in re.findall(r"\b(\d+)\s*-\s*Cost\b", fragment, re.IGNORECASE)
        )
    )


def ordered_named_values(fragment: str, mapping: Mapping[str, int]) -> list[int]:
    matches: list[tuple[int, int]] = []
    for name, value in mapping.items():
        match = re.search(rf"\b{re.escape(name)}s?\b", fragment, re.IGNORECASE)
        if match:
            matches.append((match.start(), value))
    return [value for _, value in sorted(matches)]


def product_runtime_effects(
    text: str,
    operation: Mapping[str, Any],
    trigger: str,
) -> list[dict[str, Any]]:
    """Compile one-card-per-constraint products into sequential pool effects."""

    pool_blockers = list(operation.get("pool_blockers") or [])
    if len(pool_blockers) != 1 or pool_blockers[0] not in {
        "multiple_distinct_costs",
        "multiple_distinct_rarities",
        "multiple_distinct_minion_types",
    }:
        return []
    simple_operation = dict(operation)
    simple_operation["pool_blockers"] = []
    if not is_simple_complete_effect(text, simple_operation, trigger):
        return []
    pool = operation.get("pool") or {}
    if pool_blockers[0] == "multiple_distinct_costs":
        dimension = ("cost", explicit_cost_values(str(operation.get("fragment") or text)))
    elif pool_blockers[0] == "multiple_distinct_rarities":
        dimension = (
            "rarity_ids",
            ordered_named_values(str(operation.get("fragment") or text), RARITY_IDS),
        )
    else:
        dimension = (
            "minion_type_ids",
            ordered_named_values(str(operation.get("fragment") or text), MINION_TYPE_IDS),
        )
    if len(dimension[1]) < 2:
        return []
    effects: list[dict[str, Any]] = []
    for value in dimension[1]:
        split_operation = dict(operation)
        split_pool = copy.deepcopy(pool)
        if dimension[0] == "cost":
            split_pool["cost"] = {"min": int(value), "max": int(value)}
        else:
            split_pool[dimension[0]] = [int(value)]
        split_operation["pool"] = split_pool
        split_operation["count"] = 1
        split_operation["pool_blockers"] = []
        effects.append(runtime_effect(split_operation))
    return effects


def explicit_runtime_override(
    card_id: str,
    text: str,
    operations: list[dict[str, Any]],
) -> list[dict[str, Any]] | None:
    discover_spell = {
        "source": "current_format",
        "collectible": True,
        "cost": {},
        "card_types": ["SPELL"],
        "class_mode": "controller_or_neutral",
        "class_ids": [],
        "spell_school_ids": [],
        "minion_type_ids": [],
        "card_set_ids": [],
        "rarity_ids": [],
        "keyword_ids": [],
        "required_keywords": [],
        "exclude_self": False,
        "exclude_card_ids": [],
        "dynamic_constraints": [],
        "named_pool_constraints": [],
    }
    if card_id in {"BAR_541", "CORE_BAR_541"} and text == "Deal 2 damage. Discover a spell.":
        return [
            {"kind": "damage", "amount": 2, "target": "any_character"},
            runtime_effect(
                {
                    "kind": "discover_to_hand",
                    "selection": "discover",
                    "count": 1,
                    "offer_count": 3,
                    "with_replacement": False,
                    "created_card_cost_delta": 0,
                    "pool": discover_spell,
                }
            ),
        ]
    if card_id == "JAIL_125" and text == "Freeze an enemy. Get a random Frost spell.":
        frost_pool = dict(discover_spell)
        frost_pool["class_mode"] = "any"
        frost_pool["spell_school_ids"] = [3]
        return [
            {"kind": "freeze", "target": "enemy_character"},
            runtime_effect(
                {
                    "kind": "generate_to_hand",
                    "selection": "uniform_random",
                    "count": 1,
                    "offer_count": 1,
                    "with_replacement": True,
                    "created_card_cost_delta": 0,
                    "pool": frost_pool,
                }
            ),
        ]
    compound: dict[tuple[str, str], list[dict[str, Any]]] = {
        (
            "CORE_CATA_009",
            "Freeze a character. Discover a spell.",
        ): [{"kind": "freeze", "target": "any_character"}],
        (
            "CORE_KAR_077",
            "Give a minion +2/+2. Summon a random 2-Cost minion.",
        ): [
            {"kind": "buff_attack", "amount": 2, "target": "any_minion"},
            {"kind": "buff_health", "amount": 2, "target": "any_minion"},
        ],
        (
            "CORE_WON_337",
            "Gain 4 Armor. Summon a random 4-Cost minion.",
        ): [{"kind": "armor", "amount": 4, "target": "none"}],
        (
            "EDR_848",
            "Restore 6 Health. Get 3 random Druid spells.",
        ): [{"kind": "heal", "amount": 6, "target": "any_character"}],
        (
            "MEND_042",
            "Restore 8 Health to all friendly characters. Summon two random 8-Cost minions.",
        ): [
            {
                "kind": "heal",
                "amount": 8,
                "target": "all_friendly_characters",
            }
        ],
        (
            "WW_080",
            "Restore 5 Health. Discover a spell.",
        ): [{"kind": "heal", "amount": 5, "target": "any_character"}],
    }
    prefix = compound.get((card_id, text))
    if prefix is not None and len(operations) == 1:
        return [*prefix, runtime_effect(operations[0])]
    return None


def card_type_name(card_type_id: Any) -> str:
    return {
        3: "HERO",
        4: "MINION",
        5: "SPELL",
        7: "WEAPON",
        10: "HERO_POWER",
        39: "LOCATION",
    }.get(int(card_type_id or 0), "UNKNOWN")


def build_rule(card: Mapping[str, Any]) -> dict[str, Any] | None:
    normalized = normalize_text(card.get("text"))
    if not RANDOM_OR_DISCOVER.search(normalized):
        return None
    card_id = str(card["card_id"])
    trigger = trigger_for(card.get("card_type_id"), normalized)
    operations = detected_operations(normalized)
    blockers = conditional_blockers(normalized, trigger)
    for operation in operations:
        pool = operation.get("pool") or {}
        if pool:
            if pool.get("source") != "current_format":
                blockers.append(f"pool_source:{pool.get('source')}")
            blockers.extend(f"dynamic:{value}" for value in pool.get("dynamic_constraints", []))
            blockers.extend(f"named_pool:{value}" for value in pool.get("named_pool_constraints", []))
        else:
            blockers.append(f"operation:{operation['kind']}")
        if operation["kind"] not in {
            "generate_to_hand",
            "discover_to_hand",
            "summon_from_pool",
        }:
            blockers.append(f"operation:{operation['kind']}")
        blockers.extend(f"pool_shape:{value}" for value in operation.get("pool_blockers", []))
    for phrase in ENTOURAGE_PHRASES:
        if phrase.lower() in normalized.lower():
            blockers.append(f"named_pool:{phrase}")
    blockers = sorted(set(blockers))

    override = explicit_runtime_override(card_id, normalized, operations)
    runtime_effects: list[dict[str, Any]] = []
    runtime_ready = False
    runtime_origin = "none"
    if override is not None:
        runtime_effects = override
        runtime_ready = True
        runtime_origin = "explicit_reviewed_override"
    elif len(operations) == 1 and (
        product_effects := product_runtime_effects(normalized, operations[0], trigger)
    ) and all(blocker.startswith("pool_shape:") for blocker in blockers):
        runtime_effects = product_effects
        runtime_ready = True
        runtime_origin = "reviewed_product_template"
    elif len(operations) == 1 and not blockers and is_simple_complete_effect(
        normalized, operations[0], trigger
    ):
        runtime_effects = [runtime_effect(operations[0])]
        runtime_ready = True
        runtime_origin = "reviewed_strict_template"
    if runtime_ready:
        blockers = []
    else:
        if len(operations) > 1:
            blockers.append("multiple_operations")
        elif not blockers:
            blockers.append("compound_effect_not_modeled")
        blockers = sorted(set(blockers))

    destinations = {
        "generate_to_hand": "hand",
        "discover_to_hand": "hand",
        "summon_from_pool": "battlefield",
        "cast_from_pool": "cast",
        "shuffle_from_pool": "deck",
    }
    return {
        "rule_id": f"generation-{card_id.lower().replace('_', '-')}-v1",
        "card_id": card_id,
        "dbf_id": int(card.get("dbf_id") or 0),
        "name": str(card.get("name") or card_id),
        "formats": list(card.get("formats") or []),
        "card_type": card_type_name(card.get("card_type_id")),
        "normalized_text": normalized,
        "text_sha256": sha256_text(normalized),
        "trigger": trigger,
        "operations": operations,
        "execution_status": "runtime_ready" if runtime_ready else "explicit_manual_queue",
        "runtime_origin": runtime_origin,
        "runtime_effects": runtime_effects,
        "blockers": blockers,
        "semantic_tags": {
            "stochastic": True,
            "selections": sorted({str(operation["selection"]) for operation in operations}),
            "destinations": sorted(
                {
                    destinations.get(str(operation["kind"]), "non_pool_randomness")
                    for operation in operations
                }
            ),
            "pool_sources": sorted(
                {
                    str((operation.get("pool") or {}).get("source") or "not_a_card_pool")
                    for operation in operations
                }
            ),
            "constraint_dimensions": sorted(
                {
                    field
                    for operation in operations
                    for field, value in (operation.get("pool") or {}).items()
                    if field
                    in {
                        "cost",
                        "card_types",
                        "class_mode",
                        "class_ids",
                        "spell_school_ids",
                        "minion_type_ids",
                        "card_set_ids",
                        "rarity_ids",
                        "keyword_ids",
                        "required_keywords",
                        "dynamic_constraints",
                        "named_pool_constraints",
                    }
                    and value not in ({}, [], "any")
                }
            ),
        },
    }


def build_artifact(manifest: Mapping[str, Any], cards: Iterable[Mapping[str, Any]]) -> dict[str, Any]:
    rules = [rule for card in cards if (rule := build_rule(card)) is not None]
    status = Counter(str(rule["execution_status"]) for rule in rules)
    operation_counts = Counter(
        str(operation["kind"])
        for rule in rules
        for operation in rule["operations"]
    )
    source_counts = Counter(
        str((operation.get("pool") or {}).get("source") or "not_a_card_pool")
        for rule in rules
        for operation in rule["operations"]
    )
    return {
        "schema_version": 1,
        "ruleset_id": RULESET_ID,
        "status": "complete_inventory_with_fail_closed_runtime_subset",
        "authoring_contract": {
            "free_text_is_executable": False,
            "strict_templates_require_review": True,
            "text_fingerprint_required": True,
            "unresolved_rules_are_never_executed": True,
            "zone_or_history_pools_never_fall_back_to_current_format": True,
        },
        "source": {
            "official_pool_run_id": str(manifest.get("run_id") or ""),
            "card_defs_build": str((manifest.get("card_defs") or {}).get("build") or ""),
            "card_defs_sha256": str((manifest.get("card_defs") or {}).get("sha256") or ""),
            "generated_at_utc": str(manifest.get("generated_at_utc") or ""),
            "standard_pool_sha256": str((manifest.get("pool_sha256") or {}).get("standard") or ""),
            "arena_pool_sha256": str((manifest.get("pool_sha256") or {}).get("arena") or ""),
        },
        "counts": {
            "unique_official_cards": sum(1 for _ in cards),
            "stochastic_cards": len(rules),
            "runtime_ready": status["runtime_ready"],
            "explicit_manual_queue": status["explicit_manual_queue"],
            "by_operation": dict(sorted(operation_counts.items())),
            "by_pool_source": dict(sorted(source_counts.items())),
        },
        "rules": rules,
    }


def parse_args() -> argparse.Namespace:
    default_root = (
        Path(os.environ.get("APPDATA", ""))
        / "HearthstoneDeckTracker"
        / "MetaCompanion"
        / "AdvisorData"
        / "OfficialCardPools"
        / "latest"
    )
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description="Build structured random/Discover card-pool rules")
    parser.add_argument("--pool-root", type=Path, default=default_root)
    parser.add_argument(
        "--output",
        type=Path,
        default=repository_root
        / "solver"
        / "metacompanion_solver"
        / "rules_data"
        / "card-generation-pools-v1.json",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    manifest, cards = load_cards(args.pool_root)
    artifact = build_artifact(manifest, cards)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(artifact, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    counts = artifact["counts"]
    print(
        f"{args.output}: {counts['stochastic_cards']} stochastic cards; "
        f"{counts['runtime_ready']} runtime-ready; "
        f"{counts['explicit_manual_queue']} queued"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
