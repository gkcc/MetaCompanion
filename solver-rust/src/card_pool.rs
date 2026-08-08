use std::collections::{BTreeMap, BTreeSet};
use std::env;
use std::fmt;
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration as StdDuration, SystemTime};

use html_escape::decode_html_entities;
use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};
use time::OffsetDateTime;
use time::format_description::well_known::Rfc3339;

use crate::model::{
    Card, CardPoolClassMode, CardPoolQuery, CardPoolSource, CardType, Effect, GameState,
    JsonScalar, KnownDeckCard, PlayerState, PoolSelection, ResolvedPoolCandidate, ResolvedPoolCard,
};

pub const MAX_MANIFEST_BYTES: u64 = 2 * 1024 * 1024;
pub const MAX_POOL_BYTES: u64 = 20 * 1024 * 1024;
pub const MAX_CARD_DEFS_BYTES: u64 = 128 * 1024 * 1024;
pub const DEFAULT_MAX_AGE_HOURS: f64 = 72.0;
pub const MAX_CONFIGURED_AGE_HOURS: f64 = 24.0 * 30.0;
pub const FUTURE_CLOCK_SKEW_SECONDS: i64 = 5 * 60;
pub const MAX_AGE_ENVIRONMENT_VARIABLE: &str = "METACOMPANION_OFFICIAL_CARD_POOL_MAX_AGE_HOURS";
pub const SOURCE_DESCRIPTION: &str = "Blizzard Hearthstone Card Library";
pub const MAX_EXACT_GENERATION_BRANCHES: usize = 96;
pub const MAX_REPORTED_POOL_RESOLUTION_ERRORS: usize = 40;

#[derive(Clone, Debug, Eq, PartialEq)]
struct PoolCardRecord {
    card_id: String,
    dbf_id: u64,
    name: String,
    card_type: CardType,
    cost: u16,
    attack: u16,
    health: u16,
    durability: u16,
    collectible: bool,
    card_set_id: Option<u16>,
    class_ids: BTreeSet<u16>,
    spell_school_id: Option<u16>,
    minion_type_ids: BTreeSet<u16>,
    rarity_id: Option<u16>,
    keyword_ids: BTreeSet<u16>,
    keywords: BTreeSet<String>,
    text: String,
}

impl PoolCardRecord {
    fn resolved(&self) -> ResolvedPoolCard {
        ResolvedPoolCard {
            card_id: Arc::from(self.card_id.as_str()),
            dbf_id: self.dbf_id,
            name: Arc::from(self.name.as_str()),
            card_type: self.card_type,
            cost: self.cost,
            attack: self.attack,
            health: self.health,
            durability: self.durability,
            rarity_id: self.rarity_id.unwrap_or(0),
            keywords: self
                .keywords
                .iter()
                .map(|value| Arc::from(value.as_str()))
                .collect::<Vec<_>>()
                .into(),
            text: Arc::from(self.text.as_str()),
        }
    }
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CardPoolError {
    reason: &'static str,
    message: String,
}

impl CardPoolError {
    fn new(reason: &'static str, message: impl Into<String>) -> Self {
        Self {
            reason,
            message: message.into(),
        }
    }

    #[must_use]
    pub const fn reason(&self) -> &'static str {
        self.reason
    }

    #[must_use]
    pub fn public_message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for CardPoolError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for CardPoolError {}

#[derive(Clone, Debug)]
pub struct OfficialCardPoolBundle {
    pub available: bool,
    pub run_id: String,
    pub card_defs_build: String,
    pub card_defs_sha256: String,
    pub card_defs_bytes: u64,
    pub manifest_sha256: String,
    pub generated_at_utc: String,
    pub oldest_fetched_at_utc: String,
    pub max_age_hours: f64,
    pub future_clock_skew_seconds: i64,
    cards_by_format: BTreeMap<String, BTreeMap<String, PoolCardRecord>>,
    dbf_ids_by_format: BTreeMap<String, BTreeSet<u64>>,
    pub source: &'static str,
    pub reason: String,
    pub error: String,
}

impl OfficialCardPoolBundle {
    #[must_use]
    pub fn unavailable() -> Self {
        Self::unavailable_with(
            "snapshot_unavailable",
            "尚未安装官方卡池快照。",
            DEFAULT_MAX_AGE_HOURS,
        )
    }

    fn unavailable_with(reason: &str, error: &str, max_age_hours: f64) -> Self {
        Self {
            available: false,
            run_id: String::new(),
            card_defs_build: String::new(),
            card_defs_sha256: String::new(),
            card_defs_bytes: 0,
            manifest_sha256: String::new(),
            generated_at_utc: String::new(),
            oldest_fetched_at_utc: String::new(),
            max_age_hours,
            future_clock_skew_seconds: FUTURE_CLOCK_SKEW_SECONDS,
            cards_by_format: BTreeMap::new(),
            dbf_ids_by_format: BTreeMap::new(),
            source: SOURCE_DESCRIPTION,
            reason: reason.to_owned(),
            error: error.to_owned(),
        }
    }

    pub fn load(root: &Path, card_defs_path: Option<&Path>) -> Result<Self, CardPoolError> {
        let max_age_hours = resolve_max_age_hours()?;
        Self::load_with_context(
            root,
            card_defs_path,
            StdDuration::from_secs_f64(max_age_hours * 3600.0),
            SystemTime::now(),
        )
    }

