use std::cmp::Ordering;
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::Path;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::SystemTime;

use serde::Deserialize;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use thiserror::Error;

use crate::model::{Action, GameState, PlayerState};

pub const BEHAVIOR_PRIOR_FILENAME: &str = "behavior-prior-v1.json";
pub const BEHAVIOR_PRIOR_SCHEMA: &str = "behavior-imitation-prior-v2";
pub const BEHAVIOR_PRIOR_MODEL: &str = "hierarchical-behavior-frequency-v1";

const POLICY_SCHEMA: &str = "behavior-imitation-prior-policy-v1";
const MANIFEST_SCHEMA: &str = "behavior-imitation-manifest-v1";
const OTHER_TEMPLATE: &str = "__other__";
const MAX_ARTIFACT_BYTES: usize = 64 * 1024 * 1024;
const CONTEXT_LEVELS: [&str; 6] = [
    "global",
    "actor",
    "mode",
    "patch",
    "hero_pair",
    "public_state",
];
const ACTION_KINDS: [&str; 5] = [
    "attack",
    "end_turn",
    "hero_power",
    "location_activate",
    "play_card",
];
const TEMPLATE_ACTION_KINDS: [&str; 4] = ["attack", "hero_power", "location_activate", "play_card"];
const APPROVED_USES: [&str; 3] = [
    "offline_behavior_cloning_baseline",
    "opponent_behavior_modeling",
    "legal_action_search_ordering_prior",
];
const PROHIBITED_USES: [&str; 5] = [
    "action_generation",
    "direct_live_policy",
    "direct_rl_trajectory",
    "optimal_action_ground_truth",
    "hidden_opponent_card_reconstruction",
];

#[derive(Debug, Error)]
pub enum BehaviorPriorError {
    #[error("behavior prior could not be read")]
    Io(#[source] std::io::Error),
    #[error("behavior prior is not valid JSON")]
    Json(#[source] serde_json::Error),
    #[error("behavior prior contract failed: {0}")]
    Contract(String),
    #[error("behavior prior did not pass its search-ordering gate")]
    NotReady,
}

impl BehaviorPriorError {
    #[must_use]
    pub const fn code(&self) -> &'static str {
        match self {
            Self::Io(_) => "read_failed",
            Self::Json(_) => "invalid_json",
            Self::Contract(_) => "contract_rejected",
            Self::NotReady => "quality_gate_rejected",
        }
    }
}

fn contract(message: impl Into<String>) -> BehaviorPriorError {
    BehaviorPriorError::Contract(message.into())
}

#[derive(Clone, Debug)]
pub struct BehaviorPriorRuntime {
    model: Option<Arc<BehaviorPrior>>,
    status: &'static str,
    reason: &'static str,
    rejection_code: &'static str,
}

#[derive(Clone, Debug, Eq, PartialEq)]
struct FileFingerprint {
    bytes: u64,
    modified: Option<SystemTime>,
}

#[derive(Debug)]
struct ManagedRuntime {
    fingerprint: Option<FileFingerprint>,
    runtime: BehaviorPriorRuntime,
}

/// Hot-reloads an atomically replaced model without ever making the base solver
/// depend on model availability. Paths and parser details never enter health output.
#[derive(Debug)]
pub struct BehaviorPriorManager {
    path: Option<PathBuf>,
    state: Mutex<ManagedRuntime>,
}

impl BehaviorPriorManager {
    #[must_use]
    pub fn disabled() -> Self {
        Self {
            path: None,
            state: Mutex::new(ManagedRuntime {
                fingerprint: None,
                runtime: BehaviorPriorRuntime::disabled(),
            }),
        }
    }

    #[must_use]
    pub fn new(path: Option<PathBuf>) -> Self {
        let fingerprint = path.as_deref().and_then(file_fingerprint);
        let runtime = BehaviorPriorRuntime::load(path.as_deref());
        Self {
            path,
            state: Mutex::new(ManagedRuntime {
                fingerprint,
                runtime,
            }),
        }
    }

    #[must_use]
    pub fn model(&self) -> Option<Arc<BehaviorPrior>> {
        self.refresh();
        self.state
            .lock()
            .ok()
            .and_then(|state| state.runtime.model())
    }

    #[must_use]
    pub fn health_payload(&self) -> Value {
        self.refresh();
        self.state.lock().map_or_else(
            |_| {
                json!({
                    "available": false,
                    "status": "rejected",
                    "reason": "行为先验状态暂时无法确认，已安全停用；基础求解不受影响。",
                    "rejection_code": "state_unavailable",
                    "artifact_sha256": "",
                    "search_ordering_only": true,
                    "candidate_generation_allowed": false,
                    "live_policy_eligible": false,
                    "rl_training_eligible": false,
                    "optimality_verified": false
                })
            },
            |state| state.runtime.health_payload(),
        )
    }

