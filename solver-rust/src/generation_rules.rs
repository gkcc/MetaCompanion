//! Hash-bound generated-card, Discover, and random-summon rules.
//!
//! The embedded inventory covers every stochastic Standard/Arena card found in
//! the official snapshot. Only the explicitly reviewed runtime subset is ever
//! attached to a visible HDT entity; queued rules remain unsupported.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, OnceLock};

use serde::{Deserialize, Serialize};

use crate::error::SolverError;
use crate::model::{
    Card, CardPoolSource, CardType, Effect, EffectCoverage, GameState, PlayerState,
};
use crate::rules::{normalize_card_text, normalized_text_sha256};

pub const GENERATION_RULESET_ID: &str = "card-generation-pools-v1";
pub const GENERATION_MATCHING_CONTRACT: &str =
    "card_id+normalized_english_text_sha256+card_type+reviewed_runtime_status";
const RULESET_JSON: &str =
    include_str!("../../solver/metacompanion_solver/rules_data/card-generation-pools-v1.json");
const RULESET_SCHEMA_VERSION: u32 = 1;
const RULESET_STATUS: &str = "complete_inventory_with_fail_closed_runtime_subset";
const RUNTIME_READY: &str = "runtime_ready";
const MANUAL_QUEUE: &str = "explicit_manual_queue";
const POINT_RULESET_ID: &str = crate::rules::RULESET_ID;
const POOL_EFFECT_KINDS: &[&str] = &[
    "generate_from_pool",
    "discover_from_pool",
    "summon_from_pool",
];

#[derive(Debug, Deserialize)]
struct RawBundle {
    schema_version: u32,
    ruleset_id: String,
    status: String,
    authoring_contract: RawAuthoringContract,
    source: RawSource,
    counts: RawCounts,
    rules: Vec<RawRule>,
}

#[derive(Debug, Deserialize)]
struct RawAuthoringContract {
    free_text_is_executable: bool,
    strict_templates_require_review: bool,
    text_fingerprint_required: bool,
    unresolved_rules_are_never_executed: bool,
    zone_or_history_pools_never_fall_back_to_current_format: bool,
}

#[derive(Debug, Deserialize)]
struct RawSource {
    official_pool_run_id: String,
    card_defs_build: String,
    card_defs_sha256: String,
    standard_pool_sha256: String,
    arena_pool_sha256: String,
}

#[derive(Debug, Deserialize)]
struct RawCounts {
    unique_official_cards: usize,
    stochastic_cards: usize,
    runtime_ready: usize,
    explicit_manual_queue: usize,
}

#[derive(Debug, Deserialize)]
struct RawRule {
    rule_id: String,
    card_id: String,
    dbf_id: u64,
    formats: Vec<String>,
    card_type: CardType,
    normalized_text: String,
    text_sha256: String,
    trigger: String,
    execution_status: String,
    runtime_origin: String,
    runtime_effects: Vec<Effect>,
    #[serde(default)]
    blockers: Vec<String>,
}

#[derive(Clone, Debug)]
struct GenerationRule {
    rule_id: Arc<str>,
    card_id: Arc<str>,
    card_type: CardType,
    text_sha256: Arc<str>,
    trigger: Arc<str>,
    runtime_ready: bool,
    effects: Arc<[Effect]>,
    blockers: Arc<[Arc<str>]>,
}