    pub fn load_with_context(
        root: &Path,
        card_defs_path: Option<&Path>,
        max_age: StdDuration,
        now: SystemTime,
    ) -> Result<Self, CardPoolError> {
        let max_age_hours = validate_max_age(max_age)?;
        let now = OffsetDateTime::from(now);
        let latest = root.join("latest");
        let publish_path = latest.join("publish-complete.json");
        let manifest_path = latest.join("manifest.json");
        let publish_bytes = read_bounded_json(&publish_path, MAX_MANIFEST_BYTES)?;
        let manifest_bytes = read_bounded_json(&manifest_path, MAX_MANIFEST_BYTES)?;
        let publish = parse_json_object(&publish_bytes)?;
        let manifest = parse_json_object(&manifest_bytes)?;

        if required_u64(&publish, "schema_version", 0)? != 1
            || required_u64(&manifest, "schema_version", 0)? != 1
        {
            return Err(invalid("官方卡池快照版本不受支持。"));
        }
        let run_id = required_nonempty_string(&manifest, "run_id")?;
        if publish.get("run_id").and_then(Value::as_str) != Some(run_id) {
            return Err(invalid("官方卡池发布标记与清单不匹配。"));
        }
        let manifest_sha256 = sha256_hex(&manifest_bytes);
        if normalized_hash(publish.get("manifest_sha256")) != manifest_sha256.to_lowercase() {
            return Err(invalid("官方卡池清单哈希不匹配。"));
        }
        if manifest.get("status").and_then(Value::as_str) != Some("complete") {
            return Err(invalid("官方卡池快照尚未完整发布。"));
        }

        let generated_at =
            validate_timestamp(&manifest, "generated_at", now, max_age.as_secs_f64())?;
        let source = required_object(&manifest, "source")?;
        if source.get("provider").and_then(Value::as_str) != Some("Blizzard") {
            return Err(invalid("官方卡池来源声明无效。"));
        }
        if source.get("authentication").and_then(Value::as_str) != Some("none")
            || source.get("browser_required").and_then(Value::as_bool) != Some(false)
        {
            return Err(invalid("官方卡池来源不得依赖浏览器凭据。"));
        }
        validate_rules_coverage(required_object(&manifest, "coverage")?)?;

        let pool_records = manifest
            .get("pools")
            .and_then(Value::as_array)
            .ok_or_else(|| invalid("官方卡池清单缺少双模式记录。"))?;
        if pool_records.len() != 2 {
            return Err(invalid("官方卡池必须恰好包含标准和竞技场两种模式。"));
        }
        let mut records_by_format = BTreeMap::<String, &Map<String, Value>>::new();
        for record in pool_records {
            let object = record
                .as_object()
                .ok_or_else(|| invalid("官方卡池清单包含无效模式记录。"))?;
            let format_name = required_nonempty_string(object, "format")?.to_lowercase();
            if !matches!(format_name.as_str(), "standard" | "arena") {
                return Err(invalid("官方卡池清单包含未知模式。"));
            }
            if records_by_format.insert(format_name, object).is_some() {
                return Err(invalid("官方卡池清单包含重复模式。"));
            }
        }
        if !records_by_format.contains_key("standard") || !records_by_format.contains_key("arena") {
            return Err(invalid("官方卡池必须同时包含标准和竞技场。"));
        }

        let mut fetched_at_values = Vec::new();
        for format_name in ["standard", "arena"] {
            let record = records_by_format
                .get(format_name)
                .expect("validated format record");
            let record_fetched_at =
                validate_timestamp(record, "fetched_at", now, max_age.as_secs_f64())?;
            fetched_at_values.push(record_fetched_at);
            let pages = record
                .get("pages")
                .and_then(Value::as_array)
                .filter(|pages| !pages.is_empty())
                .ok_or_else(|| invalid("官方卡池分页记录为空。"))?;
            let mut page_numbers = BTreeSet::new();
            for page in pages {
                let page = page
                    .as_object()
                    .ok_or_else(|| invalid("官方卡池包含无效分页记录。"))?;
                let page_number = required_u64(page, "page", 1)?;
                if !page_numbers.insert(page_number) {
                    return Err(invalid("官方卡池包含重复页码。"));
                }
                let page_fetched_at =
                    if page.contains_key("fetched_at_utc") || page.contains_key("fetched_at") {
                        validate_timestamp(page, "fetched_at", now, max_age.as_secs_f64())?
                    } else {
                        record_fetched_at
                    };
                fetched_at_values.push(page_fetched_at);
            }
        }

        let (card_defs_build, card_defs_bytes, card_defs_sha256) =
            validate_card_defs(&manifest, card_defs_path)?;
        let mut cards_by_format = BTreeMap::new();
        let mut dbf_ids_by_format = BTreeMap::new();
        for format_name in ["standard", "arena"] {
            let record = records_by_format
                .get(format_name)
                .expect("validated format record");
            let expected_file_name = format!("{format_name}.json");
            if record.get("file").and_then(Value::as_str) != Some(expected_file_name.as_str()) {
                return Err(invalid("官方卡池文件名不符合固定契约。"));
            }
            let declared_bytes = required_u64(record, "bytes", 1)?;
            if declared_bytes > MAX_POOL_BYTES {
                return Err(CardPoolError::new(
                    "snapshot_file_size_invalid",
                    "官方卡池文件大小超出安全范围。",
                ));
            }
            let pool_path = latest.join(&expected_file_name);
            let pool_bytes = read_bounded_json(&pool_path, MAX_POOL_BYTES)?;
            if u64::try_from(pool_bytes.len()).ok() != Some(declared_bytes) {
                return Err(invalid("官方卡池文件大小与清单不匹配。"));
            }
            if normalized_hash(record.get("sha256")) != sha256_hex(&pool_bytes).to_lowercase() {
                return Err(invalid("官方卡池文件哈希与清单不匹配。"));
            }
            let pool = parse_json_object(&pool_bytes)?;
            if required_u64(&pool, "schema_version", 0)? != 1
                || pool
                    .get("format")
                    .and_then(Value::as_str)
                    .is_none_or(|value| !value.eq_ignore_ascii_case(format_name))
                || pool.get("run_id").and_then(Value::as_str) != Some(run_id)
            {
                return Err(invalid("官方卡池文件与清单契约不匹配。"));
            }
            validate_rules_coverage(required_object(&pool, "coverage")?)?;
            let cards = pool
                .get("cards")
                .and_then(Value::as_array)
                .ok_or_else(|| invalid("官方卡池缺少卡牌数组。"))?;
            let mut card_ids = BTreeSet::new();
            let mut dbf_ids = BTreeSet::new();
            let mut card_records = BTreeMap::new();
            for card in cards {
                let card = card
                    .as_object()
                    .ok_or_else(|| invalid("官方卡池包含无效卡牌记录。"))?;
                if card.get("collectible").and_then(Value::as_bool) != Some(true) {
                    return Err(invalid("官方卡池包含不可收集卡牌记录。"));
                }
                let card_id = required_nonempty_string(card, "card_id")?.to_owned();
                let dbf_id = required_u64(card, "dbf_id", 1)?;
                if !card_ids.insert(card_id.clone()) || !dbf_ids.insert(dbf_id) {
                    return Err(invalid("官方卡池包含重复卡牌身份。"));
                }
                let mut class_ids = optional_u16_array(card, "multi_class_ids")?;
                if let Some(class_id) = optional_u16(card, "class_id")? {
                    class_ids.insert(class_id);
                }
                let mut minion_type_ids = optional_u16_array(card, "multi_type_ids")?;
                if let Some(minion_type_id) = optional_u16(card, "minion_type_id")? {
                    minion_type_ids.insert(minion_type_id);
                }
                let text = card
                    .get("text")
                    .and_then(Value::as_str)
                    .unwrap_or_default()
                    .to_owned();
                let name = card
                    .get("name")
                    .and_then(Value::as_str)
                    .filter(|value| !value.trim().is_empty())
                    .unwrap_or(&card_id)
                    .to_owned();
                let card_type = card_type_from_library_id(optional_u16(card, "card_type_id")?);
                card_records.insert(
                    card_id.clone(),
                    PoolCardRecord {
                        card_id,
                        dbf_id,
                        name,
                        card_type,
                        cost: optional_u16(card, "mana_cost")?.unwrap_or(0),
                        attack: optional_u16(card, "attack")?.unwrap_or(0),
                        health: optional_u16(card, "health")?.unwrap_or(0),
                        durability: optional_u16(card, "durability")?.unwrap_or(0),
                        collectible: true,
                        card_set_id: optional_u16(card, "card_set_id")?,
                        class_ids,
                        spell_school_id: optional_u16(card, "spell_school_id")?,
                        minion_type_ids,
                        rarity_id: optional_u16(card, "rarity_id")?,
                        keyword_ids: optional_u16_array(card, "keyword_ids")?,
                        keywords: keyword_tags(&text),
                        text,
                    },
                );
            }
            let actual_count = u64::try_from(cards.len())
                .map_err(|_| invalid("官方卡池卡牌数量超出支持范围。"))?;
            if required_u64(&pool, "declared_count", 0)? != actual_count
                || required_u64(record, "declared_count", 0)? != actual_count
                || required_u64(record, "unique_card_ids", 0)? != actual_count
                || required_u64(record, "unique_dbf_ids", 0)? != actual_count
            {
                return Err(invalid("官方卡池声明数量与实际内容不匹配。"));
            }
            cards_by_format.insert(format_name.to_owned(), card_records);
            dbf_ids_by_format.insert(format_name.to_owned(), dbf_ids);
        }

        let oldest_fetched_at = fetched_at_values
            .into_iter()
            .min()
            .ok_or_else(|| invalid("官方卡池缺少抓取时间。"))?;
        Ok(Self {
            available: true,
            run_id: run_id.to_owned(),
            card_defs_build,
            card_defs_sha256,
            card_defs_bytes,
            manifest_sha256,
            generated_at_utc: format_timestamp(generated_at)?,
            oldest_fetched_at_utc: format_timestamp(oldest_fetched_at)?,
            max_age_hours,
            future_clock_skew_seconds: FUTURE_CLOCK_SKEW_SECONDS,
            cards_by_format,
            dbf_ids_by_format,
            source: SOURCE_DESCRIPTION,
            reason: String::new(),
            error: String::new(),
        })
    }

