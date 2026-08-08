//! Hash-bound generic effects compiled from a closed card-text grammar.
//!
//! These rules are intentionally separate from the reviewed exact catalogue.
//! A full CardID, card type, and normalized English-text hash match is required,
//! but a successful match only upgrades the card to `EffectCoverage::Generic`.
//! Text templates can therefore participate in visible-state approximate search
//! without ever being presented as exact transition or optimality evidence.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, OnceLock};

use serde::{Deserialize, Serialize};

use crate::error::SolverError;
use crate::model::{
    Card, CardPoolSource, CardType, Effect, EffectCoverage, GameState, PlayerState,
    PoolDestination, PoolSelection,
};
use crate::rules::{normalize_card_text, normalized_text_sha256};

pub const TEMPLATE_RULESET_ID: &str = "hdt-text-template-effects-v1";
pub const TEMPLATE_MATCHING_CONTRACT: &str =
    "card_id+normalized_english_text_sha256+card_type+closed_grammar_v1";

const RULESET_JSON: &str =
    include_str!("../../solver/metacompanion_solver/rules_data/hdt-text-template-effects-v1.json");
const RULESET_SCHEMA_VERSION: u32 = 1;

#[derive(Debug, Deserialize)]
struct RawBundle {
    schema_version: u32,
    ruleset_id: String,
    status: String,
    matching_contract: String,
    runtime_effect_coverage: String,
    source: RawSource,
    counts: RawCounts,
    rules: Vec<RawRule>,
}

#[derive(Debug, Deserialize)]
struct RawSource {
    official_pool_run_id: String,
    card_defs_build: String,
    rules_generated_from_free_text: bool,
    exact_claim_allowed: bool,
}

#[derive(Debug, Deserialize)]
struct RawCounts {
    unique_official_cards: usize,
    compiled_generic_rules: usize,
    already_exact_cards: usize,
    uncompiled_cards: usize,
}

#[derive(Debug, Deserialize)]
struct RawRule {
    rule_id: String,
    card_ids: Vec<String>,
    card_type: CardType,
    trigger: String,
    accepted_texts: Vec<RawAcceptedText>,
    effects: Vec<Effect>,
}

#[derive(Debug, Deserialize)]
struct RawAcceptedText {
    normalized: String,
    sha256: String,
}

#[derive(Clone, Debug)]
struct TemplateRule {
    rule_id: Arc<str>,
    card_type: CardType,
    accepted_text_sha256: HashSet<String>,
    effects: Arc<[Effect]>,
    resolved_mechanics: HashSet<String>,
}

