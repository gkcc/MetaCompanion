use std::collections::{HashMap, VecDeque};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use serde::Serialize;
use sha2::{Digest, Sha256};

use crate::error::SolverError;
use crate::model::{
    Action, ActionKind, Card, CardPoolSource, CardType, DeckCardOrigin, Effect, EffectCoverage,
    GameState, JsonScalar, KnownDeckCard, PlayerState, PoolDestination, PoolSelection,
    ResolvedPoolCandidate, StateKey,
};
use crate::template_rules::apply_embedded_template_rule_to_card;

pub const ORACLE_SCOPE: &str = "oracle-turn-v1";
pub const DEFAULT_MAXIMUM_STATES: usize = 100_000;

#[derive(Clone, Debug, Eq, PartialEq, Serialize)]
pub struct OracleProof {
    pub has_lethal: bool,
    pub winning_first_action_ids: Vec<String>,
    pub explored_state_count: usize,
}

#[derive(Clone, Debug)]
pub struct TurnPlan {
    pub actions: Vec<Action>,
    pub terminal_state: GameState,
    pub minimax_utility: i64,
    pub explored_state_count: usize,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize)]
pub struct ExactProbability {
    pub numerator: u64,
    pub denominator: u64,
}

impl ExactProbability {
    pub const CERTAIN: Self = Self {
        numerator: 1,
        denominator: 1,
    };

    pub fn new(numerator: u64, denominator: u64) -> Result<Self, SolverError> {
        if denominator == 0 {
            return Err(SolverError::Unsupported(
                "chance probability denominator cannot be zero".to_owned(),
            ));
        }
        let divisor = greatest_common_divisor(numerator, denominator);
        Ok(Self {
            numerator: numerator / divisor,
            denominator: denominator / divisor,
        })
    }

    pub(crate) fn multiply(self, other: Self) -> Result<Self, SolverError> {
        let left_divisor = greatest_common_divisor(self.numerator, other.denominator);
        let right_divisor = greatest_common_divisor(other.numerator, self.denominator);
        let numerator = (self.numerator / left_divisor)
            .checked_mul(other.numerator / right_divisor)
            .ok_or_else(|| {
                SolverError::Unsupported("chance probability numerator overflow".to_owned())
            })?;
        let denominator = (self.denominator / right_divisor)
            .checked_mul(other.denominator / left_divisor)
            .ok_or_else(|| {
                SolverError::Unsupported("chance probability denominator overflow".to_owned())
            })?;
        Self::new(numerator, denominator)
    }

    pub(crate) fn add(self, other: Self) -> Result<Self, SolverError> {
        let divisor = greatest_common_divisor(self.denominator, other.denominator);
        let left_scale = other.denominator / divisor;
        let right_scale = self.denominator / divisor;
        let numerator = self
            .numerator
            .checked_mul(left_scale)
            .and_then(|left| {
                other
                    .numerator
                    .checked_mul(right_scale)
                    .and_then(|right| left.checked_add(right))
            })
            .ok_or_else(|| {
                SolverError::Unsupported("chance probability addition overflow".to_owned())
            })?;
        let denominator = self.denominator.checked_mul(left_scale).ok_or_else(|| {
            SolverError::Unsupported("chance probability denominator overflow".to_owned())
        })?;
        Self::new(numerator, denominator)
    }

    fn uniform(branch_count: usize) -> Result<Self, SolverError> {
        let denominator = u64::try_from(branch_count)
            .map_err(|_| SolverError::Unsupported("chance branch count is too large".to_owned()))?;
        Self::new(1, denominator)
    }
}

const fn greatest_common_divisor(mut left: u64, mut right: u64) -> u64 {
    while right != 0 {
        let remainder = left % right;
        left = right;
        right = remainder;
    }
    if left == 0 { 1 } else { left }
}

#[derive(Clone, Debug)]
pub struct ActionOutcome {
    pub state: GameState,
    pub ended_turn: bool,
    pub probability: ExactProbability,
}

#[derive(Clone, Debug)]
struct WeightedState {
    state: GameState,
    probability: ExactProbability,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum PlayerSide {
    Friendly,
    Opponent,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum CharacterLocation {
    Hero(PlayerSide),
    Board(PlayerSide, usize),
}

fn side_for_player(state: &GameState, player_id: &str) -> Result<PlayerSide, SolverError> {
    if state.friendly.player_id.as_ref() == player_id {
        Ok(PlayerSide::Friendly)
    } else if state.opponent.player_id.as_ref() == player_id {
        Ok(PlayerSide::Opponent)
    } else {
        Err(SolverError::schema("player_id", "unknown player"))
    }
}

fn player(state: &GameState, side: PlayerSide) -> &PlayerState {
    match side {
        PlayerSide::Friendly => &state.friendly,
        PlayerSide::Opponent => &state.opponent,
    }
}

fn player_mut(state: &mut GameState, side: PlayerSide) -> &mut PlayerState {
    match side {
        PlayerSide::Friendly => &mut state.friendly,
        PlayerSide::Opponent => &mut state.opponent,
    }
}

fn other_side(side: PlayerSide) -> PlayerSide {
    match side {
        PlayerSide::Friendly => PlayerSide::Opponent,
        PlayerSide::Opponent => PlayerSide::Friendly,
    }
}

fn one_cost_card_doubling_triggers(
    state: &GameState,
    side: PlayerSide,
) -> Result<u32, SolverError> {
    let mut triggers = 0u32;
    for effect in player(state, side)
        .board
        .iter()
        .filter(|card| card.current_health > 0 && !card.dormant)
        .flat_map(|card| card.effects.iter())
        .filter(|effect| effect.kind.as_ref() == "double_one_cost_cards")
    {
        if effect.amount != 2 || effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "one-cost card doubler has an invalid public rule".to_owned(),
            ));
        }
        triggers = triggers.saturating_add(1);
    }
    Ok(triggers)
}

fn one_cost_multiplier(trigger_count: u32) -> u16 {
    (0..trigger_count).fold(1u16, |value, _| value.saturating_mul(2))
}

fn copied_spell_target_is_missing(state: &GameState, target_id: &str) -> bool {
    !target_id.is_empty() && find_character(state, target_id).is_none()
}

fn living(cards: &[Card]) -> impl Iterator<Item = &Card> {
    cards.iter().filter(|card| card.current_health > 0)
}

fn occupies_board_slot(card: &Card) -> bool {
    card.card_type != CardType::Minion || card.current_health > 0
}

const fn maximum_attacks(card: &Card) -> u8 {
    if card.mega_windfury {
        4
    } else if card.windfury {
        2
    } else {
        1
    }
}

fn scalar_integer(value: &JsonScalar) -> Option<i64> {
    match value {
        JsonScalar::Integer(value) => Some(*value),
        JsonScalar::Float(value) if value.is_finite() && value.fract() == 0.0 => {
            Some(*value as i64)
        }
        JsonScalar::Bool(value) => Some(i64::from(*value)),
        JsonScalar::String(value) => value.trim().parse().ok(),
        JsonScalar::Float(_) | JsonScalar::Null => None,
    }
}

fn public_tag_value(card: &Card, names: &[&str], enum_id: u16) -> Option<i64> {
    let enum_id = enum_id.to_string();
    card.tags
        .iter()
        .find(|(key, _)| {
            key.as_ref() == enum_id
                || names
                    .iter()
                    .any(|name| key.as_ref().eq_ignore_ascii_case(name))
        })
        .and_then(|(_, value)| scalar_integer(value))
}

fn public_named_tag_value(card: &Card, names: &[&str]) -> Option<i64> {
    card.tags
        .iter()
        .find(|(key, _)| {
            names
                .iter()
                .any(|name| key.as_ref().eq_ignore_ascii_case(name))
        })
        .and_then(|(_, value)| scalar_integer(value))
}

fn hero_power_cost_aura_profile(owner: &PlayerState) -> Result<Option<(u16, u16)>, SolverError> {
    let mut profile = None;
    for effect in owner
        .board
        .iter()
        .filter(|card| card.current_health > 0 && !card.dormant)
        .flat_map(|card| card.effects.iter())
        .filter(|effect| effect.kind.as_ref() == "set_hero_power_cost")
    {
        let cost = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported("hero-power cost aura has an invalid cost".to_owned())
        })?;
        let threshold = effect.hand_count_at_most.ok_or_else(|| {
            SolverError::Unsupported(
                "hero-power cost aura has no public hand-count condition".to_owned(),
            )
        })?;
        let current = (cost, threshold);
        if profile.is_some_and(|existing| existing != current) {
            return Err(SolverError::Unsupported(
                "multiple different hero-power cost auras require layer ordering".to_owned(),
            ));
        }
        profile = Some(current);
    }
    Ok(profile)
}

fn unmodified_hero_power_cost(owner: &PlayerState) -> Result<u16, SolverError> {
    let power = owner.hero_power.as_ref().ok_or_else(|| {
        SolverError::Unsupported("hero-power cost aura has no hero power".to_owned())
    })?;
    let raw = public_named_tag_value(power, &["TAG_LAST_KNOWN_COST_IN_HAND"]).ok_or_else(|| {
        SolverError::Unsupported(
            "hero-power cost aura requires TAG_LAST_KNOWN_COST_IN_HAND evidence".to_owned(),
        )
    })?;
    u16::try_from(raw).map_err(|_| {
        SolverError::Unsupported("hero-power base cost evidence is invalid".to_owned())
    })
}

fn expected_hero_power_cost(owner: &PlayerState, profile: (u16, u16)) -> Result<u16, SolverError> {
    let base = unmodified_hero_power_cost(owner)?;
    Ok(if owner.hand.len() <= usize::from(profile.1) {
        profile.0
    } else {
        base
    })
}

pub(crate) fn assert_continuous_effect_state(state: &GameState) -> Result<(), SolverError> {
    for side in [PlayerSide::Friendly, PlayerSide::Opponent] {
        let owner = player(state, side);
        let Some(profile) = hero_power_cost_aura_profile(owner)? else {
            continue;
        };
        let expected = expected_hero_power_cost(owner, profile)?;
        let actual = owner.hero_power.as_ref().map_or(0, |power| power.cost);
        if actual != expected {
            return Err(SolverError::Unsupported(format!(
                "hero-power cost aura expected {expected} but HDT exposed {actual}"
            )));
        }
    }
    Ok(())
}

fn reconcile_continuous_effects(
    before: &GameState,
    next: &mut GameState,
) -> Result<(), SolverError> {
    for side in [PlayerSide::Friendly, PlayerSide::Opponent] {
        let previous_profile = hero_power_cost_aura_profile(player(before, side))?;
        let current_profile = hero_power_cost_aura_profile(player(next, side))?;
        if previous_profile.is_none() && current_profile.is_none() {
            continue;
        }
        let desired = if let Some(profile) = current_profile {
            expected_hero_power_cost(player(next, side), profile)?
        } else {
            unmodified_hero_power_cost(player(next, side))?
        };
        let owner = player_mut(next, side);
        let power = owner.hero_power.as_mut().ok_or_else(|| {
            SolverError::Unsupported("hero-power cost aura lost its hero power".to_owned())
        })?;
        power.cost = desired;
        set_public_tag_value(power, "COST", 48, i64::from(desired));
    }
    Ok(())
}

#[must_use]
pub(crate) fn public_hero_attack_history_available(hero: &Card) -> bool {
    public_tag_value(hero, &["NUM_ATTACKS_THIS_TURN"], 297).is_some()
}

fn public_tag_is_active(card: &Card, names: &[&str], enum_id: u16) -> bool {
    public_tag_value(card, names, enum_id).is_some_and(|value| value != 0)
}

fn set_public_tag_value(card: &mut Card, name: &str, enum_id: u16, value: i64) {
    let enum_id = enum_id.to_string();
    let key = card
        .tags
        .keys()
        .find(|key| key.as_ref() == enum_id || key.as_ref().eq_ignore_ascii_case(name))
        .cloned()
        .unwrap_or_else(|| Arc::<str>::from(name));
    Arc::make_mut(&mut card.tags).insert(key, JsonScalar::Integer(value));
}

#[must_use]
pub(crate) fn maximum_attacks_with_weapon(hero: &Card, weapon: Option<&Card>) -> u8 {
    let keyword_limit: u8 = if hero.mega_windfury || weapon.is_some_and(|card| card.mega_windfury) {
        4
    } else if hero.windfury || weapon.is_some_and(|card| card.windfury) {
        2
    } else {
        1
    };
    let extra = public_tag_value(hero, &["EXTRA_ATTACKS_THIS_TURN"], 444)
        .unwrap_or(0)
        .max(0);
    keyword_limit.saturating_add(u8::try_from(extra).unwrap_or(u8::MAX))
}

#[must_use]
pub(crate) fn public_attack_is_blocked(hero: &Card, weapon: Option<&Card>) -> bool {
    public_tag_is_active(hero, &["CANT_ATTACK"], 227)
        || weapon.is_some_and(|card| public_tag_is_active(card, &["CANT_ATTACK"], 227))
}

#[must_use]
pub(crate) fn public_cannot_attack_heroes(hero: &Card, weapon: Option<&Card>) -> bool {
    const NAMES: &[&str] = &["CANNOT_ATTACK_HEROES", "CANT_ATTACK_HEROES"];
    public_tag_is_active(hero, NAMES, 413)
        || weapon.is_some_and(|card| public_tag_is_active(card, NAMES, 413))
}

/// Reconcile the active hero's HDT attack count with an equipped Windfury
/// weapon. HDT exposes `NUM_ATTACKS_THIS_TURN` publicly, while the weapon owns
/// the keyword that raises the hero's attack limit.
pub(crate) fn normalize_active_weapon_attacks(state: &mut GameState) -> Result<(), SolverError> {
    let active_id = Arc::clone(&state.active_player_id);
    let actor_side = side_for_player(state, &active_id)?;
    let actor = player_mut(state, actor_side);
    let Some(weapon) = actor.weapon.as_ref() else {
        return Ok(());
    };
    let maximum = maximum_attacks_with_weapon(&actor.hero, Some(weapon));
    let attacks_used = public_tag_value(&actor.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
        .map(|value| u8::try_from(value.max(0)).unwrap_or(u8::MAX));
    let explicitly_exhausted = public_tag_is_active(&actor.hero, &["EXHAUSTED"], 43);
    let weapon_is_usable = weapon.card_type == CardType::Weapon
        && weapon.current_durability > 0
        && !public_attack_is_blocked(&actor.hero, Some(weapon));
    if let Some(attacks_used) = attacks_used {
        actor.hero.attacks_remaining = maximum.saturating_sub(attacks_used);
        actor.hero.attacks_remaining_known = true;
        actor.hero.can_attack = weapon_is_usable
            && actor.hero.attack > 0
            && actor.hero.current_health > 0
            && !actor.hero.frozen
            && !actor.hero.dormant
            && !explicitly_exhausted
            && actor.hero.attacks_remaining > 0;
    } else if !weapon_is_usable {
        actor.hero.attacks_remaining = 0;
        actor.hero.attacks_remaining_known = true;
        actor.hero.can_attack = false;
    }
    Ok(())
}

pub(crate) fn reset_public_turn_attack_tags(hero: &mut Card) {
    if public_tag_value(hero, &["NUM_ATTACKS_THIS_TURN"], 297).is_some() {
        set_public_tag_value(hero, "NUM_ATTACKS_THIS_TURN", 297, 0);
    }
    if public_tag_value(hero, &["EXHAUSTED"], 43).is_some() {
        set_public_tag_value(hero, "EXHAUSTED", 43, 0);
    }
}

fn find_character(state: &GameState, entity_id: &str) -> Option<CharacterLocation> {
    for side in [PlayerSide::Friendly, PlayerSide::Opponent] {
        let owner = player(state, side);
        if owner.hero.entity_id.as_ref() == entity_id {
            return Some(CharacterLocation::Hero(side));
        }
        if let Some(index) = owner
            .board
            .iter()
            .position(|card| card.entity_id.as_ref() == entity_id)
        {
            return Some(CharacterLocation::Board(side, index));
        }
    }
    None
}

fn character(state: &GameState, location: CharacterLocation) -> &Card {
    match location {
        CharacterLocation::Hero(side) => &player(state, side).hero,
        CharacterLocation::Board(side, index) => &player(state, side).board[index],
    }
}

fn character_mut(state: &mut GameState, location: CharacterLocation) -> &mut Card {
    match location {
        CharacterLocation::Hero(side) => &mut player_mut(state, side).hero,
        CharacterLocation::Board(side, index) => &mut player_mut(state, side).board[index],
    }
}

fn player_targetable_by_source(card: &Card, source_type: CardType) -> bool {
    let elusive = public_named_tag_value(card, &["ELUSIVE"]).is_some_and(|value| value != 0);
    match source_type {
        CardType::Spell => {
            !elusive
                && !public_named_tag_value(card, &["CANT_BE_TARGETED_BY_SPELLS"])
                    .is_some_and(|value| value != 0)
        }
        CardType::HeroPower => {
            !elusive
                && !public_named_tag_value(card, &["CANT_BE_TARGETED_BY_HERO_POWERS"])
                    .is_some_and(|value| value != 0)
        }
        _ => true,
    }
}

fn target_ids(
    state: &GameState,
    actor: PlayerSide,
    mode: &str,
    source_type: CardType,
) -> Vec<Arc<str>> {
    let enemy = other_side(actor);
    let actor_player = player(state, actor);
    let enemy_player = player(state, enemy);
    let friendly_characters = std::iter::once(&actor_player.hero)
        .chain(
            living(&actor_player.board)
                .filter(|card| card.card_type == CardType::Minion && !card.dormant),
        )
        .filter(|card| player_targetable_by_source(card, source_type));
    let enemy_characters = std::iter::once(&enemy_player.hero)
        .chain(living(&enemy_player.board).filter(|card| card.card_type == CardType::Minion))
        .filter(|card| {
            !card.stealth
                && !card.dormant
                && !card.immune
                && player_targetable_by_source(card, source_type)
        });
    match mode {
        "enemy_character" => enemy_characters
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "friendly_character" => friendly_characters
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "any_character" => friendly_characters
            .chain(enemy_characters)
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "enemy_minion" => living(&enemy_player.board)
            .filter(|card| {
                card.card_type == CardType::Minion
                    && !card.stealth
                    && !card.dormant
                    && !card.immune
                    && player_targetable_by_source(card, source_type)
            })
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "friendly_minion" => living(&actor_player.board)
            .filter(|card| {
                card.card_type == CardType::Minion
                    && !card.dormant
                    && player_targetable_by_source(card, source_type)
            })
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "any_minion" => living(&actor_player.board)
            .filter(|card| {
                card.card_type == CardType::Minion
                    && !card.dormant
                    && player_targetable_by_source(card, source_type)
            })
            .chain(living(&enemy_player.board).filter(|card| {
                card.card_type == CardType::Minion
                    && !card.stealth
                    && !card.dormant
                    && !card.immune
                    && player_targetable_by_source(card, source_type)
            }))
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "any_undamaged_minion" => living(&actor_player.board)
            .filter(|card| {
                card.card_type == CardType::Minion
                    && !card.dormant
                    && card.current_health_known
                    && card.current_health == card.health
                    && player_targetable_by_source(card, source_type)
            })
            .chain(living(&enemy_player.board).filter(|card| {
                card.card_type == CardType::Minion
                    && !card.stealth
                    && !card.dormant
                    && !card.immune
                    && card.current_health_known
                    && card.current_health == card.health
                    && player_targetable_by_source(card, source_type)
            }))
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "damaged_enemy_minion" => living(&enemy_player.board)
            .filter(|card| {
                card.card_type == CardType::Minion
                    && !card.stealth
                    && !card.dormant
                    && !card.immune
                    && card.current_health_known
                    && card.current_health < card.health
                    && player_targetable_by_source(card, source_type)
            })
            .map(|card| Arc::clone(&card.entity_id))
            .collect(),
        "enemy_hero"
            if !enemy_player.hero.stealth
                && !enemy_player.hero.immune
                && player_targetable_by_source(&enemy_player.hero, source_type) =>
        {
            vec![Arc::clone(&enemy_player.hero.entity_id)]
        }
        "friendly_hero" if player_targetable_by_source(&actor_player.hero, source_type) => {
            vec![Arc::clone(&actor_player.hero.entity_id)]
        }
        _ => Vec::new(),
    }
}

fn automatic_target_mode(mode: &str) -> bool {
    matches!(
        mode,
        "all_enemy_characters"
            | "all_friendly_characters"
            | "all_enemy_minions"
            | "all_friendly_minions"
            | "all_minions"
            | "all_characters"
            | "all_other_minions"
            | "all_other_friendly_minions"
    )
}

fn automatic_target_locations(
    state: &GameState,
    actor: PlayerSide,
    mode: &str,
    source_entity_id: &str,
) -> Option<Vec<CharacterLocation>> {
    let enemy = other_side(actor);
    let mut targets = Vec::new();
    let mut extend_side = |side: PlayerSide, include_hero: bool| {
        let owner = player(state, side);
        if include_hero && owner.hero.current_health > 0 {
            targets.push(CharacterLocation::Hero(side));
        }
        targets.extend(
            owner
                .board
                .iter()
                .enumerate()
                .filter(|(_, card)| {
                    card.card_type == CardType::Minion && card.current_health > 0 && !card.dormant
                })
                .map(|(index, _)| CharacterLocation::Board(side, index)),
        );
    };
    match mode {
        "all_enemy_characters" => extend_side(enemy, true),
        "all_friendly_characters" => extend_side(actor, true),
        "all_enemy_minions" => extend_side(enemy, false),
        "all_friendly_minions" => extend_side(actor, false),
        "all_minions" => {
            extend_side(actor, false);
            extend_side(enemy, false);
        }
        "all_other_minions" => {
            extend_side(actor, false);
            extend_side(enemy, false);
            targets.retain(|location| {
                character(state, *location).entity_id.as_ref() != source_entity_id
            });
        }
        "all_other_friendly_minions" => {
            extend_side(actor, false);
            targets.retain(|location| {
                character(state, *location).entity_id.as_ref() != source_entity_id
            });
        }
        "all_characters" => {
            extend_side(actor, true);
            extend_side(enemy, true);
        }
        _ => return None,
    }
    Some(targets)
}

fn player_target_mode(card: &Card) -> Option<&str> {
    card.effects.iter().find_map(|effect| {
        let mode = effect.target.as_ref();
        (effect.trigger.as_ref() == "resolution"
            && !effect.random
            && !matches!(mode, "none" | "self")
            && !automatic_target_mode(mode))
        .then_some(mode)
    })
}

fn primary_target_mode(card: &Card) -> &str {
    player_target_mode(card).unwrap_or("none")
}

fn supported_card_reason(card: &Card, in_hand: bool) -> Option<String> {
    if card.visibility.trim().eq_ignore_ascii_case("hidden") {
        return Some(format!("{} has hidden identity", card.entity_id));
    }
    if !card.unsupported_effects.is_empty() || card.effect_coverage == EffectCoverage::Unsupported {
        return Some(format!("{} has unsupported effects", card.entity_id));
    }
    if card.stealth {
        return Some(format!(
            "{} has stealth outside the exact oracle",
            card.entity_id
        ));
    }
    if card.frozen {
        return Some(format!(
            "{} is frozen outside the exact oracle",
            card.entity_id
        ));
    }
    if card.poisonous || card.lifesteal {
        return Some(format!(
            "{} uses an unsupported combat keyword",
            card.entity_id
        ));
    }
    if card.windfury
        || card.mega_windfury
        || card.rush
        || card.charge
        || card.reborn
        || card.dormant
        || card.immune
        || card.durability > 0
        || card.current_durability > 0
    {
        return Some(format!(
            "{} is outside the deliberately small oracle combat subset",
            card.entity_id
        ));
    }
    if !card.tags.is_empty() {
        return Some(format!(
            "{} carries unverified gameplay tags",
            card.entity_id
        ));
    }
    if in_hand && !matches!(card.card_type, CardType::Minion | CardType::Spell) {
        return Some(format!(
            "{} has unsupported playable type {}",
            card.entity_id,
            card.card_type.as_str()
        ));
    }
    if in_hand && card.card_type == CardType::Spell && card.effects.is_empty() {
        return Some(format!(
            "{} is a spell without an exact effect",
            card.entity_id
        ));
    }
    let mut target_mode: Option<&str> = None;
    for mode in card.effects.iter().filter_map(|effect| {
        let mode = effect.target.as_ref();
        (!effect.random && !matches!(mode, "none" | "self") && !automatic_target_mode(mode))
            .then_some(mode)
    }) {
        if target_mode.is_some_and(|existing| existing != mode) {
            return Some(format!("{} has multiple target groups", card.entity_id));
        }
        target_mode = Some(mode);
    }
    for effect in card.effects.iter() {
        let supported_point = match effect.kind.as_ref() {
            "freeze" => effect.amount == 0,
            "damage" | "heal" | "buff_attack" | "buff_health" | "set_health" => effect.amount > 0,
            _ => false,
        } && effect.target.as_ref() != "none";
        let supported_global = effect.kind.as_ref() == "damage_all_minions"
            && effect.amount > 0
            && effect.target.as_ref() == "none";
        let supported_owner = matches!(
            effect.kind.as_ref(),
            "armor" | "gain_hero_attack" | "gain_mana" | "buff_weapon_attack"
        ) && effect.target.as_ref() == "none";
        let supported_zone_draw = matches!(
            effect.kind.as_ref(),
            "draw" | "draw_opponent" | "draw_both_players" | "draw_until_hand_count"
        ) && effect.amount == 0
            && effect.target.as_ref() == "none"
            && (1..=10).contains(&effect.count);
        let supported_summon = effect.kind.as_ref() == "summon"
            && effect.amount == 0
            && effect.target.as_ref() == "none"
            && (1..=7).contains(&effect.count)
            && !effect.card_id.trim().is_empty()
            && !effect.name.trim().is_empty()
            && effect.health > 0;
        let point_or_owner_fields_valid = effect.count == 1
            && effect.card_id.is_empty()
            && effect.attack == 0
            && !effect.has_summoned_minion_keywords();
        let supported_random = effect.random
            && supported_point
            && point_or_owner_fields_valid
            && !matches!(effect.target.as_ref(), "none" | "self")
            && !automatic_target_mode(effect.target.as_ref());
        let empty_exact_deck_draw = effect.kind.as_ref() == "draw_from_pool"
            && effect.resolved_pool_exact
            && effect.resolved_pool_population == 0
            && effect
                .pool
                .as_ref()
                .is_some_and(|pool| pool.source == CardPoolSource::OwnerDeck);
        let supported_pool = effect.random
            && effect.pool.is_some()
            && ((!effect.resolved_pool.is_empty() && effect.resolved_pool_population > 0)
                || empty_exact_deck_draw)
            && effect.target.as_ref() == "none"
            && effect.card_id.is_empty()
            && effect.attack == 0
            && !effect.has_summoned_minion_keywords()
            && matches!(
                effect.kind.as_ref(),
                "generate_from_pool" | "discover_from_pool" | "summon_from_pool" | "draw_from_pool"
            );
        if !(supported_random
            || supported_pool
            || (!effect.random
                && (supported_summon
                    || supported_zone_draw
                    || ((supported_point || supported_global || supported_owner)
                        && point_or_owner_fields_valid))))
        {
            return Some(format!(
                "{} effect {:?} is outside the oracle subset",
                card.entity_id, effect.kind
            ));
        }
        if effect.amount < 0 {
            return Some(format!(
                "{} uses negative damage outside the oracle subset",
                card.entity_id
            ));
        }
        if effect.target.as_ref() == "self" {
            return Some(format!(
                "{} uses unsupported self targeting",
                card.entity_id
            ));
        }
    }
    None
}

fn attack_snapshot_reason_with_limit(card: &Card, maximum: u8) -> Option<String> {
    if !matches!(card.card_type, CardType::Hero | CardType::Minion) {
        return None;
    }
    if card.attacks_remaining > maximum {
        return Some(format!(
            "{} has {} attacks remaining but its keyword limit is {maximum}",
            card.entity_id, card.attacks_remaining
        ));
    }
    if card.can_attack && card.attacks_remaining == 0 {
        return Some(format!(
            "{} can attack with no attacks remaining",
            card.entity_id
        ));
    }
    if card.can_attack && (card.attack == 0 || card.current_health == 0) {
        return Some(format!(
            "{} is marked able to attack without positive attack and health",
            card.entity_id
        ));
    }
    if card.card_type == CardType::Minion
        && card.summoned_this_turn
        && card.can_attack
        && !card.rush
        && !card.charge
    {
        return Some(format!(
            "{} is newly summoned without rush or charge but can attack",
            card.entity_id
        ));
    }
    None
}

pub(crate) fn attack_snapshot_reason(card: &Card) -> Option<String> {
    attack_snapshot_reason_with_limit(card, maximum_attacks(card))
}

pub(crate) fn visible_attack_snapshot_reason(card: &Card, weapon: Option<&Card>) -> Option<String> {
    attack_snapshot_reason_with_limit(card, maximum_attacks_with_weapon(card, weapon))
}

pub fn assert_exact_oracle_state(state: &GameState) -> Result<(), SolverError> {
    if state.active_player_id != state.friendly.player_id {
        return Err(SolverError::Unsupported(
            "oracle-turn-v1 requires the friendly player to be active".to_owned(),
        ));
    }
    if state.perspective_player_id != state.friendly.player_id {
        return Err(SolverError::Unsupported(
            "oracle-turn-v1 requires the friendly perspective".to_owned(),
        ));
    }
    for owner in [&state.friendly, &state.opponent] {
        if owner.weapon.is_some() || owner.hero_power.is_some() || owner.hero_power_available {
            return Err(SolverError::Unsupported(
                "oracle-turn-v1 does not model weapons or hero powers".to_owned(),
            ));
        }
        if owner.spell_power != 0 {
            return Err(SolverError::Unsupported(
                "oracle-turn-v1 does not apply spell-power modifiers".to_owned(),
            ));
        }
        if !owner.public_rule_tags.is_empty() || owner.public_rule_tags_complete {
            return Err(SolverError::Unsupported(
                "oracle-turn-v1 does not interpret player rule tags".to_owned(),
            ));
        }
        if owner.hero.card_type != CardType::Hero {
            return Err(SolverError::Unsupported(format!(
                "{} is not typed as a hero",
                owner.hero.entity_id
            )));
        }
        for card in &owner.board {
            if card.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(format!(
                    "{} is not typed as a board minion",
                    card.entity_id
                )));
            }
        }
        for card in std::iter::once(&owner.hero).chain(owner.board.iter()) {
            if let Some(reason) = attack_snapshot_reason(card) {
                return Err(SolverError::Unsupported(reason));
            }
            if let Some(reason) = supported_card_reason(card, false) {
                return Err(SolverError::Unsupported(reason));
            }
        }
        for card in &owner.hand {
            if let Some(reason) = supported_card_reason(card, true) {
                return Err(SolverError::Unsupported(reason));
            }
        }
    }
    Ok(())
}

