from __future__ import annotations

import contextlib
import hashlib
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

from metacompanion_solver.behavior import create_behavior_record
from metacompanion_solver.behavior_learning import (
    BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID,
    BEHAVIOR_IMITATION_SCHEMA_ID,
    BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
    BehaviorLearningError,
    audit_behavior_learning_files,
    audit_runtime_behavior_learning,
    load_behavior_learning_policy,
    promote_behavior_imitation_file,
)
from metacompanion_solver.cli import main
from metacompanion_solver.logging_store import (
    TRAJECTORY_SCHEMA_ID,
    TRAINING_LOG_SCHEMA_ID,
    deterministic_game_split,
)


GAME_ONE = "anon-1111111111111111"
GAME_TWO = "anon-2222222222222222"


def _entity(
    entity_id: str,
    card_id: str,
    card_type: str,
    *,
    attack: int = 0,
    health: int = 0,
) -> dict[str, object]:
    return {
        "entity_id": entity_id,
        "card_id": card_id,
        "card_type": card_type,
        "cost": 1,
        "attack": attack,
        "health": health,
        "current_health": health,
        "playable": True,
        "can_attack": attack > 0,
        "attacks_remaining": 1 if attack > 0 else 0,
    }


def _player(role: str) -> dict[str, object]:
    local = role == "friendly"
    prefix = "f" if local else "o"
    return {
        "player_id": role,
        "hero": _entity(f"{prefix}-hero", f"{prefix.upper()}_HERO", "HERO", health=30),
        "hero_power": _entity(
            f"{prefix}-power", f"{prefix.upper()}_POWER", "HERO_POWER"
        ),
        "weapon": None,
        "hand": [
            _entity(
                f"{prefix}-hand",
                f"{prefix.upper()}_HAND",
                "MINION",
                attack=2,
                health=2,
            )
        ],
        "board": [
            _entity(
                f"{prefix}-minion",
                f"{prefix.upper()}_MINION",
                "MINION",
                attack=3,
                health=3,
            ),
            _entity(
                f"{prefix}-location",
                f"{prefix.upper()}_LOCATION",
                "LOCATION",
                health=2,
            ),
        ],
        "mana": 5,
        "max_mana": 5,
        "armor": 0,
        "deck_size": 20,
        "fatigue": 0,
        "hero_power_available": True,
        "spell_power": 0,
    }


def _state(active: str, state_id: str) -> dict[str, object]:
    return {
        "state_id": state_id,
        "turn": 5,
        "active_player_id": active,
        "perspective_player_id": "friendly",
        "friendly": _player("friendly"),
        "opponent": _player("opponent"),
        "patch": "fixture-patch",
        "mode": "standard",
    }


def _behavior(
    game_id: str,
    sequence: int,
    side: str,
    kind: str,
    *,
    observed_at_utc: str | None = None,
) -> dict[str, object]:
    actor = "friendly" if side == "local" else "opponent"
    common = {
        "game_id": game_id,
        "behavior_sequence": sequence,
        "observed_at_utc": observed_at_utc
        or f"2026-07-31T12:00:{sequence:02d}+08:00",
        "actor_side": side,
        "actor_player_id": actor,
        "boundary_status": "isolated",
        "pre_state": _state(actor, f"{game_id}-pre-{sequence}"),
        "post_state": _state(actor, f"{game_id}-post-{sequence}"),
    }
    if kind == "play_card":
        if side == "local":
            record = create_behavior_record(
                **common,
                actor_evidence="hdt_player_event",
                identity_status="exact_public_entity",
                visibility_status="public_pre_state",
                source_event="player_play",
                action={
                    "kind": kind,
                    "source_entity_id": "f-hand",
                    "target_entity_id": "",
                    "card_id": "F_HAND",
                },
            )
        else:
            record = create_behavior_record(
                **common,
                actor_evidence="hdt_opponent_event",
                identity_status="revealed_after_action",
                visibility_status="revealed_post_action",
                source_event="opponent_play",
                action={
                    "kind": kind,
                    "source_entity_id": "o-hand",
                    "target_entity_id": "",
                    "card_id": "O_REVEALED",
                },
            )
        return record.to_dict()
    if kind == "attack":
        record = create_behavior_record(
            **common,
            actor_evidence=(
                "hdt_player_event" if side == "local" else "hdt_opponent_event"
            ),
            identity_status="exact_public_entity",
            visibility_status="public_pre_state",
            source_event="player_attack" if side == "local" else "opponent_attack",
            action={
                "kind": kind,
                "source_entity_id": "f-minion" if side == "local" else "o-minion",
                "target_entity_id": "o-hero" if side == "local" else "f-hero",
                "card_id": "F_MINION" if side == "local" else "O_MINION",
            },
        )
        return record.to_dict()
    if kind == "hero_power":
        record = create_behavior_record(
            **common,
            actor_evidence=(
                "hdt_player_event" if side == "local" else "hdt_opponent_event"
            ),
            identity_status="exact_public_entity",
            visibility_status="public_pre_state",
            source_event=(
                "player_hero_power" if side == "local" else "opponent_hero_power"
            ),
            action={
                "kind": kind,
                "source_entity_id": "f-power" if side == "local" else "o-power",
                "target_entity_id": "",
                "card_id": "F_POWER" if side == "local" else "O_POWER",
            },
        )
        return record.to_dict()
    if kind == "location_activate" and side == "local":
        record = create_behavior_record(
            **common,
            actor_evidence="hdt_power_log",
            identity_status="exact_public_entity",
            visibility_status="public_pre_state",
            source_event="hdt_power_log",
            action={
                "kind": kind,
                "source_entity_id": "f-location",
                "target_entity_id": "o-hero",
                "card_id": "F_LOCATION",
            },
        )
        return record.to_dict()
    if kind == "end_turn":
        record = create_behavior_record(
            **common,
            actor_evidence="active_player",
            identity_status="event_only",
            visibility_status="public_pre_state",
            source_event=(
                "turn_passed_to_opponent" if side == "local" else "turn_passed_to_player"
            ),
            action={
                "kind": kind,
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": "",
            },
        )
        return record.to_dict()
    raise AssertionError(kind)


