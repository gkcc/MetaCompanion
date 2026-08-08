//! Privacy-safe, content-addressed behavior evidence logging.
//!
//! Behavior observations are deliberately kept separate from trajectory/RL
//! records.  The producer supplies public action evidence and canonical HDT
//! snapshots; this module projects a strict public allowlist, binds the actor
//! and action to the pre-state, fixes `rl_training_eligible=false`, and only
//! then computes the stable content address used on disk.

use std::collections::{HashMap, HashSet};
use std::fmt;
use std::fs::{self, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock, Weak};
use std::time::SystemTime;

#[cfg(test)]
use std::cell::Cell;

use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};

use crate::training_log::anonymous_id;

pub const BEHAVIOR_SCHEMA_ID: &str = "advisor-behavior-v1";
pub const BEHAVIOR_LOG_FILENAME: &str = "behavior-v1.jsonl";

const ACTOR_SIDES: &[&str] = &["local", "opponent", "unknown"];
const ACTOR_PLAYER_IDS: &[&str] = &["friendly", "opponent"];
const ACTOR_EVIDENCE: &[&str] = &[
    "active_player",
    "source_owner",
    "hdt_player_event",
    "hdt_opponent_event",
    "hdt_power_log",
    "hdt_replay_power",
    "unknown",
];
const IDENTITY_STATUSES: &[&str] = &[
    "exact_public_entity",
    "revealed_after_action",
    "event_only",
    "unknown",
];
const VISIBILITY_STATUSES: &[&str] = &["public_pre_state", "revealed_post_action", "hidden_source"];
const BOUNDARY_STATUSES: &[&str] = &["isolated", "overlapped", "unstable", "unverified"];
const ACTION_KINDS: &[&str] = &[
    "play_card",
    "attack",
    "hero_power",
    "location_activate",
    "end_turn",
];
const CARD_TYPES: &[&str] = &[
    "HERO",
    "MINION",
    "SPELL",
    "WEAPON",
    "HERO_POWER",
    "LOCATION",
    "UNKNOWN",
];
const CONTENT_KEYS: &[&str] = &[
    "schema",
    "game_id",
    "behavior_sequence",
    "observed_at_utc",
    "actor_side",
    "actor_player_id",
    "actor_evidence",
    "identity_status",
    "visibility_status",
    "boundary_status",
    "source_event",
    "action",
    "pre_state",
    "post_state",
    "behavior_eligible",
    "rl_training_eligible",
];
const TOP_LEVEL_KEYS: &[&str] = &[
    "schema",
    "behavior_id",
    "content_sha256",
    "game_id",
    "behavior_sequence",
    "observed_at_utc",
    "actor_side",
    "actor_player_id",
    "actor_evidence",
    "identity_status",
    "visibility_status",
    "boundary_status",
    "source_event",
    "action",
    "pre_state",
    "post_state",
    "behavior_eligible",
    "rl_training_eligible",
];
const ACTION_REQUIRED_KEYS: &[&str] = &["kind", "source_entity_id", "target_entity_id", "card_id"];
const ACTION_KEYS: &[&str] = &[
    "kind",
    "source_entity_id",
    "target_entity_id",
    "card_id",
    "sub_option",
    "board_position",
    "choice_status",
    "choices",
];
const CHOICE_KEYS: &[&str] = &[
    "choice_id",
    "choice_type",
    "source_entity_id",
    "option_entity_ids",
    "selected_entity_ids",
    "status",
];
const CHOICE_STATUSES: &[&str] = &["none", "selected", "unresolved", "not_observed"];
const CHOICE_ITEM_STATUSES: &[&str] = &["selected", "unresolved"];
const STATE_KEYS: &[&str] = &[
    "state_id",
    "turn",
    "active_player_id",
    "perspective_player_id",
    "friendly",
    "opponent",
    "patch",
    "mode",
];
const PLAYER_REQUIRED_KEYS: &[&str] = &[
    "player_id",
    "hero",
    "hero_power",
    "weapon",
    "hand",
    "board",
    "mana",
    "max_mana",
    "armor",
    "deck_size",
    "fatigue",
    "hero_power_available",
    "spell_power",
];
const PLAYER_KEYS: &[&str] = &[
    "player_id",
    "hero",
    "hero_power",
    "weapon",
    "hand",
    "board",
    "mana",
    "max_mana",
    "armor",
    "deck_size",
    "fatigue",
    "hero_power_available",
    "spell_power",
    "public_rule_tags",
    "public_rule_tags_complete",
];
const PUBLIC_RULE_TAG_KEYS: &[&str] = &[
    "STEADY_SHOT_CAN_TARGET",
    "CURRENT_HEROPOWER_DAMAGE_BONUS",
    "HERO_POWER_DOUBLE",
    "HEROPOWER_DAMAGE",
    "HERO_POWER_DISABLED",
];
const PUBLIC_ENTITY_KEYS: &[&str] = &[
    "entity_id",
    "card_id",
    "card_type",
    "cost",
    "attack",
    "health",
    "current_health",
    "playable",
    "can_attack",
    "attacks_remaining",
    "current_health_known",
    "taunt",
    "divine_shield",
    "stealth",
    "poisonous",
    "lifesteal",
    "windfury",
    "mega_windfury",
    "rush",
    "charge",
    "reborn",
    "dormant",
    "immune",
    "summoned_this_turn",
    "frozen",
    "durability",
    "current_durability",
];
const HIDDEN_ENTITY_KEYS: &[&str] = &["entity_id", "visibility"];
const RESERVED_TRAJECTORY_FILENAMES: &[&str] = &[
    "training.jsonl",
    "training-v2.jsonl",
    "trajectory.jsonl",
    "trajectory-v1.jsonl",
];

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BehaviorErrorClass {
    Validation,
    Conflict,
    Storage,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BehaviorError {
    class: BehaviorErrorClass,
    code: String,
    path: String,
}

impl BehaviorError {
    fn validation(code: impl Into<String>, path: impl Into<String>) -> Self {
        Self {
            class: BehaviorErrorClass::Validation,
            code: code.into(),
            path: path.into(),
        }
    }

    fn conflict(code: impl Into<String>, path: impl Into<String>) -> Self {
        Self {
            class: BehaviorErrorClass::Conflict,
            code: code.into(),
            path: path.into(),
        }
    }

    fn storage(code: impl Into<String>, path: impl Into<String>) -> Self {
        Self {
            class: BehaviorErrorClass::Storage,
            code: code.into(),
            path: path.into(),
        }
    }

    #[must_use]
    pub const fn class(&self) -> BehaviorErrorClass {
        self.class
    }

    #[must_use]
    pub fn code(&self) -> &str {
        &self.code
    }

    #[must_use]
    pub fn path(&self) -> &str {
        &self.path
    }
}

impl fmt::Display for BehaviorError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.path.is_empty() {
            formatter.write_str(&self.code)
        } else {
            write!(formatter, "{}: {}", self.path, self.code)
        }
    }
}

impl std::error::Error for BehaviorError {}

#[derive(Clone, Debug)]
struct BehaviorRecord {
    value: Value,
    behavior_id: String,
    content_sha256: String,
    game_id: String,
    behavior_sequence: u64,
    behavior_eligible: bool,
}

impl BehaviorRecord {
    fn from_submission(value: Value) -> Result<Self, BehaviorError> {
        let raw = object(&value, "behavior")?;
        strict_keys(raw, CONTENT_KEYS, CONTENT_KEYS, "behavior")?;
        let content = normalized_content(raw, false)?;
        let digest = canonical_sha256(&Value::Object(content.clone()))?;
        let behavior_id = format!("behavior-{digest}");
        let mut record = content;
        record.insert("behavior_id".to_owned(), json!(behavior_id));
        record.insert("content_sha256".to_owned(), json!(digest));
        Self::from_normalized_record(record)
    }

    fn from_persisted(value: Value) -> Result<Self, BehaviorError> {
        let raw = object(&value, "behavior")?;
        strict_keys(raw, TOP_LEVEL_KEYS, TOP_LEVEL_KEYS, "behavior")?;
        let content_raw = Map::from_iter(
            CONTENT_KEYS
                .iter()
                .map(|key| ((*key).to_owned(), raw[*key].clone())),
        );
        let content = normalized_content(&content_raw, true)?;
        let digest = canonical_sha256(&Value::Object(content.clone()))?;
        let content_sha256 = required_text(raw, "content_sha256", "behavior", 64)?;
        if !is_lower_sha256(&content_sha256) || content_sha256 != digest {
            return Err(BehaviorError::validation(
                "content_sha256_mismatch",
                "behavior.content_sha256",
            ));
        }
        let behavior_id = required_text(raw, "behavior_id", "behavior", 73)?;
        if behavior_id != format!("behavior-{digest}") {
            return Err(BehaviorError::validation(
                "behavior_id_mismatch",
                "behavior.behavior_id",
            ));
        }
        let mut record = content;
        record.insert("behavior_id".to_owned(), json!(behavior_id));
        record.insert("content_sha256".to_owned(), json!(content_sha256));
        Self::from_normalized_record(record)
    }

    fn from_normalized_record(record: Map<String, Value>) -> Result<Self, BehaviorError> {
        let behavior_id = record["behavior_id"]
            .as_str()
            .unwrap_or_default()
            .to_owned();
        let content_sha256 = record["content_sha256"]
            .as_str()
            .unwrap_or_default()
            .to_owned();
        let game_id = record["game_id"].as_str().unwrap_or_default().to_owned();
        let behavior_sequence = record["behavior_sequence"].as_u64().ok_or_else(|| {
            BehaviorError::validation("must_be_integer", "behavior.behavior_sequence")
        })?;
        let behavior_eligible = record["behavior_eligible"].as_bool().unwrap_or(false);
        Ok(Self {
            value: Value::Object(record),
            behavior_id,
            content_sha256,
            game_id,
            behavior_sequence,
            behavior_eligible,
        })
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BehaviorAppendResult {
    pub logged: bool,
    pub duplicate: bool,
    pub behavior_id: String,
    pub game_id: String,
    pub behavior_sequence: u64,
    pub behavior_eligible: bool,
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct CorpusStamp {
    len: u64,
    modified: Option<SystemTime>,
    created: Option<SystemTime>,
}

impl CorpusStamp {
    fn from_metadata(metadata: &fs::Metadata) -> Self {
        Self {
            len: metadata.len(),
            modified: metadata.modified().ok(),
            created: metadata.created().ok(),
        }
    }
}

#[derive(Debug, Default)]
struct LoggerState {
    loaded: bool,
    known_stamp: CorpusStamp,
    by_id: HashMap<String, (String, (String, u64))>,
    by_sequence: HashMap<(String, u64), String>,
    max_sequence: HashMap<String, u64>,
    last_error: String,
}

type PathLock = Arc<Mutex<()>>;

fn path_locks() -> &'static Mutex<HashMap<String, Weak<Mutex<()>>>> {
    static LOCKS: OnceLock<Mutex<HashMap<String, Weak<Mutex<()>>>>> = OnceLock::new();
    LOCKS.get_or_init(|| Mutex::new(HashMap::new()))
}

fn normalized_path_key(path: &Path) -> String {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir().map_or_else(|_| path.to_path_buf(), |root| root.join(path))
    };
    absolute.to_string_lossy().to_ascii_lowercase()
}

fn path_lock(path: &Path) -> PathLock {
    let key = normalized_path_key(path);
    let mut locks = path_locks()
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    if let Some(lock) = locks.get(&key).and_then(Weak::upgrade) {
        return lock;
    }
    let lock = Arc::new(Mutex::new(()));
    locks.insert(key, Arc::downgrade(&lock));
    lock
}

/// Independent, restart-safe JSONL logger for `advisor-behavior-v1` records.
#[derive(Clone, Debug)]
pub struct BehaviorLogger {
    path: Option<Arc<PathBuf>>,
    write_lock: PathLock,
    state: Arc<Mutex<LoggerState>>,
}

impl BehaviorLogger {
    #[must_use]
    pub fn disabled() -> Self {
        Self {
            path: None,
            write_lock: Arc::new(Mutex::new(())),
            state: Arc::new(Mutex::new(LoggerState::default())),
        }
    }

    #[must_use]
    pub fn new(path: Option<PathBuf>) -> Self {
        let invalid_path = path.as_deref().is_some_and(reserved_trajectory_path);
        let write_lock = path
            .as_deref()
            .map_or_else(|| Arc::new(Mutex::new(())), path_lock);
        let mut initial = LoggerState::default();
        if invalid_path {
            initial.last_error = "behavior_corpus_path_must_be_independent".to_owned();
        }
        let logger = Self {
            path: path.map(Arc::new),
            write_lock,
            state: Arc::new(Mutex::new(initial)),
        };
        if !invalid_path {
            logger.initialize();
        }
        logger
    }

    pub fn for_training_log_path(training_log_path: Option<&Path>) -> Result<Self, BehaviorError> {
        let path = training_log_path
            .map(behavior_path_for_training_log)
            .transpose()?;
        Ok(Self::new(path))
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
        self.state
            .lock()
            .is_ok_and(|state| state.last_error.is_empty())
    }

