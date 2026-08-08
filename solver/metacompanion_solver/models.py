from __future__ import annotations

import json
import math
import os
from pathlib import Path
from typing import Any, Mapping, Protocol, Sequence

from .schemas import Action, ActionKind, Card, CardType, GameState


class ActionPrior(Protocol):
    def probabilities(self, state: GameState, actions: Sequence[Action]) -> Mapping[str, float]:
        """Return non-negative weights keyed by Action.action_id."""


class BeliefModel(Protocol):
    def risk_adjustment(self, state: GameState, perspective_player_id: str) -> float:
        """Return a probability adjustment; positive values represent hidden-card risk."""


def _find_card(state: GameState, entity_id: str) -> Card | None:
    for player in (state.friendly, state.opponent):
        for card in [player.hero, *player.hand, *player.board]:
            if card.entity_id == entity_id:
                return card
        if player.hero_power and player.hero_power.entity_id == entity_id:
            return player.hero_power
    return None


class HeuristicActionPrior:
    """Small, replaceable prior used until a learned policy is available."""

    def __init__(
        self,
        weights: Mapping[str, float] | None = None,
        card_weights: Mapping[str, float] | None = None,
        card_weights_by_mode: Mapping[str, Mapping[str, float]] | None = None,
    ):
        defaults = {
            "play_card": 1.4,
            "attack": 1.2,
            "hero_power": 0.9,
            "end_turn": 0.15,
            "face_attack_bonus": 0.5,
            "mana_efficiency": 0.08,
        }
        if weights:
            defaults.update({key: float(value) for key, value in weights.items()})
        self.weights = defaults
        self.card_weights = {key: max(0.05, float(value)) for key, value in (card_weights or {}).items()}
        self.card_weights_by_mode = {
            str(mode).strip().lower(): {
                card_id: max(0.05, float(value)) for card_id, value in values.items()
            }
            for mode, values in (card_weights_by_mode or {}).items()
            if isinstance(values, Mapping)
        }

    @staticmethod
    def _mode_key(state: GameState) -> str:
        value = str(state.mode or "").strip().lower()
        if "arena" in value:
            return "arena"
        if "standard" in value or "ranked" in value:
            return "standard"
        return value

    @classmethod
    def from_json(cls, path: str | Path) -> "HeuristicActionPrior":
        with Path(path).open("r", encoding="utf-8") as handle:
            raw = json.load(handle)
        if not isinstance(raw, dict):
            raise ValueError("prior JSON must be an object of numeric weights")
        return cls(raw)

    def probabilities(self, state: GameState, actions: Sequence[Action]) -> Mapping[str, float]:
        actor = state.player(state.active_player_id)
        enemy = state.other_player(actor.player_id)
        contextual_card_weights = self.card_weights_by_mode.get(self._mode_key(state), {})
        raw: dict[str, float] = {}
        for action in actions:
            weight = max(0.001, self.weights.get(action.kind.value, 1.0))
            source = _find_card(state, action.source_entity_id)
            if action.kind == ActionKind.PLAY_CARD and source:
                weight += self.weights["mana_efficiency"] * source.cost
                weight *= max(0.05, source.prior_weight)
                weight *= self.card_weights.get(source.card_id, 1.0)
                weight *= contextual_card_weights.get(source.card_id, 1.0)
            elif action.kind == ActionKind.ATTACK and source:
                if action.target_entity_id == enemy.hero.entity_id:
                    weight += self.weights["face_attack_bonus"]
                    if source.attack >= enemy.hero.current_health + enemy.armor:
                        weight += 10.0
                else:
                    target = _find_card(state, action.target_entity_id)
                    if target and source.attack >= target.current_health:
                        weight += 0.25
            raw[action.action_id] = max(0.001, weight)
        total = sum(raw.values()) or 1.0
        return {key: value / total for key, value in raw.items()}


def _candidate_records(raw: Any) -> list[Mapping[str, Any]]:
    if isinstance(raw, list):
        return [item for item in raw if isinstance(item, Mapping)]
    if not isinstance(raw, Mapping):
        return []
    for key in ("cards", "card_priors", "card_stats", "rows", "items"):
        value = raw.get(key)
        if isinstance(value, list):
            return [item for item in value if isinstance(item, Mapping)]
        if isinstance(value, Mapping):
            records: list[Mapping[str, Any]] = []
            for card_id, item in value.items():
                if isinstance(item, Mapping):
                    records.append({"card_id": card_id, **item})
                elif isinstance(item, (int, float)) and not isinstance(item, bool):
                    records.append({"card_id": card_id, "prior_weight": item})
            return records
    return []


