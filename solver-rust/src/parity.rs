use std::io::{BufRead, Write};
use std::sync::atomic::AtomicBool;
use std::time::Instant;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::error::SolverError;
use crate::hdt::solve_request_from_value;
use crate::model::{Action, GameState, SolveRequest};
use crate::oracle::{
    DEFAULT_MAXIMUM_STATES, ORACLE_SCOPE, OracleProof, assert_exact_oracle_state, choose_turn_plan,
    legal_actions, prove_lethal,
};
use crate::turnpair::{
    MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, ROOT_ACTION_PORTFOLIO_MODEL, RootActionCoverage,
    TURNPAIR_SCOPE, TurnPairLine, alternative_kind, assert_turnpair_state, choose_parity_line,
    prove_scoped_lethal, prove_turnpair, ranked_lines, scoped_legal_actions,
    scoped_root_action_coverage, verified_portfolio_regret,
};

pub const PARITY_REQUEST_SCHEMA: &str = "metacompanion-rust-parity-request-v1";
pub const PARITY_RESULT_SCHEMA: &str = "metacompanion-rust-parity-result-v1";
pub const COMBAT_PROFILE: &str = "combat-v1";
pub const FULL_PROFILE: &str = "full";
pub const HDT_RULE_SCOPE: &str = "oracle-hdt-cardrules-v1";

#[derive(Clone, Debug, Deserialize)]
pub struct ParityRequestEnvelope {
    pub schema: String,
    pub case_id: String,
    pub suite_id: String,
    pub request: Value,
    #[serde(skip)]
    canonical_request: Option<SolveRequest>,
    #[serde(skip)]
    raw_hdt: bool,
}

impl ParityRequestEnvelope {
    pub fn validate(&mut self, profile: &str) -> Result<(), SolverError> {
        if !matches!(profile, COMBAT_PROFILE | FULL_PROFILE) {
            return Err(SolverError::Unsupported(format!(
                "unknown parity profile {profile:?}; expected {COMBAT_PROFILE:?} or {FULL_PROFILE:?}"
            )));
        }
        if self.schema != PARITY_REQUEST_SCHEMA {
            return Err(SolverError::schema(
                "envelope.schema",
                format!("expected {PARITY_REQUEST_SCHEMA:?}"),
            ));
        }
        if self.case_id.trim().is_empty() {
            return Err(SolverError::schema(
                "envelope.case_id",
                "must be a non-empty string",
            ));
        }
        let raw_hdt = self
            .request
            .get("state")
            .and_then(Value::as_object)
            .is_some_and(|state| {
                state.contains_key("player")
                    && state.contains_key("opponent")
                    && !state.contains_key("friendly")
            });
        let mut request = solve_request_from_value(self.request.clone())?;
        if self.suite_id == HDT_RULE_SCOPE && raw_hdt {
            crate::rules::apply_embedded_rules(&mut request.state)?;
        }
        match (profile, self.suite_id.as_str()) {
            (COMBAT_PROFILE, ORACLE_SCOPE) => assert_exact_oracle_state(&request.state)?,
            (FULL_PROFILE, TURNPAIR_SCOPE) => assert_turnpair_state(&request.state, false)?,
            (FULL_PROFILE, HDT_RULE_SCOPE) if !raw_hdt => {
                assert_turnpair_state(&request.state, true)?;
            }
            (FULL_PROFILE, HDT_RULE_SCOPE) => {}
            (COMBAT_PROFILE, _) => Err(SolverError::Unsupported(format!(
                "combat-v1 requires suite_id={ORACLE_SCOPE:?}"
            )))?,
            (FULL_PROFILE, _) => Err(SolverError::Unsupported(format!(
                "full requires suite_id={TURNPAIR_SCOPE:?} or {HDT_RULE_SCOPE:?}"
            )))?,
            _ => unreachable!("profile checked above"),
        }
        self.raw_hdt = raw_hdt;
        self.canonical_request = Some(request);
        Ok(())
    }

