from __future__ import annotations

import copy
import hashlib
import json
import math
import statistics
import time
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from .schemas import Action, ActionKind, Card, CardType, GameState, SolveRequest


TURNPAIR_SUITE_ID = "oracle-turnpair-v1"
TURNPAIR_SCHEMA_VERSION = 1
TACTICAL_SCORE_KIND = "counterplay_tactical_state_value"
RESPONSE_KIND = "minimax_best_response"
RESPONSE_SCOPE = "visible_generic_turnpair_v1"
WIN_UTILITY = 1_000_000
LOSS_UTILITY = -1_000_000
MISSING_LINE_REGRET = WIN_UTILITY - LOSS_UTILITY
MAX_ENUMERATED_NODES = 20_000
MAX_LINE_DEPTH = 12
NEAR_OPTIMAL_REGRET_MAX = 100

_AUTOMATIC_ORACLE_TARGET_MODES = {
    "all_enemy_characters",
    "all_friendly_characters",
    "all_enemy_minions",
    "all_friendly_minions",
    "all_minions",
    "all_characters",
    "all_other_minions",
    "all_other_friendly_minions",
}


class TurnPairEvaluationError(ValueError):
    pass


@dataclass(frozen=True)
class OracleActionAssessment:
    legal: bool
    complete: bool
    state: GameState
    ended_turn: bool
    assessed_action_count: int
    legal_action_count: int
    error: str = ""


@dataclass(frozen=True)
class OracleCompleteLine:
    actions: tuple[Action, ...]
    state: GameState
    ended_turn: bool


@dataclass(frozen=True)
class OracleActionOutcome:
    state: GameState
    ended_turn: bool
    probability: Fraction


@dataclass(frozen=True)
class OracleVisibleChancePolicy:
    expected_utility: Fraction
    minimum_utility: int
    maximum_utility: int
    survival_probability: Fraction
    actions: tuple[Action, ...]
    opponent_response: tuple[Action, ...]
    terminal_state: GameState
    recompute_after_random_outcome: bool
    explored_nodes: int = 0


@dataclass(frozen=True)
class OracleTurnPairLine:
    actions: tuple[Action, ...]
    opponent_response: tuple[Action, ...]
    minimax_value: int
    safe_after_response: bool
    immediate_lethal: bool

    @property
    def first_action_id(self) -> str:
        return _first_action_id(self.actions)


@dataclass(frozen=True)
class OracleTurnPairProof:
    abstained: bool
    reasons: tuple[str, ...]
    lines: tuple[OracleTurnPairLine, ...]
    optimal_value: int = LOSS_UTILITY
    optimal_first_action_ids: tuple[str, ...] = ()
    explored_friendly_nodes: int = 0
    explored_response_nodes: int = 0


@dataclass(frozen=True)
class CandidateLineAssessment:
    action_assessment: OracleActionAssessment
    minimax_value: int | None
    safe_after_response: bool | None
    immediate_lethal: bool
    response_start: GameState | None
    worst_response: tuple[Action, ...]
    explored_response_nodes: int = 0


def _canonical_hash(value: Any) -> str:
    payload = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _rate(numerator: int, denominator: int, *, empty: float = 1.0) -> float:
    if denominator <= 0:
        return empty
    return round(numerator / denominator, 6)


