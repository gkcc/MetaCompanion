from __future__ import annotations

import copy
import hashlib
import json
import os
import threading
import time
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Mapping, Sequence

from .logging_store import sanitize_for_training
from .models import StateEvaluator
from .offline import extract_solve_requests, load_records
from .schemas import Annotation, GameState, SolveRequest
from .search import PuctTurnSearcher, SearchLimits
from .simulator import advance_to_start_of_turn, apply_action


@dataclass(frozen=True)
class SelfPlaySettings:
    episodes: int = 10
    max_turns: int = 40
    time_limit_seconds: float = 3600.0
    search_budget_ms: int = 100
    max_iterations: int = 200
    max_depth: int = 24
    checkpoint_every: int = 1
    seed: int = 0

    def validate(self) -> None:
        if not 1 <= self.episodes <= 1_000_000:
            raise ValueError("episodes must be between 1 and 1000000")
        if not 1 <= self.max_turns <= 1000:
            raise ValueError("max_turns must be between 1 and 1000")
        if not 0.1 <= self.time_limit_seconds <= 7 * 24 * 3600:
            raise ValueError("time_limit_seconds must be between 0.1 and 604800")
        if not 25 <= self.search_budget_ms <= 60_000:
            raise ValueError("search_budget_ms must be between 25 and 60000")
        if not 1 <= self.max_iterations <= 1_000_000:
            raise ValueError("max_iterations must be between 1 and 1000000")
        if not 2 <= self.max_depth <= 100:
            raise ValueError("max_depth must be between 2 and 100")
        if self.checkpoint_every < 1:
            raise ValueError("checkpoint_every must be positive")


def _settings_hash(settings: SelfPlaySettings, request_count: int) -> str:
    payload = {**asdict(settings), "request_count": request_count, "format": "generic-self-play-v1"}
    return hashlib.sha256(json.dumps(payload, sort_keys=True).encode("utf-8")).hexdigest()


