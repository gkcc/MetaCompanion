use std::fs;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, atomic::AtomicBool};

use metacompanion_solver::decision_ranker::{
    DecisionRanker, DecisionRankerError, DecisionRankerManager, DecisionRankerRuntime,
};
use metacompanion_solver::model::{Action, GameState};
use metacompanion_solver::turnpair::{
    MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, SearchControl, prove_turnpair_with_control, ranked_lines,
};
use serde_json::{Value, json};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TempDirectory(PathBuf);

impl TempDirectory {
    fn new() -> Self {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "metacompanion-decision-ranker-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create decision-ranker integration directory");
        Self(path)
    }

    fn artifact(&self) -> PathBuf {
        self.0.join("decision-ranker-v1.json")
    }

    fn behavior(&self) -> PathBuf {
        self.0.join("behavior-v1.jsonl")
    }

    fn frames(&self) -> PathBuf {
        self.0.join("advisor-decision-frame-v1.jsonl")
    }

    fn policy(&self) -> PathBuf {
        self.0.join("decision-ranker-policy.json")
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

fn prepare_fixture(directory: &TempDirectory) {
    let root = repository_root();
    let script = r#"
import sys
from pathlib import Path
sys.path.insert(0, sys.argv[2])
from test_decision_ranker import _prepare
_prepare(Path(sys.argv[1]))
"#;
    let prepare = Command::new("python")
        .args([
            "-c",
            script,
            directory.0.to_str().expect("fixture directory"),
            root.join("solver/tests").to_str().expect("test directory"),
        ])
        .env("PYTHONPATH", python_root())
        .output()
        .expect("prepare decision-ranker fixture");
    assert!(
        prepare.status.success(),
        "fixture preparation failed: {}",
        String::from_utf8_lossy(&prepare.stderr)
    );
    let train = Command::new("python")
        .arg(root.join("solver/launch_solver.py"))
        .args([
            "train-decision-ranker",
            "--decision-frames",
            directory.frames().to_str().expect("frame path"),
            "--behavior",
            directory.behavior().to_str().expect("behavior path"),
            "--policy",
            directory.policy().to_str().expect("policy path"),
            "--epochs",
            "1",
            "--output",
            directory.artifact().to_str().expect("artifact path"),
        ])
        .env("PYTHONPATH", python_root())
        .output()
        .expect("run Python decision-ranker trainer");
    assert!(
        train.status.success(),
        "fixture trainer failed: {}",
        String::from_utf8_lossy(&train.stderr)
    );
}

fn first_state_and_actions(directory: &TempDirectory) -> (Value, GameState, Vec<Action>) {
    let first = fs::read_to_string(directory.frames())
        .expect("read decision frames")
        .lines()
        .next()
        .map(str::to_owned)
        .expect("first decision frame");
    let frame: Value = serde_json::from_str(&first).expect("parse decision frame");
    let state_raw = frame["pre_state"].clone();
    let mut state: GameState =
        serde_json::from_value(state_raw.clone()).expect("parse decision-ranker state");
    state.validate().expect("valid decision-ranker state");
    let actions = frame["legal_candidates"]
        .as_array()
        .expect("candidate array")
        .iter()
        .map(|candidate| {
            serde_json::from_value(candidate["action"].clone()).expect("candidate action")
        })
        .collect::<Vec<_>>();
    (state_raw, state, actions)
}

fn card(entity_id: &str, card_id: &str, card_type: &str) -> Value {
    json!({
        "entity_id": entity_id,
        "card_id": card_id,
        "name": card_id,
        "card_type": card_type,
        "health": if card_type == "HERO" { 30 } else { 2 },
        "current_health": if card_type == "HERO" { 30 } else { 2 },
        "attack": if card_type == "MINION" { 2 } else { 0 },
        "effect_coverage": "exact"
    })
}

#[test]
fn rust_scoring_matches_python_and_only_reorders_supplied_candidates() {
    let temporary = TempDirectory::new();
    prepare_fixture(&temporary);
    let ranker = DecisionRanker::load(&temporary.artifact()).expect("load ready ranker");
    let (state_raw, state, mut actions) = first_state_and_actions(&temporary);
    let rust = ranker.score_actions(&state, &actions).expect("Rust scores");

    let request = json!({
        "pre_state": state_raw,
        "mode": state.mode,
        "actions": actions,
    });
    fs::write(
        temporary.scoring_request(),
        serde_json::to_vec(&request).expect("serialize scoring request"),
    )
    .expect("write scoring request");
    let script = r#"
import json, sys
from metacompanion_solver.decision_ranker import load_decision_ranker, score_legal_decision_candidates
with open(sys.argv[2], 'r', encoding='utf-8') as handle:
    request = json.load(handle)
artifact = load_decision_ranker(sys.argv[1])
scores = score_legal_decision_candidates(
    artifact,
    pre_state=request['pre_state'],
    mode=request['mode'],
    actions=request['actions'],
)
print(json.dumps(scores, separators=(',', ':')))
"#;
    let output = Command::new("python")
        .args([
            "-c",
            script,
            temporary.artifact().to_str().expect("artifact path"),
            temporary
                .scoring_request()
                .to_str()
                .expect("scoring request"),
        ])
        .env("PYTHONPATH", python_root())
        .output()
        .expect("run Python decision-ranker scorer");
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
        ranker
            .order_actions(&state, &mut actions)
            .expect("order actions")
    );
    let after = actions.iter().map(Action::action_id).collect::<Vec<_>>();
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
}

