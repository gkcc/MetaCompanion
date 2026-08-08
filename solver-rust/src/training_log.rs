//! Privacy-preserving, versioned JSONL training log support.
//!
//! The logger deliberately treats HDT transition candidates as evidence, not as
//! verified actions. Candidate markers are validated together and are always
//! written with `training_eligible=false`; only the offline trajectory auditor
//! may later promote independently replayed transitions.

use std::collections::HashMap;
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

#[cfg(test)]
use std::cell::Cell;

use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};

use crate::API_VERSION;
use crate::error::SolverError;
use crate::hdt::adapt_hdt_state;
use crate::hdt_root::HdtRootCandidateSet;
use crate::model::{Action, ActionKind, CardType, GameState, JsonScalar, SolveRequest};

pub const TRAINING_LOG_FILENAME: &str = "training-v2.jsonl";
pub const TRAINING_LOG_SCHEMA_ID: &str = "advisor-training-log-v2";
pub const TRAJECTORY_SCHEMA_ID: &str = "trajectory-readiness-v1";

const CANDIDATE_CAPTURE_CONTRACT: &str = "partial_hdt_transition_candidate_v1";
const CANDIDATE_STATUS: &str = "post_state_candidate_unverified";
const CANDIDATE_VERIFICATION: &str = "producer_candidate_unverified";
const CANDIDATE_COMPLETENESS: &str = "partial_hdt_gameevents_v1";
const POWER_IDENTITY_CAPTURE_CONTRACT: &str = "hdt_power_action_identity_v1";
const POWER_IDENTITY_COMPLETENESS: &str = "exact_action_identity_unverified_transition_v1";
const POWER_IDENTITY_STATUS: &str = "exact_hdt_power_v1";
const POWER_IDENTITY_CHOICE_STATUS: &str = "none";
const POWER_IDENTITY_SIMULATOR_STATUS: &str = "not_replayed";
const CANDIDATE_ENVELOPE_FIELDS: &[&str] = &[
    "pre_state_id",
    "post_state_id",
    "raw_pre_snapshot_hash",
    "raw_post_snapshot_hash",
    "pre_state_hash",
    "post_state_hash",
    "pre_snapshot_sequence",
    "post_snapshot_sequence",
    "boundary_status",
    "intervening_action_count",
    "capture_warning_count",
    "transition_verification",
    "action_identity_status",
    "choice_status",
    "simulator_status",
    "game_generation",
    "power_collector_epoch",
    "power_action_ordinal",
    "power_gap_count",
];
const HIDDEN_OPPONENT_ZONE_NAMES: &[&str] = &["HAND", "DECK", "SETASIDE", "SECRET"];
const HIDDEN_OPPONENT_ZONE_IDS: &[i64] = &[2, 3, 6, 7];
const PUBLIC_ENTITY_LOCATION_KEYS: &[&str] = &[
    "entity_id",
    "zone",
    "zone_id",
    "zone_position",
    "position",
    "controller",
    "controller_id",
    "visibility",
];
const PUBLIC_ENTITY_LOCATION_TAGS: &[&str] = &["ZONE", "ZONE_POSITION", "CONTROLLER"];

type CachedState = (Value, String, i64);
type CacheEntry = ((String, String), CachedState);

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CandidateTier {
    PartialGameEvents,
    ExactPowerIdentity,
}

impl CandidateTier {
    const fn capture_contract(self) -> &'static str {
        match self {
            Self::PartialGameEvents => CANDIDATE_CAPTURE_CONTRACT,
            Self::ExactPowerIdentity => POWER_IDENTITY_CAPTURE_CONTRACT,
        }
    }

    const fn completeness(self) -> &'static str {
        match self {
            Self::PartialGameEvents => CANDIDATE_COMPLETENESS,
            Self::ExactPowerIdentity => POWER_IDENTITY_COMPLETENESS,
        }
    }
}

#[derive(Debug, Default)]
struct LoggerState {
    state_cache: HashMap<(String, String), CachedState>,
    terminal_results_loaded: bool,
    terminal_index_error: bool,
    terminal_result_ids: HashMap<String, String>,
    last_error: String,
}

/// One process-wide, concurrency-safe JSONL writer.
#[derive(Clone, Debug)]
pub struct TrainingLogger {
    path: Option<Arc<PathBuf>>,
    state: Arc<Mutex<LoggerState>>,
}

/// Stable fields returned to `/v1/observe` after validation and a write attempt.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ObservationAppendResult {
    pub kind: String,
    pub state_id: String,
    pub logged: bool,
    pub duplicate: bool,
    pub result_id: String,
    pub game_id: String,
    pub result: String,
}

impl TrainingLogger {
    #[must_use]
    pub fn disabled() -> Self {
        Self::new(None)
    }

    #[must_use]
    pub fn new(path: Option<PathBuf>) -> Self {
        Self {
            path: path.map(Arc::new),
            state: Arc::new(Mutex::new(LoggerState::default())),
        }
    }

    #[must_use]
    pub const fn enabled(&self) -> bool {
        self.path.is_some()
    }

    #[must_use]
    pub fn healthy(&self) -> bool {
        if !self.enabled() {
            return true;
        }
        let Some(path) = self.path.as_ref() else {
            return true;
        };
        let Ok(mut state) = self.state.lock() else {
            return false;
        };
        // Health is the one eager integrity check.  Ordinary action appends stay on
        // their original cheap path, while a pre-existing damaged corpus is still
        // reported before the first terminal observation arrives.
        if !state.terminal_results_loaded
            && (state.last_error.is_empty() || state.terminal_index_error)
        {
            let _ = ensure_terminal_result_index(path.as_ref(), &mut state);
        }
        state.last_error.is_empty()
    }

    /// Append a canonical solve request/result pair. Logging failures never alter
    /// the solver response and are instead reflected by [`Self::healthy`].
    pub fn append_solve(&self, request: &SolveRequest, result: &Value) -> bool {
        if !self.enabled() {
            return false;
        }
        let state_value = match serde_json::to_value(&request.state) {
            Ok(value) => value,
            Err(_) => {
                self.record_internal_error("SerializeError");
                return false;
            }
        };
        let normalized_state = sanitize_for_training(&state_value);
        let normalized_state_hash = canonical_sha256(&normalized_state);
        let raw_game_id = metadata_text(request.state.metadata.get("game_id"));
        let game_id = if raw_game_id.is_empty() {
            String::new()
        } else {
            anonymous_id(&raw_game_id)
        };
        let raw_snapshot_hash = metadata_text(request.state.metadata.get("snapshot_state_hash"));
        let state_snapshot_sequence =
            metadata_integer(request.state.metadata.get("snapshot_sequence")).unwrap_or(0);
        let request_snapshot_sequence = request.metadata.get("snapshot_sequence").map_or_else(
            || json_scalar_value(request.state.metadata.get("snapshot_sequence")),
            |value| json_scalar_value(Some(value)),
        );
        let decision_id = nonempty_metadata_text(request.metadata.get("decision_id"))
            .unwrap_or_else(|| request.state.state_id.to_string());
        let solve_stage = nonempty_metadata_text(request.metadata.get("solve_stage"))
            .unwrap_or_else(|| "legacy".to_owned())
            .trim()
            .to_ascii_lowercase();
        let trajectory_schema = nonempty_metadata_text(request.metadata.get("trajectory_schema"))
            .unwrap_or_else(|| TRAJECTORY_SCHEMA_ID.to_owned());
        let capture_contract = nonempty_metadata_text(request.metadata.get("capture_contract"))
            .unwrap_or_else(|| "unknown".to_owned());
        let planner_model = result
            .pointer("/coverage/planner_model")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        let rules_model = result
            .pointer("/coverage/rules_model")
            .and_then(Value::as_str)
            .unwrap_or("unknown");
        let adapter = nonempty_metadata_text(request.state.metadata.get("adapter"))
            .unwrap_or_else(|| "native-v1".to_owned());

        if !game_id.is_empty()
            && let Ok(mut state) = self.state.lock()
        {
            state.state_cache.insert(
                (game_id.clone(), request.state.state_id.to_string()),
                (
                    normalized_state.clone(),
                    raw_snapshot_hash.clone(),
                    state_snapshot_sequence,
                ),
            );
        }
        let request_value = match serde_json::to_value(request) {
            Ok(value) => value,
            Err(_) => {
                self.record_internal_error("SerializeError");
                return false;
            }
        };
        self.append_record(json!({
            "kind": "solve",
            "log_schema": TRAINING_LOG_SCHEMA_ID,
            "trajectory": {
                "schema": trajectory_schema,
                "game_id": game_id,
                "split": if game_id.is_empty() { "" } else { deterministic_game_split(&game_id) },
                "decision_id": decision_id,
                "state_id": request.state.state_id,
                "solve_stage": solve_stage,
                "snapshot_sequence": request_snapshot_sequence,
                "capture_contract": capture_contract,
                "patch": request.state.patch,
                "mode": request.state.mode,
                "planner_model": planner_model,
                "rules_model": rules_model,
                "adapter": adapter,
                "raw_snapshot_hash": raw_snapshot_hash,
                "normalized_state_hash": normalized_state_hash,
            },
            "request": request_value,
            "result": result,
        }))
    }

    /// Validate, normalize, and append an action/result observation.
    ///
    /// # Errors
    ///
    /// Returns a schema error before any write when an observation is malformed
    /// or a transition candidate carries inconsistent producer evidence.
    pub fn append_observation(&self, value: Value) -> Result<ObservationAppendResult, SolverError> {
        let prepared = PreparedObservation::parse(value)?;
        let kind = prepared.kind.clone();
        let state_id = prepared.state_id.clone();
        let (record, cache_entry) = prepared.into_record()?;
        if kind == "result" {
            return self.append_terminal_result(record);
        }
        let logged = self.append_record(record);
        if logged
            && let Some((key, value)) = cache_entry
            && let Ok(mut state) = self.state.lock()
        {
            state.state_cache.insert(key, value);
        }
        Ok(ObservationAppendResult {
            kind,
            state_id,
            logged,
            duplicate: false,
            result_id: String::new(),
            game_id: String::new(),
            result: String::new(),
        })
    }

    fn append_terminal_result(
        &self,
        record: Value,
    ) -> Result<ObservationAppendResult, SolverError> {
        self.append_terminal_result_with_sync(record, durable_terminal_barrier)
    }

    fn append_terminal_result_with_sync<F>(
        &self,
        record: Value,
        sync: F,
    ) -> Result<ObservationAppendResult, SolverError>
    where
        F: FnOnce(&mut File) -> std::io::Result<()>,
    {
        let content = sanitize_for_training(&record["observation"]);
        let game_id = content["game_id"].as_str().unwrap_or_default().to_owned();
        let state_id = content["state_id"].as_str().unwrap_or_default().to_owned();
        let result = content["result"].as_str().unwrap_or_default().to_owned();
        let result_id = format!("result-{}", canonical_sha256(&content));
        let key = terminal_result_key(&game_id, &state_id);
        let Some(path) = self.path.as_ref() else {
            return Ok(ObservationAppendResult {
                kind: "result".to_owned(),
                state_id,
                logged: false,
                duplicate: false,
                result_id,
                game_id,
                result,
            });
        };

        let mut state = self
            .state
            .lock()
            .map_err(|_| SolverError::Http("training log lock poisoned".to_owned()))?;
        if !state.terminal_results_loaded {
            ensure_terminal_result_index(path.as_ref(), &mut state)?;
        }
        if let Some(existing) = state.terminal_result_ids.get(&key) {
            if existing != &result_id {
                return Err(SolverError::ResultObservationConflict);
            }
            return Ok(ObservationAppendResult {
                kind: "result".to_owned(),
                state_id,
                logged: false,
                duplicate: true,
                result_id,
                game_id,
                result,
            });
        }

        let payload = serialize_training_record(&record)?;
        let logged = append_terminal_payload_with_sync(path.as_ref(), &mut state, &payload, sync);
        if logged {
            state.terminal_result_ids.insert(key, result_id.clone());
        } else {
            // write_all may have completed even when the durability barrier failed.
            // Never trust the old in-memory index after an ambiguous append: the next
            // retry must rescan (and repair a provable torn tail, if necessary).
            state.terminal_results_loaded = false;
            state.terminal_index_error = false;
            state.terminal_result_ids.clear();
        }
        Ok(ObservationAppendResult {
            kind: "result".to_owned(),
            state_id,
            logged,
            duplicate: false,
            result_id,
            game_id,
            result,
        })
    }

    fn append_record(&self, record: Value) -> bool {
        let Some(path) = self.path.as_ref() else {
            return false;
        };
        let payload = match serialize_training_record(&record) {
            Ok(payload) => payload,
            Err(_) => {
                self.record_internal_error("SerializeError");
                return false;
            }
        };
        let Ok(mut state) = self.state.lock() else {
            return false;
        };
        append_payload(path.as_ref(), &mut state, &payload)
    }

    fn record_internal_error(&self, kind: &str) {
        if let Ok(mut state) = self.state.lock() {
            state.last_error = kind.to_owned();
        }
    }
}

fn serialize_training_record(record: &Value) -> Result<Vec<u8>, SolverError> {
    let sanitized = sanitize_for_training(record);
    let mut payload = serde_json::to_vec(&sanitized)?;
    payload.push(b'\n');
    Ok(payload)
}

