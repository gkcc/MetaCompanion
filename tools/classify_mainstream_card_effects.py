#!/usr/bin/env python3
"""Build a reproducible semantic inventory for mainstream Hearthstone cards.

This tool deliberately does not turn free text into executable game rules. It tags
the concepts that a reviewed rule must represent, records ambiguity and dependency
risks, and produces a complete human-readable review catalogue.
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INPUT = ROOT / "artifacts" / "card-modeling" / "current" / "mainstream-cards.json"
DEFAULT_MANIFEST = ROOT / "artifacts" / "card-modeling" / "current" / "corpus-manifest.json"
DEFAULT_OUTPUT = ROOT / "artifacts" / "card-modeling" / "current"

TAG_RE = re.compile(r"<[^>]*>")
SPACE_RE = re.compile(r"\s+")
QUOTED_NAME_RE = re.compile(
    r'(?:"([^"\n]{2,80})"|(?<!\w)\'([^\'\n]{2,80})\'(?!\w))'
)


def _patterns(**items: Sequence[str]) -> dict[str, tuple[re.Pattern[str], ...]]:
    return {
        key: tuple(re.compile(pattern, re.IGNORECASE) for pattern in values)
        for key, values in items.items()
    }


TRIGGER_PATTERNS = _patterns(
    battlecry=(r"\bbattlecry\b",),
    deathrattle=(r"\bdeathrattle\b",),
    start_of_game=(r"\bstart of game\b",),
    start_of_turn=(r"\bat the start of (?:your|each|the) turn",),
    end_of_turn=(r"\bat the end of (?:your|each|the|their) turns?",),
    after_card_play=(r"\bafter you play a card\b",),
    after_minion_play=(r"\bafter you play (?:a|an) minion\b",),
    after_spell_cast=(r"\bafter you cast a spell\b", r"\beach time you cast a spell\b"),
    after_hero_attack=(r"\bafter your hero attacks\b",),
    after_attack=(r"\bafter (?:this|a friendly character) attacks",),
    after_heal=(r"\bafter you restore health\b", r"\bif your hero was healed\b"),
    after_damage=(r"\bif .+ take damage\b", r"\bwhile damaged\b"),
    while_dormant=(r"\bwhile dormant\b",),
    while_in_hand_or_deck=(r"\bwhile (?:in|holding|you hold|you are holding)\b", r"\bwhile in hand or deck\b"),
    quest_progress=(r"\bquest:", r"\breward:"),
    replacement=(r"\binstead of\b", r"\bwould lose\b", r"\bdeals damage instead\b"),
)

CONDITION_PATTERNS = _patterns(
    conditional_if=(r"\bif\b",),
    holding_card=(r"\b(?:holding|while holding|you're holding|you are holding)\b",),
    deck_composition=(r"\bdeck (?:has no|only has|started with|is)\b", r"\bno other minions\b", r"\bno spells\b"),
    mana_threshold=(r"\b10 or more mana\b", r"\bremaining mana\b", r"\bspend all your mana\b"),
    turn_history=(r"\bthis turn\b", r"\blast turn\b", r"\bnext turn\b"),
    game_history=(r"\bthis game\b", r"\bdied this game\b", r"\bdidn't start (?:there|in your deck)\b"),
    damage_state=(r"\bdamaged\b", r"\bsurvives?\b", r"\bif it dies\b", r"\bthat dies\b"),
    card_property=(r"\bthat costs?\b", r"\bwith (?:\d+|a|an).+attack\b", r"\bcard with\b"),
    resource_payment=(r"\bspend (?:a|\d+|all|up to)\b", r"\bcosts (?:health|corpses) instead of mana\b"),
    delayed_counter=(r"\bafter \w+ turns?\b", r"\bfor \d+ turns?\b", r"\bhold this for \d+ turns?\b"),
)

SELECTOR_PATTERNS = _patterns(
    self=(r"\bthis minion\b", r"\bthis card\b", r"\bthis\b"),
    friendly_hero=(r"\byour hero\b",),
    opponent_hero=(r"\b(?:enemy|opponent's) hero\b",),
    both_heroes=(r"\bboth heroes\b",),
    any_character=(r"\ba character\b",),
    all_characters=(r"\ball characters\b",),
    any_minion=(r"\ba minion\b",),
    friendly_minion=(r"\ba friendly minion\b", r"\banother friendly\b"),
    enemy_minion=(r"\ban enemy minion\b", r"\benemy minions?\b"),
    all_minions=(r"\ball minions\b", r"\bALL minions\b"),
    all_friendly_minions=(r"\ball friendly minions\b", r"\byour minions\b"),
    all_enemy_minions=(r"\ball enemy minions\b",),
    all_enemies=(r"\ball enemies\b",),
    all_friendly_characters=(r"\ball friendly characters\b",),
    random_enemy=(r"\brandom enem(?:y|ies)\b",),
    random_enemy_minion=(r"\brandom enemy minion\b",),
    random_friendly_minion=(r"\brandom friendly minion\b",),
    own_hand=(r"\byour hand\b",),
    opponent_hand=(r"\bopponent's hand\b", r"\benemy's hand\b"),
    own_deck=(r"\byour deck\b",),
    opponent_deck=(r"\bopponent's deck\b", r"\benemy's deck\b"),
    hero_power=(r"\bhero power\b",),
    weapon=(r"\bweapon\b",),
    location=(r"\blocation\b",),
)

OPERATION_PATTERNS = _patterns(
    damage=(r"\bdeal[s]? [${#\d{]", r"\bdamage randomly split\b", r"\bshoot \w+ times?\b"),
    heal=(r"\brestore[s]? [#{\d]", r"\brestore .+ health\b"),
    gain_armor=(r"\bgain[s]? \d+ armor\b",),
    draw=(r"\bdraw[s]?\b",),
    discover=(r"\bdiscover\b",),
    generate_card=(r"\b(?:get|add) (?:a|an|two|three|all|\d+)\b", r"\bput .+ (?:in|into|on) your hand\b"),
    summon=(r"\bsummon[s]?\b",),
    destroy=(r"\bdestroy[s]?\b",),
    transform=(r"\btransform[s]?\b",),
    modify_stats=(r"\bgive[s]? .+ [+-]\{?\d", r"\bgain[s]? [+-]\{?\d", r"\bdouble their stats\b"),
    set_stats=(r"\bset .+ stats\b", r"\bchange the health\b", r"\bstats equal\b"),
    grant_keyword=(r"\bgive[s]? .+\b(?:taunt|rush|charge|lifesteal|divine shield|poisonous|elusive|stealth)\b",),
    equip_weapon=(r"\bequip[s]?\b",),
    freeze=(r"\bfreeze\b",),
    discard=(r"\bdiscard[s]?\b", r"\boverdrawn\b"),
    shuffle=(r"\bshuffle[s]?\b",),
    return_to_hand=(r"\breturn[s]? .+ to (?:your|their|its owner's) hand\b",),
    take_control=(r"\btake control\b",),
    resurrect=(r"\bresurrect[s]?\b",),
    force_attack=(r"\battack[s]? a random\b", r"\bthat attack it\b"),
    cast=(r"\bcast[s]?\b",),
    trigger_effect=(r"\btrigger[s]? (?:the |a |your ).+effect", r"\btrigger[s]? .+deathrattles?\b"),
    double_trigger=(r"\beffects? trigger twice\b", r"\btrigger an additional time\b"),
    modify_cost=(r"\bcosts? \(\d+\) (?:less|more)\b", r"\breduce .+ cost\b", r"\bcosts? (?:health|corpses) instead of mana\b"),
    gain_mana=(r"\bgain[s]? (?:a|\d+) mana crystal\b",),
    refresh_mana=(r"\brefresh(?:es)? \d+ mana crystals?\b",),
    overload=(r"\boverload\b",),
    modify_hero_power=(r"\b(?:imbue|refresh|get|modify|replace).+hero power\b", r"\bhero power.+costs\b"),
    copy_or_duplicate=(r"\b(?:copy|copies|copied|duplicate)\b",),
    move_to_deck=(r"\bput .+ (?:on the bottom|into your deck)\b",),
    remove_from_deck=(r"\bremove the top card\b", r"\bdestroy all cards.+decks\b", r"\bdestroy minions in the enemy's deck\b"),
    dormant_or_lock=(r"\bdormant\b", r"\blocked in your hand\b"),
    grant_immunity_or_shield=(r"\bimmune\b", r"\bdivine shield\b"),
    quest_or_reward=(r"\bquest:", r"\breward:"),
    global_rule_change=(r"\bfor the rest of the game\b", r"\byour starting health\b", r"\bmaximum mana\b", r"\btake three hits\b"),
    replacement_effect=(r"\binstead of\b", r"\bwould lose\b", r"\bdeals damage instead\b"),
    set_cost_or_resource=(r"\bfirst .+ costs \(\d+\)", r"\bset your mana to \d+\b"),
    spell_damage=(r"\bspell damage [+-]\d+\b",),
    gain_hero_attack=(r"\bhero has [+-]\d+ attack\b", r"\bgive your hero [+-]\d+ attack\b"),
    choose_mode=(r"\bchoose (?!one\b)",),
    grant_random_modifier=(r"\bgive .+ random bonus effect\b",),
    continuous_keyword_aura=(r"\ball friendly minions are .+\b",),
)

ZONE_PATTERNS = _patterns(
    own_hand=(r"\byour hand\b", r"\bholding\b"),
    opponent_hand=(r"\bopponent's hand\b", r"\benemy's hand\b"),
    own_deck=(r"\byour deck\b",),
    opponent_deck=(r"\bopponent's deck\b", r"\benemy's deck\b"),
    battlefield=(r"\bminions?\b", r"\blocation\b", r"\bweapon\b"),
    hero=(r"\bhero(?:es)?\b",),
    hero_power=(r"\bhero power\b",),
    graveyard_history=(r"\bdied this game\b", r"\bresurrect\b",),
)

STOCHASTIC_PATTERNS = _patterns(
    random_target=(r"\brandom enem", r"\brandom friendly", r"\brandomly split\b"),
    random_generation=(r"\bget (?:a|two|three) random\b", r"\brandom .+ card\b", r"\brandom .+ spell\b"),
    random_summon=(r"\bsummon[s]? (?:a|two) random\b",),
    random_modifier=(r"\brandom bonus effect\b",),
    discover_choice=(r"\bdiscover\b",),
    choose_branch=(r"\bchoose one\b", r"\bchoose (?:a|an|your)\b"),
    secret_choice=(r"\bsecretly choose\b",),
    shuffled_order=(r"\bshuffle\b",),
    explicit_chance=(r"\b\d+% chance\b",),
)

HIDDEN_INFORMATION_PATTERNS = _patterns(
    opponent_hand=(r"\bopponent's hand\b", r"\benemy's hand\b"),
    opponent_deck=(r"\bopponent's deck\b", r"\benemy's deck\b"),
    own_deck_order=(r"\btop card of your deck\b", r"\bbottom .+ cards? (?:from|of) your deck\b"),
    random_pool=(r"\brandom (?:card|spell|minion|demon|dragon|weapon|paladin|shaman|druid|holy|frost|leyline)",),
    discover_pool=(r"\bdiscover\b",),
    secret_selection=(r"\bsecretly choose\b",),
)

HISTORY_PATTERNS = _patterns(
    current_turn=(r"\bthis turn\b",),
    previous_turn=(r"\blast turn\b",),
    whole_game=(r"\bthis game\b",),
    deaths=(r"\bdied this game\b", r"\bthat died\b"),
    starting_deck=(r"\bstart(?:ed|ing) .+deck\b", r"\bdidn't start (?:there|in your deck)\b"),
    cards_played=(r"\bcards? you played\b", r"\bafter you play\b",),
    spells_cast=(r"\bspells? (?:you|you've) cast\b", r"\bcast \d+ (?:other )?spells\b"),
    attacks=(r"\battacked this game\b", r"\bafter .+ attacks\b"),
    held_duration=(r"\bwhile holding\b", r"\bhold this for \d+ turns\b"),
    progression_counter=(r"\bherald\b", r"\bimbued .+ twice\b", r"\bquest:"),
)

DURATION_PATTERNS = _patterns(
    this_turn=(r"\bthis turn\b",),
    next_turn=(r"\bnext turn\b",),
    until_next_turn=(r"\buntil your next turn\b",),
    fixed_turns=(r"\blasts? \d+ turns\b", r"\bfor \d+ turns\b", r"\bdormant for \d+ turns\b"),
    end_of_turn=(r"\bat the end of your turn\b",),
    rest_of_game=(r"\bfor the rest of the game\b", r"\bthis game\b"),
    while_source_active=(r"\bwhile damaged\b", r"\byour .+ are\b", r"\byour hero has\b"),
)

CUSTOM_KEYWORDS = (
    "Choose One",
    "Colossal",
    "Combo",
    "Corpses",
    "Dark Gift",
    "Discover",
    "Dormant",
    "Herald",
    "Imbue",
    "Kindred",
    "Leyline",
    "Overload",
    "Prepare",
    "Quest",
    "Reborn",
    "Rewind",
    "Shatter",
    "Temporary",
    "Tradeable",
)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def normalize_text(value: Any) -> str:
    text = html.unescape(str(value or ""))
    text = text.replace("[x]", " ").replace("\u00a0", " ")
    text = TAG_RE.sub(" ", text)
    text = text.replace("$", "").replace("#", "")
    return SPACE_RE.sub(" ", text).strip()


def _match_labels(text: str, patterns: Mapping[str, Sequence[re.Pattern[str]]]) -> list[str]:
    return sorted(
        label
        for label, candidates in patterns.items()
        if any(candidate.search(text) for candidate in candidates)
    )


def _clean_named_dependencies(text: str) -> list[str]:
    return sorted(
        {
            (match.group(1) or match.group(2)).strip()
            for match in QUOTED_NAME_RE.finditer(text)
            if (match.group(1) or match.group(2)).strip().lower() != "deathrattle"
        },
        key=str.lower,
    )


def _text_quality_flags(official: str, runtime: str) -> list[str]:
    flags: set[str] = set()
    combined = f"{official} {runtime}"
    if re.search(r"\{\d+\}|\|\d|\$\{|#\{", combined):
        flags.add("unresolved_placeholder")
    if re.search(r"\d+\[x\]", str(runtime or "")) or str(runtime or "").count("[x]") > 1:
        flags.add("concatenated_runtime_variants")
    normalized_runtime = normalize_text(runtime)
    if re.search(r"\b(?:deal|restore|summon|draw|give|choose)\s+(?:damage|health|cards?|[+-]/)", normalized_runtime, re.IGNORECASE):
        flags.add("missing_dynamic_scalar")
    if re.search(r"\ball numbers on this card|upgrades? each turn|swaps class each turn", combined, re.IGNORECASE):
        flags.add("stateful_display_text")
    if re.search(r"\(\s*\)", combined):
        flags.add("empty_dynamic_display")
    return sorted(flags)


def _keyword_inventory(card: Mapping[str, Any], text: str) -> list[str]:
    values = {str(value).strip() for value in card.get("mechanics", []) if str(value).strip()}
    for keyword in CUSTOM_KEYWORDS:
        if re.search(rf"\b{re.escape(keyword)}\b", text, re.IGNORECASE):
            values.add(keyword)
    return sorted(values, key=str.lower)


def classify_card(card: Mapping[str, Any]) -> dict[str, Any]:
    official = normalize_text(card.get("official_text"))
    runtime = normalize_text(card.get("runtime_text"))
    text = SPACE_RE.sub(" ", f"{official} {runtime}").strip()
    card_type = str(card.get("card_type", "UNKNOWN")).upper()

    triggers = _match_labels(text, TRIGGER_PATTERNS)
    if card_type == "SPELL":
        triggers.append("play_resolution")
    elif card_type == "LOCATION":
        triggers.append("location_activation")
    triggers = sorted(set(triggers))

    conditions = _match_labels(text, CONDITION_PATTERNS)
    selectors = _match_labels(text, SELECTOR_PATTERNS)
    operations = _match_labels(text, OPERATION_PATTERNS)
    zones = _match_labels(text, ZONE_PATTERNS)
    stochasticity = _match_labels(text, STOCHASTIC_PATTERNS)
    hidden_information = _match_labels(text, HIDDEN_INFORMATION_PATTERNS)
    history = _match_labels(text, HISTORY_PATTERNS)
    durations = _match_labels(text, DURATION_PATTERNS)
    keywords = _keyword_inventory(card, text)
    named_dependencies = _clean_named_dependencies(text)
    entourage_card_ids = sorted(
        {str(value).strip() for value in card.get("entourage_card_ids", []) if str(value).strip()}
    )
    referenced_game_tags = sorted(
        {
            str(value.get("name", "")).strip()
            for value in card.get("referenced_tags", [])
            if isinstance(value, Mapping) and str(value.get("name", "")).strip()
        }
    )
    text_quality = _text_quality_flags(str(card.get("official_text", "")), str(card.get("runtime_text", "")))

    if keywords and not operations:
        operations = ["intrinsic_or_continuous_keyword"]

    families: set[str] = set()
    if stochasticity:
        families.add("stochastic_or_choice")
    if conditions or history:
        families.add("contextual_or_historical")
    if any(trigger in triggers for trigger in ("start_of_game", "start_of_turn", "end_of_turn", "replacement", "quest_progress")):
        families.add("stateful_trigger_or_replacement")
    if any(operation in operations for operation in ("global_rule_change", "modify_hero_power", "quest_or_reward", "trigger_effect", "cast")):
        families.add("engine_or_nested_rule")
    if hidden_information:
        families.add("hidden_information")
    if entourage_card_ids or named_dependencies or any(keyword in keywords for keyword in CUSTOM_KEYWORDS):
        families.add("dependency_graph")
    if not families:
        families.add("deterministic_local")

    review_reasons: set[str] = set()
    if text_quality:
        review_reasons.add("text_not_self_contained")
    if stochasticity:
        review_reasons.add("outcome_distribution_required")
    if hidden_information:
        review_reasons.add("belief_or_pool_model_required")
    if entourage_card_ids or named_dependencies:
        review_reasons.add("referenced_entity_resolution_required")
    if "engine_or_nested_rule" in families:
        review_reasons.add("event_queue_or_nested_resolution_required")
    if "stateful_trigger_or_replacement" in families:
        review_reasons.add("persistent_state_or_replacement_required")
    if any(keyword in keywords for keyword in CUSTOM_KEYWORDS):
        review_reasons.add("keyword_semantics_required")
    if len(operations) >= 3:
        review_reasons.add("multi_operation_order_required")
    if not operations:
        review_reasons.add("no_operation_recognized")

    existing_rule = bool(card.get("existing_rule_coverage"))
    if existing_rule:
        readiness = "existing_verified_rule"
    elif review_reasons & {
        "text_not_self_contained",
        "event_queue_or_nested_resolution_required",
        "persistent_state_or_replacement_required",
        "belief_or_pool_model_required",
    }:
        readiness = "manual_ir_rule_required"
    else:
        readiness = "template_candidate_requires_review"

    complexity = 1
    complexity += min(3, max(0, len(operations) - 1))
    complexity += 1 if conditions or history else 0
    complexity += 2 if stochasticity else 0
    complexity += 2 if hidden_information else 0
    complexity += 2 if "engine_or_nested_rule" in families else 0
    complexity += 2 if "stateful_trigger_or_replacement" in families else 0
    complexity += 1 if text_quality else 0
    complexity = min(10, complexity)

    meta_share = float(card.get("estimated_meta_share_pct", 0.0) or 0.0)
    if not existing_rule and (meta_share >= 20 or complexity >= 8):
        review_priority = "P0"
    elif not existing_rule and (meta_share >= 10 or complexity >= 5):
        review_priority = "P1"
    elif not existing_rule:
        review_priority = "P2"
    else:
        review_priority = "verified"

    return {
        "card_id": str(card.get("card_id", "")),
        "dbf_id": int(card.get("dbf_id", 0) or 0),
        "name": str(card.get("name", "")),
        "card_type": card_type,
        "card_class": str(card.get("card_class", "")),
        "cost": int(card.get("cost", 0) or 0),
        "official_text": str(card.get("official_text", "")),
        "runtime_text": str(card.get("runtime_text", "")),
        "normalized_text": runtime or official,
        "text_sha256": hashlib.sha256((runtime or official).encode("utf-8")).hexdigest(),
        "mechanics_and_keywords": keywords,
        "referenced_game_tags": referenced_game_tags,
        "semantic_inventory": {
            "triggers": triggers,
            "conditions": conditions,
            "target_selectors": selectors,
            "operations": operations,
            "zones_referenced": zones,
            "durations": durations,
            "history_dependencies": history,
            "stochasticity": stochasticity,
            "hidden_information": hidden_information,
            "named_dependencies": named_dependencies,
            "entourage_card_ids": entourage_card_ids,
        },
        "modeling": {
            "families": sorted(families),
            "complexity_score_1_to_10": complexity,
            "execution_readiness": readiness,
            "review_priority": review_priority,
            "curation_required": not existing_rule,
            "semantic_review_required": bool(review_reasons),
            "review_reasons": sorted(review_reasons),
            "text_quality_flags": text_quality,
        },
        "coverage": {
            "existing_rule_coverage": existing_rule,
            "existing_rules": card.get("existing_rules", []),
            "deck_variant_count": int(card.get("deck_variant_count", 0) or 0),
            "core_deck_variant_count": int(card.get("core_deck_variant_count", 0) or 0),
            "estimated_meta_share_pct": meta_share,
            "archetypes": card.get("archetypes", []),
        },
    }


def _flatten_counts(cards: Iterable[Mapping[str, Any]], path: Sequence[str]) -> Counter[str]:
    result: Counter[str] = Counter()
    for card in cards:
        value: Any = card
        for key in path:
            value = value[key]
        result.update(str(item) for item in value)
    return result


def _counter_rows(counter: Counter[str]) -> list[dict[str, Any]]:
    return [
        {"name": name, "cards": count}
        for name, count in sorted(counter.items(), key=lambda item: (-item[1], item[0]))
    ]


def build_summary(cards: Sequence[Mapping[str, Any]], manifest: Mapping[str, Any]) -> dict[str, Any]:
    readiness = Counter(card["modeling"]["execution_readiness"] for card in cards)
    priorities = Counter(card["modeling"]["review_priority"] for card in cards)
    types = Counter(card["card_type"] for card in cards)
    return {
        "schema_version": 1,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "scope": manifest.get("scope", {}),
        "counts": {
            "cards": len(cards),
            "cards_with_existing_verified_rules": readiness["existing_verified_rule"],
            "template_candidates_requiring_review": readiness["template_candidate_requires_review"],
            "manual_ir_rules_required": readiness["manual_ir_rule_required"],
            "cards_with_stochasticity": sum(bool(card["semantic_inventory"]["stochasticity"]) for card in cards),
            "cards_with_hidden_information": sum(bool(card["semantic_inventory"]["hidden_information"]) for card in cards),
            "cards_with_history_dependencies": sum(bool(card["semantic_inventory"]["history_dependencies"]) for card in cards),
            "cards_with_text_quality_flags": sum(bool(card["modeling"]["text_quality_flags"]) for card in cards),
            "cards_with_referenced_game_tags": sum(bool(card["referenced_game_tags"]) for card in cards),
            "cards_with_entourage_card_ids": sum(bool(card["semantic_inventory"]["entourage_card_ids"]) for card in cards),
        },
        "by_card_type": _counter_rows(types),
        "by_execution_readiness": _counter_rows(readiness),
        "by_review_priority": _counter_rows(priorities),
        "modeling_families": _counter_rows(_flatten_counts(cards, ("modeling", "families"))),
        "operations": _counter_rows(_flatten_counts(cards, ("semantic_inventory", "operations"))),
        "triggers": _counter_rows(_flatten_counts(cards, ("semantic_inventory", "triggers"))),
        "stochasticity": _counter_rows(_flatten_counts(cards, ("semantic_inventory", "stochasticity"))),
        "hidden_information": _counter_rows(_flatten_counts(cards, ("semantic_inventory", "hidden_information"))),
        "history_dependencies": _counter_rows(_flatten_counts(cards, ("semantic_inventory", "history_dependencies"))),
        "text_quality_flags": _counter_rows(_flatten_counts(cards, ("modeling", "text_quality_flags"))),
    }


def _escape_markdown(value: Any) -> str:
    return str(value or "").replace("|", "\\|").replace("\n", " ")


def build_catalog(cards: Sequence[Mapping[str, Any]], summary: Mapping[str, Any]) -> str:
    counts = summary["counts"]
    lines = [
        "# 当前主流标准牌组卡牌语义目录",
        "",
        "> 这是一份逐卡文本语义盘点，不是可直接执行的规则库。文本分类只用于发现需要建模的 Trigger / Condition / Selector / Operation / Randomness / History；任何新规则进入求解器前仍必须人工复核并用状态转移测试验证。",
        "",
        "## 范围与结论",
        "",
        f"- 唯一卡牌：{counts['cards']} 张。",
        f"- 已有可执行结构化规则：{counts['cards_with_existing_verified_rules']} 张。",
        f"- 可走通用模板但仍需复核：{counts['template_candidates_requiring_review']} 张。",
        f"- 需要手工 IR、事件队列或概率/隐藏信息模型：{counts['manual_ir_rules_required']} 张。",
        f"- 涉及随机或选择：{counts['cards_with_stochasticity']} 张；涉及隐藏信息/未知池：{counts['cards_with_hidden_information']} 张；依赖历史：{counts['cards_with_history_dependencies']} 张。",
        "",
        "`readiness` 含义：`existing_verified_rule` 已有严格文本指纹规则；`template_candidate_requires_review` 可由通用效果模板表达；`manual_ir_rule_required` 涉及动态文本、持续状态、嵌套结算、随机池或隐藏信息。",
        "",
    ]
    for card_type in ("HERO", "LOCATION", "WEAPON", "SPELL", "MINION"):
        subset = sorted(
            (card for card in cards if card["card_type"] == card_type),
            key=lambda card: (card["name"].lower(), card["card_id"]),
        )
        if not subset:
            continue
        lines.extend(
            [
                f"## {card_type}（{len(subset)}）",
                "",
                "| 卡牌 | 原文（规范化） | 触发 / 操作 | 随机·历史·依赖 | readiness |",
                "|---|---|---|---|---|",
            ]
        )
        for card in subset:
            inventory = card["semantic_inventory"]
            trigger_ops = ", ".join(inventory["triggers"] + inventory["operations"]) or "—"
            risks = (
                inventory["stochasticity"]
                + inventory["history_dependencies"]
                + inventory["named_dependencies"]
                + inventory["entourage_card_ids"]
                + card["modeling"]["text_quality_flags"]
            )
            lines.append(
                "| {name} (`{card_id}`) | {text} | {trigger_ops} | {risks} | {readiness} / {priority} |".format(
                    name=_escape_markdown(card["name"]),
                    card_id=_escape_markdown(card["card_id"]),
                    text=_escape_markdown(card["normalized_text"]),
                    trigger_ops=_escape_markdown(trigger_ops),
                    risks=_escape_markdown(", ".join(risks) or "—"),
                    readiness=card["modeling"]["execution_readiness"],
                    priority=card["modeling"]["review_priority"],
                )
            )
        lines.append("")
    return "\n".join(lines).rstrip() + "\n"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    raw_cards = json.loads(args.input.read_text(encoding="utf-8-sig"))
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    if not isinstance(raw_cards, list) or not raw_cards:
        raise ValueError("input must be a non-empty JSON array")
    cards = [classify_card(card) for card in raw_cards]
    if len({card["card_id"] for card in cards}) != len(cards):
        raise ValueError("input contains duplicate card_id values")
    summary = build_summary(cards, manifest)
    root = {
        "schema_version": 1,
        "artifact_kind": "mainstream-card-semantic-inventory-v1",
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "classification_contract": {
            "purpose": "triage and IR design; never executable rules generated from free text",
            "source_input": args.input.name,
            "source_sha256": file_sha256(args.input),
            "card_count": len(cards),
            "all_source_cards_retained": True,
        },
        "cards": sorted(cards, key=lambda card: card["card_id"]),
    }

    args.output_dir.mkdir(parents=True, exist_ok=True)
    semantics_path = args.output_dir / "mainstream-card-semantics.json"
    summary_path = args.output_dir / "semantic-summary.json"
    catalog_path = args.output_dir / "MAINSTREAM-CARD-CATALOG.md"
    semantics_path.write_text(json.dumps(root, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    catalog_path.write_text(build_catalog(cards, summary), encoding="utf-8")

    print(f"Classified {len(cards)} cards.")
    print(f"  Existing verified rules: {summary['counts']['cards_with_existing_verified_rules']}")
    print(f"  Template candidates: {summary['counts']['template_candidates_requiring_review']}")
    print(f"  Manual IR required: {summary['counts']['manual_ir_rules_required']}")
    print(f"  Output: {args.output_dir.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
