from __future__ import annotations

import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.cli import main
from metacompanion_solver.config import SolverConfig
from metacompanion_solver.evaluation import (
    evaluate_suite,
    load_evaluation_suite,
    prove_lethal,
    request_from_fixture,
    write_evaluation_report,
)
from metacompanion_solver.schemas import (
    Action,
    ActionKind,
    Recommendation,
    SearchResult,
    SolveRequest,
)


FIXTURES = Path(__file__).resolve().parents[1] / "fixtures" / "oracle-turn-v1.json"


def always_end_turn(request: SolveRequest) -> SearchResult:
    recommendation = Recommendation(
        rank=1,
        actions=(Action(ActionKind.END_TURN),),
        expected_win_probability=1.0,
        confidence_interval=(0.9, 1.0),
        visits=1,
        rationale="Deliberately bad negative-control candidate.",
        proof_kind="modeled_lethal",
        proof_scope="visible_generic_v2",
        is_proven_lethal=True,
    )
    return SearchResult(
        request_id=request.request_id,
        state_id=request.state.state_id,
        status="ok",
        elapsed_ms=0,
        iterations=1,
        recommendations=(recommendation,),
        progress=(),
        coverage={
            "rules_model": "negative-control",
            "exact": True,
            "approximate_effects": [],
        },
    )


def inconsistent_proof_end_turn(request: SolveRequest) -> SearchResult:
    recommendation = Recommendation(
        rank=1,
        actions=(Action(ActionKind.END_TURN),),
        expected_win_probability=0.5,
        confidence_interval=(0.4, 0.6),
        visits=1,
        rationale="Invalid proof contract negative control.",
        proof_kind="modeled_lethal",
        proof_scope="",
        is_proven_lethal=True,
    )
    return SearchResult(
        request_id=request.request_id,
        state_id=request.state.state_id,
        status="ok",
        elapsed_ms=0,
        iterations=1,
        recommendations=(recommendation,),
        progress=(),
        coverage={"rules_model": "negative-control", "exact": True},
    )


class EvaluationTests(unittest.TestCase):
    def test_fixture_contract_covers_required_deterministic_categories(self) -> None:
        suite = load_evaluation_suite(FIXTURES)
        categories = {fixture["category"] for fixture in suite["fixtures"]}
        self.assertTrue(
            {
                "unique_lethal",
                "multiple_lethal",
                "no_lethal",
                "taunt",
                "divine_shield",
                "mana_sequence",
                "negative_control",
            }.issubset(categories)
        )
        for fixture in suite["fixtures"]:
            if fixture["scope"] != "exact":
                continue
            request = request_from_fixture(fixture, suite["seed"])
            proof = prove_lethal(request.state)
            self.assertEqual(
                fixture["expected"]["has_lethal"],
                proof.has_lethal,
                fixture["id"],
            )
            self.assertEqual(
                fixture["expected"]["winning_first_action_count"],
                len(proof.winning_first_action_ids),
                fixture["id"],
            )

    def test_real_candidate_passes_minimum_oracle_gate_and_report_is_auditable(self) -> None:
        report = evaluate_suite(FIXTURES, SolverConfig())
        self.assertTrue(report["passed"])
        self.assertEqual("oracle-turn-v1", report["suite_id"])
        self.assertEqual(20260729, report["seed"])
        self.assertEqual(64, len(report["suite_hash"]))
        self.assertEqual(report["metrics"]["fixture_count"], len(report["fixture_hashes"]))
        self.assertEqual(1.0, report["metrics"]["proven_top1_rate"])
        self.assertEqual(1.0, report["metrics"]["proven_top3_rate"])
        self.assertEqual(0.0, report["metrics"]["false_lethal_rate"])
        self.assertEqual(1.0, report["metrics"]["legality_rate"])
        self.assertEqual(7, report["metrics"]["exact_fixture_count"])
        self.assertEqual(1, report["metrics"]["approximate_fixture_count"])
        self.assertEqual(1, report["metrics"]["abstain_fixture_count"])
        self.assertIn("latency_p95_ms", report["metrics"])
        self.assertIn("does not prove complete Hearthstone", report["caveat"])

    def test_always_end_turn_negative_control_fails_quality_and_false_lethal_checks(self) -> None:
        report = evaluate_suite(FIXTURES, SolverConfig(), solve=always_end_turn)
        self.assertFalse(report["passed"])
        self.assertEqual(0.0, report["metrics"]["proven_top1_rate"])
        self.assertEqual(0.0, report["metrics"]["proven_top3_rate"])
        self.assertGreater(report["metrics"]["false_lethal_count"], 0)
        failed = {item["name"] for item in report["gate"]["checks"] if not item["passed"]}
        self.assertIn("proven_top1_rate", failed)
        self.assertIn("proven_top3_rate", failed)
        self.assertIn("false_lethal_rate", failed)

    def test_false_lethal_uses_proof_contract_not_heuristic_score(self) -> None:
        report = evaluate_suite(FIXTURES, SolverConfig(), solve=inconsistent_proof_end_turn)
        self.assertFalse(report["passed"])
        self.assertGreater(report["metrics"]["lethal_claim_count"], 0)
        self.assertGreater(report["metrics"]["false_lethal_count"], 0)
        self.assertGreater(report["metrics"]["proof_contract_failure_count"], 0)
        failed = {item["name"] for item in report["gate"]["checks"] if not item["passed"]}
        self.assertIn("false_lethal_rate", failed)
        self.assertIn("proof_contract_failure_count", failed)

    def test_baseline_promotion_rejects_regression_and_does_not_invent_improvement(self) -> None:
        candidate = evaluate_suite(FIXTURES, SolverConfig())
        with tempfile.TemporaryDirectory() as directory:
            baseline_path = Path(directory) / "baseline.json"
            write_evaluation_report(candidate, baseline_path)
            equal = evaluate_suite(FIXTURES, SolverConfig(), baseline_path=baseline_path)
            self.assertTrue(equal["promotion"]["passed"])
            self.assertFalse(equal["promotion"]["quality_improvement_proven"])

            stronger = copy.deepcopy(candidate)
            stronger["metrics"]["proven_top1_rate"] = 1.01
            write_evaluation_report(stronger, baseline_path)
            regressed = evaluate_suite(FIXTURES, SolverConfig(), baseline_path=baseline_path)
            self.assertFalse(regressed["promotion"]["passed"])
            self.assertFalse(regressed["passed"])

    def test_cli_returns_nonzero_when_a_threshold_fails(self) -> None:
        suite = load_evaluation_suite(FIXTURES)
        suite["thresholds"]["min_exact_fixture_count"] = 99
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture_path = root / "failing-suite.json"
            report_path = root / "report.json"
            fixture_path.write_text(
                json.dumps(suite, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = main(
                    [
                        "evaluate",
                        "--fixtures",
                        str(fixture_path),
                        "--output",
                        str(report_path),
                    ]
                )
            self.assertEqual(3, exit_code)
            self.assertFalse(json.loads(report_path.read_text(encoding="utf-8"))["passed"])


if __name__ == "__main__":
    unittest.main()
