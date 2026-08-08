from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Mapping, Sequence


SOLVER_ROOT = Path(__file__).resolve().parents[1]
if str(SOLVER_ROOT) not in sys.path:
    sys.path.insert(0, str(SOLVER_ROOT))

from metacompanion_solver.behavior import create_behavior_record  # noqa: E402
from metacompanion_solver.behavior_learning import (  # noqa: E402
    BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
    promote_behavior_imitation_file,
)
from metacompanion_solver.behavior_prior import (  # noqa: E402
    BEHAVIOR_PRIOR_POLICY_SCHEMA_ID,
)
from metacompanion_solver.decision_frame import (  # noqa: E402
    create_decision_frame_record,
)
from metacompanion_solver.decision_ranker import (  # noqa: E402
    DECISION_RANKER_POLICY_SCHEMA_ID,
)
from metacompanion_solver.logging_store import (  # noqa: E402
    TRAJECTORY_SCHEMA_ID,
    TRAINING_LOG_SCHEMA_ID,
    deterministic_game_split,
)
from metacompanion_solver.observed_policy_evaluation import (  # noqa: E402
    OBSERVED_POLICY_EVALUATION_POLICY_SCHEMA_ID,
)


_GAMES: tuple[tuple[str, str, str], ...] = (
    ("anon-0000000000000001", "train", "local"),
    ("anon-0000000000000002", "train", "opponent"),
    ("anon-0000000000000008", "validation", "local"),
    ("anon-000000000000000d", "validation", "opponent"),
    ("anon-0000000000000003", "test", "local"),
    ("anon-0000000000000005", "test", "opponent"),
)


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
        "current_health_known": True,
        "playable": True,
        "can_attack": attack > 0,
        "attacks_remaining": 1 if attack > 0 else 0,
        "taunt": False,
        "divine_shield": False,
        "stealth": False,
        "poisonous": False,
        "lifesteal": False,
        "windfury": False,
        "mega_windfury": False,
        "rush": False,
        "charge": False,
        "reborn": False,
        "dormant": False,
        "immune": False,
        "summoned_this_turn": False,
        "frozen": False,
    }


def _player(role: str) -> dict[str, object]:
    local = role == "friendly"
    prefix = "f" if local else "o"
    return {
        "player_id": role,
        "hero": _entity(f"{prefix}-hero", f"{prefix.upper()}_HERO", "HERO", health=30),
        "hero_power": None,
        "weapon": None,
        "hand": [],
        "board": [
            _entity(
                f"{prefix}-minion",
                f"{prefix.upper()}_MINION",
                "MINION",
                attack=3,
                health=3,
            )
        ],
        "mana": 5,
        "max_mana": 5,
        "armor": 0,
        "deck_size": 0,
        "fatigue": 0,
        "hero_power_available": False,
        "spell_power": 0,
        "public_rule_tags_complete": False,
        "public_rule_tags": {},
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


def _attack(side: str) -> dict[str, object]:
    return {
        "kind": "attack",
        "source_entity_id": "f-minion" if side == "local" else "o-minion",
        "target_entity_id": "o-hero" if side == "local" else "f-hero",
        "card_id": "F_MINION" if side == "local" else "O_MINION",
    }


def _decision_attack(side: str) -> dict[str, object]:
    return {**_attack(side), "board_position": 0}


def _decision_attack_minion(side: str) -> dict[str, object]:
    return {
        **_attack(side),
        "target_entity_id": "o-minion" if side == "local" else "f-minion",
        "board_position": 0,
    }


def _end_turn() -> dict[str, object]:
    return {
        "kind": "end_turn",
        "source_entity_id": "",
        "target_entity_id": "",
        "card_id": "",
        "board_position": 0,
    }


def _behavior(game_id: str, side: str) -> dict[str, object]:
    actor = "friendly" if side == "local" else "opponent"
    record = create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00Z",
        actor_side=side,
        actor_player_id=actor,
        actor_evidence="hdt_player_event" if side == "local" else "hdt_opponent_event",
        identity_status="exact_public_entity",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="player_attack" if side == "local" else "opponent_attack",
        action=_attack(side),
        pre_state=_state(actor, f"{game_id}-pre-1"),
        post_state=_state(actor, f"{game_id}-post-1"),
    )
    return record.to_dict()


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


def _write_json(path: Path, value: Mapping[str, Any]) -> None:
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def _write_jsonl(path: Path, values: Sequence[Mapping[str, Any]]) -> None:
    path.write_text(
        "".join(
            json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
            + "\n"
            for value in values
        ),
        encoding="utf-8",
    )


