use std::collections::BTreeSet;
use std::sync::atomic::AtomicBool;

use metacompanion_solver::hdt::solve_request_from_value;
use metacompanion_solver::hdt_root::HdtRootCandidateSet;
use metacompanion_solver::model::{Card, Effect, EffectCoverage, GameState, JsonScalar};
use metacompanion_solver::oracle::{apply_action, apply_action_outcomes, legal_actions};
use metacompanion_solver::rules::{RULESET_ID, apply_embedded_rules, embedded_rule_bundle};
use metacompanion_solver::turnpair::{
    MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, SearchControl, choose_parity_line,
    plan_visible_response_with_control_and_roots, prove_scoped_lethal, prove_turnpair,
    visible_legal_actions,
};
use serde_json::{Map, Value, json};

fn value_or(raw: &Map<String, Value>, key: &str, fallback: Value) -> Value {
    raw.get(key).cloned().unwrap_or(fallback)
}

fn fixture_card_payload(
    raw: &Map<String, Value>,
    fallback_id: &str,
    default_type: &str,
    default_health: i64,
) -> Value {
    let health = raw
        .get("health")
        .and_then(Value::as_i64)
        .unwrap_or(default_health);
    let current_health = raw
        .get("current_health")
        .and_then(Value::as_i64)
        .unwrap_or(health);
    let can_attack = raw
        .get("can_attack")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let mut tags = raw
        .get("tags")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    tags.entry("NUM_TURNS_IN_PLAY")
        .or_insert(json!(if can_attack { 1 } else { 0 }));
    tags.entry("NUM_ATTACKS_THIS_TURN").or_insert(json!(0));
    let mut payload = json!({
        "entity_id": value_or(raw, "entity_id", json!(fallback_id)),
        "card_id": value_or(raw, "card_id", json!(format!("EVAL_{}", fallback_id.to_ascii_uppercase()))),
        "name": value_or(raw, "name", json!(fallback_id)),
        "card_type": value_or(raw, "card_type", json!(default_type)),
        "cost": value_or(raw, "cost", json!(0)),
        "attack": value_or(raw, "attack", json!(0)),
        "health": health,
        "damage": (health - current_health).max(0),
        "armor": value_or(raw, "armor", json!(0)),
        "card_text": value_or(raw, "text", json!("")),
        "english_text": value_or(raw, "text", json!("")),
        "is_playable_card": value_or(raw, "playable", json!(true)),
        "is_exhausted": !can_attack,
        "is_frozen": false,
        "has_taunt": value_or(raw, "taunt", json!(false)),
        "has_divine_shield": value_or(raw, "divine_shield", json!(false)),
        "has_stealth": value_or(raw, "stealth", json!(false)),
        "has_poisonous": false,
        "has_windfury": false,
        "has_rush": false,
        "has_charge": false,
        "has_reborn": false,
        "is_dormant": false,
        "is_immune": false,
        "mechanics": value_or(raw, "mechanics", json!([])),
        "tags": tags,
        "visibility": "public",
        "zone": value_or(raw, "zone", json!(if default_type == "SPELL" { "HAND" } else { "PLAY" })),
    });
    if let Some(value) = raw.get("lifesteal") {
        payload["has_lifesteal"] = json!(value.as_bool() == Some(true));
    }
    payload
}

fn fixture_hdt_player(value: Option<&Value>, player_id: i64, label: &str) -> Value {
    let empty = Map::new();
    let raw = value.and_then(Value::as_object).unwrap_or(&empty);
    let mut hero_raw = raw
        .get("hero")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    hero_raw
        .entry("entity_id")
        .or_insert(json!(if label == "friendly" { 10 } else { 30 }));
    hero_raw
        .entry("card_id")
        .or_insert(json!(format!("HERO_{}", label.to_ascii_uppercase())));
    hero_raw
        .entry("name")
        .or_insert(json!(format!("Hero {label}")));
    hero_raw.entry("card_type").or_insert(json!("HERO"));
    hero_raw.entry("health").or_insert(json!(30));
    hero_raw.entry("current_health").or_insert(json!(30));
    hero_raw
        .entry("armor")
        .or_insert(value_or(raw, "armor", json!(0)));
    let hand = raw
        .get("hand")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .enumerate()
        .map(|(index, item)| {
            fixture_card_payload(
                item.as_object().expect("hand card object"),
                &format!("{label}-hand-{index}"),
                "SPELL",
                1,
            )
        })
        .collect::<Vec<_>>();
    let board = raw
        .get("board")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .enumerate()
        .map(|(index, item)| {
            fixture_card_payload(
                item.as_object().expect("board card object"),
                &format!("{label}-board-{index}"),
                "MINION",
                1,
            )
        })
        .collect::<Vec<_>>();
    let mut power = raw
        .get("hero_power")
        .and_then(Value::as_object)
        .map(|item| fixture_card_payload(item, &format!("{label}-hero-power"), "HERO_POWER", 1));
    if let Some(power) = &mut power {
        let available = raw
            .get("hero_power_available")
            .and_then(Value::as_bool)
            .unwrap_or(true);
        power["is_exhausted"] = json!(!available);
        power["is_playable_card"] = json!(false);
        power["tags"]["HAS_ACTIVATE_POWER"] = json!(1);
        power["tags"]["EXHAUSTED"] = json!(if available { 0 } else { 1 });
    }
    let mana = raw.get("mana").and_then(Value::as_i64).unwrap_or(0);
    let player_entity = if raw
        .get("omit_player_entity")
        .and_then(Value::as_bool)
        .unwrap_or(false)
    {
        Value::Null
    } else {
        json!({
            "entity_id": player_id,
            "tags": raw.get("player_tags").cloned().unwrap_or(json!({}))
        })
    };
    json!({
        "player_id": player_id,
        "max_mana": raw.get("max_mana").and_then(Value::as_i64).unwrap_or(mana),
        "deck_count": raw.get("deck_size").and_then(Value::as_i64).unwrap_or(0),
        "fatigue": raw.get("fatigue").and_then(Value::as_i64).unwrap_or(0),
        "resources": {
            "available": mana,
            "total": raw.get("max_mana").and_then(Value::as_i64).unwrap_or(mana),
            "spell_power": raw.get("spell_power").and_then(Value::as_i64).unwrap_or(0)
        },
        "player_entity": player_entity,
        "hero": fixture_card_payload(&hero_raw, &format!("{label}-hero"), "HERO", 30),
        "hero_power": power,
        "weapon": null,
        "hand": hand,
        "board": board,
        "deck": [],
        "graveyard": [],
        "secrets": [],
        "set_aside": []
    })
}

fn raw_request_from_fixture(fixture: &Value, seed: i64) -> Value {
    let fixture_id = fixture["id"].as_str().expect("fixture id");
    let position = fixture["position"].as_object().expect("position object");
    let seed_offset = fixture
        .get("seed_offset")
        .and_then(Value::as_i64)
        .unwrap_or(0);
    json!({
        "api_version": "1.0",
        "request_id": format!("hdt-rule-candidate:{fixture_id}"),
        "state": {
            "schema_version": 1,
            "state_id": format!("hdt-rule-candidate-state:{fixture_id}"),
            "game_id": format!("hdt-rule-game:{fixture_id}"),
            "snapshot_sequence": 1,
            "turn_number": position.get("turn").cloned().unwrap_or(json!(1)),
            "active_player": "player",
            "is_local_player_turn": true,
            "environment_version": "oracle-hdt-cardrules-v1",
            "format": "STANDARD",
            "format_type": "FT_STANDARD",
            "game_mode": "RANKED_STANDARD",
            "game_type": "GT_RANKED",
            "hearthstone_build": 247416,
            "hdt_version": "1.54.0",
            "player": fixture_hdt_player(position.get("friendly"), 1, "friendly"),
            "opponent": fixture_hdt_player(position.get("opponent"), 2, "opponent"),
            "unknown_data": [],
            "unsupported_features": [],
            "metadata": {"fixture": fixture_id}
        },
        "options": {
            "time_budget_ms": 400,
            "max_iterations": 5000,
            "max_depth": 10,
            "top_k": 3,
            "search_seed": seed + seed_offset,
            "allow_approximate_effects": true
        }
    })
}

