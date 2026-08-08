use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use metacompanion_solver::card_pool::OfficialCardPoolBundle;
use metacompanion_solver::generation_rules::apply_embedded_generation_rules;
use metacompanion_solver::model::{
    Action, ActionKind, CardPoolClassMode, CardPoolQuery, CardPoolSource, CardType, GameState,
};
use metacompanion_solver::oracle::apply_action_outcomes;
use metacompanion_solver::rules::apply_embedded_rules;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use time::OffsetDateTime;
use time::format_description::well_known::Rfc3339;

const NOW_UTC: &str = "2026-07-30T12:00:00Z";
const GENERATED_AT_UTC: &str = "2026-07-29T12:00:00Z";
const FETCHED_AT_UTC: &str = "2026-07-29T13:00:00Z";

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TempDirectory(PathBuf);

impl TempDirectory {
    fn new(label: &str) -> Self {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "metacompanion-card-pool-{label}-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create card-pool integration directory");
        Self(path)
    }

    fn root(&self) -> &Path {
        &self.0
    }

    fn card_defs(&self) -> PathBuf {
        self.0.join("CardDefs.base.xml")
    }

    fn latest(&self) -> PathBuf {
        self.0.join("latest")
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn write_json(path: &Path, value: &Value) {
    let mut bytes = serde_json::to_vec(value).expect("serialize card-pool fixture");
    bytes.push(b'\n');
    fs::write(path, bytes).expect("write card-pool fixture");
}

fn read_json(path: &Path) -> Value {
    serde_json::from_slice(&fs::read(path).expect("read card-pool fixture"))
        .expect("parse card-pool fixture")
}

fn sha256(path: &Path) -> String {
    format!(
        "{:X}",
        Sha256::digest(fs::read(path).expect("hash card-pool fixture"))
    )
}

fn build_bundle(root: &Path) {
    let latest = root.join("latest");
    fs::create_dir_all(&latest).expect("create latest directory");
    let card_defs = root.join("CardDefs.base.xml");
    fs::write(
        &card_defs,
        concat!(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n",
            "<CardDefs build=\"247416\">\n",
            "  <Entity CardID=\"STD_CARD\" ID=\"1\" />\n",
            "  <Entity CardID=\"ARENA_CARD\" ID=\"2\" />\n",
            "  <Entity CardID=\"STD_MINION\" ID=\"3\" />\n",
            "</CardDefs>\n"
        ),
    )
    .expect("write CardDefs fixture");

    let run_id = "run-interop-1";
    let mut records = Vec::new();
    for (format_name, card_id, dbf_id) in [("standard", "STD_CARD", 1), ("arena", "ARENA_CARD", 2)]
    {
        let mut cards = vec![json!({
            "card_id": card_id,
            "dbf_id": dbf_id,
            "name": card_id,
            "collectible": true,
            "card_set_id": 10,
            "class_id": if format_name == "standard" { 4 } else { 8 },
            "multi_class_ids": [],
            "card_type_id": if format_name == "standard" { 5 } else { 4 },
            "spell_school_id": if format_name == "standard" { 1 } else { 0 },
            "minion_type_id": if format_name == "arena" { 24 } else { 0 },
            "multi_type_ids": [],
            "keyword_ids": [],
            "rarity_id": 1,
            "mana_cost": if format_name == "standard" { 8 } else { 3 },
            "attack": 0,
            "health": 0,
            "durability": 0,
            "text": ""
        })];
        if format_name == "standard" {
            cards.push(json!({
                "card_id": "STD_MINION",
                "dbf_id": 3,
                "name": "Standard Minion",
                "collectible": true,
                "card_set_id": 10,
                "class_id": 4,
                "multi_class_ids": [],
                "card_type_id": 4,
                "spell_school_id": 0,
                "minion_type_id": 20,
                "multi_type_ids": [],
                "keyword_ids": [],
                "rarity_id": 1,
                "mana_cost": 2,
                "attack": 2,
                "health": 2,
                "durability": 0,
                "text": ""
            }));
        }
        let declared_count = cards.len();
        let pool = json!({
            "schema_version": 1,
            "format": format_name,
            "run_id": run_id,
            "declared_count": declared_count,
            "coverage": {"rules_coverage": false},
            "cards": cards
        });
        let pool_path = latest.join(format!("{format_name}.json"));
        write_json(&pool_path, &pool);
        records.push(json!({
            "format": format_name,
            "file": format!("{format_name}.json"),
            "bytes": fs::metadata(&pool_path).expect("pool metadata").len(),
            "sha256": sha256(&pool_path),
            "declared_count": declared_count,
            "unique_card_ids": declared_count,
            "unique_dbf_ids": declared_count,
            "fetched_at_utc": FETCHED_AT_UTC,
            "pages": [{"page": 1, "fetched_at_utc": FETCHED_AT_UTC}]
        }));
    }
    let manifest = json!({
        "schema_version": 1,
        "status": "complete",
        "run_id": run_id,
        "generated_at_utc": GENERATED_AT_UTC,
        "source": {
            "provider": "Blizzard",
            "authentication": "none",
            "browser_required": false
        },
        "coverage": {"rules_coverage": false},
        "card_defs": {
            "file_name": "CardDefs.base.xml",
            "build": "247416",
            "entities": 3,
            "bytes": fs::metadata(&card_defs).expect("CardDefs metadata").len(),
            "sha256": sha256(&card_defs)
        },
        "pools": records
    });
    let manifest_path = latest.join("manifest.json");
    write_json(&manifest_path, &manifest);
    write_json(
        &latest.join("publish-complete.json"),
        &json!({
            "schema_version": 1,
            "run_id": run_id,
            "manifest_sha256": sha256(&manifest_path)
        }),
    );
}

fn refresh_manifest_marker(latest: &Path) {
    let manifest_path = latest.join("manifest.json");
    let publish_path = latest.join("publish-complete.json");
    let mut publish = read_json(&publish_path);
    publish["manifest_sha256"] = json!(sha256(&manifest_path));
    write_json(&publish_path, &publish);
}

fn refresh_pool_record(latest: &Path, format_name: &str) {
    let pool_path = latest.join(format!("{format_name}.json"));
    let pool = read_json(&pool_path);
    let mut manifest = read_json(&latest.join("manifest.json"));
    let record = manifest["pools"]
        .as_array_mut()
        .expect("manifest pools")
        .iter_mut()
        .find(|record| record["format"] == format_name)
        .expect("format record");
    record["bytes"] = json!(fs::metadata(&pool_path).expect("pool metadata").len());
    record["sha256"] = json!(sha256(&pool_path));
    record["declared_count"] = pool["declared_count"].clone();
    write_json(&latest.join("manifest.json"), &manifest);
    refresh_manifest_marker(latest);
}

fn fixed_now() -> SystemTime {
    let timestamp = OffsetDateTime::parse(NOW_UTC, &Rfc3339)
        .expect("fixed test timestamp")
        .unix_timestamp();
    UNIX_EPOCH + Duration::from_secs(u64::try_from(timestamp).expect("positive timestamp"))
}

fn rust_health(root: &Path) -> Value {
    let bundle = OfficialCardPoolBundle::load_with_context(
        root,
        Some(&root.join("CardDefs.base.xml")),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .unwrap_or_else(|error| panic!("Rust card-pool load failed: {}", error.public_message()));
    bundle.health_payload()
}

fn rust_rejection(root: &Path) -> (&'static str, String) {
    let error = OfficialCardPoolBundle::load_with_context(
        root,
        Some(&root.join("CardDefs.base.xml")),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .expect_err("tampered Rust bundle must be rejected");
    (error.reason(), error.public_message().to_owned())
}

fn python_health(root: &Path) -> Value {
    let repository_root = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repository root")
        .to_path_buf();
    let script = r#"
import json
import sys
from datetime import datetime, timedelta
from pathlib import Path
from metacompanion_solver.card_pool import OfficialCardPoolBundle

root = Path(sys.argv[1])
bundle = OfficialCardPoolBundle.load_optional(
    root,
    card_defs_path=root / 'CardDefs.base.xml',
    max_age=timedelta(hours=72),
    now_utc=datetime.fromisoformat(sys.argv[2].replace('Z', '+00:00')),
)
print(json.dumps(bundle.health(), sort_keys=True, separators=(',', ':')))
"#;
    let output = Command::new("python")
        .args(["-c", script])
        .arg(root)
        .arg(NOW_UTC)
        .env("PYTHONPATH", repository_root.join("solver"))
        .output()
        .expect("launch Python card-pool loader");
    assert!(
        output.status.success(),
        "Python card-pool loader failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).expect("parse Python card-pool health")
}

fn assert_same_rejection(root: &Path, expected_reason: &str) {
    let (rust_reason, rust_error) = rust_rejection(root);
    let python = python_health(root);
    assert_eq!(rust_reason, expected_reason);
    assert_eq!(python["available"], false);
    assert_eq!(python["reason"], expected_reason);
    assert!(!rust_error.contains(root.to_string_lossy().as_ref()));
    assert!(!python.to_string().contains(root.to_string_lossy().as_ref()));
}

#[test]
fn rust_and_python_accept_the_same_published_bundle_identity() {
    let temporary = TempDirectory::new("interop");
    build_bundle(temporary.root());
    let rust = rust_health(temporary.root());
    let python = python_health(temporary.root());
    for field in [
        "available",
        "run_id",
        "card_defs_build",
        "card_defs_sha256",
        "card_defs_bytes",
        "manifest_sha256",
        "generated_at_utc",
        "oldest_fetched_at_utc",
        "max_age_hours",
        "future_clock_skew_seconds",
        "standard_count",
        "arena_count",
        "rules_coverage",
        "source",
        "reason",
    ] {
        assert_eq!(rust[field], python[field], "health field {field}");
    }
}

#[test]
fn publish_binding_and_timestamp_tampering_fail_closed_in_both_loaders() {
    let hash_tamper = TempDirectory::new("manifest-hash");
    build_bundle(hash_tamper.root());
    let mut manifest = read_json(&hash_tamper.latest().join("manifest.json"));
    manifest["status"] = json!("changed-after-publish");
    write_json(&hash_tamper.latest().join("manifest.json"), &manifest);
    assert_same_rejection(hash_tamper.root(), "snapshot_invalid");

    let stale_page = TempDirectory::new("stale-page");
    build_bundle(stale_page.root());
    let mut manifest = read_json(&stale_page.latest().join("manifest.json"));
    manifest["pools"][0]["pages"][0]["fetched_at_utc"] = json!("2026-07-26T11:59:59Z");
    write_json(&stale_page.latest().join("manifest.json"), &manifest);
    refresh_manifest_marker(&stale_page.latest());
    assert_same_rejection(stale_page.root(), "snapshot_stale");
}

#[test]
fn card_defs_and_duplicate_identity_tampering_fail_closed_in_both_loaders() {
    let card_defs = TempDirectory::new("card-defs-hash");
    build_bundle(card_defs.root());
    let mut manifest = read_json(&card_defs.latest().join("manifest.json"));
    manifest["card_defs"]["sha256"] = json!("0".repeat(64));
    write_json(&card_defs.latest().join("manifest.json"), &manifest);
    refresh_manifest_marker(&card_defs.latest());
    assert_same_rejection(card_defs.root(), "card_defs_hash_mismatch");

    let duplicates = TempDirectory::new("duplicate-card");
    build_bundle(duplicates.root());
    let pool_path = duplicates.latest().join("standard.json");
    let mut pool = read_json(&pool_path);
    let duplicate = pool["cards"][0].clone();
    pool["cards"].as_array_mut().expect("cards").push(duplicate);
    pool["declared_count"] = json!(2);
    write_json(&pool_path, &pool);
    refresh_pool_record(&duplicates.latest(), "standard");
    assert_same_rejection(duplicates.root(), "snapshot_invalid");
}

#[test]
fn state_membership_is_provenance_only_and_keeps_generated_cards_visible() {
    let temporary = TempDirectory::new("assessment");
    build_bundle(temporary.root());
    let bundle = OfficialCardPoolBundle::load_with_context(
        temporary.root(),
        Some(&temporary.card_defs()),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .expect("load assessment fixture");
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "pool-assessment",
        "turn": 1,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "mode": "Ranked",
        "friendly": {
            "player_id": "friendly",
            "hero": {"entity_id": "fh", "card_id": "HERO_01", "card_type": "HERO", "health": 30},
            "hand": [
                {"entity_id": "in", "card_id": "STD_CARD", "card_type": "MINION"},
                {"entity_id": "generated", "card_id": "GENERATED_CARD", "card_type": "MINION"}
            ]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {"entity_id": "oh", "card_id": "HERO_02", "card_type": "HERO", "health": 30},
            "board": [{"entity_id": "hidden", "card_id": "UNKNOWN_7", "card_type": "MINION"}]
        }
    }))
    .expect("assessment state");
    state.validate().expect("valid assessment state");
    let assessment = bundle.assess_state(&state);
    assert_eq!(assessment["format"], "standard");
    assert_eq!(assessment["membership_assessed"], true);
    assert_eq!(assessment["visible_known_card_count"], 2);
    assert_eq!(assessment["visible_cards_in_pool_count"], 1);
    assert_eq!(assessment["visible_cards_outside_pool_count"], 1);
    assert_eq!(
        assessment["visible_cards_outside_pool"],
        json!(["GENERATED_CARD"])
    );
    assert_eq!(assessment["rules_coverage"], false);
    assert_eq!(assessment["generated_entities_coverage"], false);
    assert_eq!(assessment["enforces_action_legality"], false);
}

#[test]
fn structured_generation_query_filters_cost_type_school_and_class() {
    let temporary = TempDirectory::new("generation-query");
    build_bundle(temporary.root());
    let bundle = OfficialCardPoolBundle::load_with_context(
        temporary.root(),
        Some(&temporary.card_defs()),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .expect("load query fixture");
    let query = CardPoolQuery {
        source: CardPoolSource::CurrentFormat,
        cost_min: Some(8),
        cost_max: Some(8),
        card_types: vec![CardType::Spell],
        class_mode: CardPoolClassMode::Specific,
        class_ids: vec![4],
        spell_school_ids: vec![1],
        ..CardPoolQuery::default()
    };
    assert_eq!(
        bundle
            .query_card_ids("standard", &query, Some(4), "SOURCE")
            .expect("matching query"),
        vec!["STD_CARD"]
    );
    assert_eq!(
        bundle
            .query_card_ids("arena", &query, Some(4), "SOURCE")
            .expect_err("arena row must not match"),
        "pool_query_empty"
    );
}

#[test]
fn resolved_discover_pool_emits_a_public_generated_card_outcome() {
    let temporary = TempDirectory::new("discover-outcome");
    build_bundle(temporary.root());
    let bundle = OfficialCardPoolBundle::load_with_context(
        temporary.root(),
        Some(&temporary.card_defs()),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .expect("load discover fixture");
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "discover",
        "turn": 1,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "mode": "Ranked",
        "friendly": {
            "player_id": "friendly",
            "hero": {
                "entity_id": "fh",
                "card_id": "HERO_08",
                "card_type": "HERO",
                "health": 30,
                "tags": {"CLASS": 4}
            },
            "mana": 1,
            "max_mana": 1,
            "hand": [{
                "entity_id": "source",
                "card_id": "DISCOVER_SOURCE",
                "name": "Discover source",
                "card_type": "SPELL",
                "cost": 1,
                "effect_coverage": "exact",
                "effects": [{
                    "kind": "discover_from_pool",
                    "target": "none",
                    "random": true,
                    "pool_selection": "discover",
                    "pool_destination": "hand",
                    "with_replacement": false,
                    "pool": {
                        "source": "current_format",
                        "cost_min": 8,
                        "cost_max": 8,
                        "card_types": ["SPELL"],
                        "class_mode": "specific",
                        "class_ids": [4],
                        "spell_school_ids": [1]
                    }
                }]
            }]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {"entity_id": "oh", "card_id": "HERO_01", "card_type": "HERO", "health": 30}
        }
    }))
    .expect("discover state");
    state.validate().expect("valid discover state");
    let assessment = bundle.resolve_state_effect_pools(&mut state);
    assert_eq!(assessment["resolved_effect_count"], 1);
    let action = Action::new(ActionKind::PlayCard, "source", "", "DISCOVER_SOURCE");
    let outcomes = apply_action_outcomes(&state, &action).expect("discover outcomes");
    assert_eq!(outcomes.len(), 1);
    assert_eq!(outcomes[0].probability.numerator, 1);
    assert_eq!(outcomes[0].probability.denominator, 1);
    assert_eq!(outcomes[0].state.friendly.hand.len(), 1);
    assert_eq!(
        outcomes[0].state.friendly.hand[0].card_id.as_ref(),
        "STD_CARD"
    );
}

#[test]
fn complete_owner_deck_without_a_filtered_match_resolves_as_a_certain_no_op() {
    let temporary = TempDirectory::new("filtered-draw-empty");
    build_bundle(temporary.root());
    let bundle = OfficialCardPoolBundle::load_with_context(
        temporary.root(),
        Some(&temporary.card_defs()),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .expect("load filtered-draw fixture");
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "filtered-draw-empty",
        "turn": 1,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "mode": "Ranked",
        "friendly": {
            "player_id": "friendly",
            "hero": {
                "entity_id": "fh",
                "card_id": "HERO_08",
                "card_type": "HERO",
                "health": 30,
                "tags": {"CLASS": 4}
            },
            "mana": 1,
            "max_mana": 1,
            "deck_size": 1,
            "deck_identity_complete": true,
            "known_deck": [{
                "card_id": "STD_CARD",
                "count": 1,
                "origin": "started_in_deck",
                "card_type": "SPELL",
                "cost": 8,
                "name": "Standard Spell"
            }],
            "hand": [{
                "entity_id": "filtered-draw",
                "card_id": "FILTERED_DRAW",
                "name": "Draw a minion",
                "card_type": "SPELL",
                "cost": 1,
                "effect_coverage": "generic",
                "effects": [{
                    "kind": "draw_from_pool",
                    "target": "none",
                    "count": 1,
                    "random": true,
                    "pool_selection": "uniform_random",
                    "pool_destination": "hand",
                    "offer_count": 1,
                    "with_replacement": false,
                    "pool": {
                        "source": "owner_deck",
                        "card_types": ["MINION"]
                    }
                }]
            }]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {
                "entity_id": "oh",
                "card_id": "HERO_01",
                "card_type": "HERO",
                "health": 30
            }
        }
    }))
    .expect("filtered-draw state");
    state.validate().expect("valid filtered-draw state");

    let assessment = bundle.resolve_state_effect_pools(&mut state);
    assert_eq!(assessment["resolved_effect_count"], 1);
    assert_eq!(assessment["exact_effect_count"], 1);
    assert_eq!(assessment["total_candidate_population"], 0);
    let effect = &state.friendly.hand[0].effects[0];
    assert!(effect.resolved_pool.is_empty());
    assert_eq!(effect.resolved_pool_population, 0);
    assert!(effect.resolved_pool_exact);

    let action = Action::new(ActionKind::PlayCard, "filtered-draw", "", "FILTERED_DRAW");
    let outcomes = apply_action_outcomes(&state, &action).expect("empty filtered draw outcome");
    assert_eq!(outcomes.len(), 1);
    assert_eq!(outcomes[0].probability.numerator, 1);
    assert_eq!(outcomes[0].probability.denominator, 1);
    assert!(outcomes[0].state.friendly.hand.is_empty());
    assert_eq!(outcomes[0].state.friendly.deck_size, 1);
    assert_eq!(outcomes[0].state.friendly.known_deck.len(), 1);
    assert_eq!(
        outcomes[0].state.friendly.known_deck[0].card_id.as_ref(),
        "STD_CARD"
    );
}

