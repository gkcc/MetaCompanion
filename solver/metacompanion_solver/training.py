from __future__ import annotations

import hashlib
import json
import math
import tempfile
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Mapping, Sequence

from .offline import load_records
from .logging_store import (
    TRAJECTORY_SCHEMA_ID,
    TRAINING_LOG_SCHEMA_ID,
    deterministic_game_split,
)
from .trajectory import audit_trajectory_file
from .verification import load_and_validate_trajectory_verification


_ACTION_COMPLETENESS = "complete_action_trace_v1"
_RESULT_COMPLETENESS = "terminal_result"
_EXACT_RESOLUTIONS = {"exact_entity_id", "not_applicable"}
_FEATURE_SCHEMA = (
    "friendly_hero_health",
    "opponent_hero_health",
    "friendly_board_attack",
    "friendly_board_health",
    "opponent_board_attack",
    "opponent_board_health",
    "friendly_hand_size",
    "friendly_available_mana",
)


def _observation_training_eligible(observation: Mapping[str, Any]) -> bool:
    """Accept only the versioned, replayable trajectory contract; fail closed otherwise."""

    metadata = observation.get("metadata")
    if not isinstance(metadata, Mapping):
        return False
    kind = str(observation.get("kind") or "").strip().lower()
    completeness = str(metadata.get("completeness") or "").strip().lower()
    eligibility = metadata.get("training_eligible")
    eligible = False
    if isinstance(eligibility, bool):
        eligible = eligibility
    elif isinstance(eligibility, (int, float)):
        eligible = eligibility == 1
    elif isinstance(eligibility, str):
        eligible = eligibility.strip().lower() in {"true", "1", "yes"}
    if not eligible:
        return False
    if str(metadata.get("trajectory_schema") or "").strip() != TRAJECTORY_SCHEMA_ID:
        return False
    if kind == "result":
        return (
            completeness == _RESULT_COMPLETENESS
            and str(metadata.get("capture_contract") or "").strip().lower()
            == "terminal_result_v1"
        )
    if kind != "action" or completeness != _ACTION_COMPLETENESS:
        return False
    if str(metadata.get("capture_contract") or "").strip().lower() != TRAJECTORY_SCHEMA_ID:
        return False
    if str(metadata.get("transition_status") or "").strip().lower() != "replayable_exact":
        return False
    if not str(metadata.get("pre_state_id") or "").strip() or not str(
        metadata.get("post_state_id") or ""
    ).strip():
        return False
    if str(metadata.get("source_entity_resolution") or "").strip().lower() not in _EXACT_RESOLUTIONS:
        return False
    if str(metadata.get("target_entity_resolution") or "").strip().lower() not in _EXACT_RESOLUTIONS:
        return False
    sequence = metadata.get("action_sequence")
    if isinstance(sequence, bool):
        return False
    try:
        if int(str(sequence)) <= 0:
            return False
    except (TypeError, ValueError):
        return False
    action = observation.get("action")
    if not isinstance(action, Mapping):
        return False
    action_kind = str(action.get("kind") or action.get("type") or "").strip().lower()
    source = action.get("source_entity_id")
    target = action.get("target_entity_id")
    source_resolution = str(metadata.get("source_entity_resolution") or "").strip().lower()
    target_resolution = str(metadata.get("target_entity_resolution") or "").strip().lower()
    if str(observation.get("state_id") or "").strip() != str(
        metadata.get("pre_state_id") or ""
    ).strip():
        return False
    if action_kind == "end_turn":
        return (
            source in (None, "")
            and target in (None, "")
            and source_resolution == "not_applicable"
            and target_resolution == "not_applicable"
        )
    if action_kind not in {"play_card", "attack", "hero_power"}:
        return False
    if source in (None, "") or source_resolution != "exact_entity_id":
        return False
    if action_kind == "attack":
        return target not in (None, "") and target_resolution == "exact_entity_id"
    if target_resolution == "exact_entity_id":
        return target not in (None, "")
    return target_resolution == "not_applicable" and target in (None, "")


def _record_split(record: Mapping[str, Any], game_id: str) -> str:
    trajectory = record.get("trajectory")
    if not isinstance(trajectory, Mapping):
        return ""
    if trajectory.get("schema") != TRAJECTORY_SCHEMA_ID:
        return ""
    split = str(trajectory.get("split") or "").strip().lower()
    if split not in {"train", "validation", "test"}:
        return ""
    if split != deterministic_game_split(game_id):
        return ""
    return split


