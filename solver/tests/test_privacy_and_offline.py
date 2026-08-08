from __future__ import annotations

import hashlib
import json
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

from metacompanion_solver.config import TRAINING_LOG_FILENAME
from metacompanion_solver.errors import ResultObservationConflictError, SchemaError
from metacompanion_solver.logging_store import (
    TRAINING_LOG_SCHEMA_ID,
    JsonlTrainingLogger,
    deterministic_game_split,
    sanitize_for_training,
)
from metacompanion_solver.offline import load_records
from metacompanion_solver.schemas import (
    TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
    TRANSITION_CANDIDATE_STATUS,
    TRANSITION_CANDIDATE_VERIFICATION,
    Action,
    ActionKind,
    GameState,
    Observation,
    SearchResult,
    SolveRequest,
)
from metacompanion_solver.trajectory import audit_trajectory_file

from helpers import state


class PrivacyAndOfflineTests(unittest.TestCase):
    def test_terminal_writer_rejects_float_metadata_but_action_keeps_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            logger = JsonlTrainingLogger(path)
            terminal = Observation(
                kind="result",
                state_id="terminal-state",
                result="win",
                metadata={"sample_weight": 1e-7},
            )
            with self.assertRaises(SchemaError) as caught:
                logger.append_observation_with_ack(terminal)
            self.assertEqual("request.metadata.sample_weight", caught.exception.path)
            self.assertFalse(path.exists())

            action = Observation(
                kind="action",
                state_id="action-state",
                action=Action(ActionKind.END_TURN),
                metadata={"sample_weight": 1e-7},
            )
            self.assertTrue(logger.append_observation(action))
            record = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual(1e-7, record["observation"]["metadata"]["sample_weight"])

    def test_terminal_result_retry_is_idempotent_across_restart_and_conflicts_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            observation = Observation(
                kind="result",
                state_id="terminal-state",
                game_id="private-terminal-game",
                observed_at_utc="2026-07-31T12:00:00Z",
                result="win",
                metadata={
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": True,
                },
            )
            first = JsonlTrainingLogger(path).append_observation_with_ack(observation)
            self.assertTrue(first.logged)
            self.assertFalse(first.duplicate)
            self.assertRegex(first.result_id, r"^result-[0-9a-f]{64}$")

            retry = JsonlTrainingLogger(path).append_observation_with_ack(observation)
            self.assertFalse(retry.logged)
            self.assertTrue(retry.duplicate)
            self.assertEqual(first.result_id, retry.result_id)
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

            conflict = Observation(
                kind="result",
                state_id="different-terminal-state",
                game_id="private-terminal-game",
                result="loss",
                metadata=observation.metadata,
            )
            with self.assertRaises(ResultObservationConflictError):
                JsonlTrainingLogger(path).append_observation_with_ack(conflict)
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

    def test_terminal_ack_waits_for_fsync_and_sync_failure_forces_disk_rescan(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            observation = Observation(
                kind="result",
                state_id="terminal-state",
                game_id="private-terminal-game",
                result="win",
                metadata={
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": True,
                },
            )
            logger = JsonlTrainingLogger(path)
            with mock.patch(
                "metacompanion_solver.logging_store.os.fsync",
                side_effect=OSError("injected durability failure"),
            ) as fsync:
                failed = logger.append_observation_with_ack(observation)
            fsync.assert_called_once()
            self.assertFalse(failed.logged)
            self.assertFalse(logger.healthy)
            del logger

            # The stale marker died with the first worker.  A restarted worker must
            # still refuse a duplicate ACK while its own rebuild fsync fails.
            restarted = JsonlTrainingLogger(path)
            with mock.patch(
                "metacompanion_solver.logging_store.os.fsync",
                side_effect=OSError("injected restart durability failure"),
            ) as restart_fsync:
                with self.assertRaises(OSError):
                    restarted.append_observation_with_ack(observation)
                self.assertFalse(restarted.healthy)
            self.assertGreaterEqual(restart_fsync.call_count, 2)
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

            # Only a successful rebuild fsync may return duplicate; the complete
            # row from the first attempt remains the sole terminal result row.
            retry = restarted.append_observation_with_ack(observation)
            self.assertFalse(retry.logged)
            self.assertTrue(retry.duplicate)
            self.assertTrue(restarted.healthy)
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

    def test_restart_archives_torn_tail_preserves_history_and_writes_one_result(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            complete_history = b'{"kind":"legacy-complete"}\n'
            torn_fragment = b'{"kind":"observation","observation":'
            path.write_bytes(complete_history + torn_fragment)
            observation = Observation(
                kind="result",
                state_id="terminal-state",
                game_id="private-terminal-game",
                result="win",
                metadata={
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": True,
                },
            )

            logger = JsonlTrainingLogger(path)
            outcome = logger.append_observation_with_ack(observation)
            self.assertTrue(outcome.logged)
            self.assertTrue(logger.healthy)
            contents = path.read_bytes()
            self.assertTrue(contents.startswith(complete_history))
            self.assertEqual(2, contents.count(b"\n"))
            self.assertEqual(2, len(contents.splitlines()))

            archives = list(Path(directory).glob("*.torn-tail.*.fragment"))
            self.assertEqual(1, len(archives))
            archive = archives[0]
            self.assertEqual(torn_fragment, archive.read_bytes())
            self.assertFalse(archive.stat().st_mode & 0o200)

            retry = JsonlTrainingLogger(path).append_observation_with_ack(observation)
            self.assertFalse(retry.logged)
            self.assertTrue(retry.duplicate)
            self.assertEqual(2, len(path.read_text(encoding="utf-8").splitlines()))
            archive.chmod(0o666)

    def test_complete_middle_corruption_is_not_repaired_and_health_is_false(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            damaged = b'{"kind":"legacy-complete"}\nnot-json\n'
            path.write_bytes(damaged)
            logger = JsonlTrainingLogger(path)
            self.assertFalse(logger.healthy)
            with self.assertRaises(json.JSONDecodeError):
                logger.append_observation_with_ack(
                    Observation(
                        kind="result",
                        state_id="terminal-state",
                        game_id="private-terminal-game",
                        result="win",
                        metadata={
                            "capture_contract": "terminal_result_v1",
                            "completeness": "terminal_result",
                            "training_eligible": True,
                        },
                    )
                )
            self.assertFalse(logger.healthy)
            self.assertEqual(damaged, path.read_bytes())
            self.assertEqual([], list(Path(directory).glob("*.torn-tail.*.fragment")))

    def test_complete_json_object_missing_only_newline_is_preserved_and_indexed(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            observation = Observation(
                kind="result",
                state_id="terminal-state",
                game_id="private-terminal-game",
                result="win",
                metadata={
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": True,
                },
            )
            first = JsonlTrainingLogger(path).append_observation_with_ack(observation)
            self.assertTrue(first.logged)
            contents = path.read_bytes()
            self.assertTrue(contents.endswith(b"\n"))
            path.write_bytes(contents[:-1])

            logger = JsonlTrainingLogger(path)
            retry = logger.append_observation_with_ack(observation)
            self.assertFalse(retry.logged)
            self.assertTrue(retry.duplicate)
            self.assertTrue(logger.healthy)
            self.assertTrue(path.read_bytes().endswith(b"\n"))
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))
            self.assertEqual([], list(Path(directory).glob("*.torn-tail.*.fragment")))

    def test_conflicting_durable_results_mark_index_health_unhealthy(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            first = Observation(
                kind="result",
                state_id="state-one",
                game_id="private-terminal-game",
                result="win",
                metadata={
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": True,
                },
            )
            seed = JsonlTrainingLogger(path)
            self.assertTrue(seed.append_observation_with_ack(first).logged)
            conflicting = Observation(
                kind="result",
                state_id="state-two",
                game_id="private-terminal-game",
                result="loss",
                metadata=first.metadata,
            )
            conflicting_record = seed._build_observation_record_locked(conflicting)
            conflicting_line = json.dumps(
                sanitize_for_training(conflicting_record),
                ensure_ascii=False,
                separators=(",", ":"),
                sort_keys=True,
            )
            with path.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(conflicting_line + "\n")
            original = path.read_bytes()

            logger = JsonlTrainingLogger(path)
            self.assertFalse(logger.healthy)
            with self.assertRaises(ResultObservationConflictError):
                logger.append_observation_with_ack(first)
            self.assertFalse(logger.healthy)
            self.assertEqual(original, path.read_bytes())

    def test_tail_without_any_complete_line_is_fully_archived_before_retry(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            fragment = b'{"never":"completed"'
            path.write_bytes(fragment)
            observation = Observation(
                kind="result",
                state_id="terminal-state",
                game_id="private-terminal-game",
                result="win",
                metadata={
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": True,
                },
            )
            outcome = JsonlTrainingLogger(path).append_observation_with_ack(observation)
            self.assertTrue(outcome.logged)
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))
            archives = list(Path(directory).glob("*.torn-tail.*.fragment"))
            self.assertEqual(1, len(archives))
            self.assertEqual(fragment, archives[0].read_bytes())
            archives[0].chmod(0o666)

    def test_ordinary_action_observations_remain_append_only(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / TRAINING_LOG_FILENAME
            logger = JsonlTrainingLogger(path)
            action = Observation(
                kind="action",
                state_id="state-one",
                game_id="private-action-game",
                action=Action(ActionKind.END_TURN),
            )
            self.assertTrue(logger.append_observation(action))
            self.assertTrue(logger.append_observation(action))
            self.assertEqual(2, len(path.read_text(encoding="utf-8").splitlines()))

    def test_versioned_log_does_not_modify_legacy_training_log(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            legacy = root / "training.jsonl"
            legacy_contents = '{"legacy":true,"logged_at_utc":"2025-01-02T03:04:05Z"}\n'
            legacy.write_text(legacy_contents, encoding="utf-8")

            current = root / TRAINING_LOG_FILENAME
            logger = JsonlTrainingLogger(current)
            self.assertTrue(
                logger.append(
                    {
                        "kind": "observation",
                        "log_schema": TRAINING_LOG_SCHEMA_ID,
                    }
                )
            )

            self.assertEqual(legacy_contents, legacy.read_text(encoding="utf-8"))
            self.assertEqual(
                TRAINING_LOG_SCHEMA_ID,
                json.loads(current.read_text(encoding="utf-8"))["log_schema"],
            )

    def test_identity_sanitization_is_idempotent_and_drops_credentials_and_wall_clock(self) -> None:
        value = {
            "game_id": "private-game",
            "password": "do-not-write",
            "authorization": "Bearer do-not-write",
            "observed_at_utc": "2026-07-29T12:34:56Z",
            "current_deck": {"deck_id": "private-deck", "name": "Private name"},
            "nested": {"captured_at_utc": "2026-07-29T12:34:55Z"},
        }
        once = sanitize_for_training(value)
        twice = sanitize_for_training(once)
        self.assertEqual(once, twice)
        serialized = json.dumps(once)
        self.assertNotIn("do-not-write", serialized)
        self.assertNotIn("2026-07-29", serialized)
        self.assertNotIn("private-deck", serialized)
        self.assertNotIn("Private name", serialized)
        self.assertTrue(once["game_id"].startswith("anon-"))

    def test_logger_uses_the_anonymized_game_for_both_join_and_split(self) -> None:
        game = state()
        game.metadata["game_id"] = "private-game"
        request = SolveRequest(
            request_id="request-1",
            state=game,
            metadata={
                "decision_id": game.state_id,
                "solve_stage": "single",
                "trajectory_schema": "trajectory-readiness-v1",
                "capture_contract": "unit-test",
                "snapshot_sequence": 7,
            },
        )
        result = SearchResult(
            request_id=request.request_id,
            state_id=game.state_id,
            status="ok",
            elapsed_ms=1,
            iterations=1,
            recommendations=(),
            progress=(),
            coverage={
                "planner_model": "planner-v1",
                "rules_model": "rules-v1",
            },
        )
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "training.jsonl"
            logger = JsonlTrainingLogger(output)
            self.assertTrue(logger.append_solve(request, result))
            self.assertTrue(
                logger.append_observation(
                    Observation(
                        kind="action",
                        state_id=game.state_id,
                        game_id="private-game",
                        action=Action(ActionKind.END_TURN),
                        metadata={
                            "trajectory_schema": "trajectory-readiness-v1",
                            "decision_id": game.state_id,
                            "action_sequence": 1,
                            "pre_state_id": game.state_id,
                            "post_state_id": "",
                            "completeness": "partial_hdt_gameevents_v1",
                            "capture_contract": "partial_hdt_gameevents_v1",
                            "transition_status": "not_replayable",
                            "source_entity_resolution": "missing",
                            "target_entity_resolution": "missing",
                            "training_eligible": False,
                        },
                    )
                )
            )
            self.assertTrue(
                logger.append_observation(
                    Observation(
                        kind="result",
                        state_id=game.state_id,
                        game_id="private-game",
                        result="win",
                        metadata={
                            "trajectory_schema": "trajectory-readiness-v1",
                            "completeness": "terminal_result",
                            "capture_contract": "terminal_result_v1",
                            "training_eligible": True,
                        },
                    )
                )
            )
            audit = audit_trajectory_file(output)
            self.assertTrue(audit["contract_passed"])
            self.assertFalse(audit["training_ready"])
            self.assertEqual(1, audit["metrics"]["partial_action_count"])
            record = json.loads(output.read_text(encoding="utf-8").splitlines()[0])
        game_id = record["trajectory"]["game_id"]
        self.assertTrue(game_id.startswith("anon-"))
        self.assertEqual(game_id, record["request"]["state"]["metadata"]["game_id"])
        self.assertEqual(deterministic_game_split(game_id), record["trajectory"]["split"])
        self.assertEqual("planner-v1", record["trajectory"]["planner_model"])
        self.assertEqual("rules-v1", record["trajectory"]["rules_model"])
        self.assertEqual(64, len(record["trajectory"]["normalized_state_hash"]))
        self.assertEqual(7, record["trajectory"]["snapshot_sequence"])

    def test_concurrent_action_cannot_be_logged_before_its_pre_state_solve(self) -> None:
        game = state()
        game.metadata["game_id"] = "private-game"
        request = SolveRequest(
            request_id="request-ordered",
            state=game,
            metadata={
                "decision_id": game.state_id,
                "solve_stage": "single",
                "trajectory_schema": "trajectory-readiness-v1",
                "capture_contract": "unit-test",
            },
        )
        result = SearchResult(
            request_id=request.request_id,
            state_id=game.state_id,
            status="ok",
            elapsed_ms=1,
            iterations=1,
            recommendations=(),
            progress=(),
            coverage={},
        )
        observation = Observation(
            kind="action",
            state_id=game.state_id,
            game_id="private-game",
            action=Action(ActionKind.END_TURN),
            metadata={
                "trajectory_schema": "trajectory-readiness-v1",
                "decision_id": game.state_id,
                "action_sequence": 1,
                "pre_state_id": game.state_id,
                "post_state_id": "",
                "completeness": "partial_hdt_gameevents_v1",
                "capture_contract": "partial_hdt_gameevents_v1",
                "transition_status": "not_replayable",
                "source_entity_resolution": "missing",
                "target_entity_resolution": "missing",
                "training_eligible": False,
            },
        )
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "training-v2.jsonl"
            logger = JsonlTrainingLogger(output)
            original_append = logger.append
            solve_reached_append = threading.Event()
            observation_reached_append = threading.Event()
            release_solve = threading.Event()
            failures: list[BaseException] = []

            def controlled_append(record: dict[str, object]) -> bool:
                if record.get("kind") == "solve":
                    solve_reached_append.set()
                    if not release_solve.wait(timeout=2):
                        raise TimeoutError("test did not release solve append")
                elif record.get("kind") == "observation":
                    observation_reached_append.set()
                return original_append(record)

            logger.append = controlled_append  # type: ignore[method-assign]

            def run(callable_: object) -> None:
                try:
                    callable_()  # type: ignore[operator]
                except BaseException as exc:  # pragma: no cover - thread bridge
                    failures.append(exc)

            solve_thread = threading.Thread(
                target=run, args=(lambda: logger.append_solve(request, result),)
            )
            action_thread = threading.Thread(
                target=run, args=(lambda: logger.append_observation(observation),)
            )
            solve_thread.start()
            self.assertTrue(solve_reached_append.wait(timeout=1))
            action_thread.start()
            observation_reached_append.wait(timeout=0.2)
            release_solve.set()
            solve_thread.join(timeout=2)
            action_thread.join(timeout=2)

            self.assertFalse(failures)
            self.assertFalse(solve_thread.is_alive())
            self.assertFalse(action_thread.is_alive())
            kinds = [
                json.loads(line)["kind"]
                for line in output.read_text(encoding="utf-8").splitlines()
            ]
        self.assertEqual(["solve", "observation"], kinds)

    def test_logger_preserves_candidate_evidence_but_clamps_training_eligibility(self) -> None:
        pre_state = state()
        pre_state.metadata.update(
            {
                "game_id": "private-game",
                "snapshot_state_hash": "a" * 64,
                "snapshot_sequence": 10,
            }
        )
        post_state = GameState.from_dict(pre_state.to_dict())
        post_state.state_id = "state-post"
        post_state.turn += 1
        post_state.active_player_id = post_state.opponent.player_id
        post_state.metadata.update(
            {"snapshot_state_hash": "b" * 64, "snapshot_sequence": 11}
        )
        metadata = {
            "trajectory_schema": "trajectory-readiness-v1",
            "decision_id": "state-pre",
            "action_sequence": 1,
            "pre_state_id": "state-pre",
            "post_state_id": "state-post",
            "raw_pre_snapshot_hash": "a" * 64,
            "raw_post_snapshot_hash": "b" * 64,
            "pre_snapshot_sequence": 10,
            "post_snapshot_sequence": 11,
            "boundary_status": "isolated",
            "intervening_action_count": 0,
            "capture_warning_count": 0,
            "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
            "transition_status": TRANSITION_CANDIDATE_STATUS,
            "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
            "completeness": "complete_action_trace_v1",
            "training_eligible": True,
        }
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "training-v2.jsonl"
            logger = JsonlTrainingLogger(output)
            request = SolveRequest(
                request_id="candidate-pre",
                state=pre_state,
                metadata={
                    "trajectory_schema": "trajectory-readiness-v1",
                    "decision_id": pre_state.state_id,
                    "solve_stage": "single",
                    "capture_contract": "hdt-public-snapshot-v1",
                },
            )
            result = SearchResult(
                request_id=request.request_id,
                state_id=pre_state.state_id,
                status="ok",
                elapsed_ms=1,
                iterations=1,
                recommendations=(),
                progress=(),
                coverage={},
            )
            self.assertTrue(logger.append_solve(request, result))
            self.assertTrue(
                logger.append_observation(
                    Observation(
                        kind="action",
                        state_id="state-pre",
                        game_id="private-game",
                        action=Action(ActionKind.END_TURN),
                        pre_state=pre_state,
                        post_state=post_state,
                        metadata=metadata,
                    )
                )
            )
            record = json.loads(output.read_text(encoding="utf-8").splitlines()[1])

        logged_metadata = record["observation"]["metadata"]
        self.assertFalse(logged_metadata["training_eligible"])
        self.assertEqual("partial_hdt_gameevents_v1", logged_metadata["completeness"])
        self.assertEqual("state-post", record["trajectory"]["post_state_id"])
        self.assertEqual("a" * 64, record["trajectory"]["raw_pre_snapshot_hash"])
        self.assertEqual("b" * 64, record["trajectory"]["raw_post_snapshot_hash"])
        self.assertEqual(64, len(record["trajectory"]["pre_state_hash"]))
        self.assertEqual(64, len(record["trajectory"]["post_state_hash"]))
        self.assertNotEqual("a" * 64, record["trajectory"]["pre_state_hash"])
        self.assertNotEqual("b" * 64, record["trajectory"]["post_state_hash"])
        for label in ("pre", "post"):
            normalized = record["observation"][f"{label}_state"]
            expected = hashlib.sha256(
                json.dumps(
                    normalized, sort_keys=True, separators=(",", ":")
                ).encode("utf-8")
            ).hexdigest()
            self.assertEqual(expected, record["trajectory"][f"{label}_state_hash"])
        self.assertNotIn("snapshot_state_hash", record["observation"]["post_state"]["metadata"])
        self.assertEqual(
            TRANSITION_CANDIDATE_VERIFICATION,
            record["trajectory"]["transition_verification"],
        )

    def test_offline_loader_rejects_non_object_jsonl_records(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bad.jsonl"
            source.write_text('{"kind":"ok"}\n[1,2,3]\n', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "line 2 must be an object"):
                load_records(source)

    def test_offline_loader_rejects_non_object_array_items(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bad.json"
            source.write_text('[{"kind":"ok"},42]', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "item 1 must be an object"):
                load_records(source)


if __name__ == "__main__":
    unittest.main()