fn append_payload(path: &Path, state: &mut LoggerState, payload: &[u8]) -> bool {
    let result = (|| {
        if let Some(parent) = path.parent().filter(|item| !item.as_os_str().is_empty()) {
            fs::create_dir_all(parent)?;
        }
        let mut handle = OpenOptions::new().create(true).append(true).open(path)?;
        handle.write_all(payload)
    })();
    match result {
        Ok(()) => {
            state.last_error.clear();
            true
        }
        Err(error) => {
            state.last_error = format!("{:?}", error.kind());
            false
        }
    }
}

#[cfg(test)]
thread_local! {
    static FORCE_TERMINAL_BARRIER_FAILURE: Cell<bool> = const { Cell::new(false) };
}

fn durable_terminal_barrier(handle: &mut File) -> std::io::Result<()> {
    handle.flush()?;
    #[cfg(test)]
    if FORCE_TERMINAL_BARRIER_FAILURE.with(Cell::get) {
        return Err(std::io::Error::other(
            "injected terminal durability failure",
        ));
    }
    handle.sync_data()
}

fn append_terminal_payload_with_sync<F>(
    path: &Path,
    state: &mut LoggerState,
    payload: &[u8],
    sync: F,
) -> bool
where
    F: FnOnce(&mut File) -> std::io::Result<()>,
{
    let result = (|| {
        if let Some(parent) = path.parent().filter(|item| !item.as_os_str().is_empty()) {
            fs::create_dir_all(parent)?;
        }
        let mut handle = OpenOptions::new().create(true).append(true).open(path)?;
        handle.write_all(payload)?;
        sync(&mut handle)
    })();
    match result {
        Ok(()) => {
            state.last_error.clear();
            true
        }
        Err(error) => {
            state.last_error = format!("{:?}", error.kind());
            false
        }
    }
}

fn terminal_result_key(game_id: &str, state_id: &str) -> String {
    if game_id.is_empty() {
        format!("state:{state_id}")
    } else {
        format!("game:{game_id}")
    }
}

fn ensure_terminal_result_index(path: &Path, state: &mut LoggerState) -> Result<(), SolverError> {
    let result = (|| {
        load_terminal_result_index(path, state)?;
        // Rebuilding an index is a new durability trust boundary, including after
        // a worker restart where no in-memory stale marker survives.  Never commit
        // the rebuilt index (and therefore never ACK a duplicate result) until the
        // active file itself has crossed the durability barrier in this process.
        if path.exists() {
            let mut handle = OpenOptions::new().read(true).write(true).open(path)?;
            durable_terminal_barrier(&mut handle)?;
        }
        Ok(())
    })();
    match result {
        Ok(()) => {
            state.terminal_index_error = false;
            state.last_error.clear();
            Ok(())
        }
        Err(error) => {
            state.terminal_results_loaded = false;
            state.terminal_index_error = true;
            state.terminal_result_ids.clear();
            state.last_error = match &error {
                SolverError::ResultObservationConflict => "ResultObservationConflict".to_owned(),
                _ => "TrainingLogIndexLoadFailed".to_owned(),
            };
            match error {
                SolverError::ResultObservationConflict => {
                    Err(SolverError::ResultObservationConflict)
                }
                _ => Err(SolverError::Http(
                    "training log index is unavailable".to_owned(),
                )),
            }
        }
    }
}

fn load_terminal_result_index(path: &Path, state: &mut LoggerState) -> Result<(), SolverError> {
    repair_torn_training_tail(path)?;
    let mut terminal_result_ids = HashMap::new();
    if path.exists() {
        let reader = BufReader::new(File::open(path)?);
        for line in reader.lines() {
            let line = line?;
            if line.trim().is_empty() {
                continue;
            }
            let record: Value = serde_json::from_str(&line)?;
            if !record.is_object() {
                return Err(SolverError::Http(
                    "training log record must be an object".to_owned(),
                ));
            }
            if record["kind"] != "observation" || record["observation"]["kind"] != "result" {
                continue;
            }
            let content = sanitize_for_training(&record["observation"]);
            let game_id = content["game_id"].as_str().unwrap_or_default();
            let state_id = content["state_id"].as_str().unwrap_or_default();
            let key = terminal_result_key(game_id, state_id);
            let result_id = format!("result-{}", canonical_sha256(&content));
            if terminal_result_ids
                .get(&key)
                .is_some_and(|existing| existing != &result_id)
            {
                return Err(SolverError::ResultObservationConflict);
            }
            terminal_result_ids.insert(key, result_id);
        }
    }
    state.terminal_result_ids = terminal_result_ids;
    state.terminal_results_loaded = true;
    Ok(())
}

fn repair_torn_training_tail(path: &Path) -> Result<bool, SolverError> {
    if !path.exists() {
        return Ok(false);
    }
    let mut source = OpenOptions::new().read(true).open(path)?;
    let original_len = source.metadata()?.len();
    if original_len == 0 {
        return Ok(false);
    }
    source.seek(SeekFrom::End(-1))?;
    let mut last = [0_u8; 1];
    source.read_exact(&mut last)?;
    if last[0] == b'\n' {
        return Ok(false);
    }

    let mut cutoff = 0_u64;
    let mut cursor = original_len;
    let mut buffer = vec![0_u8; 8192];
    while cursor > 0 {
        let chunk_len = usize::try_from(cursor.min(buffer.len() as u64))
            .map_err(|_| SolverError::Http("training tail is too large".to_owned()))?;
        let start = cursor - chunk_len as u64;
        source.seek(SeekFrom::Start(start))?;
        source.read_exact(&mut buffer[..chunk_len])?;
        if let Some(position) = buffer[..chunk_len].iter().rposition(|byte| *byte == b'\n') {
            cutoff = start + position as u64 + 1;
            break;
        }
        cursor = start;
    }

    source.seek(SeekFrom::Start(cutoff))?;
    let tail_len = usize::try_from(original_len - cutoff)
        .map_err(|_| SolverError::Http("training tail is too large".to_owned()))?;
    let mut fragment = vec![0_u8; tail_len];
    source.read_exact(&mut fragment)?;
    drop(source);

    if serde_json::from_slice::<Map<String, Value>>(&fragment).is_ok() {
        // The final JSON object is complete and only its record delimiter was
        // lost.  Preserve it in place; a restart must be able to discover and
        // de-duplicate this already-written terminal result without C# resending.
        let mut active = OpenOptions::new().read(true).write(true).open(path)?;
        if active.metadata()?.len() != original_len {
            return Err(SolverError::Http(
                "training log changed during tail recovery".to_owned(),
            ));
        }
        active.seek(SeekFrom::Start(cutoff))?;
        let mut current_fragment = vec![0_u8; fragment.len()];
        active.read_exact(&mut current_fragment)?;
        if current_fragment != fragment {
            return Err(SolverError::Http(
                "training tail changed during recovery".to_owned(),
            ));
        }
        active.seek(SeekFrom::End(0))?;
        active.write_all(b"\n")?;
        active.flush()?;
        active.sync_data()?;
        return Ok(true);
    }

    archive_torn_fragment(path, &fragment)?;

    // A file with no complete newline has no independently verifiable JSONL
    // record.  Its entire contents are archived above, then the active corpus is
    // truncated to zero.  Otherwise only bytes after the last complete newline
    // are isolated; every complete historical record remains in place.
    let mut active = OpenOptions::new().read(true).write(true).open(path)?;
    if active.metadata()?.len() != original_len {
        return Err(SolverError::Http(
            "training log changed during tail recovery".to_owned(),
        ));
    }
    active.seek(SeekFrom::Start(cutoff))?;
    let mut current_fragment = vec![0_u8; fragment.len()];
    active.read_exact(&mut current_fragment)?;
    if current_fragment != fragment {
        return Err(SolverError::Http(
            "training tail changed during recovery".to_owned(),
        ));
    }
    active.set_len(cutoff)?;
    active.flush()?;
    active.sync_data()?;
    Ok(true)
}

fn archive_torn_fragment(path: &Path, fragment: &[u8]) -> Result<PathBuf, SolverError> {
    let digest = format!("{:x}", Sha256::digest(fragment));
    let file_name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or(TRAINING_LOG_FILENAME);
    let archive = path.with_file_name(format!("{file_name}.torn-tail.{digest}.fragment"));
    if archive.exists() {
        if fs::read(&archive)? != fragment {
            return Err(SolverError::Http(
                "training tail archive content mismatch".to_owned(),
            ));
        }
        return Ok(archive);
    }

    let temporary = archive.with_file_name(format!(
        ".{file_name}.torn-tail.{digest}.{}.tmp",
        std::process::id()
    ));
    let write_result = (|| {
        let mut handle = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        handle.write_all(fragment)?;
        handle.flush()?;
        handle.sync_data()?;
        let mut permissions = handle.metadata()?.permissions();
        permissions.set_readonly(true);
        handle.set_permissions(permissions)?;
        drop(handle);
        fs::rename(&temporary, &archive)
    })();
    if let Err(error) = write_result {
        #[cfg(windows)]
        let _ = clear_windows_readonly(&temporary);
        let _ = fs::remove_file(&temporary);
        if archive.exists() && fs::read(&archive)? == fragment {
            return Ok(archive);
        }
        return Err(error.into());
    }
    Ok(archive)
}

#[cfg(windows)]
#[allow(clippy::permissions_set_readonly_false)]
fn clear_windows_readonly(path: &Path) -> std::io::Result<()> {
    // On Windows this toggles only FILE_ATTRIBUTE_READONLY.  Clippy's warning is
    // about Unix mode bits becoming world-writable, and this helper is not built
    // on Unix.  It is used solely to remove our own read-only recovery artifacts.
    let mut permissions = fs::metadata(path)?.permissions();
    permissions.set_readonly(false);
    fs::set_permissions(path, permissions)
}

#[derive(Debug)]
struct PreparedObservation {
    kind: String,
    state_id: String,
    game_id: String,
    observed_at_utc: String,
    action: Option<ObservedAction>,
    pre_state: Option<GameState>,
    post_state: Option<GameState>,
    result: String,
    metadata: Map<String, Value>,
    candidate: Option<CandidateTier>,
}

/// Canonical simulator action plus the HDT option-frame evidence needed to
/// reconstruct the user's exact input. Choices and PowerLog boundaries stay out
/// of [`Action`]; board position is mirrored here so absence versus explicit zero
/// remains lossless while the search core models positive placement positions.
#[derive(Debug)]
struct ObservedAction {
    core: Action,
    sub_option: Option<i64>,
    board_position: Option<i64>,
    option_id: Option<String>,
    frame_id: Option<String>,
    power_start_watermark: Option<String>,
    power_end_watermark: Option<String>,
    hdt_root_candidates: Option<HdtRootCandidateSet>,
    choices: Option<Vec<Value>>,
}

impl ObservedAction {
    fn parse(value: &Value) -> Result<Self, SolverError> {
        let raw = value
            .as_object()
            .ok_or_else(|| SolverError::schema("request.action", "must be an object"))?;
        const ALLOWED: &[&str] = &[
            "action_id",
            "kind",
            "source_entity_id",
            "target_entity_id",
            "card_id",
            "text",
            "sub_option",
            "board_position",
            "option_id",
            "frame_id",
            "power_start_watermark",
            "power_end_watermark",
            "hdt_root_candidates",
            "choices",
        ];
        if let Some(unknown) = raw.keys().find(|key| !ALLOWED.contains(&key.as_str())) {
            return Err(SolverError::schema(
                "request.action",
                format!("unknown field {unknown:?}"),
            ));
        }
        let core: Action = serde_json::from_value(value.clone()).map_err(|_| {
            SolverError::schema("request.action", "must be a supported action object")
        })?;
        if let Some(action_id) = raw.get("action_id").filter(|value| !value.is_null()) {
            let supplied = action_id.as_str().ok_or_else(|| {
                SolverError::schema("request.action.action_id", "must be a string")
            })?;
            if supplied != core.action_id() {
                return Err(SolverError::schema(
                    "request.action.action_id",
                    "must match kind/source/target/board_position",
                ));
            }
        }
        let board_position = optional_i64(raw, "board_position")?;
        if board_position.is_some_and(|position| !(0..=7).contains(&position))
            || i64::from(core.board_position) != board_position.unwrap_or(0)
        {
            return Err(SolverError::schema(
                "request.action.board_position",
                "must match the canonical action placement",
            ));
        }
        Ok(Self {
            core,
            sub_option: optional_i64(raw, "sub_option")?,
            board_position,
            option_id: optional_wire_id(raw, "option_id")?,
            frame_id: optional_wire_id(raw, "frame_id")?,
            power_start_watermark: optional_text(raw, "power_start_watermark")?,
            power_end_watermark: optional_text(raw, "power_end_watermark")?,
            hdt_root_candidates: raw
                .get("hdt_root_candidates")
                .filter(|value| !value.is_null())
                .map(|value| {
                    serde_json::from_value(value.clone()).map_err(|_| {
                        SolverError::schema(
                            "request.action.hdt_root_candidates",
                            "must be a complete HDT root candidate set",
                        )
                    })
                })
                .transpose()?,
            choices: optional_choices(raw)?,
        })
    }

