from __future__ import annotations

import copy
from dataclasses import dataclass
from typing import Iterable

from .errors import IllegalActionError
from .schemas import (
    Action,
    ActionKind,
    Annotation,
    Card,
    CardType,
    Effect,
    GameState,
    PlayerState,
)


SUPPORTED_EFFECTS = {
    "damage",
    "damage_all_minions",
    "heal",
    "freeze",
    "armor",
    "gain_hero_attack",
    "draw",
    "summon",
    "buff_attack",
    "buff_health",
    "set_health",
    "gain_mana",
    "set_hero_power_cost",
    "double_one_cost_cards",
}

_AUTOMATIC_TARGET_MODES = {
    "all_enemy_characters",
    "all_friendly_characters",
    "all_enemy_minions",
    "all_friendly_minions",
    "all_minions",
    "all_characters",
    "all_other_minions",
    "all_other_friendly_minions",
}


@dataclass(frozen=True)
class SimulationOutcome:
    state: GameState
    annotations: tuple[Annotation, ...]
    ended_turn: bool = False


def _living(cards: Iterable[Card]) -> list[Card]:
    return [card for card in cards if card.current_health > 0]


def _active_minions(player: PlayerState) -> list[Card]:
    return [
        card
        for card in _living(player.board)
        if card.card_type == CardType.MINION and not card.dormant
    ]


def _characters(player: PlayerState) -> list[Card]:
    return [player.hero, *_active_minions(player)]


def _max_attacks(card: Card) -> int:
    maximum = 4 if card.mega_windfury else (2 if card.windfury else 1)
    extra = _tag_integer(card, "EXTRA_ATTACKS_THIS_TURN", 444)
    return maximum + max(0, extra or 0)


def _max_hero_attacks(actor: PlayerState) -> int:
    hero = actor.hero
    weapon = actor.weapon
    maximum = (
        4
        if hero.mega_windfury or bool(weapon and weapon.mega_windfury)
        else 2
        if hero.windfury or bool(weapon and weapon.windfury)
        else 1
    )
    extra = _tag_integer(hero, "EXTRA_ATTACKS_THIS_TURN", 444)
    return maximum + max(0, extra or 0)


def _tag_integer(card: Card, name: str, enum_id: int) -> int | None:
    for key, value in card.tags.items():
        if key.upper() not in {name, str(enum_id)}:
            continue
        if isinstance(value, bool):
            return int(value)
        if isinstance(value, int):
            return value
        if isinstance(value, float) and value.is_integer():
            return int(value)
        if isinstance(value, str):
            try:
                return int(value.strip())
            except ValueError:
                return None
        return None
    return None


def _named_tag_integer(card: Card, name: str) -> int | None:
    for key, value in card.tags.items():
        if key.upper() != name:
            continue
        if isinstance(value, bool):
            return int(value)
        if isinstance(value, int):
            return value
        if isinstance(value, float) and value.is_integer():
            return int(value)
        if isinstance(value, str):
            try:
                return int(value.strip())
            except ValueError:
                return None
        return None
    return None


def _set_tag_integer(card: Card, name: str, enum_id: int, value: int) -> None:
    numeric_name = str(enum_id)
    for key in list(card.tags):
        if key.upper() in {name, numeric_name}:
            card.tags[key] = value
            return
    card.tags[name] = value


def _tag_truthy(card: Card, name: str) -> bool:
    value = next((item for key, item in card.tags.items() if key.upper() == name), 0)
    if isinstance(value, str):
        return value.strip().lower() in {"1", "true", "yes"}
    return bool(value)


def _refresh_hero_attack_from_public_history(actor: PlayerState) -> None:
    """Re-open a temporary hero attack only when HDT exposed its attack count."""

    hero = actor.hero
    attacks_used = _tag_integer(hero, "NUM_ATTACKS_THIS_TURN", 297)
    if attacks_used is None:
        return
    maximum = _max_hero_attacks(actor)
    hero.attacks_remaining = max(0, maximum - max(0, attacks_used))
    weapon_blocked = bool(
        actor.weapon
        and (
            actor.weapon.current_durability <= 0
            or _tag_truthy(actor.weapon, "CANT_ATTACK")
        )
    )
    hero.can_attack = bool(
        hero.attack > 0
        and hero.current_health > 0
        and hero.attacks_remaining > 0
        and not _tag_truthy(hero, "FROZEN")
        and not hero.dormant
        and not _tag_truthy(hero, "EXHAUSTED")
        and not _tag_truthy(hero, "CANT_ATTACK")
        and not weapon_blocked
    )


def _increment_public_hero_attack_count(hero: Card) -> None:
    attacks_used = _tag_integer(hero, "NUM_ATTACKS_THIS_TURN", 297)
    if attacks_used is None:
        return
    for key in list(hero.tags):
        if key.upper() in {"NUM_ATTACKS_THIS_TURN", "297"}:
            hero.tags[key] = max(0, attacks_used) + 1
            return


def _enemy_targetable(card: Card) -> bool:
    return not card.stealth and not card.immune and not card.dormant


