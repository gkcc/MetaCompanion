from __future__ import annotations

import hashlib
import json
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.api import create_server
from metacompanion_solver.behavior import (
    BEHAVIOR_CORPUS_FILENAME,
    BehaviorCorpusError,
    BehaviorRecord,
    audit_behavior_corpus,
)
from metacompanion_solver.config import SolverConfig
from metacompanion_solver.logging_store import JsonlTrainingLogger
from metacompanion_solver.schemas import Observation, SearchResult, SolveRequest
from metacompanion_solver.service import SolverService

from helpers import advisor_entity, advisor_snapshot, native_request_dict


TOKEN = "unit-test-session-token-123"


def behavior_payload() -> dict:
    def public_player(role: str, hero_id: str) -> dict:
        return {
            "player_id": role,
            "hero": {
                "entity_id": hero_id,
                "card_id": "HERO_01" if role == "friendly" else "HERO_02",
                "card_type": "HERO",
                "health": 30,
                "current_health": 30,
                "name": "Private hero name",
            },
            "hero_power": None,
            "weapon": None,
            "hand": [],
            "board": [],
            "mana": 4,
            "max_mana": 4,
            "armor": 0,
            "deck_size": 20,
            "fatigue": 0,
            "hero_power_available": False,
            "spell_power": 0,
        }

    state = {
        "state_id": "behavior-state-1",
        "turn": 4,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "friendly": public_player("friendly", "1"),
        "opponent": public_player("opponent", "2"),
        "patch": "33.2",
        "mode": "standard",
    }
    post_state = dict(state)
    post_state["state_id"] = "behavior-state-2"
    return {
        "schema": "advisor-behavior-v1",
        "game_id": "private-behavior-game",
        "behavior_sequence": 1,
        "observed_at_utc": "2026-07-31T12:00:00Z",
        "actor_side": "local",
        "actor_player_id": "friendly",
        "actor_evidence": "active_player",
        "identity_status": "event_only",
        "visibility_status": "public_pre_state",
        "boundary_status": "isolated",
        "source_event": "turn_passed_to_opponent",
        "action": {
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": "",
        },
        "pre_state": state,
        "post_state": post_state,
        "behavior_eligible": True,
        "rl_training_eligible": False,
    }


class _SlowSearcher:
    def search(self, request_id, state, limits, cancel_event):
        deadline = time.monotonic() + 2
        while not cancel_event.is_set() and time.monotonic() < deadline:
            time.sleep(0.005)
        return SearchResult(
            request_id=request_id,
            state_id=state.state_id,
            status="cancelled" if cancel_event.is_set() else "ok",
            elapsed_ms=1,
            iterations=1,
            recommendations=(),
            progress=(),
            coverage={"rules_model": "test", "approximate_effects": []},
        )


class ApiTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.log_path = Path(self.temp.name) / "training.jsonl"
        config = SolverConfig(training_log_path=str(self.log_path))
        self.service = SolverService(config, logger=JsonlTrainingLogger(self.log_path))
        self.server = create_server(self.service, TOKEN, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"

    def tearDown(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        self.temp.cleanup()

    def request(self, path: str, payload=None, token: str | None = TOKEN):
        data = json.dumps(payload).encode() if payload is not None else None
        headers = {"Content-Type": "application/json"}
        if token:
            headers["X-Advisor-Token"] = token
        request = urllib.request.Request(self.base + path, data=data, headers=headers)
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, json.loads(response.read())

    def test_health_requires_auth_and_reports_logging(self) -> None:
        with self.assertRaises(urllib.error.HTTPError) as context:
            self.request("/v1/health", token=None)
        self.assertEqual(401, context.exception.code)
        status, payload = self.request("/v1/health")
        self.assertEqual(200, status)
        self.assertTrue(payload["is_ready"])
        self.assertTrue(payload["training_log_enabled"])
        self.assertTrue(payload["behavior_log_enabled"])
        self.assertTrue(payload["behavior_log_healthy"])
        self.assertEqual("python", payload["backend"])
        self.assertEqual("full", payload["parity_profile"])
        self.assertTrue(payload["production_ready"])

    def test_training_log_path_cannot_alias_behavior_corpus(self) -> None:
        collision = Path(self.temp.name) / BEHAVIOR_CORPUS_FILENAME
        with self.assertRaises(BehaviorCorpusError) as caught:
            SolverService(
                SolverConfig(training_log_path=str(collision)),
                logger=JsonlTrainingLogger(collision),
            )
        self.assertEqual(
            "behavior_corpus_path_must_be_independent", caught.exception.code
        )

    def test_python_compatibility_solver_accepts_valid_hdt_root_set_as_best_effort(self) -> None:
        payload = native_request_dict()
        payload["hdt_root_candidates"] = {
            "contract": "hdt_complete_main_action_options_v1",
            "state_id": payload["state"]["state_id"],
            "frame_id": 1,
            "collector_epoch": 1,
            "frame_watermark": 1,
            "candidate_set_complete": True,
            "candidates": [
                {
                    "option_id": 0,
                    "action": {
                        "kind": "end_turn",
                        "source_entity_id": "",
                        "target_entity_id": "",
                        "card_id": "",
                        "board_position": 0,
                    },
                    "target_evidence": "not_applicable",
                    "position_evidence": "not_applicable",
                }
            ],
        }

        status, body = self.request("/v1/solve", payload)

        self.assertEqual(200, status)
        self.assertEqual("partial", body["status"])
        self.assertGreaterEqual(len(body["recommendations"]), 1)
        candidate_coverage = body["coverage"]["hdt_root_candidates"]
        self.assertTrue(candidate_coverage["schema_validated"])
        self.assertFalse(candidate_coverage["used_for_ranking"])
        self.assertFalse(candidate_coverage["exact"])
        self.assertFalse(candidate_coverage["optimality_verified"])
        for recommendation in body["recommendations"]:
            self.assertFalse(recommendation["is_proven_lethal"])
            self.assertFalse(recommendation["is_response_verified"])
            self.assertFalse(recommendation["response_search_complete"])
            self.assertIsNone(recommendation["is_safe_after_response"])
            self.assertIsNone(recommendation["verified_portfolio_regret"])
            self.assertEqual("fallback", recommendation["alternative_kind"])
            self.assertIn(
                "approximate_hdt_root_candidates_ignored",
                {item["code"] for item in recommendation["annotations"]},
            )

    def test_python_compatibility_solver_still_rejects_invalid_hdt_root_schema(self) -> None:
        payload = native_request_dict()
        payload["hdt_root_candidates"] = {
            "contract": "invalid_contract",
            "state_id": payload["state"]["state_id"],
            "frame_id": 1,
            "collector_epoch": 1,
            "frame_watermark": 1,
            "candidate_set_complete": True,
            "candidates": [],
        }

        with self.assertRaises(urllib.error.HTTPError) as caught:
            self.request("/v1/solve", payload)
        body = json.loads(caught.exception.read().decode("utf-8"))

        self.assertEqual(400, caught.exception.code)
        self.assertEqual(
            "request.hdt_root_candidates.contract",
            body["error"]["path"],
        )

    def test_health_reports_existing_behavior_damage_before_first_append(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            training_path = Path(directory) / "training-v2.jsonl"
            config = SolverConfig(training_log_path=str(training_path))
            seed = SolverService(config, logger=JsonlTrainingLogger(training_path))
            seed.append_behavior(behavior_payload())
            behavior_path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            with behavior_path.open("ab") as handle:
                handle.write(b"not-json\n")

            service = SolverService(config, logger=JsonlTrainingLogger(training_path))
            self.assertFalse(service.health()["behavior_log_healthy"])
            server = create_server(service, TOKEN, port=0)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                endpoint = f"http://127.0.0.1:{server.server_address[1]}/v1/health"
                request = urllib.request.Request(
                    endpoint, headers={"X-Advisor-Token": TOKEN}
                )
                with urllib.request.urlopen(request, timeout=5) as response:
                    payload = json.loads(response.read())
                self.assertFalse(payload["behavior_log_healthy"])
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_observe_auth_schema_and_sanitization(self) -> None:
        observation = {
            "api_version": "1.0",
            "kind": "action",
            "state_id": "s1",
            "game_id": "private-game-id",
            "action": {
                "kind": "play_card",
                "source_entity_id": 42,
                "card_id": "CARD_001",
            },
            "metadata": {"battle_tag": "Secret#1234", "mode": "arena"},
        }
        status, payload = self.request("/v1/observe", observation)
        self.assertEqual(200, status)
        self.assertEqual("ok", payload["status"])
        self.assertTrue(payload["logged"])
        logged = json.loads(self.log_path.read_text(encoding="utf-8"))
        self.assertEqual("observation", logged["kind"])
        self.assertTrue(logged["observation"]["game_id"].startswith("anon-"))
        self.assertNotIn("battle_tag", logged["observation"]["metadata"])
        with self.assertRaises(urllib.error.HTTPError) as context:
            self.request(
                "/v1/observe",
                {"api_version": "1.0", "kind": "result", "state_id": "s1", "result": "maybe"},
            )
        self.assertEqual(400, context.exception.code)

    def test_terminal_result_response_is_content_addressed_and_idempotent(self) -> None:
        observation = {
            "api_version": "1.0",
            "kind": "result",
            "state_id": "terminal-state",
            "game_id": "private-terminal-game",
            "observed_at_utc": "2026-07-31T12:00:00Z",
            "result": "win",
            "metadata": {
                "capture_contract": "terminal_result_v1",
                "completeness": "terminal_result",
                "training_eligible": True,
                "result_metadata_version": 1,
                "terminal_adjacency": None,
            },
        }
        status, first = self.request("/v1/observe", observation)
        self.assertEqual(200, status)
        self.assertTrue(first["logged"])
        self.assertFalse(first["duplicate"])
        self.assertRegex(first["result_id"], r"^result-[0-9a-f]{64}$")
        self.assertTrue(first["game_id"].startswith("anon-"))

        status, retry = self.request("/v1/observe", observation)
        self.assertEqual(200, status)
        self.assertFalse(retry["logged"])
        self.assertTrue(retry["duplicate"])
        self.assertEqual(first["result_id"], retry["result_id"])
        self.assertEqual(1, len(self.log_path.read_text(encoding="utf-8").splitlines()))

    def test_terminal_result_rejects_float_metadata_before_write(self) -> None:
        observation = {
            "api_version": "1.0",
            "kind": "result",
            "state_id": "terminal-state",
            "game_id": "private-terminal-game",
            "result": "win",
            "metadata": {"sample_weight": 1e-7},
        }
        with self.assertRaises(urllib.error.HTTPError) as caught:
            self.request("/v1/observe", observation)
        self.assertEqual(400, caught.exception.code)
        payload = json.loads(caught.exception.read())
        self.assertEqual("schema_error", payload["error"]["code"])
        self.assertEqual("request.metadata.sample_weight", payload["error"]["path"])
        self.assertFalse(self.log_path.exists())

    def test_behavior_endpoint_is_independent_private_and_idempotent(self) -> None:
        behavior = behavior_payload()
        status, first = self.request("/v1/behavior", behavior)
        self.assertEqual(200, status)
        self.assertEqual("ok", first["status"])
        self.assertTrue(first["logged"])
        self.assertFalse(first["duplicate"])
        expected_game_id = "anon-" + hashlib.sha256(
            behavior["game_id"].encode("utf-8")
        ).hexdigest()[:16]
        self.assertEqual(expected_game_id, first["game_id"])
        self.assertRegex(first["behavior_id"], r"^behavior-[0-9a-f]{64}$")
        self.assertEqual(behavior["behavior_sequence"], first["behavior_sequence"])
        self.assertNotIn(behavior["game_id"], json.dumps(first, sort_keys=True))

        status, retry = self.request("/v1/behavior", behavior)
        self.assertEqual(200, status)
        self.assertFalse(retry["logged"])
        self.assertTrue(retry["duplicate"])
        self.assertEqual(first["behavior_id"], retry["behavior_id"])
        self.assertEqual(first["game_id"], retry["game_id"])
        self.assertEqual(first["behavior_sequence"], retry["behavior_sequence"])

        corpus_path = Path(self.temp.name) / BEHAVIOR_CORPUS_FILENAME
        lines = corpus_path.read_text(encoding="utf-8").splitlines()
        self.assertEqual(1, len(lines))
        record = BehaviorRecord.from_dict(json.loads(lines[0]))
        self.assertEqual(first["behavior_id"], record.behavior_id)
        self.assertEqual(first["game_id"], record.game_id)
        self.assertEqual(first["behavior_sequence"], record.behavior_sequence)
        self.assertFalse(record.value["rl_training_eligible"])
        corpus_text = corpus_path.read_text(encoding="utf-8")
        self.assertNotIn("private-behavior-game", corpus_text)
        self.assertNotIn("Private hero name", corpus_text)
        self.assertTrue(audit_behavior_corpus(corpus_path)["valid"])

        invalid = dict(behavior)
        invalid["raw_power_log"] = "forbidden"
        with self.assertRaises(urllib.error.HTTPError) as context:
            self.request("/v1/behavior", invalid)
        self.assertEqual(400, context.exception.code)

    def test_solve_endpoint(self) -> None:
        status, payload = self.request("/v1/solve", native_request_dict())
        self.assertEqual(200, status)
        self.assertEqual("request-1", payload["request_id"])
        self.assertIn(payload["status"], {"ok", "partial"})
        self.assertTrue(payload["is_final"])

    def test_solve_accepts_hdt_wire_snapshot(self) -> None:
        request = {
            "api_version": "1.0",
            "request_id": "hdt-wire-request",
            "state": advisor_snapshot(),
            "options": {
                "time_budget_ms": 25,
                "top_k": 2,
                "allow_approximate_effects": True,
                "environment_version": "arena-test",
            },
        }
        status, payload = self.request("/v1/solve", request)
        self.assertEqual(200, status)
        self.assertEqual("partial", payload["status"])
        self.assertGreaterEqual(len(payload["recommendations"]), 1)
        self.assertEqual("arena-test", payload["environment_version"])
        self.assertLessEqual(len(payload["recommendations"]), 2)
        self.assertIn("overall", payload["coverage"])
        self.assertFalse(payload["coverage"]["exact"])
        for recommendation in payload["recommendations"]:
            self.assertFalse(recommendation["is_proven_lethal"])
            self.assertFalse(recommendation["is_response_verified"])
            self.assertFalse(recommendation["response_search_complete"])
            self.assertIsNone(recommendation["is_safe_after_response"])
            self.assertIsNone(recommendation["verified_portfolio_regret"])
            self.assertEqual("fallback", recommendation["alternative_kind"])
            self.assertIn(
                "approximate_playable_unsupported_rule",
                {item["code"] for item in recommendation["annotations"]},
            )

    def test_solve_redacts_hidden_opponent_hand_before_logging(self) -> None:
        snapshot = advisor_snapshot()
        hidden = advisor_entity(
            99,
            "SECRET_INTERNAL_CARD",
            "SPELL",
            name="Secret internal name",
            cost=9,
            text="Secret internal text",
        )
        hidden.update(
            {
                "dbf_id": 987654,
                "zone": "HAND",
                "zone_id": 3,
                "zone_position": 2,
                "controller_id": 2,
                "visibility": "hidden",
                "tags": {
                    "ZONE": 3,
                    "ZONE_POSITION": 2,
                    "CONTROLLER": 2,
                    "COST": 9,
                    "DBF_ID": 987654,
                },
            }
        )
        snapshot["opponent"]["hand"] = [hidden]
        request = {
            "api_version": "1.0",
            "request_id": "hidden-hdt-request",
            "state": snapshot,
            "options": {"time_budget_ms": 25, "max_iterations": 3, "top_k": 1},
        }

        status, _ = self.request("/v1/solve", request)
        self.assertEqual(200, status)
        log_text = self.log_path.read_text(encoding="utf-8")
        self.assertNotIn("SECRET_INTERNAL_CARD", log_text)
        self.assertNotIn("Secret internal name", log_text)
        self.assertNotIn("Secret internal text", log_text)
        self.assertNotIn("987654", log_text)
        logged = json.loads(log_text)
        entity = logged["request"]["state"]["opponent"]["hand"][0]
        self.assertEqual("99", entity["entity_id"])
        self.assertEqual("hidden", entity["visibility"])
        self.assertEqual({"ZONE": 3, "ZONE_POSITION": 2, "CONTROLLER": 2}, entity["tags"])
        self.assertLessEqual(set(entity), {"entity_id", "visibility", "tags"})

    def test_cancel_interrupts_active_service_request(self) -> None:
        service = SolverService(
            SolverConfig(training_log_path=None),
            searcher=_SlowSearcher(),
            logger=JsonlTrainingLogger(None),
        )
        request = SolveRequest.from_dict(native_request_dict())
        result_box = []
        worker = threading.Thread(target=lambda: result_box.append(service.solve(request)))
        worker.start()
        deadline = time.monotonic() + 1
        while service.health()["active_solves"] == 0 and time.monotonic() < deadline:
            time.sleep(0.005)
        response = service.cancel(request_id=request.request_id)
        worker.join(timeout=2)
        self.assertEqual("cancellation_requested", response["status"])
        self.assertEqual("cancelled", result_box[0].status)

    def test_observe_reports_not_logged_when_training_log_is_disabled(self) -> None:
        service = SolverService(
            SolverConfig(training_log_path=None),
            logger=JsonlTrainingLogger(None),
        )
        observation = Observation.from_dict(
            {
                "api_version": "1.0",
                "kind": "result",
                "state_id": "s-disabled",
                "result": "unknown",
            }
        )
        self.assertFalse(service.health()["training_log_enabled"])
        self.assertFalse(service.health()["behavior_log_enabled"])
        self.assertFalse(service.observe(observation)["logged"])

    def test_logging_failure_does_not_break_observation_service(self) -> None:
        blocker = Path(self.temp.name) / "not-a-directory"
        blocker.write_text("blocked", encoding="utf-8")
        service = SolverService(
            SolverConfig(training_log_path=str(blocker / "training.jsonl")),
            logger=JsonlTrainingLogger(blocker / "training.jsonl"),
        )
        observation = Observation.from_dict(
            {
                "api_version": "1.0",
                "kind": "result",
                "state_id": "s-failed-log",
                "result": "unknown",
            }
        )
        self.assertFalse(service.observe(observation)["logged"])
        self.assertFalse(service.health()["training_log_healthy"])

    def test_server_rejects_non_loopback_binding(self) -> None:
        with self.assertRaises(ValueError):
            create_server(self.service, TOKEN, host="0.0.0.0", port=0)


if __name__ == "__main__":
    unittest.main()
