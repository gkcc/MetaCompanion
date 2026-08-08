from __future__ import annotations

import contextlib
import copy
import io
import json
import subprocess
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

from metacompanion_solver.hdt_rule_evaluation import (
    candidate_wire_request_from_fixture,
    load_hdt_rule_suite,
)

from tools.rust_parity_gate import (
    NEAR_OPTIMAL_REGRET_THRESHOLD,
    PROFILE_COMBAT,
    PROFILE_FULL,
    REPORT_SCHEMA,
    RESULT_SCHEMA,
    ROOT_ACTION_PORTFOLIO_MODEL,
    ParityCase,
    RustParityError,
    RustInvocation,
    _max_portfolio_regret,
    build_profile_cases,
    compare_case_result,
    discover_rust_binary,
    invoke_rust_case,
    main,
    run_gate,
)


def _valid_result(case: ParityCase, variant_index: int = 0) -> dict:
    variant = case.terminal_variants[variant_index]
    result = {
        "schema": RESULT_SCHEMA,
        "case_id": case.case_id,
        "status": "ok",
        "legal_action_ids": list(case.legal_action_ids),
        "action_ids": list(variant.friendly_action_ids),
        "opponent_action_ids": list(variant.opponent_action_ids),
        "top1_action_id": case.top1_action_id,
        "terminal": {
            "state": copy.deepcopy(variant.state),
            "hero_health": {
                "friendly": variant.friendly_health,
                "opponent": variant.opponent_health,
            },
            "hero_armor": {
                "friendly": variant.friendly_armor,
                "opponent": variant.opponent_armor,
            },
        },
        "minimax_utility": case.utility,
        "wall_time_ms": 0.25,
    }
    if case.profile == PROFILE_COMBAT:
        result["proof"] = {
            "has_lethal": case.has_lethal,
            "winning_first_action_ids": list(case.winning_first_action_ids),
            "explored_state_count": case.explored_state_count,
        }
    else:
        scoped = bool(case.ignored_unsupported_hand_entity_ids)
        legal_root_ids = list(case.legal_root_action_ids)
        values = dict(case.first_action_values)
        alternatives = []
        for first_action_id in case.required_portfolio_first_action_ids:
            regret = case.utility - values[first_action_id]
            alternatives.append(
                {
                    "first_action_id": first_action_id,
                    "verified_portfolio_regret": None if scoped else regret,
                    "alternative_kind": (
                        "best_found"
                        if scoped
                        else "co_optimal"
                        if regret == 0
                        else "near_optimal"
                        if regret <= NEAR_OPTIMAL_REGRET_THRESHOLD
                        else "backup"
                    ),
                }
            )
        verified_root_ids = (
            legal_root_ids
            if not scoped
            else list(case.required_portfolio_first_action_ids[:1])
        )
        generated_root_ids = list(verified_root_ids)
        result["portfolio"] = {
            "model": ROOT_ACTION_PORTFOLIO_MODEL,
            "legal_first_action_count": len(legal_root_ids),
            "legal_first_action_ids": legal_root_ids,
            "generated_first_action_count": len(generated_root_ids),
            "generated_first_action_ids": generated_root_ids,
            "response_verified_first_action_count": len(verified_root_ids),
            "response_verified_first_action_ids": verified_root_ids,
            "missing_first_action_ids": sorted(
                set(legal_root_ids) - set(verified_root_ids)
            ),
            "root_action_coverage_complete": not scoped,
            "portfolio_optimality_proven": not scoped,
            "alternatives": alternatives,
        }
    return result


class RustParityGateContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.combat_cases, cls.combat_paths = build_profile_cases(PROFILE_COMBAT)
        cls.full_cases, cls.full_paths = build_profile_cases(PROFILE_FULL)

    def test_profiles_are_fixed_to_every_required_exact_fixture(self) -> None:
        self.assertEqual(7, len(self.combat_cases))
        self.assertEqual(51, len(self.full_cases))
        self.assertEqual(
            {
                "oracle-turn-v1/unique-face-lethal",
                "oracle-turn-v1/multiple-face-lethals",
                "oracle-turn-v1/insufficient-face-damage",
                "oracle-turn-v1/clear-taunt-then-lethal",
                "oracle-turn-v1/divine-shield-order-matters",
                "oracle-turn-v1/mana-sequence-two-spells",
                "oracle-turn-v1/empty-turn-negative-control",
            },
            {case.case_id for case in self.combat_cases},
        )
        self.assertEqual(
            7,
            sum(case.suite_id == "oracle-turnpair-v1" for case in self.full_cases),
        )
        self.assertEqual(
            44,
            sum(case.suite_id == "oracle-hdt-cardrules-v1" for case in self.full_cases),
        )
        for case in (*self.combat_cases, *self.full_cases):
            self.assertTrue(case.legal_action_ids)
            self.assertEqual(tuple(sorted(case.legal_action_ids)), case.legal_action_ids)
            self.assertTrue(case.top1_action_id)
            self.assertIn(case.top1_action_id, case.optimal_first_action_ids)
            self.assertTrue(case.terminal_variants)
            self.assertGreaterEqual(case.python_wall_time_ms, 0)

    def test_returned_regret_limit_prefers_canonical_with_legacy_fallback(self) -> None:
        self.assertEqual(
            7,
            _max_portfolio_regret(
                {"expected": {"max_returned_alternative_regret": 7}}
            ),
        )
        self.assertEqual(
            9,
            _max_portfolio_regret(
                {
                    "expected": {
                        "max_portfolio_first_action_minimax_regret": 9
                    }
                }
            ),
        )
        with self.assertRaises(RustParityError):
            _max_portfolio_regret(
                {
                    "expected": {
                        "max_returned_alternative_regret": 7,
                        "max_portfolio_first_action_minimax_regret": 9,
                    }
                }
            )

    def test_canonical_request_does_not_send_fixture_expected_values(self) -> None:
        envelope = self.combat_cases[0].request_envelope()
        self.assertEqual(
            {"schema", "case_id", "suite_id", "request"},
            set(envelope),
        )
        encoded = json.dumps(envelope)
        self.assertNotIn("winning_first_action_ids", encoded)
        self.assertNotIn("minimax_utility", encoded)
        self.assertNotIn("terminal_variants", encoded)

    def test_only_hdt_cases_send_raw_advisor_state_shape(self) -> None:
        for case in (*self.combat_cases, *self.full_cases):
            state = case.request_envelope()["request"]["state"]
            if case.suite_id == "oracle-hdt-cardrules-v1":
                self.assertIn("player", state, case.case_id)
                self.assertIn("opponent", state, case.case_id)
                self.assertNotIn("friendly", state, case.case_id)
                request_id = case.request_envelope()["request"]["request_id"]
                self.assertEqual("hdt-rule-candidate", request_id.split(":")[0])
            else:
                self.assertIn("friendly", state, case.case_id)
                self.assertNotIn("player", state, case.case_id)

    def test_fake_binary_receives_scrubbed_raw_hdt_request(self) -> None:
        suite = load_hdt_rule_suite(self.full_paths[1])
        fixture = copy.deepcopy(
            next(item for item in suite["fixtures"] if item["scope"] == "exact")
        )
        fixture["position"].setdefault("opponent", {})["hand"] = [
            {
                "entity_id": "99",
                "card_id": "SECRET_INTERNAL_CARD",
                "dbf_id": 987654,
                "name": "Secret internal name",
                "card_type": "SPELL",
                "cost": 9,
                "text": "Secret internal text",
                "mechanics": ["SECRET_INTERNAL_MECHANIC"],
                "zone": "HAND",
                "zone_id": 3,
                "zone_position": 2,
                "controller_id": 2,
                "tags": {
                    "ZONE": 3,
                    "ZONE_POSITION": 2,
                    "CONTROLLER": 2,
                    "COST": 9,
                    "DBF_ID": 987654,
                },
            }
        ]
        original = next(
            case
            for case in self.full_cases
            if case.fixture_id == fixture["id"]
        )
        case = replace(
            original,
            request_wire_payload=candidate_wire_request_from_fixture(
                fixture, int(suite["seed"])
            ),
        )
        captured: dict[str, str] = {}

        def fake_run(command, **kwargs):
            captured["input"] = kwargs["input"]
            return subprocess.CompletedProcess(
                command,
                0,
                stdout=json.dumps(_valid_result(case)),
                stderr="",
            )

        with mock.patch("tools.rust_parity_gate.subprocess.run", side_effect=fake_run):
            invoke_rust_case(Path("fake-rust-solver.exe"), case)

        envelope = json.loads(captured["input"])
        state = envelope["request"]["state"]
        self.assertIn("player", state)
        self.assertIn("opponent", state)
        self.assertNotIn("friendly", state)
        hidden = state["opponent"]["hand"][0]
        self.assertEqual(99, hidden["entity_id"])
        self.assertEqual("hidden", hidden["visibility"])
        self.assertEqual("", hidden["card_id"])
        self.assertEqual(0, hidden["dbf_id"])
        self.assertEqual("", hidden["name"])
        self.assertEqual("UNKNOWN", hidden["card_type"])
        self.assertEqual([], hidden["mechanics"])
        self.assertEqual(
            {"ZONE": 3, "ZONE_POSITION": 2, "CONTROLLER": 2},
            hidden["tags"],
        )
        encoded = captured["input"]
        for secret in (
            "SECRET_INTERNAL_CARD",
            "Secret internal name",
            "Secret internal text",
            "SECRET_INTERNAL_MECHANIC",
            "987654",
        ):
            self.assertNotIn(secret, encoded)

    def test_valid_combat_and_full_results_match_all_semantics(self) -> None:
        self.assertEqual([], compare_case_result(self.combat_cases[0], _valid_result(self.combat_cases[0])))
        self.assertEqual([], compare_case_result(self.full_cases[0], _valid_result(self.full_cases[0])))

    def test_full_portfolio_contract_fails_closed(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "cooptimal-portfolio-no-duplicate"
        )
        valid = _valid_result(case)
        self.assertEqual([], compare_case_result(case, valid))

        missing = copy.deepcopy(valid)
        missing.pop("portfolio")
        self.assertIn(
            "portfolio must be an object",
            "\n".join(compare_case_result(case, missing)),
        )

        duplicate = copy.deepcopy(valid)
        duplicate["portfolio"]["alternatives"].append(
            copy.deepcopy(duplicate["portfolio"]["alternatives"][0])
        )
        self.assertIn(
            "duplicate first action",
            "\n".join(compare_case_result(case, duplicate)),
        )

        forged_regret = copy.deepcopy(valid)
        forged_regret["portfolio"]["alternatives"][0][
            "verified_portfolio_regret"
        ] = 1
        self.assertIn("regret=1", "\n".join(compare_case_result(case, forged_regret)))

        missing_cooptimal = copy.deepcopy(valid)
        missing_cooptimal["portfolio"]["alternatives"].pop()
        self.assertIn(
            "misses required co-optimal first actions",
            "\n".join(compare_case_result(case, missing_cooptimal)),
        )

        false_legal_count = copy.deepcopy(valid)
        false_legal_count["portfolio"]["legal_first_action_count"] -= 1
        self.assertIn(
            "does not equal",
            "\n".join(compare_case_result(case, false_legal_count)),
        )

        bogus_legal_id = copy.deepcopy(valid)
        bogus_legal_id["portfolio"]["legal_first_action_ids"][-1] = "bogus:root:action"
        bogus_legal_id["portfolio"]["legal_first_action_ids"].sort()
        self.assertIn(
            "differ from independent oracle roots",
            "\n".join(compare_case_result(case, bogus_legal_id)),
        )

        padded_backup = copy.deepcopy(valid)
        values = dict(case.first_action_values)
        backup_id = next(
            first_action_id
            for first_action_id, value in values.items()
            if value < case.utility
        )
        backup_regret = case.utility - values[backup_id]
        padded_backup["portfolio"]["alternatives"].append(
            {
                "first_action_id": backup_id,
                "verified_portfolio_regret": backup_regret,
                "alternative_kind": (
                    "near_optimal"
                    if backup_regret <= NEAR_OPTIMAL_REGRET_THRESHOLD
                    else "backup"
                ),
            }
        )
        self.assertIn(
            "exceeds fixture maximum 0",
            "\n".join(compare_case_result(case, padded_backup)),
        )

    def test_full_portfolio_rejects_known_counterlethal_padding(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "face-greed-loses-to-counterlethal"
        )
        result = _valid_result(case)
        values = dict(case.first_action_values)
        unsafe_id = next(
            first_action_id
            for first_action_id in values
            if first_action_id not in case.safe_first_action_ids
        )
        regret = case.utility - values[unsafe_id]
        result["portfolio"]["alternatives"].append(
            {
                "first_action_id": unsafe_id,
                "verified_portfolio_regret": regret,
                "alternative_kind": (
                    "near_optimal"
                    if regret <= NEAR_OPTIMAL_REGRET_THRESHOLD
                    else "backup"
                ),
            }
        )
        self.assertIn(
            "known counterlethal roots",
            "\n".join(compare_case_result(case, result)),
        )

    def test_hdt_normalization_allows_only_transport_and_rule_provenance(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "raw-hdt-elven-archer-removes-counterlethal"
        )
        result = _valid_result(case)
        state = result["terminal"]["state"]
        state["state_id"] = "candidate-transport-state"
        state["patch"] = "candidate-patch"
        state["mode"] = "candidate-mode"
        state["metadata"] = {"adapter": "hdt-snapshot-v1", "diagnostic": True}

        friendly_id = state["friendly"]["player_id"]
        state["active_player_id"] = (
            "101" if state["active_player_id"] == friendly_id else "202"
        )
        state["perspective_player_id"] = (
            "101" if state["perspective_player_id"] == friendly_id else "202"
        )
        state["friendly"]["player_id"] = "101"
        state["opponent"]["player_id"] = "202"

        for player in (state["friendly"], state["opponent"]):
            player["public_rule_tags"] = {}
            player["public_rule_tags_complete"] = True
            cards = [player["hero"], *player["hand"], *player["board"]]
            if player["hero_power"] is not None:
                cards.append(player["hero_power"])
            if player["weapon"] is not None:
                cards.append(player["weapon"])
            for card in cards:
                card["rule_id"] = "candidate-rule"
                card["rule_version"] = "candidate-ruleset"
                card["rule_text_sha256"] = "candidate-provenance"
                card["visibility"] = "public"
                card["tags"].update(
                    {
                        "EXHAUSTED": 0,
                        "HAS_ACTIVATE_POWER": 1,
                        "NUM_ATTACKS_THIS_TURN": 0,
                        "NUM_TURNS_IN_PLAY": 0,
                    }
                )
                if not card["effects"] and not card["unsupported_effects"]:
                    card["effect_coverage"] = "generic"

        self.assertEqual([], compare_case_result(case, result))

        result["terminal"]["state"]["opponent"]["hero"]["visibility"] = "hidden"
        self.assertIn("terminal state differs", "\n".join(compare_case_result(case, result)))

    def test_hdt_normalization_omits_only_zero_valued_public_rule_tags(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "raw-hdt-fireblast-targeted-lethal"
        )
        result = _valid_result(case)
        result["terminal"]["state"]["friendly"]["public_rule_tags"] = {
            "CURRENT_HEROPOWER_DAMAGE_BONUS": 0,
            "HERO_POWER_DOUBLE": False,
            "HEROPOWER_DAMAGE": "0",
        }
        result["terminal"]["state"]["friendly"][
            "public_rule_tags_complete"
        ] = True
        self.assertEqual([], compare_case_result(case, result))

        result["terminal"]["state"]["friendly"]["public_rule_tags"][
            "CURRENT_HEROPOWER_DAMAGE_BONUS"
        ] = 1
        self.assertIn("terminal state differs", "\n".join(compare_case_result(case, result)))

    def test_end_turn_root_uses_contract_id_while_line_keeps_wire_action_id(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "raw-hdt-flame-imp-self-damage-is-not-free"
        )
        result = _valid_result(case)
        self.assertEqual("end_turn", case.top1_action_id)
        self.assertEqual("end_turn::", result["action_ids"][0])
        self.assertEqual([], compare_case_result(case, result))

        result["action_ids"][0] = "end_turn:mutated:"
        self.assertIn("expected Top1", "\n".join(compare_case_result(case, result)))

    def test_scoped_hdt_normalization_only_omits_explicitly_unsupported_hand_card(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "unknown-alternative-does-not-hide-clean-direct-lethal"
        )
        self.assertEqual(("20",), case.ignored_unsupported_hand_entity_ids)
        result = _valid_result(case)
        unknown = copy.deepcopy(result["terminal"]["state"]["friendly"]["hero"])
        unknown.update(
            {
                "entity_id": "20",
                "card_id": "EVAL_UNKNOWN_ALTERNATIVE",
                "name": "Unknown Alternative",
                "card_type": "SPELL",
                "cost": 1,
                "attack": 0,
                "health": 0,
                "current_health": 0,
                "effect_coverage": "unsupported",
                "unsupported_effects": ["card_text_not_parsed"],
                "card_text": "Do something unknown.",
            }
        )
        result["terminal"]["state"]["friendly"]["hand"].append(unknown)
        self.assertEqual([], compare_case_result(case, result))

        unknown["effect_coverage"] = "exact"
        self.assertIn("terminal state differs", "\n".join(compare_case_result(case, result)))

    def test_scoped_portfolio_allows_generated_roots_to_exceed_verified_roots(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "unknown-alternative-does-not-hide-clean-direct-lethal"
        )
        result = _valid_result(case)
        portfolio = result["portfolio"]
        extra_generated = next(
            action_id
            for action_id in portfolio["legal_first_action_ids"]
            if action_id not in portfolio["response_verified_first_action_ids"]
        )
        portfolio["generated_first_action_ids"].append(extra_generated)
        portfolio["generated_first_action_ids"].sort()
        portfolio["generated_first_action_count"] = len(
            portfolio["generated_first_action_ids"]
        )
        self.assertGreater(
            portfolio["generated_first_action_count"],
            portfolio["response_verified_first_action_count"],
        )
        self.assertEqual([], compare_case_result(case, result))

    def test_hdt_normalization_does_not_hide_gameplay_semantic_mutations(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "raw-hdt-elven-archer-removes-counterlethal"
        )
        mutations = {}

        wrong_health = _valid_result(case)
        wrong_health["terminal"]["state"]["friendly"]["hero"]["current_health"] -= 1
        wrong_health["terminal"]["hero_health"]["friendly"] -= 1
        mutations["hero health"] = wrong_health

        wrong_armor = _valid_result(case)
        wrong_armor["terminal"]["state"]["friendly"]["armor"] += 1
        wrong_armor["terminal"]["hero_armor"]["friendly"] += 1
        mutations["hero armor"] = wrong_armor

        wrong_mana = _valid_result(case)
        wrong_mana["terminal"]["state"]["friendly"]["mana"] += 1
        mutations["mana"] = wrong_mana

        wrong_card_state = _valid_result(case)
        card = wrong_card_state["terminal"]["state"]["friendly"]["board"][0]
        card["divine_shield"] = not card["divine_shield"]
        mutations["card state"] = wrong_card_state

        wrong_attack_count = _valid_result(case)
        wrong_attack_count["terminal"]["state"]["friendly"]["board"][0][
            "attacks_remaining"
        ] += 1
        mutations["attacks remaining"] = wrong_attack_count

        wrong_actions = _valid_result(case)
        wrong_actions["action_ids"][-1] = "end_turn:mutated:"
        mutations["action sequence"] = wrong_actions

        wrong_utility = _valid_result(case)
        wrong_utility["minimax_utility"] -= 1
        mutations["minimax utility"] = wrong_utility

        for label, result in mutations.items():
            with self.subTest(label=label):
                self.assertTrue(compare_case_result(case, result), label)

    def test_hdt_hero_power_playable_is_gameplay_state_not_provenance(self) -> None:
        case = next(
            item
            for item in self.full_cases
            if item.fixture_id == "raw-hdt-fireblast-targeted-lethal"
        )
        result = _valid_result(case)
        result["terminal"]["state"]["friendly"]["hero_power"]["playable"] = False
        self.assertIn("terminal state differs", "\n".join(compare_case_result(case, result)))

    def test_comparison_fails_closed_for_each_required_semantic(self) -> None:
        case = self.combat_cases[0]
        mutations = {}

        wrong_legal = _valid_result(case)
        wrong_legal["legal_action_ids"] = wrong_legal["legal_action_ids"][:-1]
        mutations["legal_action_ids"] = wrong_legal

        wrong_top1 = _valid_result(case)
        wrong_top1["top1_action_id"] = case.legal_action_ids[-1]
        mutations["top1_action_id"] = wrong_top1

        wrong_utility = _valid_result(case)
        wrong_utility["minimax_utility"] = case.utility - 1
        mutations["minimax_utility"] = wrong_utility

        wrong_terminal = _valid_result(case)
        wrong_terminal["terminal"]["state"]["friendly"]["hero"]["current_health"] -= 1
        wrong_terminal["terminal"]["hero_health"]["friendly"] -= 1
        mutations["terminal state"] = wrong_terminal

        wrong_proof = _valid_result(case)
        wrong_proof["proof"]["winning_first_action_ids"] = []
        mutations["winning_first_action_ids"] = wrong_proof

        for expected_fragment, result in mutations.items():
            with self.subTest(expected_fragment=expected_fragment):
                errors = compare_case_result(case, result)
                self.assertTrue(errors)
                self.assertIn(expected_fragment, "\n".join(errors))

    def test_terminal_health_must_agree_with_returned_state(self) -> None:
        case = self.combat_cases[0]
        result = _valid_result(case)
        result["terminal"]["hero_health"]["friendly"] -= 1
        errors = compare_case_result(case, result)
        self.assertIn("disagrees with terminal state", "\n".join(errors))

    def test_missing_binary_is_a_failed_gate_not_an_empty_pass(self) -> None:
        report = run_gate(PROFILE_COMBAT, None)
        self.assertEqual(REPORT_SCHEMA, report["schema"])
        self.assertFalse(report["passed"])
        self.assertEqual("missing_binary", report["status"])
        self.assertEqual(7, report["metrics"]["failed_fixture_count"])

    def test_explicit_missing_binary_cli_returns_four_and_writes_report(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            missing = Path(directory) / "missing-metacompanion-solver.exe"
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                exit_code = main(
                    [
                        "--profile",
                        PROFILE_COMBAT,
                        "--binary",
                        str(missing),
                        "--require-binary",
                        "--output",
                        str(output),
                    ]
                )
            self.assertEqual(4, exit_code)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertFalse(report["passed"])
            self.assertEqual("missing_binary", report["status"])
            self.assertIn("does not exist", report["error"])

    def test_gate_records_oracle_process_and_engine_wall_times(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / "metacompanion-solver.exe"
            binary.write_bytes(b"test-only placeholder")

            def fake_invoke(_, case, **__):
                return RustInvocation(_valid_result(case), 1.5, "")

            with mock.patch("tools.rust_parity_gate.invoke_rust_case", side_effect=fake_invoke):
                report = run_gate(PROFILE_COMBAT, binary)
            self.assertTrue(report["passed"])
            self.assertEqual("passed", report["status"])
            metrics = report["metrics"]
            self.assertEqual(7, metrics["passed_fixture_count"])
            self.assertGreater(metrics["python_oracle_wall_time_ms_total"], 0)
            self.assertEqual(10.5, metrics["rust_process_wall_time_ms_total"])
            self.assertEqual(1.75, metrics["rust_reported_wall_time_ms_total"])
            self.assertIsNotNone(metrics["engine_wall_time_speedup"])
            self.assertFalse(metrics["timing_threshold_enforced"])

    def test_gate_does_not_turn_one_mismatch_into_a_profile_pass(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            binary = Path(directory) / "metacompanion-solver.exe"
            binary.write_bytes(b"test-only placeholder")

            def fake_invoke(_, case, **__):
                result = _valid_result(case)
                if case.fixture_id == "unique-face-lethal":
                    result["minimax_utility"] -= 1
                return RustInvocation(result, 1.0, "")

            with mock.patch("tools.rust_parity_gate.invoke_rust_case", side_effect=fake_invoke):
                report = run_gate(PROFILE_COMBAT, binary)
            self.assertFalse(report["passed"])
            self.assertEqual("parity_mismatch", report["status"])
            self.assertEqual(1, report["metrics"]["failed_fixture_count"])

    def test_explicit_binary_path_never_falls_back_to_another_worker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            self.assertIsNone(discover_rust_binary(Path(directory) / "missing.exe"))


class RustBinaryParityIntegrationTests(unittest.TestCase):
    """Real evidence when a Rust build exists; a missing build is an explicit skip."""

    @classmethod
    def setUpClass(cls) -> None:
        cls.binary = discover_rust_binary()
        if cls.binary is None:
            raise unittest.SkipTest(
                "Rust solver binary not found; the standalone parity CLI remains fail-closed"
            )

    def test_real_rust_binary_passes_fixed_combat_profile(self) -> None:
        report = run_gate(PROFILE_COMBAT, self.binary)
        self.assertTrue(
            report["passed"],
            json.dumps(report, ensure_ascii=False, indent=2),
        )


if __name__ == "__main__":
    unittest.main()
