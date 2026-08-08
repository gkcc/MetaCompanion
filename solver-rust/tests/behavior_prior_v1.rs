use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, atomic::AtomicBool};

use metacompanion_solver::behavior_prior::{
    BehaviorPrior, BehaviorPriorError, BehaviorPriorManager, BehaviorPriorRuntime,
};
use metacompanion_solver::model::{Action, GameState};
use metacompanion_solver::turnpair::{
    MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, SearchControl, prove_scoped_lethal_with_control,
    prove_turnpair_with_control, ranked_lines,
};
use serde_json::{Value, json};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TempDirectory(PathBuf);

impl TempDirectory {
    fn new() -> Self {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "metacompanion-behavior-prior-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create behavior-prior integration directory");
        Self(path)
    }

    fn artifact(&self) -> PathBuf {
        self.0.join("behavior-prior-v1.json")
    }

    fn scoring_request(&self) -> PathBuf {
        self.0.join("scoring-request.json")
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn repository_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("repository root")
        .to_path_buf()
}

fn python_root() -> PathBuf {
    repository_root().join("solver")
}

fn train_fixture(output: &Path) {
    let root = repository_root();
    let solver = root.join("solver");
    let result = Command::new("python")
        .arg(solver.join("launch_solver.py"))
        .args([
            "train-behavior-prior",
            "--input",
            solver
                .join("fixtures/behavior-prior-readiness-v1.jsonl")
                .to_str()
                .expect("dataset path"),
            "--manifest",
            solver
                .join("fixtures/behavior-prior-readiness-v1.manifest.json")
                .to_str()
                .expect("manifest path"),
            "--policy",
            solver
                .join("fixtures/behavior-prior-readiness-policy-v1.json")
                .to_str()
                .expect("policy path"),
            "--output",
            output.to_str().expect("output path"),
        ])
        .env("PYTHONPATH", python_root())
        .output()
        .expect("run Python behavior-prior trainer");
    assert!(
        result.status.success(),
        "fixture trainer failed: {}",
        String::from_utf8_lossy(&result.stderr)
    );
}

fn card(entity_id: &str, card_id: &str, card_type: &str) -> Value {
    json!({
        "entity_id": entity_id,
        "card_id": card_id,
        "name": card_id,
        "card_type": card_type,
        "health": if card_type == "HERO" { 30 } else { 1 },
        "current_health": if card_type == "HERO" { 30 } else { 1 },
        "attack": if card_type == "MINION" { 1 } else { 0 },
        "playable": true,
        "can_attack": card_type == "MINION",
        "effect_coverage": "exact"
    })
}

fn state_value(patch: &str) -> Value {
    json!({
        "state_id": "behavior-prior-state",
        "turn": 5,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "patch": patch,
        "mode": "standard",
        "friendly": {
            "player_id": "friendly",
            "hero": card("friendly-hero", "F_HERO", "HERO"),
            "mana": 5,
            "max_mana": 5,
            "hand": [card("friendly-hand", "F_HAND", "SPELL")],
            "board": [card("friendly-minion", "F_MINION", "MINION")],
            "hero_power": card("friendly-power", "F_POWER", "HERO_POWER"),
            "hero_power_available": true
        },
        "opponent": {
            "player_id": "opponent",
            "hero": card("opponent-hero", "O_HERO", "HERO"),
            "board": [card("opponent-minion", "O_MINION", "MINION")]
        }
    })
}

fn actions_value() -> Value {
    json!([
        {
            "kind": "attack",
            "source_entity_id": "friendly-minion",
            "target_entity_id": "opponent-hero",
            "card_id": "F_MINION"
        },
        {
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": ""
        },
        {
            "kind": "hero_power",
            "source_entity_id": "friendly-power",
            "target_entity_id": "opponent-hero",
            "card_id": "F_POWER"
        },
        {
            "kind": "play_card",
            "source_entity_id": "friendly-hand",
            "target_entity_id": "",
            "card_id": "F_HAND"
        }
    ])
}

fn state_and_actions(patch: &str) -> (Value, GameState, Vec<Action>) {
    let state_raw = state_value(patch);
    let mut state: GameState =
        serde_json::from_value(state_raw.clone()).expect("behavior-prior state");
    state.validate().expect("valid behavior-prior state");
    let actions: Vec<Action> =
        serde_json::from_value(actions_value()).expect("behavior-prior actions");
    (state_raw, state, actions)
}

