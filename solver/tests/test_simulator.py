from __future__ import annotations

import unittest

import _path  # noqa: F401

from metacompanion_solver.errors import IllegalActionError
from metacompanion_solver.schemas import Action, ActionKind, Card, CardType, Effect
from metacompanion_solver.simulator import apply_action, enumerate_legal_actions, scan_state_coverage

from helpers import state


class SimulatorTests(unittest.TestCase):
    def test_taunt_restricts_attack_targets(self) -> None:
        game = state()
        attacker = Card(
            "attacker", "A", "Attacker", CardType.MINION, attack=3, health=3, current_health=3,
            can_attack=True, attacks_remaining=1,
        )
        taunt = Card("taunt", "T", "Taunt", CardType.MINION, attack=1, health=4, current_health=4, taunt=True)
        game.friendly.board.append(attacker)
        game.opponent.board.append(taunt)
        attacks = [item for item in enumerate_legal_actions(game) if item.kind == ActionKind.ATTACK]
        self.assertEqual(["taunt"], [item.target_entity_id for item in attacks])

    def test_playing_vanilla_minion_spends_mana_and_summons(self) -> None:
        game = state()
        minion = Card("m1", "M1", "Minion", CardType.MINION, cost=3, attack=3, health=4, current_health=4)
        game.friendly.hand.append(minion)
        action = next(item for item in enumerate_legal_actions(game) if item.source_entity_id == "m1")
        outcome = apply_action(game, action)
        self.assertEqual(7, outcome.state.friendly.mana)
        self.assertEqual(["m1"], [item.entity_id for item in outcome.state.friendly.board])
        self.assertFalse(outcome.annotations)
        self.assertEqual(10, game.friendly.mana, "input state must remain immutable")

    def test_board_placement_enumerates_and_replays_every_one_based_position(self) -> None:
        game = state()
        game.friendly.board.extend(
            [
                Card(
                    "left",
                    "LEFT",
                    "Left",
                    CardType.MINION,
                    attack=1,
                    health=1,
                    current_health=1,
                ),
                Card(
                    "right",
                    "RIGHT",
                    "Right",
                    CardType.MINION,
                    attack=1,
                    health=1,
                    current_health=1,
                ),
            ]
        )
        game.friendly.hand.extend(
            [
                Card(
                    "placed",
                    "PLACED",
                    "Placed",
                    CardType.MINION,
                    cost=1,
                    attack=2,
                    health=2,
                    current_health=2,
                ),
                Card(
                    "location",
                    "LOCATION",
                    "Location",
                    CardType.LOCATION,
                    cost=1,
                    durability=2,
                    current_durability=2,
                ),
                Card("spell", "SPELL", "Spell", CardType.SPELL, cost=1),
            ]
        )
        actions = enumerate_legal_actions(game)

        def action_ids(source: str) -> set[str]:
            return {
                action.action_id
                for action in actions
                if action.source_entity_id == source
            }

        self.assertEqual(
            {
                "play_card:placed::position=1",
                "play_card:placed::position=2",
                "play_card:placed::position=3",
            },
            action_ids("placed"),
        )
        self.assertEqual(
            {
                "play_card:location::position=1",
                "play_card:location::position=2",
                "play_card:location::position=3",
            },
            action_ids("location"),
        )
        middle = next(
            action
            for action in actions
            if action.action_id == "play_card:placed::position=2"
        )
        self.assertEqual(
            ["left", "placed", "right"],
            [
                card.entity_id
                for card in apply_action(game, middle).state.friendly.board
            ],
        )
        right_location = next(
            action
            for action in actions
            if action.action_id == "play_card:location::position=3"
        )
        self.assertEqual(
            ["left", "right", "location"],
            [
                card.entity_id
                for card in apply_action(game, right_location).state.friendly.board
            ],
        )
        forged_spell = Action(
            ActionKind.PLAY_CARD,
            source_entity_id="spell",
            card_id="SPELL",
            board_position=1,
        )
        with self.assertRaises(IllegalActionError):
            apply_action(game, forged_spell)

    def test_generic_damage_is_deterministic(self) -> None:
        game = state()
        target = Card("target", "T", "Target", CardType.MINION, attack=2, health=3, current_health=3)
        spell = Card(
            "spell", "S", "Bolt", CardType.SPELL, cost=1,
            effects=(Effect("damage", amount=3, target="enemy_minion"),),
        )
        game.opponent.board.append(target)
        game.friendly.hand.append(spell)
        action = next(item for item in enumerate_legal_actions(game) if item.source_entity_id == "spell")
        first = apply_action(game, action)
        second = apply_action(game, action)
        self.assertEqual(first.state.to_dict(), second.state.to_dict())
        self.assertEqual([], first.state.opponent.board)

    def test_elusive_blocks_spells_and_hero_powers_but_not_battlecries(self) -> None:
        game = state()
        elusive_tags = {
            "ELUSIVE": 1,
            "CANT_BE_TARGETED_BY_SPELLS": 1,
            "CANT_BE_TARGETED_BY_HERO_POWERS": 1,
        }
        game.friendly.board.append(
            Card(
                "friendly-elusive",
                "E1",
                "Friendly Elusive",
                CardType.MINION,
                attack=1,
                health=2,
                current_health=2,
                tags=dict(elusive_tags),
            )
        )
        game.opponent.board.extend(
            [
                Card(
                    "enemy-elusive",
                    "E2",
                    "Enemy Elusive",
                    CardType.MINION,
                    attack=1,
                    health=2,
                    current_health=2,
                    tags=dict(elusive_tags),
                ),
                Card(
                    "enemy-normal",
                    "N",
                    "Enemy Normal",
                    CardType.MINION,
                    attack=1,
                    health=2,
                    current_health=2,
                ),
            ]
        )
        game.friendly.hand.extend(
            [
                Card(
                    "spell",
                    "S",
                    "Spell",
                    CardType.SPELL,
                    cost=1,
                    effects=(Effect("damage", amount=1, target="any_minion"),),
                ),
                Card(
                    "battlecry",
                    "B",
                    "Battlecry",
                    CardType.MINION,
                    cost=1,
                    attack=1,
                    health=1,
                    current_health=1,
                    effects=(Effect("damage", amount=1, target="enemy_minion"),),
                ),
            ]
        )
        game.friendly.hero_power = Card(
            "power",
            "P",
            "Power",
            CardType.HERO_POWER,
            cost=1,
            effects=(Effect("damage", amount=1, target="enemy_minion"),),
        )
        game.friendly.hero_power_available = True
        actions = enumerate_legal_actions(game)

        def targets(source: str) -> set[str]:
            return {
                action.target_entity_id
                for action in actions
                if action.source_entity_id == source
            }

        self.assertEqual({"enemy-normal"}, targets("spell"))
        self.assertEqual({"enemy-normal"}, targets("power"))
        self.assertEqual(
            {"enemy-elusive", "enemy-normal"}, targets("battlecry")
        )

    def test_frostbolt_damage_and_freeze_share_one_target_before_cleanup(self) -> None:
        game = state()
        target = Card(
            "target",
            "TARGET",
            "Target",
            CardType.MINION,
            attack=3,
            health=4,
            current_health=4,
            can_attack=True,
            attacks_remaining=1,
        )
        frostbolt = Card(
            "frostbolt",
            "CORE_CS2_024",
            "Frostbolt",
            CardType.SPELL,
            cost=2,
            effects=(
                Effect("damage", amount=3, target="any_character"),
                Effect("freeze", target="any_character"),
            ),
            effect_coverage="exact",
        )
        game.opponent.board.append(target)
        game.friendly.hand.append(frostbolt)
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "frostbolt"
            and item.target_entity_id == "target"
        )

        after = apply_action(game, action).state.opponent.board[0]
        self.assertEqual(1, after.current_health)
        self.assertTrue(after.frozen)
        self.assertFalse(after.can_attack)

    def test_automatic_group_effects_need_no_click_target_and_resolve_both_sides(self) -> None:
        game = state()
        game.friendly.spell_power = 1
        game.friendly.hero.current_health = 25
        game.friendly.board.append(
            Card(
                "friendly-minion",
                "FRIENDLY",
                "Friendly",
                CardType.MINION,
                attack=2,
                health=5,
                current_health=3,
            )
        )
        game.opponent.board.extend(
            [
                Card(
                    "survivor",
                    "SURVIVOR",
                    "Survivor",
                    CardType.MINION,
                    attack=3,
                    health=4,
                    current_health=4,
                ),
                Card(
                    "dead",
                    "DEAD",
                    "Dead",
                    CardType.MINION,
                    attack=1,
                    health=2,
                    current_health=2,
                ),
            ]
        )
        holy_nova = Card(
            "nova",
            "CORE_CS1_112",
            "Holy Nova",
            CardType.SPELL,
            cost=3,
            effects=(
                Effect("damage", amount=2, target="all_enemy_minions"),
                Effect("heal", amount=2, target="all_friendly_characters"),
            ),
        )
        game.friendly.hand.append(holy_nova)
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "nova"
        )
        self.assertEqual("", action.target_entity_id)
        after = apply_action(game, action).state
        self.assertEqual(27, after.friendly.hero.current_health)
        self.assertEqual(5, after.friendly.board[0].current_health)
        self.assertEqual(["survivor"], [card.entity_id for card in after.opponent.board])
        self.assertEqual(1, after.opponent.board[0].current_health)

    def test_set_health_applies_to_every_minion_without_changing_heroes(self) -> None:
        game = state()
        game.friendly.hero.current_health = 24
        game.opponent.hero.current_health = 19
        game.friendly.board.append(
            Card(
                "friendly-minion",
                "FRIENDLY",
                "Friendly",
                CardType.MINION,
                attack=4,
                health=7,
                current_health=3,
            )
        )
        game.opponent.board.append(
            Card(
                "enemy-minion",
                "ENEMY",
                "Enemy",
                CardType.MINION,
                attack=6,
                health=8,
                current_health=7,
            )
        )
        equality = Card(
            "equality",
            "CORE_EX1_619",
            "Equality",
            CardType.SPELL,
            cost=2,
            effects=(Effect("set_health", amount=1, target="all_minions"),),
        )
        game.friendly.hand.append(equality)
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "equality"
        )
        self.assertEqual("", action.target_entity_id)
        after = apply_action(game, action).state
        for minion in [*after.friendly.board, *after.opponent.board]:
            self.assertEqual((1, 1), (minion.health, minion.current_health))
        self.assertEqual(24, after.friendly.hero.current_health)
        self.assertEqual(19, after.opponent.hero.current_health)

    def test_other_minion_groups_exclude_the_battlecry_source(self) -> None:
        game = state()
        game.friendly.board.append(
            Card(
                "friendly-buddy",
                "BUDDY",
                "Friendly Buddy",
                CardType.MINION,
                attack=2,
                health=3,
                current_health=3,
            )
        )
        game.opponent.board.append(
            Card(
                "enemy-minion",
                "ENEMY",
                "Enemy",
                CardType.MINION,
                attack=2,
                health=3,
                current_health=3,
            )
        )
        source = Card(
            "source",
            "SOURCE",
            "Source",
            CardType.MINION,
            cost=1,
            attack=1,
            health=4,
            current_health=4,
            effects=(
                Effect("damage", amount=1, target="all_other_minions"),
                Effect(
                    "buff_attack",
                    amount=1,
                    target="all_other_friendly_minions",
                ),
            ),
        )
        game.friendly.hand.append(source)
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "source" and item.board_position == 2
        )

        after = apply_action(game, action).state
        played = next(card for card in after.friendly.board if card.entity_id == "source")
        buddy = next(
            card for card in after.friendly.board if card.entity_id == "friendly-buddy"
        )
        self.assertEqual(4, played.current_health)
        self.assertEqual(1, played.attack)
        self.assertEqual(2, buddy.current_health)
        self.assertEqual(3, buddy.attack)
        self.assertEqual(2, after.opponent.board[0].current_health)
        self.assertEqual(2, after.opponent.board[0].attack)

    def test_searing_fissure_damages_both_boards_then_enables_hero_attack(self) -> None:
        game = state()
        game.friendly.hero.tags["NUM_ATTACKS_THIS_TURN"] = 0
        game.friendly.board.append(
            Card("friendly", "F", "Friendly", CardType.MINION, health=1, current_health=1)
        )
        game.opponent.board.append(
            Card("enemy", "E", "Enemy", CardType.MINION, health=2, current_health=2)
        )
        game.friendly.hand.append(
            Card(
                "fissure",
                "CATA_582",
                "Searing Fissure",
                CardType.SPELL,
                cost=2,
                effects=(
                    Effect("damage_all_minions", amount=1),
                    Effect("gain_hero_attack", amount=3),
                ),
                effect_coverage="exact",
            )
        )
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "fissure"
        )

        after = apply_action(game, action).state
        self.assertFalse(after.friendly.board)
        self.assertEqual(1, after.opponent.board[0].current_health)
        self.assertEqual(3, after.friendly.hero.attack)
        self.assertTrue(after.friendly.hero.can_attack)
        self.assertEqual(1, after.friendly.hero.attacks_remaining)

    def test_location_placement_is_inert_and_confirmed_activation_consumes_charges(self) -> None:
        game = state()
        target = Card(
            "target",
            "TARGET",
            "Target",
            CardType.MINION,
            attack=1,
            health=3,
            current_health=3,
        )
        location = Card(
            "depths",
            "CORE_REV_990",
            "Sanguine Depths",
            CardType.LOCATION,
            cost=1,
            health=2,
            current_health=2,
            effects=(
                Effect("damage", amount=1, target="any_minion"),
                Effect("buff_attack", amount=2, target="any_minion"),
            ),
            effect_coverage="exact",
        )
        game.opponent.board.append(target)
        game.friendly.hand.append(location)
        placement = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "depths"
        )

        placed = apply_action(game, placement).state
        self.assertEqual(3, placed.opponent.board[0].current_health)
        self.assertEqual(1, placed.opponent.board[0].attack)
        self.assertFalse(
            any(
                item.kind == ActionKind.LOCATION_ACTIVATE
                for item in enumerate_legal_actions(placed)
            )
        )

        activation = Action(
            ActionKind.LOCATION_ACTIVATE,
            source_entity_id="depths",
            target_entity_id="target",
            card_id="CORE_REV_990",
        )
        after_first = apply_action(placed, activation, validate=False).state
        self.assertEqual(1, after_first.friendly.board[0].current_health)
        self.assertEqual(2, after_first.opponent.board[0].current_health)
        self.assertEqual(3, after_first.opponent.board[0].attack)

        after_second = apply_action(after_first, activation, validate=False).state
        self.assertFalse(after_second.friendly.board)
        self.assertEqual(1, after_second.opponent.board[0].current_health)
        self.assertEqual(5, after_second.opponent.board[0].attack)

    def test_damage_then_summon_creates_a_rush_token_with_minion_only_targets(self) -> None:
        game = state()
        game.opponent.board.extend(
            [
                Card(
                    "victim", "V", "Victim", CardType.MINION,
                    attack=0, health=1, current_health=1,
                ),
                Card(
                    "survivor", "S", "Survivor", CardType.MINION,
                    attack=0, health=3, current_health=3,
                ),
            ]
        )
        game.friendly.hand.append(
            Card(
                "wound-prey", "CORE_BAR_801", "Wound Prey", CardType.SPELL,
                cost=1,
                effects=(
                    Effect("damage", amount=1, target="any_character"),
                    Effect(
                        "summon", target="none", card_id="BAR_035t",
                        name="Swift Hyena", attack=1, health=1, rush=True,
                    ),
                ),
                effect_coverage="exact",
            )
        )
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "wound-prey"
            and item.target_entity_id == "victim"
        )
        after = apply_action(game, action).state
        self.assertEqual(["survivor"], [item.entity_id for item in after.opponent.board])
        token = after.friendly.board[0]
        self.assertEqual("BAR_035t", token.card_id)
        self.assertFalse(token.playable)
        self.assertTrue(token.rush)
        self.assertTrue(token.can_attack)
        token_targets = {
            item.target_entity_id
            for item in enumerate_legal_actions(after)
            if item.kind == ActionKind.ATTACK
            and item.source_entity_id == token.entity_id
        }
        self.assertEqual({"survivor"}, token_targets)

    def test_damage_still_resolves_when_full_board_blocks_followup_summon(self) -> None:
        game = state()
        game.friendly.board.extend(
            Card(
                f"friendly-{index}", f"F{index}", "Friendly", CardType.MINION,
                attack=1, health=1, current_health=1,
            )
            for index in range(7)
        )
        game.opponent.board.append(
            Card("victim", "V", "Victim", CardType.MINION, health=1, current_health=1)
        )
        game.friendly.hand.append(
            Card(
                "wound-prey", "CORE_BAR_801", "Wound Prey", CardType.SPELL,
                cost=1,
                effects=(
                    Effect("damage", amount=1, target="any_character"),
                    Effect(
                        "summon", target="none", card_id="BAR_035t",
                        name="Swift Hyena", attack=1, health=1, rush=True,
                    ),
                ),
                effect_coverage="exact",
            )
        )
        action = next(
            item
            for item in enumerate_legal_actions(game)
            if item.source_entity_id == "wound-prey"
            and item.target_entity_id == "victim"
        )
        after = apply_action(game, action).state
        self.assertEqual([], after.opponent.board)
        self.assertEqual(7, len(after.friendly.board))
        self.assertNotIn("BAR_035t", {item.card_id for item in after.friendly.board})

    def test_unknown_spell_is_playable_but_annotated(self) -> None:
        game = state()
        spell = Card(
            "mystery", "X", "Mystery", CardType.SPELL, cost=1,
            effect_coverage="unsupported", unsupported_effects=("discover",),
        )
        game.friendly.hand.append(spell)
        action = next(item for item in enumerate_legal_actions(game) if item.source_entity_id == "mystery")
        outcome = apply_action(game, action)
        codes = {item.code for item in outcome.annotations}
        self.assertIn("unsupported_card_text", codes)
        self.assertIn("unsupported_card_mechanic", codes)

    def test_divine_shield_absorbs_damage(self) -> None:
        game = state()
        attacker = Card(
            "attacker", "A", "Attacker", CardType.MINION, attack=5, health=5, current_health=5,
            can_attack=True, attacks_remaining=1,
        )
        shield = Card(
            "shield", "D", "Shield", CardType.MINION, attack=1, health=2, current_health=2,
            divine_shield=True,
        )
        game.friendly.board.append(attacker)
        game.opponent.board.append(shield)
        action = next(
            item for item in enumerate_legal_actions(game)
            if item.kind == ActionKind.ATTACK and item.target_entity_id == "shield"
        )
        outcome = apply_action(game, action)
        defended = outcome.state.opponent.board[0]
        self.assertEqual(2, defended.current_health)
        self.assertFalse(defended.divine_shield)

    def test_dormant_taunt_does_not_attack_block_or_accept_attacks(self) -> None:
        game = state()
        attacker = Card(
            "attacker", "A", "Attacker", CardType.MINION, attack=3, health=3,
            current_health=3, can_attack=True, attacks_remaining=1,
        )
        dormant = Card(
            "dormant", "D", "Dormant", CardType.MINION, attack=8, health=8,
            current_health=8, taunt=True, dormant=True, can_attack=True,
            attacks_remaining=1,
        )
        game.friendly.board.append(attacker)
        game.opponent.board.append(dormant)
        attacks = [item for item in enumerate_legal_actions(game) if item.kind == ActionKind.ATTACK]
        self.assertEqual([game.opponent.hero.entity_id], [item.target_entity_id for item in attacks])

    def test_rush_is_minion_only_but_charge_can_attack_hero(self) -> None:
        game = state()
        game.opponent.board.append(
            Card("target", "T", "Target", CardType.MINION, attack=0, health=5, current_health=5)
        )
        game.friendly.hand.extend(
            [
                Card(
                    "rush", "R", "Rush", CardType.MINION, cost=1, attack=2,
                    health=2, current_health=2, rush=True,
                ),
                Card(
                    "charge", "C", "Charge", CardType.MINION, cost=1, attack=2,
                    health=2, current_health=2, charge=True,
                ),
            ]
        )
        rush_play = next(
            item for item in enumerate_legal_actions(game) if item.source_entity_id == "rush"
        )
        after_rush = apply_action(game, rush_play).state
        rush_targets = {
            item.target_entity_id
            for item in enumerate_legal_actions(after_rush)
            if item.kind == ActionKind.ATTACK and item.source_entity_id == "rush"
        }
        self.assertEqual({"target"}, rush_targets)

        charge_play = next(
            item for item in enumerate_legal_actions(game) if item.source_entity_id == "charge"
        )
        after_charge = apply_action(game, charge_play).state
        charge_targets = {
            item.target_entity_id
            for item in enumerate_legal_actions(after_charge)
            if item.kind == ActionKind.ATTACK and item.source_entity_id == "charge"
        }
        self.assertIn(game.opponent.hero.entity_id, charge_targets)

    def test_weapon_durability_breaks_after_hero_attack(self) -> None:
        game = state()
        game.friendly.hero.attack = 3
        game.friendly.hero.can_attack = True
        game.friendly.hero.attacks_remaining = 1
        game.friendly.weapon = Card(
            "weapon", "W", "Weapon", CardType.WEAPON, attack=3,
            durability=1, current_durability=1,
        )
        action = next(
            item for item in enumerate_legal_actions(game)
            if item.kind == ActionKind.ATTACK
            and item.source_entity_id == game.friendly.hero.entity_id
        )
        outcome = apply_action(game, action)
        self.assertIsNone(outcome.state.friendly.weapon)
        self.assertEqual(0, outcome.state.friendly.hero.attack)
        self.assertFalse(outcome.state.friendly.hero.can_attack)

    def test_hand_count_aura_reprices_hero_power_after_playing_a_card(self) -> None:
        game = state()
        game.friendly.mana = 1
        game.friendly.max_mana = 1
        game.friendly.hero_power = Card(
            "power",
            "POWER",
            "Hero Power",
            CardType.HERO_POWER,
            cost=2,
            effects=(Effect("damage", amount=2, target="enemy_hero"),),
            effect_coverage="exact",
            tags={"COST": 2, "TAG_LAST_KNOWN_COST_IN_HAND": 2},
        )
        game.friendly.hero_power_available = True
        game.friendly.board.append(
            Card(
                "aura",
                "TIME_606",
                "Queldorei Fletcher",
                CardType.MINION,
                attack=1,
                health=3,
                current_health=3,
                effects=(
                    Effect(
                        "set_hero_power_cost",
                        amount=0,
                        hand_count_at_most=3,
                    ),
                ),
                effect_coverage="exact",
            )
        )
        game.friendly.hand.extend(
            [
                Card("spend", "SPEND", "Spend", CardType.MINION, cost=1, health=1, current_health=1),
                Card("f1", "F1", "F1", CardType.MINION, cost=9, health=1, current_health=1),
                Card("f2", "F2", "F2", CardType.MINION, cost=9, health=1, current_health=1),
                Card("f3", "F3", "F3", CardType.MINION, cost=9, health=1, current_health=1),
            ]
        )

        play = next(
            action
            for action in enumerate_legal_actions(game)
            if action.source_entity_id == "spend"
        )
        after_play = apply_action(game, play).state

        self.assertEqual(3, len(after_play.friendly.hand))
        self.assertEqual(0, after_play.friendly.hero_power.cost)
        power = next(
            action
            for action in enumerate_legal_actions(after_play)
            if action.kind == ActionKind.HERO_POWER
        )
        after_power = apply_action(after_play, power).state
        self.assertEqual(0, after_power.friendly.mana)
        self.assertEqual(28, after_power.opponent.hero.current_health)

    def test_hero_power_cost_returns_to_base_when_aura_source_dies(self) -> None:
        game = state()
        game.active_player_id = game.opponent.player_id
        game.friendly.hero_power = Card(
            "power",
            "POWER",
            "Hero Power",
            CardType.HERO_POWER,
            cost=0,
            tags={"COST": 0, "TAG_LAST_KNOWN_COST_IN_HAND": 2},
        )
        game.friendly.hand.extend(
            Card(f"f{index}", f"F{index}", f"F{index}", CardType.MINION, health=1, current_health=1)
            for index in range(3)
        )
        game.friendly.board.append(
            Card(
                "aura",
                "TIME_606",
                "Queldorei Fletcher",
                CardType.MINION,
                health=1,
                current_health=1,
                effects=(
                    Effect(
                        "set_hero_power_cost",
                        amount=0,
                        hand_count_at_most=3,
                    ),
                ),
                effect_coverage="exact",
            )
        )
        game.opponent.board.append(
            Card(
                "attacker",
                "ATTACKER",
                "Attacker",
                CardType.MINION,
                attack=1,
                health=1,
                current_health=1,
                can_attack=True,
                attacks_remaining=1,
            )
        )

        attack = next(
            action
            for action in enumerate_legal_actions(game)
            if action.source_entity_id == "attacker"
            and action.target_entity_id == "aura"
        )
        after = apply_action(game, attack).state

        self.assertFalse(after.friendly.board)
        self.assertEqual(2, after.friendly.hero_power.cost)
        self.assertEqual(2, after.friendly.hero_power.tags["COST"])

    def test_missing_base_cost_evidence_marks_continuous_aura_unsupported(self) -> None:
        game = state()
        game.friendly.hero_power = Card(
            "power", "POWER", "Hero Power", CardType.HERO_POWER, cost=0
        )
        game.friendly.board.append(
            Card(
                "aura",
                "TIME_606",
                "Queldorei Fletcher",
                CardType.MINION,
                health=1,
                current_health=1,
                effects=(
                    Effect(
                        "set_hero_power_cost",
                        amount=0,
                        hand_count_at_most=3,
                    ),
                ),
                effect_coverage="exact",
            )
        )

        self.assertIn(
            "missing_hero_power_base_cost",
            {item.code for item in scan_state_coverage(game)},
        )

    def test_replacing_weapon_preserves_non_weapon_attack_and_refreshes_legality(self) -> None:
        game = state()
        game.friendly.hero.attack = 4
        game.friendly.hero.tags["NUM_ATTACKS_THIS_TURN"] = 0
        game.friendly.weapon = Card(
            "old", "OLD", "Old weapon", CardType.WEAPON,
            attack=2, durability=1, current_durability=1,
        )
        game.friendly.hand.append(
            Card(
                "new", "NEW", "New weapon", CardType.WEAPON,
                cost=1, attack=3, durability=2, current_durability=2,
            )
        )

        equip = next(
            action
            for action in enumerate_legal_actions(game)
            if action.source_entity_id == "new"
        )
        after = apply_action(game, equip).state

        self.assertEqual("new", after.friendly.weapon.entity_id)
        self.assertEqual(5, after.friendly.hero.attack)
        self.assertTrue(after.friendly.hero.can_attack)
        self.assertEqual(1, after.friendly.hero.attacks_remaining)

    def test_equipping_normal_weapon_does_not_restore_used_attack(self) -> None:
        game = state()
        game.friendly.hero.tags["NUM_ATTACKS_THIS_TURN"] = 1
        game.friendly.hand.append(
            Card(
                "weapon", "WEAPON", "Weapon", CardType.WEAPON,
                cost=1, attack=3, durability=2, current_durability=2,
            )
        )

        equip = next(
            action
            for action in enumerate_legal_actions(game)
            if action.source_entity_id == "weapon"
        )
        after = apply_action(game, equip).state

        self.assertEqual(0, after.friendly.hero.attacks_remaining)
        self.assertFalse(after.friendly.hero.can_attack)
        self.assertFalse(
            any(
                action.kind == ActionKind.ATTACK
                and action.source_entity_id == after.friendly.hero.entity_id
                for action in enumerate_legal_actions(after)
            )
        )

    def test_defending_lifesteal_and_reborn_are_resolved(self) -> None:
        game = state()
        game.opponent.hero.current_health = 20
        attacker = Card(
            "attacker", "A", "Attacker", CardType.MINION, attack=3, health=4,
            current_health=4, can_attack=True, attacks_remaining=1,
        )
        defender = Card(
            "defender", "D", "Defender", CardType.MINION, attack=2, health=3,
            current_health=3, lifesteal=True, reborn=True,
        )
        game.friendly.board.append(attacker)
        game.opponent.board.append(defender)
        action = next(
            item for item in enumerate_legal_actions(game)
            if item.kind == ActionKind.ATTACK and item.target_entity_id == "defender"
        )
        outcome = apply_action(game, action)
        reborn = outcome.state.opponent.board[0]
        self.assertEqual(22, outcome.state.opponent.hero.current_health)
        self.assertEqual(1, reborn.current_health)
        self.assertFalse(reborn.reborn)
        self.assertIn("modeled_reborn", {item.code for item in outcome.annotations})

    def test_coverage_scans_visible_opponent_board_and_weapons(self) -> None:
        game = state()
        game.opponent.board.append(
            Card(
                "enemy-complex", "E", "Enemy complex", CardType.MINION,
                attack=2, health=2, current_health=2,
                effect_coverage="unsupported", unsupported_effects=("deathrattle",),
            )
        )
        game.opponent.weapon = Card(
            "enemy-weapon", "EW", "Enemy weapon", CardType.WEAPON,
            attack=2, durability=2, current_durability=2,
            effect_coverage="unsupported", unsupported_effects=("weapon_trigger",),
        )
        entities = {item.entity_id for item in scan_state_coverage(game)}
        self.assertIn("enemy-complex", entities)
        self.assertIn("enemy-weapon", entities)


if __name__ == "__main__":
    unittest.main()