#[derive(Clone, Debug)]
pub struct GenerationRuleBundle {
    rules: Vec<GenerationRule>,
    by_card_id: HashMap<String, usize>,
    unique_official_cards: usize,
    runtime_ready_count: usize,
    manual_queue_count: usize,
    source_run_id: String,
    source_card_defs_build: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct GenerationRuleMatch {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub text_sha256: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct GenerationRuleMismatch {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub reason: String,
    pub actual_text_sha256: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct QueuedGenerationRule {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub trigger: String,
    pub blockers: Vec<String>,
}

#[derive(Clone, Debug, Default, Eq, PartialEq, Serialize)]
pub struct GenerationRuleAssessment {
    pub ruleset_id: String,
    pub matched: Vec<GenerationRuleMatch>,
    pub queued: Vec<QueuedGenerationRule>,
    pub mismatches: Vec<GenerationRuleMismatch>,
}

fn required(value: &str, path: &str) -> Result<(), SolverError> {
    if value.trim().is_empty() {
        Err(SolverError::schema(path, "must be a non-empty string"))
    } else {
        Ok(())
    }
}

fn valid_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn validate_pool_effect(effect: &Effect, path: &str) -> Result<bool, SolverError> {
    effect.validate(path)?;
    if effect.trigger.as_ref() != "resolution" {
        return Err(SolverError::schema(
            format!("{path}.trigger"),
            "runtime pool effects must use play resolution",
        ));
    }
    if !POOL_EFFECT_KINDS.contains(&effect.kind.as_ref()) {
        return Ok(false);
    }
    if effect.has_summoned_minion_keywords() {
        return Err(SolverError::schema(
            path,
            "pool effects cannot carry fixed summoned-minion keywords",
        ));
    }
    let pool = effect
        .pool
        .as_ref()
        .ok_or_else(|| SolverError::schema(format!("{path}.pool"), "must be present"))?;
    if !matches!(
        pool.source,
        CardPoolSource::CurrentFormat | CardPoolSource::OwnerDeck
    ) {
        return Err(SolverError::schema(
            format!("{path}.pool.source"),
            "runtime generation rules may only use current_format or owner_deck",
        ));
    }
    Ok(true)
}

#[allow(clippy::too_many_lines)]
fn load_bundle(value: &str) -> Result<GenerationRuleBundle, SolverError> {
    let raw: RawBundle = serde_json::from_str(value)?;
    if raw.schema_version != RULESET_SCHEMA_VERSION
        || raw.ruleset_id != GENERATION_RULESET_ID
        || raw.status != RULESET_STATUS
    {
        return Err(SolverError::schema(
            "generation_rules",
            "bundle identity, schema, or status mismatch",
        ));
    }
    let contract = raw.authoring_contract;
    if contract.free_text_is_executable
        || !contract.strict_templates_require_review
        || !contract.text_fingerprint_required
        || !contract.unresolved_rules_are_never_executed
        || !contract.zone_or_history_pools_never_fall_back_to_current_format
    {
        return Err(SolverError::schema(
            "generation_rules.authoring_contract",
            "fail-closed authoring contract mismatch",
        ));
    }
    required(
        &raw.source.official_pool_run_id,
        "generation_rules.source.official_pool_run_id",
    )?;
    required(
        &raw.source.card_defs_build,
        "generation_rules.source.card_defs_build",
    )?;
    for (field, hash) in [
        ("card_defs_sha256", &raw.source.card_defs_sha256),
        ("standard_pool_sha256", &raw.source.standard_pool_sha256),
        ("arena_pool_sha256", &raw.source.arena_pool_sha256),
    ] {
        if !valid_sha256(hash) {
            return Err(SolverError::schema(
                format!("generation_rules.source.{field}"),
                "must be a SHA-256 digest",
            ));
        }
    }
    if raw.counts.unique_official_cards == 0
        || raw.counts.stochastic_cards != raw.rules.len()
        || raw.counts.runtime_ready + raw.counts.explicit_manual_queue != raw.rules.len()
    {
        return Err(SolverError::schema(
            "generation_rules.counts",
            "declared inventory counts do not match rules",
        ));
    }

    let mut rules = Vec::with_capacity(raw.rules.len());
    let mut by_card_id = HashMap::with_capacity(raw.rules.len());
    let mut rule_ids = HashSet::with_capacity(raw.rules.len());
    let mut runtime_ready_count = 0usize;
    let mut manual_queue_count = 0usize;
    for (index, row) in raw.rules.into_iter().enumerate() {
        let path = format!("generation_rules.rules[{index}]");
        required(&row.rule_id, &format!("{path}.rule_id"))?;
        required(&row.card_id, &format!("{path}.card_id"))?;
        required(&row.trigger, &format!("{path}.trigger"))?;
        if row.dbf_id == 0 || row.card_type == CardType::Unknown {
            return Err(SolverError::schema(
                &path,
                "dbf_id and a known card_type are required",
            ));
        }
        if row.formats.is_empty()
            || row
                .formats
                .iter()
                .any(|format| !matches!(format.as_str(), "standard" | "arena"))
        {
            return Err(SolverError::schema(
                format!("{path}.formats"),
                "must contain only standard and/or arena",
            ));
        }
        if normalize_card_text(&row.normalized_text) != row.normalized_text
            || !valid_sha256(&row.text_sha256)
            || normalized_text_sha256(&row.normalized_text) != row.text_sha256.to_ascii_lowercase()
        {
            return Err(SolverError::schema(
                format!("{path}.text_sha256"),
                "normalized English text fingerprint mismatch",
            ));
        }
        if !rule_ids.insert(row.rule_id.clone()) {
            return Err(SolverError::schema(
                format!("{path}.rule_id"),
                "duplicate rule ID",
            ));
        }
        let normalized_card_id = row.card_id.to_ascii_uppercase();
        if by_card_id.contains_key(&normalized_card_id) {
            return Err(SolverError::schema(
                format!("{path}.card_id"),
                "duplicate case-insensitive card ID",
            ));
        }

        let runtime_ready = match row.execution_status.as_str() {
            RUNTIME_READY => {
                runtime_ready_count = runtime_ready_count.saturating_add(1);
                if row.runtime_effects.is_empty()
                    || !matches!(
                        row.runtime_origin.as_str(),
                        "reviewed_strict_template"
                            | "reviewed_product_template"
                            | "explicit_reviewed_override"
                    )
                {
                    return Err(SolverError::schema(
                        &path,
                        "runtime-ready rules require reviewed effects and origin",
                    ));
                }
                let mut has_pool_effect = false;
                for (effect_index, effect) in row.runtime_effects.iter().enumerate() {
                    has_pool_effect |= validate_pool_effect(
                        effect,
                        &format!("{path}.runtime_effects[{effect_index}]"),
                    )?;
                }
                if !has_pool_effect {
                    return Err(SolverError::schema(
                        format!("{path}.runtime_effects"),
                        "must contain a generated-card pool effect",
                    ));
                }
                true
            }
            MANUAL_QUEUE => {
                manual_queue_count = manual_queue_count.saturating_add(1);
                if !row.runtime_effects.is_empty() || row.runtime_origin != "none" {
                    return Err(SolverError::schema(
                        &path,
                        "queued rules must never contain executable effects",
                    ));
                }
                false
            }
            _ => {
                return Err(SolverError::schema(
                    format!("{path}.execution_status"),
                    "unsupported execution status",
                ));
            }
        };
        let rule_index = rules.len();
        by_card_id.insert(normalized_card_id, rule_index);
        rules.push(GenerationRule {
            rule_id: Arc::from(row.rule_id),
            card_id: Arc::from(row.card_id),
            card_type: row.card_type,
            text_sha256: Arc::from(row.text_sha256.to_ascii_lowercase()),
            trigger: Arc::from(row.trigger),
            runtime_ready,
            effects: row.runtime_effects.into(),
            blockers: row.blockers.into_iter().map(Arc::<str>::from).collect(),
        });
    }
    if runtime_ready_count != raw.counts.runtime_ready
        || manual_queue_count != raw.counts.explicit_manual_queue
    {
        return Err(SolverError::schema(
            "generation_rules.counts",
            "execution-status counts do not match rules",
        ));
    }
    Ok(GenerationRuleBundle {
        rules,
        by_card_id,
        unique_official_cards: raw.counts.unique_official_cards,
        runtime_ready_count,
        manual_queue_count,
        source_run_id: raw.source.official_pool_run_id,
        source_card_defs_build: raw.source.card_defs_build,
    })
}

static EMBEDDED_BUNDLE: OnceLock<Result<GenerationRuleBundle, String>> = OnceLock::new();

/// Return the compile-time embedded and fully validated stochastic-card inventory.
///
/// # Errors
///
/// Returns unsupported when any identity, count, hash, or fail-closed contract
/// check fails. A partially loaded bundle is never returned.
pub fn embedded_generation_rule_bundle() -> Result<&'static GenerationRuleBundle, SolverError> {
    match EMBEDDED_BUNDLE
        .get_or_init(|| load_bundle(RULESET_JSON).map_err(|error| error.to_string()))
    {
        Ok(bundle) => Ok(bundle),
        Err(error) => Err(SolverError::Unsupported(format!(
            "generated-card rule bundle is unavailable: {error}"
        ))),
    }
}

impl GenerationRuleBundle {
    #[must_use]
    pub fn inventory_count(&self) -> usize {
        self.rules.len()
    }

    #[must_use]
    pub fn unique_official_cards(&self) -> usize {
        self.unique_official_cards
    }

    #[must_use]
    pub fn runtime_ready_count(&self) -> usize {
        self.runtime_ready_count
    }

    #[must_use]
    pub fn manual_queue_count(&self) -> usize {
        self.manual_queue_count
    }

    #[must_use]
    pub fn source_run_id(&self) -> &str {
        &self.source_run_id
    }

    #[must_use]
    pub fn source_card_defs_build(&self) -> &str {
        &self.source_card_defs_build
    }

    fn mismatch(
        assessment: &mut GenerationRuleAssessment,
        card: &Card,
        rule: &GenerationRule,
        reason: &str,
        actual_text_sha256: String,
    ) {
        assessment.mismatches.push(GenerationRuleMismatch {
            entity_id: card.entity_id.to_string(),
            card_id: card.card_id.to_string(),
            rule_id: rule.rule_id.to_string(),
            reason: reason.to_owned(),
            actual_text_sha256,
        });
    }

    fn apply_card(&self, card: &mut Card, assessment: &mut GenerationRuleAssessment) {
        let Some(index) = self.by_card_id.get(&card.card_id.to_ascii_uppercase()) else {
            return;
        };
        let rule = &self.rules[*index];
        let actual_hash = normalized_text_sha256(&card.card_text);
        if card.card_type != rule.card_type {
            Self::mismatch(assessment, card, rule, "card_type_mismatch", actual_hash);
            return;
        }
        if card.card_text.trim().is_empty() {
            Self::mismatch(assessment, card, rule, "english_text_missing", actual_hash);
            return;
        }
        if actual_hash != rule.text_sha256.as_ref() {
            Self::mismatch(
                assessment,
                card,
                rule,
                "english_text_sha256_mismatch",
                actual_hash,
            );
            return;
        }
        if !rule.runtime_ready {
            assessment.queued.push(QueuedGenerationRule {
                entity_id: card.entity_id.to_string(),
                card_id: rule.card_id.to_string(),
                rule_id: rule.rule_id.to_string(),
                trigger: rule.trigger.to_string(),
                blockers: rule.blockers.iter().map(ToString::to_string).collect(),
            });
            return;
        }
        if !card.effects.is_empty()
            && card.rule_version.as_ref() != POINT_RULESET_ID
            && card.rule_version.as_ref() != GENERATION_RULESET_ID
        {
            Self::mismatch(
                assessment,
                card,
                rule,
                "prestructured_effect_conflict",
                actual_hash,
            );
            return;
        }

        card.effects = Arc::clone(&rule.effects);
        card.rule_id = Arc::clone(&rule.rule_id);
        card.rule_version = Arc::from(GENERATION_RULESET_ID);
        card.rule_text_sha256 = Arc::from(actual_hash.as_str());
        card.unsupported_effects = card
            .unsupported_effects
            .iter()
            .filter(|mechanic| mechanic.as_ref() != "card_text_not_parsed")
            .cloned()
            .collect::<Vec<_>>()
            .into();
        card.effect_coverage = if card.unsupported_effects.is_empty() {
            EffectCoverage::Exact
        } else {
            EffectCoverage::Unsupported
        };
        assessment.matched.push(GenerationRuleMatch {
            entity_id: card.entity_id.to_string(),
            card_id: card.card_id.to_string(),
            rule_id: rule.rule_id.to_string(),
            text_sha256: actual_hash,
        });
    }

    fn apply_player(&self, player: &mut PlayerState, assessment: &mut GenerationRuleAssessment) {
        for card in &mut player.hand {
            self.apply_card(card, assessment);
        }
        for card in &mut player.board {
            self.apply_card(card, assessment);
        }
        for card in &mut player.graveyard {
            self.apply_card(card, assessment);
        }
        if let Some(card) = &mut player.hero_power {
            self.apply_card(card, assessment);
        }
        if let Some(card) = &mut player.weapon {
            self.apply_card(card, assessment);
        }
    }

    /// Apply only hash-matched runtime-ready rules. Manual-queue matches are
    /// reported but deliberately left unsupported.
    #[must_use]
    pub fn apply(&self, state: &mut GameState) -> GenerationRuleAssessment {
        let mut assessment = GenerationRuleAssessment {
            ruleset_id: GENERATION_RULESET_ID.to_owned(),
            ..GenerationRuleAssessment::default()
        };
        self.apply_player(&mut state.friendly, &mut assessment);
        self.apply_player(&mut state.opponent, &mut assessment);
        assessment
    }
}

/// Apply the shared embedded stochastic ruleset, failing closed if unavailable.
///
/// # Errors
///
/// Returns unsupported rather than continuing with an empty or partial bundle.
pub fn apply_embedded_generation_rules(
    state: &mut GameState,
) -> Result<GenerationRuleAssessment, SolverError> {
    Ok(embedded_generation_rule_bundle()?.apply(state))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{PoolDestination, PoolSelection};
    use serde_json::json;

    fn state_with_card(card: serde_json::Value) -> GameState {
        serde_json::from_value(json!({
            "state_id": "generation-rules",
            "turn": 1,
            "active_player_id": "f",
            "perspective_player_id": "f",
            "friendly": {
                "player_id": "f",
                "hero": {"entity_id": "fh", "card_id": "HERO", "card_type": "HERO", "health": 30},
                "hand": [card]
            },
            "opponent": {
                "player_id": "o",
                "hero": {"entity_id": "oh", "card_id": "OPP_HERO", "card_type": "HERO", "health": 30}
            }
        }))
        .expect("generation-rule state")
    }

    #[test]
    fn embedded_inventory_is_complete_and_fail_closed() {
        let bundle = embedded_generation_rule_bundle().expect("generation rules");
        assert_eq!(bundle.inventory_count(), 417);
        assert_eq!(bundle.unique_official_cards(), 1811);
        assert_eq!(
            bundle.runtime_ready_count() + bundle.manual_queue_count(),
            bundle.inventory_count()
        );
        assert!(bundle.runtime_ready_count() > 0);
        assert!(bundle.manual_queue_count() > bundle.runtime_ready_count());
        assert!(!bundle.source_run_id().is_empty());
        assert!(!bundle.source_card_defs_build().is_empty());
    }

    #[test]
    fn random_pirate_rule_attaches_a_typed_current_format_pool() {
        let mut state = state_with_card(json!({
            "entity_id": "sky-raider",
            "card_id": "CORE_DRG_024",
            "card_type": "MINION",
            "cost": 1,
            "attack": 1,
            "health": 2,
            "card_text": "<b>Battlecry:</b> Add a random Pirate to your hand.",
            "effect_coverage": "unsupported",
            "unsupported_effects": ["card_text_not_parsed"]
        }));
        let assessment =
            apply_embedded_generation_rules(&mut state).expect("apply generation rule");
        assert_eq!(assessment.matched.len(), 1);
        assert!(assessment.queued.is_empty());
        assert!(assessment.mismatches.is_empty());
        let card = &state.friendly.hand[0];
        assert_eq!(card.effect_coverage, EffectCoverage::Exact);
        assert_eq!(card.rule_version.as_ref(), GENERATION_RULESET_ID);
        assert_eq!(card.effects.len(), 1);
        let effect = &card.effects[0];
        assert_eq!(effect.kind.as_ref(), "generate_from_pool");
        let pool = effect.pool.as_ref().expect("pool");
        assert_eq!(pool.source, CardPoolSource::CurrentFormat);
        assert_eq!(pool.card_types, vec![CardType::Minion]);
        assert_eq!(pool.minion_type_ids, vec![23]);
        assert!(pool.required_keywords.is_empty());
    }

    #[test]
    fn tracking_uses_only_the_canonical_owner_deck_pool() {
        let mut state = state_with_card(json!({
            "entity_id": "tracking",
            "card_id": "CORE_DS1_184",
            "card_type": "SPELL",
            "cost": 1,
            "card_text": "Discover a card from your deck.",
            "effect_coverage": "unsupported",
            "unsupported_effects": ["card_text_not_parsed"]
        }));
        let assessment =
            apply_embedded_generation_rules(&mut state).expect("Tracking generation rule");
        assert_eq!(assessment.matched.len(), 1);
        assert!(assessment.queued.is_empty());
        let effect = &state.friendly.hand[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "discover_from_pool");
        assert_eq!(effect.pool_selection, PoolSelection::Discover);
        assert_eq!(effect.pool_destination, PoolDestination::Hand);
        assert_eq!(effect.offer_count, 3);
        assert!(!effect.with_replacement);
        assert_eq!(
            effect.pool.as_ref().map(|pool| pool.source),
            Some(CardPoolSource::OwnerDeck)
        );
    }

    #[test]
    fn beast_tripwire_has_both_typed_summon_and_repeat_spell_shuffle() {
        let mut state = state_with_card(json!({
            "entity_id": "beast-tripwire",
            "card_id": "JAIL_879",
            "card_type": "SPELL",
            "cost": 2,
            "card_text": "Summon a random 5-Cost Beast. Shuffle 2 spells into your deck that do it again when drawn.",
            "effect_coverage": "unsupported",
            "unsupported_effects": ["card_text_not_parsed"]
        }));
        let assessment =
            apply_embedded_generation_rules(&mut state).expect("Beast Tripwire generation rule");
        assert_eq!(assessment.matched.len(), 1);
        assert!(assessment.queued.is_empty());
        let effects = &state.friendly.hand[0].effects;
        assert_eq!(effects.len(), 2);
        let summon = &effects[0];
        assert_eq!(summon.kind.as_ref(), "summon_from_pool");
        assert_eq!(summon.pool_destination, PoolDestination::Battlefield);
        let pool = summon.pool.as_ref().expect("5-Cost Beast pool");
        assert_eq!(pool.cost_min, Some(5));
        assert_eq!(pool.cost_max, Some(5));
        assert_eq!(pool.card_types, vec![CardType::Minion]);
        assert_eq!(pool.minion_type_ids, vec![20]);
        let shuffle = &effects[1];
        assert_eq!(shuffle.kind.as_ref(), "shuffle_repeat_spell");
        assert_eq!(shuffle.count, 2);
        assert_eq!(shuffle.card_id.as_ref(), "JAIL_879_REPEAT");
    }

    #[test]
    fn product_pool_rule_compiles_to_one_effect_per_cost() {
        let mut state = state_with_card(json!({
            "entity_id": "guard-duty",
            "card_id": "DINO_433",
            "card_type": "SPELL",
            "cost": 7,
            "card_text": "Summon a random 6, 4, and 2-Cost Taunt minion.",
            "effect_coverage": "unsupported",
            "unsupported_effects": ["card_text_not_parsed"]
        }));
        let assessment =
            apply_embedded_generation_rules(&mut state).expect("product generation rule");
        assert_eq!(assessment.matched.len(), 1);
        assert!(assessment.queued.is_empty());
        let effects = &state.friendly.hand[0].effects;
        assert_eq!(effects.len(), 3);
        assert_eq!(
            effects
                .iter()
                .map(|effect| effect.pool.as_ref().and_then(|pool| pool.cost_min))
                .collect::<Vec<_>>(),
            vec![Some(6), Some(4), Some(2)]
        );
        assert!(effects.iter().all(|effect| {
            effect
                .pool
                .as_ref()
                .is_some_and(|pool| pool.required_keywords == vec![Arc::from("taunt")])
        }));
    }

    #[test]
    fn non_play_trigger_rule_stays_in_the_manual_queue() {
        let mut state = state_with_card(json!({
            "entity_id": "peon",
            "card_id": "BAR_022",
            "card_type": "MINION",
            "cost": 2,
            "attack": 2,
            "health": 3,
            "card_text": "Frenzy: Add a random spell from your class to your hand.",
            "effect_coverage": "unsupported",
            "unsupported_effects": ["card_text_not_parsed"]
        }));
        let assessment = apply_embedded_generation_rules(&mut state).expect("queued trigger rule");
        assert!(assessment.matched.is_empty());
        assert_eq!(assessment.queued.len(), 1);
        assert!(
            assessment.queued[0]
                .blockers
                .iter()
                .any(|value| value == "trigger_engine:frenzy")
        );
        assert!(state.friendly.hand[0].effects.is_empty());
    }

    #[test]
    fn text_hash_mismatch_never_attaches_a_pool() {
        let mut state = state_with_card(json!({
            "entity_id": "sky-raider",
            "card_id": "CORE_DRG_024",
            "card_type": "MINION",
            "cost": 1,
            "attack": 1,
            "health": 2,
            "card_text": "Battlecry: Add two random Pirates to your hand.",
            "effect_coverage": "unsupported",
            "unsupported_effects": ["card_text_not_parsed"]
        }));
        let assessment =
            apply_embedded_generation_rules(&mut state).expect("hash mismatch assessment");
        assert!(assessment.matched.is_empty());
        assert_eq!(assessment.mismatches.len(), 1);
        assert_eq!(
            assessment.mismatches[0].reason,
            "english_text_sha256_mismatch"
        );
        assert!(state.friendly.hand[0].effects.is_empty());
    }
}
