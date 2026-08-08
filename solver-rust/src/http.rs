use std::collections::{BTreeSet, HashMap};
use std::io::Read;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use serde::Deserialize;
use serde_json::{Value, json};
use tiny_http::{Header, Method, Request, Response, Server, StatusCode};

use crate::behavior::{BehaviorError, BehaviorErrorClass, BehaviorLogger};
use crate::behavior_prior::{BehaviorPrior, BehaviorPriorManager};
use crate::card_pool::OfficialCardPoolBundle;
use crate::decision_ranker::{DecisionRanker, DecisionRankerManager};
use crate::error::SolverError;
use crate::generation_rules::{
    GENERATION_MATCHING_CONTRACT, GENERATION_RULESET_ID, apply_embedded_generation_rules,
    embedded_generation_rule_bundle,
};
use crate::hdt::solve_request_from_value;
use crate::hdt_root::HdtRootCandidateSet;
use crate::model::{Action, ActionKind, SolveRequest};
use crate::oracle::legal_actions;
use crate::parity::ActionWire;
use crate::rules::{
    MATCHING_CONTRACT, RULESET_ID, RuleAssessment, apply_embedded_rules, embedded_rule_bundle,
};
use crate::template_rules::{
    TEMPLATE_MATCHING_CONTRACT, TEMPLATE_RULESET_ID, apply_embedded_template_rules,
    embedded_template_rule_bundle,
};
use crate::training_log::TrainingLogger;
use crate::turnpair::{
    MAX_ENUMERATED_NODES, MAX_LINE_DEPTH, RESPONSE_KIND, RESPONSE_SCOPE,
    ROOT_ACTION_PORTFOLIO_MODEL, RootActionCoverage, SearchControl, TACTICAL_SCORE_KIND,
    TurnPairLine, VISIBLE_RESPONSE_SCOPE, VisibleResponseLine, VisibleResponsePlan,
    alternative_kind, plan_visible_response_with_control_and_roots,
    prove_scoped_lethal_with_control, prove_turnpair_with_control, ranked_lines,
    scoped_root_action_coverage,
};
use crate::{API_VERSION, PACKAGE_VERSION};

const DEFAULT_MAX_REQUEST_BYTES: usize = 2 * 1024 * 1024;
const CANCEL_TOMBSTONE_TTL: Duration = Duration::from_secs(30);
const BEHAVIOR_REFERENCE_CONTRACT: &str = "hdt_complete_candidate_behavior_reference_v1";

#[derive(Clone, Debug)]
struct ActiveSolve {
    state_id: String,
    cancel: Arc<AtomicBool>,
}

#[derive(Debug)]
struct HttpState {
    session_token: String,
    max_request_bytes: usize,
    active: Mutex<HashMap<String, ActiveSolve>>,
    cancelled_before_start: Mutex<HashMap<String, Instant>>,
    solve_gate: Mutex<()>,
    training_log: TrainingLogger,
    behavior_log: BehaviorLogger,
    behavior_prior: BehaviorPriorManager,
    decision_ranker: DecisionRankerManager,
    official_card_pools: OfficialCardPoolBundle,
}

impl HttpState {
    fn health(&self) -> Value {
        let active_solves = self.active.lock().map_or(0, |active| active.len());
        let rule_bundle = embedded_rule_bundle().ok();
        let template_bundle = embedded_template_rule_bundle().ok();
        let generation_bundle = embedded_generation_rule_bundle().ok();
        json!({
            "api_version": API_VERSION,
            "status": "ready",
            "backend": "rust",
            "parity_profile": "full",
            "production_ready": true,
            "package_version": PACKAGE_VERSION,
            "worker_version": PACKAGE_VERSION,
            "model_version": "rust-turnpair-v1",
            "message": "Rust 双回合求解器优先返回精确证明；普通 HDT 局面可降级为明确标注的公开信息近似候选。",
            "is_ready": true,
            "active_solves": active_solves,
            "training_log_enabled": self.training_log.enabled(),
            "training_log_healthy": self.training_log.healthy(),
            "behavior_log_enabled": self.behavior_log.enabled(),
            "behavior_log_healthy": self.behavior_log.healthy(),
            "behavior_prior": self.behavior_prior.health_payload(),
            "decision_ranker": self.decision_ranker.health_payload(),
            "official_card_pools": self.official_card_pools.health_payload(),
            "structured_card_rules": {
                "available": rule_bundle.is_some(),
                "ruleset_id": RULESET_ID,
                "rule_count": rule_bundle.map_or(0, |bundle| bundle.rule_count()),
                "registered_card_id_count": rule_bundle
                    .map_or(0, |bundle| bundle.registered_card_id_count()),
                "context_guarded_rule_count": rule_bundle
                    .map_or(0, |bundle| bundle.context_guarded_rule_count()),
                "required_mechanic_guarded_rule_count": rule_bundle
                    .map_or(0, |bundle| bundle.required_mechanic_guarded_rule_count()),
                "matching_contract": MATCHING_CONTRACT,
                "intrinsic_mechanic_evidence": "吸血规则必须由 has_lifesteal 或 LIFESTEAL(685) 公开标签证明。"
            },
            "text_template_card_rules": {
                "available": template_bundle.is_some(),
                "ruleset_id": TEMPLATE_RULESET_ID,
                "matching_contract": TEMPLATE_MATCHING_CONTRACT,
                "runtime_effect_coverage": "generic",
                "exact_claim_allowed": false,
                "rules_generated_from_free_text": true,
                "unique_official_cards": template_bundle
                    .map_or(0, |bundle| bundle.unique_official_cards()),
                "compiled_generic_rule_count": template_bundle
                    .map_or(0, |bundle| bundle.rule_count()),
                "registered_card_id_count": template_bundle
                    .map_or(0, |bundle| bundle.registered_card_id_count()),
                "already_exact_card_count": template_bundle
                    .map_or(0, |bundle| bundle.already_exact_cards()),
                "uncompiled_card_count": template_bundle
                    .map_or(0, |bundle| bundle.uncompiled_cards()),
                "source_run_id": template_bundle
                    .map_or("", |bundle| bundle.source_run_id()),
                "source_card_defs_build": template_bundle
                    .map_or("", |bundle| bundle.source_card_defs_build())
            },
            "generation_card_rules": {
                "available": generation_bundle.is_some(),
                "ruleset_id": GENERATION_RULESET_ID,
                "matching_contract": GENERATION_MATCHING_CONTRACT,
                "unique_official_cards": generation_bundle
                    .map_or(0, |bundle| bundle.unique_official_cards()),
                "stochastic_card_count": generation_bundle
                    .map_or(0, |bundle| bundle.inventory_count()),
                "runtime_ready_count": generation_bundle
                    .map_or(0, |bundle| bundle.runtime_ready_count()),
                "explicit_manual_queue_count": generation_bundle
                    .map_or(0, |bundle| bundle.manual_queue_count()),
                "source_run_id": generation_bundle
                    .map_or("", |bundle| bundle.source_run_id()),
                "source_card_defs_build": generation_bundle
                    .map_or("", |bundle| bundle.source_card_defs_build()),
                "unresolved_rules_are_executed": false,
                "zone_or_history_fallback_to_current_format": false
            },
            "capabilities": {
                "rust_core": true,
                "oracle_turn_v1": true,
                "structured_state_key": true,
                "live_proven_lethal_only": false,
                "generic_simulator": false,
                "visible_combat_v2": false,
                "counterplay_turnpair_v1": true,
                "visible_response_v1": true,
                "root_action_portfolio_v1": true,
                "behavior_search_ordering_prior_v1": true,
                "hdt_decision_ranker_v1": true,
                "hdt_behavior_reference_v1": true,
                "hdt_visible_point_effects_v1": true,
                "hdt_text_template_effects_v1": template_bundle.is_some(),
                "generated_card_pool_rules_v1": generation_bundle.is_some(),
                "random_and_discover_chance_branches_v1": generation_bundle.is_some(),
                "opponent_visible_best_response": true,
                "full_hearthstone_rules": false,
                "cancellation": true,
                "request_time_budget": true,
                "shared_iteration_budget": true,
                "single_solve_concurrency": true,
                "progress_snapshots": false
            }
        })
    }
}

#[derive(Clone, Debug)]
pub struct ServeOptions {
    pub host: String,
    pub port: u16,
    pub session_token: String,
    pub max_request_bytes: usize,
    pub training_log_path: Option<PathBuf>,
    pub behavior_prior_path: Option<PathBuf>,
    pub decision_ranker_path: Option<PathBuf>,
    pub official_card_pool_path: Option<PathBuf>,
}

impl Default for ServeOptions {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".to_owned(),
            port: 17_853,
            session_token: String::new(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            training_log_path: None,
            behavior_prior_path: None,
            decision_ranker_path: None,
            official_card_pool_path: None,
        }
    }
}

fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    let mut difference = left.len() ^ right.len();
    let maximum = left.len().max(right.len());
    for index in 0..maximum {
        let left_byte = left.get(index).copied().unwrap_or(0);
        let right_byte = right.get(index).copied().unwrap_or(0);
        difference |= usize::from(left_byte ^ right_byte);
    }
    difference == 0
}

fn header_value<'a>(request: &'a Request, name: &'static str) -> Option<&'a str> {
    request
        .headers()
        .iter()
        .find(|header| header.field.equiv(name))
        .map(|header| header.value.as_str())
}

fn authorized(request: &Request, token: &str) -> bool {
    let bearer = header_value(request, "Authorization").and_then(|value| {
        value
            .get(..7)
            .filter(|prefix| prefix.eq_ignore_ascii_case("bearer "))
            .map(|_| value[7..].trim())
    });
    let supplied = bearer
        .or_else(|| header_value(request, "X-Advisor-Token"))
        .or_else(|| header_value(request, "X-MetaCompanion-Token"))
        .unwrap_or("");
    !supplied.is_empty() && constant_time_eq(supplied.as_bytes(), token.as_bytes())
}

fn json_response(status: u16, payload: &Value) -> Response<std::io::Cursor<Vec<u8>>> {
    let body = serde_json::to_vec(payload).unwrap_or_else(|_| {
        r#"{"api_version":"1.0","error":{"code":"internal_error","message":"求解器发生内部错误，请稍后重试。","path":""}}"#
            .as_bytes()
            .to_vec()
    });
    let mut response = Response::from_data(body).with_status_code(StatusCode(status));
    for (name, value) in [
        ("Content-Type", "application/json; charset=utf-8"),
        ("Cache-Control", "no-store"),
        ("X-Content-Type-Options", "nosniff"),
    ] {
        if let Ok(header) = Header::from_bytes(name, value) {
            response.add_header(header);
        }
    }
    response
}

fn error_payload(code: &str, message: impl Into<String>, path: &str) -> Value {
    json!({
        "api_version": API_VERSION,
        "error": {
            "code": code,
            "message": message.into(),
            "path": path
        }
    })
}

fn solver_error_response(error: &SolverError) -> (u16, Value) {
    let status = match error {
        SolverError::Schema { .. } | SolverError::Json(_) => 400,
        SolverError::IllegalAction(_)
        | SolverError::Unsupported(_)
        | SolverError::DepthLimit(_)
        | SolverError::StateLimit(_)
        | SolverError::TimeLimit => 422,
        SolverError::Cancelled => 409,
        SolverError::ResultObservationConflict => 409,
        SolverError::Io(_) | SolverError::Http(_) => 500,
    };
    (
        status,
        error_payload(error.code(), error.public_message(), error.path()),
    )
}

fn behavior_error_response(error: &BehaviorError) -> (u16, Value) {
    let (status, message) = match error.class() {
        BehaviorErrorClass::Validation => (400, "行为记录格式或公开证据绑定不正确。"),
        BehaviorErrorClass::Conflict => (409, "行为序列与已经落盘的内容冲突，已拒绝覆盖。"),
        BehaviorErrorClass::Storage => (500, "行为日志暂时无法安全写入，请检查健康状态。"),
    };
    let path = if error.class() == BehaviorErrorClass::Storage {
        "behavior_log"
    } else {
        error.path()
    };
    (status, error_payload(error.code(), message, path))
}

fn read_body(request: &mut Request, maximum: usize) -> Result<Vec<u8>, (u16, Value)> {
    let length = header_value(request, "Content-Length")
        .ok_or_else(|| {
            (
                411,
                error_payload("length_required", "请求缺少 Content-Length。", ""),
            )
        })?
        .parse::<usize>()
        .map_err(|_| {
            (
                400,
                error_payload("invalid_length", "Content-Length 必须是整数。", ""),
            )
        })?;
    if length > maximum {
        return Err((
            413,
            error_payload("request_too_large", "请求内容过大。", ""),
        ));
    }
    let mut body = Vec::with_capacity(length);
    request
        .as_reader()
        .take((maximum + 1) as u64)
        .read_to_end(&mut body)
        .map_err(|_| {
            (
                400,
                error_payload("invalid_request", "无法读取请求内容。", ""),
            )
        })?;
    if body.len() != length {
        return Err((
            400,
            error_payload(
                "invalid_length",
                "Content-Length 与实际请求内容长度不一致。",
                "",
            ),
        ));
    }
    Ok(body)
}

fn action_payload(action: &crate::parity::ActionWire, index: usize) -> Value {
    let mut value = json!({
        "index": index,
        "action_id": action.action_id,
        "kind": action.kind,
        "type": action.kind,
        "source_entity_id": action.source_entity_id,
        "target_entity_id": action.target_entity_id,
        "card_id": action.card_id,
        "text": action.text
    });
    if let Some(board_position) = action.board_position {
        value["board_position"] = json!(board_position);
    }
    value
}

fn wire_actions(actions: &[Action]) -> Vec<Value> {
    actions
        .iter()
        .enumerate()
        .map(|(index, action)| action_payload(&ActionWire::from(action), index + 1))
        .collect()
}

fn normalized_tactical_score(value: i64) -> f64 {
    if value >= 1_000_000 {
        1.0
    } else if value <= -1_000_000 {
        0.0
    } else {
        (0.5 + value as f64 / 20_000.0).clamp(0.0, 1.0)
    }
}

fn recommendation_payload(
    line: &TurnPairLine,
    rank: usize,
    verified_portfolio_regret: Option<i64>,
    alternative_kind: &str,
) -> Value {
    let actions = wire_actions(&line.actions);
    let response = wire_actions(&line.opponent_response);
    let response_is_lethal = !line.safe_after_response;
    let worst_case_score = normalized_tactical_score(line.minimax_value);
    let mut material = line
        .actions
        .iter()
        .map(Action::action_id)
        .collect::<Vec<_>>();
    material.push("response".to_owned());
    material.extend(line.opponent_response.iter().map(Action::action_id));
    let line_id = blake3::hash(material.join("|").as_bytes()).to_hex()[..16].to_owned();
    let score_components = json!({
        "oracle_tactical_utility": line.minimax_value,
        "minimax_value": line.minimax_value
    });
    let summary = if line.immediate_lethal {
        "该路线在当前可见规则范围内已证明立即斩杀。"
    } else if line.safe_after_response {
        "已穷举当前可见的对手合法回应，该路线在最坏回应后仍存活。"
    } else {
        "该路线的最坏可见回应会形成对手反杀，已明确标记为不安全。"
    };
    json!({
        "rank": rank,
        "line_id": line_id,
        "actions": actions,
        "expected_win_probability": if line.immediate_lethal { 1.0 } else { worst_case_score },
        "expected_win_rate": if line.immediate_lethal { 1.0 } else { worst_case_score },
        "score_kind": TACTICAL_SCORE_KIND,
        "confidence_interval": [worst_case_score, worst_case_score],
        "win_rate_low": worst_case_score,
        "win_rate_high": worst_case_score,
        "confidence": 1.0,
        "visits": line.response_nodes_expanded,
        "rationale": summary,
        "summary": summary,
        "risks": ["证明仅覆盖当前公开、确定且已结构化的规则，不包含隐藏手牌或未知抽牌。"],
        "approximate_effects": [],
        "annotations": [{
            "code": "visible_turnpair_scope",
            "detail": "已验证当前回合与对手最坏可见回应；未声称完整炉石全局最优。",
            "entity_id": "",
            "severity": "info"
        }],
        "proof_kind": if line.immediate_lethal { "modeled_lethal" } else { "" },
        "proof_scope": if line.immediate_lethal { "visible_generic_v2" } else { "" },
        "is_proven_lethal": line.immediate_lethal,
        "opponent_reply": response,
        "worst_case_score": worst_case_score,
        "response_scope": RESPONSE_SCOPE,
        "response_search_complete": true,
        "response_is_proven_lethal": response_is_lethal,
        "response_nodes_expanded": line.response_nodes_expanded,
        "response_searched_depth": line.opponent_response.len(),
        "response_transposition_hits": line.response_transposition_hits,
        "is_response_verified": true,
        "response_kind": RESPONSE_KIND,
        "minimax_value": line.minimax_value,
        "verified_portfolio_regret": verified_portfolio_regret,
        "alternative_kind": alternative_kind,
        "is_safe_after_response": line.safe_after_response,
        "opponent_response": {
            "actions": response,
            "tactical_value": line.minimax_value
        },
        "score_components": score_components,
        "counterplay": {
            "scope": RESPONSE_SCOPE,
            "search_complete": true,
            "is_proven_lethal": response_is_lethal,
            "worst_case_score": worst_case_score,
            "nodes_expanded": line.response_nodes_expanded,
            "searched_depth": line.opponent_response.len(),
            "transposition_hits": line.response_transposition_hits,
            "actions": response,
            "score_components": score_components
        }
    })
}

