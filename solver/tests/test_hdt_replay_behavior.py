from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.behavior import BehaviorRecord
from metacompanion_solver.behavior_learning import audit_behavior_learning_files
from metacompanion_solver.cli import _replay_error_message
from metacompanion_solver.decision_frame import (
    DecisionFrameRecord,
    DecisionFrameValidationError,
    audit_decision_frame_file,
)
from metacompanion_solver.hdt_replay_behavior import (
    BEHAVIOR_OUTPUT_FILENAME,
    DECISION_FRAME_OUTPUT_FILENAME,
    MANIFEST_OUTPUT_FILENAME,
    RESULT_OUTPUT_FILENAME,
    ReplayImportError,
    audit_hdt_replays,
    import_hdt_replays,
    parse_hdt_replay,
    scan_hdt_replay,
)
from metacompanion_solver.hdt_card_defs import (
    enrich_public_solver_state,
    load_hdt_card_defs,
    public_card_ids,
)
from metacompanion_solver.offline import load_records


def _power(value: str) -> str:
    return f"D 12:00:00.0000000 GameState.DebugPrintPower() - {value}"


def _game(value: str) -> str:
    return f"D 12:00:00.0000000 GameState.DebugPrintGame() - {value}"


def _options(value: str) -> str:
    return f"D 12:00:00.0000000 GameState.DebugPrintOptions() - {value}"


def _send_option(
    *,
    option: int = 1,
    sub_option: int = -1,
    target: int = 0,
    position: int = 1,
) -> str:
    return (
        "D 12:00:00.0000000 GameState.SendOption() - "
        f"selectedOption={option} selectedSubOption={sub_option} "
        f"selectedTarget={target} selectedPosition={position}"
    )


