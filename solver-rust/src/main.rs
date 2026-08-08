use std::fs;
use std::io::{self, Read, Write};
use std::path::PathBuf;
use std::process::ExitCode;
use std::sync::atomic::AtomicBool;

use clap::{Parser, Subcommand, ValueEnum};
use metacompanion_solver::behavior_prior::{BEHAVIOR_PRIOR_FILENAME, BehaviorPrior};
use metacompanion_solver::card_pool::{
    OfficialCardPoolBundle, select_official_card_pool_directory,
};
use metacompanion_solver::decision_ranker::{DECISION_RANKER_FILENAME, DecisionRanker};
use metacompanion_solver::error::SolverError;
use metacompanion_solver::http::{ServeOptions, serve};
use metacompanion_solver::parity::{COMBAT_PROFILE, FULL_PROFILE, evaluate_json, run_jsonl};
use metacompanion_solver::training_log::TRAINING_LOG_FILENAME;
use serde::Deserialize;

#[derive(Debug, Parser)]
#[command(name = "metacompanion-solver", version, about)]
struct Cli {
    #[command(subcommand)]
    command: Command,
}

#[derive(Clone, Copy, Debug, ValueEnum)]
enum Profile {
    CombatV1,
    Full,
}

impl Profile {
    const fn as_str(self) -> &'static str {
        match self {
            Self::CombatV1 => COMBAT_PROFILE,
            Self::Full => FULL_PROFILE,
        }
    }
}

#[derive(Debug, Subcommand)]
enum Command {
    /// Run the authenticated loopback HTTP worker.
    Serve {
        #[arg(long, env = "METACOMPANION_SOLVER_CONFIG")]
        config: Option<PathBuf>,
        #[arg(long, env = "METACOMPANION_SOLVER_TOKEN")]
        session_token: Option<String>,
        #[arg(long, env = "METACOMPANION_SOLVER_HOST")]
        host: Option<String>,
        #[arg(long, env = "METACOMPANION_SOLVER_PORT")]
        port: Option<u16>,
        #[arg(long, env = "METACOMPANION_SOLVER_DATA_DIR")]
        data_dir: Option<PathBuf>,
        #[arg(long, conflicts_with = "no_training_log")]
        training_log: Option<PathBuf>,
        #[arg(long, env = "METACOMPANION_SOLVER_NO_TRAINING_LOG")]
        no_training_log: bool,
        #[arg(long)]
        advisor_data: Option<PathBuf>,
        #[arg(long, env = "METACOMPANION_SOLVER_BEHAVIOR_PRIOR")]
        behavior_prior: Option<PathBuf>,
        #[arg(long, env = "METACOMPANION_SOLVER_DECISION_RANKER")]
        decision_ranker: Option<PathBuf>,
    },
    /// Validate one installed official Standard/Arena card-pool bundle.
    OfficialCardPoolCheck {
        #[arg(long)]
        root: PathBuf,
        #[arg(long)]
        card_defs: PathBuf,
    },
    /// Validate one offline behavior-prior artifact with the production Rust loader.
    BehaviorPriorCheck {
        #[arg(long)]
        path: PathBuf,
    },
    /// Validate one offline decision-ranker artifact with the production Rust loader.
    DecisionRankerCheck {
        #[arg(long)]
        path: PathBuf,
    },
    /// Evaluate one canonical SolveRequest envelope from stdin.
    ParityOne {
        #[arg(long, value_enum)]
        profile: Profile,
    },
    /// Evaluate newline-delimited canonical SolveRequest envelopes from stdin.
    ParityJsonl {
        #[arg(long, value_enum)]
        profile: Profile,
    },
}

#[derive(Debug, Default, Deserialize)]
struct FileConfig {
    host: Option<String>,
    port: Option<u16>,
    max_request_bytes: Option<usize>,
    training_log_path: Option<PathBuf>,
    behavior_prior_path: Option<PathBuf>,
    decision_ranker_path: Option<PathBuf>,
    advisor_data_path: Option<PathBuf>,
}

fn load_file_config(path: Option<&PathBuf>) -> Result<FileConfig, SolverError> {
    path.map_or_else(
        || Ok(FileConfig::default()),
        |path| {
            let body = fs::read(path)?;
            Ok(serde_json::from_slice(&body)?)
        },
    )
}

fn config_training_log_path(
    config_path: Option<&PathBuf>,
    training_log_path: Option<PathBuf>,
) -> Option<PathBuf> {
    training_log_path.map(|path| {
        if path.is_absolute() {
            path
        } else {
            config_path
                .and_then(|config| config.parent())
                .map_or(path.clone(), |parent| parent.join(path))
        }
    })
}

fn config_relative_path(config_path: Option<&PathBuf>, value: Option<PathBuf>) -> Option<PathBuf> {
    value.map(|path| {
        if path.is_absolute() {
            path
        } else {
            config_path
                .and_then(|config| config.parent())
                .map_or(path.clone(), |parent| parent.join(path))
        }
    })
}