    #[must_use]
    pub fn load_optional(root: Option<&Path>, card_defs_path: Option<&Path>) -> Self {
        let max_age_hours = match resolve_max_age_hours() {
            Ok(value) => value,
            Err(error) => {
                return Self::unavailable_with(
                    error.reason(),
                    error.public_message(),
                    DEFAULT_MAX_AGE_HOURS,
                );
            }
        };
        let Some(root) = root else {
            return Self::unavailable_with(
                "snapshot_unavailable",
                "尚未配置官方卡池快照目录。",
                max_age_hours,
            );
        };
        match Self::load_with_context(
            root,
            card_defs_path,
            StdDuration::from_secs_f64(max_age_hours * 3600.0),
            SystemTime::now(),
        ) {
            Ok(bundle) => bundle,
            Err(error) => {
                Self::unavailable_with(error.reason(), error.public_message(), max_age_hours)
            }
        }
    }

    #[must_use]
    pub fn health_payload(&self) -> Value {
        json!({
            "available": self.available,
            "run_id": self.run_id,
            "card_defs_build": self.card_defs_build,
            "card_defs_sha256": self.card_defs_sha256,
            "card_defs_bytes": self.card_defs_bytes,
            "manifest_sha256": self.manifest_sha256,
            "generated_at_utc": self.generated_at_utc,
            "oldest_fetched_at_utc": self.oldest_fetched_at_utc,
            "max_age_hours": self.max_age_hours,
            "future_clock_skew_seconds": self.future_clock_skew_seconds,
            "standard_count": self.cards_by_format.get("standard").map_or(0, BTreeMap::len),
            "arena_count": self.cards_by_format.get("arena").map_or(0, BTreeMap::len),
            "generation_pool_registry": self.available,
            "rules_coverage": false,
            "source": self.source,
            "reason": self.reason,
            "error": self.error
        })
    }

    #[must_use]
    pub fn assess_state(&self, state: &GameState) -> Value {
        let format_name = state_format(state);
        let card_pool = self.cards_by_format.get(format_name);
        let mut known_collectible_ids = state
            .friendly
            .hand
            .iter()
            .chain(state.friendly.board.iter())
            .chain(state.opponent.board.iter())
            .filter_map(|card| {
                let card_id = card.card_id.as_ref();
                (!card_id.is_empty() && !card_id.starts_with("UNKNOWN")).then(|| card_id.to_owned())
            })
            .collect::<BTreeSet<_>>();
        let membership_assessed = self.available && !format_name.is_empty();
        let outside_pool = if membership_assessed {
            let pool = card_pool.expect("available recognized format has a loaded pool");
            known_collectible_ids
                .iter()
                .filter(|card_id| !pool.contains_key(*card_id))
                .take(20)
                .cloned()
                .collect::<Vec<_>>()
        } else {
            Vec::new()
        };
        let in_pool_count = if membership_assessed {
            let pool = card_pool.expect("available recognized format has a loaded pool");
            Some(
                known_collectible_ids
                    .iter()
                    .filter(|card_id| pool.contains_key(*card_id))
                    .count(),
            )
        } else {
            None
        };
        let outside_pool_count = membership_assessed.then(|| {
            known_collectible_ids
                .len()
                .saturating_sub(in_pool_count.unwrap_or(0))
        });
        let visible_known_card_count = known_collectible_ids.len();
        known_collectible_ids.clear();
        json!({
            "available": self.available,
            "format": if format_name.is_empty() { "unknown" } else { format_name },
            "run_id": self.run_id,
            "card_defs_build": self.card_defs_build,
            "card_defs_sha256": self.card_defs_sha256,
            "card_defs_bytes": self.card_defs_bytes,
            "manifest_sha256": self.manifest_sha256,
            "generated_at_utc": self.generated_at_utc,
            "oldest_fetched_at_utc": self.oldest_fetched_at_utc,
            "max_age_hours": self.max_age_hours,
            "future_clock_skew_seconds": self.future_clock_skew_seconds,
            "pool_count": card_pool.map_or(0, BTreeMap::len),
            "visible_known_card_count": visible_known_card_count,
            "membership_assessed": membership_assessed,
            "visible_cards_in_pool_count": in_pool_count,
            "visible_cards_outside_pool_count": outside_pool_count,
            "visible_cards_outside_pool": outside_pool,
            "rules_coverage": false,
            "generated_entities_coverage": false,
            "enforces_action_legality": false,
            "source": self.source,
            "reason": self.reason,
            "error": self.error
        })
    }