def _outcome(record: Mapping[str, Any]) -> float | None:
    candidates = [record.get("won"), record.get("win"), record.get("outcome"), record.get("result")]
    if isinstance(record.get("result"), Mapping):
        candidates.extend([record["result"].get("won"), record["result"].get("outcome")])
    for candidate in candidates:
        if isinstance(candidate, bool):
            return 1.0 if candidate else 0.0
        if isinstance(candidate, (int, float)) and not isinstance(candidate, bool):
            return min(1.0, max(0.0, float(candidate)))
        if isinstance(candidate, str):
            normalized = candidate.strip().lower()
            if normalized == "win":
                return 1.0
            if normalized == "loss":
                return 0.0
            if normalized == "tie":
                return 0.5
        if isinstance(candidate, Mapping):
            nested = candidate.get("win") if "win" in candidate else candidate.get("won")
            if isinstance(nested, bool):
                return 1.0 if nested else 0.0
    return None


def train_frequency(
    records: Sequence[Mapping[str, Any]], *, trajectory_training_ready: bool = False
) -> dict[str, Any]:
    action_counts: Counter[str] = Counter()
    action_wins: Counter[str] = Counter()
    arena_scores: dict[str, list[float]] = defaultdict(list)
    arena_picks: Counter[str] = Counter()
    labeled = 0
    ignored_incomplete_observations = 0
    ignored_unjoined_observations = 0
    ignored_held_out_observations = 0
    conflicting_result_game_count = 0
    observed_result_sets: dict[str, set[float]] = defaultdict(set)
    eligible_action_kinds_by_game: dict[str, set[str]] = defaultdict(set)
    for record in records:
        if record.get("kind") != "observation" or not isinstance(record.get("observation"), Mapping):
            continue
        observation = record["observation"]
        game_id = observation.get("game_id")
        if (
            observation.get("kind") == "result"
            and isinstance(game_id, str)
            and game_id
            and (
                not trajectory_training_ready
                or _record_split(record, game_id) == "train"
            )
            and _observation_training_eligible(observation)
        ):
            label = _outcome(observation)
            if label is not None:
                observed_result_sets[game_id].add(label)
    conflicting_result_game_count = sum(
        1 for values in observed_result_sets.values() if len(values) > 1
    )
    observed_results = {
        game_id: next(iter(values))
        for game_id, values in observed_result_sets.items()
        if len(values) == 1
    }
    for record in records:
        if record.get("kind") == "arena_draft_pick":
            picked = record.get("picked_card_id")
            if isinstance(picked, str) and picked:
                arena_picks[picked] += 1
            scores = record.get("arenasmith_scores")
            if isinstance(scores, Mapping):
                for card_id, score in scores.items():
                    if isinstance(card_id, str) and isinstance(score, (int, float)) and not isinstance(score, bool):
                        arena_scores[card_id].append(float(score))
            continue
        if record.get("kind") == "observation" and isinstance(record.get("observation"), Mapping):
            observation = record["observation"]
            game_id = observation.get("game_id")
            if trajectory_training_ready and (
                not isinstance(game_id, str) or _record_split(record, game_id) != "train"
            ):
                ignored_held_out_observations += 1
                continue
            action = observation.get("action")
            if observation.get("kind") == "action" and isinstance(action, Mapping):
                if not trajectory_training_ready or not _observation_training_eligible(observation):
                    ignored_incomplete_observations += 1
                    continue
                kind = action.get("kind") or action.get("type")
                label = observed_results.get(game_id) if isinstance(game_id, str) else None
                if label is None:
                    ignored_unjoined_observations += 1
                    continue
                if isinstance(kind, str) and kind:
                    eligible_action_kinds_by_game[game_id].add(kind)
            continue
    # This is intentionally a descriptive game-level baseline: a long game contributes
    # at most once to each action kind instead of overpowering short games with many clicks.
    for game_id, kinds in eligible_action_kinds_by_game.items():
        label = observed_results[game_id]
        for kind in kinds:
            action_counts[kind] += 1
            action_wins[kind] += label
            labeled += 1
    action_weights = {
        kind: (action_wins[kind] + 1.0) / (count + 2.0)
        for kind, count in action_counts.items()
    }
    max_pick_count = max(arena_picks.values(), default=1)
    card_priors: dict[str, float] = {}
    for card_id in set(arena_scores) | set(arena_picks):
        score_items = arena_scores.get(card_id, [])
        score_weight = sum(score_items) / len(score_items) / 50.0 if score_items else 1.0
        pick_weight = 0.5 + arena_picks[card_id] / max_pick_count
        card_priors[card_id] = round(max(0.05, min(3.0, (score_weight + pick_weight) / 2.0)), 6)
    return {
        "model_type": "frequency-prior-v1",
        "sample_count": len(records),
        "labeled_sample_count": labeled,
        "trajectory_training_ready": trajectory_training_ready,
        "unit_of_analysis": "one_game_per_action_kind",
        "is_policy_model": False,
        "ignored_incomplete_observation_count": ignored_incomplete_observations,
        "ignored_unjoined_observation_count": ignored_unjoined_observations,
        "ignored_held_out_observation_count": ignored_held_out_observations,
        "conflicting_result_game_count": conflicting_result_game_count,
        "action_kind_weights": action_weights,
        "card_priors": card_priors,
        "caveat": "Offline baseline only; this is not a trained optimal-play policy or value network.",
    }


