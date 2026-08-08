from __future__ import annotations

import json
import hashlib
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import _path  # noqa: F401

from metacompanion_solver.arena_import import parse_arena_drafts, write_jsonl
from metacompanion_solver.logging_store import sanitize_for_training
from metacompanion_solver.models import (
    HeuristicActionPrior,
    load_mode_card_priors,
    load_normalized_card_priors,
)
from metacompanion_solver.schemas import Card, CardType
from metacompanion_solver.simulator import enumerate_legal_actions

from helpers import state
from metacompanion_solver.training import train_file, train_frequency


ARENA_XML = """<?xml version="1.0" encoding="utf-8"?>
<ArenaLastDrafts>
  <Draft Player="private-player" DeckId="private-deck" StartTime="2026-01-01T00:00:00Z">
    <Pick>
      <Slot>4</Slot><Picked>CARD_C</Picked>
      <Choice>CARD_A</Choice><Choice>CARD_B</Choice><Choice>CARD_C</Choice>
      <TimeOnChoice>1234</TimeOnChoice><ArenasmithAvailable>true</ArenasmithAvailable>
      <ArenasmithScores>
        <ArenasmithScore Card="CARD_A" Score="10.5" />
        <ArenasmithScore Card="CARD_C" Score="50" />
      </ArenasmithScores>
      <PickedCards>OLD_CARD</PickedCards>
      <Packages><Package KeyCard="CARD_C"><Card>OLD_CARD</Card></Package></Packages>
    </Pick>
  </Draft>
</ArenaLastDrafts>
"""


