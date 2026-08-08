from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.behavior import create_behavior_record
from metacompanion_solver.behavior_candidate_alignment import (
    BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY_SCHEMA_ID,
    BEHAVIOR_CANDIDATE_ALIGNMENT_REPORT_SCHEMA_ID,
    audit_behavior_candidate_alignment_files,
)
from metacompanion_solver.behavior_learning import (
    BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
    promote_behavior_imitation_file,
)
from metacompanion_solver.behavior_prior import BehaviorPriorError
from metacompanion_solver.card_rules import default_structured_card_rule_path
from metacompanion_solver.cli import main
from metacompanion_solver.logging_store import deterministic_game_split
from test_behavior_learning import _result, _write_jsonl


def _entity(
    entity_id: str,
    card_id: str,
    card_type: str,
    *,
    attack: int = 0,
    health: int = 0,
    can_attack: bool = False,
) -> dict[str, object]:
    return {
        "entity_id": entity_id,
        "card_id": card_id,
        "card_type": card_type,
        "cost": 0,
        "attack": attack,
        "health": health,
        "current_health": health,
        "playable": True,
        "can_attack": can_attack,
        "attacks_remaining": 1 if can_attack else 0,
    }


def _player(role: str, *, attacking_hero: bool) -> dict[str, object]:
    prefix = "f" if role == "friendly" else "o"
    return {
        "player_id": role,
        "hero": _entity(
            f"{prefix}-hero",
            f"{prefix.upper()}_HERO",
            "HERO",
            attack=1 if attacking_hero else 0,
            health=30,
            can_attack=attacking_hero,
        ),
        "hero_power": None,
        "weapon": None,
        "hand": [],
        "board": [],
        "mana": 0,
        "max_mana": 0,
        "armor": 0,
        "deck_size": 20,
        "fatigue": 0,
        "hero_power_available": False,
        "spell_power": 0,
    }


def _state(
    state_id: str, *, patch: str, active_player_id: str = "friendly"
) -> dict[str, object]:
    return {
        "state_id": state_id,
        "turn": 5,
        "active_player_id": active_player_id,
        "perspective_player_id": "friendly",
        "friendly": _player("friendly", attacking_hero=True),
        "opponent": _player("opponent", attacking_hero=False),
        "patch": patch,
        "mode": "standard",
    }


_COMBAT_BOOLEAN_EVIDENCE = {
    "current_health_known": True,
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


def _add_complete_public_combat_evidence(
    state: dict[str, object], *, missing_field: str | None = None
) -> None:
    for role in ("friendly", "opponent"):
        player = state[role]
        player["public_rule_tags"] = {
            "STEADY_SHOT_CAN_TARGET": 0,
            "HERO_POWER_DOUBLE": 0,
        }
        player["public_rule_tags_complete"] = True
        entities = [player["hero"], *player["board"]]
        if player["weapon"] is not None:
            entities.append(player["weapon"])
        for entity in entities:
            if entity["card_type"] in {"HERO", "MINION", "WEAPON"}:
                entity.update(_COMBAT_BOOLEAN_EVIDENCE)
    if missing_field is not None:
        state["friendly"]["board"][0].pop(missing_field)


def _game_for_split(split: str, start: int) -> str:
    value = start
    while True:
        game_id = f"anon-{value:016x}"
        if deterministic_game_split(game_id) == split:
            return game_id
        value += 1


def _attack_behavior(game_id: str, *, patch: str) -> dict[str, object]:
    pre_state = _state(f"{game_id}-pre", patch=patch)
    post_state = _state(f"{game_id}-post", patch=patch)
    post_state["friendly"]["hero"]["can_attack"] = False
    post_state["friendly"]["hero"]["attacks_remaining"] = 0
    return create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00+08:00",
        actor_side="local",
        actor_player_id="friendly",
        actor_evidence="hdt_player_event",
        identity_status="exact_public_entity",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="player_attack",
        action={
            "kind": "attack",
            "source_entity_id": "f-hero",
            "target_entity_id": "o-hero",
            "card_id": "F_HERO",
        },
        pre_state=pre_state,
        post_state=post_state,
    ).to_dict()


