use std::fs;
use std::path::PathBuf;
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use metacompanion_solver::model::{Action, ActionKind, JsonScalar, SolveRequest};
use metacompanion_solver::oracle::apply_action;
use metacompanion_solver::training_log::{
    TRAINING_LOG_FILENAME, TRAJECTORY_SCHEMA_ID, TrainingLogger,
};
use serde_json::{Value, json};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TempDirectory(PathBuf);

impl TempDirectory {
    fn new() -> Self {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "metacompanion-rust-python-audit-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create integration-test directory");
        Self(path)
    }

    fn log_path(&self) -> PathBuf {
        self.0.join(TRAINING_LOG_FILENAME)
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn solve_request(request_id: &str, state_id: &str, hash: &str, sequence: i64) -> SolveRequest {
    let mut request: SolveRequest = serde_json::from_value(json!({
        "request_id": request_id,
        "state": {
            "state_id": state_id,
            "turn": if state_id == "state-pre" { 1 } else { 2 },
            "active_player_id": if state_id == "state-pre" { "friendly" } else { "opponent" },
            "perspective_player_id": "friendly",
            "friendly": {
                "player_id": "friendly",
                "hero": {"entity_id": "1", "card_type": "HERO", "health": 30}
            },
            "opponent": {
                "player_id": "opponent",
                "hero": {"entity_id": "2", "card_type": "HERO", "health": 30}
            },
            "patch": "31.6",
            "mode": "standard",
            "metadata": {
                "game_id": "private-integration-game",
                "snapshot_state_hash": hash,
                "snapshot_sequence": sequence,
                "adapter": "hdt-snapshot-v1"
            }
        },
        "metadata": {
            "trajectory_schema": TRAJECTORY_SCHEMA_ID,
            "decision_id": state_id,
            "solve_stage": "single",
            "snapshot_sequence": sequence.to_string(),
            "capture_contract": "hdt-public-snapshot-v1"
        }
    }))
    .expect("integration solve request");
    request.validate().expect("valid integration request");
    request
}

fn solve_result(request: &SolveRequest) -> Value {
    json!({
        "api_version": "1.0",
        "schema_version": 1,
        "request_id": request.request_id,
        "state_id": request.state.state_id,
        "status": "ok",
        "elapsed_ms": 1,
        "iterations": 1,
        "recommendations": [],
        "progress": [],
        "coverage": {
            "planner_model": "rust-turnpair-v1",
            "rules_model": "hdt-visible-point-effects-v1",
            "exact": true
        }
    })
}

fn power_identity_observation(pre: &SolveRequest, post: &SolveRequest) -> Value {
    json!({
        "api_version": "1.0",
        "kind": "action",
        "state_id": "state-pre",
        "game_id": "private-integration-game",
        "action": {
            "action_id": "end_turn::",
            "kind": "end_turn",
            "source_entity_id": null,
            "target_entity_id": null,
            "card_id": "",
            "sub_option": -1,
            "board_position": 0,
            "option_id": 0,
            "frame_id": 42,
            "power_start_watermark": "g7:120",
            "power_end_watermark": "g7:128",
            "choices": []
        },
        "pre_state": serde_json::to_value(&pre.state).expect("pre-state value"),
        "post_state": serde_json::to_value(&post.state).expect("post-state value"),
        "result": "",
        "metadata": {
            "trajectory_schema": TRAJECTORY_SCHEMA_ID,
            "decision_id": "state-pre",
            "action_sequence": "1",
            "pre_state_id": "state-pre",
            "post_state_id": "state-post",
            "raw_pre_snapshot_hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "raw_post_snapshot_hash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "pre_snapshot_sequence": "10",
            "post_snapshot_sequence": "11",
            "boundary_status": "isolated",
            "intervening_action_count": "0",
            "capture_warning_count": "0",
            "game_generation": "7",
            "power_collector_epoch": "7",
            "power_action_ordinal": "1",
            "power_gap_count": "0",
            "capture_contract": "hdt_power_action_identity_v1",
            "transition_status": "post_state_candidate_unverified",
            "transition_verification": "producer_candidate_unverified",
            "completeness": "exact_action_identity_unverified_transition_v1",
            "action_identity_status": "exact_hdt_power_v1",
            "choice_status": "none",
            "simulator_status": "not_replayed",
            "source_entity_resolution": "not_applicable",
            "target_entity_resolution": "not_applicable",
            "training_eligible": false
        }
    })
}

#[test]
fn rust_and_python_round_trip_the_same_power_identity_wire_fields() {
    let temporary = TempDirectory::new();
    let logger = TrainingLogger::new(Some(temporary.log_path()));
    let pre = solve_request("request-pre", "state-pre", &"a".repeat(64), 10);
    let post = solve_request("request-post", "state-post", &"b".repeat(64), 11);
    assert!(logger.append_solve(&pre, &solve_result(&pre)));
    let observation = power_identity_observation(&pre, &post);
    assert!(
        logger
            .append_observation(observation.clone())
            .expect("Rust accepts Power identity wire")
            .logged
    );
    assert!(logger.append_solve(&post, &solve_result(&post)));
    assert!(
        logger
            .append_observation(json!({
                "api_version": "1.0",
                "kind": "result",
                "state_id": "state-post",
                "game_id": "private-integration-game",
                "result": "win",
                "metadata": {
                    "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                    "decision_id": "state-post",
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": true,
                    "game_generation": "7",
                    "power_collector_epoch": "7",
                    "power_committed_action_count": "1",
                    "power_recorded_action_count": "1",
                    "power_gap_count": "0",
                    "power_trace_status": "complete"
                }
            }))
            .expect("valid terminal observation")
            .logged
    );
    let wire_path = temporary.0.join("power-observation.json");
    fs::write(
        &wire_path,
        serde_json::to_vec(&observation).expect("serialize Power identity wire"),
    )
    .expect("write cross-language wire fixture");

    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let python_root = manifest_dir
        .parent()
        .expect("workspace directory")
        .join("solver");
    let script = r#"
import json
import sys
from metacompanion_solver.schemas import Observation
from metacompanion_solver.trajectory import audit_trajectory_file

with open(sys.argv[1], encoding="utf-8") as handle:
    raw = json.load(handle)
wire = Observation.from_dict(raw).to_dict()["action"]
expected = {
    "sub_option": -1,
    "board_position": 0,
    "option_id": "0",
    "frame_id": "42",
    "power_start_watermark": "g7:120",
    "power_end_watermark": "g7:128",
    "choices": [],
}
for key, value in expected.items():
    if wire.get(key) != value:
        raise SystemExit(f"{key}: {wire.get(key)!r} != {value!r}")
if wire.get("action_id") != "end_turn::":
    raise SystemExit("canonical action identity changed")
audit = audit_trajectory_file(sys.argv[2])
if not audit["contract_passed"] or audit["training_ready"]:
    raise SystemExit(
        "identity-only Rust corpus crossed the wrong trajectory gate: "
        + json.dumps(audit, sort_keys=True)
    )
metrics = audit["metrics"]
if metrics["candidate_transition_count"] != 1:
    raise SystemExit("Power identity candidate was not recognized")
if metrics["exact_action_count"] != 0 or metrics["replayable_transition_count"] != 0:
    raise SystemExit("Power identity candidate was promoted before offline replay")
"#;
    let output = Command::new("python")
        .arg("-c")
        .arg(script)
        .arg(&wire_path)
        .arg(temporary.log_path())
        .env("PYTHONPATH", python_root)
        .output()
        .expect("launch Python schema round-trip");
    assert!(
        output.status.success(),
        "Python schema round-trip failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    let records = fs::read_to_string(temporary.log_path()).expect("read Rust training record");
    let record: Value = serde_json::from_str(
        records
            .lines()
            .nth(1)
            .expect("Power observation is the second record"),
    )
    .expect("parse Rust Power observation record");
    let action = &record["observation"]["action"];
    assert_eq!(action["frame_id"], "42");
    assert_eq!(action["choices"], json!([]));
    for field in [
        "game_generation",
        "power_collector_epoch",
        "power_action_ordinal",
        "power_gap_count",
    ] {
        assert_eq!(
            record["trajectory"][field],
            record["observation"]["metadata"][field]
        );
    }
    assert_eq!(
        record["observation"]["metadata"]["training_eligible"],
        false
    );
    let terminal: Value = serde_json::from_str(
        records
            .lines()
            .nth(3)
            .expect("terminal result is the fourth record"),
    )
    .expect("parse Rust terminal observation record");
    let terminal_metadata = &terminal["observation"]["metadata"];
    assert_eq!(terminal_metadata["game_generation"], "7");
    assert_eq!(terminal_metadata["power_collector_epoch"], "7");
    assert_eq!(terminal_metadata["power_committed_action_count"], "1");
    assert_eq!(terminal_metadata["power_recorded_action_count"], "1");
    assert_eq!(terminal_metadata["power_gap_count"], "0");
    assert_eq!(terminal_metadata["power_trace_status"], "complete");
}

#[test]
fn rust_candidate_corpus_passes_python_auditor_without_becoming_training_ready() {
    let temporary = TempDirectory::new();
    let logger = TrainingLogger::new(Some(temporary.log_path()));
    let pre_hash = "a".repeat(64);
    let post_hash = "b".repeat(64);
    let pre = solve_request("request-pre", "state-pre", &pre_hash, 10);
    let post = solve_request("request-post", "state-post", &post_hash, 11);
    assert!(logger.append_solve(&pre, &solve_result(&pre)));
    assert!(
        logger
            .append_observation(json!({
                "api_version": "1.0",
                "kind": "action",
                "state_id": "state-pre",
                "game_id": "private-integration-game",
                "action": {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": ""
                },
                "pre_state": serde_json::to_value(&pre.state).expect("pre-state value"),
                "post_state": serde_json::to_value(&post.state).expect("post-state value"),
                "result": "",
                "metadata": {
                    "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                    "decision_id": "state-pre",
                    "action_sequence": "1",
                    "pre_state_id": "state-pre",
                    "post_state_id": "state-post",
                    "raw_pre_snapshot_hash": pre_hash,
                    "raw_post_snapshot_hash": post_hash,
                    "pre_snapshot_sequence": "10",
                    "post_snapshot_sequence": "11",
                    "boundary_status": "isolated",
                    "intervening_action_count": "0",
                    "capture_warning_count": "0",
                    "capture_contract": "partial_hdt_transition_candidate_v1",
                    "transition_status": "post_state_candidate_unverified",
                    "transition_verification": "producer_candidate_unverified",
                    "completeness": "partial_hdt_gameevents_v1",
                    "training_eligible": "false"
                }
            }))
            .expect("valid integration candidate")
            .logged
    );
    assert!(logger.append_solve(&post, &solve_result(&post)));
    assert!(
        logger
            .append_observation(json!({
                "api_version": "1.0",
                "kind": "result",
                "state_id": "state-post",
                "game_id": "private-integration-game",
                "result": "win",
                "metadata": {
                    "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                    "decision_id": "state-post",
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": true
                }
            }))
            .expect("valid integration result")
            .logged
    );

    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let python_root = manifest_dir
        .parent()
        .expect("workspace directory")
        .join("solver");
    let script = r#"
import json
import sys
from metacompanion_solver.trajectory import audit_trajectory_file

audit = audit_trajectory_file(sys.argv[1])
print(json.dumps(audit, sort_keys=True))
if not audit["contract_passed"] or audit["training_ready"]:
    raise SystemExit(2)
metrics = audit["metrics"]
if metrics["candidate_transition_count"] != 1:
    raise SystemExit(3)
if metrics["exact_action_count"] != 0 or metrics["replayable_transition_count"] != 0:
    raise SystemExit(4)
"#;
    let output = Command::new("python")
        .arg("-c")
        .arg(script)
        .arg(temporary.log_path())
        .env("PYTHONPATH", python_root)
        .output()
        .expect("launch Python trajectory auditor");
    assert!(
        output.status.success(),
        "Python audit failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn rust_exact_fixture_can_pass_a_low_threshold_python_readiness_gate() {
    let temporary = TempDirectory::new();
    let logger = TrainingLogger::new(Some(temporary.log_path()));
    let mut pre: SolveRequest = serde_json::from_value(json!({
        "request_id": "exact-pre",
        "state": {
            "state_id": "exact-state-pre",
            "turn": 1,
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": {
                "player_id": "friendly",
                "hero": {"entity_id": "1", "card_type": "HERO", "health": 30},
                "board": [{
                    "entity_id": "3", "card_id": "TEST_MINION", "card_type": "MINION",
                    "attack": 3, "health": 2, "can_attack": true
                }]
            },
            "opponent": {
                "player_id": "opponent",
                "hero": {"entity_id": "2", "card_type": "HERO", "health": 30}
            },
            "patch": "31.6",
            "mode": "standard",
            "metadata": {
                "game_id": "private-exact-game",
                "snapshot_state_hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "snapshot_sequence": 20,
                "adapter": "native-v1"
            }
        },
        "metadata": {
            "trajectory_schema": TRAJECTORY_SCHEMA_ID,
            "decision_id": "exact-state-pre",
            "solve_stage": "single",
            "snapshot_sequence": "20",
            "capture_contract": "synthetic-exact-v1"
        }
    }))
    .expect("exact pre request");
    pre.validate().expect("valid exact pre request");
    let action = Action::new(ActionKind::Attack, "3", "2", "TEST_MINION");
    let (mut post_state, _) = apply_action(&pre.state, &action).expect("apply exact attack");
    post_state.state_id = Arc::from("exact-state-post");
    post_state.metadata.insert(
        Arc::from("snapshot_state_hash"),
        JsonScalar::String(Arc::from(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        )),
    );
    post_state
        .metadata
        .insert(Arc::from("snapshot_sequence"), JsonScalar::Integer(21));
    let mut post = pre.clone();
    post.request_id = Arc::from("exact-post");
    post.state = post_state;
    post.metadata.insert(
        Arc::from("decision_id"),
        JsonScalar::String(Arc::from("exact-state-post")),
    );
    post.metadata.insert(
        Arc::from("snapshot_sequence"),
        JsonScalar::String(Arc::from("21")),
    );

    assert!(logger.append_solve(&pre, &solve_result(&pre)));
    assert!(
        logger
            .append_observation(json!({
                "api_version": "1.0",
                "kind": "action",
                "state_id": "exact-state-pre",
                "game_id": "private-exact-game",
                "action": {
                    "kind": "attack",
                    "source_entity_id": "3",
                    "target_entity_id": "2",
                    "card_id": "TEST_MINION"
                },
                "result": "",
                "metadata": {
                    "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                    "decision_id": "exact-state-pre",
                    "pre_state_id": "exact-state-pre",
                    "post_state_id": "exact-state-post",
                    "action_sequence": 1,
                    "completeness": "complete_action_trace_v1",
                    "capture_contract": TRAJECTORY_SCHEMA_ID,
                    "transition_status": "replayable_exact",
                    "source_entity_resolution": "exact_entity_id",
                    "target_entity_resolution": "exact_entity_id",
                    "training_eligible": true
                }
            }))
            .expect("valid exact observation")
            .logged
    );
    assert!(logger.append_solve(&post, &solve_result(&post)));
    assert!(
        logger
            .append_observation(json!({
                "api_version": "1.0",
                "kind": "result",
                "state_id": "exact-state-post",
                "game_id": "private-exact-game",
                "result": "win",
                "metadata": {
                    "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                    "decision_id": "exact-state-post",
                    "capture_contract": "terminal_result_v1",
                    "completeness": "terminal_result",
                    "training_eligible": true
                }
            }))
            .expect("valid exact result")
            .logged
    );
    let policy_path = temporary.0.join("low-threshold-policy.json");
    fs::write(
        &policy_path,
        serde_json::to_vec_pretty(&json!({
            "schema": "trajectory-readiness-policy-v1",
            "thresholds": {
                "min_unique_games": 1,
                "min_canonical_decisions": 2,
                "min_terminal_result_games": 1,
                "min_solve_result_join_rate": 1.0,
                "min_exact_action_rate": 1.0,
                "min_replayable_transition_rate": 1.0,
                "max_partial_action_rate": 0.0,
                "max_unsupported_solve_rate": 0.0,
                "max_cancelled_solve_rate": 0.0,
                "max_partial_solve_rate": 0.0,
                "max_non_ok_solve_rate": 0.0
            }
        }))
        .expect("serialize readiness policy"),
    )
    .expect("write readiness policy");
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let python_root = manifest_dir
        .parent()
        .expect("workspace directory")
        .join("solver");
    let script = r#"
import json
import sys
from metacompanion_solver.trajectory import audit_trajectory_file

audit = audit_trajectory_file(sys.argv[1], policy_path=sys.argv[2])
print(json.dumps(audit, sort_keys=True))
metrics = audit["metrics"]
if not audit["contract_passed"] or not audit["training_ready"]:
    raise SystemExit(2)
if metrics["exact_action_count"] != 1 or metrics["replayable_transition_count"] != 1:
    raise SystemExit(3)
"#;
    let output = Command::new("python")
        .arg("-c")
        .arg(script)
        .arg(temporary.log_path())
        .arg(policy_path)
        .env("PYTHONPATH", python_root)
        .output()
        .expect("launch Python exact-fixture auditor");
    assert!(
        output.status.success(),
        "Python exact audit failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn solve_request_fixture_keeps_scalar_metadata_types() {
    let request = solve_request("request", "state-pre", &"a".repeat(64), 10);
    assert!(matches!(
        request.state.metadata.get("snapshot_sequence"),
        Some(JsonScalar::Integer(10))
    ));
    assert!(matches!(
        request.metadata.get("snapshot_sequence"),
        Some(JsonScalar::String(value)) if value.as_ref() == "10"
    ));
    assert_eq!(
        request.state.metadata.get("game_id"),
        Some(&JsonScalar::String(Arc::from("private-integration-game")))
    );
}