def _player_targetable_by_source(card: Card, source: Card) -> bool:
    elusive = _tag_truthy(card, "ELUSIVE")
    if source.card_type == CardType.SPELL:
        return not elusive and not _tag_truthy(card, "CANT_BE_TARGETED_BY_SPELLS")
    if source.card_type == CardType.HERO_POWER:
        return not elusive and not _tag_truthy(
            card, "CANT_BE_TARGETED_BY_HERO_POWERS"
        )
    return True


def _find_entity(state: GameState, entity_id: str) -> tuple[PlayerState, Card] | None:
    for player in (state.friendly, state.opponent):
        for card in [player.hero, *player.board, *player.hand]:
            if card.entity_id == entity_id:
                return player, card
        if player.hero_power and player.hero_power.entity_id == entity_id:
            return player, player.hero_power
        if player.weapon and player.weapon.entity_id == entity_id:
            return player, player.weapon
    return None


def _target_candidates(
    state: GameState, actor: PlayerState, mode: str, source: Card
) -> list[Card]:
    enemy = state.other_player(actor.player_id)
    if mode == "none":
        return []
    if mode == "self":
        found = _find_entity(state, source.entity_id)
        return [found[1]] if found else []
    if mode == "enemy_character":
        return [
            card
            for card in _characters(enemy)
            if _enemy_targetable(card) and _player_targetable_by_source(card, source)
        ]
    if mode == "friendly_character":
        return [
            card
            for card in _characters(actor)
            if _player_targetable_by_source(card, source)
        ]
    if mode == "any_character":
        return [
            card
            for card in _characters(actor)
            if _player_targetable_by_source(card, source)
        ] + [
            card
            for card in _characters(enemy)
            if _enemy_targetable(card) and _player_targetable_by_source(card, source)
        ]
    if mode == "enemy_minion":
        return [
            card
            for card in _active_minions(enemy)
            if _enemy_targetable(card) and _player_targetable_by_source(card, source)
        ]
    if mode == "friendly_minion":
        return [
            card
            for card in _active_minions(actor)
            if _player_targetable_by_source(card, source)
        ]
    if mode == "any_minion":
        return [
            card
            for card in _active_minions(actor)
            if _player_targetable_by_source(card, source)
        ] + [
            card
            for card in _active_minions(enemy)
            if _enemy_targetable(card) and _player_targetable_by_source(card, source)
        ]
    if mode == "any_undamaged_minion":
        return [
            card
            for card in _active_minions(actor)
            if card.current_health_known
            and card.current_health == card.health
            and _player_targetable_by_source(card, source)
        ] + [
            card
            for card in _active_minions(enemy)
            if (
                _enemy_targetable(card)
                and card.current_health_known
                and card.current_health == card.health
                and _player_targetable_by_source(card, source)
            )
        ]
    if mode == "damaged_enemy_minion":
        return [
            card
            for card in _active_minions(enemy)
            if _enemy_targetable(card)
            and card.current_health_known
            and card.current_health < card.health
            and _player_targetable_by_source(card, source)
        ]
    if mode == "enemy_hero":
        return (
            [enemy.hero]
            if _enemy_targetable(enemy.hero)
            and _player_targetable_by_source(enemy.hero, source)
            else []
        )
    if mode == "friendly_hero":
        return [actor.hero] if _player_targetable_by_source(actor.hero, source) else []
    return []


def _automatic_effect_targets(
    state: GameState, actor: PlayerState, mode: str, source_entity_id: str
) -> list[tuple[PlayerState, Card]] | None:
    enemy = state.other_player(actor.player_id)

    def owned(
        owner: PlayerState, *, include_hero: bool, exclude_source: bool = False
    ) -> list[tuple[PlayerState, Card]]:
        targets = [(owner, owner.hero)] if include_hero and owner.hero.current_health > 0 else []
        targets.extend(
            (owner, card)
            for card in _active_minions(owner)
            if not exclude_source or card.entity_id != source_entity_id
        )
        return targets

    if mode == "all_enemy_characters":
        return owned(enemy, include_hero=True)
    if mode == "all_friendly_characters":
        return owned(actor, include_hero=True)
    if mode == "all_enemy_minions":
        return owned(enemy, include_hero=False)
    if mode == "all_friendly_minions":
        return owned(actor, include_hero=False)
    if mode == "all_minions":
        return owned(actor, include_hero=False) + owned(enemy, include_hero=False)
    if mode == "all_characters":
        return owned(actor, include_hero=True) + owned(enemy, include_hero=True)
    if mode == "all_other_minions":
        return owned(
            actor, include_hero=False, exclude_source=True
        ) + owned(enemy, include_hero=False, exclude_source=True)
    if mode == "all_other_friendly_minions":
        return owned(actor, include_hero=False, exclude_source=True)
    return None


def _primary_target_mode(card: Card) -> str:
    modes = [
        effect.target
        for effect in card.effects
        if effect.target not in {"none", "self"}
        and effect.target not in _AUTOMATIC_TARGET_MODES
    ]
    return modes[0] if modes else "none"


