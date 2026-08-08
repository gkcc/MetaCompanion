//! Exact current-turn plus visible opponent-response oracle.
//!
//! This is intentionally a small deterministic rules subset.  It enumerates a
//! complete friendly turn, advances the public turn boundary, and then minimizes
//! over every legal visible opponent response.  Unsupported mechanics fail closed.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

use crate::behavior_prior::BehaviorPrior;
use crate::decision_ranker::DecisionRanker;
use crate::error::SolverError;
use crate::hdt_root::HdtRootCandidateSet;
use crate::model::{
    Action, ActionKind, Card, CardPoolSource, CardType, EffectCoverage, GameState, StateKey,
};
use crate::oracle::{
    ExactProbability, action_has_random_resolution, apply_action, apply_action_outcomes,
    apply_caller_confirmed_action, apply_caller_confirmed_action_outcomes,
    assert_continuous_effect_state, attack_snapshot_reason, legal_actions,
    maximum_attacks_with_weapon, normalize_active_weapon_attacks, public_attack_is_blocked,
    public_hero_attack_history_available, reset_public_turn_attack_tags,
    resolve_active_board_trigger, tactical_utility, visible_attack_snapshot_reason,
};

pub const TURNPAIR_SCOPE: &str = "oracle-turnpair-v1";
pub const RESPONSE_SCOPE: &str = "visible_generic_turnpair_v1";
pub const RESPONSE_KIND: &str = "minimax_best_response";
pub const TACTICAL_SCORE_KIND: &str = "counterplay_tactical_state_value";
pub const WIN_UTILITY: i64 = 1_000_000;
pub const LOSS_UTILITY: i64 = -1_000_000;
pub const MAX_ENUMERATED_NODES: usize = 20_000;
pub const MAX_LINE_DEPTH: u8 = 12;
pub const ROOT_ACTION_PORTFOLIO_MODEL: &str = "root-action-portfolio-v1";
pub const NEAR_OPTIMAL_REGRET_THRESHOLD: i64 = 100;
pub const VISIBLE_RESPONSE_SCOPE: &str = "visible-response-v1";

const VISIBLE_BEAM_WIDTH: usize = 12;
const VISIBLE_CANDIDATE_LIMIT: usize = 24;

/// Request-wide monotonic search limits shared by exact, scoped-lethal, and
/// visible fallback paths.  A live HTTP solve keeps one control instance for
/// the whole pipeline so `max_iterations` is a request budget rather than a
/// fresh allowance for every response line or fallback stage.
#[derive(Debug)]
pub struct SearchControl<'a> {
    cancel: &'a AtomicBool,
    deadline: Option<Instant>,
    maximum_nodes: usize,
    nodes: usize,
    node_limit_reached: bool,
    time_limit_reached: bool,
    behavior_prior: Option<Arc<BehaviorPrior>>,
    behavior_prior_identity: String,
    behavior_prior_ordering_attempts: usize,
    behavior_prior_ordering_applied: usize,
    behavior_prior_runtime_rejected: bool,
    decision_ranker: Option<Arc<DecisionRanker>>,
    decision_ranker_identity: String,
    decision_ranker_ordering_attempts: usize,
    decision_ranker_ordering_applied: usize,
    decision_ranker_runtime_rejected: bool,
}

impl<'a> SearchControl<'a> {
    #[must_use]
    pub fn new(cancel: &'a AtomicBool, maximum_nodes: usize, deadline: Option<Instant>) -> Self {
        Self {
            cancel,
            deadline,
            maximum_nodes,
            nodes: 0,
            node_limit_reached: false,
            time_limit_reached: false,
            behavior_prior: None,
            behavior_prior_identity: String::new(),
            behavior_prior_ordering_attempts: 0,
            behavior_prior_ordering_applied: 0,
            behavior_prior_runtime_rejected: false,
            decision_ranker: None,
            decision_ranker_identity: String::new(),
            decision_ranker_ordering_attempts: 0,
            decision_ranker_ordering_applied: 0,
            decision_ranker_runtime_rejected: false,
        }
    }

    #[must_use]
    pub fn with_behavior_prior(mut self, prior: Option<Arc<BehaviorPrior>>) -> Self {
        self.behavior_prior_identity = prior
            .as_ref()
            .map_or_else(String::new, |model| model.artifact_sha256().to_owned());
        self.behavior_prior = prior;
        self
    }

    #[must_use]
    pub fn with_decision_ranker(mut self, ranker: Option<Arc<DecisionRanker>>) -> Self {
        self.decision_ranker_identity = ranker
            .as_ref()
            .map_or_else(String::new, |model| model.artifact_sha256().to_owned());
        self.decision_ranker = ranker;
        self
    }

    #[must_use]
    pub fn nodes(&self) -> usize {
        self.nodes
    }

    #[must_use]
    pub fn behavior_prior_available(&self) -> bool {
        !self.behavior_prior_identity.is_empty()
    }

    #[must_use]
    pub fn behavior_prior_applied(&self) -> bool {
        self.behavior_prior_ordering_applied > 0
    }

    #[must_use]
    pub fn behavior_prior_runtime_rejected(&self) -> bool {
        self.behavior_prior_runtime_rejected
    }

    #[must_use]
    pub fn behavior_prior_identity(&self) -> &str {
        &self.behavior_prior_identity
    }

    #[must_use]
    pub fn behavior_prior_ordering_attempts(&self) -> usize {
        self.behavior_prior_ordering_attempts
    }

    #[must_use]
    pub fn decision_ranker_available(&self) -> bool {
        !self.decision_ranker_identity.is_empty()
    }

    #[must_use]
    pub fn decision_ranker_applied(&self) -> bool {
        self.decision_ranker_ordering_applied > 0
    }

    #[must_use]
    pub fn decision_ranker_runtime_rejected(&self) -> bool {
        self.decision_ranker_runtime_rejected
    }

    #[must_use]
    pub fn decision_ranker_identity(&self) -> &str {
        &self.decision_ranker_identity
    }

    #[must_use]
    pub fn decision_ranker_ordering_attempts(&self) -> usize {
        self.decision_ranker_ordering_attempts
    }

    fn order_actions(&mut self, state: &GameState, actions: &mut [Action]) {
        if state.active_player_id == state.perspective_player_id {
            let Some(ranker) = self.decision_ranker.clone() else {
                return;
            };
            self.decision_ranker_ordering_attempts =
                self.decision_ranker_ordering_attempts.saturating_add(1);
            match ranker.order_actions(state, actions) {
                Ok(true) => {
                    self.decision_ranker_ordering_applied =
                        self.decision_ranker_ordering_applied.saturating_add(1);
                }
                Ok(false) => {}
                Err(_) => {
                    actions.sort_by_key(Action::action_id);
                    self.decision_ranker_runtime_rejected = true;
                    self.decision_ranker = None;
                }
            }
            return;
        }
        let Some(prior) = self.behavior_prior.clone() else {
            return;
        };
        self.behavior_prior_ordering_attempts =
            self.behavior_prior_ordering_attempts.saturating_add(1);
        match prior.order_actions(state, actions) {
            Ok(true) => {
                self.behavior_prior_ordering_applied =
                    self.behavior_prior_ordering_applied.saturating_add(1);
            }
            Ok(false) => {}
            Err(_) => {
                // A prior is never allowed to make the tactical solver unavailable.
                // Restore the deterministic baseline order and disable it for the
                // remainder of this request.
                actions.sort_by_key(Action::action_id);
                self.behavior_prior_runtime_rejected = true;
                self.behavior_prior = None;
            }
        }
    }

    #[must_use]
    fn remaining_nodes(&self) -> usize {
        self.maximum_nodes.saturating_sub(self.nodes)
    }

    fn checkpoint(&mut self) -> Result<(), SolverError> {
        cancelled(self.cancel)?;
        if self.observe_deadline() {
            Err(SolverError::TimeLimit)
        } else {
            Ok(())
        }
    }

    fn observe_deadline(&mut self) -> bool {
        if self
            .deadline
            .is_some_and(|deadline| Instant::now() >= deadline)
        {
            self.time_limit_reached = true;
        }
        self.time_limit_reached
    }

    fn spend_node(&mut self) -> Result<(), SolverError> {
        self.checkpoint()?;
        if self.nodes >= self.maximum_nodes {
            self.node_limit_reached = true;
            return Err(SolverError::StateLimit(self.maximum_nodes));
        }
        self.nodes += 1;
        Ok(())
    }
}

#[derive(Clone, Debug)]
struct CompleteLine {
    actions: Vec<Action>,
    state: GameState,
    ended_turn: bool,
}

#[derive(Clone, Debug)]
pub struct TurnPairLine {
    pub actions: Vec<Action>,
    pub opponent_response: Vec<Action>,
    pub terminal_state: GameState,
    pub minimax_value: i64,
    pub safe_after_response: bool,
    pub immediate_lethal: bool,
    pub response_nodes_expanded: usize,
    pub response_transposition_hits: usize,
}

impl TurnPairLine {
    #[must_use]
    pub fn first_action_id(&self) -> String {
        self.actions
            .iter()
            .find(|action| action.kind != ActionKind::EndTurn)
            .map_or_else(|| "end_turn".to_owned(), Action::action_id)
    }
}

#[derive(Clone, Debug)]
pub struct TurnPairProof {
    pub lines: Vec<TurnPairLine>,
    pub optimal_value: i64,
    pub optimal_first_action_ids: Vec<String>,
    pub root_action_coverage: RootActionCoverage,
    pub portfolio_optimality_proven: bool,
    pub friendly_nodes_expanded: usize,
    pub response_nodes_expanded: usize,
    pub transposition_hits: usize,
}

/// A bounded, explicitly unverified line over only the public actions that the
/// small oracle can safely apply. This is deliberately separate from
/// [`TurnPairLine`] so an approximate response can never acquire proof fields by
/// accident.
#[derive(Clone, Debug)]
pub struct VisibleResponseLine {
    pub actions: Vec<Action>,
    pub opponent_reply: Vec<Action>,
    pub terminal_state: GameState,
    pub tactical_value: i64,
    pub approximate_entity_ids: Vec<String>,
    pub chance: Option<VisibleChanceSummary>,
}

#[derive(Clone, Debug)]
pub struct VisibleChanceSummary {
    pub expected_utility: f64,
    pub minimum_utility: i64,
    pub maximum_utility: i64,
    pub survival_probability: ExactProbability,
    pub recompute_after_random_outcome: bool,
}

impl VisibleResponseLine {
    #[must_use]
    pub fn first_action_id(&self) -> String {
        self.actions
            .first()
            .map_or_else(|| "end_turn".to_owned(), root_action_id)
    }
}

#[derive(Clone, Debug)]
pub struct VisibleResponsePlan {
    pub lines: Vec<VisibleResponseLine>,
    pub legal_first_action_ids: Vec<String>,
    pub modeled_first_action_ids: Vec<String>,
    pub omitted_first_action_ids: Vec<String>,
    pub independent_generated_first_action_ids: Vec<String>,
    pub hdt_supplied_root_portfolio: bool,
    pub nodes_expanded: usize,
    pub assessed_line_count: usize,
    pub node_limit_reached: bool,
    pub depth_limit_reached: bool,
    pub time_limit_reached: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RootActionCoverage {
    pub legal_first_action_ids: Vec<String>,
    pub generated_first_action_ids: Vec<String>,
    pub response_verified_first_action_ids: Vec<String>,
    pub missing_first_action_ids: Vec<String>,
    pub root_action_coverage_complete: bool,
}

impl RootActionCoverage {
    pub(crate) fn from_sets(
        legal_first_action_ids: BTreeSet<String>,
        generated_first_action_ids: BTreeSet<String>,
        response_verified_first_action_ids: BTreeSet<String>,
    ) -> Self {
        let generated_first_action_ids = generated_first_action_ids
            .intersection(&legal_first_action_ids)
            .cloned()
            .collect::<BTreeSet<_>>();
        let response_verified_first_action_ids = response_verified_first_action_ids
            .intersection(&generated_first_action_ids)
            .cloned()
            .collect::<BTreeSet<_>>();
        let missing_first_action_ids = legal_first_action_ids
            .difference(&response_verified_first_action_ids)
            .cloned()
            .collect::<Vec<_>>();
        let root_action_coverage_complete = missing_first_action_ids.is_empty()
            && generated_first_action_ids.len() == legal_first_action_ids.len()
            && response_verified_first_action_ids.len() == legal_first_action_ids.len();
        Self {
            legal_first_action_ids: legal_first_action_ids.into_iter().collect(),
            generated_first_action_ids: generated_first_action_ids.into_iter().collect(),
            response_verified_first_action_ids: response_verified_first_action_ids
                .into_iter()
                .collect(),
            missing_first_action_ids,
            root_action_coverage_complete,
        }
    }

    #[must_use]
    pub fn legal_first_action_count(&self) -> usize {
        self.legal_first_action_ids.len()
    }

    #[must_use]
    pub fn generated_first_action_count(&self) -> usize {
        self.generated_first_action_ids.len()
    }

    #[must_use]
    pub fn response_verified_first_action_count(&self) -> usize {
        self.response_verified_first_action_ids.len()
    }
}

/// Difference from the best fully response-verified first-action value.
#[must_use]
pub fn verified_portfolio_regret(proof: &TurnPairProof, line: &TurnPairLine) -> i64 {
    proof.optimal_value.saturating_sub(line.minimax_value)
}

/// Stable user-facing portfolio classification shared by HTTP and parity output.
#[must_use]
pub fn alternative_kind(
    root_action_coverage_complete: bool,
    portfolio_optimality_proven: bool,
    regret: Option<i64>,
    response_verified: bool,
) -> &'static str {
    if !response_verified {
        return "fallback";
    }
    match regret {
        Some(0) if root_action_coverage_complete && portfolio_optimality_proven => "co_optimal",
        Some(0) | None => "best_found",
        Some(value)
            if root_action_coverage_complete
                && portfolio_optimality_proven
                && value <= NEAR_OPTIMAL_REGRET_THRESHOLD =>
        {
            "near_optimal"
        }
        Some(_) => "backup",
    }
}

#[derive(Default)]
struct SearchStats {
    nodes: usize,
    transposition_hits: usize,
}

fn cancelled(cancel: &AtomicBool) -> Result<(), SolverError> {
    if cancel.load(Ordering::Relaxed) {
        Err(SolverError::Cancelled)
    } else {
        Ok(())
    }
}

fn terminal(state: &GameState) -> bool {
    state.friendly.hero.current_health == 0 || state.opponent.hero.current_health == 0
}

fn automatic_visible_target_mode(mode: &str) -> bool {
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

fn modeled_visible_target_mode(mode: &str) -> bool {
    matches!(
        mode,
        "self"
            | "enemy_character"
            | "friendly_character"
            | "any_character"
            | "enemy_minion"
            | "friendly_minion"
            | "any_minion"
            | "any_undamaged_minion"
            | "damaged_enemy_minion"
            | "enemy_hero"
            | "friendly_hero"
            | "all_enemy_characters"
            | "all_friendly_characters"
            | "all_enemy_minions"
            | "all_friendly_minions"
            | "all_minions"
            | "all_characters"
            | "all_other_minions"
            | "all_other_friendly_minions"
    )
}

fn supported_visible_effect_card(card: &Card) -> bool {
    if !matches!(
        card.card_type,
        CardType::Spell
            | CardType::Minion
            | CardType::Weapon
            | CardType::HeroPower
            | CardType::Location
    ) {
        return false;
    }
    if matches!(
        card.card_type,
        CardType::Spell | CardType::HeroPower | CardType::Location
    ) && card.effects.is_empty()
    {
        return false;
    }
    if card.effects.len() > 8 {
        return false;
    }
    let mut target_mode: Option<&str> = None;
    card.effects.iter().all(|effect| {
        let resolution_trigger = effect.trigger.as_ref() == "resolution";
        let trigger_supported = matches!(
            effect.trigger.as_ref(),
            "resolution"
                | "deathrattle"
                | "after_spell_cast"
                | "spellburst"
                | "frenzy"
                | "after_hero_attack"
                | "after_hero_power"
                | "turn_start"
                | "turn_end"
        );
        let triggered_target_supported = resolution_trigger
            || matches!(
                effect.target.as_ref(),
                "none" | "self" | "enemy_hero" | "friendly_hero"
            )
            || automatic_visible_target_mode(effect.target.as_ref());
        let point_effect = matches!(
            effect.kind.as_ref(),
            "damage" | "heal" | "buff_attack" | "buff_health" | "set_attack" | "set_health"
        ) && effect.amount > 0
            && modeled_visible_target_mode(effect.target.as_ref());
        let owner_effect = matches!(
            effect.kind.as_ref(),
            "armor"
                | "gain_hero_attack"
                | "gain_mana"
                | "refresh_mana"
                | "gain_mana_crystals"
                | "gain_empty_mana_crystals"
        ) && effect.amount > 0
            && effect.target.as_ref() == "none";
        let global_effect = effect.kind.as_ref() == "damage_all_minions"
            && effect.amount > 0
            && effect.target.as_ref() == "none";
        let freeze_effect = effect.kind.as_ref() == "freeze"
            && effect.amount == 0
            && modeled_visible_target_mode(effect.target.as_ref());
        let destroy_effect = effect.kind.as_ref() == "destroy"
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
            );
        let transform_effect = effect.kind.as_ref() == "transform"
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
        let keyword_effect = effect.kind.as_ref() == "grant_keywords"
            && effect.amount == 0
            && modeled_visible_target_mode(effect.target.as_ref())
            && effect.count == 1
            && effect.card_id.is_empty()
            && effect.attack == 0
            && effect.durability == 0
            && effect.has_summoned_minion_keywords();
        let board_destroy = effect.kind.as_ref() == "destroy_all_minions_and_locations"
            && effect.amount == 0
            && effect.target.as_ref() == "none"
            && effect.count == 1
            && effect.card_id.is_empty()
            && effect.attack == 0
            && effect.durability == 0
            && !effect.has_summoned_minion_keywords();
        let equip_weapon = effect.kind.as_ref() == "equip_weapon"
            && effect.amount == 0
            && effect.target.as_ref() == "none"
            && effect.count == 1
            && !effect.card_id.trim().is_empty()
            && !effect.name.trim().is_empty()
            && effect.attack > 0
            && effect.durability > 0;
        let summon_effect = effect.kind.as_ref() == "summon"
            && effect.amount == 0
            && effect.target.as_ref() == "none"
            && (1..=7).contains(&effect.count)
            && !effect.card_id.trim().is_empty()
            && !effect.name.trim().is_empty()
            && effect.health > 0;
        let draw_effect = effect.kind.as_ref() == "draw"
            && effect.amount == 0
            && effect.target.as_ref() == "none"
            && (1..=10).contains(&effect.count)
            && effect.card_id.is_empty()
            && effect.attack == 0
            && !effect.has_summoned_minion_keywords();
        let zone_draw = matches!(
            effect.kind.as_ref(),
            "draw_opponent" | "draw_both_players" | "draw_until_hand_count"
        ) && effect.amount == 0
            && effect.target.as_ref() == "none"
            && (1..=10).contains(&effect.count)
            && effect.card_id.is_empty()
            && effect.attack == 0
            && effect.durability == 0
            && !effect.has_summoned_minion_keywords();
        let weapon_buff = effect.kind.as_ref() == "buff_weapon_attack"
            && effect.amount > 0
            && effect.target.as_ref() == "none"
            && effect.count == 1
            && effect.card_id.is_empty()
            && effect.attack == 0
            && effect.durability == 0
            && !effect.has_summoned_minion_keywords();
        let hero_power_cost_aura = effect.kind.as_ref() == "set_hero_power_cost"
            && effect.amount >= 0
            && u16::try_from(effect.amount).is_ok()
            && effect.target.as_ref() == "none"
            && effect.hand_count_at_most.is_some();
        let one_cost_card_doubler = effect.kind.as_ref() == "double_one_cost_cards"
            && effect.amount == 2
            && effect.target.as_ref() == "none";
        let weapon_deathrattle = effect.kind.as_ref() == "draw_non_starting_spell_on_weapon_break"
            && effect.amount == 0
            && effect.count == 1
            && effect.target.as_ref() == "none"
            && effect.card_id.is_empty();
        let split_damage = resolution_trigger
            && effect.kind.as_ref() == "damage_split"
            && effect.random
            && effect.amount > 0
            && effect.count == 1
            && effect.target.as_ref() == "all_enemy_characters"
            && effect.card_id.is_empty();
        let shuffle_repeat = effect.kind.as_ref() == "shuffle_repeat_spell"
            && !effect.random
            && effect.amount == 0
            && (1..=10).contains(&effect.count)
            && effect.target.as_ref() == "none"
            && !effect.card_id.trim().is_empty();
        let one_cost_replay = effect.kind.as_ref() == "replay_one_cost_cards"
            && !effect.random
            && effect.amount == 0
            && effect.count == 1
            && effect.target.as_ref() == "none"
            && effect.card_id.is_empty();
        if resolution_trigger
            && !effect.random
            && !matches!(effect.target.as_ref(), "none" | "self")
            && !automatic_visible_target_mode(effect.target.as_ref())
        {
            if target_mode.is_some_and(|existing| existing != effect.target.as_ref()) {
                return false;
            }
            target_mode = Some(effect.target.as_ref());
        }
        let point_or_owner_fields_valid = effect.count == 1
            && effect.card_id.is_empty()
            && effect.attack == 0
            && effect.durability == 0
            && !effect.has_summoned_minion_keywords();
        let random_target_effect = effect.random
            && resolution_trigger
            && point_or_owner_fields_valid
            && (point_effect || freeze_effect)
            && !matches!(effect.target.as_ref(), "none" | "self")
            && !automatic_visible_target_mode(effect.target.as_ref());
        let empty_exact_deck_draw = effect.kind.as_ref() == "draw_from_pool"
            && effect.resolved_pool_exact
            && effect.resolved_pool_population == 0
            && effect
                .pool
                .as_ref()
                .is_some_and(|pool| pool.source == CardPoolSource::OwnerDeck);
        let pool_effect = effect.random
            && resolution_trigger
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
        let deterministic_effect = !effect.random
            && (summon_effect
                || draw_effect
                || zone_draw
                || weapon_buff
                || weapon_deathrattle
                || shuffle_repeat
                || one_cost_replay
                || transform_effect
                || keyword_effect
                || board_destroy
                || equip_weapon
                || ((point_effect
                    || owner_effect
                    || global_effect
                    || freeze_effect
                    || destroy_effect
                    || hero_power_cost_aura
                    || one_cost_card_doubler)
                    && point_or_owner_fields_valid));
        trigger_supported
            && triggered_target_supported
            && (random_target_effect || split_damage || pool_effect || deterministic_effect)
    })
}