def _replay_log(
    *,
    local_name: str = "Private Local#1000",
    opponent_name: str = "Private Opponent#2000",
    local_account: str = "private-local-account",
    opponent_account: str = "private-opponent-account",
    build: str = "246003",
    format_type: str = "FT_STANDARD",
) -> str:
    lines = [
        _game(f"BuildNumber={build}"),
        _game("GameType=GT_RANKED"),
        _game(f"FormatType={format_type}"),
        _game("ScenarioID=2"),
        _game("PlayerID=1, PlayerName=UNKNOWN HUMAN PLAYER"),
        _game(f"PlayerID=2, PlayerName={local_name}"),
        _power("CREATE_GAME"),
        _power("    GameEntity EntityID=1"),
        _power("        tag=CARDTYPE value=GAME"),
        _power("        tag=TURN value=1"),
        _power(
            "    Player EntityID=2 PlayerID=1 "
            f"GameAccountId=[{opponent_account}]"
        ),
        _power("        tag=CARDTYPE value=PLAYER"),
        _power("        tag=PLAYER_ID value=1"),
        _power("        tag=CONTROLLER value=1"),
        _power("        tag=RESOURCES value=1"),
        _power("        tag=RESOURCES_USED value=0"),
        _power("        tag=CURRENT_PLAYER value=0"),
        _power(
            "    Player EntityID=3 PlayerID=2 "
            f"GameAccountId=[{local_account}]"
        ),
        _power("        tag=CARDTYPE value=PLAYER"),
        _power("        tag=PLAYER_ID value=2"),
        _power("        tag=CONTROLLER value=2"),
        _power("        tag=RESOURCES value=1"),
        _power("        tag=RESOURCES_USED value=0"),
        _power("        tag=CURRENT_PLAYER value=1"),
        _power("    FULL_ENTITY - Creating ID=10 CardID="),
        _power("        tag=CONTROLLER value=1"),
        _power("        tag=ZONE value=HAND"),
        _power("        tag=ZONE_POSITION value=1"),
        _power("    FULL_ENTITY - Creating ID=20 CardID=TEST_LOCAL_CARD"),
        _power("        tag=CONTROLLER value=2"),
        _power("        tag=CARDTYPE value=MINION"),
        _power("        tag=ZONE value=HAND"),
        _power("        tag=ZONE_POSITION value=1"),
        _power("        tag=ATK value=2"),
        _power("        tag=HEALTH value=2"),
        _power("    FULL_ENTITY - Creating ID=30 CardID=TEST_OPPONENT_HERO"),
        _power("        tag=CONTROLLER value=1"),
        _power("        tag=CARDTYPE value=HERO"),
        _power("        tag=ZONE value=PLAY"),
        _power("        tag=HEALTH value=30"),
        _power("    FULL_ENTITY - Creating ID=31 CardID=TEST_LOCAL_HERO"),
        _power("        tag=CONTROLLER value=2"),
        _power("        tag=CARDTYPE value=HERO"),
        _power("        tag=ZONE value=PLAY"),
        _power("        tag=HEALTH value=30"),
        _power("    FULL_ENTITY - Creating ID=32 CardID=TEST_OPPONENT_POWER"),
        _power("        tag=CONTROLLER value=1"),
        _power("        tag=CARDTYPE value=HERO_POWER"),
        _power("        tag=ZONE value=PLAY"),
        _power("    FULL_ENTITY - Creating ID=33 CardID=TEST_LOCAL_POWER"),
        _power("        tag=CONTROLLER value=2"),
        _power("        tag=CARDTYPE value=HERO_POWER"),
        _power("        tag=ZONE value=PLAY"),
        _options("id=1"),
        _options("  option 0 type=END_TURN mainEntity= error=INVALID errorParam="),
        _options(
            "  option 1 type=POWER "
            "mainEntity=[entityName=Local Card id=20 zone=HAND zonePos=1 "
            "cardId=TEST_LOCAL_CARD player=2] error=NONE errorParam="
        ),
        _send_option(),
        _power(
            "BLOCK_START BlockType=PLAY "
            "Entity=[entityName=Local Card id=20 zone=HAND zonePos=1 "
            "cardId=TEST_LOCAL_CARD player=2] "
            "EffectCardId=System.Collections.Generic.List`1[System.String] "
            "EffectIndex=0 Target=0 SubOption=-1"
        ),
        _power(
            "    TAG_CHANGE Entity=[entityName=Local Card id=20 zone=HAND "
            "zonePos=1 cardId=TEST_LOCAL_CARD player=2] tag=ZONE value=PLAY"
        ),
        # HDT repeats a stale pre-change descriptor for later tag mutations in
        # the same power packet. The parser must not let this EXHAUSTED line
        # revert the already observed HAND -> PLAY transition.
        _power(
            "    TAG_CHANGE Entity=[entityName=Local Card id=20 zone=HAND "
            "zonePos=1 cardId=TEST_LOCAL_CARD player=2] tag=EXHAUSTED value=1"
        ),
        _power("BLOCK_END"),
        _options("id=2"),
        _options("  option 0 type=END_TURN mainEntity= error=NONE errorParam="),
        _power("TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END"),
        _power(f"    TAG_CHANGE Entity={local_name} tag=CURRENT_PLAYER value=0"),
        _power(f"    TAG_CHANGE Entity={opponent_name} tag=CURRENT_PLAYER value=1"),
        _power("TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_ACTION"),
        _power(
            "BLOCK_START BlockType=PLAY "
            "Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=10 "
            "zone=HAND zonePos=1 cardId= player=1] "
            "EffectCardId=System.Collections.Generic.List`1[System.String] "
            "EffectIndex=0 Target=0 SubOption=-1"
        ),
        _power(
            "    SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY "
            "[cardType=INVALID] id=10 zone=HAND zonePos=1 cardId= player=1] "
            "CardID=TEST_OPPONENT_CARD"
        ),
        _power("        tag=CONTROLLER value=1"),
        _power("        tag=CARDTYPE value=MINION"),
        _power("        tag=ZONE value=PLAY"),
        _power("        tag=ZONE_POSITION value=1"),
        _power("        tag=ATK value=3"),
        _power("        tag=HEALTH value=3"),
        _power("BLOCK_END"),
        _power(
            "BLOCK_START BlockType=ATTACK "
            "Entity=[entityName=Opponent Card id=10 zone=PLAY zonePos=1 "
            "cardId=TEST_OPPONENT_CARD player=1] "
            "EffectCardId=System.Collections.Generic.List`1[System.String] "
            "EffectIndex=0 Target=[entityName=Local Hero id=31 zone=PLAY "
            "zonePos=0 cardId=TEST_LOCAL_HERO player=2] SubOption=-1"
        ),
        _power("BLOCK_END"),
        _power("TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END"),
        _power(f"    TAG_CHANGE Entity={opponent_name} tag=CURRENT_PLAYER value=0"),
        _power(f"    TAG_CHANGE Entity={local_name} tag=CURRENT_PLAYER value=1"),
        _power("TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_ACTION"),
        _power(f"TAG_CHANGE Entity={local_name} tag=PLAYSTATE value=LOST"),
        _power(f"TAG_CHANGE Entity={opponent_name} tag=PLAYSTATE value=WON"),
        _power("TAG_CHANGE Entity=GameEntity tag=STEP value=FINAL_GAMEOVER"),
    ]
    return "\n".join(lines) + "\n"