def enumerate_legal_actions(state: GameState) -> list[Action]:
    """Enumerate actions modeled by the conservative generic rules engine.

    This deliberately does not try to infer Hearthstone card text. Cards with unknown
    effects remain playable, but applying them produces an explicit approximation.
    """

    actor = state.player(state.active_player_id)
    enemy = state.other_player(actor.player_id)
    if actor.hero.current_health <= 0 or enemy.hero.current_health <= 0:
        return []

    actions: list[Action] = []

    enemy_minions = [card for card in _active_minions(enemy) if _enemy_targetable(card)]
    taunts = [card for card in enemy_minions if card.taunt]
    attackers = [actor.hero, *_active_minions(actor)]
    for attacker in attackers:
        if attacker.attack <= 0 or not attacker.can_attack or attacker.attacks_remaining <= 0:
            continue
        attack_targets = list(taunts or enemy_minions)
        rush_restricted = attacker.rush and attacker.summoned_this_turn and not attacker.charge
        if not taunts and not rush_restricted and _enemy_targetable(enemy.hero):
            attack_targets.append(enemy.hero)
        for target in attack_targets:
            actions.append(
                Action(
                    ActionKind.ATTACK,
                    attacker.entity_id,
                    target.entity_id,
                    attacker.card_id,
                    f"Attack {target.name} with {attacker.name}",
                )
            )

    for card in actor.hand:
        if not card.playable or card.cost > actor.mana:
            continue
        placement_card = card.card_type in {CardType.MINION, CardType.LOCATION}
        if placement_card and len(actor.board) >= 7:
            continue
        board_positions = range(1, len(actor.board) + 2) if placement_card else (0,)
        # A Location's text describes its later activation, not its placement.
        target_mode = "none" if card.card_type == CardType.LOCATION else _primary_target_mode(card)
        if target_mode == "none" or target_mode == "self":
            target_ids = ("",)
        else:
            target_ids = tuple(
                target.entity_id
                for target in _target_candidates(
                    state, actor, target_mode, card
                )
            )
        for target_id in target_ids:
            for board_position in board_positions:
                position_text = (
                    f" at position {board_position}" if board_position else ""
                )
                actions.append(
                    Action(
                        ActionKind.PLAY_CARD,
                        card.entity_id,
                        target_id,
                        card.card_id,
                        f"Play {card.name}{position_text}",
                        board_position,
                    )
                )

    power = actor.hero_power
    if power and actor.hero_power_available and power.cost <= actor.mana:
        target_mode = _primary_target_mode(power)
        if target_mode == "none" or target_mode == "self":
            actions.append(
                Action(ActionKind.HERO_POWER, power.entity_id, "", power.card_id, f"Use {power.name}")
            )
        else:
            for target in _target_candidates(state, actor, target_mode, power):
                actions.append(
                    Action(
                        ActionKind.HERO_POWER,
                        power.entity_id,
                        target.entity_id,
                        power.card_id,
                        f"Use {power.name}",
                    )
                )

    actions.append(Action(ActionKind.END_TURN, text="End turn"))
    return actions


def _annotation(
    code: str,
    detail: str,
    card: Card | None = None,
    *,
    severity: str = "warning",
) -> Annotation:
    return Annotation(
        code=code,
        detail=detail,
        entity_id=card.entity_id if card else "",
        severity=severity,
    )


def _damage_character(owner: PlayerState, target: Card, amount: int) -> int:
    if amount <= 0:
        return 0
    if target.immune:
        return 0
    if target.divine_shield:
        target.divine_shield = False
        return 0
    dealt = amount
    if target.card_type == CardType.HERO and owner.armor > 0:
        absorbed = min(owner.armor, amount)
        owner.armor -= absorbed
        amount -= absorbed
    target.current_health = max(0, target.current_health - amount)
    return dealt


def _heal_character(target: Card, amount: int) -> None:
    target.current_health = min(target.health, target.current_health + max(0, amount))


def _remove_dead(state: GameState, annotations: list[Annotation]) -> None:
    while True:
        queued: list[tuple[PlayerState, Card]] = []
        for player in (state.friendly, state.opponent):
            survivors: list[Card] = []
            for card in player.board:
                if card.card_type == CardType.LOCATION or card.current_health > 0:
                    survivors.append(card)
                else:
                    queued.append((player, card))
            player.board = survivors
        if not queued:
            return
        for player, card in queued:
            player.graveyard.append(copy.deepcopy(card))
            modeled = [
                effect for effect in card.effects if effect.trigger == "deathrattle"
            ]
            if not modeled and (
                _tag_truthy(card, "DEATHRATTLE")
                or "deathrattle" in card.unsupported_effects
            ):
                annotations.append(
                    _annotation(
                        "unsupported_deathrattle",
                        f"Deathrattle for {card.name} is not simulated.",
                        card,
                    )
                )
            for effect in modeled:
                _apply_effect(state, player, card, effect, "", annotations)
            if card.reborn:
                card.current_health = 1
                card.divine_shield = False
                card.reborn = False
                card.can_attack = False
                card.attacks_remaining = 0
                card.stealth = False
                card.summoned_this_turn = True
                player.board.append(card)
                annotations.append(
                    _annotation(
                        "modeled_reborn",
                        f"{card.name} returned with 1 Health using the visible generic Reborn rule.",
                        card,
                        severity="info",
                    )
                )


