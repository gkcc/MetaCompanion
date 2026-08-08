from __future__ import annotations

import unittest
import json
import tempfile
from pathlib import Path

from metacompanion_solver.errors import SchemaError
from metacompanion_solver.logging_store import JsonlTrainingLogger
from metacompanion_solver.schemas import Observation


def _observation(action: dict[str, object]) -> dict[str, object]:
    return {
        "api_version": "1.0",
        "kind": "action",
        "state_id": "state-1",
        "game_id": "game-1",
        "action": action,
        "metadata": {},
    }


def _state(state_id: str, sequence: int, active: str) -> dict[str, object]:
    return {
        "state_id": state_id,
        "turn": sequence,
        "active_player_id": active,
        "perspective_player_id": "friendly",
        "friendly": {
            "player_id": "friendly",
            "hero": {
                "entity_id": "1",
                "card_id": "FRIENDLY_HERO",
                "card_type": "HERO",
                "health": 30,
            },
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {
                "entity_id": "2",
                "card_id": "OPPONENT_HERO",
                "card_type": "HERO",
                "health": 30,
            },
        },
        "patch": "test",
        "mode": "standard",
        "metadata": {
            "game_id": "game-1",
            "snapshot_state_hash": ("a" if sequence == 1 else "b") * 64,
            "snapshot_sequence": sequence,
        },
    }


def _power_candidate() -> dict[str, object]:
    value = _observation(
        {
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": "",
            "sub_option": -1,
            "board_position": 0,
            "option_id": 0,
            "frame_id": 9,
            "power_start_watermark": "g1:100",
            "power_end_watermark": "g1:110",
            "choices": [],
        }
    )
    value["pre_state"] = _state("state-1", 1, "friendly")
    value["post_state"] = _state("state-2", 2, "opponent")
    value["metadata"] = {
        "trajectory_schema": "trajectory-readiness-v1",
        "decision_id": "state-1",
        "action_sequence": "1",
        "game_generation": "1",
        "power_collector_epoch": "1",
        "power_action_ordinal": "1",
        "power_gap_count": "0",
        "pre_state_id": "state-1",
        "post_state_id": "state-2",
        "raw_pre_snapshot_hash": "a" * 64,
        "raw_post_snapshot_hash": "b" * 64,
        "pre_snapshot_sequence": "1",
        "post_snapshot_sequence": "2",
        "boundary_status": "isolated",
        "intervening_action_count": "0",
        "capture_warning_count": "0",
        "capture_contract": "hdt_power_action_identity_v1",
        "transition_status": "post_state_candidate_unverified",
        "transition_verification": "producer_candidate_unverified",
        "completeness": "exact_action_identity_unverified_transition_v1",
        "action_identity_status": "exact_hdt_power_v1",
        "choice_status": "none",
        "simulator_status": "not_replayed",
        "source_entity_resolution": "not_applicable",
        "target_entity_resolution": "not_applicable",
        "training_eligible": False,
    }
    return value