#[derive(Clone, Debug)]
pub struct TemplateRuleBundle {
    rules: Vec<TemplateRule>,
    by_card_id: HashMap<String, usize>,
    source_run_id: String,
    source_card_defs_build: String,
    unique_official_cards: usize,
    already_exact_cards: usize,
    uncompiled_cards: usize,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct TemplateRuleMatch {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub text_sha256: String,
    pub effect_coverage: &'static str,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct TemplateRuleMismatch {
    pub entity_id: String,
    pub card_id: String,
    pub rule_id: String,
    pub reason: String,
    pub actual_text_sha256: String,
}

#[derive(Clone, Debug, Default, Eq, PartialEq, Serialize)]
pub struct TemplateRuleAssessment {
    pub ruleset_id: String,
    pub matched: Vec<TemplateRuleMatch>,
    pub mismatches: Vec<TemplateRuleMismatch>,
    pub exact_claim_allowed: bool,
}

static EMBEDDED_BUNDLE: OnceLock<Result<TemplateRuleBundle, String>> = OnceLock::new();

fn required(value: &str, path: &str) -> Result<(), SolverError> {
    if value.trim().is_empty() {
        Err(SolverError::schema(path, "must be a non-empty string"))
    } else {
        Ok(())
    }
}

fn validate_trigger(card_type: CardType, trigger: &str, path: &str) -> Result<(), SolverError> {
    let valid = matches!(
        (card_type, trigger),
        (CardType::Minion | CardType::Weapon, "battlecry")
            | (CardType::Minion | CardType::Weapon, "deathrattle")
            | (
                CardType::Minion | CardType::Weapon,
                "battlecry_and_deathrattle"
            )
            | (CardType::Minion, "frenzy" | "battlecry_and_frenzy")
            | (CardType::Minion | CardType::Weapon, "multi_event")
            | (CardType::Minion, "after_spell_cast")
            | (CardType::Minion, "spellburst" | "after_hero_power")
            | (CardType::Minion | CardType::Weapon, "after_hero_attack")
            | (
                CardType::Minion | CardType::Weapon | CardType::Location,
                "turn_start" | "turn_end"
            )
            | (CardType::Spell | CardType::HeroPower, "play_resolution")
            | (CardType::Location, "location_activation")
    );
    if valid {
        Ok(())
    } else {
        Err(SolverError::schema(
            path,
            "trigger is not executable for this card type",
        ))
    }
}

fn effect_trigger_matches_rule(rule_trigger: &str, effect_trigger: &str) -> bool {
    match rule_trigger {
        "battlecry" | "play_resolution" | "location_activation" => effect_trigger == "resolution",
        "deathrattle" => effect_trigger == "deathrattle",
        "battlecry_and_deathrattle" => {
            matches!(effect_trigger, "resolution" | "deathrattle")
        }
        "battlecry_and_frenzy" => matches!(effect_trigger, "resolution" | "frenzy"),
        "after_spell_cast" | "spellburst" | "frenzy" | "after_hero_attack" | "after_hero_power"
        | "turn_start" | "turn_end" => effect_trigger == rule_trigger,
        "multi_event" => matches!(
            effect_trigger,
            "resolution"
                | "deathrattle"
                | "after_spell_cast"
                | "spellburst"
                | "frenzy"
                | "after_hero_attack"
                | "after_hero_power"
                | "turn_start"
                | "turn_end"
        ),
        _ => false,
    }
}

fn chance_effect_trigger_is_executable(trigger: &str) -> bool {
    matches!(
        trigger,
        "resolution"
            | "deathrattle"
            | "after_spell_cast"
            | "spellburst"
            | "frenzy"
            | "after_hero_attack"
            | "after_hero_power"
            | "turn_end"
    )
}

fn validate_generic_effect(effect: &Effect, path: &str) -> Result<(), SolverError> {
    effect.validate(path)?;
    let no_summoned_keywords = !effect.has_summoned_minion_keywords();
    let point = matches!(
        effect.kind.as_ref(),
        "damage" | "heal" | "buff_attack" | "buff_health" | "set_attack" | "set_health"
    ) && effect.amount > 0
        && effect.target.as_ref() != "none"
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let freeze = effect.kind.as_ref() == "freeze"
        && effect.amount == 0
        && effect.target.as_ref() != "none"
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let owner = matches!(
        effect.kind.as_ref(),
        "armor"
            | "gain_hero_attack"
            | "gain_mana"
            | "refresh_mana"
            | "gain_mana_crystals"
            | "gain_empty_mana_crystals"
    ) && effect.amount > 0
        && effect.target.as_ref() == "none"
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let draw = effect.kind.as_ref() == "draw"
        && effect.amount == 0
        && effect.target.as_ref() == "none"
        && (1..=10).contains(&effect.count)
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let zone_draw = matches!(
        effect.kind.as_ref(),
        "draw_opponent" | "draw_both_players" | "draw_until_hand_count"
    ) && effect.amount == 0
        && effect.target.as_ref() == "none"
        && (1..=10).contains(&effect.count)
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let weapon_buff = effect.kind.as_ref() == "buff_weapon_attack"
        && effect.amount > 0
        && effect.target.as_ref() == "none"
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let filtered_draw = effect.kind.as_ref() == "draw_from_pool"
        && effect.amount == 0
        && effect.target.as_ref() == "none"
        && (1..=7).contains(&effect.count)
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords
        && effect.random
        && chance_effect_trigger_is_executable(effect.trigger.as_ref())
        && effect.pool_selection == PoolSelection::UniformRandom
        && effect.pool_destination == PoolDestination::Hand
        && effect.offer_count == 1
        && !effect.with_replacement
        && effect.created_card_cost_delta == 0
        && effect
            .pool
            .as_ref()
            .is_some_and(|pool| pool.source == CardPoolSource::OwnerDeck);
    let random_target = effect.random
        && chance_effect_trigger_is_executable(effect.trigger.as_ref())
        && effect.pool.is_none()
        && matches!(
            effect.target.as_ref(),
            "enemy_character"
                | "friendly_character"
                | "any_character"
                | "enemy_minion"
                | "friendly_minion"
                | "any_minion"
                | "any_undamaged_minion"
                | "damaged_enemy_minion"
                | "enemy_hero"
                | "friendly_hero"
        )
        && (freeze
            || (point
                && matches!(
                    effect.kind.as_ref(),
                    "damage" | "heal" | "buff_attack" | "buff_health" | "set_health"
                )));
    let summon = effect.kind.as_ref() == "summon"
        && effect.amount == 0
        && effect.target.as_ref() == "none"
        && (1..=7).contains(&effect.count)
        && !effect.card_id.trim().is_empty()
        && !effect.name.trim().is_empty()
        && effect.health > 0
        && effect.durability == 0;
    let destroy = effect.kind.as_ref() == "destroy"
        && effect.amount == 0
        && matches!(
            effect.target.as_ref(),
            "enemy_minion"
                | "friendly_minion"
                | "any_minion"
                | "damaged_enemy_minion"
                | "all_enemy_minions"
                | "all_friendly_minions"
                | "all_minions"
        )
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let transform = effect.kind.as_ref() == "transform"
        && effect.amount == 0
        && matches!(
            effect.target.as_ref(),
            "enemy_minion" | "friendly_minion" | "any_minion" | "self"
        )
        && effect.count == 1
        && !effect.card_id.trim().is_empty()
        && !effect.name.trim().is_empty()
        && effect.health > 0
        && effect.durability == 0;
    let grant_keywords = effect.kind.as_ref() == "grant_keywords"
        && effect.amount == 0
        && effect.target.as_ref() != "none"
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && !no_summoned_keywords;
    let destroy_board = effect.kind.as_ref() == "destroy_all_minions_and_locations"
        && effect.amount == 0
        && effect.target.as_ref() == "none"
        && effect.count == 1
        && effect.card_id.is_empty()
        && effect.attack == 0
        && effect.durability == 0
        && no_summoned_keywords;
    let equip_weapon = effect.kind.as_ref() == "equip_weapon"
        && effect.amount == 0
        && effect.target.as_ref() == "none"
        && effect.count == 1
        && !effect.card_id.trim().is_empty()
        && !effect.name.trim().is_empty()
        && effect.attack > 0
        && effect.durability > 0;
    if effect.random != (filtered_draw || random_target)
        || !(point
            || freeze
            || owner
            || draw
            || zone_draw
            || weapon_buff
            || filtered_draw
            || summon
            || destroy
            || transform
            || grant_keywords
            || destroy_board
            || equip_weapon)
        || (effect.pool.is_some() != filtered_draw)
        || effect.hand_count_at_most.is_some()
        || effect.summoned_card_effects_unmodeled
    {
        return Err(SolverError::schema(
            path,
            "effect is outside generic closed-grammar-v1",
        ));
    }
    Ok(())
}

#[allow(clippy::too_many_lines)]
fn load_bundle(value: &str) -> Result<TemplateRuleBundle, SolverError> {
    let raw: RawBundle = serde_json::from_str(value)?;
    if raw.schema_version != RULESET_SCHEMA_VERSION
        || raw.ruleset_id != TEMPLATE_RULESET_ID
        || raw.status != "complete"
        || raw.matching_contract != TEMPLATE_MATCHING_CONTRACT
        || raw.runtime_effect_coverage != "generic"
    {
        return Err(SolverError::schema(
            "template_rules",
            "bundle identity, status, matching contract, or coverage is invalid",
        ));
    }
    if !raw.source.rules_generated_from_free_text || raw.source.exact_claim_allowed {
        return Err(SolverError::schema(
            "template_rules.source",
            "text templates must be generic-only generated rules",
        ));
    }
    required(
        &raw.source.official_pool_run_id,
        "template_rules.source.official_pool_run_id",
    )?;
    required(
        &raw.source.card_defs_build,
        "template_rules.source.card_defs_build",
    )?;
    if raw.counts.compiled_generic_rules != raw.rules.len()
        || raw.counts.unique_official_cards
            != raw.counts.compiled_generic_rules
                + raw.counts.already_exact_cards
                + raw.counts.uncompiled_cards
    {
        return Err(SolverError::schema(
            "template_rules.counts",
            "coverage counts are inconsistent",
        ));
    }

    let mut rules = Vec::with_capacity(raw.rules.len());
    let mut by_card_id = HashMap::new();
    let mut rule_ids = HashSet::new();
    for (index, row) in raw.rules.into_iter().enumerate() {
        let path = format!("template_rules.rules[{index}]");
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
        validate_trigger(row.card_type, &row.trigger, &format!("{path}.trigger"))?;
        if row.effects.iter().any(|effect| {
            (effect.random || effect.pool.is_some())
                && !chance_effect_trigger_is_executable(effect.trigger.as_ref())
        }) {
            return Err(SolverError::schema(
                format!("{path}.trigger"),
                "chance template effect trigger is not executable",
            ));
        }
        let resolved_mechanics = match row.trigger.as_str() {
            "battlecry" => HashSet::from(["battlecry".to_owned()]),
            "deathrattle" => HashSet::from(["deathrattle".to_owned()]),
            "battlecry_and_deathrattle" => {
                HashSet::from(["battlecry".to_owned(), "deathrattle".to_owned()])
            }
            "battlecry_and_frenzy" => HashSet::from(["battlecry".to_owned(), "frenzy".to_owned()]),
            "after_spell_cast" | "after_hero_attack" | "after_hero_power" | "turn_start"
            | "turn_end" => HashSet::from(["trigger".to_owned()]),
            "spellburst" => HashSet::from(["trigger".to_owned(), "spellburst".to_owned()]),
            "frenzy" => HashSet::from(["trigger".to_owned(), "frenzy".to_owned()]),
            "multi_event" => {
                let mut mechanics = HashSet::new();
                if row
                    .effects
                    .iter()
                    .any(|effect| effect.trigger.as_ref() == "resolution")
                {
                    mechanics.insert("battlecry".to_owned());
                }
                if row
                    .effects
                    .iter()
                    .any(|effect| effect.trigger.as_ref() == "deathrattle")
                {
                    mechanics.insert("deathrattle".to_owned());
                }
                if row.effects.iter().any(|effect| {
                    matches!(
                        effect.trigger.as_ref(),
                        "after_spell_cast"
                            | "spellburst"
                            | "frenzy"
                            | "after_hero_attack"
                            | "after_hero_power"
                            | "turn_start"
                            | "turn_end"
                    )
                }) {
                    mechanics.insert("trigger".to_owned());
                }
                if row
                    .effects
                    .iter()
                    .any(|effect| effect.trigger.as_ref() == "frenzy")
                {
                    mechanics.insert("frenzy".to_owned());
                }
                if row
                    .effects
                    .iter()
                    .any(|effect| effect.trigger.as_ref() == "spellburst")
                {
                    mechanics.insert("spellburst".to_owned());
                }
                mechanics
            }
            "play_resolution" | "location_activation" => HashSet::new(),
            _ => unreachable!("validated template trigger"),
        };
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
            let expected = normalized_text_sha256(&text.normalized);
            if expected != text.sha256.to_ascii_lowercase() {
                return Err(SolverError::schema(
                    format!("{text_path}.sha256"),
                    "does not match normalized text",
                ));
            }
            accepted.insert(expected);
        }
        for (effect_index, effect) in row.effects.iter().enumerate() {
            let effect_path = format!("{path}.effects[{effect_index}]");
            validate_generic_effect(effect, &effect_path)?;
            if !effect_trigger_matches_rule(&row.trigger, effect.trigger.as_ref()) {
                return Err(SolverError::schema(
                    format!("{effect_path}.trigger"),
                    "effect trigger does not match the card rule trigger",
                ));
            }
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
        rules.push(TemplateRule {
            rule_id: Arc::from(row.rule_id),
            card_type: row.card_type,
            accepted_text_sha256: accepted,
            effects: row.effects.into(),
            resolved_mechanics,
        });
    }
    Ok(TemplateRuleBundle {
        rules,
        by_card_id,
        source_run_id: raw.source.official_pool_run_id,
        source_card_defs_build: raw.source.card_defs_build,
        unique_official_cards: raw.counts.unique_official_cards,
        already_exact_cards: raw.counts.already_exact_cards,
        uncompiled_cards: raw.counts.uncompiled_cards,
    })
}

/// Return the validated embedded generic template bundle.
pub fn embedded_template_rule_bundle() -> Result<&'static TemplateRuleBundle, SolverError> {
    match EMBEDDED_BUNDLE
        .get_or_init(|| load_bundle(RULESET_JSON).map_err(|error| error.to_string()))
    {
        Ok(bundle) => Ok(bundle),
        Err(error) => Err(SolverError::Unsupported(format!(
            "generic card-text template bundle is unavailable: {error}"
        ))),
    }
}