def generate_fixture(output_directory: Path) -> dict[str, object]:
    output_directory.mkdir(parents=True, exist_ok=True)
    for game_id, expected_split, _ in _GAMES:
        actual = deterministic_game_split(game_id)
        if actual != expected_split:
            raise ValueError(
                f"fixture split drifted for {game_id}: expected={expected_split}, actual={actual}"
            )

    behaviors = [_behavior(game_id, side) for game_id, _, side in _GAMES]
    results = [
        _result(game_id, "win" if side == "local" else "loss")
        for game_id, _, side in _GAMES
    ]
    behavior_by_game = {str(value["game_id"]): value for value in behaviors}
    frames: list[dict[str, object]] = []
    for game_id, _, side in _GAMES:
        if side != "local":
            continue
        observed = behavior_by_game[game_id]
        frame = create_decision_frame_record(
            game_id=game_id,
            decision_sequence=1,
            observed_at_utc="2026-08-01T00:00:00Z",
            client_build="fixture-patch",
            mode="standard",
            selected_behavior_id=str(observed["behavior_id"]),
            hdt_frame_id=1,
            pre_state=observed["pre_state"],
            post_state=observed["post_state"],
            selected_action=_decision_attack("local"),
            legal_candidates=[
                {
                    "option_id": 0,
                    "action": _end_turn(),
                    "target_evidence": "not_applicable",
                    "position_evidence": "not_applicable",
                },
                {
                    "option_id": 1,
                    "action": _decision_attack("local"),
                    "target_evidence": "hdt_error_none",
                    "position_evidence": "not_applicable",
                },
                {
                    "option_id": 1,
                    "action": _decision_attack_minion("local"),
                    "target_evidence": "hdt_error_none",
                    "position_evidence": "not_applicable",
                },
            ],
        )
        frames.append(frame.to_dict())

    behavior_path = output_directory / "behavior-v1.jsonl"
    trajectory_path = output_directory / "training-v2.jsonl"
    decision_path = output_directory / "advisor-decision-frame-v1.jsonl"
    imitation_path = output_directory / "behavior-imitation-v1.jsonl"
    manifest_path = output_directory / "behavior-imitation-v1.manifest.json"
    behavior_policy_path = output_directory / "behavior-learning-policy-v1.json"
    prior_policy_path = output_directory / "behavior-prior-policy-v1.json"
    ranker_policy_path = output_directory / "decision-ranker-policy-v1.json"
    evaluation_policy_path = (
        output_directory / "observed-policy-evaluation-policy-v1.json"
    )

    _write_jsonl(behavior_path, behaviors)
    _write_jsonl(trajectory_path, results)
    _write_jsonl(decision_path, frames)
    _write_json(
        behavior_policy_path,
        {
            "schema": BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
            "thresholds": {
                "min_unique_games": 6,
                "min_behavior_records": 6,
                "min_joined_result_games": 6,
                "min_joined_behavior_records": 6,
                "min_behavior_eligible_records": 6,
                "min_local_eligible_records": 3,
                "min_opponent_eligible_records": 3,
                "min_distinct_action_kinds": 1,
                "min_result_join_rate": 1.0,
                "min_both_side_game_rate": 0.0,
                "min_behavior_eligible_rate": 1.0,
                "max_unknown_actor_rate": 0.0,
                "max_unknown_identity_rate": 0.0,
                "min_train_games": 2,
                "min_validation_games": 2,
                "min_test_games": 2,
            },
        },
    )
    _write_json(
        prior_policy_path,
        {
            "schema": BEHAVIOR_PRIOR_POLICY_SCHEMA_ID,
            "thresholds": {
                "min_train_games": 2,
                "min_validation_games": 2,
                "min_test_games": 2,
                "min_train_records": 2,
                "min_validation_records": 2,
                "min_test_records": 2,
                "min_validation_seen_template_records": 2,
                "max_validation_kind_log_loss_excess": 1.0,
                "max_validation_seen_template_log_loss_excess": 1.0,
                "max_validation_unseen_template_rate": 1.0,
            },
        },
    )
    _write_json(
        ranker_policy_path,
        {
            "schema": DECISION_RANKER_POLICY_SCHEMA_ID,
            "thresholds": {
                "min_train_games": 1,
                "min_validation_games": 1,
                "min_test_games": 1,
                "min_train_records": 1,
                "min_validation_records": 1,
                "min_test_records": 1,
                "min_validation_top1_lift_over_uniform": -1.0,
                "min_validation_top3_lift_over_uniform": -1.0,
                "max_validation_log_loss_excess": 100.0,
                "max_validation_unseen_selected_template_rate": 1.0,
            },
        },
    )
    _write_json(
        evaluation_policy_path,
        {
            "schema": OBSERVED_POLICY_EVALUATION_POLICY_SCHEMA_ID,
            "thresholds": {
                "min_candidate_train_games": 1,
                "min_candidate_validation_games": 1,
                "min_candidate_test_games": 1,
                "min_candidate_train_records": 1,
                "min_candidate_validation_records": 1,
                "min_candidate_test_records": 1,
                "max_validation_candidate_log_loss_excess": 100.0,
                "min_validation_candidate_top3_accuracy": 0.0,
                "max_validation_unseen_selected_template_rate": 1.0,
                "min_opponent_train_games": 1,
                "min_opponent_validation_games": 1,
                "min_opponent_test_games": 1,
                "min_opponent_train_records": 1,
                "min_opponent_validation_records": 1,
                "min_opponent_test_records": 1,
                "max_validation_opponent_kind_log_loss_excess": 100.0,
                "max_validation_opponent_seen_template_log_loss_excess": 100.0,
                "max_validation_opponent_unseen_template_rate": 1.0,
            },
        },
    )
    manifest = promote_behavior_imitation_file(
        behavior_path,
        trajectory_path,
        imitation_path,
        manifest_path,
        policy_path=behavior_policy_path,
    )
    return {
        "schema": "observed-policy-synthetic-fixture-v1",
        "behavior_records": len(behaviors),
        "decision_frames": len(frames),
        "split_game_counts": manifest["audit"]["metrics"]["split_game_counts"],
        "rl_training_eligible": False,
        "optimality_verified": False,
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Generate a small synthetic dual-model release fixture."
    )
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args(argv)
    result = generate_fixture(args.output_dir)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