    fn to_value(&self) -> Value {
        let mut value = Map::from_iter([
            ("action_id".to_owned(), json!(self.core.action_id())),
            ("kind".to_owned(), json!(self.core.kind.as_str())),
            (
                "source_entity_id".to_owned(),
                wire_entity_id(&self.core.source_entity_id),
            ),
            (
                "target_entity_id".to_owned(),
                wire_entity_id(&self.core.target_entity_id),
            ),
            ("card_id".to_owned(), json!(self.core.card_id)),
            ("text".to_owned(), json!(self.core.text)),
        ]);
        insert_optional(&mut value, "sub_option", self.sub_option.map(Value::from));
        insert_optional(
            &mut value,
            "board_position",
            self.board_position.map(Value::from),
        );
        insert_optional(
            &mut value,
            "option_id",
            self.option_id.as_ref().map(|item| json!(item)),
        );
        insert_optional(
            &mut value,
            "frame_id",
            self.frame_id.as_ref().map(|item| json!(item)),
        );
        insert_optional(
            &mut value,
            "power_start_watermark",
            self.power_start_watermark.as_ref().map(|item| json!(item)),
        );
        insert_optional(
            &mut value,
            "power_end_watermark",
            self.power_end_watermark.as_ref().map(|item| json!(item)),
        );
        insert_optional(
            &mut value,
            "hdt_root_candidates",
            self.hdt_root_candidates.as_ref().map(|item| json!(item)),
        );
        insert_optional(
            &mut value,
            "choices",
            self.choices.as_ref().map(|items| json!(items)),
        );
        Value::Object(value)
    }

    fn validate_power_identity(
        &self,
        pre_state: &GameState,
        metadata: &Map<String, Value>,
    ) -> Result<(), SolverError> {
        if self.sub_option != Some(-1) {
            return Err(SolverError::schema(
                "request.action.sub_option",
                "must be -1 for an exact action without unresolved choices",
            ));
        }
        if self.board_position.is_none_or(|position| position < 0) {
            return Err(SolverError::schema(
                "request.action.board_position",
                "must be a nonnegative integer for exact HDT PowerLog identity evidence",
            ));
        }
        for (key, value) in [
            ("option_id", self.option_id.as_deref()),
            ("frame_id", self.frame_id.as_deref()),
            (
                "power_start_watermark",
                self.power_start_watermark.as_deref(),
            ),
            ("power_end_watermark", self.power_end_watermark.as_deref()),
        ] {
            if value.is_none_or(|item| item.trim().is_empty()) {
                return Err(SolverError::schema(
                    format!("request.action.{key}"),
                    "is required for exact HDT PowerLog identity evidence",
                ));
            }
        }
        if self.power_start_watermark == self.power_end_watermark {
            return Err(SolverError::schema(
                "request.action.power_end_watermark",
                "must differ from power_start_watermark for a completed action boundary",
            ));
        }
        for (key, value) in [
            ("option_id", self.option_id.as_deref().unwrap_or_default()),
            ("frame_id", self.frame_id.as_deref().unwrap_or_default()),
        ] {
            if !value.bytes().all(|item| item.is_ascii_digit()) {
                return Err(SolverError::schema(
                    format!("request.action.{key}"),
                    "must identify a numeric HDT option or option frame",
                ));
            }
        }
        let option_id = self
            .option_id
            .as_deref()
            .unwrap_or_default()
            .parse::<i64>()
            .unwrap_or_default();
        let frame_id = self
            .frame_id
            .as_deref()
            .unwrap_or_default()
            .parse::<i64>()
            .unwrap_or_default();
        if frame_id <= 0 {
            return Err(SolverError::schema(
                "request.action.frame_id",
                "must identify a positive HDT option frame",
            ));
        }
        let game_generation = metadata_i64(metadata, "game_generation", 1)?;
        let start_watermark =
            parse_power_watermark(self.power_start_watermark.as_deref().unwrap_or_default())
                .ok_or_else(|| {
                    SolverError::schema(
                        "request.action.power_start_watermark",
                        "must use the g<generation>:<cursor> format with positive integers",
                    )
                })?;
        let end_watermark =
            parse_power_watermark(self.power_end_watermark.as_deref().unwrap_or_default())
                .ok_or_else(|| {
                    SolverError::schema(
                        "request.action.power_end_watermark",
                        "must use the g<generation>:<cursor> format with positive integers",
                    )
                })?;
        if start_watermark.0 != game_generation || end_watermark.0 != game_generation {
            return Err(SolverError::schema(
                "request.action.power_start_watermark",
                "watermark generations must match request.metadata.game_generation",
            ));
        }
        if end_watermark.1 <= start_watermark.1 {
            return Err(SolverError::schema(
                "request.action.power_end_watermark",
                "cursor must be greater than power_start_watermark",
            ));
        }
        if let Some(candidate_set) = &self.hdt_root_candidates {
            candidate_set.validate(pre_state)?;
            if candidate_set.collector_epoch != u64::try_from(game_generation).unwrap_or(0)
                || u64::from(candidate_set.frame_id) != u64::try_from(frame_id).unwrap_or(0)
                || candidate_set.frame_watermark >= u64::try_from(start_watermark.1).unwrap_or(0)
            {
                return Err(SolverError::schema(
                    "request.action.hdt_root_candidates",
                    "frame identity must precede and match the selected Power action",
                ));
            }
            let selected_option = u32::try_from(option_id).unwrap_or(u32::MAX);
            let selected_count = candidate_set
                .candidates
                .iter()
                .filter(|candidate| {
                    candidate.option_id == selected_option
                        && candidate.action.solver_action().is_some_and(|action| {
                            action.action_id() == self.core.action_id()
                                && action.card_id == self.core.card_id
                                && action.board_position == self.core.board_position
                        })
                })
                .count();
            if selected_count != 1 {
                return Err(SolverError::schema(
                    "request.action.hdt_root_candidates.candidates",
                    "must contain the selected action exactly once",
                ));
            }
        }
        if self.choices.is_none() {
            return Err(SolverError::schema(
                "request.action.choices",
                "is required for exact HDT PowerLog identity evidence",
            ));
        }
        if self.choices.as_ref().is_some_and(|items| !items.is_empty()) {
            return Err(SolverError::schema(
                "request.action.choices",
                "must be empty while request.metadata.choice_status is 'none'",
            ));
        }

        if pre_state.active_player_id != pre_state.perspective_player_id {
            return Err(SolverError::schema(
                "request.pre_state.active_player_id",
                "must be the local player for exact local action evidence",
            ));
        }
        let actor = pre_state.player(&pre_state.perspective_player_id)?;
        let source = match self.core.kind {
            ActionKind::PlayCard => actor
                .hand
                .iter()
                .find(|card| card.entity_id == self.core.source_entity_id),
            ActionKind::Attack => std::iter::once(&actor.hero)
                .chain(actor.board.iter())
                .find(|card| card.entity_id == self.core.source_entity_id),
            ActionKind::HeroPower => actor
                .hero_power
                .as_ref()
                .filter(|card| card.entity_id == self.core.source_entity_id),
            ActionKind::LocationActivate => actor.board.iter().find(|card| {
                card.entity_id == self.core.source_entity_id && card.card_type == CardType::Location
            }),
            ActionKind::EndTurn => None,
        };
        if self.core.kind == ActionKind::EndTurn {
            if !self.core.source_entity_id.is_empty()
                || !self.core.target_entity_id.is_empty()
                || !self.core.card_id.is_empty()
                || self.option_id.as_deref() != Some("0")
            {
                return Err(SolverError::schema(
                    "request.action",
                    "end_turn must use option 0 without source, target, or card",
                ));
            }
        } else {
            if option_id <= 0 {
                return Err(SolverError::schema(
                    "request.action.option_id",
                    "must identify a positive HDT option for a non-end-turn action",
                ));
            }
            let source = source.ok_or_else(|| {
                SolverError::schema(
                    "request.action.source_entity_id",
                    "must resolve to the local action source in pre_state",
                )
            })?;
            if self.core.card_id.is_empty() || self.core.card_id != source.card_id {
                return Err(SolverError::schema(
                    "request.action.card_id",
                    "must match the exact pre_state source card",
                ));
            }
            if !positive_numeric_entity_id(&self.core.source_entity_id) {
                return Err(SolverError::schema(
                    "request.action.source_entity_id",
                    "must be a positive numeric HDT entity ID",
                ));
            }
            let position = self.board_position.unwrap_or(0);
            if self.core.kind == ActionKind::PlayCard
                && matches!(source.card_type, CardType::Minion | CardType::Location)
            {
                let maximum = i64::try_from(actor.board.len() + 1).unwrap_or(8);
                if !(1..=maximum).contains(&position) {
                    return Err(SolverError::schema(
                        "request.action.board_position",
                        "must identify a legal 1-based board placement",
                    ));
                }
            } else if position != 0 {
                return Err(SolverError::schema(
                    "request.action.board_position",
                    "must be zero when the action does not place a board entity",
                ));
            }
        }
        if self.core.kind == ActionKind::Attack && self.core.target_entity_id.is_empty() {
            return Err(SolverError::schema(
                "request.action.target_entity_id",
                "is required for an exact attack",
            ));
        }
        if !self.core.target_entity_id.is_empty() {
            if !positive_numeric_entity_id(&self.core.target_entity_id) {
                return Err(SolverError::schema(
                    "request.action.target_entity_id",
                    "must be a positive numeric HDT entity ID",
                ));
            }
            let target_exists = [&pre_state.friendly, &pre_state.opponent]
                .into_iter()
                .flat_map(|player| {
                    std::iter::once(&player.hero)
                        .chain(player.hand.iter())
                        .chain(player.board.iter())
                        .chain(player.hero_power.iter())
                        .chain(player.weapon.iter())
                })
                .any(|card| card.entity_id == self.core.target_entity_id);
            if !target_exists {
                return Err(SolverError::schema(
                    "request.action.target_entity_id",
                    "must resolve to a pre_state entity",
                ));
            }
        }
        let expected_source_resolution = if self.core.kind == ActionKind::EndTurn {
            "not_applicable"
        } else {
            "exact_entity_id"
        };
        let expected_target_resolution = if self.core.target_entity_id.is_empty() {
            "not_applicable"
        } else {
            "exact_entity_id"
        };
        for (key, expected) in [
            ("source_entity_resolution", expected_source_resolution),
            ("target_entity_resolution", expected_target_resolution),
        ] {
            if !metadata
                .get(key)
                .and_then(Value::as_str)
                .is_some_and(|value| value.trim().eq_ignore_ascii_case(expected))
            {
                return Err(SolverError::schema(
                    format!("request.metadata.{key}"),
                    format!("must be {expected:?} for this exact action identity"),
                ));
            }
        }
        Ok(())
    }
}

impl PreparedObservation {
    #[allow(clippy::too_many_lines)]
    fn parse(value: Value) -> Result<Self, SolverError> {
        let raw = value
            .as_object()
            .ok_or_else(|| SolverError::schema("request", "must be an object"))?;
        const ALLOWED: &[&str] = &[
            "api_version",
            "kind",
            "state_id",
            "game_id",
            "observed_at_utc",
            "action",
            "pre_state",
            "post_state",
            "result",
            "metadata",
        ];
        if let Some(unknown) = raw.keys().find(|key| !ALLOWED.contains(&key.as_str())) {
            return Err(SolverError::schema(
                "request",
                format!("unknown field {unknown:?}"),
            ));
        }
        let version = raw.get("api_version").map_or(Ok(API_VERSION), |value| {
            value
                .as_str()
                .ok_or_else(|| SolverError::schema("request.api_version", "must be a string"))
        })?;
        if version != API_VERSION {
            return Err(SolverError::schema(
                "request.api_version",
                format!("expected {API_VERSION:?}"),
            ));
        }
        let kind = required_text(raw, "kind", "request")?.to_ascii_lowercase();
        if kind != "action" && kind != "result" {
            return Err(SolverError::schema(
                "request.kind",
                "must be 'action' or 'result'",
            ));
        }
        let state_id = required_text(raw, "state_id", "request")?.to_owned();
        let game_id = raw.get("game_id").map_or_else(
            || Ok(String::new()),
            |value| {
                value
                    .as_str()
                    .map(str::to_owned)
                    .ok_or_else(|| SolverError::schema("request.game_id", "must be a string"))
            },
        )?;
        let observed_at_utc = match raw.get("observed_at_utc") {
            None | Some(Value::Null) => String::new(),
            Some(Value::String(value)) if value.is_empty() => String::new(),
            Some(Value::String(value)) if is_rfc3339(value) => value.trim().to_owned(),
            Some(_) => {
                return Err(SolverError::schema(
                    "request.observed_at_utc",
                    "must be a valid RFC 3339 timestamp with a UTC offset",
                ));
            }
        };
        let metadata = metadata_object(raw.get("metadata"))?;
        let candidate = candidate_tier(&metadata);
        let (action, pre_state, post_state, result) = if kind == "action" {
            let action_value = raw
                .get("action")
                .filter(|value| !value.is_null())
                .ok_or_else(|| SolverError::schema("request.action", "is required"))?;
            let action = ObservedAction::parse(action_value)?;
            if raw
                .get("result")
                .is_some_and(|value| !value.is_null() && value.as_str() != Some(""))
            {
                return Err(SolverError::schema(
                    "request.result",
                    "is only valid for result observations",
                ));
            }
            if let Some(tier) = candidate {
                validate_candidate_metadata(&metadata, &state_id, tier)?;
                let pre_value = raw
                    .get("pre_state")
                    .filter(|value| !value.is_null())
                    .ok_or_else(|| SolverError::schema("request.pre_state", "is required"))?;
                let post_value = raw
                    .get("post_state")
                    .filter(|value| !value.is_null())
                    .ok_or_else(|| SolverError::schema("request.post_state", "is required"))?;
                let pre = parse_game_state(pre_value, "request.pre_state")?;
                let post = parse_game_state(post_value, "request.post_state")?;
                validate_candidate_state(&pre, "pre", &game_id, &metadata)?;
                validate_candidate_state(&post, "post", &game_id, &metadata)?;
                if tier == CandidateTier::ExactPowerIdentity {
                    action.validate_power_identity(&pre, &metadata)?;
                }
                (Some(action), Some(pre), Some(post), String::new())
            } else {
                if raw.get("pre_state").is_some_and(|value| !value.is_null())
                    || raw.get("post_state").is_some_and(|value| !value.is_null())
                {
                    return Err(SolverError::schema(
                        "request.pre_state",
                        "pre_state and post_state are only valid for an unverified transition candidate",
                    ));
                }
                (Some(action), None, None, String::new())
            }
        } else {
            let result = raw
                .get("result")
                .and_then(Value::as_str)
                .map(str::to_ascii_lowercase)
                .ok_or_else(|| SolverError::schema("request.result", "must be a string"))?;
            if !["win", "loss", "tie", "unknown"].contains(&result.as_str()) {
                return Err(SolverError::schema(
                    "request.result",
                    "must be win, loss, tie, or unknown",
                ));
            }
            validate_result_metadata(&metadata)?;
            if raw.get("action").is_some_and(|value| !value.is_null()) {
                return Err(SolverError::schema(
                    "request.action",
                    "is only valid for action observations",
                ));
            }
            if raw.get("pre_state").is_some_and(|value| !value.is_null())
                || raw.get("post_state").is_some_and(|value| !value.is_null())
            {
                return Err(SolverError::schema(
                    "request.pre_state",
                    "is only valid for action observations",
                ));
            }
            (None, None, None, result)
        };
        Ok(Self {
            kind,
            state_id,
            game_id,
            observed_at_utc,
            action,
            pre_state,
            post_state,
            result,
            metadata,
            candidate,
        })
    }