def _combat_attack_behavior_impl(
    game_id: str, *, patch: str, missing_field: str | None = None
) -> dict[str, object]:
    pre_state = _state(f"{game_id}-pre", patch=patch)
    pre_state["friendly"]["board"] = [
        _entity(
            "f-minion",
            "F_MINION",
            "MINION",
            attack=2,
            health=3,
            can_attack=True,
        )
    ]
    post_state = _state(f"{game_id}-post", patch=patch)
    post_state["friendly"]["board"] = [
        _entity(
            "f-minion",
            "F_MINION",
            "MINION",
            attack=2,
            health=3,
            can_attack=True,
        )
    ]
    post_state["friendly"]["hero"]["can_attack"] = False
    post_state["friendly"]["hero"]["attacks_remaining"] = 0
    _add_complete_public_combat_evidence(
        pre_state, missing_field=missing_field
    )
    _add_complete_public_combat_evidence(post_state)
    return create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00+08:00",
        actor_side="local",
        actor_player_id="friendly",
        actor_evidence="hdt_player_event",
        identity_status="exact_public_entity",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="player_attack",
        action={
            "kind": "attack",
            "source_entity_id": "f-hero",
            "target_entity_id": "o-hero",
            "card_id": "F_HERO",
        },
        pre_state=pre_state,
        post_state=post_state,
    ).to_dict()


def _combat_attack_behavior(game_id: str, *, patch: str) -> dict[str, object]:
    return _combat_attack_behavior_impl(game_id, patch=patch)


def _combat_attack_behavior_missing_evidence(
    game_id: str, *, patch: str
) -> dict[str, object]:
    return _combat_attack_behavior_impl(
        game_id, patch=patch, missing_field="stealth"
    )


def _opponent_end_turn_behavior(game_id: str, *, patch: str) -> dict[str, object]:
    pre_state = _state(
        f"{game_id}-pre", patch=patch, active_player_id="opponent"
    )
    post_state = _state(
        f"{game_id}-post", patch=patch, active_player_id="friendly"
    )
    return create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00+08:00",
        actor_side="opponent",
        actor_player_id="opponent",
        actor_evidence="active_player",
        identity_status="event_only",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="turn_passed_to_player",
        action={
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": "",
        },
        pre_state=pre_state,
        post_state=post_state,
    ).to_dict()


def _location_behavior(game_id: str, *, patch: str) -> dict[str, object]:
    pre_state = _state(f"{game_id}-pre", patch=patch)
    pre_state["friendly"]["board"] = [
        _entity("f-location", "F_LOCATION", "LOCATION", health=2)
    ]
    post_state = _state(f"{game_id}-post", patch=patch)
    post_state["friendly"]["board"] = [
        _entity("f-location", "F_LOCATION", "LOCATION", health=2)
    ]
    return create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00+08:00",
        actor_side="local",
        actor_player_id="friendly",
        actor_evidence="hdt_power_log",
        identity_status="exact_public_entity",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="hdt_power_log",
        action={
            "kind": "location_activate",
            "source_entity_id": "f-location",
            "target_entity_id": "",
            "card_id": "F_LOCATION",
        },
        pre_state=pre_state,
        post_state=post_state,
    ).to_dict()


def _position_behavior(game_id: str, *, patch: str) -> dict[str, object]:
    pre_state = _state(f"{game_id}-pre", patch=patch)
    pre_state["friendly"]["hand"] = [
        _entity("f-hand", "F_MINION", "MINION", attack=2, health=2)
    ]
    post_state = _state(f"{game_id}-post", patch=patch)
    post_state["friendly"]["board"] = [
        _entity("f-hand", "F_MINION", "MINION", attack=2, health=2)
    ]
    return create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00+08:00",
        actor_side="local",
        actor_player_id="friendly",
        actor_evidence="hdt_power_log",
        identity_status="exact_public_entity",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="hdt_power_log",
        action={
            "kind": "play_card",
            "source_entity_id": "f-hand",
            "target_entity_id": "",
            "card_id": "F_MINION",
            "board_position": 1,
            "choice_status": "none",
        },
        pre_state=pre_state,
        post_state=post_state,
    ).to_dict()