pub fn legal_actions(state: &GameState) -> Result<Vec<Action>, SolverError> {
    let actor_side = side_for_player(state, &state.active_player_id)?;
    let enemy_side = other_side(actor_side);
    let actor = player(state, actor_side);
    let enemy = player(state, enemy_side);
    if actor.hero.current_health == 0 || enemy.hero.current_health == 0 {
        return Ok(Vec::new());
    }

    let mut actions = Vec::new();
    let taunts: Vec<&Card> = living(&enemy.board)
        .filter(|card| {
            card.card_type == CardType::Minion && card.taunt && !card.stealth && !card.dormant
        })
        .collect();
    let attack_targets: Vec<&Card> = if taunts.is_empty() {
        living(&enemy.board)
            .filter(|card| card.card_type == CardType::Minion)
            .chain(std::iter::once(&enemy.hero))
            .filter(|card| !card.stealth && !card.dormant)
            .collect()
    } else {
        taunts
    };
    for attacker in std::iter::once(&actor.hero)
        .chain(living(&actor.board).filter(|card| card.card_type == CardType::Minion))
    {
        let attacking_weapon = (attacker.entity_id == actor.hero.entity_id)
            .then_some(actor.weapon.as_ref())
            .flatten();
        if attacker.attack == 0
            || !attacker.can_attack
            || attacker.attacks_remaining == 0
            || attacker.frozen
            || attacker.dormant
            || public_attack_is_blocked(attacker, attacking_weapon)
            || attacking_weapon.is_some_and(|weapon| {
                weapon.card_type != CardType::Weapon || weapon.current_durability == 0
            })
        {
            continue;
        }
        for target in &attack_targets {
            if target.card_type == CardType::Hero
                && public_cannot_attack_heroes(attacker, attacking_weapon)
            {
                continue;
            }
            if attacker.card_type == CardType::Minion
                && attacker.summoned_this_turn
                && attacker.rush
                && !attacker.charge
                && target.card_type == CardType::Hero
            {
                continue;
            }
            actions.push(Action::new(
                ActionKind::Attack,
                attacker.entity_id.as_ref(),
                target.entity_id.as_ref(),
                attacker.card_id.as_ref(),
            ));
        }
    }

    for card in &actor.hand {
        if !card.playable || card.cost > actor.mana {
            continue;
        }
        if card.card_type != CardType::Minion
            && card.effects.iter().any(|effect| {
                effect.trigger.as_ref() == "resolution"
                    && effect.kind.as_ref() == "buff_weapon_attack"
            })
            && actor.weapon.is_none()
        {
            continue;
        }
        let placement_card = matches!(card.card_type, CardType::Minion | CardType::Location);
        if placement_card
            && actor
                .board
                .iter()
                .filter(|card| occupies_board_slot(card))
                .count()
                >= 7
        {
            continue;
        }
        let board_positions = if placement_card {
            (1..=actor.board.len() + 1)
                .map(|position| u8::try_from(position).expect("board position is at most seven"))
                .collect::<Vec<_>>()
        } else {
            vec![0]
        };
        // A Location's text describes its later activation, not its placement.
        let mode = if card.card_type == CardType::Location {
            "none"
        } else {
            primary_target_mode(card)
        };
        if matches!(mode, "none" | "self") {
            for board_position in &board_positions {
                actions.push(
                    Action::new(
                        ActionKind::PlayCard,
                        card.entity_id.as_ref(),
                        "",
                        card.card_id.as_ref(),
                    )
                    .with_board_position(*board_position),
                );
            }
        } else {
            for target_id in target_ids(state, actor_side, mode, card.card_type) {
                for board_position in &board_positions {
                    actions.push(
                        Action::new(
                            ActionKind::PlayCard,
                            card.entity_id.as_ref(),
                            target_id.as_ref(),
                            card.card_id.as_ref(),
                        )
                        .with_board_position(*board_position),
                    );
                }
            }
        }
    }
    if let Some(power) = actor
        .hero_power
        .as_ref()
        .filter(|power| actor.hero_power_available && power.cost <= actor.mana)
    {
        let mode = primary_target_mode(power);
        if matches!(mode, "none" | "self") {
            actions.push(Action::new(
                ActionKind::HeroPower,
                power.entity_id.as_ref(),
                "",
                power.card_id.as_ref(),
            ));
        } else {
            for target_id in target_ids(state, actor_side, mode, power.card_type) {
                actions.push(Action::new(
                    ActionKind::HeroPower,
                    power.entity_id.as_ref(),
                    target_id.as_ref(),
                    power.card_id.as_ref(),
                ));
            }
        }
    }
    actions.push(Action::end_turn());
    Ok(actions)
}

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
struct DamageOutcome {
    dealt: u16,
    hero_overkill: u16,
}

fn damage(state: &mut GameState, location: CharacterLocation, amount: u16) -> DamageOutcome {
    if amount == 0 {
        return DamageOutcome::default();
    }
    if character(state, location).immune {
        return DamageOutcome::default();
    }
    if character(state, location).divine_shield {
        character_mut(state, location).divine_shield = false;
        return DamageOutcome::default();
    }
    let mut remaining = amount;
    if let CharacterLocation::Hero(side) = location {
        let owner = player_mut(state, side);
        let absorbed = owner.armor.min(remaining);
        owner.armor -= absorbed;
        remaining -= absorbed;
    }
    let target = character_mut(state, location);
    let hero_overkill = if matches!(location, CharacterLocation::Hero(_)) {
        remaining.saturating_sub(target.current_health)
    } else {
        0
    };
    target.current_health = target.current_health.saturating_sub(remaining);
    DamageOutcome {
        dealt: amount,
        hero_overkill,
    }
}

fn heal_hero(state: &mut GameState, side: PlayerSide, amount: u16) {
    let hero = &mut player_mut(state, side).hero;
    hero.current_health = hero.current_health.saturating_add(amount).min(hero.health);
}

fn remove_dead(state: &mut GameState) -> Result<(), SolverError> {
    loop {
        let active_side = side_for_player(state, &state.active_player_id)?;
        let mut queued = Vec::<(PlayerSide, Card)>::new();
        for side in [active_side, other_side(active_side)] {
            let owner = player_mut(state, side);
            let mut surviving = Vec::with_capacity(owner.board.len());
            for card in owner.board.drain(..) {
                if occupies_board_slot(&card) {
                    surviving.push(card);
                } else {
                    queued.push((side, card));
                }
            }
            owner.board = surviving;
        }
        if queued.is_empty() {
            return Ok(());
        }
        for (owner_side, dead) in queued {
            player_mut(state, owner_side).graveyard.push(dead.clone());
            for effect in dead
                .effects
                .iter()
                .filter(|effect| effect.trigger.as_ref() == "deathrattle")
            {
                if effect.random {
                    return Err(SolverError::Unsupported(
                        "random Deathrattle requires a chance-aware death queue".to_owned(),
                    ));
                }
                apply_deterministic_effect(state, owner_side, &dead, effect, "", 0)?;
            }
        }
    }
}

fn resolve_entity_once_trigger(
    state: &mut GameState,
    owner_side: PlayerSide,
    entity_id: &str,
    trigger: &str,
) -> Result<(), SolverError> {
    let Some(source) = player(state, owner_side)
        .board
        .iter()
        .find(|source| source.entity_id.as_ref() == entity_id && source.current_health > 0)
        .cloned()
    else {
        return Ok(());
    };
    let effects = source
        .effects
        .iter()
        .filter(|effect| effect.trigger.as_ref() == trigger)
        .cloned()
        .collect::<Vec<_>>();
    if effects.is_empty() {
        return Ok(());
    }
    if let Some(live_source) = player_mut(state, owner_side)
        .board
        .iter_mut()
        .find(|candidate| candidate.entity_id.as_ref() == entity_id)
    {
        live_source.effects = live_source
            .effects
            .iter()
            .filter(|effect| effect.trigger.as_ref() != trigger)
            .cloned()
            .collect::<Vec<_>>()
            .into();
    }
    for effect in effects {
        if effect.random {
            return Err(SolverError::Unsupported(format!(
                "random {trigger} trigger requires chance-aware event resolution"
            )));
        }
        apply_deterministic_effect(state, owner_side, &source, &effect, "", 0)?;
        remove_dead(state)?;
    }
    Ok(())
}

fn resolve_board_trigger(
    state: &mut GameState,
    owner_side: PlayerSide,
    trigger: &str,
) -> Result<(), SolverError> {
    let queued = {
        let owner = player(state, owner_side);
        owner
            .board
            .iter()
            .chain(owner.weapon.iter())
            .filter(|source| source.current_health > 0 || source.card_type == CardType::Weapon)
            .flat_map(|source| {
                source
                    .effects
                    .iter()
                    .filter(move |effect| effect.trigger.as_ref() == trigger)
                    .cloned()
                    .map(move |effect| (source.clone(), effect))
            })
            .collect::<Vec<_>>()
    };
    if trigger == "spellburst" {
        let mut consumed_sources = Vec::<Arc<str>>::new();
        for (source, _) in &queued {
            if consumed_sources
                .iter()
                .any(|entity_id| entity_id == &source.entity_id)
            {
                continue;
            }
            consumed_sources.push(Arc::clone(&source.entity_id));
        }
        let owner = player_mut(state, owner_side);
        for entity_id in consumed_sources {
            let source = owner
                .board
                .iter_mut()
                .chain(owner.weapon.iter_mut())
                .find(|source| source.entity_id == entity_id);
            if let Some(source) = source {
                source.effects = source
                    .effects
                    .iter()
                    .filter(|effect| effect.trigger.as_ref() != "spellburst")
                    .cloned()
                    .collect::<Vec<_>>()
                    .into();
            }
        }
    }
    for (source, effect) in queued {
        if effect.random {
            return Err(SolverError::Unsupported(format!(
                "random {trigger} trigger requires chance-aware event resolution"
            )));
        }
        apply_deterministic_effect(state, owner_side, &source, &effect, "", 0)?;
        remove_dead(state)?;
    }
    Ok(())
}

fn consume_once_trigger(
    state: &mut GameState,
    owner_side: PlayerSide,
    entity_id: &str,
    trigger: &str,
) {
    let owner = player_mut(state, owner_side);
    if let Some(source) = owner
        .board
        .iter_mut()
        .chain(owner.weapon.iter_mut())
        .find(|source| source.entity_id.as_ref() == entity_id)
    {
        source.effects = source
            .effects
            .iter()
            .filter(|effect| effect.trigger.as_ref() != trigger)
            .cloned()
            .collect::<Vec<_>>()
            .into();
    }
}

fn resolve_entity_once_trigger_outcomes(
    state: &GameState,
    owner_side: PlayerSide,
    entity_id: &str,
    trigger: &str,
) -> Result<Vec<WeightedState>, SolverError> {
    let Some(source) = player(state, owner_side)
        .board
        .iter()
        .find(|source| source.entity_id.as_ref() == entity_id && source.current_health > 0)
        .cloned()
    else {
        return Ok(vec![WeightedState {
            state: state.clone(),
            probability: ExactProbability::CERTAIN,
        }]);
    };
    if !source
        .effects
        .iter()
        .any(|effect| effect.trigger.as_ref() == trigger)
    {
        return Ok(vec![WeightedState {
            state: state.clone(),
            probability: ExactProbability::CERTAIN,
        }]);
    }
    let mut prepared = state.clone();
    consume_once_trigger(&mut prepared, owner_side, entity_id, trigger);
    apply_trigger_effects_outcomes(&prepared, owner_side, &source, trigger, "")
}

fn resolve_board_trigger_outcomes(
    state: &GameState,
    owner_side: PlayerSide,
    trigger: &str,
) -> Result<Vec<WeightedState>, SolverError> {
    let queued = {
        let owner = player(state, owner_side);
        owner
            .board
            .iter()
            .chain(owner.weapon.iter())
            .filter(|source| source.current_health > 0 || source.card_type == CardType::Weapon)
            .flat_map(|source| {
                source
                    .effects
                    .iter()
                    .filter(move |effect| effect.trigger.as_ref() == trigger)
                    .cloned()
                    .map(move |effect| (source.clone(), effect))
            })
            .collect::<Vec<_>>()
    };
    let mut prepared = state.clone();
    if trigger == "spellburst" {
        let mut consumed_sources = Vec::<Arc<str>>::new();
        for (source, _) in &queued {
            if consumed_sources
                .iter()
                .any(|entity_id| entity_id == &source.entity_id)
            {
                continue;
            }
            consumed_sources.push(Arc::clone(&source.entity_id));
        }
        for entity_id in consumed_sources {
            consume_once_trigger(&mut prepared, owner_side, &entity_id, "spellburst");
        }
    }
    let mut outcomes = vec![WeightedState {
        state: prepared,
        probability: ExactProbability::CERTAIN,
    }];
    for (source, effect) in queued {
        let mut single_effect_source = source;
        single_effect_source.effects = vec![effect].into();
        let mut expanded = Vec::<WeightedState>::new();
        for outcome in outcomes {
            for child in apply_trigger_effects_outcomes(
                &outcome.state,
                owner_side,
                &single_effect_source,
                trigger,
                "",
            )? {
                expanded.push(WeightedState {
                    state: child.state,
                    probability: outcome.probability.multiply(child.probability)?,
                });
            }
        }
        outcomes = merge_weighted_states(expanded)?;
    }
    Ok(outcomes)
}

pub(crate) fn resolve_active_board_trigger(
    state: &mut GameState,
    trigger: &str,
) -> Result<(), SolverError> {
    let active_side = side_for_player(state, &state.active_player_id)?;
    resolve_board_trigger(state, active_side, trigger)
}

fn add_generated_deck_spell(owner: &mut PlayerState, card_id: &str, name: &str, count: u16) {
    if count == 0 {
        return;
    }
    if let Some(known) = owner.known_deck.iter_mut().find(|known| {
        known.card_id.as_ref() == card_id && known.origin == DeckCardOrigin::Generated
    }) {
        known.count = known.count.saturating_add(count);
    } else {
        owner.known_deck.push(KnownDeckCard {
            card_id: Arc::from(card_id),
            count,
            origin: DeckCardOrigin::Generated,
            card_type: CardType::Spell,
            cost: 0,
            name: Arc::from(name),
        });
    }
    owner.deck_size = owner.deck_size.saturating_add(count);
}

fn remove_known_deck_card(owner: &mut PlayerState, card_id: &str) -> Option<KnownDeckCard> {
    let index = owner
        .known_deck
        .iter()
        .position(|known| known.card_id.as_ref() == card_id && known.count > 0)?;
    let mut drawn = owner.known_deck[index].clone();
    drawn.count = 1;
    owner.known_deck[index].count -= 1;
    if owner.known_deck[index].count == 0 {
        owner.known_deck.remove(index);
    }
    owner.deck_size = owner.deck_size.saturating_sub(1);
    Some(drawn)
}

fn state_contains_entity_id(state: &GameState, entity_id: &str) -> bool {
    [&state.friendly, &state.opponent].into_iter().any(|owner| {
        std::iter::once(&owner.hero)
            .chain(owner.hand.iter())
            .chain(owner.board.iter())
            .chain(owner.graveyard.iter())
            .chain(owner.hero_power.iter())
            .chain(owner.weapon.iter())
            .any(|card| card.entity_id.as_ref() == entity_id)
    })
}

fn draw_unknown_card(state: &mut GameState, actor_side: PlayerSide, source: &Card, ordinal: u16) {
    if player(state, actor_side).deck_size == 0 {
        let fatigue = {
            let actor = player_mut(state, actor_side);
            actor.fatigue = actor.fatigue.saturating_add(1);
            actor.fatigue
        };
        damage(state, CharacterLocation::Hero(actor_side), fatigue);
        return;
    }

    let hand_is_full = {
        let actor = player_mut(state, actor_side);
        actor.deck_size = actor.deck_size.saturating_sub(1);
        // The public snapshot knows how many cards remain, but not which member
        // of the known-deck multiset was drawn.  Drop completeness rather than
        // inventing a deterministic identity that later deck effects could use.
        actor.deck_identity_complete = false;
        actor.known_deck.clear();
        actor.hand.len() >= 10
    };
    if hand_is_full {
        return;
    }

    let mut sequence = ordinal;
    let entity_id = loop {
        let candidate = format!(
            "unknown-draw-{}-{}-{sequence}",
            source.entity_id, state.turn
        );
        if !state_contains_entity_id(state, &candidate) {
            break candidate;
        }
        sequence = sequence.saturating_add(1);
    };
    player_mut(state, actor_side)
        .hand
        .push(Card::unknown_drawn_card(entity_id));
}

fn apply_draw_effect(
    state: &mut GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
) -> Result<(), SolverError> {
    if effect.target.as_ref() != "none"
        || effect.amount != 0
        || !(1..=10).contains(&effect.count)
        || !effect.card_id.is_empty()
        || effect.attack != 0
        || effect.has_summoned_minion_keywords()
    {
        return Err(SolverError::Unsupported(
            "draw effect has invalid generic fields".to_owned(),
        ));
    }
    for ordinal in 0..effect.count {
        draw_unknown_card(state, actor_side, source, ordinal);
    }
    Ok(())
}

fn draw_non_starting_spell(state: &mut GameState, actor_side: PlayerSide) {
    let selected = {
        let owner = player(state, actor_side);
        owner
            .known_deck
            .iter()
            .filter(|known| {
                known.count > 0
                    && known.origin == DeckCardOrigin::Generated
                    && known.card_type == CardType::Spell
            })
            .min_by(|left, right| left.card_id.cmp(&right.card_id))
            .map(|known| known.card_id.to_string())
    };
    let Some(card_id) = selected else {
        return;
    };
    let turn = state.turn;
    let owner = player_mut(state, actor_side);
    let Some(drawn) = remove_known_deck_card(owner, &card_id) else {
        return;
    };
    if owner.hand.len() < 10 {
        let entity_id = format!(
            "drawn-generated-{}-{turn}-{}",
            drawn.card_id,
            owner.hand.len()
        );
        owner
            .hand
            .push(Card::drawn_from_known_deck(entity_id, &drawn));
    }
}

fn resolve_broken_weapon(
    state: &mut GameState,
    actor_side: PlayerSide,
    weapon: Card,
) -> Result<(), SolverError> {
    if weapon
        .effects
        .iter()
        .any(|effect| effect.kind.as_ref() == "draw_non_starting_spell_on_weapon_break")
    {
        draw_non_starting_spell(state, actor_side);
    }
    player_mut(state, actor_side).graveyard.push(weapon.clone());
    for effect in weapon
        .effects
        .iter()
        .filter(|effect| effect.trigger.as_ref() == "deathrattle")
    {
        if effect.random {
            return Err(SolverError::Unsupported(
                "random weapon Deathrattle requires chance-aware event resolution".to_owned(),
            ));
        }
        apply_deterministic_effect(state, actor_side, &weapon, effect, "", 0)?;
    }
    remove_dead(state)
}

fn resolve_broken_weapon_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    weapon: &Card,
) -> Result<Vec<WeightedState>, SolverError> {
    let mut prepared = state.clone();
    if weapon
        .effects
        .iter()
        .any(|effect| effect.kind.as_ref() == "draw_non_starting_spell_on_weapon_break")
    {
        draw_non_starting_spell(&mut prepared, actor_side);
    }
    player_mut(&mut prepared, actor_side)
        .graveyard
        .push(weapon.clone());
    apply_trigger_effects_outcomes(&prepared, actor_side, weapon, "deathrattle", "")
}

fn gain_hero_attack(state: &mut GameState, actor_side: PlayerSide, amount: u16) {
    let actor = player_mut(state, actor_side);
    actor.hero.attack = actor.hero.attack.saturating_add(amount);
    let Some(attacks_used) = public_tag_value(&actor.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
        .map(|value| u8::try_from(value.max(0)).unwrap_or(u8::MAX))
    else {
        return;
    };
    let maximum = maximum_attacks_with_weapon(&actor.hero, actor.weapon.as_ref());
    actor.hero.attacks_remaining = maximum.saturating_sub(attacks_used);
    actor.hero.attacks_remaining_known = true;
    let weapon_allows_attack = actor.weapon.as_ref().is_none_or(|weapon| {
        weapon.card_type == CardType::Weapon
            && weapon.current_durability > 0
            && !public_attack_is_blocked(&actor.hero, Some(weapon))
    });
    actor.hero.can_attack = actor.hero.attack > 0
        && actor.hero.current_health > 0
        && actor.hero.attacks_remaining > 0
        && !actor.hero.frozen
        && !actor.hero.dormant
        && !public_tag_is_active(&actor.hero, &["EXHAUSTED"], 43)
        && !public_attack_is_blocked(&actor.hero, actor.weapon.as_ref())
        && weapon_allows_attack;
}

fn resolve_surviving_frenzy_after_damage(
    state: &mut GameState,
    location: CharacterLocation,
    outcome: DamageOutcome,
) -> Result<(), SolverError> {
    if outcome.dealt == 0 {
        return Ok(());
    }
    let CharacterLocation::Board(owner_side, _) = location else {
        return Ok(());
    };
    if character(state, location).current_health == 0 {
        return Ok(());
    }
    let entity_id = character(state, location).entity_id.to_string();
    resolve_entity_once_trigger(state, owner_side, &entity_id, "frenzy")
}

fn resolve_surviving_frenzy_after_damage_outcomes(
    state: &GameState,
    location: CharacterLocation,
    outcome: DamageOutcome,
) -> Result<Vec<WeightedState>, SolverError> {
    if outcome.dealt == 0 {
        return Ok(vec![WeightedState {
            state: state.clone(),
            probability: ExactProbability::CERTAIN,
        }]);
    }
    let CharacterLocation::Board(owner_side, _) = location else {
        return Ok(vec![WeightedState {
            state: state.clone(),
            probability: ExactProbability::CERTAIN,
        }]);
    };
    if character(state, location).current_health == 0 {
        return Ok(vec![WeightedState {
            state: state.clone(),
            probability: ExactProbability::CERTAIN,
        }]);
    }
    let entity_id = character(state, location).entity_id.to_string();
    resolve_entity_once_trigger_outcomes(state, owner_side, &entity_id, "frenzy")
}

fn apply_character_effect_without_triggers(
    state: &mut GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
    location: CharacterLocation,
    spell_power: u16,
) -> Result<Option<DamageOutcome>, SolverError> {
    let base_amount = u16::try_from(effect.amount).map_err(|_| {
        SolverError::Unsupported(format!("negative point effect on {}", source.entity_id))
    })?;
    let mut damage_outcome = None;
    match effect.kind.as_ref() {
        "damage" => {
            let outcome = damage(state, location, base_amount.saturating_add(spell_power));
            if source.card_type == CardType::Minion
                && source.poisonous
                && outcome.dealt > 0
                && matches!(location, CharacterLocation::Board(_, _))
            {
                character_mut(state, location).current_health = 0;
            }
            if source.lifesteal && outcome.dealt > 0 {
                let simultaneous_overkill = if matches!(location, CharacterLocation::Hero(side) if side == actor_side)
                {
                    outcome.hero_overkill
                } else {
                    0
                };
                heal_hero(
                    state,
                    actor_side,
                    outcome.dealt.saturating_sub(simultaneous_overkill),
                );
            }
            damage_outcome = Some(outcome);
        }
        "heal" => {
            let target = character_mut(state, location);
            target.current_health = target
                .current_health
                .saturating_add(base_amount)
                .min(target.health);
        }
        "freeze" => {
            let target = character_mut(state, location);
            if !matches!(target.card_type, CardType::Hero | CardType::Minion) {
                return Err(SolverError::Unsupported(
                    "Freeze target is not a character".to_owned(),
                ));
            }
            target.frozen = true;
            target.can_attack = false;
        }
        "buff_attack" => {
            let target = character_mut(state, location);
            if target.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(
                    "Attack buff target is not a minion".to_owned(),
                ));
            }
            target.attack = target.attack.saturating_add(base_amount);
        }
        "buff_health" => {
            let target = character_mut(state, location);
            if target.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(
                    "Health buff target is not a minion".to_owned(),
                ));
            }
            target.health = target.health.saturating_add(base_amount);
            target.current_health = target.current_health.saturating_add(base_amount);
        }
        "set_attack" => {
            let target = character_mut(state, location);
            if target.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(
                    "Attack setter target is not a minion".to_owned(),
                ));
            }
            target.attack = base_amount;
            if base_amount == 0 {
                target.can_attack = false;
                target.attacks_remaining = 0;
            }
            set_public_tag_value(target, "ATK", 47, i64::from(base_amount));
        }
        "set_health" => {
            if base_amount == 0 {
                return Err(SolverError::Unsupported(
                    "Health setter requires a positive value".to_owned(),
                ));
            }
            let target = character_mut(state, location);
            if target.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(
                    "Health setter target is not a minion".to_owned(),
                ));
            }
            target.health = base_amount;
            target.current_health = base_amount;
            set_public_tag_value(target, "HEALTH", 45, i64::from(base_amount));
            set_public_tag_value(target, "DAMAGE", 44, 0);
        }
        "destroy" => {
            let target = character_mut(state, location);
            if target.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(
                    "Destroy target is not a minion".to_owned(),
                ));
            }
            target.current_health = 0;
        }
        "transform" => {
            let target = character_mut(state, location);
            if target.card_type != CardType::Minion {
                return Err(SolverError::Unsupported(
                    "Transform target is not a minion".to_owned(),
                ));
            }
            target.transform_into_token(effect);
        }
        "grant_keywords" => {
            let target = character_mut(state, location);
            if !matches!(target.card_type, CardType::Hero | CardType::Minion) {
                return Err(SolverError::Unsupported(
                    "Keyword target is not a character".to_owned(),
                ));
            }
            let previous_maximum = maximum_attacks(target);
            let attacks_used = previous_maximum.saturating_sub(target.attacks_remaining);
            target.taunt |= effect.taunt;
            target.divine_shield |= effect.divine_shield;
            target.stealth |= effect.stealth;
            target.poisonous |= effect.poisonous;
            target.lifesteal |= effect.lifesteal;
            target.windfury |= effect.windfury;
            target.rush |= effect.rush;
            target.charge |= effect.charge;
            target.reborn |= effect.reborn;
            if target.card_type == CardType::Minion
                && target.attack > 0
                && target.summoned_this_turn
                && (target.rush || target.charge)
            {
                target.can_attack = true;
            }
            if target.can_attack {
                target.attacks_remaining = maximum_attacks(target).saturating_sub(attacks_used);
            }
        }
        _ => {
            return Err(SolverError::Unsupported(format!(
                "oracle cannot apply character effect {:?}",
                effect.kind
            )));
        }
    }
    Ok(damage_outcome)
}

fn apply_character_effect(
    state: &mut GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
    location: CharacterLocation,
    spell_power: u16,
) -> Result<(), SolverError> {
    if let Some(outcome) = apply_character_effect_without_triggers(
        state,
        actor_side,
        source,
        effect,
        location,
        spell_power,
    )? {
        resolve_surviving_frenzy_after_damage(state, location, outcome)?;
    }
    Ok(())
}

fn apply_character_effect_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
    location: CharacterLocation,
    spell_power: u16,
) -> Result<Vec<WeightedState>, SolverError> {
    let mut child = state.clone();
    let damage_outcome = apply_character_effect_without_triggers(
        &mut child,
        actor_side,
        source,
        effect,
        location,
        spell_power,
    )?;
    if let Some(outcome) = damage_outcome {
        resolve_surviving_frenzy_after_damage_outcomes(&child, location, outcome)
    } else {
        Ok(vec![WeightedState {
            state: child,
            probability: ExactProbability::CERTAIN,
        }])
    }
}

fn replay_target_id(state: &GameState, actor_side: PlayerSide, card: &Card) -> Option<String> {
    let mode = primary_target_mode(card);
    let enemy = player(state, other_side(actor_side));
    let actor = player(state, actor_side);
    let largest_enemy_minion = || {
        enemy
            .board
            .iter()
            .filter(|candidate| {
                candidate.card_type == CardType::Minion
                    && candidate.current_health > 0
                    && !candidate.stealth
                    && !candidate.dormant
            })
            .max_by_key(|candidate| {
                u32::from(candidate.attack) * 4 + u32::from(candidate.current_health)
            })
            .map(|candidate| candidate.entity_id.to_string())
    };
    let largest_friendly_minion = || {
        actor
            .board
            .iter()
            .filter(|candidate| {
                candidate.card_type == CardType::Minion
                    && candidate.current_health > 0
                    && !candidate.dormant
            })
            .max_by_key(|candidate| {
                u32::from(candidate.attack) * 4 + u32::from(candidate.current_health)
            })
            .map(|candidate| candidate.entity_id.to_string())
    };
    match mode {
        "none" | "self" => Some(String::new()),
        "enemy_hero" | "enemy_character" | "any_character" => {
            let prefers_minion = card.effects.iter().any(|effect| {
                matches!(
                    effect.kind.as_ref(),
                    "set_health" | "buff_attack" | "buff_health" | "freeze"
                )
            });
            if prefers_minion {
                largest_enemy_minion().or_else(|| Some(enemy.hero.entity_id.to_string()))
            } else {
                Some(enemy.hero.entity_id.to_string())
            }
        }
        "enemy_minion" | "any_minion" | "any_undamaged_minion" | "damaged_enemy_minion" => {
            largest_enemy_minion()
        }
        "friendly_hero" | "friendly_character" => Some(actor.hero.entity_id.to_string()),
        "friendly_minion" => largest_friendly_minion(),
        _ if automatic_target_mode(mode) => Some(String::new()),
        _ => None,
    }
}