    fn into_record(mut self) -> Result<(Value, Option<CacheEntry>), SolverError> {
        let anonymized_game_id = if self.game_id.is_empty() {
            String::new()
        } else {
            anonymous_id(&self.game_id)
        };
        let mut pre_value = Value::Null;
        let mut post_value = Value::Null;
        let mut cache_entry = None;
        if let Some(tier) = self.candidate {
            self.metadata.insert(
                "capture_contract".to_owned(),
                json!(tier.capture_contract()),
            );
            self.metadata
                .insert("transition_status".to_owned(), json!(CANDIDATE_STATUS));
            self.metadata.insert(
                "transition_verification".to_owned(),
                json!(CANDIDATE_VERIFICATION),
            );
            self.metadata
                .insert("completeness".to_owned(), json!(tier.completeness()));
            self.metadata
                .insert("training_eligible".to_owned(), json!(false));
            if tier == CandidateTier::ExactPowerIdentity {
                self.metadata.insert(
                    "action_identity_status".to_owned(),
                    json!(POWER_IDENTITY_STATUS),
                );
                self.metadata.insert(
                    "choice_status".to_owned(),
                    json!(POWER_IDENTITY_CHOICE_STATUS),
                );
                self.metadata.insert(
                    "simulator_status".to_owned(),
                    json!(POWER_IDENTITY_SIMULATOR_STATUS),
                );
            }
            let pre = self
                .pre_state
                .as_ref()
                .ok_or_else(|| SolverError::schema("request.pre_state", "is required"))?;
            let post = self
                .post_state
                .as_ref()
                .ok_or_else(|| SolverError::schema("request.post_state", "is required"))?;
            pre_value = sanitize_for_training(&serde_json::to_value(pre)?);
            post_value = sanitize_for_training(&serde_json::to_value(post)?);
            let pre_hash = canonical_sha256(&pre_value);
            let post_hash = canonical_sha256(&post_value);
            self.metadata
                .insert("pre_state_hash".to_owned(), json!(pre_hash));
            self.metadata
                .insert("post_state_hash".to_owned(), json!(post_hash));
            if !anonymized_game_id.is_empty() {
                cache_entry = Some((
                    (
                        anonymized_game_id.clone(),
                        self.metadata["post_state_id"]
                            .as_str()
                            .unwrap_or_default()
                            .to_owned(),
                    ),
                    (
                        post_value.clone(),
                        self.metadata["raw_post_snapshot_hash"]
                            .as_str()
                            .unwrap_or_default()
                            .to_owned(),
                        metadata_i64(&self.metadata, "post_snapshot_sequence", 1)?,
                    ),
                ));
            }
        }
        let action = self
            .action
            .as_ref()
            .map_or(Value::Null, ObservedAction::to_value);
        let observation = json!({
            "api_version": API_VERSION,
            "kind": self.kind,
            "state_id": self.state_id,
            "game_id": anonymized_game_id,
            "observed_at_utc": self.observed_at_utc,
            "action": action,
            "pre_state": pre_value,
            "post_state": post_value,
            "result": self.result,
            "metadata": self.metadata,
        });
        let metadata = observation["metadata"]
            .as_object()
            .ok_or_else(|| SolverError::schema("request.metadata", "must be an object"))?;
        let decision_id = metadata
            .get("decision_id")
            .and_then(nonempty_value_text)
            .or_else(|| metadata.get("pre_state_id").and_then(nonempty_value_text))
            .unwrap_or_else(|| self.state_id.clone());
        let schema = metadata
            .get("trajectory_schema")
            .and_then(nonempty_value_text)
            .unwrap_or_else(|| TRAJECTORY_SCHEMA_ID.to_owned());
        let mut trajectory = Map::new();
        trajectory.insert("schema".to_owned(), json!(schema));
        trajectory.insert("game_id".to_owned(), json!(anonymized_game_id));
        trajectory.insert(
            "split".to_owned(),
            json!(if anonymized_game_id.is_empty() {
                ""
            } else {
                deterministic_game_split(&anonymized_game_id)
            }),
        );
        trajectory.insert("decision_id".to_owned(), json!(decision_id));
        trajectory.insert("state_id".to_owned(), json!(self.state_id));
        trajectory.insert("observation_kind".to_owned(), json!(self.kind));
        for field in [
            "action_sequence",
            "completeness",
            "capture_contract",
            "transition_status",
        ] {
            trajectory.insert(
                field.to_owned(),
                metadata.get(field).cloned().unwrap_or(Value::Null),
            );
        }
        for field in CANDIDATE_ENVELOPE_FIELDS {
            if let Some(value) = metadata.get(*field) {
                trajectory.insert((*field).to_owned(), value.clone());
            }
        }
        Ok((
            json!({
                "kind": "observation",
                "log_schema": TRAINING_LOG_SCHEMA_ID,
                "trajectory": trajectory,
                "observation": observation,
            }),
            cache_entry,
        ))
    }
}

fn required_text<'a>(
    raw: &'a Map<String, Value>,
    key: &str,
    path: &str,
) -> Result<&'a str, SolverError> {
    raw.get(key)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| SolverError::schema(format!("{path}.{key}"), "must be a non-empty string"))
}

fn metadata_object(value: Option<&Value>) -> Result<Map<String, Value>, SolverError> {
    let Some(value) = value else {
        return Ok(Map::new());
    };
    let raw = value
        .as_object()
        .ok_or_else(|| SolverError::schema("request.metadata", "must be an object"))?;
    for (key, value) in raw {
        if !metadata_scalar(value) {
            return Err(SolverError::schema(
                format!("request.metadata.{key}"),
                "metadata values must be JSON scalars",
            ));
        }
    }
    Ok(raw.clone())
}

fn metadata_scalar(value: &Value) -> bool {
    value.is_null() || value.is_boolean() || value.is_number() || value.is_string()
}

fn validate_result_metadata(metadata: &Map<String, Value>) -> Result<(), SolverError> {
    for (key, value) in metadata {
        let stable_scalar = match value {
            Value::Null | Value::Bool(_) | Value::String(_) => true,
            Value::Number(number) => number.is_i64() || number.is_u64(),
            Value::Array(_) | Value::Object(_) => false,
        };
        if !stable_scalar {
            return Err(SolverError::schema(
                format!("request.metadata.{key}"),
                "must be a string, boolean, integer, or null for result observations",
            ));
        }
    }
    Ok(())
}

fn candidate_tier(metadata: &Map<String, Value>) -> Option<CandidateTier> {
    let marker = |key: &str, expected: &str| {
        metadata
            .get(key)
            .and_then(Value::as_str)
            .is_some_and(|value| value.trim().eq_ignore_ascii_case(expected))
    };
    if marker("capture_contract", POWER_IDENTITY_CAPTURE_CONTRACT)
        || marker("completeness", POWER_IDENTITY_COMPLETENESS)
        || marker("action_identity_status", POWER_IDENTITY_STATUS)
    {
        Some(CandidateTier::ExactPowerIdentity)
    } else if marker("capture_contract", CANDIDATE_CAPTURE_CONTRACT)
        || marker("completeness", CANDIDATE_COMPLETENESS)
        || marker("transition_status", CANDIDATE_STATUS)
        || marker("transition_verification", CANDIDATE_VERIFICATION)
    {
        Some(CandidateTier::PartialGameEvents)
    } else {
        None
    }
}

fn validate_candidate_metadata(
    metadata: &Map<String, Value>,
    state_id: &str,
    tier: CandidateTier,
) -> Result<(), SolverError> {
    for (key, expected) in [
        ("capture_contract", tier.capture_contract()),
        ("transition_status", CANDIDATE_STATUS),
        ("transition_verification", CANDIDATE_VERIFICATION),
        ("completeness", tier.completeness()),
    ] {
        if !metadata
            .get(key)
            .and_then(Value::as_str)
            .is_some_and(|value| value.trim().eq_ignore_ascii_case(expected))
        {
            return Err(SolverError::schema(
                format!("request.metadata.{key}"),
                format!("must be {expected:?}"),
            ));
        }
    }
    if !metadata
        .get("training_eligible")
        .is_some_and(explicit_false)
    {
        return Err(SolverError::schema(
            "request.metadata.training_eligible",
            "must be explicitly false for an unverified transition candidate",
        ));
    }
    if tier == CandidateTier::ExactPowerIdentity {
        for (key, expected) in [
            ("action_identity_status", POWER_IDENTITY_STATUS),
            ("choice_status", POWER_IDENTITY_CHOICE_STATUS),
            ("simulator_status", POWER_IDENTITY_SIMULATOR_STATUS),
        ] {
            if !metadata
                .get(key)
                .and_then(Value::as_str)
                .is_some_and(|value| value.trim().eq_ignore_ascii_case(expected))
            {
                return Err(SolverError::schema(
                    format!("request.metadata.{key}"),
                    format!("must be {expected:?}"),
                ));
            }
        }
        let game_generation = metadata_i64(metadata, "game_generation", 1)?;
        let collector_epoch = metadata_i64(metadata, "power_collector_epoch", 1)?;
        let action_ordinal = metadata_i64(metadata, "power_action_ordinal", 1)?;
        metadata_i64(metadata, "power_gap_count", 0)?;
        if collector_epoch != game_generation {
            return Err(SolverError::schema(
                "request.metadata.power_collector_epoch",
                "must match game_generation",
            ));
        }
        if action_ordinal != metadata_i64(metadata, "action_sequence", 1)? {
            return Err(SolverError::schema(
                "request.metadata.power_action_ordinal",
                "must match action_sequence",
            ));
        }
        // A nonzero gap count taints whole-game promotion, but does not erase
        // the exact identity evidence for this individual action.
    }
    let pre_state_id = metadata_required_text(metadata, "pre_state_id")?;
    let post_state_id = metadata_required_text(metadata, "post_state_id")?;
    if pre_state_id != state_id {
        return Err(SolverError::schema(
            "request.metadata.pre_state_id",
            "must match request.state_id",
        ));
    }
    if post_state_id == pre_state_id {
        return Err(SolverError::schema(
            "request.metadata.post_state_id",
            "must differ from pre_state_id",
        ));
    }
    for key in ["raw_pre_snapshot_hash", "raw_post_snapshot_hash"] {
        let value = metadata
            .get(key)
            .and_then(Value::as_str)
            .unwrap_or_default();
        if !is_lower_sha256(value) {
            return Err(SolverError::schema(
                format!("request.metadata.{key}"),
                "must be a lowercase SHA-256 hex digest",
            ));
        }
    }
    for key in ["pre_state_hash", "post_state_hash"] {
        if metadata
            .get(key)
            .is_some_and(|value| !value.is_null() && value.as_str() != Some(""))
        {
            return Err(SolverError::schema(
                format!("request.metadata.{key}"),
                "is logger-derived and must not be supplied by the producer",
            ));
        }
    }
    metadata_i64(metadata, "action_sequence", 1)?;
    let pre_sequence = metadata_i64(metadata, "pre_snapshot_sequence", 1)?;
    let post_sequence = metadata_i64(metadata, "post_snapshot_sequence", 1)?;
    if post_sequence <= pre_sequence {
        return Err(SolverError::schema(
            "request.metadata.post_snapshot_sequence",
            "must be greater than pre_snapshot_sequence",
        ));
    }
    let intervening = metadata_i64(metadata, "intervening_action_count", 0)?;
    metadata_i64(metadata, "capture_warning_count", 0)?;
    let boundary = metadata_required_text(metadata, "boundary_status")?.to_ascii_lowercase();
    if !["isolated", "overlapped", "unstable"].contains(&boundary.as_str()) {
        return Err(SolverError::schema(
            "request.metadata.boundary_status",
            "must be isolated, overlapped, or unstable",
        ));
    }
    if boundary == "isolated" && intervening != 0 {
        return Err(SolverError::schema(
            "request.metadata.intervening_action_count",
            "must be zero for an isolated boundary",
        ));
    }
    Ok(())
}