fn visible_recommendation_payload(line: &VisibleResponseLine, rank: usize) -> Value {
    let actions = wire_actions(&line.actions);
    let opponent_reply = wire_actions(&line.opponent_reply);
    let mut material = line
        .actions
        .iter()
        .map(Action::action_id)
        .collect::<Vec<_>>();
    material.push("visible-response-v1".to_owned());
    material.extend(line.opponent_reply.iter().map(Action::action_id));
    let line_id = blake3::hash(material.join("|").as_bytes()).to_hex()[..16].to_owned();
    let score = normalized_tactical_score(line.tactical_value).clamp(0.05, 0.95);
    let low = line.chance.as_ref().map_or_else(
        || (score - 0.2).max(0.0),
        |chance| normalized_tactical_score(chance.minimum_utility),
    );
    let high = line.chance.as_ref().map_or_else(
        || (score + 0.2).min(1.0),
        |chance| normalized_tactical_score(chance.maximum_utility),
    );
    let approximate_effects = line
        .approximate_entity_ids
        .iter()
        .map(|entity_id| {
            format!("实体 {entity_id} 仅按当前公开基础数值处理；其未结构化文本效果未计入评分。")
        })
        .collect::<Vec<_>>();
    let summary = if line.chance.is_some() {
        "已计算所有当前可见的随机结果及后续最优应对；随机结果出现后会立即重新计算。"
    } else if approximate_effects.is_empty() {
        "这是基于当前公开、可建模基础动作得到的近似候选。"
    } else {
        "这是包含“实体仅按公开基础数值处理”假设的公开信息近似候选。"
    };
    let mut risks =
        vec!["未穷举隐藏手牌、未知抽牌和未结构化卡牌效果，排序只用于提供候选思路。".to_owned()];
    if !line.opponent_reply.is_empty() {
        risks.push("展示的对手动作只是已发现的公开回应，不是经过证明的最坏回应。".to_owned());
    }
    if line.chance.is_some() {
        risks.push("期望值覆盖当前可见随机池；隐藏手牌、未知抽牌和发现池仍未纳入。".to_owned());
    }
    let mut payload = json!({
        "rank": rank,
        "line_id": line_id,
        "actions": actions,
        "expected_win_probability": score,
        "expected_win_rate": score,
        "score_kind": if line.chance.is_some() {
            "visible_expectiminimax_utility_v1"
        } else {
            "visible_response_heuristic_v1"
        },
        "confidence_interval": [low, high],
        "win_rate_low": low,
        "win_rate_high": high,
        "confidence": 0.25,
        "visits": 0,
        "rationale": summary,
        "summary": summary,
        "risks": risks,
        "approximate_effects": approximate_effects,
        "annotations": [{
            "code": if line.chance.is_some() {
                "visible_chance_policy"
            } else {
                "visible_response_partial"
            },
            "detail": if line.chance.is_some() {
                "随机节点取精确可见概率期望，双方行动节点分别取最优与最坏回应。"
            } else {
                "仅使用公开且当前可安全执行的规则子集生成候选。"
            },
            "entity_id": "",
            "severity": "warning"
        }],
        "opponent_reply": opponent_reply,
        "verified_portfolio_regret": Value::Null,
        "alternative_kind": "fallback",
        "score_components": {
            "visible_tactical_utility": line.tactical_value
        }
    });
    if let Some(chance) = &line.chance {
        payload["value_semantics"] = json!("visible_expectiminimax_utility_v1");
        payload["expected_tactical_utility"] = json!(chance.expected_utility);
        payload["random_outcome_utility_range"] =
            json!([chance.minimum_utility, chance.maximum_utility]);
        payload["recompute_after_random_outcome"] = json!(chance.recompute_after_random_outcome);
        payload["visible_survival_probability"] = json!({
            "numerator": chance.survival_probability.numerator,
            "denominator": chance.survival_probability.denominator,
            "value": chance.survival_probability.numerator as f64
                / chance.survival_probability.denominator as f64
        });
    }
    payload
}

fn visible_counterplay_coverage_payload(plan: &VisibleResponsePlan) -> Value {
    let legal_first_action_ids = plan.legal_first_action_ids.clone();
    let generated_first_action_ids = plan.modeled_first_action_ids.clone();
    let missing_first_action_ids = legal_first_action_ids.clone();
    json!({
        "planner_model": "rust-visible-response-v1",
        "portfolio_model": ROOT_ACTION_PORTFOLIO_MODEL,
        "portfolio_optimality_proven": false,
        "approximation_scope": VISIBLE_RESPONSE_SCOPE,
        "search_complete": false,
        "response_line_complete": false,
        "assessed_line_count": plan.assessed_line_count,
        "transposition_hits": 0,
        "legal_first_action_count": legal_first_action_ids.len(),
        "legal_first_action_ids": legal_first_action_ids,
        "generated_first_action_count": generated_first_action_ids.len(),
        "generated_first_action_ids": generated_first_action_ids,
        "response_verified_first_action_count": 0,
        "response_verified_first_action_ids": [],
        "missing_first_action_ids": missing_first_action_ids,
        "root_action_coverage_complete": false,
        "legal_first_action_basis": if plan.hdt_supplied_root_portfolio {
            "hdt_complete_main_action_options_v1"
        } else {
            "modeled_visible_subset"
        },
        "modeled_subset_complete": plan.omitted_first_action_ids.is_empty(),
        "omitted_unmodeled_first_action_count": plan.omitted_first_action_ids.len(),
        "omitted_unmodeled_first_action_ids": plan.omitted_first_action_ids,
        "node_limit_reached": plan.node_limit_reached,
        "depth_limit_reached": plan.depth_limit_reached,
        "time_limit_reached": plan.time_limit_reached
    })
}

fn independent_root_action_ids(request: &SolveRequest) -> Vec<String> {
    legal_actions(&request.state).map_or_else(
        |_| Vec::new(),
        |actions| {
            actions
                .iter()
                .map(|action| {
                    if action.kind == ActionKind::EndTurn {
                        "end_turn".to_owned()
                    } else {
                        action.action_id()
                    }
                })
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect()
        },
    )
}

fn root_candidate_source_payloads(
    independent_ids: &[String],
    hdt_roots: Option<&HdtRootCandidateSet>,
    evaluated_ids: &[String],
) -> (Value, Value) {
    let independent = independent_ids.iter().cloned().collect::<BTreeSet<_>>();
    let evaluated = evaluated_ids.iter().cloned().collect::<BTreeSet<_>>();
    let Some(hdt_roots) = hdt_roots else {
        return (
            json!({
                "contract": "solver_independent_root_generation_v1",
                "available": true,
                "generated_count": independent.len(),
                "generated_action_ids": independent,
                "hdt_reference_available": false,
                "exact_match": false,
                "false_exact": false,
                "live_policy_eligible": false,
                "rl_training_eligible": false,
                "global_optimality_verified": false
            }),
            json!({
                "contract": "hdt_complete_main_action_options_v1",
                "available": false,
                "candidate_set_complete": false,
                "live_policy_eligible": false,
                "rl_training_eligible": false,
                "global_optimality_verified": false
            }),
        );
    };
    let hdt = hdt_roots.action_ids();
    let matched = independent
        .intersection(&hdt)
        .cloned()
        .collect::<BTreeSet<_>>();
    let evaluated_hdt = evaluated
        .intersection(&hdt)
        .cloned()
        .collect::<BTreeSet<_>>();
    let independent_recall = if hdt.is_empty() {
        0.0
    } else {
        matched.len() as f64 / hdt.len() as f64
    };
    let evaluated_coverage = if hdt.is_empty() {
        0.0
    } else {
        evaluated_hdt.len() as f64 / hdt.len() as f64
    };
    (
        json!({
            "contract": "solver_independent_root_generation_v1",
            "available": true,
            "generated_count": independent.len(),
            "generated_action_ids": independent,
            "matched_hdt_count": matched.len(),
            "matched_hdt_action_ids": matched,
            "hdt_candidate_count": hdt.len(),
            "hdt_recall": independent_recall,
            "exact_match": independent == hdt,
            "false_exact": false,
            "live_policy_eligible": false,
            "rl_training_eligible": false,
            "global_optimality_verified": false
        }),
        json!({
            "contract": hdt_roots.contract,
            "available": true,
            "state_bound": true,
            "frame_id": hdt_roots.frame_id,
            "collector_epoch": hdt_roots.collector_epoch,
            "candidate_set_complete": hdt_roots.candidate_set_complete,
            "candidate_count": hdt.len(),
            "legal_action_ids": hdt,
            "evaluated_count": evaluated_hdt.len(),
            "evaluated_action_ids": evaluated_hdt,
            "evaluated_coverage": evaluated_coverage,
            "effect_simulation_complete": false,
            "root_legality_source": "hdt_debug_print_options",
            "hidden_response_generation_allowed": false,
            "live_policy_eligible": false,
            "rl_training_eligible": false,
            "global_optimality_verified": false
        }),
    )
}

fn rules_payload(rule_assessment: Option<&RuleAssessment>) -> Value {
    rule_assessment.map_or_else(
        || json!({"available": false, "ruleset_id": "", "matched": [], "mismatches": []}),
        |assessment| {
            json!({
                "available": true,
                "ruleset_id": assessment.ruleset_id,
                "matched": assessment.matched,
                "mismatches": assessment.mismatches
            })
        },
    )
}

fn behavior_prior_search_payload(control: &SearchControl<'_>) -> Value {
    let status = if control.behavior_prior_runtime_rejected() {
        "runtime_rejected"
    } else if control.behavior_prior_applied() {
        "applied"
    } else if control.behavior_prior_available() {
        "available_not_applicable"
    } else {
        "disabled"
    };
    json!({
        "status": status,
        "artifact_sha256": control.behavior_prior_identity(),
        "ordering_attempt_count": control.behavior_prior_ordering_attempts(),
        "ordering_applied": control.behavior_prior_applied(),
        "search_ordering_only": true,
        "candidate_generation_allowed": false,
        "score_override_allowed": false,
        "live_policy_eligible": false,
        "rl_training_eligible": false,
        "optimality_verified": false
    })
}

fn decision_ranker_search_payload(control: &SearchControl<'_>) -> Value {
    let status = if control.decision_ranker_runtime_rejected() {
        "runtime_rejected"
    } else if control.decision_ranker_applied() {
        "applied"
    } else if control.decision_ranker_available() {
        "available_not_applicable"
    } else {
        "disabled"
    };
    json!({
        "status": status,
        "artifact_sha256": control.decision_ranker_identity(),
        "ordering_attempt_count": control.decision_ranker_ordering_attempts(),
        "ordering_applied": control.decision_ranker_applied(),
        "local_actions_only": true,
        "search_ordering_only": true,
        "candidate_generation_allowed": false,
        "score_override_allowed": false,
        "live_policy_eligible": false,
        "rl_training_eligible": false,
        "optimality_verified": false
    })
}

fn unavailable_behavior_reference(reason: &str) -> Value {
    json!({
        "contract": BEHAVIOR_REFERENCE_CONTRACT,
        "status": "unavailable",
        "available": false,
        "reason": reason,
        "source": "local_observed_behavior_cloning_v1",
        "candidate_set_contract": "hdt_complete_main_action_options_v1",
        "candidate_set_complete": false,
        "candidate_count": 0,
        "ranked_candidate_count": 0,
        "displayed_reference_count": 0,
        "references": [],
        "behavior_reference_eligible": false,
        "candidate_generation_allowed": false,
        "tactical_score_override_allowed": false,
        "automatic_action_allowed": false,
        "live_policy_eligible": false,
        "rl_training_eligible": false,
        "optimality_verified": false,
        "outcome_used_as_action_optimality": false
    })
}

fn behavior_reference_payload_from_scores(
    roots: &HdtRootCandidateSet,
    actions: Vec<Action>,
    scores: Vec<f64>,
    artifact_sha256: &str,
    top_k: usize,
) -> Value {
    if actions.len() != roots.candidates.len()
        || scores.len() != actions.len()
        || artifact_sha256.len() != 64
        || !scores
            .iter()
            .all(|score| score.is_finite() && (0.0..=1.0).contains(score))
    {
        return unavailable_behavior_reference("ranking_contract_invalid");
    }
    let mut ranked = roots
        .candidates
        .iter()
        .zip(actions)
        .zip(scores)
        .map(|((candidate, action), probability)| {
            (candidate.action.action_id(), action, probability)
        })
        .collect::<Vec<_>>();
    ranked.sort_by(|(left_id, _, left_score), (right_id, _, right_score)| {
        right_score
            .partial_cmp(left_score)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| left_id.cmp(right_id))
    });
    let candidate_count = ranked.len();
    let references = ranked
        .into_iter()
        .take(top_k.max(1))
        .enumerate()
        .map(|(index, (legal_action_id, action, probability))| {
            json!({
                "rank": index + 1,
                "legal_action_id": legal_action_id,
                "action": action_payload(&ActionWire::from(&action), 1),
                "observed_choice_probability": probability,
                "probability_calibrated_as_win_rate": false,
                "optimality_verified": false
            })
        })
        .collect::<Vec<_>>();
    json!({
        "contract": BEHAVIOR_REFERENCE_CONTRACT,
        "status": "available",
        "available": true,
        "reason": "",
        "source": "local_observed_behavior_cloning_v1",
        "artifact_sha256": artifact_sha256,
        "candidate_set_contract": roots.contract,
        "candidate_set_complete": roots.candidate_set_complete,
        "candidate_count": candidate_count,
        "ranked_candidate_count": candidate_count,
        "displayed_reference_count": references.len(),
        "references": references,
        "behavior_reference_eligible": true,
        "candidate_generation_allowed": false,
        "tactical_score_override_allowed": false,
        "automatic_action_allowed": false,
        "live_policy_eligible": false,
        "rl_training_eligible": false,
        "optimality_verified": false,
        "outcome_used_as_action_optimality": false
    })
}

fn behavior_reference_payload(
    request: &SolveRequest,
    decision_ranker: Option<&DecisionRanker>,
    top_k: usize,
) -> Value {
    let Some(roots) = request.hdt_root_candidates.as_ref() else {
        return unavailable_behavior_reference("complete_hdt_candidates_unavailable");
    };
    let Some(ranker) = decision_ranker else {
        return unavailable_behavior_reference("decision_ranker_unavailable");
    };
    if !ranker.supports_state(&request.state) {
        return unavailable_behavior_reference("decision_ranker_context_unsupported");
    }
    let actions = roots.solver_actions();
    let Ok(scores) = ranker.score_actions(&request.state, &actions) else {
        return unavailable_behavior_reference("decision_ranker_runtime_rejected");
    };
    behavior_reference_payload_from_scores(roots, actions, scores, ranker.artifact_sha256(), top_k)
}

