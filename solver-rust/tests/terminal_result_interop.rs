use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};

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
            "metacompanion-terminal-result-interop-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create terminal-result integration directory");
        Self(path)
    }

    fn log_path(&self) -> PathBuf {
        self.0.join(TRAINING_LOG_FILENAME)
    }

    fn observation_path(&self) -> PathBuf {
        self.0.join("terminal-observation.json")
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn terminal_observation() -> Value {
    json!({
        "api_version": "1.0",
        "kind": "result",
        "state_id": "terminal-state",
        "game_id": "private-terminal-interoperability-game",
        "observed_at_utc": "2026-07-31T12:34:56+08:00",
        "result": "win",
        "metadata": {
            "trajectory_schema": TRAJECTORY_SCHEMA_ID,
            "decision_id": "terminal-state",
            "capture_contract": "terminal_result_v1",
            "completeness": "terminal_result",
            "training_eligible": true,
            "result_metadata_version": 1,
            "terminal_adjacency": null,
            "game_generation": "7",
            "power_collector_epoch": "7",
            "power_committed_action_count": "4",
            "power_recorded_action_count": "4",
            "power_gap_count": "0",
            "power_trace_status": "complete"
        }
    })
}

fn python_observe(temporary: &TempDirectory, observation: &Value) -> Value {
    fs::write(
        temporary.observation_path(),
        serde_json::to_vec(observation).expect("serialize terminal observation fixture"),
    )
    .expect("write terminal observation fixture");

    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let python_root = manifest_dir
        .parent()
        .expect("workspace directory")
        .join("solver");
    let script = r#"
import json
import sys
from pathlib import Path

from metacompanion_solver.config import SolverConfig
from metacompanion_solver.logging_store import JsonlTrainingLogger
from metacompanion_solver.schemas import Observation
from metacompanion_solver.service import SolverService

log_path = Path(sys.argv[1])
observation_path = Path(sys.argv[2])
observation = Observation.from_dict(
    json.loads(observation_path.read_text(encoding="utf-8"))
)
service = SolverService(
    SolverConfig(training_log_path=str(log_path)),
    logger=JsonlTrainingLogger(log_path),
)
print(json.dumps(service.observe(observation), sort_keys=True, separators=(",", ":")))
"#;
    let output = Command::new("python")
        .arg("-c")
        .arg(script)
        .arg(temporary.log_path())
        .arg(temporary.observation_path())
        .env("PYTHONPATH", python_root)
        .output()
        .expect("launch Python terminal-result observation");
    assert!(
        output.status.success(),
        "Python terminal-result observation failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    serde_json::from_slice(&output.stdout).unwrap_or_else(|error| {
        panic!(
            "parse Python terminal-result ACK: {error}\nstdout: {}\nstderr: {}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        )
    })
}

fn assert_single_terminal_record(path: &Path) {
    let corpus = fs::read_to_string(path).expect("read shared training corpus");
    let records = corpus.lines().collect::<Vec<_>>();
    assert_eq!(records.len(), 1, "terminal retry appended a second line");
    let record: Value = serde_json::from_str(records[0]).expect("parse terminal training record");
    assert_eq!(record["kind"], "observation");
    assert_eq!(record["observation"]["kind"], "result");
}

#[test]
fn rust_first_python_retry_is_the_same_duplicate_result() {
    let temporary = TempDirectory::new();
    let observation = terminal_observation();
    let rust = TrainingLogger::new(Some(temporary.log_path()))
        .append_observation(observation.clone())
        .expect("Rust appends the terminal result");
    assert!(rust.logged);
    assert!(!rust.duplicate);
    assert!(rust.result_id.starts_with("result-"));

    let python = python_observe(&temporary, &observation);
    assert_eq!(python["status"], "duplicate");
    assert_eq!(python["logged"], false);
    assert_eq!(python["duplicate"], true);
    assert_eq!(python["result_id"], rust.result_id);
    assert_eq!(python["game_id"], rust.game_id);
    assert_eq!(python["result"], rust.result);
    assert_single_terminal_record(&temporary.log_path());
}

#[test]
fn python_first_rust_retry_is_the_same_duplicate_result() {
    let temporary = TempDirectory::new();
    let observation = terminal_observation();
    let python = python_observe(&temporary, &observation);
    assert_eq!(python["status"], "ok");
    assert_eq!(python["logged"], true);
    assert_eq!(python["duplicate"], false);
    assert!(
        python["result_id"]
            .as_str()
            .is_some_and(|result_id| result_id.starts_with("result-"))
    );

    let rust = TrainingLogger::new(Some(temporary.log_path()))
        .append_observation(observation)
        .expect("Rust recognizes the Python terminal result");
    assert!(!rust.logged);
    assert!(rust.duplicate);
    assert_eq!(python["result_id"], rust.result_id);
    assert_eq!(python["game_id"], rust.game_id);
    assert_eq!(python["result"], rust.result);
    assert_single_terminal_record(&temporary.log_path());
}