fn validate_candidate_state(
    state: &GameState,
    label: &str,
    game_id: &str,
    metadata: &Map<String, Value>,
) -> Result<(), SolverError> {
    let expected_id = metadata_required_text(metadata, &format!("{label}_state_id"))?;
    if state.state_id.as_ref() != expected_id {
        return Err(SolverError::schema(
            format!("request.{label}_state.state_id"),
            format!("must match request.metadata.{label}_state_id"),
        ));
    }
    if metadata_text(state.metadata.get("game_id")) != game_id {
        return Err(SolverError::schema(
            format!("request.{label}_state.metadata.game_id"),
            "must match request.game_id",
        ));
    }
    let raw_hash_key = format!("raw_{label}_snapshot_hash");
    let expected_hash = metadata_required_text(metadata, &raw_hash_key)?;
    if metadata_text(state.metadata.get("snapshot_state_hash")) != expected_hash {
        return Err(SolverError::schema(
            format!("request.metadata.{raw_hash_key}"),
            format!("must match the {label}-state snapshot hash"),
        ));
    }
    let sequence_key = format!("{label}_snapshot_sequence");
    let expected_sequence = metadata_i64(metadata, &sequence_key, 1)?;
    if metadata_integer(state.metadata.get("snapshot_sequence")) != Some(expected_sequence) {
        return Err(SolverError::schema(
            format!("request.metadata.{sequence_key}"),
            format!("must match the {label}-state snapshot sequence"),
        ));
    }
    Ok(())
}

fn parse_game_state(value: &Value, path: &str) -> Result<GameState, SolverError> {
    let raw = value
        .as_object()
        .ok_or_else(|| SolverError::schema(path, "must be an object"))?;
    if raw.contains_key("player") && raw.contains_key("opponent") && !raw.contains_key("friendly") {
        return adapt_hdt_state(value);
    }
    let mut state: GameState = serde_json::from_value(value.clone())
        .map_err(|_| SolverError::schema(path, "must be a valid canonical game state"))?;
    state.validate()?;
    Ok(state)
}

fn wire_entity_id(value: &str) -> Value {
    if value.is_empty() {
        Value::Null
    } else if value.bytes().all(|item| item.is_ascii_digit()) {
        value
            .parse::<u64>()
            .map_or_else(|_| json!(value), |number| json!(number))
    } else {
        json!(value)
    }
}

fn positive_numeric_entity_id(value: &str) -> bool {
    !value.is_empty()
        && value.bytes().all(|item| item.is_ascii_digit())
        && value.parse::<u64>().is_ok_and(|item| item > 0)
}

fn optional_i64(raw: &Map<String, Value>, key: &str) -> Result<Option<i64>, SolverError> {
    match raw.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Number(value)) => value.as_i64().map(Some).ok_or_else(|| {
            SolverError::schema(
                format!("request.action.{key}"),
                "must be an integer or null",
            )
        }),
        Some(_) => Err(SolverError::schema(
            format!("request.action.{key}"),
            "must be an integer or null",
        )),
    }
}

fn optional_wire_id(raw: &Map<String, Value>, key: &str) -> Result<Option<String>, SolverError> {
    match raw.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(value)) => Ok(Some(value.clone())),
        Some(Value::Number(value)) => value
            .as_i64()
            .map(|item| Some(item.to_string()))
            .ok_or_else(|| {
                SolverError::schema(
                    format!("request.action.{key}"),
                    "must be a string, integer, or null",
                )
            }),
        Some(_) => Err(SolverError::schema(
            format!("request.action.{key}"),
            "must be a string, integer, or null",
        )),
    }
}

fn optional_text(raw: &Map<String, Value>, key: &str) -> Result<Option<String>, SolverError> {
    match raw.get(key) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(value)) => Ok(Some(value.clone())),
        Some(_) => Err(SolverError::schema(
            format!("request.action.{key}"),
            "must be a string or null",
        )),
    }
}

fn optional_choices(raw: &Map<String, Value>) -> Result<Option<Vec<Value>>, SolverError> {
    let key = "choices";
    match raw.get(key) {
        None => Ok(None),
        Some(Value::Array(values)) => {
            for (index, choice) in values.iter().enumerate() {
                validate_choice(choice, index)?;
            }
            Ok(Some(values.clone()))
        }
        Some(_) => Err(SolverError::schema(
            format!("request.action.{key}"),
            "must be an array",
        )),
    }
}

fn validate_choice(value: &Value, index: usize) -> Result<(), SolverError> {
    let path = format!("request.action.choices[{index}]");
    let raw = value
        .as_object()
        .ok_or_else(|| SolverError::schema(&path, "must be an object"))?;
    const ALLOWED: &[&str] = &[
        "choice_id",
        "choice_type",
        "source_entity_id",
        "option_entity_ids",
        "selected_entity_ids",
        "selected_index",
        "frame_id",
        "status",
    ];
    if let Some(unknown) = raw.keys().find(|key| !ALLOWED.contains(&key.as_str())) {
        return Err(SolverError::schema(
            &path,
            format!("unknown field {unknown:?}"),
        ));
    }
    for (key, item) in raw {
        let field = format!("{path}.{key}");
        match key.as_str() {
            "choice_type" | "status" => {
                if !item.is_string() {
                    return Err(SolverError::schema(field, "must be a string"));
                }
            }
            "selected_index" => {
                if !item.is_null() && item.as_i64().is_none() {
                    return Err(SolverError::schema(field, "must be an integer or null"));
                }
            }
            "option_entity_ids" | "selected_entity_ids" => {
                let entity_ids = item
                    .as_array()
                    .ok_or_else(|| SolverError::schema(&field, "must be an array"))?;
                for (entity_index, entity_id) in entity_ids.iter().enumerate() {
                    if !valid_choice_id(entity_id) {
                        return Err(SolverError::schema(
                            format!("{field}[{entity_index}]"),
                            "must be a string or integer entity ID",
                        ));
                    }
                }
            }
            "choice_id" | "source_entity_id" | "frame_id" => {
                if !item.is_null() && !valid_choice_id(item) {
                    return Err(SolverError::schema(
                        field,
                        "must be a string, integer, or null",
                    ));
                }
            }
            _ => unreachable!("choice field was checked against the allowlist"),
        }
    }
    Ok(())
}

fn valid_choice_id(value: &Value) -> bool {
    value.is_string() || value.as_i64().is_some()
}

fn insert_optional(raw: &mut Map<String, Value>, key: &str, value: Option<Value>) {
    if let Some(value) = value {
        raw.insert(key.to_owned(), value);
    }
}

fn metadata_required_text<'a>(
    metadata: &'a Map<String, Value>,
    key: &str,
) -> Result<&'a str, SolverError> {
    metadata
        .get(key)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            SolverError::schema(
                format!("request.metadata.{key}"),
                "must be a non-empty string",
            )
        })
}

fn metadata_i64(
    metadata: &Map<String, Value>,
    key: &str,
    minimum: i64,
) -> Result<i64, SolverError> {
    let value = metadata.get(key).ok_or_else(|| {
        SolverError::schema(format!("request.metadata.{key}"), "must be an integer")
    })?;
    let parsed = match value {
        Value::Number(value) => value.as_i64(),
        Value::String(value) => value
            .trim()
            .parse::<i64>()
            .ok()
            .filter(|parsed| value.trim() == parsed.to_string()),
        Value::Null | Value::Bool(_) | Value::Array(_) | Value::Object(_) => None,
    }
    .ok_or_else(|| SolverError::schema(format!("request.metadata.{key}"), "must be an integer"))?;
    if parsed < minimum {
        return Err(SolverError::schema(
            format!("request.metadata.{key}"),
            format!("must be at least {minimum}"),
        ));
    }
    Ok(parsed)
}

fn parse_power_watermark(value: &str) -> Option<(i64, i64)> {
    let (generation, cursor) = value.strip_prefix('g')?.split_once(':')?;
    if generation.is_empty()
        || cursor.is_empty()
        || generation.starts_with('0')
        || cursor.starts_with('0')
        || !generation.bytes().all(|item| item.is_ascii_digit())
        || !cursor.bytes().all(|item| item.is_ascii_digit())
    {
        return None;
    }
    Some((generation.parse().ok()?, cursor.parse().ok()?))
}

fn explicit_false(value: &Value) -> bool {
    value == &json!(false)
        || value.as_i64() == Some(0)
        || value.as_str().is_some_and(|value| {
            ["false", "0", "no"].contains(&value.trim().to_ascii_lowercase().as_str())
        })
}

fn is_lower_sha256(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|item| item.is_ascii_digit() || (b'a'..=b'f').contains(&item))
}

fn metadata_text(value: Option<&JsonScalar>) -> String {
    match value {
        None | Some(JsonScalar::Null) => String::new(),
        Some(JsonScalar::String(value)) => value.to_string(),
        Some(JsonScalar::Integer(value)) => value.to_string(),
        Some(JsonScalar::Float(value)) => value.to_string(),
        Some(JsonScalar::Bool(value)) => {
            if *value {
                "True".to_owned()
            } else {
                "False".to_owned()
            }
        }
    }
}

fn nonempty_metadata_text(value: Option<&JsonScalar>) -> Option<String> {
    let value = metadata_text(value);
    (!value.trim().is_empty()).then_some(value)
}

fn metadata_integer(value: Option<&JsonScalar>) -> Option<i64> {
    match value {
        Some(JsonScalar::Integer(value)) => Some(*value),
        Some(JsonScalar::String(value)) => value.trim().parse().ok(),
        _ => None,
    }
}

fn json_scalar_value(value: Option<&JsonScalar>) -> Value {
    value
        .and_then(|value| serde_json::to_value(value).ok())
        .unwrap_or(Value::Null)
}

fn nonempty_value_text(value: &Value) -> Option<String> {
    let text = match value {
        Value::String(value) => value.clone(),
        Value::Number(value) => value.to_string(),
        Value::Bool(value) => value.to_string(),
        Value::Null | Value::Array(_) | Value::Object(_) => String::new(),
    };
    (!text.trim().is_empty()).then_some(text)
}

#[must_use]
pub fn anonymous_id(value: &str) -> String {
    if value.len() == 21
        && value.starts_with("anon-")
        && value[5..]
            .bytes()
            .all(|item| item.is_ascii_digit() || (b'a'..=b'f').contains(&item))
    {
        return value.to_owned();
    }
    format!("anon-{}", &sha256_hex(value.as_bytes())[..16])
}

#[must_use]
pub fn deterministic_game_split(game_id: &str) -> &'static str {
    let digest = Sha256::digest(game_id.as_bytes());
    let bucket = u32::from_be_bytes([digest[0], digest[1], digest[2], digest[3]]) % 100;
    if bucket < 80 {
        "train"
    } else if bucket < 90 {
        "validation"
    } else {
        "test"
    }
}

fn canonical_sha256(value: &Value) -> String {
    let serialized = serde_json::to_vec(value).unwrap_or_default();
    sha256_hex(&serialized)
}

fn sha256_hex(value: &[u8]) -> String {
    format!("{:x}", Sha256::digest(value))
}

fn is_rfc3339(value: &str) -> bool {
    let value = value.trim();
    let Some((date, time_and_offset)) = value.split_once('T') else {
        return false;
    };
    let date_parts = date.split('-').collect::<Vec<_>>();
    if date_parts.len() != 3
        || date_parts[0].len() != 4
        || !valid_number(date_parts[0], 0, 9999)
        || !valid_number(date_parts[1], 1, 12)
        || !valid_number(date_parts[2], 1, 31)
    {
        return false;
    }
    let (time, offset_valid) = if let Some(time) = time_and_offset.strip_suffix('Z') {
        (time, true)
    } else {
        let offset_index = time_and_offset
            .char_indices()
            .skip(1)
            .find_map(|(index, item)| matches!(item, '+' | '-').then_some(index));
        let Some(index) = offset_index else {
            return false;
        };
        let offset = &time_and_offset[index + 1..];
        let offset_parts = offset.split(':').collect::<Vec<_>>();
        (
            &time_and_offset[..index],
            offset_parts.len() == 2
                && valid_number(offset_parts[0], 0, 23)
                && valid_number(offset_parts[1], 0, 59),
        )
    };
    if !offset_valid {
        return false;
    }
    let clock = time.split('.').next().unwrap_or(time);
    let parts = clock.split(':').collect::<Vec<_>>();
    parts.len() == 3
        && valid_number(parts[0], 0, 23)
        && valid_number(parts[1], 0, 59)
        && valid_number(parts[2], 0, 60)
        && time.split_once('.').is_none_or(|(_, fraction)| {
            !fraction.is_empty() && fraction.bytes().all(|item| item.is_ascii_digit())
        })
}

