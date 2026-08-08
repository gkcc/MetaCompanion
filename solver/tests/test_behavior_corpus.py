from __future__ import annotations

import concurrent.futures
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import _path  # noqa: F401

from metacompanion_solver.behavior import (
    BEHAVIOR_CORPUS_FILENAME,
    BEHAVIOR_SCHEMA_ID,
    BehaviorCorpus,
    BehaviorCorpusError,
    BehaviorRecord,
    BehaviorValidationError,
    audit_behavior_corpus,
    behavior_path_for_training_log,
    create_behavior_record,
)
from metacompanion_solver.trajectory import default_runtime_trajectory_path


def _fixture_text(*parts: str) -> str:
    """Build synthetic private values without resembling a real secret in source."""

    return "".join(parts)


def _entity(
    entity_id: str,
    card_id: str,
    card_type: str,
    *,
    attack: int = 0,
    health: int = 0,
    name: str = "Private localized name",
) -> dict[str, object]:
    return {
        "entity_id": entity_id,
        "card_id": card_id,
        "name": name,
        "controller_id": 999,
        "card_type": card_type,
        "cost": 1,
        "attack": attack,
        "health": health,
        "current_health": health,
        "playable": True,
        "can_attack": attack > 0,
        "attacks_remaining": 1 if attack > 0 else 0,
        "card_text": "Private localized rules text",
        "tags": {"CONTROLLER": 999},
    }


def _state(active: str, state_id: str = "state-1") -> dict[str, object]:
    return {
        "state_id": state_id,
        "turn": 7,
        "active_player_id": active,
        "perspective_player_id": "friendly",
        "friendly": {
            "player_id": "raw-player-1",
            "player_name": "Alice Example",
            "hero": _entity("f-hero", "FRIENDLY_HERO", "HERO", health=30),
            "hero_power": _entity("f-power", "FRIENDLY_POWER", "HERO_POWER"),
            "weapon": None,
            "hand": [_entity("f-hand", "FRIENDLY_CARD", "MINION", attack=2, health=2)],
            "board": [
                _entity("f-minion", "FRIENDLY_MINION", "MINION", attack=3, health=3),
                _entity("f-location", "FRIENDLY_LOCATION", "LOCATION", health=2),
            ],
            "mana": 7,
            "max_mana": 7,
            "armor": 0,
            "deck_size": 18,
            "fatigue": 0,
            "hero_power_available": True,
            "spell_power": 0,
        },
        "opponent": {
            "player_id": "raw-player-2",
            "opponent_name": "Bob Example",
            "hero": _entity("o-hero", "OPPONENT_HERO", "HERO", health=28),
            "hero_power": _entity("o-power", "OPPONENT_POWER", "HERO_POWER"),
            "weapon": None,
            "hand": [
                _entity(
                    "o-hand",
                    "SECRET_PRE_REVEAL_CARD",
                    "SPELL",
                    name="Secret opponent card name",
                )
            ],
            "board": [
                _entity("o-minion", "OPPONENT_MINION", "MINION", attack=4, health=4),
                _entity("o-location", "OPPONENT_LOCATION", "LOCATION", health=2),
            ],
            "mana": 6,
            "max_mana": 7,
            "armor": 0,
            "deck_size": 17,
            "fatigue": 0,
            "hero_power_available": True,
            "spell_power": 0,
        },
        "patch": "test-patch",
        "mode": "standard",
        "rng_seed": 123456,
        "belief": {"private": True},
        "metadata": {
            "password": _fixture_text("never-write-", "this-password"),
            "authorization": _fixture_text(
                "Bearer", " ", "never-write-", "this-token"
            ),
            "raw_power_log": "D 00:00:00 BLOCK_START private raw line",
        },
    }


