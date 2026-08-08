use std::sync::atomic::AtomicBool;

use metacompanion_solver::model::{SolveRequest, StateKey};
use metacompanion_solver::oracle::{
    DEFAULT_MAXIMUM_STATES, apply_action, assert_exact_oracle_state, choose_turn_plan, prove_lethal,
};
use serde_json::{Map, Value, json};

fn value_or(raw: &Map<String, Value>, key: &str, fallback: Value) -> Value {
    raw.get(key).cloned().unwrap_or(fallback)
}

fn card_payload(
    raw: &Map<String, Value>,
    fallback_id: &str,
    card_type: &str,
    can_attack_default: bool,
) -> Value {
    let health = value_or(raw, "health", json!(0));
    let can_attack = value_or(raw, "can_attack", json!(can_attack_default));
    json!({
        "entity_id": value_or(raw, "entity_id", json!(fallback_id)),
        "card_id": value_or(raw, "card_id", json!(fallback_id.to_uppercase())),
        "name": value_or(raw, "name", json!(fallback_id)),
        "card_type": value_or(raw, "card_type", json!(card_type)),
        "cost": value_or(raw, "cost", json!(0)),
        "attack": value_or(raw, "attack", json!(0)),
        "health": health,
        "current_health": value_or(raw, "current_health", value_or(raw, "health", json!(0))),
        "playable": value_or(raw, "playable", json!(true)),
        "can_attack": can_attack,
        "attacks_remaining": value_or(
            raw,
            "attacks_remaining",
            json!(if raw.get("can_attack").and_then(Value::as_bool).unwrap_or(can_attack_default) { 1 } else { 0 })
        ),
        "taunt": value_or(raw, "taunt", json!(false)),
        "divine_shield": value_or(raw, "divine_shield", json!(false)),
        "stealth": value_or(raw, "stealth", json!(false)),
        "poisonous": value_or(raw, "poisonous", json!(false)),
        "lifesteal": value_or(raw, "lifesteal", json!(false)),
        "effects": value_or(raw, "effects", json!([])),
        "effect_coverage": value_or(raw, "effect_coverage", json!("exact")),
        "unsupported_effects": value_or(raw, "unsupported_effects", json!([])),
        "prior_weight": value_or(raw, "prior_weight", json!(1.0)),
        "tags": value_or(raw, "tags", json!({}))
    })
}

fn player_payload(raw: &Map<String, Value>, player_id: &str, hero_id: &str) -> Value {
    let mut hero_raw = raw
        .get("hero")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    hero_raw.entry("health").or_insert(json!(30));
    let hero_health = hero_raw.get("health").cloned().unwrap_or(json!(30));
    hero_raw.entry("current_health").or_insert(hero_health);
    let hand: Vec<Value> = raw
        .get("hand")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .enumerate()
        .map(|(index, value)| {
            card_payload(
                value.as_object().expect("hand card object"),
                &format!("{player_id}-hand-{index}"),
                "SPELL",
                false,
            )
        })
        .collect();
    let board: Vec<Value> = raw
        .get("board")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .enumerate()
        .map(|(index, value)| {
            card_payload(
                value.as_object().expect("board card object"),
                &format!("{player_id}-board-{index}"),
                "MINION",
                player_id == "friendly",
            )
        })
        .collect();
    let mana = value_or(raw, "mana", json!(0));
    json!({
        "player_id": player_id,
        "hero": card_payload(&hero_raw, hero_id, "HERO", false),
        "mana": mana,
        "max_mana": value_or(raw, "max_mana", value_or(raw, "mana", json!(0))),
        "armor": value_or(raw, "armor", json!(0)),
        "hand": hand,
        "board": board,
        "deck_size": value_or(raw, "deck_size", json!(20)),
        "fatigue": value_or(raw, "fatigue", json!(0)),
        "hero_power": null,
        "hero_power_available": false,
        "weapon": null
    })
}

fn request_from_fixture(fixture: &Value, seed: i64) -> SolveRequest {
    let fixture = fixture.as_object().expect("fixture object");
    let fixture_id = fixture["id"].as_str().expect("fixture id");
    let position = fixture["position"].as_object().expect("position object");
    let friendly = position
        .get("friendly")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    let opponent = position
        .get("opponent")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    let seed_offset = fixture
        .get("seed_offset")
        .and_then(Value::as_i64)
        .unwrap_or(0);
    let mut request: SolveRequest = serde_json::from_value(json!({
        "api_version": "1.0",
        "request_id": format!("eval-{fixture_id}"),
        "state": {
            "state_id": format!("eval-state-{fixture_id}"),
            "turn": position.get("turn").cloned().unwrap_or(json!(5)),
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": player_payload(&friendly, "friendly", "friendly-hero"),
            "opponent": player_payload(&opponent, "opponent", "opponent-hero"),
            "patch": "oracle-turn-v1",
            "mode": "deterministic_fixture",
            "rng_seed": seed + seed_offset,
            "metadata": {"fixture_id": fixture_id, "oracle_scope": "exact"}
        },
        "options": {
            "search_seed": seed + seed_offset,
            "allow_approximate_effects": true,
            "environment_version": "oracle-turn-v1"
        }
    }))
    .expect("canonical request");
    request.validate().expect("valid canonical request");
    request
}

#[test]
fn every_exact_oracle_turn_fixture_matches_contract_and_replays() {
    let suite: Value =
        serde_json::from_str(include_str!("../../solver/fixtures/oracle-turn-v1.json"))
            .expect("suite JSON");
    let seed = suite["seed"].as_i64().expect("suite seed");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");
    let cancel = AtomicBool::new(false);
    let mut exact_count = 0;
    for fixture in fixtures {
        if fixture["scope"] != "exact" {
            continue;
        }
        exact_count += 1;
        let request = request_from_fixture(fixture, seed);
        assert_exact_oracle_state(&request.state).expect("exact support");
        let proof = prove_lethal(&request.state, DEFAULT_MAXIMUM_STATES, &cancel)
            .expect("bounded oracle proof");
        let expected = fixture["expected"].as_object().expect("expected object");
        assert_eq!(
            proof.has_lethal,
            expected["has_lethal"].as_bool().expect("expected lethal"),
            "fixture {}",
            fixture["id"]
        );
        assert_eq!(
            proof.winning_first_action_ids.len() as u64,
            expected["winning_first_action_count"]
                .as_u64()
                .expect("expected first count"),
            "fixture {}",
            fixture["id"]
        );
        let plan = choose_turn_plan(&request.state, &proof, DEFAULT_MAXIMUM_STATES, &cancel)
            .expect("deterministic turn plan");
        assert!(!plan.actions.is_empty());
        let mut replay = request.state.clone();
        for action in &plan.actions {
            replay = apply_action(&replay, action)
                .expect("plan action must replay")
                .0;
        }
        assert_eq!(
            StateKey::from_state(&replay),
            StateKey::from_state(&plan.terminal_state)
        );
        if proof.has_lethal {
            assert_eq!(plan.minimax_utility, 1_000_000);
            assert_eq!(
                plan.actions[0].action_id(),
                proof.winning_first_action_ids[0]
            );
        } else {
            assert!(plan.minimax_utility < 1_000_000);
        }
    }
    assert_eq!(exact_count, 7);
}
