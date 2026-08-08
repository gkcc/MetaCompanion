//! Rust migration core for MetaCompanion.
//!
//! The crate intentionally starts with the independently testable
//! `oracle-turn-v1` combat subset.  Unsupported Hearthstone rules are errors,
//! never silently approximated successes.

#![recursion_limit = "256"]

pub mod behavior;
pub mod behavior_prior;
pub mod card_pool;
pub mod decision_ranker;
pub mod error;
pub mod generation_rules;
pub mod hdt;
pub mod hdt_root;
pub mod http;
pub mod model;
pub mod oracle;
pub mod parity;
pub mod rules;
pub mod template_rules;
pub mod training_log;
pub mod turnpair;

pub const API_VERSION: &str = "1.0";
pub const PACKAGE_VERSION: &str = env!("CARGO_PKG_VERSION");