def _atomic_json(path: Path, payload: Mapping[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + f".{os.getpid()}.tmp")
    temporary.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def _append_records(path: Path, records: Sequence[Mapping[str, Any]]) -> None:
    if not records:
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8", newline="\n") as handle:
        for record in records:
            sanitized = sanitize_for_training(record)
            handle.write(json.dumps(sanitized, ensure_ascii=False, separators=(",", ":"), sort_keys=True))
            handle.write("\n")


def _terminal_winner(state: GameState) -> str:
    friendly_dead = state.friendly.hero.current_health <= 0
    opponent_dead = state.opponent.hero.current_health <= 0
    if friendly_dead and opponent_dead:
        return "tie"
    if friendly_dead:
        return state.opponent.player_id
    if opponent_dead:
        return state.friendly.player_id
    return ""


def _annotation_dicts(annotations: Sequence[Annotation]) -> list[dict[str, Any]]:
    return [item.to_dict() for item in annotations]


def run_generic_self_play(
    input_path: str | Path,
    output_directory: str | Path,
    settings: SelfPlaySettings,
    *,
    resume: bool = False,
    cancel_event: threading.Event | None = None,
    searcher: PuctTurnSearcher | None = None,
) -> dict[str, Any]:
    """Generate bounded generic MCTS trajectories; this does not train an RL model."""

    settings.validate()
    requests = extract_solve_requests(load_records(input_path))
    if not requests:
        raise ValueError("self-play input contains no solve requests")
    output = Path(output_directory)
    output.mkdir(parents=True, exist_ok=True)
    trajectory_path = output / "trajectories.jsonl"
    checkpoint_path = output / "checkpoint.json"
    manifest_path = output / "manifest.json"
    config_hash = _settings_hash(settings, len(requests))
    start_episode = 0
    outcomes: dict[str, int] = {"friendly": 0, "opponent": 0, "tie": 0, "truncated": 0}
    completed = 0
    if resume:
        if not checkpoint_path.is_file():
            raise ValueError("--resume requires checkpoint.json")
        checkpoint = json.loads(checkpoint_path.read_text(encoding="utf-8"))
        if checkpoint.get("settings_hash") != config_hash:
            raise ValueError("checkpoint settings/input count do not match this run")
        start_episode = int(checkpoint.get("next_episode", 0))
        completed = int(checkpoint.get("completed_episodes", 0))
        saved_outcomes = checkpoint.get("outcomes")
        if isinstance(saved_outcomes, Mapping):
            outcomes.update({key: int(saved_outcomes.get(key, 0)) for key in outcomes})
    elif trajectory_path.exists() or checkpoint_path.exists() or manifest_path.exists():
        raise ValueError("output directory already contains self-play artifacts; use --resume or a new directory")

    cancel_event = cancel_event or threading.Event()
    searcher = searcher or PuctTurnSearcher()
    evaluator = StateEvaluator()
    started = time.monotonic()
    deadline = started + settings.time_limit_seconds
    status = "completed"
    next_episode = start_episode

    for episode_index in range(start_episode, settings.episodes):
        if cancel_event.is_set():
            status = "cancelled"
            break
        if time.monotonic() >= deadline:
            status = "time_limit"
            break
        source_request: SolveRequest = requests[episode_index % len(requests)]
        state = copy.deepcopy(source_request.state)
        state.state_id = f"{source_request.state.state_id}:self-play:{episode_index}"
        state.rng_seed = settings.seed + episode_index
        episode_records: list[dict[str, Any]] = []
        winner = ""
        episode_complete = True

        for half_turn in range(settings.max_turns):
            if cancel_event.is_set() or time.monotonic() >= deadline:
                episode_complete = False
                status = "cancelled" if cancel_event.is_set() else "time_limit"
                break
            winner = _terminal_winner(state)
            if winner:
                break
            state.perspective_player_id = state.active_player_id
            state_before = copy.deepcopy(state)
            remaining_ms = int((deadline - time.monotonic()) * 1000)
            if remaining_ms < 25:
                episode_complete = False
                status = "time_limit"
                break
            result = searcher.search(
                f"self-play-{episode_index}-{half_turn}",
                state,
                SearchLimits(
                    time_budget_ms=min(settings.search_budget_ms, remaining_ms),
                    max_iterations=settings.max_iterations,
                    max_depth=settings.max_depth,
                    top_k=1,
                ),
                cancel_event,
            )
            if cancel_event.is_set():
                episode_complete = False
                status = "cancelled"
                break
            recommendation = result.recommendations[0] if result.recommendations else None
            transition_annotations: list[Annotation] = []
            if recommendation:
                for action in recommendation.actions:
                    outcome = apply_action(state, action)
                    state = outcome.state
                    transition_annotations.extend(outcome.annotations)
                    if outcome.ended_turn or _terminal_winner(state):
                        break
            winner = _terminal_winner(state)
            if not winner and state.active_player_id != state_before.active_player_id:
                refreshed = advance_to_start_of_turn(state)
                state = refreshed.state
                transition_annotations.extend(refreshed.annotations)
            episode_records.append(
                {
                    "kind": "generic_self_play_transition",
                    "schema_version": 1,
                    "episode": episode_index,
                    "half_turn": half_turn,
                    "state": state_before.to_dict(),
                    "search": result.to_dict(),
                    "selected_actions": [
                        action.to_dict() for action in recommendation.actions
                    ]
                    if recommendation
                    else [],
                    "transition_annotations": _annotation_dicts(transition_annotations),
                    "value_estimate_after": evaluator.evaluate(
                        state, source_request.state.friendly.player_id
                    ),
                    "generator": "bounded-generic-puct-v1",
                }
            )

        if not episode_complete:
            break
        winner = winner or _terminal_winner(state)
        if winner == source_request.state.friendly.player_id:
            outcome_key = "friendly"
        elif winner == source_request.state.opponent.player_id:
            outcome_key = "opponent"
        elif winner == "tie":
            outcome_key = "tie"
        else:
            outcome_key = "truncated"
        outcomes[outcome_key] += 1
        episode_records.append(
            {
                "kind": "generic_self_play_result",
                "schema_version": 1,
                "episode": episode_index,
                "outcome": outcome_key,
                "winner_player_id": winner,
                "terminal": bool(winner),
                "caveat": "Truncated outcomes are not win/loss labels.",
            }
        )
        _append_records(trajectory_path, episode_records)
        completed += 1
        next_episode = episode_index + 1
        if completed % settings.checkpoint_every == 0 or episode_index + 1 == settings.episodes:
            _atomic_json(
                checkpoint_path,
                {
                    "schema_version": 1,
                    "generator": "bounded-generic-puct-v1",
                    "settings_hash": config_hash,
                    "next_episode": next_episode,
                    "completed_episodes": completed,
                    "outcomes": outcomes,
                    "updated_at_utc": datetime.now(timezone.utc).isoformat(),
                },
            )

    elapsed_seconds = time.monotonic() - started
    _atomic_json(
        checkpoint_path,
        {
            "schema_version": 1,
            "generator": "bounded-generic-puct-v1",
            "settings_hash": config_hash,
            "next_episode": next_episode,
            "completed_episodes": completed,
            "outcomes": outcomes,
            "updated_at_utc": datetime.now(timezone.utc).isoformat(),
            "status": status,
        },
    )
    manifest = {
        "schema_version": 1,
        "kind": "generic_self_play_manifest",
        "status": status,
        "generator": "bounded-generic-puct-v1",
        "completed_episodes": completed,
        "requested_episodes": settings.episodes,
        "outcomes": outcomes,
        "elapsed_seconds": round(elapsed_seconds, 6),
        "settings": asdict(settings),
        "settings_hash": config_hash,
        "trajectory_file": trajectory_path.name,
        "checkpoint_file": checkpoint_path.name,
        "is_reinforcement_learning_model": False,
        "caveats": [
            "This command generates generic simulator/MCTS trajectories; it does not train or promote a policy.",
            "Card-text coverage and between-turn refresh are incomplete and explicitly annotated.",
            "A complete rules engine, opponent pool, evaluation gate, and policy/value optimization remain future work.",
        ],
        "finished_at_utc": datetime.now(timezone.utc).isoformat(),
    }
    _atomic_json(manifest_path, manifest)
    return manifest