fn supported_spell_lifesteal(card: &Card, allow_point_effects: bool) -> bool {
    allow_point_effects
        && card.lifesteal
        && card.card_type == CardType::Spell
        && !card.effects.is_empty()
        && card
            .effects
            .iter()
            .all(|effect| effect.kind.as_ref() == "damage")
        && supported_visible_effect_card(card)
}

fn card_unsupported(card: &Card, allow_point_effects: bool) -> bool {
    let supported_visible = allow_point_effects && supported_visible_effect_card(card);
    (!card.effects.is_empty() && !supported_visible)
        || !card.unsupported_effects.is_empty()
        || card.effect_coverage == EffectCoverage::Unsupported
        || (allow_point_effects
            && matches!(
                card.card_type,
                CardType::Spell | CardType::HeroPower | CardType::Location
            )
            && !supported_visible)
}

fn has_unmodeled_summoned_card(card: &Card) -> bool {
    card.effects
        .iter()
        .any(|effect| effect.summoned_card_effects_unmodeled)
}

fn minion_requires_whiteboard(card: &Card) -> bool {
    card.card_type == CardType::Minion
        && (card_unsupported(card, true)
            || card.card_id.eq_ignore_ascii_case("UNKNOWN")
            || (!card.card_text.trim().is_empty() && card.rule_id.trim().is_empty())
            || card.reborn
            || card.dormant
            || card.immune
            || card.durability > 0
            || card.current_durability > 0)
}

fn basic_combat_character(card: &Card) -> bool {
    matches!(card.card_type, CardType::Hero | CardType::Minion)
        && card.current_health > 0
        && !card.reborn
        && !card.dormant
        && card.durability == 0
        && card.current_durability == 0
}

fn visible_point_target_is_modeled(state: &GameState, action: &Action) -> bool {
    if action.target_entity_id.is_empty() {
        return true;
    }
    [&state.friendly, &state.opponent]
        .into_iter()
        .flat_map(|player| std::iter::once(&player.hero).chain(player.board.iter()))
        .find(|card| card.entity_id == action.target_entity_id)
        .is_some_and(|card| {
            matches!(card.card_type, CardType::Hero | CardType::Minion)
                && !card.reborn
                && !card.dormant
                && !card.immune
        })
}

fn visible_card_target_is_modeled(
    state: &GameState,
    action: &Action,
    card: &Card,
    location_placement: bool,
) -> bool {
    if location_placement {
        return action.target_entity_id.is_empty();
    }
    let requires_external_target = card.effects.iter().any(|effect| {
        !effect.random
            && !matches!(
                effect.target.as_ref(),
                "none" | "self" | "enemy_hero" | "friendly_hero"
            )
            && !automatic_visible_target_mode(effect.target.as_ref())
    });
    let has_fixed_target = card.effects.iter().any(|effect| {
        !effect.random && matches!(effect.target.as_ref(), "enemy_hero" | "friendly_hero")
    });
    if action.target_entity_id.is_empty() {
        if requires_external_target {
            return false;
        }
    } else if !requires_external_target && !has_fixed_target {
        return false;
    }
    visible_point_target_is_modeled(state, action)
}

fn assert_visible_attack_snapshots(state: &GameState) -> Result<(), SolverError> {
    for owner in [&state.friendly, &state.opponent] {
        if let Some(reason) = visible_attack_snapshot_reason(&owner.hero, owner.weapon.as_ref()) {
            return Err(SolverError::Unsupported(format!(
                "visible-response-v1 rejects inconsistent attack state: {reason}"
            )));
        }
        if owner.hero.can_attack
            && (owner.hero.windfury
                || owner.hero.mega_windfury
                || owner
                    .weapon
                    .as_ref()
                    .is_some_and(|weapon| weapon.windfury || weapon.mega_windfury))
            && !owner.hero.attacks_remaining_known
        {
            return Err(SolverError::Unsupported(format!(
                "visible-response-v1 requires an explicit attack count for {}",
                owner.hero.entity_id
            )));
        }
        for card in &owner.board {
            if let Some(reason) = attack_snapshot_reason(card) {
                return Err(SolverError::Unsupported(format!(
                    "visible-response-v1 rejects inconsistent attack state: {reason}"
                )));
            }
            if card.can_attack
                && (card.windfury || card.mega_windfury)
                && !card.attacks_remaining_known
            {
                return Err(SolverError::Unsupported(format!(
                    "visible-response-v1 requires an explicit attack count for {}",
                    card.entity_id
                )));
            }
        }
    }
    Ok(())
}

fn visible_action_is_modeled(state: &GameState, action: &Action) -> bool {
    let Ok(actor) = state.active_player() else {
        return false;
    };
    let Ok(enemy) = state.other_player(&state.active_player_id) else {
        return false;
    };
    match action.kind {
        ActionKind::EndTurn => true,
        ActionKind::Attack => {
            let source = std::iter::once(&actor.hero)
                .chain(actor.board.iter())
                .find(|card| card.entity_id == action.source_entity_id);
            let target = std::iter::once(&enemy.hero)
                .chain(enemy.board.iter())
                .find(|card| card.entity_id == action.target_entity_id);
            source.is_some_and(|card| {
                basic_combat_character(card)
                    && !card.frozen
                    && (card.entity_id != actor.hero.entity_id
                        || actor.weapon.as_ref().is_none_or(|weapon| {
                            weapon.card_type == CardType::Weapon
                                && weapon.current_durability > 0
                                && !public_attack_is_blocked(card, Some(weapon))
                        }))
            }) && target.is_some_and(|card| basic_combat_character(card) && !card.stealth)
        }
        ActionKind::PlayCard => {
            actor
                .hand
                .iter()
                .find(|card| card.entity_id == action.source_entity_id)
                .is_some_and(|card| {
                    visible_card_target_is_modeled(
                        state,
                        action,
                        card,
                        card.card_type == CardType::Location,
                    )
                        && if matches!(card.card_type, CardType::Minion | CardType::Location) {
                            action.board_position > 0
                        } else {
                            action.board_position == 0
                        }
                        // HDT can confirm a discounted play even when the
                        // historical public snapshot only retains a higher
                        // unmodified cost.  The action remains legal in the
                        // supplied portfolio, but spending an invented amount
                        // would corrupt every continuation, so omit the root.
                        && card.cost <= actor.mana
                            && match card.card_type {
                                CardType::Minion if card.reborn || card.dormant || card.immune => {
                                    false
                                }
                                CardType::Minion if card.effects.is_empty() => true,
                                CardType::Minion | CardType::Spell | CardType::Location => {
                                    supported_visible_effect_card(card)
                                        && !card_unsupported(card, true)
                                }
                                CardType::Weapon => {
                                    action.board_position == 0
                                        && card.current_durability > 0
                                        && public_hero_attack_history_available(&actor.hero)
                                        && !card.frozen
                                        && !card.dormant
                                        && !card.immune
                                }
                                CardType::Hero
                                | CardType::HeroPower
                                | CardType::Unknown => false,
                            }
                })
        }
        ActionKind::HeroPower => actor
            .hero_power
            .as_ref()
            .filter(|card| card.entity_id == action.source_entity_id)
            .is_some_and(|card| {
                card.cost <= actor.mana
                    && supported_visible_effect_card(card)
                    && !card_unsupported(card, true)
                    && visible_card_target_is_modeled(state, action, card, false)
            }),
        ActionKind::LocationActivate => actor
            .board
            .iter()
            .find(|card| {
                card.entity_id == action.source_entity_id && card.card_type == CardType::Location
            })
            .is_some_and(|card| {
                card.current_health > 0
                    && supported_visible_effect_card(card)
                    && !card_unsupported(card, true)
                    && action.board_position == 0
                    && visible_card_target_is_modeled(state, action, card, false)
            }),
    }
}

fn action_approximate_entity_ids(state: &GameState, action: &Action) -> BTreeSet<String> {
    let mut result = BTreeSet::new();
    let all_cards = || {
        [&state.friendly, &state.opponent]
            .into_iter()
            .flat_map(|player| {
                std::iter::once(&player.hero)
                    .chain(player.board.iter())
                    .chain(player.hand.iter())
                    .chain(player.hero_power.iter())
                    .chain(player.weapon.iter())
            })
    };
    match action.kind {
        ActionKind::PlayCard => {
            if let Some(card) = all_cards().find(|card| card.entity_id == action.source_entity_id) {
                if (card.effect_coverage == EffectCoverage::Generic && !card.effects.is_empty())
                    || card
                        .effects
                        .iter()
                        .any(|effect| effect.kind.as_ref() == "draw")
                {
                    result.insert(card.entity_id.to_string());
                }
                if minion_requires_whiteboard(card) {
                    result.insert(card.entity_id.to_string());
                }
                if card.card_type == CardType::Weapon
                    && (card_unsupported(card, true)
                        || card.card_id.eq_ignore_ascii_case("UNKNOWN")
                        || (!card.card_text.trim().is_empty() && card.rule_id.trim().is_empty()))
                {
                    result.insert(card.entity_id.to_string());
                }
                if card
                    .effects
                    .iter()
                    .any(|effect| effect.kind.as_ref() == "gain_hero_attack")
                {
                    let actor = state
                        .active_player()
                        .expect("validated active player for visible action");
                    if !public_hero_attack_history_available(&actor.hero) {
                        result.insert(actor.hero.entity_id.to_string());
                    }
                }
                if card.effects.iter().any(|effect| {
                    matches!(
                        effect.kind.as_ref(),
                        "draw_non_starting_spell_on_weapon_break"
                            | "shuffle_repeat_spell"
                            | "replay_one_cost_cards"
                    ) || (effect.pool.is_some()
                        && (!effect.resolved_pool_exact
                            || effect.resolved_pool.iter().any(|candidate| {
                                !candidate.card.text.trim().is_empty()
                                    || !matches!(candidate.card.card_type, CardType::Minion)
                            })))
                }) {
                    // Resource/body changes are simulated, but Casts-When-Drawn is not auto-cast,
                    // historical replay is rebuilt from visible zones, and generated card text
                    // requires the next HDT refresh before it can be interpreted exactly.
                    result.insert(card.entity_id.to_string());
                }
                if card.card_type == CardType::Spell {
                    let actor = state
                        .active_player()
                        .expect("validated active player for visible action");
                    for trigger_source in actor.board.iter().filter(|source| {
                        source
                            .effects
                            .iter()
                            .any(|effect| effect.trigger.as_ref() == "after_spell_cast")
                    }) {
                        result.insert(trigger_source.entity_id.to_string());
                    }
                }
            }
        }
        ActionKind::Attack => {
            for player in [&state.friendly, &state.opponent] {
                if action.source_entity_id == player.hero.entity_id
                    && let Some(weapon) = &player.weapon
                {
                    result.insert(weapon.entity_id.to_string());
                }
            }
            for entity_id in [&action.source_entity_id, &action.target_entity_id] {
                if let Some(card) =
                    all_cards().find(|card| card.entity_id.as_ref() == entity_id.as_ref())
                    && (minion_requires_whiteboard(card)
                        || (card.effect_coverage == EffectCoverage::Generic
                            && card
                                .effects
                                .iter()
                                .any(|effect| effect.trigger.as_ref() == "deathrattle")))
                {
                    result.insert(card.entity_id.to_string());
                }
            }
        }
        ActionKind::HeroPower => {
            let actor = state
                .active_player()
                .expect("validated active player for visible action");
            if let Some(power) = actor
                .hero_power
                .as_ref()
                .filter(|power| power.entity_id == action.source_entity_id)
                && ((power.effect_coverage == EffectCoverage::Generic && !power.effects.is_empty())
                    || power
                        .effects
                        .iter()
                        .any(|effect| effect.kind.as_ref() == "draw"))
            {
                result.insert(power.entity_id.to_string());
            }
            if actor.hero_power.as_ref().is_some_and(|power| {
                power.entity_id == action.source_entity_id
                    && power
                        .effects
                        .iter()
                        .any(|effect| effect.kind.as_ref() == "gain_hero_attack")
            }) && !public_hero_attack_history_available(&actor.hero)
            {
                result.insert(actor.hero.entity_id.to_string());
            }
        }
        ActionKind::LocationActivate => {
            let actor = state
                .active_player()
                .expect("validated active player for visible action");
            if let Some(location) = actor
                .board
                .iter()
                .find(|card| card.entity_id == action.source_entity_id)
                && ((location.effect_coverage == EffectCoverage::Generic
                    && !location.effects.is_empty())
                    || location
                        .effects
                        .iter()
                        .any(|effect| effect.kind.as_ref() == "draw"))
            {
                result.insert(location.entity_id.to_string());
            }
            if let Some(location) = actor
                .board
                .iter()
                .find(|card| card.entity_id == action.source_entity_id)
                && location
                    .effects
                    .iter()
                    .any(|effect| effect.kind.as_ref() == "gain_hero_attack")
                && !public_hero_attack_history_available(&actor.hero)
            {
                result.insert(actor.hero.entity_id.to_string());
            }
            if let Some(location) = actor
                .board
                .iter()
                .find(|card| card.entity_id == action.source_entity_id)
                && has_unmodeled_summoned_card(location)
            {
                result.insert(location.entity_id.to_string());
            }
            if let Some(target) = all_cards().find(|card| {
                card.entity_id == action.target_entity_id && card.card_type == CardType::Minion
            }) && minion_requires_whiteboard(target)
            {
                result.insert(target.entity_id.to_string());
            }
        }
        ActionKind::EndTurn => {
            let actor = state
                .active_player()
                .expect("validated active player for visible action");
            for source in actor
                .board
                .iter()
                .chain(actor.weapon.iter())
                .filter(|source| {
                    source
                        .effects
                        .iter()
                        .any(|effect| effect.trigger.as_ref() == "turn_end")
                })
            {
                result.insert(source.entity_id.to_string());
            }
        }
    }
    result
}

fn visible_action_partition_ready(
    state: &GameState,
) -> Result<(Vec<Action>, Vec<Action>), SolverError> {
    assert_visible_attack_snapshots(state)?;
    let mut modeled = Vec::new();
    let mut omitted = Vec::new();
    for action in legal_actions(state)? {
        if visible_action_is_modeled(state, &action) {
            modeled.push(action);
        } else {
            omitted.push(action);
        }
    }
    modeled.sort_by_key(Action::action_id);
    omitted.sort_by_key(Action::action_id);
    Ok((modeled, omitted))
}

fn normalized_visible_state(state: &GameState) -> Result<GameState, SolverError> {
    let mut normalized = state.clone();
    assert_continuous_effect_state(&normalized)?;
    normalize_active_weapon_attacks(&mut normalized)?;
    Ok(normalized)
}

fn visible_action_partition(state: &GameState) -> Result<(Vec<Action>, Vec<Action>), SolverError> {
    let normalized = normalized_visible_state(state)?;
    visible_action_partition_ready(&normalized)
}

/// Return the public action subset that the bounded visible-response planner can
/// actually apply. Unknown spells, weapons, locations, and hero powers are not
/// represented as legal modeled actions. A reviewed Location activation can
/// enter only as an HDT-confirmed root; ordinary follow-up generation never
/// invents another activation.
pub fn visible_legal_actions(state: &GameState) -> Result<Vec<Action>, SolverError> {
    visible_action_partition(state).map(|(modeled, _)| modeled)
}

