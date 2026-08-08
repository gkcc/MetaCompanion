from __future__ import annotations

import importlib.util
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = REPOSITORY_ROOT / "tools" / "build_stochastic_card_pool_rules.py"
SPEC = importlib.util.spec_from_file_location("stochastic_rule_builder", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
RULES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RULES)


def card(card_id: str, text: str, *, card_type_id: int = 5) -> dict[str, object]:
    return {
        "card_id": card_id,
        "dbf_id": 1,
        "name": card_id,
        "formats": ["standard"],
        "card_type_id": card_type_id,
        "text": text,
    }


class StochasticRuleBuilderTests(unittest.TestCase):
    def test_cost_school_type_and_controller_class_are_independent_pool_tags(self) -> None:
        rule = RULES.build_rule(
            card(
                "TEST_ARCANE_EIGHT",
                "Get a random 8-Cost Arcane spell from your class.",
            )
        )
        self.assertIsNotNone(rule)
        operation = rule["operations"][0]
        pool = operation["pool"]
        self.assertEqual(pool["cost"], {"min": 8, "max": 8})
        self.assertEqual(pool["card_types"], ["SPELL"])
        self.assertEqual(pool["spell_school_ids"], [1])
        self.assertEqual(pool["class_mode"], "controller")
        self.assertEqual(rule["execution_status"], "runtime_ready")

    def test_explicit_discover_class_is_a_specific_class_constraint(self) -> None:
        rule = RULES.build_rule(card("TEST_HUNTER", "Discover a Hunter minion."))
        self.assertIsNotNone(rule)
        pool = rule["operations"][0]["pool"]
        self.assertEqual(pool["card_types"], ["MINION"])
        self.assertEqual(pool["class_mode"], "specific")
        self.assertEqual(pool["class_ids"], [3])

    def test_source_battlecry_does_not_become_a_candidate_keyword(self) -> None:
        rule = RULES.build_rule(
            card(
                "TEST_PIRATE",
                "<b>Battlecry:</b> Add a random Pirate to your hand.",
                card_type_id=4,
            )
        )
        self.assertIsNotNone(rule)
        pool = rule["operations"][0]["pool"]
        self.assertEqual(pool["card_types"], ["MINION"])
        self.assertEqual(pool["minion_type_ids"], [23])
        self.assertEqual(pool["required_keywords"], [])

    def test_product_pool_compiles_and_non_play_trigger_fails_closed(self) -> None:
        product = RULES.build_rule(
            card("TEST_COST_PRODUCT", "Summon a random 6, 4, and 2-Cost Taunt minion.")
        )
        frenzy = RULES.build_rule(
            card(
                "TEST_FRENZY",
                "Frenzy: Add a random spell from your class to your hand.",
                card_type_id=4,
            )
        )
        self.assertEqual(product["execution_status"], "runtime_ready")
        self.assertEqual(product["runtime_origin"], "reviewed_product_template")
        self.assertEqual(
            [effect["pool"]["cost_min"] for effect in product["runtime_effects"]],
            [6, 4, 2],
        )
        self.assertEqual(frenzy["trigger"], "frenzy")
        self.assertIn("trigger_engine:frenzy", frenzy["blockers"])
        self.assertEqual(frenzy["execution_status"], "explicit_manual_queue")

    def test_text_normalization_matches_rust_hash_contract(self) -> None:
        self.assertEqual(
            RULES.normalize_text("<b>Battlecry:</b> Deal $1\n damage."),
            "Battlecry: Deal 1 damage.",
        )

    def test_product_dimensions_keep_card_text_order(self) -> None:
        rarity_rule = RULES.build_rule(
            card(
                "TEST_RARITIES",
                "Get a random Epic, Rare, and Common card from other classes.",
            )
        )
        tribe_rule = RULES.build_rule(
            card(
                "TEST_TRIBES",
                "Get a random Golden Pirate and Elemental from other classes.",
            )
        )
        self.assertEqual(
            [effect["pool"]["rarity_ids"] for effect in rarity_rule["runtime_effects"]],
            [[4], [3], [1]],
        )
        self.assertEqual(
            [
                effect["pool"]["minion_type_ids"]
                for effect in tribe_rule["runtime_effects"]
            ],
            [[23], [18]],
        )

    def test_reviewed_compound_rule_keeps_point_and_pool_effects(self) -> None:
        rule = RULES.build_rule(
            card("CORE_WON_337", "Gain 4 Armor. Summon a random 4-Cost minion.")
        )
        self.assertEqual(rule["execution_status"], "runtime_ready")
        self.assertEqual(rule["runtime_origin"], "explicit_reviewed_override")
        self.assertEqual(
            [effect["kind"] for effect in rule["runtime_effects"]],
            ["armor", "summon_from_pool"],
        )


if __name__ == "__main__":
    unittest.main()