    fn query_records<'a>(
        &'a self,
        format_name: &str,
        query: &CardPoolQuery,
        controller_class_id: Option<u16>,
        source_card_id: &str,
    ) -> Result<Vec<&'a PoolCardRecord>, &'static str> {
        if !self.available {
            return Err("snapshot_unavailable");
        }
        if query.source != CardPoolSource::CurrentFormat {
            return Err("pool_source_requires_zone_or_history_model");
        }
        let records = self
            .cards_by_format
            .get(format_name)
            .ok_or("current_format_unknown")?;
        if matches!(
            query.class_mode,
            CardPoolClassMode::Controller
                | CardPoolClassMode::ControllerOrNeutral
                | CardPoolClassMode::AnotherClass
        ) && controller_class_id.is_none()
        {
            return Err("controller_class_unknown");
        }
        let excluded = query
            .exclude_card_ids
            .iter()
            .map(|value| value.to_ascii_uppercase())
            .collect::<BTreeSet<_>>();
        let required_keywords = query
            .required_keywords
            .iter()
            .map(|value| normalize_keyword(value))
            .collect::<BTreeSet<_>>();
        let values = records
            .values()
            .filter(|record| record.collectible == query.collectible)
            .filter(|record| {
                query.cost_min.is_none_or(|minimum| record.cost >= minimum)
                    && query.cost_max.is_none_or(|maximum| record.cost <= maximum)
            })
            .filter(|record| {
                query.card_types.is_empty() || query.card_types.contains(&record.card_type)
            })
            .filter(|record| match query.class_mode {
                CardPoolClassMode::Any => true,
                CardPoolClassMode::Controller => {
                    controller_class_id.is_some_and(|class_id| record.class_ids.contains(&class_id))
                }
                CardPoolClassMode::ControllerOrNeutral => {
                    controller_class_id.is_some_and(|class_id| {
                        record.class_ids.contains(&class_id) || record.class_ids.contains(&12)
                    })
                }
                CardPoolClassMode::AnotherClass => controller_class_id.is_some_and(|class_id| {
                    !record.class_ids.is_empty()
                        && !record.class_ids.contains(&class_id)
                        && !record.class_ids.contains(&12)
                }),
                CardPoolClassMode::Specific => query
                    .class_ids
                    .iter()
                    .any(|class_id| record.class_ids.contains(class_id)),
            })
            .filter(|record| {
                query.spell_school_ids.is_empty()
                    || record
                        .spell_school_id
                        .is_some_and(|value| query.spell_school_ids.contains(&value))
            })
            .filter(|record| {
                query.minion_type_ids.is_empty()
                    || query
                        .minion_type_ids
                        .iter()
                        .any(|value| record.minion_type_ids.contains(value))
            })
            .filter(|record| {
                query.card_set_ids.is_empty()
                    || record
                        .card_set_id
                        .is_some_and(|value| query.card_set_ids.contains(&value))
            })
            .filter(|record| {
                query.rarity_ids.is_empty()
                    || record
                        .rarity_id
                        .is_some_and(|value| query.rarity_ids.contains(&value))
            })
            .filter(|record| {
                query.keyword_ids.is_empty()
                    || query
                        .keyword_ids
                        .iter()
                        .all(|value| record.keyword_ids.contains(value))
            })
            .filter(|record| {
                required_keywords
                    .iter()
                    .all(|value| record.keywords.contains(value))
            })
            .filter(|record| {
                !(query.exclude_self && record.card_id.eq_ignore_ascii_case(source_card_id))
            })
            .filter(|record| !excluded.contains(&record.card_id.to_ascii_uppercase()))
            .collect::<Vec<_>>();
        if values.is_empty() {
            Err("pool_query_empty")
        } else {
            Ok(values)
        }
    }

