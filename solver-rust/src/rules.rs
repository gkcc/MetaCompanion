//! Hash-bound deterministic point-effect rules for public HDT card entities.
//!
//! The embedded bundle is the same reviewed JSON used by the Python worker. A
//! card ID alone is never enough: card type, normalized English text hash,
//! declared intrinsic-mechanic evidence, and public context guards must all
//! match before effects become exact.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::sync::{Arc, OnceLock};

use html_escape::decode_html_entities;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

use crate::error::SolverError;
use crate::model::{Card, CardType, Effect, EffectCoverage, GameState, JsonScalar, PlayerState};

pub const RULESET_ID: &str = "hdt-visible-point-effects-v1";
pub const MATCHING_CONTRACT: &str = concat!(
    "card_id+normalized_english_text_sha256+card_type",
    "+required_intrinsic_mechanics+declared_context_guards"
);
const RULESET_JSON: &str =
    include_str!("../../solver/metacompanion_solver/rules_data/hdt-visible-point-effects-v1.json");
const RULESET_SCHEMA_VERSION: u32 = 1;
const DETERMINISTIC_EFFECT_KINDS: &[&str] = &[
    "damage",
    "damage_all_minions",
    "heal",
    "freeze",
    "armor",
    "buff_attack",
    "buff_health",
    "set_health",
    "gain_hero_attack",
    "gain_mana",
    "summon",
    "set_hero_power_cost",
    "double_one_cost_cards",
    "draw_non_starting_spell_on_weapon_break",
    "damage_split",
    "shuffle_repeat_spell",
    "replay_one_cost_cards",
];
const ALLOWED_REQUIRED_MECHANICS: &[&str] = &["aura", "lifesteal", "trigger"];
const ALLOWED_CONTEXT_TAGS: &[&str] = &[
    "STEADY_SHOT_CAN_TARGET",
    "CURRENT_HEROPOWER_DAMAGE_BONUS",
    "HERO_POWER_DOUBLE",
    "HEROPOWER_DAMAGE",
    "HERO_POWER_DISABLED",
];
const ALLOWED_HAND_RACES: &[&str] = &["DRAGON"];

#[derive(Debug, Deserialize)]
struct RawBundle {
    schema_version: u32,
    ruleset_id: String,
    status: String,
    matching_contract: String,
    source: RawSource,
    rules: Vec<RawRule>,
}

#[derive(Debug, Deserialize)]
struct RawSource {
    official_pool_run_id: String,
    card_defs_build: String,
    rules_generated_from_free_text: bool,
}

#[derive(Debug, Deserialize)]
struct RawRule {
    rule_id: String,
    card_ids: Vec<String>,
    card_type: CardType,
    accepted_texts: Vec<RawAcceptedText>,
    effects: Vec<Effect>,
    #[serde(default)]
    required_mechanics: Vec<String>,
    #[serde(default)]
    resolved_mechanics: Vec<String>,
    #[serde(default)]
    context: RawContext,
}

#[derive(Debug, Deserialize)]
struct RawAcceptedText {
    normalized: String,
    sha256: String,
}

#[derive(Debug, Default, Deserialize)]
struct RawContext {
    #[serde(default)]
    require_complete_owner_public_tags: bool,
    #[serde(default)]
    required_zero_context_tags: Vec<String>,
    #[serde(default)]
    required_absent_friendly_hand_races: Vec<String>,
}

#[derive(Clone, Debug)]
struct StructuredRule {
    rule_id: Arc<str>,
    card_type: CardType,
    accepted_text_sha256: HashSet<String>,
    effects: Arc<[Effect]>,
    required_mechanics: HashSet<String>,
    resolved_mechanics: HashSet<String>,
    require_complete_owner_public_tags: bool,
    required_zero_context_tags: Vec<String>,
    required_absent_friendly_hand_races: Vec<String>,
}

