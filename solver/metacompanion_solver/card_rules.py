from __future__ import annotations

import hashlib
import html
import json
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping

from .schemas import (
    GAME_TAG_ENUM_IDS,
    Card,
    CardType,
    Effect,
    GameState,
    PlayerState,
    game_tag_int,
)


RULESET_ID = "hdt-visible-point-effects-v1"
RULESET_SCHEMA_VERSION = 1
MATCHING_CONTRACT = (
    "card_id+normalized_english_text_sha256+card_type"
    "+required_intrinsic_mechanics+declared_context_guards"
)
MAX_RULESET_BYTES = 1024 * 1024
_DETERMINISTIC_EFFECT_KINDS = {
    "damage",
    "damage_all_minions",
    "heal",
    "freeze",
    "armor",
    "buff_attack",
    "buff_health",
    "set_health",
    "gain_hero_attack",
    "gain_mana",
    "summon",
    "set_hero_power_cost",
    "double_one_cost_cards",
    "draw_non_starting_spell_on_weapon_break",
    "damage_split",
    "shuffle_repeat_spell",
    "replay_one_cost_cards",
}
_ALLOWED_CARD_TYPES = {
    CardType.SPELL,
    CardType.MINION,
    CardType.WEAPON,
    CardType.HERO_POWER,
    CardType.LOCATION,
}
_LEGACY_PYTHON_UNSUPPORTED_EFFECT_KINDS = {
    "draw_non_starting_spell_on_weapon_break",
    "damage_split",
    "shuffle_repeat_spell",
    "replay_one_cost_cards",
}
_STRICT_SPECIAL_EFFECT_FIELDS = {
    "draw_non_starting_spell_on_weapon_break": frozenset({"kind", "target"}),
    "damage_split": frozenset({"kind", "amount", "target", "random"}),
    "shuffle_repeat_spell": frozenset(
        {"kind", "target", "count", "card_id", "name"}
    ),
    "replay_one_cost_cards": frozenset({"kind", "target"}),
}
_ALLOWED_REQUIRED_MECHANICS = {"aura", "lifesteal", "trigger"}
_ALLOWED_HAND_RACES = {"DRAGON": 24}
_TAG_PATTERN = re.compile(r"<[^>]*>")
_VARIABLE_PATTERN = re.compile(r"[$#](?=\d)")
_WHITESPACE_PATTERN = re.compile(r"\s+")


class CardRuleError(ValueError):
    pass


def normalize_card_text(value: str) -> str:
    """Normalize presentation-only CardDefs/API differences, never rule semantics."""

    if not isinstance(value, str):
        raise TypeError("card text must be a string")
    text = html.unescape(value).replace("[x]", " ").replace("\u00a0", " ")
    text = _TAG_PATTERN.sub(" ", text)
    text = _VARIABLE_PATTERN.sub("", text)
    return _WHITESPACE_PATTERN.sub(" ", text).strip()


def normalized_text_sha256(value: str) -> str:
    return hashlib.sha256(normalize_card_text(value).encode("utf-8")).hexdigest()