class ObservedActionEvidenceTests(unittest.TestCase):
    def test_power_evidence_round_trips_without_changing_canonical_action(self) -> None:
        parsed = Observation.from_dict(
            _observation(
                {
                    "kind": "play_card",
                    "source_entity_id": 11,
                    "target_entity_id": None,
                    "card_id": "TEST_CARD",
                    "sub_option": -1,
                    "board_position": 2,
                    "option_id": 3,
                    "frame_id": "frame-3",
                    "power_start_watermark": "generation-4:1982",
                    "power_end_watermark": "generation-4:2037",
                    "choices": [
                        {
                            "choice_type": "GENERAL",
                            "selected_entity_ids": [21, 22],
                        }
                    ],
                }
            )
        )

        self.assertEqual("11", parsed.action.source_entity_id)
        self.assertEqual("", parsed.action.target_entity_id)
        self.assertEqual("play_card:11::position=2", parsed.action.action_id)
        wire = parsed.to_dict()["action"]
        self.assertEqual("3", wire["option_id"])
        self.assertEqual("frame-3", wire["frame_id"])
        self.assertEqual(-1, wire["sub_option"])
        self.assertEqual(2, wire["board_position"])
        self.assertEqual([21, 22], wire["choices"][0]["selected_entity_ids"])

    def test_null_entity_ids_are_normalized_for_end_turn(self) -> None:
        parsed = Observation.from_dict(
            _observation(
                {
                    "kind": "end_turn",
                    "source_entity_id": None,
                    "target_entity_id": None,
                    "card_id": "",
                    "option_id": "0",
                    "frame_id": "9",
                    "power_start_watermark": "g:100",
                    "power_end_watermark": "g:110",
                    "choices": [],
                }
            )
        )
        self.assertIsNone(parsed.to_dict()["action"]["source_entity_id"])
        self.assertIsNone(parsed.to_dict()["action"]["target_entity_id"])

    def test_invalid_evidence_types_fail_closed(self) -> None:
        invalid = (
            ("sub_option", True),
            ("board_position", "1"),
            ("option_id", False),
            ("frame_id", []),
            ("power_start_watermark", 10),
            ("power_end_watermark", 11),
            ("choices", {}),
        )
        for key, value in invalid:
            with self.subTest(key=key), self.assertRaises(SchemaError):
                Observation.from_dict(
                    _observation(
                        {
                            "kind": "end_turn",
                            "source_entity_id": "",
                            "target_entity_id": "",
                            "card_id": "",
                            key: value,
                        }
                    )
                )

    def test_unknown_action_evidence_is_rejected(self) -> None:
        with self.assertRaises(SchemaError):
            Observation.from_dict(
                _observation(
                    {
                        "kind": "end_turn",
                        "source_entity_id": "",
                        "target_entity_id": "",
                        "card_id": "",
                        "raw_power_log": "must never be accepted",
                    }
                )
            )

    def test_raw_log_text_cannot_hide_inside_choice_evidence(self) -> None:
        with self.assertRaises(SchemaError):
            Observation.from_dict(
                _observation(
                    {
                        "kind": "end_turn",
                        "source_entity_id": "",
                        "target_entity_id": "",
                        "card_id": "",
                        "choices": [{"raw_power_log": "private raw line"}],
                    }
                )
            )

    def test_mismatched_action_id_is_rejected(self) -> None:
        with self.assertRaises(SchemaError):
            Observation.from_dict(
                _observation(
                    {
                        "action_id": "attack:wrong:target",
                        "kind": "end_turn",
                        "source_entity_id": "",
                        "target_entity_id": "",
                        "card_id": "",
                    }
                )
            )

    def test_power_identity_candidate_is_preserved_but_not_promoted_by_logger(self) -> None:
        parsed = Observation.from_dict(_power_candidate())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "training-v2.jsonl"
            self.assertTrue(JsonlTrainingLogger(path).append_observation(parsed))
            record = json.loads(path.read_text(encoding="utf-8"))
        metadata = record["observation"]["metadata"]
        self.assertEqual("hdt_power_action_identity_v1", metadata["capture_contract"])
        self.assertEqual(
            "exact_action_identity_unverified_transition_v1",
            metadata["completeness"],
        )
        self.assertEqual("exact_hdt_power_v1", metadata["action_identity_status"])
        self.assertEqual("not_replayed", metadata["simulator_status"])
        self.assertIs(metadata["training_eligible"], False)
        self.assertEqual("9", record["observation"]["action"]["frame_id"])

    def test_unsupported_location_stays_a_partial_non_training_trajectory(self) -> None:
        value = _power_candidate()
        value["action"].update(
            {
                "kind": "play_card",
                "source_entity_id": "31",
                "card_id": "LOCATION_CARD",
                "option_id": 1,
            }
        )
        value["metadata"].update(
            {
                "capture_contract": "partial_hdt_transition_candidate_v1",
                "completeness": "partial_hdt_gameevents_v1",
                "action_identity_status": "unsupported_location_activation",
                "simulator_status": "unsupported_location_activation",
                "source_entity_resolution": "exact_entity_id",
            }
        )
        parsed = Observation.from_dict(value)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "training-v2.jsonl"
            self.assertTrue(JsonlTrainingLogger(path).append_observation(parsed))
            record = json.loads(path.read_text(encoding="utf-8"))

        metadata = record["observation"]["metadata"]
        self.assertEqual(
            "partial_hdt_transition_candidate_v1", metadata["capture_contract"]
        )
        self.assertEqual("partial_hdt_gameevents_v1", metadata["completeness"])
        self.assertEqual(
            "unsupported_location_activation", metadata["action_identity_status"]
        )
        self.assertEqual(
            "unsupported_location_activation", metadata["simulator_status"]
        )
        self.assertIs(metadata["training_eligible"], False)

    def test_power_identity_candidate_rejects_unresolved_choice(self) -> None:
        value = _power_candidate()
        value["action"]["choices"] = [{"selected_entity_ids": [7]}]
        with self.assertRaises(SchemaError):
            Observation.from_dict(value)

    def test_power_identity_candidate_cannot_self_declare_training_ready(self) -> None:
        value = _power_candidate()
        value["metadata"]["training_eligible"] = True
        with self.assertRaises(SchemaError):
            Observation.from_dict(value)


if __name__ == "__main__":
    unittest.main()
