from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.card_rules import (
    CardRuleError,
    StructuredCardRuleBundle,
    default_structured_card_rule_path,
    normalize_card_text,
    normalized_text_sha256,
)
from metacompanion_solver.config import SolverConfig
from metacompanion_solver.logging_store import JsonlTrainingLogger
from metacompanion_solver.schemas import Action, ActionKind, GameState, SolveRequest
from metacompanion_solver.service import SolverService
from metacompanion_solver.simulator import (
    SUPPORTED_EFFECTS,
    apply_action,
    enumerate_legal_actions,
    scan_state_coverage,
)
from metacompanion_solver.turnpair_evaluation import prove_turnpair

from helpers import advisor_entity, advisor_snapshot


def _clean_snapshot() -> dict:
    raw = advisor_snapshot()
    raw["unknown_data"] = []
    raw["unsupported_features"] = []
    raw["game_id"] = "game-card-rules"
    raw["player"]["deck_count"] = 0
    raw["opponent"]["deck_count"] = 0
    raw["player"]["hero_power"] = None
    raw["player"]["hand"] = []
    return raw


def _request(snapshot: dict, request_id: str = "card-rule-request") -> SolveRequest:
    return SolveRequest.from_dict(
        {
            "api_version": "1.0",
            "request_id": request_id,
            "state": snapshot,
            "options": {
                "time_budget_ms": 250,
                "max_iterations": 1000,
                "max_depth": 8,
                "top_k": 3,
            },
        }
    )


class StructuredCardRuleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.bundle = StructuredCardRuleBundle.load(default_structured_card_rule_path())

    def assert_unverified_best_effort(self, result) -> None:
        self.assertEqual("partial", result.status)
        self.assertFalse(result.coverage["exact"])
        self.assertGreaterEqual(len(result.recommendations), 1)
        counterplay = result.coverage["details"]["counterplay"]
        self.assertFalse(counterplay["portfolio_optimality_proven"])
        self.assertFalse(counterplay["root_action_coverage_complete"])
        self.assertFalse(counterplay["search_complete"])
        self.assertEqual(0, counterplay["response_verified_first_action_count"])
        for recommendation in result.recommendations:
            wire = recommendation.to_dict()
            self.assertFalse(wire["is_proven_lethal"])
            self.assertFalse(wire["is_response_verified"])
            self.assertFalse(wire["response_search_complete"])
            self.assertIsNone(wire["is_safe_after_response"])
            self.assertIsNone(wire["verified_portfolio_regret"])
            self.assertEqual("fallback", wire["alternative_kind"])
            self.assertEqual("", wire["proof_kind"])
            self.assertEqual("", wire["proof_scope"])
            self.assertIn(
                "approximate_playable_unsupported_rule",
                {item["code"] for item in wire["annotations"]},
            )

    def test_bundle_is_curated_versioned_and_hash_bound(self) -> None:
        health = self.bundle.health()
        self.assertTrue(health["available"])
        self.assertEqual("hdt-visible-point-effects-v1", health["ruleset_id"])
        self.assertEqual(47, health["rule_count"])
        self.assertEqual(205, health["registered_card_id_count"])
        self.assertEqual(5, health["context_guarded_rule_count"])
        self.assertEqual(5, health["required_mechanic_guarded_rule_count"])
        self.assertEqual(
            "card_id+normalized_english_text_sha256+card_type"
            "+required_intrinsic_mechanics+declared_context_guards",
            health["matching_contract"],
        )
        self.assertIn("LIFESTEAL(685)", health["intrinsic_mechanic_evidence"])
        self.assertEqual(
            {
                "core-drain-soul-lifesteal-minion-damage-v1",
                "core-void-shard-lifesteal-point-damage-v1",
                "rlk-death-strike-lifesteal-minion-damage-v1",
                "time-queldorei-fletcher-hero-power-cost-aura-v1",
                "tlc-niri-one-cost-card-doubler-v1",
            },
            {
                rule.rule_id
                for rule in self.bundle.rules
                if rule.required_mechanics
            },
        )
        self.assertFalse(health["rules_generated_from_free_text"])
        self.assertEqual(
            "Battlecry: Deal 1 damage.",
            normalize_card_text("[x]<b>Battlecry:</b>  Deal $1 damage."),
        )
        self.assertEqual(
            "c4511dfa5d7f8d7e36e8ae82694389796d7c99a70b99bd3539295a2bde829a0f",
            normalized_text_sha256("Deal $6 damage."),
        )
        self.assertEqual(
            "9f6c17c42cbaebe1e3f8906412c38a2732b41bd75f1795fb7e4b3ca0e78bfbc6",
            normalized_text_sha256(
                "Hero Power\nDeal $2 damage to the enemy hero."
                "Hero Power\nDeal $2 damage."
            ),
        )

    def test_queldorei_fletcher_binds_a_structured_hand_count_cost_aura(self) -> None:
        raw = {
            "state_id": "aura-state",
            "turn": 1,
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": {
                "player_id": "friendly",
                "hero": {
                    "entity_id": "friendly-hero",
                    "card_id": "HERO",
                    "card_type": "HERO",
                    "health": 30,
                },
                "board": [
                    {
                        "entity_id": "aura",
                        "card_id": "TIME_606",
                        "card_type": "MINION",
                        "health": 3,
                        "card_text": (
                            "Your Hero Power costs (0) while your hand has "
                            "3 or less cards."
                        ),
                        "tags": {"AURA": 1},
                        "effect_coverage": "unsupported",
                        "unsupported_effects": ["card_text_not_parsed"],
                    }
                ],
            },
            "opponent": {
                "player_id": "opponent",
                "hero": {
                    "entity_id": "opponent-hero",
                    "card_id": "OPP_HERO",
                    "card_type": "HERO",
                    "health": 30,
                },
            },
        }
        state = GameState.from_dict(raw)
        assessment = self.bundle.apply(state)

        self.assertEqual(1, assessment["matched_entity_count"])
        effect = state.friendly.board[0].effects[0]
        self.assertEqual("set_hero_power_cost", effect.kind)
        self.assertEqual(0, effect.amount)
        self.assertEqual(3, effect.hand_count_at_most)
        self.assertEqual("exact", state.friendly.board[0].effect_coverage)

        missing_aura = copy.deepcopy(raw)
        missing_aura["friendly"]["board"][0]["tags"] = {}
        missing_state = GameState.from_dict(missing_aura)
        missing_assessment = self.bundle.apply(missing_state)
        self.assertEqual(0, missing_assessment["matched_entity_count"])
        self.assertEqual(
            "required_mechanic_unproven",
            missing_assessment["mismatches"][0]["reason"],
        )

    def test_underbelly_network_activation_is_useful_but_not_false_exact(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["board"] = [
            advisor_entity(
                40,
                "JAIL_877",
                "LOCATION",
                name="Underbelly Network",
                cost=2,
                health=2,
                text='Summon a 2/1 Rat with "Deathrattle: Draw a card."',
                playable=False,
            )
        ]
        state = GameState.from_dict(raw)
        assessment = self.bundle.apply(state)
        self.assertEqual(
            ["jail-underbelly-network-location-v1"],
            [item["rule_id"] for item in assessment["matched"]],
        )
        self.assertTrue(
            any(
                item.code == "unsupported_summoned_card_text"
                for item in scan_state_coverage(state)
            )
        )
        action = Action(
            ActionKind.LOCATION_ACTIVATE,
            "40",
            "",
            "JAIL_877",
        )
        outcome = apply_action(state, action, validate=False)
        location = next(
            card for card in outcome.state.friendly.board if card.entity_id == "40"
        )
        rat = next(
            card
            for card in outcome.state.friendly.board
            if card.card_id == "JAIL_877t"
        )
        self.assertEqual(1, location.current_health)
        self.assertEqual((2, 1), (rat.attack, rat.current_health))
        self.assertEqual("unsupported", rat.effect_coverage)
        self.assertIn("summoned_card_text_not_modeled", rat.unsupported_effects)
        proof = prove_turnpair(state, allow_point_effects=True)
        self.assertTrue(proof.abstained)
        self.assertTrue(
            any("unsupported card effects" in reason for reason in proof.reasons)
        )

    def test_loader_rejects_a_tampered_text_hash(self) -> None:
        raw = json.loads(default_structured_card_rule_path().read_text(encoding="utf-8"))
        raw["rules"][0]["accepted_texts"][0]["sha256"] = "0" * 64
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "rules.json"
            path.write_text(json.dumps(raw), encoding="utf-8")
            with self.assertRaisesRegex(CardRuleError, "sha256 mismatch"):
                StructuredCardRuleBundle.load(path)

    def test_loader_rejects_unknown_or_resolved_required_mechanics(self) -> None:
        original = json.loads(
            default_structured_card_rule_path().read_text(encoding="utf-8")
        )
        drain = next(
            item
            for item in original["rules"]
            if item["card_ids"] == ["CORE_ICC_055"]
        )
        for required, resolved, message in (
            (["windfury"], [], "unsupported mechanics"),
            (["lifesteal"], ["lifesteal"], "must not resolve"),
        ):
            raw = copy.deepcopy(original)
            candidate = next(
                item
                for item in raw["rules"]
                if item["rule_id"] == drain["rule_id"]
            )
            candidate["required_mechanics"] = required
            candidate["resolved_mechanics"] = resolved
            with self.subTest(required=required, resolved=resolved), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "rules.json"
                path.write_text(json.dumps(raw), encoding="utf-8")
                with self.assertRaisesRegex(CardRuleError, message):
                    StructuredCardRuleBundle.load(path)

    def test_loader_strictly_validates_new_structured_effect_shapes(self) -> None:
        original = json.loads(
            default_structured_card_rule_path().read_text(encoding="utf-8")
        )
        mutations = (
            (
                "draw_non_starting_spell_on_weapon_break",
                lambda effect: effect.pop("target"),
                "exactly the reviewed fields",
            ),
            (
                "damage_split",
                lambda effect: effect.update(random=False),
                "outside reviewed visible-effect-v1",
            ),
            (
                "shuffle_repeat_spell",
                lambda effect: effect.update(count=11),
                "outside reviewed visible-effect-v1",
            ),
            (
                "replay_one_cost_cards",
                lambda effect: effect.update(card_id="NOT_ALLOWED"),
                "exactly the reviewed fields",
            ),
        )
        for kind, mutate, message in mutations:
            raw = copy.deepcopy(original)
            candidate = next(
                effect
                for rule in raw["rules"]
                for effect in rule["effects"]
                if effect["kind"] == kind
            )
            mutate(candidate)
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "rules.json"
                path.write_text(json.dumps(raw), encoding="utf-8")
                with self.assertRaisesRegex(CardRuleError, message):
                    StructuredCardRuleBundle.load(path)

    def test_new_effects_get_unverified_legacy_python_best_effort_routes(self) -> None:
        new_effect_kinds = {
            "draw_non_starting_spell_on_weapon_break",
            "damage_split",
            "shuffle_repeat_spell",
            "replay_one_cost_cards",
        }
        loaded_kinds = {
            effect.kind
            for rule in self.bundle.rules
            for effect in rule.effects
            if effect.kind in new_effect_kinds
        }
        self.assertEqual(new_effect_kinds, loaded_kinds)
        self.assertTrue(new_effect_kinds.isdisjoint(SUPPORTED_EFFECTS))

        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 1, "total": 1})
        raw["player"]["hand"] = [
            advisor_entity(
                20,
                "JAIL_881",
                "SPELL",
                name="Arcane Tripwire",
                cost=1,
                text=(
                    "Deal 5 damage split among all enemies. "
                    "Shuffle 2 spells into your deck that do it again when drawn."
                ),
            )
        ]
        state = GameState.from_dict(raw)
        assessment = self.bundle.apply(state)
        self.assertEqual(1, assessment["matched_entity_count"])
        card = state.friendly.hand[0]
        self.assertEqual("unsupported", card.effect_coverage)
        self.assertEqual(
            {
                "legacy_python_simulator_unimplemented:damage_split",
                "legacy_python_simulator_unimplemented:shuffle_repeat_spell",
            },
            set(card.unsupported_effects),
        )
        self.assertTrue(
            any(item.code == "unsupported_effect" for item in scan_state_coverage(state))
        )

        service = SolverService(
            SolverConfig(training_log_path=None), logger=JsonlTrainingLogger(None)
        )
        result = service.solve(_request(raw, "legacy-special-effect-fail-closed"))
        self.assert_unverified_best_effort(result)

    def test_real_hdt_fireball_becomes_targeted_lethal_with_provenance(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"]["available"] = 4
        raw["player"]["resources"]["total"] = 4
        raw["player"]["hand"] = [
            advisor_entity(
                20,
                "CORE_CS2_029",
                "SPELL",
                name="Fireball",
                cost=4,
                text="Deal $6 damage.",
            )
        ]
        raw["opponent"]["hero"]["health"] = 6

        service = SolverService(SolverConfig(training_log_path=None), logger=JsonlTrainingLogger(None))
        result = service.solve(_request(raw))

        self.assertEqual("partial", result.status)
        self.assertTrue(result.recommendations[0].is_proven_lethal)
        self.assertEqual("play_card:20:30", result.recommendations[0].actions[0].action_id)
        rules = result.coverage["structured_card_rules"]
        self.assertEqual(1, rules["matched_entity_count"])
        self.assertEqual(0, rules["mismatch_entity_count"])
        self.assertEqual("core-fireball-point-damage-v1", rules["matched"][0]["rule_id"])

    def test_spell_power_is_preserved_and_applied_only_to_spells(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 4, "total": 4, "spell_power": 1})
        raw["player"]["hand"] = [
            advisor_entity(
                20,
                "CORE_CS2_029",
                "SPELL",
                name="Fireball",
                cost=4,
                text="Deal 6 damage.",
            )
        ]
        raw["opponent"]["hero"]["health"] = 7
        state = GameState.from_dict(raw)
        self.bundle.apply(state)
        self.assertEqual(1, state.friendly.spell_power)
        action = next(
            item
            for item in enumerate_legal_actions(state)
            if item.kind == ActionKind.PLAY_CARD and item.target_entity_id == "30"
        )
        outcome = apply_action(state, action)
        self.assertEqual(0, outcome.state.opponent.hero.current_health)

    def test_fireblast_alias_gets_targets_and_deals_damage(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 2, "total": 2})
        raw["player"]["hero_power"] = advisor_entity(
            11,
            "HERO_08bp",
            "HERO_POWER",
            name="Fireblast",
            cost=2,
            text="<b>Hero Power</b>\nDeal $1 damage.",
        )
        raw["player"]["hero_power"]["is_exhausted"] = False
        raw["player"]["hero_power"]["is_playable_card"] = False
        raw["player"]["hero_power"]["tags"] = {
            "HAS_ACTIVATE_POWER": 1,
            "EXHAUSTED": 0,
        }
        raw["player"]["player_entity"]["tags"] = {
            "CURRENT_HEROPOWER_DAMAGE_BONUS": 0,
            "HERO_POWER_DOUBLE": 0,
            "HEROPOWER_DAMAGE": 0,
        }
        raw["opponent"]["hero"]["health"] = 1
        state = GameState.from_dict(raw)
        assessment = self.bundle.apply(state)
        actions = [
            item
            for item in enumerate_legal_actions(state)
            if item.kind == ActionKind.HERO_POWER
        ]
        self.assertEqual({"10", "30"}, {item.target_entity_id for item in actions})
        lethal = next(item for item in actions if item.target_entity_id == "30")
        self.assertEqual(0, apply_action(state, lethal).state.opponent.hero.current_health)
        self.assertEqual(1, assessment["matched_entity_count"])

    def test_fireblast_abstains_for_damage_modifiers_or_missing_owner_tags(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 2, "total": 2})
        raw["player"]["hero_power"] = advisor_entity(
            11,
            "HERO_08bp",
            "HERO_POWER",
            name="Fireblast",
            cost=2,
            text="<b>Hero Power</b>\nDeal $1 damage.",
            playable=False,
        )
        raw["player"]["hero_power"].update(
            {
                "is_exhausted": False,
                "tags": {"HAS_ACTIVATE_POWER": 1, "EXHAUSTED": 0},
            }
        )

        for tag in (
            "CURRENT_HEROPOWER_DAMAGE_BONUS",
            "HERO_POWER_DOUBLE",
            "HEROPOWER_DAMAGE",
        ):
            modified = copy.deepcopy(raw)
            modified["player"]["player_entity"]["tags"] = {
                "CURRENT_HEROPOWER_DAMAGE_BONUS": 0,
                "HERO_POWER_DOUBLE": 0,
                "HEROPOWER_DAMAGE": 0,
                tag: 1,
            }
            assessment = self.bundle.apply(GameState.from_dict(modified))
            self.assertEqual(0, assessment["matched_entity_count"], tag)
            self.assertEqual("context_tag_active", assessment["mismatches"][0]["reason"], tag)

        missing = copy.deepcopy(raw)
        missing["player"].pop("player_entity")
        missing_assessment = self.bundle.apply(GameState.from_dict(missing))
        self.assertEqual(0, missing_assessment["matched_entity_count"])
        self.assertEqual(
            "owner_public_rule_tags_unavailable",
            missing_assessment["mismatches"][0]["reason"],
        )

    def test_armor_up_and_demon_claws_abstain_when_hero_power_is_doubled(self) -> None:
        for card_id, name, cost, card_text in (
            (
                "HERO_01dbp",
                "Armor Up!",
                2,
                "<b>Hero Power</b>\nGain $d2 Armor.",
            ),
            (
                "HERO_10cbp",
                "Demon Claws",
                1,
                "[x]<b>Hero Power</b>\n+$a1 Attack this turn.",
            ),
        ):
            with self.subTest(card_id=card_id):
                raw = _clean_snapshot()
                raw["player"]["resources"].update(
                    {"available": cost, "total": cost}
                )
                raw["player"]["hero_power"] = advisor_entity(
                    11,
                    card_id,
                    "HERO_POWER",
                    name=name,
                    cost=cost,
                    text=card_text,
                    playable=False,
                )
                raw["player"]["hero_power"].update(
                    {
                        "is_exhausted": False,
                        "tags": {"HAS_ACTIVATE_POWER": 1, "EXHAUSTED": 0},
                    }
                )
                raw["player"]["player_entity"]["tags"] = {
                    "HERO_POWER_DOUBLE": 1
                }

                assessment = self.bundle.apply(GameState.from_dict(raw))

                self.assertEqual(0, assessment["matched_entity_count"])
                self.assertEqual(1, assessment["mismatch_entity_count"])
                self.assertEqual(
                    "context_tag_active",
                    assessment["mismatches"][0]["reason"],
                )

    def test_temporary_hero_attack_requires_public_attack_history(self) -> None:
        cases = (
            (
                "demon-claws",
                "hero_power:11:",
                advisor_entity(
                    11,
                    "HERO_10cbp",
                    "HERO_POWER",
                    name="Demon Claws",
                    cost=1,
                    text="[x]<b>Hero Power</b>\n+$a1 Attack this turn.",
                    playable=False,
                ),
            ),
            (
                "static-shock",
                "play_card:20:40",
                advisor_entity(
                    20,
                    "TIME_218",
                    "SPELL",
                    name="Static Shock",
                    cost=1,
                    text="Deal $1 damage to a minion. Give your hero +1 Attack this turn.",
                ),
            ),
        )
        for label, enabling_action_id, source in cases:
            for history_known in (True, False):
                with self.subTest(label=label, history_known=history_known):
                    raw = _clean_snapshot()
                    raw["player"]["resources"].update(
                        {"available": 1, "total": 1}
                    )
                    raw["player"]["hero"].update(
                        {
                            "attack": 0,
                            "is_exhausted": False,
                            "tags": {"EXHAUSTED": 0},
                        }
                    )
                    if history_known:
                        raw["player"]["hero"]["tags"][
                            "NUM_ATTACKS_THIS_TURN"
                        ] = 0
                    if label == "demon-claws":
                        raw["player"]["hero_power"] = copy.deepcopy(source)
                        raw["player"]["hero_power"].update(
                            {
                                "is_exhausted": False,
                                "tags": {
                                    "HAS_ACTIVATE_POWER": 1,
                                    "EXHAUSTED": 0,
                                },
                            }
                        )
                        raw["player"]["player_entity"]["tags"] = {
                            "HERO_POWER_DOUBLE": 0
                        }
                    else:
                        raw["player"]["hand"] = [copy.deepcopy(source)]
                        raw["opponent"]["board"] = [
                            advisor_entity(
                                40,
                                "TEST_TAUNT",
                                "MINION",
                                attack=0,
                                health=1,
                            )
                        ]
                        raw["opponent"]["board"][0]["has_taunt"] = True

                    state = GameState.from_dict(raw)
                    assessment = self.bundle.apply(state)
                    self.assertEqual(1, assessment["matched_entity_count"])
                    enabling_action = next(
                        action
                        for action in enumerate_legal_actions(state)
                        if action.action_id == enabling_action_id
                    )
                    enabled = apply_action(state, enabling_action).state
                    hero_attacks = [
                        action
                        for action in enumerate_legal_actions(enabled)
                        if action.action_id == "attack:10:30"
                    ]

                    if not history_known:
                        self.assertFalse(hero_attacks)
                        proof = prove_turnpair(state, allow_point_effects=True)
                        self.assertTrue(proof.abstained)
                        self.assertIn(
                            "1 hero attack history is unavailable",
                            proof.reasons,
                        )
                        continue

                    self.assertEqual(1, len(hero_attacks))
                    attacked = apply_action(enabled, hero_attacks[0]).state
                    self.assertEqual(
                        1,
                        attacked.friendly.hero.tags["NUM_ATTACKS_THIS_TURN"],
                    )

    def test_steady_shot_aliases_are_fixed_enemy_hero_damage_without_spell_power(self) -> None:
        steady_shot = self.bundle.rules_by_card_id["HERO_05dbp"]
        self.assertEqual(23, len(steady_shot.card_ids))
        self.assertIn("HERO_05bp", steady_shot.card_ids)
        self.assertIn("VAN_HERO_05bp", steady_shot.card_ids)
        self.assertNotIn("HERO_05dbp2", self.bundle.rules_by_card_id)

        raw = _clean_snapshot()
        raw["player"]["resources"].update(
            {"available": 2, "total": 2, "spell_power": 5}
        )
        raw["player"]["hero_power"] = advisor_entity(
            11,
            "HERO_05dbp",
            "HERO_POWER",
            name="Steady Shot",
            cost=2,
            text=(
                "Hero Power\nDeal $2 damage to the enemy hero."
                "Hero Power\nDeal $2 damage."
            ),
        )
        raw["player"]["hero_power"]["is_exhausted"] = False
        raw["player"]["hero_power"]["is_playable_card"] = False
        raw["player"]["hero_power"]["tags"] = {
            "HAS_ACTIVATE_POWER": 1,
            "EXHAUSTED": 0,
        }
        raw["opponent"]["hero"]["health"] = 3
        state = GameState.from_dict(raw)

        assessment = self.bundle.apply(state)
        actions = [
            item
            for item in enumerate_legal_actions(state)
            if item.kind == ActionKind.HERO_POWER
        ]

        self.assertEqual(["hero_power:11:30"], [item.action_id for item in actions])
        outcome = apply_action(state, actions[0])
        self.assertEqual(1, outcome.state.opponent.hero.current_health)
        self.assertEqual(1, assessment["matched_entity_count"])
        self.assertEqual("hunter-steady-shot-point-damage-v1", state.friendly.hero_power.rule_id)
        self.assertEqual("exact", state.friendly.hero_power.effect_coverage)
        self.assertEqual((), state.friendly.hero_power.unsupported_effects)

    def test_steady_shot_context_mismatch_gets_unverified_best_effort_routes(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 2, "total": 2})
        raw["player"]["hero_power"] = advisor_entity(
            11,
            "HERO_05dbp",
            "HERO_POWER",
            name="Steady Shot",
            cost=2,
            text=(
                "Hero Power\nDeal $2 damage to the enemy hero."
                "Hero Power\nDeal $2 damage."
            ),
            playable=False,
        )
        raw["player"]["hero_power"].update(
            {
                "is_exhausted": False,
                "tags": {"HAS_ACTIVATE_POWER": 1, "EXHAUSTED": 0},
            }
        )

        modified = copy.deepcopy(raw)
        modified["player"]["player_entity"]["tags"] = {
            "STEADY_SHOT_CAN_TARGET": 1
        }
        modified_state = GameState.from_dict(modified)
        modified_assessment = self.bundle.apply(modified_state)
        self.assertEqual(0, modified_assessment["matched_entity_count"])
        self.assertEqual("context_tag_active", modified_assessment["mismatches"][0]["reason"])

        missing = copy.deepcopy(raw)
        missing["player"].pop("player_entity")
        missing_state = GameState.from_dict(missing)
        missing_assessment = self.bundle.apply(missing_state)
        self.assertEqual(0, missing_assessment["matched_entity_count"])
        self.assertEqual(
            "owner_public_rule_tags_unavailable",
            missing_assessment["mismatches"][0]["reason"],
        )

        service = SolverService(SolverConfig(training_log_path=None), logger=JsonlTrainingLogger(None))
        result = service.solve(_request(modified, "steady-shot-target-modified"))
        self.assert_unverified_best_effort(result)

    def test_battlecry_rule_removes_only_the_explicitly_resolved_mechanic(self) -> None:
        raw = _clean_snapshot()
        archer = advisor_entity(
            20,
            "CORE_CS2_189",
            "MINION",
            name="Elven Archer",
            cost=1,
            attack=1,
            health=1,
            text="<b>Battlecry:</b> Deal $1 damage.",
        )
        archer["mechanics"] = ["BATTLECRY"]
        raw["player"]["hand"] = [archer]
        state = GameState.from_dict(raw)
        self.bundle.apply(state)
        card = state.friendly.hand[0]
        self.assertEqual("exact", card.effect_coverage)
        self.assertEqual((), card.unsupported_effects)
        self.assertEqual("core-elven-archer-battlecry-damage-v1", card.rule_id)

    def test_new_high_frequency_rules_compose_visible_effects_and_gate_card_type(self) -> None:
        wyrm_raw = _clean_snapshot()
        wyrm_raw["player"]["resources"].update({"available": 5, "total": 5})
        wyrm_raw["player"]["hero"]["armor"] = 1
        wyrm = advisor_entity(
            20,
            "TLC_600",
            "MINION",
            name="Windpeak Wyrm",
            cost=5,
            attack=6,
            health=6,
            text=(
                "[x]<b>Battlecry:</b> Deal 5 damage\n"
                "and gain 5 Armor.\n"
                "<b>Kindred:</b> Costs (3) less."
            ),
        )
        wyrm["mechanics"] = ["BATTLECRY", "KINDRED"]
        wyrm_raw["player"]["hand"] = [wyrm]
        wyrm_raw["opponent"]["board"] = [
            advisor_entity(40, "VISIBLE_TARGET", "MINION", attack=2, health=5)
        ]
        wyrm_state = GameState.from_dict(wyrm_raw)
        wyrm_assessment = self.bundle.apply(wyrm_state)

        self.assertEqual(
            ["tlc-windpeak-wyrm-battlecry-v1"],
            [item["rule_id"] for item in wyrm_assessment["matched"]],
        )
        self.assertEqual(5, wyrm_state.friendly.hand[0].cost)
        self.assertEqual((), wyrm_state.friendly.hand[0].unsupported_effects)
        wyrm_action = next(
            action
            for action in enumerate_legal_actions(wyrm_state)
            if action.action_id == "play_card:20:40:position=1"
        )
        after_wyrm = apply_action(wyrm_state, wyrm_action).state
        self.assertEqual(6, after_wyrm.friendly.armor)
        self.assertEqual("TLC_600", after_wyrm.friendly.board[0].card_id)
        self.assertFalse(after_wyrm.opponent.board)

        spell_raw = _clean_snapshot()
        spell_raw["player"]["resources"].update(
            {"available": 3, "total": 3, "spell_power": 1}
        )
        spell_raw["player"]["hand"] = [
            advisor_entity(
                20,
                "JAIL_801",
                "SPELL",
                name="Molten Gold",
                cost=3,
                text=(
                    "Deal $4 damage.\n"
                    "<i>(Cast @ |4(spell, spells) to turn into a minion!)</i>"
                ),
            )
        ]
        spell_raw["opponent"]["hero"].update({"health": 5, "damage": 0})
        spell_state = GameState.from_dict(spell_raw)
        spell_assessment = self.bundle.apply(spell_state)
        self.assertEqual(
            ["jail-molten-gold-spell-v1"],
            [item["rule_id"] for item in spell_assessment["matched"]],
        )
        spell_action = next(
            action
            for action in enumerate_legal_actions(spell_state)
            if action.action_id == "play_card:20:30"
        )
        after_spell = apply_action(spell_state, spell_action).state
        self.assertEqual(0, after_spell.opponent.hero.current_health)
        self.assertFalse(after_spell.friendly.hand)
        self.assertFalse(after_spell.friendly.board)

        transformed_raw = copy.deepcopy(spell_raw)
        transformed_raw["player"]["resources"].update(
            {"available": 1, "total": 1, "spell_power": 0}
        )
        transformed_raw["player"]["hand"][0].update(
            {"card_type": "MINION", "cost": 1, "attack": 3, "health": 3}
        )
        transformed_state = GameState.from_dict(transformed_raw)
        transformed_assessment = self.bundle.apply(transformed_state)
        self.assertEqual(0, transformed_assessment["matched_entity_count"])
        self.assertEqual(
            "card_type_mismatch",
            transformed_assessment["mismatches"][0]["reason"],
        )
        self.assertEqual("unsupported", transformed_state.friendly.hand[0].effect_coverage)

    def test_new_lifesteal_and_self_damage_rules_preserve_point_effect_semantics(self) -> None:
        drain_raw = _clean_snapshot()
        drain_raw["player"]["hero"]["damage"] = 29
        drain_raw["player"]["resources"].update({"available": 2, "total": 2})
        drain = advisor_entity(
            20,
            "CORE_ICC_055",
            "SPELL",
            name="Drain Soul",
            cost=2,
            text="<b>Lifesteal</b>\nDeal $3 damage\nto a minion.",
        )
        drain.update({"has_lifesteal": True, "mechanics": ["LIFESTEAL"]})
        drain_raw["player"]["hand"] = [drain]
        drain_raw["opponent"]["board"] = [
            advisor_entity(40, "TARGET", "MINION", attack=2, health=1)
        ]
        drain_state = GameState.from_dict(drain_raw)
        self.bundle.apply(drain_state)
        drain_actions = [
            item
            for item in enumerate_legal_actions(drain_state)
            if item.kind == ActionKind.PLAY_CARD
        ]
        self.assertEqual(["play_card:20:40"], [item.action_id for item in drain_actions])
        drain_outcome = apply_action(drain_state, drain_actions[0]).state
        self.assertEqual(4, drain_outcome.friendly.hero.current_health)
        self.assertFalse(drain_outcome.opponent.board)

        shard_raw = _clean_snapshot()
        shard_raw["player"]["hero"]["damage"] = 20
        shard_raw["player"]["resources"].update({"available": 4, "total": 4})
        shard = advisor_entity(
            20,
            "CORE_SW_442",
            "SPELL",
            name="Void Shard",
            cost=4,
            text="<b>Lifesteal</b>\nDeal $4 damage.",
        )
        shard.update(
            {
                "has_lifesteal": False,
                "mechanics": ["LIFESTEAL"],
                "tags": {"685": "1"},
            }
        )
        shard_raw["player"]["hand"] = [shard]
        shard_raw["opponent"]["hero"].update({"health": 1, "damage": 0, "armor": 3})
        shard_state = GameState.from_dict(shard_raw)
        self.assertTrue(shard_state.friendly.hand[0].lifesteal)
        self.bundle.apply(shard_state)
        shard_action = next(
            item
            for item in enumerate_legal_actions(shard_state)
            if item.action_id == "play_card:20:30"
        )
        shard_outcome = apply_action(shard_state, shard_action).state
        self.assertEqual(14, shard_outcome.friendly.hero.current_health)
        self.assertEqual(0, shard_outcome.opponent.armor)
        self.assertEqual(0, shard_outcome.opponent.hero.current_health)

        imp_raw = _clean_snapshot()
        imp_raw["player"]["hero"]["damage"] = 0
        imp_raw["player"]["resources"].update({"available": 1, "total": 1})
        imp = advisor_entity(
            20,
            "CORE_EX1_319",
            "MINION",
            name="Flame Imp",
            cost=1,
            attack=3,
            health=2,
            text="<b>Battlecry:</b> Deal $3 damage to your hero.",
        )
        imp["mechanics"] = ["BATTLECRY"]
        imp_raw["player"]["hand"] = [imp]
        imp_state = GameState.from_dict(imp_raw)
        self.bundle.apply(imp_state)
        imp_action = next(
            item
            for item in enumerate_legal_actions(imp_state)
            if item.kind == ActionKind.PLAY_CARD
        )
        self.assertEqual("play_card:20:10:position=1", imp_action.action_id)
        imp_outcome = apply_action(imp_state, imp_action).state
        self.assertEqual(27, imp_outcome.friendly.hero.current_health)

    def test_lifesteal_rules_abstain_without_boolean_or_tag_evidence(self) -> None:
        cases = (
            (
                "CORE_ICC_055",
                "Drain Soul",
                2,
                "<b>Lifesteal</b>\nDeal $3 damage\nto a minion.",
                {},
            ),
            (
                "CORE_SW_442",
                "Void Shard",
                4,
                "<b>Lifesteal</b>\nDeal $4 damage.",
                {"LIFESTEAL": 0},
            ),
            (
                "RLK_024",
                "Death Strike",
                4,
                "<b>Lifesteal</b>\nDeal $6 damage\nto a minion.",
                {"685": "0"},
            ),
        )
        for card_id, name, cost, text, tags in cases:
            raw = _clean_snapshot()
            raw["player"]["resources"].update({"available": cost, "total": cost})
            card = advisor_entity(
                20,
                card_id,
                "SPELL",
                name=name,
                cost=cost,
                text=text,
            )
            card.pop("has_lifesteal")
            card.update({"mechanics": ["LIFESTEAL"], "tags": tags})
            raw["player"]["hand"] = [card]
            state = GameState.from_dict(raw)
            with self.subTest(card_id=card_id):
                self.assertFalse(state.friendly.hand[0].lifesteal)
                assessment = self.bundle.apply(state)
                self.assertEqual(0, assessment["matched_entity_count"])
                self.assertEqual(1, assessment["mismatch_entity_count"])
                self.assertEqual(
                    "required_mechanic_unproven",
                    assessment["mismatches"][0]["reason"],
                )
                self.assertFalse(state.friendly.hand[0].effects)
                self.assertEqual("unsupported", state.friendly.hand[0].effect_coverage)

    def test_coin_aliases_enable_followup_actions_in_the_same_line(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 0, "total": 1})
        coin = advisor_entity(
            20,
            "CATA_COIN5",
            "SPELL",
            name="The Coin",
            cost=0,
            text="Gain 1 Mana Crystal this turn only.",
        )
        followup = advisor_entity(
            21,
            "EVAL_COIN_FOLLOWUP",
            "MINION",
            name="Coin Follow-up",
            cost=1,
            attack=4,
            health=4,
        )
        raw["player"]["hand"] = [coin, followup]
        state = GameState.from_dict(raw)

        assessment = self.bundle.apply(state)
        self.assertEqual(
            ["coin-temporary-mana-v1"],
            [item["rule_id"] for item in assessment["matched"]],
        )
        coin_action = next(
            action
            for action in enumerate_legal_actions(state)
            if action.action_id == "play_card:20:"
        )
        after_coin = apply_action(state, coin_action).state
        self.assertEqual(1, after_coin.friendly.mana)
        self.assertIn(
            "play_card:21::position=1",
            {action.action_id for action in enumerate_legal_actions(after_coin)},
        )

        rule = self.bundle.rules_by_card_id["CATA_COIN5"]
        for alias in ("GAME_005", "TSC_COIN1", "CATA_COIN6", "EDR_COIN1", "TTN_COIN1"):
            self.assertIs(rule, self.bundle.rules_by_card_id[alias])

    def test_backstab_requires_explicit_undamaged_health_evidence(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 0, "total": 0})
        raw["player"]["hand"] = [
            advisor_entity(
                20,
                "CORE_CS2_072",
                "SPELL",
                name="Backstab",
                cost=0,
                text="Deal 2 damage to an undamaged minion.",
            )
        ]
        undamaged = advisor_entity(
            40,
            "EVAL_UNDAMAGED",
            "MINION",
            attack=2,
            health=2,
        )
        damaged = advisor_entity(
            41,
            "EVAL_DAMAGED",
            "MINION",
            attack=2,
            health=2,
        )
        damaged["damage"] = 1
        raw["opponent"]["board"] = [undamaged, damaged]
        state = GameState.from_dict(raw)
        self.bundle.apply(state)
        self.assertEqual(
            ["play_card:20:40"],
            [
                action.action_id
                for action in enumerate_legal_actions(state)
                if action.source_entity_id == "20"
            ],
        )

        missing = copy.deepcopy(raw)
        missing["opponent"]["board"] = [copy.deepcopy(undamaged)]
        missing["opponent"]["board"][0].pop("damage")
        missing_state = GameState.from_dict(missing)
        self.bundle.apply(missing_state)
        self.assertFalse(missing_state.opponent.board[0].current_health_known)
        self.assertFalse(
            any(
                action.source_entity_id == "20"
                for action in enumerate_legal_actions(missing_state)
            )
        )

    def test_sleet_storm_rule_binds_selected_and_random_targets_separately(self) -> None:
        from fractions import Fraction

        from metacompanion_solver.turnpair_evaluation import (
            _apply_oracle_action_outcomes,
            enumerate_oracle_actions,
        )

        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 1, "total": 1})
        raw["player"]["hand"] = [
            advisor_entity(
                20,
                "CATA_485",
                "SPELL",
                name="Sleet Storm",
                cost=1,
                text=(
                    "[x]Deal $2 damage.\n\u00a0Deal $1 damage to a\n"
                    "\u00a0random enemy minion."
                ),
            )
        ]
        raw["opponent"]["board"] = [
            advisor_entity(40, "EVAL_OPEN", "MINION", attack=1, health=3),
            advisor_entity(41, "EVAL_HIDDEN", "MINION", attack=1, health=3),
        ]
        raw["opponent"]["board"][1]["has_stealth"] = True
        state = GameState.from_dict(raw)
        assessment = self.bundle.apply(state)
        self.assertEqual(
            ["cata-sleet-storm-selected-and-random-damage-v1"],
            [item["rule_id"] for item in assessment["matched"]],
        )
        effects = state.friendly.hand[0].effects
        self.assertFalse(effects[0].random)
        self.assertTrue(effects[1].random)
        action = next(
            item
            for item in enumerate_oracle_actions(state)
            if item.action_id == "play_card:20:30"
        )
        outcomes = _apply_oracle_action_outcomes(state, action)
        self.assertEqual(2, len(outcomes))
        self.assertEqual(
            Fraction(1, 1), sum(item.probability for item in outcomes)
        )

    def test_complex_live_cards_remain_fail_closed(self) -> None:
        for card_id in (
            "CORE_EX1_129",  # Fan of Knives: draw identity
            "CORE_RLK_567",  # Shadow of Demise: history-bound transform
            "TIME_702",  # Ebb and Flow: history-bound conditional Armor
            "JAIL_474",  # Jade Guardians: random generation and history cost
            "EDR_888",  # Malorne: Discover and Imbue history
            "END_000p",  # Blessing of the Bronze: random Rewind generation
        ):
            with self.subTest(card_id=card_id):
                self.assertNotIn(card_id, self.bundle.rules_by_card_id)

    def test_same_card_id_with_changed_text_gets_unverified_best_effort_routes(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 4, "total": 4})
        raw["player"]["hand"] = [
            advisor_entity(
                20,
                "CORE_CS2_029",
                "SPELL",
                name="Drifted Fireball",
                cost=4,
                text="Deal 5 damage.",
            )
        ]
        service = SolverService(SolverConfig(training_log_path=None), logger=JsonlTrainingLogger(None))
        result = service.solve(_request(raw, "text-drift"))
        self.assert_unverified_best_effort(result)
        rules = result.coverage["structured_card_rules"]
        self.assertEqual(0, rules["matched_entity_count"])
        self.assertEqual(1, rules["mismatch_entity_count"])
        self.assertEqual("english_text_sha256_mismatch", rules["mismatches"][0]["reason"])

    def test_unknown_playable_text_always_disables_proof_claims_but_keeps_routes(self) -> None:
        raw = _clean_snapshot()
        raw["player"]["resources"].update({"available": 1, "total": 1})
        raw["player"]["hand"] = [
            advisor_entity(20, "UNKNOWN_RULE", "SPELL", cost=1, text="Do an unknown thing.")
        ]
        service = SolverService(SolverConfig(training_log_path=None), logger=JsonlTrainingLogger(None))
        unsupported = service.solve(_request(copy.deepcopy(raw), "unknown-nonlethal"))
        self.assert_unverified_best_effort(unsupported)

        attacker = advisor_entity(40, "VANILLA_ATTACKER", "MINION", attack=3, health=3)
        attacker.update(
            {
                "zone": "PLAY",
                "is_exhausted": False,
                "tags": {"NUM_TURNS_IN_PLAY": 1, "NUM_ATTACKS_THIS_TURN": 0},
            }
        )
        raw["player"]["board"] = [attacker]
        raw["opponent"]["hero"]["health"] = 3
        lethal = service.solve(_request(raw, "unknown-alternative-lethal"))
        self.assert_unverified_best_effort(lethal)
        self.assertEqual("attack:40:30", lethal.recommendations[0].actions[0].action_id)


if __name__ == "__main__":
    unittest.main()
