from __future__ import annotations

import copy
import unittest

import _path  # noqa: F401

from metacompanion_solver.errors import SchemaError
from metacompanion_solver.schemas import (
    TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
    TRANSITION_CANDIDATE_STATUS,
    TRANSITION_CANDIDATE_VERIFICATION,
    Action,
    Effect,
    GameState,
    Observation,
    SolveOptions,
    SolveRequest,
)

from helpers import advisor_entity, advisor_snapshot, native_request_dict, state


def _hdt_root_candidates(state_id: str) -> dict:
    return {
        "contract": "hdt_complete_main_action_options_v1",
        "state_id": state_id,
        "frame_id": 7,
        "collector_epoch": 3,
        "frame_watermark": 11,
        "candidate_set_complete": True,
        "candidates": [
            {
                "option_id": 0,
                "action": {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                    "board_position": 0,
                },
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable",
            }
        ],
    }


class SchemaTests(unittest.TestCase):
    def test_effect_schema_accepts_reviewed_trigger_and_other_target_modes(self) -> None:
        effect = Effect.from_dict(
            {
                "kind": "damage",
                "trigger": "frenzy",
                "amount": 1,
                "target": "all_other_minions",
            },
            "effect",
        )
        self.assertEqual("frenzy", effect.trigger)
        self.assertEqual("all_other_minions", effect.target)
        self.assertEqual(
            "damaged_enemy_minion",
            Effect.from_dict(
                {
                    "kind": "destroy",
                    "target": "damaged_enemy_minion",
                },
                "effect",
            ).target,
        )

    def test_action_board_position_is_bounded_and_part_of_identity(self) -> None:
        positioned = Action.from_dict(
            {
                "kind": "play_card",
                "source_entity_id": "21",
                "target_entity_id": "",
                "board_position": 7,
            }
        )
        self.assertEqual("play_card:21::position=7", positioned.action_id)
        self.assertEqual(7, positioned.to_dict()["board_position"])
        self.assertNotIn(
            "board_position",
            Action.from_dict(
                {
                    "kind": "play_card",
                    "source_entity_id": "21",
                    "target_entity_id": "",
                }
            ).to_dict(),
        )
        for invalid in (-1, 8, True, "1"):
            with self.subTest(invalid=invalid), self.assertRaises(SchemaError):
                Action.from_dict(
                    {
                        "kind": "play_card",
                        "source_entity_id": "21",
                        "target_entity_id": "",
                        "board_position": invalid,
                    }
                )

    def test_native_state_round_trip(self) -> None:
        original = state()
        parsed = GameState.from_dict(original.to_dict())
        self.assertEqual(original.to_dict(), parsed.to_dict())

    def test_duplicate_entities_are_rejected(self) -> None:
        raw = state().to_dict()
        raw["opponent"]["hero"]["entity_id"] = raw["friendly"]["hero"]["entity_id"]
        with self.assertRaises(SchemaError):
            GameState.from_dict(raw)

    def test_hdt_snapshot_adapter_is_conservative(self) -> None:
        parsed = GameState.from_dict(advisor_snapshot())
        self.assertEqual("1", parsed.active_player_id)
        self.assertEqual(27, parsed.friendly.hero.current_health)
        self.assertEqual(6, parsed.friendly.mana)
        self.assertEqual("unsupported", parsed.friendly.hand[0].effect_coverage)
        self.assertIn("card_text_not_parsed", parsed.friendly.hand[0].unsupported_effects)
        self.assertEqual("hdt-snapshot-v1", parsed.metadata["adapter"])
        self.assertEqual(4, parsed.metadata["snapshot_sequence"])

    def test_hdt_resource_total_beats_rules_cap_and_preserves_zero(self) -> None:
        raw = advisor_snapshot()
        raw["player"]["max_mana"] = 10
        raw["player"]["resources"].update({"available": 1, "total": 1})
        raw["opponent"]["max_mana"] = 10
        raw["opponent"]["resources"].update({"available": 0, "total": 0})

        parsed = GameState.from_dict(raw)

        self.assertEqual(1, parsed.friendly.max_mana)
        self.assertEqual(0, parsed.opponent.max_mana)

    def test_hdt_legacy_snapshot_without_resource_total_uses_top_level_mana(self) -> None:
        raw = advisor_snapshot()
        raw["player"]["max_mana"] = 4
        raw["player"]["resources"].pop("total")

        parsed = GameState.from_dict(raw)

        self.assertEqual(4, parsed.friendly.max_mana)

    def test_hdt_keyword_only_text_requires_matching_public_state(self) -> None:
        raw = advisor_snapshot()
        rush = advisor_entity(
            40,
            "BAR_035t",
            "MINION",
            name="Swift Hyena",
            attack=1,
            health=1,
            text="Rush",
        )
        rush.update({"has_rush": True, "mechanics": ["RUSH"]})
        elusive = advisor_entity(
            41,
            "CATA_558",
            "MINION",
            name="Reinforcement Rallier",
            attack=2,
            health=2,
            text="Elusive",
        )
        elusive.update(
            {
                "mechanics": ["ELUSIVE"],
                "tags": {
                    "ELUSIVE": 1,
                    "CANT_BE_TARGETED_BY_SPELLS": 1,
                    "CANT_BE_TARGETED_BY_HERO_POWERS": 1,
                },
            }
        )
        raw["player"]["hand"] = [rush, elusive]

        cards = GameState.from_dict(raw).friendly.hand
        for card in cards:
            with self.subTest(card=card.card_id):
                self.assertEqual("generic", card.effect_coverage)
                self.assertEqual((), card.unsupported_effects)
                self.assertEqual("hdt-intrinsic-keywords-v1", card.rule_id)
                self.assertEqual(64, len(card.rule_text_sha256))

        missing_evidence = copy.deepcopy(raw)
        missing_evidence["player"]["hand"][1]["tags"].pop("ELUSIVE")
        missing_evidence["player"]["hand"][1]["tags"].pop(
            "CANT_BE_TARGETED_BY_HERO_POWERS"
        )
        unsupported = GameState.from_dict(missing_evidence).friendly.hand[1]
        self.assertEqual("unsupported", unsupported.effect_coverage)
        self.assertIn("card_text_not_parsed", unsupported.unsupported_effects)
        self.assertEqual("", unsupported.rule_id)

    def test_hdt_keyword_prefix_does_not_hide_compound_card_text(self) -> None:
        raw = advisor_snapshot()
        card = advisor_entity(
            40,
            "RUSH_BATTLECRY",
            "MINION",
            attack=2,
            health=2,
            text="Rush. Battlecry: Draw a card.",
        )
        card.update({"has_rush": True, "mechanics": ["RUSH", "BATTLECRY"]})
        raw["player"]["hand"] = [card]

        parsed = GameState.from_dict(raw).friendly.hand[0]
        self.assertEqual("unsupported", parsed.effect_coverage)
        self.assertIn("card_text_not_parsed", parsed.unsupported_effects)
        self.assertIn("battlecry", parsed.unsupported_effects)

    def test_hdt_hero_power_readiness_uses_public_tags_not_affordability(self) -> None:
        raw = advisor_snapshot()
        power = raw["player"]["hero_power"]
        power.update(
            {
                "is_playable_card": False,
                "is_exhausted": False,
                "tags": {"HAS_ACTIVATE_POWER": 1, "EXHAUSTED": 0},
            }
        )
        raw["player"]["resources"]["available"] = 2
        parsed = GameState.from_dict(raw)
        self.assertTrue(parsed.friendly.hero_power_available)

        negative_cases = {
            "exhausted": {"power": {"is_exhausted": True, "tags": {"HAS_ACTIVATE_POWER": 1, "EXHAUSTED": 1}}},
            "no_activate_tag": {"power": {"is_exhausted": False, "tags": {"EXHAUSTED": 0}}},
            "disabled": {"player_tags": {"HERO_POWER_DISABLED": 1}},
        }
        for label, changes in negative_cases.items():
            candidate = copy.deepcopy(raw)
            candidate_power = candidate["player"]["hero_power"]
            candidate_power.update(changes.get("power", {}))
            if "player_tags" in changes:
                candidate["player"]["player_entity"]["tags"] = changes["player_tags"]
            if "mana" in changes:
                candidate["player"]["resources"]["available"] = changes["mana"]
            with self.subTest(label=label):
                self.assertFalse(GameState.from_dict(candidate).friendly.hero_power_available)

        unaffordable = copy.deepcopy(raw)
        unaffordable["player"]["resources"]["available"] = 1
        parsed_unaffordable = GameState.from_dict(unaffordable)
        self.assertTrue(parsed_unaffordable.friendly.hero_power_available)
        self.assertGreater(
            parsed_unaffordable.friendly.hero_power.cost,
            parsed_unaffordable.friendly.mana,
        )

    def test_hdt_snapshot_adapter_preserves_visible_combat_state(self) -> None:
        raw = advisor_snapshot()
        minion = advisor_entity(
            40,
            "VISIBLE_COMBAT",
            "MINION",
            name="Visible combat minion",
            attack=4,
            health=5,
            text="",
            playable=False,
        )
        minion.update(
            {
                "zone": "PLAY",
                "is_exhausted": False,
                "has_windfury": True,
                "has_rush": True,
                "has_charge": True,
                "has_reborn": True,
                "is_dormant": False,
                "tags": {"NUM_ATTACKS_THIS_TURN": 1, "NUM_TURNS_IN_PLAY": 0},
            }
        )
        weapon = advisor_entity(
            41,
            "VISIBLE_WEAPON",
            "WEAPON",
            name="Visible weapon",
            attack=3,
            text="",
            playable=False,
        )
        weapon.update({"durability": 2, "damage": 1, "has_windfury": True})
        raw["player"]["hero"].update(
            {
                "attack": 3,
                "is_exhausted": False,
                "tags": {"NUM_ATTACKS_THIS_TURN": 1, "EXHAUSTED": 0, "FROZEN": 0},
            }
        )
        raw["player"]["board"] = [minion]
        raw["player"]["weapon"] = weapon

        parsed = GameState.from_dict(raw)
        card = parsed.friendly.board[0]
        self.assertEqual(1, card.attacks_remaining)
        self.assertTrue(card.can_attack)
        self.assertTrue(card.windfury)
        self.assertTrue(card.rush)
        self.assertTrue(card.charge)
        self.assertTrue(card.reborn)
        self.assertTrue(card.summoned_this_turn)
        self.assertEqual(2, parsed.friendly.weapon.durability)
        self.assertEqual(1, parsed.friendly.weapon.current_durability)
        self.assertTrue(parsed.friendly.hero.can_attack)
        self.assertEqual(1, parsed.friendly.hero.attacks_remaining)

    def test_hdt_lifesteal_uses_boolean_or_named_or_numeric_tag_evidence(self) -> None:
        evidence_cases = (
            (True, {}, True),
            (False, {"LIFESTEAL": 1}, True),
            (False, {"685": "1"}, True),
            (False, {"LIFESTEAL": 0}, False),
            (None, {}, False),
        )
        for has_lifesteal, tags, expected in evidence_cases:
            raw = advisor_snapshot()
            card = advisor_entity(
                20,
                "CORE_ICC_055",
                "SPELL",
                name="Drain Soul",
                cost=2,
                text="<b>Lifesteal</b>\nDeal $3 damage\nto a minion.",
            )
            if has_lifesteal is None:
                card.pop("has_lifesteal")
            else:
                card["has_lifesteal"] = has_lifesteal
            card["tags"] = tags
            raw["player"]["hand"] = [card]
            with self.subTest(has_lifesteal=has_lifesteal, tags=tags):
                self.assertEqual(
                    expected,
                    GameState.from_dict(raw).friendly.hand[0].lifesteal,
                )

    def test_hdt_frozen_state_survives_canonical_round_trip(self) -> None:
        raw = advisor_snapshot()
        frozen = advisor_entity(
            40,
            "FROZEN_TARGET",
            "MINION",
            name="Frozen target",
            attack=4,
            health=5,
            text="",
            playable=False,
        )
        frozen.update(
            {
                "zone": "PLAY",
                "is_exhausted": False,
                "is_frozen": True,
                "tags": {"NUM_ATTACKS_THIS_TURN": 0, "NUM_TURNS_IN_PLAY": 1},
            }
        )
        raw["player"]["board"] = [frozen]

        parsed = GameState.from_dict(raw)
        self.assertTrue(parsed.friendly.board[0].frozen)
        self.assertFalse(parsed.friendly.board[0].can_attack)
        reparsed = GameState.from_dict(parsed.to_dict())
        self.assertTrue(reparsed.friendly.board[0].frozen)
        self.assertFalse(reparsed.friendly.board[0].can_attack)

    def test_hdt_snapshot_adapter_preserves_format_separately_from_game_mode(self) -> None:
        raw = advisor_snapshot()
        raw["format"] = "STANDARD"
        raw["format_type"] = "FT_STANDARD"
        raw["game_mode"] = "CASUAL"
        raw["game_type"] = "GT_CASUAL"

        parsed = GameState.from_dict(raw)
        self.assertEqual("CASUAL", parsed.mode)
        self.assertEqual("STANDARD", parsed.metadata["format"])
        self.assertEqual("FT_STANDARD", parsed.metadata["format_type"])
        self.assertEqual("CASUAL", parsed.metadata["game_mode"])
        self.assertEqual("GT_CASUAL", parsed.metadata["game_type"])

    def test_hdt_snapshot_adapter_redacts_hidden_opponent_hand(self) -> None:
        raw = advisor_snapshot()
        hidden = advisor_entity(
            99,
            "SECRET_INTERNAL_CARD",
            "SPELL",
            name="Secret internal name",
            cost=9,
            text="Secret internal text",
        )
        hidden.update(
            {
                "zone": "HAND",
                "zone_id": 3,
                "zone_position": 2,
                "controller_id": 2,
                "visibility": "hidden",
                "tags": {"ZONE": 3, "ZONE_POSITION": 2, "CONTROLLER": 2, "COST": 9},
            }
        )
        raw["opponent"]["hand"] = [hidden]

        parsed = GameState.from_dict(raw)
        card = parsed.opponent.hand[0]
        self.assertEqual("99", card.entity_id)
        self.assertEqual("UNKNOWN", card.card_id)
        self.assertEqual("Unknown card", card.name)
        self.assertEqual("UNKNOWN", card.card_type.value)
        self.assertEqual(0, card.cost)
        self.assertFalse(card.playable)
        self.assertEqual({"ZONE": 3, "ZONE_POSITION": 2, "CONTROLLER": 2}, card.tags)
        self.assertNotIn("card_text_not_parsed", card.unsupported_effects)

    def test_hdt_options_aliases_and_seed(self) -> None:
        request = {
            "api_version": "1.0",
            "request_id": "hdt-request",
            "state": advisor_snapshot(),
            "options": {
                "max_recommendations": 2,
                "initial_budget_milliseconds": 25,
                "time_budget_milliseconds": 40,
                "search_seed": 99,
                "allow_approximate_effects": True,
                "environment_version": "snapshot-v2",
            },
        }
        parsed = SolveRequest.from_dict(request)
        self.assertEqual(40, parsed.options.time_budget_ms)
        self.assertEqual(2, parsed.options.top_k)
        self.assertEqual(99, parsed.state.rng_seed)
        self.assertEqual("snapshot-v2", parsed.state.metadata["environment_version"])

    def test_solve_request_metadata_round_trip(self) -> None:
        request = native_request_dict()
        request["metadata"] = {
            "trajectory_schema": "trajectory-readiness-v1",
            "decision_id": "decision-1",
            "solve_stage": "final",
            "snapshot_sequence": 7,
        }
        parsed = SolveRequest.from_dict(request)
        self.assertEqual(request["metadata"], parsed.metadata)
        self.assertEqual(request["metadata"], parsed.to_dict()["metadata"])

    def test_solve_request_hdt_root_candidates_round_trip(self) -> None:
        request = native_request_dict()
        request["hdt_root_candidates"] = _hdt_root_candidates("state-1")

        parsed = SolveRequest.from_dict(request)
        serialized = parsed.to_dict()
        reparsed = SolveRequest.from_dict(serialized)

        self.assertEqual(request["hdt_root_candidates"], parsed.hdt_root_candidates)
        self.assertEqual(parsed.hdt_root_candidates, serialized["hdt_root_candidates"])
        self.assertEqual(parsed.hdt_root_candidates, reparsed.hdt_root_candidates)

    def test_solve_request_preserves_legacy_fifth_positional_api_version(self) -> None:
        legacy = SolveRequest(
            "legacy-request",
            state(),
            SolveOptions(top_k=2),
            {"caller": "legacy-positional"},
            "1.0",
        )

        self.assertEqual("1.0", legacy.api_version)
        self.assertIsNone(legacy.hdt_root_candidates)
        self.assertNotIn("hdt_root_candidates", legacy.to_dict())

    def test_observation_discriminator(self) -> None:
        action = Observation.from_dict(
            {
                "api_version": "1.0",
                "kind": "action",
                "state_id": "s1",
                "action": {"kind": "attack", "source_entity_id": 10, "target_entity_id": 20},
            }
        )
        self.assertEqual("10", action.action.source_entity_id)
        with self.assertRaises(SchemaError):
            Observation.from_dict(
                {"api_version": "1.0", "kind": "result", "state_id": "s1", "result": "maybe"}
            )

    def test_observation_rejects_unknown_fields_and_invalid_wall_clock(self) -> None:
        with self.assertRaises(SchemaError):
            Observation.from_dict(
                {
                    "api_version": "1.0",
                    "kind": "result",
                    "state_id": "s1",
                    "result": "win",
                    "unexpected": True,
                }
            )
        with self.assertRaises(SchemaError):
            Observation.from_dict(
                {
                    "api_version": "1.0",
                    "kind": "result",
                    "state_id": "s1",
                    "result": "win",
                    "observed_at_utc": "not-a-timestamp",
                }
            )
        parsed = Observation.from_dict(
            {
                "api_version": "1.0",
                "kind": "result",
                "state_id": "s1",
                "result": "win",
                "observed_at_utc": "2026-07-29T12:34:56Z",
            }
        )
        self.assertEqual("2026-07-29T12:34:56Z", parsed.observed_at_utc)

    def test_unverified_post_state_candidate_has_a_strict_fail_closed_schema(self) -> None:
        pre_state = state().to_dict()
        pre_state["state_id"] = "state-pre"
        pre_state["metadata"] = {
            "game_id": "",
            "snapshot_state_hash": "a" * 64,
            "snapshot_sequence": 10,
        }
        post_state = state().to_dict()
        post_state["state_id"] = "state-post"
        post_state["metadata"] = {
            "game_id": "",
            "snapshot_state_hash": "b" * 64,
            "snapshot_sequence": 11,
        }
        parsed = Observation.from_dict(
            {
                "api_version": "1.0",
                "kind": "action",
                "state_id": "state-pre",
                "action": {"kind": "end_turn"},
                "pre_state": pre_state,
                "post_state": post_state,
                "metadata": {
                    "trajectory_schema": "trajectory-readiness-v1",
                    "action_sequence": "1",
                    "pre_state_id": "state-pre",
                    "post_state_id": "state-post",
                    "raw_pre_snapshot_hash": "a" * 64,
                    "raw_post_snapshot_hash": "b" * 64,
                    "pre_snapshot_sequence": "10",
                    "post_snapshot_sequence": "11",
                    "boundary_status": "isolated",
                    "intervening_action_count": "0",
                    "capture_warning_count": "0",
                    "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
                    "transition_status": TRANSITION_CANDIDATE_STATUS,
                    "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
                    "completeness": "partial_hdt_gameevents_v1",
                    "training_eligible": "false",
                },
            }
        )
        self.assertEqual("state-post", parsed.metadata["post_state_id"])
        self.assertEqual(
            TRANSITION_CANDIDATE_VERIFICATION,
            parsed.metadata["transition_verification"],
        )

    def test_observation_hdt_root_candidates_round_trip(self) -> None:
        pre_state = state().to_dict()
        pre_state["state_id"] = "state-pre"
        pre_state["metadata"] = {
            "game_id": "",
            "snapshot_state_hash": "a" * 64,
            "snapshot_sequence": 10,
        }
        post_state = state().to_dict()
        post_state["state_id"] = "state-post"
        post_state["metadata"] = {
            "game_id": "",
            "snapshot_state_hash": "b" * 64,
            "snapshot_sequence": 11,
        }
        payload = {
            "api_version": "1.0",
            "kind": "action",
            "state_id": "state-pre",
            "action": {
                "kind": "end_turn",
                "hdt_root_candidates": _hdt_root_candidates("state-pre"),
            },
            "pre_state": pre_state,
            "post_state": post_state,
            "metadata": {
                "action_sequence": 1,
                "pre_state_id": "state-pre",
                "post_state_id": "state-post",
                "raw_pre_snapshot_hash": "a" * 64,
                "raw_post_snapshot_hash": "b" * 64,
                "pre_snapshot_sequence": 10,
                "post_snapshot_sequence": 11,
                "boundary_status": "isolated",
                "intervening_action_count": 0,
                "capture_warning_count": 0,
                "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
                "transition_status": TRANSITION_CANDIDATE_STATUS,
                "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
                "completeness": "partial_hdt_gameevents_v1",
                "training_eligible": False,
            },
        }

        parsed = Observation.from_dict(payload)
        serialized = parsed.to_dict()
        reparsed = Observation.from_dict(serialized)

        self.assertEqual(
            payload["action"]["hdt_root_candidates"],
            parsed.action_evidence["hdt_root_candidates"],
        )
        self.assertEqual(
            parsed.action_evidence["hdt_root_candidates"],
            reparsed.action_evidence["hdt_root_candidates"],
        )

    def test_unverified_post_state_candidate_rejects_eligibility_and_bad_boundaries(self) -> None:
        pre_state = state().to_dict()
        pre_state["state_id"] = "state-pre"
        pre_state["metadata"] = {
            "game_id": "",
            "snapshot_state_hash": "a" * 64,
            "snapshot_sequence": 10,
        }
        post_state = state().to_dict()
        post_state["state_id"] = "state-post"
        post_state["metadata"] = {
            "game_id": "",
            "snapshot_state_hash": "b" * 64,
            "snapshot_sequence": 11,
        }
        base = {
            "api_version": "1.0",
            "kind": "action",
            "state_id": "state-pre",
            "action": {"kind": "end_turn"},
            "pre_state": pre_state,
            "post_state": post_state,
            "metadata": {
                "action_sequence": 1,
                "pre_state_id": "state-pre",
                "post_state_id": "state-post",
                "raw_pre_snapshot_hash": "a" * 64,
                "raw_post_snapshot_hash": "b" * 64,
                "pre_snapshot_sequence": 10,
                "post_snapshot_sequence": 11,
                "boundary_status": "isolated",
                "intervening_action_count": 0,
                "capture_warning_count": 0,
                "capture_contract": TRANSITION_CANDIDATE_CAPTURE_CONTRACT,
                "transition_status": TRANSITION_CANDIDATE_STATUS,
                "transition_verification": TRANSITION_CANDIDATE_VERIFICATION,
                "completeness": "partial_hdt_gameevents_v1",
                "training_eligible": False,
            },
        }
        for field, value in (
            ("training_eligible", True),
            ("post_snapshot_sequence", 10),
            ("raw_pre_snapshot_hash", "not-a-hash"),
            ("intervening_action_count", 1),
        ):
            payload = {
                **base,
                "metadata": {**base["metadata"], field: value},
            }
            with self.subTest(field=field), self.assertRaises(SchemaError):
                Observation.from_dict(payload)

    def test_unknown_option_is_rejected(self) -> None:
        raw = native_request_dict()
        raw["options"]["magic"] = True
        with self.assertRaises(SchemaError):
            SolveRequest.from_dict(raw)


if __name__ == "__main__":
    unittest.main()