fn find_card<'a>(state: &'a GameState, entity_id: &str) -> Option<&'a Card> {
    for player in [&state.friendly, &state.opponent] {
        if player.hero.entity_id.as_ref() == entity_id {
            return Some(&player.hero);
        }
        if let Some(card) = player
            .hand
            .iter()
            .chain(player.board.iter())
            .chain(player.hero_power.iter())
            .chain(player.weapon.iter())
            .find(|card| card.entity_id.as_ref() == entity_id)
        {
            return Some(card);
        }
    }
    None
}

fn fixture_sources(fixture: &Value) -> Vec<&Map<String, Value>> {
    let Some(friendly) = fixture["position"]["friendly"].as_object() else {
        return Vec::new();
    };
    let mut sources = friendly
        .get("hand")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .chain(
            friendly
                .get("board")
                .and_then(Value::as_array)
                .into_iter()
                .flatten(),
        )
        .filter_map(Value::as_object)
        .collect::<Vec<_>>();
    if let Some(power) = friendly.get("hero_power").and_then(Value::as_object) {
        sources.push(power);
    }
    sources
}

#[test]
fn every_hdt_rule_fixture_preserves_exact_scoped_and_abstain_contracts() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("HDT rule fixture JSON");
    let seed = suite["seed"].as_i64().expect("suite seed");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");
    let mut exact_count = 0;
    let mut scoped_count = 0;
    let mut abstain_count = 0;
    let mut positive_rule_ids = BTreeSet::new();
    let mut positive_card_ids = BTreeSet::new();
    for fixture in fixtures {
        let fixture_id = fixture["id"].as_str().expect("fixture id");
        let scope = fixture["scope"].as_str().expect("scope");
        let mut request = solve_request_from_value(raw_request_from_fixture(fixture, seed))
            .unwrap_or_else(|error| panic!("{fixture_id}: HDT adapter failed: {error}"));
        assert!(
            matches!(
                &request.state.metadata["adapter"],
                JsonScalar::String(value) if value.as_ref() == "hdt-snapshot-v1"
            ),
            "{fixture_id}"
        );
        assert_eq!(
            request.state.perspective_player_id.as_ref(),
            "1",
            "{fixture_id}"
        );
        let assessment = apply_embedded_rules(&mut request.state)
            .unwrap_or_else(|error| panic!("{fixture_id}: rule bundle failed: {error}"));
        let expected_rule_ids = fixture["expected"]["matched_rule_ids"]
            .as_array()
            .expect("expected matched rules")
            .iter()
            .map(|value| value.as_str().expect("rule ID").to_owned())
            .collect::<Vec<_>>();
        assert_eq!(
            assessment.matched_rule_ids(),
            expected_rule_ids,
            "{fixture_id}"
        );

        if let Some(reason) = fixture["expected"]
            .get("mismatch_reason")
            .and_then(Value::as_str)
        {
            assert_eq!(assessment.mismatches.len(), 1, "{fixture_id}");
            assert_eq!(assessment.mismatches[0].reason, reason, "{fixture_id}");
        }
        match scope {
            "exact" => {
                exact_count += 1;
                positive_rule_ids.extend(expected_rule_ids.iter().cloned());
                assert_eq!(
                    assessment.matched.len(),
                    expected_rule_ids.len(),
                    "{fixture_id}"
                );
                assert!(assessment.mismatches.is_empty(), "{fixture_id}");
                let sources = fixture_sources(fixture);
                for matched in &assessment.matched {
                    let source = sources
                        .iter()
                        .copied()
                        .find(|source| {
                            source["entity_id"].as_str() == Some(matched.entity_id.as_str())
                        })
                        .expect("matched fixture source card");
                    positive_card_ids.insert(matched.card_id.clone());
                    let card = find_card(&request.state, &matched.entity_id)
                        .expect("adapted matched source");
                    assert_eq!(card.rule_version.as_ref(), RULESET_ID, "{fixture_id}");
                    assert_eq!(card.rule_id.as_ref(), matched.rule_id, "{fixture_id}");
                    assert!(!card.rule_text_sha256.is_empty(), "{fixture_id}");
                    assert_eq!(card.effect_coverage, EffectCoverage::Exact, "{fixture_id}");
                    assert!(card.unsupported_effects.is_empty(), "{fixture_id}");
                    let expected_effects: Vec<Effect> = serde_json::from_value(
                        source
                            .get("oracle_effects")
                            .or_else(|| source.get("expected_rule_effects"))
                            .cloned()
                            .expect("fixture-owned expected effects"),
                    )
                    .expect("expected effects");
                    assert_eq!(
                        card.effects.as_ref(),
                        expected_effects.as_slice(),
                        "{fixture_id}"
                    );
                    if matched.rule_id.contains("lifesteal") {
                        assert!(card.lifesteal, "{fixture_id}: raw HDT Lifesteal was lost");
                    }
                }
            }
            "scoped_lethal" => {
                scoped_count += 1;
                assert!(assessment.matched.is_empty(), "{fixture_id}");
                assert!(assessment.mismatches.is_empty(), "{fixture_id}");
                let unknown = find_card(&request.state, "20").expect("unknown alternative");
                assert_eq!(unknown.effect_coverage, EffectCoverage::Unsupported);
                assert!(
                    unknown
                        .unsupported_effects
                        .iter()
                        .any(|item| item.as_ref() == "card_text_not_parsed")
                );
                let attacker = find_card(&request.state, "40").expect("clean attacker");
                assert!(attacker.can_attack);
                let proof = prove_scoped_lethal(
                    &request.state,
                    MAX_ENUMERATED_NODES,
                    MAX_LINE_DEPTH,
                    &AtomicBool::new(false),
                )
                .expect("scoped proof")
                .expect("clean lethal line");
                assert_eq!(proof.actions[0].action_id(), "attack:40:30");
                assert!(proof.immediate_lethal);
                assert_eq!(proof.terminal_state.opponent.hero.current_health, 0);
            }
            "abstain" => {
                abstain_count += 1;
                assert!(assessment.matched.is_empty(), "{fixture_id}");
                let source =
                    find_card(&request.state, "20").or_else(|| find_card(&request.state, "11"));
                if let Some(card) = source {
                    assert_ne!(card.effect_coverage, EffectCoverage::Exact, "{fixture_id}");
                    if fixture["expected"]["mismatch_reason"] == "required_mechanic_unproven" {
                        assert!(!card.lifesteal, "{fixture_id}");
                    }
                }
            }
            other => panic!("unexpected scope {other}"),
        }
    }
    assert_eq!(exact_count, 43);
    assert_eq!(scoped_count, 1);
    assert_eq!(abstain_count, 26);
    let expected_exact_rule_ids = embedded_rule_bundle()
        .expect("embedded rules")
        .rule_ids()
        .into_iter()
        .filter(|rule_id| {
            !matches!(
                rule_id.as_str(),
                "jail-underbelly-network-location-v1"
                    | "cata-sleet-storm-selected-and-random-damage-v1"
                    | "smuggled-shovel-generated-spell-deathrattle-v1"
                    | "arcane-tripwire-split-and-shuffle-v1"
                    | "confront-the-tolvir-one-cost-replay-v1"
            )
        })
        .collect::<BTreeSet<_>>();
    assert_eq!(positive_rule_ids, expected_exact_rule_ids);
    for card_id in [
        "CORE_DS1_185",
        "CORE_UNG_084",
        "CORE_ICC_055",
        "CORE_SW_442",
        "RLK_024",
        "CORE_EX1_319",
        "BAR_745",
        "TSC_963",
        "CORE_CS2_072",
        "CATA_COIN5",
        "WW_001t",
        "CS2_008",
        "CORE_BAR_801",
        "TLC_630t",
        "HERO_01dbp",
        "HERO_10cbp",
        "TIME_218",
        "TLC_600",
        "JAIL_801",
        "JAIL_801t",
        "CORE_CS2_024",
        "CATA_582",
        "CORE_REV_990",
        "TIME_606",
        "TLC_836",
        "CATA_554",
        "CORE_CS2_093",
        "CORE_CS2_032",
        "CORE_CS2_062",
        "CORE_UNG_205",
        "CORE_ULD_191",
        "CORE_EX1_619",
        "CORE_CS2_028",
        "CORE_CS1_112",
    ] {
        assert!(positive_card_ids.contains(card_id), "{card_id}");
    }
}

