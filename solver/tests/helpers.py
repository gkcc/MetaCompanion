from __future__ import annotations

import _path  # noqa: F401

from metacompanion_solver.schemas import Card, CardType, GameState, PlayerState


def hero(entity_id: str, health: int = 30, attack: int = 0, can_attack: bool = False) -> Card:
    return Card(
        entity_id=entity_id,
        card_id=f"HERO_{entity_id}",
        name=f"Hero {entity_id}",
        card_type=CardType.HERO,
        attack=attack,
        health=health,
        current_health=health,
        can_attack=can_attack,
        attacks_remaining=1 if can_attack else 0,
    )


def player(player_id: str, *, mana: int = 10, health: int = 30) -> PlayerState:
    return PlayerState(
        player_id=player_id,
        hero=hero(f"{player_id}-hero", health),
        mana=mana,
        max_mana=mana,
        deck_size=20,
    )


def state() -> GameState:
    friendly = player("friendly")
    opponent = player("opponent", mana=0)
    return GameState(
        state_id="state-1",
        turn=5,
        active_player_id=friendly.player_id,
        perspective_player_id=friendly.player_id,
        friendly=friendly,
        opponent=opponent,
        rng_seed=7,
    )


def native_request_dict() -> dict:
    return {
        "api_version": "1.0",
        "request_id": "request-1",
        "state": state().to_dict(),
        "options": {"time_budget_ms": 30, "max_iterations": 20, "top_k": 3},
    }


def advisor_entity(
    entity_id: int,
    card_id: str,
    card_type: str,
    *,
    name: str = "Card",
    cost: int = 0,
    attack: int = 0,
    health: int = 0,
    damage: int = 0,
    text: str = "",
    playable: bool = True,
) -> dict:
    return {
        "entity_id": entity_id,
        "card_id": card_id,
        "name": name,
        "card_type": card_type,
        "cost": cost,
        "attack": attack,
        "health": health,
        "damage": damage,
        "card_text": text,
        "is_playable_card": playable,
        "is_exhausted": True,
        "is_frozen": False,
        "has_taunt": False,
        "has_divine_shield": False,
        "has_stealth": False,
        "has_poisonous": False,
        "has_lifesteal": False,
        "mechanics": [],
        "tags": {},
    }


def advisor_snapshot() -> dict:
    return {
        "schema_version": 1,
        "state_id": "hdt-state-1",
        "snapshot_sequence": 4,
        "turn_number": 6,
        "active_player": "player",
        "is_local_player_turn": True,
        "environment_version": "arena-2026-07",
        "game_mode": "arena",
        "hearthstone_build": 12345,
        "hdt_version": "1.54.0",
        "player": {
            "player_id": 1,
            "max_mana": 6,
            "deck_count": 20,
            "fatigue": 0,
            "resources": {"available": 6, "total": 6},
            "player_entity": {"entity_id": 1, "tags": {}},
            "hero": advisor_entity(10, "HERO_01", "HERO", health=30, damage=3),
            "hero_power": advisor_entity(
                11, "CS2_034", "HERO_POWER", name="Fireblast", cost=2, text="Deal 1 damage."
            ),
            "weapon": None,
            "hand": [
                advisor_entity(
                    20, "TEST_SPELL", "SPELL", name="Unknown spell", cost=2, text="Do a thing."
                )
            ],
            "board": [],
        },
        "opponent": {
            "player_id": 2,
            "max_mana": 6,
            "deck_count": 18,
            "fatigue": 0,
            "resources": {"available": 0, "total": 6},
            "player_entity": {"entity_id": 2, "tags": {}},
            "hero": advisor_entity(30, "HERO_02", "HERO", health=30),
            "hero_power": None,
            "weapon": None,
            "hand": [],
            "board": [],
        },
        "unknown_data": [{"code": "hidden_hand"}],
        "unsupported_features": ["choices"],
        "metadata": {"source": "unit-test"},
    }
