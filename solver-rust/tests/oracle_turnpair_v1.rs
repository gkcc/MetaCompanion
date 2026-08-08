use std::sync::atomic::AtomicBool;

use metacompanion_solver::model::{SolveRequest, StateKey};
use metacompanion_solver::oracle::{apply_action, tactical_utility};
use metacompanion_solver::turnpair::{
    MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, advance_to_opponent_start, choose_parity_line,
    prove_turnpair, ranked_lines, verified_portfolio_regret,
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
        "attacks_remaining": value_or(raw, "attacks_remaining", json!(
            if raw.get("can_attack").and_then(Value::as_bool).unwrap_or(can_attack_default) { 1 } else { 0 }
        )),
        "taunt": value_or(raw, "taunt", json!(false)),
        "divine_shield": value_or(raw, "divine_shield", json!(false)),
        "effects": value_or(raw, "effects", json!([])),
        "effect_coverage": value_or(raw, "effect_coverage", json!("exact")),
        "unsupported_effects": value_or(raw, "unsupported_effects", json!([])),
        "tags": value_or(raw, "tags", json!({}))
    })
}

fn player_payload(raw: &Map<String, Value>, player_id: &str, hero_id: &str) -> Value {
    let mut hero = raw
        .get("hero")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    hero.entry("health").or_insert(json!(30));
    let board = raw
        .get("board")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .enumerate()
        .map(|(index, value)| {
            card_payload(
                value.as_object().expect("board card"),
                &format!("{player_id}-board-{index}"),
                "MINION",
                player_id == "friendly",
            )
        })
        .collect::<Vec<_>>();
    let hand = raw
        .get("hand")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .enumerate()
        .map(|(index, value)| {
            card_payload(
                value.as_object().expect("hand card"),
                &format!("{player_id}-hand-{index}"),
                "SPELL",
                false,
            )
        })
        .collect::<Vec<_>>();
    let mana = value_or(raw, "mana", json!(0));
    json!({
        "player_id": player_id,
        "hero": card_payload(&hero, hero_id, "HERO", false),
        "mana": mana,
        "max_mana": value_or(raw, "max_mana", value_or(raw, "mana", json!(0))),
        "armor": value_or(raw, "armor", json!(0)),
        "hand": hand,
        "board": board,
        "deck_size": value_or(raw, "deck_size", json!(0)),
        "fatigue": value_or(raw, "fatigue", json!(0)),
        "hero_power": null,
        "hero_power_available": false,
        "weapon": null
    })
}

fn request_from_fixture(fixture: &Value, seed: i64) -> SolveRequest {
    let fixture = fixture.as_object().expect("fixture");
    let id = fixture["id"].as_str().expect("fixture id");
    let position = fixture["position"].as_object().expect("position");
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
    let mut request: SolveRequest = serde_json::from_value(json!({
        "api_version": "1.0",
        "request_id": format!("turnpair:{id}"),
        "state": {
            "state_id": format!("turnpair-state:{id}"),
            "turn": value_or(position, "turn", json!(1)),
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": player_payload(&friendly, "friendly", "friendly-hero"),
            "opponent": player_payload(&opponent, "opponent", "opponent-hero"),
            "patch": "oracle-turnpair-v1",
            "mode": "evaluation",
            "rng_seed": seed + fixture.get("seed_offset").and_then(Value::as_i64).unwrap_or(0)
        },
        "options": {"max_depth": 12, "top_k": 3}
    }))
    .expect("canonical request");
    request.validate().expect("valid request");
    request
}