impl TemplateRuleBundle {
    #[must_use]
    pub fn rule_count(&self) -> usize {
        self.rules.len()
    }

    #[must_use]
    pub fn registered_card_id_count(&self) -> usize {
        self.by_card_id.len()
    }

    #[must_use]
    pub const fn unique_official_cards(&self) -> usize {
        self.unique_official_cards
    }

    #[must_use]
    pub const fn already_exact_cards(&self) -> usize {
        self.already_exact_cards
    }

    #[must_use]
    pub const fn uncompiled_cards(&self) -> usize {
        self.uncompiled_cards
    }

    #[must_use]
    pub fn source_run_id(&self) -> &str {
        &self.source_run_id
    }

    #[must_use]
    pub fn source_card_defs_build(&self) -> &str {
        &self.source_card_defs_build
    }

    fn apply_card(&self, card: &mut Card, assessment: &mut TemplateRuleAssessment) {
        let Some(index) = self.by_card_id.get(card.card_id.as_ref()) else {
            return;
        };
        if card.effect_coverage == EffectCoverage::Exact {
            return;
        }
        let rule = &self.rules[*index];
        let actual_hash = normalized_text_sha256(&card.card_text);
        let mismatch = if card.card_type != rule.card_type {
            Some("card_type_mismatch")
        } else if card.card_text.trim().is_empty() {
            Some("english_text_missing")
        } else if !rule.accepted_text_sha256.contains(&actual_hash) {
            Some("english_text_sha256_mismatch")
        } else if !card.effects.is_empty() || !card.rule_id.trim().is_empty() {
            Some("prestructured_effect_conflict")
        } else {
            None
        };
        if let Some(reason) = mismatch {
            assessment.mismatches.push(TemplateRuleMismatch {
                entity_id: card.entity_id.to_string(),
                card_id: card.card_id.to_string(),
                rule_id: rule.rule_id.to_string(),
                reason: reason.to_owned(),
                actual_text_sha256: actual_hash,
            });
            return;
        }

        let retained = card
            .unsupported_effects
            .iter()
            .filter(|mechanic| {
                mechanic.as_ref() != "card_text_not_parsed"
                    && mechanic.as_ref() != "generated_card_effect_requires_hdt_refresh"
                    && !rule.resolved_mechanics.contains(mechanic.as_ref())
            })
            .cloned()
            .collect::<Vec<_>>();
        if !retained.is_empty() {
            assessment.mismatches.push(TemplateRuleMismatch {
                entity_id: card.entity_id.to_string(),
                card_id: card.card_id.to_string(),
                rule_id: rule.rule_id.to_string(),
                reason: "additional_unmodeled_mechanic".to_owned(),
                actual_text_sha256: actual_hash,
            });
            return;
        }

        card.effects = Arc::clone(&rule.effects);
        card.rule_id = Arc::clone(&rule.rule_id);
        card.rule_version = Arc::from(TEMPLATE_RULESET_ID);
        card.rule_text_sha256 = Arc::from(actual_hash.as_str());
        card.unsupported_effects = Arc::from([]);
        card.effect_coverage = EffectCoverage::Generic;
        assessment.matched.push(TemplateRuleMatch {
            entity_id: card.entity_id.to_string(),
            card_id: card.card_id.to_string(),
            rule_id: rule.rule_id.to_string(),
            text_sha256: actual_hash,
            effect_coverage: "generic",
        });
    }