    /// Query the full, unsampled registry. This is primarily used by audits and
    /// transition tests; live effects use bounded stratified branches below.
    pub fn query_card_ids(
        &self,
        format_name: &str,
        query: &CardPoolQuery,
        controller_class_id: Option<u16>,
        source_card_id: &str,
    ) -> Result<Vec<String>, &'static str> {
        let mut values = self
            .query_records(format_name, query, controller_class_id, source_card_id)?
            .into_iter()
            .map(|record| record.card_id.clone())
            .collect::<Vec<_>>();
        values.sort();
        Ok(values)
    }

    fn resolve_effect(
        &self,
        effect: &mut Effect,
        context: EffectPoolResolutionContext<'_>,
    ) -> Result<(usize, bool), &'static str> {
        let EffectPoolResolutionContext {
            format_name,
            controller_class_id,
            source_card_id,
            stochastic_draw_count,
            known_deck,
            deck_identity_complete,
        } = context;
        effect.resolved_pool = Vec::<ResolvedPoolCandidate>::new().into();
        effect.resolved_pool_population = 0;
        effect.resolved_pool_exact = false;
        let query = effect.pool.as_ref().ok_or("effect_pool_missing")?;
        let (mut records, source_exact) = match query.source {
            CardPoolSource::CurrentFormat => (
                self.query_records(format_name, query, controller_class_id, source_card_id)?
                    .into_iter()
                    .map(|record| (record, 1u32))
                    .collect::<Vec<_>>(),
                true,
            ),
            CardPoolSource::OwnerDeck => {
                if known_deck.is_empty() {
                    if deck_identity_complete {
                        (Vec::new(), true)
                    } else {
                        return Err("owner_deck_identity_unavailable");
                    }
                } else {
                    let mut format_query = query.clone();
                    format_query.source = CardPoolSource::CurrentFormat;
                    let allowed = self
                        .query_records(
                            format_name,
                            &format_query,
                            controller_class_id,
                            source_card_id,
                        )?
                        .into_iter()
                        .map(|record| (record.card_id.to_ascii_uppercase(), record))
                        .collect::<BTreeMap<_, _>>();
                    let definitions = self
                        .cards_by_format
                        .get(format_name)
                        .ok_or("current_format_unknown")?;
                    let mut missing_definition = false;
                    let values = known_deck
                        .iter()
                        .filter_map(|known| {
                            let record = allowed.get(&known.card_id.to_ascii_uppercase()).copied();
                            if record.is_none()
                                && !definitions.values().any(|definition| {
                                    definition.card_id.eq_ignore_ascii_case(&known.card_id)
                                })
                            {
                                missing_definition = true;
                            }
                            record.map(|record| (record, u32::from(known.count)))
                        })
                        .filter(|(_, weight)| *weight > 0)
                        .collect::<Vec<_>>();
                    if values.is_empty() && (!deck_identity_complete || missing_definition) {
                        return Err("owner_deck_pool_empty_or_unresolved");
                    }
                    (values, deck_identity_complete && !missing_definition)
                }
            }
            CardPoolSource::OpponentDeck
            | CardPoolSource::OwnerHand
            | CardPoolSource::OpponentHand
            | CardPoolSource::Graveyard
            | CardPoolSource::Historical
            | CardPoolSource::Entourage => {
                return Err("pool_source_requires_zone_or_history_model");
            }
        };
        records.sort_by(|(left, _), (right, _)| {
            pool_branch_digest(&self.run_id, source_card_id, &left.card_id)
                .cmp(&pool_branch_digest(
                    &self.run_id,
                    source_card_id,
                    &right.card_id,
                ))
                .then_with(|| left.card_id.cmp(&right.card_id))
        });
        let population = records.iter().fold(0usize, |sum, (_, weight)| {
            sum.saturating_add(usize::try_from(*weight).unwrap_or(usize::MAX))
        });
        let minimum_cap = if effect.pool_selection == PoolSelection::Discover {
            usize::from(effect.offer_count.max(3))
        } else {
            1
        };
        // Multiple generated cards form a Cartesian chance tree. Bound the
        // candidate count per draw so the complete joint transition stays at
        // roughly MAX_EXACT_GENERATION_BRANCHES instead of growing as 96^N.
        let branch_cap =
            bounded_integer_root(MAX_EXACT_GENERATION_BRANCHES, stochastic_draw_count.max(1))
                .max(minimum_cap)
                .min(MAX_EXACT_GENERATION_BRANCHES);
        let exact = source_exact && records.len() <= branch_cap;
        let candidates = if records.len() <= branch_cap {
            records
                .into_iter()
                .map(|(record, weight)| ResolvedPoolCandidate {
                    card: record.resolved(),
                    weight,
                })
                .collect::<Vec<_>>()
        } else {
            let record_count = records.len();
            (0..branch_cap)
                .filter_map(|index| {
                    let start = index.saturating_mul(record_count) / branch_cap;
                    let end = (index + 1).saturating_mul(record_count) / branch_cap;
                    (start < end).then(|| {
                        let representative = records[start + (end - start) / 2].0;
                        let weight = records[start..end]
                            .iter()
                            .fold(0u32, |sum, (_, weight)| sum.saturating_add(*weight));
                        ResolvedPoolCandidate {
                            card: representative.resolved(),
                            weight,
                        }
                    })
                })
                .collect::<Vec<_>>()
        };
        effect.resolved_pool_population = u32::try_from(population).unwrap_or(u32::MAX);
        effect.resolved_pool_exact = exact;
        effect.resolved_pool = candidates.into();
        Ok((population, exact))
    }

    fn resolve_card_effects(
        &self,
        card: &mut Card,
        format_name: &str,
        controller_class_id: Option<u16>,
        known_deck: &[KnownDeckCard],
        deck_identity_complete: bool,
        stats: &mut PoolResolutionStats,
    ) {
        let effects = Arc::make_mut(&mut card.effects);
        let stochastic_draw_count = effects
            .iter()
            .filter(|effect| effect.pool.is_some())
            .map(|effect| usize::from(effect.count.max(1)))
            .fold(0usize, usize::saturating_add)
            .max(1);
        for (effect_index, effect) in effects.iter_mut().enumerate() {
            if effect.pool.is_none() {
                continue;
            }
            stats.effect_count = stats.effect_count.saturating_add(1);
            match self.resolve_effect(
                effect,
                EffectPoolResolutionContext {
                    format_name,
                    controller_class_id,
                    source_card_id: &card.card_id,
                    stochastic_draw_count,
                    known_deck,
                    deck_identity_complete,
                },
            ) {
                Ok((population, exact)) => {
                    stats.resolved_effect_count = stats.resolved_effect_count.saturating_add(1);
                    stats.total_population = stats.total_population.saturating_add(population);
                    if exact {
                        stats.exact_effect_count = stats.exact_effect_count.saturating_add(1);
                    } else {
                        stats.sampled_effect_count = stats.sampled_effect_count.saturating_add(1);
                    }
                }
                Err(reason) => {
                    stats.unresolved_effect_count = stats.unresolved_effect_count.saturating_add(1);
                    if stats.errors.len() < MAX_REPORTED_POOL_RESOLUTION_ERRORS {
                        stats.errors.push(json!({
                            "entity_id": card.entity_id,
                            "card_id": card.card_id,
                            "effect_index": effect_index,
                            "reason": reason
                        }));
                    }
                }
            }
        }
    }

    fn resolve_player_effects(
        &self,
        player: &mut PlayerState,
        format_name: &str,
        controller_class_id: Option<u16>,
        stats: &mut PoolResolutionStats,
    ) {
        let known_deck = player.known_deck.clone();
        let deck_identity_complete = player.deck_identity_complete;
        self.resolve_card_effects(
            &mut player.hero,
            format_name,
            controller_class_id,
            &known_deck,
            deck_identity_complete,
            stats,
        );
        for card in &mut player.hand {
            self.resolve_card_effects(
                card,
                format_name,
                controller_class_id,
                &known_deck,
                deck_identity_complete,
                stats,
            );
        }
        for card in &mut player.board {
            self.resolve_card_effects(
                card,
                format_name,
                controller_class_id,
                &known_deck,
                deck_identity_complete,
                stats,
            );
        }
        for card in &mut player.graveyard {
            self.resolve_card_effects(
                card,
                format_name,
                controller_class_id,
                &known_deck,
                deck_identity_complete,
                stats,
            );
        }
        if let Some(card) = &mut player.hero_power {
            self.resolve_card_effects(
                card,
                format_name,
                controller_class_id,
                &known_deck,
                deck_identity_complete,
                stats,
            );
        }
        if let Some(card) = &mut player.weapon {
            self.resolve_card_effects(
                card,
                format_name,
                controller_class_id,
                &known_deck,
                deck_identity_complete,
                stats,
            );
        }
    }

    /// Resolve declarative pool effects against the version/hash-bound current
    /// snapshot and attach a bounded runtime branch set to each effect.
    pub fn resolve_state_effect_pools(&self, state: &mut GameState) -> Value {
        let format_name = state_format(state).to_owned();
        let friendly_class_id = player_class_id(&state.friendly, &state.metadata, "friendly");
        let opponent_class_id = player_class_id(&state.opponent, &state.metadata, "opponent");
        let mut stats = PoolResolutionStats::default();
        self.resolve_player_effects(
            &mut state.friendly,
            &format_name,
            friendly_class_id,
            &mut stats,
        );
        self.resolve_player_effects(
            &mut state.opponent,
            &format_name,
            opponent_class_id,
            &mut stats,
        );
        json!({
            "schema": "resolved-card-pools-v1",
            "available": self.available,
            "run_id": self.run_id,
            "card_defs_build": self.card_defs_build,
            "format": if format_name.is_empty() { "unknown" } else { &format_name },
            "effect_count": stats.effect_count,
            "resolved_effect_count": stats.resolved_effect_count,
            "unresolved_effect_count": stats.unresolved_effect_count,
            "exact_effect_count": stats.exact_effect_count,
            "sampled_effect_count": stats.sampled_effect_count,
            "total_candidate_population": stats.total_population,
            "branch_cap": MAX_EXACT_GENERATION_BRANCHES,
            "joint_outcome_cap": MAX_EXACT_GENERATION_BRANCHES,
            "recompute_after_public_outcome": stats.resolved_effect_count > 0,
            "errors": stats.errors
        })
    }

    #[must_use]
    pub fn contains_dbf_id(&self, format_name: &str, dbf_id: u64) -> bool {
        self.dbf_ids_by_format
            .get(format_name)
            .is_some_and(|values| values.contains(&dbf_id))
    }
}

#[derive(Default)]
struct PoolResolutionStats {
    effect_count: usize,
    resolved_effect_count: usize,
    unresolved_effect_count: usize,
    exact_effect_count: usize,
    sampled_effect_count: usize,
    total_population: usize,
    errors: Vec<Value>,
}

struct EffectPoolResolutionContext<'a> {
    format_name: &'a str,
    controller_class_id: Option<u16>,
    source_card_id: &'a str,
    stochastic_draw_count: usize,
    known_deck: &'a [KnownDeckCard],
    deck_identity_complete: bool,
}

fn scalar_u16(value: Option<&JsonScalar>) -> Option<u16> {
    match value {
        Some(JsonScalar::Integer(value)) => u16::try_from(*value).ok(),
        Some(JsonScalar::Float(value))
            if value.is_finite() && value.fract() == 0.0 && *value >= 0.0 =>
        {
            u16::try_from(*value as u64).ok()
        }
        Some(JsonScalar::String(value)) => value.parse::<u16>().ok(),
        Some(JsonScalar::Bool(_) | JsonScalar::Null) | None => None,
        Some(JsonScalar::Float(_)) => None,
    }
}

