from __future__ import annotations

import threading
import time
import unittest

import _path  # noqa: F401

from metacompanion_solver.counterplay import COUNTERPLAY_SCOPE, evaluate_counterplay
from metacompanion_solver.models import StateEvaluator
from metacompanion_solver.schemas import Action, ActionKind, Card, CardType, Effect
from metacompanion_solver.simulator import advance_to_start_of_turn, apply_action

from helpers import state


def _after_end_turn():
    game = state()
    ended = apply_action(game, Action(ActionKind.END_TURN))
    return game, ended.state


def _evaluate(ended, **overrides):
    options = {
        "evaluator": StateEvaluator(),
        "deadline": time.monotonic() + 5.0,
        "cancel_event": threading.Event(),
        "max_nodes": 2_000,
        "max_depth": 8,
    }
    options.update(overrides)
    return evaluate_counterplay(ended, **options)


class CounterplayTests(unittest.TestCase):
    def test_state_evaluator_values_one_cost_engine_by_effect_not_card_id(self) -> None:
        vanilla = state()
        engine = state()
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
        evaluator = StateEvaluator()

        self.assertEqual(
            80.0,
            evaluator.evaluate_components(engine, engine.friendly.player_id)[
                "minimax_value"
            ]
            - evaluator.evaluate_components(vanilla, vanilla.friendly.player_id)[
                "minimax_value"
            ],
        )

    def test_counterplay_scoring_expires_mana_without_mutating_replay_state(self) -> None:
        game, ended = _after_end_turn()
        original_friendly_mana = ended.friendly.mana
        evaluator = StateEvaluator()

        result = _evaluate(ended, evaluator=evaluator, max_depth=0)

        scoring = advance_to_start_of_turn(ended).state
        responder_id = scoring.active_player_id
        scoring = apply_action(scoring, Action(ActionKind.END_TURN)).state
        scoring.friendly.mana = 0
        scoring.player(responder_id).mana = 0
        self.assertAlmostEqual(
            evaluator.evaluate(scoring, game.friendly.player_id),
            result.worst_case_value,
        )
        self.assertEqual(original_friendly_mana, ended.friendly.mana)

    def test_state_evaluator_prioritizes_live_threats_without_overvaluing_blank_bodies(self) -> None:
        neutral = state()
        one_face_damage = state()
        one_face_damage.opponent.hero.current_health -= 1
        blank = state()
        blank.opponent.board.append(
            Card("blank", "BLANK", "Blank", CardType.MINION, health=1, current_health=1)
        )
        threat = state()
        threat.opponent.board.append(
            Card(
                "threat",
                "THREAT",
                "Threat",
                CardType.MINION,
                attack=4,
                health=1,
                current_health=1,
            )
        )
        evaluator = StateEvaluator()
        baseline = evaluator.evaluate_components(neutral, neutral.friendly.player_id)["minimax_value"]
        face_gain = (
            evaluator.evaluate_components(one_face_damage, one_face_damage.friendly.player_id)[
                "minimax_value"
            ]
            - baseline
        )
        blank_penalty = baseline - evaluator.evaluate_components(
            blank, blank.friendly.player_id
        )["minimax_value"]
        threat_penalty = baseline - evaluator.evaluate_components(
            threat, threat.friendly.player_id
        )["minimax_value"]

        self.assertLess(blank_penalty, face_gain)
        self.assertGreater(threat_penalty, face_gain * 10)

    def test_suicidal_end_turn_is_refuted_by_modeled_counter_lethal(self) -> None:
        game = state()
        game.friendly.hero.current_health = 5
        game.opponent.hero.current_health = 1
        game.friendly.board.append(
            Card(
                "friendly-threat",
                "FRIENDLY_THREAT",
                "Friendly threat",
                CardType.MINION,
                attack=20,
                health=20,
                current_health=20,
            )
        )
        game.opponent.board.append(
            Card(
                "counter-attacker",
                "COUNTER_ATTACKER",
                "Counter attacker",
                CardType.MINION,
                attack=5,
                health=2,
                current_health=2,
            )
        )
        ended = apply_action(game, Action(ActionKind.END_TURN)).state
        evaluator = StateEvaluator()
        self.assertGreater(evaluator.evaluate(ended, game.friendly.player_id), 0.5)

        result = _evaluate(ended, evaluator=evaluator)

        self.assertTrue(result.modeled_counter_lethal)
        self.assertTrue(result.search_complete)
        self.assertEqual(0.0, result.worst_case_value)
        self.assertEqual(ActionKind.ATTACK, result.response_actions[0].kind)
        self.assertEqual(game.friendly.hero.entity_id, result.response_actions[0].target_entity_id)
        self.assertEqual(
            "modeled_counter_lethal_scope",
            next(item.code for item in result.annotations if item.code == "modeled_counter_lethal_scope"),
        )

    def test_worst_complete_response_trades_without_claiming_lethal(self) -> None:
        game = state()
        game.friendly.board.append(
            Card(
                "fragile-threat",
                "FRAGILE_THREAT",
                "Fragile threat",
                CardType.MINION,
                attack=8,
                health=1,
                current_health=1,
            )
        )
        game.opponent.board.append(
            Card(
                "small-trader",
                "SMALL_TRADER",
                "Small trader",
                CardType.MINION,
                attack=1,
                health=2,
                current_health=2,
            )
        )
        ended = apply_action(game, Action(ActionKind.END_TURN)).state

        result = _evaluate(ended, max_depth=2)

        self.assertFalse(result.modeled_counter_lethal)
        self.assertTrue(result.search_complete)
        self.assertGreater(result.worst_case_value, 0.0)
        self.assertEqual(
            [ActionKind.ATTACK, ActionKind.END_TURN],
            [action.kind for action in result.response_actions],
        )
        self.assertEqual("fragile-threat", result.response_actions[0].target_entity_id)

        replay = advance_to_start_of_turn(ended).state
        for action in result.response_actions:
            replay = apply_action(replay, action).state
        self.assertFalse(replay.friendly.board)
        self.assertGreater(replay.friendly.hero.current_health, 0)

    def test_visible_turn_boundary_retains_approximation_scope(self) -> None:
        _, ended = _after_end_turn()

        result = _evaluate(ended, max_depth=0)
        codes = {annotation.code for annotation in result.annotations}

        self.assertEqual(COUNTERPLAY_SCOPE, result.scope)
        self.assertIn("approximate_turn_refresh", codes)
        self.assertIn("hidden_draw_identity", codes)
        self.assertIn("counterplay_scope", codes)
        self.assertEqual(ActionKind.END_TURN, result.response_actions[-1].kind)

    def test_cancellation_node_depth_and_deadline_limits_are_explicit(self) -> None:
        game = state()
        game.opponent.board.append(
            Card(
                "bounded-attacker",
                "BOUNDED_ATTACKER",
                "Bounded attacker",
                CardType.MINION,
                attack=1,
                health=2,
                current_health=2,
            )
        )
        ended = apply_action(game, Action(ActionKind.END_TURN)).state

        cancelled = threading.Event()
        cancelled.set()
        cancelled_result = _evaluate(ended, cancel_event=cancelled)
        self.assertFalse(cancelled_result.search_complete)
        self.assertEqual(0, cancelled_result.nodes_expanded)
        self.assertEqual("cancelled", cancelled_result.stop_reason)
        self.assertIn("counterplay_cancelled", {item.code for item in cancelled_result.annotations})

        node_limited = _evaluate(ended, max_nodes=1)
        self.assertFalse(node_limited.search_complete)
        self.assertLessEqual(node_limited.nodes_expanded, 1)
        self.assertEqual("node_limit", node_limited.stop_reason)
        self.assertIn("counterplay_node_limit", {item.code for item in node_limited.annotations})

        mid_line_limited = _evaluate(ended, max_nodes=2)
        self.assertFalse(mid_line_limited.search_complete)
        self.assertEqual(2, mid_line_limited.nodes_expanded)
        self.assertEqual("node_limit", mid_line_limited.stop_reason)
        self.assertTrue(mid_line_limited.response_actions)
        self.assertEqual(ActionKind.END_TURN, mid_line_limited.response_actions[-1].kind)

        depth_limited = _evaluate(ended, max_depth=0)
        self.assertFalse(depth_limited.search_complete)
        self.assertEqual(0, depth_limited.searched_depth)
        self.assertEqual("depth_limit", depth_limited.stop_reason)
        self.assertIn("counterplay_depth_limit", {item.code for item in depth_limited.annotations})

        deadline_limited = _evaluate(ended, deadline=time.monotonic() - 1.0)
        self.assertFalse(deadline_limited.search_complete)
        self.assertEqual(0, deadline_limited.nodes_expanded)
        self.assertEqual("deadline", deadline_limited.stop_reason)
        self.assertIn(
            "counterplay_deadline_reached",
            {item.code for item in deadline_limited.annotations},
        )

    def test_search_is_deterministic_and_reuses_transpositions(self) -> None:
        game = state()
        for index in range(3):
            game.opponent.board.append(
                Card(
                    f"attacker-{index}",
                    f"ATTACKER_{index}",
                    f"Attacker {index}",
                    CardType.MINION,
                    attack=1,
                    health=2,
                    current_health=2,
                )
            )
        ended = apply_action(game, Action(ActionKind.END_TURN)).state

        first = _evaluate(ended, max_nodes=2_000, max_depth=3)
        second = _evaluate(ended, max_nodes=2_000, max_depth=3)

        self.assertTrue(first.search_complete)
        self.assertFalse(first.modeled_counter_lethal)
        self.assertGreater(first.transposition_hits, 0)
        self.assertEqual(first.to_dict(), second.to_dict())
        self.assertEqual(3, first.searched_depth)
        self.assertEqual(ActionKind.END_TURN, first.response_actions[-1].kind)


if __name__ == "__main__":
    unittest.main()
