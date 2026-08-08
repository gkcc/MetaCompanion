from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPOSITORY_ROOT / "tools" / "compile_official_card_text_templates.py"
SPEC = importlib.util.spec_from_file_location("official_text_template_compiler", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
COMPILER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMPILER)


def card(card_id: str, text: str, *, card_type_id: int = 5) -> dict[str, object]:
    return {
        "card_id": card_id,
        "dbf_id": 1,
        "name": card_id,
        "card_type_id": card_type_id,
        "text": text,
    }


def hog_card_defs() -> object:
    source = COMPILER.CardDef(
        card_id="BAR_060",
        dbf_id=62481,
        name="Hog Rancher",
        text="Battlecry: Summon a 2/1 Hog with Rush.",
        card_type_id=4,
        attack=3,
        health=2,
        durability=0,
        related_dbf_ids=(63189,),
        keywords=frozenset(),
    )
    token = COMPILER.CardDef(
        card_id="BAR_060t",
        dbf_id=63189,
        name="Hog",
        text="Rush",
        card_type_id=4,
        attack=2,
        health=1,
        durability=0,
        related_dbf_ids=(),
        keywords=frozenset({"rush"}),
    )
    return COMPILER.CardDefIndex(
        by_card_id={source.card_id: source, token.card_id: token},
        by_dbf_id={source.dbf_id: source, token.dbf_id: token},
    )


def transform_and_weapon_card_defs() -> object:
    hex_source = COMPILER.CardDef(
        card_id="CORE_EX1_246",
        dbf_id=69551,
        name="Hex",
        text="Transform a minion into a 0/1 Frog with Taunt.",
        card_type_id=5,
        attack=0,
        health=0,
        durability=0,
        related_dbf_ids=(548,),
        keywords=frozenset(),
    )
    frog = COMPILER.CardDef(
        card_id="hexfrog",
        dbf_id=548,
        name="Frog",
        text="Taunt",
        card_type_id=4,
        attack=0,
        health=1,
        durability=0,
        related_dbf_ids=(),
        keywords=frozenset({"taunt"}),
    )
    tirion = COMPILER.CardDef(
        card_id="CORE_EX1_383",
        dbf_id=69613,
        name="Tirion Fordring",
        text="Divine Shield, Taunt Deathrattle: Equip a 5/3 Ashbringer.",
        card_type_id=4,
        attack=8,
        health=8,
        durability=0,
        related_dbf_ids=(1730,),
        keywords=frozenset({"divine_shield", "taunt"}),
    )
    ashbringer = COMPILER.CardDef(
        card_id="EX1_383t",
        dbf_id=1730,
        name="Ashbringer",
        text="",
        card_type_id=7,
        attack=5,
        health=0,
        durability=3,
        related_dbf_ids=(),
        keywords=frozenset(),
    )
    values = (hex_source, frog, tirion, ashbringer)
    return COMPILER.CardDefIndex(
        by_card_id={value.card_id: value for value in values},
        by_dbf_id={value.dbf_id: value for value in values},
    )