def _state_features(record: Mapping[str, Any]) -> list[float] | None:
    if record.get("kind") == "decision_snapshot":
        state = record.get("state")
        if not isinstance(state, Mapping):
            return None
    else:
        request = record.get("request")
        if not isinstance(request, Mapping) or not isinstance(
            request.get("state"), Mapping
        ):
            return None
        state = request["state"]
    friendly = state.get("friendly") or state.get("player")
    opponent = state.get("opponent")
    if not isinstance(friendly, Mapping) or not isinstance(opponent, Mapping):
        return None

    def hero_health(player: Mapping[str, Any]) -> float:
        hero = player.get("hero")
        if not isinstance(hero, Mapping):
            return 0.0
        if "current_health" in hero:
            return float(hero.get("current_health") or 0)
        return float(hero.get("health") or 0) - float(hero.get("damage") or 0)

    def board_stats(player: Mapping[str, Any]) -> tuple[float, float]:
        board = player.get("board")
        if not isinstance(board, list):
            return 0.0, 0.0
        return (
            sum(float(card.get("attack") or 0) for card in board if isinstance(card, Mapping)),
            sum(float(card.get("current_health", card.get("health", 0)) or 0) for card in board if isinstance(card, Mapping)),
        )

    friendly_board = board_stats(friendly)
    opponent_board = board_stats(opponent)
    return [
        hero_health(friendly),
        hero_health(opponent),
        friendly_board[0],
        friendly_board[1],
        opponent_board[0],
        opponent_board[1],
        float(len(friendly.get("hand", []))) if isinstance(friendly.get("hand"), list) else 0.0,
        float(friendly.get("mana", 0) or 0),
    ]


def _value_examples(records: Sequence[Mapping[str, Any]]) -> list[dict[str, Any]]:
    terminal_results: dict[str, set[float]] = defaultdict(set)
    for record in records:
        if record.get("kind") != "observation" or not isinstance(
            record.get("observation"), Mapping
        ):
            continue
        observation = record["observation"]
        game_id = observation.get("game_id")
        if observation.get("kind") != "result" or not isinstance(game_id, str):
            continue
        if not _observation_training_eligible(observation):
            continue
        label = _outcome(observation)
        if label is not None:
            terminal_results[game_id].add(label)

    examples_by_decision: dict[tuple[str, str], dict[str, Any]] = {}
    for record in records:
        kind = record.get("kind")
        if kind not in {"solve", "decision_snapshot"} or record.get(
            "log_schema"
        ) != TRAINING_LOG_SCHEMA_ID:
            continue
        trajectory = record.get("trajectory")
        if not isinstance(trajectory, Mapping) or trajectory.get("schema") != TRAJECTORY_SCHEMA_ID:
            continue
        if kind == "solve" and trajectory.get("solve_stage") not in {"final", "single"}:
            continue
        if kind == "decision_snapshot" and trajectory.get(
            "capture_contract"
        ) != "offline_power_decision_snapshot_v1":
            continue
        game_id = trajectory.get("game_id")
        decision_id = trajectory.get("decision_id")
        if not isinstance(game_id, str) or not isinstance(decision_id, str):
            continue
        labels = terminal_results.get(game_id, set())
        if len(labels) != 1:
            continue
        features = _state_features(record)
        if features is None:
            continue
        examples_by_decision[(game_id, decision_id)] = {
            "game_id": game_id,
            "decision_id": decision_id,
            "split": deterministic_game_split(game_id),
            "features": features,
            "label": next(iter(labels)),
        }
    return [examples_by_decision[key] for key in sorted(examples_by_decision)]


