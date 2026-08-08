use std::fs;
use std::path::PathBuf;
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};

use metacompanion_solver::behavior::{BEHAVIOR_LOG_FILENAME, BEHAVIOR_SCHEMA_ID, BehaviorLogger};
use serde_json::{Value, json};

static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

struct TempDirectory(PathBuf);

impl TempDirectory {
    fn new() -> Self {
        let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "metacompanion-rust-python-behavior-{}-{sequence}",
            std::process::id()
        ));
        fs::create_dir_all(&path).expect("create behavior integration directory");
        Self(path)
    }

    fn log_path(&self) -> PathBuf {
        self.0.join(BEHAVIOR_LOG_FILENAME)
    }

    fn submission_path(&self) -> PathBuf {
        self.0.join("behavior-submission.json")
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

fn entity(entity_id: &str, card_id: &str, card_type: &str) -> Value {
    json!({
        "entity_id": entity_id,
        "card_id": card_id,
        "card_type": card_type,
        "cost": 1,
        "attack": 2,
        "health": 3,
        "current_health": 3,
        "current_health_known": true,
        "playable": true,
        "can_attack": true,
        "attacks_remaining": 1,
        "taunt": false,
        "divine_shield": false,
        "stealth": false,
        "poisonous": false,
        "lifesteal": false,
        "windfury": false,
        "mega_windfury": false,
        "rush": false,
        "charge": false,
        "reborn": false,
        "dormant": false,
        "immune": false,
        "summoned_this_turn": false,
        "frozen": false,
        "durability": 0,
        "current_durability": 0,
        "name": "private localized card name",
        "controller_id": 999,
        "card_text": "private localized rules text"
    })
}

fn state() -> Value {
    json!({
        "state_id": "state-one",
        "turn": 1,
        "active_player_id": "friendly",
        "perspective_player_id": "friendly",
        "friendly": {
            "player_id": "private-player-one",
            "hero": entity("friendly-hero", "FRIENDLY_HERO", "HERO"),
            "hand": [entity("friendly-hand", "FRIENDLY_DISCOVER", "SPELL")],
            "board": [entity("friendly-location", "FRIENDLY_LOCATION", "LOCATION")],
            "public_rule_tags": {
                "STEADY_SHOT_CAN_TARGET": 1,
                "HERO_POWER_DOUBLE": 0,
                "PRIVATE_UNSAFE_TAG": 8675309
            },
            "public_rule_tags_complete": true
        },
        "opponent": {
            "player_id": "private-player-two",
            "hero": entity("opponent-hero", "OPPONENT_HERO", "HERO"),
            "hand": [{
                "entity_id": "hidden-hand-entity",
                "card_id": "SECRET_CARD_ID",
                "card_type": "SPELL",
                "name": "private opponent card"
            }],
            "board": [],
            "public_rule_tags": {
                "CURRENT_HEROPOWER_DAMAGE_BONUS": 2,
                "HERO_POWER_DISABLED": 0
            },
            "public_rule_tags_complete": true
        },
        "patch": "31.6",
        "mode": "standard",
        "metadata": {
            "password": "must-not-be-written",
            "raw_power_log": "private raw line"
        }
    })
}

fn end_turn_submission(game_id: &str) -> Value {
    let mut post_state = state();
    post_state["state_id"] = json!("state-two");
    json!({
        "schema": BEHAVIOR_SCHEMA_ID,
        "game_id": game_id,
        "behavior_sequence": 1,
        "observed_at_utc": "2026-07-31T12:34:56+08:00",
        "actor_side": "local",
        "actor_player_id": "friendly",
        "actor_evidence": "active_player",
        "identity_status": "event_only",
        "visibility_status": "public_pre_state",
        "boundary_status": "isolated",
        "source_event": "turn_passed_to_opponent",
        "action": {
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": ""
        },
        "pre_state": state(),
        "post_state": post_state,
        "behavior_eligible": true,
        "rl_training_eligible": false
    })
}

fn replay_end_turn_submission(game_id: &str, sequence: u64) -> Value {
    let mut value = end_turn_submission(game_id);
    value["behavior_sequence"] = json!(sequence);
    value["observed_at_utc"] = json!(format!("2026-07-31T12:35:{sequence:02}+08:00"));
    value["actor_evidence"] = json!("hdt_replay_power");
    value["source_event"] = json!("hdt_replay_power");
    value
}

fn location_submission(game_id: &str) -> Value {
    let mut value = end_turn_submission(game_id);
    value["behavior_sequence"] = json!(2);
    value["observed_at_utc"] = json!("2026-07-31T12:35:00+08:00");
    value["actor_evidence"] = json!("hdt_power_log");
    value["identity_status"] = json!("exact_public_entity");
    value["source_event"] = json!("hdt_power_log");
    value["action"] = json!({
        "kind": "location_activate",
        "source_entity_id": "friendly-location",
        "target_entity_id": "opponent-hero",
        "card_id": "FRIENDLY_LOCATION"
    });
    value
}

#[test]
fn behavior_rejects_impossible_hand_and_board_capacity() {
    let temporary = TempDirectory::new();
    let logger = BehaviorLogger::new(Some(temporary.log_path()));
    for (zone, count, expected) in [
        ("hand", 11, "public_hand_capacity_exceeded"),
        ("board", 8, "public_board_capacity_exceeded"),
    ] {
        let mut value = end_turn_submission(&format!("private-capacity-{zone}"));
        value["pre_state"]["friendly"][zone] = Value::Array(
            (0..count)
                .map(|index| entity(&format!("capacity-{zone}-{index}"), "CARD", "MINION"))
                .collect(),
        );
        let error = logger
            .append(value)
            .expect_err("reject impossible public state");
        assert_eq!(error.code(), expected);
    }
    assert!(!temporary.log_path().exists());
}

fn selected_choice_submission(game_id: &str) -> Value {
    let mut value = end_turn_submission(game_id);
    value["behavior_sequence"] = json!(3);
    value["observed_at_utc"] = json!("2026-07-31T12:35:01+08:00");
    value["actor_evidence"] = json!("hdt_power_log");
    value["identity_status"] = json!("exact_public_entity");
    value["source_event"] = json!("hdt_power_log");
    value["action"] = json!({
        "kind": "play_card",
        "source_entity_id": "friendly-hand",
        "target_entity_id": "",
        "card_id": "FRIENDLY_DISCOVER",
        "sub_option": -1,
        "board_position": 0,
        "choice_status": "selected",
        "choices": [{
            "choice_id": 17,
            "choice_type": "GENERAL",
            "source_entity_id": "friendly-hand",
            "option_entity_ids": ["choice-a", "choice-b"],
            "selected_entity_ids": ["choice-b"],
            "status": "selected"
        }]
    });
    value
}

fn python_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("workspace directory")
        .join("solver")
}