fn replay_one_cost_cards(
    state: &mut GameState,
    actor_side: PlayerSide,
    replay_source: &Card,
) -> Result<(), SolverError> {
    let history = {
        let actor = player(state, actor_side);
        actor
            .graveyard
            .iter()
            .chain(actor.board.iter())
            .filter(|card| {
                card.cost == 1
                    && !matches!(
                        card.card_type,
                        CardType::Hero | CardType::HeroPower | CardType::Unknown
                    )
                    && !card.card_id.eq_ignore_ascii_case("UNKNOWN")
            })
            .cloned()
            .collect::<Vec<_>>()
    };
    for (ordinal, historical) in history.into_iter().enumerate() {
        let mut replayed = historical.clone();
        replayed.entity_id = Arc::from(format!(
            "replay-{}-{}-{ordinal}",
            replay_source.entity_id, historical.entity_id
        ));
        replayed.current_health = replayed.health;
        replayed.current_health_known = true;
        replayed.summoned_this_turn = replayed.card_type == CardType::Minion;
        replayed.attacks_remaining = 0;
        replayed.attacks_remaining_known = true;
        replayed.can_attack = false;
        match replayed.card_type {
            CardType::Minion => {
                if player(state, actor_side)
                    .board
                    .iter()
                    .filter(|card| occupies_board_slot(card))
                    .count()
                    >= 7
                {
                    continue;
                }
                if replayed.attack > 0 && (replayed.charge || replayed.rush) {
                    replayed.can_attack = true;
                    replayed.attacks_remaining = maximum_attacks(&replayed);
                }
                player_mut(state, actor_side).board.push(replayed.clone());
            }
            CardType::Location => {
                if player(state, actor_side)
                    .board
                    .iter()
                    .filter(|card| occupies_board_slot(card))
                    .count()
                    < 7
                {
                    player_mut(state, actor_side).board.push(replayed.clone());
                }
                continue;
            }
            CardType::Weapon => {
                if replayed.current_durability == 0 {
                    continue;
                }
                let previous = player_mut(state, actor_side).weapon.take();
                if let Some(previous) = previous {
                    let owner = player_mut(state, actor_side);
                    owner.hero.attack = owner.hero.attack.saturating_sub(previous.attack);
                    resolve_broken_weapon(state, actor_side, previous)?;
                }
                let owner = player_mut(state, actor_side);
                owner.hero.attack = owner.hero.attack.saturating_add(replayed.attack);
                owner.weapon = Some(replayed);
                continue;
            }
            CardType::Spell => {}
            CardType::Hero | CardType::HeroPower | CardType::Unknown => continue,
        }
        if replayed.effects.is_empty()
            || replayed.effect_coverage == EffectCoverage::Unsupported
            || replayed
                .effects
                .iter()
                .any(|effect| effect.trigger.as_ref() == "resolution" && effect.random)
        {
            continue;
        }
        let Some(target_id) = replay_target_id(state, actor_side, &replayed) else {
            continue;
        };
        if apply_effects(state, actor_side, &replayed, &target_id).is_err() {
            continue;
        }
        if replayed.card_type == CardType::Spell {
            player_mut(state, actor_side).graveyard.push(replayed);
        }
    }
    Ok(())
}

fn apply_deterministic_effect(
    state: &mut GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
    target_id: &str,
    spell_power: u16,
) -> Result<(), SolverError> {
    if effect.random {
        return Err(SolverError::Unsupported(format!(
            "deterministic transition received random effect {:?}",
            effect.kind
        )));
    }
    if matches!(
        effect.kind.as_ref(),
        "set_hero_power_cost" | "double_one_cost_cards"
    ) {
        return Ok(());
    }
    if effect.kind.as_ref() == "draw_non_starting_spell_on_weapon_break" {
        // Triggered only when the equipped weapon leaves play.
        return Ok(());
    }
    if effect.kind.as_ref() == "shuffle_repeat_spell" {
        if effect.target.as_ref() != "none" || effect.card_id.trim().is_empty() {
            return Err(SolverError::Unsupported(
                "repeat-spell shuffle has an invalid reviewed rule".to_owned(),
            ));
        }
        add_generated_deck_spell(
            player_mut(state, actor_side),
            effect.card_id.as_ref(),
            effect.name.as_ref(),
            effect.count,
        );
        return Ok(());
    }
    if effect.kind.as_ref() == "replay_one_cost_cards" {
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "one-cost replay unexpectedly requires a target".to_owned(),
            ));
        }
        return replay_one_cost_cards(state, actor_side, source);
    }
    if effect.kind.as_ref() == "draw" {
        return apply_draw_effect(state, actor_side, source, effect);
    }
    if effect.kind.as_ref() == "draw_opponent" {
        return apply_draw_effect(state, other_side(actor_side), source, effect);
    }
    if effect.kind.as_ref() == "draw_both_players" {
        apply_draw_effect(state, actor_side, source, effect)?;
        return apply_draw_effect(state, other_side(actor_side), source, effect);
    }
    if effect.kind.as_ref() == "draw_until_hand_count" {
        if effect.target.as_ref() != "none"
            || effect.amount != 0
            || !(1..=10).contains(&effect.count)
            || !effect.card_id.is_empty()
            || effect.attack != 0
            || effect.has_summoned_minion_keywords()
        {
            return Err(SolverError::Unsupported(
                "draw-until effect has invalid generic fields".to_owned(),
            ));
        }
        for ordinal in 0..effect.count {
            if player(state, actor_side).hand.len() >= usize::from(effect.count) {
                break;
            }
            draw_unknown_card(state, actor_side, source, ordinal);
        }
        return Ok(());
    }
    if effect.kind.as_ref() == "buff_weapon_attack" {
        if effect.target.as_ref() != "none" || effect.amount <= 0 {
            return Err(SolverError::Unsupported(
                "weapon Attack buff has invalid generic fields".to_owned(),
            ));
        }
        let amount = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported(format!(
                "invalid weapon Attack buff on {}",
                source.entity_id
            ))
        })?;
        if player(state, actor_side).weapon.is_none() && source.card_type == CardType::Minion {
            return Ok(());
        }
        let actor = player_mut(state, actor_side);
        let weapon = actor.weapon.as_mut().ok_or_else(|| {
            SolverError::IllegalAction("weapon Attack buff requires an equipped weapon".to_owned())
        })?;
        weapon.attack = weapon.attack.saturating_add(amount);
        actor.hero.attack = actor.hero.attack.saturating_add(amount);
        let hero_attack = actor.hero.attack;
        set_public_tag_value(&mut actor.hero, "ATK", 47, i64::from(hero_attack));
        return Ok(());
    }
    if source.card_type == CardType::Weapon
        && effect.target.as_ref() == "self"
        && effect.kind.as_ref() == "buff_attack"
    {
        let amount = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported(format!("negative weapon buff on {}", source.entity_id))
        })?;
        let actor = player_mut(state, actor_side);
        let weapon = actor
            .weapon
            .as_mut()
            .filter(|weapon| weapon.entity_id == source.entity_id)
            .ok_or_else(|| {
                SolverError::Unsupported("weapon trigger source is no longer equipped".to_owned())
            })?;
        weapon.attack = weapon.attack.saturating_add(amount);
        actor.hero.attack = actor.hero.attack.saturating_add(amount);
        let hero_attack = actor.hero.attack;
        set_public_tag_value(&mut actor.hero, "ATK", 47, i64::from(hero_attack));
        return Ok(());
    }
    if let Some(targets) = automatic_target_locations(
        state,
        actor_side,
        effect.target.as_ref(),
        source.entity_id.as_ref(),
    ) {
        if !matches!(
            effect.kind.as_ref(),
            "damage"
                | "heal"
                | "freeze"
                | "buff_attack"
                | "buff_health"
                | "set_attack"
                | "set_health"
                | "destroy"
                | "transform"
                | "grant_keywords"
        ) {
            return Err(SolverError::Unsupported(format!(
                "oracle cannot apply automatic-target effect {:?}",
                effect.kind
            )));
        }
        for target in targets {
            apply_character_effect(state, actor_side, source, effect, target, spell_power)?;
        }
        return Ok(());
    }
    if effect.kind.as_ref() == "damage_all_minions" {
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "all-minion damage unexpectedly requires a target".to_owned(),
            ));
        }
        let amount = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported(format!(
                "negative all-minion damage on {}",
                source.entity_id
            ))
        })?;
        let amount = amount.saturating_add(spell_power);
        let targets = [PlayerSide::Friendly, PlayerSide::Opponent]
            .into_iter()
            .flat_map(|side| {
                player(state, side)
                    .board
                    .iter()
                    .enumerate()
                    .filter(|(_, card)| {
                        card.card_type == CardType::Minion
                            && card.current_health > 0
                            && !card.dormant
                    })
                    .map(move |(index, _)| CharacterLocation::Board(side, index))
            })
            .collect::<Vec<_>>();
        for target in targets {
            damage(state, target, amount);
        }
        return Ok(());
    }
    if matches!(effect.kind.as_ref(), "armor" | "gain_hero_attack") {
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "owner effect unexpectedly requires a target".to_owned(),
            ));
        }
        let amount = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported(format!("negative owner effect on {}", source.entity_id))
        })?;
        if effect.kind.as_ref() == "armor" {
            let actor = player_mut(state, actor_side);
            actor.armor = actor.armor.saturating_add(amount);
        } else {
            gain_hero_attack(state, actor_side, amount);
        }
        return Ok(());
    }
    if effect.kind.as_ref() == "gain_mana" {
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "mana effect unexpectedly requires a target".to_owned(),
            ));
        }
        let amount = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported(format!("negative mana effect on {}", source.entity_id))
        })?;
        let actor = player_mut(state, actor_side);
        // Temporary mana (for example The Coin) is allowed to exceed the
        // permanent crystal count for the current turn. Turn advancement
        // restores mana from max_mana, so the excess naturally expires.
        actor.mana = actor.mana.saturating_add(amount);
        return Ok(());
    }
    if matches!(
        effect.kind.as_ref(),
        "refresh_mana" | "gain_mana_crystals" | "gain_empty_mana_crystals"
    ) {
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "mana-crystal effect unexpectedly requires a target".to_owned(),
            ));
        }
        let amount = u16::try_from(effect.amount).map_err(|_| {
            SolverError::Unsupported(format!(
                "negative mana-crystal effect on {}",
                source.entity_id
            ))
        })?;
        let actor = player_mut(state, actor_side);
        match effect.kind.as_ref() {
            "refresh_mana" => {
                actor.mana = actor.mana.saturating_add(amount).min(actor.max_mana);
            }
            "gain_mana_crystals" => {
                let previous = actor.max_mana;
                actor.max_mana = actor.max_mana.saturating_add(amount).min(10);
                actor.mana = actor
                    .mana
                    .saturating_add(actor.max_mana.saturating_sub(previous));
            }
            "gain_empty_mana_crystals" => {
                actor.max_mana = actor.max_mana.saturating_add(amount).min(10);
            }
            _ => unreachable!("matched mana-crystal effect"),
        }
        return Ok(());
    }
    if effect.kind.as_ref() == "destroy_all_minions_and_locations" {
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "board destroy unexpectedly requires a target".to_owned(),
            ));
        }
        for side in [PlayerSide::Friendly, PlayerSide::Opponent] {
            let owner = player_mut(state, side);
            for card in &mut owner.board {
                if card.card_type == CardType::Minion {
                    card.current_health = 0;
                }
            }
            let mut surviving = Vec::with_capacity(owner.board.len());
            for card in owner.board.drain(..) {
                if card.card_type == CardType::Location {
                    owner.graveyard.push(card);
                } else {
                    surviving.push(card);
                }
            }
            owner.board = surviving;
        }
        return remove_dead(state);
    }
    if effect.kind.as_ref() == "equip_weapon" {
        if effect.target.as_ref() != "none"
            || effect.attack == 0
            || effect.durability == 0
            || effect.card_id.trim().is_empty()
        {
            return Err(SolverError::Unsupported(
                "generated weapon has invalid generic fields".to_owned(),
            ));
        }
        let previous_maximum = {
            let actor = player(state, actor_side);
            maximum_attacks_with_weapon(&actor.hero, actor.weapon.as_ref())
        };
        let inferred_attacks_used = {
            let actor = player(state, actor_side);
            public_tag_value(&actor.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
                .map(|value| u8::try_from(value.max(0)).unwrap_or(u8::MAX))
                .or_else(|| {
                    actor
                        .hero
                        .attacks_remaining_known
                        .then_some(previous_maximum.saturating_sub(actor.hero.attacks_remaining))
                })
        };
        if let Some(previous) = player_mut(state, actor_side).weapon.take() {
            player_mut(state, actor_side).hero.attack = player(state, actor_side)
                .hero
                .attack
                .saturating_sub(previous.attack);
            resolve_broken_weapon(state, actor_side, previous)?;
        }
        if let Some(intervening) = player_mut(state, actor_side).weapon.take() {
            player_mut(state, actor_side).hero.attack = player(state, actor_side)
                .hero
                .attack
                .saturating_sub(intervening.attack);
            resolve_broken_weapon(state, actor_side, intervening)?;
        }
        let entity_id = format!("generated-weapon-{}-{}", source.entity_id, state.turn);
        let weapon = Card::generated_weapon(entity_id, effect);
        let actor_is_active = side_for_player(state, &state.active_player_id)? == actor_side;
        let actor = player_mut(state, actor_side);
        actor.hero.attack = actor.hero.attack.saturating_add(weapon.attack);
        actor.weapon = Some(weapon);
        let maximum = maximum_attacks_with_weapon(&actor.hero, actor.weapon.as_ref());
        actor.hero.attacks_remaining =
            inferred_attacks_used.map_or(0, |used| maximum.saturating_sub(used));
        actor.hero.attacks_remaining_known = inferred_attacks_used.is_some();
        actor.hero.can_attack = actor_is_active
            && actor.hero.attack > 0
            && actor.hero.current_health > 0
            && actor.hero.attacks_remaining > 0
            && !actor.hero.frozen
            && !actor.hero.dormant
            && !public_attack_is_blocked(&actor.hero, actor.weapon.as_ref());
        let hero_attack = actor.hero.attack;
        set_public_tag_value(&mut actor.hero, "ATK", 47, i64::from(hero_attack));
        return Ok(());
    }
    if effect.kind.as_ref() == "summon" {
        remove_dead(state)?;
        if effect.target.as_ref() != "none" {
            return Err(SolverError::Unsupported(
                "summon effect unexpectedly requires a target".to_owned(),
            ));
        }
        let turn = state.turn;
        for ordinal in 0..effect.count {
            let actor = player_mut(state, actor_side);
            if actor
                .board
                .iter()
                .filter(|card| occupies_board_slot(card))
                .count()
                >= 7
            {
                break;
            }
            let entity_id = format!(
                "generated-{}-{turn}-{}-{ordinal}",
                source.entity_id,
                actor.board.len()
            );
            actor.board.push(Card::summoned_minion(entity_id, effect));
        }
        return Ok(());
    }
    if !matches!(
        effect.kind.as_ref(),
        "damage"
            | "heal"
            | "freeze"
            | "buff_attack"
            | "buff_health"
            | "set_attack"
            | "set_health"
            | "destroy"
            | "transform"
            | "grant_keywords"
    ) {
        return Err(SolverError::Unsupported(format!(
            "oracle cannot apply effect {:?}",
            effect.kind
        )));
    }
    if effect.target.as_ref() == "none" {
        return Ok(());
    }
    let resolved_target = match effect.target.as_ref() {
        "self" => source.entity_id.to_string(),
        "enemy_hero" => player(state, other_side(actor_side))
            .hero
            .entity_id
            .to_string(),
        "friendly_hero" => player(state, actor_side).hero.entity_id.to_string(),
        _ => target_id.to_owned(),
    };
    if !target_id.is_empty()
        && matches!(effect.target.as_ref(), "enemy_hero" | "friendly_hero")
        && primary_target_mode(source) == effect.target.as_ref()
        && target_id != resolved_target
    {
        return Err(SolverError::IllegalAction(
            "fixed hero target does not match the reviewed card rule".to_owned(),
        ));
    }
    let location = find_character(state, &resolved_target).ok_or_else(|| {
        SolverError::IllegalAction(format!("oracle target no longer exists: {resolved_target}"))
    })?;
    apply_character_effect(state, actor_side, source, effect, location, spell_power)
}

fn apply_deterministic_effect_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
    target_id: &str,
    spell_power: u16,
) -> Result<Vec<WeightedState>, SolverError> {
    if effect.random {
        return Err(SolverError::Unsupported(format!(
            "chance transition received random effect {:?} as deterministic",
            effect.kind
        )));
    }
    if effect.kind.as_ref() != "damage"
        || automatic_target_locations(
            state,
            actor_side,
            effect.target.as_ref(),
            source.entity_id.as_ref(),
        )
        .is_some()
        || effect.target.as_ref() == "none"
    {
        let mut child = state.clone();
        apply_deterministic_effect(
            &mut child,
            actor_side,
            source,
            effect,
            target_id,
            spell_power,
        )?;
        return Ok(vec![WeightedState {
            state: child,
            probability: ExactProbability::CERTAIN,
        }]);
    }
    let resolved_target = match effect.target.as_ref() {
        "self" => source.entity_id.to_string(),
        "enemy_hero" => player(state, other_side(actor_side))
            .hero
            .entity_id
            .to_string(),
        "friendly_hero" => player(state, actor_side).hero.entity_id.to_string(),
        _ => target_id.to_owned(),
    };
    if !target_id.is_empty()
        && matches!(effect.target.as_ref(), "enemy_hero" | "friendly_hero")
        && primary_target_mode(source) == effect.target.as_ref()
        && target_id != resolved_target
    {
        return Err(SolverError::IllegalAction(
            "fixed hero target does not match the reviewed card rule".to_owned(),
        ));
    }
    let location = find_character(state, &resolved_target).ok_or_else(|| {
        SolverError::IllegalAction(format!("oracle target no longer exists: {resolved_target}"))
    })?;
    apply_character_effect_outcomes(state, actor_side, source, effect, location, spell_power)
}

fn random_effect_target_locations(
    state: &GameState,
    actor_side: PlayerSide,
    mode: &str,
) -> Result<Vec<CharacterLocation>, SolverError> {
    fn living_minions(state: &GameState, side: PlayerSide) -> Vec<CharacterLocation> {
        player(state, side)
            .board
            .iter()
            .enumerate()
            .filter(|(_, card)| {
                card.card_type == CardType::Minion && card.current_health > 0 && !card.dormant
            })
            .map(|(index, _)| CharacterLocation::Board(side, index))
            .collect()
    }

    let enemy_side = other_side(actor_side);
    let mut targets = match mode {
        "enemy_character" => {
            let mut values = vec![CharacterLocation::Hero(enemy_side)];
            values.extend(living_minions(state, enemy_side));
            values
        }
        "friendly_character" => {
            let mut values = vec![CharacterLocation::Hero(actor_side)];
            values.extend(living_minions(state, actor_side));
            values
        }
        "any_character" => {
            let mut values = vec![
                CharacterLocation::Hero(actor_side),
                CharacterLocation::Hero(enemy_side),
            ];
            values.extend(living_minions(state, actor_side));
            values.extend(living_minions(state, enemy_side));
            values
        }
        "enemy_minion" => living_minions(state, enemy_side),
        "friendly_minion" => living_minions(state, actor_side),
        "any_minion" => {
            let mut values = living_minions(state, actor_side);
            values.extend(living_minions(state, enemy_side));
            values
        }
        "any_undamaged_minion" => {
            let mut values = living_minions(state, actor_side);
            values.extend(living_minions(state, enemy_side));
            values.retain(|location| {
                let card = character(state, *location);
                card.current_health_known && card.current_health == card.health
            });
            values
        }
        "damaged_enemy_minion" => {
            let mut values = living_minions(state, enemy_side);
            values.retain(|location| {
                let card = character(state, *location);
                card.current_health_known && card.current_health < card.health
            });
            values
        }
        "enemy_hero" => vec![CharacterLocation::Hero(enemy_side)],
        "friendly_hero" => vec![CharacterLocation::Hero(actor_side)],
        "all_enemy_characters" => {
            let mut values = vec![CharacterLocation::Hero(enemy_side)];
            values.extend(living_minions(state, enemy_side));
            values
        }
        "all_enemy_minions" => living_minions(state, enemy_side),
        "all_friendly_characters" => {
            let mut values = vec![CharacterLocation::Hero(actor_side)];
            values.extend(living_minions(state, actor_side));
            values
        }
        "all_friendly_minions" => living_minions(state, actor_side),
        _ => {
            return Err(SolverError::Unsupported(format!(
                "random effect target mode {mode:?} is unsupported"
            )));
        }
    };
    targets.retain(|location| character(state, *location).current_health > 0);
    targets.sort_by(|left, right| {
        character(state, *left)
            .entity_id
            .cmp(&character(state, *right).entity_id)
    });
    Ok(targets)
}

fn merge_weighted_states(values: Vec<WeightedState>) -> Result<Vec<WeightedState>, SolverError> {
    let mut merged: Vec<(StateKey, WeightedState)> = Vec::new();
    for value in values {
        let key = StateKey::from_state(&value.state);
        if let Some((_, existing)) = merged
            .iter_mut()
            .find(|(existing_key, _)| *existing_key == key)
        {
            existing.probability = existing.probability.add(value.probability)?;
        } else {
            merged.push((key, value));
        }
    }
    Ok(merged.into_iter().map(|(_, value)| value).collect())
}

fn pool_card_choice_score(candidate: &ResolvedPoolCandidate, available_mana: u16) -> i64 {
    let card = &candidate.card;
    let rarity = i64::from(card.rarity_id);
    let keywords = i64::try_from(card.keywords.len()).unwrap_or(i64::MAX);
    let playable_bonus = if card.cost <= available_mana { 35 } else { 0 };
    match card.card_type {
        CardType::Minion => {
            i64::from(card.attack) * 20
                + i64::from(card.health) * 13
                + rarity * 5
                + keywords * 8
                + playable_bonus
                - i64::from(card.cost) * 3
        }
        CardType::Weapon => {
            i64::from(card.attack) * i64::from(card.durability.max(1)) * 12
                + rarity * 5
                + keywords * 8
                + playable_bonus
        }
        CardType::Spell | CardType::Location | CardType::Hero => {
            i64::from(card.cost.min(10)) * 7 + rarity * 6 + keywords * 8 + playable_bonus
        }
        CardType::HeroPower | CardType::Unknown => rarity * 4 + keywords * 6 + playable_bonus,
    }
}

fn better_discover_candidate(
    left: &ResolvedPoolCandidate,
    right: &ResolvedPoolCandidate,
    available_mana: u16,
) -> bool {
    pool_card_choice_score(left, available_mana) > pool_card_choice_score(right, available_mana)
        || (pool_card_choice_score(left, available_mana)
            == pool_card_choice_score(right, available_mana)
            && left.card.card_id < right.card.card_id)
}

fn exact_discover_winner_counts(
    candidates: &[ResolvedPoolCandidate],
    offer_count: usize,
    available_mana: u16,
) -> Vec<(usize, u64)> {
    fn visit(
        candidates: &[ResolvedPoolCandidate],
        offer_count: usize,
        available_mana: u16,
        start: usize,
        chosen: &mut Vec<usize>,
        counts: &mut HashMap<usize, u64>,
    ) {
        if chosen.len() == offer_count {
            let winner = chosen
                .iter()
                .copied()
                .reduce(|left, right| {
                    if better_discover_candidate(
                        &candidates[right],
                        &candidates[left],
                        available_mana,
                    ) {
                        right
                    } else {
                        left
                    }
                })
                .expect("Discover offer is non-empty");
            *counts.entry(winner).or_default() =
                counts.get(&winner).copied().unwrap_or(0).saturating_add(1);
            return;
        }
        let remaining = offer_count - chosen.len();
        let last_start = candidates.len().saturating_sub(remaining);
        for index in start..=last_start {
            chosen.push(index);
            visit(
                candidates,
                offer_count,
                available_mana,
                index + 1,
                chosen,
                counts,
            );
            chosen.pop();
        }
    }

    let mut counts = HashMap::new();
    visit(
        candidates,
        offer_count,
        available_mana,
        0,
        &mut Vec::with_capacity(offer_count),
        &mut counts,
    );
    let mut values = counts.into_iter().collect::<Vec<_>>();
    values.sort_by_key(|(index, _)| *index);
    values
}

fn deterministic_discover_position(
    state: &GameState,
    source: &Card,
    sample: usize,
    slot: usize,
    modulus: usize,
) -> usize {
    let mut digest = Sha256::new();
    digest.update(state.rng_seed.to_le_bytes());
    digest.update(state.turn.to_le_bytes());
    digest.update(source.card_id.as_bytes());
    digest.update(source.entity_id.as_bytes());
    digest.update(sample.to_le_bytes());
    digest.update(slot.to_le_bytes());
    let bytes = digest.finalize();
    let value = u64::from_le_bytes(bytes[..8].try_into().expect("SHA-256 prefix"));
    usize::try_from(value % u64::try_from(modulus).unwrap_or(u64::MAX)).unwrap_or(0)
}

fn sampled_discover_winner_counts(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    candidates: &[ResolvedPoolCandidate],
    offer_count: usize,
) -> Vec<(usize, u64)> {
    let mut population = Vec::new();
    for (index, candidate) in candidates.iter().enumerate() {
        population.extend(std::iter::repeat_n(
            index,
            usize::try_from(candidate.weight).unwrap_or(usize::MAX),
        ));
    }
    let sample_count = population.len().saturating_mul(2).clamp(64, 256);
    let available_mana = player(state, actor_side).mana;
    let mut counts = HashMap::<usize, u64>::new();
    for sample in 0..sample_count {
        let mut bag = population.clone();
        let mut offer = Vec::with_capacity(offer_count);
        for slot in 0..offer_count {
            if bag.is_empty() {
                break;
            }
            let position = deterministic_discover_position(state, source, sample, slot, bag.len());
            let candidate_index = bag[position];
            offer.push(candidate_index);
            bag.retain(|value| *value != candidate_index);
        }
        let winner = offer.into_iter().reduce(|left, right| {
            if better_discover_candidate(&candidates[right], &candidates[left], available_mana) {
                right
            } else {
                left
            }
        });
        if let Some(winner) = winner {
            *counts.entry(winner).or_default() =
                counts.get(&winner).copied().unwrap_or(0).saturating_add(1);
        }
    }
    let mut values = counts.into_iter().collect::<Vec<_>>();
    values.sort_by_key(|(index, _)| *index);
    values
}

fn selected_pool_branches(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
    candidates: &[ResolvedPoolCandidate],
) -> Result<Vec<(usize, ExactProbability)>, SolverError> {
    if candidates.is_empty() {
        return Err(SolverError::Unsupported(
            "resolved card pool contains no candidates".to_owned(),
        ));
    }
    match effect.pool_selection {
        PoolSelection::UniformRandom => {
            let total = candidates.iter().try_fold(0u64, |sum, candidate| {
                sum.checked_add(u64::from(candidate.weight)).ok_or_else(|| {
                    SolverError::Unsupported("card-pool branch weight overflow".to_owned())
                })
            })?;
            candidates
                .iter()
                .enumerate()
                .map(|(index, candidate)| {
                    Ok((
                        index,
                        ExactProbability::new(u64::from(candidate.weight), total)?,
                    ))
                })
                .collect()
        }
        PoolSelection::Discover => {
            let offer_count = usize::from(effect.offer_count).min(candidates.len());
            let exact = effect.resolved_pool_exact
                && candidates.len() <= 18
                && candidates.iter().all(|candidate| candidate.weight == 1);
            let winners = if exact {
                exact_discover_winner_counts(
                    candidates,
                    offer_count,
                    player(state, actor_side).mana,
                )
            } else {
                sampled_discover_winner_counts(state, actor_side, source, candidates, offer_count)
            };
            let total = winners.iter().try_fold(0u64, |sum, (_, weight)| {
                sum.checked_add(*weight).ok_or_else(|| {
                    SolverError::Unsupported("Discover branch weight overflow".to_owned())
                })
            })?;
            winners
                .into_iter()
                .map(|(index, weight)| Ok((index, ExactProbability::new(weight, total)?)))
                .collect()
        }
        PoolSelection::None => Err(SolverError::Unsupported(
            "card-pool effect has no selection semantics".to_owned(),
        )),
    }
}

fn generated_pool_entity_prefix(source: &Card, turn: u32) -> String {
    format!("generated-pool-{}-{turn}-", source.entity_id)
}

