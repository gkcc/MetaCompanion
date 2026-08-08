from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.config import (
    DEFAULT_TRAINING_LOG_PATH,
    TRAINING_LOG_FILENAME,
    SolverConfig,
    load_config,
    training_log_path_for_data_dir,
)


class ConfigTests(unittest.TestCase):
    def test_default_training_log_uses_versioned_filename(self) -> None:
        config = SolverConfig()
        self.assertEqual("training-v2.jsonl", TRAINING_LOG_FILENAME)
        self.assertEqual("data/training-v2.jsonl", DEFAULT_TRAINING_LOG_PATH)
        self.assertEqual(DEFAULT_TRAINING_LOG_PATH, config.training_log_path)
        self.assertEqual(
            str(Path("runtime-data") / "training-v2.jsonl"),
            training_log_path_for_data_dir(Path("runtime-data")),
        )

        default_file = Path(__file__).resolve().parents[1] / "config.default.json"
        default_payload = json.loads(default_file.read_text(encoding="utf-8"))
        self.assertEqual(DEFAULT_TRAINING_LOG_PATH, default_payload["training_log_path"])

    def test_non_loopback_host_is_rejected(self) -> None:
        with self.assertRaises(ValueError):
            SolverConfig(host="0.0.0.0").validate()

    def test_null_training_log_is_allowed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.json"
            path.write_text('{"training_log_path":null}', encoding="utf-8")
            config = load_config(path)
            self.assertIsNone(config.training_log_path)


if __name__ == "__main__":
    unittest.main()