fn select_training_log_path(
    no_training_log: bool,
    explicit: Option<PathBuf>,
    data_dir: Option<PathBuf>,
    configured: Option<PathBuf>,
) -> Option<PathBuf> {
    if no_training_log {
        None
    } else {
        explicit
            .or_else(|| data_dir.map(|path| path.join(TRAINING_LOG_FILENAME)))
            .or(configured)
    }
}

fn select_behavior_prior_path(
    explicit: Option<PathBuf>,
    data_dir: Option<PathBuf>,
    configured: Option<PathBuf>,
) -> Option<PathBuf> {
    explicit
        .or_else(|| data_dir.map(|path| path.join(BEHAVIOR_PRIOR_FILENAME)))
        .or(configured)
}

fn select_decision_ranker_path(
    explicit: Option<PathBuf>,
    data_dir: Option<PathBuf>,
    configured: Option<PathBuf>,
) -> Option<PathBuf> {
    explicit
        .or_else(|| data_dir.map(|path| path.join(DECISION_RANKER_FILENAME)))
        .or(configured)
}

fn run(cli: Cli) -> Result<u8, SolverError> {
    match cli.command {
        Command::Serve {
            config,
            session_token,
            host,
            port,
            data_dir,
            training_log,
            no_training_log,
            advisor_data,
            behavior_prior,
            decision_ranker,
        } => {
            let file = load_file_config(config.as_ref())?;
            let defaults = ServeOptions::default();
            let configured_training_log =
                config_training_log_path(config.as_ref(), file.training_log_path);
            let training_log_path = select_training_log_path(
                no_training_log,
                training_log,
                data_dir.clone(),
                configured_training_log,
            );
            let configured_behavior_prior =
                config_relative_path(config.as_ref(), file.behavior_prior_path);
            let configured_decision_ranker =
                config_relative_path(config.as_ref(), file.decision_ranker_path);
            let configured_advisor_data =
                config_relative_path(config.as_ref(), file.advisor_data_path);
            let selected_advisor_data = advisor_data.or(configured_advisor_data);
            let official_card_pool_path = select_official_card_pool_directory(
                selected_advisor_data.as_deref(),
                data_dir.as_deref(),
            );
            let behavior_prior_path = select_behavior_prior_path(
                behavior_prior,
                data_dir.clone(),
                configured_behavior_prior,
            );
            let decision_ranker_path =
                select_decision_ranker_path(decision_ranker, data_dir, configured_decision_ranker);
            serve(ServeOptions {
                host: host.or(file.host).unwrap_or(defaults.host),
                port: port.or(file.port).unwrap_or(defaults.port),
                session_token: session_token.unwrap_or_default(),
                max_request_bytes: file.max_request_bytes.unwrap_or(defaults.max_request_bytes),
                training_log_path,
                behavior_prior_path,
                decision_ranker_path,
                official_card_pool_path,
            })?;
            Ok(0)
        }
        Command::OfficialCardPoolCheck { root, card_defs } => {
            let bundle = OfficialCardPoolBundle::load_optional(Some(&root), Some(&card_defs));
            serde_json::to_writer(
                io::stdout().lock(),
                &serde_json::json!({
                    "schema": "metacompanion-rust-official-card-pool-check-v1",
                    "status": if bundle.available { "pass" } else { "rejected" },
                    "official_card_pools": bundle.health_payload(),
                    "rules_coverage": false,
                    "enforces_action_legality": false
                }),
            )?;
            println!();
            Ok(if bundle.available { 0 } else { 3 })
        }
        Command::BehaviorPriorCheck { path } => {
            let prior = BehaviorPrior::load(&path)
                .map_err(|error| SolverError::schema("behavior_prior", error.code()))?;
            serde_json::to_writer(
                io::stdout().lock(),
                &serde_json::json!({
                    "schema": "metacompanion-rust-behavior-prior-check-v1",
                    "status": "pass",
                    "artifact_sha256": prior.artifact_sha256(),
                    "search_ordering_only": true,
                    "candidate_generation_allowed": false,
                    "live_policy_eligible": false,
                    "rl_training_eligible": false,
                    "optimality_verified": false
                }),
            )?;
            println!();
            Ok(0)
        }
        Command::DecisionRankerCheck { path } => {
            let ranker = DecisionRanker::load(&path)
                .map_err(|error| SolverError::schema("decision_ranker", error.code()))?;
            serde_json::to_writer(
                io::stdout().lock(),
                &serde_json::json!({
                    "schema": "metacompanion-rust-decision-ranker-check-v1",
                    "status": "pass",
                    "artifact_sha256": ranker.artifact_sha256(),
                    "search_ordering_only": true,
                    "local_actions_only": true,
                    "candidate_generation_allowed": false,
                    "live_policy_eligible": false,
                    "rl_training_eligible": false,
                    "optimality_verified": false
                }),
            )?;
            println!();
            Ok(0)
        }
        Command::ParityOne { profile } => {
            let mut input = String::new();
            io::stdin().read_to_string(&mut input)?;
            let cancel = AtomicBool::new(false);
            let output = evaluate_json(&input, profile.as_str(), &cancel);
            serde_json::to_writer(io::stdout().lock(), &output)?;
            println!();
            Ok(if output.is_ok() { 0 } else { 3 })
        }
        Command::ParityJsonl { profile } => {
            let cancel = AtomicBool::new(false);
            let stdin = io::stdin();
            let stdout = io::stdout();
            let all_ok = run_jsonl(stdin.lock(), stdout.lock(), profile.as_str(), &cancel)?;
            Ok(if all_ok { 0 } else { 3 })
        }
    }
}