#[test]
fn rust_scoring_matches_the_python_reference_and_only_reorders_legal_inputs() {
    let temporary = TempDirectory::new();
    train_fixture(&temporary.artifact());
    let prior = BehaviorPrior::load(&temporary.artifact()).expect("load ready prior");
    let (state_raw, state, mut actions) = state_and_actions("fixture-patch");
    let rust = prior.score_actions(&state, &actions).expect("Rust scores");

    let request = json!({"pre_state": state_raw, "actions": actions_value()});
    fs::write(
        temporary.scoring_request(),
        serde_json::to_vec(&request).expect("serialize scoring request"),
    )
    .expect("write scoring request");
    let script = r#"
import json, sys
from metacompanion_solver.behavior_prior import load_behavior_prior, score_legal_behavior_candidates
with open(sys.argv[2], 'r', encoding='utf-8') as handle:
    request = json.load(handle)
artifact = load_behavior_prior(sys.argv[1])
scores = score_legal_behavior_candidates(
    artifact,
    pre_state=request['pre_state'],
    actor_side='local',
    actor_player_id='friendly',
    actions=request['actions'],
)
print(json.dumps(scores, separators=(',', ':')))
"#;
    let output = Command::new("python")
        .args([
            "-c",
            script,
            temporary.artifact().to_str().expect("artifact path"),
            temporary.scoring_request().to_str().expect("request path"),
        ])
        .env("PYTHONPATH", python_root())
        .output()
        .expect("run Python scorer");
    assert!(
        output.status.success(),
        "Python scorer failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    let python: Vec<f64> = serde_json::from_slice(&output.stdout).expect("Python scores");
    assert_eq!(rust.len(), python.len());
    for (rust_score, python_score) in rust.iter().zip(python) {
        assert!((rust_score - python_score).abs() <= 1e-12);
    }

    let before = actions.iter().map(Action::action_id).collect::<Vec<_>>();
    assert!(
        prior
            .order_actions(&state, &mut actions)
            .expect("order actions")
    );
    let after = actions.iter().map(Action::action_id).collect::<Vec<_>>();
    assert_eq!(before.len(), after.len());
    assert_eq!(
        before
            .iter()
            .cloned()
            .collect::<std::collections::BTreeSet<_>>(),
        after
            .iter()
            .cloned()
            .collect::<std::collections::BTreeSet<_>>()
    );
    assert_eq!(actions[0].kind.as_str(), "play_card");
}

#[test]
fn unsupported_patch_is_uniform_and_cannot_bias_search() {
    let temporary = TempDirectory::new();
    train_fixture(&temporary.artifact());
    let prior = BehaviorPrior::load(&temporary.artifact()).expect("load ready prior");
    let (_, state, mut actions) = state_and_actions("different-patch");
    let scores = prior
        .score_actions(&state, &actions)
        .expect("uniform scores");
    assert!(
        scores
            .iter()
            .all(|score| (*score - 1.0 / actions.len() as f64).abs() <= 1e-12)
    );
    assert!(
        !prior
            .order_actions(&state, &mut actions)
            .expect("no ordering")
    );
}

#[test]
fn tampered_permissions_quality_and_counts_are_rejected() {
    let temporary = TempDirectory::new();
    train_fixture(&temporary.artifact());
    let original: Value =
        serde_json::from_slice(&fs::read(temporary.artifact()).expect("read trained artifact"))
            .expect("parse trained artifact");

    let mut permissions = original.clone();
    permissions["live_policy_eligible"] = json!(true);
    assert!(matches!(
        BehaviorPrior::from_slice(&serde_json::to_vec(&permissions).unwrap()),
        Err(BehaviorPriorError::Contract(_))
    ));

    let mut quality = original.clone();
    quality["quality_checks"][0]["passed"] = json!(false);
    assert!(matches!(
        BehaviorPrior::from_slice(&serde_json::to_vec(&quality).unwrap()),
        Err(BehaviorPriorError::NotReady)
    ));

    let mut counts = original;
    counts["models"]["action_kind"]["counts_by_level"]["global"]["[]"]["total"] = json!(999);
    assert!(matches!(
        BehaviorPrior::from_slice(&serde_json::to_vec(&counts).unwrap()),
        Err(BehaviorPriorError::Contract(_))
    ));
}

#[test]
fn legacy_v1_artifact_is_rejected() {
    let temporary = TempDirectory::new();
    train_fixture(&temporary.artifact());
    let mut legacy: Value =
        serde_json::from_slice(&fs::read(temporary.artifact()).expect("read trained artifact"))
            .expect("parse trained artifact");
    legacy["schema"] = json!("behavior-imitation-prior-v1");

    assert!(matches!(
        BehaviorPrior::from_slice(&serde_json::to_vec(&legacy).expect("serialize legacy artifact")),
        Err(BehaviorPriorError::Contract(_))
    ));
}

#[test]
fn runtime_rejection_is_safe_and_does_not_expose_a_path() {
    let temporary = TempDirectory::new();
    fs::write(temporary.artifact(), b"{not-json").expect("write invalid model");
    let runtime = BehaviorPriorRuntime::load(Some(&temporary.artifact()));
    let health = runtime.health_payload();
    assert_eq!(health["available"], false);
    assert_eq!(health["status"], "rejected");
    assert_eq!(health["rejection_code"], "invalid_json");
    let serialized = serde_json::to_string(&health).expect("serialize health");
    assert!(!serialized.contains(temporary.0.to_string_lossy().as_ref()));
}

#[test]
fn manager_hot_reloads_atomic_model_changes_without_restarting_the_solver() {
    let temporary = TempDirectory::new();
    let manager = BehaviorPriorManager::new(Some(temporary.artifact()));
    assert_eq!(manager.health_payload()["status"], "not_found");
    assert!(manager.model().is_none());

    train_fixture(&temporary.artifact());
    assert!(manager.model().is_some());
    assert_eq!(manager.health_payload()["status"], "ready");

    fs::write(temporary.artifact(), b"{invalid-json").expect("replace with invalid model");
    assert!(manager.model().is_none());
    assert_eq!(manager.health_payload()["status"], "rejected");

    train_fixture(&temporary.artifact());
    assert!(manager.model().is_some());
    assert_eq!(manager.health_payload()["status"], "ready");
}

#[test]
fn opponent_search_uses_the_prior_only_for_ordering_and_preserves_exhaustive_results() {
    let temporary = TempDirectory::new();
    train_fixture(&temporary.artifact());
    let prior = Arc::new(BehaviorPrior::load(&temporary.artifact()).expect("load ready prior"));
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "exhaustive-ordering",
        "turn": 5,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "patch": "fixture-patch",
        "mode": "standard",
        "friendly": {
            "player_id": "friendly",
            "hero": card("friendly-hero", "F_HERO", "HERO"),
            "mana": 5,
            "max_mana": 5,
            "board": [{
                "entity_id": "attacker",
                "card_id": "F_MINION",
                "name": "attacker",
                "card_type": "MINION",
                "attack": 2,
                "health": 2,
                "current_health": 2,
                "can_attack": true,
                "attacks_remaining": 1,
                "effect_coverage": "exact"
            }]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": card("opponent-hero", "O_HERO", "HERO"),
            "board": [{
                "entity_id": "opponent-attacker",
                "card_id": "O_MINION",
                "name": "opponent attacker",
                "card_type": "MINION",
                "attack": 1,
                "health": 2,
                "current_health": 2,
                "can_attack": false,
                "attacks_remaining": 0,
                "effect_coverage": "exact"
            }]
        }
    }))
    .expect("exhaustive state");
    state.validate().expect("valid exhaustive state");
    let cancel = AtomicBool::new(false);

    let mut baseline_control = SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None);
    let baseline =
        prove_turnpair_with_control(&state, false, MAX_LINE_DEPTH, &mut baseline_control)
            .expect("baseline proof");

    let mut prior_control =
        SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None).with_behavior_prior(Some(prior));
    let reordered = prove_turnpair_with_control(&state, false, MAX_LINE_DEPTH, &mut prior_control)
        .expect("prior-ordered proof");
    assert!(prior_control.behavior_prior_available());
    assert!(prior_control.behavior_prior_applied());
    assert!(prior_control.behavior_prior_ordering_attempts() > 0);
    assert!(!prior_control.behavior_prior_runtime_rejected());
    assert_eq!(baseline.optimal_value, reordered.optimal_value);
    assert_eq!(
        baseline.root_action_coverage.legal_first_action_ids,
        reordered.root_action_coverage.legal_first_action_ids
    );
    let baseline_lines = ranked_lines(&baseline, 3)
        .into_iter()
        .map(|line| (line.first_action_id(), line.minimax_value))
        .collect::<Vec<_>>();
    let reordered_lines = ranked_lines(&reordered, 3)
        .into_iter()
        .map(|line| (line.first_action_id(), line.minimax_value))
        .collect::<Vec<_>>();
    assert_eq!(baseline_lines, reordered_lines);
}