    /// Validate, normalize, content-address, and append one behavior record.
    ///
    /// Exact retries return `duplicate=true` without writing a second line.
    /// Reuse of a `(game_id, behavior_sequence)` for different content and
    /// sequence gaps both fail closed.
    pub fn append(&self, value: Value) -> Result<BehaviorAppendResult, BehaviorError> {
        let record = BehaviorRecord::from_submission(value)?;
        let result = |logged, duplicate| BehaviorAppendResult {
            logged,
            duplicate,
            behavior_id: record.behavior_id.clone(),
            game_id: record.game_id.clone(),
            behavior_sequence: record.behavior_sequence,
            behavior_eligible: record.behavior_eligible,
        };
        let Some(path) = self.path.as_deref() else {
            return Ok(result(false, false));
        };
        if reserved_trajectory_path(path) {
            return self.storage_failure(
                "behavior_corpus_path_must_be_independent",
                path.to_string_lossy(),
            );
        }
        let _path_guard = self.write_lock.lock().map_err(|_| {
            BehaviorError::storage("behavior_path_lock_unavailable", path.to_string_lossy())
        })?;
        let mut state = self.state.lock().map_err(|_| {
            BehaviorError::storage("behavior_state_lock_unavailable", path.to_string_lossy())
        })?;
        if let Err(error) = ensure_current(path, &mut state) {
            state.last_error = error.code.clone();
            return Err(error);
        }

        let key = (record.game_id.clone(), record.behavior_sequence);
        let existing_id = state.by_id.get(&record.behavior_id);
        let existing_sequence = state.by_sequence.get(&key);
        if let Some((digest, existing_key)) = existing_id {
            if digest != &record.content_sha256 {
                return Err(BehaviorError::conflict(
                    "behavior_id_conflict",
                    &record.behavior_id,
                ));
            }
            if existing_key == &key && existing_sequence == Some(&record.content_sha256) {
                state.last_error.clear();
                return Ok(result(false, true));
            }
            return Err(BehaviorError::conflict(
                "behavior_id_sequence_conflict",
                &record.behavior_id,
            ));
        }
        if let Some(digest) = existing_sequence {
            if digest == &record.content_sha256 {
                state.last_error.clear();
                return Ok(result(false, true));
            }
            return Err(BehaviorError::conflict(
                "behavior_sequence_conflict",
                format!("{}:{}", record.game_id, record.behavior_sequence),
            ));
        }
        let expected = state
            .max_sequence
            .get(&record.game_id)
            .copied()
            .unwrap_or(0)
            + 1;
        if record.behavior_sequence != expected {
            return Err(BehaviorError::conflict(
                "behavior_sequence_out_of_order",
                format!("{}:{}", record.game_id, record.behavior_sequence),
            ));
        }

        let mut payload = serde_json::to_vec(&record.value).map_err(|_| {
            BehaviorError::storage("behavior_serialize_failed", path.to_string_lossy())
        })?;
        payload.push(b'\n');
        let write_result = (|| -> Result<CorpusStamp, std::io::Error> {
            if let Some(parent) = path.parent().filter(|item| !item.as_os_str().is_empty()) {
                fs::create_dir_all(parent)?;
            }
            let mut handle = OpenOptions::new().create(true).append(true).open(path)?;
            handle.write_all(&payload)?;
            durable_behavior_barrier(&mut handle)?;
            Ok(CorpusStamp::from_metadata(&handle.metadata()?))
        })();
        let new_stamp = match write_result {
            Ok(stamp) => stamp,
            Err(_) => {
                invalidate_index(&mut state);
                state.last_error = "behavior_append_failed".to_owned();
                return Err(BehaviorError::storage(
                    "behavior_append_failed",
                    path.to_string_lossy(),
                ));
            }
        };
        state.known_stamp = new_stamp;
        state.by_id.insert(
            record.behavior_id.clone(),
            (record.content_sha256.clone(), key.clone()),
        );
        state.by_sequence.insert(key, record.content_sha256.clone());
        state
            .max_sequence
            .insert(record.game_id.clone(), record.behavior_sequence);
        state.last_error.clear();
        Ok(result(true, false))
    }

    fn storage_failure<T>(&self, code: &str, path: impl Into<String>) -> Result<T, BehaviorError> {
        if let Ok(mut state) = self.state.lock() {
            state.last_error = code.to_owned();
        }
        Err(BehaviorError::storage(code, path))
    }

