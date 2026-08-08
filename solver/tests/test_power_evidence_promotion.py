from __future__ import annotations

import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from metacompanion_solver.power_evidence import (
    PowerEvidenceError,
    validate_power_identity_observation,
    verify_power_identity_transition,
)
from metacompanion_solver.schemas import (
    TRANSITION_CANDIDATE_ENVELOPE_FIELDS,
    Action,
    ActionKind,
    Card,
    CardType,
)
from metacompanion_solver.simulator import apply_action
from metacompanion_solver.training import _value_examples
from metacompanion_solver.trajectory import audit_trajectory_file
from metacompanion_solver.verification import (
    _power_verified_records,
    promote_trajectory_file,
)

from helpers import state


ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "fixtures" / "trajectory-readiness-v1.jsonl"
POLICY = ROOT / "fixtures" / "trajectory-readiness-policy-v1.json"


def _canonical_hash(value: object) -> str:
    return hashlib.sha256(
        json.dumps(
            value,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    ).hexdigest()


def _records() -> list[dict[str, object]]:
    return [json.loads(line) for line in FIXTURE.read_text(encoding="utf-8").splitlines()]


def _power_source() -> list[dict[str, object]]:
    records = _records()
    states = {
        record["trajectory"]["state_id"]: record["request"]["state"]
        for record in records
        if record["kind"] == "solve"
        and record["trajectory"]["solve_stage"] in {"final", "single"}
    }
    for record in records:
        if record["kind"] != "observation" or record["observation"]["kind"] != "action":
            continue
        observation = record["observation"]
        metadata = observation["metadata"]
        pre_id = metadata["pre_state_id"]
        post_id = metadata["post_state_id"]
        pre = copy.deepcopy(states[pre_id])
        post = copy.deepcopy(states[post_id])
        observation["pre_state"] = pre
        observation["post_state"] = post
        observation["action"].update(
            {
                "sub_option": -1,
                "board_position": 0,
                "option_id": "0",
                "frame_id": "7",
                "power_start_watermark": "g1:100",
                "power_end_watermark": "g1:110",
                "choices": [],
            }
        )
        metadata.update(
            {
                "capture_contract": "hdt_power_action_identity_v1",
                "completeness": "exact_action_identity_unverified_transition_v1",
                "action_identity_status": "exact_hdt_power_v1",
                "choice_status": "none",
                "simulator_status": "not_replayed",
                "transition_status": "post_state_candidate_unverified",
                "transition_verification": "producer_candidate_unverified",
                "training_eligible": False,
                "raw_pre_snapshot_hash": "a" * 64,
                "raw_post_snapshot_hash": "b" * 64,
                "pre_state_hash": _canonical_hash(pre),
                "post_state_hash": _canonical_hash(post),
                "pre_snapshot_sequence": "10",
                "post_snapshot_sequence": "11",
                "boundary_status": "isolated",
                "intervening_action_count": "0",
                "capture_warning_count": "0",
                "game_generation": "1",
                "power_collector_epoch": "1",
                "power_action_ordinal": metadata["action_sequence"],
                "power_gap_count": "0",
            }
        )
        trajectory = record["trajectory"]
        trajectory.update(
            {
                "capture_contract": metadata["capture_contract"],
                "completeness": metadata["completeness"],
                "transition_status": metadata["transition_status"],
            }
        )
        for field in TRANSITION_CANDIDATE_ENVELOPE_FIELDS:
            trajectory[field] = metadata[field]
    action_counts: dict[str, int] = {}
    for record in records:
        if record["kind"] == "observation" and record["observation"]["kind"] == "action":
            game_id = record["trajectory"]["game_id"]
            action_counts[game_id] = action_counts.get(game_id, 0) + 1
    for record in records:
        if record["kind"] != "observation" or record["observation"]["kind"] != "result":
            continue
        count = action_counts[record["trajectory"]["game_id"]]
        record["observation"]["metadata"].update(
            {
                "game_generation": "1",
                "power_collector_epoch": "1",
                "power_committed_action_count": str(count),
                "power_recorded_action_count": str(count),
                "power_gap_count": "0",
                "power_trace_status": "complete",
            }
        )
    return records


def _write(path: Path, records: list[dict[str, object]]) -> None:
    path.write_text(
        "".join(
            json.dumps(item, ensure_ascii=False, separators=(",", ":")) + "\n"
            for item in records
        ),
        encoding="utf-8",
    )


def _insert_second_first_game_action(
    records: list[dict[str, object]],
    *,
    sequence: int = 2,
    start: str = "g1:111",
    end: str = "g1:120",
    frame: str = "8",
) -> None:
    first = records[2]
    game_id = first["trajectory"]["game_id"]
    second = copy.deepcopy(first)
    second["observation"]["metadata"]["action_sequence"] = str(sequence)
    second["observation"]["metadata"]["power_action_ordinal"] = str(sequence)
    second["trajectory"]["action_sequence"] = str(sequence)
    second["trajectory"]["power_action_ordinal"] = str(sequence)
    second["observation"]["action"].update(
        {
            "frame_id": frame,
            "power_start_watermark": start,
            "power_end_watermark": end,
        }
    )
    result_index = next(
        index
        for index, record in enumerate(records)
        if record["kind"] == "observation"
        and record["observation"]["kind"] == "result"
        and record["trajectory"]["game_id"] == game_id
    )
    result_metadata = records[result_index]["observation"]["metadata"]
    result_metadata["power_committed_action_count"] = "2"
    result_metadata["power_recorded_action_count"] = "2"
    records.insert(result_index, second)


def _action_observation(kind: ActionKind) -> dict[str, object]:
    pre = state()
    pre.state_id = "action-pre"
    pre.metadata = {
        "game_id": "action-game",
        "snapshot_state_hash": "a" * 64,
        "snapshot_sequence": 1,
    }
    pre.friendly.hero.entity_id = "1"
    pre.friendly.hero.card_id = "FRIENDLY_HERO"
    pre.opponent.hero.entity_id = "2"
    pre.opponent.hero.card_id = "OPPONENT_HERO"
    board_position = 0
    option_id = 0
    if kind == ActionKind.PLAY_CARD:
        pre.friendly.hand = [
            Card(
                entity_id="10",
                card_id="TEST_MINION",
                name="Test minion",
                card_type=CardType.MINION,
                cost=1,
                attack=1,
                health=1,
                current_health=1,
            )
        ]
        action = Action(
            kind,
            source_entity_id="10",
            card_id="TEST_MINION",
            board_position=1,
        )
        board_position = 1
        option_id = 1
    elif kind == ActionKind.ATTACK:
        pre.friendly.board = [
            Card(
                entity_id="10",
                card_id="TEST_MINION",
                name="Test minion",
                card_type=CardType.MINION,
                attack=1,
                health=1,
                current_health=1,
                can_attack=True,
                attacks_remaining=1,
            )
        ]
        action = Action(
            kind,
            source_entity_id="10",
            target_entity_id="2",
            card_id="TEST_MINION",
        )
        option_id = 1
    elif kind == ActionKind.HERO_POWER:
        pre.friendly.hero_power = Card(
            entity_id="3",
            card_id="TEST_HERO_POWER",
            name="Test hero power",
            card_type=CardType.HERO_POWER,
            cost=2,
        )
        pre.friendly.hero_power_available = True
        action = Action(kind, source_entity_id="3", card_id="TEST_HERO_POWER")
        option_id = 1
    elif kind == ActionKind.LOCATION_ACTIVATE:
        pre.friendly.board = [
            Card(
                entity_id="10",
                card_id="TEST_LOCATION",
                name="Test location",
                card_type=CardType.LOCATION,
                health=2,
                current_health=2,
                effect_coverage="exact",
            )
        ]
        action = Action(kind, source_entity_id="10", card_id="TEST_LOCATION")
        option_id = 1
    else:
        action = Action(ActionKind.END_TURN)

    post = apply_action(
        pre,
        action,
        validate=kind != ActionKind.LOCATION_ACTIVATE,
    ).state
    post.state_id = "action-post"
    post.metadata = {
        "game_id": "action-game",
        "snapshot_state_hash": "b" * 64,
        "snapshot_sequence": 2,
    }
    pre_value = pre.to_dict()
    post_value = post.to_dict()
    action_value = action.to_dict()
    action_value.update(
        {
            "sub_option": -1,
            "board_position": board_position,
            "option_id": str(option_id),
            "frame_id": "1",
            "power_start_watermark": "g1:100",
            "power_end_watermark": "g1:110",
            "choices": [],
        }
    )
    no_entities = kind == ActionKind.END_TURN
    return {
        "api_version": "1.0",
        "kind": "action",
        "state_id": "action-pre",
        "game_id": "action-game",
        "action": action_value,
        "pre_state": pre_value,
        "post_state": post_value,
        "metadata": {
            "trajectory_schema": "trajectory-readiness-v1",
            "decision_id": "action-pre",
            "action_sequence": "1",
            "game_generation": "1",
            "power_collector_epoch": "1",
            "power_action_ordinal": "1",
            "power_gap_count": "0",
            "pre_state_id": "action-pre",
            "post_state_id": "action-post",
            "raw_pre_snapshot_hash": "a" * 64,
            "raw_post_snapshot_hash": "b" * 64,
            "pre_state_hash": _canonical_hash(pre_value),
            "post_state_hash": _canonical_hash(post_value),
            "pre_snapshot_sequence": "1",
            "post_snapshot_sequence": "2",
            "boundary_status": "isolated",
            "intervening_action_count": "0",
            "capture_warning_count": "0",
            "capture_contract": "hdt_power_action_identity_v1",
            "completeness": "exact_action_identity_unverified_transition_v1",
            "action_identity_status": "exact_hdt_power_v1",
            "choice_status": "none",
            "simulator_status": "not_replayed",
            "transition_status": "post_state_candidate_unverified",
            "transition_verification": "producer_candidate_unverified",
            "source_entity_resolution": (
                "not_applicable" if no_entities else "exact_entity_id"
            ),
            "target_entity_resolution": (
                "exact_entity_id" if action.target_entity_id else "not_applicable"
            ),
            "training_eligible": False,
        },
    }


def _rehash_observation(observation: dict[str, object]) -> None:
    metadata = observation["metadata"]
    metadata["pre_state_hash"] = _canonical_hash(observation["pre_state"])
    metadata["post_state_hash"] = _canonical_hash(observation["post_state"])


def _set_action_board_position(observation: dict[str, object], position: int) -> None:
    action = observation["action"]
    action["board_position"] = position
    base = (
        f"{action['kind']}:{action.get('source_entity_id') or ''}:"
        f"{action.get('target_entity_id') or ''}"
    )
    action["action_id"] = f"{base}:position={position}" if position > 0 else base


class PowerEvidencePromotionTests(unittest.TestCase):
    def test_replay_verifies_each_supported_action_kind(self) -> None:
        for kind in ActionKind:
            with self.subTest(kind=kind.value):
                verified = verify_power_identity_transition(_action_observation(kind))
                self.assertEqual(kind, verified.action.kind)

    def test_power_identity_rejects_malformed_boundaries_and_action_semantics(self) -> None:
        cases = (
            (
                "cross_generation",
                ActionKind.END_TURN,
                lambda value: value["action"].__setitem__(
                    "power_end_watermark", "g2:110"
                ),
                "power_watermark_order_invalid",
            ),
            (
                "reversed_cursor",
                ActionKind.END_TURN,
                lambda value: value["action"].__setitem__(
                    "power_end_watermark", "g1:99"
                ),
                "power_watermark_order_invalid",
            ),
            (
                "zero_frame",
                ActionKind.END_TURN,
                lambda value: value["action"].__setitem__("frame_id", "0"),
                "power_frame_id_invalid",
            ),
            (
                "non_end_zero_option",
                ActionKind.HERO_POWER,
                lambda value: value["action"].__setitem__("option_id", "0"),
                "power_non_end_option_id_invalid",
            ),
            (
                "attack_position",
                ActionKind.ATTACK,
                lambda value: _set_action_board_position(value, 1),
                "power_non_play_board_position_invalid",
            ),
            (
                "hero_power_position",
                ActionKind.HERO_POWER,
                lambda value: _set_action_board_position(value, 1),
                "power_non_play_board_position_invalid",
            ),
            (
                "middle_minion_position",
                ActionKind.PLAY_CARD,
                lambda value: _set_action_board_position(value, 0),
                "power_board_position_not_replayable",
            ),
            (
                "collector_epoch",
                ActionKind.END_TURN,
                lambda value: value["metadata"].__setitem__(
                    "power_collector_epoch", "2"
                ),
                "power_collector_epoch_generation_mismatch",
            ),
            (
                "action_ordinal",
                ActionKind.END_TURN,
                lambda value: value["metadata"].__setitem__(
                    "power_action_ordinal", "2"
                ),
                "power_action_ordinal_sequence_mismatch",
            ),
        )
        for name, kind, mutate, expected in cases:
            with self.subTest(name=name):
                observation = _action_observation(kind)
                mutate(observation)
                with self.assertRaises(PowerEvidenceError) as raised:
                    validate_power_identity_observation(
                        observation,
                        require_isolated=True,
                    )
                self.assertEqual(expected, raised.exception.code)

    def test_nonzero_collector_gap_blocks_offline_replay(self) -> None:
        observation = _action_observation(ActionKind.END_TURN)
        observation["metadata"]["power_gap_count"] = "1"
        with self.assertRaises(PowerEvidenceError) as raised:
            verify_power_identity_transition(observation)
        self.assertEqual("power_collector_trace_tainted", raised.exception.code)

    def test_non_minion_play_cannot_claim_a_board_position(self) -> None:
        observation = _action_observation(ActionKind.PLAY_CARD)
        observation["pre_state"]["friendly"]["hand"][0]["card_type"] = "SPELL"
        _rehash_observation(observation)
        with self.assertRaises(PowerEvidenceError) as raised:
            validate_power_identity_observation(observation, require_isolated=True)
        self.assertEqual(
            "power_non_minion_board_position_invalid",
            raised.exception.code,
        )

    def test_location_activation_requires_a_local_board_location(self) -> None:
        observation = _action_observation(ActionKind.LOCATION_ACTIVATE)
        observation["pre_state"]["friendly"]["board"][0]["card_type"] = "MINION"
        _rehash_observation(observation)
        with self.assertRaises(PowerEvidenceError) as raised:
            validate_power_identity_observation(observation, require_isolated=True)
        self.assertEqual(
            "power_source_not_local_pre_state_entity",
            raised.exception.code,
        )

    def test_whole_game_gate_rejects_overlapping_watermarks_and_reused_frames(self) -> None:
        cases = (
            (
                "overlap",
                {"start": "g1:110", "end": "g1:120", "frame": "8"},
                "power_watermark_not_strictly_non_overlapping",
            ),
            (
                "frame_reuse",
                {"start": "g1:111", "end": "g1:120", "frame": "7"},
                "power_frame_id_not_strictly_increasing",
            ),
            (
                "sequence_gap",
                {
                    "sequence": 3,
                    "start": "g1:111",
                    "end": "g1:120",
                    "frame": "8",
                },
                "power_action_sequence_not_contiguous",
            ),
        )
        for name, options, expected in cases:
            with self.subTest(name=name):
                records = _power_source()
                _insert_second_first_game_action(records, **options)
                _, summary = _power_verified_records(records)
                self.assertEqual(1, summary["accepted_game_count"])
                self.assertEqual(1, summary["rejection_reasons"][expected])

    def test_terminal_collector_proof_must_match_the_promoted_actions(self) -> None:
        cases = (
            (
                "status",
                "power_trace_status",
                "tainted",
                "terminal_power_trace_not_complete",
            ),
            (
                "generation",
                "game_generation",
                "2",
                "terminal_power_collector_epoch_mismatch",
            ),
            (
                "epoch",
                "power_collector_epoch",
                "2",
                "terminal_power_collector_epoch_mismatch",
            ),
            (
                "committed_count",
                "power_committed_action_count",
                "2",
                "terminal_power_action_count_mismatch",
            ),
            (
                "recorded_count",
                "power_recorded_action_count",
                "0",
                "terminal_power_action_count_mismatch",
            ),
            (
                "gap",
                "power_gap_count",
                "1",
                "terminal_power_gap_count_nonzero",
            ),
        )
        for name, field, value, expected in cases:
            with self.subTest(name=name):
                records = _power_source()
                terminal = records[4]["observation"]["metadata"]
                terminal[field] = value
                _, summary = _power_verified_records(records)
                self.assertEqual(1, summary["accepted_game_count"])
                self.assertEqual(1, summary["rejection_reasons"][expected])

    def test_identity_only_source_is_not_ready_until_offline_replay_promotes_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "production-snapshot.jsonl"
            verified = root / "verified.jsonl"
            manifest_path = root / "manifest.json"
            _write(source, _power_source())

            source_audit = audit_trajectory_file(source, policy_path=POLICY)
            self.assertTrue(source_audit["contract_passed"])
            self.assertFalse(source_audit["training_ready"])
            self.assertEqual(0, source_audit["metrics"]["exact_action_count"])

            manifest = promote_trajectory_file(
                source,
                verified,
                manifest_path,
                policy_path=POLICY,
            )
            self.assertTrue(manifest["training_ready"])
            self.assertEqual(
                2,
                manifest["power_identity_promotion"]["verified_transition_count"],
            )
            self.assertEqual(2, manifest["power_identity_promotion"]["accepted_game_count"])
            verified_audit = audit_trajectory_file(verified, policy_path=POLICY)
            self.assertTrue(verified_audit["training_ready"])
            self.assertEqual(2, verified_audit["metrics"]["exact_action_count"])
            self.assertEqual(2, verified_audit["metrics"]["replayable_transition_count"])

    def test_offline_promotion_does_not_relabel_unsuccessful_online_solves(self) -> None:
        records = _power_source()
        statuses = ("cancelled", "unsupported")
        index = 0
        for record in records:
            if record["kind"] != "solve":
                continue
            record["result"]["status"] = statuses[index % len(statuses)]
            index += 1
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "production-snapshot.jsonl"
            verified = root / "verified.jsonl"
            manifest_path = root / "manifest.json"
            _write(source, records)
            manifest = promote_trajectory_file(
                source,
                verified,
                manifest_path,
                policy_path=POLICY,
            )
            verified_audit = audit_trajectory_file(verified, policy_path=POLICY)
        self.assertTrue(manifest["training_ready"])
        self.assertTrue(verified_audit["training_ready"])
        self.assertFalse(verified_audit["solver_runtime_ready"])
        self.assertEqual(
            {"cancelled": 3, "unsupported": 3},
            manifest["power_identity_promotion"]["source_solve_status_counts"],
        )

    def test_power_replay_provenance_does_not_require_a_successful_solve_record(self) -> None:
        records = [record for record in _power_source() if record["kind"] != "solve"]
        verified, summary = _power_verified_records(records)
        self.assertEqual(2, summary["accepted_game_count"])
        self.assertEqual(2, summary["verified_transition_count"])
        self.assertEqual({}, summary["source_solve_status_counts"])
        self.assertTrue(verified)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "verified.jsonl"
            policy = root / "policy.json"
            _write(output, verified)
            policy_value = json.loads(POLICY.read_text(encoding="utf-8"))
            policy_value["thresholds"]["min_canonical_decisions"] = 2
            policy.write_text(json.dumps(policy_value), encoding="utf-8")
            audit = audit_trajectory_file(output, policy_path=policy)
        self.assertTrue(audit["contract_passed"])
        self.assertTrue(audit["training_ready"])
        self.assertFalse(audit["solver_runtime_ready"])
        self.assertEqual(0, audit["metrics"]["solve_record_count"])
        self.assertEqual(4, audit["metrics"]["canonical_decision_count"])
        self.assertEqual(4, audit["metrics"]["decision_snapshot_record_count"])
        examples = _value_examples(verified)
        self.assertEqual(4, len(examples))
        self.assertEqual({0.0, 1.0}, {item["label"] for item in examples})

    def test_equal_power_watermarks_fail_the_source_contract(self) -> None:
        records = _power_source()
        action = records[2]["observation"]["action"]
        action["power_end_watermark"] = action["power_start_watermark"]
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "invalid.jsonl"
            _write(source, records)
            audit = audit_trajectory_file(source, policy_path=POLICY)
        self.assertFalse(audit["contract_passed"])
        self.assertIn(
            "power_watermark_order_invalid",
            audit["issues"]["contract"][0]["reason"],
        )


if __name__ == "__main__":
    unittest.main()
