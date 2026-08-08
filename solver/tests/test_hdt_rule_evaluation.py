from __future__ import annotations

import ast
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

import metacompanion_solver.hdt_rule_evaluation as hdt_rule_evaluation
from metacompanion_solver.config import SolverConfig
from metacompanion_solver.hdt_rule_evaluation import (
    HDT_RULE_SUITE_ID,
    evaluate_hdt_rule_suite,
    load_hdt_rule_suite,
    requests_from_fixture,
)
from metacompanion_solver.logging_store import JsonlTrainingLogger
from metacompanion_solver.service import SolverService


ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "solver" / "fixtures" / "oracle-hdt-cardrules-v1.json"
RULES = (
    ROOT
    / "solver"
    / "metacompanion_solver"
    / "rules_data"
    / "hdt-visible-point-effects-v1.json"
)


class HdtRuleEvaluationTests(unittest.TestCase):
    def test_fixture_contract_covers_point_effects_and_fail_closed_controls(self) -> None:
        suite = load_hdt_rule_suite(FIXTURES)
        self.assertEqual(HDT_RULE_SUITE_ID, suite["suite_id"])
        fixture_ids = {item["id"] for item in suite["fixtures"]}
        for required in {
            "raw-hdt-fireball-only-lethal",
            "raw-hdt-fireblast-targeted-lethal",
            "raw-hdt-steady-shot-fixed-enemy-hero-lethal",
            "raw-hdt-arcane-shot-only-lethal",
            "raw-hdt-elven-archer-removes-counterlethal",
            "raw-hdt-fire-plume-phoenix-only-lethal",
            "raw-hdt-flash-heal-survives-counterattack",
            "raw-hdt-drain-soul-lifesteal-overkill-survival",
            "raw-hdt-void-shard-lifesteal-through-armor-lethal",
            "raw-hdt-death-strike-lifesteal-overkill-survival",
            "drain-soul-lifesteal-evidence-missing-must-abstain",
            "void-shard-lifesteal-evidence-missing-must-abstain",
            "death-strike-lifesteal-evidence-missing-must-abstain",
            "raw-hdt-flame-imp-self-damage-is-not-free",
            "raw-hdt-hecklefang-hyena-self-damage-is-not-free",
            "raw-hdt-filletfighter-only-lethal",
            "raw-hdt-backstab-only-targets-undamaged-minion",
            "raw-hdt-coin-enables-followup-minion",
            "raw-hdt-rock-only-lethal",
            "raw-hdt-moonfire-removes-counterlethal",
            "raw-hdt-wound-prey-damage-rush-summon-lethal",
            "raw-hdt-gorishi-stinger-damage-rush-summon-lethal",
            "raw-hdt-armor-up-prevents-counterlethal",
            "raw-hdt-demon-claws-enables-hero-lethal",
            "raw-hdt-static-shock-clears-taunt-and-enables-hero-lethal",
            "raw-hdt-molten-gold-elemental-only-lethal",
            "raw-hdt-frostbolt-freezes-counterlethal",
            "raw-hdt-searing-fissure-clears-taunt-and-enables-hero-lethal",
            "raw-hdt-sanguine-depths-placement-does-not-activate",
            "raw-hdt-queldorei-fletcher-free-steady-shot-lethal",
            "raw-hdt-niri-doubles-one-cost-rock-lethal",
            "niri-trigger-evidence-missing-must-abstain",
            "niri-text-drift-must-abstain",
            "frostbolt-text-drift-must-abstain",
            "searing-fissure-wrong-card-type-must-abstain",
            "unregistered-location-mechanic-must-abstain",
            "text-hash-drift-must-abstain",
            "steady-shot-target-modifier-must-abstain",
            "fireblast-current-damage-bonus-must-abstain",
            "fireblast-double-must-abstain",
            "fireblast-damage-override-must-abstain",
            "fireblast-owner-tags-missing-must-abstain",
            "unregistered-discover-must-abstain",
            "unregistered-random-must-abstain",
            "unregistered-choose-one-must-abstain",
            "fan-of-knives-draw-identity-must-abstain",
            "shadow-of-demise-transform-history-must-abstain",
            "ebb-and-flow-history-condition-must-abstain",
            "jade-guardians-random-generation-must-abstain",
            "malorne-discover-history-must-abstain",
            "blessing-of-the-bronze-random-rewind-must-abstain",
            "unknown-alternative-does-not-hide-clean-direct-lethal",
        }:
            self.assertIn(required, fixture_ids)

    def test_every_rule_and_new_card_has_a_positive_raw_hdt_fixture(self) -> None:
        suite = load_hdt_rule_suite(FIXTURES)
        rules = json.loads(RULES.read_text(encoding="utf-8"))["rules"]
        expected_rule_ids = {item["rule_id"] for item in rules}
        registered_card_ids = {
            card_id for item in rules for card_id in item["card_ids"]
        }
        positive_rule_ids = {
            rule_id
            for fixture in suite["fixtures"]
            if fixture["scope"] == "exact"
            for rule_id in fixture["expected"]["matched_rule_ids"]
        }
        separately_verified_rule_ids = {
            "jail-underbelly-network-location-v1",
            "cata-sleet-storm-selected-and-random-damage-v1",
            "smuggled-shovel-generated-spell-deathrattle-v1",
            "arcane-tripwire-split-and-shuffle-v1",
            "confront-the-tolvir-one-cost-replay-v1",
        }
        self.assertEqual(
            expected_rule_ids - separately_verified_rule_ids, positive_rule_ids
        )
        self.assertTrue(separately_verified_rule_ids.isdisjoint(positive_rule_ids))

        positive_card_ids: set[str] = set()
        for fixture in suite["fixtures"]:
            if fixture["scope"] != "exact":
                continue
            friendly = fixture["position"].get("friendly", {})
            sources = [*friendly.get("hand", []), *friendly.get("board", [])]
            if friendly.get("hero_power") is not None:
                sources.append(friendly["hero_power"])
            positive_card_ids.update(
                str(source["card_id"])
                for source in sources
                if source.get("card_id") in registered_card_ids
            )
        self.assertTrue(
            {
                "CORE_DS1_185",
                "CORE_UNG_084",
                "CORE_ICC_055",
                "CORE_SW_442",
                "RLK_024",
                "CORE_EX1_319",
                "BAR_745",
                "TSC_963",
                "CORE_CS2_072",
                "CATA_COIN5",
                "WW_001t",
                "CS2_008",
                "CORE_BAR_801",
                "TLC_630t",
                "HERO_01dbp",
                "HERO_10cbp",
                "TIME_218",
                "JAIL_801t",
                "CORE_CS2_024",
                "CATA_582",
                "CORE_REV_990",
                "TIME_606",
                "TLC_836",
            }.issubset(positive_card_ids)
        )

    def test_lifesteal_evidence_reaches_oracle_and_raw_hdt_candidate(self) -> None:
        suite = load_hdt_rule_suite(FIXTURES)
        exact_fixtures = {
            item["id"]: item
            for item in suite["fixtures"]
            if item["scope"] == "exact" and "lifesteal" in item["id"]
        }
        self.assertEqual(3, len(exact_fixtures))
        for fixture_id, fixture in exact_fixtures.items():
            oracle, candidate = requests_from_fixture(fixture, suite["seed"])
            self.assertTrue(oracle.state.friendly.hand[0].lifesteal, fixture_id)
            self.assertTrue(candidate.state.friendly.hand[0].lifesteal, fixture_id)

        void_source = exact_fixtures[
            "raw-hdt-void-shard-lifesteal-through-armor-lethal"
        ]["position"]["friendly"]["hand"][0]
        self.assertFalse(void_source["lifesteal"])
        self.assertEqual({"LIFESTEAL": 1}, void_source["tags"])
        death_source = exact_fixtures[
            "raw-hdt-death-strike-lifesteal-overkill-survival"
        ]["position"]["friendly"]["hand"][0]
        self.assertFalse(death_source["lifesteal"])
        self.assertEqual({"685": "1"}, death_source["tags"])

        missing_ids = {
            "drain-soul-lifesteal-evidence-missing-must-abstain",
            "void-shard-lifesteal-evidence-missing-must-abstain",
            "death-strike-lifesteal-evidence-missing-must-abstain",
        }
        missing_fixtures = {
            item["id"]: item
            for item in suite["fixtures"]
            if item["id"] in missing_ids
        }
        self.assertEqual(missing_ids, set(missing_fixtures))
        for fixture_id, fixture in missing_fixtures.items():
            source = fixture["position"]["friendly"]["hand"][0]
            self.assertNotIn("lifesteal", source, fixture_id)
            self.assertEqual(
                "required_mechanic_unproven",
                fixture["expected"]["mismatch_reason"],
            )
            oracle, candidate = requests_from_fixture(fixture, suite["seed"])
            self.assertFalse(oracle.state.friendly.hand[0].lifesteal, fixture_id)
            self.assertFalse(candidate.state.friendly.hand[0].lifesteal, fixture_id)

    def test_zero_attack_board_minion_is_not_inferred_as_newly_summoned(self) -> None:
        suite = load_hdt_rule_suite(FIXTURES)
        fixture = next(
            item
            for item in suite["fixtures"]
            if item["id"] == "raw-hdt-beaming-sidekick-health-buff"
        )

        oracle, candidate = requests_from_fixture(fixture, suite["seed"])

        self.assertFalse(oracle.state.friendly.board[0].summoned_this_turn)
        self.assertFalse(candidate.state.friendly.board[0].summoned_this_turn)

    def test_fireblast_fixture_carries_zero_guards_and_fail_closed_controls(self) -> None:
        suite = load_hdt_rule_suite(FIXTURES)
        fixtures = {item["id"]: item for item in suite["fixtures"]}
        zero_tags = fixtures["raw-hdt-fireblast-targeted-lethal"]["position"][
            "friendly"
        ]["player_tags"]
        self.assertEqual(
            {
                "CURRENT_HEROPOWER_DAMAGE_BONUS": 0,
                "HERO_POWER_DOUBLE": 0,
                "HEROPOWER_DAMAGE": 0,
            },
            zero_tags,
        )
        for fixture_id in (
            "fireblast-current-damage-bonus-must-abstain",
            "fireblast-double-must-abstain",
            "fireblast-damage-override-must-abstain",
        ):
            tags = fixtures[fixture_id]["position"]["friendly"]["player_tags"]
            self.assertEqual(1, sum(int(value != 0) for value in tags.values()))
        self.assertTrue(
            fixtures["fireblast-owner-tags-missing-must-abstain"]["position"][
                "friendly"
            ]["omit_player_entity"]
        )

    def test_real_solver_passes_independent_raw_hdt_rule_gate(self) -> None:
        service = SolverService(
            SolverConfig(training_log_path=None),
            logger=JsonlTrainingLogger(None),
        )
        report = evaluate_hdt_rule_suite(FIXTURES, service.solve)
        self.assertTrue(report["passed"])
        self.assertEqual(1.0, report["metrics"]["top1_rate"])
        self.assertEqual(1.0, report["metrics"]["top3_rate"])
        self.assertEqual(1.0, report["metrics"]["friendly_action_legality_rate"])
        self.assertEqual(0, report["metrics"]["false_safe_count"])
        self.assertEqual(0, report["metrics"]["false_exact_count"])
        self.assertEqual(0, report["metrics"]["rule_provenance_failure_count"])
        self.assertEqual(0, report["metrics"]["abstain_violation_count"])

    def test_all_unsupported_candidate_fails_quality_gate(self) -> None:
        report = evaluate_hdt_rule_suite(
            FIXTURES,
            lambda request: {
                "status": "unsupported",
                "recommendations": [],
                "coverage": {
                    "structured_card_rules": {
                        "available": True,
                        "ruleset_id": "hdt-visible-point-effects-v1",
                        "matched": [],
                        "mismatches": [],
                    }
                },
            },
        )
        self.assertFalse(report["passed"])
        self.assertEqual(0.0, report["metrics"]["top1_rate"])
        self.assertGreater(report["metrics"]["rule_provenance_failure_count"], 0)

    def test_oracle_modules_do_not_import_production_rule_or_search_engines(self) -> None:
        forbidden = {
            "metacompanion_solver.card_rules",
            "metacompanion_solver.simulator",
            "metacompanion_solver.search",
            ".card_rules",
            ".simulator",
            ".search",
        }
        for module_path in (
            Path(hdt_rule_evaluation.__file__),
            ROOT / "solver" / "metacompanion_solver" / "turnpair_evaluation.py",
        ):
            tree = ast.parse(module_path.read_text(encoding="utf-8"))
            imported: set[str] = set()
            for node in ast.walk(tree):
                if isinstance(node, ast.Import):
                    imported.update(alias.name for alias in node.names)
                elif isinstance(node, ast.ImportFrom):
                    prefix = "." * node.level
                    imported.add(prefix + (node.module or ""))
            self.assertTrue(forbidden.isdisjoint(imported), imported)

    def test_cli_writes_report_and_uses_seed_override(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "hdt-rules.json"
            result = subprocess.run(
                [
                    sys.executable,
                    str(ROOT / "solver" / "launch_solver.py"),
                    "evaluate-hdt-rules",
                    "--fixtures",
                    str(FIXTURES),
                    "--output",
                    str(output),
                    "--seed",
                    "77",
                ],
                cwd=ROOT,
                capture_output=True,
                text=True,
                timeout=60,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertTrue(report["passed"])
            self.assertEqual(77, report["seed"])


if __name__ == "__main__":
    unittest.main()