fn valid_number(value: &str, minimum: u32, maximum: u32) -> bool {
    !value.is_empty()
        && value.bytes().all(|item| item.is_ascii_digit())
        && value
            .parse::<u32>()
            .is_ok_and(|value| (minimum..=maximum).contains(&value))
}

fn sanitize_for_training(value: &Value) -> Value {
    sanitize_identity(&redact_hidden_entities(value))
}

fn sanitize_identity(value: &Value) -> Value {
    match value {
        Value::Object(raw) => Value::Object(
            raw.iter()
                .filter_map(|(key, value)| {
                    let normalized = key.to_ascii_lowercase();
                    if drop_key(&normalized) {
                        None
                    } else if normalized == "game_id" || normalized == "match_id" {
                        let text = match value {
                            Value::Null => String::new(),
                            Value::String(value) => value.clone(),
                            _ => value.to_string(),
                        };
                        Some((
                            key.clone(),
                            json!(if text.is_empty() {
                                String::new()
                            } else {
                                anonymous_id(&text)
                            }),
                        ))
                    } else {
                        Some((key.clone(), sanitize_identity(value)))
                    }
                })
                .collect(),
        ),
        Value::Array(values) => Value::Array(values.iter().map(sanitize_identity).collect()),
        _ => value.clone(),
    }
}

fn drop_key(value: &str) -> bool {
    matches!(
        value,
        "account_id"
            | "accountid"
            | "battle_tag"
            | "battletag"
            | "opponent_name"
            | "player_name"
            | "email"
            | "password"
            | "cookie"
            | "authorization"
            | "session_token"
            | "access_token"
            | "refresh_token"
            | "logged_at_utc"
            | "observed_at_utc"
            | "captured_at_utc"
            | "snapshot_state_hash"
            | "current_deck"
            | "deck_id"
            | "deckid"
            | "hearthstone_deck_id"
            | "deck_name"
            | "deckname"
    )
}

fn normalized_zone(value: &str) -> String {
    value
        .trim()
        .to_ascii_uppercase()
        .replace(['_', '-', ' '], "")
}

fn zone_hint(key: &str) -> &'static str {
    match key {
        "hand" => "HAND",
        "deck" => "DECK",
        "setaside" | "set_aside" => "SETASIDE",
        "secret" | "secrets" => "SECRET",
        _ => "",
    }
}

fn hidden_entity(raw: &Map<String, Value>, in_opponent: bool, hint: &str) -> bool {
    if raw
        .get("visibility")
        .and_then(Value::as_str)
        .is_some_and(|value| value.trim().to_ascii_lowercase().contains("hidden"))
    {
        return true;
    }
    if !in_opponent {
        return false;
    }
    if HIDDEN_OPPONENT_ZONE_NAMES.contains(&normalized_zone(hint).as_str())
        || raw
            .get("zone")
            .and_then(Value::as_str)
            .is_some_and(|value| {
                HIDDEN_OPPONENT_ZONE_NAMES.contains(&normalized_zone(value).as_str())
            })
    {
        return true;
    }
    raw.get("zone_id")
        .or_else(|| {
            raw.get("tags")
                .and_then(Value::as_object)
                .and_then(|tags| tags.get("ZONE").or_else(|| tags.get("zone")))
        })
        .and_then(Value::as_i64)
        .is_some_and(|value| HIDDEN_OPPONENT_ZONE_IDS.contains(&value))
}

fn public_hidden_location(raw: &Map<String, Value>) -> Value {
    let mut result = Map::new();
    for (key, value) in raw {
        if PUBLIC_ENTITY_LOCATION_KEYS
            .iter()
            .any(|allowed| key.eq_ignore_ascii_case(allowed))
        {
            result.insert(key.clone(), value.clone());
        }
    }
    if let Some(tags) = raw.get("tags").and_then(Value::as_object) {
        let safe = tags
            .iter()
            .filter(|(key, value)| {
                PUBLIC_ENTITY_LOCATION_TAGS.contains(&key.to_ascii_uppercase().as_str())
                    && metadata_scalar(value)
            })
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect::<Map<_, _>>();
        if !safe.is_empty() {
            result.insert("tags".to_owned(), Value::Object(safe));
        }
    }
    result.insert("visibility".to_owned(), json!("hidden"));
    Value::Object(result)
}

fn redact_hidden_entities(value: &Value) -> Value {
    redact_hidden_value(value, false, "")
}

fn redact_hidden_value(value: &Value, in_opponent: bool, hint: &str) -> Value {
    match value {
        Value::Object(raw) => {
            if hidden_entity(raw, in_opponent, hint) {
                return public_hidden_location(raw);
            }
            Value::Object(
                raw.iter()
                    .map(|(key, value)| {
                        let key_lower = key.to_ascii_lowercase();
                        let child_in_opponent = in_opponent || key_lower == "opponent";
                        let child_hint = if child_in_opponent {
                            zone_hint(&key_lower)
                        } else {
                            ""
                        };
                        (
                            key.clone(),
                            redact_hidden_value(value, child_in_opponent, child_hint),
                        )
                    })
                    .collect(),
            )
        }
        Value::Array(values) => Value::Array(
            values
                .iter()
                .map(|value| redact_hidden_value(value, in_opponent, hint))
                .collect(),
        ),
        _ => value.clone(),
    }
}

#[cfg(test)]
mod tests {
    use std::io;
    use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
    use std::thread;

    use super::*;

    static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    struct TempDirectory(PathBuf);

    impl TempDirectory {
        fn new(label: &str) -> Self {
            let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "metacompanion-rust-training-{label}-{}-{sequence}",
                std::process::id()
            ));
            fs::create_dir_all(&path).expect("create temporary directory");
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