def _result(game_id: str, outcome: str) -> dict[str, object]:
    state_id = f"{game_id}-terminal"
    return {
        "kind": "observation",
        "log_schema": TRAINING_LOG_SCHEMA_ID,
        "trajectory": {
            "schema": TRAJECTORY_SCHEMA_ID,
            "game_id": game_id,
            "decision_id": state_id,
            "state_id": state_id,
            "observation_kind": "result",
            "completeness": "terminal_result",
            "capture_contract": "terminal_result_v1",
            "split": deterministic_game_split(game_id),
        },
        "observation": {
            "api_version": "1.0",
            "kind": "result",
            "state_id": state_id,
            "game_id": game_id,
            "observed_at_utc": "",
            "action": None,
            "result": outcome,
            "metadata": {
                "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                "decision_id": state_id,
                "completeness": "terminal_result",
                "capture_contract": "terminal_result_v1",
                "training_eligible": True,
            },
        },
    }


def _write_jsonl(path: Path, records: list[dict[str, object]]) -> None:
    path.write_text(
        "".join(
            json.dumps(item, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            + "\n"
            for item in records
        ),
        encoding="utf-8",
    )


def _fixture_behavior() -> list[dict[str, object]]:
    return [
        _behavior(GAME_ONE, 1, "local", "play_card"),
        _behavior(GAME_ONE, 2, "local", "attack"),
        _behavior(GAME_ONE, 3, "opponent", "hero_power"),
        _behavior(GAME_ONE, 4, "opponent", "end_turn"),
        _behavior(GAME_TWO, 1, "local", "hero_power"),
        _behavior(GAME_TWO, 2, "local", "end_turn"),
        _behavior(GAME_TWO, 3, "opponent", "play_card"),
        _behavior(GAME_TWO, 4, "opponent", "attack"),
    ]


def _write_fixture_policy(path: Path) -> None:
    path.write_text(
        json.dumps(
            {
                "schema": BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
                "thresholds": {
                    "min_unique_games": 2,
                    "min_behavior_records": 8,
                    "min_joined_result_games": 2,
                    "min_joined_behavior_records": 8,
                    "min_behavior_eligible_records": 8,
                    "min_local_eligible_records": 4,
                    "min_opponent_eligible_records": 4,
                    "min_distinct_action_kinds": 4,
                    "min_result_join_rate": 1.0,
                    "min_both_side_game_rate": 1.0,
                    "min_behavior_eligible_rate": 1.0,
                    "max_unknown_actor_rate": 0.0,
                    "max_unknown_identity_rate": 0.0,
                    "min_train_games": 0,
                    "min_validation_games": 0,
                    "min_test_games": 0,
                },
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


class BehaviorLearningAuditTests(unittest.TestCase):
    def _inputs(self, root: Path) -> tuple[Path, Path, Path]:
        behavior = root / "behavior-v1.jsonl"
        trajectory = root / "training-v2.jsonl"
        policy = root / "behavior-policy.json"
        _write_jsonl(behavior, _fixture_behavior())
        _write_jsonl(
            trajectory,
            [_result(GAME_ONE, "win"), _result(GAME_TWO, "loss")],
        )
        _write_fixture_policy(policy)
        return behavior, trajectory, policy

    def test_joint_audit_is_ready_only_for_imitation_use(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            behavior, trajectory, policy = self._inputs(Path(directory))
            report = audit_behavior_learning_files(
                behavior,
                trajectory,
                policy_path=policy,
                source_kind="synthetic_fixture",
            )

        self.assertTrue(report["contract_passed"])
        self.assertTrue(report["imitation_ready"])
        self.assertFalse(report["rl_training_ready"])
        self.assertTrue(report["passed"])
        self.assertEqual(8, report["metrics"]["behavior_record_count"])
        self.assertEqual(2, report["metrics"]["joined_result_game_count"])
        self.assertEqual(4, report["metrics"]["distinct_action_kind_count"])
        self.assertEqual(
            {"local": 4, "opponent": 4},
            report["metrics"]["actor_side_counts"],
        )
        self.assertRegex(report["behavior_input_sha256"], r"^[0-9a-f]{64}$")
        self.assertRegex(report["trajectory_input_sha256"], r"^[0-9a-f]{64}$")
        self.assertRegex(report["policy_sha256"], r"^[0-9a-f]{64}$")

    def test_missing_terminal_join_is_not_ready_but_contract_remains_honest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            behavior, trajectory, policy = self._inputs(Path(directory))
            _write_jsonl(trajectory, [_result(GAME_ONE, "win")])
            report = audit_behavior_learning_files(
                behavior, trajectory, policy_path=policy
            )

        self.assertTrue(report["contract_passed"])
        self.assertFalse(report["imitation_ready"])
        self.assertEqual(1, report["metrics"]["behavior_without_result_game_count"])
        self.assertEqual(0.5, report["metrics"]["result_join_rate"])

    def test_file_order_regression_fails_closed_even_when_sequences_are_contiguous(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            behavior, trajectory, policy = self._inputs(Path(directory))
            items = _fixture_behavior()
            items[0], items[1] = items[1], items[0]
            _write_jsonl(behavior, items)
            report = audit_behavior_learning_files(
                behavior, trajectory, policy_path=policy
            )

        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["sequence_order_violation_count"])

    def test_timestamp_regression_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            behavior, trajectory, policy = self._inputs(Path(directory))
            items = _fixture_behavior()
            items[1] = _behavior(
                GAME_ONE,
                2,
                "local",
                "attack",
                observed_at_utc="2026-07-31T11:59:59+08:00",
            )
            _write_jsonl(behavior, items)
            report = audit_behavior_learning_files(
                behavior, trajectory, policy_path=policy
            )

        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["timestamp_regression_count"])

    def test_same_input_file_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "same.jsonl"
            source.write_text("", encoding="utf-8")
            with self.assertRaises(BehaviorLearningError):
                audit_behavior_learning_files(source, source)

    def test_policy_rejects_unknown_and_non_integer_count_thresholds(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            policy = Path(directory) / "policy.json"
            policy.write_text(
                json.dumps(
                    {
                        "schema": BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
                        "thresholds": {"unknown_threshold": 1},
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaises(BehaviorLearningError):
                load_behavior_learning_policy(policy)
            policy.write_text(
                json.dumps(
                    {
                        "schema": BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
                        "thresholds": {"min_unique_games": 1.5},
                    }
                ),
                encoding="utf-8",
            )
            with self.assertRaises(BehaviorLearningError):
                load_behavior_learning_policy(policy)


class BehaviorImitationPromotionTests(unittest.TestCase):
    def test_promotion_is_hash_bound_strips_timestamps_and_never_claims_optimality(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            behavior = root / "behavior-v1.jsonl"
            trajectory = root / "training-v2.jsonl"
            policy = root / "policy.json"
            output = root / "imitation-v1.jsonl"
            manifest_path = root / "imitation-manifest.json"
            _write_jsonl(behavior, _fixture_behavior())
            _write_jsonl(
                trajectory,
                [_result(GAME_ONE, "win"), _result(GAME_TWO, "loss")],
            )
            _write_fixture_policy(policy)

            manifest = promote_behavior_imitation_file(
                behavior,
                trajectory,
                output,
                manifest_path,
                policy_path=policy,
            )
            output_bytes = output.read_bytes()
            records = [
                json.loads(line)
                for line in output_bytes.decode("utf-8").splitlines()
                if line.strip()
            ]

        self.assertEqual(BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID, manifest["schema"])
        self.assertTrue(manifest["imitation_ready"])
        self.assertFalse(manifest["rl_training_ready"])
        self.assertEqual(8, manifest["imitation_dataset"]["record_count"])
        self.assertEqual(
            hashlib.sha256(output_bytes).hexdigest(),
            manifest["imitation_dataset"]["sha256"],
        )
        self.assertEqual(8, len(records))
        for record in records:
            self.assertEqual(BEHAVIOR_IMITATION_SCHEMA_ID, record["schema"])
            self.assertNotIn("observed_at_utc", record)
            self.assertTrue(record["imitation_training_eligible"])
            self.assertFalse(record["rl_training_eligible"])
            self.assertFalse(record["provenance"]["optimality_verified"])
        opponent_winning_game = next(
            item
            for item in records
            if item["game_id"] == GAME_ONE and item["actor_side"] == "opponent"
        )
        self.assertEqual("win", opponent_winning_game["local_outcome"])
        self.assertEqual("loss", opponent_winning_game["actor_outcome"])

    def test_default_production_policy_refuses_tiny_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            behavior = root / "behavior-v1.jsonl"
            trajectory = root / "training-v2.jsonl"
            _write_jsonl(behavior, _fixture_behavior())
            _write_jsonl(
                trajectory,
                [_result(GAME_ONE, "win"), _result(GAME_TWO, "loss")],
            )
            with self.assertRaises(BehaviorLearningError):
                promote_behavior_imitation_file(
                    behavior,
                    trajectory,
                    root / "output.jsonl",
                    root / "manifest.json",
                )


class RuntimeBehaviorLearningTests(unittest.TestCase):
    def test_runtime_no_data_and_missing_result_are_distinct(self) -> None:
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(
            os.environ, {"APPDATA": directory}
        ):
            snapshots = Path(directory) / "snapshots"
            report = audit_runtime_behavior_learning(
                snapshot_directory=snapshots
            )
            self.assertEqual("NO_DATA", report["status"])
            self.assertEqual("runtime_behavior_log_not_found", report["reason"])

            worker = (
                Path(directory)
                / "HearthstoneDeckTracker"
                / "MetaCompanion"
                / "AdvisorWorker"
            )
            worker.mkdir(parents=True)
            _write_jsonl(worker / "behavior-v1.jsonl", _fixture_behavior())
            report = audit_runtime_behavior_learning(
                snapshot_directory=snapshots
            )

        self.assertEqual("NOT_READY", report["status"])
        self.assertEqual("runtime_trajectory_result_log_not_found", report["reason"])
        self.assertRegex(report["behavior_snapshot"], r"^behavior-v1\.[0-9a-f]{64}\.jsonl$")

    def test_runtime_ready_report_binds_both_snapshots(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            behavior = root / "behavior-v1.jsonl"
            trajectory = root / "training-v2.jsonl"
            policy = root / "policy.json"
            snapshots = root / "snapshots"
            _write_jsonl(behavior, _fixture_behavior())
            _write_jsonl(
                trajectory,
                [_result(GAME_ONE, "win"), _result(GAME_TWO, "loss")],
            )
            _write_fixture_policy(policy)
            report = audit_runtime_behavior_learning(
                behavior_path=behavior,
                trajectory_path=trajectory,
                snapshot_directory=snapshots,
                policy_path=policy,
            )

        self.assertEqual("READY", report["status"])
        self.assertTrue(report["snapshots_content_addressed"])
        self.assertTrue(report["imitation_ready"])
        self.assertFalse(report["rl_training_ready"])
        self.assertEqual(
            report["behavior_input_sha256"],
            report["audit"]["behavior_input_sha256"],
        )
        self.assertEqual(
            report["trajectory_input_sha256"],
            report["audit"]["trajectory_input_sha256"],
        )

    def test_cli_writes_unicode_report_and_uses_readiness_exit_codes(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            behavior = root / "behavior-v1.jsonl"
            trajectory = root / "training-v2.jsonl"
            policy = root / "policy.json"
            report_path = root / "report.json"
            _write_jsonl(behavior, _fixture_behavior())
            _write_jsonl(
                trajectory,
                [_result(GAME_ONE, "win"), _result(GAME_TWO, "loss")],
            )
            _write_fixture_policy(policy)
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = main(
                    [
                        "audit-behavior-learning",
                        "--behavior",
                        str(behavior),
                        "--trajectory",
                        str(trajectory),
                        "--policy",
                        str(policy),
                        "--source-kind",
                        "synthetic_fixture",
                        "--output",
                        str(report_path),
                    ]
                )
            report = json.loads(report_path.read_text(encoding="utf-8"))

        self.assertEqual(0, exit_code)
        self.assertTrue(report["imitation_ready"])
        self.assertIn("最优动作", report["caveat"])


if __name__ == "__main__":
    unittest.main()
