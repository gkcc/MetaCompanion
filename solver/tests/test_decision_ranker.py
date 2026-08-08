from __future__ import annotations

import contextlib
import copy
import io
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.cli import main
from metacompanion_solver.decision_frame import create_decision_frame_record
from metacompanion_solver.decision_ranker import (
    DECISION_RANKER_POLICY_SCHEMA_ID,
    DecisionRankerError,
    load_decision_ranker,
    score_legal_decision_candidates,
    train_decision_ranker_file,
    validate_decision_ranker_artifact,
)
from metacompanion_solver.logging_store import deterministic_game_split
from test_behavior_learning import _behavior, _write_jsonl
from test_behavior_prior import _games_for_split


def _policy(path: Path) -> None:
    path.write_text(
        json.dumps(
            {
                "schema": DECISION_RANKER_POLICY_SCHEMA_ID,
                "thresholds": {
                    "min_train_games": 2,
                    "min_validation_games": 2,
                    "min_test_games": 2,
                    "min_train_records": 2,
                    "min_validation_records": 2,
                    "min_test_records": 2,
                    "min_validation_top1_lift_over_uniform": -1.0,
                    "min_validation_top3_lift_over_uniform": -1.0,
                    "max_validation_log_loss_excess": 100.0,
                    "max_validation_unseen_selected_template_rate": 1.0,
                },
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def _action(value: dict[str, object]) -> dict[str, object]:
    raw = value["action"]
    assert isinstance(raw, dict)
    return {
        "kind": raw["kind"],
        "source_entity_id": raw["source_entity_id"],
        "target_entity_id": raw["target_entity_id"],
        "card_id": raw["card_id"],
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


def _prepare(root: Path, *, held_out_selects_end_turn: bool = False) -> dict[str, Path]:
    root.mkdir(parents=True, exist_ok=True)
    games = {
        split: _games_for_split(split, 2)
        for split in ("train", "validation", "test")
    }
    behaviors: list[dict[str, object]] = []
    frames: list[dict[str, object]] = []
    for split in ("train", "validation", "test"):
        for game_id in games[split]:
            observed = _behavior(game_id, 1, "local", "attack")
            behaviors.append(observed)
            selected = _action(observed)
            if held_out_selects_end_turn and split != "train":
                selected = _end_turn()
            pre_state = observed["pre_state"]
            post_state = observed["post_state"]
            assert isinstance(pre_state, dict)
            assert isinstance(post_state, dict)
            candidates = [
                {
                    "option_id": 0,
                    "action": _end_turn(),
                    "target_evidence": "not_applicable",
                    "position_evidence": "not_applicable",
                },
                {
                    "option_id": 1,
                    "action": _action(observed),
                    "target_evidence": "hdt_error_none",
                    "position_evidence": "not_applicable",
                },
            ]
            if selected["kind"] == "end_turn":
                # The selected behavior identity must still describe the exact selected
                # action, so rebuild the held-out behavior instead of relabeling a frame.
                replacement = _behavior(game_id, 1, "local", "end_turn")
                behaviors[-1] = replacement
                observed = replacement
                pre_state = replacement["pre_state"]
                post_state = replacement["post_state"]
                assert isinstance(pre_state, dict)
                assert isinstance(post_state, dict)
                attack = _behavior(game_id, 1, "local", "attack")
                candidates[1]["action"] = _action(attack)
            frame = create_decision_frame_record(
                game_id=game_id,
                decision_sequence=1,
                observed_at_utc="2026-08-01T00:00:00Z",
                client_build="fixture-patch",
                mode="standard",
                selected_behavior_id=str(observed["behavior_id"]),
                hdt_frame_id=1,
                pre_state=pre_state,
                post_state=post_state,
                selected_action=selected,
                legal_candidates=candidates,
            )
            frames.append(frame.to_dict())
    behavior = root / "behavior-v1.jsonl"
    decision = root / "advisor-decision-frame-v1.jsonl"
    policy = root / "decision-ranker-policy.json"
    output = root / "decision-ranker-v1.json"
    _write_jsonl(behavior, behaviors)
    _write_jsonl(decision, frames)
    _policy(policy)
    return {
        "behavior": behavior,
        "decision": decision,
        "policy": policy,
        "output": output,
    }


class DecisionRankerTrainingTests(unittest.TestCase):
    def test_listwise_training_is_split_safe_and_never_claims_optimality(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare(Path(directory))
            artifact = train_decision_ranker_file(
                paths["decision"],
                paths["behavior"],
                paths["output"],
                policy_path=paths["policy"],
                max_epochs=1,
            )
            loaded = load_decision_ranker(paths["output"])

        self.assertTrue(artifact["candidate_ranking_ready"])
        self.assertTrue(artifact["user_visible_behavior_reference_eligible"])
        self.assertEqual(artifact, loaded)
        self.assertEqual("train", artifact["training"]["split"])
        self.assertTrue(artifact["training"]["game_level_split"])
        self.assertFalse(artifact["training"]["outcome_used"])
        self.assertFalse(artifact["training"]["opponent_candidates_used"])
        self.assertFalse(artifact["candidate_generation_allowed"])
        self.assertFalse(artifact["live_policy_eligible"])
        self.assertFalse(artifact["rl_training_eligible"])
        self.assertFalse(artifact["optimality_verified"])
        self.assertIn(
            "user_visible_hdt_legal_behavior_reference",
            artifact["approved_uses"],
        )
        serialized = json.dumps(artifact, ensure_ascii=False, sort_keys=True)
        self.assertNotIn("anon-", serialized)
        self.assertNotIn('"game_id"', serialized)
        self.assertNotIn('"state_id"', serialized)
        self.assertNotIn('"entity_id"', serialized)
        for split in ("train", "validation", "test"):
            self.assertEqual(2, artifact["evaluation"][split]["game_count"])
            self.assertEqual(2, artifact["evaluation"][split]["record_count"])

    def test_only_caller_supplied_candidates_are_scored(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare(Path(directory))
            train_decision_ranker_file(
                paths["decision"],
                paths["behavior"],
                paths["output"],
                policy_path=paths["policy"],
                max_epochs=1,
            )
            artifact = load_decision_ranker(paths["output"])
            first = json.loads(paths["decision"].read_text(encoding="utf-8").splitlines()[0])
            actions = [item["action"] for item in first["legal_candidates"]]
            scores = score_legal_decision_candidates(
                artifact,
                pre_state=first["pre_state"],
                mode=first["mode"],
                actions=actions,
            )

        self.assertEqual(len(actions), len(scores))
        self.assertAlmostEqual(1.0, sum(scores), places=12)
        self.assertTrue(all(score > 0.0 for score in scores))

    def test_held_out_labels_do_not_change_learned_weights(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = _prepare(root / "original")
            changed = _prepare(root / "changed", held_out_selects_end_turn=True)
            first = train_decision_ranker_file(
                original["decision"],
                original["behavior"],
                original["output"],
                policy_path=original["policy"],
                max_epochs=1,
            )
            second = train_decision_ranker_file(
                changed["decision"],
                changed["behavior"],
                changed["output"],
                policy_path=changed["policy"],
                max_epochs=1,
            )

        self.assertEqual(first["model"]["weights"], second["model"]["weights"])
        self.assertNotEqual(
            first["evaluation"]["validation"],
            second["evaluation"]["validation"],
        )

    def test_contract_tampering_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare(Path(directory))
            artifact = train_decision_ranker_file(
                paths["decision"],
                paths["behavior"],
                paths["output"],
                policy_path=paths["policy"],
                max_epochs=1,
            )

        unsafe = copy.deepcopy(artifact)
        unsafe["rl_training_eligible"] = True
        with self.assertRaises(DecisionRankerError):
            validate_decision_ranker_artifact(unsafe)
        unsafe_reference = copy.deepcopy(artifact)
        unsafe_reference["user_visible_behavior_reference_eligible"] = False
        with self.assertRaises(DecisionRankerError):
            validate_decision_ranker_artifact(unsafe_reference)
        count_tamper = copy.deepcopy(artifact)
        count_tamper["model"]["weight_count"] += 1
        with self.assertRaises(DecisionRankerError):
            validate_decision_ranker_artifact(count_tamper)

    def test_cli_reports_chinese_error_and_readiness_exit_code(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare(Path(directory))
            stdout = io.StringIO()
            with contextlib.redirect_stdout(stdout):
                code = main(
                    [
                        "train-decision-ranker",
                        "--decision-frames",
                        str(paths["decision"]),
                        "--behavior",
                        str(paths["behavior"]),
                        "--output",
                        str(paths["output"]),
                        "--policy",
                        str(paths["policy"]),
                        "--epochs",
                        "1",
                    ]
                )
            stderr = io.StringIO()
            with contextlib.redirect_stderr(stderr):
                missing = main(
                    [
                        "train-decision-ranker",
                        "--decision-frames",
                        str(paths["decision"].with_name("missing.jsonl")),
                        "--behavior",
                        str(paths["behavior"]),
                        "--output",
                        str(paths["output"].with_name("other.json")),
                        "--epochs",
                        "1",
                    ]
                )

        self.assertEqual(0, code)
        self.assertTrue(json.loads(stdout.getvalue())["candidate_ranking_ready"])
        self.assertEqual(2, missing)
        self.assertTrue(stderr.getvalue().startswith("错误："))


class DecisionRankerSplitFixtureTests(unittest.TestCase):
    def test_fixture_games_really_cover_each_stable_split(self) -> None:
        for split in ("train", "validation", "test"):
            games = _games_for_split(split, 2)
            self.assertEqual({split}, {deterministic_game_split(game) for game in games})


if __name__ == "__main__":
    unittest.main()
