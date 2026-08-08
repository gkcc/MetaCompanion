from __future__ import annotations

import json
from dataclasses import dataclass, fields
from pathlib import Path
from typing import Any, Mapping


TRAINING_LOG_FILENAME = "training-v2.jsonl"
DEFAULT_TRAINING_LOG_PATH = "data/" + TRAINING_LOG_FILENAME


def training_log_path_for_data_dir(data_dir: str | Path) -> str:
    """Return the isolated v2 log path used by managed HDT worker launches."""

    return str(Path(data_dir) / TRAINING_LOG_FILENAME)


@dataclass(frozen=True)
class SolverConfig:
    host: str = "127.0.0.1"
    port: int = 17853
    default_time_budget_ms: int = 2500
    max_time_budget_ms: int = 10000
    default_max_iterations: int = 5000
    max_iterations: int = 50000
    max_depth: int = 24
    top_k: int = 3
    exploration_constant: float = 1.35
    max_request_bytes: int = 2 * 1024 * 1024
    training_log_path: str | None = DEFAULT_TRAINING_LOG_PATH
    advisor_data_path: str = ""

    @classmethod
    def from_mapping(cls, raw: Mapping[str, Any]) -> "SolverConfig":
        known = {field.name for field in fields(cls)}
        unknown = sorted(set(raw) - known)
        if unknown:
            raise ValueError(f"unknown config keys: {', '.join(unknown)}")
        config = cls(**dict(raw))
        config.validate()
        return config

    def validate(self) -> None:
        if self.host != "127.0.0.1":
            raise ValueError("solver host must be exactly 127.0.0.1")
        if not 0 <= self.port <= 65535:
            raise ValueError("port must be between 0 and 65535")
        if not 25 <= self.default_time_budget_ms <= self.max_time_budget_ms:
            raise ValueError("default_time_budget_ms must be within [25, max_time_budget_ms]")
        if self.max_time_budget_ms > 60_000:
            raise ValueError("max_time_budget_ms may not exceed 60000")
        if not 1 <= self.default_max_iterations <= self.max_iterations:
            raise ValueError("default_max_iterations must be within [1, max_iterations]")
        if self.max_depth < 2:
            raise ValueError("max_depth must be at least 2")
        if not 1 <= self.top_k <= 10:
            raise ValueError("top_k must be between 1 and 10")
        if self.exploration_constant <= 0:
            raise ValueError("exploration_constant must be positive")
        if self.max_request_bytes < 1024:
            raise ValueError("max_request_bytes must be at least 1024")


def load_config(path: str | Path | None = None) -> SolverConfig:
    if path is None:
        config = SolverConfig()
        config.validate()
        return config
    config_path = Path(path)
    with config_path.open("r", encoding="utf-8") as handle:
        raw = json.load(handle)
    if not isinstance(raw, dict):
        raise ValueError("config root must be a JSON object")
    return SolverConfig.from_mapping(raw)