#[test]
fn hunter_resource_rules_match_the_reviewed_carddefs_texts_before_transition_tests() {
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "reviewed-hunter-resources",
        "turn": 6,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "friendly": {
            "player_id": "friendly",
            "hero": {"entity_id": "fh", "card_type": "HERO", "health": 30},
            "hand": [
                {
                    "entity_id": "shovel",
                    "card_id": "JAIL_380",
                    "card_type": "WEAPON",
                    "card_text": "Deathrattle: Draw a spell that didn't start in your deck.",
                    "effect_coverage": "unsupported",
                    "unsupported_effects": ["card_text_not_parsed"]
                },
                {
                    "entity_id": "arcane-tripwire",
                    "card_id": "JAIL_881",
                    "card_type": "SPELL",
                    "card_text": "Deal 5 damage split among all enemies. Shuffle 2 spells into your deck that do it again when drawn.",
                    "effect_coverage": "unsupported",
                    "unsupported_effects": ["card_text_not_parsed"]
                },
                {
                    "entity_id": "tolvir",
                    "card_id": "CATA_560",
                    "card_type": "SPELL",
                    "card_text": "Replay each 1-Cost card you've played this game (targeting enemies if possible.)",
                    "effect_coverage": "unsupported",
                    "unsupported_effects": ["card_text_not_parsed"]
                }
            ]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {"entity_id": "oh", "card_type": "HERO", "health": 30}
        }
    }))
    .expect("reviewed hunter resource state");
    state
        .validate()
        .expect("valid reviewed hunter resource state");

    let assessment = apply_embedded_rules(&mut state).expect("match reviewed resource rules");
    assert_eq!(
        assessment
            .matched_rule_ids()
            .into_iter()
            .collect::<BTreeSet<_>>(),
        BTreeSet::from([
            "arcane-tripwire-split-and-shuffle-v1".to_owned(),
            "confront-the-tolvir-one-cost-replay-v1".to_owned(),
            "smuggled-shovel-generated-spell-deathrattle-v1".to_owned(),
        ])
    );
    assert_eq!(
        state.friendly.hand[0].effects[0].kind.as_ref(),
        "draw_non_starting_spell_on_weapon_break"
    );
    assert_eq!(state.friendly.hand[1].effects.len(), 2);
    assert_eq!(
        state.friendly.hand[2].effects[0].kind.as_ref(),
        "replay_one_cost_cards"
    );
}

#[test]
fn fixed_enemy_hero_power_from_hdt_without_click_target_is_evaluated() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let fixture = suite["fixtures"]
        .as_array()
        .expect("fixtures")
        .iter()
        .find(|fixture| fixture["id"] == "raw-hdt-queldorei-fletcher-free-steady-shot-lethal")
        .expect("Steady Shot fixture");
    let mut request = solve_request_from_value(raw_request_from_fixture(
        fixture,
        suite["seed"].as_i64().expect("seed"),
    ))
    .expect("adapt Steady Shot fixture");
    apply_embedded_rules(&mut request.state).expect("apply Steady Shot rules");
    let roots: HdtRootCandidateSet = serde_json::from_value(json!({
        "contract": "hdt_complete_main_action_options_v1",
        "state_id": request.state.state_id.as_ref(),
        "frame_id": 1,
        "collector_epoch": 1,
        "frame_watermark": 1,
        "candidate_set_complete": true,
        "candidates": [
            {
                "option_id": 67,
                "action": {
                    "kind": "hero_power",
                    "source_entity_id": "11",
                    "target_entity_id": "",
                    "card_id": "HERO_05dbp",
                    "board_position": 0
                },
                "target_evidence": "hdt_no_legal_target",
                "position_evidence": "not_applicable"
            },
            {
                "option_id": 0,
                "action": {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                    "board_position": 0
                },
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable"
            }
        ]
    }))
    .expect("HDT roots");
    roots.validate(&request.state).expect("valid HDT roots");
    let cancel = AtomicBool::new(false);
    let mut control = SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None);
    let plan = plan_visible_response_with_control_and_roots(
        &request.state,
        2,
        MAX_LINE_DEPTH,
        &mut control,
        Some(&roots),
    )
    .expect("visible response plan");
    assert!(
        plan.modeled_first_action_ids
            .iter()
            .any(|action_id| action_id == "hero_power:11:")
    );
    assert!(
        plan.omitted_first_action_ids
            .iter()
            .all(|action_id| action_id != "hero_power:11:")
    );
    let shot = plan
        .lines
        .iter()
        .find(|line| line.first_action_id() == "hero_power:11:")
        .expect("evaluated targetless HDT Steady Shot root");
    assert_eq!(shot.terminal_state.opponent.hero.current_health, 0);
}