def _percentile(values: Sequence[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(float(value) for value in values)
    index = max(0, min(len(ordered) - 1, int((len(ordered) - 1) * fraction + 0.999999)))
    return round(ordered[index], 3)


def _as_mapping(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise TurnPairEvaluationError(f"{path} must be an object")
    return value


def _as_list(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list):
        raise TurnPairEvaluationError(f"{path} must be an array")
    return value


def load_turnpair_suite(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    try:
        raw = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise TurnPairEvaluationError(f"could not load turn-pair fixture suite: {source}") from exc
    root = dict(_as_mapping(raw, "suite"))
    if root.get("schema_version") != TURNPAIR_SCHEMA_VERSION:
        raise TurnPairEvaluationError("unsupported turn-pair fixture schema_version")
    if root.get("suite_id") != TURNPAIR_SUITE_ID:
        raise TurnPairEvaluationError(f"suite_id must be {TURNPAIR_SUITE_ID!r}")
    fixtures = _as_list(root.get("fixtures"), "suite.fixtures")
    if not fixtures:
        raise TurnPairEvaluationError("turn-pair fixture suite must not be empty")
    seen: set[str] = set()
    normalized: list[dict[str, Any]] = []
    for index, item in enumerate(fixtures):
        fixture = dict(_as_mapping(item, f"suite.fixtures[{index}]"))
        fixture_id = fixture.get("id")
        if not isinstance(fixture_id, str) or not fixture_id.strip():
            raise TurnPairEvaluationError(f"suite.fixtures[{index}].id must be non-empty")
        if fixture_id in seen:
            raise TurnPairEvaluationError(f"duplicate fixture id: {fixture_id}")
        seen.add(fixture_id)
        if fixture.get("scope") not in {"exact", "approximate", "abstain"}:
            raise TurnPairEvaluationError(f"fixture {fixture_id!r} has invalid scope")
        _as_mapping(fixture.get("position"), f"fixture {fixture_id}.position")
        normalized.append(fixture)
    thresholds = root.get("thresholds", {})
    if not isinstance(thresholds, Mapping):
        raise TurnPairEvaluationError("suite.thresholds must be an object")
    root["fixtures"] = normalized
    root["thresholds"] = dict(thresholds)
    return root


def _card_payload(
    value: Any,
    *,
    fallback_id: str,
    default_type: str = "MINION",
    default_health: int = 1,
) -> dict[str, Any]:
    raw = dict(_as_mapping(value, fallback_id))
    health = raw.get("health", default_health)
    if isinstance(health, bool) or not isinstance(health, int):
        raise TurnPairEvaluationError(f"{fallback_id}.health must be an integer")
    current_health = raw.get("current_health", health)
    can_attack = raw.get("can_attack", False)
    return {
        "entity_id": str(raw.get("entity_id") or fallback_id),
        "card_id": str(raw.get("card_id") or f"EVAL_{fallback_id.upper()}"),
        "name": str(raw.get("name") or fallback_id),
        "card_type": str(raw.get("card_type") or default_type).upper(),
        "cost": raw.get("cost", 0),
        "attack": raw.get("attack", 0),
        "health": health,
        "current_health": current_health,
        "playable": raw.get("playable", True),
        "can_attack": can_attack,
        "attacks_remaining": raw.get("attacks_remaining", 1 if can_attack else 0),
        "taunt": raw.get("taunt", False),
        "divine_shield": raw.get("divine_shield", False),
        "stealth": raw.get("stealth", False),
        "poisonous": raw.get("poisonous", False),
        "lifesteal": raw.get("lifesteal", False),
        "windfury": raw.get("windfury", False),
        "mega_windfury": raw.get("mega_windfury", False),
        "rush": raw.get("rush", False),
        "charge": raw.get("charge", False),
        "reborn": raw.get("reborn", False),
        "dormant": raw.get("dormant", False),
        "immune": raw.get("immune", False),
        "summoned_this_turn": raw.get("summoned_this_turn", False),
        "durability": raw.get("durability", 0),
        "current_durability": raw.get("current_durability", raw.get("durability", 0)),
        "effects": raw.get("effects", []),
        "effect_coverage": raw.get("effect_coverage", "exact"),
        "unsupported_effects": raw.get("unsupported_effects", []),
        "tags": raw.get("tags", {}),
    }


def _player_payload(value: Any, player_id: str) -> dict[str, Any]:
    raw = dict(_as_mapping(value, player_id))
    hero_raw = raw.get("hero", {})
    if not isinstance(hero_raw, Mapping):
        raise TurnPairEvaluationError(f"{player_id}.hero must be an object")
    hero = _card_payload(
        {
            "entity_id": f"{player_id}-hero",
            "card_id": f"HERO_{player_id.upper()}",
            "name": f"Hero {player_id}",
            "card_type": "HERO",
            "health": 30,
            "current_health": 30,
            **dict(hero_raw),
        },
        fallback_id=f"{player_id}-hero",
        default_type="HERO",
        default_health=30,
    )
    hand = [
        _card_payload(item, fallback_id=f"{player_id}-hand-{index}", default_type="SPELL")
        for index, item in enumerate(_as_list(raw.get("hand", []), f"{player_id}.hand"))
    ]
    board = [
        _card_payload(item, fallback_id=f"{player_id}-board-{index}")
        for index, item in enumerate(_as_list(raw.get("board", []), f"{player_id}.board"))
    ]
    mana = raw.get("mana", 0)
    return {
        "player_id": player_id,
        "hero": hero,
        "mana": mana,
        "max_mana": raw.get("max_mana", mana),
        "armor": raw.get("armor", 0),
        "hand": hand,
        "board": board,
        "deck_size": raw.get("deck_size", 0),
        "fatigue": raw.get("fatigue", 0),
        "hero_power": None,
        "hero_power_available": False,
        "weapon": None,
    }


def request_from_turnpair_fixture(fixture: Mapping[str, Any], seed: int = 0) -> SolveRequest:
    fixture_id = str(fixture.get("id") or "turnpair-fixture")
    position = _as_mapping(fixture.get("position"), f"fixture {fixture_id}.position")
    friendly = _player_payload(position.get("friendly", {}), "friendly")
    opponent = _player_payload(position.get("opponent", {}), "opponent")
    request = {
        "api_version": "1.0",
        "request_id": f"turnpair:{fixture_id}",
        "state": {
            "state_id": f"turnpair-state:{fixture_id}",
            "turn": position.get("turn", 1),
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": friendly,
            "opponent": opponent,
            "patch": TURNPAIR_SUITE_ID,
            "mode": "evaluation",
            "rng_seed": seed + int(fixture.get("seed_offset", 0)),
            "metadata": {"turnpair_fixture_id": fixture_id},
        },
        "options": {
            "time_budget_ms": 250,
            "max_iterations": 5000,
            "max_depth": MAX_LINE_DEPTH,
            "top_k": 3,
            "allow_approximate_effects": True,
        },
    }
    return SolveRequest.from_dict(request)


def _living(cards: Sequence[Card]) -> list[Card]:
    return [
        card
        for card in cards
        if card.card_type == CardType.LOCATION or card.current_health > 0
    ]


def _terminal(state: GameState) -> bool:
    return state.friendly.hero.current_health <= 0 or state.opponent.hero.current_health <= 0


def _find_entity(state: GameState, entity_id: str) -> tuple[Any, Card] | None:
    for player in (state.friendly, state.opponent):
        for card in (player.hero, *player.board, *player.hand):
            if card.entity_id == entity_id:
                return player, card
    return None


def _oracle_active_minions(cards: Sequence[Card]) -> list[Card]:
    return [
        card
        for card in _living(cards)
        if card.card_type == CardType.MINION and not card.dormant
    ]


def _oracle_characters(player: Any) -> list[Card]:
    return [player.hero, *_oracle_active_minions(player.board)]


def _oracle_target_candidates(
    state: GameState, actor: Any, mode: str, source_entity_id: str
) -> list[Card]:
    enemy = state.other_player(actor.player_id)
    enemy_characters = [
        card
        for card in _oracle_characters(enemy)
        if not card.stealth and not card.immune and not card.dormant
    ]
    enemy_minions = [card for card in enemy_characters if card.card_type == CardType.MINION]
    friendly_characters = _oracle_characters(actor)
    friendly_minions = [
        card for card in friendly_characters if card.card_type == CardType.MINION
    ]
    if mode == "self":
        found = _find_entity(state, source_entity_id)
        return [found[1]] if found is not None else []
    if mode == "enemy_character":
        return enemy_characters
    if mode == "friendly_character":
        return friendly_characters
    if mode == "any_character":
        return [*friendly_characters, *enemy_characters]
    if mode == "enemy_minion":
        return enemy_minions
    if mode == "friendly_minion":
        return friendly_minions
    if mode == "any_minion":
        return [*friendly_minions, *enemy_minions]
    if mode == "any_undamaged_minion":
        return [
            card
            for card in (*friendly_minions, *enemy_minions)
            if card.current_health_known and card.current_health == card.health
        ]
    if mode == "damaged_enemy_minion":
        return [
            card
            for card in enemy_minions
            if card.current_health_known and card.current_health < card.health
        ]
    if mode == "enemy_hero":
        return [enemy.hero] if enemy.hero in enemy_characters else []
    if mode == "friendly_hero":
        return [actor.hero]
    return []


def _oracle_automatic_targets(
    state: GameState, actor: Any, mode: str, source_entity_id: str
) -> list[tuple[Any, Card]] | None:
    enemy = state.other_player(actor.player_id)

    def owned(
        owner: Any, *, include_hero: bool, exclude_source: bool = False
    ) -> list[tuple[Any, Card]]:
        targets = [(owner, owner.hero)] if include_hero and owner.hero.current_health > 0 else []
        targets.extend(
            (owner, card)
            for card in _oracle_active_minions(owner.board)
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


def _oracle_primary_target(card: Card) -> str:
    modes = [
        effect.target
        for effect in card.effects
        if not effect.random
        and effect.target not in {"none", "self"}
        and effect.target not in _AUTOMATIC_ORACLE_TARGET_MODES
    ]
    return modes[0] if modes else "none"


def enumerate_oracle_actions(state: GameState) -> tuple[Action, ...]:
    if _terminal(state):
        return ()
    actor = state.player(state.active_player_id)
    enemy = state.other_player(actor.player_id)
    enemy_minions = [card for card in _living(enemy.board) if not card.stealth and not card.dormant]
    taunts = [card for card in enemy_minions if card.taunt]
    actions: list[Action] = []
    for attacker in (actor.hero, *_living(actor.board)):
        if (
            attacker.attack <= 0
            or not attacker.can_attack
            or attacker.attacks_remaining <= 0
            or attacker.frozen
            or attacker.dormant
        ):
            continue
        targets = list(taunts or enemy_minions)
        rush_restricted = (
            attacker.card_type == CardType.MINION
            and attacker.rush
            and attacker.summoned_this_turn
            and not attacker.charge
        )
        if not taunts and not rush_restricted:
            targets.append(enemy.hero)
        for target in targets:
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
        target_mode = (
            "none"
            if card.card_type == CardType.LOCATION
            else _oracle_primary_target(card)
        )
        if target_mode in {"none", "self"}:
            target_ids = ("",)
        else:
            target_ids = tuple(
                target.entity_id
                for target in _oracle_target_candidates(
                    state, actor, target_mode, card.entity_id
                )
            )
        for target_id in target_ids:
            for board_position in board_positions:
                actions.append(
                    Action(
                        ActionKind.PLAY_CARD,
                        card.entity_id,
                        target_id,
                        card.card_id,
                        f"Play {card.name}",
                        board_position,
                    )
                )
    power = actor.hero_power
    if power and actor.hero_power_available and power.cost <= actor.mana:
        target_mode = _oracle_primary_target(power)
        if target_mode in {"none", "self"}:
            actions.append(
                Action(
                    ActionKind.HERO_POWER,
                    power.entity_id,
                    "",
                    power.card_id,
                    f"Use {power.name}",
                )
            )
        else:
            for target in _oracle_target_candidates(
                state, actor, target_mode, power.entity_id
            ):
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
    return tuple(sorted(actions, key=lambda item: item.action_id))


def _damage(owner: Any, target: Card, amount: int) -> int:
    if amount <= 0 or target.immune:
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


def _remove_dead(state: GameState) -> None:
    for player in (state.friendly, state.opponent):
        dead = [
            card
            for card in player.board
            if card.card_type != CardType.LOCATION and card.current_health <= 0
        ]
        player.board = _living(player.board)
        player.graveyard.extend(dead)


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


def _tag_active(card: Card, name: str, enum_id: int) -> bool:
    value = _tag_integer(card, name, enum_id)
    return value is not None and value != 0


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


def _hero_power_cost_aura_profile(owner: Any) -> tuple[int, int] | None:
    profile: tuple[int, int] | None = None
    for card in owner.board:
        if card.current_health <= 0 or card.dormant:
            continue
        for effect in card.effects:
            if effect.kind != "set_hero_power_cost":
                continue
            if not 0 <= effect.amount <= 65_535:
                raise TurnPairEvaluationError(
                    "hero-power cost aura has an invalid cost"
                )
            if effect.hand_count_at_most is None:
                raise TurnPairEvaluationError(
                    "hero-power cost aura has no public hand-count condition"
                )
            if effect.hand_count_at_most > 65_535:
                raise TurnPairEvaluationError(
                    "hero-power cost aura has an invalid hand-count condition"
                )
            current = (effect.amount, effect.hand_count_at_most)
            if profile is not None and profile != current:
                raise TurnPairEvaluationError(
                    "multiple different hero-power cost auras require layer ordering"
                )
            profile = current
    return profile


def _unmodified_hero_power_cost(owner: Any) -> int:
    power = owner.hero_power
    if power is None:
        raise TurnPairEvaluationError("hero-power cost aura has no hero power")
    value = _named_tag_integer(power, "TAG_LAST_KNOWN_COST_IN_HAND")
    if value is None:
        raise TurnPairEvaluationError(
            "hero-power cost aura requires TAG_LAST_KNOWN_COST_IN_HAND evidence"
        )
    if not 0 <= value <= 65_535:
        raise TurnPairEvaluationError("hero-power base cost evidence is invalid")
    return value


def _expected_hero_power_cost(owner: Any, profile: tuple[int, int]) -> int:
    base_cost = _unmodified_hero_power_cost(owner)
    aura_cost, hand_count_at_most = profile
    return aura_cost if len(owner.hand) <= hand_count_at_most else base_cost


def _assert_continuous_effect_state(state: GameState) -> None:
    for owner in (state.friendly, state.opponent):
        profile = _hero_power_cost_aura_profile(owner)
        if profile is None:
            continue
        expected = _expected_hero_power_cost(owner, profile)
        actual = owner.hero_power.cost if owner.hero_power is not None else 0
        if actual != expected:
            raise TurnPairEvaluationError(
                f"hero-power cost aura expected {expected} but HDT exposed {actual}"
            )


def _set_public_tag_integer(card: Card, name: str, enum_id: int, value: int) -> None:
    enum_name = str(enum_id)
    key = next(
        (
            existing
            for existing in card.tags
            if existing == enum_name or existing.upper() == name
        ),
        name,
    )
    card.tags[key] = value


def _reconcile_continuous_effects(before: GameState, next_state: GameState) -> None:
    for before_owner, next_owner in (
        (before.friendly, next_state.friendly),
        (before.opponent, next_state.opponent),
    ):
        previous_profile = _hero_power_cost_aura_profile(before_owner)
        current_profile = _hero_power_cost_aura_profile(next_owner)
        if previous_profile is None and current_profile is None:
            continue
        desired = (
            _expected_hero_power_cost(next_owner, current_profile)
            if current_profile is not None
            else _unmodified_hero_power_cost(next_owner)
        )
        power = next_owner.hero_power
        if power is None:
            raise TurnPairEvaluationError(
                "hero-power cost aura lost its hero power"
            )
        power.cost = desired
        _set_public_tag_integer(power, "COST", 48, desired)


def _one_cost_card_doubling_triggers(owner: Any) -> int:
    triggers = 0
    for card in owner.board:
        if card.current_health <= 0 or card.dormant:
            continue
        for effect in card.effects:
            if effect.kind != "double_one_cost_cards":
                continue
            if effect.amount != 2 or effect.target != "none":
                raise TurnPairEvaluationError(
                    "one-cost card doubler has an invalid public rule"
                )
            triggers += 1
    return triggers


def _one_cost_multiplier(trigger_count: int) -> int:
    return min(65_535, 2 ** max(0, trigger_count))


def _maximum_hero_attacks(actor: Any) -> int:
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


def _refresh_hero_attack_from_public_history(actor: Any) -> None:
    hero = actor.hero
    attacks_used = _tag_integer(hero, "NUM_ATTACKS_THIS_TURN", 297)
    if attacks_used is None:
        return
    hero.attacks_remaining = max(
        0, _maximum_hero_attacks(actor) - max(0, attacks_used)
    )
    weapon_blocked = bool(
        actor.weapon
        and (
            actor.weapon.current_durability <= 0
            or _tag_active(actor.weapon, "CANT_ATTACK", 227)
        )
    )
    hero.can_attack = bool(
        hero.attack > 0
        and hero.current_health > 0
        and hero.attacks_remaining > 0
        and not _tag_active(hero, "FROZEN", 20)
        and not hero.dormant
        and not _tag_active(hero, "EXHAUSTED", 43)
        and not _tag_active(hero, "CANT_ATTACK", 227)
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


def _apply_oracle_character_effect(
    actor: Any, source: Card, effect: Any, owner: Any, target: Card
) -> None:
    if effect.kind == "damage":
        amount = effect.amount + (
            actor.spell_power if source.card_type == CardType.SPELL else 0
        )
        self_hero_overkill = 0
        if target.entity_id == actor.hero.entity_id:
            damage_after_armor = max(0, amount - actor.armor)
            self_hero_overkill = max(
                0, damage_after_armor - actor.hero.current_health
            )
        dealt = _damage(owner, target, amount)
        if source.lifesteal and dealt > 0:
            healing = max(0, dealt - self_hero_overkill)
            actor.hero.current_health = min(
                actor.hero.health, actor.hero.current_health + healing
            )
        return
    if effect.kind == "heal":
        target.current_health = min(
            target.health, target.current_health + max(0, effect.amount)
        )
        return
    if effect.kind == "freeze":
        if target.card_type not in {CardType.HERO, CardType.MINION}:
            raise TurnPairEvaluationError("Freeze target is not a character")
        target.frozen = True
        target.can_attack = False
        return
    if effect.kind == "buff_attack":
        if target.card_type != CardType.MINION:
            raise TurnPairEvaluationError("Attack buff target is not a minion")
        target.attack = max(0, target.attack + effect.amount)
        return
    if effect.kind == "buff_health":
        if target.card_type != CardType.MINION:
            raise TurnPairEvaluationError("Health buff target is not a minion")
        target.health = max(1, target.health + effect.amount)
        target.current_health = max(1, target.current_health + effect.amount)
        return
    if effect.kind == "set_health":
        if target.card_type != CardType.MINION:
            raise TurnPairEvaluationError("Health setter target is not a minion")
        target.health = max(1, effect.amount)
        target.current_health = max(1, effect.amount)
        return
    raise TurnPairEvaluationError(
        f"oracle point-effect subset cannot apply {effect.kind}"
    )


def _apply_oracle_deterministic_effect(
    state: GameState,
    actor: Any,
    source: Card,
    effect: Any,
    target_entity_id: str,
) -> None:
    if effect.random:
        raise TurnPairEvaluationError(
            "deterministic oracle transition received a random effect"
        )
    if effect.kind in {"set_hero_power_cost", "double_one_cost_cards"}:
        return
    automatic_targets = _oracle_automatic_targets(
        state, actor, effect.target, source.entity_id
    )
    if automatic_targets is not None:
        for owner, target in automatic_targets:
            _apply_oracle_character_effect(actor, source, effect, owner, target)
        return
    if effect.kind == "damage_all_minions":
        amount = effect.amount + (
            actor.spell_power if source.card_type == CardType.SPELL else 0
        )
        for owner in (state.friendly, state.opponent):
            for target in list(_oracle_active_minions(owner.board)):
                _damage(owner, target, amount)
        return
    if effect.kind == "armor":
        if effect.target != "none":
            raise TurnPairEvaluationError("armor effect unexpectedly requires a target")
        actor.armor += max(0, effect.amount)
        return
    if effect.kind == "gain_hero_attack":
        if effect.target != "none":
            raise TurnPairEvaluationError(
                "hero Attack effect unexpectedly requires a target"
            )
        actor.hero.attack += max(0, effect.amount)
        _refresh_hero_attack_from_public_history(actor)
        return
    if effect.kind == "gain_mana":
        if effect.target != "none":
            raise TurnPairEvaluationError("mana effect unexpectedly requires a target")
        actor.mana = min(actor.max_mana, actor.mana + max(0, effect.amount))
        return
    if effect.kind == "summon":
        _remove_dead(state)
        if effect.target != "none":
            raise TurnPairEvaluationError(
                "summon effect unexpectedly requires a target"
            )
        for ordinal in range(effect.count):
            if len(actor.board) >= 7:
                break
            base_id = (
                f"generated-{source.entity_id}-{state.turn}-"
                f"{len(actor.board)}-{ordinal}"
            )
            entity_id = base_id
            suffix = 1
            while _find_entity(state, entity_id) is not None:
                entity_id = f"{base_id}-{suffix}"
                suffix += 1
            can_attack = effect.rush and effect.attack > 0
            actor.board.append(
                Card(
                    entity_id=entity_id,
                    card_id=effect.card_id,
                    name=effect.name,
                    card_type=CardType.MINION,
                    attack=effect.attack,
                    health=effect.health,
                    current_health=effect.health,
                    current_health_known=True,
                    playable=False,
                    can_attack=can_attack,
                    attacks_remaining=1 if can_attack else 0,
                    rush=effect.rush,
                    summoned_this_turn=True,
                    effect_coverage=(
                        "unsupported"
                        if effect.summoned_card_effects_unmodeled
                        else "exact"
                    ),
                    unsupported_effects=(
                        ("summoned_card_text_not_modeled",)
                        if effect.summoned_card_effects_unmodeled
                        else ()
                    ),
                )
            )
        return
    if effect.target == "none":
        return
    if effect.target == "self":
        target_id = source.entity_id
    elif effect.target == "enemy_hero":
        target_id = state.other_player(actor.player_id).hero.entity_id
    elif effect.target == "friendly_hero":
        target_id = actor.hero.entity_id
    else:
        target_id = target_entity_id
    if (
        target_entity_id
        and effect.target in {"enemy_hero", "friendly_hero"}
        and target_entity_id != target_id
    ):
        raise TurnPairEvaluationError(
            "fixed hero target does not match the reviewed card rule"
        )
    found = _find_entity(state, target_id) if target_id else None
    if found is None:
        raise TurnPairEvaluationError("point-effect target disappeared")
    owner, target = found
    _apply_oracle_character_effect(actor, source, effect, owner, target)


def _apply_oracle_point_effects(
    state: GameState, actor: Any, source: Card, target_entity_id: str
) -> None:
    for effect in source.effects:
        _apply_oracle_deterministic_effect(
            state, actor, source, effect, target_entity_id
        )
    _remove_dead(state)


def _oracle_random_targets(
    state: GameState, actor: Any, mode: str
) -> list[tuple[Any, Card]]:
    enemy = state.other_player(actor.player_id)

    def minions(owner: Any) -> list[tuple[Any, Card]]:
        return [(owner, card) for card in _oracle_active_minions(owner.board)]

    if mode == "enemy_character":
        targets = [(enemy, enemy.hero), *minions(enemy)]
    elif mode == "friendly_character":
        targets = [(actor, actor.hero), *minions(actor)]
    elif mode == "any_character":
        targets = [
            (actor, actor.hero),
            (enemy, enemy.hero),
            *minions(actor),
            *minions(enemy),
        ]
    elif mode == "enemy_minion":
        targets = minions(enemy)
    elif mode == "friendly_minion":
        targets = minions(actor)
    elif mode == "any_minion":
        targets = [*minions(actor), *minions(enemy)]
    elif mode == "any_undamaged_minion":
        targets = [
            pair
            for pair in (*minions(actor), *minions(enemy))
            if pair[1].current_health_known
            and pair[1].current_health == pair[1].health
        ]
    elif mode == "damaged_enemy_minion":
        targets = [
            pair
            for pair in minions(enemy)
            if pair[1].current_health_known
            and pair[1].current_health < pair[1].health
        ]
    elif mode == "enemy_hero":
        targets = [(enemy, enemy.hero)]
    elif mode == "friendly_hero":
        targets = [(actor, actor.hero)]
    else:
        raise TurnPairEvaluationError(
            f"random effect target mode {mode!r} is unsupported"
        )
    return sorted(
        (pair for pair in targets if pair[1].current_health > 0),
        key=lambda pair: pair[1].entity_id,
    )


def _merge_oracle_weighted_states(
    outcomes: Sequence[tuple[GameState, Fraction]],
) -> list[tuple[GameState, Fraction]]:
    merged: dict[str, tuple[GameState, Fraction]] = {}
    for state, probability in outcomes:
        key = _canonical_hash(state.to_dict())
        if key in merged:
            existing_state, existing_probability = merged[key]
            merged[key] = (existing_state, existing_probability + probability)
        else:
            merged[key] = (state, probability)
    return list(merged.values())


def _apply_oracle_effect_outcomes(
    state: GameState,
    actor_player_id: str,
    source: Card,
    target_entity_id: str,
) -> list[tuple[GameState, Fraction]]:
    outcomes: list[tuple[GameState, Fraction]] = [(copy.deepcopy(state), Fraction(1, 1))]
    for effect in source.effects:
        expanded: list[tuple[GameState, Fraction]] = []
        for current, probability in outcomes:
            actor = current.player(actor_player_id)
            if not effect.random:
                child = copy.deepcopy(current)
                child_actor = child.player(actor_player_id)
                _apply_oracle_deterministic_effect(
                    child, child_actor, source, effect, target_entity_id
                )
                expanded.append((child, probability))
                continue
            if (
                effect.count != 1
                or effect.card_id
                or effect.attack != 0
                or effect.rush
                or effect.kind
                not in {
                    "damage",
                    "heal",
                    "freeze",
                    "buff_attack",
                    "buff_health",
                    "set_health",
                }
            ):
                raise TurnPairEvaluationError(
                    "random effect has unsupported auxiliary fields"
                )
            targets = _oracle_random_targets(current, actor, effect.target)
            if not targets:
                expanded.append((current, probability))
                continue
            branch_probability = Fraction(1, len(targets))
            for _, target in targets:
                child = copy.deepcopy(current)
                child_actor = child.player(actor_player_id)
                found = _find_entity(child, target.entity_id)
                if found is None:
                    raise TurnPairEvaluationError("random target disappeared")
                owner, child_target = found
                _apply_oracle_character_effect(
                    child_actor, source, effect, owner, child_target
                )
                expanded.append((child, probability * branch_probability))
        outcomes = _merge_oracle_weighted_states(expanded)
    for current, _ in outcomes:
        _remove_dead(current)
    return _merge_oracle_weighted_states(outcomes)


def _apply_oracle_action(state: GameState, action: Action) -> tuple[GameState, bool]:
    legal = {item.action_id: item for item in enumerate_oracle_actions(state)}
    if action.action_id not in legal:
        raise TurnPairEvaluationError(f"illegal oracle action: {action.action_id}")
    if _oracle_action_has_random_resolution(state, action):
        raise TurnPairEvaluationError(
            "deterministic oracle transition cannot collapse a random outcome"
        )
    next_state = copy.deepcopy(state)
    actor = next_state.player(next_state.active_player_id)
    enemy = next_state.other_player(actor.player_id)
    if action.kind == ActionKind.END_TURN:
        # Current-turn and temporary mana expire at the turn boundary.  Leaving
        # it in the terminal state makes passing look better than spending mana.
        actor.mana = 0
        next_state.active_player_id = enemy.player_id
        next_state.turn += 1
        return next_state, True
    if action.kind == ActionKind.PLAY_CARD:
        card = next(
            (item for item in actor.hand if item.entity_id == action.source_entity_id),
            None,
        )
        if card is None:
            raise TurnPairEvaluationError("played card disappeared from hand")
        actor.hand.remove(card)
        one_cost_triggers = (
            _one_cost_card_doubling_triggers(actor) if card.cost == 1 else 0
        )
        actor.mana -= card.cost
        if card.card_type in {CardType.MINION, CardType.LOCATION}:
            if card.card_type == CardType.MINION:
                card.summoned_this_turn = True
                card.can_attack = False
                card.attacks_remaining = 0
            if not 1 <= action.board_position <= len(actor.board) + 1:
                raise TurnPairEvaluationError("oracle board position is invalid")
            actor.board.insert(action.board_position - 1, card)
        if card.card_type != CardType.LOCATION:
            repetitions = (
                _one_cost_multiplier(one_cost_triggers)
                if card.card_type == CardType.SPELL
                else 1
            )
            for _ in range(repetitions):
                _apply_oracle_point_effects(
                    next_state, actor, card, action.target_entity_id
                )
        if card.card_type == CardType.MINION and one_cost_triggers:
            multiplier = _one_cost_multiplier(one_cost_triggers)
            minion = next(
                (item for item in actor.board if item.entity_id == card.entity_id),
                None,
            )
            if minion is not None:
                minion.attack = min(65_535, minion.attack * multiplier)
                minion.health = min(65_535, minion.health * multiplier)
                minion.current_health = min(
                    65_535, minion.current_health * multiplier
                )
                _set_public_tag_integer(minion, "ATK", 47, minion.attack)
                _set_public_tag_integer(minion, "HEALTH", 45, minion.health)
        if card.card_type == CardType.SPELL:
            actor.graveyard.append(card)
        _reconcile_continuous_effects(state, next_state)
        return next_state, False
    if action.kind == ActionKind.HERO_POWER:
        power = actor.hero_power
        if power is None or power.entity_id != action.source_entity_id:
            raise TurnPairEvaluationError("hero power disappeared")
        actor.mana -= power.cost
        actor.hero_power_available = False
        _apply_oracle_point_effects(next_state, actor, power, action.target_entity_id)
        _reconcile_continuous_effects(state, next_state)
        return next_state, False
    if action.kind != ActionKind.ATTACK:
        raise TurnPairEvaluationError(f"oracle subset cannot apply {action.kind.value}")
    source_found = _find_entity(next_state, action.source_entity_id)
    target_found = _find_entity(next_state, action.target_entity_id)
    if source_found is None or target_found is None:
        raise TurnPairEvaluationError("attack source or target disappeared")
    source_owner, attacker = source_found
    target_owner, target = target_found
    attack_damage = attacker.attack
    retaliation = target.attack if target.card_type != CardType.HERO else 0
    _damage(target_owner, target, attack_damage)
    _damage(source_owner, attacker, retaliation)
    attacker.attacks_remaining = max(0, attacker.attacks_remaining - 1)
    if attacker.card_type == CardType.HERO:
        _increment_public_hero_attack_count(attacker)
    attacker.can_attack = attacker.attacks_remaining > 0 and not attacker.frozen
    _remove_dead(next_state)
    _reconcile_continuous_effects(state, next_state)
    return next_state, False


def _oracle_action_has_random_resolution(state: GameState, action: Action) -> bool:
    actor = state.player(state.active_player_id)
    if action.kind == ActionKind.PLAY_CARD:
        return any(
            card.entity_id == action.source_entity_id
            and card.card_type != CardType.LOCATION
            and any(effect.random for effect in card.effects)
            for card in actor.hand
        )
    if action.kind == ActionKind.HERO_POWER:
        return bool(
            actor.hero_power
            and actor.hero_power.entity_id == action.source_entity_id
            and any(effect.random for effect in actor.hero_power.effects)
        )
    if action.kind == ActionKind.LOCATION_ACTIVATE:
        return any(
            card.entity_id == action.source_entity_id
            and card.card_type == CardType.LOCATION
            and any(effect.random for effect in card.effects)
            for card in actor.board
        )
    return False


def _apply_oracle_action_outcomes(
    state: GameState, action: Action
) -> tuple[OracleActionOutcome, ...]:
    legal = {item.action_id: item for item in enumerate_oracle_actions(state)}
    if action.action_id not in legal:
        raise TurnPairEvaluationError(f"illegal oracle action: {action.action_id}")
    if not _oracle_action_has_random_resolution(state, action):
        child, ended_turn = _apply_oracle_action(state, action)
        return (OracleActionOutcome(child, ended_turn, Fraction(1, 1)),)
    if action.kind != ActionKind.PLAY_CARD:
        raise TurnPairEvaluationError(
            "chance outcomes are currently supported only for card plays"
        )

    next_state = copy.deepcopy(state)
    actor = next_state.player(next_state.active_player_id)
    card = next(
        (item for item in actor.hand if item.entity_id == action.source_entity_id),
        None,
    )
    if card is None:
        raise TurnPairEvaluationError("played card disappeared from hand")
    actor.hand.remove(card)
    one_cost_triggers = (
        _one_cost_card_doubling_triggers(actor) if card.cost == 1 else 0
    )
    actor.mana -= card.cost
    if card.card_type in {CardType.MINION, CardType.LOCATION}:
        if card.card_type == CardType.MINION:
            card.summoned_this_turn = True
            card.can_attack = False
            card.attacks_remaining = 0
        if not 1 <= action.board_position <= len(actor.board) + 1:
            raise TurnPairEvaluationError("oracle board position is invalid")
        actor.board.insert(action.board_position - 1, card)
    elif action.board_position != 0:
        raise TurnPairEvaluationError("this card type does not use a board position")

    repetitions = (
        _one_cost_multiplier(one_cost_triggers)
        if card.card_type == CardType.SPELL
        else 1
    )
    weighted: list[tuple[GameState, Fraction]] = [
        (next_state, Fraction(1, 1))
    ]
    for _ in range(repetitions):
        expanded: list[tuple[GameState, Fraction]] = []
        for current, probability in weighted:
            for child, branch_probability in _apply_oracle_effect_outcomes(
                current,
                actor.player_id,
                card,
                action.target_entity_id,
            ):
                expanded.append(
                    (child, probability * branch_probability)
                )
        weighted = _merge_oracle_weighted_states(expanded)

    if card.card_type == CardType.MINION and one_cost_triggers:
        multiplier = _one_cost_multiplier(one_cost_triggers)
        for current, _ in weighted:
            current_actor = current.player(actor.player_id)
            minion = next(
                (
                    item
                    for item in current_actor.board
                    if item.entity_id == card.entity_id
                ),
                None,
            )
            if minion is not None:
                minion.attack = min(65_535, minion.attack * multiplier)
                minion.health = min(65_535, minion.health * multiplier)
                minion.current_health = min(
                    65_535, minion.current_health * multiplier
                )
                _set_public_tag_integer(minion, "ATK", 47, minion.attack)
                _set_public_tag_integer(minion, "HEALTH", 45, minion.health)
    for current, _ in weighted:
        if card.card_type == CardType.SPELL:
            current.player(actor.player_id).graveyard.append(copy.deepcopy(card))
        _reconcile_continuous_effects(state, current)
    weighted = _merge_oracle_weighted_states(weighted)
    return tuple(
        OracleActionOutcome(current, False, probability)
        for current, probability in weighted
    )


def _advance_to_opponent_start(state: GameState) -> GameState:
    next_state = copy.deepcopy(state)
    actor = next_state.player(next_state.active_player_id)
    actor.max_mana = min(10, actor.max_mana + 1)
    actor.mana = actor.max_mana
    for card in actor.board:
        card.summoned_this_turn = False
        card.attacks_remaining = (
            1 if card.attack > 0 and not card.frozen and not card.dormant else 0
        )
        card.can_attack = card.attacks_remaining > 0
    actor.hero.attacks_remaining = (
        1 if actor.hero.attack > 0 and not actor.hero.frozen else 0
    )
    actor.hero.can_attack = actor.hero.attacks_remaining > 0
    if actor.deck_size > 0:
        raise TurnPairEvaluationError("oracle cannot determinize the opponent draw")
    actor.fatigue += 1
    _damage(actor, actor.hero, actor.fatigue)
    return next_state


def _advance_to_visible_opponent_start(state: GameState) -> GameState:
    """Refresh public turn state without inventing an unknown draw or fatigue."""

    next_state = copy.deepcopy(state)
    actor = next_state.player(next_state.active_player_id)
    actor.max_mana = min(10, actor.max_mana + 1)
    actor.mana = actor.max_mana
    for card in actor.board:
        card.summoned_this_turn = False
        card.attacks_remaining = (
            1 if card.attack > 0 and not card.frozen and not card.dormant else 0
        )
        card.can_attack = card.attacks_remaining > 0
    actor.hero.attacks_remaining = (
        1
        if actor.hero.attack > 0
        and not actor.hero.frozen
        and not actor.hero.dormant
        else 0
    )
    actor.hero.can_attack = actor.hero.attacks_remaining > 0
    return next_state


def _oracle_point_card_supported(card: Card) -> bool:
    if card.card_type not in {
        CardType.SPELL,
        CardType.MINION,
        CardType.HERO_POWER,
        CardType.LOCATION,
    }:
        return False
    if card.card_type in {
        CardType.SPELL,
        CardType.HERO_POWER,
        CardType.LOCATION,
    } and not card.effects:
        return False
    if len(card.effects) > 2:
        return False
    point_targets = {
        "self",
        "enemy_character",
        "friendly_character",
        "any_character",
        "enemy_minion",
        "friendly_minion",
        "any_minion",
        "any_undamaged_minion",
        "damaged_enemy_minion",
        "enemy_hero",
        "friendly_hero",
        *_AUTOMATIC_ORACLE_TARGET_MODES,
    }
    target_modes = {
        effect.target
        for effect in card.effects
        if not effect.random
        and effect.target not in {"none", "self"}
        and effect.target not in _AUTOMATIC_ORACLE_TARGET_MODES
    }
    if len(target_modes) > 1:
        return False
    for effect in card.effects:
        point_or_owner = (
            effect.kind
            in {"damage", "heal", "buff_attack", "buff_health", "set_health"}
            and effect.amount > 0
            and effect.target in point_targets
            or effect.kind in {"armor", "gain_hero_attack", "gain_mana"}
            and effect.amount > 0
            and effect.target == "none"
            or effect.kind == "damage_all_minions"
            and effect.amount > 0
            and effect.target == "none"
            or effect.kind == "freeze"
            and effect.amount == 0
            and effect.target in point_targets
        )
        point_or_owner_fields_valid = (
            effect.count == 1
            and not effect.card_id
            and effect.attack == 0
            and not effect.rush
        )
        summon = (
            effect.kind == "summon"
            and effect.amount == 0
            and effect.target == "none"
            and 1 <= effect.count <= 7
            and bool(effect.card_id.strip())
            and bool(effect.name.strip())
            and effect.health > 0
        )
        hero_power_cost_aura = (
            effect.kind == "set_hero_power_cost"
            and 0 <= effect.amount <= 65_535
            and effect.target == "none"
            and effect.hand_count_at_most is not None
            and effect.hand_count_at_most <= 65_535
            and point_or_owner_fields_valid
        )
        one_cost_card_doubler = (
            effect.kind == "double_one_cost_cards"
            and effect.amount == 2
            and effect.target == "none"
            and point_or_owner_fields_valid
        )
        random_target_effect = (
            effect.random
            and point_or_owner_fields_valid
            and (
                effect.kind
                in {
                    "damage",
                    "heal",
                    "buff_attack",
                    "buff_health",
                    "set_health",
                }
                and effect.amount > 0
                or effect.kind == "freeze"
                and effect.amount == 0
            )
            and effect.target not in {"none", "self"}
            and effect.target not in _AUTOMATIC_ORACLE_TARGET_MODES
        )
        deterministic_effect = (
            not effect.random
            and (
                summon
                or hero_power_cost_aura
                or one_cost_card_doubler
                or (point_or_owner and point_or_owner_fields_valid)
            )
        )
        if (
            not (random_target_effect or deterministic_effect)
            or effect.summoned_card_effects_unmodeled
        ):
            return False
    return True


def _unsupported_reasons(
    state: GameState, *, allow_point_effects: bool = False
) -> tuple[str, ...]:
    reasons: list[str] = []
    if state.active_player_id != state.friendly.player_id:
        reasons.append("fixture must start on the friendly turn")
    if state.opponent.deck_size > 0:
        reasons.append("opponent draw identity is not deterministic")
    if allow_point_effects:
        try:
            _assert_continuous_effect_state(state)
        except TurnPairEvaluationError as exc:
            reasons.append(str(exc))
    for player in (state.friendly, state.opponent):
        if player.weapon is not None:
            reasons.append(f"{player.player_id} hero power or weapon is outside turnpair-v1")
        if player.hero_power is not None and not allow_point_effects:
            reasons.append(f"{player.player_id} hero power or weapon is outside turnpair-v1")
        if player.hand and not allow_point_effects:
            reasons.append(f"{player.player_id} hand play is outside turnpair-v1")
        effect_sources = [*player.hand]
        if player.hero_power is not None:
            effect_sources.append(player.hero_power)
        gains_hero_attack = any(
            effect.kind == "gain_hero_attack"
            for card in effect_sources
            for effect in card.effects
        )
        if (
            allow_point_effects
            and gains_hero_attack
            and _tag_integer(player.hero, "NUM_ATTACKS_THIS_TURN", 297) is None
        ):
            reasons.append(
                f"{player.player_id} hero attack history is unavailable"
            )
        for card in (player.hero, *player.board, *player.hand):
            has_supported_point_effect = bool(
                allow_point_effects and _oracle_point_card_supported(card)
            )
            if (
                (card.effects and not has_supported_point_effect)
                or card.unsupported_effects
                or card.effect_coverage == "unsupported"
                or (
                    allow_point_effects
                    and card.card_type in {CardType.SPELL, CardType.HERO_POWER}
                    and not has_supported_point_effect
                )
            ):
                reasons.append(f"{card.entity_id} has unsupported card effects")
            supported_spell_lifesteal = bool(
                allow_point_effects
                and card.lifesteal
                and card.card_type == CardType.SPELL
                and card.effects
                and all(effect.kind == "damage" for effect in card.effects)
                and has_supported_point_effect
            )
            lifesteal_outside_point_effect = bool(
                card.lifesteal and not supported_spell_lifesteal
            )
            if any(
                (
                    card.stealth,
                    card.frozen,
                    card.poisonous,
                    lifesteal_outside_point_effect,
                    card.windfury,
                    card.mega_windfury,
                    card.rush,
                    card.charge,
                    card.reborn,
                    card.dormant,
                    card.immune,
                )
            ):
                reasons.append(f"{card.entity_id} has a mechanic outside turnpair-v1")
        if player.hero_power is not None:
            power = player.hero_power
            if (
                allow_point_effects
                and not _oracle_point_card_supported(power)
            ):
                reasons.append(f"{power.entity_id} has unsupported card effects")
    return tuple(dict.fromkeys(reasons))


def _enumerate_complete_lines(
    state: GameState,
    *,
    max_depth: int = MAX_LINE_DEPTH,
    max_nodes: int = MAX_ENUMERATED_NODES,
) -> tuple[tuple[OracleCompleteLine, ...], int]:
    results: dict[tuple[str, ...], OracleCompleteLine] = {}
    explored = 0

    def visit(current: GameState, actions: tuple[Action, ...], depth: int) -> None:
        nonlocal explored
        if explored >= max_nodes:
            raise TurnPairEvaluationError("turn-pair oracle node limit exceeded")
        if _terminal(current):
            results.setdefault(
                tuple(item.action_id for item in actions),
                OracleCompleteLine(actions, current, False),
            )
            return
        if depth >= max_depth:
            return
        for action in enumerate_oracle_actions(current):
            explored += 1
            next_state, ended = _apply_oracle_action(current, action)
            next_actions = (*actions, action)
            if ended or _terminal(next_state):
                results.setdefault(
                    tuple(item.action_id for item in next_actions),
                    OracleCompleteLine(next_actions, next_state, ended),
                )
            else:
                visit(next_state, next_actions, depth + 1)

    visit(copy.deepcopy(state), (), 0)
    ordered = tuple(
        results[key]
        for key in sorted(results, key=lambda item: (len(item), item))
    )
    return ordered, explored


def _survival_value(health: int, armor: int) -> int:
    effective_health = max(0, health) + max(0, armor)
    return sum(
        60
        if point <= 5
        else 30
        if point <= 10
        else 15
        if point <= 15
        else 8
        if point <= 20
        else 4
        for point in range(1, effective_health + 1)
    )


def _board_card_value(card: Card) -> int:
    if card.current_health <= 0:
        return 0
    if card.card_type == CardType.LOCATION:
        return card.current_health * 24 + len(card.effects) * 8
    if card.card_type != CardType.MINION:
        return 0

    inert_zero_attack_body = (
        card.attack == 0
        and not card.effects
        and not card.taunt
        and not card.divine_shield
        and not card.poisonous
        and not card.lifesteal
        and not card.windfury
        and not card.mega_windfury
        and not card.reborn
        and not card.stealth
        and not card.immune
    )
    health_weight = 3 if inert_zero_attack_body else 14
    value = card.attack * 24 + card.current_health * health_weight + card.attack**2 * 2
    if card.taunt:
        value += 20 + card.current_health * 3
    if card.divine_shield:
        value += 20 + card.attack * 6
    if card.poisonous:
        value += 35
    if card.lifesteal:
        value += 12 + card.attack * 6
    if card.windfury:
        value += card.attack * 10
    if card.mega_windfury:
        value += card.attack * 22
    if card.reborn:
        value += 30 + value // 3
    if card.stealth:
        value += 8 + card.attack * 4
    if card.immune:
        value += 30
    if card.dormant:
        value = value * 2 // 3
    return value


def _board_value(cards: Sequence[Card]) -> int:
    return sum(_board_card_value(card) for card in cards)


def _weapon_value(card: Card | None) -> int:
    if card is None:
        return 0
    value = card.attack * 10 + card.current_durability * 2
    if card.poisonous:
        value += 24 + card.current_durability * 4
    if card.lifesteal:
        value += card.attack * 4
    if card.windfury:
        value += card.attack * 8
    if card.mega_windfury:
        value += card.attack * 18
    return value


def _hand_value(cards: Sequence[Card]) -> int:
    value = 0
    for card in cards:
        # Match the production scorer's non-negative f64 rounding contract.
        base = math.floor(min(10.0, max(0.0, card.prior_weight)) * 25.0 + 0.5)
        engine_reserve = sum(
            80
            for effect in card.effects
            if effect.kind == "double_one_cost_cards"
        )
        value += base + engine_reserve
    return value


def tactical_utility(state: GameState, perspective_player_id: str) -> int:
    player = state.player(perspective_player_id)
    enemy = state.other_player(perspective_player_id)
    player_dead = player.hero.current_health <= 0
    enemy_dead = enemy.hero.current_health <= 0
    if player_dead and enemy_dead:
        return 0
    if enemy_dead:
        return WIN_UTILITY
    if player_dead:
        return LOSS_UTILITY
    survival = _survival_value(player.hero.current_health, player.armor) - _survival_value(
        enemy.hero.current_health, enemy.armor
    )
    material = (
        _board_value(player.board)
        - _board_value(enemy.board)
        + _weapon_value(player.weapon)
        - _weapon_value(enemy.weapon)
    )
    hand = _hand_value(player.hand) - _hand_value(enemy.hand)
    mana = (player.mana - enemy.mana) * 2
    return survival + material + hand + mana


def _oracle_visible_policy_leaf(state: GameState) -> OracleVisibleChancePolicy:
    utility = tactical_utility(state, state.perspective_player_id)
    alive = state.player(state.perspective_player_id).hero.current_health > 0
    return OracleVisibleChancePolicy(
        expected_utility=Fraction(utility, 1),
        minimum_utility=utility,
        maximum_utility=utility,
        survival_probability=Fraction(int(alive), 1),
        actions=(),
        opponent_response=(),
        terminal_state=copy.deepcopy(state),
        recompute_after_random_outcome=False,
    )


def _oracle_visible_policy_action(
    state: GameState,
    action: Action,
    *,
    remaining: int,
    counter: list[int],
    max_nodes: int,
) -> OracleVisibleChancePolicy:
    actor_is_friendly = state.active_player_id == state.friendly.player_id
    random_resolution = _oracle_action_has_random_resolution(state, action)
    outcomes = _apply_oracle_action_outcomes(state, action)
    if not outcomes:
        raise TurnPairEvaluationError("chance transition produced no outcomes")

    children: list[tuple[OracleActionOutcome, OracleVisibleChancePolicy]] = []
    for outcome in outcomes:
        child_state = outcome.state
        if _terminal(child_state):
            child = _oracle_visible_policy_leaf(child_state)
        elif outcome.ended_turn and actor_is_friendly:
            response_start = _advance_to_visible_opponent_start(child_state)
            child = (
                _oracle_visible_policy_leaf(response_start)
                if remaining <= 1 or _terminal(response_start)
                else _oracle_visible_policy_state(
                    response_start,
                    remaining=remaining - 1,
                    counter=counter,
                    max_nodes=max_nodes,
                )
            )
        elif outcome.ended_turn or remaining <= 1:
            child = _oracle_visible_policy_leaf(child_state)
        else:
            child = _oracle_visible_policy_state(
                child_state,
                remaining=remaining - 1,
                counter=counter,
                max_nodes=max_nodes,
            )
        children.append((outcome, child))

    probability_sum = sum(
        (outcome.probability for outcome, _ in children), Fraction(0, 1)
    )
    if probability_sum != Fraction(1, 1):
        raise TurnPairEvaluationError("chance probabilities do not sum to one")
    expected_utility = sum(
        (
            outcome.probability * child.expected_utility
            for outcome, child in children
        ),
        Fraction(0, 1),
    )
    survival_probability = sum(
        (
            outcome.probability * child.survival_probability
            for outcome, child in children
        ),
        Fraction(0, 1),
    )
    minimum_utility = min(child.minimum_utility for _, child in children)
    maximum_utility = max(child.maximum_utility for _, child in children)
    representative = min(
        (child for _, child in children),
        key=lambda child: child.expected_utility,
    )
    branch_requires_recompute = random_resolution and len(outcomes) > 1
    actions = () if branch_requires_recompute else representative.actions
    opponent_response = (
        () if branch_requires_recompute else representative.opponent_response
    )
    if actor_is_friendly:
        actions = (action, *actions)
    else:
        opponent_response = (action, *opponent_response)
    return OracleVisibleChancePolicy(
        expected_utility=expected_utility,
        minimum_utility=minimum_utility,
        maximum_utility=maximum_utility,
        survival_probability=survival_probability,
        actions=actions,
        opponent_response=opponent_response,
        terminal_state=representative.terminal_state,
        recompute_after_random_outcome=(
            branch_requires_recompute
            or representative.recompute_after_random_outcome
        ),
    )


def _oracle_visible_policy_state(
    state: GameState,
    *,
    remaining: int,
    counter: list[int],
    max_nodes: int,
) -> OracleVisibleChancePolicy:
    if remaining <= 0 or _terminal(state):
        return _oracle_visible_policy_leaf(state)
    maximize = state.active_player_id == state.friendly.player_id
    selected: tuple[Action, OracleVisibleChancePolicy] | None = None
    for action in enumerate_oracle_actions(state):
        if counter[0] >= max_nodes:
            break
        counter[0] += 1
        candidate = _oracle_visible_policy_action(
            state,
            action,
            remaining=remaining,
            counter=counter,
            max_nodes=max_nodes,
        )
        if selected is None:
            selected = (action, candidate)
            continue
        selected_action, selected_value = selected
        better = (
            candidate.expected_utility > selected_value.expected_utility
            if maximize
            else candidate.expected_utility < selected_value.expected_utility
        )
        if better or (
            candidate.expected_utility == selected_value.expected_utility
            and action.action_id < selected_action.action_id
        ):
            selected = (action, candidate)
    return selected[1] if selected is not None else _oracle_visible_policy_leaf(state)


def evaluate_oracle_visible_policy_root(
    state: GameState,
    action: Action,
    *,
    max_depth: int = MAX_LINE_DEPTH,
    max_nodes: int = MAX_ENUMERATED_NODES,
) -> OracleVisibleChancePolicy:
    """Independent visible expectiminimax value for one legal root action.

    Friendly and opponent action nodes choose max and min respectively. Chance
    nodes average every exact public outcome, and each outcome receives a fresh
    downstream decision, so this is a policy value rather than a fixed line.
    """

    if max_depth <= 0 or max_nodes <= 0:
        raise TurnPairEvaluationError("chance search limits must be positive")
    counter = [0]
    result = _oracle_visible_policy_action(
        copy.deepcopy(state),
        action,
        remaining=max_depth,
        counter=counter,
        max_nodes=max_nodes,
    )
    return OracleVisibleChancePolicy(
        expected_utility=result.expected_utility,
        minimum_utility=result.minimum_utility,
        maximum_utility=result.maximum_utility,
        survival_probability=result.survival_probability,
        actions=result.actions,
        opponent_response=result.opponent_response,
        terminal_state=result.terminal_state,
        recompute_after_random_outcome=result.recompute_after_random_outcome,
        explored_nodes=counter[0],
    )


def _first_action_id(actions: Sequence[Action]) -> str:
    first = next((item for item in actions if item.kind != ActionKind.END_TURN), None)
    return first.action_id if first is not None else "end_turn"


def _response_for_friendly_line(
    friendly_line: OracleCompleteLine,
    perspective_player_id: str,
) -> tuple[int, bool, tuple[Action, ...], int]:
    if _terminal(friendly_line.state):
        value = tactical_utility(friendly_line.state, perspective_player_id)
        safe = friendly_line.state.player(perspective_player_id).hero.current_health > 0
        return value, safe, (), 0
    if not friendly_line.ended_turn:
        raise TurnPairEvaluationError("friendly line is not complete")
    response_start = _advance_to_opponent_start(friendly_line.state)
    if _terminal(response_start):
        value = tactical_utility(response_start, perspective_player_id)
        safe = response_start.player(perspective_player_id).hero.current_health > 0
        return value, safe, (), 0
    response_lines, explored = _enumerate_complete_lines(response_start)
    if not response_lines:
        raise TurnPairEvaluationError("opponent response enumeration produced no complete line")
    ranked = sorted(
        response_lines,
        key=lambda line: (
            tactical_utility(line.state, perspective_player_id),
            tuple(action.action_id for action in line.actions),
        ),
    )
    worst = ranked[0]
    value = tactical_utility(worst.state, perspective_player_id)
    safe = all(
        line.state.player(perspective_player_id).hero.current_health > 0
        for line in response_lines
    )
    return value, safe, worst.actions, explored


def prove_turnpair(
    state: GameState, *, allow_point_effects: bool = False
) -> OracleTurnPairProof:
    reasons = _unsupported_reasons(state, allow_point_effects=allow_point_effects)
    if reasons:
        return OracleTurnPairProof(True, reasons, ())
    friendly_lines, friendly_nodes = _enumerate_complete_lines(state)
    analyzed: list[OracleTurnPairLine] = []
    response_nodes = 0
    for line in friendly_lines:
        value, safe, response, explored = _response_for_friendly_line(
            line, state.perspective_player_id
        )
        response_nodes += explored
        enemy = line.state.other_player(state.perspective_player_id)
        analyzed.append(
            OracleTurnPairLine(
                actions=line.actions,
                opponent_response=response,
                minimax_value=value,
                safe_after_response=safe,
                immediate_lethal=enemy.hero.current_health <= 0,
            )
        )
    if not analyzed:
        raise TurnPairEvaluationError("turn-pair oracle produced no friendly line")
    optimal_value = max(item.minimax_value for item in analyzed)
    optimal_first = tuple(
        sorted(
            {
                item.first_action_id
                for item in analyzed
                if item.minimax_value == optimal_value
            }
        )
    )
    return OracleTurnPairProof(
        False,
        (),
        tuple(analyzed),
        optimal_value,
        optimal_first,
        friendly_nodes,
        response_nodes,
    )


def _parse_action(value: Any, path: str) -> Action:
    raw = _as_mapping(value, path)
    kind_raw = raw.get("kind") or raw.get("type")
    try:
        kind = ActionKind(str(kind_raw).lower())
    except ValueError as exc:
        raise TurnPairEvaluationError(f"{path}.kind is invalid") from exc
    return Action(
        kind=kind,
        source_entity_id=str(raw.get("source_entity_id") or ""),
        target_entity_id=str(raw.get("target_entity_id") or ""),
        card_id=str(raw.get("card_id") or ""),
        text=str(raw.get("text") or ""),
    )


def _parse_actions(value: Any, path: str) -> tuple[Action, ...]:
    return tuple(
        _parse_action(item, f"{path}[{index}]")
        for index, item in enumerate(_as_list(value, path))
    )


def assess_oracle_actions(state: GameState, actions: Sequence[Action]) -> OracleActionAssessment:
    current = copy.deepcopy(state)
    legal_count = 0
    ended = False
    for index, action in enumerate(actions):
        if ended or _terminal(current):
            return OracleActionAssessment(
                False,
                True,
                current,
                ended,
                index + 1,
                legal_count,
                "line contains actions after completion",
            )
        legal_ids = {item.action_id for item in enumerate_oracle_actions(current)}
        if action.action_id not in legal_ids:
            return OracleActionAssessment(
                False,
                False,
                current,
                ended,
                index + 1,
                legal_count,
                f"illegal action {action.action_id}",
            )
        legal_count += 1
        current, ended = _apply_oracle_action(current, action)
    complete = ended or _terminal(current)
    return OracleActionAssessment(
        True,
        complete,
        current,
        ended,
        len(actions),
        legal_count,
        "" if complete else "line does not end the turn or game",
    )


def assess_turnpair_line(state: GameState, actions: Sequence[Action]) -> CandidateLineAssessment:
    action_assessment = assess_oracle_actions(state, actions)
    immediate_lethal = bool(
        action_assessment.complete
        and action_assessment.state.other_player(state.perspective_player_id).hero.current_health <= 0
    )
    if not action_assessment.legal or not action_assessment.complete:
        return CandidateLineAssessment(
            action_assessment,
            None,
            None,
            immediate_lethal,
            None,
            (),
        )
    line = OracleCompleteLine(
        tuple(actions),
        action_assessment.state,
        action_assessment.ended_turn,
    )
    value, safe, response, explored = _response_for_friendly_line(
        line, state.perspective_player_id
    )
    response_start = None
    if not _terminal(action_assessment.state) and action_assessment.ended_turn:
        response_start = _advance_to_opponent_start(action_assessment.state)
    return CandidateLineAssessment(
        action_assessment,
        value,
        safe,
        immediate_lethal,
        response_start,
        response,
        explored,
    )


def _wire_action(action: Action, index: int) -> dict[str, Any]:
    payload = action.to_dict()
    payload.update({"index": index, "type": action.kind.value})
    return payload


def oracle_recommendation_payload(
    request_or_state: SolveRequest | GameState,
    *,
    top_k: int = 3,
) -> dict[str, Any]:
    state = request_or_state.state if isinstance(request_or_state, SolveRequest) else request_or_state
    proof = prove_turnpair(state)
    if proof.abstained:
        return {
            "status": "unsupported",
            "recommendations": [],
            "warnings": list(proof.reasons),
            "model_version": TURNPAIR_SUITE_ID,
        }
    best_by_first: dict[str, OracleTurnPairLine] = {}
    for line in proof.lines:
        current = best_by_first.get(line.first_action_id)
        if current is None or (
            line.minimax_value,
            tuple(action.action_id for action in line.actions),
        ) > (
            current.minimax_value,
            tuple(action.action_id for action in current.actions),
        ):
            best_by_first[line.first_action_id] = line
    ordered = sorted(
        (
            line
            for line in best_by_first.values()
            if line.minimax_value == proof.optimal_value
        ),
        key=lambda line: (
            -line.minimax_value,
            tuple(action.action_id for action in line.actions),
        ),
    )[: max(1, top_k)]
    recommendations: list[dict[str, Any]] = []
    for rank, line in enumerate(ordered, start=1):
        worst_case_score = (
            1.0
            if line.minimax_value >= WIN_UTILITY
            else 0.0
            if line.minimax_value <= LOSS_UTILITY
            else max(0.0, min(1.0, 0.5 + line.minimax_value / 20_000.0))
        )
        response_scope = RESPONSE_SCOPE
        response_items = [
            _wire_action(action, index)
            for index, action in enumerate(line.opponent_response, start=1)
        ]
        response_is_lethal = not line.safe_after_response
        score_components = {"oracle_tactical_utility": float(line.minimax_value)}
        recommendation = {
            "rank": rank,
            "actions": [
                _wire_action(action, index)
                for index, action in enumerate(line.actions, start=1)
            ],
            "expected_win_probability": 1.0 if line.immediate_lethal else 0.5,
            "score_kind": TACTICAL_SCORE_KIND,
            "minimax_value": line.minimax_value,
            "verified_portfolio_regret": 0,
            "alternative_kind": "co_optimal",
            "is_safe_after_response": line.safe_after_response,
            "is_response_verified": True,
            "response_kind": RESPONSE_KIND,
            "opponent_reply": response_items,
            "opponent_response": {
                "actions": response_items,
                "tactical_value": line.minimax_value,
            },
            "worst_case_score": worst_case_score,
            "response_scope": response_scope,
            "response_search_complete": True,
            "response_is_proven_lethal": response_is_lethal,
            "response_nodes_expanded": 0,
            "response_searched_depth": len(line.opponent_response),
            "response_transposition_hits": 0,
            "score_components": score_components,
            "counterplay": {
                "scope": response_scope,
                "search_complete": True,
                "is_proven_lethal": response_is_lethal,
                "worst_case_score": worst_case_score,
                "nodes_expanded": 0,
                "searched_depth": len(line.opponent_response),
                "transposition_hits": 0,
                "actions": response_items,
                "score_components": score_components,
            },
            "is_proven_lethal": line.immediate_lethal,
            "proof_kind": "modeled_lethal" if line.immediate_lethal else "",
            "proof_scope": "visible_generic_v2" if line.immediate_lethal else "",
        }
        recommendations.append(recommendation)
    legal_first_action_ids = sorted(
        {
            _first_action_id((action,))
            for action in enumerate_oracle_actions(state)
        }
    )
    legal_first_action_count = len(legal_first_action_ids)
    counterplay_coverage = {
        "legal_first_action_ids": list(legal_first_action_ids),
        "legal_first_action_count": legal_first_action_count,
        "generated_first_action_ids": list(legal_first_action_ids),
        "generated_first_action_count": legal_first_action_count,
        "response_verified_first_action_ids": list(legal_first_action_ids),
        "response_verified_first_action_count": legal_first_action_count,
        "missing_first_action_ids": [],
        "root_action_coverage_complete": True,
        "portfolio_optimality_proven": True,
        "assessed_first_action_count": legal_first_action_count,
        "structurally_complete_first_action_count": legal_first_action_count,
        "unassessed_first_action_count": 0,
        "search_complete": True,
    }
    return {
        "status": "ok",
        "recommendations": recommendations,
        "model_version": TURNPAIR_SUITE_ID,
        "coverage": {
            "exact": True,
            "rules_model": TURNPAIR_SUITE_ID,
            "details": {"counterplay": counterplay_coverage},
        },
    }


def _candidate_payload(value: Any) -> dict[str, Any]:
    if hasattr(value, "to_dict") and callable(value.to_dict):
        value = value.to_dict()
    if isinstance(value, Sequence) and not isinstance(value, (str, bytes, bytearray, Mapping)):
        value = {"status": "ok", "recommendations": list(value)}
    root = dict(_as_mapping(value, "candidate result"))
    recommendations = root.get("recommendations", [])
    root["recommendations"] = [
        dict(_as_mapping(item, f"candidate.recommendations[{index}]"))
        for index, item in enumerate(_as_list(recommendations, "candidate.recommendations"))
    ]
    root["recommendations"].sort(
        key=lambda item: item.get("rank") if isinstance(item.get("rank"), int) else 1_000_000
    )
    return root


def _numeric(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _nonnegative_integer(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def _oracle_first_action_values(proof: OracleTurnPairProof) -> dict[str, int]:
    values: dict[str, int] = {}
    for line in proof.lines:
        current = values.get(line.first_action_id)
        if current is None or line.minimax_value > current:
            values[line.first_action_id] = line.minimax_value
    return values


def _root_action_coverage_contract(
    candidate: Mapping[str, Any],
    legal_first_action_ids: Sequence[str],
) -> tuple[dict[str, Any], list[str]]:
    errors: list[str] = []
    coverage_raw = candidate.get("coverage")
    coverage = coverage_raw if isinstance(coverage_raw, Mapping) else {}
    details_raw = coverage.get("details")
    details = details_raw if isinstance(details_raw, Mapping) else {}
    counterplay_raw = details.get("counterplay")
    counterplay = counterplay_raw if isinstance(counterplay_raw, Mapping) else {}
    if not isinstance(counterplay_raw, Mapping):
        errors.append("coverage.details.counterplay must be an object")

    def canonical_ids(field_name: str) -> tuple[list[str], bool]:
        raw = counterplay.get(field_name)
        valid_items = bool(
            isinstance(raw, list)
            and all(isinstance(item, str) and item for item in raw)
        )
        values = list(raw) if valid_items else []
        canonical = bool(
            valid_items
            and values == sorted(values)
            and len(values) == len(set(values))
        )
        if not canonical:
            errors.append(
                f"coverage.details.counterplay.{field_name} must be a "
                "sorted array of distinct non-empty strings"
            )
        return values, canonical

    legal_ids, legal_ids_valid = canonical_ids("legal_first_action_ids")
    generated_ids, generated_ids_valid = canonical_ids(
        "generated_first_action_ids"
    )
    verified_ids, verified_ids_valid = canonical_ids(
        "response_verified_first_action_ids"
    )
    missing_ids, missing_ids_valid = canonical_ids("missing_first_action_ids")
    legal_count = counterplay.get("legal_first_action_count")
    generated_count = counterplay.get("generated_first_action_count")
    verified_count = counterplay.get("response_verified_first_action_count")
    complete = counterplay.get("root_action_coverage_complete")
    portfolio_optimality_proven = counterplay.get(
        "portfolio_optimality_proven"
    )
    count_contracts = (
        ("legal_first_action_count", legal_count, legal_ids, legal_ids_valid),
        (
            "generated_first_action_count",
            generated_count,
            generated_ids,
            generated_ids_valid,
        ),
        (
            "response_verified_first_action_count",
            verified_count,
            verified_ids,
            verified_ids_valid,
        ),
    )
    for field_name, value, identifiers, identifiers_valid in count_contracts:
        if not _nonnegative_integer(value):
            errors.append(
                f"coverage.details.counterplay.{field_name} must be a "
                "non-negative integer"
            )
        elif identifiers_valid and value != len(identifiers):
            errors.append(
                f"coverage.details.counterplay.{field_name}={value} does "
                f"not match its ID array length {len(identifiers)}"
            )
    if not isinstance(complete, bool):
        errors.append(
            "coverage.details.counterplay.root_action_coverage_complete must be boolean"
        )
    if not isinstance(portfolio_optimality_proven, bool):
        errors.append(
            "coverage.details.counterplay.portfolio_optimality_proven must be boolean"
        )

    oracle_ids = sorted(set(legal_first_action_ids))
    if legal_ids_valid and legal_ids != oracle_ids:
        errors.append(
            f"reported legal_first_action_ids={legal_ids} != oracle {oracle_ids}"
        )
    if legal_ids_valid and generated_ids_valid and not set(
        generated_ids
    ).issubset(legal_ids):
        errors.append(
            "generated_first_action_ids must be a subset of legal_first_action_ids"
        )
    if generated_ids_valid and verified_ids_valid and not set(
        verified_ids
    ).issubset(generated_ids):
        errors.append(
            "response_verified_first_action_ids must be a subset of "
            "generated_first_action_ids"
        )
    if legal_ids_valid and verified_ids_valid and missing_ids_valid:
        expected_missing_ids = sorted(set(legal_ids) - set(verified_ids))
        if missing_ids != expected_missing_ids:
            errors.append(
                f"missing_first_action_ids={missing_ids} must equal legal "
                f"minus response-verified IDs {expected_missing_ids}"
            )

    exact_sets_complete = bool(
        legal_ids_valid
        and generated_ids_valid
        and verified_ids_valid
        and missing_ids_valid
        and legal_ids == generated_ids == verified_ids
        and not missing_ids
    )
    if not exact_sets_complete:
        errors.append(
            "exact fixture requires legal, generated, and response-verified "
            "first-action ID arrays to be identical with no missing IDs"
        )
    if isinstance(complete, bool) and complete != exact_sets_complete:
        errors.append(
            "root_action_coverage_complete is inconsistent with the canonical "
            "root-action ID arrays"
        )
    if portfolio_optimality_proven is True and not exact_sets_complete:
        errors.append(
            "portfolio_optimality_proven=true requires complete exact root-action coverage"
        )
    if complete is not True:
        errors.append("exact fixture root action coverage is not complete")

    reported = {
        "legal_first_action_ids": legal_ids,
        "legal_first_action_count": legal_count,
        "generated_first_action_ids": generated_ids,
        "generated_first_action_count": generated_count,
        "response_verified_first_action_ids": verified_ids,
        "response_verified_first_action_count": verified_count,
        "missing_first_action_ids": missing_ids,
        "root_action_coverage_complete": complete,
        "portfolio_optimality_proven": portfolio_optimality_proven,
    }
    return reported, errors


def _metric_check(name: str, value: float | int, operator: str, threshold: float | int) -> dict[str, Any]:
    if operator == ">=":
        passed = value >= threshold
    elif operator == "<=":
        passed = value <= threshold
    else:
        raise TurnPairEvaluationError(f"unsupported metric operator: {operator}")
    return {
        "name": name,
        "value": value,
        "operator": operator,
        "threshold": threshold,
        "passed": bool(passed),
    }


def _threshold_checks(metrics: Mapping[str, Any], thresholds: Mapping[str, Any]) -> list[dict[str, Any]]:
    definitions: dict[str, tuple[str, float | int, str]] = {
        "min_top1_rate": (">=", 1.0, "top1_rate"),
        "min_top3_rate": (">=", 1.0, "top3_rate"),
        "min_friendly_action_legality_rate": (">=", 1.0, "friendly_action_legality_rate"),
        "min_response_action_legality_rate": (">=", 1.0, "response_action_legality_rate"),
        "max_mean_minimax_regret": ("<=", 0.0, "mean_minimax_regret"),
        "max_max_minimax_regret": ("<=", 0, "max_minimax_regret"),
        "max_false_safe_rate": ("<=", 0.0, "false_safe_rate"),
        "max_proof_contract_failure_count": ("<=", 0, "proof_contract_failure_count"),
        "max_response_contract_failure_count": ("<=", 0, "response_contract_failure_count"),
        "max_fixture_contract_failure_count": ("<=", 0, "fixture_contract_failure_count"),
        "max_abstain_violation_count": ("<=", 0, "abstain_violation_count"),
        "min_multi_optimal_first_action_recall_at_k": (
            ">=",
            1.0,
            "multi_optimal_first_action_recall_at_k",
        ),
        "min_distinct_recommended_first_action_rate": (
            ">=",
            1.0,
            "distinct_recommended_first_action_rate",
        ),
        "max_duplicate_first_action_count": (
            "<=",
            0,
            "duplicate_first_action_count",
        ),
        "max_root_action_coverage_contract_failure_count": (
            "<=",
            0,
            "root_action_coverage_contract_failure_count",
        ),
        "max_portfolio_regret_contract_failure_count": (
            "<=",
            0,
            "portfolio_regret_contract_failure_count",
        ),
        "max_viable_portfolio_contract_failure_count": (
            "<=",
            0,
            "viable_portfolio_contract_failure_count",
        ),
        "min_multi_optimal_fixture_count": (
            ">=",
            1,
            "multi_optimal_fixture_count",
        ),
        "min_exact_fixture_count": (">=", 1, "exact_fixture_count"),
        "max_latency_p95_ms": ("<=", 10_000.0, "latency_p95_ms"),
    }
    checks: list[dict[str, Any]] = []
    for threshold_name, (operator, default, metric_name) in definitions.items():
        threshold = thresholds.get(threshold_name, default)
        if not _numeric(threshold):
            raise TurnPairEvaluationError(f"threshold {threshold_name} must be numeric")
        checks.append(_metric_check(metric_name, metrics[metric_name], operator, threshold))
    return checks


def evaluate_turnpair_suite(
    fixture_path: str | Path,
    solve: Callable[[SolveRequest], Any],
    *,
    seed_override: int | None = None,
) -> dict[str, Any]:
    suite = load_turnpair_suite(fixture_path)
    suite_seed = suite.get("seed", 0) if seed_override is None else seed_override
    if isinstance(suite_seed, bool) or not isinstance(suite_seed, int):
        raise TurnPairEvaluationError("turn-pair seed must be an integer")

    fixture_hashes: list[dict[str, str]] = []
    details: list[dict[str, Any]] = []
    latencies: list[float] = []
    exact_count = 0
    approximate_count = 0
    abstain_count = 0
    top1_count = 0
    top3_count = 0
    friendly_assessed_actions = 0
    friendly_legal_actions = 0
    response_assessed_actions = 0
    response_legal_actions = 0
    friendly_complete_lines = 0
    response_complete_lines = 0
    assessed_candidate_lines = 0
    assessed_response_lines = 0
    regrets: list[int] = []
    safe_claim_count = 0
    false_safe_count = 0
    proof_contract_failures = 0
    response_contract_failures = 0
    fixture_contract_failures = 0
    abstain_violations = 0
    multi_optimal_fixture_count = 0
    multi_optimal_required_first_action_count = 0
    multi_optimal_recalled_first_action_count = 0
    recommended_first_action_count = 0
    distinct_recommended_first_action_count = 0
    duplicate_first_action_count = 0
    root_action_coverage_contract_failures = 0
    portfolio_regret_contract_failures = 0
    viable_portfolio_contract_failures = 0
    portfolio_first_action_regrets: list[int] = []
    returned_alternative_regrets: list[int] = []

    for fixture in suite["fixtures"]:
        fixture_id = fixture["id"]
        scope = fixture["scope"]
        fixture_hash = _canonical_hash(fixture)
        fixture_hashes.append({"id": fixture_id, "sha256": fixture_hash})
        request = request_from_turnpair_fixture(fixture, suite_seed)
        oracle = prove_turnpair(request.state)
        contract_errors: list[str] = []
        expected = fixture.get("expected", {})
        if expected is not None and not isinstance(expected, Mapping):
            raise TurnPairEvaluationError(f"fixture {fixture_id}.expected must be an object")
        expected = expected if isinstance(expected, Mapping) else {}
        legal_root_first_action_ids: tuple[str, ...] = ()
        oracle_first_action_values: dict[str, int] = {}
        required_portfolio_first_action_ids: tuple[str, ...] = ()
        max_returned_alternative_regret: float | int | None = None
        expected_root_action_coverage_complete = expected.get(
            "root_action_coverage_complete"
        )
        if (
            expected_root_action_coverage_complete is not None
            and not isinstance(expected_root_action_coverage_complete, bool)
        ):
            raise TurnPairEvaluationError(
                f"fixture {fixture_id}.expected.root_action_coverage_complete "
                "must be boolean"
            )
        canonical_regret_key = "max_returned_alternative_regret"
        legacy_regret_key = "max_portfolio_first_action_minimax_regret"
        if (
            canonical_regret_key in expected
            and legacy_regret_key in expected
            and expected.get(canonical_regret_key)
            != expected.get(legacy_regret_key)
        ):
            raise TurnPairEvaluationError(
                f"fixture {fixture_id}.expected has conflicting canonical "
                "and legacy returned-alternative regret limits"
            )
        regret_limit_key = (
            canonical_regret_key
            if canonical_regret_key in expected
            else legacy_regret_key
        )
        if regret_limit_key in expected:
            max_portfolio_regret_raw = expected.get(regret_limit_key)
            if (
                not _numeric(max_portfolio_regret_raw)
                or not math.isfinite(float(max_portfolio_regret_raw))
                or max_portfolio_regret_raw < 0
            ):
                raise TurnPairEvaluationError(
                    f"fixture {fixture_id}.expected.{regret_limit_key} must be a "
                    "non-negative finite number"
                )
            max_returned_alternative_regret = max_portfolio_regret_raw
        if scope == "exact":
            exact_count += 1
            if oracle.abstained:
                contract_errors.append("exact fixture caused the independent oracle to abstain")
            expected_actions = expected.get("optimal_first_action_ids")
            if expected_actions is not None:
                if not isinstance(expected_actions, list) or not all(
                    isinstance(item, str) for item in expected_actions
                ):
                    raise TurnPairEvaluationError(
                        f"fixture {fixture_id}.expected.optimal_first_action_ids must be strings"
                    )
                if sorted(expected_actions) != list(oracle.optimal_first_action_ids):
                    contract_errors.append(
                        "oracle optimal_first_action_ids="
                        f"{list(oracle.optimal_first_action_ids)} != expected {sorted(expected_actions)}"
                    )
            expected_value = expected.get("optimal_value")
            if expected_value is not None and expected_value != oracle.optimal_value:
                contract_errors.append(
                    f"oracle optimal_value={oracle.optimal_value} != expected {expected_value}"
                )
            if not oracle.abstained:
                legal_root_first_action_ids = tuple(
                    sorted(
                        {
                            _first_action_id((action,))
                            for action in enumerate_oracle_actions(
                                request.state
                            )
                        }
                    )
                )
                oracle_first_action_values = _oracle_first_action_values(oracle)
                if set(legal_root_first_action_ids) != set(
                    oracle_first_action_values
                ):
                    contract_errors.append(
                        "oracle complete-line roots do not match independently "
                        "enumerated legal root actions"
                    )
                if len(oracle.optimal_first_action_ids) > 1:
                    multi_optimal_fixture_count += 1
                    required_actions_raw = expected.get(
                        "required_portfolio_first_action_ids"
                    )
                    if not isinstance(required_actions_raw, list) or not all(
                        isinstance(item, str) and item
                        for item in required_actions_raw
                    ):
                        raise TurnPairEvaluationError(
                            f"fixture {fixture_id}.expected."
                            "required_portfolio_first_action_ids must be an "
                            "array of non-empty strings"
                        )
                    if len(required_actions_raw) != len(set(required_actions_raw)):
                        contract_errors.append(
                            "required_portfolio_first_action_ids contains duplicates"
                        )
                    required_portfolio_first_action_ids = tuple(
                        sorted(set(required_actions_raw))
                    )
                    if len(required_portfolio_first_action_ids) < 2:
                        contract_errors.append(
                            "multi-optimal fixture must require at least two "
                            "portfolio first actions"
                        )
                    nonoptimal_required = sorted(
                        set(required_portfolio_first_action_ids)
                        - set(oracle.optimal_first_action_ids)
                    )
                    if nonoptimal_required:
                        contract_errors.append(
                            "required portfolio first actions are not oracle "
                            f"co-optimal: {nonoptimal_required}"
                        )
                    multi_optimal_required_first_action_count += len(
                        required_portfolio_first_action_ids
                    )
        elif scope == "approximate":
            approximate_count += 1
            if not oracle.abstained:
                contract_errors.append("approximate fixture unexpectedly entered exact oracle scope")
        else:
            abstain_count += 1
            if not oracle.abstained:
                contract_errors.append("abstain fixture unexpectedly entered exact oracle scope")

        started = time.perf_counter()
        candidate = _candidate_payload(solve(request))
        wall_ms = (time.perf_counter() - started) * 1000.0
        latencies.append(wall_ms)
        recommendations = candidate["recommendations"][:3]
        if scope == "abstain" and recommendations:
            abstain_violations += 1

        reported_root_action_coverage: dict[str, Any] = {}
        root_action_coverage_errors: list[str] = []
        if scope == "exact" and not oracle.abstained:
            (
                reported_root_action_coverage,
                root_action_coverage_errors,
            ) = _root_action_coverage_contract(
                candidate,
                legal_root_first_action_ids,
            )
            if (
                expected_root_action_coverage_complete is not None
                and reported_root_action_coverage.get(
                    "root_action_coverage_complete"
                )
                is not expected_root_action_coverage_complete
            ):
                root_action_coverage_errors.append(
                    "reported root_action_coverage_complete does not match "
                    "the fixture contract"
                )
            if root_action_coverage_errors:
                root_action_coverage_contract_failures += 1

        fixture_assessments: list[CandidateLineAssessment | None] = []
        fixture_portfolio_assessments: list[dict[str, Any]] = []
        fixture_recommended_first_action_ids: list[str] = []
        fixture_response_contract_failures = 0
        fixture_proof_contract_failures = 0
        for recommendation_index, recommendation in enumerate(recommendations, start=1):
            actions: tuple[Action, ...] = ()
            action_parse_error = ""
            try:
                actions = _parse_actions(
                    recommendation.get("actions", []),
                    f"fixture {fixture_id}.recommendation {recommendation_index}.actions",
                )
            except TurnPairEvaluationError as exc:
                action_parse_error = str(exc)
            first_action_id = ""
            root_action_legal = False
            first_action_minimax_value: int | None = None
            first_action_minimax_regret = MISSING_LINE_REGRET
            if scope == "exact" and not oracle.abstained:
                first_action_id = (
                    _first_action_id(actions)
                    if not action_parse_error and actions
                    else ""
                )
                if first_action_id:
                    fixture_recommended_first_action_ids.append(first_action_id)
                root_action_legal = bool(
                    first_action_id
                    and first_action_id in set(legal_root_first_action_ids)
                )
                first_action_minimax_value = (
                    oracle_first_action_values.get(first_action_id)
                    if root_action_legal
                    else None
                )
                first_action_minimax_regret = (
                    max(
                        0,
                        oracle.optimal_value - first_action_minimax_value,
                    )
                    if first_action_minimax_value is not None
                    else MISSING_LINE_REGRET
                )
                portfolio_first_action_regrets.append(
                    first_action_minimax_regret
                )
            if action_parse_error:
                assessment = None
                friendly_assessed_actions += 1
                if scope == "exact":
                    response_contract_failures += 1
                    fixture_response_contract_failures += 1
            elif scope == "exact" and not oracle.abstained:
                assessment = assess_turnpair_line(request.state, actions)
                assessed_candidate_lines += 1
                friendly_assessed_actions += assessment.action_assessment.assessed_action_count
                friendly_legal_actions += assessment.action_assessment.legal_action_count
                if assessment.action_assessment.complete:
                    friendly_complete_lines += 1
            else:
                assessment = None
            fixture_assessments.append(assessment)

            if scope == "exact" and not oracle.abstained:
                returned_line_minimax_value = (
                    assessment.minimax_value
                    if assessment is not None
                    and assessment.action_assessment.legal
                    and assessment.action_assessment.complete
                    and assessment.minimax_value is not None
                    else None
                )
                returned_alternative_regret = (
                    max(
                        0,
                        oracle.optimal_value - returned_line_minimax_value,
                    )
                    if returned_line_minimax_value is not None
                    else MISSING_LINE_REGRET
                )
                returned_alternative_regrets.append(
                    returned_alternative_regret
                )
                portfolio_contract_errors: list[str] = []
                if not root_action_legal:
                    portfolio_contract_errors.append(
                        "first action is not a legal independent-oracle root action"
                    )
                if returned_line_minimax_value is None:
                    portfolio_contract_errors.append(
                        "returned alternative is not a legal complete line"
                    )
                if (
                    max_returned_alternative_regret is not None
                    and returned_alternative_regret
                    > max_returned_alternative_regret
                ):
                    portfolio_contract_errors.append(
                        "returned alternative regret exceeds the fixture maximum"
                    )

                reported_portfolio_regret = recommendation.get(
                    "verified_portfolio_regret"
                )
                reported_regret_valid = bool(
                    _numeric(reported_portfolio_regret)
                    and math.isfinite(float(reported_portfolio_regret))
                    and reported_portfolio_regret >= 0
                )
                if not reported_regret_valid:
                    portfolio_contract_errors.append(
                        "verified_portfolio_regret must be a finite "
                        "non-negative number"
                    )
                elif (
                    abs(
                        float(reported_portfolio_regret)
                        - float(returned_alternative_regret)
                    )
                    > 1e-9
                ):
                    portfolio_contract_errors.append(
                        "verified_portfolio_regret does not match the "
                        "independent returned-line regret"
                    )

                portfolio_optimality_proven = (
                    reported_root_action_coverage.get(
                        "portfolio_optimality_proven"
                    )
                    is True
                )
                if not portfolio_optimality_proven:
                    expected_alternative_kind = (
                        "best_found"
                        if returned_alternative_regret == 0
                        else "backup"
                    )
                elif returned_alternative_regret == 0:
                    expected_alternative_kind = "co_optimal"
                elif returned_alternative_regret <= NEAR_OPTIMAL_REGRET_MAX:
                    expected_alternative_kind = "near_optimal"
                else:
                    expected_alternative_kind = "backup"
                reported_alternative_kind = recommendation.get(
                    "alternative_kind"
                )
                if reported_alternative_kind == "fallback":
                    portfolio_contract_errors.append(
                        "an exact response-verified recommendation must not "
                        "use alternative_kind=fallback"
                    )
                if reported_alternative_kind != expected_alternative_kind:
                    portfolio_contract_errors.append(
                        f"alternative_kind={reported_alternative_kind!r} "
                        f"must be {expected_alternative_kind!r}"
                    )

                portfolio_regret_passed = not portfolio_contract_errors
                if portfolio_contract_errors:
                    portfolio_regret_contract_failures += 1

                oracle_has_safe_line = any(
                    line.safe_after_response for line in oracle.lines
                )
                returned_line_safe = bool(
                    assessment is not None
                    and assessment.safe_after_response is True
                )
                viability_contract_errors: list[str] = []
                if oracle_has_safe_line and not returned_line_safe:
                    viability_contract_errors.append(
                        "returned alternative is unsafe while the independent "
                        "oracle has a safe line"
                    )
                    viable_portfolio_contract_failures += 1

                fixture_portfolio_assessments.append(
                    {
                        "rank": recommendation_index,
                        "first_action_id": first_action_id,
                        "root_action_legal": root_action_legal,
                        "oracle_first_action_minimax_value": (
                            first_action_minimax_value
                        ),
                        "first_action_minimax_regret": (
                            first_action_minimax_regret
                        ),
                        "returned_line_minimax_value": (
                            returned_line_minimax_value
                        ),
                        "returned_alternative_regret": (
                            returned_alternative_regret
                        ),
                        "max_allowed_returned_alternative_regret": (
                            max_returned_alternative_regret
                        ),
                        "reported_verified_portfolio_regret": (
                            reported_portfolio_regret
                        ),
                        "portfolio_optimality_proven": (
                            portfolio_optimality_proven
                        ),
                        "expected_alternative_kind": (
                            expected_alternative_kind
                        ),
                        "reported_alternative_kind": (
                            reported_alternative_kind
                        ),
                        "contract_errors": portfolio_contract_errors,
                        "regret_contract_passed": portfolio_regret_passed,
                        "oracle_has_safe_line": oracle_has_safe_line,
                        "returned_line_safe": returned_line_safe,
                        "viability_contract_errors": (
                            viability_contract_errors
                        ),
                        "viability_contract_passed": (
                            not viability_contract_errors
                        ),
                    }
                )

            has_proof_signal = bool(
                recommendation.get("is_proven_lethal")
                or recommendation.get("proof_kind")
                or recommendation.get("proof_scope")
            )
            valid_proof_fields = bool(
                recommendation.get("is_proven_lethal") is True
                and recommendation.get("proof_kind") == "modeled_lethal"
                and recommendation.get("proof_scope") in {RESPONSE_SCOPE, "visible_generic_v2"}
            )
            proof_failure = False
            if has_proof_signal and not valid_proof_fields:
                proof_failure = True
            if valid_proof_fields and (assessment is None or not assessment.immediate_lethal):
                proof_failure = True
            if proof_failure:
                proof_contract_failures += 1
                fixture_proof_contract_failures += 1

            has_response_signal = any(
                key in recommendation
                for key in (
                    "opponent_reply",
                    "worst_case_score",
                    "response_search_complete",
                    "response_is_proven_lethal",
                    "counterplay",
                    "is_response_verified",
                    "response_kind",
                    "response_scope",
                    "opponent_response",
                    "minimax_value",
                    "is_safe_after_response",
                )
            )
            if scope != "exact":
                counterplay_nonexact = recommendation.get("counterplay")
                nested_complete = (
                    counterplay_nonexact.get("search_complete")
                    if isinstance(counterplay_nonexact, Mapping)
                    else None
                )
                if has_response_signal and (
                    recommendation.get("response_search_complete") is True
                    or recommendation.get("is_response_verified") is True
                    or nested_complete is True
                ):
                    response_contract_failures += 1
                    fixture_response_contract_failures += 1
                continue

            response_failure = False
            if assessment is None or not assessment.action_assessment.complete:
                response_failure = True
            if recommendation.get("score_kind") != TACTICAL_SCORE_KIND:
                response_failure = True
            counterplay_raw = recommendation.get("counterplay")
            counterplay = counterplay_raw if isinstance(counterplay_raw, Mapping) else {}
            response_scope = recommendation.get("response_scope")
            response_complete = recommendation.get("response_search_complete")
            response_is_lethal = recommendation.get("response_is_proven_lethal")
            response_verified = recommendation.get("is_response_verified")
            response_kind = recommendation.get("response_kind")
            minimax_value = recommendation.get("minimax_value")
            explicit_safe = recommendation.get("is_safe_after_response")
            if response_scope != RESPONSE_SCOPE:
                response_failure = True
            if response_complete is not True:
                response_failure = True
            if not isinstance(response_is_lethal, bool):
                response_failure = True
            if response_verified is not True:
                response_failure = True
            if response_kind != RESPONSE_KIND:
                response_failure = True
            if not _numeric(minimax_value) or not math.isfinite(float(minimax_value)):
                response_failure = True
            if not isinstance(explicit_safe, bool):
                response_failure = True
            worst_case_score = recommendation.get("worst_case_score")
            if not _numeric(worst_case_score) or not math.isfinite(float(worst_case_score)):
                response_failure = True
            score_components = recommendation.get("score_components")
            if not isinstance(score_components, Mapping) or any(
                not _numeric(value) or not math.isfinite(float(value))
                for value in score_components.values()
            ):
                response_failure = True
            if assessment is not None and assessment.minimax_value is not None:
                if _numeric(minimax_value) and (
                    abs(float(minimax_value) - float(assessment.minimax_value)) > 1e-9
                ):
                    response_failure = True
                if (
                    isinstance(explicit_safe, bool)
                    and assessment.safe_after_response is not None
                ):
                    if response_verified is True and explicit_safe:
                        safe_claim_count += 1
                        if assessment.safe_after_response is not True:
                            false_safe_count += 1
                    if explicit_safe != assessment.safe_after_response:
                        response_failure = True
                if (
                    isinstance(response_is_lethal, bool)
                    and assessment.safe_after_response is not None
                    and (
                    response_is_lethal != (assessment.safe_after_response is False)
                    )
                ):
                    response_failure = True

            opponent_response_raw = recommendation.get("opponent_response")
            if isinstance(opponent_response_raw, Mapping):
                response_raw = opponent_response_raw.get("actions")
                tactical_value = opponent_response_raw.get("tactical_value")
            else:
                response_raw = None
                tactical_value = None
                response_failure = True
            if not _numeric(tactical_value) or not math.isfinite(float(tactical_value)):
                response_failure = True
            elif assessment is not None and assessment.minimax_value is not None and (
                abs(float(tactical_value) - float(assessment.minimax_value)) > 1e-9
            ):
                response_failure = True

            response_actions: tuple[Action, ...] = ()
            if not isinstance(response_raw, list):
                response_failure = True
                response_parse_failed = True
                response_assessed_actions += 1
            else:
                try:
                    response_actions = _parse_actions(
                        response_raw,
                        f"fixture {fixture_id}.recommendation "
                        f"{recommendation_index}.opponent_response.actions",
                    )
                    response_parse_failed = False
                except TurnPairEvaluationError:
                    response_parse_failed = True
                    response_assessed_actions += 1
                    response_failure = True

            opponent_reply_raw = recommendation.get("opponent_reply")
            opponent_reply_actions: tuple[Action, ...] = ()
            opponent_reply_parse_failed = False
            if not isinstance(opponent_reply_raw, list):
                opponent_reply_parse_failed = True
                response_failure = True
            else:
                try:
                    opponent_reply_actions = _parse_actions(
                        opponent_reply_raw,
                        f"fixture {fixture_id}.recommendation "
                        f"{recommendation_index}.opponent_reply",
                    )
                except TurnPairEvaluationError:
                    opponent_reply_parse_failed = True
                    response_failure = True
            if not response_parse_failed and not opponent_reply_parse_failed and (
                tuple(action.action_id for action in opponent_reply_actions)
                != tuple(action.action_id for action in response_actions)
            ):
                response_failure = True

            if not response_parse_failed and assessment is not None:
                assessed_response_lines += 1
                if assessment.response_start is None:
                    if response_actions:
                        response_assessed_actions += len(response_actions)
                        response_failure = True
                    else:
                        response_complete_lines += 1
                else:
                    response_assessment = assess_oracle_actions(
                        assessment.response_start, response_actions
                    )
                    response_assessed_actions += response_assessment.assessed_action_count
                    response_legal_actions += response_assessment.legal_action_count
                    if response_assessment.complete:
                        response_complete_lines += 1
                    response_utility = (
                        tactical_utility(response_assessment.state, request.state.perspective_player_id)
                        if response_assessment.legal and response_assessment.complete
                        else None
                    )
                    if (
                        not response_assessment.legal
                        or not response_assessment.complete
                        or response_utility != assessment.minimax_value
                    ):
                        response_failure = True
            if isinstance(counterplay_raw, Mapping):
                if counterplay.get("scope") != response_scope:
                    response_failure = True
                if counterplay.get("search_complete") != response_complete:
                    response_failure = True
                if counterplay.get("is_proven_lethal") != response_is_lethal:
                    response_failure = True
                nested_score = counterplay.get("worst_case_score")
                if (
                    _numeric(nested_score)
                    and _numeric(worst_case_score)
                    and abs(float(nested_score) - float(worst_case_score)) > 1e-9
                ):
                    response_failure = True
            if response_failure:
                response_contract_failures += 1
                fixture_response_contract_failures += 1

        fixture_distinct_first_action_ids = set(
            fixture_recommended_first_action_ids
        )
        fixture_duplicate_first_action_count = max(
            0,
            len(fixture_recommended_first_action_ids)
            - len(fixture_distinct_first_action_ids),
        )
        recalled_required_first_action_ids: tuple[str, ...] = ()
        if scope == "exact" and not oracle.abstained:
            recommended_first_action_count += len(recommendations)
            distinct_recommended_first_action_count += len(
                fixture_distinct_first_action_ids
            )
            duplicate_first_action_count += (
                fixture_duplicate_first_action_count
            )
            if required_portfolio_first_action_ids:
                recalled_required_first_action_ids = tuple(
                    sorted(
                        fixture_distinct_first_action_ids.intersection(
                            required_portfolio_first_action_ids
                        )
                    )
                )
                multi_optimal_recalled_first_action_count += len(
                    recalled_required_first_action_ids
                )

        if scope == "exact" and not oracle.abstained:
            top1 = fixture_assessments[0] if fixture_assessments else None
            if (
                top1 is not None
                and top1.action_assessment.legal
                and top1.action_assessment.complete
            ):
                first = _first_action_id(_parse_actions(recommendations[0]["actions"], "top1.actions"))
                if first in oracle.optimal_first_action_ids:
                    top1_count += 1
                regrets.append(max(0, oracle.optimal_value - int(top1.minimax_value)))
            else:
                regrets.append(MISSING_LINE_REGRET)
            top3_actions: set[str] = set()
            for recommendation, assessment in zip(recommendations, fixture_assessments):
                if assessment is None or not assessment.action_assessment.complete:
                    continue
                parsed = _parse_actions(recommendation.get("actions", []), "top3.actions")
                top3_actions.add(_first_action_id(parsed))
            if top3_actions.intersection(oracle.optimal_first_action_ids):
                top3_count += 1

        if contract_errors:
            fixture_contract_failures += 1
        details.append(
            {
                "id": fixture_id,
                "category": fixture.get("category", ""),
                "scope": scope,
                "fixture_sha256": fixture_hash,
                "latency_ms": round(wall_ms, 3),
                "oracle": (
                    {
                        "abstained": True,
                        "reasons": list(oracle.reasons),
                    }
                    if oracle.abstained
                    else {
                        "optimal_value": oracle.optimal_value,
                        "optimal_first_action_ids": list(oracle.optimal_first_action_ids),
                        "friendly_nodes": oracle.explored_friendly_nodes,
                        "response_nodes": oracle.explored_response_nodes,
                    }
                ),
                "recommendation_count": len(recommendations),
                "contract_errors": contract_errors,
                "proof_contract_failure_count": fixture_proof_contract_failures,
                "response_contract_failure_count": fixture_response_contract_failures,
                "portfolio": {
                    "required_first_action_ids": list(
                        required_portfolio_first_action_ids
                    ),
                    "recalled_required_first_action_ids": list(
                        recalled_required_first_action_ids
                    ),
                    "duplicate_first_action_count": (
                        fixture_duplicate_first_action_count
                    ),
                    "recommendations": fixture_portfolio_assessments,
                },
                "root_action_coverage": {
                    "oracle_legal_first_action_ids": list(
                        legal_root_first_action_ids
                    ),
                    "oracle_legal_first_action_count": len(
                        legal_root_first_action_ids
                    ),
                    "reported": reported_root_action_coverage,
                    "contract_errors": root_action_coverage_errors,
                },
            }
        )

    metrics = {
        "fixture_count": len(suite["fixtures"]),
        "exact_fixture_count": exact_count,
        "approximate_fixture_count": approximate_count,
        "abstain_fixture_count": abstain_count,
        "top1_count": top1_count,
        "top1_rate": _rate(top1_count, exact_count, empty=0.0),
        "top3_count": top3_count,
        "top3_rate": _rate(top3_count, exact_count, empty=0.0),
        "friendly_assessed_action_count": friendly_assessed_actions,
        "friendly_legal_action_count": friendly_legal_actions,
        "friendly_action_legality_rate": _rate(
            friendly_legal_actions, friendly_assessed_actions
        ),
        "response_assessed_action_count": response_assessed_actions,
        "response_legal_action_count": response_legal_actions,
        "response_action_legality_rate": _rate(
            response_legal_actions, response_assessed_actions
        ),
        "assessed_candidate_line_count": assessed_candidate_lines,
        "friendly_complete_line_count": friendly_complete_lines,
        "assessed_response_line_count": assessed_response_lines,
        "response_complete_line_count": response_complete_lines,
        "mean_minimax_regret": round(statistics.mean(regrets), 6) if regrets else 0.0,
        "max_minimax_regret": max(regrets, default=0),
        "safe_claim_count": safe_claim_count,
        "false_safe_count": false_safe_count,
        "false_safe_rate": _rate(false_safe_count, safe_claim_count, empty=0.0),
        "proof_contract_failure_count": proof_contract_failures,
        "response_contract_failure_count": response_contract_failures,
        "fixture_contract_failure_count": fixture_contract_failures,
        "abstain_violation_count": abstain_violations,
        "multi_optimal_fixture_count": multi_optimal_fixture_count,
        "multi_optimal_required_first_action_count": (
            multi_optimal_required_first_action_count
        ),
        "multi_optimal_recalled_first_action_count": (
            multi_optimal_recalled_first_action_count
        ),
        "multi_optimal_first_action_recall_at_k": _rate(
            multi_optimal_recalled_first_action_count,
            multi_optimal_required_first_action_count,
            empty=0.0,
        ),
        "recommended_first_action_count": recommended_first_action_count,
        "distinct_recommended_first_action_count": (
            distinct_recommended_first_action_count
        ),
        "distinct_recommended_first_action_rate": _rate(
            distinct_recommended_first_action_count,
            recommended_first_action_count,
            empty=0.0,
        ),
        "duplicate_first_action_count": duplicate_first_action_count,
        "root_action_coverage_contract_failure_count": (
            root_action_coverage_contract_failures
        ),
        "portfolio_regret_contract_failure_count": (
            portfolio_regret_contract_failures
        ),
        "viable_portfolio_contract_failure_count": (
            viable_portfolio_contract_failures
        ),
        "mean_portfolio_first_action_minimax_regret": (
            round(statistics.mean(portfolio_first_action_regrets), 6)
            if portfolio_first_action_regrets
            else 0.0
        ),
        "max_portfolio_first_action_minimax_regret": max(
            portfolio_first_action_regrets,
            default=0,
        ),
        "mean_returned_alternative_regret": (
            round(statistics.mean(returned_alternative_regrets), 6)
            if returned_alternative_regrets
            else 0.0
        ),
        "max_returned_alternative_regret": max(
            returned_alternative_regrets,
            default=0,
        ),
        "latency_mean_ms": round(statistics.mean(latencies), 3) if latencies else 0.0,
        "latency_p95_ms": _percentile(latencies, 0.95),
    }
    checks = _threshold_checks(metrics, suite["thresholds"])
    gate_passed = all(item["passed"] for item in checks)
    return {
        "schema_version": TURNPAIR_SCHEMA_VERSION,
        "kind": "advisor_turnpair_eval_report_v1",
        "suite_id": TURNPAIR_SUITE_ID,
        "suite_hash": _canonical_hash(suite),
        "fixture_file": str(Path(fixture_path)),
        "fixture_hashes": fixture_hashes,
        "seed": suite_seed,
        "metrics": metrics,
        "metric_definitions": {
            "top1_rate": "Share of exact fixtures whose first recommended first action is minimax-optimal under the independent turn-pair oracle.",
            "top3_rate": "Share of exact fixtures with a minimax-optimal first action among the first three recommendations.",
            "minimax_regret": "Independent oracle optimal tactical utility minus the first recommendation's worst-response utility.",
            "portfolio_first_action_minimax_regret": "Independent oracle optimal tactical utility minus the best worst-response utility reachable from each returned recommendation's first action.",
            "returned_alternative_regret": "Independent oracle optimal tactical utility minus the returned complete line's independently assessed worst-response utility.",
            "portfolio_alternative_kind": "With proven portfolio optimality: zero returned-line regret is co_optimal, regret up to 100 is near_optimal, and larger regret is backup. Without that proof: zero regret is best_found and every nonzero regret is backup.",
            "viable_portfolio_contract": "When the independent oracle has any safe line, every returned alternative must also be safe after its worst legal response.",
            "multi_optimal_first_action_recall_at_k": "Share of fixture-required co-optimal first actions independently observed among the returned top-K recommendations.",
            "distinct_recommended_first_action_rate": "Distinct independently parsed first actions divided by returned recommendations across exact fixtures.",
            "root_action_coverage_contract": "Canonical legal, generated, response-verified, and missing first-action ID arrays and counts are checked against the oracle's complete legal root-action set; recommendation count is not used as generated coverage.",
            "false_safe_rate": "Share of explicit safe-after-response claims for which at least one legal opponent response is lethal.",
            "action_legality": "Action-by-action legality checked independently for both friendly lines and reported opponent responses.",
        },
        "gate": {"passed": gate_passed, "checks": checks},
        "fixtures": details,
        "passed": gate_passed,
        "caveat": (
            "This gate proves ranking only inside the deterministic public turnpair-v1 combat subset. "
            "It does not model hidden hands, unknown draws, complete card scripts, or global optimal play."
        ),
    }