def _write_replay(path: Path, text: str, *, extra_entry: bool = False) -> None:
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("output_log.txt", text.encode("utf-8"))
        if extra_entry:
            archive.writestr("private.txt", b"must not be accepted")


def _write_card_defs(path: Path, *, build: str = "246003") -> None:
    path.write_text(
        "\n".join(
            [
                '<?xml version="1.0" encoding="utf-8"?>',
                f'<CardDefs build="{build}">',
                '  <Entity CardID="TEST_LOCAL_CARD" ID="1">',
                '    <Tag name="CARDNAME" type="LocString"><enUS>Public Local Card</enUS></Tag>',
                '    <Tag name="CARDTEXT" type="LocString"><enUS>&lt;b&gt;Battlecry:&lt;/b&gt; Deal 1 damage.</enUS></Tag>',
                '    <Tag name="CARDTYPE" type="Int" value="4"/>',
                '    <Tag name="COST" type="Int" value="7"/>',
                '    <Tag name="LIFESTEAL" type="Int" value="1"/>',
                "  </Entity>",
                '  <Entity CardID="NEVER_PUBLIC_SECRET" ID="2">',
                '    <Tag name="CARDTEXT" type="LocString"><enUS>Must not be selected.</enUS></Tag>',
                '    <Tag name="CARDTYPE" type="Int" value="5"/>',
                "  </Entity>",
                "</CardDefs>",
                "",
            ]
        ),
        encoding="utf-8",
    )