    fn initialize(&self) {
        let Some(path) = self.path.as_deref() else {
            return;
        };
        let Ok(_path_guard) = self.write_lock.lock() else {
            if let Ok(mut state) = self.state.lock() {
                state.last_error = "behavior_path_lock_unavailable".to_owned();
            }
            return;
        };
        let Ok(mut state) = self.state.lock() else {
            return;
        };
        if let Err(error) = ensure_current(path, &mut state) {
            state.last_error = error.code;
        }
    }
}

/// Derive the independent behavior corpus path beside an enabled trajectory log.
///
/// # Errors
///
/// Rejects a trajectory path already named `behavior-v1.jsonl`, which would
/// otherwise make the two protocols overwrite one another.
pub fn behavior_path_for_training_log(path: &Path) -> Result<PathBuf, BehaviorError> {
    let derived = path
        .parent()
        .unwrap_or_else(|| Path::new(""))
        .join(BEHAVIOR_LOG_FILENAME);
    if normalized_path_key(path) == normalized_path_key(&derived) {
        return Err(BehaviorError::validation(
            "behavior_corpus_path_must_be_independent",
            "serve.training_log_path",
        ));
    }
    Ok(derived)
}

fn reserved_trajectory_path(path: &Path) -> bool {
    path.file_name()
        .and_then(|value| value.to_str())
        .is_some_and(|value| {
            RESERVED_TRAJECTORY_FILENAMES
                .iter()
                .any(|reserved| value.eq_ignore_ascii_case(reserved))
        })
}

#[cfg(test)]
thread_local! {
    static FORCE_BEHAVIOR_BARRIER_FAILURE: Cell<bool> = const { Cell::new(false) };
}

fn durable_behavior_barrier(handle: &mut fs::File) -> std::io::Result<()> {
    handle.flush()?;
    #[cfg(test)]
    if FORCE_BEHAVIOR_BARRIER_FAILURE.with(Cell::get) {
        return Err(std::io::Error::other(
            "injected behavior durability failure",
        ));
    }
    handle.sync_data()
}

fn ensure_current(path: &Path, state: &mut LoggerState) -> Result<(), BehaviorError> {
    let current_stamp = match corpus_stamp(path) {
        Ok(stamp) => stamp,
        Err(_) => {
            invalidate_index(state);
            return Err(BehaviorError::storage(
                "behavior_corpus_stat_failed",
                path.to_string_lossy(),
            ));
        }
    };
    if state.loaded && state.known_stamp == current_stamp {
        return Ok(());
    }
    if let Err(error) = reload(path, state) {
        invalidate_index(state);
        return Err(error);
    }
    Ok(())
}

fn invalidate_index(state: &mut LoggerState) {
    state.loaded = false;
    state.known_stamp = CorpusStamp::default();
    state.by_id.clear();
    state.by_sequence.clear();
    state.max_sequence.clear();
}

fn reload(path: &Path, state: &mut LoggerState) -> Result<(), BehaviorError> {
    repair_torn_behavior_tail(path)?;
    let expected_stamp = corpus_stamp(path).map_err(|_| {
        BehaviorError::storage("behavior_corpus_stat_failed", path.to_string_lossy())
    })?;
    let body = match fs::read_to_string(path) {
        Ok(body) => body,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => String::new(),
        Err(_) => {
            return Err(BehaviorError::storage(
                "behavior_corpus_read_failed",
                path.to_string_lossy(),
            ));
        }
    };
    if !body.is_empty() && !body.ends_with('\n') {
        return Err(BehaviorError::storage(
            "behavior_corpus_changed_during_reload",
            path.to_string_lossy(),
        ));
    }
    let mut by_id = HashMap::new();
    let mut by_sequence = HashMap::new();
    let mut sequences: HashMap<String, HashSet<u64>> = HashMap::new();
    for (index, line) in body.lines().enumerate() {
        if line.trim().is_empty() {
            return Err(BehaviorError::storage(
                "blank_jsonl_line",
                format!("{}:{}", path.display(), index + 1),
            ));
        }
        let value: Value = serde_json::from_str(line).map_err(|_| {
            BehaviorError::storage(
                "existing_behavior_corpus_invalid",
                format!("{}:{}", path.display(), index + 1),
            )
        })?;
        let record = BehaviorRecord::from_persisted(value).map_err(|_| {
            BehaviorError::storage(
                "existing_behavior_corpus_invalid",
                format!("{}:{}", path.display(), index + 1),
            )
        })?;
        let key = (record.game_id.clone(), record.behavior_sequence);
        if by_id.contains_key(&record.behavior_id) || by_sequence.contains_key(&key) {
            return Err(BehaviorError::storage(
                "existing_behavior_corpus_duplicate",
                format!("{}:{}", path.display(), index + 1),
            ));
        }
        by_id.insert(
            record.behavior_id,
            (record.content_sha256.clone(), key.clone()),
        );
        by_sequence.insert(key, record.content_sha256);
        sequences
            .entry(record.game_id)
            .or_default()
            .insert(record.behavior_sequence);
    }
    let mut max_sequence = HashMap::new();
    for (game_id, values) in sequences {
        let maximum = values.iter().copied().max().unwrap_or(0);
        if values.len() as u64 != maximum || !(1..=maximum).all(|value| values.contains(&value)) {
            return Err(BehaviorError::storage(
                "existing_behavior_sequence_not_contiguous",
                game_id,
            ));
        }
        max_sequence.insert(game_id, maximum);
    }

    let known_stamp = if path.exists() {
        let mut handle = OpenOptions::new()
            .read(true)
            .write(true)
            .open(path)
            .map_err(|_| {
                BehaviorError::storage("behavior_corpus_sync_failed", path.to_string_lossy())
            })?;
        durable_behavior_barrier(&mut handle).map_err(|_| {
            BehaviorError::storage("behavior_corpus_sync_failed", path.to_string_lossy())
        })?;
        let stamp = CorpusStamp::from_metadata(&handle.metadata().map_err(|_| {
            BehaviorError::storage("behavior_corpus_stat_failed", path.to_string_lossy())
        })?);
        if stamp.len != body.len() as u64 || stamp != expected_stamp {
            return Err(BehaviorError::storage(
                "behavior_corpus_changed_during_reload",
                path.to_string_lossy(),
            ));
        }
        stamp
    } else {
        CorpusStamp::default()
    };

    state.loaded = true;
    state.known_stamp = known_stamp;
    state.by_id = by_id;
    state.by_sequence = by_sequence;
    state.max_sequence = max_sequence;
    state.last_error.clear();
    Ok(())
}

fn corpus_stamp(path: &Path) -> std::io::Result<CorpusStamp> {
    match fs::metadata(path) {
        Ok(metadata) => Ok(CorpusStamp::from_metadata(&metadata)),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(CorpusStamp::default()),
        Err(error) => Err(error),
    }
}

fn repair_torn_behavior_tail(path: &Path) -> Result<bool, BehaviorError> {
    let repair = (|| -> std::io::Result<bool> {
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
                .map_err(|_| std::io::Error::other("behavior tail is too large"))?;
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
            .map_err(|_| std::io::Error::other("behavior tail is too large"))?;
        let mut fragment = vec![0_u8; tail_len];
        source.read_exact(&mut fragment)?;
        drop(source);

        if serde_json::from_slice::<Map<String, Value>>(&fragment).is_ok() {
            let mut active = OpenOptions::new().read(true).write(true).open(path)?;
            verify_behavior_tail_unchanged(&mut active, original_len, cutoff, &fragment)?;
            active.seek(SeekFrom::End(0))?;
            active.write_all(b"\n")?;
            durable_behavior_barrier(&mut active)?;
            return Ok(true);
        }

        archive_torn_behavior_fragment(path, &fragment)?;
        let mut active = OpenOptions::new().read(true).write(true).open(path)?;
        verify_behavior_tail_unchanged(&mut active, original_len, cutoff, &fragment)?;
        active.set_len(cutoff)?;
        durable_behavior_barrier(&mut active)?;
        Ok(true)
    })();
    repair.map_err(|_| {
        BehaviorError::storage(
            "behavior_corpus_tail_recovery_failed",
            path.to_string_lossy(),
        )
    })
}

fn verify_behavior_tail_unchanged(
    active: &mut fs::File,
    original_len: u64,
    cutoff: u64,
    fragment: &[u8],
) -> std::io::Result<()> {
    if active.metadata()?.len() != original_len {
        return Err(std::io::Error::other(
            "behavior corpus changed during tail recovery",
        ));
    }
    active.seek(SeekFrom::Start(cutoff))?;
    let mut current_fragment = vec![0_u8; fragment.len()];
    active.read_exact(&mut current_fragment)?;
    if current_fragment != fragment {
        return Err(std::io::Error::other(
            "behavior tail changed during recovery",
        ));
    }
    Ok(())
}

fn archive_torn_behavior_fragment(path: &Path, fragment: &[u8]) -> std::io::Result<PathBuf> {
    let digest = format!("{:x}", Sha256::digest(fragment));
    let file_name = path
        .file_name()
        .and_then(|value| value.to_str())
        .unwrap_or(BEHAVIOR_LOG_FILENAME);
    let archive = path.with_file_name(format!("{file_name}.torn-tail.{digest}.fragment"));
    if archive.exists() {
        if fs::read(&archive)? != fragment {
            return Err(std::io::Error::other(
                "behavior tail archive content mismatch",
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
        let _ = clear_windows_behavior_readonly(&temporary);
        let _ = fs::remove_file(&temporary);
        if archive.exists() && fs::read(&archive)? == fragment {
            return Ok(archive);
        }
        return Err(error);
    }
    Ok(archive)
}

#[cfg(windows)]
#[allow(clippy::permissions_set_readonly_false)]
fn clear_windows_behavior_readonly(path: &Path) -> std::io::Result<()> {
    let mut permissions = fs::metadata(path)?.permissions();
    permissions.set_readonly(false);
    fs::set_permissions(path, permissions)
}

fn normalized_content(
    raw: &Map<String, Value>,
    persisted: bool,
) -> Result<Map<String, Value>, BehaviorError> {
    let schema = required_text(raw, "schema", "behavior", 256)?;
    if schema != BEHAVIOR_SCHEMA_ID {
        return Err(BehaviorError::validation("wrong_schema", "behavior.schema"));
    }
    let raw_game_id = required_text(raw, "game_id", "behavior", 512)?;
    let game_id = if persisted {
        if !is_anonymous_game_id(&raw_game_id) {
            return Err(BehaviorError::validation(
                "game_id_not_anonymous",
                "behavior.game_id",
            ));
        }
        raw_game_id
    } else {
        anonymous_id(&raw_game_id)
    };
    let actor_side = enum_text(raw, "actor_side", ACTOR_SIDES, "behavior")?;
    let actor_player_id = enum_text(raw, "actor_player_id", ACTOR_PLAYER_IDS, "behavior")?;
    let actor_evidence = enum_text(raw, "actor_evidence", ACTOR_EVIDENCE, "behavior")?;
    let identity_status = enum_text(raw, "identity_status", IDENTITY_STATUSES, "behavior")?;
    let visibility_status = enum_text(raw, "visibility_status", VISIBILITY_STATUSES, "behavior")?;
    let boundary_status = enum_text(raw, "boundary_status", BOUNDARY_STATUSES, "behavior")?;
    let source_event = enum_text(raw, "source_event", source_events(), "behavior")?;
    let action = behavior_action(raw.get("action"), persisted)?;
    let pre_state = public_state(raw.get("pre_state"), persisted, "behavior.pre_state")?;
    let post_state = match raw.get("post_state") {
        Some(Value::Null) => Value::Null,
        value => public_state(value, persisted, "behavior.post_state")?,
    };
    if actor_player_id != pre_state["active_player_id"].as_str().unwrap_or_default() {
        return Err(BehaviorError::validation(
            "actor_not_active_player",
            "behavior.actor_player_id",
        ));
    }
    let computed_side = if actor_player_id
        == pre_state["perspective_player_id"]
            .as_str()
            .unwrap_or_default()
    {
        "local"
    } else {
        "opponent"
    };
    if actor_side != "unknown" && actor_side != computed_side {
        return Err(BehaviorError::validation(
            "actor_side_mismatch",
            "behavior.actor_side",
        ));
    }
    validate_source_event(
        &source_event,
        &actor_side,
        action["kind"].as_str().unwrap_or_default(),
    )?;
    validate_actor_evidence(&actor_side, &actor_evidence, &source_event)?;
    let choice_status = validate_action_selection(&action, &actor_side, &source_event)?;
    validate_action_binding(
        &action,
        &pre_state,
        &actor_side,
        &actor_player_id,
        &identity_status,
        &visibility_status,
    )?;
    let behavior_eligible = required_bool(raw, "behavior_eligible", "behavior")?;
    let expected_eligible = computed_behavior_eligible(
        &actor_side,
        &actor_evidence,
        &identity_status,
        &visibility_status,
        &boundary_status,
        BehaviorEligibilityAction {
            kind: action["kind"].as_str().unwrap_or_default(),
            choice_status: &choice_status,
        },
        !post_state.is_null(),
    );
    if behavior_eligible != expected_eligible {
        return Err(BehaviorError::validation(
            "behavior_eligibility_mismatch",
            "behavior.behavior_eligible",
        ));
    }
    if raw.get("rl_training_eligible") != Some(&Value::Bool(false)) {
        return Err(BehaviorError::validation(
            "rl_training_eligible_must_be_false",
            "behavior.rl_training_eligible",
        ));
    }
    let sequence = required_u64(raw, "behavior_sequence", "behavior", 1)?;
    let observed_at = required_text(raw, "observed_at_utc", "behavior", 64)?;
    if !is_rfc3339(&observed_at) {
        return Err(BehaviorError::validation(
            "invalid_rfc3339_timestamp",
            "behavior.observed_at_utc",
        ));
    }
    Ok(Map::from_iter([
        ("schema".to_owned(), json!(schema)),
        ("game_id".to_owned(), json!(game_id)),
        ("behavior_sequence".to_owned(), json!(sequence)),
        ("observed_at_utc".to_owned(), json!(observed_at)),
        ("actor_side".to_owned(), json!(actor_side)),
        ("actor_player_id".to_owned(), json!(actor_player_id)),
        ("actor_evidence".to_owned(), json!(actor_evidence)),
        ("identity_status".to_owned(), json!(identity_status)),
        ("visibility_status".to_owned(), json!(visibility_status)),
        ("boundary_status".to_owned(), json!(boundary_status)),
        ("source_event".to_owned(), json!(source_event)),
        ("action".to_owned(), action),
        ("pre_state".to_owned(), pre_state),
        ("post_state".to_owned(), post_state),
        ("behavior_eligible".to_owned(), json!(behavior_eligible)),
        ("rl_training_eligible".to_owned(), json!(false)),
    ]))
}

fn behavior_action(value: Option<&Value>, strict: bool) -> Result<Value, BehaviorError> {
    let raw = object_option(value, "behavior.action")?;
    strict_keys(
        raw,
        ACTION_KEYS,
        if strict {
            ACTION_REQUIRED_KEYS
        } else {
            &["kind"]
        },
        "behavior.action",
    )?;
    let kind = enum_text(raw, "kind", ACTION_KINDS, "behavior.action")?;
    let source = optional_entity_id(
        raw.get("source_entity_id"),
        "behavior.action.source_entity_id",
    )?;
    let target = optional_entity_id(
        raw.get("target_entity_id"),
        "behavior.action.target_entity_id",
    )?;
    let card_id = optional_token(raw.get("card_id"), "behavior.action.card_id", 128)?;
    let mut result = Map::from_iter([
        ("kind".to_owned(), json!(kind)),
        ("source_entity_id".to_owned(), json!(source)),
        ("target_entity_id".to_owned(), json!(target)),
        ("card_id".to_owned(), json!(card_id)),
    ]);
    if raw.contains_key("sub_option") {
        result.insert(
            "sub_option".to_owned(),
            optional_signed_integer(raw.get("sub_option"), "behavior.action.sub_option", -1)?
                .map_or(Value::Null, Value::from),
        );
    }
    if raw.contains_key("board_position") {
        result.insert(
            "board_position".to_owned(),
            optional_signed_integer(
                raw.get("board_position"),
                "behavior.action.board_position",
                0,
            )?
            .map_or(Value::Null, Value::from),
        );
    }
    if raw.contains_key("choice_status") {
        result.insert(
            "choice_status".to_owned(),
            json!(enum_value(
                raw.get("choice_status"),
                CHOICE_STATUSES,
                "behavior.action.choice_status",
            )?),
        );
    }
    if raw.contains_key("choices") {
        result.insert(
            "choices".to_owned(),
            behavior_choices(raw.get("choices"), "behavior.action.choices")?,
        );
    }
    Ok(Value::Object(result))
}

fn optional_signed_integer(
    value: Option<&Value>,
    path: &str,
    minimum: i64,
) -> Result<Option<i64>, BehaviorError> {
    match value {
        None | Some(Value::Null) => Ok(None),
        Some(value) => value
            .as_i64()
            .filter(|item| *item >= minimum)
            .map(Some)
            .ok_or_else(|| BehaviorError::validation("must_be_integer", path)),
    }
}

fn positive_choice_entity_id(value: Option<&Value>, path: &str) -> Result<String, BehaviorError> {
    entity_id_value(value, path, false)
}

fn behavior_choice_entity_ids(
    value: Option<&Value>,
    path: &str,
) -> Result<Vec<Value>, BehaviorError> {
    let values = value
        .and_then(Value::as_array)
        .ok_or_else(|| BehaviorError::validation("must_be_array", path))?;
    let mut normalized = Vec::with_capacity(values.len());
    let mut seen = HashSet::new();
    for (index, value) in values.iter().enumerate() {
        let entity_id = positive_choice_entity_id(Some(value), &format!("{path}[{index}]"))?;
        if !seen.insert(entity_id.clone()) {
            return Err(BehaviorError::validation("duplicate_entity_id", path));
        }
        normalized.push(json!(entity_id));
    }
    Ok(normalized)
}

fn behavior_choices(value: Option<&Value>, path: &str) -> Result<Value, BehaviorError> {
    let values = value
        .and_then(Value::as_array)
        .ok_or_else(|| BehaviorError::validation("must_be_array", path))?;
    let mut normalized = Vec::with_capacity(values.len());
    for (index, value) in values.iter().enumerate() {
        let item_path = format!("{path}[{index}]");
        let raw = object(value, &item_path)?;
        strict_keys(raw, CHOICE_KEYS, CHOICE_KEYS, &item_path)?;
        let choice_id = match raw.get("choice_id") {
            Some(Value::Null) => Value::Null,
            Some(value) => value
                .as_u64()
                .filter(|item| *item > 0)
                .map(Value::from)
                .ok_or_else(|| {
                    BehaviorError::validation("must_be_integer", format!("{item_path}.choice_id"))
                })?,
            None => unreachable!("choice keys were checked above"),
        };
        let choice_type = token_value(
            raw.get("choice_type"),
            &format!("{item_path}.choice_type"),
            false,
            64,
        )?;
        let source_entity_id = positive_choice_entity_id(
            raw.get("source_entity_id"),
            &format!("{item_path}.source_entity_id"),
        )?;
        let option_entity_ids = behavior_choice_entity_ids(
            raw.get("option_entity_ids"),
            &format!("{item_path}.option_entity_ids"),
        )?;
        let selected_entity_ids = behavior_choice_entity_ids(
            raw.get("selected_entity_ids"),
            &format!("{item_path}.selected_entity_ids"),
        )?;
        let status = enum_value(
            raw.get("status"),
            CHOICE_ITEM_STATUSES,
            &format!("{item_path}.status"),
        )?;
        normalized.push(json!({
            "choice_id": choice_id,
            "choice_type": choice_type,
            "source_entity_id": source_entity_id,
            "option_entity_ids": option_entity_ids,
            "selected_entity_ids": selected_entity_ids,
            "status": status,
        }));
    }
    Ok(Value::Array(normalized))
}

fn public_state(value: Option<&Value>, strict: bool, path: &str) -> Result<Value, BehaviorError> {
    let raw = object_option(value, path)?;
    if strict {
        strict_keys(raw, STATE_KEYS, STATE_KEYS, path)?;
    }
    let state_id = token_value(raw.get("state_id"), &format!("{path}.state_id"), false, 256)?;
    let turn = integer_value(raw.get("turn"), &format!("{path}.turn"), 1)?;
    let active = enum_value(
        raw.get("active_player_id"),
        ACTOR_PLAYER_IDS,
        &format!("{path}.active_player_id"),
    )?;
    let perspective = enum_value(
        raw.get("perspective_player_id"),
        ACTOR_PLAYER_IDS,
        &format!("{path}.perspective_player_id"),
    )?;
    if perspective != "friendly" {
        return Err(BehaviorError::validation(
            "perspective_must_be_friendly",
            format!("{path}.perspective_player_id"),
        ));
    }
    let friendly = public_player(
        raw.get("friendly"),
        "friendly",
        strict,
        &format!("{path}.friendly"),
    )?;
    let opponent = public_player(
        raw.get("opponent"),
        "opponent",
        strict,
        &format!("{path}.opponent"),
    )?;
    let patch = optional_token(raw.get("patch"), &format!("{path}.patch"), 128)?;
    let mode = optional_token(raw.get("mode"), &format!("{path}.mode"), 128)?;
    Ok(json!({
        "state_id": state_id,
        "turn": turn,
        "active_player_id": active,
        "perspective_player_id": perspective,
        "friendly": friendly,
        "opponent": opponent,
        "patch": patch,
        "mode": mode,
    }))
}

fn public_player(
    value: Option<&Value>,
    role: &str,
    strict: bool,
    path: &str,
) -> Result<Value, BehaviorError> {
    let raw = object_option(value, path)?;
    if strict {
        strict_keys(raw, PLAYER_KEYS, PLAYER_REQUIRED_KEYS, path)?;
        if raw.get("player_id").and_then(Value::as_str) != Some(role) {
            return Err(BehaviorError::validation(
                "player_role_mismatch",
                format!("{path}.player_id"),
            ));
        }
    }
    let hero = raw
        .get("hero")
        .filter(|value| !value.is_null())
        .ok_or_else(|| BehaviorError::validation("hero_required", format!("{path}.hero")))?;
    let hero = public_entity(hero, false, strict, &format!("{path}.hero"))?;
    let hero_power =
        optional_public_entity(raw.get("hero_power"), strict, &format!("{path}.hero_power"))?;
    let weapon = optional_public_entity(raw.get("weapon"), strict, &format!("{path}.weapon"))?;
    let hand = entity_sequence(
        raw.get("hand"),
        role == "opponent",
        strict,
        &format!("{path}.hand"),
    )?;
    let board = entity_sequence(raw.get("board"), false, strict, &format!("{path}.board"))?;
    if hand.as_array().is_some_and(|items| items.len() > 10) {
        return Err(BehaviorError::validation(
            "public_hand_capacity_exceeded",
            format!("{path}.hand"),
        ));
    }
    if board.as_array().is_some_and(|items| items.len() > 7) {
        return Err(BehaviorError::validation(
            "public_board_capacity_exceeded",
            format!("{path}.board"),
        ));
    }
    let mut result = Map::from_iter([
        ("player_id".to_owned(), json!(role)),
        ("hero".to_owned(), hero),
        ("hero_power".to_owned(), hero_power),
        ("weapon".to_owned(), weapon),
        ("hand".to_owned(), hand),
        ("board".to_owned(), board),
        (
            "mana".to_owned(),
            json!(optional_integer(raw.get("mana"), &format!("{path}.mana"))?),
        ),
        (
            "max_mana".to_owned(),
            json!(optional_integer(
                raw.get("max_mana"),
                &format!("{path}.max_mana")
            )?),
        ),
        (
            "armor".to_owned(),
            json!(optional_integer(
                raw.get("armor"),
                &format!("{path}.armor")
            )?),
        ),
        (
            "deck_size".to_owned(),
            json!(optional_integer(
                raw.get("deck_size"),
                &format!("{path}.deck_size")
            )?),
        ),
        (
            "fatigue".to_owned(),
            json!(optional_integer(
                raw.get("fatigue"),
                &format!("{path}.fatigue")
            )?),
        ),
        (
            "hero_power_available".to_owned(),
            json!(optional_bool(
                raw.get("hero_power_available"),
                &format!("{path}.hero_power_available")
            )?),
        ),
        (
            "spell_power".to_owned(),
            json!(optional_integer(
                raw.get("spell_power"),
                &format!("{path}.spell_power")
            )?),
        ),
    ]);
    if let Some(value) = raw.get("public_rule_tags") {
        result.insert(
            "public_rule_tags".to_owned(),
            public_rule_tags(value, strict, &format!("{path}.public_rule_tags"))?,
        );
    }
    if let Some(value) = raw.get("public_rule_tags_complete") {
        result.insert(
            "public_rule_tags_complete".to_owned(),
            json!(bool_value(
                Some(value),
                &format!("{path}.public_rule_tags_complete")
            )?),
        );
    }
    Ok(Value::Object(result))
}

fn public_rule_tags(value: &Value, strict: bool, path: &str) -> Result<Value, BehaviorError> {
    let raw = object(value, path)?;
    if strict {
        strict_keys(raw, PUBLIC_RULE_TAG_KEYS, &[], path)?;
    }
    let mut result = Map::new();
    for key in PUBLIC_RULE_TAG_KEYS {
        if let Some(value) = raw.get(*key) {
            let value = value
                .as_i64()
                .and_then(|item| i32::try_from(item).ok())
                .ok_or_else(|| {
                    BehaviorError::validation("must_be_integer", format!("{path}.{key}"))
                })?;
            result.insert((*key).to_owned(), json!(value));
        }
    }
    Ok(Value::Object(result))
}

fn optional_public_entity(
    value: Option<&Value>,
    strict: bool,
    path: &str,
) -> Result<Value, BehaviorError> {
    match value {
        None | Some(Value::Null) => Ok(Value::Null),
        Some(value) => public_entity(value, false, strict, path),
    }
}

fn entity_sequence(
    value: Option<&Value>,
    hidden: bool,
    strict: bool,
    path: &str,
) -> Result<Value, BehaviorError> {
    let items = match value {
        None => &[][..],
        Some(Value::Array(items)) => items.as_slice(),
        Some(_) => return Err(BehaviorError::validation("must_be_array", path)),
    };
    items
        .iter()
        .enumerate()
        .map(|(index, item)| public_entity(item, hidden, strict, &format!("{path}[{index}]")))
        .collect::<Result<Vec<_>, _>>()
        .map(Value::Array)
}

fn public_entity(
    value: &Value,
    hidden: bool,
    strict: bool,
    path: &str,
) -> Result<Value, BehaviorError> {
    let raw = object(value, path)?;
    if hidden {
        if strict {
            strict_keys(raw, HIDDEN_ENTITY_KEYS, HIDDEN_ENTITY_KEYS, path)?;
            if raw.get("visibility").and_then(Value::as_str) != Some("hidden") {
                return Err(BehaviorError::validation(
                    "hidden_entity_visibility_required",
                    path,
                ));
            }
        }
        return Ok(json!({
            "entity_id": optional_entity_id(raw.get("entity_id"), &format!("{path}.entity_id"))?,
            "visibility": "hidden",
        }));
    }
    if strict {
        strict_keys(
            raw,
            PUBLIC_ENTITY_KEYS,
            &["entity_id", "card_id", "card_type"],
            path,
        )?;
    }
    let entity_id = entity_id_value(raw.get("entity_id"), &format!("{path}.entity_id"), false)?;
    let card_id = optional_token(raw.get("card_id"), &format!("{path}.card_id"), 128)?;
    let card_type = raw.get("card_type").map_or_else(
        || Ok("UNKNOWN".to_owned()),
        |value| {
            value
                .as_str()
                .map(|value| value.trim().to_ascii_uppercase())
                .ok_or_else(|| {
                    BehaviorError::validation("must_be_string", format!("{path}.card_type"))
                })
        },
    )?;
    if !CARD_TYPES.contains(&card_type.as_str()) {
        return Err(BehaviorError::validation(
            "invalid_value",
            format!("{path}.card_type"),
        ));
    }
    let mut result = Map::from_iter([
        ("entity_id".to_owned(), json!(entity_id)),
        ("card_id".to_owned(), json!(card_id)),
        ("card_type".to_owned(), json!(card_type)),
    ]);
    for key in [
        "cost",
        "attack",
        "health",
        "current_health",
        "attacks_remaining",
        "durability",
        "current_durability",
    ] {
        if let Some(value) = raw.get(key) {
            result.insert(
                key.to_owned(),
                json!(integer_value(Some(value), &format!("{path}.{key}"), 0)?),
            );
        }
    }
    for key in [
        "playable",
        "can_attack",
        "current_health_known",
        "taunt",
        "divine_shield",
        "stealth",
        "poisonous",
        "lifesteal",
        "windfury",
        "mega_windfury",
        "rush",
        "charge",
        "reborn",
        "dormant",
        "immune",
        "summoned_this_turn",
        "frozen",
    ] {
        if let Some(value) = raw.get(key) {
            result.insert(
                key.to_owned(),
                json!(bool_value(Some(value), &format!("{path}.{key}"))?),
            );
        }
    }
    Ok(Value::Object(result))
}

#[derive(Clone)]
struct EntityBinding {
    role: String,
    zone: String,
    value: Value,
}

fn entities(state: &Value) -> Result<HashMap<String, EntityBinding>, BehaviorError> {
    let mut result = HashMap::new();
    for role in ["friendly", "opponent"] {
        let player = state[role].as_object().ok_or_else(|| {
            BehaviorError::validation("must_be_object", format!("behavior.pre_state.{role}"))
        })?;
        for zone in ["hero", "hero_power", "weapon"] {
            if let Some(entity) = player.get(zone).and_then(Value::as_object) {
                let entity_id = entity
                    .get("entity_id")
                    .and_then(Value::as_str)
                    .unwrap_or_default();
                insert_entity(
                    &mut result,
                    entity_id,
                    role,
                    zone,
                    Value::Object(entity.clone()),
                )?;
            }
        }
        for zone in ["hand", "board"] {
            for entity in player
                .get(zone)
                .and_then(Value::as_array)
                .into_iter()
                .flatten()
            {
                let entity_id = entity["entity_id"].as_str().unwrap_or_default();
                insert_entity(&mut result, entity_id, role, zone, entity.clone())?;
            }
        }
    }
    Ok(result)
}

fn insert_entity(
    entities: &mut HashMap<String, EntityBinding>,
    entity_id: &str,
    role: &str,
    zone: &str,
    value: Value,
) -> Result<(), BehaviorError> {
    if entity_id.is_empty() {
        return Ok(());
    }
    if entities
        .insert(
            entity_id.to_owned(),
            EntityBinding {
                role: role.to_owned(),
                zone: zone.to_owned(),
                value,
            },
        )
        .is_some()
    {
        return Err(BehaviorError::validation(
            "duplicate_entity_id",
            format!("behavior.pre_state.{role}.{zone}"),
        ));
    }
    Ok(())
}

fn validate_action_binding(
    action: &Value,
    pre_state: &Value,
    actor_side: &str,
    actor_player_id: &str,
    identity_status: &str,
    visibility_status: &str,
) -> Result<(), BehaviorError> {
    let kind = action["kind"].as_str().unwrap_or_default();
    let source_id = action["source_entity_id"].as_str().unwrap_or_default();
    let target_id = action["target_entity_id"].as_str().unwrap_or_default();
    let card_id = action["card_id"].as_str().unwrap_or_default();
    let entities = entities(pre_state)?;
    if actor_side == "unknown" {
        if identity_status != "unknown" || visibility_status != "hidden_source" {
            return Err(BehaviorError::validation(
                "unknown_actor_tier_mismatch",
                "behavior",
            ));
        }
        if !source_id.is_empty()
            && entities
                .get(source_id)
                .is_none_or(|source| source.role != actor_player_id)
        {
            return Err(BehaviorError::validation(
                "source_owner_mismatch",
                "behavior.action",
            ));
        }
        return Ok(());
    }
    if kind == "end_turn" {
        if !source_id.is_empty() || !target_id.is_empty() || !card_id.is_empty() {
            return Err(BehaviorError::validation(
                "end_turn_must_not_have_entities",
                "behavior.action",
            ));
        }
        if identity_status != "event_only" || visibility_status != "public_pre_state" {
            return Err(BehaviorError::validation(
                "end_turn_tier_mismatch",
                "behavior.action",
            ));
        }
        return Ok(());
    }
    if identity_status == "unknown" {
        return validate_known_actor_unknown_identity(
            kind,
            source_id,
            target_id,
            card_id,
            actor_player_id,
            &entities,
        );
    }
    let source = entities
        .get(source_id)
        .ok_or_else(|| BehaviorError::validation("source_not_in_pre_state", "behavior.action"))?;
    if source.role != actor_player_id {
        return Err(BehaviorError::validation(
            "source_owner_mismatch",
            "behavior.action",
        ));
    }
    if !target_id.is_empty() && !entities.contains_key(target_id) {
        return Err(BehaviorError::validation(
            "target_not_in_pre_state",
            "behavior.action",
        ));
    }
    if kind == "play_card" {
        if source.zone != "hand" {
            return Err(BehaviorError::validation(
                "play_source_not_in_hand",
                "behavior.action",
            ));
        }
        if card_id.is_empty() {
            return Err(BehaviorError::validation(
                "play_card_id_required",
                "behavior.action",
            ));
        }
        if actor_side == "opponent" {
            if source.value["visibility"].as_str() != Some("hidden") {
                return Err(BehaviorError::validation(
                    "opponent_hand_source_must_be_hidden",
                    "behavior.action",
                ));
            }
            if identity_status != "revealed_after_action"
                || visibility_status != "revealed_post_action"
            {
                return Err(BehaviorError::validation(
                    "opponent_hidden_play_tier_mismatch",
                    "behavior.action",
                ));
            }
        } else {
            if identity_status != "exact_public_entity" || visibility_status != "public_pre_state" {
                return Err(BehaviorError::validation(
                    "local_play_tier_mismatch",
                    "behavior.action",
                ));
            }
            if source.value["card_id"].as_str() != Some(card_id) {
                return Err(BehaviorError::validation(
                    "source_card_id_mismatch",
                    "behavior.action",
                ));
            }
        }
        return Ok(());
    }
    if identity_status != "exact_public_entity" || visibility_status != "public_pre_state" {
        return Err(BehaviorError::validation(
            "public_action_tier_mismatch",
            "behavior.action",
        ));
    }
    if card_id.is_empty() || source.value["card_id"].as_str() != Some(card_id) {
        return Err(BehaviorError::validation(
            "source_card_id_mismatch",
            "behavior.action",
        ));
    }
    if kind == "attack" {
        if !["hero", "board"].contains(&source.zone.as_str()) {
            return Err(BehaviorError::validation(
                "attack_source_not_character",
                "behavior.action",
            ));
        }
        if target_id.is_empty() {
            return Err(BehaviorError::validation(
                "attack_target_required",
                "behavior.action",
            ));
        }
        let target = &entities[target_id];
        if target.role == actor_player_id || !["hero", "board"].contains(&target.zone.as_str()) {
            return Err(BehaviorError::validation(
                "attack_target_not_enemy_character",
                "behavior.action",
            ));
        }
    } else if kind == "hero_power" && source.zone != "hero_power" {
        return Err(BehaviorError::validation(
            "hero_power_source_mismatch",
            "behavior.action",
        ));
    } else if kind == "location_activate" {
        if source.zone != "board" {
            return Err(BehaviorError::validation(
                "location_source_not_on_board",
                "behavior.action",
            ));
        }
        if source.value["card_type"].as_str() != Some("LOCATION") {
            return Err(BehaviorError::validation(
                "location_source_not_location",
                "behavior.action",
            ));
        }
    }
    Ok(())
}

fn validate_action_selection(
    action: &Value,
    actor_side: &str,
    source_event: &str,
) -> Result<String, BehaviorError> {
    let raw = object(action, "behavior.action")?;
    let choice_status = raw
        .get("choice_status")
        .and_then(Value::as_str)
        .unwrap_or("not_observed");
    let sub_option = raw.get("sub_option").and_then(Value::as_i64);
    let board_position = raw.get("board_position").and_then(Value::as_i64);
    let choices = raw
        .get("choices")
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .unwrap_or(&[]);
    if raw["kind"] == "end_turn" {
        if !["none", "not_observed"].contains(&choice_status) {
            return Err(BehaviorError::validation(
                "end_turn_choice_status_invalid",
                "behavior.action.choice_status",
            ));
        }
        if !matches!(sub_option, None | Some(-1))
            || !matches!(board_position, None | Some(0))
            || !choices.is_empty()
        {
            return Err(BehaviorError::validation(
                "end_turn_has_selection",
                "behavior.action",
            ));
        }
    }
    if choice_status == "not_observed" {
        if sub_option.is_some() || board_position.is_some() || !choices.is_empty() {
            return Err(BehaviorError::validation(
                "unobserved_choice_has_power_fields",
                "behavior.action",
            ));
        }
        return Ok(choice_status.to_owned());
    }
    if choice_status == "none" {
        if !matches!(sub_option, None | Some(-1)) || !choices.is_empty() {
            return Err(BehaviorError::validation(
                "choice_none_has_selection",
                "behavior.action",
            ));
        }
        return Ok(choice_status.to_owned());
    }
    if choice_status == "selected" && (actor_side != "local" || source_event != "hdt_power_log") {
        return Err(BehaviorError::validation(
            "selected_choice_requires_local_power",
            "behavior.action.choice_status",
        ));
    }
    if choice_status == "selected" && choices.is_empty() {
        return Err(BehaviorError::validation(
            "selected_choice_missing",
            "behavior.action.choices",
        ));
    }
    let source_id = raw["source_entity_id"].as_str().unwrap_or_default();
    for (index, choice) in choices.iter().enumerate() {
        let path = format!("behavior.action.choices[{index}]");
        let item = object(choice, &path)?;
        if item["source_entity_id"].as_str().unwrap_or_default() != source_id {
            return Err(BehaviorError::validation("choice_source_mismatch", path));
        }
        let options = item["option_entity_ids"]
            .as_array()
            .expect("normalized choice options");
        let selected = item["selected_entity_ids"]
            .as_array()
            .expect("normalized selected choices");
        let option_ids = options.iter().collect::<HashSet<_>>();
        if selected
            .iter()
            .any(|entity_id| !option_ids.contains(entity_id))
        {
            return Err(BehaviorError::validation(
                "selected_entity_not_offered",
                path,
            ));
        }
        if choice_status == "selected"
            && (item["status"] != "selected" || options.is_empty() || selected.is_empty())
        {
            return Err(BehaviorError::validation(
                "selected_choice_incomplete",
                path,
            ));
        }
    }
    Ok(choice_status.to_owned())
}

fn validate_known_actor_unknown_identity(
    kind: &str,
    source_id: &str,
    target_id: &str,
    card_id: &str,
    actor_player_id: &str,
    entities: &HashMap<String, EntityBinding>,
) -> Result<(), BehaviorError> {
    let source = if source_id.is_empty() {
        None
    } else {
        let source = entities.get(source_id).ok_or_else(|| {
            BehaviorError::validation("source_not_in_pre_state", "behavior.action")
        })?;
        if source.role != actor_player_id {
            return Err(BehaviorError::validation(
                "source_owner_mismatch",
                "behavior.action",
            ));
        }
        Some(source)
    };
    let target = if target_id.is_empty() {
        None
    } else {
        Some(entities.get(target_id).ok_or_else(|| {
            BehaviorError::validation("target_not_in_pre_state", "behavior.action")
        })?)
    };
    if let Some(source) = source {
        let zone_error = match kind {
            "play_card" if source.zone != "hand" => Some("play_source_not_in_hand"),
            "attack" if !["hero", "board"].contains(&source.zone.as_str()) => {
                Some("attack_source_not_character")
            }
            "hero_power" if source.zone != "hero_power" => Some("hero_power_source_mismatch"),
            "location_activate" if source.zone != "board" => Some("location_source_not_on_board"),
            "location_activate" if source.value["card_type"].as_str() != Some("LOCATION") => {
                Some("location_source_not_location")
            }
            _ => None,
        };
        if let Some(code) = zone_error {
            return Err(BehaviorError::validation(code, "behavior.action"));
        }
        let public_card_id = source.value["card_id"].as_str().unwrap_or_default();
        if !card_id.is_empty() && !public_card_id.is_empty() && public_card_id != card_id {
            return Err(BehaviorError::validation(
                "source_card_id_mismatch",
                "behavior.action",
            ));
        }
    }
    if kind == "attack"
        && let Some(target) = target
        && (target.role == actor_player_id || !["hero", "board"].contains(&target.zone.as_str()))
    {
        return Err(BehaviorError::validation(
            "attack_target_not_enemy_character",
            "behavior.action",
        ));
    }
    Ok(())
}

fn validate_source_event(
    source_event: &str,
    actor_side: &str,
    action_kind: &str,
) -> Result<(), BehaviorError> {
    let (expected_side, expected_kind) = match source_event {
        "hdt_power_log" => (Some("local"), None),
        "hdt_replay_power" => (None, None),
        "player_play" => (Some("local"), Some("play_card")),
        "player_attack" => (Some("local"), Some("attack")),
        "player_hero_power" => (Some("local"), Some("hero_power")),
        "turn_passed_to_opponent" => (Some("local"), Some("end_turn")),
        "opponent_play" => (Some("opponent"), Some("play_card")),
        "opponent_attack" => (Some("opponent"), Some("attack")),
        "opponent_hero_power" => (Some("opponent"), Some("hero_power")),
        "turn_passed_to_player" => (Some("opponent"), Some("end_turn")),
        "unknown" => (None, None),
        _ => {
            return Err(BehaviorError::validation(
                "invalid_value",
                "behavior.source_event",
            ));
        }
    };
    if actor_side != "unknown" && expected_side.is_some_and(|side| side != actor_side) {
        return Err(BehaviorError::validation(
            "source_event_actor_mismatch",
            "behavior.source_event",
        ));
    }
    if expected_kind.is_some_and(|kind| kind != action_kind) {
        return Err(BehaviorError::validation(
            "source_event_action_mismatch",
            "behavior.source_event",
        ));
    }
    if source_event == "unknown" && actor_side != "unknown" {
        return Err(BehaviorError::validation(
            "known_actor_requires_source_event",
            "behavior.source_event",
        ));
    }
    Ok(())
}

fn validate_actor_evidence(
    actor_side: &str,
    evidence: &str,
    source_event: &str,
) -> Result<(), BehaviorError> {
    let fail = |code| BehaviorError::validation(code, "behavior.actor_evidence");
    if actor_side == "unknown" {
        return if evidence == "unknown" && source_event == "unknown" {
            Ok(())
        } else {
            Err(fail("unknown_actor_evidence_mismatch"))
        };
    }
    if evidence == "unknown" {
        return Err(fail("known_actor_requires_evidence"));
    }
    if (evidence == "hdt_player_event" && actor_side != "local")
        || (evidence == "hdt_opponent_event" && actor_side != "opponent")
    {
        return Err(fail("actor_evidence_side_mismatch"));
    }
    if evidence == "hdt_power_log" && actor_side != "local" {
        return Err(fail("power_evidence_must_be_local"));
    }
    if source_event == "hdt_power_log" && evidence != "hdt_power_log" {
        return Err(fail("power_source_requires_power_evidence"));
    }
    if source_event == "hdt_replay_power" && evidence != "hdt_replay_power" {
        return Err(fail("replay_source_requires_replay_evidence"));
    }
    if evidence == "hdt_replay_power" && source_event != "hdt_replay_power" {
        return Err(fail("replay_evidence_requires_replay_source"));
    }
    if source_event.starts_with("player_")
        && !["hdt_player_event", "source_owner"].contains(&evidence)
    {
        return Err(fail("player_event_evidence_mismatch"));
    }
    if source_event.starts_with("opponent_")
        && !["hdt_opponent_event", "source_owner"].contains(&evidence)
    {
        return Err(fail("opponent_event_evidence_mismatch"));
    }
    if source_event.starts_with("turn_passed_") && evidence != "active_player" {
        return Err(fail("turn_event_requires_active_player_evidence"));
    }
    Ok(())
}

struct BehaviorEligibilityAction<'a> {
    kind: &'a str,
    choice_status: &'a str,
}

fn computed_behavior_eligible(
    actor_side: &str,
    actor_evidence: &str,
    identity_status: &str,
    visibility_status: &str,
    boundary_status: &str,
    action: BehaviorEligibilityAction<'_>,
    has_post_state: bool,
) -> bool {
    if !has_post_state
        || actor_side == "unknown"
        || actor_evidence == "unknown"
        || boundary_status != "isolated"
        || action.choice_status == "unresolved"
    {
        return false;
    }
    if action.kind == "end_turn" {
        return identity_status == "event_only" && visibility_status == "public_pre_state";
    }
    if identity_status == "exact_public_entity" {
        return visibility_status == "public_pre_state";
    }
    actor_side == "opponent"
        && action.kind == "play_card"
        && identity_status == "revealed_after_action"
        && visibility_status == "revealed_post_action"
}

fn source_events() -> &'static [&'static str] {
    &[
        "hdt_power_log",
        "hdt_replay_power",
        "player_play",
        "player_attack",
        "player_hero_power",
        "turn_passed_to_opponent",
        "opponent_play",
        "opponent_attack",
        "opponent_hero_power",
        "turn_passed_to_player",
        "unknown",
    ]
}

fn object<'a>(value: &'a Value, path: &str) -> Result<&'a Map<String, Value>, BehaviorError> {
    value
        .as_object()
        .ok_or_else(|| BehaviorError::validation("must_be_object", path))
}