def _record_weight(record: Mapping[str, Any]) -> float | None:
    for key in ("prior_weight", "weight"):
        value = record.get(key)
        if isinstance(value, (int, float)) and not isinstance(value, bool) and value >= 0:
            return max(0.05, float(value))
    for key in ("score", "arenasmith_score"):
        value = record.get(key)
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            return max(0.05, min(3.0, float(value) / 50.0))
    for key in ("pick_rate", "win_rate", "deck_win_rate"):
        value = record.get(key)
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            normalized = float(value) / 100.0 if value > 1 else float(value)
            return max(0.05, min(3.0, 0.5 + normalized))
    return None


def load_normalized_card_priors(directory: str | Path) -> dict[str, float]:
    """Load optional normalized priors from AdvisorData/latest JSON snapshots.

    Unknown files/shapes are ignored. This is a prior hook, not a rules source.
    """

    root = Path(directory)
    if not root.is_dir():
        return {}
    candidate_directories = [root]
    for relative in (Path("latest"), Path("Arena") / "latest"):
        candidate = root / relative
        if candidate.is_dir():
            candidate_directories.append(candidate)
    unique_directories: list[Path] = []
    seen: set[str] = set()
    for candidate in candidate_directories:
        key = str(candidate.resolve()).lower()
        if key not in seen:
            seen.add(key)
            unique_directories.append(candidate)
    values: dict[str, list[float]] = {}
    paths = sorted(
        {path for candidate in unique_directories for path in candidate.glob("*.json")},
        key=lambda item: str(item).lower(),
    )
    for path in paths:
        try:
            if path.stat().st_size > 20 * 1024 * 1024:
                continue
            with path.open("r", encoding="utf-8") as handle:
                raw = json.load(handle)
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            continue
        for record in _candidate_records(raw):
            card_id = record.get("card_id") or record.get("CardId") or record.get("id")
            weight = _record_weight(record)
            if isinstance(card_id, str) and card_id and weight is not None:
                values.setdefault(card_id, []).append(weight)
    return {card_id: sum(items) / len(items) for card_id, items in values.items()}


def load_mode_card_priors(directory: str | Path) -> dict[str, dict[str, float]]:
    """Load mode-scoped action priors without mixing Arena into Standard.

    Only the explicit ``<Mode>/latest/card_priors.json`` contract is accepted here.
    Official card-pool files intentionally do not contain action-strength weights and
    are never treated as priors. The broader legacy loader remains available for
    offline compatibility, but the live worker should use this mode-scoped function.
    """

    root = Path(directory)
    result: dict[str, dict[str, float]] = {}
    for mode, directory_name in (("arena", "Arena"), ("standard", "Standard")):
        path = root / directory_name / "latest" / "card_priors.json"
        if not path.is_file():
            continue
        try:
            if path.stat().st_size > 20 * 1024 * 1024:
                continue
            with path.open("r", encoding="utf-8") as handle:
                raw = json.load(handle)
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            continue
        values: dict[str, list[float]] = {}
        for record in _candidate_records(raw):
            card_id = record.get("card_id") or record.get("CardId") or record.get("id")
            weight = _record_weight(record)
            if isinstance(card_id, str) and card_id and weight is not None:
                values.setdefault(card_id, []).append(weight)
        if values:
            result[mode] = {
                card_id: sum(items) / len(items) for card_id, items in values.items()
            }
    return result


def default_advisor_data_directory() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return Path(appdata) / "HearthstoneDeckTracker" / "MetaCompanion" / "AdvisorData"


class WeightedCandidateBeliefModel:
    """Hook for externally supplied hidden-card priors.

    Candidate ``impact`` is an intentionally generic 0..100-ish risk score supplied by
    the caller/data pipeline. It is not inferred from card text by this MVP.
    """

    def risk_adjustment(self, state: GameState, perspective_player_id: str) -> float:
        if not state.belief.candidates or state.belief.confidence <= 0:
            return 0.0
        weighted = sum(
            candidate.probability * max(0.0, candidate.impact)
            for candidate in state.belief.candidates
        )
        return min(0.15, state.belief.confidence * weighted / 100.0)


class NullBeliefModel:
    def risk_adjustment(self, state: GameState, perspective_player_id: str) -> float:
        return 0.0