class OfficialTextTemplateCompilerTests(unittest.TestCase):
    def test_draw_compounds_are_consumed_in_full(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "CORE_BT_035",
                "Give your hero +2 Attack this turn. Draw a card.",
            )
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual(
            ["gain_hero_attack", "draw"],
            [effect["kind"] for effect in rule["effects"]],
        )
        self.assertEqual(1, rule["effects"][1]["count"])

    def test_numeric_singular_draw_is_not_mistaken_for_an_unknown_clause(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "END_007",
                "Deal 1 damage. Give your hero +1 Attack this turn. "
                "Draw 1 card. Gain 1 Armor.",
            )
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual(
            ["damage", "gain_hero_attack", "draw", "armor"],
            [effect["kind"] for effect in rule["effects"]],
        )
        self.assertEqual(1, rule["effects"][2]["count"])

    def test_fixed_repeat_expands_to_sequential_effects_on_one_selected_target(self) -> None:
        rule, status = COMPILER.compile_card(
            card("TID_001", "Deal 1 damage to an enemy, twice.")
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual(2, len(rule["effects"]))
        self.assertTrue(all(effect["kind"] == "damage" for effect in rule["effects"]))
        self.assertTrue(
            all(effect["target"] == "enemy_character" for effect in rule["effects"])
        )

    def test_random_point_templates_keep_randomness_in_the_ir(self) -> None:
        freeze, freeze_status = COMPILER.compile_card(
            card(
                "BAR_305",
                "Freeze a random enemy minion. (Upgrades when you have 5 Mana.)",
            )
        )
        buff, buff_status = COMPILER.compile_card(
            card(
                "BAR_880",
                "Give a random friendly minion +3 Attack. "
                "(Upgrades when you have 5 Mana.)",
            )
        )
        self.assertEqual("compiled_generic_template", freeze_status)
        self.assertEqual("freeze", freeze["effects"][0]["kind"])
        self.assertEqual("enemy_minion", freeze["effects"][0]["target"])
        self.assertTrue(freeze["effects"][0]["random"])
        self.assertEqual("compiled_generic_template", buff_status)
        self.assertEqual("buff_attack", buff["effects"][0]["kind"])
        self.assertEqual("friendly_minion", buff["effects"][0]["target"])
        self.assertTrue(buff["effects"][0]["random"])

    def test_random_target_correlation_and_nonresolution_chance_fail_closed(self) -> None:
        correlated, correlated_status = COMPILER.compile_card(
            card("TEST_STATS", "Give a random friendly minion +2/+2.")
        )
        triggered, triggered_status = COMPILER.compile_card(
            card(
                "TEST_TRIGGER",
                "After your hero attacks, give a random friendly minion +2 Attack.",
                card_type_id=4,
            )
        )
        power, power_status = COMPILER.compile_card(
            card(
                "TEST_POWER",
                "Deal 2 damage to a random enemy.",
                card_type_id=10,
            )
        )
        self.assertIsNone(correlated)
        self.assertEqual("no_full_template_match", correlated_status)
        self.assertIsNone(triggered)
        self.assertEqual("chance_trigger_not_executable", triggered_status)
        self.assertIsNone(power)
        self.assertEqual("chance_trigger_not_executable", power_status)

    def test_summon_uses_carddefs_related_token_and_keywords(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "BAR_060",
                "<b>Battlecry:</b> Summon a 2/1 Hog with <b>Rush</b>.",
                card_type_id=4,
            ),
            hog_card_defs(),
        )
        self.assertEqual("compiled_generic_template", status)
        effect = rule["effects"][0]
        self.assertEqual("summon", effect["kind"])
        self.assertEqual("BAR_060t", effect["card_id"])
        self.assertEqual((2, 1), (effect["attack"], effect["health"]))
        self.assertTrue(effect["rush"])

    def test_dynamic_summon_count_never_degrades_to_one_token(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "BAR_060",
                "Battlecry: Summon a 2/1 Hog for every Beast that died.",
                card_type_id=4,
            ),
            hog_card_defs(),
        )
        self.assertIsNone(rule)
        self.assertEqual("no_full_template_match", status)

    def test_conditions_remain_outside_the_closed_grammar(self) -> None:
        rule, status = COMPILER.compile_card(
            card("TEST", "Draw a card. If it is a minion, draw another.")
        )
        self.assertIsNone(rule)
        self.assertEqual("outside_closed_grammar", status)

    def test_deathrattle_is_compiled_as_an_event_not_a_battlecry(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "CORE_EX1_096",
                "<b>Deathrattle:</b> Draw a card.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual("deathrattle", rule["trigger"])
        self.assertEqual("deathrattle", rule["effects"][0]["trigger"])

    def test_battlecry_and_deathrattle_emit_two_distinct_events(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "RLK_708",
                "Battlecry and Deathrattle: Draw a card.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual(
            ["resolution", "deathrattle"],
            [effect.get("trigger", "resolution") for effect in rule["effects"]],
        )

    def test_destroy_and_followup_heal_are_both_compiled(self) -> None:
        rule, status = COMPILER.compile_card(
            card("CORE_EX1_309", "Destroy a minion. Restore 3 Health to your hero.")
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual(["destroy", "heal"], [effect["kind"] for effect in rule["effects"]])

    def test_transform_requires_a_carddefs_bound_token(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "CORE_EX1_246",
                "Transform a minion into a 0/1 Frog with Taunt.",
            ),
            transform_and_weapon_card_defs(),
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual("hexfrog", rule["effects"][0]["card_id"])
        self.assertTrue(rule["effects"][0]["taunt"])

    def test_keyword_punctuation_and_weapon_dependency_are_compiled(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "CORE_EX1_383",
                "Divine Shield, Taunt Deathrattle: Equip a 5/3 Ashbringer.",
                card_type_id=4,
            ),
            transform_and_weapon_card_defs(),
        )
        self.assertEqual("compiled_generic_template", status)
        effect = rule["effects"][0]
        self.assertEqual("equip_weapon", effect["kind"])
        self.assertEqual((5, 3), (effect["attack"], effect["durability"]))
        self.assertEqual("deathrattle", effect["trigger"])

    def test_temporary_mana_and_keyword_grant_use_distinct_effects(self) -> None:
        mana, mana_status = COMPILER.compile_card(
            card("CORE_EX1_169", "Gain 1 Mana Crystal this turn only.")
        )
        shield, shield_status = COMPILER.compile_card(
            card(
                "CORE_EX1_362",
                "Battlecry: Give a friendly minion Divine Shield.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", mana_status)
        self.assertEqual("gain_mana", mana["effects"][0]["kind"])
        self.assertEqual("compiled_generic_template", shield_status)
        self.assertTrue(shield["effects"][0]["divine_shield"])

    def test_multiple_explicit_event_sections_keep_their_lifecycles(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "SCH_526",
                "Battlecry: Set the Health of all other minions to 1. "
                "Deathrattle: Deal 1 damage to all minions.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual("multi_event", rule["trigger"])
        self.assertEqual(
            [("set_health", "resolution"), ("damage", "deathrattle")],
            [
                (effect["kind"], effect.get("trigger", "resolution"))
                for effect in rule["effects"]
            ],
        )

    def test_filtered_draw_compiles_to_an_owner_deck_chance_pool(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "BAR_873",
                "Battlecry: Draw a Holy spell.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", status)
        effect = rule["effects"][0]
        self.assertEqual("draw_from_pool", effect["kind"])
        self.assertEqual("owner_deck", effect["pool"]["source"])
        self.assertEqual(["SPELL"], effect["pool"]["card_types"])
        self.assertEqual([5], effect["pool"]["spell_school_ids"])
        self.assertFalse(effect["with_replacement"])

    def test_cost_filtered_draws_keep_inclusive_pool_bounds(self) -> None:
        minimum, minimum_status = COMPILER.compile_card(
            card(
                "EDR_485_TEST",
                "Battlecry: Draw a minion that costs (7) or more.",
                card_type_id=4,
            )
        )
        maximum, maximum_status = COMPILER.compile_card(
            card(
                "EDR_571_TEST",
                "Battlecry: Draw a spell that costs (5) or less.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", minimum_status)
        self.assertEqual(7, minimum["effects"][0]["pool"]["cost_min"])
        self.assertNotIn("cost_max", minimum["effects"][0]["pool"])
        self.assertEqual("compiled_generic_template", maximum_status)
        self.assertEqual(5, maximum["effects"][0]["pool"]["cost_max"])
        self.assertNotIn("cost_min", maximum["effects"][0]["pool"])

    def test_cost_series_draws_one_card_from_each_exact_cost_pool(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "BAR_551",
                "Battlecry: Draw a 1, 2, and 3-Cost spell.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", status)
        self.assertEqual(3, len(rule["effects"]))
        self.assertEqual(
            [(1, 1), (2, 2), (3, 3)],
            [
                (effect["pool"]["cost_min"], effect["pool"]["cost_max"])
                for effect in rule["effects"]
            ],
        )
        self.assertTrue(
            all(effect["pool"]["card_types"] == ["SPELL"] for effect in rule["effects"])
        )

    def test_cost_filtered_deathrattle_stays_closed_until_chance_events_exist(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "EDR_485",
                "Deathrattle: Draw a minion that costs (7) or more.",
                card_type_id=4,
            )
        )
        self.assertIsNone(rule)
        self.assertEqual("chance_trigger_not_executable", status)

    def test_filtered_draw_on_a_deathrattle_fails_closed_until_chance_events_exist(self) -> None:
        rule, status = COMPILER.compile_card(
            card(
                "BAR_330",
                "Deathrattle: Draw a Deathrattle minion.",
                card_type_id=7,
            )
        )
        self.assertIsNone(rule)
        self.assertEqual("chance_trigger_not_executable", status)

    def test_draw_until_and_each_player_draw_have_distinct_ir(self) -> None:
        until, until_status = COMPILER.compile_card(
            card(
                "TIME_601",
                "Battlecry: Draw until you have 3 cards.",
                card_type_id=4,
            )
        )
        both, both_status = COMPILER.compile_card(
            card(
                "CORE_DMF_067",
                "Battlecry and Deathrattle: Each player draws a card.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", until_status)
        self.assertEqual("draw_until_hand_count", until["effects"][0]["kind"])
        self.assertEqual(3, until["effects"][0]["count"])
        self.assertEqual("compiled_generic_template", both_status)
        self.assertEqual(
            ["resolution", "deathrattle"],
            [effect.get("trigger", "resolution") for effect in both["effects"]],
        )
        self.assertTrue(
            all(effect["kind"] == "draw_both_players" for effect in both["effects"])
        )

    def test_rank_reminder_and_safe_conjunction_do_not_hide_known_effects(self) -> None:
        ranked, ranked_status = COMPILER.compile_card(
            card("BAR_319", "Deal 2 damage. (Upgrades when you have 5 Mana.)")
        )
        compound, compound_status = COMPILER.compile_card(
            card(
                "CORE_NX2_028",
                "After your hero attacks, gain 4 Armor and draw a card.",
                card_type_id=4,
            )
        )
        self.assertEqual("compiled_generic_template", ranked_status)
        self.assertEqual("damage", ranked["effects"][0]["kind"])
        self.assertEqual("compiled_generic_template", compound_status)
        self.assertEqual(
            ["armor", "draw"], [effect["kind"] for effect in compound["effects"]]
        )

    def test_weapon_buff_and_damaged_selector_remain_structured(self) -> None:
        weapon, weapon_status = COMPILER.compile_card(
            card("CORE_CS2_074", "Give your weapon +2 Attack.")
        )
        execute, execute_status = COMPILER.compile_card(
            card("CORE_CS2_108", "Destroy a damaged enemy minion.")
        )
        self.assertEqual("compiled_generic_template", weapon_status)
        self.assertEqual("buff_weapon_attack", weapon["effects"][0]["kind"])
        self.assertEqual("compiled_generic_template", execute_status)
        self.assertEqual("damaged_enemy_minion", execute["effects"][0]["target"])


if __name__ == "__main__":
    unittest.main()