fn object_option<'a>(
    value: Option<&'a Value>,
    path: &str,
) -> Result<&'a Map<String, Value>, BehaviorError> {
    value
        .and_then(Value::as_object)
        .ok_or_else(|| BehaviorError::validation("must_be_object", path))
}

fn strict_keys(
    raw: &Map<String, Value>,
    allowed: &[&str],
    required: &[&str],
    path: &str,
) -> Result<(), BehaviorError> {
    if let Some(key) = raw.keys().find(|key| !allowed.contains(&key.as_str())) {
        return Err(BehaviorError::validation(
            format!("unknown_field:{key}"),
            path,
        ));
    }
    if let Some(key) = required.iter().find(|key| !raw.contains_key(**key)) {
        return Err(BehaviorError::validation(
            format!("missing_field:{key}"),
            path,
        ));
    }
    Ok(())
}

fn required_text(
    raw: &Map<String, Value>,
    key: &str,
    path: &str,
    limit: usize,
) -> Result<String, BehaviorError> {
    text_value(raw.get(key), &format!("{path}.{key}"), false, limit)
}

fn text_value(
    value: Option<&Value>,
    path: &str,
    allow_empty: bool,
    limit: usize,
) -> Result<String, BehaviorError> {
    let text = value
        .and_then(Value::as_str)
        .ok_or_else(|| BehaviorError::validation("must_be_string", path))?
        .trim()
        .to_owned();
    if (!allow_empty && text.is_empty()) || text.chars().count() > limit {
        return Err(BehaviorError::validation("invalid_length", path));
    }
    Ok(text)
}