class StateEvaluator:
    def __init__(self, belief_model: BeliefModel | None = None):
        self.belief_model = belief_model or WeightedCandidateBeliefModel()

    def evaluate_components(
        self, state: GameState, perspective_player_id: str
    ) -> dict[str, float]:
        """Return the complete, auditable breakdown used by ``evaluate``.

        The result is deliberately described as a tactical state value.  It is not a
        calibrated match win probability; exposing every term prevents the turn-pair
        planner (and its UI) from presenting a single opaque number as learned truth.
        """

        player = state.player(perspective_player_id)
        enemy = state.other_player(perspective_player_id)
        player_dead = player.hero.current_health <= 0
        enemy_dead = enemy.hero.current_health <= 0
        if player_dead and enemy_dead:
            return {
                "terminal_value": 0.5,
                "minimax_value": 0.0,
                "tactical_state_value": 0.5,
            }
        if enemy_dead:
            return {
                "terminal_value": 1.0,
                "minimax_value": 1_000_000.0,
                "tactical_state_value": 1.0,
            }
        if player_dead:
            return {
                "terminal_value": 0.0,
                "minimax_value": -1_000_000.0,
                "tactical_state_value": 0.0,
            }

        def survival_value(health: int, armor: int) -> float:
            effective_health = max(0, health) + max(0, armor)
            return float(
                sum(
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
            )

        def board_card_value(card: Card) -> float:
            if card.current_health <= 0:
                return 0.0
            if card.card_type == CardType.LOCATION:
                return float(card.current_health * 24 + len(card.effects) * 8)
            if card.card_type != CardType.MINION:
                return 0.0
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
            value = float(
                card.attack * 24
                + card.current_health * health_weight
                + card.attack**2 * 2
            )
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
                value += 30 + int(value) // 3
            if card.stealth:
                value += 8 + card.attack * 4
            if card.immune:
                value += 30
            if card.dormant:
                value = float(int(value) * 2 // 3)
            return value

        def board_value(cards: Sequence[Card]) -> float:
            return sum(board_card_value(card) for card in cards)

        def weapon_value(card: Card | None) -> float:
            if card is None:
                return 0.0
            value = float(card.attack * 10 + card.current_durability * 2)
            if card.poisonous:
                value += 24 + card.current_durability * 4
            if card.lifesteal:
                value += card.attack * 4
            if card.windfury:
                value += card.attack * 8
            if card.mega_windfury:
                value += card.attack * 18
            return value

        def hand_value(cards: Sequence[Card]) -> float:
            value = 0.0
            for card in cards:
                base = float(
                    math.floor(
                        min(10.0, max(0.0, card.prior_weight)) * 25.0 + 0.5
                    )
                )
                engine_reserve = float(
                    sum(
                        80
                        for effect in card.effects
                        if effect.kind == "double_one_cost_cards"
                    )
                )
                value += base + engine_reserve
            return value

        player_effective_health = player.hero.current_health + player.armor
        enemy_effective_health = enemy.hero.current_health + enemy.armor
        player_board_value = board_value(player.board)
        enemy_board_value = board_value(enemy.board)
        player_weapon_value = weapon_value(player.weapon)
        enemy_weapon_value = weapon_value(enemy.weapon)
        health_delta = float(player_effective_health - enemy_effective_health)
        player_survival_value = survival_value(player.hero.current_health, player.armor)
        enemy_survival_value = survival_value(enemy.hero.current_health, enemy.armor)
        board_delta = player_board_value - enemy_board_value
        weapon_delta = player_weapon_value - enemy_weapon_value
        hand_delta = float(len(player.hand) - len(enemy.hand))
        player_hand_value = hand_value(player.hand)
        enemy_hand_value = hand_value(enemy.hand)
        mana_delta = float(player.mana - enemy.mana)
        health_term = player_survival_value - enemy_survival_value
        hand_term = player_hand_value - enemy_hand_value
        mana_term = mana_delta * 2.0
        raw_score = health_term + board_delta + weapon_delta + hand_term + mana_term
        # This monotone projection exists only for the legacy 0..1 UI slot. Ranking
        # and the independent turn-pair oracle use ``minimax_value`` directly.
        unadjusted = 1.0 / (1.0 + math.exp(-raw_score / 1200.0))
        belief_risk = self.belief_model.risk_adjustment(state, perspective_player_id)
        value = min(1.0, max(0.0, unadjusted - belief_risk))
        return {
            "terminal_value": -1.0,
            "friendly_effective_health": float(player_effective_health),
            "opponent_effective_health": float(enemy_effective_health),
            "effective_health_delta": health_delta,
            "friendly_survival_value": player_survival_value,
            "opponent_survival_value": enemy_survival_value,
            "health_term": health_term,
            "friendly_board_value": player_board_value,
            "opponent_board_value": enemy_board_value,
            "board_value_delta": board_delta,
            "friendly_weapon_value": player_weapon_value,
            "opponent_weapon_value": enemy_weapon_value,
            "weapon_value_delta": weapon_delta,
            "hand_count_delta": hand_delta,
            "friendly_hand_value": player_hand_value,
            "opponent_hand_value": enemy_hand_value,
            "hand_term": hand_term,
            "mana_delta": mana_delta,
            "mana_term": mana_term,
            "raw_score": raw_score,
            "minimax_value": raw_score,
            "unadjusted_state_value": unadjusted,
            "belief_risk_adjustment": belief_risk,
            "tactical_state_value": value,
        }

    def evaluate(self, state: GameState, perspective_player_id: str) -> float:
        return self.evaluate_components(state, perspective_player_id)[
            "tactical_state_value"
        ]