#[test]
fn visible_search_reprices_steady_shot_after_hand_drops_from_four_to_three() {
    let fixture = json!({
        "id": "raw-hdt-queldorei-fletcher-four-to-three-free-shot",
        "scope": "exact",
        "seed_offset": 0,
        "position": {
            "friendly": {
                "mana": 1,
                "max_mana": 1,
                "hero_power_available": true,
                "player_tags": {
                    "STEADY_SHOT_CAN_TARGET": 0,
                    "CURRENT_HEROPOWER_DAMAGE_BONUS": 0,
                    "HERO_POWER_DOUBLE": 0,
                    "HEROPOWER_DAMAGE": 0
                },
                "hero_power": {
                    "entity_id": "11",
                    "card_id": "HERO_05dbp",
                    "name": "Steady Shot",
                    "card_type": "HERO_POWER",
                    "cost": 2,
                    "text": "<b>Hero Power</b>\nDeal $2 damage to the enemy hero.@ <b>Hero Power</b>\nDeal $2 damage.",
                    "tags": {"COST": 2, "TAG_LAST_KNOWN_COST_IN_HAND": 2}
                },
                "hand": [
                    {
                        "entity_id": "20",
                        "card_id": "EVAL_SPEND",
                        "name": "Spend",
                        "card_type": "MINION",
                        "cost": 1,
                        "attack": 1,
                        "health": 1,
                        "current_health": 1,
                        "text": ""
                    },
                    {"entity_id": "21", "card_id": "EVAL_FILLER_1", "card_type": "MINION", "cost": 9, "health": 1, "text": ""},
                    {"entity_id": "22", "card_id": "EVAL_FILLER_2", "card_type": "MINION", "cost": 9, "health": 1, "text": ""},
                    {"entity_id": "23", "card_id": "EVAL_FILLER_3", "card_type": "MINION", "cost": 9, "health": 1, "text": ""}
                ],
                "board": [{
                    "entity_id": "40",
                    "card_id": "TIME_606",
                    "name": "Queldorei Fletcher",
                    "card_type": "MINION",
                    "attack": 2,
                    "health": 3,
                    "current_health": 3,
                    "text": "Your Hero Power costs (0) while your hand has 3 or less cards.",
                    "tags": {"AURA": 1, "NUM_TURNS_IN_PLAY": 1}
                }]
            },
            "opponent": {
                "hero": {"health": 2, "current_health": 2}
            }
        }
    });
    let mut request = solve_request_from_value(raw_request_from_fixture(&fixture, 20260801))
        .expect("adapt four-to-three Fletcher fixture");
    apply_embedded_rules(&mut request.state).expect("apply Fletcher and Steady Shot rules");
    assert_eq!(request.state.friendly.hand.len(), 4);
    assert_eq!(
        request
            .state
            .friendly
            .hero_power
            .as_ref()
            .map(|power| power.cost),
        Some(2)
    );
    assert!(
        request.state.friendly.hero_power_available,
        "public readiness must survive temporary unaffordability"
    );

    let roots: HdtRootCandidateSet = serde_json::from_value(json!({
        "contract": "hdt_complete_main_action_options_v1",
        "state_id": request.state.state_id.as_ref(),
        "frame_id": 3,
        "collector_epoch": 1,
        "frame_watermark": 3,
        "candidate_set_complete": true,
        "candidates": [
            {
                "option_id": 20,
                "action": {
                    "kind": "play_card",
                    "source_entity_id": "20",
                    "target_entity_id": "",
                    "card_id": "EVAL_SPEND",
                    "board_position": 2
                },
                "target_evidence": "hdt_no_legal_target",
                "position_evidence": "core_board_slots_v1"
            },
            {
                "option_id": 0,
                "action": {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                    "board_position": 0
                },
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable"
            }
        ]
    }))
    .expect("four-to-three HDT roots");
    roots
        .validate(&request.state)
        .expect("valid four-to-three HDT roots");

    let cancel = AtomicBool::new(false);
    let mut control = SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None);
    let plan = plan_visible_response_with_control_and_roots(
        &request.state,
        2,
        MAX_LINE_DEPTH,
        &mut control,
        Some(&roots),
    )
    .expect("four-to-three visible response plan");
    let lethal = plan.lines.first().expect("ranked four-to-three line");
    assert_eq!(
        lethal
            .actions
            .iter()
            .map(|action| action.action_id())
            .collect::<Vec<_>>(),
        vec![
            "play_card:20::position=2".to_owned(),
            "hero_power:11:30".to_owned(),
        ]
    );
    assert_eq!(lethal.terminal_state.opponent.hero.current_health, 0);
    assert_eq!(lethal.terminal_state.friendly.mana, 0);
}

#[test]
fn underbelly_network_hdt_root_summons_visible_rat_without_false_exact_claim() {
    let fixture = json!({
        "id": "raw-hdt-underbelly-network-activation",
        "scope": "approximate",
        "seed_offset": 0,
        "position": {
            "friendly": {
                "mana": 0,
                "max_mana": 2,
                "board": [{
                    "entity_id": "40",
                    "card_id": "JAIL_877",
                    "name": "Underbelly Network",
                    "card_type": "LOCATION",
                    "cost": 2,
                    "health": 2,
                    "current_health": 2,
                    "text": "Summon a 2/1 Rat with \"Deathrattle: Draw a card.\""
                }]
            },
            "opponent": {}
        }
    });
    let mut request = solve_request_from_value(raw_request_from_fixture(&fixture, 20260801))
        .expect("adapt Underbelly Network fixture");
    let assessment = apply_embedded_rules(&mut request.state).expect("apply Location rule");
    assert_eq!(
        assessment.matched_rule_ids(),
        vec!["jail-underbelly-network-location-v1"]
    );
    let roots: HdtRootCandidateSet = serde_json::from_value(json!({
        "contract": "hdt_complete_main_action_options_v1",
        "state_id": request.state.state_id.as_ref(),
        "frame_id": 2,
        "collector_epoch": 1,
        "frame_watermark": 2,
        "candidate_set_complete": true,
        "candidates": [
            {
                "option_id": 9,
                "action": {
                    "kind": "location_activate",
                    "source_entity_id": "40",
                    "target_entity_id": "",
                    "card_id": "JAIL_877",
                    "board_position": 0
                },
                "target_evidence": "hdt_no_legal_target",
                "position_evidence": "not_applicable"
            },
            {
                "option_id": 0,
                "action": {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                    "board_position": 0
                },
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable"
            }
        ]
    }))
    .expect("HDT roots");
    roots.validate(&request.state).expect("valid HDT roots");
    let error = prove_turnpair(
        &request.state,
        true,
        MAX_ENUMERATED_NODES,
        MAX_LINE_DEPTH,
        &AtomicBool::new(false),
    )
    .expect_err("unmodeled summoned Deathrattle must fail exact proof");
    assert!(
        error.to_string().contains("unsupported card effects"),
        "{error}"
    );

    let cancel = AtomicBool::new(false);
    let mut control = SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None);
    let plan = plan_visible_response_with_control_and_roots(
        &request.state,
        2,
        MAX_LINE_DEPTH,
        &mut control,
        Some(&roots),
    )
    .expect("approximate visible Location plan");
    let location_line = plan
        .lines
        .iter()
        .find(|line| line.first_action_id() == "location_activate:40:")
        .expect("evaluated Location root");
    let location = location_line
        .terminal_state
        .friendly
        .board
        .iter()
        .find(|card| card.entity_id.as_ref() == "40")
        .expect("Location still has one charge");
    assert_eq!(location.current_health, 1);
    let rat = location_line
        .terminal_state
        .friendly
        .board
        .iter()
        .find(|card| card.card_id.as_ref() == "JAIL_877t")
        .expect("summoned Snoot Hoarder");
    assert_eq!((rat.attack, rat.current_health), (2, 1));
    assert_eq!(rat.effect_coverage, EffectCoverage::Unsupported);
    assert!(
        rat.unsupported_effects
            .iter()
            .any(|effect| effect.as_ref() == "summoned_card_text_not_modeled")
    );
    assert!(
        location_line
            .approximate_entity_ids
            .iter()
            .any(|entity_id| entity_id == "40")
    );
}