def _per_game_weights(examples: Sequence[Mapping[str, Any]]) -> list[float]:
    counts = Counter(str(item["game_id"]) for item in examples)
    raw = [1.0 / counts[str(item["game_id"])] for item in examples]
    total = sum(raw)
    return [value * len(raw) / total for value in raw] if total else []


def _weighted_metrics(
    predictions: Sequence[float],
    labels: Sequence[float],
    weights: Sequence[float],
) -> dict[str, float]:
    total = sum(weights)
    if not predictions or len(predictions) != len(labels) or len(labels) != len(weights) or total <= 0:
        raise ValueError("cannot evaluate an empty or misaligned value-model split")
    clipped = [min(1.0 - 1e-7, max(1e-7, float(value))) for value in predictions]
    brier = sum(
        weight * (prediction - label) ** 2
        for prediction, label, weight in zip(clipped, labels, weights)
    ) / total
    log_loss = -sum(
        weight
        * (label * math.log(prediction) + (1.0 - label) * math.log(1.0 - prediction))
        for prediction, label, weight in zip(clipped, labels, weights)
    ) / total
    accuracy = sum(
        weight * float((prediction >= 0.5) == (label >= 0.5))
        for prediction, label, weight in zip(clipped, labels, weights)
    ) / total
    return {
        "brier": round(brier, 8),
        "log_loss": round(log_loss, 8),
        "accuracy": round(accuracy, 8),
    }