#[test]
fn local_scoped_lethal_never_falls_back_to_the_opponent_behavior_prior() {
    let temporary = TempDirectory::new();
    train_fixture(&temporary.artifact());
    let prior = Arc::new(BehaviorPrior::load(&temporary.artifact()).expect("load ready prior"));
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "local-prior-negative-control",
        "turn": 5,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "patch": "fixture-patch",
        "mode": "standard",
        "friendly": {
            "player_id": "friendly",
            "hero": card("friendly-hero", "F_HERO", "HERO"),
            "board": [{
                "entity_id": "lethal-attacker",
                "card_id": "F_MINION",
                "name": "lethal attacker",
                "card_type": "MINION",
                "attack": 3,
                "health": 2,
                "current_health": 2,
                "can_attack": true,
                "attacks_remaining": 1,
                "effect_coverage": "exact"
            }]
        },
        "opponent": {
            "player_id": "opponent",
            "hero": {
                "entity_id": "opponent-hero",
                "card_id": "O_HERO",
                "name": "opponent hero",
                "card_type": "HERO",
                "health": 2,
                "current_health": 2,
                "effect_coverage": "exact"
            }
        }
    }))
    .expect("local negative-control state");
    state
        .validate()
        .expect("valid local negative-control state");
    let cancel = AtomicBool::new(false);
    let mut control =
        SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None).with_behavior_prior(Some(prior));

    let lethal = prove_scoped_lethal_with_control(&state, MAX_LINE_DEPTH, &mut control)
        .expect("scoped lethal search");
    assert!(lethal.is_some());
    assert!(control.behavior_prior_available());
    assert_eq!(control.behavior_prior_ordering_attempts(), 0);
    assert!(!control.behavior_prior_applied());
    assert!(!control.behavior_prior_runtime_rejected());
}
