from __future__ import annotations

import json
import unittest
from fractions import Fraction
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.card_rules import (
    StructuredCardRuleBundle,
    default_structured_card_rule_path,
)
from metacompanion_solver.schemas import SolveRequest
from metacompanion_solver.turnpair_evaluation import (
    TurnPairEvaluationError,
    _apply_oracle_action,
    _apply_oracle_action_outcomes,
    enumerate_oracle_actions,
)


ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "solver" / "fixtures" / "oracle-visible-chance-v1.json"


def _outcome_summary(outcome: object) -> dict[str, object]:
    state = outcome.state
    probability = outcome.probability
    return {
        "probability": {
            "numerator": probability.numerator,
            "denominator": probability.denominator,
        },
        "friendly_mana": state.friendly.mana,
        "friendly_hand_count": len(state.friendly.hand),
        "opponent_hero_health": state.opponent.hero.current_health,
        "opponent_minion_health": {
            card.entity_id: card.current_health
            for card in sorted(state.opponent.board, key=lambda item: item.entity_id)
        },
    }


class VisibleChanceParityTests(unittest.TestCase):
    def test_shared_exact_outcome_fixtures(self) -> None:
        suite = json.loads(FIXTURES.read_text(encoding="utf-8"))
        self.assertEqual(1, suite["schema_version"])
        self.assertEqual("oracle-visible-chance-v1", suite["suite_id"])
        self.assertGreaterEqual(len(suite["fixtures"]), 2)
        bundle = StructuredCardRuleBundle.load(default_structured_card_rule_path())

        for fixture in suite["fixtures"]:
            with self.subTest(fixture=fixture["id"]):
                request = SolveRequest.from_dict(fixture["request"])
                assessment = bundle.apply(request.state)
                self.assertEqual(
                    [fixture["expected"]["matched_rule_id"]],
                    [item["rule_id"] for item in assessment["matched"]],
                )
                action = next(
                    item
                    for item in enumerate_oracle_actions(request.state)
                    if item.action_id == fixture["action_id"]
                )
                outcomes = _apply_oracle_action_outcomes(request.state, action)
                self.assertEqual(
                    Fraction(1, 1),
                    sum((item.probability for item in outcomes), Fraction(0, 1)),
                )
                actual = sorted(
                    (_outcome_summary(item) for item in outcomes),
                    key=lambda item: json.dumps(item, sort_keys=True),
                )
                expected = sorted(
                    fixture["expected"]["outcomes"],
                    key=lambda item: json.dumps(item, sort_keys=True),
                )
                self.assertEqual(expected, actual)
                if fixture["expected"]["deterministic_transition_rejected"]:
                    with self.assertRaises(TurnPairEvaluationError):
                        _apply_oracle_action(request.state, action)


if __name__ == "__main__":
    unittest.main()