    fn request() -> SolveRequest {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "request-pre",
            "state": {
                "state_id": "state-pre",
                "turn": 1,
                "active_player_id": "friendly",
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
                    "game_id": "private-game",
                    "snapshot_state_hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "snapshot_sequence": 10,
                    "adapter": "hdt-snapshot-v1",
                    "password": "must-not-be-written"
                }
            },
            "metadata": {
                "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                "decision_id": "state-pre",
                "solve_stage": "single",
                "snapshot_sequence": "10",
                "capture_contract": "hdt-public-snapshot-v1",
                "authorization": "Bearer must-not-be-written"
            }
        }))
        .expect("solve request fixture");
        request.validate().expect("valid solve request fixture");
        request
    }

    fn post_request() -> SolveRequest {
        let mut request = request();
        request.request_id = Arc::from("request-post");
        request.state.state_id = Arc::from("state-post");
        request.state.turn = 2;
        request.state.active_player_id = Arc::clone(&request.state.opponent.player_id);
        request.state.metadata.insert(
            Arc::from("snapshot_state_hash"),
            JsonScalar::String(Arc::from(
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            )),
        );
        request
            .state
            .metadata
            .insert(Arc::from("snapshot_sequence"), JsonScalar::Integer(11));
        request.metadata.insert(
            Arc::from("decision_id"),
            JsonScalar::String(Arc::from("state-post")),
        );
        request.metadata.insert(
            Arc::from("snapshot_sequence"),
            JsonScalar::String(Arc::from("11")),
        );
        request
    }

    fn solve_result(request: &SolveRequest) -> Value {
        json!({
            "api_version": API_VERSION,
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

    fn result_observation(state_id: &str, game_id: &str) -> Value {
        json!({
            "api_version": API_VERSION,
            "kind": "result",
            "state_id": state_id,
            "game_id": game_id,
            "observed_at_utc": "2026-07-31T12:34:56.1234567Z",
            "action": null,
            "pre_state": null,
            "post_state": null,
            "result": "win",
            "metadata": {
                "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                "capture_contract": "terminal_result_v1",
                "completeness": "terminal_result",
                "training_eligible": "true",
                "game_generation": "7",
                "power_collector_epoch": "7",
                "power_committed_action_count": "1",
                "power_recorded_action_count": "1",
                "power_gap_count": "0",
                "power_trace_status": "complete"
            }
        })
    }

    fn prepared_result_record(state_id: &str, game_id: &str) -> Value {
        let prepared = PreparedObservation::parse(result_observation(state_id, game_id))
            .expect("valid result observation");
        prepared.into_record().expect("prepared result record").0
    }

    fn candidate_observation(pre: &SolveRequest, post: &SolveRequest) -> Value {
        json!({
            "api_version": API_VERSION,
            "kind": "action",
            "state_id": pre.state.state_id,
            "game_id": "private-game",
            "observed_at_utc": "2026-07-31T12:34:56Z",
            "action": {
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": ""
            },
            "pre_state": serde_json::to_value(&pre.state).expect("serialize pre-state"),
            "post_state": serde_json::to_value(&post.state).expect("serialize post-state"),
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
                "capture_contract": CANDIDATE_CAPTURE_CONTRACT,
                "transition_status": CANDIDATE_STATUS,
                "transition_verification": CANDIDATE_VERIFICATION,
                "completeness": CANDIDATE_COMPLETENESS,
                "action_identity_status": "unverified_hdt_gameevents_v1",
                "choice_status": "not_observed",
                "simulator_status": POWER_IDENTITY_SIMULATOR_STATUS,
                "training_eligible": "false"
            }
        })
    }

    fn power_identity_observation(pre: &SolveRequest, post: &SolveRequest) -> Value {
        let mut observation = candidate_observation(pre, post);
        observation["action"] = json!({
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": "",
            "sub_option": -1,
            "board_position": 0,
            "option_id": 0,
            "frame_id": "42",
            "power_start_watermark": "g7:120",
            "power_end_watermark": "g7:128",
            "choices": []
        });
        observation["metadata"]["capture_contract"] = json!(POWER_IDENTITY_CAPTURE_CONTRACT);
        observation["metadata"]["completeness"] = json!(POWER_IDENTITY_COMPLETENESS);
        observation["metadata"]["action_identity_status"] = json!(POWER_IDENTITY_STATUS);
        observation["metadata"]["choice_status"] = json!(POWER_IDENTITY_CHOICE_STATUS);
        observation["metadata"]["simulator_status"] = json!(POWER_IDENTITY_SIMULATOR_STATUS);
        observation["metadata"]["source_entity_resolution"] = json!("not_applicable");
        observation["metadata"]["target_entity_resolution"] = json!("not_applicable");
        observation["metadata"]["game_generation"] = json!("7");
        observation["metadata"]["power_collector_epoch"] = json!("7");
        observation["metadata"]["power_action_ordinal"] = json!("1");
        observation["metadata"]["power_gap_count"] = json!("0");
        observation
    }

    fn records(path: &PathBuf) -> Vec<Value> {
        fs::read_to_string(path)
            .expect("read JSONL")
            .lines()
            .map(|line| serde_json::from_str(line).expect("valid complete JSONL record"))
            .collect()
    }

    #[test]
    fn anonymous_ids_and_splits_match_the_python_contract() {
        let anonymized = anonymous_id("private-game");
        assert_eq!(anonymized, "anon-b471a09783973d76");
        assert_eq!(anonymous_id(&anonymized), anonymized);
        assert_eq!(deterministic_game_split(&anonymized), "validation");
        assert_eq!(deterministic_game_split("anon-1111111111111111"), "train");
        assert_eq!(deterministic_game_split("anon-2222222222222222"), "test");
    }

    #[test]
    fn disabled_logger_validates_but_never_creates_a_file() {
        let temporary = TempDirectory::new("disabled");
        fs::write(temporary.log_path(), b"existing-record\n").expect("write existing log");
        let logger = TrainingLogger::disabled();
        let outcome = logger
            .append_observation(result_observation("state-post", "private-game"))
            .expect("valid disabled observation");
        assert!(!logger.append_solve(&request(), &solve_result(&request())));
        assert!(!outcome.logged);
        assert!(!logger.enabled());
        assert!(logger.healthy());
        assert_eq!(
            fs::read_to_string(temporary.log_path()).expect("read untouched log"),
            "existing-record\n"
        );
    }

    #[test]
    fn solve_record_has_canonical_hash_models_and_no_private_fields() {
        let temporary = TempDirectory::new("solve");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let request = request();
        assert!(logger.append_solve(&request, &solve_result(&request)));
        let records = records(&temporary.log_path());
        assert_eq!(records.len(), 1);
        let record = &records[0];
        assert_eq!(record["log_schema"], TRAINING_LOG_SCHEMA_ID);
        assert_eq!(record["trajectory"]["game_id"], "anon-b471a09783973d76");
        assert_eq!(record["trajectory"]["split"], "validation");
        assert_eq!(record["trajectory"]["snapshot_sequence"], "10");
        assert_eq!(record["trajectory"]["planner_model"], "rust-turnpair-v1");
        assert_eq!(
            record["trajectory"]["rules_model"],
            "hdt-visible-point-effects-v1"
        );
        assert_eq!(
            record["trajectory"]["normalized_state_hash"]
                .as_str()
                .map(str::len),
            Some(64)
        );
        let serialized = serde_json::to_string(record).expect("serialize record");
        assert!(!serialized.contains("must-not-be-written"));
        assert!(!serialized.contains("authorization"));
        assert!(!serialized.contains("snapshot_state_hash"));
    }

    #[test]
    fn candidate_is_normalized_and_permanently_ineligible() {
        let temporary = TempDirectory::new("candidate");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        let outcome = logger
            .append_observation(candidate_observation(&pre, &post))
            .expect("valid candidate");
        assert!(outcome.logged);
        let records = records(&temporary.log_path());
        let record = &records[0];
        let metadata = &record["observation"]["metadata"];
        assert_eq!(metadata["training_eligible"], false);
        assert_eq!(metadata["completeness"], CANDIDATE_COMPLETENESS);
        assert_eq!(metadata["transition_status"], CANDIDATE_STATUS);
        assert_eq!(record["observation"]["action"]["action_id"], "end_turn::");
        assert!(record["observation"]["action"]["source_entity_id"].is_null());
        assert_eq!(metadata["pre_state_hash"].as_str().map(str::len), Some(64));
        assert_eq!(metadata["post_state_hash"].as_str().map(str::len), Some(64));
        assert_eq!(
            record["trajectory"]["pre_state_hash"],
            metadata["pre_state_hash"]
        );
        assert_eq!(
            record["trajectory"]["post_state_hash"],
            metadata["post_state_hash"]
        );
    }

    #[test]
    fn exact_power_identity_tier_is_preserved_but_never_promoted() {
        let temporary = TempDirectory::new("power-identity");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        assert!(
            logger
                .append_observation(power_identity_observation(&pre, &post))
                .expect("valid exact PowerLog identity candidate")
                .logged
        );
        let records = records(&temporary.log_path());
        let record = &records[0];
        let metadata = &record["observation"]["metadata"];
        assert_eq!(
            metadata["capture_contract"],
            POWER_IDENTITY_CAPTURE_CONTRACT
        );
        assert_eq!(metadata["completeness"], POWER_IDENTITY_COMPLETENESS);
        assert_eq!(metadata["action_identity_status"], POWER_IDENTITY_STATUS);
        assert_eq!(metadata["choice_status"], POWER_IDENTITY_CHOICE_STATUS);
        assert_eq!(
            metadata["simulator_status"],
            POWER_IDENTITY_SIMULATOR_STATUS
        );
        assert_eq!(metadata["transition_status"], CANDIDATE_STATUS);
        assert_eq!(metadata["transition_verification"], CANDIDATE_VERIFICATION);
        assert_eq!(metadata["training_eligible"], false);
        assert_eq!(
            record["trajectory"]["capture_contract"],
            metadata["capture_contract"]
        );
        assert_eq!(
            record["trajectory"]["action_identity_status"],
            metadata["action_identity_status"]
        );
        assert_eq!(record["observation"]["action"]["option_id"], "0");
        assert_eq!(record["observation"]["action"]["choices"], json!([]));
        for field in [
            "game_generation",
            "power_collector_epoch",
            "power_action_ordinal",
            "power_gap_count",
        ] {
            assert_eq!(record["trajectory"][field], metadata[field]);
        }
    }

    #[test]
    fn unsupported_location_identity_stays_partial_and_non_training() {
        let temporary = TempDirectory::new("unsupported-location");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        let mut observation = power_identity_observation(&pre, &post);
        observation["action"]["kind"] = json!("play_card");
        observation["action"]["source_entity_id"] = json!("31");
        observation["action"]["card_id"] = json!("LOCATION_CARD");
        observation["action"]["option_id"] = json!(1);
        observation["metadata"]["capture_contract"] = json!(CANDIDATE_CAPTURE_CONTRACT);
        observation["metadata"]["completeness"] = json!(CANDIDATE_COMPLETENESS);
        observation["metadata"]["action_identity_status"] =
            json!("unsupported_location_activation");
        observation["metadata"]["simulator_status"] = json!("unsupported_location_activation");
        observation["metadata"]["source_entity_resolution"] = json!("exact_entity_id");

        assert!(
            logger
                .append_observation(observation)
                .expect("unsupported location remains a valid partial candidate")
                .logged
        );
        let records = records(&temporary.log_path());
        let metadata = &records[0]["observation"]["metadata"];
        assert_eq!(metadata["capture_contract"], CANDIDATE_CAPTURE_CONTRACT);
        assert_eq!(metadata["completeness"], CANDIDATE_COMPLETENESS);
        assert_eq!(
            metadata["action_identity_status"],
            "unsupported_location_activation"
        );
        assert_eq!(
            metadata["simulator_status"],
            "unsupported_location_activation"
        );
        assert_eq!(metadata["training_eligible"], false);
    }

    #[test]
    fn exact_power_identity_tier_rejects_missing_or_promoted_evidence() {
        let temporary = TempDirectory::new("power-identity-reject");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        for (path, value) in [
            ("/metadata/training_eligible", json!(true)),
            ("/metadata/simulator_status", json!("replayed")),
            ("/metadata/choice_status", json!("selected")),
            ("/metadata/source_entity_resolution", json!("missing")),
            (
                "/metadata/target_entity_resolution",
                json!("exact_entity_id"),
            ),
            ("/action/sub_option", json!(0)),
            ("/action/board_position", Value::Null),
            ("/action/board_position", json!(-1)),
            ("/action/frame_id", json!(0)),
            ("/action/frame_id", json!("frame-42")),
            ("/action/power_end_watermark", json!("")),
            ("/action/power_start_watermark", json!("generation-7:120")),
            ("/action/power_end_watermark", json!("g7:119")),
            ("/action/option_id", Value::Null),
            ("/action/target_entity_id", json!(2)),
            ("/action/choices", json!([{"entity_id": 3}])),
            ("/metadata/game_generation", json!(0)),
            ("/metadata/power_collector_epoch", json!(8)),
            ("/metadata/power_action_ordinal", json!(2)),
            ("/metadata/power_gap_count", json!(-1)),
        ] {
            let mut observation = power_identity_observation(&pre, &post);
            *observation.pointer_mut(path).expect("fixture path") = value;
            assert!(
                logger.append_observation(observation).is_err(),
                "{path} must fail closed"
            );
        }
        let mut equal_watermarks = power_identity_observation(&pre, &post);
        equal_watermarks["action"]["power_end_watermark"] =
            equal_watermarks["action"]["power_start_watermark"].clone();
        assert!(logger.append_observation(equal_watermarks).is_err());
        let mut wrong_turn = power_identity_observation(&pre, &post);
        wrong_turn["pre_state"]["active_player_id"] = json!("opponent");
        assert!(logger.append_observation(wrong_turn).is_err());
        let mut unresolved_source = power_identity_observation(&pre, &post);
        unresolved_source["action"]["kind"] = json!("play_card");
        unresolved_source["action"]["source_entity_id"] = json!(99);
        unresolved_source["action"]["card_id"] = json!("MISSING_CARD");
        assert!(logger.append_observation(unresolved_source).is_err());
        for field in [
            "game_generation",
            "power_collector_epoch",
            "power_action_ordinal",
            "power_gap_count",
        ] {
            let mut missing = power_identity_observation(&pre, &post);
            missing["metadata"]
                .as_object_mut()
                .expect("metadata object")
                .remove(field);
            assert!(
                logger.append_observation(missing).is_err(),
                "missing {field} must fail closed"
            );
        }
        assert!(!temporary.log_path().exists());
    }

    #[test]
    fn terminal_power_trace_metadata_is_preserved_losslessly() {
        let temporary = TempDirectory::new("terminal-power-trace");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        assert!(
            logger
                .append_observation(result_observation("state-post", "private-game"))
                .expect("valid terminal trace observation")
                .logged
        );
        let records = records(&temporary.log_path());
        let metadata = &records[0]["observation"]["metadata"];
        assert_eq!(metadata["game_generation"], "7");
        assert_eq!(metadata["power_collector_epoch"], "7");
        assert_eq!(metadata["power_committed_action_count"], "1");
        assert_eq!(metadata["power_recorded_action_count"], "1");
        assert_eq!(metadata["power_gap_count"], "0");
        assert_eq!(metadata["power_trace_status"], "complete");
    }

    #[test]
    fn terminal_result_retry_is_idempotent_across_restart_and_conflicts_fail_closed() {
        let temporary = TempDirectory::new("terminal-idempotency");
        let observation = result_observation("state-post", "private-game");
        let first = TrainingLogger::new(Some(temporary.log_path()))
            .append_observation(observation.clone())
            .expect("first terminal result");
        assert!(first.logged);
        assert!(!first.duplicate);
        assert!(first.result_id.starts_with("result-"));

        let retry = TrainingLogger::new(Some(temporary.log_path()))
            .append_observation(observation)
            .expect("retry after worker restart");
        assert!(!retry.logged);
        assert!(retry.duplicate);
        assert_eq!(retry.result_id, first.result_id);
        assert_eq!(records(&temporary.log_path()).len(), 1);

        let mut conflict = result_observation("different-state", "private-game");
        conflict["result"] = json!("loss");
        assert!(matches!(
            TrainingLogger::new(Some(temporary.log_path())).append_observation(conflict),
            Err(SolverError::ResultObservationConflict)
        ));
        assert_eq!(records(&temporary.log_path()).len(), 1);
    }

    #[test]
    fn terminal_ack_waits_for_barrier_and_sync_failure_forces_disk_rescan() {
        let temporary = TempDirectory::new("terminal-sync-barrier");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let barrier_called = Arc::new(AtomicBool::new(false));
        let observed_barrier = Arc::clone(&barrier_called);
        let failed = logger
            .append_terminal_result_with_sync(
                prepared_result_record("state-post", "private-game"),
                move |handle| {
                    handle.flush()?;
                    observed_barrier.store(true, Ordering::SeqCst);
                    Err(io::Error::other("injected durability failure"))
                },
            )
            .expect("a durability failure is a soft logging failure");
        assert!(barrier_called.load(Ordering::SeqCst));
        assert!(!failed.logged);
        assert!(!logger.healthy());
        drop(logger);

        // The stale marker died with the first worker.  A restarted worker still
        // must not trust the complete line while its own rebuild sync fails.
        FORCE_TERMINAL_BARRIER_FAILURE.with(|flag| flag.set(true));
        let restarted = TrainingLogger::new(Some(temporary.log_path()));
        assert!(matches!(
            restarted.append_observation(result_observation("state-post", "private-game")),
            Err(SolverError::Http(_))
        ));
        assert!(!restarted.healthy());
        assert_eq!(records(&temporary.log_path()).len(), 1);

        // Only a successful durability barrier may turn the disk rebuild into a
        // duplicate ACK; the original complete row remains the sole result row.
        FORCE_TERMINAL_BARRIER_FAILURE.with(|flag| flag.set(false));
        let retry = restarted
            .append_observation(result_observation("state-post", "private-game"))
            .expect("retry after restarted worker sync succeeds");
        assert!(!retry.logged);
        assert!(retry.duplicate);
        assert_eq!(records(&temporary.log_path()).len(), 1);
        assert!(restarted.healthy());
    }

    #[test]
    fn restart_archives_torn_tail_preserves_history_and_writes_one_result() {
        let temporary = TempDirectory::new("terminal-torn-tail");
        let complete_history = b"{\"kind\":\"legacy-complete\"}\n";
        let torn_fragment = b"{\"kind\":\"observation\",\"observation\":";
        fs::write(temporary.log_path(), complete_history).expect("write complete history");
        OpenOptions::new()
            .append(true)
            .open(temporary.log_path())
            .expect("open torn corpus")
            .write_all(torn_fragment)
            .expect("append torn fragment");

        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let outcome = logger
            .append_observation(result_observation("state-post", "private-game"))
            .expect("repair tail and append terminal result");
        assert!(outcome.logged);
        assert!(logger.healthy());
        let contents = fs::read(temporary.log_path()).expect("read repaired corpus");
        assert!(contents.starts_with(complete_history));
        assert_eq!(contents.iter().filter(|byte| **byte == b'\n').count(), 2);
        assert_eq!(records(&temporary.log_path()).len(), 2);

        let archive = fs::read_dir(&temporary.0)
            .expect("list torn archive")
            .map(|entry| entry.expect("archive entry").path())
            .find(|path| {
                path.file_name()
                    .and_then(|name| name.to_str())
                    .is_some_and(|name| name.contains(".torn-tail."))
            })
            .expect("content-addressed torn archive");
        assert_eq!(
            fs::read(&archive).expect("read torn archive"),
            torn_fragment
        );
        assert!(
            fs::metadata(&archive)
                .expect("torn archive metadata")
                .permissions()
                .readonly()
        );

        let retry = TrainingLogger::new(Some(temporary.log_path()))
            .append_observation(result_observation("state-post", "private-game"))
            .expect("idempotent restart after tail repair");
        assert!(retry.duplicate);
        assert_eq!(records(&temporary.log_path()).len(), 2);

        #[cfg(windows)]
        {
            clear_windows_readonly(&archive).expect("make test archive removable");
        }
    }

    #[test]
    fn complete_json_object_missing_only_newline_is_preserved_and_indexed() {
        let temporary = TempDirectory::new("terminal-missing-newline");
        let observation = result_observation("state-post", "private-game");
        let first = TrainingLogger::new(Some(temporary.log_path()))
            .append_observation(observation.clone())
            .expect("seed durable result");
        assert!(first.logged);
        let mut contents = fs::read(temporary.log_path()).expect("read seeded result");
        assert_eq!(contents.pop(), Some(b'\n'));
        fs::write(temporary.log_path(), &contents).expect("remove only record delimiter");

        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let retry = logger
            .append_observation(observation)
            .expect("repair missing delimiter and index result");
        assert!(!retry.logged);
        assert!(retry.duplicate);
        assert!(logger.healthy());
        let repaired = fs::read(temporary.log_path()).expect("read repaired result");
        assert!(repaired.ends_with(b"\n"));
        assert_eq!(records(&temporary.log_path()).len(), 1);
        assert!(
            fs::read_dir(&temporary.0)
                .expect("list repaired directory")
                .all(|entry| !entry
                    .expect("directory entry")
                    .file_name()
                    .to_string_lossy()
                    .contains(".torn-tail."))
        );
    }

    #[test]
    fn complete_middle_corruption_is_not_repaired_and_marks_health_unhealthy() {
        let temporary = TempDirectory::new("terminal-middle-corruption");
        let damaged = b"{\"kind\":\"legacy-complete\"}\nnot-json\n";
        fs::write(temporary.log_path(), damaged).expect("write middle corruption");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        assert!(!logger.healthy());
        assert!(
            logger
                .append_observation(result_observation("state-post", "private-game"))
                .is_err()
        );
        assert!(!logger.healthy());
        assert_eq!(
            fs::read(temporary.log_path()).expect("read damaged log"),
            damaged
        );
        assert!(
            fs::read_dir(&temporary.0)
                .expect("list damaged directory")
                .all(|entry| !entry
                    .expect("directory entry")
                    .file_name()
                    .to_string_lossy()
                    .contains(".torn-tail."))
        );
    }

    #[test]
    fn conflicting_durable_results_mark_index_health_unhealthy() {
        let temporary = TempDirectory::new("terminal-index-conflict");
        let first = prepared_result_record("state-one", "private-game");
        let mut conflicting_observation = result_observation("state-two", "private-game");
        conflicting_observation["result"] = json!("loss");
        let conflicting = PreparedObservation::parse(conflicting_observation)
            .expect("valid conflicting observation")
            .into_record()
            .expect("prepared conflicting result")
            .0;
        let mut corpus = serialize_training_record(&first).expect("serialize first result");
        corpus
            .extend(serialize_training_record(&conflicting).expect("serialize conflicting result"));
        fs::write(temporary.log_path(), &corpus).expect("write conflicting corpus");

        let logger = TrainingLogger::new(Some(temporary.log_path()));
        assert!(!logger.healthy());
        assert!(matches!(
            logger.append_observation(result_observation("state-one", "private-game")),
            Err(SolverError::ResultObservationConflict)
        ));
        assert!(!logger.healthy());
        assert_eq!(
            fs::read(temporary.log_path()).expect("read conflicting corpus"),
            corpus
        );
    }

    #[test]
    fn ordinary_action_observations_remain_append_only() {
        let temporary = TempDirectory::new("ordinary-action-append-only");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let action = json!({
            "api_version": API_VERSION,
            "kind": "action",
            "state_id": "state-one",
            "game_id": "private-action-game",
            "action": {
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": ""
            },
            "result": "",
            "metadata": {}
        });
        assert!(
            logger
                .append_observation(action.clone())
                .expect("first action")
                .logged
        );
        assert!(
            logger
                .append_observation(action)
                .expect("second action")
                .logged
        );
        assert_eq!(records(&temporary.log_path()).len(), 2);
    }

    #[test]
    fn hdt_option_frame_evidence_round_trips_normalized_choice_fields() {
        let temporary = TempDirectory::new("option-frame");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        let mut observation = candidate_observation(&pre, &post);
        observation["action"] = json!({
            "action_id": "play_card:17:31:position=3",
            "kind": "play_card",
            "source_entity_id": 17,
            "target_entity_id": "31",
            "card_id": "TEST_CARD",
            "text": "",
            "sub_option": -1,
            "board_position": 3,
            "option_id": 0,
            "frame_id": "frame-42",
            "power_start_watermark": "generation-7:120",
            "power_end_watermark": "generation-7:128",
            "choices": [{
                "choice_id": 9,
                "choice_type": "GENERAL",
                "source_entity_id": 17,
                "option_entity_ids": [31, 32],
                "selected_entity_ids": [31, 32],
                "selected_index": 0,
                "frame_id": "42",
                "status": "selected"
            }]
        });
        assert!(
            logger
                .append_observation(observation)
                .expect("valid option-frame action")
                .logged
        );
        let records = records(&temporary.log_path());
        let action = &records[0]["observation"]["action"];
        assert_eq!(action["action_id"], "play_card:17:31:position=3");
        assert_eq!(action["sub_option"], -1);
        assert_eq!(action["board_position"], 3);
        assert_eq!(action["option_id"], "0");
        assert_eq!(action["frame_id"], "frame-42");
        assert_eq!(action["power_start_watermark"], "generation-7:120");
        assert_eq!(action["power_end_watermark"], "generation-7:128");
        assert_eq!(action["choices"][0]["selected_entity_ids"], json!([31, 32]));
        assert_eq!(
            records[0]["observation"]["metadata"]["training_eligible"],
            false
        );
    }

    #[test]
    fn hdt_root_candidate_set_round_trips_with_exact_selected_action() {
        let temporary = TempDirectory::new("hdt-root-candidates");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        let mut observation = power_identity_observation(&pre, &post);
        observation["action"]["hdt_root_candidates"] = json!({
            "contract": "hdt_complete_main_action_options_v1",
            "state_id": "state-pre",
            "frame_id": 42,
            "collector_epoch": 7,
            "frame_watermark": 119,
            "candidate_set_complete": true,
            "candidates": [{
                "option_id": 0,
                "action": {"kind": "end_turn"},
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable"
            }]
        });

        assert!(
            logger
                .append_observation(observation)
                .expect("valid HDT root portfolio observation")
                .logged
        );
        let records = records(&temporary.log_path());
        let candidates = &records[0]["observation"]["action"]["hdt_root_candidates"];
        assert_eq!(
            candidates["contract"],
            "hdt_complete_main_action_options_v1"
        );
        assert_eq!(candidates["state_id"], "state-pre");
        assert_eq!(candidates["frame_id"], 42);
        assert_eq!(candidates["collector_epoch"], 7);
        assert_eq!(candidates["frame_watermark"], 119);
        assert_eq!(candidates["candidates"][0]["action"]["kind"], "end_turn");
        assert_eq!(
            records[0]["observation"]["metadata"]["training_eligible"],
            false
        );
    }

    #[test]
    fn tampered_hdt_root_frame_state_or_selected_option_fails_before_writing() {
        let temporary = TempDirectory::new("hdt-root-candidates-reject");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        let valid_candidates = json!({
            "contract": "hdt_complete_main_action_options_v1",
            "state_id": "state-pre",
            "frame_id": 42,
            "collector_epoch": 7,
            "frame_watermark": 119,
            "candidate_set_complete": true,
            "candidates": [{
                "option_id": 0,
                "action": {"kind": "end_turn"},
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable"
            }]
        });

        for (label, mutation) in [
            ("state", ("state_id", json!("state-other"))),
            ("frame", ("frame_id", json!(43))),
            ("epoch", ("collector_epoch", json!(8))),
            ("watermark", ("frame_watermark", json!(120))),
        ] {
            let mut observation = power_identity_observation(&pre, &post);
            let mut candidates = valid_candidates.clone();
            candidates[mutation.0] = mutation.1;
            observation["action"]["hdt_root_candidates"] = candidates;
            assert!(
                logger.append_observation(observation).is_err(),
                "{label} tampering must fail closed"
            );
        }

        let mut wrong_option = power_identity_observation(&pre, &post);
        wrong_option["action"]["option_id"] = json!("1");
        wrong_option["action"]["hdt_root_candidates"] = valid_candidates;
        assert!(logger.append_observation(wrong_option).is_err());
        assert!(!temporary.log_path().exists());
    }

    #[test]
    fn malformed_option_frame_evidence_fails_before_writing() {
        let temporary = TempDirectory::new("option-frame-reject");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        for (field, value) in [
            ("sub_option", json!(1.5)),
            ("board_position", json!(true)),
            ("option_id", json!({"raw": 1})),
            ("frame_id", json!(false)),
            ("power_start_watermark", json!(42)),
            ("power_end_watermark", json!([])),
            ("choices", json!({"choice": 1})),
            ("choices", Value::Null),
            ("choices", json!([{"raw_power_log": "private raw line"}])),
            ("choices", json!([{"entity_name": "private entity name"}])),
            ("choices", json!([{"selected_entity_ids": [true]}])),
            ("choices", json!(["not an object"])),
        ] {
            let mut observation = candidate_observation(&pre, &post);
            observation["action"][field] = value;
            assert!(
                logger.append_observation(observation).is_err(),
                "{field} must fail closed"
            );
        }
        let mut mismatched_id = candidate_observation(&pre, &post);
        mismatched_id["action"]["action_id"] = json!("attack:1:2");
        assert!(logger.append_observation(mismatched_id).is_err());
        let mut unknown = candidate_observation(&pre, &post);
        unknown["action"]["raw_power_log_line"] = json!("private raw line");
        assert!(logger.append_observation(unknown).is_err());
        assert!(!temporary.log_path().exists());
    }

    #[test]
    fn candidate_marker_flip_and_state_mismatch_fail_before_writing() {
        let temporary = TempDirectory::new("candidate-reject");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let pre = request();
        let post = post_request();
        let mut marker_flip = candidate_observation(&pre, &post);
        marker_flip["metadata"]["completeness"] = json!("complete_action_trace_v1");
        marker_flip["metadata"]["training_eligible"] = json!(true);
        assert!(logger.append_observation(marker_flip).is_err());
        let mut mismatch = candidate_observation(&pre, &post);
        mismatch["metadata"]["raw_post_snapshot_hash"] =
            json!("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        assert!(logger.append_observation(mismatch).is_err());
        assert!(!temporary.log_path().exists());
    }

    #[test]
    fn sanitization_is_idempotent_and_hides_opponent_card_identity() {
        let raw = json!({
            "game_id": "private-game",
            "password": "secret-password",
            "observed_at_utc": "2026-07-31T12:34:56Z",
            "current_deck": {"deck_id": "private-deck", "deck_name": "Private deck"},
            "opponent": {
                "hand": [{
                    "entity_id": 42,
                    "card_id": "SECRET_CARD",
                    "name": "Secret name",
                    "zone": "HAND",
                    "tags": {"ZONE": 3, "CONTROLLER": 2, "COST": 7}
                }]
            }
        });
        let once = sanitize_for_training(&raw);
        let twice = sanitize_for_training(&once);
        assert_eq!(once, twice);
        assert_eq!(once["game_id"], "anon-b471a09783973d76");
        assert_eq!(once["opponent"]["hand"][0]["entity_id"], 42);
        assert_eq!(once["opponent"]["hand"][0]["visibility"], "hidden");
        assert!(once["opponent"]["hand"][0]["card_id"].is_null());
        assert!(once["opponent"]["hand"][0]["tags"]["COST"].is_null());
        let serialized = serde_json::to_string(&once).expect("serialize sanitized value");
        for private in [
            "secret-password",
            "SECRET_CARD",
            "Secret name",
            "private-deck",
            "Private deck",
            "2026-07-31",
        ] {
            assert!(!serialized.contains(private));
        }
    }

    #[test]
    fn write_failure_is_soft_unhealthy_and_recovers_after_success() {
        let temporary = TempDirectory::new("write-failure");
        let blocker = temporary.0.join("blocker");
        fs::write(&blocker, b"not a directory").expect("create blocking file");
        let path = blocker.join(TRAINING_LOG_FILENAME);
        let logger = TrainingLogger::new(Some(path));
        let failed = logger
            .append_observation(result_observation("state-post", "private-game"))
            .expect("valid observation despite write failure");
        assert!(!failed.logged);
        assert!(!logger.healthy());
        fs::remove_file(&blocker).expect("remove blocking file");
        fs::create_dir(&blocker).expect("replace blocker with directory");
        let recovered = logger
            .append_observation(result_observation("state-post", "private-game"))
            .expect("valid observation after recovery");
        assert!(recovered.logged);
        assert!(logger.healthy());
    }

    #[test]
    fn concurrent_writers_emit_only_complete_json_lines() {
        let temporary = TempDirectory::new("concurrent");
        let logger = TrainingLogger::new(Some(temporary.log_path()));
        let threads = (0..24)
            .map(|index| {
                let logger = logger.clone();
                thread::spawn(move || {
                    logger
                        .append_observation(result_observation(
                            &format!("state-{index}"),
                            &format!("game-{index}"),
                        ))
                        .expect("valid concurrent observation")
                        .logged
                })
            })
            .collect::<Vec<_>>();
        assert!(
            threads
                .into_iter()
                .all(|thread| thread.join().expect("writer thread"))
        );
        let records = records(&temporary.log_path());
        assert_eq!(records.len(), 24);
        assert!(records.iter().all(|record| {
            record["kind"] == "observation" && record["log_schema"] == TRAINING_LOG_SCHEMA_ID
        }));
    }
}
