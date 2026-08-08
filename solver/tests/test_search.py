from __future__ import annotations

import threading
import unittest

import _path  # noqa: F401

from metacompanion_solver.models import StateEvaluator
from metacompanion_solver.schemas import (
    Action,
    ActionKind,
    BeliefCandidate,
    BeliefState,
    Card,
    CardType,
)
from metacompanion_solver.search import PuctTurnSearcher, SearchLimits, _LineStats
from metacompanion_solver.simulator import apply_action

from helpers import state


class _AdversarialRootPrior:
    def probabilities(self, _state, actions):
        raw = {}
        for action in actions:
            if action.kind == ActionKind.END_TURN:
                raw[action.action_id] = 1_000_000.0
            elif action.source_entity_id == "a-card":
                raw[action.action_id] = 1_000.0
            else:
                raw[action.action_id] = 0.000001
        total = sum(raw.values()) or 1.0
        return {key: value / total for key, value in raw.items()}


class SearchTests(unittest.TestCase):
    @staticmethod
    def _three_root_portfolio_state():
        game = state()
        game.friendly.deck_size = 0
        game.opponent.deck_size = 0
        game.friendly.mana = 1
        game.friendly.max_mana = 1
        game.friendly.hand.extend(
            [
                Card(
                    "a-card",
                    "A_CARD",
                    "A card",
                    CardType.MINION,
                    cost=1,
                    attack=1,
                    health=1,
                    current_health=1,
                ),
                Card(
                    "z-card",
                    "Z_CARD",
                    "Z card",
                    CardType.MINION,
                    cost=1,
                    attack=1,
                    health=1,
                    current_health=1,
                ),
            ]
        )
        return game

    def test_search_returns_ranked_legal_complete_lines(self) -> None:
        game = state()
        game.friendly.hand.extend(
            [
                Card("m1", "M1", "One", CardType.MINION, cost=1, attack=1, health=1, current_health=1),
                Card("m2", "M2", "Two", CardType.MINION, cost=2, attack=2, health=2, current_health=2),
            ]
        )
        limits = SearchLimits(100, 150, 12, 3)
        result = PuctTurnSearcher().search("r1", game, limits)
        self.assertGreaterEqual(len(result.recommendations), 1)
        self.assertLessEqual(len(result.recommendations), 3)
        self.assertEqual(sorted(r.rank for r in result.recommendations), list(range(1, len(result.recommendations) + 1)))
        for recommendation in result.recommendations:
            current = game
            ended = False
            for action in recommendation.actions:
                outcome = apply_action(current, action)
                current = outcome.state
                ended = outcome.ended_turn or current.opponent.hero.current_health <= 0
            self.assertTrue(ended)

    def test_lethal_attack_has_full_value(self) -> None:
        game = state()
        game.opponent.hero.current_health = 3
        game.friendly.board.append(
            Card(
                "lethal", "L", "Lethal", CardType.MINION, attack=3, health=2, current_health=2,
                can_attack=True, attacks_remaining=1,
            )
        )
        result = PuctTurnSearcher().search("r2", game, SearchLimits(100, 100, 8, 3))
        self.assertEqual("attack", result.recommendations[0].actions[0].kind.value)
        self.assertEqual(1.0, result.recommendations[0].expected_win_probability)

    def test_pre_cancelled_search_returns_baseline(self) -> None:
        event = threading.Event()
        event.set()
        result = PuctTurnSearcher().search("r3", state(), SearchLimits(100, 100, 8, 3), event)
        self.assertEqual("cancelled", result.status)
        self.assertEqual(0, result.iterations)
        self.assertEqual("end_turn", result.recommendations[0].actions[-1].kind.value)

    def test_belief_hook_reduces_value(self) -> None:
        clean = state()
        risky = state()
        risky.belief = BeliefState(
            opponent_hand_slots=3,
            candidates=(BeliefCandidate("BOARD_CLEAR", 1.0, 100.0),),
            confidence=1.0,
        )
        evaluator = StateEvaluator()
        self.assertLess(
            evaluator.evaluate(risky, risky.perspective_player_id),
            evaluator.evaluate(clean, clean.perspective_player_id),
        )

    def test_visible_combat_planner_proves_shortest_modeled_lethal(self) -> None:
        game = state()
        game.opponent.hero.current_health = 5
        game.opponent.board.append(
            Card(
                "taunt", "T", "Taunt", CardType.MINION, attack=0, health=1,
                current_health=1, taunt=True,
            )
        )
        game.friendly.board.extend(
            [
                Card(
                    "a-small", "S", "Small", CardType.MINION, attack=1, health=2,
                    current_health=2, can_attack=True, attacks_remaining=1,
                ),
                Card(
                    "b-big", "B", "Big", CardType.MINION, attack=5, health=5,
                    current_health=5, can_attack=True, attacks_remaining=1,
                ),
            ]
        )
        results = [
            PuctTurnSearcher().search("lethal", game, SearchLimits(100, 100, 4, 3))
            for _ in range(3)
        ]
        expected = ["attack:a-small:taunt", "attack:b-big:opponent-hero"]
        for result in results:
            self.assertEqual("partial", result.status)
            self.assertFalse(result.coverage["exact"])
            self.assertEqual("visible-combat-v2", result.coverage["rules_model"])
            self.assertEqual("counterplay-turnpair-v1", result.coverage["planner_model"])
            top = result.recommendations[0]
            self.assertEqual(expected, [action.action_id for action in top.actions])
            self.assertTrue(top.is_proven_lethal)
            self.assertEqual("modeled_lethal", top.proof_kind)
            self.assertEqual("visible_generic_v2", top.proof_scope)
            wire = top.to_dict()
            self.assertTrue(wire["is_proven_lethal"])
            self.assertEqual("counterplay_tactical_state_value", wire["score_kind"])
            self.assertEqual("counterplay-turnpair-v1", result.to_dict()["model_version"])
            planner = result.coverage["details"]["planner"]
            self.assertTrue(planner["modeled_lethal_found"])
            self.assertGreater(planner["lethal_nodes_expanded"], 0)

    def test_search_enforces_tree_depth_and_checks_transpositions(self) -> None:
        game = state()
        game.friendly.board.append(
            Card(
                "multi", "M", "Multi", CardType.MINION, attack=1, health=3,
                current_health=3, can_attack=True, attacks_remaining=6,
            )
        )
        result = PuctTurnSearcher().search("depth", game, SearchLimits(250, 300, 2, 10))
        for recommendation in result.recommendations:
            non_end_actions = [
                action for action in recommendation.actions if action.kind != ActionKind.END_TURN
            ]
            self.assertLessEqual(len(non_end_actions), 2)

        transposition_game = state()
        for index in range(4):
            transposition_game.friendly.board.append(
                Card(
                    f"m{index}", f"M{index}", f"M{index}", CardType.MINION,
                    attack=1, health=2, current_health=2, can_attack=True,
                    attacks_remaining=1,
                )
            )
        transposition_result = PuctTurnSearcher().search(
            "transpositions",
            transposition_game,
            SearchLimits(500, 1000, 4, 3),
        )
        planner = transposition_result.coverage["details"]["planner"]
        self.assertGreater(planner["lethal_transposition_hits"], 0)

    def test_top_k_prefers_distinct_first_actions(self) -> None:
        game = state()
        for index in range(4):
            game.friendly.board.append(
                Card(
                    f"m{index}", f"M{index}", f"M{index}", CardType.MINION,
                    attack=1, health=2, current_health=2, can_attack=True,
                    attacks_remaining=1,
                )
            )
        result = PuctTurnSearcher().search("diverse", game, SearchLimits(500, 2000, 8, 3))
        first_actions = [
            next(
                (
                    action.action_id
                    for action in item.actions
                    if action.kind != ActionKind.END_TURN
                ),
                "end_turn",
            )
            for item in result.recommendations
        ]
        self.assertEqual(len(first_actions), len(set(first_actions)))

    def test_multiple_cooptimal_roots_are_not_padded_with_inferior_backup(self) -> None:
        game = state()
        game.opponent.hero.current_health = 2
        game.friendly.deck_size = 0
        game.opponent.deck_size = 0
        game.friendly.board.extend(
            [
                Card(
                    "portfolio-a", "PORTFOLIO_A", "A", CardType.MINION,
                    attack=2, health=1, current_health=1, can_attack=True,
                    attacks_remaining=1,
                ),
                Card(
                    "portfolio-b", "PORTFOLIO_B", "B", CardType.MINION,
                    attack=2, health=1, current_health=1, can_attack=True,
                    attacks_remaining=1,
                ),
            ]
        )

        result = PuctTurnSearcher().search(
            "cooptimal-no-padding",
            game,
            SearchLimits(500, 100, 4, 3),
        )

        first_actions = [item.actions[0].action_id for item in result.recommendations]
        self.assertEqual(
            {
                "attack:portfolio-a:opponent-hero",
                "attack:portfolio-b:opponent-hero",
            },
            set(first_actions),
        )
        self.assertEqual(2, len(first_actions))
        self.assertTrue(
            all(item.alternative_kind == "best_found" for item in result.recommendations)
        )
        self.assertFalse(
            result.coverage["details"]["counterplay"][
                "portfolio_optimality_proven"
            ]
        )

    def test_root_coverage_does_not_claim_unexhausted_continuations_are_optimal(self) -> None:
        game = state()
        game.friendly.deck_size = 0
        game.opponent.deck_size = 0
        game.friendly.board.extend(
            [
                Card(
                    "a", "A", "A", CardType.MINION,
                    attack=1, health=1, current_health=1, can_attack=True,
                    attacks_remaining=1,
                ),
                Card(
                    "b", "B", "B", CardType.MINION,
                    attack=1, health=1, current_health=1, can_attack=True,
                    attacks_remaining=1,
                ),
            ]
        )

        result = PuctTurnSearcher(prior=_AdversarialRootPrior()).search(
            "bounded-continuations",
            game,
            SearchLimits(2000, 3, 12, 3),
        )

        counterplay = result.coverage["details"]["counterplay"]
        self.assertTrue(counterplay["root_action_coverage_complete"])
        self.assertFalse(counterplay["portfolio_optimality_proven"])
        self.assertTrue(result.recommendations)
        self.assertTrue(
            all(
                item.alternative_kind in {"best_found", "backup"}
                for item in result.recommendations
            )
        )
        self.assertTrue(
            all(
                item.alternative_kind not in {"co_optimal", "near_optimal"}
                for item in result.recommendations
            )
        )

    def test_low_budget_reports_unseeded_legal_root_action(self) -> None:
        result = PuctTurnSearcher(prior=_AdversarialRootPrior()).search(
            "root-coverage-low-budget",
            self._three_root_portfolio_state(),
            SearchLimits(1000, 2, 3, 3),
        )

        counterplay = result.coverage["details"]["counterplay"]
        self.assertEqual(3, counterplay["legal_first_action_count"])
        self.assertEqual(2, counterplay["generated_first_action_count"])
        self.assertEqual(1, counterplay["unassessed_first_action_count"])
        self.assertEqual(
            ["play_card:z-card::position=1"],
            counterplay["missing_generated_first_action_ids"],
        )
        self.assertIn(
            "play_card:z-card::position=1",
            counterplay["missing_first_action_ids"],
        )
        self.assertFalse(counterplay["root_action_coverage_complete"])

    def test_root_seeding_and_response_verification_cover_every_legal_action(self) -> None:
        result = PuctTurnSearcher(prior=_AdversarialRootPrior()).search(
            "root-coverage-complete",
            self._three_root_portfolio_state(),
            SearchLimits(1000, 3, 3, 3),
        )

        counterplay = result.coverage["details"]["counterplay"]
        self.assertEqual(3, counterplay["legal_first_action_count"])
        self.assertEqual(3, counterplay["generated_first_action_count"])
        self.assertEqual(3, counterplay["response_verified_first_action_count"])
        self.assertEqual([], counterplay["missing_first_action_ids"])
        self.assertEqual(1.0, counterplay["root_action_generation_coverage_rate"])
        self.assertEqual(1.0, counterplay["root_action_response_coverage_rate"])
        self.assertTrue(counterplay["root_action_coverage_complete"])
        self.assertTrue(counterplay["search_complete"])

    def test_recommendations_keep_only_the_best_verified_line_per_root(self) -> None:
        game = state()
        shared_first = Action(ActionKind.PLAY_CARD, "shared-root")
        better_shared = _LineStats(
            actions=(
                shared_first,
                Action(ActionKind.HERO_POWER, "follow-up"),
                Action(ActionKind.END_TURN),
            ),
            visits=5,
            value_sum=2.5,
            terminal_state_key="shared-better",
            worst_case_score=0.5,
            response_scope="visible_generic_turnpair_v1",
            response_search_complete=True,
            response_evaluated=True,
            score_components={"minimax_value": 1000.0},
        )
        worse_shared = _LineStats(
            actions=(shared_first, Action(ActionKind.END_TURN)),
            visits=20,
            value_sum=10.0,
            terminal_state_key="shared-worse",
            worst_case_score=0.5,
            response_scope="visible_generic_turnpair_v1",
            response_search_complete=True,
            response_evaluated=True,
            score_components={"minimax_value": 900.0},
        )
        other_root = _LineStats(
            actions=(
                Action(ActionKind.PLAY_CARD, "other-root"),
                Action(ActionKind.END_TURN),
            ),
            visits=4,
            value_sum=2.0,
            terminal_state_key="other",
            worst_case_score=0.5,
            response_scope="visible_generic_turnpair_v1",
            response_search_complete=True,
            response_evaluated=True,
            score_components={"minimax_value": 950.0},
        )

        recommendations = PuctTurnSearcher()._recommendations(
            game,
            {
                ("shared-better",): better_shared,
                ("shared-worse",): worse_shared,
                ("other",): other_root,
            },
            top_k=3,
            root_action_coverage_complete=True,
        )

        self.assertEqual(2, len(recommendations))
        self.assertEqual(
            2,
            len({item.actions[0].action_id for item in recommendations}),
        )
        retained_shared = next(
            item
            for item in recommendations
            if item.actions[0].source_entity_id == "shared-root"
        )
        self.assertEqual("follow-up", retained_shared.actions[1].source_entity_id)

    def test_recommendation_wire_classifies_verified_portfolio_regret(self) -> None:
        game = state()

        def verified_line(source: str, minimax_value: float) -> _LineStats:
            return _LineStats(
                actions=(
                    Action(ActionKind.PLAY_CARD, source),
                    Action(ActionKind.END_TURN),
                ),
                visits=5,
                value_sum=2.5,
                terminal_state_key=source,
                worst_case_score=0.5,
                response_scope="visible_generic_turnpair_v1",
                response_search_complete=True,
                response_evaluated=True,
                score_components={"minimax_value": minimax_value},
            )

        lines = {
            ("best",): verified_line("best", 1000.0),
            ("near",): verified_line("near", 950.0),
            ("backup",): verified_line("backup", 800.0),
        }
        complete = PuctTurnSearcher()._recommendations(
            game,
            lines,
            top_k=3,
            root_action_coverage_complete=True,
            portfolio_optimality_proven=True,
        )
        self.assertEqual(
            ["co_optimal", "near_optimal", "backup"],
            [item.alternative_kind for item in complete],
        )
        self.assertEqual(
            [0.0, 50.0, 200.0],
            [item.verified_portfolio_regret for item in complete],
        )
        self.assertEqual("co_optimal", complete[0].to_dict()["alternative_kind"])
        self.assertEqual(0.0, complete[0].to_dict()["verified_portfolio_regret"])

        incomplete = PuctTurnSearcher()._recommendations(
            game,
            lines,
            top_k=3,
            root_action_coverage_complete=False,
        )
        self.assertEqual("best_found", incomplete[0].alternative_kind)
        self.assertEqual("backup", incomplete[1].alternative_kind)

    def test_incomplete_counterplay_never_pads_verified_recommendations(self) -> None:
        game = state()
        complete_trade = _LineStats(
            actions=(
                Action(ActionKind.ATTACK, "z-big", "threat"),
                Action(ActionKind.END_TURN),
            ),
            visits=5,
            value_sum=0.5,
            terminal_state_key="complete-trade",
            worst_case_score=0.1,
            response_scope="visible_generic_turnpair_v1",
            response_search_complete=True,
            response_evaluated=True,
            score_components={"minimax_value": -2352.0},
        )
        complete_backup = _LineStats(
            actions=(
                Action(ActionKind.ATTACK, "a-small", "bait"),
                Action(ActionKind.END_TURN),
            ),
            visits=4,
            value_sum=0.4,
            terminal_state_key="complete-backup",
            worst_case_score=0.09,
            response_scope="visible_generic_turnpair_v1",
            response_search_complete=True,
            response_evaluated=True,
            score_components={"minimax_value": -2442.0},
        )
        deadline_limited = _LineStats(
            actions=(
                Action(ActionKind.ATTACK, "a-small", "opponent-hero"),
                Action(ActionKind.ATTACK, "b-small", "opponent-hero"),
                Action(ActionKind.END_TURN),
            ),
            visits=100,
            value_sum=99.0,
            terminal_state_key="deadline-limited",
            worst_case_score=0.99,
            response_scope="visible_generic_turnpair_v1",
            response_search_complete=False,
            response_evaluated=True,
            score_components={"minimax_value": -2352.0},
        )

        recommendations = PuctTurnSearcher()._recommendations(
            game,
            {
                ("complete-trade",): complete_trade,
                ("complete-backup",): complete_backup,
                ("deadline-limited",): deadline_limited,
            },
            top_k=3,
        )

        self.assertEqual(2, len(recommendations))
        self.assertEqual(
            {"attack:z-big:threat", "attack:a-small:bait"},
            {item.actions[0].action_id for item in recommendations},
        )
        self.assertTrue(all(item.response_search_complete for item in recommendations))
        self.assertTrue(all(item.to_dict()["is_response_verified"] for item in recommendations))

    def test_unverified_fallback_is_limited_to_one_recommendation(self) -> None:
        game = state()
        incomplete_lines = {
            (f"line-{index}",): _LineStats(
                actions=(Action(ActionKind.END_TURN),),
                visits=3 - index,
                value_sum=float(3 - index),
                terminal_state_key=f"line-{index}",
                response_scope="visible_generic_turnpair_v1",
                response_search_complete=False,
                response_evaluated=True,
            )
            for index in range(3)
        }

        recommendations = PuctTurnSearcher()._recommendations(
            game,
            incomplete_lines,
            top_k=3,
        )

        self.assertEqual(1, len(recommendations))
        self.assertFalse(recommendations[0].response_search_complete)
        self.assertFalse(recommendations[0].to_dict()["is_response_verified"])
        self.assertEqual("fallback", recommendations[0].alternative_kind)
        self.assertIsNone(recommendations[0].verified_portfolio_regret)
        self.assertEqual("fallback", recommendations[0].to_dict()["alternative_kind"])
        self.assertIsNone(
            recommendations[0].to_dict()["verified_portfolio_regret"]
        )

    def test_counterplay_refutes_face_attack_and_prefers_the_trade(self) -> None:
        game = state()
        game.friendly.hero.current_health = 3
        game.friendly.board.append(
            Card(
                "friendly-3-3",
                "FRIENDLY_3_3",
                "Friendly 3/3",
                CardType.MINION,
                attack=3,
                health=3,
                current_health=3,
                can_attack=True,
                attacks_remaining=1,
            )
        )
        game.opponent.board.append(
            Card(
                "opponent-3-1",
                "OPPONENT_3_1",
                "Opponent 3/1",
                CardType.MINION,
                attack=3,
                health=1,
                current_health=1,
            )
        )

        result = PuctTurnSearcher().search(
            "counterplay-regression",
            game,
            SearchLimits(300, 400, 4, 3),
        )

        top = result.recommendations[0]
        self.assertEqual("opponent-3-1", top.actions[0].target_entity_id)
        self.assertFalse(top.response_is_proven_lethal)
        self.assertEqual("visible_generic_turnpair_v1", top.response_scope)
        self.assertTrue(
            all(not item.response_is_proven_lethal for item in result.recommendations)
        )
        self.assertGreater(
            result.coverage["details"]["counterplay"]["modeled_counter_lethal_count"],
            0,
        )

    def test_counterplay_covers_wide_first_actions_and_withholds_known_losses(self) -> None:
        game = state()
        game.friendly.hero.current_health = 3
        game.friendly.deck_size = 0
        game.opponent.deck_size = 0
        game.friendly.board.extend(
            [
                Card(
                    "a-small", "A_SMALL", "Small A", CardType.MINION,
                    attack=1, health=1, current_health=1, can_attack=True,
                    attacks_remaining=1,
                ),
                Card(
                    "b-small", "B_SMALL", "Small B", CardType.MINION,
                    attack=1, health=1, current_health=1, can_attack=True,
                    attacks_remaining=1,
                ),
                Card(
                    "z-big", "Z_BIG", "Big", CardType.MINION,
                    attack=3, health=3, current_health=3, can_attack=True,
                    attacks_remaining=1,
                ),
            ]
        )
        game.opponent.board.extend(
            [
                Card(
                    "threat", "THREAT", "Threat", CardType.MINION,
                    attack=3, health=3, current_health=3,
                ),
                Card(
                    "bait", "BAIT", "Bait", CardType.MINION,
                    attack=0, health=1, current_health=1,
                ),
            ]
        )

        result = PuctTurnSearcher().search(
            "wide-first-action-counterlethal",
            game,
            SearchLimits(250, 5000, 12, 3),
        )

        optimal_first_actions = {
            "attack:a-small:opponent-hero",
            "attack:b-small:opponent-hero",
            "attack:z-big:threat",
        }
        self.assertEqual(
            optimal_first_actions,
            {item.actions[0].action_id for item in result.recommendations},
        )
        self.assertEqual(3, len(result.recommendations))
        self.assertTrue(
            all(item.verified_portfolio_regret == 0 for item in result.recommendations)
        )
        self.assertGreater(result.iterations, 0)
        self.assertTrue(result.recommendations[0].response_search_complete)
        self.assertTrue(
            all(not item.response_is_proven_lethal for item in result.recommendations)
        )
        counterplay = result.coverage["details"]["counterplay"]
        self.assertGreater(counterplay["generated_first_action_count"], 6)
        self.assertEqual(
            counterplay["generated_first_action_count"],
            counterplay["assessed_first_action_count"],
        )
        self.assertEqual(0, counterplay["unassessed_first_action_count"])

    def test_counterplay_ranking_is_repeatable_for_a_fixed_seed(self) -> None:
        game = state()
        game.friendly.hero.current_health = 6
        game.friendly.board.append(
            Card(
                "friendly-attacker",
                "FRIENDLY_ATTACKER",
                "Friendly attacker",
                CardType.MINION,
                attack=3,
                health=3,
                current_health=3,
                can_attack=True,
                attacks_remaining=1,
            )
        )
        for index in range(2):
            game.opponent.board.append(
                Card(
                    f"opponent-{index}",
                    f"OPPONENT_{index}",
                    f"Opponent {index}",
                    CardType.MINION,
                    attack=2,
                    health=2,
                    current_health=2,
                )
            )
        limits = SearchLimits(500, 100, 4, 3)

        signatures = []
        for _ in range(3):
            result = PuctTurnSearcher().search("repeatable", game, limits)
            signatures.append(
                (
                    [
                        (
                            [action.action_id for action in item.actions],
                            [action.action_id for action in item.opponent_reply],
                            item.worst_case_score,
                            item.response_search_complete,
                            item.response_is_proven_lethal,
                        )
                        for item in result.recommendations
                    ],
                    result.coverage["details"]["counterplay"],
                )
            )
        self.assertEqual(signatures[0], signatures[1])
        self.assertEqual(signatures[0], signatures[2])
        self.assertEqual(7, signatures[0][1]["seed"])

    def test_counterplay_unsupported_state_degrades_with_explicit_scope(self) -> None:
        game = state()
        game.opponent.board.append(
            Card(
                "unsupported-responder",
                "UNSUPPORTED_RESPONDER",
                "Unsupported responder",
                CardType.MINION,
                attack=1,
                health=2,
                current_health=2,
                effect_coverage="unsupported",
                unsupported_effects=("end_of_turn_trigger",),
            )
        )

        result = PuctTurnSearcher().search(
            "counterplay-unsupported",
            game,
            SearchLimits(200, 50, 3, 1),
        )

        self.assertEqual("partial", result.status)
        self.assertFalse(result.coverage["exact"])
        recommendation = result.recommendations[0]
        self.assertEqual("visible_generic_turnpair_v1", recommendation.response_scope)
        self.assertIsNotNone(recommendation.worst_case_score)
        codes = {annotation.code for annotation in recommendation.annotations}
        self.assertIn("unsupported_card_text", codes)
        self.assertIn("unsupported_card_mechanic", codes)
        self.assertIn("approximate_turn_refresh", codes)
        self.assertIn("hidden_draw_identity", codes)
        counterplay = result.coverage["details"]["counterplay"]
        self.assertEqual("counterplay-turnpair-v1", counterplay["planner_model"])
        self.assertEqual("counterplay_tactical_state_value", counterplay["score_kind"])

    def test_playable_unsupported_card_returns_legal_unverified_best_effort_route(self) -> None:
        game = state()
        game.friendly.mana = 1
        game.friendly.max_mana = 1
        game.friendly.hand.append(
            Card(
                "unsupported-choice",
                "UNSUPPORTED_CHOICE",
                "Unsupported choice",
                CardType.SPELL,
                cost=1,
                playable=True,
                effect_coverage="unsupported",
                unsupported_effects=("discover",),
            )
        )
        game.friendly.board.append(
            Card(
                "modeled-attacker",
                "MODELED_ATTACKER",
                "Modeled attacker",
                CardType.MINION,
                attack=2,
                health=2,
                current_health=2,
                can_attack=True,
                attacks_remaining=1,
            )
        )

        result = PuctTurnSearcher().search(
            "playable-unsupported-best-effort",
            game,
            SearchLimits(200, 100, 5, 3),
        )

        self.assertEqual("partial", result.status)
        self.assertFalse(result.coverage["exact"])
        self.assertEqual("", result.coverage["exact_scope"])
        self.assertGreaterEqual(len(result.recommendations), 1)
        for recommendation in result.recommendations:
            current = game
            ended = False
            for action in recommendation.actions:
                outcome = apply_action(current, action)
                current = outcome.state
                ended = outcome.ended_turn or current.opponent.hero.current_health <= 0
            self.assertTrue(ended)

            wire = recommendation.to_dict()
            self.assertFalse(wire["is_proven_lethal"])
            self.assertFalse(wire["is_response_verified"])
            self.assertFalse(wire["response_search_complete"])
            self.assertIsNone(wire["is_safe_after_response"])
            self.assertIsNone(wire["verified_portfolio_regret"])
            self.assertEqual("fallback", wire["alternative_kind"])
            self.assertEqual("", wire["proof_kind"])
            self.assertEqual("", wire["proof_scope"])
            self.assertIn(
                "approximate_playable_unsupported_rule",
                {item["code"] for item in wire["annotations"]},
            )

        counterplay = result.coverage["details"]["counterplay"]
        self.assertFalse(counterplay["portfolio_optimality_proven"])
        self.assertFalse(counterplay["root_action_coverage_complete"])
        self.assertFalse(counterplay["search_complete"])
        self.assertEqual(0, counterplay["response_verified_first_action_count"])
        planner = result.coverage["details"]["planner"]
        self.assertFalse(planner["abstained"])
        self.assertTrue(planner["best_effort_due_to_approximation"])

    def test_low_budget_does_not_treat_incomplete_counterplay_as_safe(self) -> None:
        game = state()
        game.friendly.hero.current_health = 3
        game.opponent.deck_size = 0
        game.friendly.board.append(
            Card(
                "budget-friendly",
                "BUDGET_FRIENDLY",
                "Budget friendly",
                CardType.MINION,
                attack=3,
                health=3,
                current_health=3,
                can_attack=True,
                attacks_remaining=1,
            )
        )
        game.opponent.board.append(
            Card(
                "budget-opponent",
                "BUDGET_OPPONENT",
                "Budget opponent",
                CardType.MINION,
                attack=3,
                health=1,
                current_health=1,
            )
        )

        result = PuctTurnSearcher().search(
            "counterplay-low-budget",
            game,
            SearchLimits(500, 3, 2, 2),
        )

        top = result.recommendations[0]
        self.assertEqual("budget-opponent", top.actions[0].target_entity_id)
        self.assertTrue(top.response_search_complete)
        self.assertFalse(top.response_is_proven_lethal)
        for recommendation in result.recommendations:
            self.assertTrue(recommendation.response_scope)
            if not recommendation.response_search_complete:
                self.assertFalse(recommendation.response_is_proven_lethal)
                self.assertIn(
                    "counterplay_node_limit",
                    {item.code for item in recommendation.annotations},
                )
        counterplay = result.coverage["details"]["counterplay"]
        self.assertEqual(1, counterplay["per_line_node_budget"])
        self.assertEqual(
            counterplay["shortlisted_line_count"],
            counterplay["assessed_line_count"],
        )


if __name__ == "__main__":
    unittest.main()