def _resolve_board_trigger(
    state: GameState,
    actor: PlayerState,
    trigger: str,
    annotations: list[Annotation],
) -> None:
    queued = [
        (copy.deepcopy(source), effect)
        for source in [*actor.board, *([actor.weapon] if actor.weapon else [])]
        for effect in source.effects
        if effect.trigger == trigger
    ]
    for source, effect in queued:
        _apply_effect(state, actor, source, effect, "", annotations)
        _remove_dead(state, annotations)


def _hero_power_cost_aura_profile(
    owner: PlayerState,
) -> tuple[tuple[int, int] | None, Card | None, str | None]:
    profile: tuple[int, int] | None = None
    source: Card | None = None
    for card in owner.board:
        if card.current_health <= 0 or card.dormant:
            continue
        for effect in card.effects:
            if effect.kind != "set_hero_power_cost":
                continue
            if effect.amount < 0 or effect.hand_count_at_most is None:
                return None, card, "英雄技能费用光环缺少有效的公开费用或手牌数条件。"
            current = (effect.amount, effect.hand_count_at_most)
            if profile is not None and profile != current:
                return None, card, "多个不同的英雄技能费用光环需要尚未建模的结算层级。"
            profile = current
            source = card
    return profile, source, None


def _one_cost_card_doubling_profile(
    owner: PlayerState,
) -> tuple[int, Card | None, str | None]:
    triggers = 0
    source: Card | None = None
    for card in owner.board:
        if card.current_health <= 0 or card.dormant:
            continue
        for effect in card.effects:
            if effect.kind != "double_one_cost_cards":
                continue
            if effect.amount != 2 or effect.target != "none":
                return 0, card, "1费牌翻倍触发器缺少有效的公开规则。"
            triggers += 1
            source = card
    return triggers, source, None


def _one_cost_multiplier(trigger_count: int) -> int:
    return min(65_535, 2 ** max(0, trigger_count))


def _unmodified_hero_power_cost(owner: PlayerState) -> int | None:
    if owner.hero_power is None:
        return None
    value = _named_tag_integer(owner.hero_power, "TAG_LAST_KNOWN_COST_IN_HAND")
    return value if value is not None and value >= 0 else None


def _continuous_effect_annotations(state: GameState) -> list[Annotation]:
    annotations: list[Annotation] = []
    for owner in (state.friendly, state.opponent):
        _, doubler_source, doubler_error = _one_cost_card_doubling_profile(owner)
        if doubler_error is not None:
            annotations.append(
                _annotation(
                    "unsupported_continuous_effect",
                    doubler_error,
                    doubler_source,
                )
            )
        profile, source, error = _hero_power_cost_aura_profile(owner)
        if error is not None:
            annotations.append(
                _annotation("unsupported_continuous_effect", error, source)
            )
            continue
        if profile is None:
            continue
        power = owner.hero_power
        base_cost = _unmodified_hero_power_cost(owner)
        if power is None or base_cost is None:
            annotations.append(
                _annotation(
                    "missing_hero_power_base_cost",
                    "英雄技能费用光环缺少 TAG_LAST_KNOWN_COST_IN_HAND 基础费用证据。",
                    power or source,
                )
            )
            continue
        expected = profile[0] if len(owner.hand) <= profile[1] else base_cost
        if power.cost != expected:
            annotations.append(
                _annotation(
                    "unsupported_continuous_effect_state",
                    f"英雄技能费用光环期望费用为 {expected}，但 HDT 当前公开费用为 {power.cost}。",
                    power,
                )
            )
    return annotations


def _reconcile_continuous_effects(
    before: GameState,
    next_state: GameState,
    annotations: list[Annotation],
) -> None:
    for player_id in (before.friendly.player_id, before.opponent.player_id):
        previous_owner = before.player(player_id)
        owner = next_state.player(player_id)
        previous_profile, previous_source, previous_error = (
            _hero_power_cost_aura_profile(previous_owner)
        )
        current_profile, current_source, current_error = (
            _hero_power_cost_aura_profile(owner)
        )
        error = current_error or previous_error
        if error is not None:
            annotations.append(
                _annotation(
                    "unsupported_continuous_effect",
                    error,
                    current_source or previous_source,
                )
            )
            continue
        if previous_profile is None and current_profile is None:
            continue
        power = owner.hero_power
        base_cost = _unmodified_hero_power_cost(owner)
        if power is None or base_cost is None:
            annotations.append(
                _annotation(
                    "missing_hero_power_base_cost",
                    "无法重算英雄技能费用：缺少 TAG_LAST_KNOWN_COST_IN_HAND 基础费用证据。",
                    power or current_source or previous_source,
                )
            )
            continue
        desired = (
            current_profile[0]
            if current_profile is not None and len(owner.hand) <= current_profile[1]
            else base_cost
        )
        power.cost = desired
        _set_tag_integer(power, "COST", 48, desired)