#[derive(Clone, Debug)]
pub struct StructuredRuleBundle {
    rules: Vec<StructuredRule>,
    by_card_id: HashMap<String, usize>,
    source_run_id: String,
    source_card_defs_build: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct RuleMatch {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub text_sha256: String,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct RuleMismatch {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub reason: String,
    pub actual_text_sha256: String,
}

#[derive(Clone, Debug, Default, Eq, PartialEq, Serialize)]
pub struct RuleAssessment {
    pub ruleset_id: String,
    pub matched: Vec<RuleMatch>,
    pub mismatches: Vec<RuleMismatch>,
}

impl RuleAssessment {
    #[must_use]
    pub fn matched_rule_ids(&self) -> Vec<String> {
        let mut values = self
            .matched
            .iter()
            .map(|item| item.rule_id.clone())
            .collect::<Vec<_>>();
        values.sort();
        values.dedup();
        values
    }
}

#[derive(Clone)]
struct OwnerContext {
    public_tags: BTreeMap<Arc<str>, JsonScalar>,
    public_tags_complete: bool,
    active_card_tags: Vec<Arc<BTreeMap<Arc<str>, JsonScalar>>>,
    hand_card_tags: Vec<Arc<BTreeMap<Arc<str>, JsonScalar>>>,
}

/// Normalize presentation-only text differences exactly as the Python rule gate.
#[must_use]
pub fn normalize_card_text(value: &str) -> String {
    let decoded = decode_html_entities(value);
    let replaced = decoded.replace("[x]", " ").replace('\u{a0}', " ");
    let mut without_tags = String::with_capacity(replaced.len());
    let mut inside_tag = false;
    for character in replaced.chars() {
        match character {
            '<' => inside_tag = true,
            '>' if inside_tag => {
                inside_tag = false;
                without_tags.push(' ');
            }
            _ if !inside_tag => without_tags.push(character),
            _ => {}
        }
    }
    let mut without_variables = String::with_capacity(without_tags.len());
    let mut characters = without_tags.chars().peekable();
    while let Some(character) = characters.next() {
        if matches!(character, '$' | '#') && characters.peek().is_some_and(char::is_ascii_digit) {
            continue;
        }
        without_variables.push(character);
    }
    without_variables
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
}

/// Return the lowercase SHA-256 of normalized card text.
#[must_use]
pub fn normalized_text_sha256(value: &str) -> String {
    format!(
        "{:x}",
        Sha256::digest(normalize_card_text(value).as_bytes())
    )
}

fn required(value: &str, path: &str) -> Result<(), SolverError> {
    if value.trim().is_empty() {
        Err(SolverError::schema(path, "must be a non-empty string"))
    } else {
        Ok(())
    }
}

fn validate_effect(effect: &Effect, path: &str) -> Result<(), SolverError> {
    effect.validate(path)?;
    if effect.trigger.as_ref() != "resolution" {
        return Err(SolverError::schema(
            format!("{path}.trigger"),
            "reviewed visible effects must resolve through their existing card lifecycle",
        ));
    }
    let point_effect = matches!(
        effect.kind.as_ref(),
        "damage" | "heal" | "buff_attack" | "buff_health" | "set_health"
    ) && effect.target.as_ref() != "none";
    let freeze_effect =
        effect.kind.as_ref() == "freeze" && effect.amount == 0 && effect.target.as_ref() != "none";
    let global_effect = effect.kind.as_ref() == "damage_all_minions"
        && effect.amount > 0
        && effect.target.as_ref() == "none";
    let owner_effect = matches!(
        effect.kind.as_ref(),
        "armor" | "gain_hero_attack" | "gain_mana"
    ) && effect.target.as_ref() == "none";
    let summon_effect = effect.kind.as_ref() == "summon"
        && effect.target.as_ref() == "none"
        && effect.amount == 0
        && (1..=7).contains(&effect.count)
        && !effect.card_id.trim().is_empty()
        && !effect.name.trim().is_empty()
        && effect.health > 0;
    let hero_power_cost_aura = effect.kind.as_ref() == "set_hero_power_cost"
        && effect.target.as_ref() == "none"
        && effect.amount >= 0
        && u16::try_from(effect.amount).is_ok()
        && effect.hand_count_at_most.is_some();
    let one_cost_card_doubler = effect.kind.as_ref() == "double_one_cost_cards"
        && effect.target.as_ref() == "none"
        && effect.amount == 2;
    let weapon_deathrattle = effect.kind.as_ref() == "draw_non_starting_spell_on_weapon_break"
        && effect.target.as_ref() == "none"
        && effect.amount == 0
        && effect.count == 1
        && effect.card_id.is_empty();
    let split_damage = effect.kind.as_ref() == "damage_split"
        && effect.target.as_ref() == "all_enemy_characters"
        && effect.random
        && effect.amount > 0
        && effect.count == 1
        && effect.card_id.is_empty();
    let shuffle_repeat = effect.kind.as_ref() == "shuffle_repeat_spell"
        && effect.target.as_ref() == "none"
        && !effect.random
        && effect.amount == 0
        && (1..=10).contains(&effect.count)
        && !effect.card_id.trim().is_empty()
        && !effect.name.trim().is_empty();
    let one_cost_replay = effect.kind.as_ref() == "replay_one_cost_cards"
        && effect.target.as_ref() == "none"
        && !effect.random
        && effect.amount == 0
        && effect.count == 1
        && effect.card_id.is_empty();
    let point_or_owner_fields_valid = effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && !effect.has_summoned_minion_keywords();
    let random_target_effect = effect.random
        && point_or_owner_fields_valid
        && ((point_effect && effect.amount > 0) || freeze_effect)
        && !matches!(effect.target.as_ref(), "none" | "self")
        && !matches!(
            effect.target.as_ref(),
            "all_enemy_characters"
                | "all_friendly_characters"
                | "all_enemy_minions"
                | "all_friendly_minions"
                | "all_minions"
                | "all_characters"
        );
    let deterministic_effect = !effect.random
        && (point_effect
            || freeze_effect
            || global_effect
            || owner_effect
            || summon_effect
            || hero_power_cost_aura
            || one_cost_card_doubler
            || weapon_deathrattle
            || shuffle_repeat
            || one_cost_replay);
    if !DETERMINISTIC_EFFECT_KINDS.contains(&effect.kind.as_ref())
        || ((point_effect || owner_effect || global_effect)
            && (effect.amount <= 0 || !point_or_owner_fields_valid))
        || (freeze_effect && !point_or_owner_fields_valid)
        || (hero_power_cost_aura && !point_or_owner_fields_valid)
        || (one_cost_card_doubler && !point_or_owner_fields_valid)
        || !(random_target_effect || split_damage || deterministic_effect)
        || (effect.kind.as_ref() != "set_hero_power_cost" && effect.hand_count_at_most.is_some())
        || (effect.kind.as_ref() != "summon" && effect.summoned_card_effects_unmodeled)
    {
        return Err(SolverError::schema(
            path,
            "effect is outside reviewed visible-effect-v1",
        ));
    }
    Ok(())
}

#[allow(clippy::too_many_lines)]
fn load_bundle(value: &str) -> Result<StructuredRuleBundle, SolverError> {
    let raw: RawBundle = serde_json::from_str(value)?;
    if raw.schema_version != RULESET_SCHEMA_VERSION {
        return Err(SolverError::schema(
            "rules.schema_version",
            "unsupported structured rule schema",
        ));
    }
    if raw.ruleset_id != RULESET_ID
        || raw.status != "complete"
        || raw.matching_contract != MATCHING_CONTRACT
    {
        return Err(SolverError::schema(
            "rules",
            "structured rule bundle identity/status/matching contract mismatch",
        ));
    }
    if raw.source.rules_generated_from_free_text {
        return Err(SolverError::schema(
            "rules.source.rules_generated_from_free_text",
            "must be false",
        ));
    }
    required(
        &raw.source.official_pool_run_id,
        "rules.source.official_pool_run_id",
    )?;
    required(&raw.source.card_defs_build, "rules.source.card_defs_build")?;
    if raw.rules.is_empty() {
        return Err(SolverError::schema("rules.rules", "must not be empty"));
    }
    let mut rules = Vec::with_capacity(raw.rules.len());
    let mut by_card_id = HashMap::new();
    let mut rule_ids = HashSet::new();
    for (index, row) in raw.rules.into_iter().enumerate() {
        let path = format!("rules.rules[{index}]");
        required(&row.rule_id, &format!("{path}.rule_id"))?;
        if !rule_ids.insert(row.rule_id.clone()) {
            return Err(SolverError::schema(
                format!("{path}.rule_id"),
                "duplicate rule ID",
            ));
        }
        if row.card_ids.is_empty() || row.accepted_texts.is_empty() || row.effects.is_empty() {
            return Err(SolverError::schema(
                &path,
                "card_ids, accepted_texts, and effects must not be empty",
            ));
        }
        if !matches!(
            row.card_type,
            CardType::Spell
                | CardType::Minion
                | CardType::Weapon
                | CardType::HeroPower
                | CardType::Location
        ) {
            return Err(SolverError::schema(
                format!("{path}.card_type"),
                "is outside deterministic point-effect-v1",
            ));
        }
        let mut accepted = HashSet::new();
        for (text_index, text) in row.accepted_texts.into_iter().enumerate() {
            let text_path = format!("{path}.accepted_texts[{text_index}]");
            required(&text.normalized, &format!("{text_path}.normalized"))?;
            if normalize_card_text(&text.normalized) != text.normalized {
                return Err(SolverError::schema(
                    format!("{text_path}.normalized"),
                    "is not canonical",
                ));
            }
            if normalized_text_sha256(&text.normalized) != text.sha256.to_ascii_lowercase() {
                return Err(SolverError::schema(
                    format!("{text_path}.sha256"),
                    "does not match normalized text",
                ));
            }
            accepted.insert(text.sha256.to_ascii_lowercase());
        }
        for (effect_index, effect) in row.effects.iter().enumerate() {
            validate_effect(effect, &format!("{path}.effects[{effect_index}]"))?;
        }
        let required_mechanics = row
            .required_mechanics
            .iter()
            .enumerate()
            .map(|(mechanic_index, value)| {
                required(
                    value,
                    &format!("{path}.required_mechanics[{mechanic_index}]"),
                )?;
                Ok(value.trim().to_ascii_lowercase().replace(['-', ' '], "_"))
            })
            .collect::<Result<Vec<_>, SolverError>>()?;
        let required_mechanic_set = required_mechanics.iter().cloned().collect::<HashSet<_>>();
        if required_mechanic_set.len() != required_mechanics.len() {
            return Err(SolverError::schema(
                format!("{path}.required_mechanics"),
                "contains normalized duplicates",
            ));
        }
        if required_mechanic_set
            .iter()
            .any(|mechanic| !ALLOWED_REQUIRED_MECHANICS.contains(&mechanic.as_str()))
        {
            return Err(SolverError::schema(
                format!("{path}.required_mechanics"),
                "contains an unsupported intrinsic mechanic",
            ));
        }
        let resolved_mechanics = row
            .resolved_mechanics
            .iter()
            .map(|value| value.trim().to_ascii_lowercase().replace(['-', ' '], "_"))
            .collect::<HashSet<_>>();
        if required_mechanic_set
            .iter()
            .any(|mechanic| resolved_mechanics.contains(mechanic))
        {
            return Err(SolverError::schema(
                &path,
                "must not resolve a required intrinsic mechanic",
            ));
        }
        if row
            .context
            .required_zero_context_tags
            .iter()
            .any(|tag| !ALLOWED_CONTEXT_TAGS.contains(&tag.trim().to_ascii_uppercase().as_str()))
        {
            return Err(SolverError::schema(
                format!("{path}.context.required_zero_context_tags"),
                "contains an unknown public GameTag",
            ));
        }
        if !row.context.required_zero_context_tags.is_empty()
            && !row.context.require_complete_owner_public_tags
        {
            return Err(SolverError::schema(
                format!("{path}.context"),
                "guarded tags require complete owner public tags",
            ));
        }
        let required_absent_friendly_hand_races = row
            .context
            .required_absent_friendly_hand_races
            .iter()
            .map(|race| race.trim().to_ascii_uppercase())
            .collect::<Vec<_>>();
        if required_absent_friendly_hand_races
            .iter()
            .any(|race| !ALLOWED_HAND_RACES.contains(&race.as_str()))
        {
            return Err(SolverError::schema(
                format!("{path}.context.required_absent_friendly_hand_races"),
                "contains an unknown card race",
            ));
        }
        if required_absent_friendly_hand_races
            .iter()
            .collect::<HashSet<_>>()
            .len()
            != required_absent_friendly_hand_races.len()
        {
            return Err(SolverError::schema(
                format!("{path}.context.required_absent_friendly_hand_races"),
                "contains normalized duplicates",
            ));
        }
        let rule_index = rules.len();
        for card_id in row.card_ids {
            required(&card_id, &format!("{path}.card_ids"))?;
            if by_card_id.insert(card_id.clone(), rule_index).is_some() {
                return Err(SolverError::schema(
                    format!("{path}.card_ids"),
                    format!("card ID {card_id:?} is registered twice"),
                ));
            }
        }
        rules.push(StructuredRule {
            rule_id: Arc::from(row.rule_id),
            card_type: row.card_type,
            accepted_text_sha256: accepted,
            effects: row.effects.into(),
            required_mechanics: required_mechanic_set,
            resolved_mechanics,
            require_complete_owner_public_tags: row.context.require_complete_owner_public_tags,
            required_zero_context_tags: row
                .context
                .required_zero_context_tags
                .into_iter()
                .map(|value| value.trim().to_ascii_uppercase())
                .collect(),
            required_absent_friendly_hand_races,
        });
    }
    Ok(StructuredRuleBundle {
        rules,
        by_card_id,
        source_run_id: raw.source.official_pool_run_id,
        source_card_defs_build: raw.source.card_defs_build,
    })
}

static EMBEDDED_BUNDLE: OnceLock<Result<StructuredRuleBundle, String>> = OnceLock::new();

/// Return the validated, compile-time embedded reviewed rule bundle.
///
/// # Errors
///
/// Returns an unsupported-rules error if the shared bundle is malformed. The caller
/// must not continue with an empty or partially loaded ruleset.
pub fn embedded_rule_bundle() -> Result<&'static StructuredRuleBundle, SolverError> {
    match EMBEDDED_BUNDLE
        .get_or_init(|| load_bundle(RULESET_JSON).map_err(|error| error.to_string()))
    {
        Ok(bundle) => Ok(bundle),
        Err(error) => Err(SolverError::Unsupported(format!(
            "structured card-rule bundle is unavailable: {error}"
        ))),
    }
}

impl StructuredRuleBundle {
    #[must_use]
    pub fn ruleset_id(&self) -> &'static str {
        RULESET_ID
    }