#[test]
fn high_frequency_rules_apply_composed_effects_and_reject_the_transformed_same_id() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let seed = suite["seed"].as_i64().expect("seed");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");

    let wyrm_fixture = fixtures
        .iter()
        .find(|fixture| fixture["id"] == "raw-hdt-windpeak-wyrm-damage-and-armor-lethal")
        .expect("Windpeak Wyrm fixture");
    let mut wyrm = solve_request_from_value(raw_request_from_fixture(wyrm_fixture, seed))
        .expect("adapt Windpeak Wyrm fixture");
    let wyrm_assessment = apply_embedded_rules(&mut wyrm.state).expect("apply Windpeak Wyrm rule");
    assert_eq!(
        wyrm_assessment.matched_rule_ids(),
        vec!["tlc-windpeak-wyrm-battlecry-v1".to_owned()]
    );
    let wyrm_action = legal_actions(&wyrm.state)
        .expect("Windpeak Wyrm actions")
        .into_iter()
        .find(|action| action.action_id() == "play_card:20:30:position=1")
        .expect("Windpeak Wyrm lethal action");
    let (after_wyrm, _) = apply_action(&wyrm.state, &wyrm_action).expect("apply Windpeak Wyrm");
    assert_eq!(after_wyrm.friendly.armor, 6);
    assert_eq!(after_wyrm.opponent.hero.current_health, 0);
    assert_eq!(after_wyrm.friendly.board[0].card_id.as_ref(), "TLC_600");

    let spell_fixture = fixtures
        .iter()
        .find(|fixture| fixture["id"] == "raw-hdt-molten-gold-spell-only-lethal")
        .expect("Molten Gold spell fixture");
    let mut spell = solve_request_from_value(raw_request_from_fixture(spell_fixture, seed))
        .expect("adapt Molten Gold spell fixture");
    let spell_assessment = apply_embedded_rules(&mut spell.state).expect("apply Molten Gold rule");
    assert_eq!(
        spell_assessment.matched_rule_ids(),
        vec!["jail-molten-gold-spell-v1".to_owned()]
    );
    let spell_action = legal_actions(&spell.state)
        .expect("Molten Gold actions")
        .into_iter()
        .find(|action| action.action_id() == "play_card:20:30")
        .expect("Molten Gold lethal action");
    let (after_spell, _) = apply_action(&spell.state, &spell_action).expect("apply Molten Gold");
    assert_eq!(after_spell.opponent.hero.current_health, 0);
    assert!(after_spell.friendly.hand.is_empty());
    assert!(after_spell.friendly.board.is_empty());

    let transformed_fixture = fixtures
        .iter()
        .find(|fixture| fixture["id"] == "molten-gold-transformed-same-id-must-abstain")
        .expect("transformed Molten Gold fixture");
    let mut transformed =
        solve_request_from_value(raw_request_from_fixture(transformed_fixture, seed))
            .expect("adapt transformed Molten Gold fixture");
    let transformed_assessment =
        apply_embedded_rules(&mut transformed.state).expect("assess transformed Molten Gold");
    assert!(transformed_assessment.matched.is_empty());
    assert_eq!(transformed_assessment.mismatches.len(), 1);
    assert_eq!(
        transformed_assessment.mismatches[0].reason,
        "card_type_mismatch"
    );
}

#[test]
fn earthen_roar_enters_visible_search_only_when_the_second_target_is_impossible() {
    let fixture = json!({
        "id": "earthen-roar-no-dragon",
        "position": {
            "friendly": {
                "mana": 1,
                "hand": [{
                    "entity_id": "20",
                    "card_id": "CATA_554",
                    "name": "Earthen Roar",
                    "card_type": "SPELL",
                    "cost": 1,
                    "text": "Set an enemy minion's Health to 1. If you're holding a Dragon, pick another."
                }]
            },
            "opponent": {
                "board": [
                    {"entity_id": "40", "card_type": "MINION", "health": 7, "current_health": 7},
                    {"entity_id": "41", "card_type": "MINION", "health": 5, "current_health": 5}
                ]
            }
        }
    });
    let mut request = solve_request_from_value(raw_request_from_fixture(&fixture, 1))
        .expect("adapt Earthen Roar state");
    let assessment = apply_embedded_rules(&mut request.state).expect("apply Earthen Roar rule");
    assert_eq!(
        assessment.matched_rule_ids(),
        vec!["cata-earthen-roar-single-target-no-dragon-v1".to_owned()]
    );
    let actions = visible_legal_actions(&request.state).expect("visible Earthen Roar actions");
    let target_actions = actions
        .iter()
        .filter(|action| action.source_entity_id.as_ref() == "20")
        .map(|action| action.action_id())
        .collect::<BTreeSet<_>>();
    assert_eq!(
        target_actions,
        BTreeSet::from(["play_card:20:40".to_owned(), "play_card:20:41".to_owned()])
    );
    let action = actions
        .into_iter()
        .find(|action| action.action_id() == "play_card:20:40")
        .expect("first concrete Earthen Roar target");
    let (after, _) = apply_action(&request.state, &action).expect("apply Earthen Roar");
    assert_eq!(after.opponent.board[0].health, 1);
    assert_eq!(after.opponent.board[0].current_health, 1);
    assert_eq!(after.opponent.board[1].current_health, 5);

    let mut with_dragon_fixture = fixture;
    with_dragon_fixture["id"] = json!("earthen-roar-with-dragon");
    with_dragon_fixture["position"]["friendly"]["hand"]
        .as_array_mut()
        .expect("friendly hand")
        .push(json!({
            "entity_id": "21",
            "card_id": "DRAGON_IN_HAND",
            "name": "Dragon",
            "card_type": "MINION",
            "cost": 9,
            "health": 9,
            "tags": {"CARDRACE": 24}
        }));
    let mut guarded = solve_request_from_value(raw_request_from_fixture(&with_dragon_fixture, 1))
        .expect("adapt guarded Earthen Roar state");
    let assessment = apply_embedded_rules(&mut guarded.state).expect("guard Earthen Roar rule");
    assert!(assessment.matched.is_empty());
    assert!(
        assessment.mismatches.iter().any(|item| {
            item.card_id == "CATA_554" && item.reason == "context_hand_race_present"
        })
    );
    assert!(
        visible_legal_actions(&guarded.state)
            .expect("guarded visible actions")
            .iter()
            .all(|action| action.source_entity_id.as_ref() != "20")
    );
}