def _draw_card(player: PlayerState, state: GameState, annotations: list[Annotation]) -> None:
    if player.deck_size <= 0:
        player.fatigue += 1
        _damage_character(player, player.hero, player.fatigue)
        return
    player.deck_size -= 1
    if len(player.hand) >= 10:
        annotations.append(_annotation("unknown_burn", "The identity of the burned card is unknown."))
        return
    sequence = 0
    existing_ids = {
        card.entity_id
        for owner in (state.friendly, state.opponent)
        for card in [owner.hero, *owner.hand, *owner.board]
    }
    while f"unknown-draw-{player.player_id}-{state.turn}-{sequence}" in existing_ids:
        sequence += 1
    placeholder = Card(
        entity_id=f"unknown-draw-{player.player_id}-{state.turn}-{sequence}",
        card_id="UNKNOWN_DRAW",
        name="Unknown drawn card",
        card_type=CardType.UNKNOWN,
        cost=99,
        effect_coverage="unsupported",
        unsupported_effects=("hidden_identity",),
    )
    player.hand.append(placeholder)
    annotations.append(
        _annotation(
            "hidden_draw_identity",
            "Draw count is simulated, but the drawn card is not determinized.",
            placeholder,
        )
    )


def _apply_resolved_character_effect(
    actor: PlayerState,
    source: Card,
    effect: Effect,
    owner: PlayerState,
    target: Card,
) -> bool:
    if effect.kind == "damage":
        amount = effect.amount
        if source.card_type == CardType.SPELL:
            amount += max(0, actor.spell_power)
        self_hero_overkill = 0
        if target.entity_id == actor.hero.entity_id:
            damage_after_armor = max(0, amount - actor.armor)
            self_hero_overkill = max(0, damage_after_armor - actor.hero.current_health)
        dealt = _damage_character(owner, target, amount)
        if source.lifesteal and dealt > 0:
            _heal_character(actor.hero, max(0, dealt - self_hero_overkill))
        return True
    if effect.kind == "heal":
        _heal_character(target, effect.amount)
        return True
    if effect.kind == "freeze":
        if target.card_type in {CardType.HERO, CardType.MINION}:
            target.frozen = True
            target.can_attack = False
        return True
    if effect.kind == "buff_attack":
        target.attack = max(0, target.attack + effect.amount)
        return True
    if effect.kind == "buff_health":
        target.health = max(1, target.health + effect.amount)
        target.current_health = max(1, target.current_health + effect.amount)
        return True
    if effect.kind == "set_health":
        target.health = max(1, effect.amount)
        target.current_health = max(1, effect.amount)
        return True
    return False


def _apply_effect(
    state: GameState,
    actor: PlayerState,
    source: Card,
    effect: Effect,
    target_id: str,
    annotations: list[Annotation],
) -> None:
    if effect.random:
        annotations.append(
            _annotation(
                "unsupported_random_effect",
                f"Random {effect.kind} effect on {source.name} was not sampled.",
                source,
            )
        )
        return
    if effect.kind not in SUPPORTED_EFFECTS:
        annotations.append(
            _annotation(
                "unsupported_effect",
                f"Effect {effect.kind!r} on {source.name} is not implemented.",
                source,
            )
        )
        return

    if effect.kind in {"set_hero_power_cost", "double_one_cost_cards"}:
        # This is a continuous board aura. Repricing happens after the complete
        # action resolves. The one-cost trigger is consumed by the surrounding
        # card-play transition instead of resolving as a one-shot effect.
        return

    if effect.kind == "damage_all_minions":
        amount = effect.amount
        if source.card_type == CardType.SPELL:
            amount += max(0, actor.spell_power)
        for owner in (state.friendly, state.opponent):
            for target in list(_active_minions(owner)):
                _damage_character(owner, target, amount)
        return

    automatic_targets = _automatic_effect_targets(
        state, actor, effect.target, source.entity_id
    )
    if automatic_targets is not None:
        for owner, target in automatic_targets:
            _apply_resolved_character_effect(actor, source, effect, owner, target)
        return

    found = _find_entity(state, target_id) if target_id else None
    if effect.target != "none" and found is None:
        annotations.append(
            _annotation("missing_effect_target", f"Targeted effect on {source.name} could not be applied.", source)
        )
        return

    if found and _apply_resolved_character_effect(actor, source, effect, found[0], found[1]):
        return
    if effect.kind == "armor":
        actor.armor += max(0, effect.amount)
    elif effect.kind == "gain_hero_attack":
        actor.hero.attack += max(0, effect.amount)
        _refresh_hero_attack_from_public_history(actor)
    elif effect.kind == "draw":
        for _ in range(max(0, effect.amount or effect.count)):
            _draw_card(actor, state, annotations)
    elif effect.kind == "summon":
        for index in range(effect.count):
            if len(actor.board) >= 7:
                break
            effects_unmodeled = effect.summoned_card_effects_unmodeled
            generated = Card(
                entity_id=f"generated-{source.entity_id}-{state.turn}-{len(actor.board)}-{index}",
                card_id=effect.card_id or "GENERIC_TOKEN",
                name=effect.name,
                card_type=CardType.MINION,
                attack=effect.attack,
                health=effect.health,
                current_health=effect.health,
                playable=False,
                summoned_this_turn=True,
                can_attack=(effect.rush or effect.charge) and effect.attack > 0,
                attacks_remaining=(
                    (2 if effect.windfury else 1)
                    if (effect.rush or effect.charge) and effect.attack > 0
                    else 0
                ),
                rush=effect.rush,
                charge=effect.charge,
                taunt=effect.taunt,
                divine_shield=effect.divine_shield,
                stealth=effect.stealth,
                poisonous=effect.poisonous,
                lifesteal=effect.lifesteal,
                windfury=effect.windfury,
                reborn=effect.reborn,
                effect_coverage="unsupported" if effects_unmodeled else "exact",
                unsupported_effects=(
                    ("summoned_card_text_not_modeled",)
                    if effects_unmodeled
                    else ()
                ),
            )
            actor.board.append(generated)
    elif effect.kind == "gain_mana":
        actor.mana = min(actor.max_mana, actor.mana + max(0, effect.amount))