/// Refresh only public start-of-turn state after the friendly end-turn action.
/// The ordinary turn draw remains hidden and is not invented here. Reviewed
/// start-of-turn board triggers are resolved after resources and attacks refresh.
pub fn advance_to_visible_opponent_start(state: &GameState) -> Result<GameState, SolverError> {
    if state.active_player_id != state.opponent.player_id {
        return Err(SolverError::IllegalAction(
            "visible response requires the opponent to be active".to_owned(),
        ));
    }
    let mut next = state.clone();
    let active_id = Arc::clone(&next.active_player_id);
    let actor = next.player_mut(&active_id)?;
    actor.max_mana = actor.max_mana.saturating_add(1).min(10);
    actor.mana = actor.max_mana;
    for card in &mut actor.board {
        card.summoned_this_turn = false;
        card.attacks_remaining = if card.attack > 0 && !card.dormant && !card.frozen {
            if card.mega_windfury {
                4
            } else if card.windfury {
                2
            } else {
                1
            }
        } else {
            0
        };
        card.attacks_remaining_known = true;
        card.can_attack = card.attacks_remaining > 0;
    }
    let weapon_is_usable = actor.weapon.as_ref().is_none_or(|weapon| {
        weapon.card_type == CardType::Weapon
            && weapon.current_durability > 0
            && !public_attack_is_blocked(&actor.hero, Some(weapon))
    });
    let hero_attack_limit = maximum_attacks_with_weapon(&actor.hero, actor.weapon.as_ref());
    reset_public_turn_attack_tags(&mut actor.hero);
    actor.hero.attacks_remaining = if actor.hero.attack > 0
        && actor.hero.current_health > 0
        && !actor.hero.frozen
        && !actor.hero.dormant
        && weapon_is_usable
    {
        hero_attack_limit
    } else {
        0
    };
    actor.hero.attacks_remaining_known = true;
    actor.hero.can_attack = actor.hero.attacks_remaining > 0;
    resolve_active_board_trigger(&mut next, "turn_start")?;
    Ok(next)
}

/// Reject every position whose visible behavior is not exact in this oracle.
pub fn assert_turnpair_state(
    state: &GameState,
    allow_point_effects: bool,
) -> Result<(), SolverError> {
    assert_continuous_effect_state(state)?;
    let mut reasons = Vec::new();
    if state.active_player_id != state.friendly.player_id {
        reasons.push("turnpair-v1 requires the friendly player to be active".to_owned());
    }
    if state.perspective_player_id != state.friendly.player_id {
        reasons.push("turnpair-v1 requires the friendly perspective".to_owned());
    }
    if state.opponent.deck_size > 0 {
        reasons.push("opponent draw identity is not deterministic".to_owned());
    }
    for owner in [&state.friendly, &state.opponent] {
        if owner.weapon.is_some() {
            reasons.push(format!("{} weapon is outside turnpair-v1", owner.player_id));
        }
        if owner.hero_power.is_some() && !allow_point_effects {
            reasons.push(format!(
                "{} hero power is outside turnpair-v1",
                owner.player_id
            ));
        }
        if !owner.hand.is_empty() && !allow_point_effects {
            reasons.push(format!(
                "{} hand play is outside turnpair-v1",
                owner.player_id
            ));
        }
        let gains_hero_attack = owner
            .hand
            .iter()
            .chain(owner.hero_power.iter())
            .flat_map(|card| card.effects.iter())
            .any(|effect| effect.kind.as_ref() == "gain_hero_attack");
        if allow_point_effects
            && gains_hero_attack
            && !public_hero_attack_history_available(&owner.hero)
        {
            reasons.push(format!(
                "{} hero attack history is unavailable",
                owner.player_id
            ));
        }
        for card in std::iter::once(&owner.hero)
            .chain(owner.board.iter())
            .chain(owner.hand.iter())
            .chain(owner.hero_power.iter())
        {
            if card.effects.iter().any(|effect| effect.random) {
                reasons.push(format!(
                    "{} has chance effects outside deterministic turnpair-v1",
                    card.entity_id
                ));
            }
            if card.effect_coverage == EffectCoverage::Generic && !card.effects.is_empty() {
                reasons.push(format!(
                    "{} uses generic text-compiled effects outside exact turnpair-v1",
                    card.entity_id
                ));
            }
            if card_unsupported(card, allow_point_effects) || has_unmodeled_summoned_card(card) {
                reasons.push(format!("{} has unsupported card effects", card.entity_id));
            }
            let lifesteal_outside_point_effect =
                card.lifesteal && !supported_spell_lifesteal(card, allow_point_effects);
            if card.stealth
                || card.frozen
                || card.poisonous
                || lifesteal_outside_point_effect
                || card.windfury
                || card.mega_windfury
                || card.rush
                || card.charge
                || card.reborn
                || card.dormant
                || card.immune
            {
                reasons.push(format!(
                    "{} has a mechanic outside turnpair-v1",
                    card.entity_id
                ));
            }
        }
        for card in std::iter::once(&owner.hero).chain(owner.board.iter()) {
            if let Some(reason) = attack_snapshot_reason(card) {
                reasons.push(reason);
            }
        }
        if let Some(power) = &owner.hero_power
            && allow_point_effects
            && !supported_visible_effect_card(power)
        {
            reasons.push(format!("{} has unsupported card effects", power.entity_id));
        }
    }
    reasons.sort();
    reasons.dedup();
    if reasons.is_empty() {
        Ok(())
    } else {
        Err(SolverError::Unsupported(reasons.join("; ")))
    }
}

fn line_ids(actions: &[Action]) -> Vec<String> {
    actions.iter().map(Action::action_id).collect()
}

fn early_hero_power_penalty(actions: &[Action]) -> u8 {
    let mut meaningful = actions
        .iter()
        .filter(|action| action.kind != ActionKind::EndTurn);
    let Some(first) = meaningful.next() else {
        return 0;
    };
    u8::from(first.kind == ActionKind::HeroPower && meaningful.next().is_some())
}

fn root_action_id(action: &Action) -> String {
    if action.kind == ActionKind::EndTurn {
        "end_turn".to_owned()
    } else {
        action.action_id()
    }
}

fn action_source_card<'a>(state: &'a GameState, action: &Action) -> Option<&'a Card> {
    let actor = if state.active_player_id == state.friendly.player_id {
        &state.friendly
    } else if state.active_player_id == state.opponent.player_id {
        &state.opponent
    } else {
        return None;
    };
    match action.kind {
        ActionKind::PlayCard => actor
            .hand
            .iter()
            .find(|card| card.entity_id == action.source_entity_id),
        ActionKind::HeroPower => actor
            .hero_power
            .as_ref()
            .filter(|card| card.entity_id == action.source_entity_id),
        ActionKind::LocationActivate => actor
            .board
            .iter()
            .find(|card| card.entity_id == action.source_entity_id),
        ActionKind::Attack | ActionKind::EndTurn => None,
    }
}

fn is_pure_temporary_mana_action(state: &GameState, action: &Action) -> bool {
    if action.kind != ActionKind::PlayCard {
        return false;
    }
    action_source_card(state, action).is_some_and(|card| {
        card.card_type == CardType::Spell
            && card.effect_coverage == EffectCoverage::Exact
            && !card.effects.is_empty()
            && card.effects.iter().all(|effect| {
                !effect.random
                    && effect.kind.as_ref() == "gain_mana"
                    && effect.amount > 0
                    && effect.target.as_ref() == "none"
            })
    })
}

fn is_unproven_friendly_damage_action(state: &GameState, action: &Action) -> bool {
    if state.active_player_id != state.friendly.player_id || action.target_entity_id.is_empty() {
        return false;
    }
    let target_is_friendly = state.friendly.hero.entity_id == action.target_entity_id
        || state
            .friendly
            .board
            .iter()
            .any(|card| card.entity_id == action.target_entity_id && card.current_health > 0);
    if !target_is_friendly {
        return false;
    }
    action_source_card(state, action).is_some_and(|card| {
        // A minion body can make a self-damaging Battlecry worthwhile. This
        // guard is for expendable point-damage sources whose modeled result is
        // only harm; legal target generation remains unchanged.
        card.card_type != CardType::Minion
            && card.effect_coverage == EffectCoverage::Exact
            && !card.lifesteal
            && !card.effects.is_empty()
            && card.effects.iter().all(|effect| {
                !effect.random
                    && effect.kind.as_ref() == "damage"
                    && effect.amount > 0
                    && modeled_visible_target_mode(effect.target.as_ref())
                    && !matches!(
                        effect.target.as_ref(),
                        "none" | "self" | "enemy_hero" | "friendly_hero"
                    )
                    && !automatic_visible_target_mode(effect.target.as_ref())
            })
    })
}

fn suffix_is_legal_without_temporary_mana(
    state_before_mana: &GameState,
    suffix: &[Action],
) -> bool {
    let mut replay = state_before_mana.clone();
    for (index, action) in suffix.iter().enumerate() {
        let Ok(legal) = legal_actions(&replay) else {
            return false;
        };
        if !legal
            .iter()
            .any(|candidate| candidate.action_id() == action.action_id())
        {
            return false;
        }
        if index + 1 == suffix.len() {
            return true;
        }
        let Ok(outcomes) = apply_action_outcomes(&replay, action) else {
            return false;
        };
        if outcomes.len() != 1 {
            return false;
        }
        replay = outcomes[0].state.clone();
    }
    true
}

fn visible_line_contains_unadvisable_action(
    initial: &GameState,
    actions: &[Action],
    caller_confirmed_root: bool,
) -> bool {
    let mut replay = initial.clone();
    for (index, action) in actions.iter().enumerate() {
        if is_unproven_friendly_damage_action(&replay, action) {
            return true;
        }
        if is_pure_temporary_mana_action(&replay, action)
            && suffix_is_legal_without_temporary_mana(&replay, &actions[index + 1..])
        {
            return true;
        }
        if index + 1 == actions.len() {
            break;
        }
        let result = if caller_confirmed_root && index == 0 {
            apply_caller_confirmed_action_outcomes(&replay, action)
        } else {
            apply_action_outcomes(&replay, action)
        };
        let Ok(outcomes) = result else {
            return false;
        };
        if outcomes.len() != 1 {
            return false;
        }
        replay = outcomes[0].state.clone();
    }
    false
}

#[derive(Clone, Debug)]
struct VisibleSearchNode {
    actions: Vec<Action>,
    state: GameState,
    approximate_entity_ids: BTreeSet<String>,
}

#[derive(Clone, Debug)]
struct CompletedVisibleTurn {
    actions: Vec<Action>,
    state: GameState,
    approximate_entity_ids: BTreeSet<String>,
}

#[derive(Debug)]
struct VisibleSearchBudget<'control, 'cancel> {
    control: &'control mut SearchControl<'cancel>,
    starting_nodes: usize,
    maximum_root_nodes: usize,
    root_quota_reached: bool,
    node_limit_reached: bool,
    depth_limit_reached: bool,
    time_limit_reached: bool,
}

impl<'control, 'cancel> VisibleSearchBudget<'control, 'cancel> {
    fn new(control: &'control mut SearchControl<'cancel>, maximum_root_nodes: usize) -> Self {
        let starting_nodes = control.nodes();
        Self {
            control,
            starting_nodes,
            maximum_root_nodes,
            root_quota_reached: false,
            node_limit_reached: false,
            depth_limit_reached: false,
            time_limit_reached: false,
        }
    }

    fn nodes(&self) -> usize {
        self.control.nodes().saturating_sub(self.starting_nodes)
    }

    fn expansion_allowed(&mut self) -> Result<bool, SolverError> {
        match self.control.checkpoint() {
            Ok(()) => {}
            Err(SolverError::StateLimit(_)) => {
                self.node_limit_reached = true;
                return Ok(false);
            }
            Err(SolverError::TimeLimit) => {
                self.time_limit_reached = true;
                return Ok(false);
            }
            Err(error) => return Err(error),
        }
        if self.nodes() >= self.maximum_root_nodes {
            self.root_quota_reached = true;
            return Ok(false);
        }
        Ok(true)
    }

    fn spend_node(&mut self) -> Result<bool, SolverError> {
        if self.nodes() >= self.maximum_root_nodes {
            self.root_quota_reached = true;
            return Ok(false);
        }
        match self.control.spend_node() {
            Ok(()) => Ok(true),
            Err(SolverError::StateLimit(_)) => {
                self.node_limit_reached = true;
                Ok(false)
            }
            Err(SolverError::TimeLimit) => {
                self.time_limit_reached = true;
                Ok(false)
            }
            Err(error) => Err(error),
        }
    }
}

fn merge_action_approximations(node: &mut VisibleSearchNode, before: &GameState, action: &Action) {
    node.approximate_entity_ids
        .extend(action_approximate_entity_ids(before, action));
}

fn apply_visible_search_action(
    node: &VisibleSearchNode,
    action: &Action,
) -> Result<VisibleSearchNode, SolverError> {
    // `visible_legal_actions` derives actions from a normalized clone. Apply
    // the selected action to that same normalized state as well. Simulated
    // effects can equip a weapon between search steps (for example Confront
    // the Tol'vir replaying Smuggled Shovel); in that case normalization makes
    // the newly armed hero attackable. Applying the generated attack to the
    // stale, unnormalized node used to fail the entire recommendation with an
    // `illegal_action` error.
    let normalized = normalized_visible_state(&node.state)?;
    let (state, _) = apply_action(&normalized, action).map_err(|error| match error {
        SolverError::IllegalAction(detail) => SolverError::IllegalAction(format!(
            "visible search failed to apply {} after [{}]: {detail}",
            action.action_id(),
            line_ids(&node.actions).join(" -> ")
        )),
        other => other,
    })?;
    let mut child = VisibleSearchNode {
        actions: node.actions.clone(),
        state,
        approximate_entity_ids: node.approximate_entity_ids.clone(),
    };
    child.actions.push(action.clone());
    merge_action_approximations(&mut child, &normalized, action);
    Ok(child)
}

fn complete_friendly_visible_turn(
    node: &VisibleSearchNode,
    cancel: &AtomicBool,
) -> Result<CompletedVisibleTurn, SolverError> {
    cancelled(cancel)?;
    let mut completed = node.clone();
    if terminal(&completed.state) {
        return Ok(CompletedVisibleTurn {
            actions: completed.actions,
            state: completed.state,
            approximate_entity_ids: completed.approximate_entity_ids,
        });
    }
    if completed.state.active_player_id == completed.state.friendly.player_id {
        completed = apply_visible_search_action(&completed, &Action::end_turn())?;
    }
    if completed.state.active_player_id != completed.state.opponent.player_id {
        return Err(SolverError::IllegalAction(
            "friendly visible line did not reach the opponent turn".to_owned(),
        ));
    }
    completed.state = advance_to_visible_opponent_start(&completed.state)?;
    Ok(CompletedVisibleTurn {
        actions: completed.actions,
        state: completed.state,
        approximate_entity_ids: completed.approximate_entity_ids,
    })
}

fn complete_opponent_visible_turn(
    node: &VisibleSearchNode,
    cancel: &AtomicBool,
) -> Result<CompletedVisibleTurn, SolverError> {
    cancelled(cancel)?;
    let mut completed = node.clone();
    if !terminal(&completed.state)
        && completed.state.active_player_id == completed.state.opponent.player_id
    {
        completed = apply_visible_search_action(&completed, &Action::end_turn())?;
    }
    Ok(CompletedVisibleTurn {
        actions: completed.actions,
        state: completed.state,
        approximate_entity_ids: completed.approximate_entity_ids,
    })
}

fn sort_friendly_nodes(nodes: &mut [VisibleSearchNode], perspective_player_id: &str) {
    nodes.sort_by(|left, right| {
        tactical_utility(&right.state, perspective_player_id)
            .cmp(&tactical_utility(&left.state, perspective_player_id))
            .then_with(|| {
                early_hero_power_penalty(&left.actions)
                    .cmp(&early_hero_power_penalty(&right.actions))
            })
            .then_with(|| line_ids(&left.actions).cmp(&line_ids(&right.actions)))
    });
}

fn sort_opponent_nodes(nodes: &mut [VisibleSearchNode], perspective_player_id: &str) {
    nodes.sort_by(|left, right| {
        tactical_utility(&left.state, perspective_player_id)
            .cmp(&tactical_utility(&right.state, perspective_player_id))
            .then_with(|| line_ids(&left.actions).cmp(&line_ids(&right.actions)))
    });
}

fn friendly_visible_candidates(
    state: &GameState,
    root: &Action,
    max_depth: u8,
    budget: &mut VisibleSearchBudget<'_, '_>,
    cancel: &AtomicBool,
    caller_confirmed_root: bool,
) -> Result<Vec<CompletedVisibleTurn>, SolverError> {
    cancelled(cancel)?;
    let initial = VisibleSearchNode {
        actions: Vec::new(),
        state: state.clone(),
        approximate_entity_ids: BTreeSet::new(),
    };
    let root_node = if caller_confirmed_root {
        let (root_state, _) = apply_caller_confirmed_action(&initial.state, root)?;
        let mut child = VisibleSearchNode {
            actions: vec![root.clone()],
            state: root_state,
            approximate_entity_ids: initial.approximate_entity_ids,
        };
        merge_action_approximations(&mut child, &initial.state, root);
        child
    } else {
        apply_visible_search_action(&initial, root)?
    };
    let baseline = complete_friendly_visible_turn(&root_node, cancel)?;
    let baseline_ids = line_ids(&baseline.actions);
    let mut completed = vec![baseline.clone()];
    if terminal(&root_node.state)
        || root_node.state.active_player_id == root_node.state.opponent.player_id
    {
        return Ok(completed);
    }

    let maximum_depth = usize::from(max_depth.max(1));
    if baseline.actions.len() > maximum_depth {
        budget.depth_limit_reached = true;
    }
    let mut beam = vec![root_node];
    while !beam.is_empty() && budget.expansion_allowed()? {
        cancelled(cancel)?;
        let mut next = Vec::new();
        let mut exhausted = false;
        for node in beam {
            cancelled(cancel)?;
            let mut actions = visible_legal_actions(&node.state)?
                .into_iter()
                .filter(|action| action.kind != ActionKind::EndTurn)
                .collect::<Vec<_>>();
            budget.control.order_actions(&node.state, &mut actions);
            for action in actions {
                cancelled(cancel)?;
                if node.actions.len() >= maximum_depth {
                    budget.depth_limit_reached = true;
                    continue;
                }
                if !budget.spend_node()? {
                    exhausted = true;
                    break;
                }
                let child = apply_visible_search_action(&node, &action)?;
                if terminal(&child.state) {
                    completed.push(CompletedVisibleTurn {
                        actions: child.actions,
                        state: child.state,
                        approximate_entity_ids: child.approximate_entity_ids,
                    });
                    continue;
                }
                if child.actions.len().saturating_add(1) <= maximum_depth {
                    completed.push(complete_friendly_visible_turn(&child, cancel)?);
                } else {
                    budget.depth_limit_reached = true;
                }
                if child.actions.len().saturating_add(2) <= maximum_depth {
                    next.push(child);
                } else if visible_legal_actions(&child.state)?
                    .iter()
                    .any(|candidate| candidate.kind != ActionKind::EndTurn)
                {
                    budget.depth_limit_reached = true;
                }
            }
            if exhausted {
                break;
            }
        }
        if exhausted {
            break;
        }
        sort_friendly_nodes(&mut next, &state.perspective_player_id);
        next.truncate(VISIBLE_BEAM_WIDTH);
        beam = next;
    }

    completed.sort_by(|left, right| {
        tactical_utility(&right.state, &state.perspective_player_id)
            .cmp(&tactical_utility(&left.state, &state.perspective_player_id))
            .then_with(|| {
                early_hero_power_penalty(&left.actions)
                    .cmp(&early_hero_power_penalty(&right.actions))
            })
            .then_with(|| line_ids(&left.actions).cmp(&line_ids(&right.actions)))
    });
    completed.dedup_by(|left, right| line_ids(&left.actions) == line_ids(&right.actions));
    completed.truncate(VISIBLE_CANDIDATE_LIMIT);
    if !completed
        .iter()
        .any(|candidate| line_ids(&candidate.actions) == baseline_ids)
    {
        if completed.len() == VISIBLE_CANDIDATE_LIMIT {
            completed.pop();
        }
        completed.push(baseline);
    }
    Ok(completed)
}