fn visible_partial_solve_result(
    request: &SolveRequest,
    rule_assessment: Option<&RuleAssessment>,
    decision_ranker: Option<&DecisionRanker>,
    max_depth: u8,
    control: &mut SearchControl<'_>,
    started: Instant,
) -> Result<Value, SolverError> {
    let top_k = usize::from(request.options.top_k.unwrap_or(3));
    let plan = plan_visible_response_with_control_and_roots(
        &request.state,
        top_k,
        max_depth,
        control,
        request.hdt_root_candidates.as_ref(),
    )?;
    let recommendations = plan
        .lines
        .iter()
        .enumerate()
        .map(|(index, line)| visible_recommendation_payload(line, index + 1))
        .collect::<Vec<_>>();
    let counterplay_coverage = visible_counterplay_coverage_payload(&plan);
    let (independent_generated_root_coverage, hdt_supplied_root_portfolio_coverage) =
        root_candidate_source_payloads(
            &plan.independent_generated_first_action_ids,
            request.hdt_root_candidates.as_ref(),
            &plan.modeled_first_action_ids,
        );
    let behavior_prior = behavior_prior_search_payload(control);
    let decision_ranker_status = decision_ranker_search_payload(control);
    let behavior_references = behavior_reference_payload(request, decision_ranker, top_k);
    let modeled_count = plan.modeled_first_action_ids.len();
    let omitted_count = plan.omitted_first_action_ids.len();
    let coverage_ratio = if modeled_count + omitted_count == 0 {
        0.0
    } else {
        modeled_count as f64 / (modeled_count + omitted_count) as f64
    };
    let mut warnings = vec![
        "当前结果未穷举隐藏手牌、未知抽牌和未结构化效果，只是公开信息近似建议，不代表已证明的全局最优出牌。"
            .to_owned(),
    ];
    if omitted_count > 0 {
        warnings.push(format!(
            "有 {omitted_count} 个首步因卡牌效果或动作规则尚未建模而未参与排序。"
        ));
    }
    if plan.node_limit_reached {
        warnings.push("搜索达到节点上限，已保留每个已建模首步的有界候选。".to_owned());
    }
    if plan.depth_limit_reached {
        warnings.push("搜索达到深度上限，已保留能够完整展示的有界候选。".to_owned());
    }
    if plan.time_limit_reached {
        warnings.push("搜索达到本次时间上限，已保留每个已建模首步的安全基线候选。".to_owned());
    }
    Ok(json!({
        "api_version": API_VERSION,
        "schema_version": 1,
        "request_id": request.request_id,
        "state_id": request.state.state_id,
        "status": "partial",
        "elapsed_ms": started.elapsed().as_millis() as u64,
        "iterations": control.nodes(),
        "recommendations": recommendations,
        "behavior_references": behavior_references,
        "progress": [],
        "coverage": {
            "rules_model": RULESET_ID,
            "planner_model": "rust-visible-response-v1",
            "exact": false,
            "exact_scope": VISIBLE_RESPONSE_SCOPE,
            "scoped_lethal": false,
            "time_limit_reached": plan.time_limit_reached,
            "unsupported_count": omitted_count,
            "approximate_effects": [],
            "overall": coverage_ratio,
            "card_coverage": coverage_ratio,
            "rule_coverage": coverage_ratio,
            "summary": "已返回公开信息规则子集内的近似候选；未声称回应验证或组合最优。",
            "structured_card_rules": rules_payload(rule_assessment),
            "behavior_prior": behavior_prior,
            "decision_ranker": decision_ranker_status,
            "independent_generated_root_coverage": independent_generated_root_coverage,
            "hdt_supplied_root_portfolio_coverage": hdt_supplied_root_portfolio_coverage,
            "details": {"counterplay": counterplay_coverage.clone()},
            "counterplay": counterplay_coverage
        },
        "warnings": warnings,
        "model_version": "rust-visible-response-v1",
        "environment_version": VISIBLE_RESPONSE_SCOPE,
        "is_final": true,
        "message": "已生成可操作的近似候选；未知规则已明确排除或标注。"
    }))
}

fn visible_fallback_eligible(error: &SolverError) -> bool {
    matches!(
        error,
        SolverError::Unsupported(_)
            | SolverError::DepthLimit(_)
            | SolverError::StateLimit(_)
            | SolverError::TimeLimit
    )
}

#[cfg(test)]
fn live_solve_result(
    request: SolveRequest,
    allow_point_effects: bool,
    rule_assessment: Option<&RuleAssessment>,
    cancel: &AtomicBool,
) -> Result<Value, SolverError> {
    live_solve_result_started(
        request,
        allow_point_effects,
        rule_assessment,
        cancel,
        Instant::now(),
    )
}

#[cfg(test)]
fn live_solve_result_started(
    request: SolveRequest,
    allow_point_effects: bool,
    rule_assessment: Option<&RuleAssessment>,
    cancel: &AtomicBool,
    started: Instant,
) -> Result<Value, SolverError> {
    live_solve_result_started_with_models(
        request,
        allow_point_effects,
        rule_assessment,
        cancel,
        started,
        None,
        None,
    )
}

fn live_solve_result_started_with_models(
    request: SolveRequest,
    allow_point_effects: bool,
    rule_assessment: Option<&RuleAssessment>,
    cancel: &AtomicBool,
    started: Instant,
    behavior_prior: Option<Arc<BehaviorPrior>>,
    decision_ranker: Option<Arc<DecisionRanker>>,
) -> Result<Value, SolverError> {
    let request_id = request.request_id.to_string();
    let state_id = request.state.state_id.to_string();
    let top_k = usize::from(request.options.top_k.unwrap_or(3));
    let allow_visible_fallback = request.options.allow_approximate_effects;
    let max_nodes = request
        .options
        .max_iterations
        .map_or(MAX_ENUMERATED_NODES, |value| {
            usize::try_from(value)
                .unwrap_or(MAX_ENUMERATED_NODES)
                .min(MAX_ENUMERATED_NODES)
        });
    let max_depth = request.options.max_depth.map_or(MAX_LINE_DEPTH, |value| {
        u8::try_from(value.min(u16::from(MAX_LINE_DEPTH))).unwrap_or(MAX_LINE_DEPTH)
    });
    let deadline = request.options.time_budget_ms.and_then(|milliseconds| {
        started.checked_add(Duration::from_millis(u64::from(milliseconds)))
    });
    let behavior_reference_ranker = decision_ranker.clone();
    let mut control = SearchControl::new(cancel, max_nodes, deadline)
        .with_behavior_prior(behavior_prior)
        .with_decision_ranker(decision_ranker);
    let independent_root_ids = independent_root_action_ids(&request);
    let exact_proof =
        prove_turnpair_with_control(&request.state, allow_point_effects, max_depth, &mut control)
            .and_then(|proof| {
                if request.hdt_root_candidates.as_ref().is_some_and(|roots| {
                    roots.action_ids()
                        != independent_root_ids
                            .iter()
                            .cloned()
                            .collect::<BTreeSet<_>>()
                }) {
                    Err(SolverError::Unsupported(
                        "independent root generation does not match the HDT portfolio".to_owned(),
                    ))
                } else {
                    Ok(proof)
                }
            });
    let (
        lines,
        root_action_coverage,
        optimal_value,
        portfolio_optimality_proven,
        _friendly_nodes,
        _response_nodes,
        transposition_hits,
        assessed_line_count,
        scoped_lethal,
    ) = match exact_proof {
        Ok(proof) => {
            let lines = ranked_lines(&proof, top_k);
            let assessed_line_count = proof.lines.len();
            (
                lines,
                proof.root_action_coverage,
                Some(proof.optimal_value),
                proof.portfolio_optimality_proven,
                proof.friendly_nodes_expanded,
                proof.response_nodes_expanded,
                proof.transposition_hits,
                assessed_line_count,
                false,
            )
        }
        Err(error) if visible_fallback_eligible(&error) => {
            match prove_scoped_lethal_with_control(&request.state, max_depth, &mut control) {
                Ok(Some(line)) => {
                    let first_action_id = line.first_action_id();
                    if request
                        .hdt_root_candidates
                        .as_ref()
                        .is_some_and(|roots| !roots.action_ids().contains(&first_action_id))
                    {
                        if allow_visible_fallback {
                            return visible_partial_solve_result(
                                &request,
                                rule_assessment,
                                behavior_reference_ranker.as_deref(),
                                max_depth,
                                &mut control,
                                started,
                            );
                        }
                        return Err(error);
                    }
                    let root_action_coverage =
                        if let Some(roots) = request.hdt_root_candidates.as_ref() {
                            RootActionCoverage::from_sets(
                                roots.action_ids(),
                                BTreeSet::from([first_action_id.clone()]),
                                BTreeSet::from([first_action_id]),
                            )
                        } else {
                            scoped_root_action_coverage(&request.state, &line)?
                        };
                    (
                        vec![line],
                        root_action_coverage,
                        None,
                        false,
                        0,
                        0,
                        0,
                        1,
                        true,
                    )
                }
                Ok(None) if allow_visible_fallback => {
                    return visible_partial_solve_result(
                        &request,
                        rule_assessment,
                        behavior_reference_ranker.as_deref(),
                        max_depth,
                        &mut control,
                        started,
                    );
                }
                Ok(None) => return Err(error),
                Err(scoped_error)
                    if allow_visible_fallback && visible_fallback_eligible(&scoped_error) =>
                {
                    return visible_partial_solve_result(
                        &request,
                        rule_assessment,
                        behavior_reference_ranker.as_deref(),
                        max_depth,
                        &mut control,
                        started,
                    );
                }
                Err(scoped_error) if visible_fallback_eligible(&scoped_error) => {
                    return Err(error);
                }
                Err(scoped_error) => return Err(scoped_error),
            }
        }
        Err(error) => return Err(error),
    };
    let recommendations = lines
        .iter()
        .enumerate()
        .map(|(index, line)| {
            let regret = optimal_value.map(|value| value.saturating_sub(line.minimax_value));
            let kind = alternative_kind(
                root_action_coverage.root_action_coverage_complete,
                portfolio_optimality_proven,
                regret,
                true,
            );
            recommendation_payload(line, index + 1, regret, kind)
        })
        .collect::<Vec<_>>();
    let rules = rules_payload(rule_assessment);
    let counterplay_coverage = counterplay_coverage_payload(
        &root_action_coverage,
        portfolio_optimality_proven,
        assessed_line_count,
        transposition_hits,
    );
    let behavior_prior = behavior_prior_search_payload(&control);
    let decision_ranker = decision_ranker_search_payload(&control);
    let (independent_generated_root_coverage, hdt_supplied_root_portfolio_coverage) =
        root_candidate_source_payloads(
            &independent_root_ids,
            request.hdt_root_candidates.as_ref(),
            &root_action_coverage.generated_first_action_ids,
        );
    Ok(json!({
        "api_version": API_VERSION,
        "schema_version": 1,
        "request_id": request_id,
        "state_id": state_id,
        "status": "ok",
        "elapsed_ms": started.elapsed().as_millis() as u64,
        "iterations": control.nodes(),
        "recommendations": recommendations,
        "progress": [],
        "coverage": {
            "rules_model": if allow_point_effects { RULESET_ID } else { "oracle-turnpair-v1" },
            "planner_model": "rust-turnpair-v1",
            "exact": !scoped_lethal,
            "exact_scope": if scoped_lethal { "visible_scoped_lethal_v1" } else { RESPONSE_SCOPE },
            "scoped_lethal": scoped_lethal,
            "unsupported_count": if scoped_lethal { 1 } else { 0 },
            "approximate_effects": [],
            "overall": 1.0,
            "card_coverage": 1.0,
            "rule_coverage": 1.0,
            "summary": if scoped_lethal {
                "存在未支持的备选动作，但已独立证明一条不依赖这些动作的直接斩杀路线。"
            } else {
                "已完成当前回合与对手最坏可见回应的精确搜索。"
            },
            "structured_card_rules": rules,
            "behavior_prior": behavior_prior,
            "decision_ranker": decision_ranker,
            "independent_generated_root_coverage": independent_generated_root_coverage,
            "hdt_supplied_root_portfolio_coverage": hdt_supplied_root_portfolio_coverage,
            "details": {"counterplay": counterplay_coverage.clone()},
            "counterplay": counterplay_coverage
        },
        "warnings": if scoped_lethal {
            vec!["局面含未支持的备选动作；当前仅证明展示的直接斩杀，不评价其他路线。"]
        } else {
            Vec::<&str>::new()
        },
        "model_version": "rust-turnpair-v1",
        "environment_version": "oracle-turnpair-v1",
        "is_final": true,
        "message": if scoped_lethal {
            "已生成不依赖未知备选动作的直接斩杀证明。"
        } else {
            "已生成经过最坏可见回应验证的出牌方案。"
        }
    }))
}

fn counterplay_coverage_payload(
    coverage: &RootActionCoverage,
    portfolio_optimality_proven: bool,
    assessed_line_count: usize,
    transposition_hits: usize,
) -> Value {
    json!({
        "planner_model": "rust-turnpair-v1",
        "portfolio_model": ROOT_ACTION_PORTFOLIO_MODEL,
        "portfolio_optimality_proven": portfolio_optimality_proven,
        "response_scope": RESPONSE_SCOPE,
        "search_complete": portfolio_optimality_proven,
        "response_line_complete": true,
        "assessed_line_count": assessed_line_count,
        "transposition_hits": transposition_hits,
        "legal_first_action_count": coverage.legal_first_action_count(),
        "legal_first_action_ids": coverage.legal_first_action_ids,
        "generated_first_action_count": coverage.generated_first_action_count(),
        "generated_first_action_ids": coverage.generated_first_action_ids,
        "response_verified_first_action_count": coverage.response_verified_first_action_count(),
        "response_verified_first_action_ids": coverage.response_verified_first_action_ids,
        "missing_first_action_ids": coverage.missing_first_action_ids,
        "root_action_coverage_complete": coverage.root_action_coverage_complete
    })
}

fn cancelled_solve_payload(request_id: &str, state_id: &str) -> Value {
    json!({
        "api_version": API_VERSION,
        "schema_version": 1,
        "request_id": request_id,
        "state_id": state_id,
        "status": "cancelled",
        "elapsed_ms": 0,
        "iterations": 0,
        "recommendations": [],
        "progress": [],
        "coverage": {
            "exact": false,
            "exact_scope": "",
            "summary": "搜索已取消，未把未完成结果标记为已验证。",
            "counterplay": {
                "response_scope": RESPONSE_SCOPE,
                "search_complete": false,
                "assessed_line_count": 0
            }
        },
        "warnings": ["本次搜索在完成前被取消，没有可安全返回的完整候选。"],
        "model_version": "rust-turnpair-v1",
        "environment_version": "oracle-turnpair-v1",
        "is_final": false,
        "message": "本次求解已取消。"
    })
}

fn failed_solve_payload(request: &SolveRequest, error: &SolverError) -> Value {
    let status = match error {
        SolverError::Unsupported(_) => "unsupported",
        SolverError::Cancelled => "cancelled",
        _ => "error",
    };
    json!({
        "api_version": API_VERSION,
        "schema_version": 1,
        "request_id": request.request_id,
        "state_id": request.state.state_id,
        "status": status,
        "elapsed_ms": 0,
        "iterations": 0,
        "recommendations": [],
        "progress": [],
        "coverage": {
            "exact": false,
            "planner_model": "rust-turnpair-v1",
            "rules_model": RULESET_ID,
            "summary": "求解失败，未生成可训练的决策标签。"
        },
        "warnings": [error.public_message()],
        "model_version": "rust-turnpair-v1",
        "environment_version": "oracle-turnpair-v1",
        "is_final": true,
        "error": {
            "code": error.code(),
            "message": error.public_message(),
            "path": error.path()
        },
        "message": error.public_message()
    })
}

#[derive(Debug, Deserialize)]
struct CancelRequest {
    #[serde(default = "default_api_version")]
    api_version: String,
    request_id: Option<String>,
    state_id: Option<String>,
}

fn default_api_version() -> String {
    API_VERSION.to_owned()
}

fn handle_cancel(state: &HttpState, body: &[u8]) -> Result<Value, SolverError> {
    let request: CancelRequest = serde_json::from_slice(body)?;
    if request.api_version != API_VERSION {
        return Err(SolverError::schema(
            "request.api_version",
            format!("expected {API_VERSION:?}"),
        ));
    }
    if request.request_id.as_deref().is_none_or(str::is_empty)
        && request.state_id.as_deref().is_none_or(str::is_empty)
    {
        return Err(SolverError::schema(
            "request",
            "必须提供 request_id 或 state_id",
        ));
    }
    let mut cancelled = Vec::new();
    let active = state
        .active
        .lock()
        .map_err(|_| SolverError::Http("active solve lock was poisoned".to_owned()))?;
    let now = Instant::now();
    let mut tombstones = state
        .cancelled_before_start
        .lock()
        .map_err(|_| SolverError::Http("cancel tombstone lock was poisoned".to_owned()))?;
    tombstones.retain(|_, created| now.duration_since(*created) <= CANCEL_TOMBSTONE_TTL);
    for (active_request_id, solve) in active.iter() {
        let matches_request = request
            .request_id
            .as_deref()
            .is_some_and(|value| value == active_request_id);
        let matches_state = request
            .state_id
            .as_deref()
            .is_some_and(|value| value == solve.state_id)
            && request.request_id.as_deref().is_none_or(str::is_empty);
        if matches_request || matches_state {
            solve.cancel.store(true, Ordering::Relaxed);
            cancelled.push(active_request_id.clone());
        }
    }
    if cancelled.is_empty()
        && let Some(request_id) = request
            .request_id
            .as_deref()
            .filter(|value| !value.is_empty())
    {
        tombstones.insert(request_id.to_owned(), now);
        cancelled.push(request_id.to_owned());
    }
    cancelled.sort();
    Ok(json!({
        "api_version": API_VERSION,
        "status": if cancelled.is_empty() { "not_found" } else { "cancellation_requested" },
        "cancelled_request_ids": cancelled
    }))
}