def _apply_card_effects(
    state: GameState,
    actor: PlayerState,
    source: Card,
    target_id: str,
    annotations: list[Annotation],
) -> None:
    if source.effect_coverage == "unsupported":
        annotations.append(
            _annotation(
                "unsupported_card_text",
                f"Card text for {source.name} is not represented; only generic costs/stats are applied.",
                source,
            )
        )
    for feature in source.unsupported_effects:
        annotations.append(
            _annotation(
                "unsupported_card_mechanic",
                f"{source.name}: {feature} is not simulated.",
                source,
            )
        )
    target_modes = {
        effect.target
        for effect in source.effects
        if effect.trigger == "resolution"
        if effect.target not in {"none", "self"}
        and effect.target not in _AUTOMATIC_TARGET_MODES
    }
    if len(target_modes) > 1:
        annotations.append(
            _annotation(
                "multiple_target_groups",
                f"{source.name} uses multiple target groups; the single supplied target is reused conservatively.",
                source,
            )
        )
    for effect in source.effects:
        if effect.trigger != "resolution":
            continue
        # Resolve deaths before a summon so a minion killed by an earlier effect
        # frees its board slot. Other compound effects keep the target entity
        # available until the card finishes resolving (damage + Freeze/buff).
        if effect.kind == "summon":
            _remove_dead(state, annotations)
        resolved_target = source.entity_id if effect.target == "self" else target_id
        _apply_effect(state, actor, source, effect, resolved_target, annotations)
    _remove_dead(state, annotations)


def _apply_attack(state: GameState, actor: PlayerState, action: Action, annotations: list[Annotation]) -> None:
    source_found = _find_entity(state, action.source_entity_id)
    target_found = _find_entity(state, action.target_entity_id)
    if not source_found or not target_found:
        raise IllegalActionError("attack source or target no longer exists")
    _, attacker = source_found
    target_owner, target = target_found
    attacker_owner = actor

    attacker_damage = attacker.attack
    retaliation = target.attack if target.card_type != CardType.HERO else 0
    dealt = _damage_character(target_owner, target, attacker_damage)
    received = _damage_character(attacker_owner, attacker, retaliation)
    weapon = actor.weapon if attacker.card_type == CardType.HERO else None
    attacker_poisonous = attacker.poisonous or bool(weapon and weapon.poisonous)
    attacker_lifesteal = attacker.lifesteal or bool(weapon and weapon.lifesteal)
    if attacker_poisonous and dealt > 0 and target.card_type == CardType.MINION:
        target.current_health = 0
    if target.poisonous and received > 0 and attacker.card_type == CardType.MINION:
        attacker.current_health = 0
    if attacker_lifesteal and dealt > 0:
        _heal_character(actor.hero, dealt)
    if target.lifesteal and received > 0:
        _heal_character(target_owner.hero, received)
    attacker.stealth = False
    if attacker.card_type == CardType.HERO:
        _increment_public_hero_attack_count(attacker)
    attacker.attacks_remaining = max(0, attacker.attacks_remaining - 1)
    attacker.can_attack = attacker.attacks_remaining > 0 and not attacker.frozen
    if attacker.card_type == CardType.HERO and weapon:
        weapon.current_durability = max(0, weapon.current_durability - 1)
        if weapon.current_durability <= 0:
            actor.weapon = None
            attacker.attack = max(0, attacker.attack - weapon.attack)
            if weapon.windfury or weapon.mega_windfury:
                # Hero keyword flags are intrinsic to the hero here; weapon
                # Windfury is read through actor.weapon and must not erase them.
                if _tag_integer(attacker, "NUM_ATTACKS_THIS_TURN", 297) is not None:
                    _refresh_hero_attack_from_public_history(actor)
                else:
                    attacker.attacks_remaining = 0
            if attacker.attack <= 0:
                attacker.attacks_remaining = 0
            attacker.can_attack = attacker.attacks_remaining > 0 and attacker.attack > 0
    _remove_dead(state, annotations)