fn worst_visible_reply(
    response_start: &GameState,
    max_depth: u8,
    budget: &mut VisibleSearchBudget<'_, '_>,
    cancel: &AtomicBool,
) -> Result<(CompletedVisibleTurn, usize), SolverError> {
    cancelled(cancel)?;
    let initial = VisibleSearchNode {
        actions: Vec::new(),
        state: response_start.clone(),
        approximate_entity_ids: BTreeSet::new(),
    };
    let baseline = complete_opponent_visible_turn(&initial, cancel)?;
    let mut completed = vec![baseline];
    if terminal(response_start)
        || response_start.active_player_id != response_start.opponent.player_id
    {
        return Ok((completed.remove(0), 1));
    }

    let maximum_depth = usize::from(max_depth.max(1));
    let mut beam = vec![initial];
    while !beam.is_empty() && budget.expansion_allowed()? {
        cancelled(cancel)?;
        let mut next = Vec::new();
        let mut exhausted = false;
        for node in beam {
            cancelled(cancel)?;
            let mut actions = visible_legal_actions(&node.state)?
                .into_iter()
                .filter(|action| action.kind != ActionKind::EndTurn)
                .collect::<Vec<_>>();
            budget.control.order_actions(&node.state, &mut actions);
            for action in actions {
                cancelled(cancel)?;
                if node.actions.len() >= maximum_depth {
                    budget.depth_limit_reached = true;
                    continue;
                }
                if !budget.spend_node()? {
                    exhausted = true;
                    break;
                }
                let child = apply_visible_search_action(&node, &action)?;
                if terminal(&child.state) {
                    completed.push(CompletedVisibleTurn {
                        actions: child.actions,
                        state: child.state,
                        approximate_entity_ids: child.approximate_entity_ids,
                    });
                    continue;
                }
                if child.actions.len().saturating_add(1) <= maximum_depth {
                    completed.push(complete_opponent_visible_turn(&child, cancel)?);
                } else {
                    budget.depth_limit_reached = true;
                }
                if child.actions.len().saturating_add(2) <= maximum_depth {
                    next.push(child);
                } else if visible_legal_actions(&child.state)?
                    .iter()
                    .any(|candidate| candidate.kind != ActionKind::EndTurn)
                {
                    budget.depth_limit_reached = true;
                }
            }
            if exhausted {
                break;
            }
        }
        if exhausted {
            break;
        }
        sort_opponent_nodes(&mut next, &response_start.perspective_player_id);
        next.truncate(VISIBLE_BEAM_WIDTH);
        beam = next;
    }

    let assessed = completed.len();
    completed.sort_by(|left, right| {
        tactical_utility(&left.state, &response_start.perspective_player_id)
            .cmp(&tactical_utility(
                &right.state,
                &response_start.perspective_player_id,
            ))
            .then_with(|| line_ids(&left.actions).cmp(&line_ids(&right.actions)))
    });
    completed
        .into_iter()
        .next()
        .map(|worst| (worst, assessed))
        .ok_or_else(|| {
            SolverError::IllegalAction("visible response baseline is missing".to_owned())
        })
}

#[derive(Clone, Debug)]
struct VisiblePolicyEvaluation {
    expected_utility: f64,
    minimum_utility: i64,
    maximum_utility: i64,
    survival_probability: ExactProbability,
    actions: Vec<Action>,
    opponent_reply: Vec<Action>,
    terminal_state: GameState,
    approximate_entity_ids: BTreeSet<String>,
    recompute_after_random_outcome: bool,
}

fn probability_as_f64(value: ExactProbability) -> f64 {
    value.numerator as f64 / value.denominator as f64
}

fn visible_policy_leaf(state: &GameState) -> Result<VisiblePolicyEvaluation, SolverError> {
    let utility = tactical_utility(state, &state.perspective_player_id);
    let alive = state
        .player(&state.perspective_player_id)?
        .hero
        .current_health
        > 0;
    Ok(VisiblePolicyEvaluation {
        expected_utility: utility as f64,
        minimum_utility: utility,
        maximum_utility: utility,
        survival_probability: ExactProbability::new(u64::from(alive), 1)?,
        actions: Vec::new(),
        opponent_reply: Vec::new(),
        terminal_state: state.clone(),
        approximate_entity_ids: BTreeSet::new(),
        recompute_after_random_outcome: false,
    })
}

fn visible_policy_evaluation_is_better(
    candidate: &VisiblePolicyEvaluation,
    current: &VisiblePolicyEvaluation,
    maximize: bool,
    candidate_action: &Action,
    current_action: &Action,
) -> bool {
    let ordering = candidate
        .expected_utility
        .total_cmp(&current.expected_utility);
    (maximize && ordering.is_gt())
        || (!maximize && ordering.is_lt())
        || (ordering.is_eq() && candidate_action.action_id() < current_action.action_id())
}

fn evaluate_visible_policy_action(
    state: &GameState,
    action: &Action,
    remaining: u8,
    budget: &mut VisibleSearchBudget<'_, '_>,
    cancel: &AtomicBool,
    caller_confirmed: bool,
) -> Result<VisiblePolicyEvaluation, SolverError> {
    cancelled(cancel)?;
    let actor_is_friendly = state.active_player_id == state.friendly.player_id;
    let random_resolution = action_has_random_resolution(state, action);
    let outcomes = if caller_confirmed {
        apply_caller_confirmed_action_outcomes(state, action)?
    } else {
        apply_action_outcomes(state, action)?
    };
    if outcomes.is_empty() {
        return Err(SolverError::IllegalAction(
            "visible chance transition produced no outcomes".to_owned(),
        ));
    }

    let mut probability_sum = ExactProbability::new(0, 1)?;
    let mut expected_utility = 0.0;
    let mut minimum_utility = i64::MAX;
    let mut maximum_utility = i64::MIN;
    let mut survival_probability = ExactProbability::new(0, 1)?;
    let mut representative = None::<VisiblePolicyEvaluation>;
    for outcome in outcomes.iter() {
        probability_sum = probability_sum.add(outcome.probability)?;
        let mut child_state = outcome.state.clone();
        let child = if terminal(&child_state) {
            visible_policy_leaf(&child_state)?
        } else if outcome.ended_turn && actor_is_friendly {
            child_state = advance_to_visible_opponent_start(&child_state)?;
            if remaining <= 1 || terminal(&child_state) {
                visible_policy_leaf(&child_state)?
            } else {
                evaluate_visible_policy_state(&child_state, remaining - 1, budget, cancel)?
            }
        } else if outcome.ended_turn || remaining <= 1 {
            visible_policy_leaf(&child_state)?
        } else {
            evaluate_visible_policy_state(&child_state, remaining - 1, budget, cancel)?
        };
        expected_utility += probability_as_f64(outcome.probability) * child.expected_utility;
        minimum_utility = minimum_utility.min(child.minimum_utility);
        maximum_utility = maximum_utility.max(child.maximum_utility);
        survival_probability =
            survival_probability.add(outcome.probability.multiply(child.survival_probability)?)?;
        if representative
            .as_ref()
            .is_none_or(|current| child.expected_utility < current.expected_utility)
        {
            representative = Some(child);
        }
    }
    if probability_sum != ExactProbability::CERTAIN {
        return Err(SolverError::Unsupported(
            "visible chance probabilities do not sum to one".to_owned(),
        ));
    }
    let mut representative = representative.ok_or_else(|| {
        SolverError::IllegalAction("visible chance representative is missing".to_owned())
    })?;
    representative
        .approximate_entity_ids
        .extend(action_approximate_entity_ids(state, action));
    let branch_requires_recompute = random_resolution && outcomes.len() > 1;
    if branch_requires_recompute {
        representative.actions.clear();
        representative.opponent_reply.clear();
    }
    if actor_is_friendly {
        representative.actions.insert(0, action.clone());
    } else {
        representative.opponent_reply.insert(0, action.clone());
    }
    Ok(VisiblePolicyEvaluation {
        expected_utility,
        minimum_utility,
        maximum_utility,
        survival_probability,
        actions: representative.actions,
        opponent_reply: representative.opponent_reply,
        terminal_state: representative.terminal_state,
        approximate_entity_ids: representative.approximate_entity_ids,
        recompute_after_random_outcome: branch_requires_recompute
            || representative.recompute_after_random_outcome,
    })
}

fn evaluate_visible_policy_state(
    state: &GameState,
    remaining: u8,
    budget: &mut VisibleSearchBudget<'_, '_>,
    cancel: &AtomicBool,
) -> Result<VisiblePolicyEvaluation, SolverError> {
    cancelled(cancel)?;
    if remaining == 0 || terminal(state) {
        return visible_policy_leaf(state);
    }
    // Chance-aware recursion must preserve the same invariant as the beam
    // search: actions are generated from, and then applied to, one normalized
    // state. A deterministic branch inside a position containing any random
    // source can equip a weapon through a replay effect. Generating the hero
    // attack from a normalized clone but applying it to the stale branch state
    // caused the whole solve to fail with `illegal_action`.
    let normalized = normalized_visible_state(state)?;
    let state = &normalized;
    let maximize = state.active_player_id == state.friendly.player_id;
    let mut actions = visible_legal_actions(state)?;
    budget.control.order_actions(state, &mut actions);
    if actions.is_empty() {
        return visible_policy_leaf(state);
    }
    let mut selected = None::<(Action, VisiblePolicyEvaluation)>;
    for action in actions {
        if !budget.spend_node()? {
            break;
        }
        let evaluation =
            evaluate_visible_policy_action(state, &action, remaining, budget, cancel, false)?;
        if selected.as_ref().is_none_or(|(current_action, current)| {
            visible_policy_evaluation_is_better(
                &evaluation,
                current,
                maximize,
                &action,
                current_action,
            )
        }) {
            selected = Some((action, evaluation));
        }
    }
    selected
        .map(|(_, evaluation)| evaluation)
        .map_or_else(|| visible_policy_leaf(state), Ok)
}

fn has_visible_chance_source(state: &GameState) -> bool {
    [&state.friendly, &state.opponent].into_iter().any(|owner| {
        owner
            .hand
            .iter()
            .chain(owner.board.iter())
            .chain(owner.hero_power.iter())
            .any(|card| card.effects.iter().any(|effect| effect.random))
    })
}

fn rounded_expected_utility(value: f64) -> i64 {
    value.round().clamp(i64::MIN as f64, i64::MAX as f64) as i64
}

fn visible_line_is_better(candidate: &VisibleResponseLine, current: &VisibleResponseLine) -> bool {
    candidate.tactical_value > current.tactical_value
        || (candidate.tactical_value == current.tactical_value
            && (early_hero_power_penalty(&candidate.actions)
                < early_hero_power_penalty(&current.actions)
                || (early_hero_power_penalty(&candidate.actions)
                    == early_hero_power_penalty(&current.actions)
                    && (candidate.actions.len() < current.actions.len()
                        || (candidate.actions.len() == current.actions.len()
                            && line_ids(&candidate.actions) < line_ids(&current.actions))))))
}

/// Build a bounded public-information portfolio after exact and scoped proof
/// paths decline the position. Every modeled root keeps an immediate end-turn
/// baseline, so exhausting the node or depth budget never discards the entire
/// result.
pub fn plan_visible_response(
    state: &GameState,
    top_k: usize,
    max_nodes: usize,
    max_depth: u8,
    cancel: &AtomicBool,
) -> Result<VisibleResponsePlan, SolverError> {
    let mut control = SearchControl::new(cancel, max_nodes, None);
    plan_visible_response_with_control(state, top_k, max_depth, &mut control)
}

/// Bounded visible-response planning that shares the caller's request-wide
/// deadline and node allowance. Deadline or node exhaustion only stops deeper
/// expansion: every modeled root still receives its immediate end-turn and
/// no-response baseline before candidates are ranked.
pub fn plan_visible_response_with_control(
    state: &GameState,
    top_k: usize,
    max_depth: u8,
    control: &mut SearchControl<'_>,
) -> Result<VisibleResponsePlan, SolverError> {
    plan_visible_response_with_control_and_roots(state, top_k, max_depth, control, None)
}

/// Visible-response planning with an optional caller-confirmed complete HDT root portfolio.
/// The portfolio replaces only root generation; every later action remains independently
/// generated and every unmodeled supplied root stays visible in coverage as omitted.
pub fn plan_visible_response_with_control_and_roots(
    state: &GameState,
    top_k: usize,
    max_depth: u8,
    control: &mut SearchControl<'_>,
    hdt_roots: Option<&HdtRootCandidateSet>,
) -> Result<VisibleResponsePlan, SolverError> {
    let cancel = control.cancel;
    cancelled(cancel)?;
    if state.active_player_id != state.friendly.player_id
        || state.perspective_player_id != state.friendly.player_id
    {
        return Err(SolverError::Unsupported(
            "visible-response-v1 requires the friendly active perspective".to_owned(),
        ));
    }
    let visible_state = normalized_visible_state(state)?;
    assert_visible_attack_snapshots(&visible_state)?;
    let independent_actions = legal_actions(&visible_state)?;
    let independent_generated_first_action_ids = independent_actions
        .iter()
        .map(root_action_id)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect::<Vec<_>>();
    let (
        mut modeled_actions,
        legal_first_action_ids,
        omitted_first_action_ids,
        caller_confirmed_root,
    ) = if let Some(roots) = hdt_roots {
        let legal_ids = roots.action_ids();
        let mut modeled = Vec::new();
        let mut modeled_ids = BTreeSet::new();
        for action in roots.solver_actions() {
            if visible_action_is_modeled(&visible_state, &action) {
                modeled_ids.insert(root_action_id(&action));
                modeled.push(action);
            }
        }
        let omitted = legal_ids
            .difference(&modeled_ids)
            .cloned()
            .collect::<Vec<_>>();
        (modeled, legal_ids.into_iter().collect(), omitted, true)
    } else {
        let (modeled, omitted) = visible_action_partition_ready(&visible_state)?;
        let legal = modeled.iter().map(root_action_id).collect::<BTreeSet<_>>();
        let omitted_ids = omitted
            .iter()
            .map(root_action_id)
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        (modeled, legal.into_iter().collect(), omitted_ids, false)
    };
    if modeled_actions.is_empty() {
        return Err(SolverError::IllegalAction(
            "visible-response-v1 produced no modeled root action".to_owned(),
        ));
    }
    control.order_actions(&visible_state, &mut modeled_actions);
    let modeled_first_action_ids = modeled_actions
        .iter()
        .map(root_action_id)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect::<Vec<_>>();
    let initial_board_approximate_entity_ids = visible_state
        .friendly
        .board
        .iter()
        .chain(visible_state.opponent.board.iter())
        .filter(|card| minion_requires_whiteboard(card))
        .map(|card| card.entity_id.to_string())
        .collect::<BTreeSet<_>>();
    let starting_nodes = control.nodes();
    let mut best_by_root = BTreeMap::<String, VisibleResponseLine>::new();
    let mut assessed_line_count = 0usize;
    let mut node_limit_reached = control.node_limit_reached;
    let mut depth_limit_reached = false;
    let mut time_limit_reached = control.time_limit_reached;
    let mut root_quota_reached = false;
    let root_count = modeled_actions.len();
    let chance_mode = has_visible_chance_source(&visible_state);

    for (root_index, root) in modeled_actions.into_iter().enumerate() {
        cancelled(cancel)?;
        let remaining_roots = root_count.saturating_sub(root_index).max(1);
        let root_node_allowance = control.remaining_nodes().div_ceil(remaining_roots);
        let mut budget = VisibleSearchBudget::new(control, root_node_allowance);
        let root_id = root_action_id(&root);
        if chance_mode {
            let mut evaluation = evaluate_visible_policy_action(
                &visible_state,
                &root,
                max_depth.max(1),
                &mut budget,
                cancel,
                caller_confirmed_root,
            )?;
            evaluation
                .approximate_entity_ids
                .extend(initial_board_approximate_entity_ids.iter().cloned());
            let chance =
                evaluation
                    .recompute_after_random_outcome
                    .then_some(VisibleChanceSummary {
                        expected_utility: evaluation.expected_utility,
                        minimum_utility: evaluation.minimum_utility,
                        maximum_utility: evaluation.maximum_utility,
                        survival_probability: evaluation.survival_probability,
                        recompute_after_random_outcome: true,
                    });
            best_by_root.insert(
                root_id,
                VisibleResponseLine {
                    actions: evaluation.actions,
                    opponent_reply: evaluation.opponent_reply,
                    tactical_value: rounded_expected_utility(evaluation.expected_utility),
                    terminal_state: evaluation.terminal_state,
                    approximate_entity_ids: evaluation.approximate_entity_ids.into_iter().collect(),
                    chance,
                },
            );
            assessed_line_count = assessed_line_count.saturating_add(1);
            node_limit_reached |= budget.node_limit_reached;
            depth_limit_reached |= budget.depth_limit_reached;
            time_limit_reached |= budget.time_limit_reached;
            root_quota_reached |= budget.root_quota_reached;
            continue;
        }
        let candidates = friendly_visible_candidates(
            &visible_state,
            &root,
            max_depth,
            &mut budget,
            cancel,
            caller_confirmed_root,
        )?;
        let mut best = None::<VisibleResponseLine>;
        for candidate in candidates {
            cancelled(cancel)?;
            let (reply, assessed_replies) =
                worst_visible_reply(&candidate.state, max_depth, &mut budget, cancel)?;
            assessed_line_count = assessed_line_count.saturating_add(assessed_replies);
            let mut approximate_entity_ids = candidate.approximate_entity_ids;
            approximate_entity_ids.extend(reply.approximate_entity_ids);
            approximate_entity_ids.extend(initial_board_approximate_entity_ids.iter().cloned());
            let line = VisibleResponseLine {
                actions: candidate.actions,
                opponent_reply: reply.actions,
                tactical_value: tactical_utility(
                    &reply.state,
                    &visible_state.perspective_player_id,
                ),
                terminal_state: reply.state,
                approximate_entity_ids: approximate_entity_ids.into_iter().collect(),
                chance: None,
            };
            if best
                .as_ref()
                .is_none_or(|current| visible_line_is_better(&line, current))
            {
                best = Some(line);
            }
        }
        if let Some(best) = best {
            best_by_root.insert(root_id, best);
        }
        node_limit_reached |= budget.node_limit_reached;
        depth_limit_reached |= budget.depth_limit_reached;
        time_limit_reached |= budget.time_limit_reached;
        root_quota_reached |= budget.root_quota_reached;
    }

    let mut lines = best_by_root
        .into_values()
        .filter(|line| {
            !visible_line_contains_unadvisable_action(
                &visible_state,
                &line.actions,
                caller_confirmed_root,
            )
        })
        .collect::<Vec<_>>();
    lines.sort_by(|left, right| {
        right
            .tactical_value
            .cmp(&left.tactical_value)
            .then_with(|| {
                early_hero_power_penalty(&left.actions)
                    .cmp(&early_hero_power_penalty(&right.actions))
            })
            .then_with(|| line_ids(&left.actions).cmp(&line_ids(&right.actions)))
    });
    lines.truncate(top_k.max(1));
    // Reaching an equal-share root quota is still a node-budget truncation even
    // when a simpler later root leaves a few request nodes unused.
    node_limit_reached |= control.node_limit_reached || root_quota_reached;
    control.observe_deadline();
    time_limit_reached |= control.time_limit_reached;
    Ok(VisibleResponsePlan {
        lines,
        legal_first_action_ids,
        modeled_first_action_ids,
        omitted_first_action_ids,
        independent_generated_first_action_ids,
        hdt_supplied_root_portfolio: caller_confirmed_root,
        nodes_expanded: control.nodes().saturating_sub(starting_nodes),
        assessed_line_count,
        node_limit_reached,
        depth_limit_reached,
        time_limit_reached,
    })
}

