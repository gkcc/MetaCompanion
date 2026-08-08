from __future__ import annotations

import json
import statistics
import time
from pathlib import Path
from typing import Any, Iterable, Mapping

from .config import SolverConfig
from .logging_store import JsonlTrainingLogger
from .schemas import SolveRequest
from .service import SolverService
from .simulator import apply_action


def load_records(path: str | Path) -> list[Mapping[str, Any]]:
    source = Path(path)
    text = source.read_text(encoding="utf-8")
    stripped = text.lstrip()
    if not stripped:
        return []
    if stripped.startswith("["):
        raw = json.loads(text)
        if not isinstance(raw, list):
            raise ValueError("JSON input must be an array")
        for index, item in enumerate(raw):
            if not isinstance(item, Mapping):
                raise ValueError(f"JSON array item {index} must be an object")
        return list(raw)
    if stripped.startswith("{"):
        try:
            raw = json.loads(text)
        except json.JSONDecodeError:
            pass
        else:
            if not isinstance(raw, Mapping):
                raise ValueError("JSON input must be an object")
            return [raw]
    records: list[Mapping[str, Any]] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        if not line.strip():
            continue
        try:
            item = json.loads(line)
        except json.JSONDecodeError as exc:
            raise ValueError(f"invalid JSONL at line {line_number}: {exc}") from exc
        if not isinstance(item, Mapping):
            raise ValueError(f"JSONL item at line {line_number} must be an object")
        records.append(item)
    return records


def extract_solve_requests(records: Iterable[Mapping[str, Any]]) -> list[SolveRequest]:
    requests: list[SolveRequest] = []
    for record in records:
        candidate = record.get("request") if record.get("kind") == "solve" else record
        if isinstance(candidate, Mapping) and "state" in candidate and "request_id" in candidate:
            requests.append(SolveRequest.from_dict(candidate))
    return requests


def replay_file(
    input_path: str | Path,
    output_path: str | Path,
    config: SolverConfig,
) -> int:
    requests = extract_solve_requests(load_records(input_path))
    service = SolverService(config, logger=JsonlTrainingLogger(None))
    destination = Path(output_path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("w", encoding="utf-8", newline="\n") as handle:
        for request in requests:
            result = service.solve(request)
            handle.write(json.dumps(result.to_dict(), ensure_ascii=False, separators=(",", ":")))
            handle.write("\n")
    return len(requests)


def _line_is_legal(request: SolveRequest, actions: Iterable[Any]) -> bool:
    state = request.state
    try:
        for action in actions:
            outcome = apply_action(state, action)
            state = outcome.state
            if outcome.ended_turn:
                break
    except Exception:
        return False
    return True


def benchmark_file(input_path: str | Path, config: SolverConfig) -> dict[str, Any]:
    requests = extract_solve_requests(load_records(input_path))
    service = SolverService(config, logger=JsonlTrainingLogger(None))
    latencies: list[float] = []
    legal_lines = 0
    total_lines = 0
    statuses: dict[str, int] = {}
    for request in requests:
        started = time.perf_counter()
        result = service.solve(request)
        latencies.append((time.perf_counter() - started) * 1000)
        statuses[result.status] = statuses.get(result.status, 0) + 1
        for recommendation in result.recommendations:
            total_lines += 1
            if _line_is_legal(request, recommendation.actions):
                legal_lines += 1
    ordered = sorted(latencies)
    p95_index = max(0, min(len(ordered) - 1, int(len(ordered) * 0.95) - 1)) if ordered else 0
    return {
        "request_count": len(requests),
        "status_counts": statuses,
        "latency_mean_ms": round(statistics.mean(latencies), 3) if latencies else 0.0,
        "latency_p50_ms": round(statistics.median(latencies), 3) if latencies else 0.0,
        "latency_p95_ms": round(ordered[p95_index], 3) if ordered else 0.0,
        "recommendation_count": total_lines,
        "legal_recommendation_rate": legal_lines / total_lines if total_lines else 1.0,
    }