    fn refresh(&self) {
        let Some(path) = self.path.as_deref() else {
            return;
        };
        let current = file_fingerprint(path);
        let Ok(mut state) = self.state.lock() else {
            return;
        };
        if state.fingerprint == current {
            return;
        }
        state.runtime = BehaviorPriorRuntime::load(Some(path));
        state.fingerprint = current;
    }
}

fn file_fingerprint(path: &Path) -> Option<FileFingerprint> {
    let metadata = fs::metadata(path).ok()?;
    if !metadata.is_file() {
        return None;
    }
    Some(FileFingerprint {
        bytes: metadata.len(),
        modified: metadata.modified().ok(),
    })
}

impl BehaviorPriorRuntime {
    #[must_use]
    pub const fn disabled() -> Self {
        Self {
            model: None,
            status: "disabled",
            reason: "尚未配置通过质量门禁的行为先验，继续使用基础搜索顺序。",
            rejection_code: "",
        }
    }

    #[must_use]
    pub fn load(path: Option<&Path>) -> Self {
        let Some(path) = path else {
            return Self::disabled();
        };
        if !path.is_file() {
            return Self {
                model: None,
                status: "not_found",
                reason: "尚无可用的行为先验，继续使用基础搜索顺序。",
                rejection_code: "",
            };
        }
        match BehaviorPrior::load(path) {
            Ok(model) => Self {
                model: Some(Arc::new(model)),
                status: "ready",
                reason: "行为先验已通过门禁，只用于合法动作的搜索顺序。",
                rejection_code: "",
            },
            Err(error) => Self {
                model: None,
                status: "rejected",
                reason: "行为先验未通过完整性或质量门禁，已安全停用；基础求解不受影响。",
                rejection_code: error.code(),
            },
        }
    }

    #[must_use]
    pub fn model(&self) -> Option<Arc<BehaviorPrior>> {
        self.model.clone()
    }

    #[must_use]
    pub fn health_payload(&self) -> Value {
        json!({
            "available": self.model.is_some(),
            "status": self.status,
            "reason": self.reason,
            "rejection_code": self.rejection_code,
            "artifact_sha256": self.model.as_ref().map_or("", |model| model.artifact_sha256()),
            "search_ordering_only": true,
            "candidate_generation_allowed": false,
            "live_policy_eligible": false,
            "rl_training_eligible": false,
            "optimality_verified": false
        })
    }
}

#[derive(Clone, Debug)]
pub struct BehaviorPrior {
    artifact_sha256: String,
    supported_modes: BTreeSet<String>,
    supported_patches: BTreeSet<String>,
    action_kind: CountModel,
    templates: BTreeMap<String, CountModel>,
}

impl BehaviorPrior {
    pub fn load(path: &Path) -> Result<Self, BehaviorPriorError> {
        let payload = fs::read(path).map_err(BehaviorPriorError::Io)?;
        Self::from_slice(&payload)
    }

    pub fn from_slice(payload: &[u8]) -> Result<Self, BehaviorPriorError> {
        if payload.is_empty() || payload.len() > MAX_ARTIFACT_BYTES {
            return Err(contract("artifact size is outside the supported range"));
        }
        if payload.windows(5).any(|window| window == b"anon-") {
            return Err(contract("artifact contains a game identifier"));
        }
        let raw: RawArtifact = serde_json::from_slice(payload).map_err(BehaviorPriorError::Json)?;
        validate_artifact(&raw)?;
        let artifact_sha256 = hex_sha256(payload);
        Ok(Self {
            artifact_sha256,
            supported_modes: raw.training.supported_modes.into_iter().collect(),
            supported_patches: raw.training.supported_patches.into_iter().collect(),
            action_kind: raw.models.action_kind.into(),
            templates: raw
                .models
                .action_template_by_kind
                .into_iter()
                .map(|(kind, model)| (kind, model.into()))
                .collect(),
        })
    }

    #[must_use]
    pub fn artifact_sha256(&self) -> &str {
        &self.artifact_sha256
    }

    #[must_use]
    pub fn supports_state(&self, state: &GameState) -> bool {
        self.supported_modes.contains(&normalize_mode(&state.mode))
            && self.supported_patches.contains(state.patch.as_ref())
    }