class HdtReplayBehaviorTest(unittest.TestCase):
    def test_replay_cli_error_is_chinese_and_keeps_a_stable_code(self) -> None:
        message = _replay_error_message(
            ReplayImportError("output_exists", "behavior-v1.jsonl")
        )
        self.assertIn("输出文件已存在", message)
        self.assertIn("output_exists", message)
        self.assertNotIn("behavior-v1.jsonl", message)

    def test_parses_both_sides_end_turn_and_terminal_result(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "private-name.hdtreplay"
            _write_replay(source, _replay_log())

            game = parse_hdt_replay(source)

        self.assertEqual("246003", game.build)
        self.assertEqual("standard", game.mode)
        self.assertEqual("loss", game.result)
        self.assertEqual(5, len(game.records))
        self.assertEqual(
            ["local", "local", "opponent", "opponent", "opponent"],
            [record.value["actor_side"] for record in game.records],
        )
        self.assertEqual(
            ["play_card", "end_turn", "play_card", "attack", "end_turn"],
            [record.value["action"]["kind"] for record in game.records],
        )
        self.assertTrue(all(record.value["behavior_eligible"] for record in game.records))
        self.assertTrue(
            all(record.value["rl_training_eligible"] is False for record in game.records)
        )
        opponent_play = game.records[2].value
        self.assertEqual("revealed_after_action", opponent_play["identity_status"])
        hidden = opponent_play["pre_state"]["opponent"]["hand"][0]
        self.assertEqual({"entity_id": "10", "visibility": "hidden"}, hidden)
        self.assertEqual("TEST_OPPONENT_CARD", opponent_play["action"]["card_id"])
        opponent_attack = game.records[3].value
        attack_source = next(
            item
            for item in opponent_attack["pre_state"]["opponent"]["board"]
            if item["entity_id"] == "10"
        )
        self.assertIs(attack_source["can_attack"], True)
        self.assertGreaterEqual(attack_source["attacks_remaining"], 1)
        self.assertEqual(
            opponent_play["post_state"]["state_id"],
            opponent_attack["pre_state"]["state_id"],
        )
        local_play = game.records[0].value
        self.assertEqual(1, local_play["action"]["board_position"])
        self.assertEqual("none", local_play["action"]["choice_status"])
        self.assertNotIn(
            "20",
            {
                item["entity_id"]
                for item in local_play["post_state"]["friendly"]["hand"]
            },
        )
        self.assertIn(
            "20",
            {
                item["entity_id"]
                for item in local_play["post_state"]["friendly"]["board"]
            },
        )
        self.assertEqual(1, len(game.decision_frames))
        decision = game.decision_frames[0].value
        self.assertEqual("advisor-decision-frame-v1", decision["schema"])
        self.assertEqual(local_play["behavior_id"], decision["selected_behavior_id"])
        self.assertEqual(local_play["action"]["kind"], decision["selected_action"]["kind"])
        self.assertEqual(2, len(decision["legal_candidates"]))
        self.assertEqual(
            {"end_turn", "play_card"},
            {item["action"]["kind"] for item in decision["legal_candidates"]},
        )
        self.assertTrue(decision["candidate_set_complete"])
        self.assertTrue(decision["imitation_training_eligible"])
        self.assertFalse(decision["optimality_verified"])
        self.assertFalse(decision["rl_training_eligible"])

    def test_decision_frame_rejects_choice_branch_target_ambiguity(self) -> None:
        choice = _options(
            "  subOption 0 entity=[entityName=Choice id=40 zone=SETASIDE "
            "zonePos=0 cardId=TEST_CHOICE player=2] error=NONE errorParam="
        )
        text = _replay_log().replace(
            _send_option(),
            choice + "\n" + _send_option(sub_option=0),
            1,
        ).replace("Target=0 SubOption=-1", "Target=0 SubOption=0", 1)

        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "choice-branch.hdtreplay"
            _write_replay(source, text)
            game = parse_hdt_replay(source)

        self.assertEqual([], game.decision_frames)
        self.assertEqual(
            1,
            game.decision_rejections["selected_choice_branch_unsupported"],
        )

    def test_decision_frame_hash_and_non_optimality_flags_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "decision-contract.hdtreplay"
            _write_replay(source, _replay_log())
            game = parse_hdt_replay(source)

        raw = game.decision_frames[0].to_dict()
        raw["optimality_verified"] = True
        with self.assertRaises(DecisionFrameValidationError) as caught:
            DecisionFrameRecord.from_dict(raw)
        self.assertIn(
            caught.exception.code,
            {"must_be_false", "content_sha256_mismatch"},
        )

    def test_does_not_guess_board_position_from_mismatched_send_option(self) -> None:
        cases = {
            "source": _replay_log().replace(
                "Local Card id=20 zone=HAND zonePos=1",
                "Local Card id=21 zone=HAND zonePos=1",
                1,
            ),
            "target": _replay_log().replace(
                _send_option(),
                _send_option(target=30),
                1,
            ),
            "out_of_range": _replay_log().replace(
                _send_option(),
                _send_option(position=2),
                1,
            ),
            "stale_frame": _replay_log().replace(
                _send_option(),
                _send_option() + "\n" + _options("id=99"),
                1,
            ),
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, text in cases.items():
                with self.subTest(name=name):
                    source = root / f"{name}.hdtreplay"
                    _write_replay(source, text)
                    game = parse_hdt_replay(source)
                    self.assertNotIn(
                        "board_position",
                        game.records[0].value["action"],
                    )

    def test_full_entity_update_without_entity_prefix_binds_implicit_tags(self) -> None:
        update = "\n".join(
            [
                _power(
                    "FULL_ENTITY - Updating [entityName=Generated Card id=21 "
                    "zone=HAND zonePos=2 cardId= player=2] "
                    "CardID=TEST_GENERATED_CARD"
                ),
                _power("        tag=CONTROLLER value=2"),
                _power("        tag=CARDTYPE value=MINION"),
                _power("        tag=COST value=1"),
                _power("        tag=ATK value=1"),
                _power("        tag=HEALTH value=1"),
                _power("        tag=ZONE value=HAND"),
                _power("        tag=ZONE_POSITION value=2"),
            ]
        )
        text = _replay_log().replace(
            _options("id=1"),
            update + "\n" + _options("id=1"),
            1,
        )

        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "full-entity-update.hdtreplay"
            _write_replay(source, text)
            game = parse_hdt_replay(source)

        local_play = game.records[0].value
        generated = next(
            item
            for item in local_play["pre_state"]["friendly"]["hand"]
            if item["entity_id"] == "21"
        )
        self.assertEqual("TEST_GENERATED_CARD", generated["card_id"])
        self.assertEqual("MINION", generated["card_type"])
        self.assertEqual(1, generated["cost"])

    def test_opponent_action_never_inherits_local_send_option_position(self) -> None:
        opponent_root = _power(
            "BLOCK_START BlockType=PLAY "
            "Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=10 "
            "zone=HAND zonePos=1 cardId= player=1] "
            "EffectCardId=System.Collections.Generic.List`1[System.String] "
            "EffectIndex=0 Target=0 SubOption=-1"
        )
        stale_local_selection = "\n".join(
            [
                _options("id=98"),
                _options(
                    "  option 1 type=POWER "
                    "mainEntity=[entityName=Hidden id=10 zone=HAND zonePos=1 "
                    "cardId= player=2] error=NONE errorParam="
                ),
                _send_option(),
            ]
        )
        text = _replay_log().replace(
            opponent_root,
            stale_local_selection + "\n" + opponent_root,
            1,
        )

        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "opponent-no-position.hdtreplay"
            _write_replay(source, text)
            game = parse_hdt_replay(source)

        opponent_play = next(
            record.value
            for record in game.records
            if record.value["actor_side"] == "opponent"
            and record.value["action"]["kind"] == "play_card"
        )
        self.assertNotIn("board_position", opponent_play["action"])

    def test_stale_block_descriptor_does_not_turn_location_use_into_play(self) -> None:
        first_option = _options(
            "  option 0 type=END_TURN mainEntity= error=NONE errorParam="
        )
        location_use = "\n".join(
            [
                _power(
                    "BLOCK_START BlockType=PLAY "
                    "Entity=[entityName=Local Location id=20 zone=HAND zonePos=1 "
                    "cardId=TEST_LOCAL_CARD player=2] "
                    "EffectCardId=System.Collections.Generic.List`1[System.String] "
                    "EffectIndex=0 Target=0 SubOption=-1"
                ),
                _power(
                    "    TAG_CHANGE Entity=[entityName=Local Location id=20 "
                    "zone=HAND zonePos=1 cardId=TEST_LOCAL_CARD player=2] "
                    "tag=DAMAGE value=1"
                ),
                _power("BLOCK_END"),
                _options("id=2"),
            ]
        )
        text = _replay_log().replace(
            _power("        tag=CARDTYPE value=MINION"),
            _power("        tag=CARDTYPE value=LOCATION"),
            1,
        )
        text = text.replace(first_option, first_option + "\n" + location_use, 1)

        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "stale-location-block.hdtreplay"
            _write_replay(source, text)
            game = parse_hdt_replay(source)

        local_actions = [
            record.value
            for record in game.records
            if record.value["actor_side"] == "local"
        ]
        self.assertEqual(
            ["play_card", "location_activate", "end_turn"],
            [record["action"]["kind"] for record in local_actions],
        )
        location = local_actions[1]
        self.assertIn(
            "20",
            {
                item["entity_id"]
                for item in location["pre_state"]["friendly"]["board"]
            },
        )
        self.assertNotIn(
            "20",
            {
                item["entity_id"]
                for item in location["pre_state"]["friendly"]["hand"]
            },
        )

    def test_private_identity_changes_do_not_change_public_game_id(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = root / "first.hdtreplay"
            second = root / "second.hdtreplay"
            _write_replay(
                first,
                _replay_log(
                    local_name="First Local#1111",
                    opponent_name="First Opponent#2222",
                    local_account="first-local-account",
                    opponent_account="first-opponent-account",
                ),
            )
            _write_replay(
                second,
                _replay_log(
                    local_name="Second Local#3333",
                    opponent_name="Second Opponent#4444",
                    local_account="second-local-account",
                    opponent_account="second-opponent-account",
                ),
            )

            first_game = parse_hdt_replay(first)
            second_game = parse_hdt_replay(second)

        self.assertEqual(first_game.public_digest_sha256, second_game.public_digest_sha256)
        self.assertEqual(first_game.game_id, second_game.game_id)
        first_payload = json.dumps(
            [record.to_dict() for record in first_game.records],
            ensure_ascii=False,
            sort_keys=True,
        )
        second_payload = json.dumps(
            [record.to_dict() for record in second_game.records],
            ensure_ascii=False,
            sort_keys=True,
        )
        self.assertEqual(first_payload, second_payload)
        for private_value in (
            "First Local",
            "First Opponent",
            "first-local-account",
            "first-opponent-account",
        ):
            self.assertNotIn(private_value, first_payload)

    def test_import_writes_hash_bound_independent_corpora(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            replay_dir = root / "replays"
            output_dir = root / "output"
            replay_dir.mkdir()
            _write_replay(replay_dir / "one.hdtreplay", _replay_log())

            manifest = import_hdt_replays(replay_dir, output_dir)

            behavior_path = output_dir / BEHAVIOR_OUTPUT_FILENAME
            decision_frame_path = output_dir / DECISION_FRAME_OUTPUT_FILENAME
            result_path = output_dir / RESULT_OUTPUT_FILENAME
            manifest_path = output_dir / MANIFEST_OUTPUT_FILENAME
            self.assertTrue(behavior_path.is_file())
            self.assertTrue(decision_frame_path.is_file())
            self.assertTrue(result_path.is_file())
            self.assertTrue(manifest_path.is_file())
            self.assertTrue(manifest["passed"])
            self.assertTrue(manifest["ready_for_imitation_audit"])
            self.assertFalse(manifest["training_ready"])
            self.assertTrue(
                manifest["eligibility"]["output_observed_transition_contract_valid"]
            )
            self.assertFalse(manifest["eligibility"]["solver_evaluation_ready"])
            self.assertTrue(
                all(item["passed"] for item in manifest["transition_quality_checks"])
            )
            self.assertEqual(
                0,
                manifest["metrics"]["play_source_still_actor_hand_post_count"],
            )
            self.assertEqual(5, manifest["outputs"]["behavior"]["records"])
            self.assertEqual(
                1, manifest["outputs"]["decision_frames"]["records"]
            )
            self.assertFalse(
                manifest["outputs"]["decision_frames"]["rl_training_eligible"]
            )
            decision_frames = [
                DecisionFrameRecord.from_dict(json.loads(line))
                for line in decision_frame_path.read_text(
                    encoding="utf-8"
                ).splitlines()
            ]
            self.assertEqual(1, len(decision_frames))
            decision_audit = audit_decision_frame_file(
                decision_frame_path,
                behavior_path=behavior_path,
            )
            self.assertEqual("READY", decision_audit["status"])
            self.assertTrue(decision_audit["candidate_imitation_ready"])
            self.assertFalse(decision_audit["rl_training_ready"])
            records = [
                BehaviorRecord.from_dict(json.loads(line))
                for line in behavior_path.read_text(encoding="utf-8").splitlines()
            ]
            self.assertEqual(5, len(records))
            terminal = load_records(result_path)
            self.assertEqual(1, len(terminal))
            self.assertEqual("result", terminal[0]["observation"]["kind"])
            self.assertEqual("loss", terminal[0]["observation"]["result"])
            metadata = terminal[0]["observation"]["metadata"]
            self.assertEqual("trajectory-readiness-v1", metadata["trajectory_schema"])
            self.assertEqual("terminal_result_v1", metadata["capture_contract"])
            self.assertEqual("terminal_result", metadata["completeness"])
            self.assertIs(metadata["training_eligible"], True)
            learning_audit = audit_behavior_learning_files(behavior_path, result_path)
            self.assertTrue(learning_audit["contract_passed"])
            self.assertFalse(learning_audit["rl_training_ready"])
            self.assertEqual(
                2, learning_audit["metrics"]["replay_play_card_record_count"]
            )
            self.assertEqual(
                0,
                learning_audit["metrics"][
                    "replay_play_source_still_actor_hand_post_count"
                ],
            )
            self.assertEqual(
                1.0,
                learning_audit["metrics"][
                    "replay_attack_source_readiness_explicit_rate"
                ],
            )
            all_output = (
                behavior_path.read_text(encoding="utf-8")
                + decision_frame_path.read_text(encoding="utf-8")
                + result_path.read_text(encoding="utf-8")
            )
            self.assertNotIn("Private Local", all_output)
            self.assertNotIn("Private Opponent", all_output)
            self.assertNotIn("private-local-account", all_output)
            self.assertNotIn("private-opponent-account", all_output)
            with self.assertRaises(ReplayImportError) as context:
                import_hdt_replays(replay_dir, output_dir)
            self.assertEqual("output_exists", context.exception.code)

    def test_same_build_card_defs_is_hash_bound_and_never_fills_hidden_hand(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            replay_dir = root / "replays"
            output_dir = root / "output"
            replay_dir.mkdir()
            _write_replay(replay_dir / "one.hdtreplay", _replay_log())
            card_defs_path = root / "CardDefs.base.xml"
            _write_card_defs(card_defs_path)

            manifest = import_hdt_replays(
                replay_dir,
                output_dir,
                card_defs_path=card_defs_path,
            )
            game = parse_hdt_replay(replay_dir / "one.hdtreplay")
            state = game.decision_frames[0].value["pre_state"]
            snapshot = load_hdt_card_defs(
                card_defs_path,
                requested_card_ids=public_card_ids([state]),
                expected_builds={"246003"},
            )
            enriched, metrics = enrich_public_solver_state(state, snapshot)

        card_defs = manifest["public_card_metadata_enrichment"]["card_defs"]
        self.assertTrue(manifest["public_card_metadata_enrichment"]["enabled"])
        self.assertEqual("246003", card_defs["build"])
        self.assertEqual(64, len(card_defs["sha256"]))
        self.assertEqual(1, card_defs["matched_public_card_id_count"])
        self.assertNotIn(str(card_defs_path), json.dumps(manifest))
        local = enriched["friendly"]["hand"][0]
        self.assertNotIn("cost", local, "CardDefs base cost must not replace replay state")
        self.assertIn("Battlecry", local["english_text"])
        self.assertTrue(local["lifesteal"])
        self.assertEqual(1, metrics["english_text_injected_entity_count"])
        self.assertEqual(
            {"entity_id": "10", "visibility": "hidden"},
            enriched["opponent"]["hand"][0],
        )
        self.assertNotIn("NEVER_PUBLIC_SECRET", json.dumps(manifest))

    def test_card_defs_build_mismatch_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            replay_dir = root / "replays"
            replay_dir.mkdir()
            _write_replay(replay_dir / "one.hdtreplay", _replay_log())
            card_defs_path = root / "CardDefs.base.xml"
            _write_card_defs(card_defs_path, build="247416")

            with self.assertRaises(ReplayImportError) as context:
                import_hdt_replays(
                    replay_dir,
                    root / "output",
                    card_defs_path=card_defs_path,
                )

        self.assertEqual("card_defs_build_mismatch", context.exception.code)

    def test_audit_defaults_to_latest_standard_and_arena_build(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _write_replay(root / "old.hdtreplay", _replay_log(build="100"))
            _write_replay(root / "new.hdtreplay", _replay_log(build="200"))
            _write_replay(
                root / "wild.hdtreplay",
                _replay_log(build="300", format_type="FT_WILD"),
            )

            report = audit_hdt_replays(root)

        self.assertTrue(report["passed"])
        self.assertEqual(["200"], report["selection"]["selected_builds"])
        self.assertEqual(1, report["selection"]["selected_archive_count"])

    def test_rejects_archive_with_unexpected_entry(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bad.hdtreplay"
            _write_replay(source, _replay_log(), extra_entry=True)

            scan = scan_hdt_replay(source)
            self.assertEqual("archive_layout_invalid", scan.error)
            with self.assertRaises(ReplayImportError) as context:
                parse_hdt_replay(source)
            self.assertEqual("archive_layout_invalid", context.exception.code)


if __name__ == "__main__":
    unittest.main()