def _atomic_torch_save(torch: Any, payload: Mapping[str, Any], destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb",
            prefix=destination.name + ".",
            suffix=".tmp",
            dir=str(destination.parent),
            delete=False,
        ) as handle:
            temporary = Path(handle.name)
        torch.save(dict(payload), temporary)
        temporary.replace(destination)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def train_torch(
    records: Sequence[Mapping[str, Any]],
    output_path: str | Path,
    epochs: int = 100,
    *,
    dataset_sha256: str = "",
) -> dict[str, Any]:
    try:
        import torch
        from torch import nn
    except ImportError as exc:
        raise RuntimeError("PyTorch is not installed; use --backend frequency") from exc

    examples = _value_examples(records)
    split_examples = {
        name: [item for item in examples if item["split"] == name]
        for name in ("train", "validation", "test")
    }
    missing_splits = [name for name, items in split_examples.items() if not items]
    if missing_splits:
        raise ValueError(
            "torch training requires non-empty game-level train/validation/test splits; missing "
            + ", ".join(missing_splits)
        )
    train_labels = [float(item["label"]) for item in split_examples["train"]]
    if len(set(train_labels)) < 2:
        raise ValueError("torch training requires at least two distinct outcome labels in train")

    train_weights = _per_game_weights(split_examples["train"])
    feature_count = len(_FEATURE_SCHEMA)
    weighted_total = sum(train_weights)
    means = [
        sum(
            weight * float(item["features"][index])
            for item, weight in zip(split_examples["train"], train_weights)
        )
        / weighted_total
        for index in range(feature_count)
    ]
    standard_deviations = []
    for index, mean in enumerate(means):
        variance = sum(
            weight * (float(item["features"][index]) - mean) ** 2
            for item, weight in zip(split_examples["train"], train_weights)
        ) / weighted_total
        standard_deviations.append(max(math.sqrt(variance), 1e-6))

    def tensors(items: Sequence[Mapping[str, Any]]):
        normalized = [
            [
                (float(item["features"][index]) - means[index])
                / standard_deviations[index]
                for index in range(feature_count)
            ]
            for item in items
        ]
        return (
            torch.tensor(normalized, dtype=torch.float32),
            torch.tensor([[float(item["label"])] for item in items], dtype=torch.float32),
            torch.tensor(_per_game_weights(items), dtype=torch.float32).reshape(-1, 1),
        )

    train_x, train_y, train_w = tensors(split_examples["train"])
    validation_x, validation_y, validation_w = tensors(split_examples["validation"])
    test_x, _, _ = tensors(split_examples["test"])
    torch.manual_seed(0)
    model = nn.Sequential(
        nn.Linear(feature_count, 16),
        nn.ReLU(),
        nn.Linear(16, 1),
        nn.Sigmoid(),
    )
    optimizer = torch.optim.Adam(model.parameters(), lr=0.01)
    loss_fn = nn.BCELoss(reduction="none")
    max_epochs = max(1, epochs)
    patience = max(5, min(20, max_epochs // 5))
    best_epoch = 0
    best_validation_loss = float("inf")
    best_state: dict[str, Any] | None = None
    epochs_without_improvement = 0
    for epoch in range(1, max_epochs + 1):
        model.train()
        optimizer.zero_grad()
        losses = loss_fn(model(train_x), train_y)
        loss = (losses * train_w).sum() / train_w.sum()
        loss.backward()
        optimizer.step()
        model.eval()
        with torch.no_grad():
            validation_loss = float(
                (
                    (loss_fn(model(validation_x), validation_y) * validation_w).sum()
                    / validation_w.sum()
                )
                .detach()
                .item()
            )
        if validation_loss + 1e-7 < best_validation_loss:
            best_validation_loss = validation_loss
            best_epoch = epoch
            best_state = {
                key: value.detach().cpu().clone()
                for key, value in model.state_dict().items()
            }
            epochs_without_improvement = 0
        else:
            epochs_without_improvement += 1
            if epochs_without_improvement >= patience:
                break
    if best_state is None:
        raise RuntimeError("torch training did not produce a validation checkpoint")
    model.load_state_dict(best_state)
    model.eval()

    def predictions(x: Any) -> list[float]:
        with torch.no_grad():
            return [float(value) for value in model(x).reshape(-1).tolist()]

    train_prediction = predictions(train_x)
    validation_prediction = predictions(validation_x)
    test_prediction = predictions(test_x)
    split_metrics: dict[str, Any] = {}
    train_rate = sum(
        label * weight for label, weight in zip(train_labels, train_weights)
    ) / sum(train_weights)
    for name, items, predicted in (
        ("train", split_examples["train"], train_prediction),
        ("validation", split_examples["validation"], validation_prediction),
        ("test", split_examples["test"], test_prediction),
    ):
        labels = [float(item["label"]) for item in items]
        weights = _per_game_weights(items)
        model_metrics = _weighted_metrics(predicted, labels, weights)
        baseline_metrics = _weighted_metrics([train_rate] * len(items), labels, weights)
        split_metrics[name] = {
            "decision_count": len(items),
            "game_count": len({str(item["game_id"]) for item in items}),
            "model": model_metrics,
            "constant_train_rate_baseline": baseline_metrics,
            "beats_baseline": bool(
                model_metrics["brier"] <= baseline_metrics["brier"]
                and model_metrics["log_loss"] <= baseline_metrics["log_loss"]
            ),
        }

    assignment = {
        str(item["game_id"]): str(item["split"])
        for item in examples
    }
    split_sha256 = hashlib.sha256(
        json.dumps(assignment, sort_keys=True, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    trajectory_envelopes = [
        record.get("trajectory")
        for record in records
        if isinstance(record.get("trajectory"), Mapping)
    ]
    provenance = {
        "dataset_sha256": dataset_sha256,
        "split_assignment_sha256": split_sha256,
        "trajectory_schema": TRAJECTORY_SCHEMA_ID,
        "training_log_schema": TRAINING_LOG_SCHEMA_ID,
        "planner_models": sorted(
            {
                str(item.get("planner_model"))
                for item in trajectory_envelopes
                if item.get("planner_model")
            }
        ),
        "rules_models": sorted(
            {
                str(item.get("rules_model"))
                for item in trajectory_envelopes
                if item.get("rules_model")
            }
        ),
        "adapters": sorted(
            {
                str(item.get("adapter"))
                for item in trajectory_envelopes
                if item.get("adapter")
            }
        ),
    }
    checkpoint = {
        "schema": "metacompanion-value-checkpoint-v2",
        "model_type": "torch-value-baseline-v2",
        "model_state_dict": best_state,
        "feature_schema": list(_FEATURE_SCHEMA),
        "normalization": {
            "fit_split": "train",
            "mean": means,
            "standard_deviation": standard_deviations,
        },
        "provenance": provenance,
        "evaluation": split_metrics,
        "best_epoch": best_epoch,
        "epochs_completed": epoch,
        "promotion_ready": bool(
            split_metrics["validation"]["beats_baseline"]
            and split_metrics["test"]["beats_baseline"]
        ),
    }
    destination = Path(output_path)
    _atomic_torch_save(torch, checkpoint, destination)
    return {
        "model_type": checkpoint["model_type"],
        "sample_count": len(examples),
        "split_metrics": split_metrics,
        "best_epoch": best_epoch,
        "epochs_completed": epoch,
        "promotion_ready": checkpoint["promotion_ready"],
        "output": destination.name,
        "provenance": provenance,
        "caveat": (
            "Experimental offline value baseline only; it is not a live policy and does "
            "not establish globally optimal play."
        ),
    }


def _atomic_write_json(value: Mapping[str, Any], destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    temporary.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    temporary.replace(destination)


def train_file(
    input_path: str | Path,
    output_path: str | Path,
    backend: str = "frequency",
    epochs: int = 100,
    verification_manifest_path: str | Path | None = None,
) -> dict[str, Any]:
    source = Path(input_path)
    # The live worker may append to its JSONL while an offline command starts. Read it
    # exactly once and make both the audit and trainer consume that immutable snapshot.
    # A torn final line then fails closed instead of auditing one corpus and training on
    # a later one.
    dataset_bytes = source.read_bytes()
    dataset_sha256 = hashlib.sha256(dataset_bytes).hexdigest()
    with tempfile.TemporaryDirectory(prefix="metacompanion-train-") as directory:
        snapshot = Path(directory) / (source.name or "training.jsonl")
        snapshot.write_bytes(dataset_bytes)
        records = load_records(snapshot)
        trajectory_records = [
            item for item in records if item.get("kind") in {"solve", "observation"}
        ]
        trajectory_audit = None
        if trajectory_records and len(trajectory_records) == len(records):
            trajectory_audit = audit_trajectory_file(snapshot)

        trajectory_verification = None
        if verification_manifest_path is not None:
            if not trajectory_audit or not trajectory_audit["training_ready"]:
                raise ValueError(
                    "trajectory verification requires a passing production audit"
                )
            trajectory_verification = load_and_validate_trajectory_verification(
                verification_manifest_path,
                dataset_bytes=dataset_bytes,
                audit_report=trajectory_audit,
            )

        verified_training_ready = bool(
            trajectory_audit
            and trajectory_audit["training_ready"]
            and trajectory_verification
        )
        if backend == "torch":
            if not trajectory_audit or not trajectory_audit["training_ready"]:
                raise ValueError(
                    "torch training requires a passing trajectory-readiness-v1 audit; "
                    "collect exact replayable transitions and joined terminal results first"
                )
            if not trajectory_verification:
                raise ValueError(
                    "torch training requires a trajectory-verification-manifest-v1; "
                    "promote an immutable verified corpus before training"
                )
            return train_torch(
                records,
                output_path,
                epochs,
                dataset_sha256=dataset_sha256,
            )
        artifact = train_frequency(
            records,
            trajectory_training_ready=verified_training_ready,
        )
    artifact["dataset_sha256"] = dataset_sha256
    if trajectory_audit is not None:
        artifact["trajectory_audit"] = {
            "schema": trajectory_audit["schema"],
            "contract_passed": trajectory_audit["contract_passed"],
            "training_ready": trajectory_audit["training_ready"],
            "metrics": trajectory_audit["metrics"],
        }
        artifact["trajectory_verification"] = trajectory_verification or {
            "schema": "trajectory-verification-manifest-v1",
            "verified": False,
            "reason": (
                "verification_manifest_required"
                if trajectory_audit["training_ready"]
                else "trajectory_not_training_ready"
            ),
        }
    destination = Path(output_path)
    _atomic_write_json(artifact, destination)
    return artifact
