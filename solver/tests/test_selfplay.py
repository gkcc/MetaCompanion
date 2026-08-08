from __future__ import annotations

import json
import tempfile
import threading
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.selfplay import SelfPlaySettings, run_generic_self_play

from helpers import native_request_dict


class SelfPlayTests(unittest.TestCase):
    def test_bounded_run_writes_trajectory_checkpoint_and_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "states.json"
            output = root / "run"
            source.write_text(json.dumps([native_request_dict()]), encoding="utf-8")
            manifest = run_generic_self_play(
                source,
                output,
                SelfPlaySettings(
                    episodes=1,
                    max_turns=2,
                    time_limit_seconds=5,
                    search_budget_ms=25,
                    max_iterations=3,
                    max_depth=6,
                ),
            )
            self.assertEqual("completed", manifest["status"])
            self.assertFalse(manifest["is_reinforcement_learning_model"])
            self.assertEqual(1, manifest["completed_episodes"])
            self.assertTrue((output / "trajectories.jsonl").is_file())
            checkpoint = json.loads((output / "checkpoint.json").read_text(encoding="utf-8"))
            self.assertEqual(1, checkpoint["next_episode"])
            self.assertTrue((output / "manifest.json").is_file())

    def test_pre_cancelled_run_is_bounded_and_checkpointed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "states.json"
            output = root / "cancelled"
            source.write_text(json.dumps([native_request_dict()]), encoding="utf-8")
            event = threading.Event()
            event.set()
            manifest = run_generic_self_play(
                source,
                output,
                SelfPlaySettings(episodes=2, time_limit_seconds=5, search_budget_ms=25),
                cancel_event=event,
            )
            self.assertEqual("cancelled", manifest["status"])
            self.assertEqual(0, manifest["completed_episodes"])
            self.assertTrue((output / "checkpoint.json").is_file())


if __name__ == "__main__":
    unittest.main()