    fn apply_player(&self, player: &mut PlayerState, assessment: &mut TemplateRuleAssessment) {
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

    /// Apply generic rules without changing exact cards or claiming exactness.
    pub fn apply(&self, state: &mut GameState) -> TemplateRuleAssessment {
        let mut assessment = TemplateRuleAssessment {
            ruleset_id: TEMPLATE_RULESET_ID.to_owned(),
            exact_claim_allowed: false,
            ..TemplateRuleAssessment::default()
        };
        self.apply_player(&mut state.friendly, &mut assessment);
        self.apply_player(&mut state.opponent, &mut assessment);
        assessment
    }
}

/// Apply the embedded generic card-text templates.
pub fn apply_embedded_template_rules(
    state: &mut GameState,
) -> Result<TemplateRuleAssessment, SolverError> {
    Ok(embedded_template_rule_bundle()?.apply(state))
}

/// Hydrate one generated/drawn card immediately when its CardID and official
/// English text match a generic template. This keeps same-turn search useful
/// without waiting for the next HDT snapshot, while preserving generic-only
/// coverage and the same hash gate as normal state hydration.
pub(crate) fn apply_embedded_template_rule_to_card(card: &mut Card) -> Result<bool, SolverError> {
    let mut assessment = TemplateRuleAssessment {
        ruleset_id: TEMPLATE_RULESET_ID.to_owned(),
        exact_claim_allowed: false,
        ..TemplateRuleAssessment::default()
    };
    embedded_template_rule_bundle()?.apply_card(card, &mut assessment);
    Ok(!assessment.matched.is_empty())
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    fn state_with_spell(text: &str, coverage: &str) -> GameState {
        serde_json::from_value(json!({
            "state_id": "template-state",
            "turn": 1,
            "active_player_id": "friendly",
            "perspective_player_id": "friendly",
            "friendly": {
                "player_id": "friendly",
                "hero": {"entity_id": "friendly-hero", "card_type": "HERO", "health": 30},
                "mana": 5,
                "max_mana": 5,
                "hand": [{
                    "entity_id": "pride",
                    "card_id": "BAR_534",
                    "name": "Pride's Fury",
                    "card_type": "SPELL",
                    "cost": 3,
                    "card_text": text,
                    "effect_coverage": coverage,
                    "unsupported_effects": ["card_text_not_parsed"]
                }]
            },
            "opponent": {
                "player_id": "opponent",
                "hero": {"entity_id": "opponent-hero", "card_type": "HERO", "health": 30}
            }
        }))
        .expect("valid game state")
    }

    #[test]
    fn embedded_bundle_is_generic_only_and_complete() {
        let bundle = embedded_template_rule_bundle().expect("template bundle");
        assert_eq!(bundle.unique_official_cards(), 1811);
        assert_eq!(bundle.rule_count(), 157);
        assert_eq!(bundle.registered_card_id_count(), 157);
        assert_eq!(
            bundle.rule_count() + bundle.already_exact_cards() + bundle.uncompiled_cards(),
            1811
        );
    }

    #[test]
    fn full_hash_match_applies_generic_effects() {
        let mut state = state_with_spell("Give your minions +1/+3.", "unsupported");
        let assessment = apply_embedded_template_rules(&mut state).expect("apply templates");
        assert_eq!(assessment.matched.len(), 1);
        assert!(!assessment.exact_claim_allowed);
        let card = &state.friendly.hand[0];
        assert_eq!(card.effect_coverage, EffectCoverage::Generic);
        assert_eq!(card.rule_version.as_ref(), TEMPLATE_RULESET_ID);
        assert!(card.unsupported_effects.is_empty());
        assert_eq!(card.effects.len(), 2);
        assert_eq!(card.effects[0].kind.as_ref(), "buff_attack");
        assert_eq!(card.effects[1].kind.as_ref(), "buff_health");
    }

    #[test]
    fn changed_text_fails_closed() {
        let mut state = state_with_spell("Give your minions +2/+3.", "unsupported");
        let assessment = apply_embedded_template_rules(&mut state).expect("apply templates");
        assert!(assessment.matched.is_empty());
        assert_eq!(
            assessment.mismatches[0].reason,
            "english_text_sha256_mismatch"
        );
        assert_eq!(
            state.friendly.hand[0].effect_coverage,
            EffectCoverage::Unsupported
        );
    }

    #[test]
    fn exact_card_is_never_overridden() {
        let mut state = state_with_spell("Give your minions +1/+3.", "exact");
        state.friendly.hand[0].unsupported_effects = Arc::from([]);
        let assessment = apply_embedded_template_rules(&mut state).expect("apply templates");
        assert!(assessment.matched.is_empty());
        assert!(assessment.mismatches.is_empty());
        assert_eq!(
            state.friendly.hand[0].effect_coverage,
            EffectCoverage::Exact
        );
        assert!(state.friendly.hand[0].effects.is_empty());
    }

    #[test]
    fn draw_template_is_generic_and_counted() {
        let mut state = state_with_spell("Draw 2 cards.", "unsupported");
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("CORE_CS2_023");
        card.name = Arc::from("Arcane Intellect");
        let assessment = apply_embedded_template_rules(&mut state).expect("apply draw template");
        assert_eq!(assessment.matched.len(), 1);
        let effect = &state.friendly.hand[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "draw");
        assert_eq!(effect.count, 2);
        assert_eq!(
            state.friendly.hand[0].effect_coverage,
            EffectCoverage::Generic
        );
    }

    #[test]
    fn random_target_template_remains_a_chance_effect() {
        let mut state = state_with_spell(
            "Freeze a random enemy minion. (Upgrades when you have 5 Mana.)",
            "unsupported",
        );
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("BAR_305");
        card.name = Arc::from("Flurry (Rank 1)");
        let assessment =
            apply_embedded_template_rules(&mut state).expect("apply random target template");
        assert_eq!(assessment.matched.len(), 1);
        let effect = &state.friendly.hand[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "freeze");
        assert_eq!(effect.target.as_ref(), "enemy_minion");
        assert!(effect.random);
        assert!(effect.pool.is_none());
    }

    #[test]
    fn exact_cost_series_draw_keeps_three_distinct_deck_queries() {
        let mut state =
            state_with_spell("Battlecry: Draw a 1, 2, and 3-Cost spell.", "unsupported");
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("BAR_551");
        card.name = Arc::from("Barak Kodobane");
        card.card_type = CardType::Minion;
        card.attack = 3;
        card.health = 5;
        card.current_health = 5;
        card.unsupported_effects =
            Arc::from([Arc::from("card_text_not_parsed"), Arc::from("battlecry")]);
        let assessment =
            apply_embedded_template_rules(&mut state).expect("apply cost-series draw template");
        assert_eq!(assessment.matched.len(), 1);
        let effects = &state.friendly.hand[0].effects;
        assert_eq!(effects.len(), 3);
        assert_eq!(
            effects
                .iter()
                .map(|effect| {
                    let pool = effect.pool.as_ref().expect("owner deck pool");
                    (pool.cost_min, pool.cost_max)
                })
                .collect::<Vec<_>>(),
            vec![(Some(1), Some(1)), (Some(2), Some(2)), (Some(3), Some(3))]
        );
        assert!(effects.iter().all(|effect| effect.random));
    }

    #[test]
    fn carddefs_bound_summon_keeps_token_identity_and_keywords() {
        let mut state = state_with_spell("Summon two 1/2 Turtles with Taunt .", "unsupported");
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("BAR_533");
        card.name = Arc::from("Thorngrowth Sentries");
        let assessment = apply_embedded_template_rules(&mut state).expect("apply summon template");
        assert_eq!(assessment.matched.len(), 1);
        let effect = &state.friendly.hand[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "summon");
        assert_eq!(effect.card_id.as_ref(), "BAR_533t");
        assert_eq!(effect.count, 2);
        assert!(effect.taunt);
    }

    #[test]
    fn deathrattle_template_resolves_only_its_reviewed_mechanic() {
        let mut state = state_with_spell("Deathrattle: Draw a card.", "unsupported");
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("CORE_EX1_096");
        card.name = Arc::from("Loot Hoarder");
        card.card_type = CardType::Minion;
        card.attack = 2;
        card.health = 1;
        card.current_health = 1;
        card.unsupported_effects =
            Arc::from([Arc::from("card_text_not_parsed"), Arc::from("deathrattle")]);
        let assessment = apply_embedded_template_rules(&mut state).expect("apply Deathrattle");
        assert_eq!(assessment.matched.len(), 1);
        assert!(state.friendly.hand[0].unsupported_effects.is_empty());
        let effect = &state.friendly.hand[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "draw");
        assert_eq!(effect.trigger.as_ref(), "deathrattle");
    }

    #[test]
    fn random_filtered_deathrattle_is_embedded_as_an_owner_deck_chance() {
        let mut state = state_with_spell(
            "Deathrattle: Draw a minion that costs (7) or more.",
            "unsupported",
        );
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("EDR_485");
        card.name = Arc::from("Rotheart Dryad");
        card.card_type = CardType::Minion;
        card.attack = 3;
        card.health = 4;
        card.current_health = 4;
        card.unsupported_effects =
            Arc::from([Arc::from("card_text_not_parsed"), Arc::from("deathrattle")]);
        let assessment =
            apply_embedded_template_rules(&mut state).expect("apply random Deathrattle");
        assert_eq!(assessment.matched.len(), 1);
        let effect = &state.friendly.hand[0].effects[0];
        assert_eq!(effect.kind.as_ref(), "draw_from_pool");
        assert_eq!(effect.trigger.as_ref(), "deathrattle");
        assert!(effect.random);
        let pool = effect.pool.as_ref().expect("owner-deck pool");
        assert_eq!(pool.source, CardPoolSource::OwnerDeck);
        assert_eq!(pool.cost_min, Some(7));
        assert_eq!(pool.card_types.as_slice(), &[CardType::Minion]);
    }

    #[test]
    fn random_turn_start_card_remains_outside_the_embedded_bundle() {
        let mut state =
            state_with_spell("At the start of your turn, draw a Murloc.", "unsupported");
        let card = &mut state.friendly.hand[0];
        card.card_id = Arc::from("BAR_043");
        card.name = Arc::from("Tinyfin's Caravan");
        card.card_type = CardType::Minion;
        card.attack = 1;
        card.health = 3;
        card.current_health = 3;
        let assessment =
            apply_embedded_template_rules(&mut state).expect("assess random turn-start card");
        assert!(assessment.matched.is_empty());
        assert!(assessment.mismatches.is_empty());
        assert_eq!(
            state.friendly.hand[0].effect_coverage,
            EffectCoverage::Unsupported
        );
        assert!(state.friendly.hand[0].effects.is_empty());
    }
}