fn player_class_id(
    player: &PlayerState,
    metadata: &BTreeMap<Arc<str>, JsonScalar>,
    side_name: &str,
) -> Option<u16> {
    for key in ["CLASS", "199", "class_id"] {
        if let Some(value) = scalar_u16(player.hero.tags.get(key)) {
            return Some(value);
        }
        if let Some(value) = scalar_u16(player.public_rule_tags.get(key)) {
            return Some(value);
        }
    }
    for key in [
        format!("{side_name}_class_id"),
        format!("{side_name}_card_class_id"),
    ] {
        if let Some(value) = scalar_u16(metadata.get(key.as_str())) {
            return Some(value);
        }
    }
    None
}

fn pool_branch_digest(run_id: &str, source_card_id: &str, candidate_card_id: &str) -> [u8; 32] {
    let mut digest = Sha256::new();
    digest.update(run_id.as_bytes());
    digest.update([0]);
    digest.update(source_card_id.as_bytes());
    digest.update([0]);
    digest.update(candidate_card_id.as_bytes());
    digest.finalize().into()
}

fn bounded_integer_root(limit: usize, exponent: usize) -> usize {
    if exponent <= 1 || limit <= 1 {
        return limit;
    }
    let mut root = 1usize;
    loop {
        let candidate = root.saturating_add(1);
        let mut product = 1usize;
        let mut within_limit = true;
        for _ in 0..exponent {
            let Some(next) = product.checked_mul(candidate) else {
                within_limit = false;
                break;
            };
            product = next;
            if product > limit {
                within_limit = false;
                break;
            }
        }
        if !within_limit {
            return root;
        }
        root = candidate;
    }
}

fn normalize_keyword(value: &str) -> String {
    let decoded = decode_html_entities(value);
    let mut normalized = String::with_capacity(decoded.len());
    let mut pending_separator = false;
    for character in decoded.chars() {
        if character.is_ascii_alphanumeric() {
            if pending_separator && !normalized.is_empty() {
                normalized.push('_');
            }
            normalized.push(character.to_ascii_lowercase());
            pending_separator = false;
        } else {
            pending_separator = true;
        }
    }
    let mut normalized = normalized.trim_matches('_').to_owned();
    if normalized.starts_with("spell_damage_") {
        normalized = "spell_damage".to_owned();
    }
    normalized
}

fn keyword_tags(text: &str) -> BTreeSet<String> {
    let lowercase = text.to_ascii_lowercase();
    let mut result = BTreeSet::new();
    let mut cursor = 0usize;
    while let Some(relative_start) = lowercase[cursor..].find("<b>") {
        let start = cursor + relative_start + 3;
        let Some(relative_end) = lowercase[start..].find("</b>") else {
            break;
        };
        let end = start + relative_end;
        let value = normalize_keyword(&text[start..end]);
        if !value.is_empty() {
            result.insert(value);
        }
        cursor = end + 4;
    }
    result
}

const fn card_type_from_library_id(value: Option<u16>) -> CardType {
    match value {
        Some(3) => CardType::Hero,
        Some(4) => CardType::Minion,
        Some(5) => CardType::Spell,
        Some(7) => CardType::Weapon,
        Some(10) => CardType::HeroPower,
        Some(39) => CardType::Location,
        Some(_) | None => CardType::Unknown,
    }
}

fn invalid(message: impl Into<String>) -> CardPoolError {
    CardPoolError::new("snapshot_invalid", message)
}

fn resolve_max_age_hours() -> Result<f64, CardPoolError> {
    let hours = match env::var(MAX_AGE_ENVIRONMENT_VARIABLE) {
        Ok(value) if !value.trim().is_empty() => value.trim().parse::<f64>().map_err(|_| {
            CardPoolError::new("configuration_invalid", "官方卡池最大年龄配置无效。")
        })?,
        Ok(_) | Err(env::VarError::NotPresent) => DEFAULT_MAX_AGE_HOURS,
        Err(env::VarError::NotUnicode(_)) => {
            return Err(CardPoolError::new(
                "configuration_invalid",
                "官方卡池最大年龄配置无效。",
            ));
        }
    };
    if !hours.is_finite() || hours <= 0.0 || hours > MAX_CONFIGURED_AGE_HOURS {
        return Err(CardPoolError::new(
            "configuration_invalid",
            "官方卡池最大年龄配置超出允许范围。",
        ));
    }
    Ok(hours)
}

fn validate_max_age(max_age: StdDuration) -> Result<f64, CardPoolError> {
    let hours = max_age.as_secs_f64() / 3600.0;
    if !hours.is_finite() || hours <= 0.0 || hours > MAX_CONFIGURED_AGE_HOURS {
        return Err(CardPoolError::new(
            "configuration_invalid",
            "官方卡池最大年龄配置超出允许范围。",
        ));
    }
    Ok(hours)
}

fn read_bounded_json(path: &Path, maximum_bytes: u64) -> Result<Vec<u8>, CardPoolError> {
    let metadata = fs::metadata(path)
        .map_err(|_| CardPoolError::new("snapshot_file_missing", "官方卡池快照文件缺失。"))?;
    let declared_length = metadata.len();
    if !metadata.is_file() || declared_length == 0 || declared_length > maximum_bytes {
        return Err(CardPoolError::new(
            "snapshot_file_size_invalid",
            "官方卡池快照文件大小无效。",
        ));
    }
    let capacity = usize::try_from(declared_length).map_err(|_| {
        CardPoolError::new("snapshot_file_size_invalid", "官方卡池快照文件大小无效。")
    })?;
    let mut bytes = Vec::with_capacity(capacity);
    File::open(path)
        .and_then(|file| {
            file.take(maximum_bytes + 1)
                .read_to_end(&mut bytes)
                .map(|_| ())
        })
        .map_err(|_| {
            CardPoolError::new("snapshot_file_unreadable", "无法读取官方卡池快照文件。")
        })?;
    if bytes.len() != capacity
        || u64::try_from(bytes.len())
            .ok()
            .is_none_or(|len| len > maximum_bytes)
    {
        return Err(CardPoolError::new(
            "snapshot_file_size_invalid",
            "官方卡池快照文件在校验期间发生变化。",
        ));
    }
    Ok(bytes)
}

fn parse_json_object(bytes: &[u8]) -> Result<Map<String, Value>, CardPoolError> {
    serde_json::from_slice::<Value>(bytes)
        .map_err(|_| CardPoolError::new("snapshot_json_invalid", "官方卡池 JSON 无效。"))?
        .as_object()
        .cloned()
        .ok_or_else(|| invalid("官方卡池 JSON 根节点必须是对象。"))
}

fn required_object<'a>(
    object: &'a Map<String, Value>,
    field: &str,
) -> Result<&'a Map<String, Value>, CardPoolError> {
    object
        .get(field)
        .and_then(Value::as_object)
        .ok_or_else(|| invalid("官方卡池快照缺少必需对象字段。"))
}

fn required_nonempty_string<'a>(
    object: &'a Map<String, Value>,
    field: &str,
) -> Result<&'a str, CardPoolError> {
    object
        .get(field)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| invalid("官方卡池快照缺少必需字符串字段。"))
}

fn required_u64(
    object: &Map<String, Value>,
    field: &str,
    minimum: u64,
) -> Result<u64, CardPoolError> {
    object
        .get(field)
        .and_then(Value::as_u64)
        .filter(|value| *value >= minimum)
        .ok_or_else(|| invalid("官方卡池快照包含无效整数。"))
}