fn token_value(
    value: Option<&Value>,
    path: &str,
    allow_empty: bool,
    limit: usize,
) -> Result<String, BehaviorError> {
    let text = text_value(value, path, allow_empty, limit)?;
    if !text.is_empty() && !text.bytes().all(safe_token_byte) {
        return Err(BehaviorError::validation("unsafe_token", path));
    }
    Ok(text)
}

fn optional_token(
    value: Option<&Value>,
    path: &str,
    limit: usize,
) -> Result<String, BehaviorError> {
    match value {
        None => Ok(String::new()),
        Some(value) => token_value(Some(value), path, true, limit),
    }
}

fn safe_token_byte(value: u8) -> bool {
    value.is_ascii_alphanumeric() || matches!(value, b'_' | b'.' | b':' | b'-')
}

fn entity_id_value(
    value: Option<&Value>,
    path: &str,
    allow_empty: bool,
) -> Result<String, BehaviorError> {
    let text = match value {
        Some(Value::String(value)) => value.clone(),
        Some(Value::Number(value)) if value.as_i64().is_some() || value.as_u64().is_some() => {
            value.to_string()
        }
        None | Some(Value::Null) if allow_empty => String::new(),
        _ => return Err(BehaviorError::validation("invalid_entity_id", path)),
    };
    let normalized = text.trim().to_owned();
    if (!allow_empty && normalized.is_empty())
        || normalized.chars().count() > 128
        || (!normalized.is_empty() && !normalized.bytes().all(safe_token_byte))
    {
        return Err(BehaviorError::validation("invalid_entity_id", path));
    }
    Ok(normalized)
}

fn optional_entity_id(value: Option<&Value>, path: &str) -> Result<String, BehaviorError> {
    entity_id_value(value, path, true)
}

fn enum_text(
    raw: &Map<String, Value>,
    key: &str,
    allowed: &[&str],
    path: &str,
) -> Result<String, BehaviorError> {
    enum_value(raw.get(key), allowed, &format!("{path}.{key}"))
}

fn enum_value(
    value: Option<&Value>,
    allowed: &[&str],
    path: &str,
) -> Result<String, BehaviorError> {
    let normalized = text_value(value, path, false, 256)?.to_ascii_lowercase();
    if !allowed.contains(&normalized.as_str()) {
        return Err(BehaviorError::validation("invalid_value", path));
    }
    Ok(normalized)
}

fn required_u64(
    raw: &Map<String, Value>,
    key: &str,
    path: &str,
    minimum: u64,
) -> Result<u64, BehaviorError> {
    integer_value(raw.get(key), &format!("{path}.{key}"), minimum)
}

