use std::collections::{BTreeSet, HashMap};

use serde::{Deserialize, Serialize};

use crate::error::SolverError;
use crate::model::{Action, ActionKind, Card, CardType, GameState, PlayerState};

pub const HDT_ROOT_CANDIDATE_CONTRACT: &str = "hdt_complete_main_action_options_v1";
const MAX_CANDIDATES: usize = 512;

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HdtRootActionKind {
    PlayCard,
    Attack,
    HeroPower,
    LocationActivate,
    EndTurn,
}

impl HdtRootActionKind {
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

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HdtTargetEvidence {
    HdtErrorNone,
    HdtNoLegalTarget,
    NotApplicable,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HdtPositionEvidence {
    CoreBoardSlotsV1,
    NotApplicable,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct HdtRootAction {
    pub kind: HdtRootActionKind,
    #[serde(default)]
    pub source_entity_id: String,
    #[serde(default)]
    pub target_entity_id: String,
    #[serde(default)]
    pub card_id: String,
    #[serde(default)]
    pub board_position: u8,
}

impl HdtRootAction {
    #[must_use]
    pub fn action_id(&self) -> String {
        if self.kind == HdtRootActionKind::EndTurn {
            return "end_turn".to_owned();
        }
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

    #[must_use]
    pub fn solver_action(&self) -> Option<Action> {
        let kind = match self.kind {
            HdtRootActionKind::PlayCard => ActionKind::PlayCard,
            HdtRootActionKind::Attack => ActionKind::Attack,
            HdtRootActionKind::HeroPower => ActionKind::HeroPower,
            HdtRootActionKind::LocationActivate => ActionKind::LocationActivate,
            HdtRootActionKind::EndTurn => ActionKind::EndTurn,
        };
        Some(
            Action::new(
                kind,
                self.source_entity_id.as_str(),
                self.target_entity_id.as_str(),
                self.card_id.as_str(),
            )
            .with_board_position(self.board_position),
        )
    }
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct HdtRootCandidate {
    pub option_id: u32,
    pub action: HdtRootAction,
    pub target_evidence: HdtTargetEvidence,
    pub position_evidence: HdtPositionEvidence,
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct HdtRootCandidateSet {
    pub contract: String,
    pub state_id: String,
    pub frame_id: u32,
    pub collector_epoch: u64,
    pub frame_watermark: u64,
    pub candidate_set_complete: bool,
    pub candidates: Vec<HdtRootCandidate>,
}

#[derive(Clone, Copy)]
struct EntityBinding<'a> {
    card: &'a Card,
    role: &'static str,
    zone: &'static str,
}

impl HdtRootCandidateSet {
    pub fn validate(&self, state: &GameState) -> Result<(), SolverError> {
        if self.contract != HDT_ROOT_CANDIDATE_CONTRACT {
            return Err(SolverError::schema(
                "request.hdt_root_candidates.contract",
                format!("expected {HDT_ROOT_CANDIDATE_CONTRACT:?}"),
            ));
        }
        if self.state_id != state.state_id.as_ref() {
            return Err(SolverError::schema(
                "request.hdt_root_candidates.state_id",
                "must match request.state.state_id",
            ));
        }
        if self.frame_id == 0 || self.collector_epoch == 0 || self.frame_watermark == 0 {
            return Err(SolverError::schema(
                "request.hdt_root_candidates",
                "frame and collector identities must be positive",
            ));
        }
        if !self.candidate_set_complete {
            return Err(SolverError::schema(
                "request.hdt_root_candidates.candidate_set_complete",
                "must be true",
            ));
        }
        if self.candidates.is_empty() || self.candidates.len() > MAX_CANDIDATES {
            return Err(SolverError::schema(
                "request.hdt_root_candidates.candidates",
                format!("must contain between 1 and {MAX_CANDIDATES} actions"),
            ));
        }
        if state.active_player_id != state.friendly.player_id
            || state.perspective_player_id != state.friendly.player_id
        {
            return Err(SolverError::schema(
                "request.hdt_root_candidates",
                "requires the friendly active perspective",
            ));
        }

        let entities = public_entities(state)?;
        let mut action_ids = BTreeSet::new();
        let mut end_turn_count = 0usize;
        for (index, candidate) in self.candidates.iter().enumerate() {
            let path = format!("request.hdt_root_candidates.candidates[{index}]");
            validate_candidate(candidate, state, &entities, &path)?;
            if !action_ids.insert(candidate.action.action_id()) {
                return Err(SolverError::schema(
                    "request.hdt_root_candidates.candidates",
                    "candidate actions must be unique",
                ));
            }
            if candidate.action.kind == HdtRootActionKind::EndTurn {
                end_turn_count += 1;
            }
        }
        if end_turn_count != 1 {
            return Err(SolverError::schema(
                "request.hdt_root_candidates.candidates",
                "must contain exactly one end-turn action",
            ));
        }
        Ok(())
    }

    #[must_use]
    pub fn action_ids(&self) -> BTreeSet<String> {
        self.candidates
            .iter()
            .map(|candidate| candidate.action.action_id())
            .collect()
    }

    #[must_use]
    pub fn solver_actions(&self) -> Vec<Action> {
        self.candidates
            .iter()
            .filter_map(|candidate| candidate.action.solver_action())
            .collect()
    }
}

fn public_entities<'a>(
    state: &'a GameState,
) -> Result<HashMap<&'a str, EntityBinding<'a>>, SolverError> {
    let mut result = HashMap::new();
    add_player(&mut result, &state.friendly, "friendly")?;
    add_player(&mut result, &state.opponent, "opponent")?;
    Ok(result)
}

fn add_player<'a>(
    result: &mut HashMap<&'a str, EntityBinding<'a>>,
    player: &'a PlayerState,
    role: &'static str,
) -> Result<(), SolverError> {
    add_entity(result, &player.hero, role, "hero")?;
    if let Some(power) = &player.hero_power {
        add_entity(result, power, role, "hero_power")?;
    }
    if let Some(weapon) = &player.weapon {
        add_entity(result, weapon, role, "weapon")?;
    }
    for card in &player.hand {
        add_entity(result, card, role, "hand")?;
    }
    for card in &player.board {
        add_entity(result, card, role, "board")?;
    }
    Ok(())
}

fn add_entity<'a>(
    result: &mut HashMap<&'a str, EntityBinding<'a>>,
    card: &'a Card,
    role: &'static str,
    zone: &'static str,
) -> Result<(), SolverError> {
    if result
        .insert(card.entity_id.as_ref(), EntityBinding { card, role, zone })
        .is_some()
    {
        return Err(SolverError::schema(
            "request.state",
            "public entity IDs must be unique before HDT root binding",
        ));
    }
    Ok(())
}

fn validate_candidate(
    candidate: &HdtRootCandidate,
    state: &GameState,
    entities: &HashMap<&str, EntityBinding<'_>>,
    path: &str,
) -> Result<(), SolverError> {
    let action = &candidate.action;
    if action.board_position > 7 {
        return Err(SolverError::schema(
            format!("{path}.action.board_position"),
            "must be between 0 and 7",
        ));
    }
    if action.kind == HdtRootActionKind::EndTurn {
        if candidate.option_id != 0
            || !action.source_entity_id.is_empty()
            || !action.target_entity_id.is_empty()
            || !action.card_id.is_empty()
            || action.board_position != 0
            || candidate.target_evidence != HdtTargetEvidence::NotApplicable
            || candidate.position_evidence != HdtPositionEvidence::NotApplicable
        {
            return Err(SolverError::schema(
                path,
                "end-turn evidence is inconsistent",
            ));
        }
        return Ok(());
    }
    if candidate.option_id == 0 {
        return Err(SolverError::schema(
            format!("{path}.option_id"),
            "power actions require a positive option ID",
        ));
    }
    let source = entities
        .get(action.source_entity_id.as_str())
        .ok_or_else(|| {
            SolverError::schema(
                format!("{path}.action.source_entity_id"),
                "must resolve in the public state",
            )
        })?;
    if source.role != "friendly" || action.card_id != source.card.card_id.as_ref() {
        return Err(SolverError::schema(
            format!("{path}.action"),
            "source ownership or card identity does not match the public state",
        ));
    }
    let target = if action.target_entity_id.is_empty() {
        None
    } else {
        Some(
            entities
                .get(action.target_entity_id.as_str())
                .ok_or_else(|| {
                    SolverError::schema(
                        format!("{path}.action.target_entity_id"),
                        "must resolve in the public state",
                    )
                })?,
        )
    };
    if target.is_some_and(|binding| binding.role == "opponent" && binding.zone == "hand") {
        return Err(SolverError::schema(
            format!("{path}.action.target_entity_id"),
            "must not bind a hidden opponent hand entity",
        ));
    }
    if action.target_entity_id.is_empty()
        != (candidate.target_evidence == HdtTargetEvidence::HdtNoLegalTarget)
    {
        return Err(SolverError::schema(
            format!("{path}.target_evidence"),
            "must match target presence",
        ));
    }
    if !action.target_entity_id.is_empty()
        && candidate.target_evidence != HdtTargetEvidence::HdtErrorNone
    {
        return Err(SolverError::schema(
            format!("{path}.target_evidence"),
            "targeted actions require HDT error=NONE evidence",
        ));
    }
    let expected_position = if action.board_position > 0 {
        HdtPositionEvidence::CoreBoardSlotsV1
    } else {
        HdtPositionEvidence::NotApplicable
    };
    if candidate.position_evidence != expected_position {
        return Err(SolverError::schema(
            format!("{path}.position_evidence"),
            "must match board placement",
        ));
    }

    match action.kind {
        HdtRootActionKind::PlayCard => {
            if source.zone != "hand"
                || !matches!(
                    source.card.card_type,
                    CardType::Hero
                        | CardType::Minion
                        | CardType::Spell
                        | CardType::Weapon
                        | CardType::Location
                )
            {
                return Err(SolverError::schema(
                    path,
                    "play source is not a friendly hand card",
                ));
            }
            let placement = matches!(source.card.card_type, CardType::Minion | CardType::Location);
            if placement {
                let maximum = state.friendly.board.len().saturating_add(1).min(7);
                if maximum == 0 || !(1..=maximum).contains(&usize::from(action.board_position)) {
                    return Err(SolverError::schema(
                        path,
                        "board position is outside public slots",
                    ));
                }
            } else if action.board_position != 0 {
                return Err(SolverError::schema(
                    path,
                    "board position is not applicable",
                ));
            }
        }
        HdtRootActionKind::Attack => {
            if !matches!(source.zone, "hero" | "board")
                || !matches!(source.card.card_type, CardType::Hero | CardType::Minion)
                || !target.is_some_and(|binding| {
                    binding.role == "opponent"
                        && matches!(binding.zone, "hero" | "board")
                        && matches!(binding.card.card_type, CardType::Hero | CardType::Minion)
                })
                || action.board_position != 0
            {
                return Err(SolverError::schema(path, "attack binding is invalid"));
            }
        }
        HdtRootActionKind::HeroPower => {
            if source.zone != "hero_power"
                || source.card.card_type != CardType::HeroPower
                || action.board_position != 0
            {
                return Err(SolverError::schema(path, "hero-power binding is invalid"));
            }
        }
        HdtRootActionKind::LocationActivate => {
            if source.zone != "board"
                || source.card.card_type != CardType::Location
                || action.board_position != 0
            {
                return Err(SolverError::schema(path, "location binding is invalid"));
            }
        }
        HdtRootActionKind::EndTurn => unreachable!("handled above"),
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use crate::model::{ActionKind, SolveRequest};

    #[test]
    fn complete_portfolio_binds_public_actions_and_keeps_location_unmodeled() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "hdt-roots",
            "state": {
                "state_id": "s1",
                "turn": 3,
                "active_player_id": "friendly",
                "perspective_player_id": "friendly",
                "friendly": {
                    "player_id": "friendly",
                    "hero": {"entity_id": "fh", "card_id": "HERO", "card_type": "HERO", "health": 30},
                    "hand": [{"entity_id": "m", "card_id": "MINION", "card_type": "MINION", "cost": 1}],
                    "board": [{"entity_id": "loc", "card_id": "LOCATION", "card_type": "LOCATION", "durability": 2}],
                    "mana": 2,
                    "max_mana": 2
                },
                "opponent": {
                    "player_id": "opponent",
                    "hero": {"entity_id": "oh", "card_id": "OPP_HERO", "card_type": "HERO", "health": 30}
                }
            },
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "s1",
                "frame_id": 7,
                "collector_epoch": 2,
                "frame_watermark": 9,
                "candidate_set_complete": true,
                "candidates": [
                    {"option_id": 0, "action": {"kind": "end_turn"}, "target_evidence": "not_applicable", "position_evidence": "not_applicable"},
                    {"option_id": 1, "action": {"kind": "play_card", "source_entity_id": "m", "card_id": "MINION", "board_position": 1}, "target_evidence": "hdt_no_legal_target", "position_evidence": "core_board_slots_v1"},
                    {"option_id": 2, "action": {"kind": "location_activate", "source_entity_id": "loc", "card_id": "LOCATION"}, "target_evidence": "hdt_no_legal_target", "position_evidence": "not_applicable"}
                ]
            }
        }))
        .expect("request JSON");
        request.validate().expect("bound HDT roots");
        let roots = request.hdt_root_candidates.as_ref().expect("portfolio");
        assert_eq!(roots.action_ids().len(), 3);
        assert_eq!(roots.solver_actions().len(), 3);
        assert!(roots.solver_actions().iter().any(|action| {
            action.kind == ActionKind::LocationActivate
                && action.action_id() == "location_activate:loc:"
        }));
    }
}