fn optional_u16(object: &Map<String, Value>, field: &str) -> Result<Option<u16>, CardPoolError> {
    let Some(value) = object.get(field) else {
        return Ok(None);
    };
    if value.is_null() {
        return Ok(None);
    }
    let value = value
        .as_u64()
        .and_then(|value| u16::try_from(value).ok())
        .ok_or_else(|| invalid("官方卡池生成池元数据包含无效整数。"))?;
    Ok(Some(value))
}

fn optional_u16_array(
    object: &Map<String, Value>,
    field: &str,
) -> Result<BTreeSet<u16>, CardPoolError> {
    let Some(value) = object.get(field) else {
        return Ok(BTreeSet::new());
    };
    if value.is_null() {
        return Ok(BTreeSet::new());
    }
    let values = value
        .as_array()
        .ok_or_else(|| invalid("官方卡池生成池元数据包含无效数组。"))?;
    let mut result = BTreeSet::new();
    for value in values {
        let value = value
            .as_u64()
            .and_then(|value| u16::try_from(value).ok())
            .ok_or_else(|| invalid("官方卡池生成池元数据包含无效数组整数。"))?;
        result.insert(value);
    }
    Ok(result)
}

fn normalized_hash(value: Option<&Value>) -> String {
    value
        .and_then(Value::as_str)
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase()
}

fn valid_sha256(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn sha256_hex(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
}

fn parse_timestamp(value: Option<&Value>) -> Result<OffsetDateTime, CardPoolError> {
    let raw = value
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            CardPoolError::new("snapshot_timestamp_invalid", "官方卡池快照时间缺失或无效。")
        })?;
    let normalized = if let Some(prefix) = raw.strip_suffix('z') {
        format!("{prefix}Z")
    } else {
        raw.to_owned()
    };
    OffsetDateTime::parse(&normalized, &Rfc3339).map_err(|_| {
        CardPoolError::new("snapshot_timestamp_invalid", "官方卡池快照时间缺失或无效。")
    })
}

fn validate_timestamp(
    object: &Map<String, Value>,
    field: &str,
    now: OffsetDateTime,
    max_age_seconds: f64,
) -> Result<OffsetDateTime, CardPoolError> {
    let utc_field = format!("{field}_utc");
    let parsed = parse_timestamp(object.get(&utc_field).or_else(|| object.get(field)))?;
    let delta = (parsed - now).as_seconds_f64();
    if delta > FUTURE_CLOCK_SKEW_SECONDS as f64 {
        return Err(CardPoolError::new(
            "snapshot_timestamp_in_future",
            "官方卡池快照时间超出允许的时钟偏差。",
        ));
    }
    if -delta > max_age_seconds {
        return Err(CardPoolError::new("snapshot_stale", "官方卡池快照已过期。"));
    }
    Ok(parsed)
}

fn format_timestamp(value: OffsetDateTime) -> Result<String, CardPoolError> {
    value.format(&Rfc3339).map_err(|_| {
        CardPoolError::new("snapshot_timestamp_invalid", "官方卡池快照时间无法规范化。")
    })
}

fn validate_rules_coverage(coverage: &Map<String, Value>) -> Result<(), CardPoolError> {
    if coverage.get("rules_coverage").and_then(Value::as_bool) != Some(false) {
        return Err(invalid("官方卡池快照不得声明卡牌规则覆盖。"));
    }
    Ok(())
}

fn validate_card_defs(
    manifest: &Map<String, Value>,
    card_defs_path: Option<&Path>,
) -> Result<(String, u64, String), CardPoolError> {
    let card_defs = required_object(manifest, "card_defs").map_err(|_| {
        CardPoolError::new("card_defs_metadata_invalid", "CardDefs 元数据缺失或无效。")
    })?;
    if card_defs.get("file_name").and_then(Value::as_str) != Some("CardDefs.base.xml") {
        return Err(CardPoolError::new(
            "card_defs_metadata_invalid",
            "CardDefs 文件名元数据无效。",
        ));
    }
    let build = card_defs
        .get("build")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty() && value.len() <= 128)
        .ok_or_else(|| {
            CardPoolError::new("card_defs_metadata_invalid", "CardDefs 版本元数据无效。")
        })?;
    let declared_bytes = required_u64(card_defs, "bytes", 1).map_err(|_| {
        CardPoolError::new("card_defs_metadata_invalid", "CardDefs 大小元数据无效。")
    })?;
    if declared_bytes > MAX_CARD_DEFS_BYTES {
        return Err(CardPoolError::new(
            "card_defs_metadata_invalid",
            "CardDefs 大小元数据超出安全范围。",
        ));
    }
    let declared_hash = normalized_hash(card_defs.get("sha256"));
    if !valid_sha256(&declared_hash) {
        return Err(CardPoolError::new(
            "card_defs_metadata_invalid",
            "CardDefs 哈希元数据无效。",
        ));
    }

    let selected_path = card_defs_path
        .map(Path::to_path_buf)
        .or_else(default_hdt_card_defs_path)
        .ok_or_else(|| CardPoolError::new("card_defs_missing", "当前 HDT CardDefs 不可用。"))?;
    if selected_path
        .file_name()
        .and_then(|name| name.to_str())
        .is_none_or(|name| !name.eq_ignore_ascii_case("CardDefs.base.xml"))
    {
        return Err(CardPoolError::new(
            "card_defs_missing",
            "当前 HDT CardDefs 不可用。",
        ));
    }
    let metadata = fs::metadata(&selected_path)
        .map_err(|_| CardPoolError::new("card_defs_missing", "当前 HDT CardDefs 不可用。"))?;
    if !metadata.is_file() {
        return Err(CardPoolError::new(
            "card_defs_missing",
            "当前 HDT CardDefs 不可用。",
        ));
    }
    if metadata.len() != declared_bytes {
        return Err(CardPoolError::new(
            "card_defs_size_mismatch",
            "当前 HDT CardDefs 大小与官方卡池快照不匹配。",
        ));
    }
    let mut file = File::open(&selected_path)
        .map_err(|_| CardPoolError::new("card_defs_unreadable", "无法读取当前 HDT CardDefs。"))?;
    let mut hasher = Sha256::new();
    let mut prefix = Vec::with_capacity(64 * 1024);
    let mut total = 0_u64;
    let mut buffer = vec![0_u8; 1024 * 1024];
    loop {
        let read = file.read(&mut buffer).map_err(|_| {
            CardPoolError::new("card_defs_unreadable", "无法读取当前 HDT CardDefs。")
        })?;
        if read == 0 {
            break;
        }
        total = total.checked_add(read as u64).ok_or_else(|| {
            CardPoolError::new("card_defs_unreadable", "当前 HDT CardDefs 大小无效。")
        })?;
        if prefix.len() < 64 * 1024 {
            let remaining = (64 * 1024) - prefix.len();
            prefix.extend_from_slice(&buffer[..read.min(remaining)]);
        }
        hasher.update(&buffer[..read]);
        if total > MAX_CARD_DEFS_BYTES {
            return Err(CardPoolError::new(
                "card_defs_size_mismatch",
                "当前 HDT CardDefs 大小与官方卡池快照不匹配。",
            ));
        }
    }
    if total != declared_bytes {
        return Err(CardPoolError::new(
            "card_defs_size_mismatch",
            "当前 HDT CardDefs 在校验期间发生变化。",
        ));
    }
    let actual_hash = format!("{:X}", hasher.finalize());
    if actual_hash.to_lowercase() != declared_hash {
        return Err(CardPoolError::new(
            "card_defs_hash_mismatch",
            "当前 HDT CardDefs 哈希与官方卡池快照不匹配。",
        ));
    }
    let actual_build = extract_card_defs_build(&prefix).ok_or_else(|| {
        CardPoolError::new(
            "card_defs_build_invalid",
            "无法验证当前 HDT CardDefs 版本。",
        )
    })?;
    if actual_build != build {
        return Err(CardPoolError::new(
            "card_defs_build_mismatch",
            "当前 HDT CardDefs 版本与官方卡池快照不匹配。",
        ));
    }
    Ok((build.to_owned(), total, actual_hash))
}