fn integer_value(value: Option<&Value>, path: &str, minimum: u64) -> Result<u64, BehaviorError> {
    value
        .and_then(Value::as_u64)
        .filter(|value| *value >= minimum)
        .ok_or_else(|| BehaviorError::validation("must_be_integer", path))
}

fn optional_integer(value: Option<&Value>, path: &str) -> Result<u64, BehaviorError> {
    match value {
        None => Ok(0),
        Some(value) => integer_value(Some(value), path, 0),
    }
}

fn required_bool(raw: &Map<String, Value>, key: &str, path: &str) -> Result<bool, BehaviorError> {
    bool_value(raw.get(key), &format!("{path}.{key}"))
}

fn bool_value(value: Option<&Value>, path: &str) -> Result<bool, BehaviorError> {
    value
        .and_then(Value::as_bool)
        .ok_or_else(|| BehaviorError::validation("must_be_boolean", path))
}

fn optional_bool(value: Option<&Value>, path: &str) -> Result<bool, BehaviorError> {
    match value {
        None => Ok(false),
        Some(value) => bool_value(Some(value), path),
    }
}

fn canonical_sha256(value: &Value) -> Result<String, BehaviorError> {
    let payload = serde_json::to_vec(value)
        .map_err(|_| BehaviorError::storage("behavior_serialize_failed", "behavior"))?;
    Ok(format!("{:x}", Sha256::digest(payload)))
}

fn is_lower_sha256(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|item| item.is_ascii_digit() || (b'a'..=b'f').contains(&item))
}

fn is_anonymous_game_id(value: &str) -> bool {
    value.len() == 21
        && value.starts_with("anon-")
        && value[5..]
            .bytes()
            .all(|item| item.is_ascii_digit() || (b'a'..=b'f').contains(&item))
}

