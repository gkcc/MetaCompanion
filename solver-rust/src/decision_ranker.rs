use std::cmp::Ordering;
use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::SystemTime;

use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use thiserror::Error;

use crate::model::{Action, Card, GameState};

pub const DECISION_RANKER_FILENAME: &str = "decision-ranker-v1.json";
pub const DECISION_RANKER_SCHEMA: &str = "advisor-decision-ranker-v1";
pub const DECISION_RANKER_MODEL: &str = "sparse-listwise-logistic-v1";
pub const DECISION_RANKER_FEATURES: &str = "public-decision-candidate-features-v1";

const POLICY_SCHEMA: &str = "advisor-decision-ranker-policy-v1";
const MAX_ARTIFACT_BYTES: usize = 16 * 1024 * 1024;
const APPROVED_USES: [&str; 3] = [
    "rerank_hdt_supplied_legal_candidates",
    "offline_top_k_behavior_cloning",
    "user_visible_hdt_legal_behavior_reference",
];
const PROHIBITED_USES: [&str; 4] = [
    "action_generation",
    "optimal_action_ground_truth",
    "direct_rl_trajectory",
    "opponent_candidate_reconstruction",
];

#[derive(Debug, Error)]
pub enum DecisionRankerError {
    #[error("decision ranker could not be read")]
    Io(#[source] std::io::Error),
    #[error("decision ranker is not valid JSON")]
    Json(#[source] serde_json::Error),
    #[error("decision ranker contract failed: {0}")]
    Contract(String),
    #[error("decision ranker did not pass its candidate-ranking gate")]
    NotReady,
}

impl DecisionRankerError {
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

fn contract(message: impl Into<String>) -> DecisionRankerError {
    DecisionRankerError::Contract(message.into())
}

#[derive(Clone, Debug)]
pub struct DecisionRankerRuntime {
    model: Option<Arc<DecisionRanker>>,
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
    runtime: DecisionRankerRuntime,
}

#[derive(Debug)]
pub struct DecisionRankerManager {
    path: Option<PathBuf>,
    state: Mutex<ManagedRuntime>,
}

impl DecisionRankerManager {
    #[must_use]
    pub fn disabled() -> Self {
        Self {
            path: None,
            state: Mutex::new(ManagedRuntime {
                fingerprint: None,
                runtime: DecisionRankerRuntime::disabled(),
            }),
        }
    }

    #[must_use]
    pub fn new(path: Option<PathBuf>) -> Self {
        let fingerprint = path.as_deref().and_then(file_fingerprint);
        let runtime = DecisionRankerRuntime::load(path.as_deref());
        Self {
            path,
            state: Mutex::new(ManagedRuntime {
                fingerprint,
                runtime,
            }),
        }
    }

    #[must_use]
    pub fn model(&self) -> Option<Arc<DecisionRanker>> {
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
                    "reason": "本方决策排序器状态暂时无法确认，已安全停用；基础求解不受影响。",
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
        state.runtime = DecisionRankerRuntime::load(Some(path));
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

impl DecisionRankerRuntime {
    #[must_use]
    pub const fn disabled() -> Self {
        Self {
            model: None,
            status: "disabled",
            reason: "尚未配置通过质量门禁的本方决策排序器，继续使用基础搜索顺序。",
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
                reason: "尚无可用的本方决策排序器，继续使用基础搜索顺序。",
                rejection_code: "",
            };
        }
        match DecisionRanker::load(path) {
            Ok(model) => Self {
                model: Some(Arc::new(model)),
                status: "ready",
                reason: "本方决策排序器已通过门禁，只重排规则引擎给出的合法动作。",
                rejection_code: "",
            },
            Err(error) => Self {
                model: None,
                status: "rejected",
                reason: "本方决策排序器未通过完整性或质量门禁，已安全停用；基础求解不受影响。",
                rejection_code: error.code(),
            },
        }
    }

    #[must_use]
    pub fn model(&self) -> Option<Arc<DecisionRanker>> {
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
pub struct DecisionRanker {
    artifact_sha256: String,
    temperature: f64,
    weights: BTreeMap<String, f64>,
    supported_modes: BTreeSet<String>,
    supported_patches: BTreeSet<String>,
}

impl DecisionRanker {
    pub fn load(path: &Path) -> Result<Self, DecisionRankerError> {
        let payload = fs::read(path).map_err(DecisionRankerError::Io)?;
        Self::from_slice(&payload)
    }

    pub fn from_slice(payload: &[u8]) -> Result<Self, DecisionRankerError> {
        if payload.is_empty() || payload.len() > MAX_ARTIFACT_BYTES {
            return Err(contract("artifact size is outside the supported range"));
        }
        for marker in [
            b"anon-".as_slice(),
            br#""game_id""#.as_slice(),
            br#""state_id""#.as_slice(),
            br#""entity_id""#.as_slice(),
        ] {
            if payload.windows(marker.len()).any(|window| window == marker) {
                return Err(contract("artifact contains a forbidden identity"));
            }
        }
        let raw: RawArtifact =
            serde_json::from_slice(payload).map_err(DecisionRankerError::Json)?;
        validate_artifact(&raw)?;
        Ok(Self {
            artifact_sha256: hex_sha256(payload),
            temperature: raw.model.temperature,
            weights: raw.model.weights,
            supported_modes: raw.model.supported_modes.into_iter().collect(),
            supported_patches: raw.model.supported_patches.into_iter().collect(),
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
    ) -> Result<Vec<f64>, DecisionRankerError> {
        if actions.is_empty() {
            return Ok(Vec::new());
        }
        if state.active_player_id != state.perspective_player_id
            || state.active_player_id != state.friendly.player_id
        {
            return Err(contract(
                "decision ranker only accepts the local active player",
            ));
        }
        if !self.supports_state(state) {
            return Ok(vec![1.0 / actions.len() as f64; actions.len()]);
        }
        let features = actions
            .iter()
            .map(|action| candidate_features(state, action, actions))
            .collect::<Result<Vec<_>, _>>()?;
        probabilities(&self.weights, &features, self.temperature)
    }

    /// Reorder only caller-supplied actions. No action is created, removed, or mutated.
    pub fn order_actions(
        &self,
        state: &GameState,
        actions: &mut [Action],
    ) -> Result<bool, DecisionRankerError> {
        if actions.len() < 2
            || !self.supports_state(state)
            || state.active_player_id != state.perspective_player_id
            || state.active_player_id != state.friendly.player_id
        {
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

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct RawArtifact {
    schema: String,
    model_type: String,
    feature_contract: String,
    source_decision_frames: DecisionFrameSource,
    source_behavior: BehaviorSource,
    policy: Policy,
    policy_sha256: String,
    training: Training,
    evaluation: Evaluation,
    quality_checks: Vec<QualityCheck>,
    model: RawModel,
    candidate_ranking_training_complete: bool,
    candidate_ranking_ready: bool,
    user_visible_behavior_reference_eligible: bool,
    candidate_generation_allowed: bool,
    live_policy_eligible: bool,
    rl_training_eligible: bool,
    optimality_verified: bool,
    outcome_used_as_action_optimality: bool,
    approved_uses: Vec<String>,
    prohibited_uses: Vec<String>,
    caveat_zh: String,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct DecisionFrameSource {
    name: String,
    bytes: u64,
    sha256: String,
    record_count: u64,
    game_count: u64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct BehaviorSource {
    name: String,
    bytes: u64,
    sha256: String,
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
struct Policy {
    max_validation_log_loss_excess: f64,
    max_validation_unseen_selected_template_rate: f64,
    min_test_games: u64,
    min_test_records: u64,
    min_train_games: u64,
    min_train_records: u64,
    min_validation_games: u64,
    min_validation_records: u64,
    min_validation_top1_lift_over_uniform: f64,
    min_validation_top3_lift_over_uniform: f64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Training {
    split: String,
    game_level_split: bool,
    max_epochs: u64,
    selected_epoch: u64,
    learning_rate: f64,
    weight_decay: f64,
    optimizer: String,
    example_order: String,
    record_count: u64,
    game_count: u64,
    validation_selected_temperature: f64,
    temperature_grid: Vec<f64>,
    training_curve: Vec<TrainingCurvePoint>,
    outcome_used: bool,
    opponent_candidates_used: bool,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct TrainingCurvePoint {
    epoch: u64,
    validation_log_loss: f64,
    validation_top1_accuracy: f64,
    validation_top3_accuracy: f64,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Evaluation {
    train: EvaluationSplit,
    validation: EvaluationSplit,
    test: EvaluationSplit,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct EvaluationSplit {
    status: String,
    record_count: u64,
    game_count: u64,
    candidate_count: u64,
    average_candidate_count: f64,
    top1_accuracy: f64,
    top3_accuracy: f64,
    mean_reciprocal_rank: f64,
    log_loss: f64,
    uniform_top1_expected_accuracy: f64,
    uniform_top3_expected_accuracy: f64,
    uniform_log_loss: f64,
    top1_lift_over_uniform: f64,
    top3_lift_over_uniform: f64,
    log_loss_excess: f64,
    unseen_selected_template_count: u64,
    unseen_selected_template_rate: f64,
    selected_action_kind_counts: BTreeMap<String, u64>,
    tie_breaker: String,
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
struct RawModel {
    temperature: f64,
    weights: BTreeMap<String, f64>,
    weight_count: u64,
    supported_modes: Vec<String>,
    supported_patches: Vec<String>,
}

fn validate_artifact(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    if raw.schema != DECISION_RANKER_SCHEMA
        || raw.model_type != DECISION_RANKER_MODEL
        || raw.feature_contract != DECISION_RANKER_FEATURES
    {
        return Err(contract(
            "artifact schema, model, or feature contract is unsupported",
        ));
    }
    if !raw.candidate_ranking_training_complete {
        return Err(contract("candidate-ranking training is incomplete"));
    }
    if !raw.candidate_ranking_ready {
        return Err(DecisionRankerError::NotReady);
    }
    if !raw.user_visible_behavior_reference_eligible {
        return Err(contract("user-visible behavior reference is not eligible"));
    }
    if raw.candidate_generation_allowed
        || raw.live_policy_eligible
        || raw.rl_training_eligible
        || raw.optimality_verified
        || raw.outcome_used_as_action_optimality
    {
        return Err(contract("artifact overstates its permitted use"));
    }
    if raw.approved_uses != strings(&APPROVED_USES)
        || raw.prohibited_uses != strings(&PROHIBITED_USES)
        || !raw.caveat_zh.contains("不证明任何选择最优")
    {
        return Err(contract("artifact use boundaries drifted"));
    }
    validate_sources(raw)?;
    validate_policy(raw)?;
    validate_training(raw)?;
    validate_evaluation(raw)?;
    validate_quality_checks(raw)?;
    validate_model(raw)?;
    Ok(())
}

fn validate_sources(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    let frames = &raw.source_decision_frames;
    let behavior = &raw.source_behavior;
    if !plain_name(&frames.name)
        || frames.bytes == 0
        || frames.record_count == 0
        || frames.game_count == 0
        || !sha256_text(&frames.sha256)
        || !plain_name(&behavior.name)
        || behavior.bytes == 0
        || !sha256_text(&behavior.sha256)
    {
        return Err(contract("source identity is invalid"));
    }
    let evaluated_records = raw
        .evaluation
        .train
        .record_count
        .checked_add(raw.evaluation.validation.record_count)
        .and_then(|value| value.checked_add(raw.evaluation.test.record_count));
    if evaluated_records != Some(frames.record_count) {
        return Err(contract("evaluation does not cover every decision frame"));
    }
    Ok(())
}

fn validate_policy(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    let policy = &raw.policy;
    let finite = [
        policy.max_validation_log_loss_excess,
        policy.max_validation_unseen_selected_template_rate,
        policy.min_validation_top1_lift_over_uniform,
        policy.min_validation_top3_lift_over_uniform,
    ]
    .into_iter()
    .all(f64::is_finite);
    if !finite
        || !(0.0..=1.0).contains(&policy.max_validation_unseen_selected_template_rate)
        || [
            policy.min_test_games,
            policy.min_test_records,
            policy.min_train_games,
            policy.min_train_records,
            policy.min_validation_games,
            policy.min_validation_records,
        ]
        .contains(&0)
    {
        return Err(contract("decision-ranker policy is invalid"));
    }
    let canonical = serde_json::to_vec(&json!({
        "schema": POLICY_SCHEMA,
        "thresholds": policy
    }))
    .map_err(DecisionRankerError::Json)?;
    if raw.policy_sha256 != hex_sha256(&canonical) {
        return Err(contract("decision-ranker policy hash is invalid"));
    }
    Ok(())
}

fn validate_training(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    let training = &raw.training;
    if training.split != "train"
        || !training.game_level_split
        || training.max_epochs == 0
        || training.selected_epoch == 0
        || training.selected_epoch > training.max_epochs
        || !training.learning_rate.is_finite()
        || training.learning_rate <= 0.0
        || !training.weight_decay.is_finite()
        || training.weight_decay < 0.0
        || training.optimizer != "deterministic_adagrad"
        || training.example_order != "sha256_decision_frame_id_epoch"
        || training.record_count != raw.evaluation.train.record_count
        || training.game_count != raw.evaluation.train.game_count
        || !training.validation_selected_temperature.is_finite()
        || training.validation_selected_temperature <= 0.0
        || training.temperature_grid.is_empty()
        || !training
            .temperature_grid
            .iter()
            .all(|value| value.is_finite() && *value > 0.0)
        || !training
            .temperature_grid
            .iter()
            .any(|value| (*value - training.validation_selected_temperature).abs() <= f64::EPSILON)
        || training.training_curve.len()
            != usize::try_from(training.max_epochs).unwrap_or(usize::MAX)
        || training.outcome_used
        || training.opponent_candidates_used
    {
        return Err(contract("training contract is invalid"));
    }
    for (index, point) in training.training_curve.iter().enumerate() {
        if point.epoch != u64::try_from(index + 1).unwrap_or(u64::MAX)
            || !point.validation_log_loss.is_finite()
            || !rate(point.validation_top1_accuracy)
            || !rate(point.validation_top3_accuracy)
        {
            return Err(contract("training curve is invalid"));
        }
    }
    Ok(())
}

fn validate_evaluation(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    for split in [
        &raw.evaluation.train,
        &raw.evaluation.validation,
        &raw.evaluation.test,
    ] {
        let finite = [
            split.average_candidate_count,
            split.top1_accuracy,
            split.top3_accuracy,
            split.mean_reciprocal_rank,
            split.log_loss,
            split.uniform_top1_expected_accuracy,
            split.uniform_top3_expected_accuracy,
            split.uniform_log_loss,
            split.top1_lift_over_uniform,
            split.top3_lift_over_uniform,
            split.log_loss_excess,
            split.unseen_selected_template_rate,
        ]
        .into_iter()
        .all(f64::is_finite);
        let kind_total = split
            .selected_action_kind_counts
            .values()
            .try_fold(0_u64, |total, count| total.checked_add(*count));
        if split.status != "EVALUATED"
            || split.record_count == 0
            || split.game_count == 0
            || split.candidate_count < split.record_count
            || !finite
            || !rate(split.top1_accuracy)
            || !rate(split.top3_accuracy)
            || !rate(split.mean_reciprocal_rank)
            || !rate(split.uniform_top1_expected_accuracy)
            || !rate(split.uniform_top3_expected_accuracy)
            || !rate(split.unseen_selected_template_rate)
            || split.unseen_selected_template_count > split.record_count
            || kind_total != Some(split.record_count)
            || split.tie_breaker != "probability_desc_then_candidate_id_asc"
        {
            return Err(contract("evaluation split is invalid"));
        }
    }
    Ok(())
}

fn validate_quality_checks(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    if raw.quality_checks.is_empty() {
        return Err(contract("quality checks are absent"));
    }
    let mut names = BTreeSet::new();
    for check in &raw.quality_checks {
        if check.name.trim().is_empty()
            || !names.insert(check.name.as_str())
            || !check.actual.is_finite()
            || !check.expected.is_finite()
        {
            return Err(contract("quality check is invalid"));
        }
        let computed = match check.operator.as_str() {
            ">=" => check.actual >= check.expected,
            "<=" => check.actual <= check.expected,
            _ => return Err(contract("quality check operator is invalid")),
        };
        if check.passed != computed {
            return Err(contract("quality check result was not recomputed"));
        }
        if !check.passed {
            return Err(DecisionRankerError::NotReady);
        }
    }
    Ok(())
}

fn validate_model(raw: &RawArtifact) -> Result<(), DecisionRankerError> {
    let model = &raw.model;
    if !model.temperature.is_finite()
        || model.temperature <= 0.0
        || (model.temperature - raw.training.validation_selected_temperature).abs() > f64::EPSILON
        || model.weights.is_empty()
        || model.weight_count != u64::try_from(model.weights.len()).unwrap_or(u64::MAX)
        || model.supported_modes.is_empty()
        || model.supported_patches.is_empty()
        || model.supported_modes.iter().any(|value| !safe_token(value))
        || model
            .supported_patches
            .iter()
            .any(|value| !safe_token(value))
        || model.supported_modes.iter().collect::<BTreeSet<_>>().len()
            != model.supported_modes.len()
        || model
            .supported_patches
            .iter()
            .collect::<BTreeSet<_>>()
            .len()
            != model.supported_patches.len()
    {
        return Err(contract("model metadata is invalid"));
    }
    for (feature, weight) in &model.weights {
        if feature.is_empty()
            || feature.len() > 512
            || feature.chars().any(char::is_control)
            || !weight.is_finite()
        {
            return Err(contract("model weight is invalid"));
        }
    }
    Ok(())
}

fn candidate_features(
    state: &GameState,
    action: &Action,
    legal_actions: &[Action],
) -> Result<BTreeMap<String, f64>, DecisionRankerError> {
    let kind = action.kind.as_str();
    let friendly = &state.friendly;
    let opponent = &state.opponent;
    let source = find_entity(state, &action.source_entity_id);
    let target = find_entity(state, &action.target_entity_id);
    let turn = state.turn.min(30);
    let mana = friendly.mana.min(10);
    let mode = normalize_mode(&state.mode);
    let has_attack = legal_actions
        .iter()
        .any(|candidate| candidate.kind.as_str() == "attack");
    let has_play = legal_actions
        .iter()
        .any(|candidate| candidate.kind.as_str() == "play_card");
    let mut features = BTreeMap::new();
    for name in [
        "bias".to_owned(),
        format!("kind={kind}"),
        format!("kind_mode={kind}|{mode}"),
        format!("kind_turn={kind}|{}", turn / 2),
        format!("kind_mana={kind}|{mana}"),
        format!(
            "kind_boards={kind}|{}|{}",
            friendly.board.len(),
            opponent.board.len()
        ),
        format!("kind_hand={kind}|{}", friendly.hand.len().min(10)),
        format!(
            "kind_heroes={kind}|{}|{}",
            card_id(&friendly.hero),
            card_id(&opponent.hero)
        ),
        format!(
            "kind_candidate_count={kind}|{}",
            legal_actions.len().min(20)
        ),
        format!(
            "kind_candidate_mix={kind}|attack={}|play={}",
            usize::from(has_attack),
            usize::from(has_play)
        ),
        format!("kind_position={kind}|{}", action.board_position),
    ] {
        add_feature(&mut features, name, 1.0);
    }

    if let Some((_, source_zone, entity)) = source {
        let source_card_id = if action.card_id.is_empty() {
            card_id(entity)
        } else {
            action.card_id.to_string()
        };
        let cost = entity.cost.min(20);
        let target_role = target.map_or_else(
            || "none".to_owned(),
            |(side, zone, _)| format!("{side}_{zone}"),
        );
        for name in [
            format!("kind_zone={kind}|{source_zone}"),
            format!("kind_type={kind}|{}", entity.card_type.as_str()),
            format!("kind_card={kind}|{source_card_id}"),
            format!("mode_card={mode}|{source_card_id}"),
            format!("kind_cost={kind}|{cost}"),
            format!("card_target={source_card_id}|{target_role}"),
        ] {
            add_feature(&mut features, name, 1.0);
        }
        add_feature(
            &mut features,
            format!("numeric_cost={kind}"),
            f64::from(cost) / 10.0,
        );
        add_feature(
            &mut features,
            format!("numeric_mana_after={kind}"),
            f64::from(i32::from(mana) - i32::from(cost)) / 10.0,
        );
        add_feature(
            &mut features,
            format!("numeric_source_attack={kind}"),
            f64::from(entity.attack.min(20)) / 10.0,
        );
        add_feature(
            &mut features,
            format!("numeric_source_health={kind}"),
            f64::from(entity.current_health.min(30)) / 10.0,
        );
    }

    if let Some((target_side, target_zone, entity)) = target {
        for name in [
            format!("kind_target={kind}|{target_side}_{target_zone}"),
            format!("kind_target_card={kind}|{}", card_id(entity)),
        ] {
            add_feature(&mut features, name, 1.0);
        }
        add_feature(
            &mut features,
            format!("numeric_target_attack={kind}"),
            f64::from(entity.attack.min(20)) / 10.0,
        );
        add_feature(
            &mut features,
            format!("numeric_target_health={kind}"),
            f64::from(entity.current_health.min(30)) / 10.0,
        );
    }
    Ok(features)
}

fn find_entity<'a>(
    state: &'a GameState,
    entity_id: &str,
) -> Option<(&'static str, &'static str, &'a Card)> {
    if entity_id.is_empty() {
        return None;
    }
    for (side, player) in [("friendly", &state.friendly), ("opponent", &state.opponent)] {
        for (zone, entity) in [
            ("hero", Some(&player.hero)),
            ("hero_power", player.hero_power.as_ref()),
            ("weapon", player.weapon.as_ref()),
        ] {
            if entity.is_some_and(|card| card.entity_id.as_ref() == entity_id) {
                return entity.map(|card| (side, zone, card));
            }
        }
        for (zone, cards) in [("hand", &player.hand), ("board", &player.board)] {
            if let Some(card) = cards
                .iter()
                .find(|card| card.entity_id.as_ref() == entity_id)
            {
                return Some((side, zone, card));
            }
        }
    }
    None
}

fn add_feature(features: &mut BTreeMap<String, f64>, name: String, value: f64) {
    if value != 0.0 {
        *features.entry(name).or_default() += value;
    }
}

fn probabilities(
    weights: &BTreeMap<String, f64>,
    candidates: &[BTreeMap<String, f64>],
    temperature: f64,
) -> Result<Vec<f64>, DecisionRankerError> {
    if candidates.is_empty() {
        return Ok(Vec::new());
    }
    let scores = candidates
        .iter()
        .map(|features| {
            features
                .iter()
                .map(|(feature, value)| weights.get(feature).copied().unwrap_or(0.0) * value)
                .sum::<f64>()
                / temperature
        })
        .collect::<Vec<_>>();
    if !scores.iter().all(|score| score.is_finite()) {
        return Err(contract("candidate score is not finite"));
    }
    let maximum = scores.iter().copied().fold(f64::NEG_INFINITY, f64::max);
    let exponentials = scores
        .iter()
        .map(|score| (score - maximum).max(-60.0).exp())
        .collect::<Vec<_>>();
    let total = exponentials.iter().sum::<f64>();
    if !total.is_finite() || total <= 0.0 {
        return Err(contract("candidate scores are not normalizable"));
    }
    Ok(exponentials
        .into_iter()
        .map(|value| value / total)
        .collect())
}

fn normalize_mode(value: &str) -> String {
    value.trim().to_lowercase()
}

fn card_id(card: &Card) -> String {
    if card.card_id.is_empty() {
        "unknown".to_owned()
    } else {
        card.card_id.to_string()
    }
}

fn strings<const N: usize>(values: &[&str; N]) -> Vec<String> {
    values.iter().map(|value| (*value).to_owned()).collect()
}

fn rate(value: f64) -> bool {
    value.is_finite() && (0.0..=1.0).contains(&value)
}

fn safe_token(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 256
        && value
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || b"_.:-".contains(&byte))
}

fn plain_name(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 255
        && !value.contains('/')
        && !value.contains('\\')
        && !value.contains('\0')
}

fn sha256_text(value: &str) -> bool {
    value.len() == 64
        && value
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
}

fn hex_sha256(value: &[u8]) -> String {
    format!("{:x}", Sha256::digest(value))
}
