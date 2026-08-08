from __future__ import annotations

import ast
import contextlib
import copy
import io
import json
import tempfile
import unittest
from fractions import Fraction
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.cli import main
from metacompanion_solver.schemas import Action, ActionKind, Card, CardType, Effect
from metacompanion_solver.turnpair_evaluation import (
    LOSS_UTILITY,
    NEAR_OPTIMAL_REGRET_MAX,
    RESPONSE_KIND,
    RESPONSE_SCOPE,
    TACTICAL_SCORE_KIND,
    TurnPairEvaluationError,
    WIN_UTILITY,
    assess_turnpair_line,
    evaluate_turnpair_suite,
    load_turnpair_suite,
    oracle_recommendation_payload,
    prove_turnpair,
    request_from_turnpair_fixture,
    tactical_utility,
)


FIXTURES = Path(__file__).resolve().parents[1] / "fixtures" / "oracle-turnpair-v1.json"


def _wire_actions(actions: tuple[Action, ...]) -> list[dict]:
    result: list[dict] = []
    for index, action in enumerate(actions, start=1):
        item = action.to_dict()
        item.update({"index": index, "type": action.kind.value})
        result.append(item)
    return result


def _verified_payload(request, actions: tuple[Action, ...], *, safe_override=None) -> dict:
    assessment = assess_turnpair_line(request.state, actions)
    proof = prove_turnpair(request.state)
    assert assessment.minimax_value is not None
    portfolio_regret = max(
        0,
        proof.optimal_value - assessment.minimax_value,
    )
    alternative_kind = (
        "co_optimal"
        if portfolio_regret == 0
        else "near_optimal"
        if portfolio_regret <= NEAR_OPTIMAL_REGRET_MAX
        else "backup"
    )
    safe = assessment.safe_after_response if safe_override is None else safe_override
    worst_case_score = (
        1.0
        if assessment.minimax_value >= WIN_UTILITY
        else 0.0
        if assessment.minimax_value <= LOSS_UTILITY
        else max(0.0, min(1.0, 0.5 + assessment.minimax_value / 20_000.0))
    )
    response_scope = RESPONSE_SCOPE
    opponent_reply = _wire_actions(assessment.worst_response)
    response_is_proven_lethal = not safe
    score_components = {"oracle_tactical_utility": float(assessment.minimax_value)}
    return {
        "status": "ok",
        "recommendations": [
            {
                "rank": 1,
                "actions": _wire_actions(actions),
                "score_kind": TACTICAL_SCORE_KIND,
                "minimax_value": assessment.minimax_value,
                "verified_portfolio_regret": portfolio_regret,
                "alternative_kind": alternative_kind,
                "is_safe_after_response": safe,
                "is_response_verified": True,
                "response_kind": RESPONSE_KIND,
                "opponent_reply": opponent_reply,
                "opponent_response": {
                    "actions": opponent_reply,
                    "tactical_value": assessment.minimax_value,
                },
                "worst_case_score": worst_case_score,
                "response_scope": response_scope,
                "response_search_complete": True,
                "response_is_proven_lethal": response_is_proven_lethal,
                "score_components": score_components,
                "counterplay": {
                    "scope": response_scope,
                    "search_complete": True,
                    "is_proven_lethal": response_is_proven_lethal,
                    "worst_case_score": worst_case_score,
                    "actions": opponent_reply,
                    "score_components": score_components,
                },
                "is_proven_lethal": False,
                "proof_kind": "",
                "proof_scope": "",
            }
        ],
    }


class _JsonResult:
    def __init__(self, payload: dict):
        self.payload = payload

    def to_dict(self) -> dict:
        return copy.deepcopy(self.payload)