fn enumerate_complete_lines(
    initial: &GameState,
    max_depth: u8,
    control: &mut SearchControl<'_>,
) -> Result<(Vec<CompleteLine>, SearchStats), SolverError> {
    type Memo = HashMap<(StateKey, u8), Vec<CompleteLine>>;

    fn visit(
        current: &GameState,
        remaining: u8,
        depth_limit: u8,
        control: &mut SearchControl<'_>,
        memo: &mut Memo,
        stats: &mut SearchStats,
    ) -> Result<Vec<CompleteLine>, SolverError> {
        control.checkpoint()?;
        if terminal(current) {
            return Ok(vec![CompleteLine {
                actions: Vec::new(),
                state: current.clone(),
                ended_turn: false,
            }]);
        }
        if remaining == 0 {
            return Err(SolverError::DepthLimit(depth_limit));
        }
        let key = (StateKey::from_state(current), remaining);
        if let Some(cached) = memo.get(&key) {
            stats.transposition_hits += 1;
            return Ok(cached.clone());
        }
        let mut actions = legal_actions(current)?;
        actions.sort_by_key(Action::action_id);
        control.order_actions(current, &mut actions);
        let mut results = Vec::new();
        for action in actions {
            control.spend_node()?;
            stats.nodes += 1;
            let (child, ended) = apply_action(current, &action)?;
            if ended || terminal(&child) {
                results.push(CompleteLine {
                    actions: vec![action],
                    state: child,
                    ended_turn: ended,
                });
                continue;
            }
            for suffix in visit(&child, remaining - 1, depth_limit, control, memo, stats)? {
                let mut combined = Vec::with_capacity(suffix.actions.len() + 1);
                combined.push(action.clone());
                combined.extend(suffix.actions);
                results.push(CompleteLine {
                    actions: combined,
                    state: suffix.state,
                    ended_turn: suffix.ended_turn,
                });
            }
        }
        let mut unique = BTreeMap::new();
        for line in results {
            unique.entry(line_ids(&line.actions)).or_insert(line);
        }
        let results = unique.into_values().collect::<Vec<_>>();
        memo.insert(key, results.clone());
        Ok(results)
    }

    let mut memo = Memo::new();
    let mut stats = SearchStats::default();
    let mut lines = visit(
        initial, max_depth, max_depth, control, &mut memo, &mut stats,
    )?;
    lines.sort_by_key(|line| (line.actions.len(), line_ids(&line.actions)));
    Ok((lines, stats))
}

/// Apply the deterministic public transition between the two modeled turns.
pub fn advance_to_opponent_start(state: &GameState) -> Result<GameState, SolverError> {
    let mut next = state.clone();
    let active_id = Arc::clone(&next.active_player_id);
    let actor = next.player_mut(&active_id)?;
    actor.max_mana = actor.max_mana.saturating_add(1).min(10);
    actor.mana = actor.max_mana;
    for card in &mut actor.board {
        card.summoned_this_turn = false;
        card.attacks_remaining = u8::from(card.attack > 0 && !card.dormant && !card.frozen);
        card.attacks_remaining_known = true;
        card.can_attack = card.attacks_remaining > 0;
    }
    actor.hero.attacks_remaining = u8::from(actor.hero.attack > 0 && !actor.hero.frozen);
    actor.hero.attacks_remaining_known = true;
    actor.hero.can_attack = actor.hero.attacks_remaining > 0;
    if actor.deck_size > 0 {
        return Err(SolverError::Unsupported(
            "opponent draw identity is not deterministic".to_owned(),
        ));
    }
    actor.fatigue = actor.fatigue.saturating_add(1);
    let damage = actor.fatigue;
    if actor.hero.immune {
        return Ok(next);
    }
    if actor.hero.divine_shield {
        actor.hero.divine_shield = false;
        return Ok(next);
    }
    let absorbed = actor.armor.min(damage);
    actor.armor -= absorbed;
    actor.hero.current_health = actor
        .hero
        .current_health
        .saturating_sub(damage.saturating_sub(absorbed));
    Ok(next)
}

fn response_for_line(
    line: &CompleteLine,
    perspective_player_id: &str,
    max_depth: u8,
    control: &mut SearchControl<'_>,
) -> Result<(i64, bool, Vec<Action>, GameState, SearchStats), SolverError> {
    if terminal(&line.state) {
        let value = tactical_utility(&line.state, perspective_player_id);
        let safe = line
            .state
            .player(perspective_player_id)?
            .hero
            .current_health
            > 0;
        return Ok((
            value,
            safe,
            Vec::new(),
            line.state.clone(),
            SearchStats::default(),
        ));
    }
    if !line.ended_turn {
        return Err(SolverError::IllegalAction(
            "friendly turn-pair line is incomplete".to_owned(),
        ));
    }
    let response_start = advance_to_opponent_start(&line.state)?;
    if terminal(&response_start) {
        let value = tactical_utility(&response_start, perspective_player_id);
        let safe = response_start
            .player(perspective_player_id)?
            .hero
            .current_health
            > 0;
        return Ok((
            value,
            safe,
            Vec::new(),
            response_start,
            SearchStats::default(),
        ));
    }
    let (responses, stats) = enumerate_complete_lines(&response_start, max_depth, control)?;
    if responses.is_empty() {
        return Err(SolverError::IllegalAction(
            "opponent response enumeration produced no complete line".to_owned(),
        ));
    }
    let safe = responses.iter().all(|response| {
        response
            .state
            .player(perspective_player_id)
            .is_ok_and(|player| player.hero.current_health > 0)
    });
    let worst = responses
        .into_iter()
        .min_by_key(|response| {
            (
                tactical_utility(&response.state, perspective_player_id),
                line_ids(&response.actions),
            )
        })
        .ok_or_else(|| SolverError::IllegalAction("missing opponent response".to_owned()))?;
    let value = tactical_utility(&worst.state, perspective_player_id);
    Ok((value, safe, worst.actions, worst.state, stats))
}

/// Exhaustively prove minimax utility and the visible worst response.
pub fn prove_turnpair(
    state: &GameState,
    allow_point_effects: bool,
    max_nodes: usize,
    max_depth: u8,
    cancel: &AtomicBool,
) -> Result<TurnPairProof, SolverError> {
    let mut control = SearchControl::new(cancel, max_nodes, None);
    prove_turnpair_with_control(state, allow_point_effects, max_depth, &mut control)
}

/// Exhaustive proof using request-wide limits supplied by the live pipeline.
/// Any interruption returns an error, so a truncated tree can never acquire
/// exact/minimax proof fields.
pub fn prove_turnpair_with_control(
    state: &GameState,
    allow_point_effects: bool,
    max_depth: u8,
    control: &mut SearchControl<'_>,
) -> Result<TurnPairProof, SolverError> {
    control.checkpoint()?;
    assert_turnpair_state(state, allow_point_effects)?;
    let legal_first_action_ids = legal_actions(state)?
        .into_iter()
        .map(|action| root_action_id(&action))
        .collect::<BTreeSet<_>>();
    let (friendly_lines, friendly_stats) = enumerate_complete_lines(state, max_depth, control)?;
    if friendly_lines.is_empty() {
        return Err(SolverError::IllegalAction(
            "turn-pair oracle produced no friendly line".to_owned(),
        ));
    }
    let mut analyzed = Vec::with_capacity(friendly_lines.len());
    let mut response_nodes = 0usize;
    let mut response_hits = 0usize;
    for line in friendly_lines {
        control.checkpoint()?;
        let immediate_lethal = line.state.opponent.hero.current_health == 0;
        let (value, safe, response, terminal_state, stats) =
            response_for_line(&line, &state.perspective_player_id, max_depth, control)?;
        response_nodes = response_nodes.saturating_add(stats.nodes);
        response_hits = response_hits.saturating_add(stats.transposition_hits);
        analyzed.push(TurnPairLine {
            actions: line.actions,
            opponent_response: response,
            terminal_state,
            minimax_value: value,
            safe_after_response: safe,
            immediate_lethal,
            response_nodes_expanded: stats.nodes,
            response_transposition_hits: stats.transposition_hits,
        });
    }
    let optimal_value = analyzed
        .iter()
        .map(|line| line.minimax_value)
        .max()
        .ok_or_else(|| SolverError::IllegalAction("turn-pair optimum is missing".to_owned()))?;
    let mut optimal_first_action_ids = analyzed
        .iter()
        .filter(|line| line.minimax_value == optimal_value)
        .map(TurnPairLine::first_action_id)
        .collect::<Vec<_>>();
    optimal_first_action_ids.sort();
    optimal_first_action_ids.dedup();
    let generated_first_action_ids = analyzed
        .iter()
        .map(TurnPairLine::first_action_id)
        .collect::<BTreeSet<_>>();
    // A line enters `analyzed` only after its complete visible worst response has
    // been enumerated.  Keep the two sets distinct in the contract so a future
    // bounded/partial implementation cannot silently equate generation with proof.
    let response_verified_first_action_ids = generated_first_action_ids.clone();
    let root_action_coverage = RootActionCoverage::from_sets(
        legal_first_action_ids,
        generated_first_action_ids,
        response_verified_first_action_ids,
    );
    let portfolio_optimality_proven = root_action_coverage.root_action_coverage_complete;
    control.checkpoint()?;
    Ok(TurnPairProof {
        lines: analyzed,
        optimal_value,
        optimal_first_action_ids,
        root_action_coverage,
        portfolio_optimality_proven,
        friendly_nodes_expanded: friendly_stats.nodes,
        response_nodes_expanded: response_nodes,
        transposition_hits: friendly_stats
            .transposition_hits
            .saturating_add(response_hits),
    })
}

/// Honest root-action coverage for a scoped proof that intentionally ignores
/// unsupported alternatives.  The denominator remains every initially legal
/// action, while only the proven line's first action is generated and verified.
pub fn scoped_root_action_coverage(
    state: &GameState,
    line: &TurnPairLine,
) -> Result<RootActionCoverage, SolverError> {
    let legal_first_action_ids = legal_actions(state)?
        .into_iter()
        .map(|action| root_action_id(&action))
        .collect::<BTreeSet<_>>();
    let first_action_id = line.first_action_id();
    let generated_first_action_ids = BTreeSet::from([first_action_id]);
    let response_verified_first_action_ids = generated_first_action_ids.clone();
    Ok(RootActionCoverage::from_sets(
        legal_first_action_ids,
        generated_first_action_ids,
        response_verified_first_action_ids,
    ))
}

/// Stable one-line selection used by the parity protocol.
pub fn choose_parity_line(proof: &TurnPairProof) -> Result<TurnPairLine, SolverError> {
    if proof.optimal_first_action_ids.is_empty() {
        return Err(SolverError::IllegalAction(
            "turn-pair Top1 is missing".to_owned(),
        ));
    }
    proof
        .lines
        .iter()
        .filter(|line| line.minimax_value == proof.optimal_value)
        .min_by_key(|line| {
            (
                early_hero_power_penalty(&line.actions),
                line.actions.len(),
                line_ids(&line.actions),
            )
        })
        .cloned()
        .ok_or_else(|| SolverError::IllegalAction("turn-pair Top1 line is missing".to_owned()))
}

/// Return at most one stable recommendation per distinct first action.
///
/// When exhaustive root coverage proves multiple co-optimal decisions, expose
/// that co-optimal set without padding the remaining Top-K slots with an inferior
/// root. With a unique optimum, lower-regret backups remain useful alternatives.
pub fn ranked_lines(proof: &TurnPairProof, top_k: usize) -> Vec<TurnPairLine> {
    let mut best: HashMap<String, TurnPairLine> = HashMap::new();
    let legal_first_actions = proof
        .root_action_coverage
        .legal_first_action_ids
        .iter()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    for line in &proof.lines {
        let first = line.first_action_id();
        if !legal_first_actions.contains(first.as_str()) {
            continue;
        }
        let replace = best.get(&first).is_none_or(|current| {
            line.minimax_value > current.minimax_value
                || (line.minimax_value == current.minimax_value
                    && line_ids(&line.actions) > line_ids(&current.actions))
        });
        if replace {
            best.insert(first, line.clone());
        }
    }
    let mut values = best.into_values().collect::<Vec<_>>();
    values.sort_by(|left, right| {
        right
            .minimax_value
            .cmp(&left.minimax_value)
            .then_with(|| {
                early_hero_power_penalty(&left.actions)
                    .cmp(&early_hero_power_penalty(&right.actions))
            })
            .then_with(|| line_ids(&left.actions).cmp(&line_ids(&right.actions)))
    });
    if values
        .iter()
        .any(|line| line.immediate_lethal || line.safe_after_response)
    {
        values.retain(|line| line.immediate_lethal || line.safe_after_response);
    }
    let cooptimal_count = values
        .iter()
        .filter(|line| line.minimax_value == proof.optimal_value)
        .count();
    if proof.root_action_coverage.root_action_coverage_complete && cooptimal_count >= 2 {
        values.retain(|line| line.minimax_value == proof.optimal_value);
    }
    values.truncate(top_k.max(1));
    values
}

fn action_source_is_modeled(state: &GameState, action: &Action) -> bool {
    match action.kind {
        ActionKind::Attack => true,
        ActionKind::EndTurn => false,
        ActionKind::LocationActivate => false,
        ActionKind::PlayCard | ActionKind::HeroPower => state
            .friendly
            .hand
            .iter()
            .chain(state.friendly.hero_power.iter())
            .chain(state.opponent.hand.iter())
            .chain(state.opponent.hero_power.iter())
            .find(|card| card.entity_id == action.source_entity_id)
            .is_some_and(|card| {
                !card.stealth
                    && supported_visible_effect_card(card)
                    && !card_unsupported(card, true)
            }),
    }
}

fn assert_scoped_lethal_board(state: &GameState) -> Result<(), SolverError> {
    if state.active_player_id != state.friendly.player_id
        || state.perspective_player_id != state.friendly.player_id
    {
        return Err(SolverError::Unsupported(
            "scoped lethal requires the friendly active perspective".to_owned(),
        ));
    }
    for owner in [&state.friendly, &state.opponent] {
        if owner.weapon.is_some() {
            return Err(SolverError::Unsupported(
                "scoped lethal does not model weapons".to_owned(),
            ));
        }
        for card in std::iter::once(&owner.hero).chain(owner.board.iter()) {
            if attack_snapshot_reason(card).is_some()
                || card_unsupported(card, true)
                || card.stealth
                || card.frozen
                || card.poisonous
                || card.lifesteal
                || card.windfury
                || card.mega_windfury
                || card.rush
                || card.charge
                || card.reborn
                || card.dormant
                || card.immune
            {
                return Err(SolverError::Unsupported(format!(
                    "{} prevents a clean scoped lethal proof",
                    card.entity_id
                )));
            }
        }
    }
    Ok(())
}

/// Legal action surface available to an independent scoped-lethal proof.
pub fn scoped_legal_actions(state: &GameState) -> Result<Vec<Action>, SolverError> {
    assert_scoped_lethal_board(state)?;
    let mut actions = legal_actions(state)?
        .into_iter()
        .filter(|action| {
            action.kind == ActionKind::EndTurn || action_source_is_modeled(state, action)
        })
        .collect::<Vec<_>>();
    actions.sort_by_key(Action::action_id);
    Ok(actions)
}

/// Prove an independently modeled current-turn lethal while retaining unknown
/// alternative cards as evidence. Unknown actions are never applied or scored;
/// success is only an existential proof for a clean, fully modeled lethal line.
pub fn prove_scoped_lethal(
    state: &GameState,
    max_nodes: usize,
    max_depth: u8,
    cancel: &AtomicBool,
) -> Result<Option<TurnPairLine>, SolverError> {
    let mut control = SearchControl::new(cancel, max_nodes, None);
    prove_scoped_lethal_with_control(state, max_depth, &mut control)
}