def _state_with_public_legality_evidence(
    active: str, state_id: str = "state-legality"
) -> dict[str, object]:
    state = _state(active, state_id)
    for role in ("friendly", "opponent"):
        player = state[role]
        player["public_rule_tags"] = {
            "STEADY_SHOT_CAN_TARGET": 1,
            "HERO_POWER_DOUBLE": 0,
            "PRIVATE_UNSAFE_TAG": 8675309,
        }
        player["public_rule_tags_complete"] = True
        entities = [player["hero"], player["hero_power"], *player["hand"], *player["board"]]
        for entity in entities:
            entity.update(
                {
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
            )
    return state


def _record(side: str, kind: str, sequence: int, *, game_id: str = "raw-game-1") -> BehaviorRecord:
    active = "friendly" if side == "local" else "opponent"
    pre = _state(active)
    common = {
        "game_id": game_id,
        "behavior_sequence": sequence,
        "observed_at_utc": f"2026-07-31T12:00:{sequence % 60:02d}+08:00",
        "actor_side": side,
        "actor_player_id": active,
        "boundary_status": "isolated",
        "pre_state": pre,
        "post_state": _state(active, f"state-post-{sequence}"),
    }
    if kind == "play_card":
        if side == "local":
            return create_behavior_record(
                **common,
                actor_evidence="hdt_player_event",
                identity_status="exact_public_entity",
                visibility_status="public_pre_state",
                source_event="player_play",
                action={
                    "kind": kind,
                    "source_entity_id": "f-hand",
                    "target_entity_id": "",
                    "card_id": "FRIENDLY_CARD",
                },
            )
        return create_behavior_record(
            **common,
            actor_evidence="hdt_opponent_event",
            identity_status="revealed_after_action",
            visibility_status="revealed_post_action",
            source_event="opponent_play",
            action={
                "kind": kind,
                "source_entity_id": "o-hand",
                "target_entity_id": "",
                "card_id": "OPPONENT_REVEALED_CARD",
            },
        )
    if kind == "attack":
        source = "f-minion" if side == "local" else "o-minion"
        target = "o-hero" if side == "local" else "f-hero"
        card_id = "FRIENDLY_MINION" if side == "local" else "OPPONENT_MINION"
        return create_behavior_record(
            **common,
            actor_evidence="hdt_player_event" if side == "local" else "hdt_opponent_event",
            identity_status="exact_public_entity",
            visibility_status="public_pre_state",
            source_event="player_attack" if side == "local" else "opponent_attack",
            action={
                "kind": kind,
                "source_entity_id": source,
                "target_entity_id": target,
                "card_id": card_id,
            },
        )
    if kind == "hero_power":
        source = "f-power" if side == "local" else "o-power"
        card_id = "FRIENDLY_POWER" if side == "local" else "OPPONENT_POWER"
        return create_behavior_record(
            **common,
            actor_evidence="hdt_player_event" if side == "local" else "hdt_opponent_event",
            identity_status="exact_public_entity",
            visibility_status="public_pre_state",
            source_event=(
                "player_hero_power" if side == "local" else "opponent_hero_power"
            ),
            action={
                "kind": kind,
                "source_entity_id": source,
                "target_entity_id": "",
                "card_id": card_id,
            },
        )
    if kind == "location_activate" and side == "local":
        return create_behavior_record(
            **common,
            actor_evidence="hdt_power_log",
            identity_status="exact_public_entity",
            visibility_status="public_pre_state",
            source_event="hdt_power_log",
            action={
                "kind": kind,
                "source_entity_id": "f-location",
                "target_entity_id": "f-minion",
                "card_id": "FRIENDLY_LOCATION",
            },
        )
    if kind == "end_turn":
        return create_behavior_record(
            **common,
            actor_evidence="active_player",
            identity_status="event_only",
            visibility_status="public_pre_state",
            source_event=(
                "turn_passed_to_opponent" if side == "local" else "turn_passed_to_player"
            ),
            action={
                "kind": kind,
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": "",
            },
        )
    raise AssertionError(kind)


class BehaviorSchemaTests(unittest.TestCase):
    def test_public_state_rejects_impossible_hand_and_board_capacity(self) -> None:
        for zone, count, expected in (
            ("hand", 11, "public_hand_capacity_exceeded"),
            ("board", 8, "public_board_capacity_exceeded"),
        ):
            with self.subTest(zone=zone):
                pre = _state("friendly")
                pre["friendly"][zone] = [
                    _entity(
                        f"capacity-{zone}-{index}",
                        f"CARD_{index}",
                        "MINION",
                    )
                    for index in range(count)
                ]
                with self.assertRaises(BehaviorValidationError) as context:
                    create_behavior_record(
                        game_id="capacity-game",
                        behavior_sequence=1,
                        observed_at_utc="2026-07-31T12:00:01Z",
                        actor_side="local",
                        actor_player_id="friendly",
                        actor_evidence="active_player",
                        identity_status="event_only",
                        visibility_status="public_pre_state",
                        boundary_status="isolated",
                        source_event="turn_passed_to_opponent",
                        action={"kind": "end_turn"},
                        pre_state=pre,
                        post_state=_state("opponent", "capacity-post"),
                    )
                self.assertEqual(expected, context.exception.code)

    def test_both_sides_base_actions_and_local_location_are_supported(self) -> None:
        records = [
            _record(side, kind, index)
            for index, (side, kind) in enumerate(
                (
                    (side, kind)
                    for side in ("local", "opponent")
                    for kind in ("play_card", "attack", "hero_power", "end_turn")
                ),
                1,
            )
        ]
        records.append(_record("local", "location_activate", len(records) + 1))

        self.assertEqual(9, len(records))
        for record in records:
            with self.subTest(
                side=record.value["actor_side"], kind=record.value["action"]["kind"]
            ):
                reparsed = BehaviorRecord.from_dict(record.to_dict())
                self.assertEqual(BEHAVIOR_SCHEMA_ID, reparsed.value["schema"])
                self.assertTrue(reparsed.value["behavior_eligible"])
                self.assertIs(reparsed.value["rl_training_eligible"], False)
                self.assertEqual(
                    reparsed.value["actor_player_id"],
                    reparsed.value["pre_state"]["active_player_id"],
                )

        opponent_play = next(
            item
            for item in records
            if item.value["actor_side"] == "opponent"
            and item.value["action"]["kind"] == "play_card"
        )
        self.assertEqual("revealed_after_action", opponent_play.value["identity_status"])
        self.assertEqual("revealed_post_action", opponent_play.value["visibility_status"])
        self.assertEqual(
            {"entity_id": "o-hand", "visibility": "hidden"},
            opponent_play.value["pre_state"]["opponent"]["hand"][0],
        )

        location = records[-1]
        self.assertEqual("hdt_power_log", location.value["actor_evidence"])
        self.assertEqual("location_activate", location.value["action"]["kind"])
        self.assertEqual("f-location", location.value["action"]["source_entity_id"])
        self.assertTrue(location.value["behavior_eligible"])

    def test_location_activation_requires_public_board_location(self) -> None:
        common = {
            "game_id": "location-binding",
            "behavior_sequence": 1,
            "observed_at_utc": "2026-07-31T12:00:00Z",
            "actor_side": "local",
            "actor_player_id": "friendly",
            "actor_evidence": "hdt_power_log",
            "identity_status": "exact_public_entity",
            "visibility_status": "public_pre_state",
            "boundary_status": "isolated",
            "source_event": "hdt_power_log",
            "pre_state": _state("friendly"),
            "post_state": _state("friendly", "location-post"),
        }
        cases = (
            ("f-hand", "FRIENDLY_CARD", "location_source_not_on_board"),
            ("f-minion", "FRIENDLY_MINION", "location_source_not_location"),
        )
        for source_entity_id, card_id, expected_code in cases:
            with self.subTest(source_entity_id=source_entity_id), self.assertRaises(
                BehaviorValidationError
            ) as caught:
                create_behavior_record(
                    **common,
                    action={
                        "kind": "location_activate",
                        "source_entity_id": source_entity_id,
                        "target_entity_id": "o-hero",
                        "card_id": card_id,
                    },
                )
            self.assertEqual(expected_code, caught.exception.code)

    def test_selected_choice_and_board_position_round_trip_without_rl_promotion(self) -> None:
        record = create_behavior_record(
            game_id="selected-choice",
            behavior_sequence=1,
            observed_at_utc="2026-07-31T12:00:00Z",
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
                "card_id": "FRIENDLY_CARD",
                "sub_option": 1,
                "board_position": 3,
                "choice_status": "selected",
                "choices": [
                    {
                        "choice_id": None,
                        "choice_type": "SUB_OPTION",
                        "source_entity_id": "f-hand",
                        "option_entity_ids": ["choice-a", "choice-b"],
                        "selected_entity_ids": ["choice-b"],
                        "status": "selected",
                    }
                ],
            },
            pre_state=_state("friendly"),
            post_state=_state("friendly", "selected-choice-post"),
        )
        action = record.value["action"]
        self.assertEqual(1, action["sub_option"])
        self.assertEqual(3, action["board_position"])
        self.assertEqual("selected", action["choice_status"])
        self.assertEqual(["choice-a", "choice-b"], action["choices"][0]["option_entity_ids"])
        self.assertEqual(["choice-b"], action["choices"][0]["selected_entity_ids"])
        self.assertTrue(record.value["behavior_eligible"])
        self.assertIs(record.value["rl_training_eligible"], False)
        self.assertEqual(record.to_dict(), BehaviorRecord.from_dict(record.to_dict()).to_dict())

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            self.assertTrue(BehaviorCorpus(path).append(record))
            audit = audit_behavior_corpus(path)
            self.assertTrue(audit["valid"])
            self.assertEqual({"selected": 1}, audit["choice_status_counts"])
            self.assertEqual(1, audit["board_position_record_count"])
            self.assertEqual(1, audit["choice_item_count"])
            self.assertEqual(2, audit["offered_choice_entity_count"])
            self.assertEqual(1, audit["selected_choice_entity_count"])

        legacy = _record("local", "play_card", 2)
        self.assertNotIn("choice_status", legacy.value["action"])
        self.assertEqual(legacy.to_dict(), BehaviorRecord.from_dict(legacy.to_dict()).to_dict())

    def test_unresolved_or_spoofed_choice_cannot_be_behavior_eligible(self) -> None:
        common = {
            "game_id": "choice-quality",
            "behavior_sequence": 1,
            "observed_at_utc": "2026-07-31T12:00:00Z",
            "actor_side": "local",
            "actor_player_id": "friendly",
            "actor_evidence": "hdt_power_log",
            "identity_status": "exact_public_entity",
            "visibility_status": "public_pre_state",
            "boundary_status": "isolated",
            "source_event": "hdt_power_log",
            "pre_state": _state("friendly"),
            "post_state": _state("friendly", "choice-quality-post"),
        }
        unresolved_action = {
            "kind": "play_card",
            "source_entity_id": "f-hand",
            "target_entity_id": "",
            "card_id": "FRIENDLY_CARD",
            "sub_option": -1,
            "board_position": 0,
            "choice_status": "unresolved",
            "choices": [
                {
                    "choice_id": 17,
                    "choice_type": "GENERAL",
                    "source_entity_id": "f-hand",
                    "option_entity_ids": ["choice-a"],
                    "selected_entity_ids": [],
                    "status": "unresolved",
                }
            ],
        }
        unresolved = create_behavior_record(**common, action=unresolved_action)
        self.assertIs(unresolved.value["behavior_eligible"], False)
        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(
                **common,
                action=unresolved_action,
                behavior_eligible=True,
            )
        self.assertEqual("behavior_eligibility_mismatch", caught.exception.code)

        selected = dict(unresolved_action)
        selected["choice_status"] = "selected"
        selected["choices"] = [dict(unresolved_action["choices"][0])]
        selected["choices"][0]["status"] = "selected"
        selected["choices"][0]["selected_entity_ids"] = ["choice-b"]
        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(**common, action=selected)
        self.assertEqual("selected_entity_not_offered", caught.exception.code)

        selected["choices"][0]["selected_entity_ids"] = ["choice-a"]
        selected["choices"][0]["raw_power_log"] = "private line"
        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(**common, action=selected)
        self.assertTrue(caught.exception.code.startswith("unknown_field:"))

    def test_unknown_actor_is_retained_only_as_ineligible_evidence(self) -> None:
        record = create_behavior_record(
            game_id="game-unknown",
            behavior_sequence=1,
            observed_at_utc="2026-07-31T12:00:00Z",
            actor_side="unknown",
            actor_player_id="friendly",
            actor_evidence="unknown",
            identity_status="unknown",
            visibility_status="hidden_source",
            boundary_status="unverified",
            source_event="unknown",
            action={
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": "",
            },
            pre_state=_state("friendly"),
        )
        self.assertEqual("unknown", record.value["actor_side"])
        self.assertFalse(record.value["behavior_eligible"])

    def test_known_actor_unknown_identity_accepts_all_visibility_tiers_only_as_downgrade(
        self,
    ) -> None:
        for visibility_status in (
            "public_pre_state",
            "revealed_post_action",
            "hidden_source",
        ):
            with self.subTest(visibility_status=visibility_status):
                record = create_behavior_record(
                    game_id="known-actor-unresolved",
                    behavior_sequence=1,
                    observed_at_utc="2026-07-31T12:00:00Z",
                    actor_side="local",
                    actor_player_id="friendly",
                    actor_evidence="hdt_player_event",
                    identity_status="unknown",
                    visibility_status=visibility_status,
                    boundary_status="isolated",
                    source_event="player_attack",
                    action={
                        "kind": "attack",
                        "source_entity_id": "",
                        "target_entity_id": "",
                        "card_id": "",
                    },
                    pre_state=_state("friendly"),
                    behavior_eligible=False,
                )
                self.assertEqual("unknown", record.value["identity_status"])
                self.assertEqual(visibility_status, record.value["visibility_status"])
                self.assertIs(record.value["behavior_eligible"], False)
                self.assertIs(record.value["rl_training_eligible"], False)
                self.assertEqual(
                    record.to_dict(),
                    BehaviorRecord.from_dict(record.to_dict()).to_dict(),
                )

    def test_known_actor_unknown_identity_cannot_spoof_bindings_or_self_promote(
        self,
    ) -> None:
        common = {
            "game_id": "known-actor-unresolved",
            "behavior_sequence": 1,
            "observed_at_utc": "2026-07-31T12:00:00Z",
            "actor_side": "local",
            "actor_player_id": "friendly",
            "actor_evidence": "hdt_player_event",
            "identity_status": "unknown",
            "visibility_status": "hidden_source",
            "boundary_status": "isolated",
            "source_event": "player_attack",
            "pre_state": _state("friendly"),
        }
        cases = (
            (
                "wrong-side-source",
                {
                    "kind": "attack",
                    "source_entity_id": "o-minion",
                    "target_entity_id": "",
                    "card_id": "",
                },
                False,
                "source_owner_mismatch",
            ),
            (
                "invented-target",
                {
                    "kind": "attack",
                    "source_entity_id": "",
                    "target_entity_id": "missing-target",
                    "card_id": "",
                },
                False,
                "target_not_in_pre_state",
            ),
            (
                "self-promotion",
                {
                    "kind": "attack",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                },
                True,
                "behavior_eligibility_mismatch",
            ),
        )
        for name, action, behavior_eligible, expected_code in cases:
            with self.subTest(name=name), self.assertRaises(
                BehaviorValidationError
            ) as caught:
                create_behavior_record(
                    **common,
                    action=action,
                    behavior_eligible=behavior_eligible,
                )
            self.assertEqual(expected_code, caught.exception.code)

    def test_actor_spoof_and_source_owner_spoof_fail_closed(self) -> None:
        cases = (
            {
                "actor_side": "opponent",
                "actor_player_id": "friendly",
                "source_entity_id": "f-minion",
                "target_entity_id": "o-hero",
                "card_id": "FRIENDLY_MINION",
            },
            {
                "actor_side": "local",
                "actor_player_id": "opponent",
                "source_entity_id": "o-minion",
                "target_entity_id": "f-hero",
                "card_id": "OPPONENT_MINION",
            },
            {
                "actor_side": "local",
                "actor_player_id": "friendly",
                "source_entity_id": "o-minion",
                "target_entity_id": "f-hero",
                "card_id": "OPPONENT_MINION",
            },
        )
        for case in cases:
            with self.subTest(case=case), self.assertRaises(BehaviorValidationError):
                create_behavior_record(
                    game_id="spoof-game",
                    behavior_sequence=1,
                    observed_at_utc="2026-07-31T12:00:00Z",
                    actor_side=case["actor_side"],
                    actor_player_id=case["actor_player_id"],
                    actor_evidence="source_owner",
                    identity_status="exact_public_entity",
                    visibility_status="public_pre_state",
                    boundary_status="isolated",
                    source_event=(
                        "player_attack"
                        if case["actor_side"] == "local"
                        else "opponent_attack"
                    ),
                    action={
                        "kind": "attack",
                        "source_entity_id": case["source_entity_id"],
                        "target_entity_id": case["target_entity_id"],
                        "card_id": case["card_id"],
                    },
                    pre_state=_state("friendly"),
                )

    def test_hidden_opponent_play_cannot_claim_public_exact_identity(self) -> None:
        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(
                game_id="hidden-spoof",
                behavior_sequence=1,
                observed_at_utc="2026-07-31T12:00:00Z",
                actor_side="opponent",
                actor_player_id="opponent",
                actor_evidence="hdt_opponent_event",
                identity_status="exact_public_entity",
                visibility_status="public_pre_state",
                boundary_status="isolated",
                source_event="opponent_play",
                action={
                    "kind": "play_card",
                    "source_entity_id": "o-hand",
                    "target_entity_id": "",
                    "card_id": "OPPONENT_REVEALED_CARD",
                },
                pre_state=_state("opponent"),
            )
        self.assertEqual("opponent_hidden_play_tier_mismatch", caught.exception.code)

    def test_rl_flag_and_behavior_tier_cannot_be_self_promoted(self) -> None:
        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(
                game_id="bad-rl",
                behavior_sequence=1,
                observed_at_utc="2026-07-31T12:00:00Z",
                actor_side="local",
                actor_player_id="friendly",
                actor_evidence="active_player",
                identity_status="event_only",
                visibility_status="public_pre_state",
                boundary_status="isolated",
                source_event="turn_passed_to_opponent",
                action={
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                },
                pre_state=_state("friendly"),
                rl_training_eligible=True,
            )
        self.assertEqual("rl_training_eligible_must_be_false", caught.exception.code)

        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(
                game_id="bad-behavior-tier",
                behavior_sequence=1,
                observed_at_utc="2026-07-31T12:00:00Z",
                actor_side="local",
                actor_player_id="friendly",
                actor_evidence="active_player",
                identity_status="event_only",
                visibility_status="public_pre_state",
                boundary_status="overlapped",
                source_event="turn_passed_to_opponent",
                action={
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                },
                pre_state=_state("friendly"),
                behavior_eligible=True,
            )
        self.assertEqual("behavior_eligibility_mismatch", caught.exception.code)

    def test_missing_post_state_is_retained_only_as_ineligible_evidence(self) -> None:
        kwargs = {
            "game_id": "missing-post-state",
            "behavior_sequence": 1,
            "observed_at_utc": "2026-07-31T12:00:00Z",
            "actor_side": "local",
            "actor_player_id": "friendly",
            "actor_evidence": "active_player",
            "identity_status": "event_only",
            "visibility_status": "public_pre_state",
            "boundary_status": "isolated",
            "source_event": "turn_passed_to_opponent",
            "action": {
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": "",
            },
            "pre_state": _state("friendly"),
            "post_state": None,
        }
        record = create_behavior_record(**kwargs)
        self.assertIs(record.value["post_state"], None)
        self.assertIs(record.value["behavior_eligible"], False)
        self.assertIs(record.value["rl_training_eligible"], False)

        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(**kwargs, behavior_eligible=True)
        self.assertEqual("behavior_eligibility_mismatch", caught.exception.code)

    def test_game_id_is_required_and_written_only_as_anonymous_id(self) -> None:
        with self.assertRaises(BehaviorValidationError) as caught:
            create_behavior_record(
                game_id=None,
                behavior_sequence=1,
                observed_at_utc="2026-07-31T12:00:00Z",
                actor_side="local",
                actor_player_id="friendly",
                actor_evidence="active_player",
                identity_status="event_only",
                visibility_status="public_pre_state",
                boundary_status="isolated",
                source_event="turn_passed_to_opponent",
                action={
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                },
                pre_state=_state("friendly"),
            )
        self.assertEqual("game_id_required", caught.exception.code)
        self.assertRegex(_record("local", "end_turn", 1).game_id, r"^anon-[0-9a-f]{16}$")

    def test_public_state_projection_drops_names_controller_ids_logs_and_credentials(self) -> None:
        record = _record("local", "attack", 1)
        payload = record.to_dict()
        serialized = json.dumps(payload, ensure_ascii=False)
        for secret in (
            "Alice Example",
            "Bob Example",
            "Secret opponent card name",
            _fixture_text("never-write-", "this-password"),
            _fixture_text("never-write-", "this-token"),
            "BLOCK_START private raw line",
            "Private localized name",
            "Private localized rules text",
            "SECRET_PRE_REVEAL_CARD",
        ):
            self.assertNotIn(secret, serialized)

        forbidden_keys = {
            "name",
            "player_name",
            "opponent_name",
            "controller",
            "controller_id",
            "raw_power_log",
            "password",
            "authorization",
            "metadata",
            "card_text",
        }

        def visit(value: object) -> None:
            if isinstance(value, dict):
                self.assertFalse(forbidden_keys.intersection(value))
                for child in value.values():
                    visit(child)
            elif isinstance(value, list):
                for child in value:
                    visit(child)

        visit(payload)

        injected = record.to_dict()
        injected["raw_power_log"] = "private"
        with self.assertRaises(BehaviorValidationError):
            BehaviorRecord.from_dict(injected)
        with self.assertRaises(BehaviorValidationError):
            create_behavior_record(
                game_id="bad-action",
                behavior_sequence=2,
                observed_at_utc="2026-07-31T12:00:00Z",
                actor_side="local",
                actor_player_id="friendly",
                actor_evidence="active_player",
                identity_status="event_only",
                visibility_status="public_pre_state",
                boundary_status="isolated",
                source_event="turn_passed_to_opponent",
                action={
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                    "password": _fixture_text("synthetic-", "private-value"),
                },
                pre_state=_state("friendly"),
            )

    def test_public_legality_evidence_is_preserved_by_a_strict_allowlist(self) -> None:
        record = create_behavior_record(
            game_id="public-legality-evidence",
            behavior_sequence=1,
            observed_at_utc="2026-08-01T01:00:00+08:00",
            actor_side="local",
            actor_player_id="friendly",
            actor_evidence="active_player",
            identity_status="event_only",
            visibility_status="public_pre_state",
            boundary_status="isolated",
            source_event="turn_passed_to_opponent",
            action={
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": "",
            },
            pre_state=_state_with_public_legality_evidence("friendly"),
            post_state=_state_with_public_legality_evidence(
                "friendly", "state-legality-post"
            ),
        )

        pre_state = record.value["pre_state"]
        self.assertEqual(
            {"HERO_POWER_DOUBLE": 0, "STEADY_SHOT_CAN_TARGET": 1},
            pre_state["friendly"]["public_rule_tags"],
        )
        self.assertIs(pre_state["friendly"]["public_rule_tags_complete"], True)
        minion = pre_state["friendly"]["board"][0]
        for key in (
            "current_health_known",
            "taunt",
            "divine_shield",
            "stealth",
            "poisonous",
            "lifesteal",
            "windfury",
            "mega_windfury",
            "rush",
            "charge",
            "reborn",
            "dormant",
            "immune",
            "summoned_this_turn",
            "frozen",
        ):
            self.assertIn(key, minion)
            self.assertIsInstance(minion[key], bool)
        self.assertEqual(
            {"entity_id": "o-hand", "visibility": "hidden"},
            pre_state["opponent"]["hand"][0],
        )
        serialized = json.dumps(record.to_dict(), ensure_ascii=False)
        self.assertNotIn("PRIVATE_UNSAFE_TAG", serialized)
        self.assertNotIn("Private localized rules text", serialized)
        self.assertEqual(
            record.to_dict(), BehaviorRecord.from_dict(record.to_dict()).to_dict()
        )

        invalid_cases = (
            ("unsafe_rule_tag", "unknown_field:PRIVATE_UNSAFE_TAG"),
            ("invalid_rule_tag_type", "must_be_integer"),
            ("invalid_completeness_type", "must_be_boolean"),
            ("invalid_entity_evidence_type", "must_be_boolean"),
        )
        for case, expected_code in invalid_cases:
            with self.subTest(case=case):
                invalid = record.to_dict()
                if case == "unsafe_rule_tag":
                    invalid["pre_state"]["friendly"]["public_rule_tags"][
                        "PRIVATE_UNSAFE_TAG"
                    ] = 1
                elif case == "invalid_rule_tag_type":
                    invalid["pre_state"]["friendly"]["public_rule_tags"][
                        "HERO_POWER_DOUBLE"
                    ] = True
                elif case == "invalid_completeness_type":
                    invalid["pre_state"]["friendly"][
                        "public_rule_tags_complete"
                    ] = "yes"
                else:
                    invalid["pre_state"]["friendly"]["board"][0]["taunt"] = 1
                with self.assertRaises(BehaviorValidationError) as caught:
                    BehaviorRecord.from_dict(invalid)
                self.assertEqual(expected_code, caught.exception.code)

        legacy = _record("local", "end_turn", 2)
        self.assertNotIn("public_rule_tags", legacy.value["pre_state"]["friendly"])
        self.assertNotIn(
            "current_health_known",
            legacy.value["pre_state"]["friendly"]["hero"],
        )
        self.assertEqual(
            legacy.to_dict(), BehaviorRecord.from_dict(legacy.to_dict()).to_dict()
        )