def apply_action(state: GameState, action: Action, *, validate: bool = True) -> SimulationOutcome:
    next_state = copy.deepcopy(state)
    legal = enumerate_legal_actions(next_state)
    if validate and action.action_id not in {candidate.action_id for candidate in legal}:
        raise IllegalActionError(f"illegal action: {action.action_id}")

    actor = next_state.player(next_state.active_player_id)
    enemy = next_state.other_player(actor.player_id)
    annotations: list[Annotation] = []

    if action.kind == ActionKind.END_TURN:
        _resolve_board_trigger(next_state, actor, "turn_end", annotations)
        next_state.active_player_id = enemy.player_id
        next_state.turn += 1
        return SimulationOutcome(next_state, (), True)

    if action.kind == ActionKind.ATTACK:
        _apply_attack(next_state, actor, action, annotations)
        _reconcile_continuous_effects(state, next_state, annotations)
        return SimulationOutcome(next_state, tuple(annotations))

    if action.kind == ActionKind.PLAY_CARD:
        card = next((item for item in actor.hand if item.entity_id == action.source_entity_id), None)
        if card is None:
            raise IllegalActionError("card is not in the active player's hand")
        one_cost_triggers = 0
        if card.cost == 1:
            one_cost_triggers, trigger_source, trigger_error = (
                _one_cost_card_doubling_profile(actor)
            )
            if trigger_error is not None:
                annotations.append(
                    _annotation(
                        "unsupported_continuous_effect",
                        trigger_error,
                        trigger_source,
                    )
                )
                one_cost_triggers = 0
        actor.hand.remove(card)
        actor.mana -= card.cost
        placement_card = card.card_type in {CardType.MINION, CardType.LOCATION}
        if placement_card:
            if not 1 <= action.board_position <= len(actor.board) + 1:
                raise IllegalActionError("board position is outside the legal range")
            actor.board.insert(action.board_position - 1, card)
        elif action.board_position != 0:
            raise IllegalActionError("this card type does not use a board position")
        if card.card_type == CardType.MINION:
            card.summoned_this_turn = True
            card.can_attack = bool(
                card.attack > 0
                and not card.frozen
                and not card.dormant
                and (card.charge or card.rush)
            )
            card.attacks_remaining = _max_attacks(card) if card.can_attack else 0
        elif card.card_type == CardType.WEAPON:
            if card.current_durability <= 0:
                card.current_durability = card.durability
            if actor.weapon is not None:
                actor.hero.attack = max(0, actor.hero.attack - actor.weapon.attack)
            actor.weapon = card
            actor.hero.attack += card.attack
            _refresh_hero_attack_from_public_history(actor)
        elif card.card_type == CardType.UNKNOWN:
            annotations.append(
                _annotation(
                    "unsupported_card_type",
                    f"{card.card_type.value} behavior for {card.name} is not modeled.",
                    card,
                )
            )
        elif card.card_type == CardType.LOCATION and card.effect_coverage != "exact":
            annotations.append(
                _annotation(
                    "unsupported_card_type",
                    f"LOCATION behavior for {card.name} is not modeled.",
                    card,
                )
            )
        if card.card_type != CardType.LOCATION:
            repetitions = (
                _one_cost_multiplier(one_cost_triggers)
                if card.card_type == CardType.SPELL
                else 1
            )
            for _ in range(repetitions):
                _apply_card_effects(
                    next_state,
                    actor,
                    card,
                    action.target_entity_id,
                    annotations,
                )
                if card.card_type == CardType.SPELL:
                    _resolve_board_trigger(
                        next_state, actor, "after_spell_cast", annotations
                    )
        if card.card_type == CardType.MINION and one_cost_triggers:
            multiplier = _one_cost_multiplier(one_cost_triggers)
            card.attack = min(65_535, card.attack * multiplier)
            card.health = min(65_535, card.health * multiplier)
            card.current_health = min(65_535, card.current_health * multiplier)
            _set_tag_integer(card, "ATK", 47, card.attack)
            _set_tag_integer(card, "HEALTH", 45, card.health)
        if card.card_type == CardType.SPELL:
            actor.graveyard.append(card)
        _remove_dead(next_state, annotations)
        _reconcile_continuous_effects(state, next_state, annotations)
        return SimulationOutcome(next_state, tuple(annotations))

    if action.kind == ActionKind.LOCATION_ACTIVATE:
        location = next(
            (
                item
                for item in actor.board
                if item.entity_id == action.source_entity_id
                and item.card_type == CardType.LOCATION
            ),
            None,
        )
        if location is None or location.current_health <= 0:
            raise IllegalActionError("location is not available on the active player's board")
        _apply_card_effects(
            next_state, actor, location, action.target_entity_id, annotations
        )
        location.current_health = max(0, location.current_health - 1)
        if location.current_health == 0:
            actor.board.remove(location)
        _reconcile_continuous_effects(state, next_state, annotations)
        return SimulationOutcome(next_state, tuple(annotations))

    if action.kind == ActionKind.HERO_POWER:
        power = actor.hero_power
        if power is None:
            raise IllegalActionError("active player has no hero power")
        actor.mana -= power.cost
        actor.hero_power_available = False
        _apply_card_effects(next_state, actor, power, action.target_entity_id, annotations)
        _remove_dead(next_state, annotations)
        _reconcile_continuous_effects(state, next_state, annotations)
        return SimulationOutcome(next_state, tuple(annotations))

    raise IllegalActionError(f"unhandled action kind: {action.kind.value}")