/// Scoped-lethal proof using the live request's remaining node/time budget.
pub fn prove_scoped_lethal_with_control(
    state: &GameState,
    max_depth: u8,
    control: &mut SearchControl<'_>,
) -> Result<Option<TurnPairLine>, SolverError> {
    control.checkpoint()?;
    assert_scoped_lethal_board(state)?;
    let mut queue = VecDeque::from([(state.clone(), Vec::<Action>::new())]);
    let mut visited = HashSet::from([StateKey::from_state(state)]);
    while let Some((current, prefix)) = queue.pop_front() {
        control.checkpoint()?;
        if prefix.len() >= usize::from(max_depth) {
            continue;
        }
        let mut actions = scoped_legal_actions(&current)?
            .into_iter()
            .filter(|action| action.kind != ActionKind::EndTurn)
            .collect::<Vec<_>>();
        control.order_actions(&current, &mut actions);
        for action in actions {
            control.spend_node()?;
            let (child, _) = apply_action(&current, &action)?;
            let mut child_actions = prefix.clone();
            child_actions.push(action);
            if child.opponent.hero.current_health == 0 {
                control.checkpoint()?;
                return Ok(Some(TurnPairLine {
                    actions: child_actions,
                    opponent_response: Vec::new(),
                    minimax_value: WIN_UTILITY,
                    safe_after_response: child.friendly.hero.current_health > 0,
                    immediate_lethal: true,
                    terminal_state: child,
                    response_nodes_expanded: 0,
                    response_transposition_hits: 0,
                }));
            }
            let key = StateKey::from_state(&child);
            if visited.insert(key) {
                queue.push_back((child, child_actions));
            }
        }
    }
    Ok(None)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::SolveRequest;

    fn request(value: &str) -> SolveRequest {
        let mut request: SolveRequest = serde_json::from_str(value).expect("request JSON");
        request.validate().expect("valid request");
        request
    }

    #[test]
    fn refreshes_opponent_and_applies_fatigue() {
        let request = request(
            r#"{"request_id":"refresh","state":{"state_id":"s","turn":2,"active_player_id":"o","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"max_mana":2,"mana":0,"deck_size":0,"board":[{"entity_id":"m","card_type":"MINION","attack":3,"health":2}]}}}"#,
        );
        let next = advance_to_opponent_start(&request.state).expect("refresh");
        assert_eq!(next.opponent.max_mana, 3);
        assert_eq!(next.opponent.mana, 3);
        assert_eq!(next.opponent.fatigue, 1);
        assert_eq!(next.opponent.hero.current_health, 29);
        assert!(next.opponent.board[0].can_attack);
    }

    #[test]
    fn exact_refresh_keeps_frozen_characters_inactive() {
        let request = request(
            r#"{"request_id":"frozen-refresh","state":{"state_id":"s","turn":2,"active_player_id":"o","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","attack":2,"health":30,"frozen":true},"deck_size":0,"board":[{"entity_id":"m","card_type":"MINION","attack":3,"health":2,"frozen":true}]}}}"#,
        );
        let next = advance_to_opponent_start(&request.state).expect("frozen refresh");
        assert_eq!(next.opponent.board[0].attacks_remaining, 0);
        assert!(!next.opponent.board[0].can_attack);
        assert_eq!(next.opponent.hero.attacks_remaining, 0);
        assert!(!next.opponent.hero.can_attack);
    }

    #[test]
    fn visible_refresh_never_draws_or_applies_fatigue() {
        let request = request(
            r#"{"request_id":"visible-refresh","state":{"state_id":"s","turn":2,"active_player_id":"o","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30,"current_health":28},"max_mana":2,"mana":0,"deck_size":7,"fatigue":3,"board":[{"entity_id":"m","card_type":"MINION","attack":3,"health":2}]}}}"#,
        );
        let next = advance_to_visible_opponent_start(&request.state).expect("visible refresh");
        assert_eq!(next.opponent.deck_size, 7);
        assert_eq!(next.opponent.fatigue, 3);
        assert_eq!(next.opponent.hero.current_health, 28);
        assert_eq!(next.opponent.max_mana, 3);
        assert_eq!(next.opponent.mana, 3);
        assert!(next.opponent.board[0].can_attack);
    }

    #[test]
    fn visible_policy_models_random_targets_as_chance_and_stops_ui_line_for_recompute() {
        let request = request(
            r#"{
              "request_id":"visible-chance",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,
                  "hand":[{"entity_id":"sleet","card_id":"CATA_485","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"},{"kind":"damage","amount":1,"target":"enemy_minion","random":true}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"small","card_type":"MINION","attack":3,"health":1},{"entity_id":"large","card_type":"MINION","attack":1,"health":3}]}}
            }"#,
        );
        let plan = plan_visible_response(&request.state, 16, 10_000, 6, &AtomicBool::new(false))
            .expect("visible chance plan");
        assert!(
            plan.modeled_first_action_ids
                .iter()
                .any(|action_id| action_id == "play_card:sleet:oh")
        );
        let line = plan
            .lines
            .iter()
            .find(|line| line.first_action_id() == "play_card:sleet:oh")
            .expect("Sleet Storm root line");
        let chance = line.chance.as_ref().expect("chance summary");
        assert!(chance.recompute_after_random_outcome);
        assert!(chance.minimum_utility < chance.maximum_utility);
        assert_eq!(line.actions.len(), 1);
        assert_eq!(line.actions[0].action_id(), "play_card:sleet:oh");
        assert!(line.opponent_reply.is_empty());
        assert!(chance.survival_probability.denominator > 0);
    }

    #[test]
    fn visible_refresh_restores_windfury_counts_but_keeps_frozen_minions_inactive() {
        let request = request(
            r#"{"request_id":"visible-keyword-refresh","state":{"state_id":"s","turn":2,"active_player_id":"o","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"deck_size":6,"fatigue":1,"board":[{"entity_id":"wind","card_type":"MINION","attack":2,"health":2,"windfury":true},{"entity_id":"mega","card_type":"MINION","attack":2,"health":2,"mega_windfury":true},{"entity_id":"frozen","card_type":"MINION","attack":2,"health":2,"windfury":true,"frozen":true}]}}}"#,
        );
        let next = advance_to_visible_opponent_start(&request.state).expect("visible refresh");
        assert_eq!(next.opponent.board[0].attacks_remaining, 2);
        assert_eq!(next.opponent.board[1].attacks_remaining, 4);
        assert_eq!(next.opponent.board[2].attacks_remaining, 0);
        assert!(!next.opponent.board[2].can_attack);
        assert_eq!(next.opponent.deck_size, 6);
        assert_eq!(next.opponent.fatigue, 1);
    }

    #[test]
    fn visible_refresh_uses_equipped_mega_windfury_for_the_opponent_hero() {
        let request = request(
            r#"{"request_id":"visible-weapon-mega-refresh","state":{"state_id":"s","turn":2,"active_player_id":"o","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","attack":3,"health":30},"weapon":{"entity_id":"ow","card_type":"WEAPON","attack":3,"durability":4,"current_durability":4,"mega_windfury":true},"deck_size":6}}}"#,
        );
        let next =
            advance_to_visible_opponent_start(&request.state).expect("visible weapon refresh");
        assert_eq!(next.opponent.hero.attacks_remaining, 4);
        assert!(next.opponent.hero.can_attack);
        assert!(
            visible_legal_actions(&next)
                .expect("mega-windfury hero actions")
                .iter()
                .any(|action| action.action_id() == "attack:oh:fh")
        );
    }

    #[test]
    fn continuous_hand_count_aura_reprices_hero_power_after_playing_a_card() {
        let request = request(
            r#"{"request_id":"cost-aura","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_id":"HERO","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},"hero_power":{"entity_id":"hp","card_id":"POWER","card_type":"HERO_POWER","cost":2,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_hero"}],"tags":{"COST":2,"TAG_LAST_KNOWN_COST_IN_HAND":2}},"hero_power_available":true,"mana":1,"max_mana":1,"hand":[{"entity_id":"spend","card_id":"SPEND","card_type":"MINION","cost":1,"attack":1,"health":1},{"entity_id":"f1","card_id":"F1","card_type":"MINION","cost":9,"health":1},{"entity_id":"f2","card_id":"F2","card_type":"MINION","cost":9,"health":1},{"entity_id":"f3","card_id":"F3","card_type":"MINION","cost":9,"health":1}],"board":[{"entity_id":"aura","card_id":"AURA","card_type":"MINION","attack":1,"health":3,"effect_coverage":"exact","effects":[{"kind":"set_hero_power_cost","amount":0,"target":"none","hand_count_at_most":3}],"tags":{"AURA":1}}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_id":"OPP_HERO","card_type":"HERO","health":30}}}}"#,
        );
        let play = visible_legal_actions(&request.state)
            .expect("aura root actions")
            .into_iter()
            .find(|action| action.source_entity_id.as_ref() == "spend")
            .expect("spend-card action");
        let (after_play, _) = apply_action(&request.state, &play).expect("play under aura");
        assert_eq!(after_play.friendly.hand.len(), 3);
        assert_eq!(
            after_play
                .friendly
                .hero_power
                .as_ref()
                .map(|power| power.cost),
            Some(0)
        );
        let power = visible_legal_actions(&after_play)
            .expect("free power actions")
            .into_iter()
            .find(|action| action.kind == ActionKind::HeroPower)
            .expect("zero-cost hero power");
        let (after_power, _) = apply_action(&after_play, &power).expect("free hero power");
        assert_eq!(after_power.friendly.mana, 0);
        assert_eq!(after_power.opponent.hero.current_health, 28);
    }

    #[test]
    fn continuous_hand_count_aura_restores_base_cost_when_source_dies() {
        let request = request(
            r#"{"request_id":"cost-aura-source-dies","state":{"state_id":"s","turn":1,"active_player_id":"o","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_id":"HERO","card_type":"HERO","health":30},"hero_power":{"entity_id":"hp","card_id":"POWER","card_type":"HERO_POWER","cost":0,"tags":{"COST":0,"TAG_LAST_KNOWN_COST_IN_HAND":2}},"hand":[{"entity_id":"f1","card_id":"F1","card_type":"MINION","health":1},{"entity_id":"f2","card_id":"F2","card_type":"MINION","health":1},{"entity_id":"f3","card_id":"F3","card_type":"MINION","health":1}],"board":[{"entity_id":"aura","card_id":"AURA","card_type":"MINION","health":1,"effect_coverage":"exact","effects":[{"kind":"set_hero_power_cost","amount":0,"target":"none","hand_count_at_most":3}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_id":"OPP_HERO","card_type":"HERO","health":30},"board":[{"entity_id":"attacker","card_id":"ATTACKER","card_type":"MINION","attack":1,"health":1,"can_attack":true,"attacks_remaining":1}]}}}"#,
        );
        let attack = visible_legal_actions(&request.state)
            .expect("actions while aura is active")
            .into_iter()
            .find(|action| action.action_id() == "attack:attacker:aura")
            .expect("attack that kills aura source");
        let (after, _) = apply_action(&request.state, &attack).expect("kill aura source");
        assert!(after.friendly.board.is_empty());
        assert_eq!(
            after.friendly.hero_power.as_ref().map(|power| power.cost),
            Some(2)
        );
    }

    #[test]
    fn continuous_cost_aura_fails_closed_without_public_base_cost_evidence() {
        let request = request(
            r#"{"request_id":"cost-aura-no-base","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_id":"HERO","card_type":"HERO","health":30},"hero_power":{"entity_id":"hp","card_id":"POWER","card_type":"HERO_POWER","cost":0,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_hero"}]},"hero_power_available":true,"hand":[{"entity_id":"f1","card_id":"F1","card_type":"MINION","cost":9,"health":1}],"board":[{"entity_id":"aura","card_id":"AURA","card_type":"MINION","health":3,"effect_coverage":"exact","effects":[{"kind":"set_hero_power_cost","amount":0,"target":"none","hand_count_at_most":3}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_id":"OPP_HERO","card_type":"HERO","health":30}}}}"#,
        );
        let error = visible_legal_actions(&request.state)
            .expect_err("missing base-cost evidence must abstain");
        assert!(
            error.to_string().contains("TAG_LAST_KNOWN_COST_IN_HAND"),
            "{error}"
        );
    }

    #[test]
    fn playing_weapon_equips_it_and_searches_the_immediate_hero_attack() {
        let request = request(
            r#"{"request_id":"equip-weapon","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_id":"HERO","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},"mana":1,"max_mana":1,"hand":[{"entity_id":"weapon","card_id":"WEAPON","card_type":"WEAPON","cost":1,"attack":3,"durability":2,"current_durability":2,"effect_coverage":"unsupported","unsupported_effects":["card_text_not_parsed"],"card_text":"Unknown Deathrattle."}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_id":"OPP_HERO","card_type":"HERO","health":30}}}}"#,
        );
        let plan = plan_visible_response(&request.state, 3, 256, 6, &AtomicBool::new(false))
            .expect("weapon visible plan");
        let line = plan
            .lines
            .iter()
            .find(|line| {
                line.actions.first().is_some_and(|action| {
                    action.kind == ActionKind::PlayCard
                        && action.source_entity_id.as_ref() == "weapon"
                })
            })
            .expect("weapon root line");
        assert!(line.actions.iter().any(|action| {
            action.kind == ActionKind::Attack
                && action.source_entity_id.as_ref() == "fh"
                && action.target_entity_id.as_ref() == "oh"
        }));
        assert!(line.approximate_entity_ids.iter().any(|id| id == "weapon"));
        assert_eq!(line.terminal_state.opponent.hero.current_health, 27);
        assert_eq!(
            line.terminal_state
                .friendly
                .weapon
                .as_ref()
                .map(|weapon| weapon.current_durability),
            Some(1)
        );
    }

    #[test]
    fn equipping_a_new_normal_weapon_does_not_restore_an_attack_already_used() {
        let request = request(
            r#"{"request_id":"equip-after-attack","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_id":"HERO","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":1}},"mana":1,"max_mana":1,"hand":[{"entity_id":"weapon","card_id":"WEAPON","card_type":"WEAPON","cost":1,"attack":3,"durability":2,"current_durability":2}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_id":"OPP_HERO","card_type":"HERO","health":30}}}}"#,
        );
        let equip = visible_legal_actions(&request.state)
            .expect("weapon actions")
            .into_iter()
            .find(|action| action.source_entity_id.as_ref() == "weapon")
            .expect("equip action");
        let (equipped, _) = apply_action(&request.state, &equip).expect("equip weapon");
        assert_eq!(equipped.friendly.hero.attack, 3);
        assert_eq!(equipped.friendly.hero.attacks_remaining, 0);
        assert!(!equipped.friendly.hero.can_attack);
        assert!(
            !visible_legal_actions(&equipped)
                .expect("post-equip actions")
                .iter()
                .any(|action| action.kind == ActionKind::Attack)
        );
    }

    #[test]
    fn replacing_a_weapon_preserves_non_weapon_hero_attack() {
        let request = request(
            r#"{"request_id":"replace-weapon","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_id":"HERO","card_type":"HERO","attack":5,"health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},"mana":1,"max_mana":1,"weapon":{"entity_id":"old","card_id":"OLD","card_type":"WEAPON","attack":3,"durability":1,"current_durability":1},"hand":[{"entity_id":"new","card_id":"NEW","card_type":"WEAPON","cost":1,"attack":4,"durability":2,"current_durability":2}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_id":"OPP_HERO","card_type":"HERO","health":30}}}}"#,
        );
        let equip = visible_legal_actions(&request.state)
            .expect("weapon replacement actions")
            .into_iter()
            .find(|action| action.source_entity_id.as_ref() == "new")
            .expect("equip replacement weapon");
        let (after, _) = apply_action(&request.state, &equip).expect("replace weapon");
        assert_eq!(after.friendly.hero.attack, 6);
        assert_eq!(
            after
                .friendly
                .weapon
                .as_ref()
                .map(|weapon| weapon.entity_id.as_ref()),
            Some("new")
        );
        assert_eq!(after.friendly.hero.attacks_remaining, 1);
        assert!(after.friendly.hero.can_attack);
    }

    #[test]
    fn visible_keyword_actions_are_allowed_without_relaxing_exact_turnpair_gate() {
        let request = request(
            r#"{"request_id":"visible-keywords","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":20},"board":[{"entity_id":"keyword","card_type":"MINION","attack":2,"health":3,"can_attack":true,"attacks_remaining":2,"stealth":true,"windfury":true,"poisonous":true,"lifesteal":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":0,"health":5}]}}}"#,
        );
        assert!(assert_turnpair_state(&request.state, true).is_err());
        let modeled = visible_legal_actions(&request.state)
            .expect("visible actions")
            .into_iter()
            .map(|item| item.action_id())
            .collect::<BTreeSet<_>>();
        assert!(modeled.contains("attack:keyword:target"));
        assert!(modeled.contains("attack:keyword:oh"));
        let (after, _) = apply_action(
            &request.state,
            &modeled
                .iter()
                .find(|id| id.as_str() == "attack:keyword:target")
                .and_then(|id| {
                    visible_legal_actions(&request.state)
                        .ok()?
                        .into_iter()
                        .find(|item| item.action_id() == *id)
                })
                .expect("keyword attack"),
        )
        .expect("apply keyword attack");
        assert!(!after.friendly.board[0].stealth);
        assert_eq!(after.friendly.board[0].attacks_remaining, 1);
        assert!(after.opponent.board.is_empty());
        assert_eq!(after.friendly.hero.current_health, 22);
    }

    #[test]
    fn exact_turnpair_allows_only_supported_damage_spell_lifesteal() {
        let supported = request(
            r#"{"request_id":"spell-lifesteal","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":5},"mana":2,"max_mana":2,"hand":[{"entity_id":"drain","card_type":"SPELL","cost":2,"lifesteal":true,"effect_coverage":"exact","effects":[{"kind":"damage","amount":3,"target":"any_minion"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":1,"health":2}]}}}"#,
        );
        assert_turnpair_state(&supported.state, true)
            .expect("exact point-effect mode supports damage-only spell Lifesteal");
        assert!(assert_turnpair_state(&supported.state, false).is_err());

        for unsupported in [
            request(
                r#"{"request_id":"minion-lifesteal","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"hand":[{"entity_id":"source","card_type":"MINION","cost":1,"attack":1,"health":1,"lifesteal":true,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"any_character"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
            ),
            request(
                r#"{"request_id":"healing-lifesteal","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"hand":[{"entity_id":"source","card_type":"SPELL","cost":1,"lifesteal":true,"effect_coverage":"exact","effects":[{"kind":"heal","amount":1,"target":"friendly_hero"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
            ),
            request(
                r#"{"request_id":"power-lifesteal","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,"hero_power_available":true,"hero_power":{"entity_id":"source","card_type":"HERO_POWER","cost":2,"lifesteal":true,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"any_character"}]}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
            ),
        ] {
            assert!(assert_turnpair_state(&unsupported.state, true).is_err());
        }
    }

    #[test]
    fn visible_weapon_attack_obeys_taunt_freeze_and_public_target_restrictions() {
        let taunt = request(
            r#"{"request_id":"weapon-taunt","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":3,"health":30,"can_attack":true,"attacks_remaining":1},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":3,"durability":2,"current_durability":2}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"taunt","card_type":"MINION","attack":1,"health":4,"taunt":true}]}}}"#,
        );
        let taunt_actions = visible_legal_actions(&taunt.state)
            .expect("visible weapon actions")
            .into_iter()
            .map(|action| action.action_id())
            .collect::<BTreeSet<_>>();
        assert!(taunt_actions.contains("attack:fh:taunt"));
        assert!(!taunt_actions.contains("attack:fh:oh"));
        assert!(crate::oracle::assert_exact_oracle_state(&taunt.state).is_err());
        assert!(assert_turnpair_state(&taunt.state, true).is_err());
        assert!(scoped_legal_actions(&taunt.state).is_err());

        let frozen = request(
            r#"{"request_id":"weapon-frozen","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":3,"health":30,"can_attack":true,"attacks_remaining":1,"frozen":true},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":3,"durability":2,"current_durability":2}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(
            !visible_legal_actions(&frozen.state)
                .expect("frozen weapon state")
                .iter()
                .any(|action| action.kind == ActionKind::Attack)
        );

        let face_blocked = request(
            r#"{"request_id":"weapon-face-blocked","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":3,"health":30,"can_attack":true,"attacks_remaining":1},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":3,"durability":2,"current_durability":2,"tags":{"CANNOT_ATTACK_HEROES":1}}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"minion","card_type":"MINION","attack":0,"health":4}]}}}"#,
        );
        let face_blocked_actions = visible_legal_actions(&face_blocked.state)
            .expect("face-restricted weapon state")
            .into_iter()
            .map(|action| action.action_id())
            .collect::<BTreeSet<_>>();
        assert!(face_blocked_actions.contains("attack:fh:minion"));
        assert!(!face_blocked_actions.contains("attack:fh:oh"));

        let fully_blocked = request(
            r#"{"request_id":"weapon-attack-blocked","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":3,"health":30,"can_attack":true,"attacks_remaining":1},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":3,"durability":2,"current_durability":2,"tags":{"227":1}}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"minion","card_type":"MINION","attack":0,"health":4}]}}}"#,
        );
        assert!(
            !visible_legal_actions(&fully_blocked.state)
                .expect("attack-restricted weapon state")
                .iter()
                .any(|action| action.kind == ActionKind::Attack)
        );
    }

    #[test]
    fn visible_weapon_durability_breaks_and_stops_remaining_windfury_attacks() {
        let two_durability = request(
            r#"{"request_id":"weapon-durability","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":5,"health":30,"can_attack":true,"attacks_remaining":2},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":3,"durability":2,"current_durability":2,"windfury":true}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let face = visible_legal_actions(&two_durability.state)
            .expect("first weapon attack")
            .into_iter()
            .find(|action| action.action_id() == "attack:fh:oh")
            .expect("face attack");
        let (after_first, _) =
            apply_action(&two_durability.state, &face).expect("first weapon attack");
        assert_eq!(after_first.opponent.hero.current_health, 25);
        assert_eq!(
            after_first
                .friendly
                .weapon
                .as_ref()
                .map(|weapon| weapon.current_durability),
            Some(1)
        );
        assert_eq!(after_first.friendly.hero.attack, 5);
        assert_eq!(after_first.friendly.hero.attacks_remaining, 1);

        let second_face = visible_legal_actions(&after_first)
            .expect("second weapon attack")
            .into_iter()
            .find(|action| action.action_id() == "attack:fh:oh")
            .expect("second face attack");
        let (after_second, _) =
            apply_action(&after_first, &second_face).expect("second weapon attack");
        assert_eq!(after_second.opponent.hero.current_health, 20);
        assert!(after_second.friendly.weapon.is_none());
        assert_eq!(after_second.friendly.hero.attack, 2);
        assert_eq!(after_second.friendly.hero.attacks_remaining, 0);
        assert!(!after_second.friendly.hero.can_attack);
        let plan = plan_visible_response(
            &two_durability.state,
            3,
            128,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("visible weapon plan");
        let weapon_root = plan
            .lines
            .iter()
            .find(|line| line.first_action_id() == "attack:fh:oh")
            .expect("weapon attack root");
        assert!(
            weapon_root
                .approximate_entity_ids
                .iter()
                .any(|id| id == "fw")
        );

        let one_durability = request(
            r#"{"request_id":"weapon-one-durability","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":3,"health":30,"can_attack":true,"attacks_remaining":2},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":3,"durability":1,"current_durability":1,"windfury":true}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let attack = visible_legal_actions(&one_durability.state)
            .expect("one-durability attack")
            .into_iter()
            .find(|action| action.kind == ActionKind::Attack)
            .expect("weapon attack");
        let (broken, _) =
            apply_action(&one_durability.state, &attack).expect("breaking weapon attack");
        assert!(broken.friendly.weapon.is_none());
        assert!(
            !visible_legal_actions(&broken)
                .expect("post-break actions")
                .iter()
                .any(|action| action.kind == ActionKind::Attack)
        );
    }

    #[test]
    fn visible_weapon_lifesteal_and_poisonous_use_normal_damage_prevention() {
        let keyword_request = request(
            r#"{"request_id":"weapon-keywords","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":4,"health":30,"current_health":10,"can_attack":true,"attacks_remaining":1},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":4,"durability":2,"current_durability":2,"lifesteal":true,"poisonous":true}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"target","card_type":"MINION","attack":0,"health":8}]}}}"#,
        );
        let attack_target = |state: &GameState| {
            visible_legal_actions(state)
                .expect("weapon keyword actions")
                .into_iter()
                .find(|action| action.action_id() == "attack:fh:target")
                .expect("target attack")
        };
        let (ordinary, _) = apply_action(
            &keyword_request.state,
            &attack_target(&keyword_request.state),
        )
        .expect("poisonous lifesteal attack");
        assert!(ordinary.opponent.board.is_empty());
        assert_eq!(ordinary.friendly.hero.current_health, 14);

        let mut shielded_state = keyword_request.state.clone();
        shielded_state.opponent.board[0].divine_shield = true;
        let (shielded, _) = apply_action(&shielded_state, &attack_target(&shielded_state))
            .expect("shielded weapon attack");
        assert_eq!(shielded.opponent.board[0].current_health, 8);
        assert!(!shielded.opponent.board[0].divine_shield);
        assert_eq!(shielded.friendly.hero.current_health, 10);

        let mut immune_state = keyword_request.state.clone();
        immune_state.opponent.board[0].immune = true;
        let (immune, _) = apply_action(
            &immune_state,
            &visible_legal_actions(&immune_state)
                .expect("immune visible actions")
                .into_iter()
                .find(|action| action.action_id() == "attack:fh:target")
                .expect("immune target attack"),
        )
        .expect("immune weapon attack");
        assert_eq!(immune.opponent.board[0].current_health, 8);
        assert_eq!(immune.friendly.hero.current_health, 10);

        let armored = request(
            r#"{"request_id":"weapon-armor","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","attack":4,"health":30,"current_health":10,"can_attack":true,"attacks_remaining":1},"weapon":{"entity_id":"fw","card_type":"WEAPON","attack":4,"durability":2,"current_durability":2,"lifesteal":true}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"armor":3}}}"#,
        );
        let face = visible_legal_actions(&armored.state)
            .expect("armored face actions")
            .into_iter()
            .find(|action| action.action_id() == "attack:fh:oh")
            .expect("face attack");
        let (through_armor, _) =
            apply_action(&armored.state, &face).expect("lifesteal through armor");
        assert_eq!(through_armor.opponent.armor, 0);
        assert_eq!(through_armor.opponent.hero.current_health, 29);
        assert_eq!(through_armor.friendly.hero.current_health, 14);
    }

    #[test]
    fn visible_opponent_weapon_counterlethal_is_partial_and_marks_weapon_dependency() {
        let request = request(
            r#"{"request_id":"opponent-weapon-counterlethal","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":3}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","attack":4,"health":30},"weapon":{"entity_id":"ow","card_type":"WEAPON","attack":4,"durability":1,"current_durability":1},"deck_size":5}}}"#,
        );
        let plan = plan_visible_response(
            &request.state,
            3,
            128,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("visible opponent weapon response");
        let end_turn = plan
            .lines
            .iter()
            .find(|line| line.first_action_id() == "end_turn")
            .expect("end-turn root");
        assert_eq!(
            end_turn
                .opponent_reply
                .first()
                .map(Action::action_id)
                .as_deref(),
            Some("attack:oh:fh")
        );
        assert_eq!(end_turn.terminal_state.friendly.hero.current_health, 0);
        assert!(end_turn.approximate_entity_ids.iter().any(|id| id == "ow"));
    }

    #[test]
    fn every_visible_line_marks_initial_whiteboard_and_excluded_keyword_minions() {
        let request = request(
            r#"{"request_id":"visible-static-approx","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"hand":[{"entity_id":"reborn-hand","card_id":"REBORN_HAND","card_type":"MINION","cost":1,"attack":1,"health":1,"reborn":true}],"board":[{"entity_id":"friendly-unknown","card_id":"UNKNOWN","card_type":"MINION","attack":0,"health":2,"effect_coverage":"unsupported","unsupported_effects":["card_text_not_parsed"]},{"entity_id":"reborn-board","card_id":"REBORN_BOARD","card_type":"MINION","attack":1,"health":2,"can_attack":true,"reborn":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"deck_size":3,"board":[{"entity_id":"opponent-unknown","card_id":"UNKNOWN","card_type":"MINION","attack":0,"health":2,"effect_coverage":"unsupported","unsupported_effects":["deathrattle"]},{"entity_id":"dormant-board","card_id":"DORMANT","card_type":"MINION","attack":5,"health":5,"dormant":true},{"entity_id":"immune-board","card_id":"IMMUNE","card_type":"MINION","attack":0,"health":2,"immune":true}]}}}"#,
        );
        let modeled = visible_legal_actions(&request.state)
            .expect("visible actions")
            .into_iter()
            .map(|item| item.action_id())
            .collect::<BTreeSet<_>>();
        assert!(!modeled.iter().any(|id| id.contains("reborn-hand")));
        assert!(!modeled.iter().any(|id| id.contains("reborn-board")));
        assert!(!modeled.iter().any(|id| id.contains("immune-board")));

        let plan = plan_visible_response(
            &request.state,
            3,
            8,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("visible plan");
        let expected = BTreeSet::from([
            "dormant-board".to_owned(),
            "friendly-unknown".to_owned(),
            "immune-board".to_owned(),
            "opponent-unknown".to_owned(),
            "reborn-board".to_owned(),
        ]);
        assert!(!plan.lines.is_empty());
        for line in plan.lines {
            assert!(
                expected.is_subset(
                    &line
                        .approximate_entity_ids
                        .into_iter()
                        .collect::<BTreeSet<_>>()
                )
            );
        }
    }

    #[test]
    fn frozen_state_remains_fail_closed_for_exact_turnpair() {
        let request = request(
            r#"{"request_id":"frozen-exact","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"frozen","card_type":"MINION","attack":2,"health":2,"frozen":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(assert_turnpair_state(&request.state, true).is_err());
        assert!(crate::oracle::assert_exact_oracle_state(&request.state).is_err());
    }

    #[test]
    fn inconsistent_attack_snapshots_fail_closed_across_every_solver_gate() {
        let with_card = |fields: &str| {
            request(&format!(
                r#"{{"request_id":"bad-attack-state","state":{{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{{"player_id":"f","hero":{{"entity_id":"fh","card_type":"HERO","health":30}},"board":[{{"entity_id":"source","card_type":"MINION","health":2,{fields}}}]}},"opponent":{{"player_id":"o","hero":{{"entity_id":"oh","card_type":"HERO","health":30}}}}}}}}"#
            ))
        };
        for fields in [
            r#""attack":2,"can_attack":true,"attacks_remaining":2"#,
            r#""attack":2,"can_attack":true,"attacks_remaining":0"#,
            r#""attack":2,"can_attack":true,"attacks_remaining":1,"summoned_this_turn":true"#,
            r#""attack":0,"can_attack":true,"attacks_remaining":1"#,
        ] {
            let invalid = with_card(fields);
            assert!(crate::oracle::assert_exact_oracle_state(&invalid.state).is_err());
            assert!(assert_turnpair_state(&invalid.state, true).is_err());
            assert!(scoped_legal_actions(&invalid.state).is_err());
            assert!(visible_legal_actions(&invalid.state).is_err());
        }

        let unable_for_an_external_reason =
            with_card(r#""attack":2,"can_attack":false,"attacks_remaining":1"#);
        crate::oracle::assert_exact_oracle_state(&unable_for_an_external_reason.state)
            .expect("can_attack=false is a valid conservative snapshot");
        assert_turnpair_state(&unable_for_an_external_reason.state, true)
            .expect("turnpair accepts a conservatively disabled attacker");
        scoped_legal_actions(&unable_for_an_external_reason.state)
            .expect("scoped lethal accepts a conservatively disabled attacker");
        let visible = visible_legal_actions(&unable_for_an_external_reason.state)
            .expect("visible mode accepts a conservatively disabled attacker");
        assert!(
            !visible
                .iter()
                .any(|action| action.kind == ActionKind::Attack)
        );
    }

    #[test]
    fn visible_windfury_requires_an_explicit_remaining_attack_count() {
        let omitted = request(
            r#"{"request_id":"wind-unknown-count","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"wind","card_type":"MINION","attack":2,"health":2,"can_attack":true,"windfury":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(visible_legal_actions(&omitted.state).is_err());

        let explicit = request(
            r#"{"request_id":"wind-known-count","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"wind","card_type":"MINION","attack":2,"health":2,"can_attack":true,"attacks_remaining":1,"windfury":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(
            visible_legal_actions(&explicit.state)
                .expect("explicit count")
                .iter()
                .any(|action| action.action_id() == "attack:wind:oh")
        );
    }

    #[test]
    fn stealth_hero_power_is_rejected_by_exact_and_omitted_by_scoped_lethal() {
        let request = request(
            r#"{"request_id":"stealth-power","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,"hero_power":{"entity_id":"power","card_type":"HERO_POWER","cost":2,"stealth":true,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"enemy_hero"}]},"hero_power_available":true},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        assert!(assert_turnpair_state(&request.state, true).is_err());
        assert!(
            !scoped_legal_actions(&request.state)
                .expect("scoped action subset")
                .iter()
                .any(|action| action.source_entity_id.as_ref() == "power")
        );
    }

    #[test]
    fn visible_point_targets_are_conservative_and_excluded_entities_stay_annotated() {
        let request = request(
            r#"{"request_id":"point-target-filter","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,"hand":[{"entity_id":"damage","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_minion"}]},{"entity_id":"heal","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"heal","amount":1,"target":"friendly_minion"}]}],"board":[{"entity_id":"friendly-normal","card_type":"MINION","attack":0,"health":2},{"entity_id":"friendly-reborn","card_type":"MINION","attack":0,"health":2,"reborn":true},{"entity_id":"friendly-immune","card_type":"MINION","attack":0,"health":2,"immune":true},{"entity_id":"durability-source","card_type":"MINION","attack":2,"health":2,"can_attack":true,"attacks_remaining":1,"durability":1,"current_durability":1}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"enemy-normal","card_type":"MINION","attack":0,"health":2},{"entity_id":"enemy-reborn","card_type":"MINION","attack":0,"health":2,"reborn":true},{"entity_id":"enemy-dormant","card_type":"MINION","attack":0,"health":2,"dormant":true},{"entity_id":"enemy-immune","card_type":"MINION","attack":0,"health":2,"immune":true}]}}}"#,
        );
        let legal = legal_actions(&request.state)
            .expect("raw legal actions")
            .into_iter()
            .map(|action| action.action_id())
            .collect::<BTreeSet<_>>();
        assert!(legal.contains("play_card:heal:friendly-immune"));
        assert!(!legal.contains("play_card:damage:enemy-immune"));

        let modeled = visible_legal_actions(&request.state)
            .expect("visible actions")
            .into_iter()
            .map(|action| action.action_id())
            .collect::<BTreeSet<_>>();
        assert!(modeled.contains("play_card:damage:enemy-normal"));
        assert!(modeled.contains("play_card:heal:friendly-normal"));
        for excluded_target in [
            "friendly-reborn",
            "friendly-immune",
            "enemy-reborn",
            "enemy-dormant",
            "enemy-immune",
        ] {
            assert!(
                !modeled
                    .iter()
                    .any(|action_id| action_id.ends_with(excluded_target)),
                "unexpected modeled target for {excluded_target}: {modeled:?}"
            );
        }
        assert!(
            !modeled
                .iter()
                .any(|action_id| action_id.starts_with("attack:durability-source:"))
        );

        let plan = plan_visible_response(
            &request.state,
            3,
            128,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("conservative visible plan");
        let expected_annotations = BTreeSet::from([
            "durability-source".to_owned(),
            "enemy-dormant".to_owned(),
            "enemy-immune".to_owned(),
            "enemy-reborn".to_owned(),
            "friendly-immune".to_owned(),
            "friendly-reborn".to_owned(),
        ]);
        assert!(!plan.lines.is_empty());
        for line in plan.lines {
            assert!(
                expected_annotations.is_subset(
                    &line
                        .approximate_entity_ids
                        .into_iter()
                        .collect::<BTreeSet<_>>()
                )
            );
        }
    }

    #[test]
    fn visible_planner_filters_unknown_nonminions_and_labels_whiteboard_minion() {
        let request = request(
            r#"{"request_id":"visible-filter","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":5,"max_mana":5,"hand":[{"entity_id":"spell","card_id":"UNKNOWN_SPELL","card_type":"SPELL","cost":1,"effect_coverage":"unsupported"},{"entity_id":"weapon","card_id":"UNKNOWN_WEAPON","card_type":"WEAPON","cost":1,"effect_coverage":"unsupported"},{"entity_id":"minion","card_id":"UNKNOWN_MINION","card_type":"MINION","cost":1,"attack":2,"health":2,"effect_coverage":"unsupported","unsupported_effects":["card_text_not_parsed"],"card_text":"Battlecry: unknown"}],"hero_power":{"entity_id":"power","card_id":"UNKNOWN_POWER","card_type":"HERO_POWER","cost":2,"effect_coverage":"unsupported"},"hero_power_available":true,"board":[{"entity_id":"a","card_type":"MINION","attack":1,"health":1,"can_attack":true},{"entity_id":"b","card_type":"MINION","attack":1,"health":1,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"deck_size":7,"fatigue":2}}}"#,
        );
        let modeled = visible_legal_actions(&request.state)
            .expect("modeled actions")
            .into_iter()
            .map(|action| action.action_id())
            .collect::<BTreeSet<_>>();
        assert_eq!(
            modeled
                .iter()
                .filter(|id| id.starts_with("play_card:minion::position="))
                .cloned()
                .collect::<BTreeSet<_>>(),
            BTreeSet::from([
                "play_card:minion::position=1".to_owned(),
                "play_card:minion::position=2".to_owned(),
                "play_card:minion::position=3".to_owned(),
            ])
        );
        assert!(!modeled.iter().any(|id| id.contains("spell")));
        assert!(!modeled.iter().any(|id| id.contains("weapon")));
        assert!(!modeled.iter().any(|id| id.contains("power")));

        let plan = plan_visible_response(
            &request.state,
            10,
            0,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("bounded visible plan");
        assert!(plan.node_limit_reached);
        assert_eq!(plan.nodes_expanded, 0);
        assert!(
            plan.omitted_first_action_ids
                .iter()
                .any(|id| id.contains("spell"))
        );
        assert!(
            plan.omitted_first_action_ids
                .iter()
                .any(|id| id.contains("weapon"))
        );
        assert!(
            plan.omitted_first_action_ids
                .iter()
                .any(|id| id.contains("power"))
        );
        assert!(plan.lines.iter().all(|line| {
            line.actions.iter().all(|action| {
                !matches!(
                    action.source_entity_id.as_ref(),
                    "spell" | "weapon" | "power"
                )
            })
        }));
        let minion_line = plan
            .lines
            .iter()
            .find(|line| line.first_action_id() == "play_card:minion::position=1")
            .expect("whiteboard minion root");
        assert_eq!(minion_line.approximate_entity_ids, vec!["a", "b", "minion"]);
        assert_eq!(minion_line.terminal_state.opponent.deck_size, 7);
        assert_eq!(minion_line.terminal_state.opponent.fatigue, 2);
        assert_eq!(minion_line.terminal_state.opponent.hero.current_health, 30);
        assert_eq!(
            plan.lines
                .iter()
                .map(VisibleResponseLine::first_action_id)
                .collect::<BTreeSet<_>>()
                .len(),
            plan.lines.len()
        );
        assert!(plan.lines.len() >= 2);
    }

    #[test]
    fn visible_planner_propagates_cancellation() {
        let request = request(
            r#"{"request_id":"visible-cancel","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"deck_size":1}}}"#,
        );
        let error = plan_visible_response(
            &request.state,
            3,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &AtomicBool::new(true),
        )
        .expect_err("cancelled");
        assert!(matches!(error, SolverError::Cancelled));
    }

    #[test]
    fn fair_root_quota_reports_truncation_even_when_a_simple_root_leaves_nodes_unused() {
        let request = request(
            r#"{"request_id":"fair-root-limit","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"a","card_type":"MINION","attack":1,"health":2,"can_attack":true},{"entity_id":"b","card_type":"MINION","attack":1,"health":2,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let plan = plan_visible_response(
            &request.state,
            10,
            3,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("fair bounded plan");

        assert_eq!(plan.modeled_first_action_ids.len(), 3);
        assert_eq!(plan.lines.len(), 3);
        assert_eq!(plan.nodes_expanded, 2);
        assert!(plan.node_limit_reached);
    }

    #[test]
    fn cancellation_is_observed_before_search() {
        let request = request(
            r#"{"request_id":"cancel","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let cancel = AtomicBool::new(true);
        let error = prove_turnpair(
            &request.state,
            false,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &cancel,
        )
        .expect_err("cancelled");
        assert!(matches!(error, SolverError::Cancelled));
    }

    #[test]
    fn equal_value_lines_defer_a_repeatable_hero_power_until_other_actions_finish() {
        let power_first = vec![
            Action::new(ActionKind::HeroPower, "power", "oh", "POWER"),
            Action::new(ActionKind::PlayCard, "card", "", "CARD"),
            Action::end_turn(),
        ];
        let card_first = vec![
            Action::new(ActionKind::PlayCard, "card", "", "CARD"),
            Action::new(ActionKind::HeroPower, "power", "oh", "POWER"),
            Action::end_turn(),
        ];
        assert_eq!(early_hero_power_penalty(&power_first), 1);
        assert_eq!(early_hero_power_penalty(&card_first), 0);
        assert_eq!(
            early_hero_power_penalty(&[
                Action::new(ActionKind::HeroPower, "power", "oh", "POWER"),
                Action::end_turn(),
            ]),
            0,
            "the hero power remains a valid last resource sink"
        );
    }

    #[test]
    fn visible_portfolio_removes_redundant_temporary_mana_but_keeps_real_unlocks() {
        let redundant = request(
            r#"{"request_id":"redundant-coin","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"hand":[{"entity_id":"coin","card_id":"GAME_005","card_type":"SPELL","cost":0,"effect_coverage":"exact","effects":[{"kind":"gain_mana","amount":1,"target":"none"}]},{"entity_id":"one","card_type":"MINION","cost":1,"attack":1,"health":1}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let redundant_plan =
            plan_visible_response(&redundant.state, 4, 2_048, 6, &AtomicBool::new(false))
                .expect("redundant temporary-mana plan");
        assert!(!redundant_plan.lines.is_empty());
        assert!(redundant_plan.lines.iter().all(|line| {
            line.actions
                .iter()
                .all(|action| action.source_entity_id.as_ref() != "coin")
        }));

        let unlocking = request(
            r#"{"request_id":"unlocking-coin","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"hand":[{"entity_id":"coin","card_id":"GAME_005","card_type":"SPELL","cost":0,"effect_coverage":"exact","effects":[{"kind":"gain_mana","amount":1,"target":"none"}]},{"entity_id":"two","card_type":"MINION","cost":2,"attack":3,"health":2}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let unlocking_plan =
            plan_visible_response(&unlocking.state, 3, 2_048, 6, &AtomicBool::new(false))
                .expect("unlocking temporary-mana plan");
        assert!(unlocking_plan.lines.iter().any(|line| {
            let ids = line_ids(&line.actions);
            ids.iter().any(|id| id == "play_card:coin:")
                && ids.iter().any(|id| id == "play_card:two::position=1")
        }));
    }

    #[test]
    fn playable_opening_development_beats_passing_with_unspent_mana() {
        let request = request(
            r#"{"request_id":"opening-development","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":1,"hand":[{"entity_id":"one-drop","card_type":"MINION","cost":1,"attack":2,"health":2}]} ,"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let plan = plan_visible_response(&request.state, 3, 2_048, 6, &AtomicBool::new(false))
            .expect("opening development plan");
        assert_eq!(
            plan.lines.first().map(VisibleResponseLine::first_action_id),
            Some("play_card:one-drop::position=1".to_owned())
        );
    }

    #[test]
    fn temporary_mana_does_not_cash_in_a_one_cost_engine_without_a_payoff() {
        let request = request(
            r#"{"request_id":"reserve-engine","state":{"state_id":"s","turn":2,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,"hand":[{"entity_id":"coin","card_id":"GAME_005","card_type":"SPELL","cost":0,"effect_coverage":"exact","effects":[{"kind":"gain_mana","amount":1,"target":"none"}]},{"entity_id":"engine","card_type":"MINION","cost":3,"attack":2,"health":5,"effect_coverage":"exact","effects":[{"kind":"double_one_cost_cards","amount":2,"target":"none"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let plan = plan_visible_response(&request.state, 3, 4_096, 6, &AtomicBool::new(false))
            .expect("engine reserve plan");
        assert_eq!(
            plan.lines.first().map(VisibleResponseLine::first_action_id),
            Some("end_turn".to_owned())
        );
        let coin_line = plan
            .lines
            .iter()
            .find(|line| line.first_action_id() == "play_card:coin:")
            .expect("coin remains a legal alternative");
        assert!(
            coin_line
                .actions
                .iter()
                .any(|action| action.source_entity_id.as_ref() == "engine")
        );
        assert!(coin_line.tactical_value < plan.lines[0].tactical_value);
    }

    #[test]
    fn one_cost_payoff_makes_the_engine_first_action_best() {
        let request = request(
            r#"{"request_id":"engine-payoff","state":{"state_id":"s","turn":5,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":4,"max_mana":5,"hand":[{"entity_id":"engine","card_type":"MINION","cost":3,"attack":2,"health":5,"effect_coverage":"exact","effects":[{"kind":"double_one_cost_cards","amount":2,"target":"none"}]},{"entity_id":"shot","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"threat","card_type":"MINION","attack":5,"health":4}]}}}"#,
        );
        let plan = plan_visible_response(&request.state, 3, 8_192, 6, &AtomicBool::new(false))
            .expect("engine payoff plan");
        let best = plan.lines.first().expect("best engine line");
        assert_eq!(best.first_action_id(), "play_card:engine::position=1");
        assert_eq!(
            line_ids(&best.actions)[..2],
            [
                "play_card:engine::position=1".to_owned(),
                "play_card:shot:threat".to_owned(),
            ]
        );
        assert!(
            best.terminal_state
                .opponent
                .board
                .iter()
                .all(|card| card.entity_id.as_ref() != "threat")
        );
    }

    #[test]
    fn wound_prey_beats_arcane_shot_when_its_rush_token_is_real_value() {
        let request = request(
            r#"{"request_id":"wound-prey-priority","state":{"state_id":"s","turn":3,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":1,"max_mana":3,"hand":[{"entity_id":"wound","card_id":"CORE_BAR_801","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"any_character"},{"kind":"summon","target":"none","card_id":"BAR_035t","name":"Swift Hyena","attack":1,"health":1,"rush":true}]},{"entity_id":"arcane","card_id":"CORE_DS1_185","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":2,"target":"any_character"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"victim","card_type":"MINION","attack":3,"health":1}]}}}"#,
        );
        let plan = plan_visible_response(&request.state, 3, 4_096, 6, &AtomicBool::new(false))
            .expect("Wound Prey comparison");
        assert_eq!(
            plan.lines.first().map(VisibleResponseLine::first_action_id),
            Some("play_card:wound:victim".to_owned())
        );
        assert!(
            plan.lines[0]
                .terminal_state
                .friendly
                .board
                .iter()
                .any(|card| card.card_id.as_ref() == "BAR_035t")
        );
    }

    #[test]
    fn history_and_generated_resource_effects_are_never_labeled_exact() {
        let request = request(
            r#"{"request_id":"approximate-effects","state":{"state_id":"s","turn":6,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":6,"hand":[{"entity_id":"tolvir","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"replay_one_cost_cards","target":"none"}]},{"entity_id":"tripwire","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"shuffle_repeat_spell","target":"none","count":2,"card_id":"ECHO","name":"Echo"}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let tolvir = Action::new(ActionKind::PlayCard, "tolvir", "", "");
        let tripwire = Action::new(ActionKind::PlayCard, "tripwire", "", "");
        assert_eq!(
            action_approximate_entity_ids(&request.state, &tolvir),
            BTreeSet::from(["tolvir".to_owned()])
        );
        assert_eq!(
            action_approximate_entity_ids(&request.state, &tripwire),
            BTreeSet::from(["tripwire".to_owned()])
        );
    }

    #[test]
    fn visible_search_applies_actions_to_the_normalized_replay_state() {
        let replay_request = request(
            r#"{
              "request_id":"tolvir-replayed-weapon-attack",
              "state":{"state_id":"s","turn":7,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f",
                  "hero":{"entity_id":"fh","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},
                  "mana":5,"max_mana":7,
                  "hand":[
                    {"entity_id":"tolvir","card_id":"CATA_560","card_type":"SPELL","cost":5,"playable":true,"effect_coverage":"exact","effects":[{"kind":"replay_one_cost_cards","target":"none"}]},
                    {"entity_id":"chance","card_id":"RANDOM_SOURCE","card_type":"SPELL","cost":10,"playable":false,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_character","random":true}]}
                  ],
                  "graveyard":[{"entity_id":"old-shovel","card_id":"JAIL_380","card_type":"WEAPON","cost":1,"attack":1,"durability":2,"current_durability":2,"effect_coverage":"exact","effects":[{"kind":"draw_non_starting_spell_on_weapon_break","target":"none"}]}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
            }"#,
        );
        let root = visible_legal_actions(&replay_request.state)
            .expect("root actions")
            .into_iter()
            .find(|action| action.action_id() == "play_card:tolvir:")
            .expect("Tol'vir root");
        let after_replay = apply_visible_search_action(
            &VisibleSearchNode {
                actions: Vec::new(),
                state: replay_request.state,
                approximate_entity_ids: BTreeSet::new(),
            },
            &root,
        )
        .expect("replay the one-cost weapon");
        assert_eq!(
            after_replay
                .state
                .friendly
                .weapon
                .as_ref()
                .map(|weapon| weapon.card_id.as_ref()),
            Some("JAIL_380")
        );
        let attack = visible_legal_actions(&after_replay.state)
            .expect("normalized follow-up actions")
            .into_iter()
            .find(|action| action.action_id() == "attack:fh:oh")
            .expect("newly equipped hero attack");
        let after_attack = apply_visible_search_action(&after_replay, &attack)
            .expect("apply the action generated from the normalized state");
        assert_eq!(after_attack.state.opponent.hero.current_health, 29);

        let cancel = AtomicBool::new(false);
        let plan = plan_visible_response(
            &request(
                r#"{
                  "request_id":"tolvir-replayed-weapon-chance-path",
                  "state":{"state_id":"s","turn":7,"active_player_id":"f","perspective_player_id":"f",
                    "friendly":{"player_id":"f",
                      "hero":{"entity_id":"fh","card_type":"HERO","health":30,"tags":{"NUM_ATTACKS_THIS_TURN":0}},
                      "mana":5,"max_mana":7,
                      "hand":[
                        {"entity_id":"tolvir","card_id":"CATA_560","card_type":"SPELL","cost":5,"playable":true,"effect_coverage":"exact","effects":[{"kind":"replay_one_cost_cards","target":"none"}]},
                        {"entity_id":"chance","card_id":"RANDOM_SOURCE","card_type":"SPELL","cost":10,"playable":false,"effect_coverage":"exact","effects":[{"kind":"damage","amount":1,"target":"enemy_character","random":true}]}
                      ],
                      "graveyard":[{"entity_id":"old-shovel","card_id":"JAIL_380","card_type":"WEAPON","cost":1,"attack":1,"durability":2,"current_durability":2,"effect_coverage":"exact","effects":[{"kind":"draw_non_starting_spell_on_weapon_break","target":"none"}]}]},
                    "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
                }"#,
            )
            .state,
            3,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &cancel,
        )
        .expect("chance-aware replay line remains legal");
        assert!(!plan.lines.is_empty());
    }

    #[test]
    fn visible_top_k_keeps_friendly_point_damage_legal_but_never_advises_it() {
        let request = request(
            r#"{"request_id":"friendly-rock-filter","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":2,"max_mana":2,"hand":[{"entity_id":"rock-a","card_id":"WW_001t","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":3,"target":"any_character"}]},{"entity_id":"rock-b","card_id":"WW_001t","card_type":"SPELL","cost":1,"effect_coverage":"exact","effects":[{"kind":"damage","amount":3,"target":"any_character"}]}],"board":[{"entity_id":"ally","card_type":"MINION","attack":2,"health":5}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let legal_ids = legal_actions(&request.state)
            .expect("raw legal Rock targets")
            .into_iter()
            .map(|action| action.action_id())
            .collect::<BTreeSet<_>>();
        assert!(legal_ids.contains("play_card:rock-a:fh"));
        assert!(legal_ids.contains("play_card:rock-a:ally"));

        let plan = plan_visible_response(&request.state, 3, 4_096, 6, &AtomicBool::new(false))
            .expect("filtered Rock plan");
        assert!(!plan.lines.is_empty());
        assert!(plan.lines.iter().all(|line| {
            line.actions.iter().all(|action| {
                action.target_entity_id.as_ref() != "fh"
                    && action.target_entity_id.as_ref() != "ally"
            })
        }));
    }

    #[test]
    fn full_health_board_control_beats_greedy_face_damage() {
        let request = request(
            r#"{"request_id":"trade-before-face","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"ours","card_type":"MINION","attack":3,"health":3,"can_attack":true,"attacks_remaining":1}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"threat","card_type":"MINION","attack":4,"health":2}]}}}"#,
        );
        let proof = prove_turnpair(
            &request.state,
            false,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &AtomicBool::new(false),
        )
        .expect("complete board-control proof");
        let portfolio = ranked_lines(&proof, 3);
        assert_eq!(portfolio[0].first_action_id(), "attack:ours:threat");
        assert!(
            proof
                .optimal_first_action_ids
                .iter()
                .all(|action_id| action_id != "attack:ours:oh")
        );
    }

    #[test]
    fn depth_truncation_fails_closed_before_claiming_complete_roots() {
        let request = request(
            r#"{"request_id":"bounded-root","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"a","card_type":"MINION","attack":1,"health":1,"can_attack":true},{"entity_id":"b","card_type":"MINION","attack":1,"health":1,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let cancel = AtomicBool::new(false);
        let error = prove_turnpair(&request.state, false, MAX_ENUMERATED_NODES, 2, &cancel)
            .expect_err("two attacks plus end-turn exceed max_depth=2");
        assert!(matches!(error, SolverError::DepthLimit(2)));
    }

    #[test]
    fn portfolio_classification_has_stable_five_state_contract() {
        assert_eq!(alternative_kind(true, true, Some(0), true), "co_optimal");
        assert_eq!(
            alternative_kind(true, true, Some(100), true),
            "near_optimal"
        );
        assert_eq!(alternative_kind(false, true, Some(0), true), "best_found");
        assert_eq!(alternative_kind(true, false, Some(0), true), "best_found");
        assert_eq!(alternative_kind(true, true, Some(101), true), "backup");
        assert_eq!(alternative_kind(true, true, None, false), "fallback");
    }

    #[test]
    fn cooptimal_portfolio_is_not_padded_with_inferior_root() {
        let request = request(
            r#"{"request_id":"cooptimal","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"a","card_type":"MINION","attack":2,"health":1,"can_attack":true},{"entity_id":"b","card_type":"MINION","attack":2,"health":1,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":2,"current_health":2}}}}"#,
        );
        let cancel = AtomicBool::new(false);
        let proof = prove_turnpair(
            &request.state,
            false,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &cancel,
        )
        .expect("complete proof");
        let portfolio = ranked_lines(&proof, 3);
        assert_eq!(portfolio.len(), 2);
        assert!(
            portfolio
                .iter()
                .all(|line| verified_portfolio_regret(&proof, line) == 0)
        );
        assert_eq!(
            portfolio
                .iter()
                .map(TurnPairLine::first_action_id)
                .collect::<BTreeSet<_>>(),
            BTreeSet::from(["attack:a:oh".to_owned(), "attack:b:oh".to_owned()])
        );
    }

    #[test]
    fn all_counterlethal_roots_still_return_the_least_bad_choice() {
        let request = request(
            r#"{"request_id":"all-unsafe","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":1},"board":[{"entity_id":"ours","card_type":"MINION","attack":1,"health":1,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},"board":[{"entity_id":"threat","card_type":"MINION","attack":1,"health":2}]}}}"#,
        );
        let cancel = AtomicBool::new(false);
        let proof = prove_turnpair(
            &request.state,
            false,
            MAX_ENUMERATED_NODES,
            MAX_LINE_DEPTH,
            &cancel,
        )
        .expect("complete proof");
        let portfolio = ranked_lines(&proof, 3);
        assert!(!portfolio.is_empty());
        assert!(portfolio.iter().all(|line| !line.safe_after_response));
        assert_eq!(portfolio[0].minimax_value, proof.optimal_value);
    }

    #[test]
    fn generic_text_effects_enter_visible_search_but_never_exact_proof() {
        let request = request(
            r#"{"request_id":"generic-draw-gate","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"mana":3,"max_mana":3,"deck_size":5,"hand":[{"entity_id":"draw","card_id":"CORE_CS2_023","card_type":"SPELL","cost":3,"effect_coverage":"generic","effects":[{"kind":"draw","target":"none","count":2}]}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}}"#,
        );
        let error = assert_turnpair_state(&request.state, true)
            .expect_err("generic text effects cannot acquire exact scope");
        assert!(error.to_string().contains("generic text-compiled effects"));
        assert!(
            visible_legal_actions(&request.state)
                .expect("visible generic actions")
                .iter()
                .any(|action| action.action_id() == "play_card:draw:")
        );
    }
}