    #[must_use]
    pub fn rule_count(&self) -> usize {
        self.rules.len()
    }

    #[must_use]
    pub fn registered_card_id_count(&self) -> usize {
        self.by_card_id.len()
    }

    #[must_use]
    pub fn context_guarded_rule_count(&self) -> usize {
        self.rules
            .iter()
            .filter(|rule| {
                rule.require_complete_owner_public_tags
                    || !rule.required_absent_friendly_hand_races.is_empty()
            })
            .count()
    }

    #[must_use]
    pub fn required_mechanic_guarded_rule_count(&self) -> usize {
        self.rules
            .iter()
            .filter(|rule| !rule.required_mechanics.is_empty())
            .count()
    }

    #[must_use]
    pub fn source_run_id(&self) -> &str {
        &self.source_run_id
    }

    #[must_use]
    pub fn source_card_defs_build(&self) -> &str {
        &self.source_card_defs_build
    }

    #[must_use]
    pub fn rule_ids(&self) -> Vec<String> {
        let mut values = self
            .rules
            .iter()
            .map(|rule| rule.rule_id.to_string())
            .collect::<Vec<_>>();
        values.sort();
        values
    }

    fn context_mismatch(rule: &StructuredRule, context: &OwnerContext) -> Option<&'static str> {
        if rule.require_complete_owner_public_tags && !context.public_tags_complete {
            return Some("owner_public_rule_tags_unavailable");
        }
        for tag in &rule.required_zero_context_tags {
            if game_tag_int(&context.public_tags, tag).unwrap_or(0) != 0
                || context
                    .active_card_tags
                    .iter()
                    .any(|tags| game_tag_int(tags, tag).unwrap_or(0) != 0)
            {
                return Some("context_tag_active");
            }
        }
        for race in &rule.required_absent_friendly_hand_races {
            let Some(race_id) = card_race_id(race) else {
                return Some("context_hand_race_unknown");
            };
            if context
                .hand_card_tags
                .iter()
                .any(|tags| game_tag_int_with_id(tags, "CARDRACE", 200) == Some(race_id))
            {
                return Some("context_hand_race_present");
            }
        }
        None
    }

    fn required_mechanic_mismatch(rule: &StructuredRule, card: &Card) -> Option<&'static str> {
        for mechanic in &rule.required_mechanics {
            if mechanic == "lifesteal" && !card.lifesteal {
                return Some("required_mechanic_unproven");
            }
            if mechanic == "aura" && game_tag_int(&card.tags, "AURA").unwrap_or(0) == 0 {
                return Some("required_mechanic_unproven");
            }
            if mechanic == "trigger" && game_tag_int(&card.tags, "TRIGGER_VISUAL").unwrap_or(0) == 0
            {
                return Some("required_mechanic_unproven");
            }
        }
        None
    }

    fn apply_card(&self, card: &mut Card, context: &OwnerContext, assessment: &mut RuleAssessment) {
        let Some(index) = self.by_card_id.get(card.card_id.as_ref()) else {
            return;
        };
        let rule = &self.rules[*index];
        let actual_hash = normalized_text_sha256(&card.card_text);
        let mismatch = if card.card_type != rule.card_type {
            Some("card_type_mismatch")
        } else if card.card_text.trim().is_empty() {
            Some("english_text_missing")
        } else if !rule.accepted_text_sha256.contains(&actual_hash) {
            Some("english_text_sha256_mismatch")
        } else if let Some(reason) = Self::required_mechanic_mismatch(rule, card) {
            Some(reason)
        } else if let Some(reason) = Self::context_mismatch(rule, context) {
            Some(reason)
        } else if !card.effects.is_empty()
            && (card.rule_id != rule.rule_id || card.rule_version.as_ref() != RULESET_ID)
        {
            Some("prestructured_effect_conflict")
        } else {
            None
        };
        if let Some(reason) = mismatch {
            assessment.mismatches.push(RuleMismatch {
                entity_id: card.entity_id.to_string(),
                card_id: card.card_id.to_string(),
                rule_id: rule.rule_id.to_string(),
                reason: reason.to_owned(),
                actual_text_sha256: actual_hash,
            });
            return;
        }
        card.effects = Arc::clone(&rule.effects);
        card.rule_id = Arc::clone(&rule.rule_id);
        card.rule_version = Arc::from(RULESET_ID);
        card.rule_text_sha256 = Arc::from(actual_hash.as_str());
        let retained = card
            .unsupported_effects
            .iter()
            .filter(|mechanic| {
                mechanic.as_ref() != "card_text_not_parsed"
                    && !rule.resolved_mechanics.contains(mechanic.as_ref())
            })
            .cloned()
            .collect::<Vec<_>>();
        card.unsupported_effects = retained.into();
        card.effect_coverage = if card.unsupported_effects.is_empty() {
            EffectCoverage::Exact
        } else {
            EffectCoverage::Unsupported
        };
        assessment.matched.push(RuleMatch {
            entity_id: card.entity_id.to_string(),
            card_id: card.card_id.to_string(),
            rule_id: rule.rule_id.to_string(),
            text_sha256: actual_hash,
        });
    }

    fn apply_player(&self, player: &mut PlayerState, assessment: &mut RuleAssessment) {
        let mut active_card_tags = vec![Arc::clone(&player.hero.tags)];
        active_card_tags.extend(player.board.iter().map(|card| Arc::clone(&card.tags)));
        active_card_tags.extend(player.hero_power.iter().map(|card| Arc::clone(&card.tags)));
        active_card_tags.extend(player.weapon.iter().map(|card| Arc::clone(&card.tags)));
        let context = OwnerContext {
            public_tags: player.public_rule_tags.clone(),
            public_tags_complete: player.public_rule_tags_complete,
            active_card_tags,
            hand_card_tags: player
                .hand
                .iter()
                .map(|card| Arc::clone(&card.tags))
                .collect(),
        };
        for card in &mut player.hand {
            self.apply_card(card, &context, assessment);
        }
        for card in &mut player.board {
            self.apply_card(card, &context, assessment);
        }
        for card in &mut player.graveyard {
            self.apply_card(card, &context, assessment);
        }
        if let Some(card) = &mut player.hero_power {
            self.apply_card(card, &context, assessment);
        }
        if let Some(card) = &mut player.weapon {
            self.apply_card(card, &context, assessment);
        }
    }

    /// Apply only fully matched structured rules to visible entities.
    pub fn apply(&self, state: &mut GameState) -> RuleAssessment {
        let mut assessment = RuleAssessment {
            ruleset_id: RULESET_ID.to_owned(),
            ..RuleAssessment::default()
        };
        self.apply_player(&mut state.friendly, &mut assessment);
        self.apply_player(&mut state.opponent, &mut assessment);
        assessment
    }
}