    fn canonical_request(&self) -> Result<&SolveRequest, SolverError> {
        self.canonical_request
            .as_ref()
            .ok_or_else(|| SolverError::schema("envelope.request", "request was not validated"))
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct ActionWire {
    pub action_id: String,
    pub kind: &'static str,
    pub source_entity_id: String,
    pub target_entity_id: String,
    pub card_id: String,
    pub text: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub board_position: Option<u8>,
}

impl From<&Action> for ActionWire {
    fn from(action: &Action) -> Self {
        Self {
            action_id: action.action_id(),
            kind: action.kind.as_str(),
            source_entity_id: action.source_entity_id.to_string(),
            target_entity_id: action.target_entity_id.to_string(),
            card_id: action.card_id.to_string(),
            text: action.text.to_string(),
            board_position: (action.board_position > 0).then_some(action.board_position),
        }
    }
}

#[derive(Clone, Copy, Debug, Serialize)]
pub struct HeroPair {
    pub friendly: u16,
    pub opponent: u16,
}

#[derive(Clone, Debug, Serialize)]
pub struct TerminalSnapshot {
    pub state: GameState,
    pub hero_health: HeroPair,
    pub hero_armor: HeroPair,
}

#[derive(Clone, Debug, Serialize)]
pub struct PortfolioAlternativeWire {
    pub first_action_id: String,
    pub verified_portfolio_regret: Option<i64>,
    pub alternative_kind: &'static str,
}

#[derive(Clone, Debug, Serialize)]
pub struct RootActionPortfolioWire {
    pub model: &'static str,
    pub legal_first_action_count: usize,
    pub legal_first_action_ids: Vec<String>,
    pub generated_first_action_count: usize,
    pub generated_first_action_ids: Vec<String>,
    pub response_verified_first_action_count: usize,
    pub response_verified_first_action_ids: Vec<String>,
    pub missing_first_action_ids: Vec<String>,
    pub root_action_coverage_complete: bool,
    pub portfolio_optimality_proven: bool,
    pub alternatives: Vec<PortfolioAlternativeWire>,
}

impl RootActionPortfolioWire {
    fn from_exact(proof: &crate::turnpair::TurnPairProof, top_k: usize) -> RootActionPortfolioWire {
        let alternatives = ranked_lines(proof, top_k)
            .iter()
            .map(|line| {
                let regret = verified_portfolio_regret(proof, line);
                PortfolioAlternativeWire {
                    first_action_id: line.first_action_id(),
                    verified_portfolio_regret: Some(regret),
                    alternative_kind: alternative_kind(
                        proof.root_action_coverage.root_action_coverage_complete,
                        proof.portfolio_optimality_proven,
                        Some(regret),
                        true,
                    ),
                }
            })
            .collect();
        Self::from_coverage(
            &proof.root_action_coverage,
            proof.portfolio_optimality_proven,
            alternatives,
        )
    }

    fn from_scoped(coverage: &RootActionCoverage, line: &TurnPairLine) -> Self {
        Self::from_coverage(
            coverage,
            false,
            vec![PortfolioAlternativeWire {
                first_action_id: line.first_action_id(),
                verified_portfolio_regret: None,
                alternative_kind: alternative_kind(
                    coverage.root_action_coverage_complete,
                    false,
                    None,
                    true,
                ),
            }],
        )
    }

    fn from_coverage(
        coverage: &RootActionCoverage,
        portfolio_optimality_proven: bool,
        alternatives: Vec<PortfolioAlternativeWire>,
    ) -> Self {
        Self {
            model: ROOT_ACTION_PORTFOLIO_MODEL,
            legal_first_action_count: coverage.legal_first_action_count(),
            legal_first_action_ids: coverage.legal_first_action_ids.clone(),
            generated_first_action_count: coverage.generated_first_action_count(),
            generated_first_action_ids: coverage.generated_first_action_ids.clone(),
            response_verified_first_action_count: coverage.response_verified_first_action_count(),
            response_verified_first_action_ids: coverage.response_verified_first_action_ids.clone(),
            missing_first_action_ids: coverage.missing_first_action_ids.clone(),
            root_action_coverage_complete: coverage.root_action_coverage_complete,
            portfolio_optimality_proven,
            alternatives,
        }
    }
}

impl From<GameState> for TerminalSnapshot {
    fn from(state: GameState) -> Self {
        let hero_health = HeroPair {
            friendly: state.friendly.hero.current_health,
            opponent: state.opponent.hero.current_health,
        };
        let hero_armor = HeroPair {
            friendly: state.friendly.armor,
            opponent: state.opponent.armor,
        };
        Self {
            state,
            hero_health,
            hero_armor,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct ParityCaseResult {
    pub schema: &'static str,
    pub case_id: String,
    pub suite_id: String,
    pub profile: &'static str,
    pub scope: &'static str,
    pub status: &'static str,
    pub legal_action_ids: Vec<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub proof: Option<OracleProof>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub portfolio: Option<RootActionPortfolioWire>,
    pub actions: Vec<ActionWire>,
    pub action_ids: Vec<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub opponent_actions: Vec<ActionWire>,
    pub opponent_action_ids: Vec<String>,
    pub top1_action_id: String,
    pub terminal: TerminalSnapshot,
    pub minimax_utility: i64,
    pub wall_time_ms: f64,
}

#[derive(Clone, Debug, Serialize)]
pub struct ParityErrorBody {
    pub code: &'static str,
    pub message: String,
    pub path: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct ParityErrorResult {
    pub schema: &'static str,
    pub case_id: String,
    pub suite_id: String,
    pub profile: &'static str,
    pub status: &'static str,
    pub error: ParityErrorBody,
}

#[derive(Clone, Debug, Serialize)]
#[serde(untagged)]
pub enum ParityOutput {
    Ok(Box<ParityCaseResult>),
    Error(ParityErrorResult),
}

impl ParityOutput {
    #[must_use]
    pub const fn is_ok(&self) -> bool {
        matches!(self, Self::Ok(_))
    }
}

pub fn solve_combat_case(
    envelope: &mut ParityRequestEnvelope,
    cancel: &AtomicBool,
) -> Result<ParityCaseResult, SolverError> {
    let started = Instant::now();
    envelope.validate(COMBAT_PROFILE)?;
    let request = envelope.canonical_request()?;
    let mut legal_action_ids: Vec<String> = legal_actions(&request.state)?
        .iter()
        .map(Action::action_id)
        .collect();
    legal_action_ids.sort();
    let proof = prove_lethal(&request.state, DEFAULT_MAXIMUM_STATES, cancel)?;
    let plan = choose_turn_plan(&request.state, &proof, DEFAULT_MAXIMUM_STATES, cancel)?;
    let top1_action_id = plan
        .actions
        .first()
        .map(Action::action_id)
        .ok_or_else(|| SolverError::IllegalAction("turn plan is empty".to_owned()))?;
    let action_ids = plan.actions.iter().map(Action::action_id).collect();
    let actions = plan.actions.iter().map(ActionWire::from).collect();
    Ok(ParityCaseResult {
        schema: PARITY_RESULT_SCHEMA,
        case_id: envelope.case_id.clone(),
        suite_id: envelope.suite_id.clone(),
        profile: COMBAT_PROFILE,
        scope: "current_turn_oracle_v1",
        status: "ok",
        legal_action_ids,
        proof: Some(proof),
        portfolio: None,
        actions,
        action_ids,
        opponent_actions: Vec::new(),
        opponent_action_ids: Vec::new(),
        top1_action_id,
        terminal: TerminalSnapshot::from(plan.terminal_state),
        minimax_utility: plan.minimax_utility,
        wall_time_ms: started.elapsed().as_secs_f64() * 1000.0,
    })
}

pub fn solve_turnpair_case(
    envelope: &mut ParityRequestEnvelope,
    cancel: &AtomicBool,
) -> Result<ParityCaseResult, SolverError> {
    let started = Instant::now();
    envelope.validate(FULL_PROFILE)?;
    let request = envelope.canonical_request()?;
    let allow_point_effects = envelope.suite_id == HDT_RULE_SCOPE;
    let exact = prove_turnpair(
        &request.state,
        allow_point_effects,
        MAX_ENUMERATED_NODES,
        MAX_LINE_DEPTH,
        cancel,
    );
    let top_k = usize::from(request.options.top_k.unwrap_or(3));
    let (line, mut legal_action_ids, portfolio) = match exact {
        Ok(proof) => {
            let legal = legal_actions(&request.state)?
                .iter()
                .map(Action::action_id)
                .collect::<Vec<_>>();
            let portfolio = RootActionPortfolioWire::from_exact(&proof, top_k);
            (choose_parity_line(&proof)?, legal, portfolio)
        }
        Err(SolverError::Unsupported(_)) if envelope.raw_hdt => {
            let line =
                prove_scoped_lethal(&request.state, MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, cancel)?
                    .ok_or_else(|| {
                        SolverError::Unsupported(
                            "raw HDT fixture has no independently modeled scoped lethal".to_owned(),
                        )
                    })?;
            let legal = scoped_legal_actions(&request.state)?
                .iter()
                .map(Action::action_id)
                .collect::<Vec<_>>();
            let coverage = scoped_root_action_coverage(&request.state, &line)?;
            let portfolio = RootActionPortfolioWire::from_scoped(&coverage, &line);
            (line, legal, portfolio)
        }
        Err(error) => return Err(error),
    };
    legal_action_ids.sort();
    let top1_action_id = line.first_action_id();
    let action_ids = line.actions.iter().map(Action::action_id).collect();
    let actions = line.actions.iter().map(ActionWire::from).collect();
    let opponent_action_ids = line
        .opponent_response
        .iter()
        .map(Action::action_id)
        .collect();
    let opponent_actions = line
        .opponent_response
        .iter()
        .map(ActionWire::from)
        .collect();
    Ok(ParityCaseResult {
        schema: PARITY_RESULT_SCHEMA,
        case_id: envelope.case_id.clone(),
        suite_id: envelope.suite_id.clone(),
        profile: FULL_PROFILE,
        scope: crate::turnpair::RESPONSE_SCOPE,
        status: "ok",
        legal_action_ids,
        proof: None,
        portfolio: Some(portfolio),
        actions,
        action_ids,
        opponent_actions,
        opponent_action_ids,
        top1_action_id,
        terminal: TerminalSnapshot::from(line.terminal_state),
        minimax_utility: line.minimax_value,
        wall_time_ms: started.elapsed().as_secs_f64() * 1000.0,
    })
}

fn error_output(
    case_id: String,
    suite_id: String,
    profile: &'static str,
    error: &SolverError,
) -> ParityOutput {
    ParityOutput::Error(ParityErrorResult {
        schema: PARITY_RESULT_SCHEMA,
        case_id,
        suite_id,
        profile,
        status: if matches!(error, SolverError::Unsupported(_)) {
            "unsupported"
        } else if matches!(error, SolverError::Cancelled) {
            "cancelled"
        } else {
            "error"
        },
        error: ParityErrorBody {
            code: error.code(),
            message: error.public_message(),
            path: error.path().to_owned(),
        },
    })
}

pub fn evaluate_json(input: &str, profile: &str, cancel: &AtomicBool) -> ParityOutput {
    let output_profile = if profile == FULL_PROFILE {
        FULL_PROFILE
    } else {
        COMBAT_PROFILE
    };
    let parsed = serde_json::from_str::<ParityRequestEnvelope>(input);
    let mut envelope = match parsed {
        Ok(envelope) => envelope,
        Err(error) => {
            let error = SolverError::Json(error);
            return error_output(String::new(), String::new(), output_profile, &error);
        }
    };
    let case_id = envelope.case_id.clone();
    let suite_id = envelope.suite_id.clone();
    if !matches!(profile, COMBAT_PROFILE | FULL_PROFILE) {
        let error = SolverError::Unsupported(format!(
            "unknown parity profile {profile:?}; expected {COMBAT_PROFILE:?} or {FULL_PROFILE:?}"
        ));
        return error_output(case_id, suite_id, output_profile, &error);
    }
    let result = if profile == FULL_PROFILE {
        solve_turnpair_case(&mut envelope, cancel)
    } else {
        solve_combat_case(&mut envelope, cancel)
    };
    match result {
        Ok(result) => ParityOutput::Ok(Box::new(result)),
        Err(error) => error_output(case_id, suite_id, output_profile, &error),
    }
}

pub fn run_jsonl<R: BufRead, W: Write>(
    reader: R,
    mut writer: W,
    profile: &str,
    cancel: &AtomicBool,
) -> Result<bool, SolverError> {
    let mut all_ok = true;
    for line in reader.lines() {
        let line = line?;
        if line.trim().is_empty() {
            continue;
        }
        let output = evaluate_json(&line, profile, cancel);
        all_ok &= output.is_ok();
        serde_json::to_writer(&mut writer, &output)?;
        writer.write_all(b"\n")?;
        writer.flush()?;
    }
    Ok(all_ok)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_profile_is_not_reported_as_success() {
        let cancel = AtomicBool::new(false);
        let output = evaluate_json("{}", "full", &cancel);
        assert!(!output.is_ok());
    }
}