#[test]
fn automatic_group_rules_need_no_click_target_and_apply_every_declared_effect() {
    let holy_nova = json!({
        "id": "holy-nova-groups",
        "position": {
            "friendly": {
                "mana": 3,
                "spell_power": 1,
                "hero": {"entity_id": "10", "health": 30, "current_health": 25},
                "hand": [{
                    "entity_id": "20",
                    "card_id": "CORE_CS1_112",
                    "name": "Holy Nova",
                    "card_type": "SPELL",
                    "cost": 3,
                    "text": "Deal 2 damage to all enemy minions. Restore 2 Health to all friendly characters."
                }],
                "board": [{"entity_id": "25", "card_type": "MINION", "health": 5, "current_health": 3}]
            },
            "opponent": {
                "board": [
                    {"entity_id": "40", "card_type": "MINION", "health": 4, "current_health": 4},
                    {"entity_id": "41", "card_type": "MINION", "health": 2, "current_health": 2}
                ]
            }
        }
    });
    let mut nova_request = solve_request_from_value(raw_request_from_fixture(&holy_nova, 1))
        .expect("adapt Holy Nova state");
    apply_embedded_rules(&mut nova_request.state).expect("apply Holy Nova rule");
    let nova = visible_legal_actions(&nova_request.state)
        .expect("Holy Nova actions")
        .into_iter()
        .find(|action| action.source_entity_id.as_ref() == "20")
        .expect("Holy Nova action");
    assert!(nova.target_entity_id.is_empty());
    let (after_nova, _) = apply_action(&nova_request.state, &nova).expect("apply Holy Nova");
    assert_eq!(after_nova.friendly.hero.current_health, 27);
    assert_eq!(after_nova.friendly.board[0].current_health, 5);
    assert_eq!(after_nova.opponent.board.len(), 1);
    assert_eq!(after_nova.opponent.board[0].current_health, 1);

    let equality = json!({
        "id": "equality-groups",
        "position": {
            "friendly": {
                "mana": 2,
                "hand": [{
                    "entity_id": "20",
                    "card_id": "CORE_EX1_619",
                    "name": "Equality",
                    "card_type": "SPELL",
                    "cost": 2,
                    "text": "Change the Health of ALL minions to 1."
                }],
                "board": [{"entity_id": "25", "card_type": "MINION", "health": 6, "current_health": 3}]
            },
            "opponent": {
                "board": [{"entity_id": "40", "card_type": "MINION", "health": 8, "current_health": 7}]
            }
        }
    });
    let mut equality_request = solve_request_from_value(raw_request_from_fixture(&equality, 1))
        .expect("adapt Equality state");
    apply_embedded_rules(&mut equality_request.state).expect("apply Equality rule");
    let equality_action = visible_legal_actions(&equality_request.state)
        .expect("Equality actions")
        .into_iter()
        .find(|action| action.source_entity_id.as_ref() == "20")
        .expect("Equality action");
    assert!(equality_action.target_entity_id.is_empty());
    let (after_equality, _) =
        apply_action(&equality_request.state, &equality_action).expect("apply Equality");
    for minion in after_equality
        .friendly
        .board
        .iter()
        .chain(after_equality.opponent.board.iter())
    {
        assert_eq!((minion.health, minion.current_health), (1, 1));
    }

    let blizzard = json!({
        "id": "blizzard-groups",
        "position": {
            "friendly": {
                "mana": 6,
                "spell_power": 1,
                "hand": [{
                    "entity_id": "20",
                    "card_id": "CORE_CS2_028",
                    "name": "Blizzard",
                    "card_type": "SPELL",
                    "cost": 6,
                    "text": "Deal 2 damage to all enemy minions and <b>Freeze</b> them."
                }],
                "board": [{"entity_id": "25", "card_type": "MINION", "health": 5, "current_health": 5}]
            },
            "opponent": {
                "board": [{"entity_id": "40", "card_type": "MINION", "health": 6, "current_health": 6, "can_attack": true}]
            }
        }
    });
    let mut blizzard_request = solve_request_from_value(raw_request_from_fixture(&blizzard, 1))
        .expect("adapt Blizzard state");
    apply_embedded_rules(&mut blizzard_request.state).expect("apply Blizzard rule");
    let blizzard_action = visible_legal_actions(&blizzard_request.state)
        .expect("Blizzard actions")
        .into_iter()
        .find(|action| action.source_entity_id.as_ref() == "20")
        .expect("Blizzard action");
    let (after_blizzard, _) =
        apply_action(&blizzard_request.state, &blizzard_action).expect("apply Blizzard");
    assert_eq!(after_blizzard.friendly.board[0].current_health, 5);
    assert_eq!(after_blizzard.opponent.board[0].current_health, 3);
    assert!(after_blizzard.opponent.board[0].frozen);
    assert!(!after_blizzard.opponent.board[0].can_attack);
}

#[test]
fn sleet_storm_rule_separates_player_target_from_exact_random_outcomes() {
    let fixture = json!({
        "id": "sleet-storm-chance",
        "position": {
            "friendly": {
                "mana": 1,
                "hand": [{
                    "entity_id": "20",
                    "card_id": "CATA_485",
                    "name": "Sleet Storm",
                    "card_type": "SPELL",
                    "cost": 1,
                    "text": "[x]Deal $2 damage.\n\u{00a0}Deal $1 damage to a\n\u{00a0}random enemy minion."
                }]
            },
            "opponent": {
                "board": [
                    {"entity_id": "40", "card_type": "MINION", "health": 3},
                    {"entity_id": "41", "card_type": "MINION", "health": 3, "stealth": true}
                ]
            }
        }
    });
    let mut request = solve_request_from_value(raw_request_from_fixture(&fixture, 1))
        .expect("adapt Sleet Storm state");
    let assessment = apply_embedded_rules(&mut request.state).expect("apply Sleet Storm rule");
    assert_eq!(
        assessment.matched_rule_ids(),
        vec!["cata-sleet-storm-selected-and-random-damage-v1"]
    );
    let spell = legal_actions(&request.state)
        .expect("Sleet Storm actions")
        .into_iter()
        .find(|action| action.action_id() == "play_card:20:30")
        .expect("selected hero target");
    let outcomes = apply_action_outcomes(&request.state, &spell).expect("chance outcomes");
    assert_eq!(outcomes.len(), 2);
    assert!(outcomes.iter().all(|outcome| {
        outcome.probability.numerator == 1
            && outcome.probability.denominator == 2
            && outcome.state.opponent.hero.current_health == 28
    }));
    assert!(outcomes.iter().any(|outcome| {
        outcome
            .state
            .opponent
            .board
            .iter()
            .find(|card| card.entity_id.as_ref() == "41")
            .is_some_and(|card| card.current_health == 2)
    }));
    assert!(apply_action(&request.state, &spell).is_err());
}

#[test]
fn coin_enables_a_followup_action_and_backstab_requires_undamaged_health_evidence() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let seed = suite["seed"].as_i64().expect("seed");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");

    let coin_fixture = fixtures
        .iter()
        .find(|fixture| fixture["id"] == "raw-hdt-coin-enables-followup-minion")
        .expect("coin fixture");
    let mut coin_request = solve_request_from_value(raw_request_from_fixture(coin_fixture, seed))
        .expect("adapt coin fixture");
    apply_embedded_rules(&mut coin_request.state).expect("apply coin rule");
    let coin = legal_actions(&coin_request.state)
        .expect("coin legal actions")
        .into_iter()
        .find(|action| action.action_id() == "play_card:20:")
        .expect("coin action");
    let (after_coin, _) = apply_action(&coin_request.state, &coin).expect("apply coin");
    assert_eq!(after_coin.friendly.mana, 1);
    assert!(
        legal_actions(&after_coin)
            .expect("follow-up actions")
            .iter()
            .any(|action| action.action_id() == "play_card:21::position=1")
    );

    let backstab_fixture = fixtures
        .iter()
        .find(|fixture| fixture["id"] == "raw-hdt-backstab-only-targets-undamaged-minion")
        .expect("backstab fixture");
    let mut exact = solve_request_from_value(raw_request_from_fixture(backstab_fixture, seed))
        .expect("adapt backstab fixture");
    apply_embedded_rules(&mut exact.state).expect("apply backstab rule");
    assert!(
        legal_actions(&exact.state)
            .expect("backstab exact actions")
            .iter()
            .any(|action| action.action_id() == "play_card:20:40")
    );

    let mut damaged_raw = raw_request_from_fixture(backstab_fixture, seed);
    damaged_raw["state"]["opponent"]["board"][0]["damage"] = json!(1);
    let mut damaged = solve_request_from_value(damaged_raw).expect("adapt damaged target");
    apply_embedded_rules(&mut damaged.state).expect("apply rule to damaged target");
    assert!(
        legal_actions(&damaged.state)
            .expect("damaged target actions")
            .iter()
            .all(|action| action.source_entity_id.as_ref() != "20")
    );

    let mut unknown_raw = raw_request_from_fixture(backstab_fixture, seed);
    unknown_raw["state"]["opponent"]["board"][0]
        .as_object_mut()
        .expect("target object")
        .remove("damage");
    let mut unknown = solve_request_from_value(unknown_raw).expect("adapt unknown health target");
    apply_embedded_rules(&mut unknown.state).expect("apply rule to unknown health target");
    assert!(!unknown.state.opponent.board[0].current_health_known);
    assert!(
        legal_actions(&unknown.state)
            .expect("unknown target actions")
            .iter()
            .all(|action| action.source_entity_id.as_ref() != "20")
    );
}