fn handle_observe(state: &HttpState, body: &[u8]) -> Result<Value, SolverError> {
    let request: Value = serde_json::from_slice(body)?;
    let outcome = state.training_log.append_observation(request)?;
    let is_result = outcome.kind == "result";
    let mut response = json!({
        "api_version": API_VERSION,
        "status": if outcome.duplicate { "duplicate" } else { "ok" },
        "kind": outcome.kind,
        "state_id": outcome.state_id,
        "logged": outcome.logged,
        "message": if outcome.logged {
            "本次观察已写入脱敏训练日志。"
        } else if state.training_log.enabled() {
            "训练日志暂时写入失败；求解服务仍可继续使用。"
        } else {
            "训练日志已关闭；本次观察已安全忽略。"
        }
    });
    if is_result {
        response["duplicate"] = json!(outcome.duplicate);
        response["result_id"] = json!(outcome.result_id);
        response["game_id"] = json!(outcome.game_id);
        response["result"] = json!(outcome.result);
    }
    Ok(response)
}

fn handle_behavior(state: &HttpState, body: &[u8]) -> Result<Value, (u16, Value)> {
    let request: Value = serde_json::from_slice(body).map_err(|_| {
        (
            400,
            error_payload("invalid_json", "行为请求不是有效的 JSON 数据。", "behavior"),
        )
    })?;
    let outcome = state
        .behavior_log
        .append(request)
        .map_err(|error| behavior_error_response(&error))?;
    Ok(json!({
        "api_version": API_VERSION,
        "status": if !state.behavior_log.enabled() {
            "disabled"
        } else if outcome.duplicate {
            "duplicate"
        } else {
            "ok"
        },
        "logged": outcome.logged,
        "duplicate": outcome.duplicate,
        "behavior_id": outcome.behavior_id,
        "game_id": outcome.game_id,
        "behavior_sequence": outcome.behavior_sequence,
        "behavior_eligible": outcome.behavior_eligible,
        "rl_training_eligible": false,
        "message": if outcome.logged {
            "本次双方行为证据已写入独立脱敏日志。"
        } else if outcome.duplicate {
            "本次行为证据已经记录；重试未产生重复行。"
        } else if state.behavior_log.enabled() {
            "行为日志暂时未写入；请检查健康状态。"
        } else {
            "训练日志已关闭；双方行为日志也已同步关闭。"
        }
    }))
}

fn with_official_card_pool_coverage(mut payload: Value, assessment: &Value) -> Value {
    if let Some(coverage) = payload.get_mut("coverage").and_then(Value::as_object_mut) {
        coverage.insert("official_card_pool".to_owned(), assessment.clone());
    }
    payload
}

fn handle_solve(
    state: &Arc<HttpState>,
    body: &[u8],
    request_started: Instant,
) -> Result<Value, (u16, Value)> {
    let raw: Value = serde_json::from_slice(body).map_err(|error| {
        let error = SolverError::Json(error);
        solver_error_response(&error)
    })?;
    let is_raw_hdt = raw
        .get("state")
        .and_then(Value::as_object)
        .is_some_and(|snapshot| {
            snapshot.contains_key("player")
                && snapshot.contains_key("opponent")
                && !snapshot.contains_key("friendly")
        });
    let mut request =
        solve_request_from_value(raw).map_err(|error| solver_error_response(&error))?;
    // Historical decision frames are already stored in canonical solver shape,
    // but their complete HDT option portfolio still proves the request came
    // through the HDT decision path.  Keep hash-bound public rules enabled for
    // both live raw-HDT snapshots and those canonical offline replays.
    let uses_hdt_visible_rules = is_raw_hdt || request.hdt_root_candidates.is_some();
    let rule_assessment = if uses_hdt_visible_rules {
        Some(
            apply_embedded_rules(&mut request.state)
                .map_err(|error| solver_error_response(&error))?,
        )
    } else {
        None
    };
    let template_rule_assessment = if uses_hdt_visible_rules {
        Some(
            apply_embedded_template_rules(&mut request.state)
                .map_err(|error| solver_error_response(&error))?,
        )
    } else {
        None
    };
    let generation_rule_assessment = if uses_hdt_visible_rules {
        Some(
            apply_embedded_generation_rules(&mut request.state)
                .map_err(|error| solver_error_response(&error))?,
        )
    } else {
        None
    };
    let pool_effect_resolution = state
        .official_card_pools
        .resolve_state_effect_pools(&mut request.state);
    let request_id = request.request_id.to_string();
    let state_id = request.state.state_id.to_string();
    let mut official_card_pool_assessment = state.official_card_pools.assess_state(&request.state);
    if let Some(coverage) = official_card_pool_assessment.as_object_mut() {
        coverage.insert(
            "text_template_rule_application".to_owned(),
            template_rule_assessment.as_ref().map_or_else(
                || json!({"ruleset_id": TEMPLATE_RULESET_ID, "applied": false}),
                |assessment| json!(assessment),
            ),
        );
        coverage.insert(
            "generation_rule_application".to_owned(),
            generation_rule_assessment.as_ref().map_or_else(
                || json!({"ruleset_id": GENERATION_RULESET_ID, "applied": false}),
                |assessment| json!(assessment),
            ),
        );
        coverage.insert("effect_pool_resolution".to_owned(), pool_effect_resolution);
    }
    let log_request = request.clone();
    let cancellation = Arc::new(AtomicBool::new(false));
    {
        let mut active = state.active.lock().map_err(|_| {
            (
                500,
                error_payload("internal_error", "求解器发生内部错误，请稍后重试。", ""),
            )
        })?;
        let mut tombstones = state.cancelled_before_start.lock().map_err(|_| {
            (
                500,
                error_payload("internal_error", "求解器发生内部错误，请稍后重试。", ""),
            )
        })?;
        if active.contains_key(&request_id) {
            return Err((
                409,
                error_payload(
                    "duplicate_request",
                    format!("请求 {request_id:?} 正在处理中，请勿重复提交。"),
                    "",
                ),
            ));
        }
        let now = Instant::now();
        tombstones.retain(|_, created| now.duration_since(*created) <= CANCEL_TOMBSTONE_TTL);
        if tombstones.remove(&request_id).is_some() {
            cancellation.store(true, Ordering::Relaxed);
        }
        active.insert(
            request_id.clone(),
            ActiveSolve {
                state_id: state_id.clone(),
                cancel: Arc::clone(&cancellation),
            },
        );
    }
    if cancellation.load(Ordering::Relaxed) {
        if let Ok(mut active) = state.active.lock() {
            active.remove(&request_id);
        }
        let payload = with_official_card_pool_coverage(
            cancelled_solve_payload(&request_id, &state_id),
            &official_card_pool_assessment,
        );
        state.training_log.append_solve(&log_request, &payload);
        return Ok(payload);
    }
    let _solve_guard = match state.solve_gate.lock() {
        Ok(guard) => guard,
        Err(_) => {
            if let Ok(mut active) = state.active.lock() {
                active.remove(&request_id);
            }
            let error = SolverError::Http("solve gate is unavailable".to_owned());
            let failed = with_official_card_pool_coverage(
                failed_solve_payload(&log_request, &error),
                &official_card_pool_assessment,
            );
            state.training_log.append_solve(&log_request, &failed);
            return Err((
                500,
                error_payload("internal_error", "求解器发生内部错误，请稍后重试。", ""),
            ));
        }
    };
    let result = live_solve_result_started_with_models(
        request,
        uses_hdt_visible_rules,
        rule_assessment.as_ref(),
        &cancellation,
        request_started,
        state.behavior_prior.model(),
        state.decision_ranker.model(),
    );
    if let Ok(mut active) = state.active.lock() {
        active.remove(&request_id);
    }
    match result {
        Ok(payload) => {
            let payload = with_official_card_pool_coverage(payload, &official_card_pool_assessment);
            state.training_log.append_solve(&log_request, &payload);
            Ok(payload)
        }
        Err(SolverError::Cancelled) => {
            let payload = with_official_card_pool_coverage(
                cancelled_solve_payload(&request_id, &state_id),
                &official_card_pool_assessment,
            );
            state.training_log.append_solve(&log_request, &payload);
            Ok(payload)
        }
        Err(error) => {
            let failed = with_official_card_pool_coverage(
                failed_solve_payload(&log_request, &error),
                &official_card_pool_assessment,
            );
            state.training_log.append_solve(&log_request, &failed);
            Err(solver_error_response(&error))
        }
    }
}

fn handle_request(mut request: Request, state: Arc<HttpState>) {
    let request_started = Instant::now();
    if !authorized(&request, &state.session_token) {
        let _ = request.respond(json_response(
            401,
            &error_payload("unauthorized", "需要有效的本地会话令牌。", ""),
        ));
        return;
    }
    let method = request.method().clone();
    let path = request
        .url()
        .split('?')
        .next()
        .unwrap_or(request.url())
        .to_owned();
    if method == Method::Get && path == "/v1/health" {
        let _ = request.respond(json_response(200, &state.health()));
        return;
    }
    if method != Method::Post {
        let _ = request.respond(json_response(
            404,
            &error_payload("not_found", "请求的接口不存在。", ""),
        ));
        return;
    }
    let body = match read_body(&mut request, state.max_request_bytes) {
        Ok(body) => body,
        Err((status, payload)) => {
            let _ = request.respond(json_response(status, &payload));
            return;
        }
    };
    let outcome = match path.as_str() {
        "/v1/solve" => handle_solve(&state, &body, request_started),
        "/v1/cancel" => handle_cancel(&state, &body).map_err(|error| solver_error_response(&error)),
        "/v1/observe" => {
            handle_observe(&state, &body).map_err(|error| solver_error_response(&error))
        }
        "/v1/behavior" => handle_behavior(&state, &body),
        _ => Err((404, error_payload("not_found", "请求的接口不存在。", ""))),
    };
    let (status, payload) = match outcome {
        Ok(payload) => (200, payload),
        Err(error) => error,
    };
    let _ = request.respond(json_response(status, &payload));
}