class TurnPairEvaluationTests(unittest.TestCase):
    def test_tactical_utility_values_one_cost_engine_by_effect_not_card_id(self) -> None:
        suite = load_turnpair_suite(FIXTURES)
        request = request_from_turnpair_fixture(suite["fixtures"][0], suite["seed"])
        vanilla = copy.deepcopy(request.state)
        engine = copy.deepcopy(request.state)
        vanilla.friendly.hand = [
            Card("vanilla", "ANY_ID", "Vanilla", CardType.MINION, cost=3)
        ]
        engine.friendly.hand = [
            Card(
                "engine",
                "DIFFERENT_ID",
                "Engine",
                CardType.MINION,
                cost=3,
                effects=(
                    Effect("double_one_cost_cards", amount=2, target="none"),
                ),
            )
        ]

        self.assertEqual(
            80,
            tactical_utility(engine, engine.friendly.player_id)
            - tactical_utility(vanilla, vanilla.friendly.player_id),
        )

    def test_oracle_end_turn_expires_all_unspent_current_turn_mana(self) -> None:
        from metacompanion_solver.turnpair_evaluation import _apply_oracle_action

        suite = load_turnpair_suite(FIXTURES)
        request = request_from_turnpair_fixture(suite["fixtures"][0], suite["seed"])
        actor = request.state.player(request.state.active_player_id)
        actor.mana = max(1, actor.mana)

        child, ended_turn = _apply_oracle_action(
            request.state, Action(ActionKind.END_TURN)
        )

        self.assertTrue(ended_turn)
        self.assertEqual(0, child.player(actor.player_id).mana)
        self.assertGreater(request.state.player(actor.player_id).mana, 0)

    def test_oracle_module_does_not_import_production_search_simulator_or_models(self) -> None:
        module_path = FIXTURES.parents[1] / "metacompanion_solver" / "turnpair_evaluation.py"
        tree = ast.parse(module_path.read_text(encoding="utf-8"))
        imported: set[str] = set()
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                imported.update(alias.name for alias in node.names)
            elif isinstance(node, ast.ImportFrom):
                imported.add(node.module or "")
        forbidden = {
            "metacompanion_solver.search",
            "metacompanion_solver.simulator",
            "metacompanion_solver.models",
            "search",
            "simulator",
            "models",
        }
        self.assertTrue(imported.isdisjoint(forbidden), imported.intersection(forbidden))

    def test_random_target_outcomes_are_exact_and_include_stealth_minions(self) -> None:
        from metacompanion_solver.turnpair_evaluation import (
            _apply_oracle_action,
            _apply_oracle_action_outcomes,
            evaluate_oracle_visible_policy_root,
            enumerate_oracle_actions,
        )

        request = request_from_turnpair_fixture(
            {
                "id": "sleet-storm-random-target",
                "position": {
                    "friendly": {
                        "mana": 1,
                        "max_mana": 1,
                        "hand": [
                            {
                                "entity_id": "sleet",
                                "card_id": "CATA_485",
                                "name": "Sleet Storm",
                                "card_type": "SPELL",
                                "cost": 1,
                                "effects": [
                                    {
                                        "kind": "damage",
                                        "amount": 2,
                                        "target": "any_character",
                                    },
                                    {
                                        "kind": "damage",
                                        "amount": 1,
                                        "target": "enemy_minion",
                                        "random": True,
                                    },
                                ],
                            }
                        ],
                    },
                    "opponent": {
                        "hero": {"health": 30},
                        "board": [
                            {"entity_id": "open", "attack": 1, "health": 3},
                            {
                                "entity_id": "hidden",
                                "attack": 1,
                                "health": 3,
                                "stealth": True,
                            },
                        ],
                    },
                },
            }
        )
        spell = next(
            action
            for action in enumerate_oracle_actions(request.state)
            if action.action_id == "play_card:sleet:opponent-hero"
        )
        outcomes = _apply_oracle_action_outcomes(request.state, spell)
        self.assertEqual(2, len(outcomes))
        self.assertEqual(Fraction(1, 1), sum(item.probability for item in outcomes))
        self.assertEqual({Fraction(1, 2)}, {item.probability for item in outcomes})
        self.assertTrue(
            any(
                next(
                    card
                    for card in item.state.opponent.board
                    if card.entity_id == "hidden"
                ).current_health
                == 2
                for item in outcomes
            )
        )
        self.assertTrue(
            any(
                next(
                    card
                    for card in item.state.opponent.board
                    if card.entity_id == "open"
                ).current_health
                == 2
                for item in outcomes
            )
        )
        self.assertTrue(
            all(item.state.opponent.hero.current_health == 28 for item in outcomes)
        )
        with self.assertRaises(TurnPairEvaluationError):
            _apply_oracle_action(request.state, spell)
        policy = evaluate_oracle_visible_policy_root(
            request.state, spell, max_depth=5, max_nodes=2_000
        )
        self.assertTrue(policy.recompute_after_random_outcome)
        self.assertEqual(1, len(policy.actions))
        self.assertEqual(spell.action_id, policy.actions[0].action_id)
        self.assertLessEqual(policy.minimum_utility, policy.maximum_utility)
        self.assertGreater(policy.explored_nodes, 0)
        self.assertEqual(Fraction(1, 1), policy.survival_probability)

    def test_random_effect_fizzles_when_selected_damage_removed_only_candidate(self) -> None:
        from metacompanion_solver.turnpair_evaluation import (
            _apply_oracle_action_outcomes,
            enumerate_oracle_actions,
        )

        request = request_from_turnpair_fixture(
            {
                "id": "sleet-storm-random-fizzle",
                "position": {
                    "friendly": {
                        "mana": 1,
                        "max_mana": 1,
                        "hand": [
                            {
                                "entity_id": "sleet",
                                "card_id": "CATA_485",
                                "name": "Sleet Storm",
                                "card_type": "SPELL",
                                "cost": 1,
                                "effects": [
                                    {
                                        "kind": "damage",
                                        "amount": 2,
                                        "target": "any_character",
                                    },
                                    {
                                        "kind": "damage",
                                        "amount": 1,
                                        "target": "enemy_minion",
                                        "random": True,
                                    },
                                ],
                            }
                        ],
                    },
                    "opponent": {
                        "hero": {"health": 30},
                        "board": [
                            {"entity_id": "only", "attack": 1, "health": 2}
                        ],
                    },
                },
            }
        )
        spell = next(
            action
            for action in enumerate_oracle_actions(request.state)
            if action.action_id == "play_card:sleet:only"
        )
        outcomes = _apply_oracle_action_outcomes(request.state, spell)
        self.assertEqual(1, len(outcomes))
        self.assertEqual(Fraction(1, 1), outcomes[0].probability)
        self.assertEqual([], outcomes[0].state.opponent.board)

    def test_other_minion_automatic_target_excludes_played_source(self) -> None:
        from metacompanion_solver.turnpair_evaluation import (
            _apply_oracle_action,
            enumerate_oracle_actions,
        )

        request = request_from_turnpair_fixture(
            {
                "id": "all-other-minions",
                "position": {
                    "friendly": {
                        "mana": 1,
                        "max_mana": 1,
                        "hand": [
                            {
                                "entity_id": "source",
                                "card_id": "SOURCE",
                                "name": "Source",
                                "card_type": "MINION",
                                "cost": 1,
                                "attack": 1,
                                "health": 4,
                                "effects": [
                                    {
                                        "kind": "damage",
                                        "amount": 1,
                                        "target": "all_other_minions",
                                    }
                                ],
                            }
                        ],
                        "board": [
                            {"entity_id": "buddy", "attack": 2, "health": 3}
                        ],
                    },
                    "opponent": {
                        "hero": {"health": 30},
                        "board": [
                            {"entity_id": "enemy", "attack": 2, "health": 3}
                        ],
                    },
                },
            }
        )
        action = next(
            item
            for item in enumerate_oracle_actions(request.state)
            if item.source_entity_id == "source" and item.board_position == 2
        )

        after, ended_turn = _apply_oracle_action(request.state, action)
        self.assertFalse(ended_turn)
        played = next(card for card in after.friendly.board if card.entity_id == "source")
        buddy = next(card for card in after.friendly.board if card.entity_id == "buddy")
        self.assertEqual(4, played.current_health)
        self.assertEqual(2, buddy.current_health)
        self.assertEqual(2, after.opponent.board[0].current_health)

    def test_fixture_contract_covers_counterplay_and_best_effort_categories(self) -> None:
        suite = load_turnpair_suite(FIXTURES)
        categories = {item["category"] for item in suite["fixtures"]}
        self.assertTrue(
            {
                "counterlethal_negative_control",
                "taunt",
                "divine_shield",
                "multiple_first_actions",
                "wide_first_action_counterlethal",
                "cooptimal_portfolio_no_duplicate",
                "unsupported_best_effort_control",
            }.issubset(categories)
        )
        self.assertEqual(7, sum(item["scope"] == "exact" for item in suite["fixtures"]))
        self.assertEqual(2, sum(item["scope"] == "approximate" for item in suite["fixtures"]))
        self.assertEqual(0, sum(item["scope"] == "abstain" for item in suite["fixtures"]))

    def test_tactical_utility_values_threats_without_clearing_blank_zero_attack_bodies(self) -> None:
        blank = request_from_turnpair_fixture(
            {
                "id": "blank-body-value",
                "position": {
                    "friendly": {"hero": {"health": 30}},
                    "opponent": {
                        "hero": {"health": 30},
                        "board": [{"entity_id": "blank", "attack": 0, "health": 1}],
                    },
                },
            }
        )
        threat = request_from_turnpair_fixture(
            {
                "id": "live-threat-value",
                "position": {
                    "friendly": {"hero": {"health": 30}},
                    "opponent": {
                        "hero": {"health": 30},
                        "board": [{"entity_id": "threat", "attack": 4, "health": 1}],
                    },
                },
            }
        )
        one_face_damage = request_from_turnpair_fixture(
            {
                "id": "one-face-damage",
                "position": {
                    "friendly": {"hero": {"health": 30}},
                    "opponent": {"hero": {"health": 30, "current_health": 29}},
                },
            }
        )
        neutral = request_from_turnpair_fixture(
            {
                "id": "neutral-value",
                "position": {
                    "friendly": {"hero": {"health": 30}},
                    "opponent": {"hero": {"health": 30}},
                },
            }
        )

        blank_penalty = tactical_utility(neutral.state, "friendly") - tactical_utility(
            blank.state, "friendly"
        )
        threat_penalty = tactical_utility(neutral.state, "friendly") - tactical_utility(
            threat.state, "friendly"
        )
        face_gain = tactical_utility(one_face_damage.state, "friendly") - tactical_utility(
            neutral.state, "friendly"
        )
        self.assertLess(blank_penalty, face_gain)
        self.assertGreater(threat_penalty, face_gain * 10)

    def test_returned_regret_limit_prefers_canonical_and_legacy_conflicts_fail_closed(
        self,
    ) -> None:
        suite = load_turnpair_suite(FIXTURES)
        legacy_suite = copy.deepcopy(suite)
        for fixture in legacy_suite["fixtures"]:
            expected = fixture.get("expected", {})
            if "max_returned_alternative_regret" in expected:
                expected[
                    "max_portfolio_first_action_minimax_regret"
                ] = expected.pop("max_returned_alternative_regret")
        with tempfile.TemporaryDirectory() as directory:
            fixture_path = Path(directory) / "legacy-turnpair.json"
            fixture_path.write_text(
                json.dumps(legacy_suite, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            report = evaluate_turnpair_suite(
                fixture_path,
                oracle_recommendation_payload,
            )
            self.assertTrue(report["passed"])

            conflicting_suite = copy.deepcopy(suite)
            expected = next(
                item["expected"]
                for item in conflicting_suite["fixtures"]
                if "max_returned_alternative_regret" in item.get(
                    "expected", {}
                )
            )
            expected["max_portfolio_first_action_minimax_regret"] = 1
            fixture_path.write_text(
                json.dumps(
                    conflicting_suite,
                    ensure_ascii=False,
                    indent=2,
                )
                + "\n",
                encoding="utf-8",
            )
            with self.assertRaises(TurnPairEvaluationError):
                evaluate_turnpair_suite(
                    fixture_path,
                    oracle_recommendation_payload,
                )

    def test_counterlethal_oracle_prefers_trade_over_greedy_face_attack(self) -> None:
        suite = load_turnpair_suite(FIXTURES)
        fixture = next(
            item for item in suite["fixtures"]
            if item["id"] == "face-greed-loses-to-counterlethal"
        )
        request = request_from_turnpair_fixture(fixture, suite["seed"])
        proof = prove_turnpair(request.state)
        self.assertFalse(proof.abstained)
        self.assertEqual(("attack:ours:theirs",), proof.optimal_first_action_ids)

        face = assess_turnpair_line(
            request.state,
            (
                Action(ActionKind.ATTACK, "ours", "opponent-hero"),
                Action(ActionKind.END_TURN),
            ),
        )
        trade = assess_turnpair_line(
            request.state,
            (
                Action(ActionKind.ATTACK, "ours", "theirs"),
                Action(ActionKind.END_TURN),
            ),
        )
        self.assertFalse(face.safe_after_response)
        self.assertEqual(LOSS_UTILITY, face.minimax_value)
        self.assertTrue(trade.safe_after_response)
        self.assertGreater(trade.minimax_value, face.minimax_value)

    def test_oracle_json_candidate_passes_all_turnpair_gates(self) -> None:
        report = evaluate_turnpair_suite(
            FIXTURES,
            lambda request: _JsonResult(oracle_recommendation_payload(request)),
        )
        self.assertTrue(report["passed"])
        metrics = report["metrics"]
        self.assertEqual(1.0, metrics["top1_rate"])
        self.assertEqual(1.0, metrics["top3_rate"])
        self.assertEqual(1.0, metrics["friendly_action_legality_rate"])
        self.assertEqual(1.0, metrics["response_action_legality_rate"])
        self.assertEqual(0.0, metrics["mean_minimax_regret"])
        self.assertEqual(0, metrics["max_minimax_regret"])
        self.assertEqual(0.0, metrics["false_safe_rate"])
        self.assertEqual(0, metrics["proof_contract_failure_count"])
        self.assertEqual(0, metrics["response_contract_failure_count"])
        self.assertEqual(0, metrics["fixture_contract_failure_count"])
        self.assertEqual(3, metrics["multi_optimal_fixture_count"])
        self.assertEqual(
            1.0,
            metrics["multi_optimal_first_action_recall_at_k"],
        )
        self.assertEqual(
            1.0,
            metrics["distinct_recommended_first_action_rate"],
        )
        self.assertEqual(0, metrics["duplicate_first_action_count"])
        self.assertEqual(
            0,
            metrics["root_action_coverage_contract_failure_count"],
        )
        self.assertEqual(
            0,
            metrics["portfolio_regret_contract_failure_count"],
        )
        self.assertEqual(
            0,
            metrics["viable_portfolio_contract_failure_count"],
        )
        self.assertIn("latency_p95_ms", metrics)
        self.assertIn("does not model hidden hands", report["caveat"])

    def test_top_k_one_cannot_pass_cooptimal_portfolio_gate(self) -> None:
        report = evaluate_turnpair_suite(
            FIXTURES,
            lambda request: oracle_recommendation_payload(request, top_k=1),
        )
        self.assertFalse(report["passed"])
        metrics = report["metrics"]
        self.assertLess(
            metrics["multi_optimal_first_action_recall_at_k"],
            1.0,
        )
        self.assertEqual(0, metrics["duplicate_first_action_count"])
        self.assertEqual(
            0,
            metrics["portfolio_regret_contract_failure_count"],
        )
        failed = {
            item["name"]
            for item in report["gate"]["checks"]
            if not item["passed"]
        }
        self.assertIn("multi_optimal_first_action_recall_at_k", failed)

    def test_duplicate_cooptimal_first_action_fails_portfolio_gate(self) -> None:
        def duplicate_one_first_action(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "cooptimal-portfolio-no-duplicate"
            ):
                payload = copy.deepcopy(payload)
                duplicate = copy.deepcopy(payload["recommendations"][0])
                duplicate["rank"] = 2
                payload["recommendations"] = [
                    payload["recommendations"][0],
                    duplicate,
                ]
            return payload

        report = evaluate_turnpair_suite(FIXTURES, duplicate_one_first_action)
        self.assertFalse(report["passed"])
        metrics = report["metrics"]
        self.assertGreater(metrics["duplicate_first_action_count"], 0)
        self.assertLess(
            metrics["distinct_recommended_first_action_rate"],
            1.0,
        )
        self.assertLess(
            metrics["multi_optimal_first_action_recall_at_k"],
            1.0,
        )

    def test_each_returned_alternative_gets_independent_first_action_regret(
        self,
    ) -> None:
        def append_suboptimal_end_turn(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "cooptimal-portfolio-no-duplicate"
            ):
                payload = copy.deepcopy(payload)
                suboptimal = _verified_payload(
                    request,
                    (Action(ActionKind.END_TURN),),
                )["recommendations"][0]
                suboptimal["rank"] = 3
                payload["recommendations"].append(suboptimal)
            return payload

        report = evaluate_turnpair_suite(FIXTURES, append_suboptimal_end_turn)
        self.assertFalse(report["passed"])
        self.assertGreater(
            report["metrics"]["portfolio_regret_contract_failure_count"],
            0,
        )
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "cooptimal-portfolio-no-duplicate"
        )
        suboptimal = detail["portfolio"]["recommendations"][2]
        self.assertEqual("end_turn", suboptimal["first_action_id"])
        self.assertGreater(
            suboptimal["first_action_minimax_regret"],
            0,
        )
        self.assertFalse(suboptimal["regret_contract_passed"])

    def test_cooptimal_first_action_with_worse_continuation_fails(self) -> None:
        def worse_rank_two_continuation(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "two-equivalent-safe-first-trades"
            ):
                payload = copy.deepcopy(payload)
                worse_line = _verified_payload(
                    request,
                    (
                        Action(
                            ActionKind.ATTACK,
                            "trade-b",
                            "threat",
                        ),
                        Action(ActionKind.END_TURN),
                    ),
                )["recommendations"][0]
                worse_line["rank"] = 2
                payload["recommendations"][1] = worse_line
            return payload

        report = evaluate_turnpair_suite(FIXTURES, worse_rank_two_continuation)
        self.assertFalse(report["passed"])
        self.assertEqual(0, report["metrics"]["response_contract_failure_count"])
        self.assertGreater(
            report["metrics"]["portfolio_regret_contract_failure_count"],
            0,
        )
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "two-equivalent-safe-first-trades"
        )
        rank_two = detail["portfolio"]["recommendations"][1]
        self.assertEqual(0, rank_two["first_action_minimax_regret"])
        self.assertGreater(rank_two["returned_alternative_regret"], 0)
        self.assertTrue(rank_two["returned_line_safe"])
        self.assertFalse(rank_two["regret_contract_passed"])
        self.assertTrue(rank_two["viability_contract_passed"])

    def test_single_optimum_fixture_can_report_an_honest_backup(self) -> None:
        def append_honest_backup(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "immediate-lethal-keeps-proof-contract"
            ):
                payload = copy.deepcopy(payload)
                backup = _verified_payload(
                    request,
                    (Action(ActionKind.END_TURN),),
                )["recommendations"][0]
                backup["rank"] = 2
                payload["recommendations"].append(backup)
            return payload

        report = evaluate_turnpair_suite(FIXTURES, append_honest_backup)
        self.assertTrue(report["passed"])
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "immediate-lethal-keeps-proof-contract"
        )
        backup = detail["portfolio"]["recommendations"][1]
        self.assertIsNone(
            backup["max_allowed_returned_alternative_regret"]
        )
        self.assertGreater(
            backup["returned_alternative_regret"],
            NEAR_OPTIMAL_REGRET_MAX,
        )
        self.assertEqual("backup", backup["reported_alternative_kind"])
        self.assertTrue(backup["returned_line_safe"])
        self.assertTrue(backup["regret_contract_passed"])
        self.assertTrue(backup["viability_contract_passed"])

    def test_known_counterlethal_cannot_be_returned_as_backup(self) -> None:
        def append_counterlethal_backup(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "face-greed-loses-to-counterlethal"
            ):
                payload = copy.deepcopy(payload)
                counterlethal = _verified_payload(
                    request,
                    (
                        Action(
                            ActionKind.ATTACK,
                            "ours",
                            "opponent-hero",
                        ),
                        Action(ActionKind.END_TURN),
                    ),
                )["recommendations"][0]
                counterlethal["rank"] = 2
                payload["recommendations"].append(counterlethal)
            return payload

        report = evaluate_turnpair_suite(FIXTURES, append_counterlethal_backup)
        self.assertFalse(report["passed"])
        self.assertEqual(
            0,
            report["metrics"]["portfolio_regret_contract_failure_count"],
        )
        self.assertGreater(
            report["metrics"]["viable_portfolio_contract_failure_count"],
            0,
        )
        self.assertEqual(0, report["metrics"]["response_contract_failure_count"])
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "face-greed-loses-to-counterlethal"
        )
        backup = detail["portfolio"]["recommendations"][1]
        self.assertEqual("backup", backup["reported_alternative_kind"])
        self.assertFalse(backup["returned_line_safe"])
        self.assertFalse(backup["viability_contract_passed"])

    def test_tampered_portfolio_wire_fields_fail_independent_gate(
        self,
    ) -> None:
        mutations = {
            "verified_portfolio_regret": 1,
            "alternative_kind": "fallback",
        }
        for field, value in mutations.items():
            with self.subTest(field=field):
                def tampered(request, field_name=field, field_value=value):
                    payload = oracle_recommendation_payload(request)
                    if request.request_id.endswith(
                        "cooptimal-portfolio-no-duplicate"
                    ):
                        payload = copy.deepcopy(payload)
                        payload["recommendations"][0][
                            field_name
                        ] = field_value
                    return payload

                report = evaluate_turnpair_suite(FIXTURES, tampered)
                self.assertFalse(report["passed"])
                self.assertGreater(
                    report["metrics"][
                        "portfolio_regret_contract_failure_count"
                    ],
                    0,
                )
                detail = next(
                    item
                    for item in report["fixtures"]
                    if item["id"]
                    == "cooptimal-portfolio-no-duplicate"
                )
                errors = detail["portfolio"]["recommendations"][0][
                    "contract_errors"
                ]
                self.assertTrue(errors)

    def test_bogus_root_action_id_in_any_canonical_array_fails(self) -> None:
        fields = (
            "legal_first_action_ids",
            "generated_first_action_ids",
            "response_verified_first_action_ids",
        )
        for field in fields:
            with self.subTest(field=field):
                def bogus_id(request, field_name=field):
                    payload = oracle_recommendation_payload(request)
                    if request.request_id.endswith(
                        "cooptimal-portfolio-no-duplicate"
                    ):
                        payload = copy.deepcopy(payload)
                        counterplay = payload["coverage"]["details"][
                            "counterplay"
                        ]
                        counterplay[field_name][0] = "bogus-first-action"
                        counterplay[field_name].sort()
                    return payload

                report = evaluate_turnpair_suite(FIXTURES, bogus_id)
                self.assertFalse(report["passed"])
                self.assertGreater(
                    report["metrics"][
                        "root_action_coverage_contract_failure_count"
                    ],
                    0,
                )

    def test_complete_but_unproven_portfolio_uses_best_found(self) -> None:
        def unproven(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "cooptimal-portfolio-no-duplicate"
            ):
                payload = copy.deepcopy(payload)
                counterplay = payload["coverage"]["details"]["counterplay"]
                counterplay["portfolio_optimality_proven"] = False
                for recommendation in payload["recommendations"]:
                    recommendation["alternative_kind"] = "best_found"
            return payload

        report = evaluate_turnpair_suite(FIXTURES, unproven)
        self.assertTrue(report["passed"])
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "cooptimal-portfolio-no-duplicate"
        )
        self.assertTrue(
            all(
                item["expected_alternative_kind"] == "best_found"
                for item in detail["portfolio"]["recommendations"]
            )
        )

    def test_portfolio_optimality_proof_flag_missing_or_inconsistent_fails(
        self,
    ) -> None:
        for mutation in ("missing", "inconsistent"):
            with self.subTest(mutation=mutation):
                def malformed_proof_flag(request, mutation_name=mutation):
                    payload = oracle_recommendation_payload(request)
                    if request.request_id.endswith(
                        "cooptimal-portfolio-no-duplicate"
                    ):
                        payload = copy.deepcopy(payload)
                        counterplay = payload["coverage"]["details"][
                            "counterplay"
                        ]
                        if mutation_name == "missing":
                            del counterplay["portfolio_optimality_proven"]
                        else:
                            counterplay[
                                "response_verified_first_action_ids"
                            ].remove("end_turn")
                            counterplay[
                                "response_verified_first_action_count"
                            ] -= 1
                            counterplay["missing_first_action_ids"] = [
                                "end_turn"
                            ]
                            counterplay[
                                "root_action_coverage_complete"
                            ] = False
                    return payload

                report = evaluate_turnpair_suite(FIXTURES, malformed_proof_flag)
                self.assertFalse(report["passed"])
                self.assertGreater(
                    report["metrics"][
                        "root_action_coverage_contract_failure_count"
                    ],
                    0,
                )
                detail = next(
                    item
                    for item in report["fixtures"]
                    if item["id"]
                    == "cooptimal-portfolio-no-duplicate"
                )
                errors = detail["root_action_coverage"]["contract_errors"]
                self.assertTrue(
                    any("portfolio_optimality_proven" in error for error in errors),
                    errors,
                )

    def test_honest_incomplete_root_coverage_is_independently_rejected(
        self,
    ) -> None:
        def incomplete_root_coverage(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "cooptimal-portfolio-no-duplicate"
            ):
                payload = copy.deepcopy(payload)
                counterplay = payload["coverage"]["details"]["counterplay"]
                counterplay["response_verified_first_action_ids"].remove(
                    "end_turn"
                )
                counterplay["response_verified_first_action_count"] -= 1
                counterplay["missing_first_action_ids"] = ["end_turn"]
                counterplay["root_action_coverage_complete"] = False
                counterplay["portfolio_optimality_proven"] = False
                for recommendation in payload["recommendations"]:
                    recommendation["alternative_kind"] = "best_found"
            return payload

        report = evaluate_turnpair_suite(FIXTURES, incomplete_root_coverage)
        self.assertFalse(report["passed"])
        self.assertGreater(
            report["metrics"][
                "root_action_coverage_contract_failure_count"
            ],
            0,
        )
        failed = {
            item["name"]
            for item in report["gate"]["checks"]
            if not item["passed"]
        }
        self.assertIn(
            "root_action_coverage_contract_failure_count",
            failed,
        )
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "cooptimal-portfolio-no-duplicate"
        )
        errors = detail["root_action_coverage"]["contract_errors"]
        self.assertFalse(
            any(
                "does not match its ID array length" in error
                or "must equal legal minus response-verified" in error
                for error in errors
            ),
            errors,
        )

    def test_incomplete_coverage_maps_every_nonzero_regret_to_backup(
        self,
    ) -> None:
        def incomplete_with_backup(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "face-greed-loses-to-counterlethal"
            ):
                payload = copy.deepcopy(payload)
                counterplay = payload["coverage"]["details"]["counterplay"]
                counterplay["response_verified_first_action_ids"].remove(
                    "end_turn"
                )
                counterplay["response_verified_first_action_count"] -= 1
                counterplay["missing_first_action_ids"] = ["end_turn"]
                counterplay["root_action_coverage_complete"] = False
                counterplay["portfolio_optimality_proven"] = False
                payload["recommendations"][0][
                    "alternative_kind"
                ] = "best_found"
                backup = _verified_payload(
                    request,
                    (
                        Action(
                            ActionKind.ATTACK,
                            "ours",
                            "opponent-hero",
                        ),
                        Action(ActionKind.END_TURN),
                    ),
                )["recommendations"][0]
                backup["rank"] = 2
                payload["recommendations"].append(backup)
            return payload

        report = evaluate_turnpair_suite(FIXTURES, incomplete_with_backup)
        self.assertFalse(report["passed"])
        self.assertEqual(
            0,
            report["metrics"]["portfolio_regret_contract_failure_count"],
        )
        detail = next(
            item
            for item in report["fixtures"]
            if item["id"] == "face-greed-loses-to-counterlethal"
        )
        portfolio = detail["portfolio"]["recommendations"]
        self.assertEqual("best_found", portfolio[0]["expected_alternative_kind"])
        self.assertEqual("backup", portfolio[1]["expected_alternative_kind"])
        self.assertTrue(portfolio[1]["regret_contract_passed"])

    def test_cli_runs_real_solver_writes_report_and_uses_seed_override(self) -> None:
        suite = load_turnpair_suite(FIXTURES)
        suite["thresholds"]["min_exact_fixture_count"] = 99
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture_path = root / "failing-turnpair-suite.json"
            report_path = root / "turnpair-report.json"
            fixture_path.write_text(
                json.dumps(suite, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = main(
                    [
                        "evaluate-turnpair",
                        "--fixtures",
                        str(fixture_path),
                        "--output",
                        str(report_path),
                        "--seed",
                        "4242",
                    ]
                )
            report = json.loads(report_path.read_text(encoding="utf-8"))
            self.assertEqual(3, exit_code)
            self.assertEqual("advisor_turnpair_eval_report_v1", report["kind"])
            self.assertEqual(4242, report["seed"])
            self.assertFalse(report["passed"])

    def test_exact_response_contract_requires_explicit_fields_without_fallbacks(self) -> None:
        required_paths = (
            ("score_kind",),
            ("minimax_value",),
            ("is_safe_after_response",),
            ("is_response_verified",),
            ("response_kind",),
            ("response_scope",),
            ("opponent_response",),
            ("opponent_response", "actions"),
            ("opponent_response", "tactical_value"),
        )
        for path in required_paths:
            with self.subTest(path=".".join(path)):
                def missing_field(request, missing_path=path):
                    payload = oracle_recommendation_payload(request)
                    if request.request_id.endswith(
                        "face-greed-loses-to-counterlethal"
                    ):
                        payload = copy.deepcopy(payload)
                        owner = payload["recommendations"][0]
                        for key in missing_path[:-1]:
                            owner = owner[key]
                        del owner[missing_path[-1]]
                    return payload

                report = evaluate_turnpair_suite(FIXTURES, missing_field)
                self.assertFalse(report["passed"])
                self.assertGreater(
                    report["metrics"]["response_contract_failure_count"], 0
                )

    def test_immediate_lethal_uses_the_same_visible_response_scope(self) -> None:
        suite = load_turnpair_suite(FIXTURES)
        fixture = next(
            item
            for item in suite["fixtures"]
            if item["id"] == "immediate-lethal-keeps-proof-contract"
        )
        request = request_from_turnpair_fixture(fixture, suite["seed"])
        payload = oracle_recommendation_payload(request)
        self.assertTrue(payload["recommendations"][0]["is_proven_lethal"])
        self.assertTrue(
            all(
                item["response_scope"] == RESPONSE_SCOPE
                for item in payload["recommendations"]
            )
        )

    def test_greedy_face_safe_claim_fails_top1_regret_and_false_safe_gates(self) -> None:
        def greedy_or_oracle(request):
            if request.request_id.endswith("face-greed-loses-to-counterlethal"):
                return _verified_payload(
                    request,
                    (
                        Action(ActionKind.ATTACK, "ours", "opponent-hero"),
                        Action(ActionKind.END_TURN),
                    ),
                    safe_override=True,
                )
            return oracle_recommendation_payload(request)

        report = evaluate_turnpair_suite(FIXTURES, greedy_or_oracle)
        self.assertFalse(report["passed"])
        metrics = report["metrics"]
        self.assertLess(metrics["top1_rate"], 1.0)
        self.assertGreater(metrics["mean_minimax_regret"], 0.0)
        self.assertGreater(metrics["false_safe_count"], 0)
        self.assertGreater(metrics["false_safe_rate"], 0.0)
        failed = {item["name"] for item in report["gate"]["checks"] if not item["passed"]}
        self.assertIn("top1_rate", failed)
        self.assertIn("mean_minimax_regret", failed)
        self.assertIn("false_safe_rate", failed)

    def test_illegal_friendly_and_response_actions_reduce_separate_legality_rates(self) -> None:
        def malformed(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith("face-greed-loses-to-counterlethal"):
                payload = copy.deepcopy(payload)
                payload["recommendations"][0]["actions"][0]["target_entity_id"] = "missing"
            elif request.request_id.endswith("taunt-clear-order-preserves-survival"):
                payload = copy.deepcopy(payload)
                illegal_response = [
                    {
                        "kind": "attack",
                        "source_entity_id": "missing",
                        "target_entity_id": "friendly-hero"
                    }
                ]
                payload["recommendations"][0]["opponent_reply"] = illegal_response
                payload["recommendations"][0]["opponent_response"]["actions"] = (
                    copy.deepcopy(illegal_response)
                )
            return payload

        report = evaluate_turnpair_suite(FIXTURES, malformed)
        self.assertFalse(report["passed"])
        self.assertLess(report["metrics"]["friendly_action_legality_rate"], 1.0)
        self.assertLess(report["metrics"]["response_action_legality_rate"], 1.0)
        self.assertGreater(report["metrics"]["response_contract_failure_count"], 0)

    def test_verified_response_claim_on_unsupported_partial_fixture_is_rejected(self) -> None:
        def violates_partial_contract(request):
            payload = oracle_recommendation_payload(request)
            if request.request_id.endswith(
                "unsupported-transform-returns-best-effort-partial"
            ):
                return {
                    "status": "ok",
                    "recommendations": [
                        {
                            "rank": 1,
                            "actions": [{"kind": "end_turn"}],
                            "score_kind": TACTICAL_SCORE_KIND,
                            "opponent_reply": [],
                            "worst_case_score": 0.5,
                            "response_scope": RESPONSE_SCOPE,
                            "response_search_complete": True,
                            "response_is_proven_lethal": False,
                            "score_components": {"oracle_tactical_utility": 0.0},
                        }
                    ]
                }
            return payload

        report = evaluate_turnpair_suite(FIXTURES, violates_partial_contract)
        self.assertFalse(report["passed"])
        self.assertEqual(0, report["metrics"]["abstain_violation_count"])
        self.assertGreater(report["metrics"]["response_contract_failure_count"], 0)

    def test_no_safe_claims_have_zero_false_safe_rate_but_do_not_pass_quality(self) -> None:
        report = evaluate_turnpair_suite(
            FIXTURES,
            lambda request: {"status": "unsupported", "recommendations": []},
        )
        self.assertFalse(report["passed"])
        self.assertEqual(0, report["metrics"]["safe_claim_count"])
        self.assertEqual(0.0, report["metrics"]["false_safe_rate"])
        self.assertEqual(0.0, report["metrics"]["top1_rate"])
        self.assertGreater(report["metrics"]["mean_minimax_regret"], 0.0)


if __name__ == "__main__":
    unittest.main()