fn main() -> ExitCode {
    match run(Cli::parse()) {
        Ok(code) => ExitCode::from(code),
        Err(error) => {
            let _ = writeln!(io::stderr().lock(), "错误：{}", error.public_message());
            ExitCode::from(2)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn training_log_path_priority_is_fail_closed_and_deterministic() {
        let explicit = PathBuf::from("explicit.jsonl");
        let data_dir = PathBuf::from("managed-data");
        let configured = PathBuf::from("configured.jsonl");
        assert_eq!(
            select_training_log_path(
                false,
                Some(explicit.clone()),
                Some(data_dir.clone()),
                Some(configured.clone()),
            ),
            Some(explicit)
        );
        assert_eq!(
            select_training_log_path(
                false,
                None,
                Some(data_dir.clone()),
                Some(configured.clone()),
            ),
            Some(data_dir.join(TRAINING_LOG_FILENAME))
        );
        assert_eq!(
            select_training_log_path(false, None, None, Some(configured.clone())),
            Some(configured)
        );
        assert_eq!(
            select_training_log_path(
                true,
                Some(PathBuf::from("explicit.jsonl")),
                Some(data_dir),
                Some(PathBuf::from("configured.jsonl")),
            ),
            None
        );
        assert_eq!(select_training_log_path(false, None, None, None), None);
    }

    #[test]
    fn relative_config_log_path_is_resolved_beside_the_config() {
        let config = PathBuf::from("settings").join("solver.json");
        assert_eq!(
            config_training_log_path(
                Some(&config),
                Some(PathBuf::from("data").join(TRAINING_LOG_FILENAME)),
            ),
            Some(
                PathBuf::from("settings")
                    .join("data")
                    .join(TRAINING_LOG_FILENAME)
            )
        );
    }

    #[test]
    fn cli_training_selection_derives_behavior_log_in_the_same_data_directory() {
        let training =
            select_training_log_path(false, None, Some(PathBuf::from("managed-data")), None)
                .expect("selected training path");
        assert_eq!(
            metacompanion_solver::behavior::behavior_path_for_training_log(&training)
                .expect("independent behavior path"),
            PathBuf::from("managed-data")
                .join(metacompanion_solver::behavior::BEHAVIOR_LOG_FILENAME)
        );
        assert!(
            select_training_log_path(true, None, Some(PathBuf::from("managed-data")), None)
                .is_none()
        );
    }

    #[test]
    fn behavior_prior_path_is_derived_beside_runtime_logs_with_explicit_priority() {
        let explicit = PathBuf::from("explicit-prior.json");
        let data_dir = PathBuf::from("managed-data");
        let configured = PathBuf::from("configured-prior.json");
        assert_eq!(
            select_behavior_prior_path(
                Some(explicit.clone()),
                Some(data_dir.clone()),
                Some(configured.clone()),
            ),
            Some(explicit)
        );
        assert_eq!(
            select_behavior_prior_path(None, Some(data_dir.clone()), Some(configured.clone())),
            Some(data_dir.join(BEHAVIOR_PRIOR_FILENAME))
        );
        assert_eq!(
            select_behavior_prior_path(None, None, Some(configured.clone())),
            Some(configured)
        );
        assert_eq!(select_behavior_prior_path(None, None, None), None);
    }

    #[test]
    fn decision_ranker_path_is_derived_beside_runtime_logs_with_explicit_priority() {
        let explicit = PathBuf::from("explicit-ranker.json");
        let data_dir = PathBuf::from("managed-data");
        let configured = PathBuf::from("configured-ranker.json");
        assert_eq!(
            select_decision_ranker_path(
                Some(explicit.clone()),
                Some(data_dir.clone()),
                Some(configured.clone()),
            ),
            Some(explicit)
        );
        assert_eq!(
            select_decision_ranker_path(None, Some(data_dir.clone()), Some(configured.clone())),
            Some(data_dir.join(DECISION_RANKER_FILENAME))
        );
        assert_eq!(
            select_decision_ranker_path(None, None, Some(configured.clone())),
            Some(configured)
        );
        assert_eq!(select_decision_ranker_path(None, None, None), None);
    }
}