#[test]
fn lifesteal_rule_fixtures_preserve_overkill_and_armor_damage_events() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let seed = suite["seed"].as_i64().expect("seed");
    let expectations = [
        (
            "raw-hdt-drain-soul-lifesteal-overkill-survival",
            "play_card:20:40",
            4,
            0,
            30,
        ),
        (
            "raw-hdt-void-shard-lifesteal-through-armor-lethal",
            "play_card:20:30",
            14,
            0,
            0,
        ),
        (
            "raw-hdt-death-strike-lifesteal-overkill-survival",
            "play_card:20:40",
            7,
            0,
            30,
        ),
    ];
    for (fixture_id, action_id, friendly_health, opponent_armor, opponent_health) in expectations {
        let fixture = suite["fixtures"]
            .as_array()
            .expect("fixtures")
            .iter()
            .find(|fixture| fixture["id"] == fixture_id)
            .expect("lifesteal fixture");
        let mut request = solve_request_from_value(raw_request_from_fixture(fixture, seed))
            .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        apply_embedded_rules(&mut request.state)
            .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        let action = legal_actions(&request.state)
            .expect("legal actions")
            .into_iter()
            .find(|action| action.action_id() == action_id)
            .expect("expected action");
        let (outcome, _) = apply_action(&request.state, &action).expect("apply Lifesteal action");
        assert_eq!(
            outcome.friendly.hero.current_health, friendly_health,
            "{fixture_id}"
        );
        assert_eq!(outcome.opponent.armor, opponent_armor, "{fixture_id}");
        assert_eq!(
            outcome.opponent.hero.current_health, opponent_health,
            "{fixture_id}"
        );
    }
}

#[test]
fn lifesteal_rule_fixtures_preserve_health_through_exact_turnpair_responses() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let seed = suite["seed"].as_i64().expect("seed");
    for (fixture_id, expected_action, expected_response, expected_terminal_health) in [
        (
            "raw-hdt-drain-soul-lifesteal-overkill-survival",
            "play_card:20:40",
            &["attack:41:10", "end_turn::"][..],
            2,
        ),
        (
            "raw-hdt-void-shard-lifesteal-through-armor-lethal",
            "play_card:20:30",
            &[][..],
            14,
        ),
        (
            "raw-hdt-death-strike-lifesteal-overkill-survival",
            "play_card:20:40",
            &["attack:41:10", "end_turn::"][..],
            3,
        ),
    ] {
        let fixture = suite["fixtures"]
            .as_array()
            .expect("fixtures")
            .iter()
            .find(|fixture| fixture["id"] == fixture_id)
            .expect("Lifesteal fixture");
        let mut request = solve_request_from_value(raw_request_from_fixture(fixture, seed))
            .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        apply_embedded_rules(&mut request.state)
            .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        let proof = prove_turnpair(
            &request.state,
            true,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        let line =
            choose_parity_line(&proof).unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        assert_eq!(line.first_action_id(), expected_action, "{fixture_id}");
        assert_eq!(
            line.opponent_response
                .iter()
                .map(metacompanion_solver::model::Action::action_id)
                .collect::<Vec<_>>(),
            expected_response,
            "{fixture_id}"
        );
        assert_eq!(
            line.terminal_state.friendly.hero.current_health, expected_terminal_health,
            "{fixture_id}"
        );
    }
}

#[test]
fn self_damage_rules_target_only_the_owner_hero() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let seed = suite["seed"].as_i64().expect("seed");
    for fixture_id in [
        "raw-hdt-flame-imp-self-damage-is-not-free",
        "raw-hdt-hecklefang-hyena-self-damage-is-not-free",
    ] {
        let fixture = suite["fixtures"]
            .as_array()
            .expect("fixtures")
            .iter()
            .find(|fixture| fixture["id"] == fixture_id)
            .expect("self-damage fixture");
        let mut request = solve_request_from_value(raw_request_from_fixture(fixture, seed))
            .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        apply_embedded_rules(&mut request.state)
            .unwrap_or_else(|error| panic!("{fixture_id}: {error}"));
        let play_actions = legal_actions(&request.state)
            .expect("legal actions")
            .into_iter()
            .filter(|action| action.source_entity_id.as_ref() == "20")
            .collect::<Vec<_>>();
        assert_eq!(play_actions.len(), 1, "{fixture_id}");
        assert_eq!(
            play_actions[0].action_id(),
            "play_card:20:10:position=1",
            "{fixture_id}"
        );
        let (outcome, _) =
            apply_action(&request.state, &play_actions[0]).expect("apply self damage");
        assert_eq!(outcome.friendly.hero.current_health, 1, "{fixture_id}");
    }
}

#[test]
fn hero_power_availability_uses_activation_tags_and_canonicalizes_playable() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let fixture = suite["fixtures"]
        .as_array()
        .expect("fixtures")
        .iter()
        .find(|fixture| fixture["id"] == "raw-hdt-fireblast-targeted-lethal")
        .expect("fireblast fixture");
    let request =
        solve_request_from_value(raw_request_from_fixture(fixture, 20260729)).expect("HDT request");
    assert!(request.state.friendly.hero_power_available);
    assert!(request.state.friendly.public_rule_tags_complete);
    for tag in [
        "CURRENT_HEROPOWER_DAMAGE_BONUS",
        "HERO_POWER_DOUBLE",
        "HEROPOWER_DAMAGE",
    ] {
        assert!(
            matches!(
                request.state.friendly.public_rule_tags.get(tag),
                Some(JsonScalar::Integer(0))
            ),
            "{tag}"
        );
    }
    assert!(
        request
            .state
            .friendly
            .hero_power
            .as_ref()
            .expect("power")
            .playable
    );
}

#[test]
fn armor_up_and_demon_claws_abstain_when_hero_power_is_doubled() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");
    for fixture_id in [
        "raw-hdt-armor-up-prevents-counterlethal",
        "raw-hdt-demon-claws-enables-hero-lethal",
    ] {
        let fixture = fixtures
            .iter()
            .find(|fixture| fixture["id"] == fixture_id)
            .expect("hero-power fixture");
        let mut raw = raw_request_from_fixture(fixture, 20260729);
        raw["state"]["player"]["player_entity"]["tags"]["HERO_POWER_DOUBLE"] = json!(1);
        let mut request = solve_request_from_value(raw).expect("HDT request");
        let assessment = apply_embedded_rules(&mut request.state).expect("embedded rules");
        assert!(assessment.matched.is_empty(), "{fixture_id}");
        assert_eq!(assessment.mismatches.len(), 1, "{fixture_id}");
        assert_eq!(
            assessment.mismatches[0].reason, "context_tag_active",
            "{fixture_id}"
        );
    }
}