#[test]
fn every_exact_turnpair_fixture_matches_top1_and_replays_worst_response() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-turnpair-v1.json"
    ))
    .expect("fixture suite");
    let seed = suite["seed"].as_i64().expect("seed");
    let cancel = AtomicBool::new(false);
    let mut exact_count = 0;
    for fixture in suite["fixtures"].as_array().expect("fixtures") {
        if fixture["scope"] != "exact" {
            continue;
        }
        exact_count += 1;
        let request = request_from_fixture(fixture, seed);
        let proof = prove_turnpair(
            &request.state,
            false,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &cancel,
        )
        .expect("turnpair proof");
        let expected = fixture["expected"]["optimal_first_action_ids"]
            .as_array()
            .expect("expected actions")
            .iter()
            .map(|value| value.as_str().expect("action id").to_owned())
            .collect::<Vec<_>>();
        assert_eq!(
            proof.optimal_first_action_ids, expected,
            "{}",
            fixture["id"]
        );
        let legal_first_action_ids = metacompanion_solver::oracle::legal_actions(&request.state)
            .expect("legal root actions")
            .into_iter()
            .map(|action| {
                if action.kind == metacompanion_solver::model::ActionKind::EndTurn {
                    "end_turn".to_owned()
                } else {
                    action.action_id()
                }
            })
            .collect::<std::collections::BTreeSet<_>>();
        assert_eq!(
            proof
                .root_action_coverage
                .legal_first_action_ids
                .iter()
                .cloned()
                .collect::<std::collections::BTreeSet<_>>(),
            legal_first_action_ids,
            "{}",
            fixture["id"]
        );
        assert_eq!(
            proof.root_action_coverage.generated_first_action_ids,
            proof.root_action_coverage.legal_first_action_ids,
            "{}",
            fixture["id"]
        );
        assert_eq!(
            proof
                .root_action_coverage
                .response_verified_first_action_ids,
            proof.root_action_coverage.legal_first_action_ids,
            "{}",
            fixture["id"]
        );
        assert!(
            proof
                .root_action_coverage
                .missing_first_action_ids
                .is_empty()
        );
        assert!(proof.root_action_coverage.root_action_coverage_complete);
        assert!(proof.portfolio_optimality_proven);
        let portfolio = ranked_lines(&proof, 3);
        let portfolio_first_actions = portfolio
            .iter()
            .map(|item| item.first_action_id())
            .collect::<Vec<_>>();
        assert_eq!(
            portfolio_first_actions.len(),
            portfolio_first_actions
                .iter()
                .collect::<std::collections::HashSet<_>>()
                .len(),
            "{}",
            fixture["id"]
        );
        assert!(
            portfolio
                .iter()
                .all(|item| verified_portfolio_regret(&proof, item) >= 0),
            "{}",
            fixture["id"]
        );
        if let Some(required) =
            fixture["expected"]["required_portfolio_first_action_ids"].as_array()
        {
            let returned = portfolio_first_actions
                .into_iter()
                .collect::<std::collections::HashSet<_>>();
            for action_id in required {
                assert!(
                    returned.contains(action_id.as_str().expect("required first action")),
                    "{} missing {}",
                    fixture["id"],
                    action_id
                );
            }
        }
        let max_returned_regret = fixture["expected"]["max_returned_alternative_regret"]
            .as_i64()
            .or_else(|| fixture["expected"]["max_portfolio_first_action_minimax_regret"].as_i64());
        if max_returned_regret == Some(0) {
            assert!(
                portfolio
                    .iter()
                    .all(|item| verified_portfolio_regret(&proof, item) == 0),
                "{} padded its co-optimal portfolio with an inferior root",
                fixture["id"]
            );
        }

        let line = choose_parity_line(&proof).expect("Top1 line");
        let mut replay = request.state.clone();
        for action in &line.actions {
            replay = apply_action(&replay, action).expect("friendly replay").0;
        }
        if replay.friendly.hero.current_health > 0
            && replay.opponent.hero.current_health > 0
            && !line.opponent_response.is_empty()
        {
            replay = advance_to_opponent_start(&replay).expect("response start");
            for action in &line.opponent_response {
                replay = apply_action(&replay, action).expect("response replay").0;
            }
        }
        assert_eq!(
            StateKey::from_state(&replay),
            StateKey::from_state(&line.terminal_state)
        );
        assert_eq!(
            tactical_utility(&replay, &request.state.perspective_player_id),
            line.minimax_value
        );
    }
    assert_eq!(exact_count, 7);
}