#[test]
fn rust_record_round_trips_through_python_behavior_record_and_auditor() {
    let temporary = TempDirectory::new();
    let logger = BehaviorLogger::new(Some(temporary.log_path()));
    let outcome = logger
        .append(end_turn_submission("private-cross-language-game"))
        .expect("append Rust behavior fixture");
    assert!(outcome.logged);
    assert!(!outcome.duplicate);
    let location = logger
        .append(location_submission("private-cross-language-game"))
        .expect("append Rust location behavior fixture");
    assert!(location.logged);
    assert!(location.behavior_eligible);
    let choice = logger
        .append(selected_choice_submission("private-cross-language-game"))
        .expect("append Rust selected-choice behavior fixture");
    assert!(choice.logged);
    assert!(choice.behavior_eligible);
    let replay = logger
        .append(replay_end_turn_submission("private-cross-language-game", 4))
        .expect("append HDT replay behavior fixture");
    assert!(replay.logged);
    assert!(replay.behavior_eligible);
    let downgrade = logger
        .append(json!({
            "schema": BEHAVIOR_SCHEMA_ID,
            "game_id": "private-cross-language-game",
            "behavior_sequence": 5,
            "observed_at_utc": "2026-07-31T12:35:05+08:00",
            "actor_side": "local",
            "actor_player_id": "friendly",
            "actor_evidence": "hdt_player_event",
            "identity_status": "unknown",
            "visibility_status": "public_pre_state",
            "boundary_status": "isolated",
            "source_event": "player_attack",
            "action": {
                "kind": "attack",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": ""
            },
            "pre_state": state(),
            "post_state": null,
            "behavior_eligible": false,
            "rl_training_eligible": false
        }))
        .expect("append known-actor downgrade fixture");
    assert!(downgrade.logged);
    assert!(!downgrade.behavior_eligible);

    let script = r#"
import json
import sys
from metacompanion_solver.behavior import BehaviorCorpus, BehaviorRecord, audit_behavior_corpus

with open(sys.argv[1], encoding="utf-8") as handle:
    records = [json.loads(line) for line in handle if line.strip()]
if len(records) != 5:
    raise SystemExit("Rust did not emit all behavior records")
for raw in records:
    record = BehaviorRecord.from_dict(raw)
    if record.to_dict() != raw:
        raise SystemExit("Python normalization changed the Rust wire record")
    if not record.game_id.startswith("anon-"):
        raise SystemExit("game_id was not anonymized")
raw = records[0]
if raw["pre_state"]["opponent"]["hand"] != [
    {"entity_id": "hidden-hand-entity", "visibility": "hidden"}
]:
    raise SystemExit("opponent hidden hand was not projected")
if raw["pre_state"]["friendly"].get("public_rule_tags") != {
    "HERO_POWER_DOUBLE": 0,
    "STEADY_SHOT_CAN_TARGET": 1,
}:
    raise SystemExit("public player rule tags were not strictly projected")
if raw["pre_state"]["friendly"].get("public_rule_tags_complete") is not True:
    raise SystemExit("public player rule-tag completeness was not preserved")
for key in (
    "current_health_known", "taunt", "divine_shield", "stealth",
    "poisonous", "lifesteal", "windfury", "mega_windfury", "rush",
    "charge", "reborn", "dormant", "immune", "summoned_this_turn", "frozen",
):
    if not isinstance(raw["pre_state"]["friendly"]["hero"].get(key), bool):
        raise SystemExit(f"public combat evidence was not preserved: {key}")
serialized = json.dumps(records, ensure_ascii=False, sort_keys=True)
if "PRIVATE_UNSAFE_TAG" in serialized or "private localized rules text" in serialized:
    raise SystemExit("non-allowlisted state leaked across the behavior boundary")
if any(item["rl_training_eligible"] is not False for item in records):
    raise SystemExit("behavior evidence crossed the RL gate")
if records[1]["action"]["kind"] != "location_activate" or records[1]["behavior_eligible"] is not True:
    raise SystemExit("location behavior did not survive Rust/Python interoperability")
if records[2]["action"].get("choice_status") != "selected":
    raise SystemExit("selected choice was not preserved")
if records[2]["action"]["choices"][0]["option_entity_ids"] != ["choice-a", "choice-b"]:
    raise SystemExit("offered choice set was not preserved")
if records[3]["actor_evidence"] != "hdt_replay_power" or records[3]["source_event"] != "hdt_replay_power":
    raise SystemExit("HDT replay evidence did not survive Rust/Python interoperability")
if records[3]["behavior_eligible"] is not True:
    raise SystemExit("eligible HDT replay behavior was downgraded across runtimes")
if records[4]["identity_status"] != "unknown" or records[4]["behavior_eligible"] is not False:
    raise SystemExit("known-actor downgrade crossed its eligibility gate")
audit = audit_behavior_corpus(sys.argv[1])
if not audit["valid"] or audit["record_count"] != 5:
    raise SystemExit(json.dumps(audit, sort_keys=True))
if audit["behavior_eligible_count"] != 4:
    raise SystemExit("eligible behavior was not recognized")
if audit["identity_status_counts"].get("unknown") != 1:
    raise SystemExit("downgraded identity was not audited")
corpus = BehaviorCorpus(sys.argv[1])
logged = corpus.append(BehaviorRecord.from_dict(records[0]))
print(json.dumps({
    "logged": logged,
    "duplicate": not logged,
    "behavior_id": records[0]["behavior_id"],
    "line_count": len(open(sys.argv[1], encoding="utf-8").read().splitlines()),
}, sort_keys=True, separators=(",", ":")))
"#;
    let output = Command::new("python")
        .arg("-c")
        .arg(script)
        .arg(temporary.log_path())
        .env("PYTHONPATH", python_root())
        .output()
        .expect("launch Python behavior round-trip");
    assert!(
        output.status.success(),
        "Python behavior round-trip failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let python_retry: Value = serde_json::from_slice(&output.stdout)
        .expect("parse Python behavior duplicate acknowledgement");
    assert_eq!(python_retry["logged"], false);
    assert_eq!(python_retry["duplicate"], true);
    assert_eq!(python_retry["behavior_id"], outcome.behavior_id);
    assert_eq!(python_retry["line_count"], 5);

    let record = fs::read_to_string(temporary.log_path()).expect("read behavior fixture");
    assert!(!record.contains("must-not-be-written"));
    assert!(!record.contains("SECRET_CARD_ID"));
    assert!(record.contains(&outcome.behavior_id));
    assert!(record.contains(&location.behavior_id));
    assert!(record.contains(&choice.behavior_id));
    assert!(record.contains(&replay.behavior_id));
    assert!(record.contains(&downgrade.behavior_id));
}

#[test]
fn python_first_rust_retry_has_the_same_behavior_id_and_duplicate_ack() {
    let temporary = TempDirectory::new();
    let submission = replay_end_turn_submission("private-python-first-behavior-game", 1);
    fs::write(
        temporary.submission_path(),
        serde_json::to_vec(&submission).expect("serialize behavior submission"),
    )
    .expect("write behavior submission");
    let script = r#"
import json
import sys
from pathlib import Path

from metacompanion_solver.behavior import BehaviorCorpus, create_behavior_record

payload = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
record = create_behavior_record(
    game_id=payload["game_id"],
    behavior_sequence=payload["behavior_sequence"],
    observed_at_utc=payload["observed_at_utc"],
    actor_side=payload["actor_side"],
    actor_player_id=payload["actor_player_id"],
    actor_evidence=payload["actor_evidence"],
    identity_status=payload["identity_status"],
    visibility_status=payload["visibility_status"],
    boundary_status=payload["boundary_status"],
    source_event=payload["source_event"],
    action=payload["action"],
    pre_state=payload["pre_state"],
    post_state=payload["post_state"],
    behavior_eligible=payload["behavior_eligible"],
    rl_training_eligible=payload["rl_training_eligible"],
)
logged = BehaviorCorpus(sys.argv[1]).append(record)
print(json.dumps({
    "logged": logged,
    "duplicate": not logged,
    "behavior_id": record.behavior_id,
    "game_id": record.game_id,
}, sort_keys=True, separators=(",", ":")))
"#;
    let output = Command::new("python")
        .arg("-c")
        .arg(script)
        .arg(temporary.log_path())
        .arg(temporary.submission_path())
        .env("PYTHONPATH", python_root())
        .output()
        .expect("launch Python behavior writer");
    assert!(
        output.status.success(),
        "Python behavior write failed\nstdout: {}\nstderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let python: Value =
        serde_json::from_slice(&output.stdout).expect("parse Python behavior acknowledgement");
    assert_eq!(python["logged"], true);
    assert_eq!(python["duplicate"], false);

    let rust = BehaviorLogger::new(Some(temporary.log_path()))
        .append(submission)
        .expect("Rust recognizes Python behavior row");
    assert!(!rust.logged);
    assert!(rust.duplicate);
    assert_eq!(python["behavior_id"], rust.behavior_id);
    assert_eq!(python["game_id"], rust.game_id);
    assert_eq!(
        fs::read_to_string(temporary.log_path())
            .expect("read shared behavior corpus")
            .lines()
            .count(),
        1
    );
}

#[test]
fn replay_evidence_and_source_must_be_bound_together() {
    let temporary = TempDirectory::new();
    let logger = BehaviorLogger::new(Some(temporary.log_path()));

    let mut missing_replay_evidence = replay_end_turn_submission("private-replay-negative", 1);
    missing_replay_evidence["actor_evidence"] = json!("active_player");
    assert_eq!(
        logger.append(missing_replay_evidence).unwrap_err().code(),
        "replay_source_requires_replay_evidence"
    );

    let mut missing_replay_source = replay_end_turn_submission("private-replay-negative", 1);
    missing_replay_source["source_event"] = json!("turn_passed_to_opponent");
    assert_eq!(
        logger.append(missing_replay_source).unwrap_err().code(),
        "replay_evidence_requires_replay_source"
    );
    assert!(!temporary.log_path().exists());
}