#[test]
fn temporary_hero_attack_requires_public_attack_history() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let fixtures = suite["fixtures"].as_array().expect("fixtures");
    for (fixture_id, enabling_action_id) in [
        ("raw-hdt-demon-claws-enables-hero-lethal", "hero_power:11:"),
        (
            "raw-hdt-static-shock-clears-taunt-and-enables-hero-lethal",
            "play_card:20:40",
        ),
    ] {
        let fixture = fixtures
            .iter()
            .find(|fixture| fixture["id"] == fixture_id)
            .expect("temporary Attack fixture");

        let mut known = solve_request_from_value(raw_request_from_fixture(fixture, 20260729))
            .expect("known-history HDT request");
        apply_embedded_rules(&mut known.state).expect("known-history rules");
        assert!(
            known.state.friendly.hero.attacks_remaining_known,
            "{fixture_id}"
        );
        let enabling_action = legal_actions(&known.state)
            .expect("known-history legal actions")
            .into_iter()
            .find(|action| action.action_id() == enabling_action_id)
            .expect("enabling action");
        let (enabled, _) = apply_action(&known.state, &enabling_action)
            .expect("apply known-history enabling action");
        let hero_attack = legal_actions(&enabled)
            .expect("enabled legal actions")
            .into_iter()
            .find(|action| action.action_id() == "attack:10:30")
            .expect("public zero-count history must enable the hero attack");
        let (attacked, _) = apply_action(&enabled, &hero_attack).expect("apply hero attack");
        assert!(matches!(
            attacked.friendly.hero.tags.get("NUM_ATTACKS_THIS_TURN"),
            Some(JsonScalar::Integer(1))
        ));

        let mut missing_raw = raw_request_from_fixture(fixture, 20260729);
        missing_raw["state"]["player"]["hero"]["tags"]
            .as_object_mut()
            .expect("hero tags")
            .remove("NUM_ATTACKS_THIS_TURN");
        let mut missing =
            solve_request_from_value(missing_raw).expect("missing-history HDT request");
        apply_embedded_rules(&mut missing.state).expect("missing-history rules");
        assert!(
            !missing.state.friendly.hero.attacks_remaining_known,
            "{fixture_id}: {:?}",
            missing.state.friendly.hero.tags
        );
        let enabling_action = legal_actions(&missing.state)
            .expect("missing-history legal actions")
            .into_iter()
            .find(|action| action.action_id() == enabling_action_id)
            .expect("missing-history enabling action");
        let (enabled, _) = apply_action(&missing.state, &enabling_action)
            .expect("apply missing-history enabling action");
        assert!(
            legal_actions(&enabled)
                .expect("post-effect legal actions")
                .iter()
                .all(|action| action.action_id() != "attack:10:30"),
            "{fixture_id}: missing history must not invent an unused hero attack"
        );
        let error = prove_turnpair(
            &missing.state,
            true,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect_err("exact turnpair must fail closed without hero attack history");
        assert!(
            error
                .to_string()
                .contains("hero attack history is unavailable"),
            "{fixture_id}: {error}"
        );
    }
}

#[test]
fn malicious_hidden_hdt_payload_cannot_reach_state_serialization_or_errors() {
    let suite: Value = serde_json::from_str(include_str!(
        "../../solver/fixtures/oracle-hdt-cardrules-v1.json"
    ))
    .expect("suite");
    let fixture = suite["fixtures"]
        .as_array()
        .and_then(|fixtures| fixtures.first())
        .expect("fixture");
    let seed = suite["seed"].as_i64().expect("seed");
    let mut raw = raw_request_from_fixture(fixture, seed);
    raw["state"]["opponent"]["hand"] = json!([{
        "entity_id": 9001,
        "card_id": "MALICIOUS_SECRET_CARD_ID",
        "name": "MALICIOUS_SECRET_CARD_NAME",
        "card_type": "SPELL",
        "cost": 9,
        "english_text": "MALICIOUS_SECRET_CARD_TEXT",
        "mechanics": ["MALICIOUS_SECRET_MECHANIC"],
        "is_playable_card": true,
        "visibility": "public",
        "zone": "HAND",
        "zone_id": 3,
        "zone_position": 2,
        "controller_id": 2,
        "tags": {
            "ZONE": 3,
            "ZONE_POSITION": 2,
            "CONTROLLER": 2,
            "COST": 9,
            "DBF_ID": 987654
        }
    }]);
    raw["state"]["player"]["board"] = json!([{
        "entity_id": 9002,
        "card_id": "MALICIOUS_FRIENDLY_HIDDEN_ID",
        "name": "MALICIOUS_FRIENDLY_HIDDEN_NAME",
        "card_type": "MINION",
        "attack": 30,
        "health": 30,
        "english_text": "MALICIOUS_FRIENDLY_HIDDEN_TEXT",
        "visibility": "explicitly-hidden",
        "zone": "PLAY",
        "tags": {"ZONE": 1, "COST": 10}
    }]);

    let request = solve_request_from_value(raw.clone()).expect("redacted HDT request");
    let hidden = request
        .state
        .opponent
        .hand
        .first()
        .expect("opponent hidden slot");
    assert_eq!(hidden.entity_id.as_ref(), "9001");
    assert_eq!(hidden.card_id.as_ref(), "UNKNOWN");
    assert_eq!(hidden.name.as_ref(), "Unknown card");
    assert_eq!(hidden.card_type.as_str(), "UNKNOWN");
    assert_eq!(hidden.cost, 0);
    assert!(!hidden.playable);
    assert!(hidden.card_text.is_empty());
    assert!(hidden.unsupported_effects.is_empty());
    assert_eq!(hidden.tags.len(), 3);
    assert!(hidden.tags.contains_key("ZONE"));
    assert!(hidden.tags.contains_key("ZONE_POSITION"));
    assert!(hidden.tags.contains_key("CONTROLLER"));

    let state_json = serde_json::to_string(&request.state).expect("state JSON");
    for secret in [
        "MALICIOUS_SECRET_CARD_ID",
        "MALICIOUS_SECRET_CARD_NAME",
        "MALICIOUS_SECRET_CARD_TEXT",
        "MALICIOUS_SECRET_MECHANIC",
        "MALICIOUS_FRIENDLY_HIDDEN_ID",
        "MALICIOUS_FRIENDLY_HIDDEN_NAME",
        "MALICIOUS_FRIENDLY_HIDDEN_TEXT",
        "987654",
    ] {
        assert!(!state_json.contains(secret), "state leaked {secret}");
    }

    raw["state"]["is_local_player_turn"] = Value::Null;
    raw["state"]["active_player"] = json!("invalid-active-player");
    let error = solve_request_from_value(raw).expect_err("invalid public state must fail");
    let error_text = error.to_string();
    for secret in [
        "MALICIOUS_SECRET_CARD_ID",
        "MALICIOUS_SECRET_CARD_NAME",
        "MALICIOUS_SECRET_CARD_TEXT",
        "MALICIOUS_SECRET_MECHANIC",
        "MALICIOUS_FRIENDLY_HIDDEN_ID",
        "MALICIOUS_FRIENDLY_HIDDEN_NAME",
        "MALICIOUS_FRIENDLY_HIDDEN_TEXT",
        "987654",
    ] {
        assert!(!error_text.contains(secret), "error leaked {secret}");
    }
}