fn is_rfc3339(value: &str) -> bool {
    let Some((date, time_and_offset)) = value.split_once('T') else {
        return false;
    };
    let date_parts = date.split('-').collect::<Vec<_>>();
    if date_parts.len() != 3
        || date_parts[0].len() != 4
        || !valid_number(date_parts[0], 1, 9999)
        || !valid_number(date_parts[1], 1, 12)
    {
        return false;
    }
    let year = date_parts[0].parse::<u32>().unwrap_or_default();
    let month = date_parts[1].parse::<u32>().unwrap_or_default();
    let maximum_day = match month {
        2 if year.is_multiple_of(400) || (year.is_multiple_of(4) && !year.is_multiple_of(100)) => {
            29
        }
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    };
    if !valid_number(date_parts[2], 1, maximum_day) {
        return false;
    }
    let time = if let Some(time) = time_and_offset.strip_suffix('Z') {
        time
    } else {
        let Some(index) = time_and_offset
            .char_indices()
            .skip(1)
            .find_map(|(index, item)| matches!(item, '+' | '-').then_some(index))
        else {
            return false;
        };
        let offset = &time_and_offset[index + 1..];
        let parts = offset.split(':').collect::<Vec<_>>();
        if parts.len() != 2 || !valid_number(parts[0], 0, 23) || !valid_number(parts[1], 0, 59) {
            return false;
        }
        &time_and_offset[..index]
    };
    let clock = time.split('.').next().unwrap_or(time);
    let parts = clock.split(':').collect::<Vec<_>>();
    parts.len() == 3
        && valid_number(parts[0], 0, 23)
        && valid_number(parts[1], 0, 59)
        && valid_number(parts[2], 0, 59)
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::thread;

    static TEMP_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    struct TempDirectory(PathBuf);

    impl TempDirectory {
        fn new(label: &str) -> Self {
            let sequence = TEMP_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "metacompanion-rust-behavior-{label}-{}-{sequence}",
                std::process::id()
            ));
            fs::create_dir_all(&path).expect("create behavior test directory");
            Self(path)
        }

        fn path(&self) -> PathBuf {
            self.0.join(BEHAVIOR_LOG_FILENAME)
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
            "attack": 3,
            "health": 3,
            "current_health": 3,
            "playable": true,
            "can_attack": true,
            "attacks_remaining": 1,
            "name": "绝不能落盘的本地化名称",
            "controller_id": 999,
            "card_text": "绝不能落盘的规则文本"
        })
    }

    fn state(active: &str, state_id: &str) -> Value {
        json!({
            "state_id": state_id,
            "turn": 7,
            "active_player_id": active,
            "perspective_player_id": "friendly",
            "friendly": {
                "player_id": "private-player-one",
                "player_name": "Alice Private",
                "hero": entity("f-hero", "FRIENDLY_HERO", "HERO"),
                "hero_power": entity("f-power", "FRIENDLY_POWER", "HERO_POWER"),
                "weapon": null,
                "hand": [entity("f-hand", "FRIENDLY_CARD", "MINION")],
                "board": [
                    entity("f-minion", "FRIENDLY_MINION", "MINION"),
                    entity("f-location", "FRIENDLY_LOCATION", "LOCATION")
                ],
                "mana": 7,
                "max_mana": 7,
                "armor": 0,
                "deck_size": 18,
                "fatigue": 0,
                "hero_power_available": true,
                "spell_power": 0
            },
            "opponent": {
                "player_id": "private-player-two",
                "opponent_name": "Bob Private",
                "hero": entity("o-hero", "OPPONENT_HERO", "HERO"),
                "hero_power": entity("o-power", "OPPONENT_POWER", "HERO_POWER"),
                "weapon": null,
                "hand": [entity("o-hand", "SECRET_OPPONENT_CARD", "SPELL")],
                "board": [
                    entity("o-minion", "OPPONENT_MINION", "MINION"),
                    entity("o-location", "OPPONENT_LOCATION", "LOCATION")
                ],
                "mana": 6,
                "max_mana": 7,
                "armor": 0,
                "deck_size": 17,
                "fatigue": 0,
                "hero_power_available": true,
                "spell_power": 0
            },
            "patch": "test-patch",
            "mode": "standard",
            "metadata": {
                "password": "never-write-password",
                "authorization": "Bearer never-write-token",
                "raw_power_log": "private raw Power.log line"
            }
        })
    }

    fn add_public_legality_entity_evidence(entity: &mut Value) {
        let raw = entity.as_object_mut().expect("public entity fixture");
        for (key, value) in [
            ("current_health_known", json!(true)),
            ("taunt", json!(false)),
            ("divine_shield", json!(false)),
            ("stealth", json!(false)),
            ("poisonous", json!(false)),
            ("lifesteal", json!(false)),
            ("windfury", json!(false)),
            ("mega_windfury", json!(false)),
            ("rush", json!(false)),
            ("charge", json!(false)),
            ("reborn", json!(false)),
            ("dormant", json!(false)),
            ("immune", json!(false)),
            ("summoned_this_turn", json!(false)),
            ("frozen", json!(false)),
        ] {
            raw.insert(key.to_owned(), value);
        }
    }

    fn state_with_public_legality_evidence(active: &str, state_id: &str) -> Value {
        let mut value = state(active, state_id);
        for role in ["friendly", "opponent"] {
            let player = value[role].as_object_mut().expect("public player fixture");
            player.insert(
                "public_rule_tags".to_owned(),
                json!({
                    "STEADY_SHOT_CAN_TARGET": 1,
                    "HERO_POWER_DOUBLE": 0,
                    "PRIVATE_UNSAFE_TAG": 8675309,
                }),
            );
            player.insert("public_rule_tags_complete".to_owned(), json!(true));
            for zone in ["hero", "hero_power", "weapon"] {
                if let Some(entity) = player.get_mut(zone)
                    && !entity.is_null()
                {
                    add_public_legality_entity_evidence(entity);
                }
            }
            for zone in ["hand", "board"] {
                for entity in player[zone]
                    .as_array_mut()
                    .expect("public entity sequence fixture")
                {
                    add_public_legality_entity_evidence(entity);
                }
            }
        }
        value
    }

    fn end_turn(game_id: &str, sequence: u64, side: &str) -> Value {
        let (actor, source_event) = if side == "local" {
            ("friendly", "turn_passed_to_opponent")
        } else {
            ("opponent", "turn_passed_to_player")
        };
        json!({
            "schema": BEHAVIOR_SCHEMA_ID,
            "game_id": game_id,
            "behavior_sequence": sequence,
            "observed_at_utc": format!("2026-07-31T12:00:{:02}+08:00", sequence % 60),
            "actor_side": side,
            "actor_player_id": actor,
            "actor_evidence": "active_player",
            "identity_status": "event_only",
            "visibility_status": "public_pre_state",
            "boundary_status": "isolated",
            "source_event": source_event,
            "action": {
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": ""
            },
            "pre_state": state(actor, &format!("state-{sequence}")),
            "post_state": state(actor, &format!("state-after-{sequence}")),
            "behavior_eligible": true,
            "rl_training_eligible": false
        })
    }

    fn local_attack(game_id: &str, sequence: u64) -> Value {
        let mut value = end_turn(game_id, sequence, "local");
        value["actor_evidence"] = json!("hdt_player_event");
        value["identity_status"] = json!("exact_public_entity");
        value["source_event"] = json!("player_attack");
        value["action"] = json!({
            "kind": "attack",
            "source_entity_id": "f-minion",
            "target_entity_id": "o-hero",
            "card_id": "FRIENDLY_MINION"
        });
        value
    }

    fn opponent_play(game_id: &str, sequence: u64) -> Value {
        let mut value = end_turn(game_id, sequence, "opponent");
        value["actor_evidence"] = json!("hdt_opponent_event");
        value["identity_status"] = json!("revealed_after_action");
        value["visibility_status"] = json!("revealed_post_action");
        value["source_event"] = json!("opponent_play");
        value["action"] = json!({
            "kind": "play_card",
            "source_entity_id": "o-hand",
            "target_entity_id": "",
            "card_id": "OPPONENT_REVEALED_CARD"
        });
        value["post_state"] = state("opponent", "state-after-play");
        value
    }

    fn action_submission(side: &str, kind: &str) -> Value {
        let mut value = end_turn(&format!("{side}-{kind}-game"), 1, side);
        let local = side == "local";
        match kind {
            "end_turn" => value,
            "play_card" => {
                value["actor_evidence"] = json!(if local {
                    "hdt_player_event"
                } else {
                    "hdt_opponent_event"
                });
                value["identity_status"] = json!(if local {
                    "exact_public_entity"
                } else {
                    "revealed_after_action"
                });
                value["visibility_status"] = json!(if local {
                    "public_pre_state"
                } else {
                    "revealed_post_action"
                });
                value["source_event"] = json!(if local {
                    "player_play"
                } else {
                    "opponent_play"
                });
                value["action"] = json!({
                    "kind": "play_card",
                    "source_entity_id": if local { "f-hand" } else { "o-hand" },
                    "target_entity_id": "",
                    "card_id": if local { "FRIENDLY_CARD" } else { "REVEALED_OPPONENT_CARD" }
                });
                value
            }
            "attack" => {
                value["actor_evidence"] = json!(if local {
                    "hdt_player_event"
                } else {
                    "hdt_opponent_event"
                });
                value["identity_status"] = json!("exact_public_entity");
                value["source_event"] = json!(if local {
                    "player_attack"
                } else {
                    "opponent_attack"
                });
                value["action"] = json!({
                    "kind": "attack",
                    "source_entity_id": if local { "f-minion" } else { "o-minion" },
                    "target_entity_id": if local { "o-hero" } else { "f-hero" },
                    "card_id": if local { "FRIENDLY_MINION" } else { "OPPONENT_MINION" }
                });
                value
            }
            "hero_power" => {
                value["actor_evidence"] = json!(if local {
                    "hdt_player_event"
                } else {
                    "hdt_opponent_event"
                });
                value["identity_status"] = json!("exact_public_entity");
                value["source_event"] = json!(if local {
                    "player_hero_power"
                } else {
                    "opponent_hero_power"
                });
                value["action"] = json!({
                    "kind": "hero_power",
                    "source_entity_id": if local { "f-power" } else { "o-power" },
                    "target_entity_id": "",
                    "card_id": if local { "FRIENDLY_POWER" } else { "OPPONENT_POWER" }
                });
                value
            }
            "location_activate" => {
                assert!(local, "production location evidence is local Power input");
                value["actor_evidence"] = json!("hdt_power_log");
                value["identity_status"] = json!("exact_public_entity");
                value["source_event"] = json!("hdt_power_log");
                value["action"] = json!({
                    "kind": "location_activate",
                    "source_entity_id": "f-location",
                    "target_entity_id": "o-hero",
                    "card_id": "FRIENDLY_LOCATION"
                });
                value
            }
            _ => panic!("unsupported fixture action"),
        }
    }

    fn records(path: &Path) -> Vec<Value> {
        fs::read_to_string(path)
            .expect("read behavior JSONL")
            .lines()
            .map(|line| serde_json::from_str(line).expect("valid behavior record"))
            .collect()
    }

    fn persisted_line(submission: Value) -> Vec<u8> {
        let record = BehaviorRecord::from_submission(submission)
            .expect("normalize persisted behavior fixture");
        let mut line = serde_json::to_vec(&record.value).expect("serialize behavior fixture");
        line.push(b'\n');
        line
    }

    #[test]
    fn both_sides_are_bound_and_private_state_is_projected() {
        let temporary = TempDirectory::new("privacy");
        let logger = BehaviorLogger::new(Some(temporary.path()));
        assert!(
            logger
                .append(local_attack("private-game", 1))
                .unwrap()
                .logged
        );
        assert!(
            logger
                .append(opponent_play("private-game", 2))
                .unwrap()
                .logged
        );
        let records = records(&temporary.path());
        assert_eq!(records.len(), 2);
        assert!(
            records[0]["game_id"]
                .as_str()
                .is_some_and(|value| value.starts_with("anon-"))
        );
        assert_eq!(records[1]["actor_side"], "opponent");
        assert_eq!(
            records[1]["pre_state"]["opponent"]["hand"][0],
            json!({"entity_id": "o-hand", "visibility": "hidden"})
        );
        assert_eq!(records[0]["rl_training_eligible"], false);
        let serialized = serde_json::to_string(&records).unwrap();
        for secret in [
            "Alice Private",
            "Bob Private",
            "SECRET_OPPONENT_CARD",
            "never-write-password",
            "never-write-token",
            "private raw Power.log line",
            "绝不能落盘",
        ] {
            assert!(!serialized.contains(secret), "leaked {secret}");
        }
        for record in records {
            BehaviorRecord::from_persisted(record).expect("Rust record must self-verify");
        }
    }

    #[test]
    fn public_legality_evidence_is_allowlisted_and_fail_closed() {
        let mut submission = end_turn("public-legality-evidence", 1, "local");
        submission["pre_state"] = state_with_public_legality_evidence("friendly", "state-legality");
        submission["post_state"] =
            state_with_public_legality_evidence("friendly", "state-legality-post");
        let record = BehaviorRecord::from_submission(submission)
            .expect("normalize public legality evidence");
        let friendly = &record.value["pre_state"]["friendly"];
        assert_eq!(
            friendly["public_rule_tags"],
            json!({"HERO_POWER_DOUBLE": 0, "STEADY_SHOT_CAN_TARGET": 1})
        );
        assert_eq!(friendly["public_rule_tags_complete"], true);
        for key in [
            "current_health_known",
            "taunt",
            "divine_shield",
            "stealth",
            "poisonous",
            "lifesteal",
            "windfury",
            "mega_windfury",
            "rush",
            "charge",
            "reborn",
            "dormant",
            "immune",
            "summoned_this_turn",
            "frozen",
        ] {
            assert!(friendly["board"][0].get(key).is_some(), "missing {key}");
            assert!(friendly["board"][0][key].is_boolean(), "invalid {key}");
        }
        assert_eq!(
            record.value["pre_state"]["opponent"]["hand"][0],
            json!({"entity_id": "o-hand", "visibility": "hidden"})
        );
        assert!(
            !serde_json::to_string(&record.value)
                .unwrap()
                .contains("PRIVATE_UNSAFE_TAG")
        );
        BehaviorRecord::from_persisted(record.value.clone())
            .expect("new evidence must self-verify");

        let mut unsafe_tag = record.value.clone();
        unsafe_tag["pre_state"]["friendly"]["public_rule_tags"]["PRIVATE_UNSAFE_TAG"] = json!(1);
        assert_eq!(
            BehaviorRecord::from_persisted(unsafe_tag)
                .unwrap_err()
                .code(),
            "unknown_field:PRIVATE_UNSAFE_TAG"
        );

        let mut invalid_tag = record.value.clone();
        invalid_tag["pre_state"]["friendly"]["public_rule_tags"]["HERO_POWER_DOUBLE"] = json!(true);
        assert_eq!(
            BehaviorRecord::from_persisted(invalid_tag)
                .unwrap_err()
                .code(),
            "must_be_integer"
        );

        let mut invalid_entity = record.value.clone();
        invalid_entity["pre_state"]["friendly"]["board"][0]["taunt"] = json!(1);
        assert_eq!(
            BehaviorRecord::from_persisted(invalid_entity)
                .unwrap_err()
                .code(),
            "must_be_boolean"
        );

        let legacy = BehaviorRecord::from_submission(end_turn("legacy-evidence", 1, "local"))
            .expect("old behavior state remains valid");
        assert!(
            legacy.value["pre_state"]["friendly"]
                .get("public_rule_tags")
                .is_none()
        );
        assert!(
            legacy.value["pre_state"]["friendly"]["hero"]
                .get("current_health_known")
                .is_none()
        );
        BehaviorRecord::from_persisted(legacy.value).expect("old behavior record self-verifies");
    }

    #[test]
    fn both_sides_and_all_base_action_kinds_are_supported() {
        for side in ["local", "opponent"] {
            for kind in ["play_card", "attack", "hero_power", "end_turn"] {
                let record = BehaviorRecord::from_submission(action_submission(side, kind))
                    .unwrap_or_else(|error| panic!("{side}/{kind}: {error}"));
                assert_eq!(record.value["actor_side"], side);
                assert_eq!(record.value["action"]["kind"], kind);
                assert_eq!(record.value["behavior_eligible"], true);
                assert_eq!(record.value["rl_training_eligible"], false);
            }
        }
    }

    #[test]
    fn selected_choice_and_board_position_round_trip_without_rl_promotion() {
        let mut selected = action_submission("local", "play_card");
        selected["actor_evidence"] = json!("hdt_power_log");
        selected["source_event"] = json!("hdt_power_log");
        selected["action"]["sub_option"] = json!(1);
        selected["action"]["board_position"] = json!(3);
        selected["action"]["choice_status"] = json!("selected");
        selected["action"]["choices"] = json!([{
            "choice_id": null,
            "choice_type": "SUB_OPTION",
            "source_entity_id": "f-hand",
            "option_entity_ids": ["choice-a", "choice-b"],
            "selected_entity_ids": ["choice-b"],
            "status": "selected"
        }]);
        let record = BehaviorRecord::from_submission(selected).expect("selected choice");
        assert_eq!(record.value["action"]["sub_option"], 1);
        assert_eq!(record.value["action"]["board_position"], 3);
        assert_eq!(record.value["action"]["choice_status"], "selected");
        assert_eq!(
            record.value["action"]["choices"][0]["option_entity_ids"],
            json!(["choice-a", "choice-b"])
        );
        assert_eq!(record.value["behavior_eligible"], true);
        assert_eq!(record.value["rl_training_eligible"], false);
        BehaviorRecord::from_persisted(record.value).expect("choice record self-verifies");

        let legacy = BehaviorRecord::from_submission(action_submission("local", "play_card"))
            .expect("legacy action remains compatible");
        assert!(legacy.value["action"].get("choice_status").is_none());
        BehaviorRecord::from_persisted(legacy.value).expect("legacy hash remains stable");
    }

    #[test]
    fn unresolved_or_spoofed_choice_cannot_be_behavior_eligible() {
        let mut unresolved = action_submission("local", "play_card");
        unresolved["actor_evidence"] = json!("hdt_power_log");
        unresolved["source_event"] = json!("hdt_power_log");
        unresolved["action"]["sub_option"] = json!(-1);
        unresolved["action"]["board_position"] = json!(0);
        unresolved["action"]["choice_status"] = json!("unresolved");
        unresolved["action"]["choices"] = json!([{
            "choice_id": 17,
            "choice_type": "GENERAL",
            "source_entity_id": "f-hand",
            "option_entity_ids": ["choice-a"],
            "selected_entity_ids": [],
            "status": "unresolved"
        }]);
        unresolved["behavior_eligible"] = json!(false);
        let record = BehaviorRecord::from_submission(unresolved.clone())
            .expect("unresolved evidence is retained");
        assert_eq!(record.value["behavior_eligible"], false);

        unresolved["behavior_eligible"] = json!(true);
        assert_eq!(
            BehaviorRecord::from_submission(unresolved.clone())
                .unwrap_err()
                .code(),
            "behavior_eligibility_mismatch"
        );

        unresolved["behavior_eligible"] = json!(false);
        unresolved["action"]["choice_status"] = json!("selected");
        unresolved["action"]["choices"][0]["status"] = json!("selected");
        unresolved["action"]["choices"][0]["selected_entity_ids"] = json!(["choice-b"]);
        assert_eq!(
            BehaviorRecord::from_submission(unresolved)
                .unwrap_err()
                .code(),
            "selected_entity_not_offered"
        );
    }

    #[test]
    fn local_location_activation_requires_a_public_board_location() {
        let record =
            BehaviorRecord::from_submission(action_submission("local", "location_activate"))
                .expect("local exact Power location evidence is valid behavior");
        assert_eq!(record.value["action"]["kind"], "location_activate");
        assert_eq!(record.value["behavior_eligible"], true);
        assert_eq!(record.value["rl_training_eligible"], false);
        BehaviorRecord::from_persisted(record.value.clone())
            .expect("location content addressing must self-verify");

        let mut wrong_zone = action_submission("local", "location_activate");
        wrong_zone["action"]["source_entity_id"] = json!("f-hand");
        wrong_zone["action"]["card_id"] = json!("FRIENDLY_CARD");
        assert_eq!(
            BehaviorRecord::from_submission(wrong_zone)
                .unwrap_err()
                .code(),
            "location_source_not_on_board"
        );

        let mut wrong_type = action_submission("local", "location_activate");
        wrong_type["action"]["source_entity_id"] = json!("f-minion");
        wrong_type["action"]["card_id"] = json!("FRIENDLY_MINION");
        assert_eq!(
            BehaviorRecord::from_submission(wrong_type)
                .unwrap_err()
                .code(),
            "location_source_not_location"
        );
    }

    #[test]
    fn missing_post_state_forces_behavior_eligibility_false() {
        let mut downgraded = end_turn("missing-post-game", 1, "local");
        downgraded["post_state"] = Value::Null;
        downgraded["behavior_eligible"] = json!(false);
        let record = BehaviorRecord::from_submission(downgraded.clone())
            .expect("missing post-state evidence remains valid only as a downgrade");
        assert_eq!(record.value["post_state"], Value::Null);
        assert_eq!(record.value["behavior_eligible"], false);

        downgraded["behavior_eligible"] = json!(true);
        assert_eq!(
            BehaviorRecord::from_submission(downgraded)
                .unwrap_err()
                .code(),
            "behavior_eligibility_mismatch"
        );
    }

    #[test]
    fn known_actor_unknown_identity_is_retained_only_as_ineligible_evidence() {
        let mut local = action_submission("local", "attack");
        local["identity_status"] = json!("unknown");
        local["visibility_status"] = json!("hidden_source");
        local["action"]["source_entity_id"] = json!("");
        local["action"]["target_entity_id"] = json!("");
        local["behavior_eligible"] = json!(false);
        let local_record = BehaviorRecord::from_submission(local.clone()).unwrap();
        assert_eq!(local_record.value["actor_side"], "local");
        assert_eq!(local_record.value["identity_status"], "unknown");
        assert_eq!(local_record.value["behavior_eligible"], false);

        let mut opponent = action_submission("opponent", "play_card");
        opponent["identity_status"] = json!("unknown");
        opponent["action"]["source_entity_id"] = json!("");
        opponent["behavior_eligible"] = json!(false);
        let opponent_record = BehaviorRecord::from_submission(opponent).unwrap();
        assert_eq!(opponent_record.value["actor_side"], "opponent");
        assert_eq!(opponent_record.value["behavior_eligible"], false);

        let mut valid_bound = local.clone();
        valid_bound["action"]["source_entity_id"] = json!("f-minion");
        valid_bound["action"]["target_entity_id"] = json!("o-hero");
        BehaviorRecord::from_submission(valid_bound)
            .expect("provided downgrade entities still bind exactly");

        let mut wrong_owner = local.clone();
        wrong_owner["action"]["source_entity_id"] = json!("o-minion");
        assert_eq!(
            BehaviorRecord::from_submission(wrong_owner)
                .unwrap_err()
                .code(),
            "source_owner_mismatch"
        );
        let mut fake_target = local.clone();
        fake_target["action"]["target_entity_id"] = json!("missing-target");
        assert_eq!(
            BehaviorRecord::from_submission(fake_target)
                .unwrap_err()
                .code(),
            "target_not_in_pre_state"
        );
        let mut self_promoted = local.clone();
        self_promoted["behavior_eligible"] = json!(true);
        assert_eq!(
            BehaviorRecord::from_submission(self_promoted)
                .unwrap_err()
                .code(),
            "behavior_eligibility_mismatch"
        );
        let mut wrong_evidence = local;
        wrong_evidence["actor_evidence"] = json!("hdt_opponent_event");
        assert_eq!(
            BehaviorRecord::from_submission(wrong_evidence)
                .unwrap_err()
                .code(),
            "actor_evidence_side_mismatch"
        );
    }

    #[test]
    fn spoofing_unknown_fields_and_self_promotion_fail_before_writing() {
        let temporary = TempDirectory::new("reject");
        let logger = BehaviorLogger::new(Some(temporary.path()));
        let mut spoof = local_attack("private-game", 1);
        spoof["actor_player_id"] = json!("opponent");
        assert_eq!(
            logger.append(spoof).unwrap_err().code(),
            "actor_not_active_player"
        );
        let mut wrong_source = local_attack("private-game", 1);
        wrong_source["action"]["source_entity_id"] = json!("o-minion");
        assert_eq!(
            logger.append(wrong_source).unwrap_err().code(),
            "source_owner_mismatch"
        );
        let mut promoted = local_attack("private-game", 1);
        promoted["rl_training_eligible"] = json!(true);
        assert_eq!(
            logger.append(promoted).unwrap_err().code(),
            "rl_training_eligible_must_be_false"
        );
        let mut injected = local_attack("private-game", 1);
        injected["content_sha256"] = json!("producer-must-not-hash");
        assert!(
            logger
                .append(injected)
                .unwrap_err()
                .code()
                .starts_with("unknown_field")
        );
        let mut action_injected = local_attack("private-game", 1);
        action_injected["action"]["raw_power_log"] = json!("private");
        assert!(
            logger
                .append(action_injected)
                .unwrap_err()
                .code()
                .starts_with("unknown_field")
        );
        assert!(!temporary.path().exists());
        assert!(logger.healthy());
    }

    #[test]
    fn retries_restart_conflicts_and_sequence_gaps_are_fail_closed() {
        let temporary = TempDirectory::new("retry");
        let first = BehaviorLogger::new(Some(temporary.path()));
        let record = local_attack("restart-game", 1);
        let written = first.append(record.clone()).unwrap();
        assert!(written.logged);
        assert!(!written.duplicate);
        let retry = first.append(record.clone()).unwrap();
        assert!(!retry.logged);
        assert!(retry.duplicate);

        let restarted = BehaviorLogger::new(Some(temporary.path()));
        assert!(restarted.append(record).unwrap().duplicate);
        let conflict = restarted
            .append(end_turn("restart-game", 1, "local"))
            .unwrap_err();
        assert_eq!(conflict.code(), "behavior_sequence_conflict");
        let gap = restarted
            .append(end_turn("restart-game", 3, "local"))
            .unwrap_err();
        assert_eq!(gap.code(), "behavior_sequence_out_of_order");
        assert!(
            restarted
                .append(end_turn("restart-game", 2, "local"))
                .unwrap()
                .logged
        );
        assert_eq!(records(&temporary.path()).len(), 2);
    }

    #[test]
    fn same_size_external_rewrite_is_revalidated_before_append() {
        let temporary = TempDirectory::new("same-size-rewrite");
        let path = temporary.path();
        let logger = BehaviorLogger::new(Some(path.clone()));
        assert!(
            logger
                .append(local_attack("rewrite-game", 1))
                .unwrap()
                .logged
        );
        let original = fs::read(&path).expect("read behavior corpus");
        let damaged = String::from_utf8(original.clone())
            .expect("behavior corpus is UTF-8")
            .replacen("\"kind\":\"attack\"", "\"kind\":\"attacx\"", 1)
            .into_bytes();
        assert_ne!(original, damaged);
        assert_eq!(original.len(), damaged.len());

        fs::write(&path, &damaged).expect("rewrite corpus with equal length");
        OpenOptions::new()
            .write(true)
            .open(&path)
            .expect("open rewritten corpus")
            .set_times(
                fs::FileTimes::new()
                    .set_modified(SystemTime::now() + std::time::Duration::from_secs(2)),
            )
            .expect("advance rewritten corpus timestamp");
        assert_eq!(
            logger
                .append(end_turn("rewrite-game", 2, "opponent"))
                .unwrap_err()
                .code(),
            "existing_behavior_corpus_invalid"
        );
        assert_eq!(fs::read(&path).unwrap(), damaged);

        fs::write(&path, &original).expect("restore corpus");
        OpenOptions::new()
            .write(true)
            .open(&path)
            .expect("open restored corpus")
            .set_times(
                fs::FileTimes::new()
                    .set_modified(SystemTime::now() + std::time::Duration::from_secs(4)),
            )
            .expect("advance restored corpus timestamp");
        assert!(
            logger
                .append(end_turn("rewrite-game", 2, "opponent"))
                .unwrap()
                .logged
        );
        assert_eq!(records(&path).len(), 2);
    }

    #[test]
    fn durable_ack_waits_for_sync_and_restart_rebuild_sync() {
        let temporary = TempDirectory::new("sync-barrier");
        let path = temporary.path();
        let record = local_attack("durable-game", 1);

        FORCE_BEHAVIOR_BARRIER_FAILURE.with(|flag| flag.set(true));
        let logger = BehaviorLogger::new(Some(path.clone()));
        assert_eq!(
            logger.append(record.clone()).unwrap_err().code(),
            "behavior_append_failed"
        );
        assert!(!logger.healthy());
        assert_eq!(records(&path).len(), 1);
        drop(logger);

        // A new worker has no in-memory stale marker. Its disk rebuild still
        // must cross a fresh barrier before recognizing the complete row.
        let restarted = BehaviorLogger::new(Some(path.clone()));
        assert!(!restarted.healthy());
        assert_eq!(
            restarted.append(record.clone()).unwrap_err().code(),
            "behavior_corpus_sync_failed"
        );
        assert_eq!(records(&path).len(), 1);

        FORCE_BEHAVIOR_BARRIER_FAILURE.with(|flag| flag.set(false));
        let retry = restarted
            .append(record)
            .expect("successful rebuild may acknowledge the durable duplicate");
        assert!(!retry.logged);
        assert!(retry.duplicate);
        assert!(restarted.healthy());
        assert_eq!(records(&path).len(), 1);
    }

    #[test]
    fn torn_tail_is_archived_before_truncation_and_retry_writes_one_row() {
        let temporary = TempDirectory::new("torn-tail");
        let path = temporary.path();
        let seed = BehaviorLogger::new(Some(path.clone()));
        assert!(seed.append(local_attack("torn-game", 1)).unwrap().logged);
        let complete_history = fs::read(&path).expect("read complete behavior history");
        let torn_fragment = b"{\"behavior_id\":\"behavior-incomplete";
        OpenOptions::new()
            .append(true)
            .open(&path)
            .expect("open behavior corpus")
            .write_all(torn_fragment)
            .expect("append torn fragment");
        drop(seed);

        let recovered = BehaviorLogger::new(Some(path.clone()));
        assert!(recovered.healthy());
        assert!(
            recovered
                .append(end_turn("torn-game", 2, "local"))
                .unwrap()
                .logged
        );
        let contents = fs::read(&path).expect("read recovered behavior corpus");
        assert!(contents.starts_with(&complete_history));
        assert_eq!(records(&path).len(), 2);

        let digest = format!("{:x}", Sha256::digest(torn_fragment));
        let archive = temporary.0.join(format!(
            "{BEHAVIOR_LOG_FILENAME}.torn-tail.{digest}.fragment"
        ));
        assert_eq!(
            fs::read(&archive).expect("read torn behavior archive"),
            torn_fragment
        );
        assert!(
            fs::metadata(&archive)
                .expect("torn behavior archive metadata")
                .permissions()
                .readonly()
        );
        let retry = BehaviorLogger::new(Some(path.clone()))
            .append(end_turn("torn-game", 2, "local"))
            .expect("retry recovered behavior");
        assert!(retry.duplicate);
        assert_eq!(records(&path).len(), 2);

        #[cfg(windows)]
        clear_windows_behavior_readonly(&archive).expect("make test archive removable");
    }

    #[test]
    fn complete_json_missing_only_newline_is_preserved_without_archive() {
        let temporary = TempDirectory::new("missing-newline");
        let path = temporary.path();
        let record = local_attack("newline-game", 1);
        assert!(
            BehaviorLogger::new(Some(path.clone()))
                .append(record.clone())
                .unwrap()
                .logged
        );
        let mut contents = fs::read(&path).expect("read behavior corpus");
        assert_eq!(contents.pop(), Some(b'\n'));
        fs::write(&path, contents).expect("remove only behavior delimiter");

        let recovered = BehaviorLogger::new(Some(path.clone()));
        assert!(recovered.healthy());
        let retry = recovered
            .append(record)
            .expect("index complete behavior row");
        assert!(retry.duplicate);
        assert!(fs::read(&path).unwrap().ends_with(b"\n"));
        assert_eq!(records(&path).len(), 1);
        assert!(
            fs::read_dir(&temporary.0)
                .expect("list behavior directory")
                .all(|entry| !entry
                    .expect("behavior directory entry")
                    .file_name()
                    .to_string_lossy()
                    .contains(".torn-tail."))
        );
    }

    #[test]
    fn complete_middle_corruption_is_not_repaired_and_health_is_false() {
        let temporary = TempDirectory::new("middle-corruption");
        let path = temporary.path();
        assert!(
            BehaviorLogger::new(Some(path.clone()))
                .append(local_attack("damaged-game", 1))
                .unwrap()
                .logged
        );
        OpenOptions::new()
            .append(true)
            .open(&path)
            .expect("open behavior corpus")
            .write_all(b"not-json\n")
            .expect("append complete corrupt line");
        let damaged = fs::read(&path).expect("read damaged corpus");

        let logger = BehaviorLogger::new(Some(path.clone()));
        assert!(!logger.healthy());
        assert_eq!(
            logger
                .append(end_turn("damaged-game", 2, "local"))
                .unwrap_err()
                .code(),
            "existing_behavior_corpus_invalid"
        );
        assert!(!logger.healthy());
        assert_eq!(fs::read(&path).unwrap(), damaged);
        assert!(
            fs::read_dir(&temporary.0)
                .expect("list behavior directory")
                .all(|entry| !entry
                    .expect("behavior directory entry")
                    .file_name()
                    .to_string_lossy()
                    .contains(".torn-tail."))
        );
    }

    #[test]
    fn duplicate_and_noncontiguous_existing_corpora_fail_closed() {
        let duplicate = TempDirectory::new("persisted-duplicate");
        let duplicate_line = persisted_line(local_attack("duplicate-game", 1));
        let mut duplicate_corpus = duplicate_line.clone();
        duplicate_corpus.extend_from_slice(&duplicate_line);
        fs::write(duplicate.path(), &duplicate_corpus).expect("write duplicate corpus");
        let duplicate_logger = BehaviorLogger::new(Some(duplicate.path()));
        assert!(!duplicate_logger.healthy());
        assert_eq!(
            duplicate_logger
                .append(end_turn("duplicate-game", 2, "local"))
                .unwrap_err()
                .code(),
            "existing_behavior_corpus_duplicate"
        );
        assert_eq!(fs::read(duplicate.path()).unwrap(), duplicate_corpus);

        let gap = TempDirectory::new("persisted-gap");
        let mut gap_corpus = persisted_line(local_attack("gap-game", 1));
        gap_corpus.extend(persisted_line(end_turn("gap-game", 3, "local")));
        fs::write(gap.path(), &gap_corpus).expect("write noncontiguous corpus");
        let gap_logger = BehaviorLogger::new(Some(gap.path()));
        assert!(!gap_logger.healthy());
        assert_eq!(
            gap_logger
                .append(end_turn("gap-game", 2, "local"))
                .unwrap_err()
                .code(),
            "existing_behavior_sequence_not_contiguous"
        );
        assert_eq!(fs::read(gap.path()).unwrap(), gap_corpus);
    }

    #[test]
    fn concurrent_loggers_emit_complete_lines_and_deduplicate_retries() {
        let temporary = TempDirectory::new("concurrent");
        let loggers = (0..16)
            .map(|_| BehaviorLogger::new(Some(temporary.path())))
            .collect::<Vec<_>>();
        let duplicate = local_attack("same-game", 1);
        let handles = loggers
            .iter()
            .cloned()
            .map(|logger| {
                let duplicate = duplicate.clone();
                thread::spawn(move || logger.append(duplicate).unwrap())
            })
            .collect::<Vec<_>>();
        let outcomes = handles
            .into_iter()
            .map(|handle| handle.join().expect("behavior writer"))
            .collect::<Vec<_>>();
        assert_eq!(outcomes.iter().filter(|item| item.logged).count(), 1);
        assert_eq!(outcomes.iter().filter(|item| item.duplicate).count(), 15);

        let handles = (0..64)
            .map(|index| {
                let logger = loggers[index % loggers.len()].clone();
                thread::spawn(move || {
                    logger
                        .append(end_turn(&format!("parallel-game-{index}"), 1, "local"))
                        .unwrap()
                })
            })
            .collect::<Vec<_>>();
        assert!(
            handles
                .into_iter()
                .all(|handle| handle.join().expect("parallel behavior writer").logged)
        );
        let records = records(&temporary.path());
        assert_eq!(records.len(), 65);
        assert_eq!(
            records
                .iter()
                .filter_map(|record| record["behavior_id"].as_str())
                .collect::<HashSet<_>>()
                .len(),
            65
        );
    }

    #[test]
    fn corrupted_existing_corpus_marks_health_unhealthy_without_overwrite() {
        let temporary = TempDirectory::new("corrupt");
        fs::write(temporary.path(), b"{not-json}\n").expect("write corrupt corpus");
        let logger = BehaviorLogger::new(Some(temporary.path()));
        let before = fs::read(temporary.path()).unwrap();
        assert_eq!(
            logger
                .append(local_attack("private-game", 1))
                .unwrap_err()
                .code(),
            "existing_behavior_corpus_invalid"
        );
        assert!(!logger.healthy());
        assert_eq!(fs::read(temporary.path()).unwrap(), before);
    }

    #[test]
    fn training_path_derivation_is_independent_and_disable_is_coupled() {
        let training = PathBuf::from("managed-data").join("training-v2.jsonl");
        let logger = BehaviorLogger::for_training_log_path(Some(&training)).unwrap();
        assert!(logger.enabled());
        assert_eq!(
            logger.path.as_deref().map(AsRef::as_ref),
            Some(
                Path::new("managed-data")
                    .join(BEHAVIOR_LOG_FILENAME)
                    .as_path()
            )
        );
        let disabled = BehaviorLogger::for_training_log_path(None).unwrap();
        assert!(!disabled.enabled());
        assert!(disabled.healthy());
        let collision =
            BehaviorLogger::for_training_log_path(Some(Path::new(BEHAVIOR_LOG_FILENAME)))
                .unwrap_err();
        assert_eq!(collision.code(), "behavior_corpus_path_must_be_independent");
    }
}