#[test]
fn unsupported_patch_is_uniform_and_opponent_turn_is_rejected() {
    let temporary = TempDirectory::new();
    prepare_fixture(&temporary);
    let ranker = DecisionRanker::load(&temporary.artifact()).expect("load ready ranker");
    let (_, mut state, actions) = first_state_and_actions(&temporary);
    state.patch = "different-patch".into();
    let scores = ranker
        .score_actions(&state, &actions)
        .expect("uniform scores");
    assert!(
        scores
            .iter()
            .all(|score| (*score - 1.0 / actions.len() as f64).abs() <= 1e-12)
    );
    state.patch = "fixture-patch".into();
    state.active_player_id = state.opponent.player_id.clone();
    assert!(matches!(
        ranker.score_actions(&state, &actions),
        Err(DecisionRankerError::Contract(_))
    ));
}

#[test]
fn tampered_permissions_quality_weights_and_privacy_are_rejected() {
    let temporary = TempDirectory::new();
    prepare_fixture(&temporary);
    let original: Value =
        serde_json::from_slice(&fs::read(temporary.artifact()).expect("read trained ranker"))
            .expect("parse trained ranker");

    let mut permissions = original.clone();
    permissions["rl_training_eligible"] = json!(true);
    assert!(matches!(
        DecisionRanker::from_slice(&serde_json::to_vec(&permissions).unwrap()),
        Err(DecisionRankerError::Contract(_))
    ));

    let mut behavior_reference = original.clone();
    behavior_reference["user_visible_behavior_reference_eligible"] = json!(false);
    assert!(matches!(
        DecisionRanker::from_slice(&serde_json::to_vec(&behavior_reference).unwrap()),
        Err(DecisionRankerError::Contract(_))
    ));

    let mut quality = original.clone();
    quality["quality_checks"][0]["passed"] = json!(false);
    assert!(matches!(
        DecisionRanker::from_slice(&serde_json::to_vec(&quality).unwrap()),
        Err(DecisionRankerError::Contract(_))
    ));

    let mut weights = original.clone();
    weights["model"]["weight_count"] = json!(999);
    assert!(matches!(
        DecisionRanker::from_slice(&serde_json::to_vec(&weights).unwrap()),
        Err(DecisionRankerError::Contract(_))
    ));

    let mut privacy = original;
    privacy["caveat_zh"] = json!("anon-1111111111111111");
    assert!(matches!(
        DecisionRanker::from_slice(&serde_json::to_vec(&privacy).unwrap()),
        Err(DecisionRankerError::Contract(_))
    ));
}

#[test]
fn runtime_and_manager_fail_closed_and_hot_reload() {
    let temporary = TempDirectory::new();
    fs::write(temporary.artifact(), b"{not-json").expect("write invalid model");
    let runtime = DecisionRankerRuntime::load(Some(&temporary.artifact()));
    let health = runtime.health_payload();
    assert_eq!(health["available"], false);
    assert_eq!(health["status"], "rejected");
    assert_eq!(health["rejection_code"], "invalid_json");
    assert!(
        !serde_json::to_string(&health)
            .expect("serialize health")
            .contains(temporary.0.to_string_lossy().as_ref())
    );

    let manager = DecisionRankerManager::new(Some(temporary.artifact()));
    assert!(manager.model().is_none());
    prepare_fixture(&temporary);
    assert!(manager.model().is_some());
    assert_eq!(manager.health_payload()["status"], "ready");
    fs::write(temporary.artifact(), b"{invalid-json").expect("replace model");
    assert!(manager.model().is_none());
    assert_eq!(manager.health_payload()["status"], "rejected");
}

#[test]
fn real_trained_artifact_contains_no_private_identifiers() {
    let temporary = TempDirectory::new();
    prepare_fixture(&temporary);
    let payload = fs::read_to_string(temporary.artifact()).expect("read ranker");
    for forbidden in ["anon-", "\"game_id\"", "\"state_id\"", "\"entity_id\""] {
        assert!(!payload.contains(forbidden), "found forbidden {forbidden}");
    }
}

#[test]
fn local_ranker_only_reorders_search_and_preserves_exhaustive_tactical_results() {
    let temporary = TempDirectory::new();
    prepare_fixture(&temporary);
    let ranker = Arc::new(DecisionRanker::load(&temporary.artifact()).expect("load ready ranker"));
    let mut state: GameState = serde_json::from_value(json!({
        "state_id": "decision-ranker-exhaustive-ordering",
        "turn": 5,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "patch": "fixture-patch",
        "mode": "standard",
        "friendly": {
            "player_id": "friendly",
            "hero": card("friendly-hero", "F_HERO", "HERO"),
            "board": [{
                "entity_id": "friendly-attacker",
                "card_id": "F_MINION",
                "name": "friendly attacker",
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
    .expect("ranker exhaustive state");
    state.validate().expect("valid ranker exhaustive state");
    let cancel = AtomicBool::new(false);

    let mut baseline_control = SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None);
    let baseline =
        prove_turnpair_with_control(&state, false, MAX_LINE_DEPTH, &mut baseline_control)
            .expect("baseline proof");

    let mut ranker_control =
        SearchControl::new(&cancel, MAX_ENUMERATED_NODES, None).with_decision_ranker(Some(ranker));
    let reordered = prove_turnpair_with_control(&state, false, MAX_LINE_DEPTH, &mut ranker_control)
        .expect("ranker-ordered proof");
    assert!(ranker_control.decision_ranker_available());
    assert!(ranker_control.decision_ranker_applied());
    assert!(ranker_control.decision_ranker_ordering_attempts() > 0);
    assert!(!ranker_control.decision_ranker_runtime_rejected());
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
