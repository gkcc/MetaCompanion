use metacompanion_solver::hdt::solve_request_from_value;
use metacompanion_solver::oracle::{apply_action, apply_action_outcomes, legal_actions};
use metacompanion_solver::rules::apply_embedded_rules;
use serde_json::{Value, json};

fn outcome_summary(outcome: &metacompanion_solver::oracle::ActionOutcome) -> Value {
    let mut minions = outcome
        .state
        .opponent
        .board
        .iter()
        .map(|card| (card.entity_id.to_string(), card.current_health))
        .collect::<Vec<_>>();
    minions.sort_by(|left, right| left.0.cmp(&right.0));
    let minion_health = minions
        .into_iter()
        .map(|(entity_id, health)| (entity_id, json!(health)))
        .collect::<serde_json::Map<_, _>>();
    json!({
        "probability": {
            "numerator": outcome.probability.numerator,
            "denominator": outcome.probability.denominator
        },
        "friendly_mana": outcome.state.friendly.mana,
        "friendly_hand_count": outcome.state.friendly.hand.len(),
        "opponent_hero_health": outcome.state.opponent.hero.current_health,
        "opponent_minion_health": minion_health
    })
}

#[test]
fn shared_exact_outcome_fixtures_match_rust_transition_engine() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-visible-chance-v1.json"
    ))
    .expect("chance fixture suite");
    assert_eq!(suite["schema_version"], 1);
    assert_eq!(suite["suite_id"], "oracle-visible-chance-v1");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");
    assert!(fixtures.len() >= 2);

    for fixture in fixtures {
        let mut request =
            solve_request_from_value(fixture["request"].clone()).expect("canonical chance request");
        let assessment = apply_embedded_rules(&mut request.state).expect("bind reviewed rule");
        assert_eq!(
            assessment.matched_rule_ids(),
            vec![
                fixture["expected"]["matched_rule_id"]
                    .as_str()
                    .expect("matched rule id")
                    .to_owned()
            ]
        );
        let action = legal_actions(&request.state)
            .expect("legal actions")
            .into_iter()
            .find(|action| action.action_id() == fixture["action_id"].as_str().unwrap())
            .expect("fixture action");
        let outcomes = apply_action_outcomes(&request.state, &action).expect("chance outcomes");
        let common_denominator = outcomes
            .iter()
            .map(|outcome| u128::from(outcome.probability.denominator))
            .product::<u128>();
        let numerator_sum = outcomes
            .iter()
            .map(|outcome| {
                u128::from(outcome.probability.numerator) * common_denominator
                    / u128::from(outcome.probability.denominator)
            })
            .sum::<u128>();
        assert_eq!(numerator_sum, common_denominator);

        let mut actual = outcomes.iter().map(outcome_summary).collect::<Vec<_>>();
        actual.sort_by_key(Value::to_string);
        let mut expected = fixture["expected"]["outcomes"]
            .as_array()
            .expect("expected outcomes")
            .clone();
        expected.sort_by_key(Value::to_string);
        assert_eq!(actual, expected, "{}", fixture["id"]);
        if fixture["expected"]["deterministic_transition_rejected"] == true {
            assert!(apply_action(&request.state, &action).is_err());
        }
    }
}