    pub fn score_actions(
        &self,
        state: &GameState,
        actions: &[Action],
    ) -> Result<Vec<f64>, BehaviorPriorError> {
        if actions.is_empty() {
            return Ok(Vec::new());
        }
        if !self.supports_state(state) {
            return Ok(vec![1.0 / actions.len() as f64; actions.len()]);
        }
        let actor = state
            .player(&state.active_player_id)
            .map_err(|_| contract("active player is not present in the state"))?;
        let other = state
            .other_player(&state.active_player_id)
            .map_err(|_| contract("opposing player is not present in the state"))?;
        let actor_side = if state.active_player_id == state.perspective_player_id {
            "local"
        } else {
            "opponent"
        };
        let contexts = context_keys(state, actor, other, actor_side)?;
        let kind_probabilities = self.action_kind.probabilities(&contexts)?;
        let mut raw_scores = Vec::with_capacity(actions.len());
        for action in actions {
            let kind = action.kind.as_str();
            let mut score = *kind_probabilities
                .get(kind)
                .ok_or_else(|| contract("candidate action kind is absent from the prior"))?;
            if kind != "end_turn" {
                let template_model = self
                    .templates
                    .get(kind)
                    .ok_or_else(|| contract("candidate template model is absent"))?;
                let template_probabilities = template_model.probabilities(&contexts)?;
                let template = action_template(state, actor, action)?;
                let label = if template_probabilities.contains_key(&template) {
                    template.as_str()
                } else {
                    OTHER_TEMPLATE
                };
                score *= template_probabilities.get(label).copied().ok_or_else(|| {
                    contract("candidate template fallback is absent from the prior")
                })?;
            }
            raw_scores.push(score.max(1e-12));
        }
        let total: f64 = raw_scores.iter().sum();
        if !total.is_finite() || total <= 0.0 {
            return Err(contract("candidate scores are not normalizable"));
        }
        Ok(raw_scores.into_iter().map(|score| score / total).collect())
    }