def advance_to_start_of_turn(state: GameState) -> SimulationOutcome:
    """Apply the explicitly approximate between-turn refresh used by offline self-play.

    The live turn solver never calls this helper. It exists only so bounded generic
    self-play fixtures can alternate actors without pretending to implement every
    Hearthstone start-of-turn trigger.
    """

    next_state = copy.deepcopy(state)
    actor = next_state.player(next_state.active_player_id)
    actor.max_mana = min(10, actor.max_mana + 1)
    actor.mana = actor.max_mana
    actor.hero_power_available = actor.hero_power is not None
    for card in actor.board:
        card.summoned_this_turn = False
        attacks = _max_attacks(card)
        card.attacks_remaining = (
            attacks if card.attack > 0 and not card.frozen and not card.dormant else 0
        )
        card.can_attack = card.attacks_remaining > 0
    actor.hero.summoned_this_turn = False
    actor.hero.attacks_remaining = (
        _max_hero_attacks(actor)
        if actor.hero.attack > 0 and not actor.hero.frozen
        else 0
    )
    actor.hero.can_attack = actor.hero.attacks_remaining > 0
    annotations = [
        _annotation(
            "approximate_turn_refresh",
            "Offline self-play refreshes mana/attacks generically; start-of-turn triggers are not simulated.",
        )
    ]
    _resolve_board_trigger(next_state, actor, "turn_start", annotations)
    _draw_card(actor, next_state, annotations)
    _reconcile_continuous_effects(state, next_state, annotations)
    return SimulationOutcome(next_state, tuple(annotations))


def scan_state_coverage(state: GameState) -> tuple[Annotation, ...]:
    annotations: list[Annotation] = _continuous_effect_annotations(state)
    actor = state.player(state.active_player_id)
    enemy = state.other_player(actor.player_id)
    cards = [*actor.hand, *actor.board, *enemy.board]
    if actor.hero_power:
        cards.append(actor.hero_power)
    if enemy.hero_power:
        cards.append(enemy.hero_power)
    if actor.weapon:
        cards.append(actor.weapon)
    if enemy.weapon:
        cards.append(enemy.weapon)
    for card in cards:
        if card.effect_coverage == "unsupported":
            annotations.append(
                _annotation("unsupported_card_text", f"Card text for {card.name} is unavailable.", card)
            )
        for effect in card.effects:
            if effect.kind not in SUPPORTED_EFFECTS or effect.random:
                annotations.append(
                    _annotation(
                        "unsupported_effect",
                        f"{card.name} uses unsupported or random effect {effect.kind!r}.",
                        card,
                    )
                )
            if effect.summoned_card_effects_unmodeled:
                annotations.append(
                    _annotation(
                        "unsupported_summoned_card_text",
                        f"{card.name} summons a card whose later text is not fully modeled.",
                        card,
                    )
                )
        for mechanic in card.unsupported_effects:
            annotations.append(
                _annotation("unsupported_card_mechanic", f"{card.name}: {mechanic}.", card)
            )
        if _tag_truthy(card, "DEATHRATTLE") and "deathrattle" not in card.unsupported_effects:
            annotations.append(
                _annotation(
                    "unsupported_card_mechanic",
                    f"{card.name}: deathrattle is not simulated.",
                    card,
                )
            )
    unsupported_count = state.metadata.get("unsupported_feature_count", 0)
    unknown_count = state.metadata.get("unknown_data_count", 0)
    if isinstance(unsupported_count, int) and unsupported_count > 0:
        annotations.append(
            _annotation(
                "unsupported_snapshot_features",
                f"HDT reported {unsupported_count} unsupported snapshot feature(s).",
            )
        )
    if isinstance(unknown_count, int) and unknown_count > 0:
        annotations.append(
            _annotation(
                "unknown_snapshot_data",
                f"HDT reported {unknown_count} unknown or intentionally hidden snapshot-data entry/entries.",
            )
        )
    unique = {(item.code, item.entity_id, item.detail): item for item in annotations}
    return tuple(unique.values())
