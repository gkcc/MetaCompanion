from __future__ import annotations

import contextlib
import copy
import hashlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

from metacompanion_solver.cli import main
from metacompanion_solver.schemas import (
    TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
    TRANSITION_CANDIDATE_STATUS,
    TRANSITION_CANDIDATE_VERIFICATION,
)
from metacompanion_solver.trajectory import (
    SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
    SOURCE_KIND_SYNTHETIC_FIXTURE,
    audit_runtime_trajectory,
    audit_trajectory_file,
)


FIXTURE_DIR = Path(__file__).resolve().parents[1] / "fixtures"
TRAJECTORY_FIXTURE = FIXTURE_DIR / "trajectory-readiness-v1.jsonl"
FIXTURE_POLICY = FIXTURE_DIR / "trajectory-readiness-policy-v1.json"


def _fixture_records() -> list[dict]:
    return [
        json.loads(line)
        for line in TRAJECTORY_FIXTURE.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def _write_records(root: Path, records: list[dict]) -> Path:
    output = root / "trajectory.jsonl"
    output.write_text(
        "".join(json.dumps(item, separators=(",", ":")) + "\n" for item in records),
        encoding="utf-8",
    )
    return output


def _make_first_action_a_candidate(records: list[dict]) -> None:
    raw_pre_hash = "a" * 64
    raw_post_hash = "b" * 64
    for index in (0, 1):
        records[index]["request"]["state"]["metadata"].update(
            {"snapshot_sequence": 10}
        )
        records[index]["trajectory"]["raw_snapshot_hash"] = raw_pre_hash
    records[3]["request"]["state"]["metadata"].update(
        {"snapshot_sequence": 11}
    )
    records[3]["trajectory"]["raw_snapshot_hash"] = raw_post_hash
    pre_state = copy.deepcopy(records[1]["request"]["state"])
    post_state = copy.deepcopy(records[3]["request"]["state"])
    pre_hash = hashlib.sha256(
        json.dumps(pre_state, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    post_hash = hashlib.sha256(
        json.dumps(post_state, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    candidate = {
        "pre_state_id": "g1-pre",
        "post_state_id": "g1-post",
        "raw_pre_snapshot_hash": raw_pre_hash,
        "raw_post_snapshot_hash": raw_post_hash,
        "pre_state_hash": pre_hash,
        "post_state_hash": post_hash,
        "pre_snapshot_sequence": 10,
        "post_snapshot_sequence": 11,
        "boundary_status": "isolated",
        "intervening_action_count": 0,
        "capture_warning_count": 0,
        "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
    }
    action = records[2]
    action["observation"]["pre_state"] = pre_state
    action["observation"]["post_state"] = post_state
    action["trajectory"].update(candidate)
    action["trajectory"].update(
        {
            "completeness": "partial_hdt_gameevents_v1",
            "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
            "transition_status": TRANSITION_CANDIDATE_STATUS,
        }
    )
    action["observation"]["metadata"].update(candidate)
    action["observation"]["metadata"].update(
        {
            "completeness": "partial_hdt_gameevents_v1",
            "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
            "transition_status": TRANSITION_CANDIDATE_STATUS,
            "training_eligible": False,
        }
    )


def _insert_second_end_turn_segment(records: list[dict]) -> None:
    pre_solve = copy.deepcopy(records[3])
    pre_solve["trajectory"].update(
        {
            "decision_id": "g1-segment-pre",
            "state_id": "g1-segment-pre",
            "solve_stage": "single",
        }
    )
    pre_solve["request"]["request_id"] = "g1-segment-pre-single"
    pre_solve["request"]["state"]["state_id"] = "g1-segment-pre"
    pre_solve["request"]["metadata"].update(
        {"decision_id": "g1-segment-pre", "solve_stage": "single"}
    )

    action = copy.deepcopy(records[2])
    action["trajectory"].update(
        {
            "decision_id": "g1-segment-pre",
            "state_id": "g1-segment-pre",
            "action_sequence": 2,
        }
    )
    action["observation"]["state_id"] = "g1-segment-pre"
    action["observation"]["metadata"].update(
        {
            "decision_id": "g1-segment-pre",
            "pre_state_id": "g1-segment-pre",
            "post_state_id": "g1-segment-post",
            "action_sequence": 2,
        }
    )

    post_solve = copy.deepcopy(pre_solve)
    post_solve["trajectory"].update(
        {"decision_id": "g1-segment-post", "state_id": "g1-segment-post"}
    )
    post_solve["request"]["request_id"] = "g1-segment-post-single"
    post_solve["request"]["state"].update(
        {
            "state_id": "g1-segment-post",
            "turn": pre_solve["request"]["state"]["turn"] + 1,
            "active_player_id": "friendly",
        }
    )
    post_solve["request"]["metadata"]["decision_id"] = "g1-segment-post"
    records[4:4] = [pre_solve, action, post_solve]


class TrajectoryAuditTests(unittest.TestCase):
    def test_versioned_fixture_is_training_ready_under_fixture_policy(self) -> None:
        report = audit_trajectory_file(
            TRAJECTORY_FIXTURE,
            policy_path=FIXTURE_POLICY,
            source_kind=SOURCE_KIND_SYNTHETIC_FIXTURE,
        )
        self.assertTrue(report["contract_passed"])
        self.assertTrue(report["training_ready"])
        self.assertEqual(4, report["metrics"]["canonical_decision_count"])
        self.assertEqual(2, report["metrics"]["superseded_initial_solve_count"])
        self.assertEqual(2, report["metrics"]["replayable_transition_count"])
        self.assertEqual({"test": 1, "train": 1}, report["metrics"]["split_game_counts"])
        self.assertEqual(2, len(report["verified_transitions"]))
        self.assertEqual(SOURCE_KIND_SYNTHETIC_FIXTURE, report["source_kind"])
        self.assertEqual(TRAJECTORY_FIXTURE.stat().st_size, report["input_bytes"])
        self.assertRegex(report["input_sha256"], r"^[0-9a-f]{64}$")
        self.assertRegex(report["policy_sha256"], r"^[0-9a-f]{64}$")
        self.assertEqual(6, report["metrics"]["ok_solve_count"])
        self.assertEqual(0, report["metrics"]["non_ok_solve_count"])
        for transition in report["verified_transitions"]:
            self.assertTrue(transition["game_id"].startswith("anon-"))
            self.assertEqual(64, len(transition["normalized_pre_state_hash"]))
            self.assertEqual(64, len(transition["normalized_post_state_hash"]))
            self.assertGreater(transition["observation_line"], 0)

    def test_fixture_contract_passes_but_production_volume_gate_stays_closed(self) -> None:
        report = audit_trajectory_file(TRAJECTORY_FIXTURE)
        self.assertTrue(report["contract_passed"])
        self.assertFalse(report["training_ready"])
        self.assertTrue(report["solver_runtime_ready"])
        failed = {
            item["name"] for item in report["readiness_checks"] if not item["passed"]
        }
        self.assertEqual(
            {"unique_game_count", "canonical_decision_count", "terminal_result_game_count"},
            failed,
        )

    def test_solve_status_metrics_are_disjoint_and_keep_an_explicit_total(self) -> None:
        records = _fixture_records()
        solve_records = [record for record in records if record.get("kind") == "solve"]
        self.assertEqual(6, len(solve_records))
        for record, status in zip(
            solve_records,
            ("ok", "partial", "cancelled", "unsupported", "error", "unavailable"),
        ):
            record["result"]["status"] = status
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        metrics = report["metrics"]
        self.assertEqual(1, metrics["ok_solve_count"])
        self.assertEqual(1, metrics["partial_solve_count"])
        self.assertEqual(1, metrics["cancelled_solve_count"])
        self.assertEqual(1, metrics["unsupported_solve_count"])
        self.assertEqual(2, metrics["error_solve_count"])
        self.assertEqual(2, metrics["non_ok_solve_count"])
        self.assertEqual(5, metrics["unsuccessful_solve_count"])
        self.assertAlmostEqual(1 / 6, metrics["unsupported_solve_rate"], places=6)
        self.assertAlmostEqual(2 / 6, metrics["non_ok_solve_rate"], places=6)
        self.assertAlmostEqual(5 / 6, metrics["unsuccessful_solve_rate"], places=6)
        self.assertEqual(
            ["ok", "partial", "cancelled", "unsupported", "non_ok"],
            report["solve_status_semantics"]["policy_buckets"],
        )
        self.assertEqual(
            ["error", "other"],
            report["solve_status_semantics"]["non_ok_members"],
        )
        self.assertTrue(report["training_ready"])
        self.assertFalse(report["solver_runtime_ready"])
        failed = {
            item["name"]
            for item in report["operational_checks"]
            if not item["passed"]
        }
        self.assertTrue(
            {
                "unsupported_solve_rate",
                "cancelled_solve_rate",
                "partial_solve_rate",
                "non_ok_solve_rate",
            }
            <= failed
        )

    def test_issue_reason_aggregation_is_complete_when_details_are_truncated(self) -> None:
        records = [
            {
                "kind": "unsupported-test-record",
                "trajectory": {
                    "schema": "trajectory-readiness-v1",
                    "game_id": "anon-1111111111111111",
                    "split": "train",
                },
            }
            for _ in range(125)
        ]
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(_write_records(Path(directory), records))
        issues = report["issues"]
        self.assertEqual(100, len(issues["contract"]))
        self.assertEqual(25, issues["truncated_counts"]["contract"])
        self.assertEqual(
            125,
            issues["reason_counts"]["contract"]["unsupported_record_kind"],
        )
        self.assertEqual(
            125,
            issues["all_reason_counts"]["unsupported_record_kind"],
        )

    def test_runtime_audit_snapshots_once_and_reports_ready_not_ready_or_no_data(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            missing = root / "missing-training-v2.jsonl"
            snapshots = root / "snapshots"
            no_data = audit_runtime_trajectory(
                input_path=missing,
                snapshot_directory=snapshots,
            )
            self.assertEqual("NO_DATA", no_data["status"])
            self.assertEqual(0, no_data["input_bytes"])
            self.assertFalse(no_data["training_ready"])

            live = root / "training-v2.jsonl"
            payload = TRAJECTORY_FIXTURE.read_bytes()
            live.write_bytes(payload)
            ready = audit_runtime_trajectory(
                input_path=live,
                snapshot_directory=snapshots,
                policy_path=FIXTURE_POLICY,
            )
            self.assertEqual("READY", ready["status"])
            self.assertTrue(ready["snapshot_content_addressed"])
            self.assertEqual(len(payload), ready["input_bytes"])
            self.assertEqual(hashlib.sha256(payload).hexdigest(), ready["input_sha256"])
            self.assertEqual(ready["policy_sha256"], ready["audit"]["policy_sha256"])
            self.assertEqual(
                SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
                ready["audit"]["source_kind"],
            )
            snapshot = snapshots / ready["snapshot"]
            self.assertEqual(payload, snapshot.read_bytes())

            not_ready = audit_runtime_trajectory(
                input_path=live,
                snapshot_directory=snapshots,
            )
            self.assertEqual("NOT_READY", not_ready["status"])
            self.assertFalse(not_ready["training_ready"])
            self.assertEqual(ready["snapshot"], not_ready["snapshot"])

    def test_privacy_gate_rejects_raw_ids_battletags_and_hidden_card_identity(self) -> None:
        records = _fixture_records()
        records[0]["trajectory"]["game_id"] = "raw-game-id"
        records[1]["request"]["metadata"]["debug"] = "FixtureUser#1234"
        hidden = {
            "entity_id": "hidden-card",
            "card_id": "SECRET_INTERNAL_CARD",
            "card_type": "SPELL",
            "visibility": "hidden",
        }
        for index in (0, 1):
            records[index]["request"]["state"]["opponent"]["hand"] = [copy.deepcopy(hidden)]
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        reasons = {item["reason"] for item in report["issues"]["privacy"]}
        self.assertTrue(
            {"raw_game_identifier", "battle_tag_like_value", "hidden_card_identity"}
            <= reasons
        )

    def test_duplicate_final_solve_is_a_contract_failure(self) -> None:
        records = _fixture_records()
        records.insert(2, copy.deepcopy(records[1]))
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["duplicate_solve_count"])
        self.assertEqual(1, report["metrics"]["conflicting_final_solve_count"])

    def test_conflicting_terminal_result_is_a_contract_failure(self) -> None:
        records = _fixture_records()
        conflict = copy.deepcopy(records[4])
        conflict["observation"]["result"] = "loss"
        records.insert(5, conflict)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["conflicting_result_game_count"])

    def test_replay_gate_rejects_a_post_state_mismatch(self) -> None:
        records = _fixture_records()
        records[3]["request"]["state"]["turn"] = 7
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["replay_failure_count"])
        self.assertEqual("post_state_mismatch", report["issues"]["replay"][0]["reason"])

    def test_replay_normalizes_null_entity_ids_for_end_turn(self) -> None:
        records = _fixture_records()
        for index in (2, 7):
            records[index]["observation"]["action"]["source_entity_id"] = None
            records[index]["observation"]["action"]["target_entity_id"] = None
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertTrue(report["contract_passed"])
        self.assertEqual(2, report["metrics"]["replayable_transition_count"])

    def test_replay_reports_solver_errors_instead_of_aborting_the_audit(self) -> None:
        records = _fixture_records()
        action = records[2]
        action["observation"]["action"].update(
            {
                "kind": "attack",
                "source_entity_id": "g1-friendly-hero",
                "target_entity_id": "g1-opponent-hero",
                "card_id": "UNKNOWN",
            }
        )
        action["observation"]["metadata"].update(
            {
                "source_entity_resolution": "exact_entity_id",
                "target_entity_resolution": "exact_entity_id",
            }
        )
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["replay_failure_count"])
        self.assertEqual("IllegalActionError", report["issues"]["replay"][0]["reason"])

    def test_malformed_solve_state_fails_contract_instead_of_aborting_audit(self) -> None:
        records = _fixture_records()
        records[0]["request"]["state"]["friendly"] = None
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertIn(
            "invalid_solve_state",
            {item["reason"] for item in report["issues"]["contract"]},
        )

    def test_malformed_candidate_state_fails_contract_instead_of_aborting_audit(self) -> None:
        records = _fixture_records()
        _make_first_action_a_candidate(records)
        candidate = records[2]["observation"]
        candidate["post_state"]["friendly"] = None
        candidate["metadata"]["post_state_hash"] = hashlib.sha256(
            json.dumps(
                candidate["post_state"], sort_keys=True, separators=(",", ":")
            ).encode("utf-8")
        ).hexdigest()
        records[2]["trajectory"]["post_state_hash"] = candidate["metadata"][
            "post_state_hash"
        ]
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertIn(
            "candidate_invalid_post_state",
            {item["reason"] for item in report["issues"]["contract"]},
        )

    def test_replay_cannot_resolve_a_post_state_from_another_game(self) -> None:
        records = _fixture_records()
        records[7]["observation"]["metadata"]["post_state_id"] = "g1-post"
        records[9]["observation"]["state_id"] = "g1-post"
        records[9]["trajectory"]["state_id"] = "g1-post"
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["replay_failure_count"])
        self.assertEqual("missing_pre_or_post_state", report["issues"]["replay"][0]["reason"])

    def test_reusing_a_state_id_across_games_is_a_contract_failure(self) -> None:
        records = _fixture_records()
        records[8]["request"]["state"]["state_id"] = "g1-post"
        records[8]["request"]["metadata"]["decision_id"] = "g1-post"
        records[8]["trajectory"]["state_id"] = "g1-post"
        records[8]["trajectory"]["decision_id"] = "g1-post"
        records[7]["observation"]["metadata"]["post_state_id"] = "g1-post"
        records[9]["observation"]["state_id"] = "g1-post"
        records[9]["trajectory"]["state_id"] = "g1-post"
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["cross_game_state_id_reuse_count"])

    def test_action_sequence_must_start_at_one_and_follow_log_order(self) -> None:
        records = _fixture_records()
        records[7]["observation"]["metadata"]["action_sequence"] = "2"
        records[7]["trajectory"]["action_sequence"] = "2"
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["non_contiguous_action_sequence_game_count"])

    def test_exact_action_must_join_its_canonical_decision(self) -> None:
        records = _fixture_records()
        records[2]["trajectory"]["decision_id"] = "unrelated-decision"
        records[2]["observation"]["metadata"]["decision_id"] = "unrelated-decision"
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["action_decision_join_failure_count"])

    def test_attached_post_state_can_replace_a_missing_post_solve(self) -> None:
        records = _fixture_records()
        records[2]["observation"]["post_state"] = copy.deepcopy(
            records[3]["request"]["state"]
        )
        records.pop(3)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertTrue(report["contract_passed"])
        self.assertEqual(0, report["metrics"]["post_state_order_violation_count"])

    def test_early_post_solve_cannot_be_hidden_by_an_attached_post_state(self) -> None:
        records = _fixture_records()
        records[2]["observation"]["post_state"] = copy.deepcopy(
            records[3]["request"]["state"]
        )
        post_solve = records.pop(3)
        records.insert(2, post_solve)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["post_state_order_violation_count"])

    def test_terminal_result_must_follow_the_last_action(self) -> None:
        records = _fixture_records()
        result = records.pop(4)
        records.insert(2, result)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["terminal_before_last_action_count"])

    def test_zero_threshold_policy_cannot_make_an_empty_file_training_ready(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "empty.jsonl"
            source.write_text("", encoding="utf-8")
            policy = root / "zero-policy.json"
            policy.write_text(
                json.dumps(
                    {
                        "schema": "trajectory-readiness-policy-v1",
                        "thresholds": {
                            "min_unique_games": 0,
                            "min_canonical_decisions": 0,
                            "min_terminal_result_games": 0,
                            "min_solve_result_join_rate": 0,
                            "min_exact_action_rate": 0,
                            "min_replayable_transition_rate": 0,
                            "max_partial_action_rate": 0,
                            "max_unsupported_solve_rate": 0,
                        },
                    }
                ),
                encoding="utf-8",
            )
            report = audit_trajectory_file(source, policy_path=policy)
        self.assertTrue(report["contract_passed"])
        self.assertFalse(report["training_ready"])
        failed = {
            item["name"] for item in report["readiness_checks"] if not item["passed"]
        }
        self.assertTrue(
            {
                "has_canonical_decision",
                "has_terminal_result_game",
                "has_joined_game",
                "has_exact_action",
                "has_replayable_transition",
            }
            <= failed
        )

    def test_report_does_not_disclose_the_absolute_input_path(self) -> None:
        report = audit_trajectory_file(
            TRAJECTORY_FIXTURE,
            policy_path=FIXTURE_POLICY,
        )
        self.assertEqual(TRAJECTORY_FIXTURE.name, report["input"])
        self.assertNotIn(str(TRAJECTORY_FIXTURE.parent), report["input"])

    def test_declared_cross_split_leakage_is_a_contract_failure(self) -> None:
        records = _fixture_records()
        records[1]["trajectory"]["split"] = "validation"
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["cross_split_leakage_count"])
        self.assertEqual(1, report["metrics"]["split_assignment_mismatch_count"])

    def test_partial_action_remains_ineligible_even_with_positive_flag(self) -> None:
        records = _fixture_records()
        action = records[2]
        action["trajectory"]["completeness"] = "partial_hdt_gameevents_v1"
        action["trajectory"]["capture_contract"] = "partial_hdt_gameevents_v1"
        action["trajectory"]["transition_status"] = "not_replayable"
        action["observation"]["metadata"].update(
            {
                "completeness": "partial_hdt_gameevents_v1",
                "capture_contract": "partial_hdt_gameevents_v1",
                "transition_status": "not_replayable",
                "post_state_id": "",
                "training_eligible": True,
            }
        )
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertFalse(report["training_ready"])
        self.assertEqual(1, report["metrics"]["partial_action_count"])
        self.assertEqual(1, report["metrics"]["exact_action_count"])
        self.assertIn(
            "non_exact_action_marked_training_eligible",
            {item["reason"] for item in report["issues"]["contract"]},
        )

    def test_partial_only_capture_is_honest_but_never_training_ready(self) -> None:
        records = _fixture_records()
        for index in (2, 7):
            record = records[index]
            record["trajectory"]["completeness"] = "partial_hdt_gameevents_v1"
            record["trajectory"]["capture_contract"] = "partial_hdt_gameevents_v1"
            record["trajectory"]["transition_status"] = "not_replayable"
            record["observation"]["metadata"].update(
                {
                    "completeness": "partial_hdt_gameevents_v1",
                    "capture_contract": "partial_hdt_gameevents_v1",
                    "transition_status": "not_replayable",
                    "post_state_id": "",
                    "training_eligible": False,
                    "source_entity_resolution": "card_id_match",
                    "target_entity_resolution": "missing",
                }
            )
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertTrue(report["contract_passed"])
        self.assertFalse(report["training_ready"])
        self.assertEqual(0, report["metrics"]["exact_action_count"])
        failed = {
            item["name"] for item in report["readiness_checks"] if not item["passed"]
        }
        self.assertIn("has_exact_action", failed)
        self.assertIn("has_replayable_transition", failed)

    def test_post_state_candidate_is_consistent_evidence_but_never_exact(self) -> None:
        records = _fixture_records()
        _make_first_action_a_candidate(records)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertTrue(report["contract_passed"])
        self.assertFalse(report["training_ready"])
        self.assertEqual(1, report["metrics"]["candidate_transition_count"])
        self.assertEqual(1, report["metrics"]["candidate_evidence_consistent_count"])
        self.assertEqual(1, report["metrics"]["exact_action_count"])
        self.assertEqual(1, report["metrics"]["replayable_transition_count"])
        self.assertEqual(1, len(report["verified_transitions"]))
        self.assertNotEqual(3, report["verified_transitions"][0]["observation_line"])

    def test_candidate_reason_counts_expand_the_reason_list(self) -> None:
        records = _fixture_records()
        _make_first_action_a_candidate(records)
        action = records[2]
        action["trajectory"]["boundary_status"] = "overlapped"
        action["observation"]["metadata"]["boundary_status"] = "overlapped"
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )

        candidate_counts = report["issues"]["reason_counts"]["candidate"]
        self.assertEqual(1, candidate_counts["candidate_boundary_not_isolated_and_clean"])
        self.assertNotIn("unspecified", candidate_counts)

    def test_candidate_identity_ignores_capture_sequence_and_rule_enrichment(self) -> None:
        records = _fixture_records()
        _make_first_action_a_candidate(records)
        action = records[2]
        post_state = action["observation"]["post_state"]
        post_state["metadata"]["snapshot_sequence"] = 12
        post_state["friendly"]["hero"].update(
            {
                "effect_coverage": "exact",
                "effects": [],
                "rule_id": "derived-rule",
                "rule_text_sha256": "c" * 64,
                "rule_version": "derived-v1",
                "unsupported_effects": [],
            }
        )
        post_hash = hashlib.sha256(
            json.dumps(post_state, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()
        for payload in (action["trajectory"], action["observation"]["metadata"]):
            payload["post_snapshot_sequence"] = 12
            payload["post_state_hash"] = post_hash

        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )

        self.assertTrue(report["contract_passed"])
        self.assertEqual(0, report["metrics"]["state_content_conflict_count"])
        self.assertEqual(0, report["metrics"]["candidate_state_hash_mismatch_count"])
        self.assertEqual(0, report["metrics"]["candidate_snapshot_sequence_mismatch_count"])
        self.assertEqual(1, report["metrics"]["candidate_evidence_consistent_count"])

    def test_candidate_markers_block_exact_even_if_other_flags_are_flipped(self) -> None:
        records = _fixture_records()
        _make_first_action_a_candidate(records)
        action = records[2]
        action["trajectory"].update(
            {
                "capture_contract": "trajectory-readiness-v1",
                "transition_status": "replayable_exact",
                "completeness": "complete_action_trace_v1",
            }
        )
        action["observation"]["metadata"].update(
            {
                "capture_contract": "trajectory-readiness-v1",
                "transition_status": "replayable_exact",
                "completeness": "complete_action_trace_v1",
                "training_eligible": True,
            }
        )
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertFalse(report["contract_passed"])
        self.assertFalse(report["training_ready"])
        self.assertEqual(1, report["metrics"]["exact_action_count"])
        reasons = {item["reason"] for item in report["issues"]["contract"]}
        self.assertIn("invalid_candidate_capture_contract", reasons)
        self.assertIn("candidate_transition_marked_training_eligible", reasons)

    def test_partial_or_candidate_actions_do_not_create_exact_chain_breaks(self) -> None:
        records = _fixture_records()
        _insert_second_end_turn_segment(records)
        _make_first_action_a_candidate(records)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertTrue(report["contract_passed"])
        self.assertEqual(0, report["metrics"]["action_chain_break_count"])

    def test_end_turn_closes_the_exact_local_turn_chain_segment(self) -> None:
        records = _fixture_records()
        _insert_second_end_turn_segment(records)
        with tempfile.TemporaryDirectory() as directory:
            report = audit_trajectory_file(
                _write_records(Path(directory), records),
                policy_path=FIXTURE_POLICY,
            )
        self.assertTrue(report["contract_passed"])
        self.assertEqual(0, report["metrics"]["action_chain_break_count"])

    def test_terminal_state_only_matches_last_action_when_adjacency_is_explicit(self) -> None:
        records = _fixture_records()
        records[4]["observation"]["state_id"] = "non-adjacent-terminal-state"
        records[4]["trajectory"]["state_id"] = "non-adjacent-terminal-state"
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = audit_trajectory_file(
                _write_records(root, records), policy_path=FIXTURE_POLICY
            )
            self.assertTrue(report["contract_passed"])
            self.assertEqual(0, report["metrics"]["terminal_state_mismatch_count"])
            records[4]["observation"]["metadata"]["terminal_adjacency"] = "immediate"
            report = audit_trajectory_file(
                _write_records(root, records), policy_path=FIXTURE_POLICY
            )
        self.assertFalse(report["contract_passed"])
        self.assertEqual(1, report["metrics"]["terminal_state_mismatch_count"])

    def test_audit_hashes_the_same_single_byte_snapshot_that_it_parses(self) -> None:
        payload = TRAJECTORY_FIXTURE.read_bytes()
        expected_hash = hashlib.sha256(payload).hexdigest()
        original = Path.read_bytes
        calls = 0

        def counted_read(path: Path) -> bytes:
            nonlocal calls
            calls += 1
            return original(path)

        with mock.patch.object(Path, "read_bytes", counted_read):
            report = audit_trajectory_file(
                TRAJECTORY_FIXTURE,
                policy_path=FIXTURE_POLICY,
            )
        self.assertEqual(1, calls)
        self.assertEqual(expected_hash, report["input_sha256"])

    def test_cli_returns_zero_only_for_a_training_ready_report(self) -> None:
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            ready_code = main(
                [
                    "audit-trajectories",
                    "--input",
                    str(TRAJECTORY_FIXTURE),
                    "--policy",
                    str(FIXTURE_POLICY),
                    "--source-kind",
                    SOURCE_KIND_SYNTHETIC_FIXTURE,
                ]
            )
        self.assertEqual(0, ready_code)
        ready_report = json.loads(stdout.getvalue())
        self.assertTrue(ready_report["training_ready"])
        self.assertEqual(SOURCE_KIND_SYNTHETIC_FIXTURE, ready_report["source_kind"])

        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            production_code = main(
                ["audit-trajectories", "--input", str(TRAJECTORY_FIXTURE)]
            )
        self.assertEqual(3, production_code)
        self.assertFalse(json.loads(stdout.getvalue())["training_ready"])

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                no_data_code = main(
                    [
                        "audit-runtime-trajectories",
                        "--input",
                        str(root / "missing.jsonl"),
                        "--snapshot-dir",
                        str(root / "snapshots"),
                    ]
                )
        self.assertEqual(4, no_data_code)
        self.assertEqual("NO_DATA", json.loads(stdout.getvalue())["status"])


if __name__ == "__main__":
    unittest.main()
