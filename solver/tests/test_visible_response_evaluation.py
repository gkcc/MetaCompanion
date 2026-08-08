from __future__ import annotations

import ast
import copy
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

import metacompanion_solver.visible_response_evaluation as visible_response_evaluation
from metacompanion_solver.visible_response_evaluation import (
    VISIBLE_RESPONSE_REPORT_SCHEMA,
    VISIBLE_RESPONSE_SUITE_ID,
    VisibleResponseEvaluationError,
    evaluate_visible_response_suite,
    load_visible_response_suite,
)
from metacompanion_solver.rust_worker_client import _rust_worker_command


ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "solver" / "fixtures" / "visible-response-v1.json"


def _action(index: int, action_id: str) -> dict[str, object]:
    kind, source, target = action_id.split(":", 2)
    return {
        "index": index,
        "action_id": action_id,
        "kind": kind,
        "type": kind,
        "source_entity_id": int(source),
        "target_entity_id": int(target),
        "card_id": "",
        "text": "fixture action",
    }


def _recommendation(rank: int, action_id: str) -> dict[str, object]:
    return {
        "rank": rank,
        "line_id": f"visible-{rank}",
        "actions": [_action(1, action_id)],
        "expected_win_probability": 0.5,
        "expected_win_rate": 0.5,
        "score_kind": "visible_response_heuristic_v1",
        "summary": "Hidden-information heuristic candidate.",
        "risks": ["Unknown hidden cards may change this line."],
        "is_proven_lethal": False,
        "proof_kind": "",
        "proof_scope": "",
        "response_search_complete": False,
        "is_response_verified": False,
        "response_is_proven_lethal": False,
        "minimax_value": None,
        "verified_portfolio_regret": None,
        "alternative_kind": "fallback",
        "is_safe_after_response": None,
        "opponent_response": None,
    }


def _good_response(fixture: dict[str, object]) -> dict[str, object]:
    request = fixture["request"]
    expected = fixture["expected"]
    legal = sorted(expected["legal_first_action_ids"])
    top = expected["top_first_action_ids"][0]
    second = next(action_id for action_id in legal if action_id != top)
    generated = sorted([top, second])
    counterplay = {
        "planner_model": "rust-visible-response-v1",
        "portfolio_model": "visible-response-root-v1",
        "response_scope": "visible-response-v1",
        "search_complete": False,
        "response_line_complete": False,
        "legal_first_action_count": len(legal),
        "legal_first_action_ids": legal,
        "generated_first_action_count": len(generated),
        "generated_first_action_ids": generated,
        "response_verified_first_action_count": 0,
        "response_verified_first_action_ids": [],
        "missing_first_action_ids": legal,
        "root_action_coverage_complete": False,
        "portfolio_optimality_proven": False,
    }
    return {
        "api_version": "1.0",
        "schema_version": 1,
        "request_id": request["request_id"],
        "state_id": request["state"]["state_id"],
        "status": "partial",
        "is_final": True,
        "elapsed_ms": 1,
        "recommendations": [
            _recommendation(1, top),
            _recommendation(2, second),
        ],
        "coverage": {
            "exact": False,
            "exact_scope": "visible-response-v1",
            "summary": "仅按当前公开信息生成近似候选，不构成安全或最优证明。",
            "details": {"counterplay": counterplay},
            "counterplay": copy.deepcopy(counterplay),
        },
        "warnings": ["对手手牌和牌库含隐藏或未知信息；当前路线仅供参考。"],
        "message": "已生成公开信息范围内的近似候选。",
    }


class VisibleResponseEvaluationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.suite = load_visible_response_suite(FIXTURES)
        self.fixtures = {fixture["id"]: fixture for fixture in self.suite["fixtures"]}
        self.fixture = self.fixtures[
            "hidden-hand-and-deck-prioritize-visible-counterlethal"
        ]
        self.fixtures_by_request = {
            fixture["request"]["request_id"]: fixture
            for fixture in self.suite["fixtures"]
        }

    def evaluate_with_mutation(self, fixture_id: str, mutate) -> dict[str, object]:
        def solve(request):
            fixture = self.fixtures_by_request[request["request_id"]]
            response = _good_response(fixture)
            if fixture["id"] == fixture_id:
                mutate(response)
            return response

        return evaluate_visible_response_suite(FIXTURES, solve)

    def test_fixture_is_raw_hdt_and_exercises_every_required_unknown_boundary(self) -> None:
        self.assertEqual(VISIBLE_RESPONSE_SUITE_ID, self.suite["suite_id"])
        self.assertEqual(3, len(self.suite["fixtures"]))
        request = self.fixture["request"]
        state = request["state"]
        self.assertIn("player", state)
        self.assertIn("opponent", state)
        self.assertNotIn("friendly", state)
        self.assertGreater(state["opponent"]["deck_size"], 0)
        self.assertGreater(state["opponent"]["deck_count"], 0)
        self.assertTrue(state["opponent"]["hand"])
        self.assertEqual("hidden", state["opponent"]["hand"][0]["visibility"])
        unknown_fixture = self.fixtures[
            "unknown-friendly-spell-never-generates-an-action"
        ]
        unknown_card = unknown_fixture["request"]["state"]["player"]["hand"][0]
        self.assertEqual(150, unknown_card["entity_id"])
        self.assertFalse(unknown_card["is_known"])
        vanilla = self.fixtures["vanilla-minions-return-distinct-approximate-roots"]
        self.assertTrue(vanilla["expected"]["requires_vanilla_approximation"])
        self.assertGreaterEqual(
            self.fixture["expected"]["minimum_recommendation_count"], 2
        )

    def test_worker_command_keeps_session_token_out_of_process_arguments(self) -> None:
        command = _rust_worker_command(
            Path("metacompanion-solver.exe"), 43123, "worker-data"
        )
        self.assertFalse(any("session-token" in item for item in command))
        self.assertIn("--port=43123", command)

    def test_independent_expected_contract_accepts_honest_partial_portfolio(self) -> None:
        report = evaluate_visible_response_suite(
            FIXTURES,
            lambda request: _good_response(self.fixtures_by_request[request["request_id"]]),
        )
        self.assertTrue(report["passed"], report)
        self.assertEqual(VISIBLE_RESPONSE_REPORT_SCHEMA, report["schema"])
        self.assertEqual(3, report["metrics"]["partial_status_count"])
        self.assertEqual(1, report["metrics"]["threat_fixture_count"])
        self.assertEqual(1, report["metrics"]["threat_priority_passed_count"])
        self.assertEqual(3, report["metrics"]["distinct_first_actions_fixture_count"])
        self.assertEqual(3, report["metrics"]["distinct_first_actions_passed_count"])
        self.assertEqual(0, report["metrics"]["duplicate_first_action_count"])
        self.assertEqual(1, report["metrics"]["unknown_source_fixture_count"])
        self.assertEqual(1, report["metrics"]["unknown_friendly_action_blocked_count"])
        self.assertEqual(1, report["metrics"]["approximation_fixture_count"])
        self.assertEqual(1, report["metrics"]["approximation_passed_count"])
        self.assertEqual(0, report["metrics"]["false_claim_count"])

    def test_every_forbidden_exact_or_safety_claim_fails_closed(self) -> None:
        mutations = {
            "status ok": lambda value: value.__setitem__("status", "ok"),
            "coverage exact": lambda value: value["coverage"].__setitem__("exact", True),
            "response verified": lambda value: value["recommendations"][0].__setitem__(
                "is_response_verified", True
            ),
            "response search complete": lambda value: value["recommendations"][0].__setitem__(
                "response_search_complete", True
            ),
            "coverage search complete": lambda value: value["coverage"]["details"][
                "counterplay"
            ].__setitem__("search_complete", True),
            "root complete": lambda value: value["coverage"]["details"][
                "counterplay"
            ].__setitem__("root_action_coverage_complete", True),
            "portfolio optimal": lambda value: value["coverage"]["details"][
                "counterplay"
            ].__setitem__("portfolio_optimality_proven", True),
            "verified regret": lambda value: value["recommendations"][0].__setitem__(
                "verified_portfolio_regret", 0
            ),
            "safety verdict": lambda value: value["recommendations"][0].__setitem__(
                "is_safe_after_response", False
            ),
            "minimax value": lambda value: value["recommendations"][0].__setitem__(
                "minimax_value", 100
            ),
            "opponent response": lambda value: value["recommendations"][0].__setitem__(
                "opponent_response", {"actions": [], "tactical_value": 0}
            ),
            "response scope metadata": lambda value: value["recommendations"][0].__setitem__(
                "response_scope", "visible-response-v1"
            ),
            "counterplay object": lambda value: value["recommendations"][0].__setitem__(
                "counterplay", {"search_complete": False, "actions": []}
            ),
        }
        for label, mutate in mutations.items():
            with self.subTest(label=label):
                report = self.evaluate_with_mutation(self.fixture["id"], mutate)
                self.assertFalse(report["passed"], report)
                self.assertGreater(report["metrics"]["contract_failure_count"], 0)

    def test_unknown_friendly_spell_duplicate_roots_and_ignored_threat_all_fail(self) -> None:
        responses: dict[str, dict[str, object]] = {}

        unknown_fixture = self.fixtures[
            "unknown-friendly-spell-never-generates-an-action"
        ]
        unknown = _good_response(unknown_fixture)
        unknown["recommendations"][1] = _recommendation(2, "play_card:150:201")
        unknown["coverage"]["details"]["counterplay"]["generated_first_action_ids"] = [
            unknown["recommendations"][0]["actions"][0]["action_id"],
            "play_card:150:201",
        ]
        unknown["coverage"]["details"]["counterplay"]["generated_first_action_count"] = 2
        responses["unknown friendly spell"] = (unknown_fixture["id"], unknown)

        vanilla_fixture = self.fixtures[
            "vanilla-minions-return-distinct-approximate-roots"
        ]
        duplicate = _good_response(vanilla_fixture)
        duplicate["recommendations"][1] = copy.deepcopy(duplicate["recommendations"][0])
        duplicate["recommendations"][1]["rank"] = 2
        responses["duplicate first action"] = (vanilla_fixture["id"], duplicate)

        ignored_threat = _good_response(self.fixture)
        ignored_threat["recommendations"] = [
            _recommendation(1, "attack:101:30"),
            _recommendation(2, "attack:101:201"),
        ]
        responses["ignored visible counterlethal"] = (self.fixture["id"], ignored_threat)

        for label, (fixture_id, response) in responses.items():
            with self.subTest(label=label):
                report = self.evaluate_with_mutation(
                    fixture_id,
                    lambda target, replacement=response: (
                        target.clear(), target.update(copy.deepcopy(replacement))
                    ),
                )
                self.assertFalse(report["passed"], report)

    def test_fixture_expected_is_authoritative_and_malformed_suite_is_rejected(self) -> None:
        malformed = copy.deepcopy(self.suite)
        malformed["fixtures"][0]["request"]["state"]["opponent"]["deck_count"] = 0
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "malformed.json"
            path.write_text(json.dumps(malformed), encoding="utf-8")
            with self.assertRaisesRegex(
                VisibleResponseEvaluationError, "deck_size/deck_count"
            ):
                load_visible_response_suite(path)

    def test_gate_module_does_not_import_production_search_or_rust_sorting(self) -> None:
        tree = ast.parse(
            Path(visible_response_evaluation.__file__).read_text(encoding="utf-8")
        )
        imported: set[str] = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                imported.update(alias.name for alias in node.names)
            elif isinstance(node, ast.ImportFrom):
                imported.add((node.module or ""))
        forbidden = {
            "metacompanion_solver.search",
            "metacompanion_solver.simulator",
            "metacompanion_solver.turnpair_evaluation",
            "metacompanion_solver.hdt_rule_evaluation",
        }
        self.assertTrue(forbidden.isdisjoint(imported), imported)


if __name__ == "__main__":
    unittest.main()