    /// Reorder only caller-supplied actions. No action is created, removed, or mutated.
    pub fn order_actions(
        &self,
        state: &GameState,
        actions: &mut [Action],
    ) -> Result<bool, BehaviorPriorError> {
        if actions.len() < 2 || !self.supports_state(state) {
            return Ok(false);
        }
        let scores = self.score_actions(state, actions)?;
        let mut ranked = actions.iter().cloned().zip(scores).collect::<Vec<_>>();
        ranked.sort_by(|(left_action, left_score), (right_action, right_score)| {
            right_score
                .partial_cmp(left_score)
                .unwrap_or(Ordering::Equal)
                .then_with(|| left_action.action_id().cmp(&right_action.action_id()))
        });
        for (slot, (action, _)) in actions.iter_mut().zip(ranked) {
            *slot = action;
        }
        Ok(true)
    }
}

#[derive(Clone, Debug)]
struct CountModel {
    labels: Vec<String>,
    alpha: f64,
    prior_strength: f64,
    counts_by_level: BTreeMap<String, BTreeMap<String, CountBucket>>,
}

impl CountModel {
    fn probabilities(
        &self,
        contexts: &BTreeMap<String, String>,
    ) -> Result<BTreeMap<String, f64>, BehaviorPriorError> {
        let mut probabilities = self
            .labels
            .iter()
            .map(|label| (label.clone(), 1.0 / self.labels.len() as f64))
            .collect::<BTreeMap<_, _>>();
        for level in CONTEXT_LEVELS {
            let context = contexts
                .get(level)
                .ok_or_else(|| contract("candidate context is incomplete"))?;
            let Some(bucket) = self
                .counts_by_level
                .get(level)
                .and_then(|buckets| buckets.get(context))
            else {
                continue;
            };
            let total = bucket.total as f64;
            if level == "global" {
                let denominator = total + self.alpha * self.labels.len() as f64;
                for label in &self.labels {
                    let count = bucket.counts.get(label).copied().unwrap_or(0) as f64;
                    probabilities.insert(label.clone(), (count + self.alpha) / denominator);
                }
            } else {
                let denominator = total + self.prior_strength;
                for label in &self.labels {
                    let count = bucket.counts.get(label).copied().unwrap_or(0) as f64;
                    let parent = probabilities[label];
                    probabilities.insert(
                        label.clone(),
                        (count + self.prior_strength * parent) / denominator,
                    );
                }
            }
        }
        Ok(probabilities)
    }
}

impl From<RawCountModel> for CountModel {
    fn from(value: RawCountModel) -> Self {
        Self {
            labels: value.labels,
            alpha: value.alpha,
            prior_strength: value.prior_strength,
            counts_by_level: value.counts_by_level,
        }
    }
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct RawArtifact {
    schema: String,
    model_type: String,
    source_dataset: SourceDataset,
    source_manifest: SourceManifest,
    policy: Policy,
    policy_sha256: String,
    training: Training,
    evaluation: Evaluation,
    quality_checks: Vec<QualityCheck>,
    imitation_training_complete: bool,
    search_ordering_prior_ready: bool,
    live_policy_eligible: bool,
    rl_training_eligible: bool,
    optimality_verified: bool,
    candidate_generation_allowed: bool,
    outcome_used_for_training: bool,
    models: RawModels,
    approved_uses: Vec<String>,
    prohibited_uses: Vec<String>,
    caveat: String,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct SourceDataset {
    name: String,
    sha256: String,
    bytes: u64,
    record_count: u64,
    game_count: u64,
    split_record_counts: SplitCounts,
    split_game_counts: SplitCounts,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct SourceManifest {
    name: String,
    sha256: String,
    schema: String,
}

#[derive(Clone, Copy, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct SplitCounts {
    train: u64,
    validation: u64,
    test: u64,
}

impl SplitCounts {
    fn total(self) -> Option<u64> {
        self.train
            .checked_add(self.validation)?
            .checked_add(self.test)
    }
}

#[derive(Debug, Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields)]
struct Policy {
    max_validation_kind_log_loss_excess: f64,
    max_validation_seen_template_log_loss_excess: f64,
    max_validation_unseen_template_rate: f64,
    min_test_games: u64,
    min_test_records: u64,
    min_train_games: u64,
    min_train_records: u64,
    min_validation_games: u64,
    min_validation_records: u64,
    min_validation_seen_template_records: u64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Training {
    split: String,
    record_count: u64,
    game_count: u64,
    actor_side_record_counts: BTreeMap<String, u64>,
    action_kind_record_counts: BTreeMap<String, u64>,
    supported_modes: Vec<String>,
    supported_patches: Vec<String>,
    unit_of_analysis: String,
    game_level_split: bool,
    actor_outcome_used: bool,
    local_outcome_used: bool,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Evaluation {
    validation: EvaluationSplit,
    test: EvaluationSplit,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct EvaluationSplit {
    status: String,
    record_count: u64,
    game_count: u64,
    actor_side_record_counts: BTreeMap<String, u64>,
    action_kind_record_counts: BTreeMap<String, u64>,
    kind_log_loss: f64,
    global_kind_log_loss: f64,
    kind_log_loss_excess: f64,
    kind_top1_accuracy: f64,
    global_kind_top1_accuracy: f64,
    game_macro_kind_log_loss: f64,
    game_macro_global_kind_log_loss: f64,
    template_record_count: u64,
    seen_template_record_count: u64,
    unseen_template_count: u64,
    unseen_template_rate: f64,
    seen_template_log_loss: f64,
    global_seen_template_log_loss: f64,
    seen_template_log_loss_excess: f64,
    seen_template_top1_accuracy: f64,
    global_seen_template_top1_accuracy: f64,
    game_macro_seen_template_log_loss: f64,
    game_macro_global_seen_template_log_loss: f64,
    caveat: String,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct QualityCheck {
    name: String,
    actual: f64,
    operator: String,
    expected: f64,
    passed: bool,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct RawModels {
    action_kind: RawCountModel,
    action_template_by_kind: BTreeMap<String, RawCountModel>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct RawCountModel {
    labels: Vec<String>,
    other_label: String,
    alpha: f64,
    prior_strength: f64,
    context_levels: Vec<String>,
    counts_by_level: BTreeMap<String, BTreeMap<String, CountBucket>>,
}

#[derive(Clone, Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct CountBucket {
    total: u64,
    counts: BTreeMap<String, u64>,
}

fn validate_artifact(raw: &RawArtifact) -> Result<(), BehaviorPriorError> {
    if raw.schema != BEHAVIOR_PRIOR_SCHEMA || raw.model_type != BEHAVIOR_PRIOR_MODEL {
        return Err(contract("artifact schema or model type is unsupported"));
    }
    if !raw.imitation_training_complete {
        return Err(contract("imitation training is incomplete"));
    }
    if !raw.search_ordering_prior_ready {
        return Err(BehaviorPriorError::NotReady);
    }
    if raw.live_policy_eligible
        || raw.rl_training_eligible
        || raw.optimality_verified
        || raw.candidate_generation_allowed
        || raw.outcome_used_for_training
    {
        return Err(contract("artifact overstates its permitted use"));
    }
    if raw.approved_uses != strings(&APPROVED_USES)
        || raw.prohibited_uses != strings(&PROHIBITED_USES)
        || !raw.caveat.contains("不等于最优动作")
    {
        return Err(contract("artifact use boundaries drifted"));
    }
    validate_source(raw)?;
    validate_policy(raw)?;
    validate_training(raw)?;
    validate_evaluation(raw)?;
    validate_quality_checks(raw)?;
    let action_kind_total = validate_count_model(
        &raw.models.action_kind,
        Some(&ACTION_KINDS),
        "",
        "action_kind",
    )?;
    if action_kind_total != raw.training.record_count {
        return Err(contract(
            "action-kind model did not use exactly the train split",
        ));
    }
    if raw.models.action_template_by_kind.len() != TEMPLATE_ACTION_KINDS.len()
        || raw
            .models
            .action_template_by_kind
            .keys()
            .map(String::as_str)
            .collect::<BTreeSet<_>>()
            != TEMPLATE_ACTION_KINDS.into_iter().collect::<BTreeSet<_>>()
    {
        return Err(contract("template model kinds drifted"));
    }
    for kind in TEMPLATE_ACTION_KINDS {
        let model = &raw.models.action_template_by_kind[kind];
        let total = validate_count_model(model, None, OTHER_TEMPLATE, kind)?;
        if total
            != raw
                .training
                .action_kind_record_counts
                .get(kind)
                .copied()
                .unwrap_or(0)
        {
            return Err(contract(format!("{kind} template count drifted")));
        }
    }
    Ok(())
}

fn validate_source(raw: &RawArtifact) -> Result<(), BehaviorPriorError> {
    let source = &raw.source_dataset;
    if !plain_name(&source.name)
        || !sha256_text(&source.sha256)
        || source.bytes == 0
        || source.record_count == 0
        || source.game_count == 0
        || source.split_record_counts.total() != Some(source.record_count)
        || source.split_game_counts.total() != Some(source.game_count)
    {
        return Err(contract("source dataset identity is invalid"));
    }
    if !plain_name(&raw.source_manifest.name)
        || !sha256_text(&raw.source_manifest.sha256)
        || raw.source_manifest.schema != MANIFEST_SCHEMA
    {
        return Err(contract("source manifest identity is invalid"));
    }
    Ok(())
}

fn validate_policy(raw: &RawArtifact) -> Result<(), BehaviorPriorError> {
    let policy = &raw.policy;
    let finite = [
        policy.max_validation_kind_log_loss_excess,
        policy.max_validation_seen_template_log_loss_excess,
        policy.max_validation_unseen_template_rate,
    ]
    .into_iter()
    .all(f64::is_finite);
    if !finite
        || !(0.0..=1.0).contains(&policy.max_validation_unseen_template_rate)
        || [
            policy.min_test_games,
            policy.min_test_records,
            policy.min_train_games,
            policy.min_train_records,
            policy.min_validation_games,
            policy.min_validation_records,
            policy.min_validation_seen_template_records,
        ]
        .contains(&0)
    {
        return Err(contract("behavior-prior policy is invalid"));
    }
    let canonical = serde_json::to_vec(&json!({
        "schema": POLICY_SCHEMA,
        "thresholds": policy
    }))
    .map_err(BehaviorPriorError::Json)?;
    if raw.policy_sha256 != hex_sha256(&canonical) {
        return Err(contract("behavior-prior policy hash is invalid"));
    }
    Ok(())
}

fn validate_training(raw: &RawArtifact) -> Result<(), BehaviorPriorError> {
    let training = &raw.training;
    if training.split != "train"
        || training.unit_of_analysis != "observed_action"
        || !training.game_level_split
        || training.actor_outcome_used
        || training.local_outcome_used
        || training.record_count != raw.source_dataset.split_record_counts.train
        || training.game_count != raw.source_dataset.split_game_counts.train
    {
        return Err(contract("training split semantics drifted"));
    }
    if sum_counts(&training.actor_side_record_counts)? != training.record_count
        || training
            .actor_side_record_counts
            .keys()
            .any(|key| key != "local" && key != "opponent")
        || sum_counts(&training.action_kind_record_counts)? != training.record_count
        || training
            .action_kind_record_counts
            .keys()
            .any(|key| !ACTION_KINDS.contains(&key.as_str()))
        || !sorted_unique_nonempty(&training.supported_modes)
        || !sorted_unique_nonempty(&training.supported_patches)
    {
        return Err(contract(
            "training counts or supported contexts are invalid",
        ));
    }
    Ok(())
}

fn validate_evaluation(raw: &RawArtifact) -> Result<(), BehaviorPriorError> {
    for (split, expected_records, expected_games) in [
        (
            &raw.evaluation.validation,
            raw.source_dataset.split_record_counts.validation,
            raw.source_dataset.split_game_counts.validation,
        ),
        (
            &raw.evaluation.test,
            raw.source_dataset.split_record_counts.test,
            raw.source_dataset.split_game_counts.test,
        ),
    ] {
        if split.status != "EVALUATED"
            || split.record_count != expected_records
            || split.game_count != expected_games
            || sum_counts(&split.actor_side_record_counts)? != split.record_count
            || sum_counts(&split.action_kind_record_counts)? != split.record_count
            || split.template_record_count
                != split
                    .seen_template_record_count
                    .checked_add(split.unseen_template_count)
                    .ok_or_else(|| contract("evaluation template count overflowed"))?
            || !split.caveat.contains("not legal-action coverage")
        {
            return Err(contract("held-out evaluation semantics drifted"));
        }
        let metrics = [
            split.kind_log_loss,
            split.global_kind_log_loss,
            split.kind_log_loss_excess,
            split.kind_top1_accuracy,
            split.global_kind_top1_accuracy,
            split.game_macro_kind_log_loss,
            split.game_macro_global_kind_log_loss,
            split.unseen_template_rate,
            split.seen_template_log_loss,
            split.global_seen_template_log_loss,
            split.seen_template_log_loss_excess,
            split.seen_template_top1_accuracy,
            split.global_seen_template_top1_accuracy,
            split.game_macro_seen_template_log_loss,
            split.game_macro_global_seen_template_log_loss,
        ];
        if !metrics.into_iter().all(f64::is_finite)
            || ![split.kind_top1_accuracy, split.global_kind_top1_accuracy]
                .into_iter()
                .all(|value| (0.0..=1.0).contains(&value))
            || !(0.0..=1.0).contains(&split.unseen_template_rate)
            || ![
                split.seen_template_top1_accuracy,
                split.global_seen_template_top1_accuracy,
            ]
            .into_iter()
            .all(|value| (0.0..=1.0).contains(&value))
        {
            return Err(contract("held-out evaluation contains invalid metrics"));
        }
        let expected_unseen = if split.template_record_count == 0 {
            0.0
        } else {
            split.unseen_template_count as f64 / split.template_record_count as f64
        };
        if !close(split.unseen_template_rate, expected_unseen) {
            return Err(contract("held-out unseen-template rate drifted"));
        }
    }
    Ok(())
}

fn validate_quality_checks(raw: &RawArtifact) -> Result<(), BehaviorPriorError> {
    let validation = &raw.evaluation.validation;
    let specs = [
        (
            "train_game_count",
            raw.source_dataset.split_game_counts.train as f64,
            ">=",
            raw.policy.min_train_games as f64,
        ),
        (
            "validation_game_count",
            raw.source_dataset.split_game_counts.validation as f64,
            ">=",
            raw.policy.min_validation_games as f64,
        ),
        (
            "test_game_count",
            raw.source_dataset.split_game_counts.test as f64,
            ">=",
            raw.policy.min_test_games as f64,
        ),
        (
            "train_record_count",
            raw.source_dataset.split_record_counts.train as f64,
            ">=",
            raw.policy.min_train_records as f64,
        ),
        (
            "validation_record_count",
            raw.source_dataset.split_record_counts.validation as f64,
            ">=",
            raw.policy.min_validation_records as f64,
        ),
        (
            "test_record_count",
            raw.source_dataset.split_record_counts.test as f64,
            ">=",
            raw.policy.min_test_records as f64,
        ),
        (
            "validation_seen_template_record_count",
            validation.seen_template_record_count as f64,
            ">=",
            raw.policy.min_validation_seen_template_records as f64,
        ),
        (
            "validation_kind_log_loss_excess",
            validation.kind_log_loss_excess,
            "<=",
            raw.policy.max_validation_kind_log_loss_excess,
        ),
        (
            "validation_seen_template_log_loss_excess",
            validation.seen_template_log_loss_excess,
            "<=",
            raw.policy.max_validation_seen_template_log_loss_excess,
        ),
        (
            "validation_unseen_template_rate",
            validation.unseen_template_rate,
            "<=",
            raw.policy.max_validation_unseen_template_rate,
        ),
    ];
    if raw.quality_checks.len() != specs.len() {
        return Err(contract("quality checks are incomplete"));
    }
    for (check, (name, actual, operator, expected)) in raw.quality_checks.iter().zip(specs) {
        let passed = if operator == ">=" {
            actual >= expected
        } else {
            actual <= expected
        };
        if check.name != name
            || check.operator != operator
            || !check.actual.is_finite()
            || !check.expected.is_finite()
            || !close(check.actual, actual)
            || !close(check.expected, expected)
            || check.passed != passed
            || !passed
        {
            return Err(BehaviorPriorError::NotReady);
        }
    }
    Ok(())
}

fn validate_count_model(
    model: &RawCountModel,
    expected_labels: Option<&[&str]>,
    expected_other: &str,
    label: &str,
) -> Result<u64, BehaviorPriorError> {
    if model.labels.is_empty()
        || !sorted_unique_nonempty(&model.labels)
        || model.other_label != expected_other
        || !model.alpha.is_finite()
        || model.alpha <= 0.0
        || !model.prior_strength.is_finite()
        || model.prior_strength <= 0.0
        || model.context_levels != strings(&CONTEXT_LEVELS)
        || model.counts_by_level.len() != CONTEXT_LEVELS.len()
        || model
            .counts_by_level
            .keys()
            .map(String::as_str)
            .collect::<BTreeSet<_>>()
            != CONTEXT_LEVELS.into_iter().collect::<BTreeSet<_>>()
    {
        return Err(contract(format!("{label} count model contract drifted")));
    }
    if let Some(expected) = expected_labels {
        if model.labels != strings(expected) {
            return Err(contract(format!("{label} labels drifted")));
        }
    } else if !model.labels.iter().any(|item| item == OTHER_TEMPLATE) {
        return Err(contract(format!("{label} other-template label is missing")));
    }
    let label_set = model
        .labels
        .iter()
        .map(String::as_str)
        .collect::<BTreeSet<_>>();
    let mut global_total = None;
    for (level_index, level) in CONTEXT_LEVELS.iter().enumerate() {
        let buckets = &model.counts_by_level[*level];
        if *level == "global" && (buckets.len() != 1 || !buckets.contains_key("[]")) {
            return Err(contract(format!("{label} global bucket is invalid")));
        }
        let mut level_total = 0u64;
        for (context, bucket) in buckets {
            validate_context_key(context, level_index, label)?;
            if bucket
                .counts
                .keys()
                .any(|key| !label_set.contains(key.as_str()))
                || bucket.counts.values().any(|count| *count == 0)
                || sum_counts(&bucket.counts)? != bucket.total
            {
                return Err(contract(format!("{label} bucket counts are invalid")));
            }
            level_total = level_total
                .checked_add(bucket.total)
                .ok_or_else(|| contract(format!("{label} bucket total overflowed")))?;
        }
        if let Some(expected) = global_total {
            if level_total != expected {
                return Err(contract(format!("{label} context totals drifted")));
            }
        } else {
            global_total = Some(level_total);
        }
    }
    Ok(global_total.unwrap_or(0))
}

fn validate_context_key(
    text: &str,
    level_index: usize,
    label: &str,
) -> Result<(), BehaviorPriorError> {
    let value: Value = serde_json::from_str(text).map_err(BehaviorPriorError::Json)?;
    let values = value
        .as_array()
        .ok_or_else(|| contract(format!("{label} context key is not an array")))?;
    let expected_length = [0, 1, 2, 3, 5, 12][level_index];
    if values.len() != expected_length
        || serde_json::to_string(&value).map_err(BehaviorPriorError::Json)? != text
    {
        return Err(contract(format!("{label} context key is not canonical")));
    }
    Ok(())
}

fn context_keys(
    state: &GameState,
    actor: &PlayerState,
    other: &PlayerState,
    actor_side: &str,
) -> Result<BTreeMap<String, String>, BehaviorPriorError> {
    let mode = normalize_mode(&state.mode);
    let patch = if state.patch.trim().is_empty() {
        "unknown"
    } else {
        state.patch.as_ref()
    };
    let actor_hero = known_card_id(&actor.hero.card_id);
    let other_hero = known_card_id(&other.hero.card_id);
    let turn_bucket = i64::from(state.turn.min(60) / 2).min(15);
    let values = [
        ("global", json!([])),
        ("actor", json!([actor_side])),
        ("mode", json!([actor_side, mode])),
        ("patch", json!([actor_side, mode, patch])),
        (
            "hero_pair",
            json!([actor_side, mode, patch, actor_hero, other_hero]),
        ),
        (
            "public_state",
            json!([
                actor_side,
                mode,
                patch,
                actor_hero,
                other_hero,
                actor.mana.min(20),
                actor.max_mana.min(20),
                actor.hand.len().min(12),
                actor.board.len().min(7),
                other.board.len().min(7),
                i32::from(actor.hero_power_available),
                turn_bucket
            ]),
        ),
    ];
    values
        .into_iter()
        .map(|(level, value)| {
            serde_json::to_string(&value)
                .map(|serialized| (level.to_owned(), serialized))
                .map_err(BehaviorPriorError::Json)
        })
        .collect()
}

fn action_template(
    state: &GameState,
    actor: &PlayerState,
    action: &Action,
) -> Result<String, BehaviorPriorError> {
    let source_card_id = if action.card_id.trim().is_empty() {
        find_card(state, &action.source_entity_id)
            .map(|card| known_card_id(&card.card_id))
            .unwrap_or("unknown")
    } else {
        known_card_id(&action.card_id)
    };
    let target_role = entity_role(state, actor, &action.target_entity_id);
    let template = if action.board_position > 0 {
        json!([source_card_id, target_role, action.board_position])
    } else {
        json!([source_card_id, target_role])
    };
    serde_json::to_string(&template).map_err(BehaviorPriorError::Json)
}

fn find_card<'a>(state: &'a GameState, entity_id: &str) -> Option<&'a crate::model::Card> {
    if entity_id.is_empty() {
        return None;
    }
    for player in [&state.friendly, &state.opponent] {
        let cards = std::iter::once(&player.hero)
            .chain(player.hero_power.iter())
            .chain(player.weapon.iter())
            .chain(player.hand.iter())
            .chain(player.board.iter());
        if let Some(card) = cards
            .into_iter()
            .find(|card| card.entity_id.as_ref() == entity_id)
        {
            return Some(card);
        }
    }
    None
}

fn entity_role(state: &GameState, actor: &PlayerState, entity_id: &str) -> String {
    if entity_id.is_empty() {
        return "none".to_owned();
    }
    for player in [&state.friendly, &state.opponent] {
        let relation = if player.player_id == actor.player_id {
            "self"
        } else {
            "enemy"
        };
        for (zone, card) in [
            ("hero", Some(&player.hero)),
            ("hero_power", player.hero_power.as_ref()),
            ("weapon", player.weapon.as_ref()),
        ] {
            if card.is_some_and(|card| card.entity_id.as_ref() == entity_id) {
                return format!("{relation}_{zone}");
            }
        }
        for (zone, cards) in [("hand", &player.hand), ("board", &player.board)] {
            if cards
                .iter()
                .any(|card| card.entity_id.as_ref() == entity_id)
            {
                return format!("{relation}_{zone}");
            }
        }
    }
    "unknown".to_owned()
}

fn normalize_mode(value: &str) -> String {
    let normalized = value.trim().to_lowercase();
    if normalized.contains("arena") {
        "arena".to_owned()
    } else if normalized.contains("standard") || normalized.contains("ranked") {
        "standard".to_owned()
    } else if normalized.is_empty() {
        "unknown".to_owned()
    } else {
        normalized
    }
}

fn known_card_id(value: &str) -> &str {
    if value.trim().is_empty() || value.eq_ignore_ascii_case("UNKNOWN") {
        "unknown"
    } else {
        value
    }
}

fn strings(values: &[&str]) -> Vec<String> {
    values.iter().map(|value| (*value).to_owned()).collect()
}

fn sorted_unique_nonempty(values: &[String]) -> bool {
    !values.is_empty()
        && values.iter().all(|value| !value.trim().is_empty())
        && values.windows(2).all(|pair| pair[0] < pair[1])
}

fn sum_counts(values: &BTreeMap<String, u64>) -> Result<u64, BehaviorPriorError> {
    values.values().try_fold(0u64, |total, value| {
        total
            .checked_add(*value)
            .ok_or_else(|| contract("count total overflowed"))
    })
}

fn plain_name(value: &str) -> bool {
    !value.trim().is_empty()
        && value
            == Path::new(value)
                .file_name()
                .and_then(|name| name.to_str())
                .unwrap_or("")
}

fn sha256_text(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn hex_sha256(payload: &[u8]) -> String {
    let digest = Sha256::digest(payload);
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

fn close(left: f64, right: f64) -> bool {
    (left - right).abs() <= 1e-12
}

#[cfg(test)]
mod tests {
    use crate::model::{ActionKind, SolveRequest};

    use super::*;

    #[test]
    fn action_template_distinguishes_board_positions() {
        let mut request: SolveRequest = serde_json::from_str(
            r#"{
              "request_id":"position-template",
              "state":{"state_id":"s","turn":1,"active_player_id":"friendly",
                "perspective_player_id":"friendly",
                "friendly":{"player_id":"friendly","hero":{"entity_id":"fh","card_type":"HERO","health":30}},
                "opponent":{"player_id":"opponent","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        )
        .expect("valid request");
        request.validate().expect("valid state");
        let actor = &request.state.friendly;
        let plain = Action::new(ActionKind::PlayCard, "21", "", "CARD");
        let left = plain.clone().with_board_position(1);
        let middle = plain.clone().with_board_position(2);

        assert_eq!(
            action_template(&request.state, actor, &plain).expect("plain template"),
            r#"["CARD","none"]"#
        );
        assert_eq!(
            action_template(&request.state, actor, &left).expect("left template"),
            r#"["CARD","none",1]"#
        );
        assert_eq!(
            action_template(&request.state, actor, &middle).expect("middle template"),
            r#"["CARD","none",2]"#
        );
    }

    #[test]
    fn mode_normalization_matches_the_offline_trainer() {
        assert_eq!(normalize_mode("Arena"), "arena");
        assert_eq!(normalize_mode("Ranked Standard"), "standard");
        assert_eq!(normalize_mode(""), "unknown");
        assert_eq!(normalize_mode("Twist"), "twist");
    }

    #[test]
    fn disabled_runtime_never_claims_policy_or_optimality() {
        let payload = BehaviorPriorRuntime::disabled().health_payload();
        assert_eq!(payload["available"], false);
        assert_eq!(payload["candidate_generation_allowed"], false);
        assert_eq!(payload["live_policy_eligible"], false);
        assert_eq!(payload["rl_training_eligible"], false);
        assert_eq!(payload["optimality_verified"], false);
    }
}