fn game_tag_int(tags: &BTreeMap<Arc<str>, JsonScalar>, name: &str) -> Option<i64> {
    let enum_id = match name {
        "TRIGGER_VISUAL" => 32,
        "AURA" => 362,
        "STEADY_SHOT_CAN_TARGET" => 383,
        "CURRENT_HEROPOWER_DAMAGE_BONUS" => 395,
        "HERO_POWER_DOUBLE" => 366,
        "HEROPOWER_DAMAGE" => 396,
        "HERO_POWER_DISABLED" => 777,
        _ => return None,
    };
    tags.iter()
        .find(|(key, _)| {
            key.eq_ignore_ascii_case(name)
                || key.as_ref() == enum_id.to_string()
                || (name == "HERO_POWER_DOUBLE"
                    && key.eq_ignore_ascii_case("TAG_HERO_POWER_DOUBLE"))
        })
        .and_then(|(_, value)| match value {
            JsonScalar::Bool(value) => Some(i64::from(*value)),
            JsonScalar::Integer(value) => Some(*value),
            JsonScalar::Float(value) if value.is_finite() && value.fract() == 0.0 => {
                value.to_string().parse().ok()
            }
            JsonScalar::String(value) => value.trim().parse().ok(),
            JsonScalar::Float(_) | JsonScalar::Null => None,
        })
}