def _required_string(value: Any, path: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise CardRuleError(f"{path} must be a non-empty string")
    return value.strip()


def _string_array(value: Any, path: str, *, allow_empty: bool = False) -> tuple[str, ...]:
    if not isinstance(value, list) or (not value and not allow_empty):
        qualifier = "an array" if allow_empty else "a non-empty array"
        raise CardRuleError(f"{path} must be {qualifier}")
    items: list[str] = []
    for index, item in enumerate(value):
        items.append(_required_string(item, f"{path}[{index}]"))
    if len(items) != len(set(items)):
        raise CardRuleError(f"{path} contains duplicates")
    return tuple(items)


def _reviewed_effect(value: Any, path: str) -> Effect:
    effect = Effect.from_dict(value, path)
    expected_fields = _STRICT_SPECIAL_EFFECT_FIELDS.get(effect.kind)
    if expected_fields is not None:
        if not isinstance(value, Mapping) or set(value) != expected_fields:
            raise CardRuleError(
                f"{path} must use exactly the reviewed fields for {effect.kind}"
            )
    return effect


@dataclass(frozen=True)
class StructuredCardRule:
    rule_id: str
    card_ids: tuple[str, ...]
    card_type: CardType
    accepted_text_sha256: frozenset[str]
    effects: tuple[Effect, ...]
    required_mechanics: frozenset[str] = frozenset()
    resolved_mechanics: frozenset[str] = frozenset()
    require_complete_owner_public_tags: bool = False
    required_zero_context_tags: frozenset[str] = frozenset()
    required_absent_friendly_hand_races: frozenset[str] = frozenset()


@dataclass(frozen=True)
class StructuredCardRuleBundle:
    available: bool = False
    ruleset_id: str = ""
    rules: tuple[StructuredCardRule, ...] = ()
    rules_by_card_id: dict[str, StructuredCardRule] = field(default_factory=dict)
    source_run_id: str = ""
    source_card_defs_build: str = ""
    error: str = ""

    @classmethod
    def unavailable(
        cls, error: str = "structured card-rule bundle is not installed"
    ) -> "StructuredCardRuleBundle":
        return cls(error=error)

    @classmethod
    def load(cls, path: str | Path) -> "StructuredCardRuleBundle":
        source_path = Path(path)
        try:
            size = source_path.stat().st_size
        except OSError as exc:
            raise CardRuleError(f"missing structured card-rule file: {source_path.name}") from exc
        if size <= 0 or size > MAX_RULESET_BYTES:
            raise CardRuleError("structured card-rule file has invalid size")
        try:
            root = json.loads(source_path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise CardRuleError("invalid structured card-rule JSON") from exc
        if not isinstance(root, Mapping):
            raise CardRuleError("structured card-rule root must be an object")
        if root.get("schema_version") != RULESET_SCHEMA_VERSION:
            raise CardRuleError("unsupported structured card-rule schema version")
        if root.get("ruleset_id") != RULESET_ID:
            raise CardRuleError(f"ruleset_id must be {RULESET_ID!r}")
        if root.get("status") != "complete":
            raise CardRuleError("structured card-rule bundle is not complete")
        if root.get("matching_contract") != MATCHING_CONTRACT:
            raise CardRuleError("structured card-rule matching_contract is unsupported")
        source = root.get("source")
        if not isinstance(source, Mapping):
            raise CardRuleError("structured card-rule source must be an object")
        if source.get("rules_generated_from_free_text") is not False:
            raise CardRuleError("structured rules must be explicitly curated, not generated from free text")
        source_run_id = _required_string(source.get("official_pool_run_id"), "source.official_pool_run_id")
        source_build = _required_string(source.get("card_defs_build"), "source.card_defs_build")
        rule_rows = root.get("rules")
        if not isinstance(rule_rows, list) or not rule_rows:
            raise CardRuleError("rules must be a non-empty array")

        rules: list[StructuredCardRule] = []
        rules_by_card_id: dict[str, StructuredCardRule] = {}
        seen_rule_ids: set[str] = set()
        for index, raw in enumerate(rule_rows):
            path_prefix = f"rules[{index}]"
            if not isinstance(raw, Mapping):
                raise CardRuleError(f"{path_prefix} must be an object")
            rule_id = _required_string(raw.get("rule_id"), f"{path_prefix}.rule_id")
            if rule_id in seen_rule_ids:
                raise CardRuleError(f"duplicate rule_id: {rule_id}")
            seen_rule_ids.add(rule_id)
            card_ids = _string_array(raw.get("card_ids"), f"{path_prefix}.card_ids")
            card_type_raw = _required_string(raw.get("card_type"), f"{path_prefix}.card_type").upper()
            try:
                card_type = CardType(card_type_raw)
            except ValueError as exc:
                raise CardRuleError(f"{path_prefix}.card_type is invalid") from exc
            if card_type not in _ALLOWED_CARD_TYPES:
                raise CardRuleError(f"{path_prefix}.card_type is outside point-effect-v1")

            text_rows = raw.get("accepted_texts")
            if not isinstance(text_rows, list) or not text_rows:
                raise CardRuleError(f"{path_prefix}.accepted_texts must be a non-empty array")
            accepted_hashes: set[str] = set()
            for text_index, text_row in enumerate(text_rows):
                text_path = f"{path_prefix}.accepted_texts[{text_index}]"
                if not isinstance(text_row, Mapping):
                    raise CardRuleError(f"{text_path} must be an object")
                normalized = _required_string(text_row.get("normalized"), f"{text_path}.normalized")
                if normalize_card_text(normalized) != normalized:
                    raise CardRuleError(f"{text_path}.normalized is not canonical")
                expected_hash = _required_string(text_row.get("sha256"), f"{text_path}.sha256").lower()
                actual_hash = normalized_text_sha256(normalized)
                if expected_hash != actual_hash:
                    raise CardRuleError(f"{text_path}.sha256 mismatch")
                accepted_hashes.add(actual_hash)

            effect_rows = raw.get("effects")
            if not isinstance(effect_rows, list) or not effect_rows:
                raise CardRuleError(f"{path_prefix}.effects must be a non-empty array")
            effects = tuple(
                _reviewed_effect(item, f"{path_prefix}.effects[{effect_index}]")
                for effect_index, item in enumerate(effect_rows)
            )
            for effect in effects:
                point_effect = (
                    effect.kind
                    in {"damage", "heal", "buff_attack", "buff_health", "set_health"}
                    and effect.target != "none"
                )
                freeze_effect = (
                    effect.kind == "freeze"
                    and effect.amount == 0
                    and effect.target != "none"
                )
                global_effect = (
                    effect.kind == "damage_all_minions"
                    and effect.amount > 0
                    and effect.target == "none"
                )
                owner_effect = (
                    effect.kind in {"armor", "gain_hero_attack", "gain_mana"}
                    and effect.target == "none"
                )
                summon_effect = (
                    effect.kind == "summon"
                    and effect.target == "none"
                    and effect.amount == 0
                    and 1 <= effect.count <= 7
                    and bool(effect.card_id.strip())
                    and bool(effect.name.strip())
                    and effect.health > 0
                )
                hero_power_cost_aura = (
                    effect.kind == "set_hero_power_cost"
                    and effect.target == "none"
                    and 0 <= effect.amount <= 65535
                    and effect.hand_count_at_most is not None
                )
                one_cost_card_doubler = (
                    effect.kind == "double_one_cost_cards"
                    and effect.target == "none"
                    and effect.amount == 2
                )
                weapon_deathrattle = (
                    effect.kind == "draw_non_starting_spell_on_weapon_break"
                    and effect.target == "none"
                    and effect.amount == 0
                    and effect.count == 1
                    and not effect.card_id
                )
                split_damage = (
                    effect.kind == "damage_split"
                    and effect.target == "all_enemy_characters"
                    and effect.random
                    and effect.amount > 0
                    and effect.count == 1
                    and not effect.card_id
                )
                shuffle_repeat = (
                    effect.kind == "shuffle_repeat_spell"
                    and effect.target == "none"
                    and not effect.random
                    and effect.amount == 0
                    and 1 <= effect.count <= 10
                    and bool(effect.card_id.strip())
                    and bool(effect.name.strip())
                )
                one_cost_replay = (
                    effect.kind == "replay_one_cost_cards"
                    and effect.target == "none"
                    and not effect.random
                    and effect.amount == 0
                    and effect.count == 1
                    and not effect.card_id
                )
                point_or_owner_fields_valid = (
                    effect.count == 1
                    and not effect.card_id
                    and effect.attack == 0
                    and not effect.rush
                )
                random_target_effect = (
                    effect.random
                    and point_or_owner_fields_valid
                    and (
                        (point_effect and effect.amount > 0)
                        or freeze_effect
                    )
                    and effect.target not in {"none", "self"}
                    and not effect.target.startswith("all_")
                )
                deterministic_effect = (
                    not effect.random
                    and (
                        point_effect
                        or freeze_effect
                        or global_effect
                        or owner_effect
                        or summon_effect
                        or hero_power_cost_aura
                        or one_cost_card_doubler
                        or weapon_deathrattle
                        or shuffle_repeat
                        or one_cost_replay
                    )
                )
                if (
                    effect.kind not in _DETERMINISTIC_EFFECT_KINDS
                    or (
                        (point_effect or owner_effect or global_effect)
                        and (effect.amount <= 0 or not point_or_owner_fields_valid)
                    )
                    or (freeze_effect and not point_or_owner_fields_valid)
                    or (hero_power_cost_aura and not point_or_owner_fields_valid)
                    or (one_cost_card_doubler and not point_or_owner_fields_valid)
                    or not (
                        random_target_effect
                        or split_damage
                        or deterministic_effect
                    )
                    or (
                        effect.kind != "set_hero_power_cost"
                        and effect.hand_count_at_most is not None
                    )
                    or (
                        effect.kind != "summon"
                        and effect.summoned_card_effects_unmodeled
                    )
                ):
                    raise CardRuleError(
                        f"{path_prefix}.effects contains behavior outside reviewed visible-effect-v1"
                    )
            required_mechanic_rows = _string_array(
                raw.get("required_mechanics", []),
                f"{path_prefix}.required_mechanics",
                allow_empty=True,
            )
            required_mechanics = frozenset(
                item.lower().replace("-", "_").replace(" ", "_")
                for item in required_mechanic_rows
            )
            if len(required_mechanics) != len(required_mechanic_rows):
                raise CardRuleError(
                    f"{path_prefix}.required_mechanics contains normalized duplicates"
                )
            unknown_required_mechanics = sorted(
                required_mechanics.difference(_ALLOWED_REQUIRED_MECHANICS)
            )
            if unknown_required_mechanics:
                raise CardRuleError(
                    f"{path_prefix}.required_mechanics contains unsupported mechanics: "
                    + ", ".join(unknown_required_mechanics)
                )
            resolved = frozenset(
                item.lower().replace("-", "_").replace(" ", "_")
                for item in _string_array(
                    raw.get("resolved_mechanics", []),
                    f"{path_prefix}.resolved_mechanics",
                    allow_empty=True,
                )
            )
            if required_mechanics.intersection(resolved):
                raise CardRuleError(
                    f"{path_prefix} must not resolve a required intrinsic mechanic"
                )
            context_raw = raw.get("context", {})
            if not isinstance(context_raw, Mapping):
                raise CardRuleError(f"{path_prefix}.context must be an object")
            require_complete_owner_public_tags = context_raw.get(
                "require_complete_owner_public_tags", False
            )
            if not isinstance(require_complete_owner_public_tags, bool):
                raise CardRuleError(
                    f"{path_prefix}.context.require_complete_owner_public_tags must be boolean"
                )
            required_zero_context_tags = frozenset(
                item.strip().upper()
                for item in _string_array(
                    context_raw.get("required_zero_context_tags", []),
                    f"{path_prefix}.context.required_zero_context_tags",
                    allow_empty=True,
                )
            )
            unknown_context_tags = sorted(
                required_zero_context_tags.difference(GAME_TAG_ENUM_IDS)
            )
            if unknown_context_tags:
                raise CardRuleError(
                    f"{path_prefix}.context contains unknown GameTags: "
                    + ", ".join(unknown_context_tags)
                )
            if required_zero_context_tags and not require_complete_owner_public_tags:
                raise CardRuleError(
                    f"{path_prefix}.context must require complete owner public tags"
                )
            required_absent_friendly_hand_races = frozenset(
                item.strip().upper()
                for item in _string_array(
                    context_raw.get("required_absent_friendly_hand_races", []),
                    f"{path_prefix}.context.required_absent_friendly_hand_races",
                    allow_empty=True,
                )
            )
            unknown_hand_races = sorted(
                required_absent_friendly_hand_races.difference(_ALLOWED_HAND_RACES)
            )
            if unknown_hand_races:
                raise CardRuleError(
                    f"{path_prefix}.context contains unknown hand races: "
                    + ", ".join(unknown_hand_races)
                )
            rule = StructuredCardRule(
                rule_id=rule_id,
                card_ids=card_ids,
                card_type=card_type,
                accepted_text_sha256=frozenset(accepted_hashes),
                effects=effects,
                required_mechanics=required_mechanics,
                resolved_mechanics=resolved,
                require_complete_owner_public_tags=require_complete_owner_public_tags,
                required_zero_context_tags=required_zero_context_tags,
                required_absent_friendly_hand_races=required_absent_friendly_hand_races,
            )
            for card_id in card_ids:
                if card_id in rules_by_card_id:
                    raise CardRuleError(f"card_id is registered by multiple rules: {card_id}")
                rules_by_card_id[card_id] = rule
            rules.append(rule)
        return cls(
            available=True,
            ruleset_id=RULESET_ID,
            rules=tuple(rules),
            rules_by_card_id=rules_by_card_id,
            source_run_id=source_run_id,
            source_card_defs_build=source_build,
        )

    @classmethod
    def load_optional(cls, path: str | Path | None) -> "StructuredCardRuleBundle":
        if path is None:
            return cls.unavailable()
        try:
            return cls.load(path)
        except (CardRuleError, TypeError, ValueError, OverflowError) as exc:
            return cls.unavailable(str(exc))

    def health(self) -> dict[str, Any]:
        return {
            "available": self.available,
            "ruleset_id": self.ruleset_id,
            "rule_count": len(self.rules),
            "registered_card_id_count": len(self.rules_by_card_id),
            "context_guarded_rule_count": sum(
                1
                for rule in self.rules
                if rule.require_complete_owner_public_tags
                or rule.required_absent_friendly_hand_races
            ),
            "required_mechanic_guarded_rule_count": sum(
                1 for rule in self.rules if rule.required_mechanics
            ),
            "source_official_pool_run_id": self.source_run_id,
            "source_card_defs_build": self.source_card_defs_build,
            "matching_contract": MATCHING_CONTRACT,
            "intrinsic_mechanic_evidence": (
                "吸血规则必须由 has_lifesteal 或 LIFESTEAL(685) 公开标签证明；"
                "持续光环规则必须由 AURA(362) 公开标签证明；"
                "触发器规则必须由 TRIGGER_VISUAL(32) 公开标签证明。"
            ),
            "rules_generated_from_free_text": False,
            "error": self.error,
        }

    @staticmethod
    def _visible_cards(state: GameState) -> list[tuple[PlayerState, Card]]:
        cards: list[tuple[PlayerState, Card]] = []
        for player in (state.friendly, state.opponent):
            cards.extend((player, card) for card in player.hand)
            cards.extend((player, card) for card in player.board)
            if player.hero_power is not None:
                cards.append((player, player.hero_power))
            if player.weapon is not None:
                cards.append((player, player.weapon))
        return cards

    @staticmethod
    def _context_mismatch(
        rule: StructuredCardRule, owner: PlayerState, source: Card
    ) -> str:
        if (
            rule.require_complete_owner_public_tags
            and not owner.public_rule_tags_complete
        ):
            return "owner_public_rule_tags_unavailable"
        active_cards = [owner.hero, *owner.board]
        if owner.hero_power is not None:
            active_cards.append(owner.hero_power)
        if owner.weapon is not None:
            active_cards.append(owner.weapon)
        for tag in rule.required_zero_context_tags:
            if game_tag_int(owner.public_rule_tags, tag):
                return "context_tag_active"
            if any(game_tag_int(card.tags, tag) for card in active_cards):
                return "context_tag_active"
        for race in rule.required_absent_friendly_hand_races:
            race_id = _ALLOWED_HAND_RACES[race]
            for card in owner.hand:
                raw_race = next(
                    (
                        value
                        for key, value in card.tags.items()
                        if key.upper() in {"CARDRACE", "200"}
                    ),
                    None,
                )
                try:
                    present_race = int(raw_race) if raw_race is not None else None
                except (TypeError, ValueError, OverflowError):
                    present_race = None
                if present_race == race_id:
                    return "context_hand_race_present"
        return ""

    @staticmethod
    def _required_mechanic_mismatch(
        rule: StructuredCardRule, source: Card
    ) -> str:
        for mechanic in rule.required_mechanics:
            if mechanic == "lifesteal" and not source.lifesteal:
                return "required_mechanic_unproven"
            if mechanic == "aura" and not game_tag_int(source.tags, "AURA"):
                return "required_mechanic_unproven"
            if mechanic == "trigger" and not game_tag_int(
                source.tags, "TRIGGER_VISUAL"
            ):
                return "required_mechanic_unproven"
        return ""

    def apply(self, state: GameState) -> dict[str, Any]:
        matched: list[dict[str, str]] = []
        mismatches: list[dict[str, str]] = []
        if not self.available:
            return {
                **self.health(),
                "matched_entity_count": 0,
                "mismatch_entity_count": 0,
                "matched": [],
                "mismatches": [],
            }
        for owner, card in self._visible_cards(state):
            rule = self.rules_by_card_id.get(card.card_id)
            if rule is None:
                continue
            actual_hash = normalized_text_sha256(card.card_text)
            mismatch_reason = ""
            if card.card_type != rule.card_type:
                mismatch_reason = "card_type_mismatch"
            elif not card.card_text.strip():
                mismatch_reason = "english_text_missing"
            elif actual_hash not in rule.accepted_text_sha256:
                mismatch_reason = "english_text_sha256_mismatch"
            elif mechanic_mismatch := self._required_mechanic_mismatch(rule, card):
                mismatch_reason = mechanic_mismatch
            elif context_mismatch := self._context_mismatch(rule, owner, card):
                mismatch_reason = context_mismatch
            elif card.effects and not (
                card.rule_id == rule.rule_id and card.rule_version == self.ruleset_id
            ):
                mismatch_reason = "prestructured_effect_conflict"
            if mismatch_reason:
                mismatches.append(
                    {
                        "entity_id": card.entity_id,
                        "card_id": card.card_id,
                        "rule_id": rule.rule_id,
                        "reason": mismatch_reason,
                        "actual_text_sha256": actual_hash,
                    }
                )
                continue

            card.effects = rule.effects
            card.rule_id = rule.rule_id
            card.rule_version = self.ruleset_id
            card.rule_text_sha256 = actual_hash
            card.unsupported_effects = tuple(
                mechanic
                for mechanic in card.unsupported_effects
                if mechanic not in {"card_text_not_parsed", *rule.resolved_mechanics}
            )
            card.unsupported_effects = tuple(
                dict.fromkeys(
                    (
                        *card.unsupported_effects,
                        *(
                            f"legacy_python_simulator_unimplemented:{effect.kind}"
                            for effect in rule.effects
                            if effect.kind in _LEGACY_PYTHON_UNSUPPORTED_EFFECT_KINDS
                        ),
                    )
                )
            )
            card.effect_coverage = "exact" if not card.unsupported_effects else "unsupported"
            matched.append(
                {
                    "entity_id": card.entity_id,
                    "card_id": card.card_id,
                    "rule_id": rule.rule_id,
                    "text_sha256": actual_hash,
                }
            )
        return {
            **self.health(),
            "matched_entity_count": len(matched),
            "mismatch_entity_count": len(mismatches),
            "matched": matched,
            "mismatches": mismatches,
        }


def default_structured_card_rule_path() -> Path:
    return Path(__file__).resolve().parent / "rules_data" / f"{RULESET_ID}.json"