fn extract_card_defs_build(prefix: &[u8]) -> Option<String> {
    let lower = prefix
        .iter()
        .map(u8::to_ascii_lowercase)
        .collect::<Vec<_>>();
    let marker = b"<carddefs";
    let mut offset = 0;
    while let Some(relative) = find_bytes(&lower[offset..], marker) {
        let start = offset + relative;
        let after_marker = start + marker.len();
        if lower
            .get(after_marker)
            .is_none_or(|byte| !byte.is_ascii_whitespace() && *byte != b'>')
        {
            offset = after_marker;
            continue;
        }
        let end = lower[after_marker..]
            .iter()
            .position(|byte| *byte == b'>')
            .map(|relative| after_marker + relative)?;
        let tag = &lower[after_marker..end];
        let original_tag = &prefix[after_marker..end];
        let mut attribute_offset = 0;
        while let Some(relative) = find_bytes(&tag[attribute_offset..], b"build") {
            let name_start = attribute_offset + relative;
            let name_end = name_start + 5;
            let preceding_ok = name_start == 0 || tag[name_start - 1].is_ascii_whitespace();
            let following_ok = tag
                .get(name_end)
                .is_some_and(|byte| byte.is_ascii_whitespace() || *byte == b'=');
            if !preceding_ok || !following_ok {
                attribute_offset = name_end;
                continue;
            }
            let mut cursor = name_end;
            while tag.get(cursor).is_some_and(u8::is_ascii_whitespace) {
                cursor += 1;
            }
            if tag.get(cursor) != Some(&b'=') {
                attribute_offset = name_end;
                continue;
            }
            cursor += 1;
            while tag.get(cursor).is_some_and(u8::is_ascii_whitespace) {
                cursor += 1;
            }
            let quote = *tag.get(cursor)?;
            if quote != b'\'' && quote != b'"' {
                return None;
            }
            cursor += 1;
            let value_end = tag[cursor..]
                .iter()
                .position(|byte| *byte == quote)
                .map(|relative| cursor + relative)?;
            let value = std::str::from_utf8(&original_tag[cursor..value_end])
                .ok()?
                .trim();
            return (!value.is_empty()).then(|| value.to_owned());
        }
        return None;
    }
    None
}

fn find_bytes(haystack: &[u8], needle: &[u8]) -> Option<usize> {
    (!needle.is_empty() && haystack.len() >= needle.len())
        .then(|| {
            haystack
                .windows(needle.len())
                .position(|window| window == needle)
        })
        .flatten()
}

fn state_format(state: &GameState) -> &str {
    let metadata_format = metadata_string(state, "format");
    let metadata_mode = metadata_string(state, "game_mode");
    let normalized = format!("{} {metadata_format} {metadata_mode}", state.mode).to_lowercase();
    if normalized.contains("arena") {
        "arena"
    } else if normalized.contains("standard") || normalized.contains("ranked") {
        "standard"
    } else {
        ""
    }
}

fn metadata_string<'a>(state: &'a GameState, key: &str) -> &'a str {
    match state.metadata.get(key) {
        Some(JsonScalar::String(value)) => value,
        _ => "",
    }
}

#[must_use]
pub fn default_hdt_card_defs_path() -> Option<PathBuf> {
    env::var_os("APPDATA").map(|app_data| {
        PathBuf::from(app_data)
            .join("HearthstoneDeckTracker")
            .join("CardDefs")
            .join("CardDefs.base.xml")
    })
}

#[must_use]
pub fn default_official_card_pool_directory() -> Option<PathBuf> {
    env::var_os("APPDATA").map(|app_data| {
        PathBuf::from(app_data)
            .join("HearthstoneDeckTracker")
            .join("MetaCompanion")
            .join("AdvisorData")
            .join("OfficialCardPools")
    })
}

#[must_use]
pub fn select_official_card_pool_directory(
    advisor_data: Option<&Path>,
    data_dir: Option<&Path>,
) -> Option<PathBuf> {
    advisor_data
        .map(|path| path.join("OfficialCardPools"))
        .or_else(|| {
            data_dir.and_then(|path| {
                path.parent()
                    .map(|parent| parent.join("AdvisorData").join("OfficialCardPools"))
            })
        })
        .or_else(default_official_card_pool_directory)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn card_defs_build_parser_accepts_attribute_spacing_and_both_quote_styles() {
        assert_eq!(
            extract_card_defs_build(br#"<?xml?><CardDefs build="247416"><Entity/>"#),
            Some("247416".to_owned())
        );
        assert_eq!(
            extract_card_defs_build(b"<carddefs other='x' BUILD = '999999'>"),
            Some("999999".to_owned())
        );
        assert_eq!(extract_card_defs_build(b"<NotCardDefs build='1'>"), None);
    }

    #[test]
    fn official_pool_path_priority_is_explicit_then_managed_parent() {
        let explicit = Path::new("explicit-advisor-data");
        let managed = Path::new("plugin-data").join("AdvisorWorker");
        assert_eq!(
            select_official_card_pool_directory(Some(explicit), Some(&managed)),
            Some(explicit.join("OfficialCardPools"))
        );
        assert_eq!(
            select_official_card_pool_directory(None, Some(&managed)),
            Some(
                Path::new("plugin-data")
                    .join("AdvisorData")
                    .join("OfficialCardPools")
            )
        );
    }

    #[test]
    fn unavailable_health_is_stable_and_never_claims_rules_coverage() {
        let health = OfficialCardPoolBundle::unavailable().health_payload();
        assert_eq!(health["available"], false);
        assert_eq!(health["reason"], "snapshot_unavailable");
        assert_eq!(health["rules_coverage"], false);
        assert_eq!(health["future_clock_skew_seconds"], 300);
    }

    #[test]
    fn stochastic_candidate_cap_bounds_cartesian_products() {
        assert_eq!(bounded_integer_root(96, 1), 96);
        assert_eq!(bounded_integer_root(96, 2), 9);
        assert_eq!(bounded_integer_root(96, 3), 4);
        assert_eq!(bounded_integer_root(96, 4), 3);
        assert!(bounded_integer_root(96, 3).pow(3) <= 96);
        assert!((bounded_integer_root(96, 3) + 1).pow(3) > 96);
    }
}