fn apply_pool_effect_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    effect: &Effect,
) -> Result<Vec<WeightedState>, SolverError> {
    if effect.pool.is_none() {
        return Err(SolverError::Unsupported(format!(
            "card-pool effect on {} was not resolved from the official registry",
            source.entity_id
        )));
    }
    let takes_from_owner_deck = effect.pool.as_ref().is_some_and(|pool| {
        pool.source == CardPoolSource::OwnerDeck && effect.pool_destination == PoolDestination::Hand
    });
    let draws_from_owner_deck = effect.kind.as_ref() == "draw_from_pool" && takes_from_owner_deck;
    if effect.resolved_pool.is_empty() {
        if draws_from_owner_deck
            && effect.resolved_pool_exact
            && effect.resolved_pool_population == 0
        {
            return Ok(vec![WeightedState {
                state: state.clone(),
                probability: ExactProbability::CERTAIN,
            }]);
        }
        return Err(SolverError::Unsupported(format!(
            "card-pool effect on {} has no resolved candidates",
            source.entity_id
        )));
    }
    if effect.pool_destination == PoolDestination::Cast {
        return Err(SolverError::Unsupported(
            "cast card-pool effects require a nested rule engine".to_owned(),
        ));
    }
    let prefix = generated_pool_entity_prefix(source, state.turn);
    let mut outcomes = vec![WeightedState {
        state: state.clone(),
        probability: ExactProbability::CERTAIN,
    }];
    for ordinal in 0..effect.count {
        let mut expanded = Vec::new();
        for outcome in outcomes {
            let used_card_ids = if effect.with_replacement || takes_from_owner_deck {
                Vec::new()
            } else {
                let actor = player(&outcome.state, actor_side);
                actor
                    .hand
                    .iter()
                    .chain(actor.board.iter())
                    .filter(|card| card.entity_id.starts_with(&prefix))
                    .map(|card| card.card_id.to_string())
                    .collect::<Vec<_>>()
            };
            let candidates = if takes_from_owner_deck {
                let actor = player(&outcome.state, actor_side);
                effect
                    .resolved_pool
                    .iter()
                    .filter_map(|candidate| {
                        let remaining = actor
                            .known_deck
                            .iter()
                            .find(|known| {
                                known.card_id.eq_ignore_ascii_case(&candidate.card.card_id)
                            })
                            .map_or(0, |known| u32::from(known.count));
                        (remaining > 0).then(|| {
                            let mut candidate = candidate.clone();
                            candidate.weight = remaining;
                            candidate
                        })
                    })
                    .collect::<Vec<_>>()
            } else {
                effect
                    .resolved_pool
                    .iter()
                    .filter(|candidate| {
                        !used_card_ids
                            .iter()
                            .any(|card_id| candidate.card.card_id.as_ref() == card_id)
                    })
                    .cloned()
                    .collect::<Vec<_>>()
            };
            if candidates.is_empty() {
                expanded.push(outcome);
                continue;
            }
            let branches =
                selected_pool_branches(&outcome.state, actor_side, source, effect, &candidates)?;
            for (candidate_index, probability) in branches {
                let candidate = &candidates[candidate_index];
                let mut child = outcome.state.clone();
                let actor = player_mut(&mut child, actor_side);
                let entity_id = format!(
                    "{prefix}{ordinal}-{}-{}",
                    candidate.card.dbf_id,
                    actor.hand.len().saturating_add(actor.board.len())
                );
                match effect.pool_destination {
                    PoolDestination::Hand => {
                        if takes_from_owner_deck
                            && remove_known_deck_card(actor, &candidate.card.card_id).is_none()
                        {
                            return Err(SolverError::Unsupported(
                                "owner-deck choice was not present in canonical deck identity"
                                    .to_owned(),
                            ));
                        }
                        if actor.hand.len() < 10 {
                            let mut generated = Card::generated_from_pool(
                                entity_id,
                                &candidate.card,
                                effect.created_card_cost_delta,
                                false,
                            );
                            apply_embedded_template_rule_to_card(&mut generated)?;
                            actor.hand.push(generated);
                        }
                    }
                    PoolDestination::Battlefield => {
                        if candidate.card.card_type != CardType::Minion {
                            return Err(SolverError::Unsupported(
                                "summon_from_pool produced a non-minion".to_owned(),
                            ));
                        }
                        if actor
                            .board
                            .iter()
                            .filter(|card| occupies_board_slot(card))
                            .count()
                            < 7
                        {
                            actor.board.push(Card::generated_from_pool(
                                entity_id,
                                &candidate.card,
                                effect.created_card_cost_delta,
                                true,
                            ));
                        }
                    }
                    PoolDestination::Deck => {
                        if let Some(known) = actor.known_deck.iter_mut().find(|known| {
                            known.card_id == candidate.card.card_id
                                && known.origin == DeckCardOrigin::Generated
                        }) {
                            known.count = known.count.saturating_add(1);
                        } else {
                            actor.known_deck.push(KnownDeckCard {
                                card_id: Arc::clone(&candidate.card.card_id),
                                count: 1,
                                origin: DeckCardOrigin::Generated,
                                card_type: candidate.card.card_type,
                                cost: candidate.card.cost,
                                name: Arc::clone(&candidate.card.name),
                            });
                        }
                        actor.deck_size = actor.deck_size.saturating_add(1);
                    }
                    PoolDestination::None | PoolDestination::Cast => {
                        return Err(SolverError::Unsupported(
                            "unsupported card-pool destination".to_owned(),
                        ));
                    }
                }
                expanded.push(WeightedState {
                    state: child,
                    probability: outcome.probability.multiply(probability)?,
                });
            }
        }
        outcomes = merge_weighted_states(expanded)?;
    }
    Ok(outcomes)
}

fn apply_effect_sequence_raw_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    effects: &[Effect],
    target_id: &str,
) -> Result<Vec<WeightedState>, SolverError> {
    let mut outcomes = vec![WeightedState {
        state: state.clone(),
        probability: ExactProbability::CERTAIN,
    }];
    for effect in effects {
        let mut expanded = Vec::new();
        for outcome in outcomes {
            let spell_power = if source.card_type == CardType::Spell {
                player(&outcome.state, actor_side).spell_power
            } else {
                0
            };
            if effect.pool.is_some() {
                for child in apply_pool_effect_outcomes(&outcome.state, actor_side, source, effect)?
                {
                    expanded.push(WeightedState {
                        state: child.state,
                        probability: outcome.probability.multiply(child.probability)?,
                    });
                }
                continue;
            }
            if !effect.random {
                for child in apply_deterministic_effect_outcomes(
                    &outcome.state,
                    actor_side,
                    source,
                    effect,
                    target_id,
                    spell_power,
                )? {
                    expanded.push(WeightedState {
                        state: child.state,
                        probability: outcome.probability.multiply(child.probability)?,
                    });
                }
                continue;
            }
            if effect.kind.as_ref() == "damage_split" {
                if effect.amount <= 0
                    || effect.count != 1
                    || effect.target.as_ref() != "all_enemy_characters"
                    || !effect.card_id.is_empty()
                    || effect.attack != 0
                    || effect.has_summoned_minion_keywords()
                {
                    return Err(SolverError::Unsupported(
                        "split-damage effect has invalid reviewed fields".to_owned(),
                    ));
                }
                let mut split = vec![outcome];
                for _ in 0..u16::try_from(effect.amount).unwrap_or(u16::MAX) {
                    let mut point_outcomes = Vec::new();
                    for point in split {
                        let targets = random_effect_target_locations(
                            &point.state,
                            actor_side,
                            effect.target.as_ref(),
                        )?;
                        if targets.is_empty() {
                            point_outcomes.push(point);
                            continue;
                        }
                        let branch_probability = ExactProbability::uniform(targets.len())?;
                        for target in targets {
                            let mut child = point.state.clone();
                            let dealt = damage(&mut child, target, 1);
                            for frenzy_child in resolve_surviving_frenzy_after_damage_outcomes(
                                &child, target, dealt,
                            )? {
                                for death_child in
                                    resolve_death_queue_outcomes(&frenzy_child.state)?
                                {
                                    point_outcomes.push(WeightedState {
                                        state: death_child.state,
                                        probability: point
                                            .probability
                                            .multiply(branch_probability)?
                                            .multiply(frenzy_child.probability)?
                                            .multiply(death_child.probability)?,
                                    });
                                }
                            }
                        }
                    }
                    split = merge_weighted_states(point_outcomes)?;
                }
                expanded.extend(split);
                continue;
            }
            if effect.count != 1
                || !effect.card_id.is_empty()
                || effect.attack != 0
                || effect.has_summoned_minion_keywords()
                || !matches!(
                    effect.kind.as_ref(),
                    "damage" | "heal" | "freeze" | "buff_attack" | "buff_health" | "set_health"
                )
            {
                return Err(SolverError::Unsupported(format!(
                    "random effect {:?} has unsupported auxiliary fields",
                    effect.kind
                )));
            }
            let targets =
                random_effect_target_locations(&outcome.state, actor_side, effect.target.as_ref())?;
            if targets.is_empty() {
                expanded.push(outcome);
                continue;
            }
            let branch_probability = ExactProbability::uniform(targets.len())?;
            for target in targets {
                for child in apply_character_effect_outcomes(
                    &outcome.state,
                    actor_side,
                    source,
                    effect,
                    target,
                    spell_power,
                )? {
                    expanded.push(WeightedState {
                        state: child.state,
                        probability: outcome
                            .probability
                            .multiply(branch_probability)?
                            .multiply(child.probability)?,
                    });
                }
            }
        }
        outcomes = merge_weighted_states(expanded)?;
    }
    merge_weighted_states(outcomes)
}

fn trigger_effects(source: &Card, trigger: &str) -> Vec<Effect> {
    source
        .effects
        .iter()
        .filter(|effect| effect.trigger.as_ref() == trigger)
        .cloned()
        .collect()
}

fn apply_trigger_effects_raw_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    trigger: &str,
    target_id: &str,
) -> Result<Vec<WeightedState>, SolverError> {
    let effects = trigger_effects(source, trigger);
    apply_effect_sequence_raw_outcomes(state, actor_side, source, &effects, target_id)
}

fn collect_dead_batch(state: &mut GameState) -> Result<Vec<(PlayerSide, Card)>, SolverError> {
    let active_side = side_for_player(state, &state.active_player_id)?;
    let mut queued = Vec::<(PlayerSide, Card)>::new();
    for side in [active_side, other_side(active_side)] {
        let owner = player_mut(state, side);
        let mut surviving = Vec::with_capacity(owner.board.len());
        for card in owner.board.drain(..) {
            if occupies_board_slot(&card) {
                surviving.push(card);
            } else {
                queued.push((side, card));
            }
        }
        owner.board = surviving;
    }
    for (owner_side, dead) in &queued {
        player_mut(state, *owner_side).graveyard.push(dead.clone());
    }
    Ok(queued)
}

/// Resolve simultaneous deaths in active-player order while preserving every
/// public chance branch emitted by Deathrattles. Newly-created deaths are
/// queued only after the complete current batch has resolved, matching the
/// deterministic queue above without collapsing a random draw or target.
fn resolve_death_queue_outcomes(state: &GameState) -> Result<Vec<WeightedState>, SolverError> {
    let mut frontier = vec![WeightedState {
        state: state.clone(),
        probability: ExactProbability::CERTAIN,
    }];
    let mut completed = Vec::<WeightedState>::new();
    while !frontier.is_empty() {
        let mut next_frontier = Vec::<WeightedState>::new();
        for outcome in frontier {
            let mut base = outcome.state;
            let queued = collect_dead_batch(&mut base)?;
            if queued.is_empty() {
                completed.push(WeightedState {
                    state: base,
                    probability: outcome.probability,
                });
                continue;
            }
            let mut branches = vec![WeightedState {
                state: base,
                probability: outcome.probability,
            }];
            for (owner_side, dead) in queued {
                let mut expanded = Vec::<WeightedState>::new();
                for branch in branches {
                    for child in apply_trigger_effects_raw_outcomes(
                        &branch.state,
                        owner_side,
                        &dead,
                        "deathrattle",
                        "",
                    )? {
                        expanded.push(WeightedState {
                            state: child.state,
                            probability: branch.probability.multiply(child.probability)?,
                        });
                    }
                }
                branches = merge_weighted_states(expanded)?;
            }
            next_frontier.extend(branches);
        }
        frontier = merge_weighted_states(next_frontier)?;
    }
    merge_weighted_states(completed)
}

fn resolve_weighted_death_queues(
    outcomes: Vec<WeightedState>,
) -> Result<Vec<WeightedState>, SolverError> {
    let mut expanded = Vec::<WeightedState>::new();
    for outcome in outcomes {
        for child in resolve_death_queue_outcomes(&outcome.state)? {
            expanded.push(WeightedState {
                state: child.state,
                probability: outcome.probability.multiply(child.probability)?,
            });
        }
    }
    merge_weighted_states(expanded)
}

fn apply_trigger_effects_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    trigger: &str,
    target_id: &str,
) -> Result<Vec<WeightedState>, SolverError> {
    resolve_weighted_death_queues(apply_trigger_effects_raw_outcomes(
        state, actor_side, source, trigger, target_id,
    )?)
}

fn apply_effects_outcomes(
    state: &GameState,
    actor_side: PlayerSide,
    source: &Card,
    target_id: &str,
) -> Result<Vec<WeightedState>, SolverError> {
    apply_trigger_effects_outcomes(state, actor_side, source, "resolution", target_id)
}

fn effect_has_chance(effect: &Effect) -> bool {
    effect.random || effect.pool.is_some()
}

fn card_has_chance_trigger(card: &Card, triggers: &[&str]) -> bool {
    card.effects.iter().any(|effect| {
        effect_has_chance(effect)
            && triggers
                .iter()
                .any(|trigger| effect.trigger.as_ref() == *trigger)
    })
}

fn state_has_chance_trigger(state: &GameState, triggers: &[&str]) -> bool {
    [&state.friendly, &state.opponent].into_iter().any(|owner| {
        owner
            .board
            .iter()
            .chain(owner.weapon.iter())
            .any(|card| card_has_chance_trigger(card, triggers))
    })
}

pub(crate) fn action_has_random_resolution(state: &GameState, action: &Action) -> bool {
    let Ok(actor_side) = side_for_player(state, &state.active_player_id) else {
        return false;
    };
    let actor = player(state, actor_side);
    match action.kind {
        ActionKind::PlayCard => actor
            .hand
            .iter()
            .find(|card| card.entity_id == action.source_entity_id)
            .is_some_and(|card| {
                card.card_type != CardType::Location
                    && (card.effects.iter().any(|effect| {
                        effect.trigger.as_ref() == "resolution" && effect_has_chance(effect)
                    }) || state_has_chance_trigger(
                        state,
                        &["deathrattle", "frenzy", "after_spell_cast", "spellburst"],
                    ))
            }),
        ActionKind::HeroPower => {
            actor.hero_power.as_ref().is_some_and(|card| {
                card.entity_id == action.source_entity_id
                    && card.effects.iter().any(|effect| {
                        effect.trigger.as_ref() == "resolution" && effect_has_chance(effect)
                    })
            }) || state_has_chance_trigger(state, &["deathrattle", "frenzy", "after_hero_power"])
        }
        ActionKind::LocationActivate => {
            actor.board.iter().any(|card| {
                card.entity_id == action.source_entity_id
                    && card.card_type == CardType::Location
                    && card.effects.iter().any(|effect| {
                        effect.trigger.as_ref() == "resolution" && effect_has_chance(effect)
                    })
            }) || state_has_chance_trigger(state, &["deathrattle", "frenzy"])
        }
        ActionKind::Attack => {
            state_has_chance_trigger(state, &["deathrattle", "frenzy", "after_hero_attack"])
        }
        ActionKind::EndTurn => state_has_chance_trigger(state, &["turn_end", "deathrattle"]),
    }
}

