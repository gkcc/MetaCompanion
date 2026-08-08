from __future__ import annotations

import hashlib
import json
import math
import statistics
import time
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from .schemas import Action, SolveRequest
from .turnpair_evaluation import (
    MISSING_LINE_REGRET,
    RESPONSE_KIND,
    RESPONSE_SCOPE,
    TurnPairEvaluationError,
    assess_oracle_actions,
    assess_turnpair_line,
    prove_turnpair,
)


HDT_RULE_SUITE_ID = "oracle-hdt-cardrules-v1"
HDT_RULE_SCHEMA_VERSION = 1


class HdtRuleEvaluationError(ValueError):
    pass


def _mapping(value: Any, path: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise HdtRuleEvaluationError(f"{path} must be an object")
    return value


def _array(value: Any, path: str) -> list[Any]:
    if not isinstance(value, list):
        raise HdtRuleEvaluationError(f"{path} must be an array")
    return value


def _canonical_hash(value: Any) -> str:
    payload = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _rate(numerator: int, denominator: int, *, empty: float = 1.0) -> float:
    return round(numerator / denominator, 6) if denominator else empty


def _percentile(values: Sequence[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(float(item) for item in values)
    index = max(0, min(len(ordered) - 1, math.ceil((len(ordered) - 1) * fraction)))
    return round(ordered[index], 3)


def _wire_entity_id(value: Any, fallback: str) -> int | str:
    candidate = value if value not in (None, "", 0, "0") else fallback
    if isinstance(candidate, int) and not isinstance(candidate, bool):
        return candidate
    if isinstance(candidate, str):
        try:
            return int(candidate)
        except ValueError:
            return candidate
    return fallback


def _zone_id(value: Any) -> int:
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    normalized = str(value or "").strip().upper().replace("_", "")
    return {
        "PLAY": 1,
        "DECK": 2,
        "HAND": 3,
        "GRAVEYARD": 4,
        "REMOVEDFROMGAME": 5,
        "SETASIDE": 6,
        "SECRET": 7,
    }.get(normalized, 0)


def _fixture_lifesteal_evidence(raw: Mapping[str, Any]) -> bool:
    if raw.get("lifesteal") is True:
        return True
    tags = raw.get("tags")
    if not isinstance(tags, Mapping):
        return False
    for key, value in tags.items():
        if str(key).strip().upper() not in {"LIFESTEAL", "685"}:
            continue
        if isinstance(value, bool):
            return value
        if isinstance(value, int):
            return value != 0
        if isinstance(value, str):
            try:
                return int(value.strip()) != 0
            except ValueError:
                return False
    return False


def load_hdt_rule_suite(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    try:
        root = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise HdtRuleEvaluationError(f"could not load HDT rule fixture suite: {source}") from exc
    if not isinstance(root, dict):
        raise HdtRuleEvaluationError("suite root must be an object")
    if root.get("schema_version") != HDT_RULE_SCHEMA_VERSION:
        raise HdtRuleEvaluationError("unsupported HDT rule fixture schema_version")
    if root.get("suite_id") != HDT_RULE_SUITE_ID:
        raise HdtRuleEvaluationError(f"suite_id must be {HDT_RULE_SUITE_ID!r}")
    fixtures = _array(root.get("fixtures"), "suite.fixtures")
    if not fixtures:
        raise HdtRuleEvaluationError("suite.fixtures must not be empty")
    seen: set[str] = set()
    for index, item in enumerate(fixtures):
        fixture = _mapping(item, f"suite.fixtures[{index}]")
        fixture_id = fixture.get("id")
        if not isinstance(fixture_id, str) or not fixture_id or fixture_id in seen:
            raise HdtRuleEvaluationError("fixture IDs must be non-empty and unique")
        seen.add(fixture_id)
        if fixture.get("scope") not in {"exact", "abstain", "scoped_lethal"}:
            raise HdtRuleEvaluationError(f"fixture {fixture_id} has an invalid scope")
        _mapping(fixture.get("position"), f"fixture {fixture_id}.position")
        _mapping(fixture.get("expected", {}), f"fixture {fixture_id}.expected")
    if not isinstance(root.get("thresholds", {}), Mapping):
        raise HdtRuleEvaluationError("suite.thresholds must be an object")
    return root


def _card_payload(
    value: Any,
    *,
    fallback_id: str,
    default_type: str = "MINION",
    default_health: int = 1,
    oracle: bool,
) -> dict[str, Any] | None:
    raw = dict(_mapping(value, fallback_id))
    if oracle and raw.get("oracle_omit") is True:
        return None
    card_type = str(raw.get("card_type") or default_type).upper()
    health = raw.get("health", default_health)
    current_health = raw.get("current_health", health)
    damage = max(0, int(health) - int(current_health))
    durability = raw.get("durability", 0)
    if oracle and durability == 0 and card_type in {"WEAPON", "LOCATION"}:
        # Live HDT exposes weapon durability and Location charges through
        # HEALTH/DAMAGE when no separate DURABILITY tag is present.  Keep the
        # independent oracle on the same public semantics without copying any
        # production rule inference.
        durability = health
    current_durability = raw.get(
        "current_durability",
        max(0, int(durability) - damage),
    )
    can_attack = raw.get("can_attack", False)
    summoned_this_turn = raw.get("summoned_this_turn", False)
    if not isinstance(summoned_this_turn, bool):
        raise HdtRuleEvaluationError(
            f"{fallback_id}.summoned_this_turn must be boolean"
        )
    base = {
        "entity_id": str(raw.get("entity_id") or fallback_id),
        "card_id": str(raw.get("card_id") or f"EVAL_{fallback_id.upper()}"),
        "name": str(raw.get("name") or fallback_id),
        "card_type": card_type,
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
        "poisonous": False,
        "lifesteal": _fixture_lifesteal_evidence(raw),
        "windfury": False,
        "mega_windfury": False,
        "rush": False,
        "charge": False,
        "reborn": False,
        "dormant": False,
        "immune": False,
        "summoned_this_turn": summoned_this_turn,
        "durability": durability if oracle else 0,
        "current_durability": current_durability if oracle else 0,
        "spell_power": 0,
    }
    if oracle:
        base.update(
            {
                "effects": raw.get("oracle_effects", []),
                "effect_coverage": raw.get("oracle_effect_coverage", "exact"),
                "unsupported_effects": raw.get("oracle_unsupported_effects", []),
                "card_text": raw.get("text", ""),
                "tags": raw.get("tags", {}),
            }
        )
        return base
    mechanics = raw.get("mechanics", [])
    tags = {
        "NUM_TURNS_IN_PLAY": 0 if summoned_this_turn else 1,
        "NUM_ATTACKS_THIS_TURN": 0,
    }
    raw_tags = raw.get("tags", {})
    if isinstance(raw_tags, Mapping):
        tags.update({str(key): item for key, item in raw_tags.items()})
    zone = raw.get("zone", "PLAY" if default_type != "SPELL" else "HAND")
    payload = {
        "entity_id": _wire_entity_id(raw.get("entity_id"), fallback_id),
        "card_id": base["card_id"],
        "dbf_id": raw.get("dbf_id", 0),
        "name": base["name"],
        "zone": zone,
        "zone_id": raw.get("zone_id", _zone_id(zone)),
        "zone_position": raw.get("zone_position", 0),
        "controller_id": raw.get("controller_id", 0),
        "card_type": base["card_type"],
        "card_type_id": raw.get("card_type_id", 0),
        "cost": base["cost"],
        "attack": base["attack"],
        "health": health,
        "damage": damage,
        "armor": raw.get("armor", 0),
        "durability": raw.get("durability", 0),
        "is_known": raw.get("is_known", True),
        "is_created": raw.get("is_created", False),
        "is_revealed": raw.get("is_revealed", True),
        "card_text": raw.get("text", ""),
        "english_text": raw.get("text", ""),
        "is_playable_card": base["playable"],
        "is_exhausted": not bool(can_attack),
        "is_frozen": False,
        "is_silenced": raw.get("is_silenced", False),
        "has_taunt": base["taunt"],
        "has_divine_shield": base["divine_shield"],
        "has_stealth": base["stealth"],
        "has_poisonous": False,
        "has_windfury": False,
        "has_rush": False,
        "has_charge": False,
        "has_reborn": False,
        "is_dormant": False,
        "is_immune": False,
        "creator_entity_id": raw.get("creator_entity_id", 0),
        "original_card_id": raw.get("original_card_id", ""),
        "race": raw.get("race", ""),
        "card_class": raw.get("card_class", ""),
        "rarity": raw.get("rarity", ""),
        "mechanics": mechanics,
        "tags": tags,
        "visibility": raw.get("visibility", "public"),
    }
    if "lifesteal" in raw:
        payload["has_lifesteal"] = raw["lifesteal"] is True
    return payload


def _oracle_player(value: Any, player_id: str) -> dict[str, Any]:
    raw = dict(_mapping(value, player_id))
    hero_entity_id = "10" if player_id == "friendly" else "30"
    hero_raw = {
        "entity_id": hero_entity_id,
        "card_id": f"HERO_{player_id.upper()}",
        "name": f"Hero {player_id}",
        "card_type": "HERO",
        "health": 30,
        "current_health": 30,
        **dict(_mapping(raw.get("hero", {}), f"{player_id}.hero")),
    }
    hero = _card_payload(
        hero_raw,
        fallback_id=hero_entity_id,
        default_type="HERO",
        default_health=30,
        oracle=True,
    )
    hand = [
        card
        for index, item in enumerate(_array(raw.get("hand", []), f"{player_id}.hand"))
        if (
            card := _card_payload(
                item,
                fallback_id=f"{player_id}-hand-{index}",
                default_type="SPELL",
                oracle=True,
            )
        )
        is not None
    ]
    board = [
        card
        for index, item in enumerate(_array(raw.get("board", []), f"{player_id}.board"))
        if (
            card := _card_payload(
                item,
                fallback_id=f"{player_id}-board-{index}",
                oracle=True,
            )
        )
        is not None
    ]
    power = None
    if raw.get("hero_power") is not None:
        power = _card_payload(
            raw["hero_power"],
            fallback_id=f"{player_id}-hero-power",
            default_type="HERO_POWER",
            oracle=True,
        )
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
        "hero_power": power,
        "hero_power_available": bool(power and raw.get("hero_power_available", True)),
        "weapon": None,
        "spell_power": raw.get("spell_power", 0),
    }


def _scrub_hidden_opponent_entity(entity: dict[str, Any]) -> dict[str, Any]:
    """Mirror AdvisorGameStateExtractor.ScrubHiddenOpponentEntity."""

    safe_tags = {
        key: value
        for key in ("ZONE", "ZONE_POSITION", "CONTROLLER")
        if (value := entity.get("tags", {}).get(key)) is not None
    }
    for key in (
        "dbf_id",
        "card_type_id",
        "cost",
        "attack",
        "health",
        "damage",
        "armor",
        "durability",
        "creator_entity_id",
    ):
        entity[key] = 0
    for key in (
        "is_known",
        "is_created",
        "is_revealed",
        "is_playable_card",
        "is_exhausted",
        "is_frozen",
        "is_silenced",
        "has_taunt",
        "has_divine_shield",
        "has_stealth",
        "has_windfury",
        "has_rush",
        "has_charge",
        "has_lifesteal",
        "has_poisonous",
        "has_reborn",
        "is_dormant",
    ):
        entity[key] = False
    for key in (
        "card_id",
        "name",
        "original_card_id",
        "card_text",
        "english_text",
        "race",
        "card_class",
        "rarity",
    ):
        entity[key] = ""
    entity["card_type"] = "UNKNOWN"
    entity["visibility"] = "hidden"
    entity["mechanics"] = []
    entity["tags"] = safe_tags
    return entity


def _hdt_entities(
    value: Any,
    *,
    label: str,
    player_id: int,
    container: str,
    zone: str,
    default_type: str,
    always_hidden: bool = False,
) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for index, item in enumerate(_array(value, f"{label}.{container}")):
        raw = dict(_mapping(item, f"{label}.{container}[{index}]"))
        raw.setdefault("zone", zone)
        raw.setdefault("controller_id", player_id)
        card = _card_payload(
            raw,
            fallback_id=f"{label}-{container}-{index}",
            default_type=default_type,
            oracle=False,
        )
        assert card is not None
        explicitly_hidden = "hidden" in str(card.get("visibility", "")).lower()
        if always_hidden or explicitly_hidden:
            _scrub_hidden_opponent_entity(card)
        result.append(card)
    return result


def _hdt_player(value: Any, player_id: int, label: str) -> dict[str, Any]:
    raw = dict(_mapping(value, label))
    hero_raw = {
        "entity_id": 10 if label == "friendly" else 30,
        "card_id": f"HERO_{label.upper()}",
        "name": f"Hero {label}",
        "card_type": "HERO",
        "health": 30,
        "current_health": 30,
        "armor": raw.get("armor", 0),
        **dict(_mapping(raw.get("hero", {}), f"{label}.hero")),
    }
    hero = _card_payload(
        hero_raw,
        fallback_id=f"{label}-hero",
        default_type="HERO",
        default_health=30,
        oracle=False,
    )
    assert hero is not None
    hero["controller_id"] = player_id
    hand = _hdt_entities(
        raw.get("hand", []),
        label=label,
        player_id=player_id,
        container="hand",
        zone="HAND",
        default_type="SPELL",
        always_hidden=label == "opponent",
    )
    board = _hdt_entities(
        raw.get("board", []),
        label=label,
        player_id=player_id,
        container="board",
        zone="PLAY",
        default_type="MINION",
    )
    power = None
    if raw.get("hero_power") is not None:
        power = _card_payload(
            raw["hero_power"],
            fallback_id=f"{label}-hero-power",
            default_type="HERO_POWER",
            oracle=False,
        )
        assert power is not None
        power["controller_id"] = player_id
        power["is_exhausted"] = not raw.get("hero_power_available", True)
        # HDT reports Entity.IsPlayableCard=false for real hero-power entities.
        # Availability must therefore come from public activation/exhaustion tags.
        power["is_playable_card"] = False
        power["tags"].update(
            {
                "HAS_ACTIVATE_POWER": 1,
                "EXHAUSTED": 1 if power["is_exhausted"] else 0,
            }
        )
    mana = raw.get("mana", 0)
    private = label == "opponent"
    deck = _hdt_entities(
        raw.get("deck", []),
        label=label,
        player_id=player_id,
        container="deck",
        zone="DECK",
        default_type="UNKNOWN",
        always_hidden=private,
    )
    secrets = _hdt_entities(
        raw.get("secrets", []),
        label=label,
        player_id=player_id,
        container="secrets",
        zone="SECRET",
        default_type="UNKNOWN",
        always_hidden=private,
    )
    set_aside = _hdt_entities(
        raw.get("set_aside", []),
        label=label,
        player_id=player_id,
        container="set_aside",
        zone="SETASIDE",
        default_type="UNKNOWN",
        always_hidden=private,
    )
    return {
        "player_id": player_id,
        "entity_id": player_id,
        "is_local_player": label == "friendly",
        "class": raw.get("class", ""),
        "original_class": raw.get("original_class", ""),
        "hand_count": raw.get("hand_count", len(hand)),
        "max_mana": raw.get("max_mana", mana),
        "deck_count": raw.get("deck_size", 0),
        "fatigue": raw.get("fatigue", 0),
        "max_hand_size": raw.get("max_hand_size", 10),
        "corpses": raw.get("corpses"),
        "has_coin": raw.get("has_coin", False),
        "resources": {
            "available": mana,
            "total": raw.get("max_mana", mana),
            "spell_power": raw.get("spell_power", 0),
        },
        "player_entity": None
        if raw.get("omit_player_entity") is True
        else {
            "entity_id": player_id,
            "tags": dict(_mapping(raw.get("player_tags", {}), f"{label}.player_tags")),
        },
        "hero": hero,
        "hero_power": power,
        "weapon": None,
        "hand": hand,
        "board": board,
        "deck": deck,
        "graveyard": [],
        "secrets": secrets,
        "set_aside": set_aside,
        "removed_from_game": [],
        "other_entities": [],
        "known_cards_in_deck": [],
    }


def oracle_request_from_fixture(fixture: Mapping[str, Any], seed: int) -> SolveRequest:
    fixture_id = str(fixture.get("id") or "fixture")
    position = _mapping(fixture.get("position"), f"fixture {fixture_id}.position")
    friendly = _oracle_player(position.get("friendly", {}), "friendly")
    opponent = _oracle_player(position.get("opponent", {}), "opponent")
    return SolveRequest.from_dict(
        {
            "api_version": "1.0",
            "request_id": f"hdt-rule-oracle:{fixture_id}",
            "state": {
                "state_id": f"hdt-rule-oracle-state:{fixture_id}",
                "turn": position.get("turn", 1),
                "active_player_id": "friendly",
                "perspective_player_id": "friendly",
                "friendly": friendly,
                "opponent": opponent,
                "patch": HDT_RULE_SUITE_ID,
                "mode": "evaluation",
                "rng_seed": seed + int(fixture.get("seed_offset", 0)),
                "metadata": {"hdt_rule_fixture_id": fixture_id},
            },
            "options": {},
        }
    )


def candidate_wire_request_from_fixture(
    fixture: Mapping[str, Any], seed: int
) -> dict[str, Any]:
    """Build the unparsed request shape emitted by AdvisorWireProtocol."""

    fixture_id = str(fixture.get("id") or "fixture")
    position = _mapping(fixture.get("position"), f"fixture {fixture_id}.position")
    candidate_snapshot = {
        "schema_version": 1,
        "state_id": f"hdt-rule-candidate-state:{fixture_id}",
        "state_hash": "",
        "game_id": f"hdt-rule-game:{fixture_id}",
        "snapshot_sequence": 1,
        "captured_at_utc": "2026-07-29T00:00:00.0000000Z",
        "turn_number": position.get("turn", 1),
        "active_player": "player",
        "is_local_player_turn": True,
        "environment_version": HDT_RULE_SUITE_ID,
        "format": "STANDARD",
        "format_type": "FT_STANDARD",
        "game_mode": "RANKED_STANDARD",
        "game_type": "GT_RANKED",
        "hdt_mode": "",
        "is_running": True,
        "is_mulligan_done": True,
        "is_spectating": False,
        "hearthstone_build": 247416,
        "hdt_version": "1.54.0",
        "phase": {
            "step": "MAIN_ACTION",
            "next_step": "",
            "state": "RUNNING",
            "player_play_state": "PLAYING",
            "opponent_play_state": "PLAYING",
            "mulligan_state": "",
            "proposed_attacker_entity_id": 0,
            "proposed_defender_entity_id": 0,
            "has_pending_choice": False,
            "can_local_player_act": True,
        },
        "current_deck": {
            "is_known": False,
            "source": "",
            "deck_id": "",
            "hearthstone_deck_id": 0,
            "name": "",
            "hero_card_id": "",
            "hero_power_card_id": "",
            "format_type": 0,
            "deck_type": 0,
            "cards": [],
        },
        "arena": {
            "is_arena_match": False,
            "season_id": None,
            "wins": None,
            "losses": None,
            "rating": None,
            "package_inference_attempted": False,
            "package_anchor_card_id": "",
            "inferred_package_cards": [],
        },
        "player": _hdt_player(position.get("friendly", {}), 1, "friendly"),
        "opponent": _hdt_player(position.get("opponent", {}), 2, "opponent"),
        "game_entity": None,
        "other_public_entities": [],
        "unknown_data": [],
        "unsupported_features": [],
        "capture_warnings": [],
        "metadata": {"fixture": fixture_id},
    }
    return {
        "api_version": "1.0",
        "request_id": f"hdt-rule-candidate:{fixture_id}",
        "state": candidate_snapshot,
        "options": {
            "time_budget_ms": 400,
            "top_k": 3,
            "search_seed": seed + int(fixture.get("seed_offset", 0)),
            "allow_approximate_effects": True,
            "environment_version": HDT_RULE_SUITE_ID,
        },
        "metadata": {},
    }


def requests_from_fixture(
    fixture: Mapping[str, Any], seed: int
) -> tuple[SolveRequest, SolveRequest]:
    oracle_request = oracle_request_from_fixture(fixture, seed)
    candidate_request = SolveRequest.from_dict(
        candidate_wire_request_from_fixture(fixture, seed)
    )
    return oracle_request, candidate_request


def _candidate_payload(value: Any) -> dict[str, Any]:
    if hasattr(value, "to_dict") and callable(value.to_dict):
        value = value.to_dict()
    root = dict(_mapping(value, "candidate"))
    recommendations = _array(root.get("recommendations", []), "candidate.recommendations")
    root["recommendations"] = [dict(_mapping(item, "candidate.recommendation")) for item in recommendations]
    root["recommendations"].sort(
        key=lambda item: item.get("rank") if isinstance(item.get("rank"), int) else 1_000_000
    )
    return root


def _parse_action(value: Any) -> Action:
    try:
        raw = dict(_mapping(value, "action"))
        if "kind" not in raw and "type" in raw:
            raw["kind"] = raw["type"]
        for key in ("source_entity_id", "target_entity_id"):
            item = raw.get(key, "")
            if item is None:
                raw[key] = ""
            elif isinstance(item, int) and not isinstance(item, bool):
                raw[key] = str(item)
        return Action.from_dict(raw)
    except Exception as exc:
        raise HdtRuleEvaluationError(f"invalid candidate action: {exc}") from exc


def _parse_actions(value: Any) -> tuple[Action, ...]:
    return tuple(_parse_action(item) for item in _array(value, "actions"))


def _first_action_id(actions: Sequence[Action]) -> str:
    first = next((item for item in actions if item.kind.value != "end_turn"), None)
    return first.action_id if first is not None else "end_turn"


def _numeric(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(float(value))


def evaluate_hdt_rule_suite(
    fixture_path: str | Path,
    solve: Callable[[SolveRequest], Any],
    *,
    seed_override: int | None = None,
) -> dict[str, Any]:
    suite = load_hdt_rule_suite(fixture_path)
    seed = suite.get("seed", 0) if seed_override is None else seed_override
    if isinstance(seed, bool) or not isinstance(seed, int):
        raise HdtRuleEvaluationError("suite seed must be an integer")
    exact_count = 0
    abstain_count = 0
    scoped_lethal_count = 0
    top1 = 0
    top3 = 0
    friendly_assessed = 0
    friendly_legal = 0
    response_assessed = 0
    response_legal = 0
    regrets: list[int] = []
    false_safe = 0
    safe_claims = 0
    false_exact = 0
    provenance_failures = 0
    abstain_violations = 0
    fixture_contract_failures = 0
    latencies: list[float] = []
    details: list[dict[str, Any]] = []
    fixture_hashes: list[dict[str, str]] = []

    for fixture in suite["fixtures"]:
        fixture_id = fixture["id"]
        scope = fixture["scope"]
        expected = dict(_mapping(fixture.get("expected", {}), f"fixture {fixture_id}.expected"))
        fixture_hashes.append({"id": fixture_id, "sha256": _canonical_hash(fixture)})
        oracle_request, candidate_request = requests_from_fixture(fixture, seed)
        proof = None
        contract_errors: list[str] = []
        if scope in {"exact", "scoped_lethal"}:
            proof = prove_turnpair(oracle_request.state, allow_point_effects=True)
            if proof.abstained:
                contract_errors.append("independent point-effect oracle abstained")
            expected_actions = expected.get("optimal_first_action_ids")
            if expected_actions is not None and sorted(expected_actions) != list(proof.optimal_first_action_ids):
                contract_errors.append(
                    f"oracle optimal actions {list(proof.optimal_first_action_ids)} != expected {sorted(expected_actions)}"
                )
        if scope == "exact":
            exact_count += 1
        elif scope == "scoped_lethal":
            scoped_lethal_count += 1
        else:
            abstain_count += 1

        started = time.perf_counter()
        candidate = _candidate_payload(solve(candidate_request))
        latency_ms = (time.perf_counter() - started) * 1000.0
        latencies.append(latency_ms)
        recommendations = candidate["recommendations"][:3]
        coverage = candidate.get("coverage")
        coverage = coverage if isinstance(coverage, Mapping) else {}
        rules = coverage.get("structured_card_rules")
        rules = rules if isinstance(rules, Mapping) else {}
        matched = rules.get("matched") if isinstance(rules.get("matched"), list) else []
        mismatches = rules.get("mismatches") if isinstance(rules.get("mismatches"), list) else []
        matched_rule_ids = sorted(
            str(item.get("rule_id"))
            for item in matched
            if isinstance(item, Mapping) and item.get("rule_id")
        )
        expected_rule_ids = sorted(str(item) for item in expected.get("matched_rule_ids", []))
        if matched_rule_ids != expected_rule_ids:
            provenance_failures += 1
            contract_errors.append(
                f"matched rule IDs {matched_rule_ids} != expected {expected_rule_ids}"
            )
        expected_mismatch = expected.get("mismatch_reason")
        actual_mismatch_reasons = sorted(
            str(item.get("reason"))
            for item in mismatches
            if isinstance(item, Mapping) and item.get("reason")
        )
        if expected_mismatch and expected_mismatch not in actual_mismatch_reasons:
            provenance_failures += 1
            contract_errors.append(
                f"expected mismatch reason {expected_mismatch!r} was not reported"
            )
        if scope == "abstain":
            counterplay = coverage.get("details")
            counterplay = counterplay if isinstance(counterplay, Mapping) else {}
            counterplay = counterplay.get("counterplay")
            counterplay = counterplay if isinstance(counterplay, Mapping) else {}
            best_effort_valid = (
                candidate.get("status") == "partial"
                and bool(recommendations)
                and coverage.get("exact") is False
                and coverage.get("exact_scope") == ""
                and counterplay.get("portfolio_optimality_proven") is False
                and counterplay.get("root_action_coverage_complete") is False
                and counterplay.get("search_complete") is False
                and counterplay.get("response_verified_first_action_count") == 0
            )
            for recommendation in recommendations:
                annotations = recommendation.get("annotations")
                annotations = annotations if isinstance(annotations, list) else []
                annotation_codes = {
                    str(item.get("code"))
                    for item in annotations
                    if isinstance(item, Mapping)
                }
                best_effort_valid = best_effort_valid and (
                    recommendation.get("is_proven_lethal") is False
                    and recommendation.get("is_response_verified") is False
                    and recommendation.get("response_search_complete") is False
                    and recommendation.get("is_safe_after_response") is None
                    and recommendation.get("verified_portfolio_regret") is None
                    and recommendation.get("alternative_kind") == "fallback"
                    and recommendation.get("proof_kind") == ""
                    and recommendation.get("proof_scope") == ""
                    and "approximate_playable_unsupported_rule" in annotation_codes
                )
            if not best_effort_valid:
                abstain_violations += 1
                contract_errors.append(
                    "unsupported-rule fixture did not return an explicitly unverified partial route"
                )
            if matched_rule_ids:
                false_exact += 1
        elif rules.get("available") is not True or rules.get("ruleset_id") != "hdt-visible-point-effects-v1":
            provenance_failures += 1
            contract_errors.append("candidate did not report the expected structured ruleset")

        assessments = []
        if proof is not None and not proof.abstained:
            for recommendation in recommendations:
                try:
                    actions = _parse_actions(recommendation.get("actions", []))
                    assessment = assess_turnpair_line(oracle_request.state, actions)
                except (HdtRuleEvaluationError, TurnPairEvaluationError):
                    assessment = None
                    friendly_assessed += 1
                assessments.append(assessment)
                if assessment is None:
                    continue
                friendly_assessed += assessment.action_assessment.assessed_action_count
                friendly_legal += assessment.action_assessment.legal_action_count
                verified = recommendation.get("is_response_verified") is True
                safe = recommendation.get("is_safe_after_response")
                if verified and safe is True:
                    safe_claims += 1
                    if assessment.safe_after_response is not True:
                        false_safe += 1
                if verified:
                    if recommendation.get("response_scope") != RESPONSE_SCOPE:
                        provenance_failures += 1
                    if recommendation.get("response_kind") != RESPONSE_KIND:
                        provenance_failures += 1
                    reported_minimax = recommendation.get("minimax_value")
                    if not _numeric(reported_minimax) or int(float(reported_minimax)) != assessment.minimax_value:
                        provenance_failures += 1
                    response_root = recommendation.get("opponent_response")
                    response_root = response_root if isinstance(response_root, Mapping) else {}
                    try:
                        response_actions = _parse_actions(response_root.get("actions", []))
                    except HdtRuleEvaluationError:
                        response_actions = ()
                        response_assessed += 1
                    if assessment.response_start is None:
                        if response_actions:
                            response_assessed += len(response_actions)
                        else:
                            response_legal += 0
                    else:
                        response = assess_oracle_actions(
                            assessment.response_start, response_actions
                        )
                        response_assessed += response.assessed_action_count
                        response_legal += response.legal_action_count

            first = assessments[0] if assessments else None
            if first is not None and first.action_assessment.legal and first.action_assessment.complete:
                first_actions = _parse_actions(recommendations[0].get("actions", []))
                if _first_action_id(first_actions) in proof.optimal_first_action_ids:
                    top1 += 1
                regrets.append(max(0, proof.optimal_value - int(first.minimax_value)))
            else:
                regrets.append(MISSING_LINE_REGRET)
            top3_ids: set[str] = set()
            for recommendation, assessment in zip(recommendations, assessments):
                if assessment is None or not assessment.action_assessment.complete:
                    continue
                top3_ids.add(_first_action_id(_parse_actions(recommendation.get("actions", []))))
            if top3_ids.intersection(proof.optimal_first_action_ids):
                top3 += 1

        if contract_errors:
            fixture_contract_failures += 1
        details.append(
            {
                "id": fixture_id,
                "scope": scope,
                "status": candidate.get("status"),
                "recommendation_count": len(recommendations),
                "matched_rule_ids": matched_rule_ids,
                "mismatch_reasons": actual_mismatch_reasons,
                "oracle_optimal_first_action_ids": list(proof.optimal_first_action_ids) if proof else [],
                "latency_ms": round(latency_ms, 3),
                "contract_errors": contract_errors,
            }
        )

    ranked_count = exact_count + scoped_lethal_count
    metrics = {
        "exact_fixture_count": exact_count,
        "scoped_lethal_fixture_count": scoped_lethal_count,
        "abstain_fixture_count": abstain_count,
        "top1_rate": _rate(top1, ranked_count),
        "top3_rate": _rate(top3, ranked_count),
        "friendly_action_legality_rate": _rate(friendly_legal, friendly_assessed),
        "response_action_legality_rate": _rate(response_legal, response_assessed),
        "mean_minimax_regret": round(statistics.mean(regrets), 6) if regrets else 0.0,
        "max_minimax_regret": max(regrets, default=0),
        "safe_claim_count": safe_claims,
        "false_safe_count": false_safe,
        "false_safe_rate": _rate(false_safe, safe_claims, empty=0.0),
        "false_exact_count": false_exact,
        "rule_provenance_failure_count": provenance_failures,
        "abstain_violation_count": abstain_violations,
        "fixture_contract_failure_count": fixture_contract_failures,
        "latency_p50_ms": _percentile(latencies, 0.5),
        "latency_p95_ms": _percentile(latencies, 0.95),
    }
    thresholds = {
        "min_top1_rate": 1.0,
        "min_top3_rate": 1.0,
        "min_friendly_action_legality_rate": 1.0,
        "min_response_action_legality_rate": 1.0,
        "max_mean_minimax_regret": 0.0,
        "max_max_minimax_regret": 0,
        "max_false_safe_count": 0,
        "max_false_exact_count": 0,
        "max_rule_provenance_failure_count": 0,
        "max_abstain_violation_count": 0,
        "max_fixture_contract_failure_count": 0,
        "min_exact_fixture_count": 6,
        "min_abstain_fixture_count": 3,
        "max_latency_p95_ms": 10_000.0,
        **dict(suite.get("thresholds", {})),
    }
    checks: list[dict[str, Any]] = []
    check_specs = [
        ("top1_rate", ">=", thresholds["min_top1_rate"]),
        ("top3_rate", ">=", thresholds["min_top3_rate"]),
        ("friendly_action_legality_rate", ">=", thresholds["min_friendly_action_legality_rate"]),
        ("response_action_legality_rate", ">=", thresholds["min_response_action_legality_rate"]),
        ("mean_minimax_regret", "<=", thresholds["max_mean_minimax_regret"]),
        ("max_minimax_regret", "<=", thresholds["max_max_minimax_regret"]),
        ("false_safe_count", "<=", thresholds["max_false_safe_count"]),
        ("false_exact_count", "<=", thresholds["max_false_exact_count"]),
        ("rule_provenance_failure_count", "<=", thresholds["max_rule_provenance_failure_count"]),
        ("abstain_violation_count", "<=", thresholds["max_abstain_violation_count"]),
        ("fixture_contract_failure_count", "<=", thresholds["max_fixture_contract_failure_count"]),
        ("exact_fixture_count", ">=", thresholds["min_exact_fixture_count"]),
        ("abstain_fixture_count", ">=", thresholds["min_abstain_fixture_count"]),
        ("latency_p95_ms", "<=", thresholds["max_latency_p95_ms"]),
    ]
    for name, operator, threshold in check_specs:
        value = metrics[name]
        passed = value >= threshold if operator == ">=" else value <= threshold
        checks.append(
            {
                "name": name,
                "value": value,
                "operator": operator,
                "threshold": threshold,
                "passed": bool(passed),
            }
        )
    return {
        "schema_version": 1,
        "suite_id": HDT_RULE_SUITE_ID,
        "model_version": "counterplay-turnpair-v1+hdt-visible-point-effects-v1",
        "seed": seed,
        "fixture_file": str(Path(fixture_path).resolve()),
        "fixture_file_sha256": hashlib.sha256(Path(fixture_path).read_bytes()).hexdigest(),
        "fixture_hashes": fixture_hashes,
        "oracle_independence": {
            "imports_production_card_rules": False,
            "imports_production_simulator": False,
            "imports_production_search": False,
            "rule_truth_source": "explicit oracle_effects in versioned fixtures",
        },
        "metrics": metrics,
        "checks": checks,
        "passed": all(item["passed"] for item in checks),
        "details": details,
        "caveat": (
            "This gate proves only the curated deterministic point-effect slice and its "
            "strict provenance/best-effort downgrade contracts; it is not complete Hearthstone "
            "or RL evidence."
        ),
    }


def write_hdt_rule_report(report: Mapping[str, Any], path: str | Path) -> None:
    output = Path(path)
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temporary.replace(output)