fn game_tag_int_with_id(
    tags: &BTreeMap<Arc<str>, JsonScalar>,
    name: &str,
    enum_id: u16,
) -> Option<i64> {
    let enum_id = enum_id.to_string();
    tags.iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name) || key.as_ref() == enum_id)
        .and_then(|(_, value)| match value {
            JsonScalar::Bool(value) => Some(i64::from(*value)),
            JsonScalar::Integer(value) => Some(*value),
            JsonScalar::Float(value) if value.is_finite() && value.fract() == 0.0 => {
                value.to_string().parse().ok()
            }
            JsonScalar::String(value) => value.trim().parse().ok(),
            JsonScalar::Float(_) | JsonScalar::Null => None,
        })
}

fn card_race_id(name: &str) -> Option<i64> {
    match name {
        "DRAGON" => Some(24),
        _ => None,
    }
}

/// Apply the shared embedded ruleset, failing closed if it cannot be validated.
///
/// # Errors
///
/// Returns an unsupported-rules error if the embedded bundle is unavailable.
pub fn apply_embedded_rules(state: &mut GameState) -> Result<RuleAssessment, SolverError> {
    Ok(embedded_rule_bundle()?.apply(state))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn text_normalization_matches_reviewed_hash_contract() {
        assert_eq!(
            normalize_card_text("<b>Battlecry:</b> Deal $1\n damage."),
            "Battlecry: Deal 1 damage."
        );
        assert_eq!(
            normalized_text_sha256("Deal $6 damage."),
            "c4511dfa5d7f8d7e36e8ae82694389796d7c99a70b99bd3539295a2bde829a0f"
        );
    }

    #[test]
    fn shared_bundle_is_complete_and_not_generated_from_text() {
        let bundle = embedded_rule_bundle().expect("embedded rules");
        assert_eq!(bundle.ruleset_id(), RULESET_ID);
        assert_eq!(bundle.rule_count(), 47);
        assert_eq!(bundle.registered_card_id_count(), 205);
        assert_eq!(bundle.context_guarded_rule_count(), 5);
        assert_eq!(bundle.required_mechanic_guarded_rule_count(), 5);
        assert!(!bundle.source_run_id().is_empty());
        assert!(!bundle.source_card_defs_build().is_empty());
    }

    #[test]
    fn required_intrinsic_mechanics_are_allowlisted_and_not_resolved() {
        let original: serde_json::Value = serde_json::from_str(RULESET_JSON).expect("rules JSON");
        let rule_index = original["rules"]
            .as_array()
            .expect("rules")
            .iter()
            .position(|rule| rule["card_ids"] == serde_json::json!(["CORE_ICC_055"]))
            .expect("Drain Soul rule");
        for (required_mechanics, resolved_mechanics, expected) in [
            (
                serde_json::json!(["windfury"]),
                serde_json::json!([]),
                "unsupported intrinsic mechanic",
            ),
            (
                serde_json::json!(["lifesteal"]),
                serde_json::json!(["lifesteal"]),
                "must not resolve a required intrinsic mechanic",
            ),
        ] {
            let mut candidate = original.clone();
            candidate["rules"][rule_index]["required_mechanics"] = required_mechanics;
            candidate["rules"][rule_index]["resolved_mechanics"] = resolved_mechanics;
            let error = load_bundle(&serde_json::to_string(&candidate).expect("serialize"))
                .expect_err("invalid required mechanic must fail closed");
            assert!(error.to_string().contains(expected), "{error}");
        }
    }

    #[test]
    fn queldorei_fletcher_rule_requires_text_identity_and_public_aura_evidence() {
        let raw = serde_json::json!({
            "state_id": "s",
            "turn": 1,
            "active_player_id": "f",
            "perspective_player_id": "f",
            "friendly": {
                "player_id": "f",
                "hero": {"entity_id": "fh", "card_id": "HERO", "card_type": "HERO", "health": 30},
                "board": [{
                    "entity_id": "aura",
                    "card_id": "TIME_606",
                    "card_type": "MINION",
                    "health": 3,
                    "card_text": "Your Hero Power costs (0) while your hand has 3 or less cards.",
                    "tags": {"AURA": 1},
                    "unsupported_effects": ["card_text_not_parsed"],
                    "effect_coverage": "unsupported"
                }]
            },
            "opponent": {
                "player_id": "o",
                "hero": {"entity_id": "oh", "card_id": "OPP_HERO", "card_type": "HERO", "health": 30}
            }
        });
        let mut state: GameState = serde_json::from_value(raw.clone()).expect("aura state");
        let assessment = apply_embedded_rules(&mut state).expect("apply aura rule");
        assert!(assessment.matched.iter().any(|item| {
            item.card_id == "TIME_606"
                && item.rule_id == "time-queldorei-fletcher-hero-power-cost-aura-v1"
        }));
        let effect = &state.friendly.board[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "set_hero_power_cost");
        assert_eq!(effect.amount, 0);
        assert_eq!(effect.hand_count_at_most, Some(3));
        assert_eq!(
            state.friendly.board[0].effect_coverage,
            EffectCoverage::Exact
        );

        let mut missing_aura: GameState = serde_json::from_value(raw).expect("aura state");
        Arc::make_mut(&mut missing_aura.friendly.board[0].tags).remove("AURA");
        let assessment = apply_embedded_rules(&mut missing_aura).expect("fail-closed rule");
        assert!(assessment.matched.is_empty());
        assert!(assessment.mismatches.iter().any(|item| {
            item.card_id == "TIME_606" && item.reason == "required_mechanic_unproven"
        }));
    }
}
