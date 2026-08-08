from __future__ import annotations

import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.behavior_learning import (
    BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
    promote_behavior_imitation_file,
)
from metacompanion_solver.behavior_prior import (
    BEHAVIOR_PRIOR_POLICY_SCHEMA_ID,
    BEHAVIOR_PRIOR_SCHEMA_ID,
    BehaviorPriorError,
    _hierarchical_prior_strength,
    _template_label,
    load_behavior_prior,
    score_legal_behavior_candidates,
    train_behavior_prior_file,
    validate_behavior_prior_artifact,
)
from metacompanion_solver.cli import main
from metacompanion_solver.logging_store import deterministic_game_split
from test_behavior_learning import _behavior, _result, _state, _write_jsonl


def _games_for_split(split: str, count: int) -> list[str]:
    result: list[str] = []
    value = 1
    while len(result) < count:
        candidate = f"anon-{value:016x}"
        if deterministic_game_split(candidate) == split:
            result.append(candidate)
        value += 1
    return result


def _write_behavior_policy(path: Path) -> None:
    path.write_text(
        json.dumps(
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
                    "min_distinct_action_kinds": 2,
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
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def _write_prior_policy(path: Path) -> None:
    path.write_text(
        json.dumps(
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
                    "max_validation_kind_log_loss_excess": 0.0,
                    "max_validation_seen_template_log_loss_excess": 0.0,
                    "max_validation_unseen_template_rate": 0.0,
                },
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def _prepare_promoted_dataset(
    root: Path,
    *,
    change_held_out_actions: bool = False,
    change_outcomes: bool = False,
    use_location_actions: bool = False,
) -> dict[str, Path]:
    root.mkdir(parents=True, exist_ok=True)
    games = {
        split: _games_for_split(split, 2)
        for split in ("train", "validation", "test")
    }
    behavior_records: list[dict[str, object]] = []
    result_records: list[dict[str, object]] = []
    for split in ("train", "validation", "test"):
        first_kind = "location_activate" if use_location_actions else "play_card"
        second_kind = "attack"
        if change_held_out_actions and split != "train":
            first_kind = "end_turn"
            second_kind = "hero_power"
        behavior_records.extend(
            [
                _behavior(games[split][0], 1, "local", first_kind),
                _behavior(games[split][1], 1, "opponent", second_kind),
            ]
        )
        first_result = "loss" if change_outcomes else "win"
        second_result = "win" if change_outcomes else "loss"
        result_records.extend(
            [
                _result(games[split][0], first_result),
                _result(games[split][1], second_result),
            ]
        )
    behavior = root / "behavior-v1.jsonl"
    trajectory = root / "training-v2.jsonl"
    behavior_policy = root / "behavior-policy.json"
    prior_policy = root / "prior-policy.json"
    dataset = root / "behavior-imitation-v1.jsonl"
    manifest = root / "behavior-imitation-v1.manifest.json"
    output = root / "behavior-prior-v1.json"
    _write_jsonl(behavior, behavior_records)
    _write_jsonl(trajectory, result_records)
    _write_behavior_policy(behavior_policy)
    _write_prior_policy(prior_policy)
    promote_behavior_imitation_file(
        behavior,
        trajectory,
        dataset,
        manifest,
        policy_path=behavior_policy,
    )
    return {
        "behavior": behavior,
        "trajectory": trajectory,
        "behavior_policy": behavior_policy,
        "prior_policy": prior_policy,
        "dataset": dataset,
        "manifest": manifest,
        "output": output,
    }


class BehaviorPriorTrainingTests(unittest.TestCase):
    def test_action_template_distinguishes_board_position_and_keeps_legacy_shape(
        self,
    ) -> None:
        legacy = _behavior("anon-0000000000000001", 1, "local", "play_card")
        left = copy.deepcopy(legacy)
        middle = copy.deepcopy(legacy)
        left["action"]["board_position"] = 1
        middle["action"]["board_position"] = 2

        legacy_template = json.loads(_template_label(legacy))
        left_template = json.loads(_template_label(left))
        middle_template = json.loads(_template_label(middle))
        self.assertEqual(2, len(legacy_template))
        self.assertEqual([*legacy_template, 1], left_template)
        self.assertEqual([*legacy_template, 2], middle_template)
        self.assertNotEqual(left_template, middle_template)

    def test_hierarchical_strength_scales_with_sparse_label_vocabulary(self) -> None:
        self.assertEqual(8.0, _hierarchical_prior_strength(5, 0.5))
        self.assertEqual(250.0, _hierarchical_prior_strength(500, 0.5))

    def test_fixed_release_fixture_covers_all_splits_and_is_ready(self) -> None:
        fixtures = Path(__file__).resolve().parents[1] / "fixtures"
        with tempfile.TemporaryDirectory() as directory:
            artifact = train_behavior_prior_file(
                fixtures / "behavior-prior-readiness-v1.jsonl",
                fixtures / "behavior-prior-readiness-v1.manifest.json",
                Path(directory) / "behavior-prior-v1.json",
                policy_path=fixtures / "behavior-prior-readiness-policy-v1.json",
            )

        self.assertTrue(artifact["search_ordering_prior_ready"])
        self.assertEqual(
            {"train": 2, "validation": 2, "test": 2},
            artifact["source_dataset"]["split_record_counts"],
        )
        self.assertEqual(
            {"train": 2, "validation": 2, "test": 2},
            artifact["source_dataset"]["split_game_counts"],
        )
        self.assertFalse(artifact["live_policy_eligible"])
        self.assertFalse(artifact["optimality_verified"])

    def test_training_is_hash_bound_split_safe_and_never_claims_optimality(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )

        self.assertEqual(BEHAVIOR_PRIOR_SCHEMA_ID, artifact["schema"])
        self.assertTrue(artifact["imitation_training_complete"])
        self.assertTrue(artifact["search_ordering_prior_ready"])
        self.assertFalse(artifact["live_policy_eligible"])
        self.assertFalse(artifact["rl_training_eligible"])
        self.assertFalse(artifact["optimality_verified"])
        self.assertFalse(artifact["candidate_generation_allowed"])
        self.assertFalse(artifact["outcome_used_for_training"])
        self.assertEqual(
            {"train": 2, "validation": 2, "test": 2},
            artifact["source_dataset"]["split_record_counts"],
        )
        self.assertEqual(2, artifact["training"]["record_count"])
        self.assertEqual(2, artifact["training"]["game_count"])
        self.assertEqual("EVALUATED", artifact["evaluation"]["validation"]["status"])
        self.assertEqual("EVALUATED", artifact["evaluation"]["test"]["status"])
        self.assertTrue(all(item["passed"] for item in artifact["quality_checks"]))
        self.assertEqual(
            2,
            artifact["models"]["action_kind"]["counts_by_level"]["global"][
                "[]"
            ]["total"],
        )
        self.assertNotIn("anon-", json.dumps(artifact, ensure_ascii=False))
        validate_behavior_prior_artifact(artifact)

    def test_legacy_v1_artifact_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )
            artifact["schema"] = "behavior-imitation-prior-v1"
            paths["output"].write_text(
                json.dumps(artifact, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(
                BehaviorPriorError, "unsupported behavior prior artifact schema"
            ):
                load_behavior_prior(paths["output"])

    def test_unseen_location_kind_keeps_a_smoothed_empty_template_model(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )

        self.assertIn("location_activate", artifact["models"]["action_kind"]["labels"])
        location_model = artifact["models"]["action_template_by_kind"][
            "location_activate"
        ]
        self.assertEqual(0, location_model["counts_by_level"]["global"]["[]"]["total"])
        self.assertGreater(float(location_model["alpha"]), 0.0)
        validate_behavior_prior_artifact(artifact)

    def test_location_training_and_legal_candidate_ordering_are_supported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(
                Path(directory), use_location_actions=True
            )
            train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )
            artifact = load_behavior_prior(paths["output"])

        location_global = artifact["models"]["action_template_by_kind"][
            "location_activate"
        ]["counts_by_level"]["global"]["[]"]
        self.assertEqual(1, location_global["total"])
        self.assertEqual(1, artifact["training"]["action_kind_record_counts"][
            "location_activate"
        ])

        state = _state("friendly", "location-candidate-state")
        probabilities = score_legal_behavior_candidates(
            artifact,
            pre_state=state,
            actor_side="local",
            actor_player_id="friendly",
            actions=[
                {
                    "kind": "location_activate",
                    "source_entity_id": "f-location",
                    "target_entity_id": "o-hero",
                    "card_id": "F_LOCATION",
                },
                {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                },
            ],
        )
        self.assertAlmostEqual(1.0, sum(probabilities))
        self.assertGreater(probabilities[0], probabilities[1])

    def test_held_out_actions_cannot_change_trained_model(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original_paths = _prepare_promoted_dataset(root / "original")
            changed_paths = _prepare_promoted_dataset(
                root / "changed", change_held_out_actions=True
            )
            original = train_behavior_prior_file(
                original_paths["dataset"],
                original_paths["manifest"],
                original_paths["output"],
                policy_path=original_paths["prior_policy"],
            )
            changed = train_behavior_prior_file(
                changed_paths["dataset"],
                changed_paths["manifest"],
                changed_paths["output"],
                policy_path=changed_paths["prior_policy"],
            )

        self.assertEqual(original["models"], changed["models"])
        self.assertEqual(
            original["training"]["action_kind_record_counts"],
            changed["training"]["action_kind_record_counts"],
        )
        self.assertNotEqual(original["evaluation"], changed["evaluation"])

    def test_outcomes_do_not_change_behavior_model_or_evaluation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original_paths = _prepare_promoted_dataset(root / "original")
            changed_paths = _prepare_promoted_dataset(
                root / "changed", change_outcomes=True
            )
            original = train_behavior_prior_file(
                original_paths["dataset"],
                original_paths["manifest"],
                original_paths["output"],
                policy_path=original_paths["prior_policy"],
            )
            changed = train_behavior_prior_file(
                changed_paths["dataset"],
                changed_paths["manifest"],
                changed_paths["output"],
                policy_path=changed_paths["prior_policy"],
            )

        self.assertEqual(original["models"], changed["models"])
        self.assertEqual(original["evaluation"], changed["evaluation"])
        self.assertFalse(original["outcome_used_for_training"])

    def test_dataset_or_manifest_tampering_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            paths["dataset"].write_bytes(paths["dataset"].read_bytes() + b"\n")
            with self.assertRaises(BehaviorPriorError):
                train_behavior_prior_file(
                    paths["dataset"],
                    paths["manifest"],
                    paths["output"],
                    policy_path=paths["prior_policy"],
                )

        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            manifest = json.loads(paths["manifest"].read_text(encoding="utf-8"))
            manifest["rl_training_ready"] = True
            paths["manifest"].write_text(
                json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(BehaviorPriorError, "cannot be RL-ready"):
                train_behavior_prior_file(
                    paths["dataset"],
                    paths["manifest"],
                    paths["output"],
                    policy_path=paths["prior_policy"],
                )

        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            manifest = json.loads(paths["manifest"].read_text(encoding="utf-8"))
            del manifest["audit"]["metrics"]["replay_attack_record_count"]
            paths["manifest"].write_text(
                json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(
                BehaviorPriorError, "missing the replay transition contract"
            ):
                train_behavior_prior_file(
                    paths["dataset"],
                    paths["manifest"],
                    paths["output"],
                    policy_path=paths["prior_policy"],
                )

    def test_default_production_policy_refuses_tiny_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"], paths["manifest"], paths["output"]
            )

        self.assertFalse(artifact["search_ordering_prior_ready"])
        self.assertTrue(artifact["imitation_training_complete"])
        self.assertTrue(any(not item["passed"] for item in artifact["quality_checks"]))

    def test_policy_rejects_zero_sample_thresholds(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            policy = json.loads(paths["prior_policy"].read_text(encoding="utf-8"))
            policy["thresholds"]["min_validation_records"] = 0
            paths["prior_policy"].write_text(
                json.dumps(policy, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            with self.assertRaisesRegex(BehaviorPriorError, "positive integer"):
                train_behavior_prior_file(
                    paths["dataset"],
                    paths["manifest"],
                    paths["output"],
                    policy_path=paths["prior_policy"],
                )

    def test_output_must_not_overlap_input_or_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            with self.assertRaises(BehaviorPriorError):
                train_behavior_prior_file(
                    paths["dataset"],
                    paths["manifest"],
                    paths["dataset"],
                    policy_path=paths["prior_policy"],
                )
            with self.assertRaises(BehaviorPriorError):
                train_behavior_prior_file(
                    paths["dataset"],
                    paths["manifest"],
                    paths["prior_policy"],
                    policy_path=paths["prior_policy"],
                )

    def test_artifact_readiness_and_hierarchical_counts_are_self_checked(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )

        readiness_tamper = copy.deepcopy(artifact)
        readiness_tamper["search_ordering_prior_ready"] = False
        with self.assertRaisesRegex(BehaviorPriorError, "readiness contradicts"):
            validate_behavior_prior_artifact(readiness_tamper)

        count_tamper = copy.deepcopy(artifact)
        actor_buckets = count_tamper["models"]["action_kind"]["counts_by_level"][
            "actor"
        ]
        first_bucket = next(iter(actor_buckets.values()))
        first_label = next(iter(first_bucket["counts"]))
        first_bucket["counts"][first_label] += 1
        first_bucket["total"] += 1
        with self.assertRaisesRegex(BehaviorPriorError, "do not reconcile"):
            validate_behavior_prior_artifact(count_tamper)

    def test_artifact_quality_check_rejects_non_numeric_values_cleanly(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )

        for invalid in (None, "2", True, float("inf")):
            tampered = copy.deepcopy(artifact)
            tampered["quality_checks"][0]["actual"] = invalid
            with self.subTest(invalid=invalid):
                with self.assertRaisesRegex(BehaviorPriorError, "non-finite number"):
                    validate_behavior_prior_artifact(tampered)


class BehaviorPriorScoringTests(unittest.TestCase):
    def test_ready_prior_only_orders_supplied_legal_actions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            train_behavior_prior_file(
                paths["dataset"],
                paths["manifest"],
                paths["output"],
                policy_path=paths["prior_policy"],
            )
            artifact = load_behavior_prior(paths["output"])

        state = _state("friendly", "candidate-state")
        actions = [
            {
                "kind": "play_card",
                "source_entity_id": "f-hand",
                "target_entity_id": "",
                "card_id": "F_HAND",
            },
            {
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": "",
            },
        ]
        probabilities = score_legal_behavior_candidates(
            artifact,
            pre_state=state,
            actor_side="local",
            actor_player_id="friendly",
            actions=actions,
        )
        self.assertAlmostEqual(1.0, sum(probabilities))
        self.assertGreater(probabilities[0], probabilities[1])
        self.assertEqual(2, len(actions))
        state["patch"] = "unseen-patch"
        self.assertEqual(
            [0.5, 0.5],
            score_legal_behavior_candidates(
                artifact,
                pre_state=state,
                actor_side="local",
                actor_player_id="friendly",
                actions=actions,
            ),
        )

    def test_not_ready_prior_cannot_score_candidates(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            artifact = train_behavior_prior_file(
                paths["dataset"], paths["manifest"], paths["output"]
            )
        with self.assertRaisesRegex(BehaviorPriorError, "not ready"):
            score_legal_behavior_candidates(
                artifact,
                pre_state=_state("friendly", "candidate-state"),
                actor_side="local",
                actor_player_id="friendly",
                actions=[{"kind": "end_turn"}],
            )


class BehaviorPriorCliTests(unittest.TestCase):
    def test_cli_uses_readiness_exit_code_and_writes_unicode_artifact(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_promoted_dataset(Path(directory))
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                exit_code = main(
                    [
                        "train-behavior-prior",
                        "--input",
                        str(paths["dataset"]),
                        "--manifest",
                        str(paths["manifest"]),
                        "--output",
                        str(paths["output"]),
                        "--policy",
                        str(paths["prior_policy"]),
                    ]
                )
            payload = paths["output"].read_bytes()
            report = json.loads(stdout.getvalue())

        self.assertEqual(0, exit_code)
        self.assertTrue(report["search_ordering_prior_ready"])
        self.assertIn("观察行为不等于最优动作".encode("utf-8"), payload)


if __name__ == "__main__":
    unittest.main()
