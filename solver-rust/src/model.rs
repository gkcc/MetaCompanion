use std::collections::{BTreeMap, HashSet};
use std::fmt;
use std::sync::Arc;

use serde::{Deserialize, Deserializer, Serialize};

use crate::API_VERSION;
use crate::error::SolverError;
use crate::hdt_root::HdtRootCandidateSet;

pub type SharedStr = Arc<str>;

fn shared(value: impl AsRef<str>) -> SharedStr {
    Arc::<str>::from(value.as_ref())
}

fn api_version() -> SharedStr {
    shared(API_VERSION)
}

fn unknown_card_id() -> SharedStr {
    shared("UNKNOWN")
}

fn unknown_card_name() -> SharedStr {
    shared("Unknown card")
}

fn generated_minion_name() -> SharedStr {
    shared("Generated minion")
}

fn one() -> u16 {
    1
}

fn one_f64() -> f64 {
    1.0
}

fn shared_str_is_empty(value: &SharedStr) -> bool {
    value.is_empty()
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum JsonScalar {
    String(SharedStr),
    Integer(i64),
    Float(f64),
    Bool(bool),
    Null,
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum CardType {
    Hero,
    Minion,
    Spell,
    Weapon,
    HeroPower,
    Location,
    Unknown,
}

impl CardType {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Hero => "HERO",
            Self::Minion => "MINION",
            Self::Spell => "SPELL",
            Self::Weapon => "WEAPON",
            Self::HeroPower => "HERO_POWER",
            Self::Location => "LOCATION",
            Self::Unknown => "UNKNOWN",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ActionKind {
    PlayCard,
    Attack,
    HeroPower,
    LocationActivate,
    EndTurn,
}

impl ActionKind {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::PlayCard => "play_card",
            Self::Attack => "attack",
            Self::HeroPower => "hero_power",
            Self::LocationActivate => "location_activate",
            Self::EndTurn => "end_turn",
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum EffectCoverage {
    Exact,
    Generic,
    Unsupported,
}

impl EffectCoverage {
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Exact => "exact",
            Self::Generic => "generic",
            Self::Unsupported => "unsupported",
        }
    }
}

/// The public source used to construct a generated-card candidate set.
///
/// Only `current_format` is resolved from the official Blizzard snapshot. The
/// other variants are deliberately explicit so a deck/hand/history pool can
/// never be silently approximated by Standard or Arena cards.
#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CardPoolSource {
    #[default]
    CurrentFormat,
    OwnerDeck,
    OpponentDeck,
    OwnerHand,
    OpponentHand,
    Graveyard,
    Historical,
    Entourage,
}

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CardPoolClassMode {
    #[default]
    Any,
    Controller,
    ControllerOrNeutral,
    AnotherClass,
    Specific,
}

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PoolSelection {
    #[default]
    None,
    UniformRandom,
    Discover,
}

impl PoolSelection {
    fn is_none(&self) -> bool {
        *self == Self::None
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PoolDestination {
    #[default]
    None,
    Hand,
    Battlefield,
    Deck,
    Cast,
}

impl PoolDestination {
    fn is_none(&self) -> bool {
        *self == Self::None
    }
}

/// Declarative constraints for one generated-card pool.
#[derive(Clone, Debug, Eq, Hash, PartialEq, Serialize, Deserialize)]
pub struct CardPoolQuery {
    #[serde(default)]
    pub source: CardPoolSource,
    #[serde(default = "default_true", skip_serializing_if = "is_true")]
    pub collectible: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost_min: Option<u16>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cost_max: Option<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub card_types: Vec<CardType>,
    #[serde(default)]
    pub class_mode: CardPoolClassMode,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub class_ids: Vec<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub spell_school_ids: Vec<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub minion_type_ids: Vec<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub card_set_ids: Vec<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub rarity_ids: Vec<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub keyword_ids: Vec<u16>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub required_keywords: Vec<SharedStr>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub exclude_self: bool,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub exclude_card_ids: Vec<SharedStr>,
}

impl Default for CardPoolQuery {
    fn default() -> Self {
        Self {
            source: CardPoolSource::CurrentFormat,
            collectible: true,
            cost_min: None,
            cost_max: None,
            card_types: Vec::new(),
            class_mode: CardPoolClassMode::Any,
            class_ids: Vec::new(),
            spell_school_ids: Vec::new(),
            minion_type_ids: Vec::new(),
            card_set_ids: Vec::new(),
            rarity_ids: Vec::new(),
            keyword_ids: Vec::new(),
            required_keywords: Vec::new(),
            exclude_self: false,
            exclude_card_ids: Vec::new(),
        }
    }
}

impl CardPoolQuery {
    fn validate(&self, path: &str) -> Result<(), SolverError> {
        if self
            .cost_min
            .is_some_and(|minimum| self.cost_max.is_some_and(|maximum| minimum > maximum))
        {
            return Err(SolverError::schema(
                format!("{path}.cost_min"),
                "must not exceed cost_max",
            ));
        }
        if self.class_mode == CardPoolClassMode::Specific && self.class_ids.is_empty() {
            return Err(SolverError::schema(
                format!("{path}.class_ids"),
                "must not be empty when class_mode is specific",
            ));
        }
        if self.class_mode != CardPoolClassMode::Specific && !self.class_ids.is_empty() {
            return Err(SolverError::schema(
                format!("{path}.class_ids"),
                "is only valid when class_mode is specific",
            ));
        }
        if self
            .required_keywords
            .iter()
            .chain(self.exclude_card_ids.iter())
            .any(|value| value.trim().is_empty())
        {
            return Err(SolverError::schema(
                path,
                "contains an empty string constraint",
            ));
        }
        Ok(())
    }
}

/// Immutable public metadata for one candidate returned by a resolved pool.
#[derive(Clone, Debug, PartialEq)]
pub struct ResolvedPoolCard {
    pub card_id: SharedStr,
    pub dbf_id: u64,
    pub name: SharedStr,
    pub card_type: CardType,
    pub cost: u16,
    pub attack: u16,
    pub health: u16,
    pub durability: u16,
    pub rarity_id: u16,
    pub keywords: Arc<[SharedStr]>,
    pub text: SharedStr,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ResolvedPoolCandidate {
    pub card: ResolvedPoolCard,
    /// Number of original pool members represented by this branch.
    pub weight: u32,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Effect {
    pub kind: SharedStr,
    #[serde(
        default = "default_effect_trigger",
        skip_serializing_if = "is_resolution_trigger"
    )]
    pub trigger: SharedStr,
    #[serde(default)]
    pub amount: i32,
    #[serde(default = "default_target")]
    pub target: SharedStr,
    #[serde(default = "one")]
    pub count: u16,
    #[serde(default)]
    pub card_id: SharedStr,
    #[serde(default = "generated_minion_name")]
    pub name: SharedStr,
    #[serde(default)]
    pub attack: u16,
    #[serde(default = "one")]
    pub health: u16,
    #[serde(default)]
    pub durability: u16,
    #[serde(default)]
    pub random: bool,
    #[serde(default, skip_serializing_if = "PoolSelection::is_none")]
    pub pool_selection: PoolSelection,
    #[serde(default, skip_serializing_if = "PoolDestination::is_none")]
    pub pool_destination: PoolDestination,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pool: Option<CardPoolQuery>,
    #[serde(default = "three", skip_serializing_if = "is_three")]
    pub offer_count: u16,
    #[serde(default = "default_true", skip_serializing_if = "is_true")]
    pub with_replacement: bool,
    #[serde(default, skip_serializing_if = "is_zero_i16")]
    pub created_card_cost_delta: i16,
    /// Runtime-only, hash-bound candidates resolved from the current snapshot.
    #[serde(skip)]
    pub resolved_pool: Arc<[ResolvedPoolCandidate]>,
    #[serde(skip)]
    pub resolved_pool_population: u32,
    #[serde(skip)]
    pub resolved_pool_exact: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub rush: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub taunt: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub divine_shield: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub stealth: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub poisonous: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub lifesteal: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub windfury: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub charge: bool,
    #[serde(default, skip_serializing_if = "is_false")]
    pub reborn: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hand_count_at_most: Option<u16>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub summoned_card_effects_unmodeled: bool,
}

fn default_target() -> SharedStr {
    shared("none")
}

fn default_effect_trigger() -> SharedStr {
    shared("resolution")
}

fn is_resolution_trigger(value: &SharedStr) -> bool {
    value.as_ref() == "resolution"
}

fn three() -> u16 {
    3
}

fn is_three(value: &u16) -> bool {
    *value == 3
}

fn is_true(value: &bool) -> bool {
    *value
}

fn is_zero_i16(value: &i16) -> bool {
    *value == 0
}

impl Effect {
    #[must_use]
    pub const fn has_summoned_minion_keywords(&self) -> bool {
        self.rush
            || self.taunt
            || self.divine_shield
            || self.stealth
            || self.poisonous
            || self.lifesteal
            || self.windfury
            || self.charge
            || self.reborn
    }

    pub fn validate(&self, path: &str) -> Result<(), SolverError> {
        if self.kind.trim().is_empty() {
            return Err(SolverError::schema(
                format!("{path}.kind"),
                "must be a non-empty string",
            ));
        }
        const TRIGGERS: &[&str] = &[
            "resolution",
            "deathrattle",
            "after_spell_cast",
            "spellburst",
            "frenzy",
            "after_hero_attack",
            "after_hero_power",
            "turn_start",
            "turn_end",
        ];
        if !TRIGGERS.contains(&self.trigger.as_ref()) {
            return Err(SolverError::schema(
                format!("{path}.trigger"),
                format!("unsupported effect trigger {:?}", self.trigger),
            ));
        }
        const MODES: &[&str] = &[
            "none",
            "self",
            "enemy_character",
            "friendly_character",
            "any_character",
            "enemy_minion",
            "friendly_minion",
            "any_minion",
            "any_undamaged_minion",
            "damaged_enemy_minion",
            "enemy_hero",
            "friendly_hero",
            "all_enemy_characters",
            "all_friendly_characters",
            "all_enemy_minions",
            "all_friendly_minions",
            "all_minions",
            "all_characters",
            "all_other_minions",
            "all_other_friendly_minions",
        ];
        if !MODES.contains(&self.target.as_ref()) {
            return Err(SolverError::schema(
                format!("{path}.target"),
                format!("unsupported target mode {:?}", self.target),
            ));
        }
        if self.count == 0 {
            return Err(SolverError::schema(
                format!("{path}.count"),
                "must be at least 1",
            ));
        }
        if self.health == 0 {
            return Err(SolverError::schema(
                format!("{path}.health"),
                "must be at least 1",
            ));
        }
        let has_pool = self.pool.is_some();
        if has_pool != (self.pool_selection != PoolSelection::None)
            || has_pool != (self.pool_destination != PoolDestination::None)
        {
            return Err(SolverError::schema(
                path,
                "pool, pool_selection, and pool_destination must be declared together",
            ));
        }
        if let Some(pool) = &self.pool {
            pool.validate(&format!("{path}.pool"))?;
            if !self.random {
                return Err(SolverError::schema(
                    format!("{path}.random"),
                    "pool resolution must be marked random",
                ));
            }
            if self.offer_count == 0 {
                return Err(SolverError::schema(
                    format!("{path}.offer_count"),
                    "must be at least 1",
                ));
            }
            if self.pool_selection == PoolSelection::Discover
                && (self.offer_count < 2 || self.with_replacement)
            {
                return Err(SolverError::schema(
                    path,
                    "Discover requires at least two offers sampled without replacement",
                ));
            }
            let expected_kind = match self.pool_destination {
                PoolDestination::Hand => matches!(
                    self.kind.as_ref(),
                    "generate_from_pool" | "discover_from_pool" | "draw_from_pool"
                ),
                PoolDestination::Battlefield => self.kind.as_ref() == "summon_from_pool",
                PoolDestination::Deck => self.kind.as_ref() == "shuffle_from_pool",
                PoolDestination::Cast => self.kind.as_ref() == "cast_from_pool",
                PoolDestination::None => false,
            };
            if !expected_kind {
                return Err(SolverError::schema(
                    format!("{path}.kind"),
                    "does not match pool_destination",
                ));
            }
        } else if self.created_card_cost_delta != 0
            || self.offer_count != 3
            || !self.with_replacement
        {
            return Err(SolverError::schema(path, "pool-only fields require a pool"));
        }
        Ok(())
    }
}

#[derive(Deserialize)]
#[serde(untagged)]
enum EntityIdInput {
    String(String),
    Unsigned(u64),
    Signed(i64),
}

impl EntityIdInput {
    fn into_shared(self, path: &str, allow_empty: bool) -> Result<SharedStr, SolverError> {
        let value = match self {
            Self::String(value) => value.trim().to_owned(),
            Self::Unsigned(value) => value.to_string(),
            Self::Signed(value) => value.to_string(),
        };
        if !allow_empty && value.is_empty() {
            return Err(SolverError::schema(path, "must be a non-empty string"));
        }
        Ok(shared(value))
    }
}

fn deserialize_required_entity_id<'de, D>(deserializer: D) -> Result<SharedStr, D::Error>
where
    D: Deserializer<'de>,
{
    let input = EntityIdInput::deserialize(deserializer)?;
    input
        .into_shared("entity_id", false)
        .map_err(serde::de::Error::custom)
}

#[derive(Deserialize)]
struct RawCard {
    #[serde(deserialize_with = "deserialize_required_entity_id")]
    entity_id: SharedStr,
    #[serde(default = "unknown_card_id")]
    card_id: SharedStr,
    #[serde(default = "unknown_card_name")]
    name: SharedStr,
    #[serde(default = "unknown_card_type")]
    card_type: CardType,
    #[serde(default)]
    cost: u16,
    #[serde(default)]
    attack: u16,
    #[serde(default)]
    health: u16,
    current_health: Option<u16>,
    #[serde(default)]
    current_health_known: Option<bool>,
    #[serde(default = "default_true")]
    playable: bool,
    #[serde(default)]
    can_attack: bool,
    attacks_remaining: Option<u8>,
    #[serde(default)]
    attacks_remaining_known: Option<bool>,
    #[serde(default)]
    taunt: bool,
    #[serde(default)]
    divine_shield: bool,
    #[serde(default)]
    frozen: bool,
    #[serde(default)]
    stealth: bool,
    #[serde(default)]
    poisonous: bool,
    #[serde(default)]
    lifesteal: bool,
    #[serde(default)]
    windfury: bool,
    #[serde(default)]
    mega_windfury: bool,
    #[serde(default)]
    rush: bool,
    #[serde(default)]
    charge: bool,
    #[serde(default)]
    reborn: bool,
    #[serde(default)]
    dormant: bool,
    #[serde(default)]
    immune: bool,
    #[serde(default)]
    summoned_this_turn: bool,
    #[serde(default)]
    durability: u16,
    current_durability: Option<u16>,
    #[serde(default)]
    effects: Vec<Effect>,
    effect_coverage: Option<EffectCoverage>,
    #[serde(default)]
    unsupported_effects: Vec<SharedStr>,
    #[serde(default = "one_f64")]
    prior_weight: f64,
    #[serde(default)]
    tags: BTreeMap<SharedStr, JsonScalar>,
    #[serde(default)]
    card_text: SharedStr,
    #[serde(default)]
    english_text: SharedStr,
    #[serde(default)]
    text: SharedStr,
    #[serde(default)]
    rule_id: SharedStr,
    #[serde(default)]
    rule_version: SharedStr,
    #[serde(default)]
    rule_text_sha256: SharedStr,
    #[serde(default)]
    visibility: SharedStr,
}

const fn unknown_card_type() -> CardType {
    CardType::Unknown
}

const fn default_true() -> bool {
    true
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct Card {
    pub entity_id: SharedStr,
    pub card_id: SharedStr,
    pub name: SharedStr,
    pub card_type: CardType,
    pub cost: u16,
    pub attack: u16,
    pub health: u16,
    pub current_health: u16,
    pub current_health_known: bool,
    pub playable: bool,
    pub can_attack: bool,
    pub attacks_remaining: u8,
    #[serde(skip_serializing)]
    pub attacks_remaining_known: bool,
    pub taunt: bool,
    pub divine_shield: bool,
    pub frozen: bool,
    pub stealth: bool,
    pub poisonous: bool,
    pub lifesteal: bool,
    pub windfury: bool,
    pub mega_windfury: bool,
    pub rush: bool,
    pub charge: bool,
    pub reborn: bool,
    pub dormant: bool,
    pub immune: bool,
    pub summoned_this_turn: bool,
    pub durability: u16,
    pub current_durability: u16,
    pub effects: Arc<[Effect]>,
    pub effect_coverage: EffectCoverage,
    pub unsupported_effects: Arc<[SharedStr]>,
    pub prior_weight: f64,
    pub tags: Arc<BTreeMap<SharedStr, JsonScalar>>,
    pub card_text: SharedStr,
    pub rule_id: SharedStr,
    pub rule_version: SharedStr,
    pub rule_text_sha256: SharedStr,
    #[serde(skip_serializing_if = "shared_str_is_empty")]
    pub visibility: SharedStr,
}

impl<'de> Deserialize<'de> for Card {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let raw = RawCard::deserialize(deserializer)?;
        if raw.card_id.trim().is_empty() {
            return Err(serde::de::Error::custom(
                "card_id must be a non-empty string",
            ));
        }
        if raw.name.trim().is_empty() {
            return Err(serde::de::Error::custom("name must be a non-empty string"));
        }
        if !raw.prior_weight.is_finite() || raw.prior_weight < 0.0 {
            return Err(serde::de::Error::custom(
                "prior_weight must be a finite non-negative number",
            ));
        }
        for (index, effect) in raw.effects.iter().enumerate() {
            effect
                .validate(&format!("effects[{index}]"))
                .map_err(serde::de::Error::custom)?;
        }
        if raw
            .unsupported_effects
            .iter()
            .any(|item| item.trim().is_empty())
        {
            return Err(serde::de::Error::custom(
                "unsupported_effects entries must be non-empty strings",
            ));
        }
        let default_coverage = if matches!(
            raw.card_type,
            CardType::Spell | CardType::Weapon | CardType::HeroPower | CardType::Location
        ) && raw.effects.is_empty()
        {
            EffectCoverage::Unsupported
        } else {
            EffectCoverage::Generic
        };
        let card_text = if !raw.english_text.is_empty() {
            raw.english_text
        } else if !raw.card_text.is_empty() {
            raw.card_text
        } else {
            raw.text
        };
        let hidden = raw.visibility.trim().eq_ignore_ascii_case("hidden");
        let attacks_remaining_known = raw.attacks_remaining_known.unwrap_or_else(|| {
            raw.attacks_remaining.is_some() || !(raw.windfury || raw.mega_windfury)
        });
        let current_health_known = raw
            .current_health_known
            .unwrap_or_else(|| raw.current_health.is_some());
        Ok(Self {
            entity_id: raw.entity_id,
            card_id: raw.card_id,
            name: raw.name,
            card_type: raw.card_type,
            cost: raw.cost,
            attack: raw.attack,
            health: raw.health,
            current_health: raw.current_health.unwrap_or(raw.health),
            current_health_known,
            playable: raw.playable && !hidden,
            can_attack: raw.can_attack,
            attacks_remaining: raw.attacks_remaining.unwrap_or(u8::from(raw.can_attack)),
            attacks_remaining_known,
            taunt: raw.taunt,
            divine_shield: raw.divine_shield,
            frozen: raw.frozen,
            stealth: raw.stealth,
            poisonous: raw.poisonous,
            lifesteal: raw.lifesteal,
            windfury: raw.windfury,
            mega_windfury: raw.mega_windfury,
            rush: raw.rush,
            charge: raw.charge,
            reborn: raw.reborn,
            dormant: raw.dormant,
            immune: raw.immune,
            summoned_this_turn: raw.summoned_this_turn,
            durability: raw.durability,
            current_durability: raw.current_durability.unwrap_or(raw.durability),
            effects: raw.effects.into(),
            effect_coverage: raw.effect_coverage.unwrap_or(default_coverage),
            unsupported_effects: raw.unsupported_effects.into(),
            prior_weight: raw.prior_weight,
            tags: Arc::new(raw.tags),
            card_text,
            rule_id: raw.rule_id,
            rule_version: raw.rule_version,
            rule_text_sha256: raw.rule_text_sha256,
            visibility: raw.visibility,
        })
    }
}

impl Card {
    pub(crate) fn transform_into_token(&mut self, effect: &Effect) {
        let previous_can_attack = self.can_attack;
        let previous_attacks_remaining = self.attacks_remaining;
        let previous_attacks_remaining_known = self.attacks_remaining_known;
        let summoned_this_turn = self.summoned_this_turn;
        let health = effect.health.max(1);
        self.card_id = Arc::clone(&effect.card_id);
        self.name = Arc::clone(&effect.name);
        self.card_type = CardType::Minion;
        self.attack = effect.attack;
        self.health = health;
        self.current_health = health;
        self.current_health_known = true;
        self.playable = false;
        self.can_attack = previous_can_attack && effect.attack > 0;
        self.attacks_remaining = if effect.attack > 0 {
            previous_attacks_remaining
        } else {
            0
        };
        self.attacks_remaining_known = previous_attacks_remaining_known;
        self.taunt = effect.taunt;
        self.divine_shield = effect.divine_shield;
        self.frozen = false;
        self.stealth = effect.stealth;
        self.poisonous = effect.poisonous;
        self.lifesteal = effect.lifesteal;
        self.windfury = effect.windfury;
        self.mega_windfury = false;
        self.rush = effect.rush;
        self.charge = effect.charge;
        self.reborn = effect.reborn;
        self.dormant = false;
        self.immune = false;
        self.summoned_this_turn = summoned_this_turn;
        self.durability = 0;
        self.current_durability = 0;
        self.effects = Vec::<Effect>::new().into();
        self.effect_coverage = EffectCoverage::Exact;
        self.unsupported_effects = Vec::<SharedStr>::new().into();
        self.tags = Arc::new(BTreeMap::new());
        self.card_text = shared("");
        self.rule_id = shared("");
        self.rule_version = shared("");
        self.rule_text_sha256 = shared("");
    }

    pub(crate) fn generated_weapon(entity_id: impl AsRef<str>, effect: &Effect) -> Self {
        Self {
            entity_id: shared(entity_id),
            card_id: Arc::clone(&effect.card_id),
            name: Arc::clone(&effect.name),
            card_type: CardType::Weapon,
            cost: 0,
            attack: effect.attack,
            health: 0,
            current_health: 0,
            current_health_known: true,
            playable: false,
            can_attack: false,
            attacks_remaining: 0,
            attacks_remaining_known: true,
            taunt: false,
            divine_shield: false,
            frozen: false,
            stealth: effect.stealth,
            poisonous: effect.poisonous,
            lifesteal: effect.lifesteal,
            windfury: effect.windfury,
            mega_windfury: false,
            rush: false,
            charge: false,
            reborn: false,
            dormant: false,
            immune: false,
            summoned_this_turn: false,
            durability: effect.durability,
            current_durability: effect.durability,
            effects: Vec::<Effect>::new().into(),
            effect_coverage: EffectCoverage::Exact,
            unsupported_effects: Vec::<SharedStr>::new().into(),
            prior_weight: 1.0,
            tags: Arc::new(BTreeMap::new()),
            card_text: shared(""),
            rule_id: shared(""),
            rule_version: shared(""),
            rule_text_sha256: shared(""),
            visibility: shared(""),
        }
    }

    pub(crate) fn unknown_drawn_card(entity_id: impl AsRef<str>) -> Self {
        Self {
            entity_id: shared(entity_id),
            card_id: shared("UNKNOWN_DRAW"),
            name: shared("Unknown drawn card"),
            card_type: CardType::Unknown,
            cost: 99,
            attack: 0,
            health: 0,
            current_health: 0,
            current_health_known: false,
            playable: false,
            can_attack: false,
            attacks_remaining: 0,
            attacks_remaining_known: true,
            taunt: false,
            divine_shield: false,
            frozen: false,
            stealth: false,
            poisonous: false,
            lifesteal: false,
            windfury: false,
            mega_windfury: false,
            rush: false,
            charge: false,
            reborn: false,
            dormant: false,
            immune: false,
            summoned_this_turn: false,
            durability: 0,
            current_durability: 0,
            effects: Vec::<Effect>::new().into(),
            effect_coverage: EffectCoverage::Unsupported,
            unsupported_effects: vec![shared("hidden_draw_identity")].into(),
            prior_weight: 1.0,
            tags: Arc::new(BTreeMap::new()),
            card_text: shared(""),
            rule_id: shared(""),
            rule_version: shared(""),
            rule_text_sha256: shared(""),
            visibility: shared("private_owner"),
        }
    }

    pub(crate) fn drawn_from_known_deck(entity_id: impl AsRef<str>, known: &KnownDeckCard) -> Self {
        let card_type = known.card_type;
        let health = if card_type == CardType::Minion { 1 } else { 0 };
        let identity_unknown = card_type == CardType::Unknown;
        let spell_effect_unknown = matches!(
            card_type,
            CardType::Spell | CardType::Weapon | CardType::HeroPower | CardType::Location
        );
        Self {
            entity_id: shared(entity_id),
            card_id: Arc::clone(&known.card_id),
            name: if known.name.is_empty() {
                Arc::clone(&known.card_id)
            } else {
                Arc::clone(&known.name)
            },
            card_type,
            cost: known.cost,
            attack: 0,
            health,
            current_health: health,
            current_health_known: true,
            playable: !identity_unknown,
            can_attack: false,
            attacks_remaining: 0,
            attacks_remaining_known: true,
            taunt: false,
            divine_shield: false,
            frozen: false,
            stealth: false,
            poisonous: false,
            lifesteal: false,
            windfury: false,
            mega_windfury: false,
            rush: false,
            charge: false,
            reborn: false,
            dormant: false,
            immune: false,
            summoned_this_turn: false,
            durability: 0,
            current_durability: 0,
            effects: Vec::<Effect>::new().into(),
            effect_coverage: if spell_effect_unknown || identity_unknown {
                EffectCoverage::Unsupported
            } else {
                EffectCoverage::Generic
            },
            unsupported_effects: if spell_effect_unknown || identity_unknown {
                vec![shared("drawn_card_effect_not_resolved")].into()
            } else {
                Vec::<SharedStr>::new().into()
            },
            prior_weight: 1.0,
            tags: Arc::new(BTreeMap::new()),
            card_text: shared(""),
            rule_id: shared(""),
            rule_version: shared(""),
            rule_text_sha256: shared(""),
            visibility: shared("private_owner"),
        }
    }

    pub(crate) fn generated_from_pool(
        entity_id: impl AsRef<str>,
        definition: &ResolvedPoolCard,
        created_card_cost_delta: i16,
        summoned: bool,
    ) -> Self {
        let has_keyword = |name: &str| {
            definition
                .keywords
                .iter()
                .any(|value| value.eq_ignore_ascii_case(name))
        };
        let adjusted_cost = i32::from(definition.cost)
            .saturating_add(i32::from(created_card_cost_delta))
            .clamp(0, i32::from(u16::MAX)) as u16;
        let health = if definition.card_type == CardType::Minion {
            definition.health.max(1)
        } else {
            definition.health
        };
        let rush = has_keyword("rush");
        let charge = has_keyword("charge");
        let can_attack = summoned
            && definition.card_type == CardType::Minion
            && definition.attack > 0
            && (rush || charge);
        let has_unmodeled_text = !definition.text.trim().is_empty();
        let requires_effect_model = has_unmodeled_text
            || matches!(
                definition.card_type,
                CardType::Spell
                    | CardType::Weapon
                    | CardType::Hero
                    | CardType::HeroPower
                    | CardType::Location
                    | CardType::Unknown
            );
        let keyword_bonus = definition.keywords.len().min(6) as f64 * 0.08;
        let body_ratio = if definition.card_type == CardType::Minion {
            f64::from(definition.attack.saturating_add(health))
                / f64::from(definition.cost.saturating_mul(2).saturating_add(2))
        } else {
            f64::from(definition.cost.min(10)) * 0.08
        };
        let prior_weight = (0.75 + body_ratio + keyword_bonus).clamp(0.5, 3.5);
        Self {
            entity_id: shared(entity_id),
            card_id: Arc::clone(&definition.card_id),
            name: Arc::clone(&definition.name),
            card_type: definition.card_type,
            cost: adjusted_cost,
            attack: definition.attack,
            health,
            current_health: health,
            current_health_known: true,
            playable: !summoned,
            can_attack,
            attacks_remaining: u8::from(can_attack),
            attacks_remaining_known: true,
            taunt: has_keyword("taunt"),
            divine_shield: has_keyword("divine_shield"),
            frozen: false,
            stealth: has_keyword("stealth"),
            poisonous: has_keyword("poisonous"),
            lifesteal: has_keyword("lifesteal"),
            windfury: has_keyword("windfury"),
            mega_windfury: has_keyword("mega_windfury"),
            rush,
            charge,
            reborn: has_keyword("reborn"),
            dormant: has_keyword("dormant"),
            immune: has_keyword("immune"),
            summoned_this_turn: summoned && definition.card_type == CardType::Minion,
            durability: definition.durability,
            current_durability: definition.durability,
            effects: Vec::<Effect>::new().into(),
            effect_coverage: if requires_effect_model {
                EffectCoverage::Unsupported
            } else {
                EffectCoverage::Generic
            },
            unsupported_effects: if requires_effect_model {
                vec![shared("generated_card_effect_requires_hdt_refresh")].into()
            } else {
                Vec::<SharedStr>::new().into()
            },
            prior_weight,
            tags: Arc::new(BTreeMap::new()),
            card_text: Arc::clone(&definition.text),
            rule_id: shared(""),
            rule_version: shared(""),
            rule_text_sha256: shared(""),
            visibility: shared(""),
        }
    }

    pub(crate) fn summoned_minion(entity_id: impl AsRef<str>, effect: &Effect) -> Self {
        let health = effect.health.max(1);
        let can_attack = (effect.rush || effect.charge) && effect.attack > 0;
        let attacks_remaining = if can_attack {
            if effect.windfury { 2 } else { 1 }
        } else {
            0
        };
        Self {
            entity_id: shared(entity_id),
            card_id: Arc::clone(&effect.card_id),
            name: Arc::clone(&effect.name),
            card_type: CardType::Minion,
            cost: 0,
            attack: effect.attack,
            health,
            current_health: health,
            current_health_known: true,
            playable: false,
            can_attack,
            attacks_remaining,
            attacks_remaining_known: true,
            taunt: effect.taunt,
            divine_shield: effect.divine_shield,
            frozen: false,
            stealth: effect.stealth,
            poisonous: effect.poisonous,
            lifesteal: effect.lifesteal,
            windfury: effect.windfury,
            mega_windfury: false,
            rush: effect.rush,
            charge: effect.charge,
            reborn: effect.reborn,
            dormant: false,
            immune: false,
            summoned_this_turn: true,
            durability: 0,
            current_durability: 0,
            effects: Vec::<Effect>::new().into(),
            effect_coverage: if effect.summoned_card_effects_unmodeled {
                EffectCoverage::Unsupported
            } else {
                EffectCoverage::Exact
            },
            unsupported_effects: if effect.summoned_card_effects_unmodeled {
                vec![shared("summoned_card_text_not_modeled")].into()
            } else {
                Vec::<SharedStr>::new().into()
            },
            prior_weight: 1.0,
            tags: Arc::new(BTreeMap::new()),
            card_text: shared(""),
            rule_id: shared(""),
            rule_version: shared(""),
            rule_text_sha256: shared(""),
            visibility: shared(""),
        }
    }
}

#[derive(Clone, Copy, Debug, Default, Eq, Hash, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DeckCardOrigin {
    StartedInDeck,
    Generated,
    #[default]
    Unknown,
}

/// Publicly known identity for one or more cards that are still in a deck.
///
/// HDT exposes the local deck list, visible zone history, and explicitly known
/// shuffled cards separately.  Keeping the origin here lets deck-backed effects
/// (Tracking, draw-a-card-that-did-not-start-in-deck, and future shuffle rules)
/// share one canonical source without pretending hidden opponent cards are known.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct KnownDeckCard {
    pub card_id: SharedStr,
    #[serde(default = "one")]
    pub count: u16,
    #[serde(default)]
    pub origin: DeckCardOrigin,
    #[serde(default = "unknown_card_type")]
    pub card_type: CardType,
    #[serde(default)]
    pub cost: u16,
    #[serde(default)]
    pub name: SharedStr,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct PlayerState {
    pub player_id: SharedStr,
    pub hero: Card,
    #[serde(default)]
    pub mana: u16,
    #[serde(default)]
    pub max_mana: u16,
    #[serde(default)]
    pub armor: u16,
    #[serde(default)]
    pub hand: Vec<Card>,
    #[serde(default)]
    pub board: Vec<Card>,
    #[serde(default)]
    pub graveyard: Vec<Card>,
    #[serde(default)]
    pub deck_size: u16,
    #[serde(default)]
    pub known_deck: Vec<KnownDeckCard>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub deck_identity_complete: bool,
    #[serde(default)]
    pub fatigue: u16,
    #[serde(default)]
    pub hero_power: Option<Card>,
    #[serde(default)]
    pub hero_power_available: bool,
    #[serde(default)]
    pub weapon: Option<Card>,
    #[serde(default)]
    pub spell_power: u16,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub public_rule_tags: BTreeMap<SharedStr, JsonScalar>,
    #[serde(default, skip_serializing_if = "is_false")]
    pub public_rule_tags_complete: bool,
}

const fn is_false(value: &bool) -> bool {
    !*value
}

impl PlayerState {
    fn validate(&self, path: &str) -> Result<(), SolverError> {
        if self.player_id.trim().is_empty() {
            return Err(SolverError::schema(
                format!("{path}.player_id"),
                "must be a non-empty string",
            ));
        }
        if self.hand.len() > 10 {
            return Err(SolverError::schema(
                format!("{path}.hand"),
                "may not contain more than 10 cards",
            ));
        }
        if self.board.len() > 7 {
            return Err(SolverError::schema(
                format!("{path}.board"),
                "may not contain more than 7 minions",
            ));
        }
        for (index, card) in self.known_deck.iter().enumerate() {
            if card.card_id.trim().is_empty() || card.count == 0 {
                return Err(SolverError::schema(
                    format!("{path}.known_deck[{index}]"),
                    "card_id must be non-empty and count must be positive",
                ));
            }
        }
        let known_deck_count = self
            .known_deck
            .iter()
            .fold(0u32, |sum, card| sum.saturating_add(u32::from(card.count)));
        if self.deck_identity_complete && known_deck_count != u32::from(self.deck_size) {
            return Err(SolverError::schema(
                format!("{path}.known_deck"),
                "complete deck identity must account for deck_size exactly",
            ));
        }
        if self.mana > self.max_mana.saturating_add(20) {
            return Err(SolverError::schema(
                format!("{path}.mana"),
                "is implausibly larger than max_mana",
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct BeliefCandidate {
    #[serde(default)]
    pub card_id: SharedStr,
    #[serde(default)]
    pub probability: f64,
    #[serde(default)]
    pub impact: f64,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct BeliefState {
    #[serde(default)]
    pub opponent_hand_slots: u16,
    #[serde(default)]
    pub candidates: Vec<BeliefCandidate>,
    #[serde(default)]
    pub source_snapshot: SharedStr,
    #[serde(default)]
    pub confidence: f64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct GameState {
    pub state_id: SharedStr,
    pub turn: u32,
    pub active_player_id: SharedStr,
    #[serde(default)]
    pub perspective_player_id: SharedStr,
    pub friendly: PlayerState,
    pub opponent: PlayerState,
    #[serde(default = "unknown_text")]
    pub patch: SharedStr,
    #[serde(default = "unknown_text")]
    pub mode: SharedStr,
    #[serde(default)]
    pub rng_seed: i64,
    #[serde(default)]
    pub belief: BeliefState,
    #[serde(default)]
    pub metadata: BTreeMap<SharedStr, JsonScalar>,
}

fn unknown_text() -> SharedStr {
    shared("unknown")
}

impl GameState {
    pub fn validate(&mut self) -> Result<(), SolverError> {
        if self.state_id.trim().is_empty() {
            return Err(SolverError::schema(
                "state.state_id",
                "must be a non-empty string",
            ));
        }
        if self.turn == 0 {
            return Err(SolverError::schema("state.turn", "must be at least 1"));
        }
        self.friendly.validate("state.friendly")?;
        self.opponent.validate("state.opponent")?;
        if self.friendly.player_id == self.opponent.player_id {
            return Err(SolverError::schema(
                "state",
                "friendly and opponent player_id values must differ",
            ));
        }
        if self.perspective_player_id.is_empty() {
            self.perspective_player_id = Arc::clone(&self.friendly.player_id);
        }
        for (path, value) in [
            ("state.active_player_id", &self.active_player_id),
            ("state.perspective_player_id", &self.perspective_player_id),
        ] {
            if value != &self.friendly.player_id && value != &self.opponent.player_id {
                return Err(SolverError::schema(
                    path,
                    "must match friendly or opponent player_id",
                ));
            }
        }
        if !(0.0..=1.0).contains(&self.belief.confidence) || !self.belief.confidence.is_finite() {
            return Err(SolverError::schema(
                "state.belief.confidence",
                "must be a finite number between 0 and 1",
            ));
        }
        for (index, candidate) in self.belief.candidates.iter().enumerate() {
            if !(0.0..=1.0).contains(&candidate.probability) || !candidate.probability.is_finite() {
                return Err(SolverError::schema(
                    format!("state.belief.candidates[{index}].probability"),
                    "must be a finite number between 0 and 1",
                ));
            }
            if !candidate.impact.is_finite() {
                return Err(SolverError::schema(
                    format!("state.belief.candidates[{index}].impact"),
                    "must be finite",
                ));
            }
        }
        self.validate_entity_ids()
    }

    fn validate_entity_ids(&self) -> Result<(), SolverError> {
        let mut seen: HashSet<&str> = HashSet::new();
        for player in [&self.friendly, &self.opponent] {
            let cards = std::iter::once(&player.hero)
                .chain(player.hand.iter())
                .chain(player.board.iter())
                .chain(player.hero_power.iter())
                .chain(player.weapon.iter());
            for card in cards {
                if card.entity_id.trim().is_empty() {
                    return Err(SolverError::schema(
                        "state",
                        "entity_id values must be non-empty",
                    ));
                }
                if !seen.insert(card.entity_id.as_ref()) {
                    return Err(SolverError::schema(
                        "state",
                        format!("entity_id values must be unique: {}", card.entity_id),
                    ));
                }
            }
        }
        Ok(())
    }

    pub fn player(&self, player_id: &str) -> Result<&PlayerState, SolverError> {
        if self.friendly.player_id.as_ref() == player_id {
            Ok(&self.friendly)
        } else if self.opponent.player_id.as_ref() == player_id {
            Ok(&self.opponent)
        } else {
            Err(SolverError::schema("player_id", "unknown player"))
        }
    }

    pub fn player_mut(&mut self, player_id: &str) -> Result<&mut PlayerState, SolverError> {
        if self.friendly.player_id.as_ref() == player_id {
            Ok(&mut self.friendly)
        } else if self.opponent.player_id.as_ref() == player_id {
            Ok(&mut self.opponent)
        } else {
            Err(SolverError::schema("player_id", "unknown player"))
        }
    }

    pub fn other_player(&self, player_id: &str) -> Result<&PlayerState, SolverError> {
        if self.friendly.player_id.as_ref() == player_id {
            Ok(&self.opponent)
        } else if self.opponent.player_id.as_ref() == player_id {
            Ok(&self.friendly)
        } else {
            Err(SolverError::schema("player_id", "unknown player"))
        }
    }

    pub fn active_player(&self) -> Result<&PlayerState, SolverError> {
        self.player(&self.active_player_id)
    }
}

#[derive(Clone, Debug, Eq, Hash, PartialEq, Serialize)]
pub struct Action {
    pub kind: ActionKind,
    pub source_entity_id: SharedStr,
    pub target_entity_id: SharedStr,
    pub card_id: SharedStr,
    pub text: SharedStr,
    #[serde(skip_serializing_if = "board_position_is_zero")]
    pub board_position: u8,
}

const fn board_position_is_zero(value: &u8) -> bool {
    *value == 0
}

#[derive(Deserialize)]
struct RawAction {
    kind: ActionKind,
    #[serde(default)]
    source_entity_id: Option<EntityIdInput>,
    #[serde(default)]
    target_entity_id: Option<EntityIdInput>,
    #[serde(default)]
    card_id: SharedStr,
    #[serde(default)]
    text: SharedStr,
    #[serde(default)]
    board_position: u8,
}

impl<'de> Deserialize<'de> for Action {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let raw = RawAction::deserialize(deserializer)?;
        let source = raw
            .source_entity_id
            .map_or_else(
                || Ok(shared("")),
                |value| value.into_shared("source_entity_id", true),
            )
            .map_err(serde::de::Error::custom)?;
        let target = raw
            .target_entity_id
            .map_or_else(
                || Ok(shared("")),
                |value| value.into_shared("target_entity_id", true),
            )
            .map_err(serde::de::Error::custom)?;
        if raw.board_position > 7 {
            return Err(serde::de::Error::custom(
                "board_position must be between 0 and 7",
            ));
        }
        Ok(Self {
            kind: raw.kind,
            source_entity_id: source,
            target_entity_id: target,
            card_id: raw.card_id,
            text: raw.text,
            board_position: raw.board_position,
        })
    }
}

impl Action {
    #[must_use]
    pub fn new(
        kind: ActionKind,
        source_entity_id: impl AsRef<str>,
        target_entity_id: impl AsRef<str>,
        card_id: impl AsRef<str>,
    ) -> Self {
        Self {
            kind,
            source_entity_id: shared(source_entity_id),
            target_entity_id: shared(target_entity_id),
            card_id: shared(card_id),
            text: shared(""),
            board_position: 0,
        }
    }

    #[must_use]
    pub const fn with_board_position(mut self, board_position: u8) -> Self {
        self.board_position = board_position;
        self
    }

    #[must_use]
    pub fn end_turn() -> Self {
        Self::new(ActionKind::EndTurn, "", "", "")
    }

    #[must_use]
    pub fn action_id(&self) -> String {
        let base = format!(
            "{}:{}:{}",
            self.kind.as_str(),
            self.source_entity_id,
            self.target_entity_id
        );
        if self.board_position > 0 {
            format!("{base}:position={}", self.board_position)
        } else {
            base
        }
    }
}

impl fmt::Display for Action {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.action_id())
    }
}

#[derive(Clone, Debug, PartialEq, Serialize)]
pub struct SolveOptions {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub time_budget_ms: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_iterations: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub max_depth: Option<u16>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub top_k: Option<u8>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub search_seed: Option<i64>,
    pub allow_approximate_effects: bool,
    #[serde(skip_serializing_if = "shared_str_is_empty")]
    pub environment_version: SharedStr,
}

impl Default for SolveOptions {
    fn default() -> Self {
        Self {
            time_budget_ms: None,
            max_iterations: None,
            max_depth: None,
            top_k: None,
            search_seed: None,
            allow_approximate_effects: true,
            environment_version: shared(""),
        }
    }
}

#[derive(Deserialize, Default)]
#[serde(deny_unknown_fields)]
struct RawSolveOptions {
    time_budget_ms: Option<u32>,
    max_iterations: Option<u32>,
    max_depth: Option<u16>,
    top_k: Option<u8>,
    time_budget_milliseconds: Option<u32>,
    initial_budget_milliseconds: Option<u32>,
    max_recommendations: Option<u8>,
    search_seed: Option<i64>,
    #[serde(default = "default_true")]
    allow_approximate_effects: bool,
    #[serde(default)]
    environment_version: SharedStr,
}

impl<'de> Deserialize<'de> for SolveOptions {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        let raw = RawSolveOptions::deserialize(deserializer)?;
        let time_budget_ms = raw.time_budget_ms.or(raw.time_budget_milliseconds);
        let top_k = raw.top_k.or(raw.max_recommendations);
        let positive = [
            time_budget_ms.map(u64::from),
            raw.max_iterations.map(u64::from),
            raw.max_depth.map(u64::from),
            top_k.map(u64::from),
            raw.initial_budget_milliseconds.map(u64::from),
        ];
        if positive.into_iter().flatten().any(|value| value == 0) {
            return Err(serde::de::Error::custom(
                "solve option limits must be positive",
            ));
        }
        Ok(Self {
            time_budget_ms,
            max_iterations: raw.max_iterations,
            max_depth: raw.max_depth,
            top_k,
            search_seed: raw.search_seed,
            allow_approximate_effects: raw.allow_approximate_effects,
            environment_version: raw.environment_version,
        })
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SolveRequest {
    #[serde(default = "api_version")]
    pub api_version: SharedStr,
    pub request_id: SharedStr,
    pub state: GameState,
    #[serde(default)]
    pub options: SolveOptions,
    #[serde(default)]
    pub metadata: BTreeMap<SharedStr, JsonScalar>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hdt_root_candidates: Option<HdtRootCandidateSet>,
}

impl SolveRequest {
    pub fn validate(&mut self) -> Result<(), SolverError> {
        if self.api_version.as_ref() != API_VERSION {
            return Err(SolverError::schema(
                "request.api_version",
                format!("expected {API_VERSION:?}"),
            ));
        }
        if self.request_id.trim().is_empty() {
            return Err(SolverError::schema(
                "request.request_id",
                "must be a non-empty string",
            ));
        }
        if let Some(seed) = self.options.search_seed {
            self.state.rng_seed = seed;
        }
        if !self.options.environment_version.is_empty() {
            self.state.metadata.insert(
                shared("environment_version"),
                JsonScalar::String(Arc::clone(&self.options.environment_version)),
            );
        }
        self.state.validate()?;
        if let Some(candidates) = &self.hdt_root_candidates {
            candidates.validate(&self.state)?;
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct StateKey(pub [u8; 32]);

impl StateKey {
    #[must_use]
    pub fn from_state(state: &GameState) -> Self {
        let mut writer = KeyWriter(blake3::Hasher::new());
        writer.text(&state.active_player_id);
        writer.u32(state.turn);
        writer.player(&state.friendly);
        writer.player(&state.opponent);
        Self(*writer.0.finalize().as_bytes())
    }
}

struct KeyWriter(blake3::Hasher);

impl KeyWriter {
    fn bytes(&mut self, value: &[u8]) {
        self.0.update(&(value.len() as u64).to_le_bytes());
        self.0.update(value);
    }

    fn text(&mut self, value: &str) {
        self.bytes(value.as_bytes());
    }

    fn boolean(&mut self, value: bool) {
        self.0.update(&[u8::from(value)]);
    }

    fn u8(&mut self, value: u8) {
        self.0.update(&[value]);
    }

    fn u16(&mut self, value: u16) {
        self.0.update(&value.to_le_bytes());
    }

    fn u32(&mut self, value: u32) {
        self.0.update(&value.to_le_bytes());
    }

    fn u64(&mut self, value: u64) {
        self.0.update(&value.to_le_bytes());
    }

    fn i16(&mut self, value: i16) {
        self.0.update(&value.to_le_bytes());
    }

    fn i32(&mut self, value: i32) {
        self.0.update(&value.to_le_bytes());
    }

    fn scalar(&mut self, value: &JsonScalar) {
        match value {
            JsonScalar::String(value) => {
                self.u8(0);
                self.text(value);
            }
            JsonScalar::Integer(value) => {
                self.u8(1);
                self.0.update(&value.to_le_bytes());
            }
            JsonScalar::Float(value) => {
                self.u8(2);
                self.0.update(&value.to_bits().to_le_bytes());
            }
            JsonScalar::Bool(value) => {
                self.u8(3);
                self.boolean(*value);
            }
            JsonScalar::Null => self.u8(4),
        }
    }

    fn effect(&mut self, effect: &Effect) {
        self.text(&effect.kind);
        self.text(&effect.trigger);
        self.i32(effect.amount);
        self.text(&effect.target);
        self.u16(effect.count);
        self.text(&effect.card_id);
        self.text(&effect.name);
        self.u16(effect.attack);
        self.u16(effect.health);
        self.u16(effect.durability);
        self.boolean(effect.random);
        self.u8(match effect.pool_selection {
            PoolSelection::None => 0,
            PoolSelection::UniformRandom => 1,
            PoolSelection::Discover => 2,
        });
        self.u8(match effect.pool_destination {
            PoolDestination::None => 0,
            PoolDestination::Hand => 1,
            PoolDestination::Battlefield => 2,
            PoolDestination::Deck => 3,
            PoolDestination::Cast => 4,
        });
        if let Some(pool) = &effect.pool {
            self.boolean(true);
            self.u8(match pool.source {
                CardPoolSource::CurrentFormat => 0,
                CardPoolSource::OwnerDeck => 1,
                CardPoolSource::OpponentDeck => 2,
                CardPoolSource::OwnerHand => 3,
                CardPoolSource::OpponentHand => 4,
                CardPoolSource::Graveyard => 5,
                CardPoolSource::Historical => 6,
                CardPoolSource::Entourage => 7,
            });
            self.boolean(pool.collectible);
            for value in [pool.cost_min, pool.cost_max] {
                if let Some(value) = value {
                    self.boolean(true);
                    self.u16(value);
                } else {
                    self.boolean(false);
                }
            }
            self.u32(pool.card_types.len() as u32);
            for card_type in &pool.card_types {
                self.text(card_type.as_str());
            }
            self.u8(match pool.class_mode {
                CardPoolClassMode::Any => 0,
                CardPoolClassMode::Controller => 1,
                CardPoolClassMode::ControllerOrNeutral => 2,
                CardPoolClassMode::AnotherClass => 3,
                CardPoolClassMode::Specific => 4,
            });
            for values in [
                &pool.class_ids,
                &pool.spell_school_ids,
                &pool.minion_type_ids,
                &pool.card_set_ids,
                &pool.rarity_ids,
                &pool.keyword_ids,
            ] {
                self.u32(values.len() as u32);
                for value in values {
                    self.u16(*value);
                }
            }
            self.u32(pool.required_keywords.len() as u32);
            for value in &pool.required_keywords {
                self.text(value);
            }
            self.boolean(pool.exclude_self);
            self.u32(pool.exclude_card_ids.len() as u32);
            for value in &pool.exclude_card_ids {
                self.text(value);
            }
        } else {
            self.boolean(false);
        }
        self.u16(effect.offer_count);
        self.boolean(effect.with_replacement);
        self.i16(effect.created_card_cost_delta);
        self.u32(effect.resolved_pool_population);
        self.boolean(effect.resolved_pool_exact);
        self.u32(effect.resolved_pool.len() as u32);
        for candidate in effect.resolved_pool.iter() {
            self.text(&candidate.card.card_id);
            self.u64(candidate.card.dbf_id);
            self.u32(candidate.weight);
        }
        for value in [
            effect.rush,
            effect.taunt,
            effect.divine_shield,
            effect.stealth,
            effect.poisonous,
            effect.lifesteal,
            effect.windfury,
            effect.charge,
            effect.reborn,
        ] {
            self.boolean(value);
        }
        self.boolean(effect.summoned_card_effects_unmodeled);
        match effect.hand_count_at_most {
            Some(value) => {
                self.boolean(true);
                self.u16(value);
            }
            None => self.boolean(false),
        }
    }

    #[allow(clippy::too_many_lines)]
    fn card(&mut self, card: &Card) {
        self.text(&card.entity_id);
        self.text(&card.card_id);
        self.text(card.card_type.as_str());
        self.u16(card.cost);
        self.u16(card.attack);
        self.u16(card.health);
        self.u16(card.current_health);
        self.boolean(card.current_health_known);
        self.boolean(card.playable);
        self.boolean(card.can_attack);
        self.u8(card.attacks_remaining);
        self.boolean(card.attacks_remaining_known);
        for value in [
            card.taunt,
            card.divine_shield,
            card.frozen,
            card.stealth,
            card.poisonous,
            card.lifesteal,
            card.windfury,
            card.mega_windfury,
            card.rush,
            card.charge,
            card.reborn,
            card.dormant,
            card.immune,
            card.summoned_this_turn,
        ] {
            self.boolean(value);
        }
        self.u16(card.durability);
        self.u16(card.current_durability);
        self.text(card.effect_coverage.as_str());
        self.u32(card.effects.len() as u32);
        for effect in card.effects.iter() {
            self.effect(effect);
        }
        self.u32(card.unsupported_effects.len() as u32);
        for item in card.unsupported_effects.iter() {
            self.text(item);
        }
        self.u32(card.tags.len() as u32);
        for (key, value) in card.tags.iter() {
            self.text(key);
            self.scalar(value);
        }
    }

    fn player(&mut self, player: &PlayerState) {
        self.text(&player.player_id);
        self.card(&player.hero);
        self.u16(player.mana);
        self.u16(player.max_mana);
        self.u16(player.armor);
        self.u16(player.deck_size);
        self.u16(player.fatigue);
        self.u16(player.spell_power);
        self.boolean(player.hero_power_available);
        self.u32(player.hand.len() as u32);
        for card in &player.hand {
            self.card(card);
        }
        self.u32(player.board.len() as u32);
        for card in &player.board {
            self.card(card);
        }
        self.u32(player.graveyard.len() as u32);
        for card in &player.graveyard {
            self.card(card);
        }
        self.u32(player.known_deck.len() as u32);
        for card in &player.known_deck {
            self.text(&card.card_id);
            self.u16(card.count);
            self.u8(match card.origin {
                DeckCardOrigin::StartedInDeck => 0,
                DeckCardOrigin::Generated => 1,
                DeckCardOrigin::Unknown => 2,
            });
            self.text(card.card_type.as_str());
            self.u16(card.cost);
            self.text(&card.name);
        }
        self.boolean(player.deck_identity_complete);
        for card in [&player.hero_power, &player.weapon] {
            self.boolean(card.is_some());
            if let Some(card) = card {
                self.card(card);
            }
        }
        self.u32(player.public_rule_tags.len() as u32);
        for (key, value) in &player.public_rule_tags {
            self.text(key);
            self.scalar(value);
        }
        self.boolean(player.public_rule_tags_complete);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn state() -> GameState {
        let mut request: SolveRequest = serde_json::from_str(
            r#"{
              "request_id":"test",
              "state":{
                "state_id":"s","turn":1,"active_player_id":"friendly",
                "perspective_player_id":"friendly",
                "friendly":{"player_id":"friendly","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":0,"max_mana":0},
                "opponent":{"player_id":"opponent","hero":{"entity_id":"oh","card_type":"HERO","health":30},"mana":0,"max_mana":0}
              }
            }"#,
        )
        .expect("valid fixture");
        request.validate().expect("valid request");
        request.state
    }

    #[test]
    fn clone_shares_immutable_card_identity_and_preserves_source() {
        let original = state();
        let mut cloned = original.clone();
        assert!(Arc::ptr_eq(
            &original.friendly.hero.entity_id,
            &cloned.friendly.hero.entity_id
        ));
        cloned.friendly.hero.current_health = 1;
        assert_eq!(original.friendly.hero.current_health, 30);
    }

    #[test]
    fn structured_key_tracks_gameplay_but_not_metadata() {
        let original = state();
        let mut changed = original.clone();
        changed
            .metadata
            .insert(shared("diagnostic"), JsonScalar::Bool(true));
        assert_eq!(
            StateKey::from_state(&original),
            StateKey::from_state(&changed)
        );
        changed.friendly.hero.current_health -= 1;
        assert_ne!(
            StateKey::from_state(&original),
            StateKey::from_state(&changed)
        );
    }

    #[test]
    fn duplicate_entity_ids_are_rejected() {
        let mut state = state();
        state.opponent.hero.entity_id = Arc::clone(&state.friendly.hero.entity_id);
        let error = state.validate().expect_err("duplicate must fail");
        assert_eq!(error.code(), "schema_error");
    }

    #[test]
    fn board_position_is_bounded_serialized_and_part_of_action_identity() {
        let positioned = Action::new(ActionKind::PlayCard, "21", "", "CARD").with_board_position(7);
        assert_eq!(positioned.action_id(), "play_card:21::position=7");
        assert_eq!(
            serde_json::to_value(&positioned).expect("serialize action")["board_position"],
            7
        );
        let plain = Action::new(ActionKind::PlayCard, "21", "", "CARD");
        assert!(
            serde_json::to_value(&plain)
                .expect("serialize plain action")
                .get("board_position")
                .is_none()
        );
        let error = serde_json::from_value::<Action>(serde_json::json!({
            "kind": "play_card",
            "source_entity_id": "21",
            "target_entity_id": "",
            "board_position": 8
        }))
        .expect_err("out-of-range position must fail");
        assert!(error.to_string().contains("between 0 and 7"));
    }
}