class BehaviorCorpusTests(unittest.TestCase):
    def test_content_address_and_sequence_are_idempotent(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            corpus = BehaviorCorpus(path)
            record = _record("local", "attack", 1)
            self.assertTrue(corpus.append(record))
            self.assertFalse(corpus.append(record.to_dict()))
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

            conflicting = _record("local", "hero_power", 1)
            with self.assertRaises(BehaviorCorpusError) as caught:
                corpus.append(conflicting)
            self.assertEqual("behavior_sequence_conflict", caught.exception.code)

            tampered = record.to_dict()
            tampered["action"]["target_entity_id"] = "o-minion"
            with self.assertRaises(BehaviorValidationError) as caught:
                BehaviorRecord.from_dict(tampered)
            self.assertEqual("content_sha256_mismatch", caught.exception.code)

            audit = audit_behavior_corpus(path)
            self.assertTrue(audit["valid"])
            self.assertEqual(1, audit["record_count"])
            self.assertEqual(1, audit["behavior_eligible_count"])

    def test_same_size_external_rewrite_is_revalidated_before_append(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            corpus = BehaviorCorpus(path)
            self.assertTrue(corpus.append(_record("local", "attack", 1)))
            original = path.read_bytes()
            original_stat = path.stat()
            damaged = original.replace(b'"kind":"attack"', b'"kind":"attacx"', 1)
            self.assertNotEqual(original, damaged)
            self.assertEqual(len(original), len(damaged))

            path.write_bytes(damaged)
            os.utime(
                path,
                ns=(original_stat.st_atime_ns, original_stat.st_mtime_ns + 2_000_000_000),
            )
            with self.assertRaises(BehaviorCorpusError) as caught:
                corpus.append(_record("opponent", "end_turn", 2))
            self.assertEqual("existing_corpus_invalid", caught.exception.code)
            self.assertEqual(damaged, path.read_bytes())

            path.write_bytes(original)
            os.utime(
                path,
                ns=(original_stat.st_atime_ns, original_stat.st_mtime_ns + 4_000_000_000),
            )
            self.assertTrue(corpus.append(_record("opponent", "end_turn", 2)))
            self.assertEqual(2, len(path.read_text(encoding="utf-8").splitlines()))

    def test_sequence_gaps_fail_closed_before_and_after_restart(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            corpus = BehaviorCorpus(path)
            self.assertTrue(corpus.append(_record("local", "attack", 1)))

            with self.assertRaises(BehaviorCorpusError) as caught:
                corpus.append(_record("local", "end_turn", 3))
            self.assertEqual("behavior_sequence_out_of_order", caught.exception.code)

            restarted = BehaviorCorpus(path)
            with self.assertRaises(BehaviorCorpusError) as caught:
                restarted.append(_record("local", "end_turn", 3))
            self.assertEqual("behavior_sequence_out_of_order", caught.exception.code)
            self.assertTrue(restarted.append(_record("opponent", "end_turn", 2)))
            self.assertEqual(2, len(path.read_text(encoding="utf-8").splitlines()))

    def test_reload_rejects_noncontiguous_existing_sequences_without_overwrite(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            records = (
                _record("local", "attack", 1),
                _record("local", "end_turn", 3),
            )
            path.write_text(
                "".join(
                    json.dumps(
                        record.to_dict(),
                        ensure_ascii=False,
                        sort_keys=True,
                        separators=(",", ":"),
                    )
                    + "\n"
                    for record in records
                ),
                encoding="utf-8",
            )
            before = path.read_bytes()

            corpus = BehaviorCorpus(path)
            self.assertEqual(
                "existing_behavior_sequence_not_contiguous", corpus.startup_error
            )
            with self.assertRaises(BehaviorCorpusError) as caught:
                corpus.append(_record("local", "hero_power", 4))
            self.assertEqual(
                "existing_behavior_sequence_not_contiguous", caught.exception.code
            )
            self.assertEqual(before, path.read_bytes())

    def test_reload_rejects_duplicate_existing_row_without_overwrite(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            self.assertTrue(BehaviorCorpus(path).append(_record("local", "attack", 1)))
            line = path.read_bytes()
            path.write_bytes(line + line)
            damaged = path.read_bytes()

            corpus = BehaviorCorpus(path)
            self.assertEqual("existing_corpus_duplicate", corpus.startup_error)
            with self.assertRaises(BehaviorCorpusError) as caught:
                corpus.append(_record("opponent", "end_turn", 2))
            self.assertEqual("existing_corpus_duplicate", caught.exception.code)
            self.assertEqual(damaged, path.read_bytes())

    def test_durable_ack_waits_for_fsync_and_restart_rebuild_fsync(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            record = _record(
                "local", "attack", 1, game_id="behavior-durability-game"
            )
            corpus = BehaviorCorpus(path)
            with mock.patch(
                "metacompanion_solver.behavior.os.fsync",
                side_effect=OSError("injected behavior durability failure"),
            ) as fsync:
                with self.assertRaises(BehaviorCorpusError) as caught:
                    corpus.append(record)
            self.assertEqual("corpus_append_failed", caught.exception.code)
            fsync.assert_called_once()
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))
            del corpus

            # A restarted worker has lost the first process's stale marker. Its
            # own rebuild must still fsync before it can acknowledge a retry.
            with mock.patch(
                "metacompanion_solver.behavior.os.fsync",
                side_effect=OSError("injected restart durability failure"),
            ) as restart_fsync:
                restarted = BehaviorCorpus(path)
                self.assertTrue(restarted.startup_error)
                with self.assertRaises(BehaviorCorpusError):
                    restarted.append(record)
            self.assertGreaterEqual(restart_fsync.call_count, 2)
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

            self.assertFalse(restarted.append(record))
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))

    def test_restart_archives_torn_tail_and_retry_writes_one_row(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            first = _record("local", "attack", 1, game_id="behavior-torn-game")
            seed = BehaviorCorpus(path)
            self.assertTrue(seed.append(first))
            complete_history = path.read_bytes()
            torn_fragment = b'{"behavior_id":"behavior-incomplete'
            with path.open("ab") as handle:
                handle.write(torn_fragment)

            recovered = BehaviorCorpus(path)
            self.assertEqual("", recovered.startup_error)
            second = _record(
                "opponent", "end_turn", 2, game_id="behavior-torn-game"
            )
            self.assertTrue(recovered.append(second))
            contents = path.read_bytes()
            self.assertTrue(contents.startswith(complete_history))
            self.assertEqual(2, len(contents.splitlines()))

            archives = list(Path(directory).glob("*.torn-tail.*.fragment"))
            self.assertEqual(1, len(archives))
            archive = archives[0]
            self.assertEqual(torn_fragment, archive.read_bytes())
            self.assertFalse(archive.stat().st_mode & 0o200)
            self.assertFalse(BehaviorCorpus(path).append(second))
            self.assertEqual(2, len(path.read_text(encoding="utf-8").splitlines()))
            archive.chmod(0o666)

    def test_complete_json_without_terminal_newline_is_preserved(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            record = _record("local", "attack", 1)
            self.assertTrue(BehaviorCorpus(path).append(record))
            contents = path.read_bytes()
            self.assertTrue(contents.endswith(b"\n"))
            path.write_bytes(contents[:-1])

            corpus = BehaviorCorpus(path)
            self.assertEqual("", corpus.startup_error)
            self.assertFalse(corpus.append(record))
            self.assertTrue(path.read_bytes().endswith(b"\n"))
            self.assertEqual(1, len(path.read_text(encoding="utf-8").splitlines()))
            self.assertEqual(
                [], list(Path(directory).glob("*.torn-tail.*.fragment"))
            )

    def test_complete_middle_corruption_is_not_repaired_and_health_is_false(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            self.assertTrue(BehaviorCorpus(path).append(_record("local", "attack", 1)))
            with path.open("ab") as handle:
                handle.write(b"not-json\n")
            damaged = path.read_bytes()

            corpus = BehaviorCorpus(path)
            self.assertEqual("existing_corpus_invalid", corpus.startup_error)
            with self.assertRaises(BehaviorCorpusError) as caught:
                corpus.append(_record("opponent", "end_turn", 2))
            self.assertEqual("existing_corpus_invalid", caught.exception.code)
            self.assertEqual(damaged, path.read_bytes())
            self.assertEqual(
                [], list(Path(directory).glob("*.torn-tail.*.fragment"))
            )

    def test_reserved_trajectory_log_path_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(BehaviorCorpusError) as caught:
                BehaviorCorpus(Path(directory) / "training-v2.jsonl")
        self.assertEqual(
            "behavior_corpus_path_must_be_independent", caught.exception.code
        )

    def test_default_behavior_and_trajectory_runtime_paths_are_distinct(self) -> None:
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(
            os.environ, {"APPDATA": directory}
        ):
            behavior_path = BehaviorCorpus.for_data_directory(directory).path
            trajectory_path = default_runtime_trajectory_path()
        self.assertEqual(BEHAVIOR_CORPUS_FILENAME, behavior_path.name)
        self.assertIsNotNone(trajectory_path)
        self.assertEqual("training-v2.jsonl", trajectory_path.name)
        self.assertNotEqual(behavior_path.resolve(), trajectory_path.resolve())

    def test_training_log_path_cannot_alias_the_behavior_corpus(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            training_path = Path(directory) / "training-v2.jsonl"
            self.assertEqual(
                Path(directory) / BEHAVIOR_CORPUS_FILENAME,
                behavior_path_for_training_log(training_path),
            )
            self.assertIsNone(behavior_path_for_training_log(None))

            with self.assertRaises(BehaviorCorpusError) as caught:
                behavior_path_for_training_log(
                    Path(directory) / BEHAVIOR_CORPUS_FILENAME
                )
            self.assertEqual(
                "behavior_corpus_path_must_be_independent", caught.exception.code
            )

    def test_concurrent_writers_preserve_complete_jsonl_and_contiguous_sequences(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            corpora = (BehaviorCorpus(path), BehaviorCorpus(path))
            records = [
                _record(
                    "local" if sequence % 2 else "opponent",
                    ("attack", "hero_power", "end_turn", "play_card")[sequence % 4],
                    sequence,
                    game_id=f"concurrent-game-{game_index}",
                )
                for game_index in range(16)
                for sequence in range(1, 5)
            ]

            def append_game(game_index: int) -> list[bool]:
                start = game_index * 4
                return [
                    corpora[(game_index + record.behavior_sequence) % 2].append(record)
                    for record in records[start : start + 4]
                ]

            with concurrent.futures.ThreadPoolExecutor(max_workers=12) as executor:
                appended = list(executor.map(append_game, range(16)))
            self.assertEqual(64, sum(result for game in appended for result in game))

            with concurrent.futures.ThreadPoolExecutor(max_workers=12) as executor:
                duplicates = list(
                    executor.map(
                        lambda item: corpora[(item.behavior_sequence + 1) % 2].append(item),
                        records,
                    )
                )
            self.assertFalse(any(duplicates))

            lines = path.read_text(encoding="utf-8").splitlines()
            self.assertEqual(64, len(lines))
            self.assertEqual(64, len({json.loads(line)["behavior_id"] for line in lines}))
            audit = audit_behavior_corpus(path)
            self.assertTrue(audit["valid"], audit["issues"])
            self.assertEqual(64, audit["valid_record_count"])
            self.assertEqual(0, audit["non_contiguous_game_count"])
            self.assertEqual({"local": 32, "opponent": 32}, audit["actor_side_counts"])

    def test_audit_reports_smuggled_fields_without_rewriting_the_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / BEHAVIOR_CORPUS_FILENAME
            corpus = BehaviorCorpus(path)
            self.assertTrue(corpus.append(_record("local", "end_turn", 1)))
            before = path.read_bytes()
            invalid = _record("opponent", "end_turn", 2).to_dict()
            invalid["raw_power_log"] = "private raw line"
            with path.open("a", encoding="utf-8", newline="\n") as handle:
                handle.write(json.dumps(invalid, separators=(",", ":")) + "\n")
            written = path.read_bytes()

            audit = audit_behavior_corpus(path)
            self.assertFalse(audit["valid"])
            self.assertEqual(2, audit["record_count"])
            self.assertEqual(1, audit["valid_record_count"])
            self.assertGreaterEqual(audit["privacy_violation_count"], 1)
            self.assertEqual(written, path.read_bytes())
            self.assertTrue(written.startswith(before))


if __name__ == "__main__":
    unittest.main()