class DataToolTests(unittest.TestCase):
    def test_arena_import_is_anonymized_and_trainable(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "ArenaLastDrafts.xml"
            output = Path(directory) / "arena.jsonl"
            model = Path(directory) / "prior.json"
            source.write_text(ARENA_XML, encoding="utf-8")
            records, warnings = parse_arena_drafts(source)
            self.assertFalse(warnings)
            self.assertEqual("draft-0001", records[0]["draft_id"])
            self.assertNotIn("private-player", json.dumps(records))
            self.assertNotIn("private-deck", json.dumps(records))
            self.assertNotIn("2026-01-01", json.dumps(records))
            self.assertEqual(50.0, records[0]["arenasmith_scores"]["CARD_C"])
            self.assertEqual(1, write_jsonl(records, output))
            artifact = train_file(output, model)
            self.assertIn("CARD_C", artifact["card_priors"])
            self.assertTrue(model.exists())

    def test_arena_import_rejects_entities(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bad.xml"
            source.write_text("<!DOCTYPE x [<!ENTITY y 'z'>]><ArenaLastDrafts />", encoding="utf-8")
            with self.assertRaises(ValueError):
                parse_arena_drafts(source)

    def test_normalized_advisor_priors_are_optional_and_defensive(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "cards.json").write_text(
                json.dumps({"cards": [{"card_id": "A", "win_rate": 60}, {"card_id": "B", "score": 25}]}),
                encoding="utf-8",
            )
            (root / "broken.json").write_text("not-json", encoding="utf-8")
            priors = load_normalized_card_priors(root)
            self.assertAlmostEqual(1.1, priors["A"])
            self.assertAlmostEqual(0.5, priors["B"])

    def test_priors_discover_arena_and_legacy_latest_layouts(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "AdvisorData"
            (root / "Arena" / "latest").mkdir(parents=True)
            (root / "latest").mkdir(parents=True)
            (root / "Arena" / "latest" / "arena.json").write_text(
                json.dumps({"cards": [{"card_id": "ARENA_CARD", "score": 50}]}),
                encoding="utf-8",
            )
            (root / "latest" / "legacy.json").write_text(
                json.dumps({"cards": [{"card_id": "LEGACY_CARD", "prior_weight": 1.2}]}),
                encoding="utf-8",
            )
            priors = load_normalized_card_priors(root)
            self.assertIn("ARENA_CARD", priors)
            self.assertIn("LEGACY_CARD", priors)

    def test_live_mode_priors_do_not_leak_arena_weights_into_standard(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "AdvisorData"
            (root / "Arena" / "latest").mkdir(parents=True)
            (root / "Arena" / "latest" / "card_priors.json").write_text(
                json.dumps({"cards": [{"card_id": "ARENA_CARD", "prior_weight": 3.0}]}),
                encoding="utf-8",
            )
            mode_priors = load_mode_card_priors(root)
            self.assertEqual({"arena"}, set(mode_priors))

            arena_state = state()
            arena_state.mode = "Arena"
            standard_state = state()
            standard_state.mode = "Ranked"
            for game in (arena_state, standard_state):
                game.friendly.hand.extend(
                    [
                        Card("a", "ARENA_CARD", "Arena", CardType.MINION, cost=1),
                        Card("b", "NEUTRAL_CARD", "Neutral", CardType.MINION, cost=1),
                    ]
                )
            prior = HeuristicActionPrior(card_weights_by_mode=mode_priors)

            def play_weights(game):
                actions = [
                    action for action in enumerate_legal_actions(game)
                    if action.kind.value == "play_card"
                ]
                return prior.probabilities(game, actions)

            arena_weights = play_weights(arena_state)
            standard_weights = play_weights(standard_state)
            self.assertGreater(
                arena_weights["play_card:a::position=1"],
                arena_weights["play_card:b::position=1"],
            )
            self.assertAlmostEqual(
                standard_weights["play_card:a::position=1"],
                standard_weights["play_card:b::position=1"],
            )

    def test_identity_sanitizer_drops_and_hashes(self) -> None:
        sanitized = sanitize_for_training(
            {
                "game_id": "secret",
                "account_id": "remove",
                "password": "remove",
                "observed_at_utc": "2026-07-29T12:34:56Z",
                "current_deck": {"deck_id": "private", "name": "Private deck"},
                "nested": {"opponent_name": "remove"},
            }
        )
        self.assertTrue(sanitized["game_id"].startswith("anon-"))
        self.assertNotIn("account_id", sanitized)
        self.assertNotIn("password", sanitized)
        self.assertNotIn("observed_at_utc", sanitized)
        self.assertNotIn("current_deck", sanitized)
        self.assertNotIn("opponent_name", sanitized["nested"])
        self.assertEqual(sanitized["game_id"], sanitize_for_training(sanitized)["game_id"])

    def test_frequency_training_pairs_action_and_result_observations(self) -> None:
        records = [
            {
                "kind": "observation",
                "trajectory": {
                    "schema": "trajectory-readiness-v1",
                    "split": "train",
                },
                "observation": {
                    "kind": "action",
                    "state_id": "state-pre",
                    "game_id": "anon-game",
                    "action": {
                        "kind": "play_card",
                        "source_entity_id": "card-1",
                        "card_id": "CARD_A",
                    },
                    "metadata": {
                        "trajectory_schema": "trajectory-readiness-v1",
                        "training_eligible": True,
                        "completeness": "complete_action_trace_v1",
                        "capture_contract": "trajectory-readiness-v1",
                        "transition_status": "replayable_exact",
                        "pre_state_id": "state-pre",
                        "post_state_id": "state-post",
                        "action_sequence": 1,
                        "source_entity_resolution": "exact_entity_id",
                        "target_entity_resolution": "not_applicable",
                    },
                },
            },
            {
                "kind": "observation",
                "trajectory": {
                    "schema": "trajectory-readiness-v1",
                    "split": "train",
                },
                "observation": {
                    "kind": "result",
                    "game_id": "anon-game",
                    "result": "win",
                    "metadata": {
                        "trajectory_schema": "trajectory-readiness-v1",
                        "training_eligible": "true",
                        "completeness": "terminal_result",
                        "capture_contract": "terminal_result_v1",
                    },
                },
            },
        ]
        artifact = train_frequency(records, trajectory_training_ready=True)
        self.assertEqual(1, artifact["labeled_sample_count"])
        self.assertGreater(artifact["action_kind_weights"]["play_card"], 0.5)

    def test_frequency_training_never_uses_held_out_games(self) -> None:
        def observation_records(game_id: str, split: str, result: str) -> list[dict]:
            return [
                {
                    "kind": "observation",
                    "trajectory": {
                        "schema": "trajectory-readiness-v1",
                        "split": split,
                    },
                    "observation": {
                        "kind": "action",
                        "state_id": game_id + "-pre",
                        "game_id": game_id,
                        "action": {
                            "kind": "end_turn",
                            "source_entity_id": "",
                            "target_entity_id": "",
                        },
                        "metadata": {
                            "trajectory_schema": "trajectory-readiness-v1",
                            "training_eligible": True,
                            "completeness": "complete_action_trace_v1",
                            "capture_contract": "trajectory-readiness-v1",
                            "transition_status": "replayable_exact",
                            "pre_state_id": game_id + "-pre",
                            "post_state_id": game_id + "-post",
                            "action_sequence": 1,
                            "source_entity_resolution": "not_applicable",
                            "target_entity_resolution": "not_applicable",
                        },
                    },
                },
                {
                    "kind": "observation",
                    "trajectory": {
                        "schema": "trajectory-readiness-v1",
                        "split": split,
                    },
                    "observation": {
                        "kind": "result",
                        "state_id": game_id + "-post",
                        "game_id": game_id,
                        "result": result,
                        "metadata": {
                            "trajectory_schema": "trajectory-readiness-v1",
                            "training_eligible": True,
                            "completeness": "terminal_result",
                            "capture_contract": "terminal_result_v1",
                        },
                    },
                },
            ]

        records = observation_records("anon-1111111111111111", "train", "win")
        records += observation_records("anon-2222222222222222", "test", "loss")
        artifact = train_frequency(records, trajectory_training_ready=True)
        self.assertEqual(1, artifact["labeled_sample_count"])
        self.assertEqual(2, artifact["ignored_held_out_observation_count"])
        self.assertAlmostEqual(2.0 / 3.0, artifact["action_kind_weights"]["end_turn"])

    def test_production_training_gate_rejects_fixture_volume(self) -> None:
        fixture = Path(__file__).resolve().parents[1] / "fixtures" / "trajectory-readiness-v1.jsonl"
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "frequency.json"
            artifact = train_file(fixture, output)
            self.assertTrue(artifact["trajectory_audit"]["contract_passed"])
            self.assertFalse(artifact["trajectory_audit"]["training_ready"])
            self.assertEqual(0, artifact["labeled_sample_count"])
            with self.assertRaisesRegex(ValueError, "passing trajectory-readiness-v1 audit"):
                train_file(fixture, Path(directory) / "value.pt", backend="torch")

    def test_train_file_uses_one_immutable_input_snapshot(self) -> None:
        initial = (
            json.dumps(
                {
                    "kind": "arena_draft_pick",
                    "picked_card_id": "INITIAL_CARD",
                    "arenasmith_scores": {"INITIAL_CARD": 50},
                },
                separators=(",", ":"),
            )
            + "\n"
        ).encode("utf-8")
        changed = (
            json.dumps(
                {
                    "kind": "arena_draft_pick",
                    "picked_card_id": "CHANGED_CARD",
                    "arenasmith_scores": {"CHANGED_CARD": 1},
                },
                separators=(",", ":"),
            )
            + "\n"
        ).encode("utf-8")
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "live.jsonl"
            output = Path(directory) / "prior.json"
            source.write_bytes(initial)
            from metacompanion_solver import training

            original_load_records = training.load_records

            def mutate_live_file_after_snapshot(snapshot: Path):
                source.write_bytes(changed)
                return original_load_records(snapshot)

            with patch(
                "metacompanion_solver.training.load_records",
                side_effect=mutate_live_file_after_snapshot,
            ):
                artifact = train_file(source, output)

        self.assertEqual(hashlib.sha256(initial).hexdigest(), artifact["dataset_sha256"])
        self.assertIn("INITIAL_CARD", artifact["card_priors"])
        self.assertNotIn("CHANGED_CARD", artifact["card_priors"])

    def test_frequency_training_ignores_explicitly_incomplete_hdt_actions(self) -> None:
        records = [
            {
                "kind": "observation",
                "observation": {
                    "kind": "action",
                    "game_id": "anon-game",
                    "action": {"kind": "play_card", "card_id": "CARD_A"},
                    "metadata": {
                        "training_eligible": "false",
                        "completeness": "partial_hdt_gameevents_v1",
                    },
                },
            },
            {
                "kind": "observation",
                "observation": {
                    "kind": "result",
                    "game_id": "anon-game",
                    "result": "win",
                    "metadata": {
                        "training_eligible": True,
                        "completeness": "terminal_result",
                    },
                },
            },
        ]
        artifact = train_frequency(records)
        self.assertEqual(0, artifact["labeled_sample_count"])
        self.assertEqual(1, artifact["ignored_incomplete_observation_count"])
        self.assertNotIn("play_card", artifact["action_kind_weights"])

    def test_frequency_training_rejects_partial_completeness_without_eligibility(self) -> None:
        records = [
            {
                "kind": "observation",
                "observation": {
                    "kind": "action",
                    "game_id": "anon-game",
                    "action": {"kind": "play_card", "card_id": "CARD_A"},
                    "metadata": {"completeness": "partial_hdt_gameevents_v1"},
                },
            },
            {
                "kind": "observation",
                "observation": {
                    "kind": "result",
                    "game_id": "anon-game",
                    "result": "win",
                    "metadata": {
                        "training_eligible": True,
                        "completeness": "terminal_result",
                    },
                },
            },
        ]
        artifact = train_frequency(records)
        self.assertEqual(0, artifact["labeled_sample_count"])
        self.assertEqual(1, artifact["ignored_incomplete_observation_count"])
        self.assertNotIn("play_card", artifact["action_kind_weights"])

    def test_partial_completeness_overrides_positive_eligibility(self) -> None:
        records = [
            {
                "kind": "observation",
                "observation": {
                    "kind": "action",
                    "game_id": "anon-game",
                    "action": {"kind": "play_card", "card_id": "CARD_A"},
                    "metadata": {
                        "training_eligible": True,
                        "completeness": "partial_hdt_gameevents_v1",
                    },
                },
            },
            {
                "kind": "observation",
                "observation": {
                    "kind": "result",
                    "game_id": "anon-game",
                    "result": "win",
                    "metadata": {
                        "training_eligible": True,
                        "completeness": "terminal_result",
                    },
                },
            },
        ]
        artifact = train_frequency(records)
        self.assertEqual(0, artifact["labeled_sample_count"])
        self.assertEqual(1, artifact["ignored_incomplete_observation_count"])
        self.assertNotIn("play_card", artifact["action_kind_weights"])

    def test_frequency_training_treats_numeric_zero_as_ineligible(self) -> None:
        records = [
            {
                "kind": "observation",
                "observation": {
                    "kind": "action",
                    "game_id": "anon-game",
                    "action": {"kind": "play_card", "card_id": "CARD_A"},
                    "metadata": {
                        "training_eligible": 0,
                        "completeness": "complete_action_v1",
                    },
                },
            },
            {
                "kind": "observation",
                "observation": {
                    "kind": "result",
                    "game_id": "anon-game",
                    "result": "win",
                    "metadata": {
                        "training_eligible": 1,
                        "completeness": "terminal_result",
                    },
                },
            },
        ]
        artifact = train_frequency(records)
        self.assertEqual(0, artifact["labeled_sample_count"])
        self.assertEqual(1, artifact["ignored_incomplete_observation_count"])
        self.assertNotIn("play_card", artifact["action_kind_weights"])

    def test_training_sanitizer_redacts_all_opponent_hidden_zones(self) -> None:
        private = {
            "entity_id": "hidden-1",
            "card_id": "SECRET_INTERNAL_CARD",
            "name": "Secret internal name",
            "card_text": "Secret internal text",
            "cost": 9,
            "zone": "HAND",
            "zone_position": 2,
            "controller_id": 2,
            "tags": {"ZONE": 3, "ZONE_POSITION": 2, "CONTROLLER": 2, "COST": 9},
        }
        payload = {
            "state": {
                "friendly": {
                    "hand": [
                        {
                            "entity_id": "friendly-1",
                            "card_id": "FRIENDLY_VISIBLE",
                            "name": "Friendly visible card",
                            "zone": "HAND",
                        }
                    ]
                },
                "opponent": {
                    "hand": [private],
                    "deck": [{**private, "entity_id": "hidden-2", "zone": "DECK"}],
                    "set_aside": [{**private, "entity_id": "hidden-3", "zone": "SETASIDE"}],
                    "secrets": [{**private, "entity_id": "hidden-4", "zone": "SECRET"}],
                    "board": [
                        {
                            **private,
                            "entity_id": "hidden-5",
                            "zone": "PLAY",
                            "visibility": "hidden",
                        }
                    ],
                },
            }
        }
        sanitized = sanitize_for_training(payload)
        serialized = json.dumps(sanitized, sort_keys=True)
        self.assertNotIn("SECRET_INTERNAL_CARD", serialized)
        self.assertNotIn("Secret internal name", serialized)
        self.assertNotIn("Secret internal text", serialized)
        self.assertIn("FRIENDLY_VISIBLE", serialized)
        opponent = sanitized["state"]["opponent"]
        for zone in ("hand", "deck", "set_aside", "secrets", "board"):
            entity = opponent[zone][0]
            self.assertLessEqual(
                set(entity),
                {"entity_id", "zone", "zone_position", "controller_id", "visibility", "tags"},
            )
            self.assertNotIn("COST", entity.get("tags", {}))


if __name__ == "__main__":
    unittest.main()