pub fn serve(options: ServeOptions) -> Result<(), SolverError> {
    if options.host != "127.0.0.1" {
        return Err(SolverError::schema(
            "serve.host",
            "must be exactly 127.0.0.1",
        ));
    }
    if options.session_token.len() < 16 {
        return Err(SolverError::schema(
            "serve.session_token",
            "must contain at least 16 characters",
        ));
    }
    if options.max_request_bytes < 1024 {
        return Err(SolverError::schema(
            "serve.max_request_bytes",
            "must be at least 1024",
        ));
    }
    let server = Arc::new(
        Server::http((options.host.as_str(), options.port))
            .map_err(|error| SolverError::Http(error.to_string()))?,
    );
    let training_log = TrainingLogger::new(options.training_log_path.clone());
    let behavior_log = BehaviorLogger::for_training_log_path(options.training_log_path.as_deref())
        .map_err(|error| SolverError::schema(error.path(), error.code()))?;
    let behavior_prior = BehaviorPriorManager::new(options.behavior_prior_path.clone());
    let behavior_prior_health = behavior_prior.health_payload();
    let decision_ranker = DecisionRankerManager::new(options.decision_ranker_path.clone());
    let decision_ranker_health = decision_ranker.health_payload();
    let official_card_pools =
        OfficialCardPoolBundle::load_optional(options.official_card_pool_path.as_deref(), None);
    let official_card_pool_health = official_card_pools.health_payload();
    let generation_rule_health = embedded_generation_rule_bundle()
        .map(|bundle| {
            json!({
                "available": true,
                "ruleset_id": GENERATION_RULESET_ID,
                "stochastic_card_count": bundle.inventory_count(),
                "runtime_ready_count": bundle.runtime_ready_count(),
                "explicit_manual_queue_count": bundle.manual_queue_count()
            })
        })
        .unwrap_or_else(|error| {
            json!({
                "available": false,
                "ruleset_id": GENERATION_RULESET_ID,
                "error": error.public_message()
            })
        });
    let template_rule_health = embedded_template_rule_bundle()
        .map(|bundle| {
            json!({
                "available": true,
                "ruleset_id": TEMPLATE_RULESET_ID,
                "runtime_effect_coverage": "generic",
                "exact_claim_allowed": false,
                "compiled_generic_rule_count": bundle.rule_count(),
                "unique_official_cards": bundle.unique_official_cards(),
                "uncompiled_card_count": bundle.uncompiled_cards()
            })
        })
        .unwrap_or_else(|error| {
            json!({
                "available": false,
                "ruleset_id": TEMPLATE_RULESET_ID,
                "error": error.public_message()
            })
        });
    let state = Arc::new(HttpState {
        session_token: options.session_token,
        max_request_bytes: options.max_request_bytes,
        active: Mutex::new(HashMap::new()),
        cancelled_before_start: Mutex::new(HashMap::new()),
        solve_gate: Mutex::new(()),
        training_log: training_log.clone(),
        behavior_log: behavior_log.clone(),
        behavior_prior,
        decision_ranker,
        official_card_pools,
    });
    println!(
        "{}",
        serde_json::to_string(&json!({
            "event": "ready",
            "host": options.host,
            "port": options.port,
            "api_version": API_VERSION,
            "model_version": "rust-turnpair-v1",
            "training_log_enabled": training_log.enabled(),
            "training_log_healthy": training_log.healthy(),
            "behavior_log_enabled": behavior_log.enabled(),
            "behavior_log_healthy": behavior_log.healthy(),
            "behavior_prior": behavior_prior_health,
            "decision_ranker": decision_ranker_health,
            "official_card_pools": official_card_pool_health,
            "text_template_card_rules": template_rule_health,
            "generation_card_rules": generation_rule_health
        }))?
    );
    for request in server.incoming_requests() {
        let state = Arc::clone(&state);
        thread::spawn(move || handle_request(request, state));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write as _;
    use std::net::TcpStream;
    use std::path::PathBuf;
    use std::sync::atomic::AtomicU64;
    use std::time::Duration;

    static LOG_SEQUENCE: AtomicU64 = AtomicU64::new(0);

    struct TestLogDirectory(PathBuf);

    impl TestLogDirectory {
        fn new(label: &str) -> Self {
            let sequence = LOG_SEQUENCE.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "metacompanion-rust-http-{label}-{}-{sequence}",
                std::process::id()
            ));
            fs::create_dir_all(&path).expect("create temporary log directory");
            Self(path)
        }

        fn path(&self) -> PathBuf {
            self.0.join(crate::training_log::TRAINING_LOG_FILENAME)
        }

        fn behavior_path(&self) -> PathBuf {
            self.0.join(crate::behavior::BEHAVIOR_LOG_FILENAME)
        }
    }

    impl Drop for TestLogDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn http_state(training_log: TrainingLogger) -> Arc<HttpState> {
        Arc::new(HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log,
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        })
    }

    fn logged_solve_body(request_id: &str, stealth: bool) -> Vec<u8> {
        let board = if stealth {
            json!([{
                "entity_id": "ours",
                "card_type": "MINION",
                "attack": 1,
                "health": 1,
                "can_attack": true,
                "stealth": true
            }])
        } else {
            json!([])
        };
        serde_json::to_vec(&json!({
            "api_version": API_VERSION,
            "request_id": request_id,
            "state": {
                "state_id": "logged-state",
                "turn": 1,
                "active_player_id": "friendly",
                "perspective_player_id": "friendly",
                "friendly": {
                    "player_id": "friendly",
                    "hero": {"entity_id": "friendly-hero", "card_type": "HERO", "health": 30},
                    "board": board
                },
                "opponent": {
                    "player_id": "opponent",
                    "hero": {"entity_id": "opponent-hero", "card_type": "HERO", "health": 30}
                },
                "patch": "31.6",
                "mode": "standard",
                "metadata": {
                    "game_id": "private-http-game",
                    "snapshot_state_hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "snapshot_sequence": 1,
                    "adapter": "native-v1"
                }
            },
            "options": {
                "time_budget_ms": 50,
                "max_iterations": 100,
                "max_depth": 4,
                "top_k": 1,
                "allow_approximate_effects": !stealth
            },
            "metadata": {
                "trajectory_schema": crate::training_log::TRAJECTORY_SCHEMA_ID,
                "decision_id": "logged-state",
                "solve_stage": "single",
                "snapshot_sequence": "1",
                "capture_contract": "unit-http-v1"
            }
        }))
        .expect("serialize solve body")
    }

    fn behavior_body(sequence: u64) -> Vec<u8> {
        serde_json::to_vec(&json!({
            "schema": crate::behavior::BEHAVIOR_SCHEMA_ID,
            "game_id": "private-http-behavior-game",
            "behavior_sequence": sequence,
            "observed_at_utc": format!("2026-07-31T12:00:{:02}+08:00", sequence % 60),
            "actor_side": "local",
            "actor_player_id": "friendly",
            "actor_evidence": "active_player",
            "identity_status": "event_only",
            "visibility_status": "public_pre_state",
            "boundary_status": "isolated",
            "source_event": "turn_passed_to_opponent",
            "action": {
                "kind": "end_turn",
                "source_entity_id": "",
                "target_entity_id": "",
                "card_id": ""
            },
            "pre_state": {
                "state_id": format!("behavior-state-{sequence}"),
                "turn": 1,
                "active_player_id": "friendly",
                "perspective_player_id": "friendly",
                "friendly": {
                    "player_id": "private-one",
                    "hero": {
                        "entity_id": "friendly-hero",
                        "card_id": "FRIENDLY_HERO",
                        "card_type": "HERO",
                        "name": "private friendly name"
                    },
                    "hand": [],
                    "board": []
                },
                "opponent": {
                    "player_id": "private-two",
                    "hero": {
                        "entity_id": "opponent-hero",
                        "card_id": "OPPONENT_HERO",
                        "card_type": "HERO",
                        "name": "private opponent name"
                    },
                    "hand": [{
                        "entity_id": "hidden-card",
                        "card_id": "SECRET_CARD",
                        "card_type": "SPELL",
                        "name": "private hidden card"
                    }],
                    "board": []
                },
                "patch": "31.6",
                "mode": "standard",
                "metadata": {
                    "password": "never-write-password",
                    "raw_power_log": "never-write-raw-line"
                }
            },
            "post_state": null,
            "behavior_eligible": false,
            "rl_training_eligible": false
        }))
        .expect("serialize behavior body")
    }

    #[test]
    fn token_comparison_checks_length_and_content() {
        assert!(constant_time_eq(b"0123456789abcdef", b"0123456789abcdef"));
        assert!(!constant_time_eq(b"0123456789abcdef", b"0123456789abcdeg"));
        assert!(!constant_time_eq(b"short", b"shorter"));
    }

    #[test]
    fn solve_route_rejects_impossible_remaining_attack_snapshot() {
        let session_token = "0123456789abcdef";
        let server = Arc::new(Server::http("127.0.0.1:0").expect("test HTTP server"));
        let address = server.server_addr().to_ip().expect("IP listen address");
        let state = Arc::new(HttpState {
            session_token: session_token.to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        });
        let server_thread = {
            let server = Arc::clone(&server);
            let state = Arc::clone(&state);
            thread::spawn(move || {
                let request = server.recv().expect("test request");
                handle_request(request, state);
            })
        };

        // A non-windfury minion can never have thirteen remaining attacks. The
        // route must reject this snapshot before exact or partial search. The
        // legal depth-limit branch is covered by turnpair::depth_truncation_...
        let body = r#"{
          "request_id":"depth-limit-http",
          "state":{
            "state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
            "friendly":{
              "player_id":"f",
              "hero":{"entity_id":"fh","card_type":"HERO","health":30},
              "board":[{
                "entity_id":"many-attacks","card_type":"MINION","attack":1,"health":1,
                "can_attack":true,"attacks_remaining":13
              }]
            },
            "opponent":{
              "player_id":"o",
              "hero":{"entity_id":"oh","card_type":"HERO","health":30}
            }
          }
        }"#;
        let mut stream = TcpStream::connect(address).expect("connect to test HTTP server");
        stream
            .set_read_timeout(Some(Duration::from_secs(5)))
            .expect("set read timeout");
        write!(
            stream,
            "POST /v1/solve HTTP/1.1\r\nHost: {address}\r\nAuthorization: Bearer {session_token}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        )
        .expect("write request");
        stream.flush().expect("flush request");
        let mut response = String::new();
        stream
            .read_to_string(&mut response)
            .expect("read HTTP response");
        server_thread.join().expect("server thread");

        assert_eq!(
            response
                .lines()
                .next()
                .and_then(|line| line.split_whitespace().nth(1)),
            Some("422")
        );
        assert!(response.contains(r#""code":"unsupported_scope""#));
        assert!(response.contains("当前局面包含尚未支持"));
    }

    #[test]
    fn state_limit_maps_to_compatibility_response() {
        let (status, payload) = solver_error_response(&SolverError::StateLimit(20_000));
        assert_eq!(status, 422);
        assert_eq!(payload["error"]["code"], "state_limit");
        assert_eq!(
            payload["error"]["message"],
            "局面搜索量超过安全上限，请缩小局面或稍后重试。"
        );
    }

    #[test]
    fn time_limit_maps_to_a_chinese_fail_closed_response() {
        let (status, payload) = solver_error_response(&SolverError::TimeLimit);
        assert_eq!(status, 422);
        assert_eq!(payload["error"]["code"], "time_limit_reached");
        assert_eq!(
            payload["error"]["message"],
            "局面搜索达到本次时间上限，未把截断结果标记为完整。"
        );
    }

    #[test]
    fn expired_live_budget_returns_baselines_without_proof_claims() {
        let mut request: SolveRequest = serde_json::from_str(
            r#"{
              "request_id":"expired-budget",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"ours","card_type":"MINION","attack":2,"health":2,"can_attack":true}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}},
              "options":{"time_budget_ms":1,"max_iterations":100,"max_depth":8,"top_k":10}
            }"#,
        )
        .expect("request JSON");
        request.validate().expect("valid request");
        let started = Instant::now()
            .checked_sub(Duration::from_millis(10))
            .expect("representable earlier instant");
        let payload =
            live_solve_result_started(request, true, None, &AtomicBool::new(false), started)
                .expect("honest visible fallback");

        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(payload["coverage"]["scoped_lethal"], false);
        assert_eq!(payload["coverage"]["time_limit_reached"], true);
        assert_eq!(
            payload["coverage"]["counterplay"]["time_limit_reached"],
            true
        );
        let recommendations = payload["recommendations"]
            .as_array()
            .expect("baseline recommendations");
        assert!(!recommendations.is_empty());
        assert_eq!(
            recommendations.len() as u64,
            payload["coverage"]["counterplay"]["generated_first_action_count"]
                .as_u64()
                .expect("modeled root count")
        );
        assert!(recommendations.iter().all(|item| {
            item.get("is_response_verified").is_none()
                && item.get("response_search_complete").is_none()
                && item.get("is_proven_lethal").is_none()
                && item["alternative_kind"] == "fallback"
        }));
        assert!(
            payload["warnings"]
                .as_array()
                .expect("warnings")
                .iter()
                .any(|warning| warning
                    .as_str()
                    .is_some_and(|text| text.contains("时间上限")))
        );
    }

    #[test]
    fn node_budget_is_shared_across_exact_scoped_and_visible_stages() {
        let mut request: SolveRequest = serde_json::from_str(
            r#"{
              "request_id":"shared-node-budget",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"ours","card_type":"MINION","attack":2,"health":2,"can_attack":true}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}},
              "options":{"time_budget_ms":1000,"max_iterations":1,"max_depth":8,"top_k":10}
            }"#,
        )
        .expect("request JSON");
        request.validate().expect("valid request");
        let payload = live_solve_result(request, true, None, &AtomicBool::new(false))
            .expect("bounded visible fallback");

        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["iterations"], 1);
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(
            payload["coverage"]["counterplay"]["node_limit_reached"],
            true
        );
        assert!(
            payload["recommendations"]
                .as_array()
                .is_some_and(|items| !items.is_empty())
        );
    }

    #[test]
    fn hdt_root_missing_from_independent_generation_is_still_evaluated_as_partial() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "hdt-supplied-missing-root",
            "state": {
                "state_id": "s",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "friendly": {
                    "player_id": "f",
                    "hero": {"entity_id": "fh", "card_id": "F_HERO", "card_type": "HERO", "health": 30},
                    "hand": [{
                        "entity_id": "m",
                        "card_id": "MODELED_MINION",
                        "card_type": "MINION",
                        "cost": 0,
                        "attack": 3,
                        "health": 2,
                        "playable": false
                    }],
                    "mana": 0,
                    "max_mana": 0
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {"entity_id": "oh", "card_id": "O_HERO", "card_type": "HERO", "health": 30}
                }
            },
            "options": {"top_k": 10, "max_depth": 8, "max_iterations": 10000},
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "s",
                "frame_id": 9,
                "collector_epoch": 3,
                "frame_watermark": 17,
                "candidate_set_complete": true,
                "candidates": [
                    {
                        "option_id": 0,
                        "action": {"kind": "end_turn"},
                        "target_evidence": "not_applicable",
                        "position_evidence": "not_applicable"
                    },
                    {
                        "option_id": 1,
                        "action": {
                            "kind": "play_card",
                            "source_entity_id": "m",
                            "card_id": "MODELED_MINION",
                            "board_position": 1
                        },
                        "target_evidence": "hdt_no_legal_target",
                        "position_evidence": "core_board_slots_v1"
                    }
                ]
            }
        }))
        .expect("request JSON");
        request.validate().expect("valid HDT-bound request");

        let payload = live_solve_result(request, true, None, &AtomicBool::new(false))
            .expect("HDT-root visible fallback");
        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(
            payload["coverage"]["independent_generated_root_coverage"]["generated_action_ids"],
            json!(["end_turn"])
        );
        assert_eq!(
            payload["coverage"]["independent_generated_root_coverage"]["hdt_recall"],
            0.5
        );
        assert_eq!(
            payload["coverage"]["independent_generated_root_coverage"]["exact_match"],
            false
        );
        assert_eq!(
            payload["coverage"]["independent_generated_root_coverage"]["false_exact"],
            false
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["candidate_count"],
            2
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_count"],
            2
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_coverage"],
            1.0
        );
        let recommendation_roots = payload["recommendations"]
            .as_array()
            .expect("recommendations")
            .iter()
            .map(|item| {
                item["actions"][0]["action_id"]
                    .as_str()
                    .expect("root action")
            })
            .collect::<BTreeSet<_>>();
        assert_eq!(
            recommendation_roots,
            BTreeSet::from(["end_turn::", "play_card:m::position=1"])
        );
        assert_eq!(
            payload["coverage"]["counterplay"]["legal_first_action_basis"],
            "hdt_complete_main_action_options_v1"
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["rl_training_eligible"],
            false
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["global_optimality_verified"],
            false
        );
    }

    #[test]
    fn canonical_hdt_replay_enables_hash_bound_rules_and_evaluates_the_root() {
        let state = http_state(TrainingLogger::disabled());
        let body = serde_json::to_vec(&json!({
            "api_version": API_VERSION,
            "request_id": "canonical-hdt-rule-replay",
            "state": {
                "state_id": "canonical-hdt-state",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "friendly": {
                    "player_id": "f",
                    "hero": {
                        "entity_id": "fh",
                        "card_id": "F_HERO",
                        "card_type": "HERO",
                        "health": 30,
                        "current_health": 30
                    },
                    "hand": [{
                        "entity_id": "arcane-shot",
                        "card_id": "CORE_DS1_185",
                        "name": "Arcane Shot",
                        "card_type": "SPELL",
                        "cost": 1,
                        "playable": true,
                        "english_text": "Deal $2 damage.",
                        "unsupported_effects": ["card_text_not_parsed"],
                        "effect_coverage": "unsupported"
                    }],
                    "mana": 1,
                    "max_mana": 1
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {
                        "entity_id": "oh",
                        "card_id": "O_HERO",
                        "card_type": "HERO",
                        "health": 30,
                        "current_health": 30
                    }
                }
            },
            "options": {
                "time_budget_ms": 1000,
                "max_iterations": 10000,
                "max_depth": 8,
                "top_k": 10,
                "allow_approximate_effects": true
            },
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "canonical-hdt-state",
                "frame_id": 12,
                "collector_epoch": 3,
                "frame_watermark": 22,
                "candidate_set_complete": true,
                "candidates": [
                    {
                        "option_id": 0,
                        "action": {"kind": "end_turn"},
                        "target_evidence": "not_applicable",
                        "position_evidence": "not_applicable"
                    },
                    {
                        "option_id": 1,
                        "action": {
                            "kind": "play_card",
                            "source_entity_id": "arcane-shot",
                            "target_entity_id": "oh",
                            "card_id": "CORE_DS1_185"
                        },
                        "target_evidence": "hdt_error_none",
                        "position_evidence": "not_applicable"
                    }
                ]
            }
        }))
        .expect("serialize canonical HDT replay");

        let payload =
            handle_solve(&state, &body, Instant::now()).expect("canonical HDT replay should solve");
        assert_eq!(
            payload["coverage"]["structured_card_rules"]["available"],
            true
        );
        assert!(
            payload["coverage"]["structured_card_rules"]["matched"]
                .as_array()
                .is_some_and(|items| items.iter().any(|item| {
                    item["card_id"] == "CORE_DS1_185"
                        && item["rule_id"] == "core-arcane-shot-point-damage-v1"
                }))
        );
        assert!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_action_ids"]
                .as_array()
                .is_some_and(|items| items
                    .iter()
                    .any(|item| { item.as_str() == Some("play_card:arcane-shot:oh") }))
        );
    }

    #[test]
    fn unstructured_hdt_location_root_remains_legal_but_explicitly_omitted() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "hdt-location-omitted",
            "state": {
                "state_id": "s",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "friendly": {
                    "player_id": "f",
                    "hero": {"entity_id": "fh", "card_id": "F_HERO", "card_type": "HERO", "health": 30},
                    "board": [{
                        "entity_id": "loc",
                        "card_id": "PUBLIC_LOCATION",
                        "card_type": "LOCATION",
                        "durability": 2,
                        "current_durability": 2
                    }]
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {"entity_id": "oh", "card_id": "O_HERO", "card_type": "HERO", "health": 30}
                }
            },
            "options": {"top_k": 10},
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "s",
                "frame_id": 10,
                "collector_epoch": 3,
                "frame_watermark": 20,
                "candidate_set_complete": true,
                "candidates": [
                    {
                        "option_id": 0,
                        "action": {"kind": "end_turn"},
                        "target_evidence": "not_applicable",
                        "position_evidence": "not_applicable"
                    },
                    {
                        "option_id": 1,
                        "action": {
                            "kind": "location_activate",
                            "source_entity_id": "loc",
                            "card_id": "PUBLIC_LOCATION"
                        },
                        "target_evidence": "hdt_no_legal_target",
                        "position_evidence": "not_applicable"
                    }
                ]
            }
        }))
        .expect("request JSON");
        request.validate().expect("valid location portfolio");

        let payload = live_solve_result(request, true, None, &AtomicBool::new(false))
            .expect("honest location fallback");
        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(payload["coverage"]["unsupported_count"], 1);
        assert_eq!(
            payload["coverage"]["counterplay"]["omitted_unmodeled_first_action_ids"],
            json!(["location_activate:loc:"])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["legal_action_ids"],
            json!(["end_turn", "location_activate:loc:"])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_action_ids"],
            json!(["end_turn"])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_coverage"],
            0.5
        );
        assert_eq!(
            payload["coverage"]["independent_generated_root_coverage"]["false_exact"],
            false
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["live_policy_eligible"],
            false
        );
    }

    #[test]
    fn reviewed_sanguine_depths_root_is_evaluated_without_relaxing_unknown_locations() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "hdt-reviewed-location",
            "state": {
                "state_id": "s",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "friendly": {
                    "player_id": "f",
                    "hero": {
                        "entity_id": "fh",
                        "card_id": "F_HERO",
                        "card_type": "HERO",
                        "health": 30
                    },
                    "board": [
                        {
                            "entity_id": "loc",
                            "card_id": "CORE_REV_990",
                            "name": "Sanguine Depths",
                            "card_type": "LOCATION",
                            "health": 3,
                            "current_health": 3,
                            "english_text": "[x]Deal 1 damage to a minion and give it +2 Attack.",
                            "unsupported_effects": ["card_text_not_parsed"],
                            "effect_coverage": "unsupported"
                        },
                        {
                            "entity_id": "target",
                            "card_id": "PUBLIC_TARGET",
                            "name": "Public target",
                            "card_type": "MINION",
                            "attack": 1,
                            "health": 3,
                            "current_health": 3
                        }
                    ]
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {
                        "entity_id": "oh",
                        "card_id": "O_HERO",
                        "card_type": "HERO",
                        "health": 30
                    }
                }
            },
            "options": {
                "time_budget_ms": 1000,
                "max_iterations": 10000,
                "max_depth": 8,
                "top_k": 10,
                "allow_approximate_effects": true
            },
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "s",
                "frame_id": 11,
                "collector_epoch": 3,
                "frame_watermark": 21,
                "candidate_set_complete": true,
                "candidates": [
                    {
                        "option_id": 0,
                        "action": {"kind": "end_turn"},
                        "target_evidence": "not_applicable",
                        "position_evidence": "not_applicable"
                    },
                    {
                        "option_id": 1,
                        "action": {
                            "kind": "location_activate",
                            "source_entity_id": "loc",
                            "target_entity_id": "target",
                            "card_id": "CORE_REV_990"
                        },
                        "target_evidence": "hdt_error_none",
                        "position_evidence": "not_applicable"
                    }
                ]
            }
        }))
        .expect("request JSON");
        request
            .validate()
            .expect("valid reviewed Location portfolio");
        let assessment =
            apply_embedded_rules(&mut request.state).expect("apply reviewed Location rule");

        let payload = live_solve_result(request, true, Some(&assessment), &AtomicBool::new(false))
            .expect("reviewed Location solve");
        assert!(
            payload["coverage"]["structured_card_rules"]["matched"]
                .as_array()
                .is_some_and(|items| items.iter().any(|item| {
                    item["card_id"] == "CORE_REV_990"
                        && item["rule_id"] == "core-sanguine-depths-location-v1"
                }))
        );
        assert_eq!(
            payload["coverage"]["counterplay"]["omitted_unmodeled_first_action_ids"],
            json!([])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_action_ids"],
            json!(["end_turn", "location_activate:loc:target"])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_coverage"],
            1.0
        );
        assert_eq!(
            payload["coverage"]["independent_generated_root_coverage"]["false_exact"],
            false
        );
    }

    #[test]
    fn random_card_payload_reports_expectiminimax_range_and_recompute_boundary() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "visible-random-card",
            "state": {
                "state_id": "visible-random-state",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "friendly": {
                    "player_id": "f",
                    "hero": {
                        "entity_id": "fh",
                        "card_id": "F_HERO",
                        "name": "Friendly hero",
                        "card_type": "HERO",
                        "health": 30
                    },
                    "mana": 1,
                    "max_mana": 1,
                    "hand": [{
                        "entity_id": "sleet",
                        "card_id": "CATA_485",
                        "name": "Sleet Storm",
                        "card_type": "SPELL",
                        "cost": 1,
                        "card_text": "[x]Deal $2 damage.\n\u{00a0}Deal $1 damage to a\n\u{00a0}random enemy minion.",
                        "effect_coverage": "unsupported",
                        "unsupported_effects": ["card_text_not_parsed"]
                    }]
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {
                        "entity_id": "oh",
                        "card_id": "O_HERO",
                        "name": "Opponent hero",
                        "card_type": "HERO",
                        "health": 30
                    },
                    "deck_size": 8,
                    "board": [
                        {
                            "entity_id": "small",
                            "card_id": "SMALL",
                            "name": "Small threat",
                            "card_type": "MINION",
                            "attack": 3,
                            "health": 1
                        },
                        {
                            "entity_id": "large",
                            "card_id": "LARGE",
                            "name": "Large threat",
                            "card_type": "MINION",
                            "attack": 1,
                            "health": 3
                        }
                    ]
                }
            },
            "options": {
                "time_budget_ms": 1000,
                "max_iterations": 10000,
                "max_depth": 6,
                "top_k": 10,
                "allow_approximate_effects": true
            }
        }))
        .expect("request JSON");
        request.validate().expect("valid chance request");
        let assessment = apply_embedded_rules(&mut request.state).expect("Sleet Storm rule");
        let payload = live_solve_result(request, true, Some(&assessment), &AtomicBool::new(false))
            .expect("visible chance response");
        assert_eq!(payload["status"], "partial");
        let recommendation = payload["recommendations"]
            .as_array()
            .expect("recommendations")
            .iter()
            .find(|item| item["actions"][0]["action_id"] == "play_card:sleet:oh")
            .expect("Sleet Storm recommendation");
        assert_eq!(
            recommendation["score_kind"],
            "visible_expectiminimax_utility_v1"
        );
        assert_eq!(
            recommendation["value_semantics"],
            "visible_expectiminimax_utility_v1"
        );
        assert_eq!(recommendation["recompute_after_random_outcome"], true);
        assert!(
            recommendation["visible_survival_probability"]["denominator"]
                .as_u64()
                .is_some()
        );
        assert_eq!(
            recommendation["actions"].as_array().expect("actions").len(),
            1
        );
    }

    #[test]
    fn complete_hdt_candidates_can_form_non_optimal_behavior_references() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "behavior-reference-location",
            "state": {
                "state_id": "s",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "patch": "fixture-patch",
                "mode": "standard",
                "friendly": {
                    "player_id": "f",
                    "hero": {"entity_id": "fh", "card_id": "F_HERO", "card_type": "HERO", "health": 30},
                    "board": [{
                        "entity_id": "loc",
                        "card_id": "PUBLIC_LOCATION",
                        "card_type": "LOCATION",
                        "durability": 2,
                        "current_durability": 2
                    }]
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {"entity_id": "oh", "card_id": "O_HERO", "card_type": "HERO", "health": 30}
                }
            },
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "s",
                "frame_id": 11,
                "collector_epoch": 3,
                "frame_watermark": 21,
                "candidate_set_complete": true,
                "candidates": [
                    {
                        "option_id": 0,
                        "action": {"kind": "end_turn"},
                        "target_evidence": "not_applicable",
                        "position_evidence": "not_applicable"
                    },
                    {
                        "option_id": 1,
                        "action": {
                            "kind": "location_activate",
                            "source_entity_id": "loc",
                            "card_id": "PUBLIC_LOCATION"
                        },
                        "target_evidence": "hdt_no_legal_target",
                        "position_evidence": "not_applicable"
                    }
                ]
            }
        }))
        .expect("behavior-reference request JSON");
        request
            .validate()
            .expect("valid behavior-reference request");
        let roots = request.hdt_root_candidates.as_ref().expect("HDT roots");
        let payload = behavior_reference_payload_from_scores(
            roots,
            roots.solver_actions(),
            vec![0.25, 0.75],
            &"a".repeat(64),
            3,
        );

        assert_eq!(payload["available"], true);
        assert_eq!(payload["candidate_set_complete"], true);
        assert_eq!(payload["candidate_count"], 2);
        assert_eq!(payload["ranked_candidate_count"], 2);
        assert_eq!(payload["displayed_reference_count"], 2);
        assert_eq!(
            payload["references"][0]["legal_action_id"],
            "location_activate:loc:"
        );
        assert_eq!(
            payload["references"][0]["action"]["kind"],
            "location_activate"
        );
        assert_eq!(payload["references"][0]["optimality_verified"], false);
        assert_eq!(payload["candidate_generation_allowed"], false);
        assert_eq!(payload["tactical_score_override_allowed"], false);
        assert_eq!(payload["automatic_action_allowed"], false);
        assert_eq!(payload["live_policy_eligible"], false);
        assert_eq!(payload["rl_training_eligible"], false);
        assert_eq!(payload["optimality_verified"], false);
    }

    #[test]
    fn hdt_discounted_root_remains_legal_but_is_omitted_without_guessing_cost() {
        let mut request: SolveRequest = serde_json::from_value(json!({
            "request_id": "hdt-discounted-omitted",
            "state": {
                "state_id": "s",
                "turn": 1,
                "active_player_id": "f",
                "perspective_player_id": "f",
                "friendly": {
                    "player_id": "f",
                    "hero": {"entity_id": "fh", "card_id": "F_HERO", "card_type": "HERO", "health": 30},
                    "hand": [{
                        "entity_id": "discounted",
                        "card_id": "PUBLIC_DISCOUNTED_MINION",
                        "card_type": "MINION",
                        "cost": 3,
                        "attack": 2,
                        "health": 2
                    }],
                    "mana": 1,
                    "max_mana": 1
                },
                "opponent": {
                    "player_id": "o",
                    "hero": {"entity_id": "oh", "card_id": "O_HERO", "card_type": "HERO", "health": 30}
                }
            },
            "options": {"top_k": 10},
            "hdt_root_candidates": {
                "contract": "hdt_complete_main_action_options_v1",
                "state_id": "s",
                "frame_id": 11,
                "collector_epoch": 3,
                "frame_watermark": 21,
                "candidate_set_complete": true,
                "candidates": [
                    {
                        "option_id": 0,
                        "action": {"kind": "end_turn"},
                        "target_evidence": "not_applicable",
                        "position_evidence": "not_applicable"
                    },
                    {
                        "option_id": 1,
                        "action": {
                            "kind": "play_card",
                            "source_entity_id": "discounted",
                            "card_id": "PUBLIC_DISCOUNTED_MINION",
                            "board_position": 1
                        },
                        "target_evidence": "hdt_no_legal_target",
                        "position_evidence": "core_board_slots_v1"
                    }
                ]
            }
        }))
        .expect("request JSON");
        request.validate().expect("valid discounted HDT portfolio");

        let payload = live_solve_result(request, true, None, &AtomicBool::new(false))
            .expect("discounted root must not abort the remaining portfolio");
        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(
            payload["coverage"]["counterplay"]["legal_first_action_ids"],
            json!(["end_turn", "play_card:discounted::position=1"])
        );
        assert_eq!(
            payload["coverage"]["counterplay"]["omitted_unmodeled_first_action_ids"],
            json!(["play_card:discounted::position=1"])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_action_ids"],
            json!(["end_turn"])
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["evaluated_coverage"],
            0.5
        );
        assert_eq!(
            payload["coverage"]["hdt_supplied_root_portfolio_coverage"]["rl_training_eligible"],
            false
        );
    }

    #[test]
    fn health_reports_full_gate_backend_without_claiming_full_rules() {
        let state = HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        };
        let health = state.health();
        assert_eq!(health["is_ready"], true);
        assert_eq!(health["backend"], "rust");
        assert_eq!(health["parity_profile"], "full");
        assert_eq!(health["production_ready"], true);
        assert_eq!(health["capabilities"]["oracle_turn_v1"], true);
        assert_eq!(health["capabilities"]["counterplay_turnpair_v1"], true);
        assert_eq!(health["capabilities"]["visible_response_v1"], true);
        assert_eq!(health["capabilities"]["root_action_portfolio_v1"], true);
        assert_eq!(
            health["capabilities"]["behavior_search_ordering_prior_v1"],
            true
        );
        assert_eq!(health["capabilities"]["hdt_decision_ranker_v1"], true);
        assert_eq!(health["capabilities"]["request_time_budget"], true);
        assert_eq!(health["capabilities"]["shared_iteration_budget"], true);
        assert_eq!(health["capabilities"]["single_solve_concurrency"], true);
        assert_eq!(health["capabilities"]["full_hearthstone_rules"], false);
        assert_eq!(health["behavior_prior"]["status"], "disabled");
        assert_eq!(health["behavior_prior"]["available"], false);
        assert_eq!(health["decision_ranker"]["status"], "disabled");
        assert_eq!(health["decision_ranker"]["available"], false);
        assert_eq!(
            health["decision_ranker"]["candidate_generation_allowed"],
            false
        );
        assert_eq!(health["decision_ranker"]["optimality_verified"], false);
        assert_eq!(health["official_card_pools"]["available"], false);
        assert_eq!(
            health["official_card_pools"]["reason"],
            "snapshot_unavailable"
        );
        assert_eq!(health["official_card_pools"]["rules_coverage"], false);
        assert_eq!(
            health["structured_card_rules"]["matching_contract"],
            MATCHING_CONTRACT
        );
        assert_eq!(
            health["structured_card_rules"]["context_guarded_rule_count"],
            5
        );
        assert_eq!(
            health["structured_card_rules"]["required_mechanic_guarded_rule_count"],
            5
        );
        assert_eq!(health["generation_card_rules"]["available"], true);
        assert_eq!(
            health["generation_card_rules"]["ruleset_id"],
            GENERATION_RULESET_ID
        );
        assert_eq!(
            health["generation_card_rules"]["matching_contract"],
            GENERATION_MATCHING_CONTRACT
        );
        assert_eq!(
            health["generation_card_rules"]["stochastic_card_count"],
            417
        );
        assert_eq!(health["generation_card_rules"]["runtime_ready_count"], 34);
        assert_eq!(
            health["generation_card_rules"]["explicit_manual_queue_count"],
            383
        );
        assert_eq!(
            health["generation_card_rules"]["zone_or_history_fallback_to_current_format"],
            false
        );
        assert_eq!(
            health["capabilities"]["random_and_discover_chance_branches_v1"],
            true
        );
        assert!(
            health["structured_card_rules"]["intrinsic_mechanic_evidence"]
                .as_str()
                .is_some_and(|message| message.contains("LIFESTEAL(685)"))
        );
    }

    #[test]
    fn live_turnpair_response_satisfies_strict_verified_contract() {
        let mut request: SolveRequest = serde_json::from_str(
            r#"{
              "request_id":"strict-contract",
              "state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f",
                "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30,"current_health":3},
                  "board":[{"entity_id":"ours","card_type":"MINION","attack":3,"health":3,"can_attack":true}]},
                "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30},
                  "board":[{"entity_id":"theirs","card_type":"MINION","attack":3,"health":1}]}}
            }"#,
        )
        .expect("request JSON");
        request.options.top_k = Some(3);
        request.validate().expect("valid request");
        let cancel = AtomicBool::new(false);
        let payload = live_solve_result(request, false, None, &cancel).expect("solve");
        assert_eq!(payload["recommendations"].as_array().unwrap().len(), 1);
        let recommendation = &payload["recommendations"][0];
        assert_eq!(recommendation["is_response_verified"], true);
        assert_eq!(recommendation["response_scope"], RESPONSE_SCOPE);
        assert_eq!(recommendation["response_kind"], RESPONSE_KIND);
        assert_eq!(recommendation["response_search_complete"], true);
        assert_eq!(
            recommendation["is_safe_after_response"].as_bool(),
            recommendation["response_is_proven_lethal"]
                .as_bool()
                .map(|value| !value)
        );
        assert_eq!(
            recommendation["opponent_reply"],
            recommendation["opponent_response"]["actions"]
        );
        assert_eq!(
            recommendation["opponent_reply"],
            recommendation["counterplay"]["actions"]
        );
        assert_eq!(
            recommendation["minimax_value"],
            recommendation["opponent_response"]["tactical_value"]
        );
        assert_eq!(recommendation["verified_portfolio_regret"], 0);
        assert_eq!(recommendation["alternative_kind"], "co_optimal");
        let canonical = &payload["coverage"]["details"]["counterplay"];
        assert_eq!(canonical["portfolio_model"], ROOT_ACTION_PORTFOLIO_MODEL);
        assert_eq!(canonical["portfolio_optimality_proven"], true);
        assert_eq!(canonical["legal_first_action_count"], 3);
        assert_eq!(
            canonical["legal_first_action_ids"]
                .as_array()
                .unwrap()
                .len(),
            3
        );
        assert_eq!(canonical["generated_first_action_count"], 3);
        assert_eq!(
            canonical["generated_first_action_ids"],
            canonical["legal_first_action_ids"]
        );
        assert_eq!(canonical["response_verified_first_action_count"], 3);
        assert_eq!(
            canonical["response_verified_first_action_ids"],
            canonical["legal_first_action_ids"]
        );
        assert_eq!(canonical["missing_first_action_ids"], json!([]));
        assert_eq!(canonical["root_action_coverage_complete"], true);
        assert!(
            canonical["assessed_line_count"].as_u64().unwrap()
                > payload["recommendations"].as_array().unwrap().len() as u64
        );
        assert_eq!(payload["coverage"]["counterplay"], *canonical);
        assert_eq!(payload["coverage"]["behavior_prior"]["status"], "disabled");
        assert_eq!(
            payload["coverage"]["behavior_prior"]["ordering_attempt_count"],
            0
        );
        assert_eq!(payload["coverage"]["decision_ranker"]["status"], "disabled");
        assert_eq!(
            payload["coverage"]["decision_ranker"]["ordering_attempt_count"],
            0
        );
        let first_action_ids = payload["recommendations"]
            .as_array()
            .expect("recommendations")
            .iter()
            .map(|item| {
                item["actions"][0]["action_id"]
                    .as_str()
                    .expect("first action")
            })
            .collect::<std::collections::HashSet<_>>();
        assert_eq!(
            first_action_ids.len(),
            payload["recommendations"].as_array().unwrap().len()
        );
        for item in payload["recommendations"].as_array().unwrap() {
            assert_eq!(item["is_safe_after_response"], true);
            if item["alternative_kind"] == "co_optimal" {
                assert_eq!(item["verified_portfolio_regret"], 0);
                assert_eq!(canonical["root_action_coverage_complete"], true);
            }
        }
    }

    #[test]
    fn live_cooptimal_portfolio_returns_two_roots_without_backup_padding() {
        let mut request: SolveRequest = serde_json::from_str(
            r#"{"request_id":"cooptimal-http","state":{"state_id":"s","turn":1,"active_player_id":"f","perspective_player_id":"f","friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30},"board":[{"entity_id":"a","card_type":"MINION","attack":2,"health":1,"can_attack":true},{"entity_id":"b","card_type":"MINION","attack":2,"health":1,"can_attack":true}]},"opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":2,"current_health":2}}},"options":{"top_k":3}}"#,
        )
        .expect("request JSON");
        request.validate().expect("valid request");
        let payload = live_solve_result(request, false, None, &AtomicBool::new(false))
            .expect("co-optimal solve");
        let recommendations = payload["recommendations"]
            .as_array()
            .expect("recommendations");
        assert_eq!(recommendations.len(), 2);
        assert!(recommendations.iter().all(|item| {
            item["alternative_kind"] == "co_optimal"
                && item["verified_portfolio_regret"] == 0
                && item["is_safe_after_response"] == true
        }));
        assert_eq!(
            recommendations
                .iter()
                .map(|item| item["actions"][0]["action_id"].as_str().unwrap())
                .collect::<std::collections::BTreeSet<_>>(),
            std::collections::BTreeSet::from(["attack:a:oh", "attack:b:oh"])
        );
        assert_eq!(
            payload["coverage"]["details"]["counterplay"]["portfolio_optimality_proven"],
            true
        );
    }

    #[test]
    fn raw_hdt_visible_fallback_is_partial_filtered_and_contract_honest() {
        let public_card = |entity_id: i64,
                           card_id: &str,
                           card_type: &str,
                           cost: i64,
                           attack: i64,
                           health: i64,
                           exhausted: bool,
                           text: &str,
                           zone: &str,
                           tags: Value| {
            json!({
                "entity_id": entity_id,
                "card_id": card_id,
                "name": card_id,
                "card_type": card_type,
                "cost": cost,
                "attack": attack,
                "health": health,
                "damage": 0,
                "english_text": text,
                "is_playable_card": true,
                "is_exhausted": exhausted,
                "is_frozen": false,
                "mechanics": [],
                "tags": tags,
                "visibility": "public",
                "zone": zone
            })
        };
        let player = |player_id: i64,
                      hero: Value,
                      hand: Vec<Value>,
                      board: Vec<Value>,
                      hero_power: Option<Value>,
                      deck_count: i64,
                      fatigue: i64| {
            json!({
                "player_id": player_id,
                "max_mana": 5,
                "deck_count": deck_count,
                "fatigue": fatigue,
                "resources": {"available": 5, "total": 5, "spell_power": 0},
                "player_entity": {"entity_id": player_id, "tags": {}},
                "hero": hero,
                "hero_power": hero_power,
                "weapon": null,
                "hand": hand,
                "board": board,
                "deck": [],
                "graveyard": [],
                "secrets": [],
                "set_aside": []
            })
        };
        let hero = |entity_id, card_id| {
            public_card(
                entity_id,
                card_id,
                "HERO",
                0,
                0,
                30,
                true,
                "",
                "PLAY",
                json!({}),
            )
        };
        let mut raw = json!({
            "api_version": "1.0",
            "request_id": "raw-visible-partial",
            "state": {
                "schema_version": 1,
                "state_id": "raw-visible-state",
                "turn_number": 3,
                "active_player": "player",
                "is_local_player_turn": true,
                "player": player(
                    1,
                    hero(10, "HERO_FRIENDLY"),
                    vec![
                        public_card(20, "UNKNOWN_SPELL", "SPELL", 1, 0, 1, true, "Unknown spell.", "HAND", json!({})),
                        public_card(21, "UNKNOWN_WEAPON", "WEAPON", 1, 2, 2, true, "Unknown weapon.", "HAND", json!({})),
                        public_card(22, "UNKNOWN_MINION", "MINION", 1, 2, 2, true, "Unknown battlecry.", "HAND", json!({}))
                    ],
                    vec![
                        public_card(40, "ATTACKER_A", "MINION", 0, 1, 2, false, "", "PLAY", json!({"NUM_ATTACKS_THIS_TURN": 0})),
                        public_card(41, "ATTACKER_B", "MINION", 0, 1, 2, false, "", "PLAY", json!({"NUM_ATTACKS_THIS_TURN": 0}))
                    ],
                    Some(public_card(23, "UNKNOWN_POWER", "HERO_POWER", 2, 0, 1, false, "Unknown power.", "PLAY", json!({"HAS_ACTIVATE_POWER": 1}))),
                    12,
                    0
                ),
                "opponent": player(
                    2,
                    hero(30, "HERO_OPPONENT"),
                    vec![],
                    vec![],
                    None,
                    7,
                    2
                ),
                "unknown_data": [],
                "unsupported_features": []
            },
            "options": {"top_k": 3, "max_iterations": 1, "max_depth": 4, "allow_approximate_effects": true}
        });
        let state = Arc::new(HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        });
        let payload = handle_solve(
            &state,
            &serde_json::to_vec(&raw).expect("raw request"),
            Instant::now(),
        )
        .expect("partial solve");
        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(payload["coverage"]["scoped_lethal"], false);
        assert_eq!(payload["coverage"]["exact_scope"], VISIBLE_RESPONSE_SCOPE);
        assert_eq!(
            payload["coverage"]["official_card_pool"]["available"],
            false
        );
        assert_eq!(
            payload["coverage"]["official_card_pool"]["membership_assessed"],
            false
        );
        assert_eq!(
            payload["coverage"]["official_card_pool"]["enforces_action_legality"],
            false
        );
        let recommendations = payload["recommendations"]
            .as_array()
            .expect("recommendations");
        assert!(recommendations.len() >= 2);
        let first_action_ids = recommendations
            .iter()
            .map(|item| {
                item["actions"][0]["action_id"]
                    .as_str()
                    .expect("first action")
            })
            .collect::<std::collections::BTreeSet<_>>();
        assert_eq!(first_action_ids.len(), recommendations.len());
        for recommendation in recommendations {
            let serialized = serde_json::to_string(&recommendation["actions"]).expect("actions");
            assert!(!serialized.contains("UNKNOWN_SPELL"));
            assert!(!serialized.contains("UNKNOWN_WEAPON"));
            assert!(!serialized.contains("UNKNOWN_POWER"));
            for forbidden in [
                "response_scope",
                "response_kind",
                "response_search_complete",
                "is_response_verified",
                "minimax_value",
                "is_safe_after_response",
                "response_is_proven_lethal",
                "opponent_response",
                "counterplay",
                "proof_kind",
                "proof_scope",
                "is_proven_lethal",
            ] {
                assert!(
                    recommendation.get(forbidden).is_none(),
                    "unexpected {forbidden}"
                );
            }
            assert_eq!(recommendation["verified_portfolio_regret"], Value::Null);
            assert_eq!(recommendation["alternative_kind"], "fallback");
        }
        assert!(recommendations.iter().any(|item| {
            item["approximate_effects"].as_array().is_some_and(|items| {
                items
                    .iter()
                    .any(|value| value.as_str().is_some_and(|text| text.contains("22")))
            })
        }));
        let coverage = &payload["coverage"]["details"]["counterplay"];
        assert_eq!(
            coverage["legal_first_action_ids"],
            coverage["generated_first_action_ids"]
        );
        assert_eq!(coverage["response_verified_first_action_ids"], json!([]));
        assert_eq!(
            coverage["missing_first_action_ids"],
            coverage["legal_first_action_ids"]
        );
        assert_eq!(coverage["root_action_coverage_complete"], false);
        assert_eq!(coverage["portfolio_optimality_proven"], false);
        assert_eq!(coverage["node_limit_reached"], true);
        assert_eq!(
            coverage["legal_first_action_count"].as_u64(),
            coverage["legal_first_action_ids"]
                .as_array()
                .map(|items| items.len() as u64)
        );
        assert_eq!(
            coverage["generated_first_action_count"].as_u64(),
            coverage["generated_first_action_ids"]
                .as_array()
                .map(|items| items.len() as u64)
        );
        assert_eq!(coverage["response_verified_first_action_count"], 0);
        let omitted = serde_json::to_string(&coverage["omitted_unmodeled_first_action_ids"])
            .expect("omitted IDs");
        assert!(omitted.contains("20"));
        assert!(omitted.contains("21"));
        assert!(omitted.contains("23"));

        raw["request_id"] = json!("raw-visible-disabled");
        raw["options"]["allow_approximate_effects"] = json!(false);
        let (status, error) = handle_solve(
            &state,
            &serde_json::to_vec(&raw).expect("strict raw request"),
            Instant::now(),
        )
        .expect_err("explicitly disabled approximation must fail closed");
        assert_eq!(status, 422);
        assert_eq!(error["error"]["code"], "unsupported_scope");
        assert!(error.get("recommendations").is_none());
    }

    #[test]
    fn raw_hdt_equipped_weapon_stays_partial_and_exposes_its_approximate_dependency() {
        let public_entity = |entity_id: i64,
                             card_id: &str,
                             card_type: &str,
                             attack: i64,
                             health: i64,
                             exhausted: bool,
                             tags: Value| {
            json!({
                "entity_id": entity_id,
                "card_id": card_id,
                "name": card_id,
                "card_type": card_type,
                "attack": attack,
                "health": health,
                "damage": 0,
                "is_exhausted": exhausted,
                "is_frozen": false,
                "is_known": true,
                "is_revealed": true,
                "visibility": "public",
                "zone": "PLAY",
                "tags": tags
            })
        };
        let player = |player_id: i64, hero: Value, weapon: Value, deck_count: i64| {
            json!({
                "player_id": player_id,
                "max_mana": 5,
                "deck_count": deck_count,
                "fatigue": 0,
                "resources": {"available": 0, "total": 5, "spell_power": 0},
                "player_entity": {"entity_id": player_id, "tags": {}},
                "hero": hero,
                "hero_power": null,
                "weapon": weapon,
                "hand": [],
                "board": [],
                "deck": [],
                "graveyard": [],
                "secrets": [],
                "set_aside": []
            })
        };
        let mut friendly_weapon =
            public_entity(11, "VISIBLE_WEAPON", "WEAPON", 3, 0, false, json!({}));
        friendly_weapon["durability"] = json!(2);
        friendly_weapon["has_windfury"] = json!(true);
        friendly_weapon["english_text"] = json!("Unknown trigger text is intentionally ignored.");
        let mut opponent_weapon = public_entity(
            31,
            "VISIBLE_COUNTER_WEAPON",
            "WEAPON",
            4,
            0,
            false,
            json!({}),
        );
        opponent_weapon["durability"] = json!(1);
        let mut raw = json!({
            "api_version": "1.0",
            "request_id": "raw-visible-equipped-weapon",
            "state": {
                "schema_version": 1,
                "state_id": "raw-visible-equipped-weapon-state",
                "turn_number": 7,
                "active_player": "player",
                "is_local_player_turn": true,
                "player": player(
                    1,
                    public_entity(
                        10,
                        "HERO_FRIENDLY",
                        "HERO",
                        3,
                        3,
                        false,
                        json!({"NUM_ATTACKS_THIS_TURN": 0})
                    ),
                    friendly_weapon,
                    8
                ),
                "opponent": player(
                    2,
                    public_entity(
                        30,
                        "HERO_OPPONENT",
                        "HERO",
                        4,
                        30,
                        true,
                        json!({"NUM_ATTACKS_THIS_TURN": 0})
                    ),
                    opponent_weapon,
                    9
                ),
                "unknown_data": [],
                "unsupported_features": []
            },
            "options": {
                "top_k": 3,
                "max_iterations": 128,
                "max_depth": 6,
                "allow_approximate_effects": true
            }
        });
        let state = Arc::new(HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        });
        let payload = handle_solve(
            &state,
            &serde_json::to_vec(&raw).expect("raw weapon request"),
            Instant::now(),
        )
        .expect("partial weapon solve");
        assert_eq!(payload["status"], "partial");
        assert_eq!(payload["coverage"]["exact"], false);
        assert_eq!(payload["coverage"]["scoped_lethal"], false);
        assert!(
            payload["coverage"]["details"]["counterplay"]["legal_first_action_ids"]
                .as_array()
                .is_some_and(|ids| ids.iter().any(|id| id == "attack:10:30"))
        );
        let weapon_recommendation = payload["recommendations"]
            .as_array()
            .and_then(|items| {
                items.iter().find(|item| {
                    item["actions"][0]["action_id"]
                        .as_str()
                        .is_some_and(|id| id == "attack:10:30")
                })
            })
            .expect("weapon attack recommendation");
        assert!(
            weapon_recommendation["approximate_effects"]
                .as_array()
                .is_some_and(|items| items.iter().any(|item| {
                    item.as_str().is_some_and(|text| {
                        text.contains("11") && text.contains("仅按当前公开基础数值处理")
                    })
                }))
        );

        raw["request_id"] = json!("raw-visible-equipped-weapon-strict");
        raw["options"]["allow_approximate_effects"] = json!(false);
        let (status, error) = handle_solve(
            &state,
            &serde_json::to_vec(&raw).expect("strict weapon request"),
            Instant::now(),
        )
        .expect_err("strict weapon solve must abstain");
        assert_eq!(status, 422);
        assert_eq!(error["error"]["code"], "unsupported_scope");
        assert!(error.get("recommendations").is_none());
    }

    #[test]
    fn raw_hdt_unknown_alternative_keeps_clean_scoped_lethal() {
        let public_card = |entity_id: i64,
                           card_id: &str,
                           card_type: &str,
                           attack: i64,
                           health: i64,
                           exhausted: bool,
                           text: &str,
                           zone: &str| {
            json!({
                "entity_id": entity_id,
                "card_id": card_id,
                "name": card_id,
                "card_type": card_type,
                "cost": if card_type == "SPELL" { 1 } else { 0 },
                "attack": attack,
                "health": health,
                "damage": 0,
                "english_text": text,
                "is_playable_card": true,
                "is_exhausted": exhausted,
                "is_frozen": false,
                "mechanics": [],
                "tags": {"NUM_ATTACKS_THIS_TURN": 0},
                "visibility": "public",
                "zone": zone
            })
        };
        let player = |player_id: i64, hero: Value, hand: Vec<Value>, board: Vec<Value>| {
            json!({
                "player_id": player_id,
                "max_mana": 1,
                "deck_count": 0,
                "fatigue": 0,
                "resources": {"available": 1, "total": 1, "spell_power": 0},
                "player_entity": {"entity_id": player_id, "tags": {}},
                "hero": hero,
                "hero_power": null,
                "weapon": null,
                "hand": hand,
                "board": board,
                "deck": [],
                "graveyard": [],
                "secrets": [],
                "set_aside": []
            })
        };
        let raw = json!({
            "api_version": "1.0",
            "request_id": "raw-scoped-lethal",
            "state": {
                "schema_version": 1,
                "state_id": "raw-scoped-state",
                "turn_number": 1,
                "active_player": "player",
                "is_local_player_turn": true,
                "player": player(
                    1,
                    public_card(10, "HERO_FRIENDLY", "HERO", 0, 30, true, "", "PLAY"),
                    vec![public_card(
                        20,
                        "EVAL_UNKNOWN_ALTERNATIVE",
                        "SPELL",
                        0,
                        1,
                        true,
                        "Do something unknown.",
                        "HAND"
                    )],
                    vec![public_card(
                        40,
                        "EVAL_ATTACKER",
                        "MINION",
                        3,
                        3,
                        false,
                        "",
                        "PLAY"
                    )]
                ),
                "opponent": player(
                    2,
                    public_card(30, "HERO_OPPONENT", "HERO", 0, 3, true, "", "PLAY"),
                    vec![],
                    vec![]
                ),
                "unknown_data": [],
                "unsupported_features": []
            },
            "options": {"top_k": 3, "allow_approximate_effects": true}
        });
        let state = Arc::new(HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        });
        let payload = handle_solve(
            &state,
            &serde_json::to_vec(&raw).expect("raw request"),
            Instant::now(),
        )
        .expect("scoped solve");
        assert_eq!(payload["coverage"]["scoped_lethal"], true);
        assert_eq!(
            payload["recommendations"][0]["actions"][0]["action_id"],
            "attack:40:30"
        );
        assert_eq!(payload["recommendations"][0]["is_proven_lethal"], true);
        assert_eq!(payload["recommendations"][0]["is_response_verified"], true);
        assert_eq!(
            payload["recommendations"][0]["verified_portfolio_regret"],
            Value::Null
        );
        assert_eq!(
            payload["recommendations"][0]["alternative_kind"],
            "best_found"
        );
        let coverage = &payload["coverage"]["details"]["counterplay"];
        assert_eq!(coverage["root_action_coverage_complete"], false);
        assert_eq!(coverage["portfolio_optimality_proven"], false);
        assert_eq!(coverage["search_complete"], false);
        assert_eq!(coverage["response_line_complete"], true);
        assert_eq!(
            coverage["legal_first_action_count"].as_u64(),
            coverage["legal_first_action_ids"]
                .as_array()
                .map(|items| items.len() as u64)
        );
        assert_eq!(
            coverage["generated_first_action_count"].as_u64(),
            coverage["generated_first_action_ids"]
                .as_array()
                .map(|items| items.len() as u64)
        );
        assert_eq!(
            coverage["response_verified_first_action_count"].as_u64(),
            coverage["response_verified_first_action_ids"]
                .as_array()
                .map(|items| items.len() as u64)
        );
        assert!(
            coverage["legal_first_action_count"].as_u64()
                > coverage["response_verified_first_action_count"].as_u64()
        );
        assert!(
            !coverage["missing_first_action_ids"]
                .as_array()
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn cancellation_prefers_exact_request_id_over_a_stale_state_id() {
        let first = Arc::new(AtomicBool::new(false));
        let second = Arc::new(AtomicBool::new(false));
        let state = HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::from([
                (
                    "request-one".to_owned(),
                    ActiveSolve {
                        state_id: "state-one".to_owned(),
                        cancel: Arc::clone(&first),
                    },
                ),
                (
                    "request-two".to_owned(),
                    ActiveSolve {
                        state_id: "state-two".to_owned(),
                        cancel: Arc::clone(&second),
                    },
                ),
            ])),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        };
        let response = handle_cancel(
            &state,
            br#"{"api_version":"1.0","request_id":"request-one","state_id":"state-two"}"#,
        )
        .expect("cancel request");
        assert_eq!(response["status"], "cancellation_requested");
        assert_eq!(response["cancelled_request_ids"], json!(["request-one"]));
        assert!(first.load(Ordering::Relaxed));
        assert!(!second.load(Ordering::Relaxed));

        let response = handle_cancel(&state, br#"{"api_version":"1.0","state_id":"state-two"}"#)
            .expect("state-only cancel request");
        assert_eq!(response["cancelled_request_ids"], json!(["request-two"]));
        assert!(second.load(Ordering::Relaxed));
    }

    #[test]
    fn cancellation_before_registration_is_consumed_by_the_exact_request() {
        let state = Arc::new(HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        });
        let response = handle_cancel(
            &state,
            br#"{"api_version":"1.0","request_id":"future-request"}"#,
        )
        .expect("pre-registration cancel");
        assert_eq!(response["status"], "cancellation_requested");
        assert_eq!(response["cancelled_request_ids"], json!(["future-request"]));

        let body = br#"{
          "request_id":"future-request",
          "state":{"state_id":"state-one","turn":1,"active_player_id":"f","perspective_player_id":"f",
            "friendly":{"player_id":"f","hero":{"entity_id":"fh","card_type":"HERO","health":30}},
            "opponent":{"player_id":"o","hero":{"entity_id":"oh","card_type":"HERO","health":30}}}
        }"#;
        let payload = handle_solve(&state, body, Instant::now()).expect("cancelled solve payload");
        assert_eq!(payload["status"], "cancelled");
        assert_eq!(payload["is_final"], false);
        assert!(state.active.lock().expect("active lock").is_empty());
        assert!(
            state
                .cancelled_before_start
                .lock()
                .expect("tombstone lock")
                .is_empty()
        );
    }

    #[test]
    fn observation_is_safely_acknowledged_without_logging() {
        let state = HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::disabled(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        };
        let payload = handle_observe(
            &state,
            br#"{"api_version":"1.0","state_id":"state-one","kind":"result","result":"win","metadata":{}}"#,
        )
        .expect("observation response");
        assert_eq!(payload["status"], "ok");
        assert_eq!(payload["kind"], "result");
        assert_eq!(payload["state_id"], "state-one");
        assert_eq!(payload["logged"], false);
    }

    #[test]
    fn behavior_route_derives_independent_path_and_deduplicates_retries() {
        let temporary = TestLogDirectory::new("behavior");
        let training_log = TrainingLogger::new(Some(temporary.path()));
        let behavior_log = BehaviorLogger::for_training_log_path(Some(&temporary.path()))
            .expect("derive behavior path");
        let state = HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log,
            behavior_log,
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        };
        assert_eq!(state.health()["training_log_enabled"], true);
        assert_eq!(state.health()["behavior_log_enabled"], true);
        assert_eq!(state.health()["behavior_log_healthy"], true);

        let body = behavior_body(1);
        let first = handle_behavior(&state, &body).expect("write behavior");
        assert_eq!(first["status"], "ok");
        assert_eq!(first["logged"], true);
        assert_eq!(first["duplicate"], false);
        assert_eq!(first["rl_training_eligible"], false);
        assert!(
            first["game_id"]
                .as_str()
                .is_some_and(|value| value.starts_with("anon-"))
        );
        let retry = handle_behavior(&state, &body).expect("deduplicate behavior retry");
        assert_eq!(retry["status"], "duplicate");
        assert_eq!(retry["logged"], false);
        assert_eq!(retry["duplicate"], true);
        assert_eq!(retry["behavior_id"], first["behavior_id"]);

        let persisted =
            fs::read_to_string(temporary.behavior_path()).expect("read independent behavior log");
        assert_eq!(persisted.lines().count(), 1);
        assert!(!temporary.path().exists());
        assert!(!persisted.contains("SECRET_CARD"));
        assert!(!persisted.contains("never-write"));
        assert!(persisted.contains(r#""rl_training_eligible":false"#));

        let mut producer_hash: Value = serde_json::from_slice(&body).unwrap();
        producer_hash["behavior_id"] = json!("producer-must-not-declare-id");
        let (status, error) = handle_behavior(&state, &serde_json::to_vec(&producer_hash).unwrap())
            .expect_err("producer hash/id must fail");
        assert_eq!(status, 400);
        assert!(
            error["error"]["code"]
                .as_str()
                .is_some_and(|value| value.starts_with("unknown_field"))
        );

        let mut conflict: Value = serde_json::from_slice(&body).unwrap();
        conflict["observed_at_utc"] = json!("2026-07-31T12:00:59+08:00");
        let (status, error) = handle_behavior(&state, &serde_json::to_vec(&conflict).unwrap())
            .expect_err("same sequence with different content must fail");
        assert_eq!(status, 409);
        assert_eq!(error["error"]["code"], "behavior_sequence_conflict");
        assert_eq!(state.health()["behavior_log_healthy"], true);
    }

    #[test]
    fn behavior_http_route_requires_the_local_session_token() {
        let temporary = TestLogDirectory::new("behavior-token");
        let token = "0123456789abcdef";
        let server = Arc::new(Server::http("127.0.0.1:0").expect("test HTTP server"));
        let address = server.server_addr().to_ip().expect("IP listen address");
        let state = Arc::new(HttpState {
            session_token: token.to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::new(Some(temporary.path())),
            behavior_log: BehaviorLogger::new(Some(temporary.behavior_path())),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        });
        let server_thread = {
            let server = Arc::clone(&server);
            let state = Arc::clone(&state);
            thread::spawn(move || {
                for _ in 0..2 {
                    let request = server.recv().expect("test behavior request");
                    handle_request(request, Arc::clone(&state));
                }
            })
        };
        let body = String::from_utf8(behavior_body(1)).expect("UTF-8 behavior body");
        let send = |authorization: Option<&str>| {
            let mut stream = TcpStream::connect(address).expect("connect behavior route");
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .expect("set behavior read timeout");
            let authorization = authorization
                .map(|value| format!("Authorization: Bearer {value}\r\n"))
                .unwrap_or_default();
            write!(
                stream,
                "POST /v1/behavior HTTP/1.1\r\nHost: {address}\r\n{authorization}Content-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            )
            .expect("write behavior request");
            stream.flush().expect("flush behavior request");
            let mut response = String::new();
            stream
                .read_to_string(&mut response)
                .expect("read behavior response");
            response
        };
        let unauthorized = send(None);
        assert_eq!(
            unauthorized
                .lines()
                .next()
                .and_then(|line| line.split_whitespace().nth(1)),
            Some("401")
        );
        assert!(!temporary.behavior_path().exists());
        let authorized = send(Some(token));
        assert_eq!(
            authorized
                .lines()
                .next()
                .and_then(|line| line.split_whitespace().nth(1)),
            Some("200")
        );
        assert!(authorized.contains(r#""logged":true"#));
        server_thread.join().expect("behavior server thread");
        assert_eq!(
            fs::read_to_string(temporary.behavior_path())
                .expect("read authenticated behavior log")
                .lines()
                .count(),
            1
        );
    }

    #[test]
    fn disabling_training_also_disables_behavior_logging() {
        let state = HttpState {
            session_token: "0123456789abcdef".to_owned(),
            max_request_bytes: DEFAULT_MAX_REQUEST_BYTES,
            active: Mutex::new(HashMap::new()),
            cancelled_before_start: Mutex::new(HashMap::new()),
            solve_gate: Mutex::new(()),
            training_log: TrainingLogger::disabled(),
            behavior_log: BehaviorLogger::for_training_log_path(None).unwrap(),
            behavior_prior: BehaviorPriorManager::disabled(),
            decision_ranker: DecisionRankerManager::disabled(),
            official_card_pools: OfficialCardPoolBundle::unavailable(),
        };
        assert_eq!(state.health()["training_log_enabled"], false);
        assert_eq!(state.health()["behavior_log_enabled"], false);
        assert_eq!(state.health()["behavior_log_healthy"], true);
        let response = handle_behavior(&state, &behavior_body(1)).expect("disabled behavior ack");
        assert_eq!(response["status"], "disabled");
        assert_eq!(response["logged"], false);
        assert_eq!(response["duplicate"], false);
        assert!(
            response["behavior_id"]
                .as_str()
                .is_some_and(|value| value.starts_with("behavior-"))
        );
    }

    #[test]
    fn observe_route_writes_anonymized_record_and_health_is_dynamic() {
        let temporary = TestLogDirectory::new("observe");
        let state = http_state(TrainingLogger::new(Some(temporary.path())));
        let health = state.health();
        assert_eq!(health["training_log_enabled"], true);
        assert_eq!(health["training_log_healthy"], true);
        let request = br#"{
              "api_version":"1.0","kind":"result","state_id":"state-post",
              "game_id":"private-http-game","observed_at_utc":"2026-07-31T12:34:56Z",
              "result":"win","metadata":{
                "trajectory_schema":"trajectory-readiness-v1",
                "capture_contract":"terminal_result_v1",
                "completeness":"terminal_result","training_eligible":"true"
              }
            }"#;
        let payload = handle_observe(&state, request).expect("logged observation");
        assert_eq!(payload["kind"], "result");
        assert_eq!(payload["logged"], true);
        assert_eq!(payload["duplicate"], false);
        assert!(
            payload["result_id"]
                .as_str()
                .is_some_and(|value| value.starts_with("result-"))
        );
        let retry = handle_observe(&state, request).expect("idempotent retry");
        assert_eq!(retry["logged"], false);
        assert_eq!(retry["duplicate"], true);
        assert_eq!(retry["result_id"], payload["result_id"]);
        let body = fs::read_to_string(temporary.path()).expect("read observation log");
        assert_eq!(body.lines().count(), 1);
        let record: Value = serde_json::from_str(body.trim()).expect("valid observation record");
        assert_eq!(record["log_schema"], "advisor-training-log-v2");
        assert!(
            record["trajectory"]["game_id"]
                .as_str()
                .is_some_and(|value| value.starts_with("anon-"))
        );
        assert!(record["observation"]["observed_at_utc"].is_null());
        assert_eq!(state.health()["training_log_healthy"], true);

        let conflict = handle_observe(
            &state,
            br#"{
              "api_version":"1.0","kind":"result","state_id":"different-state",
              "game_id":"private-http-game","result":"loss","metadata":{}
            }"#,
        )
        .expect_err("conflicting terminal result must fail closed");
        assert!(matches!(conflict, SolverError::ResultObservationConflict));
        assert_eq!(solver_error_response(&conflict).0, 409);
    }

    #[test]
    fn observe_rejects_float_result_metadata_before_write_but_action_keeps_float() {
        let temporary = TestLogDirectory::new("result-float-metadata");
        let state = http_state(TrainingLogger::new(Some(temporary.path())));
        let error = handle_observe(
            &state,
            br#"{
              "api_version":"1.0","kind":"result","state_id":"terminal-state",
              "game_id":"private-http-game","result":"win",
              "metadata":{"sample_weight":1e-7}
            }"#,
        )
        .expect_err("floating-point terminal metadata must fail closed");
        let (status, payload) = solver_error_response(&error);
        assert_eq!(status, 400);
        assert_eq!(payload["error"]["code"], "schema_error");
        assert_eq!(payload["error"]["path"], "request.metadata.sample_weight");
        assert!(!temporary.path().exists());

        let accepted = handle_observe(
            &state,
            br#"{
              "api_version":"1.0","kind":"action","state_id":"action-state",
              "game_id":"private-http-game",
              "action":{"kind":"end_turn","source_entity_id":"","target_entity_id":"","card_id":""},
              "metadata":{"sample_weight":1e-7}
            }"#,
        )
        .expect("ordinary action metadata still accepts floating point");
        assert_eq!(accepted["logged"], true);
        let record: Value = serde_json::from_str(
            fs::read_to_string(temporary.path())
                .expect("read action observation")
                .trim(),
        )
        .expect("parse action observation");
        assert_eq!(record["observation"]["metadata"]["sample_weight"], 1e-7);
    }

    #[test]
    fn valid_solve_outcomes_are_logged_but_duplicates_are_not() {
        let temporary = TestLogDirectory::new("solve");
        let state = http_state(TrainingLogger::new(Some(temporary.path())));
        let body = logged_solve_body("logged-request", false);
        let payload = handle_solve(&state, &body, Instant::now()).expect("successful solve");
        assert!(matches!(payload["status"].as_str(), Some("ok" | "partial")));
        let lines = fs::read_to_string(temporary.path()).expect("read solve log");
        assert_eq!(lines.lines().count(), 1);
        let record: Value = serde_json::from_str(lines.trim()).expect("valid solve record");
        assert_eq!(record["kind"], "solve");
        assert_eq!(record["result"]["status"], payload["status"]);

        state.active.lock().expect("active lock").insert(
            "duplicate-request".to_owned(),
            ActiveSolve {
                state_id: "logged-state".to_owned(),
                cancel: Arc::new(AtomicBool::new(false)),
            },
        );
        let duplicate = handle_solve(
            &state,
            &logged_solve_body("duplicate-request", false),
            Instant::now(),
        )
        .expect_err("duplicate must fail");
        assert_eq!(duplicate.0, 409);
        let lines = fs::read_to_string(temporary.path()).expect("re-read solve log");
        assert_eq!(lines.lines().count(), 1);
    }

    #[test]
    fn cancelled_and_unsupported_solves_have_stable_log_records() {
        let temporary = TestLogDirectory::new("solve-terminal-status");
        let state = http_state(TrainingLogger::new(Some(temporary.path())));
        state
            .cancelled_before_start
            .lock()
            .expect("tombstone lock")
            .insert("cancelled-request".to_owned(), Instant::now());
        let cancelled = handle_solve(
            &state,
            &logged_solve_body("cancelled-request", false),
            Instant::now(),
        )
        .expect("cancelled response");
        assert_eq!(cancelled["status"], "cancelled");

        let unsupported = handle_solve(
            &state,
            &logged_solve_body("unsupported-request", true),
            Instant::now(),
        )
        .expect_err("stealth canonical state must fail closed");
        assert_eq!(unsupported.0, 422);
        let records = fs::read_to_string(temporary.path())
            .expect("read terminal solve logs")
            .lines()
            .map(|line| serde_json::from_str::<Value>(line).expect("valid complete solve record"))
            .collect::<Vec<_>>();
        assert_eq!(records.len(), 2);
        assert_eq!(records[0]["result"]["status"], "cancelled");
        assert_eq!(records[1]["result"]["status"], "unsupported");
    }

    #[test]
    fn cancelled_solve_is_nonfinal_and_never_claims_verified_results() {
        let payload = cancelled_solve_payload("request-one", "state-one");
        assert_eq!(payload["status"], "cancelled");
        assert_eq!(payload["is_final"], false);
        assert_eq!(payload["recommendations"], json!([]));
        assert_eq!(payload["coverage"]["counterplay"]["search_complete"], false);
    }
}