#[test]
fn hash_bound_runed_orb_rule_reaches_the_real_discover_chance_transition() {
    let temporary = TempDirectory::new("runed-orb-generation-rule");
    build_bundle(temporary.root());
    let bundle = OfficialCardPoolBundle::load_with_context(
        temporary.root(),
        Some(&temporary.card_defs()),
        Duration::from_secs(72 * 3600),
        fixed_now(),
    )
    .expect("load Runed Orb fixture");
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "runed-orb",
        "turn": 1,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "mode": "Ranked",
        "friendly": {
            "player_id": "friendly",
            "hero": {
                "entity_id": "fh",
                "card_id": "HERO_08",
                "card_type": "HERO",
                "health": 30,
                "tags": {"CLASS": 4}
            },
            "mana": 1,
            "max_mana": 1,
            "hand": [{
                "entity_id": "runed-orb",
                "card_id": "BAR_541",
                "name": "Runed Orb",
                "card_type": "SPELL",
                "cost": 1,
                "card_text": "Deal 2 damage. Discover a spell.",
                "effect_coverage": "unsupported",
                "unsupported_effects": ["card_text_not_parsed"]
            }]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {
                "entity_id": "oh",
                "card_id": "HERO_01",
                "card_type": "HERO",
                "health": 30
            }
        }
    }))
    .expect("Runed Orb state");
    state.validate().expect("valid Runed Orb state");

    let point_assessment = apply_embedded_rules(&mut state).expect("point rule");
    assert!(point_assessment.matched.is_empty());
    let generation_assessment =
        apply_embedded_generation_rules(&mut state).expect("generation rule");
    assert_eq!(generation_assessment.matched.len(), 1);
    assert_eq!(state.friendly.hand[0].effects.len(), 2);

    let pool_assessment = bundle.resolve_state_effect_pools(&mut state);
    assert_eq!(pool_assessment["resolved_effect_count"], 1);
    let action = Action::new(ActionKind::PlayCard, "runed-orb", "oh", "BAR_541");
    let outcomes = apply_action_outcomes(&state, &action).expect("Runed Orb outcomes");
    assert_eq!(outcomes.len(), 1);
    assert_eq!(outcomes[0].probability.numerator, 1);
    assert_eq!(outcomes[0].probability.denominator, 1);
    assert_eq!(outcomes[0].state.opponent.hero.current_health, 28);
    assert_eq!(outcomes[0].state.friendly.hand.len(), 1);
    assert_eq!(
        outcomes[0].state.friendly.hand[0].card_id.as_ref(),
        "STD_CARD"
    );
}