def _position_behavior_missing(game_id: str, *, patch: str) -> dict[str, object]:
    pre_state = _state(f"{game_id}-pre", patch=patch)
    pre_state["friendly"]["hand"] = [
        _entity("f-hand", "F_MINION", "MINION", attack=2, health=2)
    ]
    post_state = _state(f"{game_id}-post", patch=patch)
    post_state["friendly"]["board"] = [
        _entity("f-hand", "F_MINION", "MINION", attack=2, health=2)
    ]
    return create_behavior_record(
        game_id=game_id,
        behavior_sequence=1,
        observed_at_utc="2026-08-01T00:00:00+08:00",
        actor_side="local",
        actor_player_id="friendly",
        actor_evidence="hdt_power_log",
        identity_status="exact_public_entity",
        visibility_status="public_pre_state",
        boundary_status="isolated",
        source_event="hdt_power_log",
        action={
            "kind": "play_card",
            "source_entity_id": "f-hand",
            "target_entity_id": "",
            "card_id": "F_MINION",
            "choice_status": "none",
        },
        pre_state=pre_state,
        post_state=post_state,
    ).to_dict()


def _write_policy(path: Path, *, schema: str, thresholds: dict[str, object]) -> None:
    path.write_text(
        json.dumps(
            {"schema": schema, "thresholds": thresholds},
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def _prepare_ready_dataset(
    root: Path, *, patch: str = "247416", local_behavior=_attack_behavior
) -> dict[str, Path]:
    local_games = {
        split: _game_for_split(split, index * 100 + 1)
        for index, split in enumerate(("train", "validation", "test"), start=1)
    }
    opponent_games = {
        split: _game_for_split(split, index * 100 + 50)
        for index, split in enumerate(("train", "validation", "test"), start=1)
    }
    behavior = root / "behavior-v1.jsonl"
    results = root / "training-v2.jsonl"
    behavior_policy = root / "behavior-policy.json"
    dataset = root / "behavior-imitation-v1.jsonl"
    manifest = root / "behavior-imitation-v1.manifest.json"
    candidate_policy = root / "candidate-policy.json"
    _write_jsonl(
        behavior,
        [
            item
            for split in local_games
            for item in (
                local_behavior(local_games[split], patch=patch),
                _opponent_end_turn_behavior(opponent_games[split], patch=patch),
            )
        ],
    )
    _write_jsonl(
        results,
        [
            item
            for split in local_games
            for item in (
                _result(local_games[split], "win"),
                _result(opponent_games[split], "loss"),
            )
        ],
    )
    _write_policy(
        behavior_policy,
        schema=BEHAVIOR_LEARNING_POLICY_SCHEMA_ID,
        thresholds={
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
            "min_train_games": 1,
            "min_validation_games": 1,
            "min_test_games": 1,
        },
    )
    _write_policy(
        candidate_policy,
        schema=BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY_SCHEMA_ID,
        thresholds={
            "min_train_eligible_games": 1,
            "min_validation_eligible_games": 1,
            "min_test_eligible_games": 1,
            "min_train_eligible_records": 1,
            "min_validation_eligible_records": 1,
            "min_test_eligible_records": 1,
            "min_local_exact_alignment_rate": 1.0,
            "min_local_candidate_set_eligible_rate": 1.0,
        },
    )
    promote_behavior_imitation_file(
        behavior,
        results,
        dataset,
        manifest,
        policy_path=behavior_policy,
    )
    return {
        "dataset": dataset,
        "manifest": manifest,
        "policy": candidate_policy,
    }


class BehaviorCandidateAlignmentTests(unittest.TestCase):
    def test_dynamic_complete_candidate_sets_can_reach_ready(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(Path(directory))
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        self.assertEqual(
            BEHAVIOR_CANDIDATE_ALIGNMENT_REPORT_SCHEMA_ID, report["schema"]
        )
        self.assertEqual("READY", report["status"])
        self.assertTrue(report["candidate_ranking_training_ready"])
        self.assertEqual(6, report["metrics"]["overall"]["exact_count"])
        self.assertEqual(
            3, report["metrics"]["overall"]["candidate_set_eligible_count"]
        )
        self.assertEqual(
            {"train": 1, "validation": 1, "test": 1},
            report["metrics"]["candidate_set_eligible_split_record_counts"],
        )
        self.assertEqual(
            3,
            report["metrics"]["candidate_set_blocker_record_counts"][
                "opponent_actions_excluded_from_candidate_set_training"
            ],
        )
        self.assertEqual(
            {},
            report["metrics"][
                "candidate_set_blocker_record_counts_by_actor_side"
            ]["local"],
        )
        self.assertFalse(report["candidate_generation_allowed"])
        self.assertFalse(report["live_policy_eligible"])
        self.assertFalse(report["rl_training_eligible"])
        self.assertFalse(report["optimality_verified"])
        self.assertNotIn("anon-", json.dumps(report, ensure_ascii=False))
        self.assertNotIn("f-hero", json.dumps(report, ensure_ascii=False))

    def test_fixed_prior_fixture_fails_candidate_completeness_gate(self) -> None:
        fixtures = Path(__file__).resolve().parents[1] / "fixtures"
        report = audit_behavior_candidate_alignment_files(
            fixtures / "behavior-prior-readiness-v1.jsonl",
            fixtures / "behavior-prior-readiness-v1.manifest.json",
            policy_path=fixtures / "behavior-candidate-alignment-policy-v1.json",
            rules_path=default_structured_card_rule_path(),
        )

        self.assertEqual("NOT_READY", report["status"])
        self.assertFalse(report["candidate_ranking_training_ready"])
        self.assertEqual(3, report["metrics"]["overall"]["exact_count"])
        self.assertEqual(0, report["metrics"]["overall"]["candidate_set_eligible_count"])
        blockers = report["metrics"]["candidate_set_blocker_record_counts"]
        self.assertEqual(6, blockers["structured_rules_build_mismatch"])
        self.assertEqual(3, blockers["opponent_hidden_hand_unavailable"])
        self.assertEqual(6, blockers["board_combat_rules_unverified"])
        self.assertGreater(blockers["actionable_card_rules_unverified"], 0)

    def test_explicit_public_combat_evidence_clears_the_combat_blocker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(
                Path(directory), local_behavior=_combat_attack_behavior
            )
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        self.assertEqual("READY", report["status"])
        self.assertEqual(
            3,
            report["metrics"]["by_actor_side"]["local"][
                "candidate_set_eligible_count"
            ],
        )
        self.assertNotIn(
            "board_combat_rules_unverified",
            report["metrics"]["candidate_set_blocker_record_counts"],
        )

    def test_one_missing_public_combat_field_keeps_the_blocker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(
                Path(directory),
                local_behavior=_combat_attack_behavior_missing_evidence,
            )
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        self.assertEqual("NOT_READY", report["status"])
        self.assertEqual(
            3,
            report["metrics"]["candidate_set_blocker_record_counts"][
                "board_combat_rules_unverified"
            ],
        )
        self.assertEqual(
            0,
            report["metrics"]["by_actor_side"]["local"][
                "candidate_set_eligible_count"
            ],
        )

    def test_rule_bundle_is_never_applied_across_carddefs_builds(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(Path(directory), patch="247415")
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        rules = report["structured_rules"]
        self.assertEqual(0, rules["build_match_record_count"])
        self.assertEqual(6, rules["build_mismatch_record_count"])
        self.assertEqual(0, rules["matched_entity_count"])
        self.assertFalse(report["candidate_ranking_training_ready"])
        self.assertEqual(
            6,
            report["metrics"]["candidate_set_blocker_record_counts"][
                "structured_rules_build_mismatch"
            ],
        )

    def test_locations_remain_observations_but_are_not_generated_candidates(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(
                Path(directory), local_behavior=_location_behavior
            )
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        local = report["metrics"]["by_actor_side"]["local"]
        blockers = report["metrics"]["candidate_set_blocker_record_counts"]
        self.assertEqual(3, local["not_generated_count"])
        self.assertEqual(3, blockers["location_activation_not_generated"])
        self.assertEqual(3, blockers["board_locations_not_modeled"])
        self.assertEqual(0, local["candidate_set_eligible_count"])
        self.assertFalse(report["candidate_ranking_training_ready"])

    def test_minion_board_position_is_an_exact_candidate_dimension(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(
                Path(directory), local_behavior=_position_behavior
            )
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        local = report["metrics"]["by_actor_side"]["local"]
        blockers = report["metrics"]["candidate_set_blocker_record_counts"]
        self.assertEqual(3, local["exact_count"])
        self.assertNotIn("minion_board_positions_not_modeled", blockers)
        self.assertEqual(3, blockers["actionable_card_rules_unverified"])
        self.assertEqual(0, local["candidate_set_eligible_count"])
        self.assertFalse(report["candidate_ranking_training_ready"])

    def test_legacy_minion_action_without_position_remains_not_generated(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(
                Path(directory), local_behavior=_position_behavior_missing
            )
            report = audit_behavior_candidate_alignment_files(
                paths["dataset"],
                paths["manifest"],
                policy_path=paths["policy"],
                rules_path=default_structured_card_rule_path(),
            )

        local = report["metrics"]["by_actor_side"]["local"]
        blockers = report["metrics"]["candidate_set_blocker_record_counts"]
        self.assertEqual(3, local["not_generated_count"])
        self.assertEqual(3, blockers["observed_action_not_exactly_generated"])
        self.assertEqual(0, local["candidate_set_eligible_count"])
        self.assertFalse(report["candidate_ranking_training_ready"])

    def test_manifest_tampering_fails_before_candidate_audit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            paths = _prepare_ready_dataset(Path(directory))
            manifest = json.loads(paths["manifest"].read_text(encoding="utf-8"))
            manifest["imitation_dataset"]["sha256"] = "0" * 64
            paths["manifest"].write_text(
                json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )

            with self.assertRaisesRegex(
                BehaviorPriorError, "manifest does not bind the dataset bytes"
            ):
                audit_behavior_candidate_alignment_files(
                    paths["dataset"],
                    paths["manifest"],
                    policy_path=paths["policy"],
                    rules_path=default_structured_card_rule_path(),
                )

    def test_cli_writes_not_ready_report_and_returns_gate_exit_code(self) -> None:
        fixtures = Path(__file__).resolve().parents[1] / "fixtures"
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "candidate-alignment.json"
            with contextlib.redirect_stdout(io.StringIO()):
                exit_code = main(
                    [
                        "audit-behavior-candidates",
                        "--input",
                        str(fixtures / "behavior-prior-readiness-v1.jsonl"),
                        "--manifest",
                        str(
                            fixtures
                            / "behavior-prior-readiness-v1.manifest.json"
                        ),
                        "--policy",
                        str(
                            fixtures
                            / "behavior-candidate-alignment-policy-v1.json"
                        ),
                        "--rules",
                        str(default_structured_card_rule_path()),
                        "--output",
                        str(output),
                    ]
                )
            persisted = json.loads(output.read_text(encoding="utf-8"))

        self.assertEqual(3, exit_code)
        self.assertEqual("NOT_READY", persisted["status"])


if __name__ == "__main__":
    unittest.main()