fn apply_chance_card_play_outcomes(
    state: &GameState,
    action: &Action,
) -> Result<Vec<ActionOutcome>, SolverError> {
    let mut next = state.clone();
    let actor_side = side_for_player(&next, &next.active_player_id)?;
    let hand_index = player(&next, actor_side)
        .hand
        .iter()
        .position(|card| card.entity_id == action.source_entity_id)
        .ok_or_else(|| {
            SolverError::IllegalAction("card is not in the active player's hand".to_owned())
        })?;
    let card = player_mut(&mut next, actor_side).hand.remove(hand_index);
    if card.card_type == CardType::Location {
        return Err(SolverError::Unsupported(
            "Location placement does not resolve its activation effect".to_owned(),
        ));
    }
    let one_cost_triggers = if card.cost == 1 {
        one_cost_card_doubling_triggers(state, actor_side)?
    } else {
        0
    };
    let replaced_weapon = if card.card_type == CardType::Weapon {
        let actor = player_mut(&mut next, actor_side);
        let previous = actor.weapon.take();
        if let Some(previous) = &previous {
            actor.hero.attack = actor.hero.attack.saturating_sub(previous.attack);
        }
        previous
    } else {
        None
    };
    let mut weighted = if let Some(previous) = replaced_weapon {
        resolve_broken_weapon_outcomes(&next, actor_side, &previous)?
    } else {
        vec![WeightedState {
            state: next,
            probability: ExactProbability::CERTAIN,
        }]
    };
    for outcome in &mut weighted {
        let actor = player_mut(&mut outcome.state, actor_side);
        actor.mana = actor.mana.checked_sub(card.cost).ok_or_else(|| {
            SolverError::IllegalAction("card cost exceeds available mana".to_owned())
        })?;
        if matches!(card.card_type, CardType::Minion | CardType::Location) {
            let position = usize::from(action.board_position);
            if position == 0 || position > actor.board.len() + 1 {
                return Err(SolverError::IllegalAction(
                    "board position is outside the legal range".to_owned(),
                ));
            }
            let mut minion = card.clone();
            if card.card_type == CardType::Minion {
                minion.summoned_this_turn = true;
                minion.can_attack = minion.attack > 0
                    && !minion.frozen
                    && !minion.dormant
                    && (minion.charge || minion.rush);
                minion.attacks_remaining = if minion.can_attack {
                    maximum_attacks(&minion)
                } else {
                    0
                };
                minion.attacks_remaining_known = true;
            }
            actor.board.insert(position - 1, minion);
        } else if card.card_type == CardType::Weapon {
            if card.current_durability == 0 {
                return Err(SolverError::IllegalAction(
                    "weapon has no public durability".to_owned(),
                ));
            }
            let attacks_used = public_tag_value(&actor.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
                .map(|value| u8::try_from(value.max(0)).unwrap_or(u8::MAX));
            if let Some(previous) = actor.weapon.take() {
                actor.hero.attack = actor.hero.attack.saturating_sub(previous.attack);
            }
            actor.hero.attack = actor.hero.attack.saturating_add(card.attack);
            actor.weapon = Some(card.clone());
            let maximum = maximum_attacks_with_weapon(&actor.hero, actor.weapon.as_ref());
            actor.hero.attacks_remaining =
                attacks_used.map_or(0, |used| maximum.saturating_sub(used));
            actor.hero.attacks_remaining_known = attacks_used.is_some();
            let weapon_is_usable = actor.weapon.as_ref().is_some_and(|weapon| {
                weapon.current_durability > 0
                    && !public_attack_is_blocked(&actor.hero, Some(weapon))
            });
            actor.hero.can_attack = actor.hero.attack > 0
                && actor.hero.current_health > 0
                && actor.hero.attacks_remaining > 0
                && !actor.hero.frozen
                && !actor.hero.dormant
                && weapon_is_usable;
            let hero_attack = actor.hero.attack;
            set_public_tag_value(&mut actor.hero, "ATK", 47, i64::from(hero_attack));
        } else if action.board_position != 0 {
            return Err(SolverError::IllegalAction(
                "this card type does not use a board position".to_owned(),
            ));
        }
    }

    let repetitions = if card.card_type == CardType::Spell {
        usize::from(one_cost_multiplier(one_cost_triggers))
    } else {
        1
    };
    for repetition in 0..repetitions {
        let mut expanded = Vec::new();
        for outcome in weighted {
            if repetition > 0
                && copied_spell_target_is_missing(&outcome.state, &action.target_entity_id)
            {
                // A repeated targeted spell keeps its original target. If an
                // earlier copy removed that target, this copy fizzles instead
                // of invalidating the entire player action and search tree.
                expanded.push(outcome);
                continue;
            }
            for child in
                apply_effects_outcomes(&outcome.state, actor_side, &card, &action.target_entity_id)?
            {
                if card.card_type == CardType::Spell {
                    let after_spell = resolve_board_trigger_outcomes(
                        &child.state,
                        actor_side,
                        "after_spell_cast",
                    )?;
                    for after_spell_child in after_spell {
                        for spellburst_child in resolve_board_trigger_outcomes(
                            &after_spell_child.state,
                            actor_side,
                            "spellburst",
                        )? {
                            expanded.push(WeightedState {
                                state: spellburst_child.state,
                                probability: outcome
                                    .probability
                                    .multiply(child.probability)?
                                    .multiply(after_spell_child.probability)?
                                    .multiply(spellburst_child.probability)?,
                            });
                        }
                    }
                    continue;
                }
                expanded.push(WeightedState {
                    state: child.state,
                    probability: outcome.probability.multiply(child.probability)?,
                });
            }
        }
        weighted = merge_weighted_states(expanded)?;
    }

    if card.card_type == CardType::Minion && one_cost_triggers > 0 {
        let multiplier = one_cost_multiplier(one_cost_triggers);
        for outcome in &mut weighted {
            if let Some(minion) = player_mut(&mut outcome.state, actor_side)
                .board
                .iter_mut()
                .find(|candidate| candidate.entity_id == card.entity_id)
            {
                minion.attack = minion.attack.saturating_mul(multiplier);
                minion.health = minion.health.saturating_mul(multiplier);
                minion.current_health = minion.current_health.saturating_mul(multiplier);
                let attack = minion.attack;
                let health = minion.health;
                set_public_tag_value(minion, "ATK", 47, i64::from(attack));
                set_public_tag_value(minion, "HEALTH", 45, i64::from(health));
            }
        }
    }
    for outcome in &mut weighted {
        if card.card_type == CardType::Spell {
            player_mut(&mut outcome.state, actor_side)
                .graveyard
                .push(card.clone());
        }
        reconcile_continuous_effects(state, &mut outcome.state)?;
    }
    let weighted = merge_weighted_states(weighted)?;
    Ok(weighted
        .into_iter()
        .map(|outcome| ActionOutcome {
            state: outcome.state,
            ended_turn: false,
            probability: outcome.probability,
        })
        .collect())
}

fn apply_chance_hero_power_outcomes(
    state: &GameState,
    action: &Action,
) -> Result<Vec<ActionOutcome>, SolverError> {
    let mut prepared = state.clone();
    let actor_side = side_for_player(&prepared, &prepared.active_player_id)?;
    let power = player(&prepared, actor_side)
        .hero_power
        .as_ref()
        .filter(|power| power.entity_id == action.source_entity_id)
        .cloned()
        .ok_or_else(|| SolverError::IllegalAction("hero power disappeared".to_owned()))?;
    {
        let actor = player_mut(&mut prepared, actor_side);
        actor.mana = actor.mana.checked_sub(power.cost).ok_or_else(|| {
            SolverError::IllegalAction("hero power cost exceeds available mana".to_owned())
        })?;
        actor.hero_power_available = false;
    }
    let mut weighted = Vec::<WeightedState>::new();
    for outcome in apply_effects_outcomes(&prepared, actor_side, &power, &action.target_entity_id)?
    {
        for triggered in
            resolve_board_trigger_outcomes(&outcome.state, actor_side, "after_hero_power")?
        {
            weighted.push(WeightedState {
                state: triggered.state,
                probability: outcome.probability.multiply(triggered.probability)?,
            });
        }
    }
    let mut weighted = merge_weighted_states(weighted)?;
    for outcome in &mut weighted {
        reconcile_continuous_effects(state, &mut outcome.state)?;
    }
    Ok(weighted
        .into_iter()
        .map(|outcome| ActionOutcome {
            state: outcome.state,
            ended_turn: false,
            probability: outcome.probability,
        })
        .collect())
}

fn apply_chance_location_outcomes(
    state: &GameState,
    action: &Action,
) -> Result<Vec<ActionOutcome>, SolverError> {
    let actor_side = side_for_player(state, &state.active_player_id)?;
    let location = player(state, actor_side)
        .board
        .iter()
        .find(|card| {
            card.entity_id == action.source_entity_id && card.card_type == CardType::Location
        })
        .cloned()
        .ok_or_else(|| {
            SolverError::IllegalAction("location is not on the active player's board".to_owned())
        })?;
    if location.current_health == 0 {
        return Err(SolverError::IllegalAction(
            "location has no remaining charges".to_owned(),
        ));
    }
    let mut weighted =
        apply_effects_outcomes(state, actor_side, &location, &action.target_entity_id)?;
    for outcome in &mut weighted {
        let actor = player_mut(&mut outcome.state, actor_side);
        let Some(location_index) = actor
            .board
            .iter()
            .position(|card| card.entity_id == action.source_entity_id)
        else {
            continue;
        };
        let active_location = &mut actor.board[location_index];
        active_location.current_health = active_location.current_health.saturating_sub(1);
        if active_location.current_durability > 0 {
            active_location.current_durability =
                active_location.current_durability.saturating_sub(1);
        }
        if active_location.current_health == 0 {
            let expired = actor.board.remove(location_index);
            actor.graveyard.push(expired);
        }
        reconcile_continuous_effects(state, &mut outcome.state)?;
    }
    let weighted = merge_weighted_states(weighted)?;
    Ok(weighted
        .into_iter()
        .map(|outcome| ActionOutcome {
            state: outcome.state,
            ended_turn: false,
            probability: outcome.probability,
        })
        .collect())
}

fn apply_chance_end_turn_outcomes(state: &GameState) -> Result<Vec<ActionOutcome>, SolverError> {
    let actor_side = side_for_player(state, &state.active_player_id)?;
    let enemy_side = other_side(actor_side);
    let mut weighted = resolve_board_trigger_outcomes(state, actor_side, "turn_end")?;
    for outcome in &mut weighted {
        player_mut(&mut outcome.state, actor_side).mana = 0;
        outcome.state.active_player_id = Arc::clone(&player(&outcome.state, enemy_side).player_id);
        outcome.state.turn = outcome.state.turn.saturating_add(1);
    }
    let weighted = merge_weighted_states(weighted)?;
    Ok(weighted
        .into_iter()
        .map(|outcome| ActionOutcome {
            state: outcome.state,
            ended_turn: true,
            probability: outcome.probability,
        })
        .collect())
}

fn apply_chance_attack_outcomes(
    state: &GameState,
    action: &Action,
) -> Result<Vec<ActionOutcome>, SolverError> {
    let mut prepared = state.clone();
    let source = find_character(&prepared, &action.source_entity_id)
        .ok_or_else(|| SolverError::IllegalAction("attack source disappeared".to_owned()))?;
    let target = find_character(&prepared, &action.target_entity_id)
        .ok_or_else(|| SolverError::IllegalAction("attack target disappeared".to_owned()))?;
    let hero_attacked = matches!(source, CharacterLocation::Hero(_));
    let attacker_damage = character(&prepared, source).attack;
    let retaliation = if matches!(target, CharacterLocation::Hero(_)) {
        0
    } else {
        character(&prepared, target).attack
    };
    let attacking_weapon = match source {
        CharacterLocation::Hero(side) => player(&prepared, side).weapon.clone(),
        CharacterLocation::Board(_, _) => None,
    };
    let attacker_poisonous = character(&prepared, source).poisonous
        || attacking_weapon
            .as_ref()
            .is_some_and(|weapon| weapon.poisonous);
    let attacker_lifesteal = character(&prepared, source).lifesteal
        || attacking_weapon
            .as_ref()
            .is_some_and(|weapon| weapon.lifesteal);
    let defender_poisonous = character(&prepared, target).poisonous;
    let defender_lifesteal = character(&prepared, target).lifesteal;
    let attacker_is_minion = matches!(source, CharacterLocation::Board(_, _));
    let defender_is_minion = matches!(target, CharacterLocation::Board(_, _));
    let attacker_side = match source {
        CharacterLocation::Hero(side) | CharacterLocation::Board(side, _) => side,
    };
    let defender_side = match target {
        CharacterLocation::Hero(side) | CharacterLocation::Board(side, _) => side,
    };
    let attacker_entity_id = character(&prepared, source).entity_id.to_string();
    let defender_entity_id = character(&prepared, target).entity_id.to_string();
    let dealt = damage(&mut prepared, target, attacker_damage);
    let received = damage(&mut prepared, source, retaliation);
    if attacker_poisonous && dealt.dealt > 0 && defender_is_minion {
        character_mut(&mut prepared, target).current_health = 0;
    }
    if defender_poisonous && received.dealt > 0 && attacker_is_minion {
        character_mut(&mut prepared, source).current_health = 0;
    }
    for side in [PlayerSide::Friendly, PlayerSide::Opponent] {
        let healing = u16::from(attacker_lifesteal && attacker_side == side)
            .saturating_mul(dealt.dealt)
            .saturating_add(
                u16::from(defender_lifesteal && defender_side == side)
                    .saturating_mul(received.dealt),
            );
        let overkill = if matches!(target, CharacterLocation::Hero(target_side) if target_side == side)
        {
            dealt.hero_overkill
        } else if matches!(source, CharacterLocation::Hero(source_side) if source_side == side) {
            received.hero_overkill
        } else {
            0
        };
        heal_hero(&mut prepared, side, healing.saturating_sub(overkill));
    }

    let mut weighted = vec![WeightedState {
        state: prepared,
        probability: ExactProbability::CERTAIN,
    }];
    for (should_trigger, side, entity_id) in [
        (
            attacker_is_minion && received.dealt > 0,
            attacker_side,
            attacker_entity_id.as_str(),
        ),
        (
            defender_is_minion && dealt.dealt > 0,
            defender_side,
            defender_entity_id.as_str(),
        ),
    ] {
        if !should_trigger {
            continue;
        }
        let mut expanded = Vec::<WeightedState>::new();
        for outcome in weighted {
            for child in
                resolve_entity_once_trigger_outcomes(&outcome.state, side, entity_id, "frenzy")?
            {
                expanded.push(WeightedState {
                    state: child.state,
                    probability: outcome.probability.multiply(child.probability)?,
                });
            }
        }
        weighted = merge_weighted_states(expanded)?;
    }

    let mut after_weapon = Vec::<WeightedState>::new();
    for mut outcome in weighted {
        if hero_attacked {
            let owner = player_mut(&mut outcome.state, attacker_side);
            if let Some(attacks_used) =
                public_tag_value(&owner.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
            {
                set_public_tag_value(
                    &mut owner.hero,
                    "NUM_ATTACKS_THIS_TURN",
                    297,
                    attacks_used.saturating_add(1),
                );
            }
        }
        let broken_weapon = if let Some(attacking_weapon) = &attacking_weapon {
            let owner = player_mut(&mut outcome.state, attacker_side);
            if let Some(weapon) = owner.weapon.as_mut() {
                weapon.current_durability = weapon.current_durability.saturating_sub(1);
            }
            if owner
                .weapon
                .as_ref()
                .is_some_and(|weapon| weapon.current_durability == 0)
            {
                let broken = owner.weapon.take();
                owner.hero.attack = owner.hero.attack.saturating_sub(attacking_weapon.attack);
                broken
            } else {
                None
            }
        } else {
            None
        };
        let weapon_broke = broken_weapon.is_some();
        let branches = if let Some(broken_weapon) = broken_weapon {
            resolve_broken_weapon_outcomes(&outcome.state, attacker_side, &broken_weapon)?
        } else {
            vec![WeightedState {
                state: outcome.state,
                probability: ExactProbability::CERTAIN,
            }]
        };
        for mut child in branches {
            if let Some(attacker_location) = find_character(&child.state, &attacker_entity_id) {
                let attacker = character_mut(&mut child.state, attacker_location);
                attacker.stealth = false;
                attacker.attacks_remaining = attacker.attacks_remaining.saturating_sub(1);
                attacker.attacks_remaining_known = true;
                attacker.can_attack =
                    !weapon_broke && attacker.attacks_remaining > 0 && !attacker.frozen;
            }
            after_weapon.push(WeightedState {
                state: child.state,
                probability: outcome.probability.multiply(child.probability)?,
            });
        }
    }

    let mut after_deaths = Vec::<WeightedState>::new();
    for outcome in merge_weighted_states(after_weapon)? {
        for child in resolve_death_queue_outcomes(&outcome.state)? {
            after_deaths.push(WeightedState {
                state: child.state,
                probability: outcome.probability.multiply(child.probability)?,
            });
        }
    }
    let mut final_states = if hero_attacked {
        let mut triggered = Vec::<WeightedState>::new();
        for outcome in merge_weighted_states(after_deaths)? {
            for child in
                resolve_board_trigger_outcomes(&outcome.state, attacker_side, "after_hero_attack")?
            {
                triggered.push(WeightedState {
                    state: child.state,
                    probability: outcome.probability.multiply(child.probability)?,
                });
            }
        }
        merge_weighted_states(triggered)?
    } else {
        merge_weighted_states(after_deaths)?
    };
    for outcome in &mut final_states {
        reconcile_continuous_effects(state, &mut outcome.state)?;
    }
    Ok(final_states
        .into_iter()
        .map(|outcome| ActionOutcome {
            state: outcome.state,
            ended_turn: false,
            probability: outcome.probability,
        })
        .collect())
}

fn apply_effects(
    state: &mut GameState,
    actor_side: PlayerSide,
    source: &Card,
    target_id: &str,
) -> Result<(), SolverError> {
    let spell_power = if source.card_type == CardType::Spell {
        player(state, actor_side).spell_power
    } else {
        0
    };
    for effect in source
        .effects
        .iter()
        .filter(|effect| effect.trigger.as_ref() == "resolution")
    {
        if effect.random {
            return Err(SolverError::Unsupported(format!(
                "oracle cannot apply effect {:?}",
                effect.kind
            )));
        }
        if matches!(
            effect.kind.as_ref(),
            "set_attack"
                | "destroy"
                | "transform"
                | "grant_keywords"
                | "refresh_mana"
                | "gain_mana_crystals"
                | "gain_empty_mana_crystals"
                | "destroy_all_minions_and_locations"
                | "equip_weapon"
        ) {
            apply_deterministic_effect(state, actor_side, source, effect, target_id, spell_power)?;
            continue;
        }
        if matches!(
            effect.kind.as_ref(),
            "set_hero_power_cost" | "double_one_cost_cards"
        ) {
            // Continuous effects are recalculated after the complete action has
            // resolved, or are consumed by the surrounding card-play transition.
            continue;
        }
        if effect.kind.as_ref() == "draw_non_starting_spell_on_weapon_break" {
            continue;
        }
        if effect.kind.as_ref() == "shuffle_repeat_spell" {
            if effect.target.as_ref() != "none" || effect.card_id.trim().is_empty() {
                return Err(SolverError::Unsupported(
                    "repeat-spell shuffle has an invalid reviewed rule".to_owned(),
                ));
            }
            add_generated_deck_spell(
                player_mut(state, actor_side),
                effect.card_id.as_ref(),
                effect.name.as_ref(),
                effect.count,
            );
            continue;
        }
        if effect.kind.as_ref() == "replay_one_cost_cards" {
            if effect.target.as_ref() != "none" {
                return Err(SolverError::Unsupported(
                    "one-cost replay unexpectedly requires a target".to_owned(),
                ));
            }
            replay_one_cost_cards(state, actor_side, source)?;
            continue;
        }
        if effect.kind.as_ref() == "draw" {
            apply_draw_effect(state, actor_side, source, effect)?;
            continue;
        }
        if matches!(
            effect.kind.as_ref(),
            "draw_opponent" | "draw_both_players" | "draw_until_hand_count" | "buff_weapon_attack"
        ) {
            apply_deterministic_effect(state, actor_side, source, effect, target_id, spell_power)?;
            continue;
        }
        if let Some(targets) = automatic_target_locations(
            state,
            actor_side,
            effect.target.as_ref(),
            source.entity_id.as_ref(),
        ) {
            if !matches!(
                effect.kind.as_ref(),
                "damage" | "heal" | "freeze" | "buff_attack" | "buff_health" | "set_health"
            ) {
                return Err(SolverError::Unsupported(format!(
                    "oracle cannot apply automatic-target effect {:?}",
                    effect.kind
                )));
            }
            for target in targets {
                apply_character_effect(state, actor_side, source, effect, target, spell_power)?;
            }
            continue;
        }
        if effect.kind.as_ref() == "damage_all_minions" {
            if effect.target.as_ref() != "none" {
                return Err(SolverError::Unsupported(
                    "all-minion damage unexpectedly requires a target".to_owned(),
                ));
            }
            let amount = u16::try_from(effect.amount).map_err(|_| {
                SolverError::Unsupported(format!(
                    "negative all-minion damage on {}",
                    source.entity_id
                ))
            })?;
            let amount = amount.saturating_add(spell_power);
            let targets = [PlayerSide::Friendly, PlayerSide::Opponent]
                .into_iter()
                .flat_map(|side| {
                    player(state, side)
                        .board
                        .iter()
                        .enumerate()
                        .filter(|(_, card)| {
                            card.card_type == CardType::Minion
                                && card.current_health > 0
                                && !card.dormant
                        })
                        .map(move |(index, _)| CharacterLocation::Board(side, index))
                })
                .collect::<Vec<_>>();
            for target in targets {
                damage(state, target, amount);
            }
            continue;
        }
        if matches!(effect.kind.as_ref(), "armor" | "gain_hero_attack") {
            if effect.target.as_ref() != "none" {
                return Err(SolverError::Unsupported(
                    "owner effect unexpectedly requires a target".to_owned(),
                ));
            }
            let amount = u16::try_from(effect.amount).map_err(|_| {
                SolverError::Unsupported(format!("negative owner effect on {}", source.entity_id))
            })?;
            if effect.kind.as_ref() == "armor" {
                let actor = player_mut(state, actor_side);
                actor.armor = actor.armor.saturating_add(amount);
            } else {
                gain_hero_attack(state, actor_side, amount);
            }
            continue;
        }
        if effect.kind.as_ref() == "gain_mana" {
            if effect.target.as_ref() != "none" {
                return Err(SolverError::Unsupported(
                    "mana effect unexpectedly requires a target".to_owned(),
                ));
            }
            let amount = u16::try_from(effect.amount).map_err(|_| {
                SolverError::Unsupported(format!("negative mana effect on {}", source.entity_id))
            })?;
            let actor = player_mut(state, actor_side);
            actor.mana = actor.mana.saturating_add(amount);
            continue;
        }
        if effect.kind.as_ref() == "summon" {
            // Deaths caused by an earlier effect free board slots before Summon.
            // Other compound effects keep their target addressable until the
            // complete source finishes resolving (damage + Freeze/buff).
            remove_dead(state)?;
            if effect.target.as_ref() != "none" {
                return Err(SolverError::Unsupported(
                    "summon effect unexpectedly requires a target".to_owned(),
                ));
            }
            let turn = state.turn;
            for ordinal in 0..effect.count {
                let actor = player_mut(state, actor_side);
                if actor
                    .board
                    .iter()
                    .filter(|card| occupies_board_slot(card))
                    .count()
                    >= 7
                {
                    break;
                }
                let entity_id = format!(
                    "generated-{}-{turn}-{}-{ordinal}",
                    source.entity_id,
                    actor.board.len()
                );
                actor.board.push(Card::summoned_minion(entity_id, effect));
            }
            continue;
        }
        if !matches!(
            effect.kind.as_ref(),
            "damage" | "heal" | "freeze" | "buff_attack" | "buff_health" | "set_health"
        ) {
            return Err(SolverError::Unsupported(format!(
                "oracle cannot apply effect {:?}",
                effect.kind
            )));
        }
        if effect.target.as_ref() == "none" {
            continue;
        }
        let resolved_target = match effect.target.as_ref() {
            "self" => source.entity_id.to_string(),
            "enemy_hero" => player(state, other_side(actor_side))
                .hero
                .entity_id
                .to_string(),
            "friendly_hero" => player(state, actor_side).hero.entity_id.to_string(),
            _ => target_id.to_owned(),
        };
        if !target_id.is_empty()
            && matches!(effect.target.as_ref(), "enemy_hero" | "friendly_hero")
            && primary_target_mode(source) == effect.target.as_ref()
            && target_id != resolved_target
        {
            return Err(SolverError::IllegalAction(
                "fixed hero target does not match the reviewed card rule".to_owned(),
            ));
        }
        let location = find_character(state, &resolved_target).ok_or_else(|| {
            SolverError::IllegalAction(format!("oracle target no longer exists: {resolved_target}"))
        })?;
        apply_character_effect(state, actor_side, source, effect, location, spell_power)?;
    }
    remove_dead(state)?;
    Ok(())
}

pub fn apply_action(state: &GameState, action: &Action) -> Result<(GameState, bool), SolverError> {
    let legal = legal_actions(state)?;
    if !legal
        .iter()
        .any(|candidate| candidate.action_id() == action.action_id())
    {
        return Err(SolverError::IllegalAction(action.action_id()));
    }

    if action_has_random_resolution(state, action) {
        return Err(SolverError::Unsupported(
            "deterministic action transition cannot collapse a random outcome".to_owned(),
        ));
    }

    apply_caller_confirmed_action(state, action)
}

/// Apply every public outcome of a legal action. Random targets are emitted as
/// exact rational-probability branches and are never converted into a player
/// target or silently collapsed into one deterministic state.
pub fn apply_action_outcomes(
    state: &GameState,
    action: &Action,
) -> Result<Vec<ActionOutcome>, SolverError> {
    let legal = legal_actions(state)?;
    if !legal
        .iter()
        .any(|candidate| candidate.action_id() == action.action_id())
    {
        return Err(SolverError::IllegalAction(action.action_id()));
    }
    apply_caller_confirmed_action_outcomes(state, action)
}

/// Chance-aware counterpart of [`apply_caller_confirmed_action`]. Direct random
/// effects and random board/death events share the same exact-probability
/// transition path, so no action kind has to collapse a triggered outcome.
pub(crate) fn apply_caller_confirmed_action_outcomes(
    state: &GameState,
    action: &Action,
) -> Result<Vec<ActionOutcome>, SolverError> {
    if action_has_random_resolution(state, action) {
        return match action.kind {
            ActionKind::PlayCard => apply_chance_card_play_outcomes(state, action),
            ActionKind::Attack => apply_chance_attack_outcomes(state, action),
            ActionKind::HeroPower => apply_chance_hero_power_outcomes(state, action),
            ActionKind::LocationActivate => apply_chance_location_outcomes(state, action),
            ActionKind::EndTurn => apply_chance_end_turn_outcomes(state),
        };
    }
    let (state, ended_turn) = apply_caller_confirmed_action(state, action)?;
    Ok(vec![ActionOutcome {
        state,
        ended_turn,
        probability: ExactProbability::CERTAIN,
    }])
}

/// Apply a root action whose legality was independently confirmed by a complete HDT option
/// frame. Callers must validate public entity binding before entering this path. It is used only
/// for the first action; subsequent search actions still pass through [`apply_action`].
pub(crate) fn apply_caller_confirmed_action(
    state: &GameState,
    action: &Action,
) -> Result<(GameState, bool), SolverError> {
    let mut next = state.clone();
    let actor_side = side_for_player(&next, &next.active_player_id)?;
    let enemy_side = other_side(actor_side);
    if action.kind == ActionKind::EndTurn {
        resolve_board_trigger(&mut next, actor_side, "turn_end")?;
        // Unspent temporary/current-turn mana expires here.  Keeping it in a
        // terminal score rewarded passing with playable cards in hand.
        player_mut(&mut next, actor_side).mana = 0;
        next.active_player_id = Arc::clone(&player(&next, enemy_side).player_id);
        next.turn = next.turn.saturating_add(1);
        return Ok((next, true));
    }

    if action.kind == ActionKind::Attack {
        let source = find_character(&next, &action.source_entity_id)
            .ok_or_else(|| SolverError::IllegalAction("attack source disappeared".to_owned()))?;
        let target = find_character(&next, &action.target_entity_id)
            .ok_or_else(|| SolverError::IllegalAction("attack target disappeared".to_owned()))?;
        let hero_attacked = matches!(source, CharacterLocation::Hero(_));
        let attacker_damage = character(&next, source).attack;
        let retaliation = if matches!(target, CharacterLocation::Hero(_)) {
            0
        } else {
            character(&next, target).attack
        };
        let attacking_weapon = match source {
            CharacterLocation::Hero(side) => player(&next, side).weapon.clone(),
            CharacterLocation::Board(_, _) => None,
        };
        let attacker_poisonous = character(&next, source).poisonous
            || attacking_weapon
                .as_ref()
                .is_some_and(|weapon| weapon.poisonous);
        let attacker_lifesteal = character(&next, source).lifesteal
            || attacking_weapon
                .as_ref()
                .is_some_and(|weapon| weapon.lifesteal);
        let defender_poisonous = character(&next, target).poisonous;
        let defender_lifesteal = character(&next, target).lifesteal;
        let attacker_is_minion = matches!(source, CharacterLocation::Board(_, _));
        let defender_is_minion = matches!(target, CharacterLocation::Board(_, _));
        let attacker_side = match source {
            CharacterLocation::Hero(side) | CharacterLocation::Board(side, _) => side,
        };
        let defender_side = match target {
            CharacterLocation::Hero(side) | CharacterLocation::Board(side, _) => side,
        };
        let attacker_entity_id = character(&next, source).entity_id.to_string();
        let defender_entity_id = character(&next, target).entity_id.to_string();
        let dealt = damage(&mut next, target, attacker_damage);
        let received = damage(&mut next, source, retaliation);
        if attacker_poisonous && dealt.dealt > 0 && defender_is_minion {
            character_mut(&mut next, target).current_health = 0;
        }
        if defender_poisonous && received.dealt > 0 && attacker_is_minion {
            character_mut(&mut next, source).current_health = 0;
        }
        for side in [PlayerSide::Friendly, PlayerSide::Opponent] {
            let healing = u16::from(attacker_lifesteal && attacker_side == side)
                .saturating_mul(dealt.dealt)
                .saturating_add(
                    u16::from(defender_lifesteal && defender_side == side)
                        .saturating_mul(received.dealt),
                );
            let overkill = if matches!(target, CharacterLocation::Hero(target_side) if target_side == side)
            {
                dealt.hero_overkill
            } else if matches!(source, CharacterLocation::Hero(source_side) if source_side == side)
            {
                received.hero_overkill
            } else {
                0
            };
            heal_hero(&mut next, side, healing.saturating_sub(overkill));
        }
        if attacker_is_minion && received.dealt > 0 {
            resolve_entity_once_trigger(&mut next, attacker_side, &attacker_entity_id, "frenzy")?;
        }
        if defender_is_minion && dealt.dealt > 0 {
            resolve_entity_once_trigger(&mut next, defender_side, &defender_entity_id, "frenzy")?;
        }
        if matches!(source, CharacterLocation::Hero(_)) {
            let owner = player_mut(&mut next, attacker_side);
            if let Some(attacks_used) =
                public_tag_value(&owner.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
            {
                set_public_tag_value(
                    &mut owner.hero,
                    "NUM_ATTACKS_THIS_TURN",
                    297,
                    attacks_used.saturating_add(1),
                );
            }
        }
        let broken_weapon = if let Some(attacking_weapon) = attacking_weapon {
            let owner = player_mut(&mut next, attacker_side);
            if let Some(weapon) = owner.weapon.as_mut() {
                weapon.current_durability = weapon.current_durability.saturating_sub(1);
            }
            if owner
                .weapon
                .as_ref()
                .is_some_and(|weapon| weapon.current_durability == 0)
            {
                let broken = owner.weapon.take();
                owner.hero.attack = owner.hero.attack.saturating_sub(attacking_weapon.attack);
                broken
            } else {
                None
            }
        } else {
            None
        };
        let weapon_broke = broken_weapon.is_some();
        if let Some(broken_weapon) = broken_weapon {
            resolve_broken_weapon(&mut next, attacker_side, broken_weapon)?;
        }
        let attacker = character_mut(&mut next, source);
        attacker.stealth = false;
        attacker.attacks_remaining = attacker.attacks_remaining.saturating_sub(1);
        attacker.attacks_remaining_known = true;
        attacker.can_attack = !weapon_broke && attacker.attacks_remaining > 0 && !attacker.frozen;
        remove_dead(&mut next)?;
        if hero_attacked {
            resolve_board_trigger(&mut next, attacker_side, "after_hero_attack")?;
        }
        reconcile_continuous_effects(state, &mut next)?;
        return Ok((next, false));
    }

    if action.kind == ActionKind::LocationActivate {
        let location = player(&next, actor_side)
            .board
            .iter()
            .find(|card| {
                card.entity_id == action.source_entity_id && card.card_type == CardType::Location
            })
            .cloned()
            .ok_or_else(|| {
                SolverError::IllegalAction(
                    "location is not on the active player's board".to_owned(),
                )
            })?;
        if location.current_health == 0 {
            return Err(SolverError::IllegalAction(
                "location has no remaining charges".to_owned(),
            ));
        }
        apply_effects(&mut next, actor_side, &location, &action.target_entity_id)?;
        let actor = player_mut(&mut next, actor_side);
        let location_index = actor
            .board
            .iter()
            .position(|card| card.entity_id == action.source_entity_id)
            .ok_or_else(|| {
                SolverError::IllegalAction("location disappeared during activation".to_owned())
            })?;
        let active_location = &mut actor.board[location_index];
        active_location.current_health = active_location.current_health.saturating_sub(1);
        if active_location.current_durability > 0 {
            active_location.current_durability =
                active_location.current_durability.saturating_sub(1);
        }
        if active_location.current_health == 0 {
            let expired = actor.board.remove(location_index);
            actor.graveyard.push(expired);
        }
        reconcile_continuous_effects(state, &mut next)?;
        return Ok((next, false));
    }

    if action.kind == ActionKind::PlayCard {
        let hand_index = player(&next, actor_side)
            .hand
            .iter()
            .position(|card| card.entity_id == action.source_entity_id)
            .ok_or_else(|| {
                SolverError::IllegalAction("card is not in the active player's hand".to_owned())
            })?;
        let card = player_mut(&mut next, actor_side).hand.remove(hand_index);
        let one_cost_triggers = if card.cost == 1 {
            one_cost_card_doubling_triggers(state, actor_side)?
        } else {
            0
        };
        let replaced_weapon = if card.card_type == CardType::Weapon {
            let actor = player_mut(&mut next, actor_side);
            let previous = actor.weapon.take();
            if let Some(previous) = &previous {
                actor.hero.attack = actor.hero.attack.saturating_sub(previous.attack);
            }
            previous
        } else {
            None
        };
        if let Some(previous) = replaced_weapon {
            resolve_broken_weapon(&mut next, actor_side, previous)?;
        }
        {
            let actor = player_mut(&mut next, actor_side);
            actor.mana = actor.mana.checked_sub(card.cost).ok_or_else(|| {
                SolverError::IllegalAction("card cost exceeds available mana".to_owned())
            })?;
            if matches!(card.card_type, CardType::Minion | CardType::Location) {
                let position = usize::from(action.board_position);
                if position == 0 || position > actor.board.len() + 1 {
                    return Err(SolverError::IllegalAction(
                        "board position is outside the legal range".to_owned(),
                    ));
                }
                let mut minion = card.clone();
                if card.card_type == CardType::Minion {
                    minion.summoned_this_turn = true;
                    minion.can_attack = minion.attack > 0
                        && !minion.frozen
                        && !minion.dormant
                        && (minion.charge || minion.rush);
                    minion.attacks_remaining = if minion.can_attack {
                        maximum_attacks(&minion)
                    } else {
                        0
                    };
                    minion.attacks_remaining_known = true;
                }
                actor.board.insert(position - 1, minion);
            } else if card.card_type == CardType::Weapon {
                if card.current_durability == 0 {
                    return Err(SolverError::IllegalAction(
                        "weapon has no public durability".to_owned(),
                    ));
                }
                let attacks_used = public_tag_value(&actor.hero, &["NUM_ATTACKS_THIS_TURN"], 297)
                    .map(|value| u8::try_from(value.max(0)).unwrap_or(u8::MAX));
                if let Some(previous) = actor.weapon.take() {
                    actor.hero.attack = actor.hero.attack.saturating_sub(previous.attack);
                }
                actor.hero.attack = actor.hero.attack.saturating_add(card.attack);
                actor.weapon = Some(card.clone());
                let maximum = maximum_attacks_with_weapon(&actor.hero, actor.weapon.as_ref());
                actor.hero.attacks_remaining =
                    attacks_used.map_or(0, |used| maximum.saturating_sub(used));
                actor.hero.attacks_remaining_known = attacks_used.is_some();
                let weapon_is_usable = actor.weapon.as_ref().is_some_and(|weapon| {
                    weapon.current_durability > 0
                        && !public_attack_is_blocked(&actor.hero, Some(weapon))
                });
                actor.hero.can_attack = actor.hero.attack > 0
                    && actor.hero.current_health > 0
                    && actor.hero.attacks_remaining > 0
                    && !actor.hero.frozen
                    && !actor.hero.dormant
                    && weapon_is_usable;
                let hero_attack = actor.hero.attack;
                set_public_tag_value(&mut actor.hero, "ATK", 47, i64::from(hero_attack));
            } else if action.board_position != 0 {
                return Err(SolverError::IllegalAction(
                    "this card type does not use a board position".to_owned(),
                ));
            }
        }
        if card.card_type != CardType::Location {
            let repetitions = if card.card_type == CardType::Spell {
                usize::from(one_cost_multiplier(one_cost_triggers))
            } else {
                1
            };
            for repetition in 0..repetitions {
                if repetition > 0 && copied_spell_target_is_missing(&next, &action.target_entity_id)
                {
                    break;
                }
                apply_effects(&mut next, actor_side, &card, &action.target_entity_id)?;
                if card.card_type == CardType::Spell {
                    resolve_board_trigger(&mut next, actor_side, "after_spell_cast")?;
                    resolve_board_trigger(&mut next, actor_side, "spellburst")?;
                }
            }
        }
        if card.card_type == CardType::Minion && one_cost_triggers > 0 {
            let multiplier = one_cost_multiplier(one_cost_triggers);
            if let Some(minion) = player_mut(&mut next, actor_side)
                .board
                .iter_mut()
                .find(|candidate| candidate.entity_id == card.entity_id)
            {
                minion.attack = minion.attack.saturating_mul(multiplier);
                minion.health = minion.health.saturating_mul(multiplier);
                minion.current_health = minion.current_health.saturating_mul(multiplier);
                let attack = minion.attack;
                let health = minion.health;
                set_public_tag_value(minion, "ATK", 47, i64::from(attack));
                set_public_tag_value(minion, "HEALTH", 45, i64::from(health));
            }
        }
        if card.card_type == CardType::Spell {
            player_mut(&mut next, actor_side)
                .graveyard
                .push(card.clone());
        }
        reconcile_continuous_effects(state, &mut next)?;
        return Ok((next, false));
    }

    if action.kind == ActionKind::HeroPower {
        let power = player(&next, actor_side)
            .hero_power
            .as_ref()
            .filter(|power| power.entity_id == action.source_entity_id)
            .cloned()
            .ok_or_else(|| SolverError::IllegalAction("hero power disappeared".to_owned()))?;
        {
            let actor = player_mut(&mut next, actor_side);
            actor.mana = actor.mana.checked_sub(power.cost).ok_or_else(|| {
                SolverError::IllegalAction("hero power cost exceeds available mana".to_owned())
            })?;
            actor.hero_power_available = false;
        }
        apply_effects(&mut next, actor_side, &power, &action.target_entity_id)?;
        resolve_board_trigger(&mut next, actor_side, "after_hero_power")?;
        reconcile_continuous_effects(state, &mut next)?;
        return Ok((next, false));
    }

    Err(SolverError::Unsupported(format!(
        "oracle cannot apply {}",
        action.kind.as_str()
    )))
}

fn cancelled(cancel: &AtomicBool) -> Result<(), SolverError> {
    if cancel.load(Ordering::Relaxed) {
        Err(SolverError::Cancelled)
    } else {
        Ok(())
    }
}

pub fn prove_lethal(
    state: &GameState,
    maximum_states: usize,
    cancel: &AtomicBool,
) -> Result<OracleProof, SolverError> {
    assert_exact_oracle_state(state)?;
    let enemy_id = Arc::clone(&state.opponent.player_id);
    let mut memo: HashMap<StateKey, bool> = HashMap::new();
    let mut explored = 0usize;

    fn can_lethal(
        current: &GameState,
        enemy_id: &str,
        memo: &mut HashMap<StateKey, bool>,
        explored: &mut usize,
        maximum_states: usize,
        cancel: &AtomicBool,
    ) -> Result<bool, SolverError> {
        cancelled(cancel)?;
        if current.player(enemy_id)?.hero.current_health == 0 {
            return Ok(true);
        }
        let key = StateKey::from_state(current);
        if let Some(value) = memo.get(&key) {
            return Ok(*value);
        }
        *explored += 1;
        if *explored > maximum_states {
            return Err(SolverError::StateLimit(maximum_states));
        }
        memo.insert(key, false);
        let mut actions = legal_actions(current)?;
        actions.sort_by_key(Action::action_id);
        for action in actions
            .iter()
            .filter(|action| action.kind != ActionKind::EndTurn)
        {
            let (child, _) = apply_action(current, action)?;
            if can_lethal(&child, enemy_id, memo, explored, maximum_states, cancel)? {
                memo.insert(key, true);
                return Ok(true);
            }
        }
        Ok(false)
    }

    let mut winning = Vec::new();
    let mut actions = legal_actions(state)?;
    actions.sort_by_key(Action::action_id);
    for action in actions
        .iter()
        .filter(|action| action.kind != ActionKind::EndTurn)
    {
        let (child, _) = apply_action(state, action)?;
        if can_lethal(
            &child,
            &enemy_id,
            &mut memo,
            &mut explored,
            maximum_states,
            cancel,
        )? {
            winning.push(action.action_id());
        }
    }
    winning.sort();
    Ok(OracleProof {
        has_lethal: !winning.is_empty(),
        winning_first_action_ids: winning,
        explored_state_count: explored,
    })
}

fn shortest_lethal_continuation(
    initial: &GameState,
    maximum_states: usize,
    cancel: &AtomicBool,
) -> Result<(Vec<Action>, GameState, usize), SolverError> {
    if initial.opponent.hero.current_health == 0 {
        return Ok((Vec::new(), initial.clone(), 0));
    }
    let mut queue = VecDeque::from([(initial.clone(), Vec::<Action>::new())]);
    let mut visited = HashMap::from([(StateKey::from_state(initial), 0usize)]);
    let mut explored = 0usize;
    while let Some((state, prefix)) = queue.pop_front() {
        cancelled(cancel)?;
        explored += 1;
        if explored > maximum_states {
            return Err(SolverError::StateLimit(maximum_states));
        }
        let mut actions = legal_actions(&state)?;
        actions.sort_by_key(Action::action_id);
        for action in actions
            .into_iter()
            .filter(|action| action.kind != ActionKind::EndTurn)
        {
            let (child, _) = apply_action(&state, &action)?;
            let mut child_actions = prefix.clone();
            child_actions.push(action);
            if child.opponent.hero.current_health == 0 {
                return Ok((child_actions, child, explored));
            }
            let depth = child_actions.len();
            let key = StateKey::from_state(&child);
            if visited
                .get(&key)
                .is_none_or(|known_depth| depth < *known_depth)
            {
                visited.insert(key, depth);
                queue.push_back((child, child_actions));
            }
        }
    }
    Err(SolverError::IllegalAction(
        "winning first action had no reconstructable lethal continuation".to_owned(),
    ))
}

#[must_use]
pub fn tactical_utility(state: &GameState, perspective_player_id: &str) -> i64 {
    let (perspective, enemy) = if state.friendly.player_id.as_ref() == perspective_player_id {
        (&state.friendly, &state.opponent)
    } else {
        (&state.opponent, &state.friendly)
    };
    let player_dead = perspective.hero.current_health == 0;
    let enemy_dead = enemy.hero.current_health == 0;
    if player_dead && enemy_dead {
        return 0;
    }
    if enemy_dead {
        return 1_000_000;
    }
    if player_dead {
        return -1_000_000;
    }
    fn survival_value(health: u16, armor: u16) -> i64 {
        let effective_health = u32::from(health) + u32::from(armor);
        (1..=effective_health)
            .map(|point| match point {
                1..=5 => 60,
                6..=10 => 30,
                11..=15 => 15,
                16..=20 => 8,
                _ => 4,
            })
            .sum()
    }

    fn board_card_value(card: &Card) -> i64 {
        if card.current_health == 0 {
            return 0;
        }
        if card.card_type == CardType::Location {
            return i64::from(card.current_health) * 24
                + i64::try_from(card.effects.len()).unwrap_or(i64::MAX) * 8;
        }
        if card.card_type != CardType::Minion {
            return 0;
        }
        let attack = i64::from(card.attack);
        let health = i64::from(card.current_health);
        let inert_zero_attack_body = card.attack == 0
            && card.effects.is_empty()
            && !card.taunt
            && !card.divine_shield
            && !card.poisonous
            && !card.lifesteal
            && !card.windfury
            && !card.mega_windfury
            && !card.reborn
            && !card.stealth
            && !card.immune;
        let health_weight = if inert_zero_attack_body { 3 } else { 14 };
        let mut value = attack * 24 + health * health_weight + attack * attack * 2;
        if card.taunt {
            value += 20 + health * 3;
        }
        if card.divine_shield {
            value += 20 + attack * 6;
        }
        if card.poisonous {
            value += 35;
        }
        if card.lifesteal {
            value += 12 + attack * 6;
        }
        if card.windfury {
            value += attack * 10;
        }
        if card.mega_windfury {
            value += attack * 22;
        }
        if card.reborn {
            value += 30 + value / 3;
        }
        if card.stealth {
            value += 8 + attack * 4;
        }
        if card.immune {
            value += 30;
        }
        if card.dormant {
            value = value * 2 / 3;
        }
        value
    }

    fn board_value(cards: &[Card]) -> i64 {
        cards.iter().map(board_card_value).sum()
    }

    fn weapon_value(weapon: Option<&Card>) -> i64 {
        weapon.map_or(0, |card| {
            let attack = i64::from(card.attack);
            let durability = i64::from(card.current_durability);
            // Attack is the weapon's reusable tactical identity; durability is a small
            // future option value. Multiplying both made the one-turn planner hoard charges
            // instead of taking a safe attack that disappears at end of turn.
            let mut value = attack * 10 + durability * 2;
            if card.poisonous {
                value += 24 + durability * 4;
            }
            if card.lifesteal {
                value += attack * 4;
            }
            if card.windfury {
                value += attack * 8;
            }
            if card.mega_windfury {
                value += attack * 18;
            }
            value
        })
    }

    fn hand_value(cards: &[Card]) -> i64 {
        cards
            .iter()
            .map(|card| {
                let base = (card.prior_weight.clamp(0.0, 10.0) * 25.0).round() as i64;
                let engine_reserve = card
                    .effects
                    .iter()
                    .map(|effect| match effect.kind.as_ref() {
                        // A persistent component that multiplies later one-cost cards is not
                        // interchangeable with a vanilla body in hand. Reserving part of its
                        // future option value prevents temporary mana from being spent merely to
                        // expose the engine with no same-turn payoff. Once a one-cost card is
                        // actually doubled, the simulated stats/effects outweigh this reserve.
                        "double_one_cost_cards" => 80,
                        _ => 0,
                    })
                    .sum::<i64>();
                base.saturating_add(engine_reserve)
            })
            .sum()
    }

    survival_value(perspective.hero.current_health, perspective.armor)
        - survival_value(enemy.hero.current_health, enemy.armor)
        + board_value(&perspective.board)
        - board_value(&enemy.board)
        + weapon_value(perspective.weapon.as_ref())
        - weapon_value(enemy.weapon.as_ref())
        + hand_value(&perspective.hand)
        - hand_value(&enemy.hand)
        + (i64::from(perspective.mana) - i64::from(enemy.mana)) * 2
}

fn line_ids(actions: &[Action]) -> Vec<String> {
    actions.iter().map(Action::action_id).collect()
}

fn better_plan(candidate: &TurnPlan, current: &TurnPlan) -> bool {
    candidate.minimax_utility > current.minimax_utility
        || (candidate.minimax_utility == current.minimax_utility
            && (candidate.actions.len() < current.actions.len()
                || (candidate.actions.len() == current.actions.len()
                    && line_ids(&candidate.actions) < line_ids(&current.actions))))
}

fn best_nonlethal_turn(
    state: &GameState,
    perspective_player_id: &str,
    maximum_states: usize,
    cancel: &AtomicBool,
) -> Result<TurnPlan, SolverError> {
    let mut memo: HashMap<StateKey, TurnPlan> = HashMap::new();
    let mut explored = 0usize;

    fn explore(
        current: &GameState,
        perspective_player_id: &str,
        maximum_states: usize,
        cancel: &AtomicBool,
        memo: &mut HashMap<StateKey, TurnPlan>,
        explored: &mut usize,
    ) -> Result<TurnPlan, SolverError> {
        cancelled(cancel)?;
        if current.friendly.hero.current_health == 0 || current.opponent.hero.current_health == 0 {
            return Ok(TurnPlan {
                actions: Vec::new(),
                terminal_state: current.clone(),
                minimax_utility: tactical_utility(current, perspective_player_id),
                explored_state_count: 0,
            });
        }
        let key = StateKey::from_state(current);
        if let Some(cached) = memo.get(&key) {
            return Ok(cached.clone());
        }
        *explored += 1;
        if *explored > maximum_states {
            return Err(SolverError::StateLimit(maximum_states));
        }
        let mut actions = legal_actions(current)?;
        actions.sort_by_key(Action::action_id);
        let end_turn = actions
            .iter()
            .find(|action| action.kind == ActionKind::EndTurn)
            .ok_or_else(|| SolverError::IllegalAction("end_turn is missing".to_owned()))?;
        let (end_state, _) = apply_action(current, end_turn)?;
        let mut best = TurnPlan {
            actions: vec![end_turn.clone()],
            terminal_state: end_state.clone(),
            minimax_utility: tactical_utility(&end_state, perspective_player_id),
            explored_state_count: 0,
        };
        for action in actions
            .into_iter()
            .filter(|action| action.kind != ActionKind::EndTurn)
        {
            let (child, _) = apply_action(current, &action)?;
            let child_plan = explore(
                &child,
                perspective_player_id,
                maximum_states,
                cancel,
                memo,
                explored,
            )?;
            let mut candidate_actions = Vec::with_capacity(child_plan.actions.len() + 1);
            candidate_actions.push(action);
            candidate_actions.extend(child_plan.actions.iter().cloned());
            let candidate = TurnPlan {
                actions: candidate_actions,
                terminal_state: child_plan.terminal_state,
                minimax_utility: child_plan.minimax_utility,
                explored_state_count: 0,
            };
            if better_plan(&candidate, &best) {
                best = candidate;
            }
        }
        memo.insert(key, best.clone());
        Ok(best)
    }

    let mut plan = explore(
        state,
        perspective_player_id,
        maximum_states,
        cancel,
        &mut memo,
        &mut explored,
    )?;
    plan.explored_state_count = explored;
    Ok(plan)
}

pub fn choose_turn_plan(
    state: &GameState,
    proof: &OracleProof,
    maximum_states: usize,
    cancel: &AtomicBool,
) -> Result<TurnPlan, SolverError> {
    if let Some(first_action_id) = proof.winning_first_action_ids.first() {
        let first = legal_actions(state)?
            .into_iter()
            .find(|action| action.action_id() == *first_action_id)
            .ok_or_else(|| {
                SolverError::IllegalAction(format!(
                    "winning first action disappeared: {first_action_id}"
                ))
            })?;
        let (child, _) = apply_action(state, &first)?;
        let (continuation, terminal_state, explored) =
            shortest_lethal_continuation(&child, maximum_states, cancel)?;
        let mut actions = Vec::with_capacity(continuation.len() + 1);
        actions.push(first);
        actions.extend(continuation);
        return Ok(TurnPlan {
            actions,
            minimax_utility: tactical_utility(&terminal_state, &state.perspective_player_id),
            terminal_state,
            explored_state_count: explored,
        });
    }
    best_nonlethal_turn(state, &state.perspective_player_id, maximum_states, cancel)
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeSet;
    use std::sync::atomic::AtomicBool;

    use crate::model::SolveRequest;

    use super::*;

    fn request(json: &str) -> SolveRequest {
        let mut request: SolveRequest = serde_json::from_str(json).expect("valid JSON");
        request.validate().expect("valid request");
        request
    }

    fn action(state: &GameState, action_id: &str) -> Action {
        legal_actions(state)
            .expect("legal actions")
            .into_iter()
            .find(|candidate| candidate.action_id() == action_id)
            .unwrap_or_else(|| panic!("missing action {action_id}"))
    }

    fn resolved_minion_candidate(
        card_id: &'static str,
        dbf_id: u64,
        cost: u16,
    ) -> ResolvedPoolCandidate {
        ResolvedPoolCandidate {
            card: crate::model::ResolvedPoolCard {
                card_id: Arc::from(card_id),
                dbf_id,
                name: Arc::from(card_id),
                card_type: CardType::Minion,
                cost,
                attack: cost.max(1),
                health: cost.max(1),
                durability: 0,
                rarity_id: 1,
                keywords: Vec::new().into(),
                text: Arc::from(""),
            },
            weight: 1,
        }
    }

    fn outcome_probability_sum(outcomes: &[ActionOutcome]) -> ExactProbability {
        outcomes
            .iter()
            .try_fold(
                ExactProbability::new(0, 1).expect("zero"),
                |sum, outcome| sum.add(outcome.probability),
            )
            .expect("valid probability sum")
    }

    #[test]
    fn tactical_utility_values_early_board_development_over_chip_damage() {
        let chip = request(
            r#"{"request_id":"chip","state":{"state_id":"chip","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30,"current_health":28}}}}"#,
        );
        let board = request(
            r#"{"request_id":"board","state":{"state_id":"board","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"two-two","card_type":"MINION","attack":2,"health":2}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(
            tactical_utility(&board.state, "f") > tactical_utility(&chip.state, "f"),
            "a developed 2/2 should matter more than two points of nonlethal opening chip damage"
        );
    }

    #[test]
    fn tactical_utility_escalates_face_damage_only_near_lethal() {
        let at_thirty = request(
            r#"{"request_id":"at-thirty","state":{"state_id":"at-thirty","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let at_twenty_eight = request(
            r#"{"request_id":"at-twenty-eight","state":{"state_id":"at-twenty-eight","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30,"current_health":28}}}}"#,
        );
        let at_four = request(
            r#"{"request_id":"at-four","state":{"state_id":"at-four","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30,"current_health":4}}}}"#,
        );
        let at_two = request(
            r#"{"request_id":"at-two","state":{"state_id":"at-two","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30,"current_health":2}}}}"#,
        );
        let early_gain =
            tactical_utility(&at_twenty_eight.state, "f") - tactical_utility(&at_thirty.state, "f");
        let closing_gain =
            tactical_utility(&at_two.state, "f") - tactical_utility(&at_four.state, "f");
        assert!(closing_gain > early_gain * 5);
    }

    #[test]
    fn tactical_utility_does_not_treat_a_blank_zero_attack_body_as_a_live_threat() {
        let request = request(
            r#"{"request_id":"blank-bait","state":{"state_id":"blank-bait","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"one-one","card_type":"MINION","attack":1,"health":1,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"blank-zero-one","card_type":"MINION","attack":0,"health":1}]}}}"#,
        );
        let (face, _) = apply_action(&request.state, &action(&request.state, "attack:one-one:oh"))
            .expect("face attack");
        let (clear, _) = apply_action(
            &request.state,
            &action(&request.state, "attack:one-one:blank-zero-one"),
        )
        .expect("blank body attack");

        assert!(
            tactical_utility(&face, "f") > tactical_utility(&clear, "f"),
            "a harmless blank 0/1 should not trigger indiscriminate board clearing"
        );
    }

    #[test]
    fn taunt_blocks_face_until_removed() {
        let request = request(
            r#"{
              "request_id":"taunt",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":0,"max_mana":0,
                  "board":[{"entity_id":"a","card_type":"MINION","attack":2,"health":2,"can_attack":true}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":2},"mana":0,"max_mana":0,
                  "board":[{"entity_id":"t","card_type":"MINION","attack":0,"health":2,"taunt":true}]}}
            }"#,
        );
        let ids: Vec<String> = legal_actions(&request.state)
            .expect("legal actions")
            .iter()
            .map(Action::action_id)
            .collect();
        assert!(ids.contains(&"attack:a:t".to_owned()));
        assert!(!ids.contains(&"attack:a:oh".to_owned()));
    }

    #[test]
    fn elusive_blocks_only_player_selected_spell_and_hero_power_targets() {
        let request = request(
            r#"{
              "request_id":"elusive-targeting",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "mana":3,"max_mana":3,"hero_power_available":true,
                  "hero_power":{"entity_id":"power","card_type":"HERO_POWER","cost":1,
                    "effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_minion"}]},
                  "hand":[
                    {"entity_id":"spell","card_type":"SPELL","cost":1,"effect_coverage":"exact",
                      "effects":[{"kind":"damage","amount":1,"target":"any_minion"}]},
                    {"entity_id":"battlecry","card_type":"MINION","cost":1,"attack":1,"health":1,
                      "effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_minion"}]}
                  ],
                  "board":[{"entity_id":"friendly-elusive","card_type":"MINION","attack":1,"health":2,
                    "tags":{"ELUSIVE":1,"CANT_BE_TARGETED_BY_SPELLS":1,"CANT_BE_TARGETED_BY_HERO_POWERS":1}}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[
                    {"entity_id":"enemy-elusive","card_type":"MINION","attack":1,"health":2,
                      "tags":{"ELUSIVE":1,"CANT_BE_TARGETED_BY_SPELLS":1,"CANT_BE_TARGETED_BY_HERO_POWERS":1}},
                    {"entity_id":"enemy-normal","card_type":"MINION","attack":1,"health":2}
                  ]}}
            }"#,
        );
        let actions = legal_actions(&request.state).expect("legal actions");
        let targets = |source: &str| {
            actions
                .iter()
                .filter(|action| action.source_entity_id.as_ref() == source)
                .map(|action| action.target_entity_id.as_ref())
                .collect::<BTreeSet<_>>()
        };

        assert_eq!(targets("spell"), BTreeSet::from(["enemy-normal"]));
        assert_eq!(targets("power"), BTreeSet::from(["enemy-normal"]));
        assert_eq!(
            targets("battlecry"),
            BTreeSet::from(["enemy-elusive", "enemy-normal"]),
            "Battlecries are not spells or Hero Powers"
        );
    }

    #[test]
    fn divine_shield_absorbs_first_hit() {
        let request = request(
            r#"{
              "request_id":"shield",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":0,"max_mana":0,
                  "board":[{"entity_id":"a","card_type":"MINION","attack":2,"health":2,"can_attack":true}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"mana":0,"max_mana":0,
                  "board":[{"entity_id":"t","card_type":"MINION","attack":0,"health":1,"taunt":true,"divine_shield":true}]}}
            }"#,
        );
        let action = legal_actions(&request.state)
            .expect("legal actions")
            .into_iter()
            .find(|action| action.action_id() == "attack:a:t")
            .expect("attack");
        let (next, _) = apply_action(&request.state, &action).expect("apply");
        assert_eq!(next.opponent.board[0].current_health, 1);
        assert!(!next.opponent.board[0].divine_shield);
    }

    #[test]
    fn damage_then_summon_creates_a_rush_token_with_minion_only_targets() {
        let request = request(
            r#"{
              "request_id":"damage-summon-rush",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"spell","card_id":"CORE_BAR_801","card_type":"SPELL","cost":1,
                    "effect_coverage":"exact","effects":[
                      {"kind":"damage","amount":1,"target":"any_character"},
                      {"kind":"summon","target":"none","card_id":"BAR_035t","name":"Swift Hyena","attack":1,"health":1,"rush":true}
                    ]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[
                    {"entity_id":"victim","card_type":"MINION","attack":0,"health":1},
                    {"entity_id":"survivor","card_type":"MINION","attack":0,"health":3}
                  ]}}
            }"#,
        );
        let spell = action(&request.state, "play_card:spell:victim");
        let (after, _) = apply_action(&request.state, &spell).expect("damage and summon");
        assert_eq!(
            after
                .opponent
                .board
                .iter()
                .map(|card| card.entity_id.as_ref())
                .collect::<Vec<_>>(),
            vec!["survivor"]
        );
        let token = after.friendly.board.first().expect("summoned token");
        assert_eq!(token.entity_id.as_ref(), "generated-spell-1-0-0");
        assert_eq!(token.card_id.as_ref(), "BAR_035t");
        assert!(token.rush);
        assert!(token.can_attack);
        let targets = legal_actions(&after)
            .expect("token actions")
            .into_iter()
            .filter(|candidate| candidate.source_entity_id == token.entity_id)
            .map(|candidate| candidate.target_entity_id.to_string())
            .collect::<BTreeSet<_>>();
        assert_eq!(targets, BTreeSet::from(["survivor".to_owned()]));
    }

    #[test]
    fn damage_resolves_when_full_board_blocks_followup_summon() {
        let request = request(
            r#"{
              "request_id":"damage-summon-full-board",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"spell","card_id":"CORE_BAR_801","card_type":"SPELL","cost":1,
                    "effect_coverage":"exact","effects":[
                      {"kind":"damage","amount":1,"target":"any_character"},
                      {"kind":"summon","target":"none","card_id":"BAR_035t","name":"Swift Hyena","attack":1,"health":1,"rush":true}
                    ]}],
                  "board":[
                    {"entity_id":"f1","card_type":"MINION","health":1},
                    {"entity_id":"f2","card_type":"MINION","health":1},
                    {"entity_id":"f3","card_type":"MINION","health":1},
                    {"entity_id":"f4","card_type":"MINION","health":1},
                    {"entity_id":"f5","card_type":"MINION","health":1},
                    {"entity_id":"f6","card_type":"MINION","health":1},
                    {"entity_id":"f7","card_type":"MINION","health":1}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"victim","card_type":"MINION","health":1}]}}
            }"#,
        );
        let spell = action(&request.state, "play_card:spell:victim");
        let (after, _) = apply_action(&request.state, &spell).expect("damage with full board");
        assert!(after.opponent.board.is_empty());
        assert_eq!(after.friendly.board.len(), 7);
        assert!(
            after
                .friendly
                .board
                .iter()
                .all(|card| card.card_id.as_ref() != "BAR_035t")
        );
    }

    #[test]
    fn location_occupies_the_seventh_board_slot_for_minion_plays() {
        let request = request(
            r#"{
              "request_id":"location-board-capacity",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"hand-minion","card_type":"MINION","cost":1,"attack":1,"health":1}],
                  "board":[
                    {"entity_id":"location","card_type":"LOCATION","durability":2},
                    {"entity_id":"m1","card_type":"MINION","attack":1,"health":1},
                    {"entity_id":"m2","card_type":"MINION","attack":1,"health":1},
                    {"entity_id":"m3","card_type":"MINION","attack":1,"health":1},
                    {"entity_id":"m4","card_type":"MINION","attack":1,"health":1},
                    {"entity_id":"m5","card_type":"MINION","attack":1,"health":1},
                    {"entity_id":"m6","card_type":"MINION","attack":1,"health":1}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let actions = legal_actions(&request.state).expect("legal actions");
        assert!(
            !actions
                .iter()
                .any(|item| item.action_id() == "play_card:hand-minion:")
        );
    }

    #[test]
    fn board_placement_actions_cover_every_position_and_replay_in_order() {
        let request = request(
            r#"{
              "request_id":"board-placement",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":3,
                  "hand":[
                    {"entity_id":"hand-minion","card_type":"MINION","cost":1,"attack":2,"health":2},
                    {"entity_id":"hand-location","card_type":"LOCATION","cost":1,"durability":2},
                    {"entity_id":"hand-spell","card_type":"SPELL","cost":1}
                  ],
                  "board":[
                    {"entity_id":"left","card_type":"MINION","attack":1,"health":1},
                    {"entity_id":"right","card_type":"MINION","attack":1,"health":1}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let actions = legal_actions(&request.state).expect("legal placement actions");
        let ids = |source: &str| {
            actions
                .iter()
                .filter(|action| action.source_entity_id.as_ref() == source)
                .map(Action::action_id)
                .collect::<BTreeSet<_>>()
        };
        assert_eq!(
            ids("hand-minion"),
            BTreeSet::from([
                "play_card:hand-minion::position=1".to_owned(),
                "play_card:hand-minion::position=2".to_owned(),
                "play_card:hand-minion::position=3".to_owned(),
            ])
        );
        assert_eq!(
            ids("hand-location"),
            BTreeSet::from([
                "play_card:hand-location::position=1".to_owned(),
                "play_card:hand-location::position=2".to_owned(),
                "play_card:hand-location::position=3".to_owned(),
            ])
        );

        let (after_minion, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:hand-minion::position=2"),
        )
        .expect("middle minion placement");
        assert_eq!(
            after_minion
                .friendly
                .board
                .iter()
                .map(|card| card.entity_id.as_ref())
                .collect::<Vec<_>>(),
            vec!["left", "hand-minion", "right"]
        );

        let (after_location, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:hand-location::position=3"),
        )
        .expect("right location placement");
        assert_eq!(
            after_location
                .friendly
                .board
                .iter()
                .map(|card| card.entity_id.as_ref())
                .collect::<Vec<_>>(),
            vec!["left", "right", "hand-location"]
        );

        let forged_spell =
            Action::new(ActionKind::PlayCard, "hand-spell", "", "").with_board_position(1);
        assert!(apply_action(&request.state, &forged_spell).is_err());
    }

    #[test]
    fn combat_cleanup_keeps_location_without_modeling_location_actions() {
        let request = request(
            r#"{
              "request_id":"location-combat-cleanup",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"hand-location","card_type":"LOCATION","cost":1,"durability":2}],
                  "board":[
                    {"entity_id":"board-location","card_type":"LOCATION","durability":2},
                    {"entity_id":"attacker","card_type":"MINION","attack":2,"health":2,"can_attack":true}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"defender","card_type":"MINION","attack":0,"health":1}]}}
            }"#,
        );
        let modeled = crate::turnpair::visible_legal_actions(&request.state)
            .expect("visible modeled actions");
        assert!(
            !modeled
                .iter()
                .any(|item| item.source_entity_id.as_ref() == "hand-location")
        );

        let attack = action(&request.state, "attack:attacker:defender");
        let (next, _) = apply_action(&request.state, &attack).expect("apply combat");
        let location = next
            .friendly
            .board
            .iter()
            .find(|card| card.entity_id.as_ref() == "board-location")
            .expect("location remains on board");
        assert_eq!(location.card_type, CardType::Location);
        assert_eq!(location.current_health, 0);
        assert!(
            next.opponent
                .board
                .iter()
                .all(|card| card.entity_id.as_ref() != "defender")
        );
    }

    #[test]
    fn stealth_attacker_can_act_then_loses_stealth_while_enemy_stealth_is_untargetable() {
        let request = request(
            r#"{"request_id":"stealth","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"board":[{"entity_id":"a","card_type":"MINION","attack":2,"health":2,"can_attack":true,"stealth":true}],"hand":[{"entity_id":"spell","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_character"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"hidden","card_type":"MINION","attack":1,"health":2,"stealth":true}]}}}"#,
        );
        let ids = legal_actions(&request.state)
            .expect("legal actions")
            .into_iter()
            .map(|item| item.action_id())
            .collect::<BTreeSet<_>>();
        assert!(ids.contains("attack:a:oh"));
        assert!(!ids.contains("attack:a:hidden"));
        assert!(!ids.contains("play_card:spell:hidden"));
        let (next, _) = apply_action(&request.state, &action(&request.state, "attack:a:oh"))
            .expect("stealth attack");
        assert!(!next.friendly.board[0].stealth);
    }

    #[test]
    fn windfury_uses_snapshot_attacks_remaining_without_granting_extra_attacks() {
        let request = request(
            r#"{"request_id":"windfury","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"wind","card_type":"MINION","attack":2,"health":3,"can_attack":true,"attacks_remaining":2,"windfury":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let first = action(&request.state, "attack:wind:oh");
        let (after_first, _) = apply_action(&request.state, &first).expect("first attack");
        assert_eq!(after_first.friendly.board[0].attacks_remaining, 1);
        assert!(after_first.friendly.board[0].can_attack);
        let second = action(&after_first, "attack:wind:oh");
        let (after_second, _) = apply_action(&after_first, &second).expect("second attack");
        assert_eq!(after_second.friendly.board[0].attacks_remaining, 0);
        assert!(!after_second.friendly.board[0].can_attack);

        let mut one_left = request.state;
        one_left.friendly.board[0].attacks_remaining = 1;
        let (spent, _) = apply_action(&one_left, &action(&one_left, "attack:wind:oh"))
            .expect("last snapshot attack");
        assert!(
            !legal_actions(&spent)
                .expect("post attack actions")
                .iter()
                .any(|item| item.source_entity_id.as_ref() == "wind")
        );
    }

    #[test]
    fn newly_played_rush_is_minion_only_but_charge_can_attack_face() {
        let request = request(
            r#"{"request_id":"rush-charge","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,"hand":[{"entity_id":"rush","card_type":"MINION","cost":1,"attack":2,"health":2,"rush":true},{"entity_id":"charge","card_type":"MINION","cost":1,"attack":2,"health":2,"charge":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":0,"health":5}]}}}"#,
        );
        let (after_rush, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:rush::position=1"),
        )
        .expect("play rush");
        let rush_targets = legal_actions(&after_rush)
            .expect("rush actions")
            .into_iter()
            .filter(|item| {
                item.kind == ActionKind::Attack && item.source_entity_id.as_ref() == "rush"
            })
            .map(|item| item.target_entity_id.to_string())
            .collect::<BTreeSet<_>>();
        assert_eq!(rush_targets, BTreeSet::from(["target".to_owned()]));

        let (after_charge, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:charge::position=1"),
        )
        .expect("play charge");
        let charge_targets = legal_actions(&after_charge)
            .expect("charge actions")
            .into_iter()
            .filter(|item| {
                item.kind == ActionKind::Attack && item.source_entity_id.as_ref() == "charge"
            })
            .map(|item| item.target_entity_id.to_string())
            .collect::<BTreeSet<_>>();
        assert!(charge_targets.contains("target"));
        assert!(charge_targets.contains("oh"));
    }

    #[test]
    fn poisonous_requires_unprevented_minion_damage() {
        let base = |extra: &str| {
            request(&format!(
                r#"{{"request_id":"poison","state":{{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{{"player_id":"f","hero":{{"entity_id":"fh","card_type":"HERO","health":30}},"board":[{{"entity_id":"poison","card_type":"MINION","attack":1,"health":3,"can_attack":true,"poisonous":true}}]}},"opponent":{{"player_id":"o","hero":{{"entity_id":"oh","card_type":"HERO","health":30}},"board":[{{"entity_id":"target","card_type":"MINION","attack":0,"health":8{extra}}}]}}}}}}"#
            ))
        };
        let ordinary = base("");
        let (destroyed, _) = apply_action(
            &ordinary.state,
            &action(&ordinary.state, "attack:poison:target"),
        )
        .expect("poison damage");
        assert!(destroyed.opponent.board.is_empty());

        let shielded = base(",\"divine_shield\":true");
        let (shield_result, _) = apply_action(
            &shielded.state,
            &action(&shielded.state, "attack:poison:target"),
        )
        .expect("shielded poison");
        assert_eq!(shield_result.opponent.board[0].current_health, 8);
        assert!(!shield_result.opponent.board[0].divine_shield);

        let immune = base(",\"immune\":true");
        let (immune_result, _) = apply_action(
            &immune.state,
            &action(&immune.state, "attack:poison:target"),
        )
        .expect("immune poison");
        assert_eq!(immune_result.opponent.board[0].current_health, 8);
    }

    #[test]
    fn lifesteal_heals_for_damage_event_through_armor_and_overkill_but_not_shield() {
        let armored = request(
            r#"{"request_id":"lifesteal-armor","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":10},"board":[{"entity_id":"life","card_type":"MINION","attack":5,"health":5,"can_attack":true,"lifesteal":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"armor":3}}}"#,
        );
        let (armor_result, _) =
            apply_action(&armored.state, &action(&armored.state, "attack:life:oh"))
                .expect("armored lifesteal");
        assert_eq!(armor_result.friendly.hero.current_health, 15);
        assert_eq!(armor_result.opponent.armor, 0);
        assert_eq!(armor_result.opponent.hero.current_health, 28);

        let overkill = request(
            r#"{"request_id":"lifesteal-overkill","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":10},"board":[{"entity_id":"life","card_type":"MINION","attack":5,"health":5,"can_attack":true,"lifesteal":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"small","card_type":"MINION","attack":0,"health":1}]}}}"#,
        );
        let (overkill_result, _) = apply_action(
            &overkill.state,
            &action(&overkill.state, "attack:life:small"),
        )
        .expect("overkill lifesteal");
        assert_eq!(overkill_result.friendly.hero.current_health, 15);

        let shielded = request(
            r#"{"request_id":"lifesteal-shield","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":10},"board":[{"entity_id":"life","card_type":"MINION","attack":5,"health":5,"can_attack":true,"lifesteal":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"shield","card_type":"MINION","attack":0,"health":1,"divine_shield":true}]}}}"#,
        );
        let (shield_result, _) = apply_action(
            &shielded.state,
            &action(&shielded.state, "attack:life:shield"),
        )
        .expect("shielded lifesteal");
        assert_eq!(shield_result.friendly.hero.current_health, 10);
        assert_eq!(shield_result.opponent.board[0].current_health, 1);

        let defending = request(
            r#"{"request_id":"lifesteal-owner","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"attacker","card_type":"MINION","attack":3,"health":5,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30,"current_health":20},"board":[{"entity_id":"defender","card_type":"MINION","attack":2,"health":5,"lifesteal":true}]}}}"#,
        );
        let (defending_result, _) = apply_action(
            &defending.state,
            &action(&defending.state, "attack:attacker:defender"),
        )
        .expect("defending lifesteal");
        assert_eq!(defending_result.opponent.hero.current_health, 22);
        assert_eq!(defending_result.friendly.hero.current_health, 30);
    }

    #[test]
    fn hero_lifesteal_offsets_fatal_retaliation_before_death_is_resolved() {
        let position = |hero_extra: &str, player_extra: &str| {
            request(&format!(
                r#"{{"request_id":"hero-lifesteal","state":{{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{{"player_id":"f","hero":{{"entity_id":"fh","card_type":"HERO","attack":3,"health":30,"current_health":5,"can_attack":true,"attacks_remaining":1,"lifesteal":true{hero_extra}}}{player_extra}}},"opponent":{{"player_id":"o","hero":{{"entity_id":"oh","card_type":"HERO","health":30}},"board":[{{"entity_id":"retaliator","card_type":"MINION","attack":7,"health":10}}]}}}}}}"#
            ))
        };

        let ordinary = position("", "");
        let (ordinary_result, _) = apply_action(
            &ordinary.state,
            &action(&ordinary.state, "attack:fh:retaliator"),
        )
        .expect("simultaneous lifesteal combat");
        assert_eq!(ordinary_result.friendly.hero.current_health, 1);

        let armored = position("", ",\"armor\":2");
        let (armored_result, _) = apply_action(
            &armored.state,
            &action(&armored.state, "attack:fh:retaliator"),
        )
        .expect("armored simultaneous lifesteal combat");
        assert_eq!(armored_result.friendly.armor, 0);
        assert_eq!(armored_result.friendly.hero.current_health, 3);

        let shielded = position(",\"divine_shield\":true", "");
        let (shielded_result, _) = apply_action(
            &shielded.state,
            &action(&shielded.state, "attack:fh:retaliator"),
        )
        .expect("shielded simultaneous lifesteal combat");
        assert!(!shielded_result.friendly.hero.divine_shield);
        assert_eq!(shielded_result.friendly.hero.current_health, 8);

        let immune = position(",\"immune\":true", "");
        let (immune_result, _) = apply_action(
            &immune.state,
            &action(&immune.state, "attack:fh:retaliator"),
        )
        .expect("immune simultaneous lifesteal combat");
        assert!(immune_result.friendly.hero.immune);
        assert_eq!(immune_result.friendly.hero.current_health, 8);
    }

    #[test]
    fn poisonous_minion_point_damage_requires_unprevented_positive_damage() {
        let position = |target_extra: &str| {
            request(&format!(
                r#"{{"request_id":"point-poison","state":{{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{{"player_id":"f","hero":{{"entity_id":"fh","card_type":"HERO","health":30}},"mana":1,"max_mana":1,"hand":[{{"entity_id":"poison-source","card_type":"MINION","cost":1,"attack":1,"health":1,"poisonous":true,"effect_coverage":"exact","effects":[{{"kind":"damage","amount":1,"target":"enemy_minion"}}]}}]}},"opponent":{{"player_id":"o","hero":{{"entity_id":"oh","card_type":"HERO","health":30}},"board":[{{"entity_id":"target","card_type":"MINION","attack":0,"health":8{target_extra}}}]}}}}}}"#
            ))
        };

        let ordinary = position("");
        let (destroyed, _) = apply_action(
            &ordinary.state,
            &action(&ordinary.state, "play_card:poison-source:target:position=1"),
        )
        .expect("poisonous point damage");
        assert!(destroyed.opponent.board.is_empty());

        let shielded = position(",\"divine_shield\":true");
        let (shield_result, _) = apply_action(
            &shielded.state,
            &action(&shielded.state, "play_card:poison-source:target:position=1"),
        )
        .expect("shielded poisonous point damage");
        assert_eq!(shield_result.opponent.board[0].current_health, 8);
        assert!(!shield_result.opponent.board[0].divine_shield);

        let mut immune = position(",\"immune\":true").state;
        let source = immune.friendly.hand[0].clone();
        apply_effects(&mut immune, PlayerSide::Friendly, &source, "target")
            .expect("immune poisonous point damage");
        assert_eq!(immune.opponent.board[0].current_health, 8);
    }

    #[test]
    fn lifesteal_point_damage_to_own_hero_settles_overkill_before_healing() {
        let position = |hero_extra: &str, player_extra: &str| {
            request(&format!(
                r#"{{"request_id":"self-lifesteal","state":{{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{{"player_id":"f","hero":{{"entity_id":"fh","card_type":"HERO","health":30,"current_health":5{hero_extra}}},"mana":1,"max_mana":1,"hand":[{{"entity_id":"self-life","card_type":"MINION","cost":1,"attack":1,"health":1,"lifesteal":true,"effect_coverage":"exact","effects":[{{"kind":"damage","amount":10,"target":"friendly_hero"}}]}}]{player_extra}}},"opponent":{{"player_id":"o","hero":{{"entity_id":"oh","card_type":"HERO","health":30}}}}}}}}"#
            ))
        };
        let resolve = |request: &SolveRequest| {
            apply_action(
                &request.state,
                &action(&request.state, "play_card:self-life:fh:position=1"),
            )
            .expect("self-targeted lifesteal point damage")
            .0
        };

        let ordinary = position("", "");
        assert!(assert_exact_oracle_state(&ordinary.state).is_err());
        assert!(crate::turnpair::assert_turnpair_state(&ordinary.state, true).is_err());
        assert!(
            crate::turnpair::visible_legal_actions(&ordinary.state)
                .expect("visible structured action")
                .iter()
                .any(|candidate| { candidate.action_id() == "play_card:self-life:fh:position=1" })
        );
        let scoped_error =
            crate::turnpair::prove_scoped_lethal(&ordinary.state, 128, 4, &AtomicBool::new(false))
                .expect_err("scoped proof must not promote a lifesteal board state to exact");
        assert!(matches!(scoped_error, SolverError::Unsupported(_)));
        assert_eq!(resolve(&ordinary).friendly.hero.current_health, 5);

        let armored = position("", ",\"armor\":3");
        let armored_result = resolve(&armored);
        assert_eq!(armored_result.friendly.armor, 0);
        assert_eq!(armored_result.friendly.hero.current_health, 8);

        let shielded = position(",\"divine_shield\":true", "");
        let shielded_result = resolve(&shielded);
        assert!(!shielded_result.friendly.hero.divine_shield);
        assert_eq!(shielded_result.friendly.hero.current_health, 5);

        let immune = position(",\"immune\":true", "");
        let immune_result = resolve(&immune);
        assert!(immune_result.friendly.hero.immune);
        assert_eq!(immune_result.friendly.hero.current_health, 5);
    }

    #[test]
    fn exact_gate_rejects_stealth_but_attack_primitive_remains_available_to_partial_mode() {
        let request = request(
            r#"{"request_id":"stealth-gate","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"hidden","card_type":"MINION","attack":2,"health":2,"can_attack":true,"stealth":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(assert_exact_oracle_state(&request.state).is_err());
        assert!(
            legal_actions(&request.state)
                .expect("partial combat primitive")
                .iter()
                .any(|candidate| candidate.action_id() == "attack:hidden:oh")
        );
    }

    #[test]
    fn freeze_and_all_minion_damage_compounds_resolve_in_order() {
        let frostbolt = request(
            r#"{
              "request_id":"frostbolt-compound",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,
                  "hand":[{"entity_id":"frostbolt","card_type":"SPELL","cost":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":3,"target":"any_character"},{"kind":"freeze","target":"any_character"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"target","card_type":"MINION","attack":3,"health":4,"can_attack":true,"attacks_remaining":1}]}}
            }"#,
        );
        let frostbolt_action = legal_actions(&frostbolt.state)
            .expect("Frostbolt actions")
            .into_iter()
            .find(|action| action.action_id() == "play_card:frostbolt:target")
            .expect("targeted Frostbolt");
        let (after_frostbolt, _) =
            apply_action(&frostbolt.state, &frostbolt_action).expect("apply Frostbolt");
        let frozen = &after_frostbolt.opponent.board[0];
        assert_eq!(frozen.current_health, 1);
        assert!(frozen.frozen);
        assert!(!frozen.can_attack);

        let fissure = request(
            r#"{
              "request_id":"fissure-compound",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},"mana":2,"max_mana":2,"spell_power":1,
                  "hand":[{"entity_id":"fissure","card_type":"SPELL","cost":2,"effect_coverage":"exact","effects":[{"kind":"damage_all_minions","amount":1},{"kind":"gain_hero_attack","amount":3}]}],
                  "board":[{"entity_id":"friendly-minion","card_type":"MINION","health":2}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"enemy-minion","card_type":"MINION","health":3}]}}
            }"#,
        );
        let fissure_action = legal_actions(&fissure.state)
            .expect("Fissure actions")
            .into_iter()
            .find(|action| action.action_id() == "play_card:fissure:")
            .expect("untargeted Fissure");
        let (after_fissure, _) =
            apply_action(&fissure.state, &fissure_action).expect("apply Fissure");
        assert!(after_fissure.friendly.board.is_empty());
        assert_eq!(after_fissure.opponent.board[0].current_health, 1);
        assert_eq!(after_fissure.friendly.hero.attack, 3);
        assert!(after_fissure.friendly.hero.can_attack);
    }

    #[test]
    fn location_placement_is_inert_and_confirmed_activation_consumes_charges() {
        let request = request(
            r#"{
              "request_id":"location-activation",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"depths","card_id":"CORE_REV_990","card_type":"LOCATION","cost":1,"health":2,"current_health":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"any_minion"},{"kind":"buff_attack","amount":2,"target":"any_minion"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":3}]}}
            }"#,
        );
        let placement = legal_actions(&request.state)
            .expect("location placement")
            .into_iter()
            .find(|action| action.action_id() == "play_card:depths::position=1")
            .expect("one legal placement");
        let (placed, _) = apply_action(&request.state, &placement).expect("place Location");
        assert_eq!(placed.opponent.board[0].current_health, 3);
        assert_eq!(placed.opponent.board[0].attack, 1);
        assert!(
            legal_actions(&placed)
                .expect("post-placement actions")
                .iter()
                .all(|action| action.kind != ActionKind::LocationActivate)
        );

        let activation = Action::new(
            ActionKind::LocationActivate,
            "depths",
            "target",
            "CORE_REV_990",
        );
        assert!(apply_action(&placed, &activation).is_err());
        let (after_first, _) = apply_caller_confirmed_action(&placed, &activation)
            .expect("confirmed first activation");
        assert_eq!(after_first.friendly.board[0].current_health, 1);
        assert_eq!(after_first.opponent.board[0].current_health, 2);
        assert_eq!(after_first.opponent.board[0].attack, 3);
        assert!(
            legal_actions(&after_first)
                .expect("post-activation actions")
                .iter()
                .all(|action| action.kind != ActionKind::LocationActivate)
        );

        let (after_second, _) = apply_caller_confirmed_action(&after_first, &activation)
            .expect("confirmed final activation");
        assert!(after_second.friendly.board.is_empty());
        assert_eq!(after_second.opponent.board[0].current_health, 1);
        assert_eq!(after_second.opponent.board[0].attack, 5);
    }

    #[test]
    fn random_enemy_minion_damage_emits_exact_branches_and_can_hit_stealth() {
        let request = request(
            r#"{
              "request_id":"random-target",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"sleet","card_id":"CATA_485","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"},{"kind":"damage","amount":1,"target":"enemy_minion","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"open","card_type":"MINION","attack":1,"health":3},{"entity_id":"hidden","card_type":"MINION","attack":1,"health":3,"stealth":true}]}}
            }"#,
        );
        let spell = legal_actions(&request.state)
            .expect("Sleet Storm actions")
            .into_iter()
            .find(|action| action.action_id() == "play_card:sleet:oh")
            .expect("player-selected hero target");
        let outcomes = apply_action_outcomes(&request.state, &spell).expect("chance outcomes");
        assert_eq!(outcomes.len(), 2);
        let probability_sum = outcomes
            .iter()
            .try_fold(
                ExactProbability::new(0, 1).expect("zero"),
                |sum, outcome| sum.add(outcome.probability),
            )
            .expect("probability sum");
        assert_eq!(probability_sum, ExactProbability::CERTAIN);
        for outcome in &outcomes {
            assert_eq!(
                outcome.probability,
                ExactProbability::new(1, 2).expect("half")
            );
            assert_eq!(outcome.state.opponent.hero.current_health, 28);
            assert!(outcome.state.friendly.hand.is_empty());
            assert_eq!(outcome.state.friendly.mana, 0);
        }
        assert!(outcomes.iter().any(|outcome| {
            outcome
                .state
                .opponent
                .board
                .iter()
                .find(|card| card.entity_id.as_ref() == "hidden")
                .is_some_and(|card| card.current_health == 2)
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome
                .state
                .opponent
                .board
                .iter()
                .find(|card| card.entity_id.as_ref() == "open")
                .is_some_and(|card| card.current_health == 2)
        }));
        assert!(apply_action(&request.state, &spell).is_err());
    }

    #[test]
    fn location_placement_stays_deterministic_beside_a_random_deathrattle() {
        let request = request(
            r#"{
              "request_id":"location-random-board",
              "state":{"state_id":"s","turn":2,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":2,
                  "hand":[{"entity_id":"place","card_id":"TEST_LOCATION","card_type":"LOCATION","cost":1,"health":2,"current_health":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_minion"}]}],
                  "board":[{"entity_id":"rattle","card_type":"MINION","attack":1,"health":1,"effect_coverage":"exact","effects":[{"kind":"damage","trigger":"deathrattle","amount":1,"target":"enemy_character","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":2}]}}
            }"#,
        );
        let placement = action(&request.state, "play_card:place::position=2");
        assert!(!action_has_random_resolution(&request.state, &placement));
        let (after, _) = apply_action(&request.state, &placement).expect("place Location");
        assert_eq!(after.friendly.board.len(), 2);
        assert_eq!(after.friendly.board[1].entity_id.as_ref(), "place");
        assert_eq!(after.opponent.board[0].current_health, 2);
    }

    #[test]
    fn random_deathrattle_draw_preserves_exact_owner_deck_branches() {
        let mut request = request(
            r#"{
              "request_id":"random-deathrattle-draw",
              "state":{"state_id":"s","turn":4,"active_player_id":"o","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"deck_size":2,"deck_identity_complete":true,
                  "known_deck":[
                    {"card_id":"HIGH_A","count":1,"origin":"started_in_deck","card_type":"MINION","cost":7,"name":"A"},
                    {"card_id":"HIGH_B","count":1,"origin":"started_in_deck","card_type":"MINION","cost":8,"name":"B"}
                  ],
                  "board":[{"entity_id":"drawbot","card_id":"EDR_485","card_type":"MINION","attack":1,"health":1,"effect_coverage":"generic","effects":[{"kind":"draw_from_pool","trigger":"deathrattle","target":"none","count":1,"random":true,"pool_selection":"uniform_random","pool_destination":"hand","offer_count":1,"with_replacement":false,"pool":{"source":"owner_deck","cost_min":7,"card_types":["MINION"]}}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"attacker","card_type":"MINION","attack":2,"health":3,"can_attack":true,"attacks_remaining":1,"attacks_remaining_known":true}]}}
            }"#,
        );
        let effect = &mut Arc::make_mut(&mut request.state.friendly.board[0].effects)[0];
        effect.resolved_pool = vec![
            resolved_minion_candidate("HIGH_A", 101, 7),
            resolved_minion_candidate("HIGH_B", 102, 8),
        ]
        .into();
        effect.resolved_pool_population = 2;
        effect.resolved_pool_exact = true;

        let attack = action(&request.state, "attack:attacker:drawbot");
        assert!(action_has_random_resolution(&request.state, &attack));
        let outcomes = apply_action_outcomes(&request.state, &attack)
            .expect("random Deathrattle draw outcomes");
        assert_eq!(outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&outcomes),
            ExactProbability::CERTAIN
        );
        assert!(outcomes.iter().all(|outcome| {
            outcome.probability == ExactProbability::new(1, 2).expect("half")
                && outcome.state.friendly.deck_size == 1
                && outcome.state.friendly.hand.len() == 1
                && outcome
                    .state
                    .friendly
                    .graveyard
                    .iter()
                    .any(|card| card.entity_id.as_ref() == "drawbot")
        }));
        assert_eq!(
            outcomes
                .iter()
                .map(|outcome| outcome.state.friendly.hand[0].card_id.as_ref())
                .collect::<BTreeSet<_>>(),
            BTreeSet::from(["HIGH_A", "HIGH_B"])
        );
    }

    #[test]
    fn deterministic_damage_can_branch_through_a_random_frenzy() {
        let request = request(
            r#"{
              "request_id":"random-frenzy",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":4,
                  "hand":[{"entity_id":"ping","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_minion"}]}],
                  "board":[{"entity_id":"ally","card_type":"MINION","attack":2,"health":2}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"frenzy","card_type":"MINION","attack":1,"health":3,"effect_coverage":"exact","effects":[{"kind":"damage","trigger":"frenzy","amount":1,"target":"enemy_character","random":true}]}]}}
            }"#,
        );
        let ping = action(&request.state, "play_card:ping:frenzy");
        let outcomes = apply_action_outcomes(&request.state, &ping)
            .expect("deterministic hit with random Frenzy outcomes");
        assert_eq!(outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&outcomes),
            ExactProbability::CERTAIN
        );
        assert!(outcomes.iter().all(|outcome| {
            outcome.state.opponent.board[0].current_health == 2
                && outcome.state.opponent.board[0].effects.is_empty()
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome.state.friendly.hero.current_health == 29
                && outcome.state.friendly.board[0].current_health == 2
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome.state.friendly.hero.current_health == 30
                && outcome.state.friendly.board[0].current_health == 1
        }));
    }

    #[test]
    fn hero_attack_after_trigger_branches_over_friendly_minions() {
        let request = request(
            r#"{
              "request_id":"random-after-hero-attack",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":1,"health":30,"can_attack":true,"attacks_remaining":1,"attacks_remaining_known":true,"tags":{"NUM_ATTACKS_THIS_TURN":0}},
                  "board":[
                    {"entity_id":"ally","card_type":"MINION","attack":2,"health":2},
                    {"entity_id":"engine","card_id":"CATA_467","card_type":"MINION","attack":1,"health":3,"effect_coverage":"generic","effects":[{"kind":"buff_attack","trigger":"after_hero_attack","amount":2,"target":"friendly_minion","random":true}]}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let outcomes =
            apply_action_outcomes(&request.state, &action(&request.state, "attack:fh:oh"))
                .expect("random after-Hero-attack outcomes");
        assert_eq!(outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&outcomes),
            ExactProbability::CERTAIN
        );
        assert!(
            outcomes
                .iter()
                .all(|outcome| outcome.state.opponent.hero.current_health == 29)
        );
        assert_eq!(
            outcomes
                .iter()
                .map(|outcome| {
                    (
                        outcome.state.friendly.board[0].attack,
                        outcome.state.friendly.board[1].attack,
                    )
                })
                .collect::<BTreeSet<_>>(),
            BTreeSet::from([(2, 3), (4, 1)])
        );
    }

    #[test]
    fn hero_power_after_trigger_branches_after_payment_and_resolution() {
        let request = request(
            r#"{
              "request_id":"random-after-power",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,"hero_power_available":true,
                  "hero_power":{"entity_id":"power","card_type":"HERO_POWER","cost":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_hero"}]},
                  "board":[{"entity_id":"dragon","card_id":"CORE_DRG_256","card_type":"MINION","attack":8,"health":8,"effect_coverage":"generic","effects":[{"kind":"damage","trigger":"after_hero_power","amount":5,"target":"enemy_character","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":6}]}}
            }"#,
        );
        let outcomes = apply_action_outcomes(
            &request.state,
            &action(&request.state, "hero_power:power:oh"),
        )
        .expect("random after-Hero-Power outcomes");
        assert_eq!(outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&outcomes),
            ExactProbability::CERTAIN
        );
        assert!(outcomes.iter().all(|outcome| {
            outcome.state.friendly.mana == 0 && !outcome.state.friendly.hero_power_available
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome.state.opponent.hero.current_health == 24
                && outcome.state.opponent.board[0].current_health == 6
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome.state.opponent.hero.current_health == 29
                && outcome.state.opponent.board[0].current_health == 1
        }));
    }

    #[test]
    fn direct_random_hero_power_and_location_emit_public_outcomes() {
        let power_request = request(
            r#"{
              "request_id":"random-power",
              "state":{"state_id":"p","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,"hero_power_available":true,
                  "hero_power":{"entity_id":"power","card_type":"HERO_POWER","cost":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_character","random":true}]}},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":3}]}}
            }"#,
        );
        let power_outcomes = apply_action_outcomes(
            &power_request.state,
            &action(&power_request.state, "hero_power:power:"),
        )
        .expect("direct random Hero Power outcomes");
        assert_eq!(power_outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&power_outcomes),
            ExactProbability::CERTAIN
        );

        let location_request = request(
            r#"{
              "request_id":"random-location",
              "state":{"state_id":"l","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"place","card_id":"RANDOM_LOCATION","card_type":"LOCATION","health":2,"current_health":2,"durability":2,"current_durability":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_character","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":3}]}}
            }"#,
        );
        let location_outcomes = apply_caller_confirmed_action_outcomes(
            &location_request.state,
            &Action::new(ActionKind::LocationActivate, "place", "", "RANDOM_LOCATION"),
        )
        .expect("direct random Location outcomes");
        assert_eq!(location_outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&location_outcomes),
            ExactProbability::CERTAIN
        );
        assert!(location_outcomes.iter().all(|outcome| {
            outcome.state.friendly.board[0].current_health == 1
                && outcome.state.friendly.board[0].current_durability == 1
        }));
    }

    #[test]
    fn random_turn_end_trigger_resolves_before_the_player_switch() {
        let request = request(
            r#"{
              "request_id":"random-turn-end",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":4,
                  "board":[{"entity_id":"engine","card_id":"CORE_YOP_034","card_type":"MINION","attack":7,"health":7,"effect_coverage":"generic","effects":[{"kind":"damage","trigger":"turn_end","amount":5,"target":"enemy_character","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":6}]}}
            }"#,
        );
        let outcomes = apply_action_outcomes(&request.state, &Action::end_turn())
            .expect("random turn-end outcomes");
        assert_eq!(outcomes.len(), 2);
        assert_eq!(
            outcome_probability_sum(&outcomes),
            ExactProbability::CERTAIN
        );
        assert!(outcomes.iter().all(|outcome| {
            outcome.ended_turn
                && outcome.state.active_player_id.as_ref() == "o"
                && outcome.state.friendly.mana == 0
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome.state.opponent.hero.current_health == 25
                && outcome.state.opponent.board[0].current_health == 6
        }));
        assert!(outcomes.iter().any(|outcome| {
            outcome.state.opponent.hero.current_health == 30
                && outcome.state.opponent.board[0].current_health == 1
        }));
    }

    #[test]
    fn death_batch_uses_active_player_order_before_queueing_new_deaths() {
        let mut request = request(
            r#"{
              "request_id":"death-batch-order",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"first","card_type":"MINION","attack":1,"health":1,"effect_coverage":"exact","effects":[{"kind":"summon","trigger":"deathrattle","target":"none","count":1,"card_id":"TOKEN","name":"Token","attack":1,"health":1}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"second","card_type":"MINION","attack":1,"health":1,"effect_coverage":"exact","effects":[{"kind":"damage","trigger":"deathrattle","amount":1,"target":"enemy_minion","random":true}]}]}}
            }"#,
        );
        request.state.friendly.board[0].current_health = 0;
        request.state.opponent.board[0].current_health = 0;
        let outcomes = resolve_death_queue_outcomes(&request.state).expect("ordered death batch");
        assert_eq!(outcomes.len(), 1);
        assert_eq!(outcomes[0].probability, ExactProbability::CERTAIN);
        assert!(outcomes[0].state.friendly.board.is_empty());
        assert!(
            outcomes[0]
                .state
                .friendly
                .graveyard
                .iter()
                .any(|card| card.card_id.as_ref() == "TOKEN")
        );
        assert!(
            outcomes[0]
                .state
                .opponent
                .graveyard
                .iter()
                .any(|card| card.entity_id.as_ref() == "second")
        );
    }

    #[test]
    fn random_effect_fizzles_after_the_selected_effect_removes_its_only_target() {
        let request = request(
            r#"{
              "request_id":"random-fizzle",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"sleet","card_id":"CATA_485","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"},{"kind":"damage","amount":1,"target":"enemy_minion","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"only","card_type":"MINION","attack":1,"health":2}]}}
            }"#,
        );
        let spell = legal_actions(&request.state)
            .expect("Sleet Storm actions")
            .into_iter()
            .find(|action| action.action_id() == "play_card:sleet:only")
            .expect("selected minion target");
        let outcomes = apply_action_outcomes(&request.state, &spell).expect("fizzle outcome");
        assert_eq!(outcomes.len(), 1);
        assert_eq!(outcomes[0].probability, ExactProbability::CERTAIN);
        assert!(outcomes[0].state.opponent.board.is_empty());
        assert_eq!(outcomes[0].state.opponent.hero.current_health, 30);
    }

    #[test]
    fn temporary_mana_can_exceed_permanent_crystals_for_the_current_turn() {
        let request = request(
            r#"{
              "request_id":"temporary-mana",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"coin","card_id":"GAME_005","card_type":"SPELL","cost":0,"effect_coverage":"exact","effects":[{"kind":"gain_mana","amount":1,"target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "play_card:coin:"))
            .expect("temporary mana play");
        assert_eq!(after.friendly.mana, 2);
        assert_eq!(after.friendly.max_mana, 1);
    }

    #[test]
    fn ending_the_turn_expires_all_unspent_current_turn_mana() {
        let request = request(
            r#"{
              "request_id":"end-turn-mana-expiry",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":2},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, ended) = apply_action(&request.state, &Action::end_turn()).expect("end turn");
        assert!(ended);
        assert_eq!(after.friendly.mana, 0);
        assert_eq!(after.friendly.max_mana, 2);
    }

    #[test]
    fn arcane_tripwire_probabilities_sum_to_one_and_shovel_draws_only_after_shuffle() {
        let request = request(
            r#"{
              "request_id":"tripwire-shovel",
              "state":{"state_id":"s","turn":5,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":1,"health":30,"can_attack":true,"attacks_remaining":1,"attacks_remaining_known":true},"mana":1,"max_mana":5,
                  "hand":[{"entity_id":"tripwire","card_id":"JAIL_881","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[
                    {"kind":"damage_split","amount":5,"target":"all_enemy_characters","random":true},
                    {"kind":"shuffle_repeat_spell","target":"none","count":2,"card_id":"JAIL_881_REPEAT","name":"Arcane Tripwire Echo"}
                  ]}],
                  "weapon":{"entity_id":"shovel","card_id":"JAIL_380","card_type":"WEAPON","attack":1,"durability":1,"current_durability":1,"effect_coverage":"exact","effects":[{"kind":"draw_non_starting_spell_on_weapon_break","target":"none"}]}},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"victim","card_type":"MINION","attack":1,"health":2}]}}
            }"#,
        );

        let outcomes = apply_action_outcomes(
            &request.state,
            &action(&request.state, "play_card:tripwire:"),
        )
        .expect("split-damage outcomes");
        assert!(outcomes.len() > 1);
        let probability_sum = outcomes
            .iter()
            .try_fold(
                ExactProbability::new(0, 1).expect("zero"),
                |sum, outcome| sum.add(outcome.probability),
            )
            .expect("probability sum");
        assert_eq!(probability_sum, ExactProbability::CERTAIN);
        for outcome in &outcomes {
            assert_eq!(outcome.state.friendly.deck_size, 2);
            assert_eq!(
                outcome
                    .state
                    .friendly
                    .known_deck
                    .iter()
                    .find(|known| known.card_id.as_ref() == "JAIL_881_REPEAT")
                    .map(|known| known.count),
                Some(2)
            );
        }

        let after_tripwire = &outcomes[0].state;
        let (after_break, _) =
            apply_action(after_tripwire, &action(after_tripwire, "attack:fh:oh"))
                .expect("break shovel after the generated spell exists");
        assert!(after_break.friendly.weapon.is_none());
        assert_eq!(after_break.friendly.deck_size, 1);
        assert_eq!(after_break.friendly.hand.len(), 1);
        assert_eq!(
            after_break.friendly.hand[0].card_id.as_ref(),
            "JAIL_881_REPEAT"
        );
    }

    #[test]
    fn shovel_break_without_a_generated_spell_draws_nothing() {
        let request = request(
            r#"{
              "request_id":"empty-shovel",
              "state":{"state_id":"s","turn":2,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":1,"health":30,"can_attack":true,"attacks_remaining":1,"attacks_remaining_known":true},
                  "weapon":{"entity_id":"shovel","card_id":"JAIL_380","card_type":"WEAPON","attack":1,"durability":1,"current_durability":1,"effect_coverage":"exact","effects":[{"kind":"draw_non_starting_spell_on_weapon_break","target":"none"}]}},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "attack:fh:oh"))
            .expect("empty shovel attack");
        assert!(after.friendly.weapon.is_none());
        assert!(after.friendly.hand.is_empty());
        assert_eq!(after.friendly.deck_size, 0);
    }

    #[test]
    fn beast_tripwire_summons_from_its_typed_pool_and_shuffles_two_echoes() {
        let mut request = request(
            r#"{
              "request_id":"beast-tripwire",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f","mode":"Ranked",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,
                  "hand":[{"entity_id":"tripwire","card_id":"JAIL_879","card_type":"SPELL","cost":2,"effect_coverage":"exact","effects":[
                    {"kind":"summon_from_pool","target":"none","random":true,"pool_selection":"uniform_random","pool_destination":"battlefield","offer_count":1,"with_replacement":true,"pool":{"source":"current_format","cost_min":5,"cost_max":5,"card_types":["MINION"]}},
                    {"kind":"shuffle_repeat_spell","target":"none","count":2,"card_id":"JAIL_879_REPEAT","name":"Beast Tripwire Echo"}
                  ]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let effect = &mut Arc::make_mut(&mut request.state.friendly.hand[0].effects)[0];
        effect.resolved_pool = vec![ResolvedPoolCandidate {
            card: crate::model::ResolvedPoolCard {
                card_id: Arc::from("TEST_FIVE_COST_BEAST"),
                dbf_id: 42,
                name: Arc::from("Test Beast"),
                card_type: CardType::Minion,
                cost: 5,
                attack: 5,
                health: 4,
                durability: 0,
                rarity_id: 1,
                keywords: Vec::new().into(),
                text: Arc::from(""),
            },
            weight: 1,
        }]
        .into();
        effect.resolved_pool_population = 1;
        effect.resolved_pool_exact = true;

        let outcomes = apply_action_outcomes(
            &request.state,
            &action(&request.state, "play_card:tripwire:"),
        )
        .expect("Beast Tripwire outcome");
        assert_eq!(outcomes.len(), 1);
        assert_eq!(outcomes[0].probability, ExactProbability::CERTAIN);
        assert_eq!(outcomes[0].state.friendly.board.len(), 1);
        assert_eq!(
            outcomes[0].state.friendly.board[0].card_id.as_ref(),
            "TEST_FIVE_COST_BEAST"
        );
        assert_eq!(
            (
                outcomes[0].state.friendly.board[0].attack,
                outcomes[0].state.friendly.board[0].current_health,
            ),
            (5, 4)
        );
        assert_eq!(outcomes[0].state.friendly.deck_size, 2);
        assert_eq!(outcomes[0].state.friendly.known_deck[0].count, 2);
        assert_eq!(
            outcomes[0].state.friendly.known_deck[0].card_id.as_ref(),
            "JAIL_879_REPEAT"
        );
    }

    #[test]
    fn tracking_removes_the_selected_identity_and_deck_count_together() {
        let mut request = request(
            r#"{
              "request_id":"tracking-owner-deck",
              "state":{"state_id":"s","turn":3,"active_player_id":"f","perspective_player_id":"f","mode":"Ranked",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":3,"deck_size":2,"deck_identity_complete":true,
                  "known_deck":[
                    {"card_id":"DECK_A","count":1,"origin":"started_in_deck","card_type":"MINION","cost":2,"name":"A"},
                    {"card_id":"DECK_B","count":1,"origin":"started_in_deck","card_type":"MINION","cost":5,"name":"B"}
                  ],
                  "hand":[{"entity_id":"tracking","card_id":"CORE_DS1_184","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"discover_from_pool","target":"none","random":true,"pool_selection":"discover","pool_destination":"hand","offer_count":2,"with_replacement":false,"pool":{"source":"owner_deck"}}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let effect = &mut Arc::make_mut(&mut request.state.friendly.hand[0].effects)[0];
        effect.resolved_pool = [
            ("DECK_A", 1_u64, 2_u16, 2_u16, 2_u16),
            ("DECK_B", 2_u64, 5_u16, 5_u16, 5_u16),
        ]
        .into_iter()
        .map(
            |(card_id, dbf_id, cost, attack, health)| ResolvedPoolCandidate {
                card: crate::model::ResolvedPoolCard {
                    card_id: Arc::from(card_id),
                    dbf_id,
                    name: Arc::from(card_id),
                    card_type: CardType::Minion,
                    cost,
                    attack,
                    health,
                    durability: 0,
                    rarity_id: 1,
                    keywords: Vec::new().into(),
                    text: Arc::from(""),
                },
                weight: 1,
            },
        )
        .collect::<Vec<_>>()
        .into();
        effect.resolved_pool_population = 2;
        effect.resolved_pool_exact = true;

        let outcomes = apply_action_outcomes(
            &request.state,
            &action(&request.state, "play_card:tracking:"),
        )
        .expect("Tracking outcome");
        assert_eq!(outcomes.len(), 1);
        let after = &outcomes[0].state.friendly;
        assert_eq!(after.deck_size, 1);
        assert_eq!(
            after
                .known_deck
                .iter()
                .map(|known| known.count)
                .sum::<u16>(),
            1
        );
        assert_eq!(after.hand.len(), 1);
        assert!(matches!(
            after.hand[0].card_id.as_ref(),
            "DECK_A" | "DECK_B"
        ));
        assert!(
            after
                .known_deck
                .iter()
                .all(|known| known.card_id != after.hand[0].card_id)
        );
    }

    #[test]
    fn confront_the_tolvir_replays_visible_one_cost_minion_and_spell_history() {
        let request = request(
            r#"{
              "request_id":"tolvir-replay",
              "state":{"state_id":"s","turn":6,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":6,
                  "hand":[{"entity_id":"tolvir","card_id":"CATA_560","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"replay_one_cost_cards","target":"none"}]}],
                  "graveyard":[
                    {"entity_id":"old-minion","card_id":"ONE_MINION","card_type":"MINION","cost":1,"attack":2,"health":1,"effect_coverage":"generic"},
                    {"entity_id":"old-shot","card_id":"CORE_DS1_185","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"}]}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "play_card:tolvir:"))
            .expect("visible one-cost replay");
        assert_eq!(after.friendly.board.len(), 1);
        assert_eq!(after.friendly.board[0].card_id.as_ref(), "ONE_MINION");
        assert_eq!(after.opponent.hero.current_health, 28);
        assert!(
            after
                .friendly
                .graveyard
                .iter()
                .any(|card| card.entity_id.starts_with("replay-tolvir-old-shot"))
        );
    }

    #[test]
    fn repeated_targeted_spell_fizzles_after_the_first_copy_removes_its_target() {
        let request = request(
            r#"{
              "request_id":"repeat-target-fizzle",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"shot","card_id":"CORE_DS1_185","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"}]}],
                  "board":[{"entity_id":"doubler","card_id":"TLC_836","card_type":"MINION","attack":2,"health":4,"effect_coverage":"exact","effects":[{"kind":"double_one_cost_cards","amount":2,"target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":2}]}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:shot:target"),
        )
        .expect("second targeted copy should fizzle");
        assert!(after.opponent.board.is_empty());
        assert_eq!(after.opponent.hero.current_health, 30);
    }

    #[test]
    fn repeated_random_spell_fizzles_missing_selected_target_without_losing_outcomes() {
        let request = request(
            r#"{
              "request_id":"repeat-random-target-fizzle",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"sleet","card_id":"CATA_485","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"},{"kind":"damage","amount":1,"target":"enemy_minion","random":true}]}],
                  "board":[{"entity_id":"doubler","card_id":"TLC_836","card_type":"MINION","attack":2,"health":4,"effect_coverage":"exact","effects":[{"kind":"double_one_cost_cards","amount":2,"target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":2}]}}
            }"#,
        );
        let spell = action(&request.state, "play_card:sleet:target");
        let outcomes = apply_action_outcomes(&request.state, &spell)
            .expect("chance path should preserve the fizzled repeated copy");
        assert_eq!(outcomes.len(), 1);
        assert_eq!(outcomes[0].probability, ExactProbability::CERTAIN);
        assert!(outcomes[0].state.opponent.board.is_empty());
        assert_eq!(outcomes[0].state.opponent.hero.current_health, 30);
    }

    #[test]
    fn generic_draw_tracks_count_burn_and_fatigue_without_inventing_identity() {
        let request = request(
            r#"{
              "request_id":"generic-draw",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":4,"deck_size":1,
                  "hand":[{"entity_id":"draw","card_id":"CORE_CS2_023","card_type":"SPELL","cost":3,"effect_coverage":"generic","effects":[{"kind":"draw","target":"none","count":2}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "play_card:draw:"))
            .expect("generic draw transition");
        assert_eq!(after.friendly.deck_size, 0);
        assert_eq!(after.friendly.fatigue, 1);
        assert_eq!(after.friendly.hero.current_health, 29);
        assert_eq!(after.friendly.hand.len(), 1);
        assert_eq!(after.friendly.hand[0].card_id.as_ref(), "UNKNOWN_DRAW");
        assert_eq!(
            after.friendly.hand[0].effect_coverage,
            EffectCoverage::Unsupported
        );
    }

    #[test]
    fn draw_until_hand_count_uses_the_post_play_hand_size() {
        let request = request(
            r#"{
              "request_id":"draw-until-three",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,"deck_size":4,
                  "hand":[
                    {"entity_id":"retriever","card_id":"TIME_601","card_type":"MINION","cost":2,"attack":2,"health":2,"effect_coverage":"generic","effects":[{"kind":"draw_until_hand_count","target":"none","count":3}]},
                    {"entity_id":"kept","card_id":"KEPT","card_type":"MINION","cost":4,"attack":4,"health":4}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:retriever::position=1"),
        )
        .expect("draw-until transition");
        assert_eq!(after.friendly.hand.len(), 3);
        assert_eq!(after.friendly.deck_size, 2);
        assert_eq!(after.friendly.board[0].card_id.as_ref(), "TIME_601");
    }

    #[test]
    fn draw_both_players_updates_each_deck_without_conflating_owners() {
        let request = request(
            r#"{
              "request_id":"draw-both",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,"deck_size":1,
                  "hand":[{"entity_id":"vendor","card_id":"CORE_DMF_067","card_type":"MINION","cost":2,"attack":2,"health":3,"effect_coverage":"generic","effects":[{"kind":"draw_both_players","target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"deck_size":1}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:vendor::position=1"),
        )
        .expect("both-player draw transition");
        assert_eq!(after.friendly.deck_size, 0);
        assert_eq!(after.opponent.deck_size, 0);
        assert_eq!(after.friendly.hand.len(), 1);
        assert_eq!(after.opponent.hand.len(), 1);
        assert_ne!(
            after.friendly.hand[0].entity_id,
            after.opponent.hand[0].entity_id
        );
    }

    #[test]
    fn filtered_draw_without_replacement_preserves_duplicate_card_copies() {
        let mut request = request(
            r#"{
              "request_id":"filtered-draw-copies",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f","mode":"Ranked",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,"deck_size":3,"deck_identity_complete":true,
                  "known_deck":[
                    {"card_id":"MURLOC_A","count":2,"origin":"started_in_deck","card_type":"MINION","cost":1,"name":"A"},
                    {"card_id":"MURLOC_B","count":1,"origin":"started_in_deck","card_type":"MINION","cost":2,"name":"B"}
                  ],
                  "hand":[{"entity_id":"ravager","card_id":"TSC_034","card_type":"MINION","cost":2,"attack":2,"health":2,"effect_coverage":"generic","effects":[{"kind":"draw_from_pool","target":"none","count":2,"random":true,"pool_selection":"uniform_random","pool_destination":"hand","offer_count":1,"with_replacement":false,"pool":{"source":"owner_deck","card_types":["MINION"],"minion_type_ids":[14]}}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let effect = &mut Arc::make_mut(&mut request.state.friendly.hand[0].effects)[0];
        effect.resolved_pool = [
            ("MURLOC_A", 1_u64, 1_u16, 1_u16, 1_u16, 2_u32),
            ("MURLOC_B", 2_u64, 2_u16, 2_u16, 2_u16, 1_u32),
        ]
        .into_iter()
        .map(
            |(card_id, dbf_id, cost, attack, health, weight)| ResolvedPoolCandidate {
                card: crate::model::ResolvedPoolCard {
                    card_id: Arc::from(card_id),
                    dbf_id,
                    name: Arc::from(card_id),
                    card_type: CardType::Minion,
                    cost,
                    attack,
                    health,
                    durability: 0,
                    rarity_id: 1,
                    keywords: Vec::new().into(),
                    text: Arc::from(""),
                },
                weight,
            },
        )
        .collect::<Vec<_>>()
        .into();
        effect.resolved_pool_population = 3;
        effect.resolved_pool_exact = true;

        let outcomes = apply_action_outcomes(
            &request.state,
            &action(&request.state, "play_card:ravager::position=1"),
        )
        .expect("filtered draw outcomes");
        assert_eq!(outcomes.len(), 3);
        assert!(outcomes.iter().all(|outcome| {
            outcome.state.friendly.deck_size == 1
                && outcome.state.friendly.hand.len() == 2
                && outcome
                    .state
                    .friendly
                    .known_deck
                    .iter()
                    .map(|known| known.count)
                    .sum::<u16>()
                    == 1
        }));
        let double_a = outcomes
            .iter()
            .find(|outcome| {
                outcome
                    .state
                    .friendly
                    .hand
                    .iter()
                    .all(|card| card.card_id.as_ref() == "MURLOC_A")
            })
            .expect("two copies of the same card remain drawable");
        assert_eq!(double_a.probability, ExactProbability::new(1, 3).unwrap());
    }

    #[test]
    fn weapon_attack_buff_updates_weapon_and_current_hero_attack() {
        let request = request(
            r#"{
              "request_id":"weapon-buff",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":2,"health":30,"can_attack":true,"attacks_remaining":1},"mana":1,"max_mana":4,
                  "weapon":{"entity_id":"bow","card_id":"BOW","card_type":"WEAPON","attack":2,"durability":2,"current_durability":2},
                  "hand":[{"entity_id":"poison","card_id":"CORE_CS2_074","card_type":"SPELL","cost":1,"effect_coverage":"generic","effects":[{"kind":"buff_weapon_attack","amount":2,"target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "play_card:poison:"))
            .expect("weapon buff transition");
        assert_eq!(after.friendly.weapon.as_ref().unwrap().attack, 4);
        assert_eq!(after.friendly.hero.attack, 4);
    }

    #[test]
    fn deterministic_summon_carries_carddefs_token_keywords() {
        let request = request(
            r#"{
              "request_id":"token-keywords",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":4,
                  "hand":[{"entity_id":"sentries","card_id":"BAR_533","card_type":"SPELL","cost":3,"effect_coverage":"generic","effects":[{"kind":"summon","target":"none","count":2,"card_id":"BAR_533t","name":"Thornguard Turtle","attack":1,"health":2,"taunt":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:sentries:"),
        )
        .expect("generic summon transition");
        assert_eq!(after.friendly.board.len(), 2);
        assert!(after.friendly.board.iter().all(|token| token.taunt));
        assert!(
            after
                .friendly
                .board
                .iter()
                .all(|token| token.card_id.as_ref() == "BAR_533t")
        );
    }

    #[test]
    fn death_queue_resolves_generic_deathrattle_after_simultaneous_removal() {
        let request = request(
            r#"{
              "request_id":"deathrattle-draw",
              "state":{"state_id":"s","turn":4,"active_player_id":"o","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"deck_size":1,
                  "board":[{"entity_id":"loot","card_id":"CORE_EX1_096","card_type":"MINION","attack":2,"health":1,"effect_coverage":"generic","effects":[{"kind":"draw","trigger":"deathrattle","target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"attacker","card_type":"MINION","attack":1,"health":3,"can_attack":true,"attacks_remaining":1}]}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "attack:attacker:loot"),
        )
        .expect("Deathrattle attack transition");
        assert!(after.friendly.board.is_empty());
        assert_eq!(after.friendly.graveyard[0].entity_id.as_ref(), "loot");
        assert_eq!(after.friendly.deck_size, 0);
        assert_eq!(after.friendly.hand[0].card_id.as_ref(), "UNKNOWN_DRAW");
    }

    #[test]
    fn after_spell_trigger_resolves_after_each_spell_copy() {
        let request = request(
            r#"{
              "request_id":"after-spell",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":4,
                  "hand":[{"entity_id":"shot","card_id":"CORE_DS1_185","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_hero"}]}],
                  "board":[{"entity_id":"runner","card_id":"BAR_035","card_type":"MINION","attack":3,"health":4,"effect_coverage":"generic","effects":[{"kind":"summon","trigger":"after_spell_cast","target":"none","card_id":"BAR_035t","name":"Swift Hyena","attack":1,"health":1,"rush":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "play_card:shot:oh"))
            .expect("after-spell trigger transition");
        assert_eq!(after.opponent.hero.current_health, 28);
        assert_eq!(after.friendly.board.len(), 2);
        assert_eq!(after.friendly.board[1].card_id.as_ref(), "BAR_035t");
        assert!(after.friendly.board[1].rush);
    }

    #[test]
    fn end_turn_trigger_resolves_before_active_player_switches() {
        let request = request(
            r#"{
              "request_id":"turn-end-draw",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,"deck_size":1,
                  "board":[{"entity_id":"panner","card_id":"WW_391","card_type":"MINION","attack":1,"health":2,"effect_coverage":"generic","effects":[{"kind":"draw","trigger":"turn_end","target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, ended_turn) =
            apply_action(&request.state, &Action::end_turn()).expect("end-turn trigger transition");
        assert!(ended_turn);
        assert_eq!(after.active_player_id.as_ref(), "o");
        assert_eq!(after.friendly.deck_size, 0);
        assert_eq!(after.friendly.hand[0].card_id.as_ref(), "UNKNOWN_DRAW");
    }

    #[test]
    fn generic_destroy_resolves_death_queue_before_followup_state_is_scored() {
        let request = request(
            r#"{
              "request_id":"destroy-and-heal",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":10},"mana":4,"max_mana":4,
                  "hand":[{"entity_id":"siphon","card_id":"CORE_EX1_309","card_type":"SPELL","cost":4,"effect_coverage":"generic","effects":[{"kind":"destroy","target":"any_minion"},{"kind":"heal","amount":3,"target":"friendly_hero"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"target","card_type":"MINION","attack":8,"health":8}]}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:siphon:target"),
        )
        .expect("generic destroy transition");
        assert!(after.opponent.board.is_empty());
        assert_eq!(after.friendly.hero.current_health, 13);
        assert_eq!(after.opponent.graveyard[0].entity_id.as_ref(), "target");
    }

    #[test]
    fn transform_replaces_identity_and_suppresses_original_deathrattle() {
        let request = request(
            r#"{
              "request_id":"transform-token",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":4,
                  "hand":[{"entity_id":"hex","card_id":"CORE_EX1_246","card_type":"SPELL","cost":3,"effect_coverage":"generic","effects":[{"kind":"transform","target":"any_minion","card_id":"hexfrog","name":"Frog","attack":0,"health":1,"taunt":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"target","card_type":"MINION","attack":5,"health":5,"effect_coverage":"exact","effects":[{"kind":"damage","trigger":"deathrattle","amount":5,"target":"friendly_hero"}]}]}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:hex:target"),
        )
        .expect("generic transform transition");
        let frog = &after.opponent.board[0];
        assert_eq!(frog.entity_id.as_ref(), "target");
        assert_eq!(frog.card_id.as_ref(), "hexfrog");
        assert_eq!((frog.attack, frog.current_health), (0, 1));
        assert!(frog.taunt);
        assert!(frog.effects.is_empty());
        assert_eq!(after.friendly.hero.current_health, 30);
    }

    #[test]
    fn deathrattle_can_equip_a_real_carddefs_bound_weapon() {
        let request = request(
            r#"{
              "request_id":"deathrattle-weapon",
              "state":{"state_id":"s","turn":4,"active_player_id":"o","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},
                  "board":[{"entity_id":"tirion","card_id":"CORE_EX1_383","card_type":"MINION","attack":8,"health":8,"effect_coverage":"generic","effects":[{"kind":"equip_weapon","trigger":"deathrattle","target":"none","card_id":"EX1_383t","name":"Ashbringer","attack":5,"durability":3}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"attacker","card_type":"MINION","attack":8,"health":10,"can_attack":true,"attacks_remaining":1}]}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "attack:attacker:tirion"),
        )
        .expect("weapon Deathrattle transition");
        let weapon = after.friendly.weapon.as_ref().expect("Ashbringer equipped");
        assert_eq!(weapon.card_id.as_ref(), "EX1_383t");
        assert_eq!((weapon.attack, weapon.current_durability), (5, 3));
        assert_eq!(after.friendly.hero.attack, 5);
        assert!(
            !after.friendly.hero.can_attack,
            "it is still the opponent's turn"
        );
    }

    #[test]
    fn generic_keyword_grant_changes_the_selected_entity() {
        let request = request(
            r#"{
              "request_id":"grant-shield",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":4,"max_mana":4,
                  "hand":[{"entity_id":"protector","card_id":"CORE_EX1_362","card_type":"MINION","cost":4,"attack":4,"health":4,"effect_coverage":"generic","effects":[{"kind":"grant_keywords","target":"friendly_minion","divine_shield":true}]}],
                  "board":[{"entity_id":"ally","card_type":"MINION","attack":2,"health":2}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:protector:ally:position=2"),
        )
        .expect("keyword grant transition");
        assert!(after.friendly.board[0].divine_shield);
    }

    #[test]
    fn board_destroy_removes_locations_and_resolves_minion_deaths() {
        let request = request(
            r#"{
              "request_id":"destroy-board",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":8,"max_mana":8,
                  "hand":[{"entity_id":"nether","card_id":"CORE_EX1_312","card_type":"SPELL","cost":8,"effect_coverage":"generic","effects":[{"kind":"destroy_all_minions_and_locations","target":"none"}]}],
                  "board":[{"entity_id":"ally","card_type":"MINION","attack":2,"health":2},{"entity_id":"place","card_type":"LOCATION","health":2,"current_health":2}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"enemy","card_type":"MINION","attack":3,"health":3}]}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "play_card:nether:"))
            .expect("board destroy transition");
        assert!(after.friendly.board.is_empty());
        assert!(after.opponent.board.is_empty());
        assert_eq!(
            after.friendly.graveyard.len(),
            3,
            "spell, minion, and location"
        );
    }

    #[test]
    fn spellburst_is_consumed_after_the_first_spell() {
        let request = request(
            r#"{
              "request_id":"spellburst-once",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,
                  "hand":[
                    {"entity_id":"shot-a","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_hero"}]},
                    {"entity_id":"shot-b","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_hero"}]}
                  ],
                  "board":[{"entity_id":"student","card_id":"SCH_231","card_type":"MINION","attack":2,"health":3,"effect_coverage":"generic","effects":[{"kind":"buff_attack","trigger":"spellburst","amount":2,"target":"self"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after_first, _) = apply_action(
            &request.state,
            &action(&request.state, "play_card:shot-a:oh"),
        )
        .expect("first spell");
        assert_eq!(after_first.friendly.board[0].attack, 4);
        assert!(after_first.friendly.board[0].effects.is_empty());
        let (after_second, _) =
            apply_action(&after_first, &action(&after_first, "play_card:shot-b:oh"))
                .expect("second spell");
        assert_eq!(after_second.friendly.board[0].attack, 4);
        assert_eq!(after_second.opponent.hero.current_health, 28);
    }

    #[test]
    fn hero_attack_trigger_can_grow_the_equipped_weapon() {
        let request = request(
            r#"{
              "request_id":"weapon-after-attack",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":1,"health":30,"can_attack":true,"attacks_remaining":1,"tags":{"NUM_ATTACKS_THIS_TURN":0}},
                  "weapon":{"entity_id":"blade","card_id":"SCH_622","card_type":"WEAPON","attack":1,"durability":2,"current_durability":2,"effect_coverage":"generic","effects":[{"kind":"buff_attack","trigger":"after_hero_attack","amount":1,"target":"self"}]}},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(&request.state, &action(&request.state, "attack:fh:oh"))
            .expect("hero attack trigger");
        assert_eq!(after.friendly.weapon.as_ref().unwrap().attack, 2);
        assert_eq!(
            after.friendly.weapon.as_ref().unwrap().current_durability,
            1
        );
        assert_eq!(after.friendly.hero.attack, 2);
    }

    #[test]
    fn hero_power_trigger_observes_mana_after_payment() {
        let request = request(
            r#"{
              "request_id":"power-refresh",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":4,
                  "hero_power":{"entity_id":"power","card_type":"HERO_POWER","cost":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_hero"}]},"hero_power_available":true,
                  "board":[{"entity_id":"roach","card_id":"END_008","card_type":"MINION","attack":2,"health":3,"effect_coverage":"generic","effects":[{"kind":"refresh_mana","trigger":"after_hero_power","amount":2,"target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let (after, _) = apply_action(
            &request.state,
            &action(&request.state, "hero_power:power:oh"),
        )
        .expect("hero-power trigger");
        assert_eq!(after.friendly.mana, 2);
        assert_eq!(after.opponent.hero.current_health, 28);
    }

    #[test]
    fn frenzy_fires_only_when_damage_is_taken_and_the_source_survives() {
        let surviving = request(
            r#"{
              "request_id":"frenzy-once",
              "state":{"state_id":"s","turn":4,"active_player_id":"o","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"thrasher","card_id":"BAR_024","card_type":"MINION","attack":2,"health":3,"effect_coverage":"generic","effects":[{"kind":"damage","trigger":"frenzy","amount":3,"target":"enemy_hero"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"attacker","card_type":"MINION","attack":1,"health":4,"can_attack":true,"attacks_remaining":1}]}}
            }"#,
        );
        let (after, _) = apply_action(
            &surviving.state,
            &action(&surviving.state, "attack:attacker:thrasher"),
        )
        .expect("surviving Frenzy transition");
        assert_eq!(after.friendly.board[0].current_health, 2);
        assert!(after.friendly.board[0].effects.is_empty());
        assert_eq!(after.opponent.hero.current_health, 27);

        let lethal = request(
            r#"{
              "request_id":"frenzy-lethal",
              "state":{"state_id":"s","turn":4,"active_player_id":"o","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"thrasher","card_id":"BAR_024","card_type":"MINION","attack":2,"health":3,"effect_coverage":"generic","effects":[{"kind":"damage","trigger":"frenzy","amount":3,"target":"enemy_hero"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"attacker","card_type":"MINION","attack":3,"health":4,"can_attack":true,"attacks_remaining":1}]}}
            }"#,
        );
        let (after_lethal, _) = apply_action(
            &lethal.state,
            &action(&lethal.state, "attack:attacker:thrasher"),
        )
        .expect("lethal damage transition");
        assert_eq!(after_lethal.opponent.hero.current_health, 30);
    }

    #[test]
    fn divine_shield_prevents_frenzy_but_split_damage_can_trigger_it() {
        let shielded = request(
            r#"{
              "request_id":"frenzy-shield",
              "state":{"state_id":"s","turn":4,"active_player_id":"o","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"thrasher","card_id":"BAR_024","card_type":"MINION","attack":2,"health":3,"divine_shield":true,"effect_coverage":"generic","effects":[{"kind":"damage","trigger":"frenzy","amount":3,"target":"enemy_hero"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"attacker","card_type":"MINION","attack":1,"health":4,"can_attack":true,"attacks_remaining":1}]}}
            }"#,
        );
        let (after_shield, _) = apply_action(
            &shielded.state,
            &action(&shielded.state, "attack:attacker:thrasher"),
        )
        .expect("shielded Frenzy transition");
        assert!(!after_shield.friendly.board[0].divine_shield);
        assert_eq!(after_shield.opponent.hero.current_health, 30);
        assert_eq!(after_shield.friendly.board[0].effects.len(), 1);

        let mut split = request(
            r#"{
              "request_id":"frenzy-split",
              "state":{"state_id":"s","turn":4,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":4,
                  "hand":[{"entity_id":"split","card_id":"SPLIT","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage_split","amount":1,"target":"all_enemy_characters","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"thrasher","card_id":"BAR_024","card_type":"MINION","attack":2,"health":3,"effect_coverage":"generic","effects":[{"kind":"damage","trigger":"frenzy","amount":3,"target":"enemy_hero"}]}]}}
            }"#,
        );
        split.state.rng_seed = 7;
        let outcomes =
            apply_action_outcomes(&split.state, &action(&split.state, "play_card:split:"))
                .expect("split-damage Frenzy outcomes");
        assert_eq!(outcomes.len(), 2);
        let minion_hit = outcomes
            .iter()
            .find(|outcome| outcome.state.opponent.board[0].current_health == 2)
            .expect("split damage can hit the Frenzy minion");
        assert_eq!(minion_hit.state.friendly.hero.current_health, 27);
        assert!(minion_hit.state.opponent.board[0].effects.is_empty());
    }

    #[test]
    fn unknown_effect_is_fail_closed() {
        let request = request(
            r#"{
              "request_id":"unsupported",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"x","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"transform","target":"enemy_minion"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"mana":0,"max_mana":0,
                  "board":[{"entity_id":"t","card_type":"MINION","attack":1,"health":1}]}}
            }"#,
        );
        let error = assert_exact_oracle_state(&request.state).expect_err("must reject");
        assert_eq!(error.code(), "unsupported_scope");
    }

    #[test]
    fn stable_lethal_top1_uses_sorted_winning_action() {
        let request = request(
            r#"{
              "request_id":"lethal",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":0,"max_mana":0,
                  "board":[
                    {"entity_id":"b","card_type":"MINION","attack":4,"health":2,"can_attack":true},
                    {"entity_id":"a","card_type":"MINION","attack":3,"health":2,"can_attack":true}
                  ]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":3},"mana":0,"max_mana":0}}
            }"#,
        );
        let cancel = AtomicBool::new(false);
        let proof = prove_lethal(&request.state, 10_000, &cancel).expect("proof");
        assert_eq!(
            proof.winning_first_action_ids,
            vec!["attack:a:oh", "attack:b:oh"]
        );
        let plan = choose_turn_plan(&request.state, &proof, 10_000, &cancel).expect("plan");
        assert_eq!(plan.actions[0].action_id(), "attack:a:oh");
        assert_eq!(plan.minimax_utility, 1_000_000);
    }
}
