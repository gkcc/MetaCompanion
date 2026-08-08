//! Conservative adapter for the public HDT advisor snapshot schema.
//!
//! This module translates only fields already exposed by HDT. Hidden entities are
//! never inferred. Card text stays unsupported unless the entire normalized text
//! is a sequence of intrinsic keywords backed by live flags/tags; all other
//! structured effects are applied separately by [`crate::rules`].

use std::collections::{BTreeMap, HashSet};

use serde_json::{Map, Value, json};

use crate::error::SolverError;
use crate::model::{GameState, SolveRequest};
use crate::rules::{normalize_card_text, normalized_text_sha256};

const GAME_TAG_EXHAUSTED: i64 = 43;
const GAME_TAG_LIFESTEAL: i64 = 685;
const GAME_TAG_HERO_POWER_DISABLED: i64 = 777;
const GAME_TAG_HAS_ACTIVATE_POWER: i64 = 2840;

const PUBLIC_PLAYER_RULE_TAGS: &[(&str, i64)] = &[
    ("STEADY_SHOT_CAN_TARGET", 383),
    ("CURRENT_HEROPOWER_DAMAGE_BONUS", 395),
    ("HERO_POWER_DOUBLE", 366),
    ("HEROPOWER_DAMAGE", 396),
    ("HERO_POWER_DISABLED", GAME_TAG_HERO_POWER_DISABLED),
];

const HIDDEN_OPPONENT_ZONE_NAMES: &[&str] = &["HAND", "DECK", "SETASIDE", "SECRET"];
const HIDDEN_OPPONENT_ZONE_IDS: &[i64] = &[2, 3, 6, 7];
const PUBLIC_ENTITY_LOCATION_KEYS: &[&str] = &[
    "entity_id",
    "zone",
    "zone_id",
    "zone_position",
    "position",
    "controller",
    "controller_id",
    "visibility",
];
const PUBLIC_ENTITY_LOCATION_TAGS: &[&str] = &["ZONE", "ZONE_POSITION", "CONTROLLER"];

const GENERIC_MECHANICS: &[&str] = &[
    "taunt",
    "divine_shield",
    "stealth",
    "lifesteal",
    "poisonous",
    "windfury",
    "mega_windfury",
    "rush",
    "charge",
    "reborn",
    "dormant",
    "immune",
];

const INTRINSIC_KEYWORD_RULE_ID: &str = "hdt-intrinsic-keywords-v1";

fn raw_flag_or_tag(
    raw: &Map<String, Value>,
    tags: &Map<String, Value>,
    raw_key: &str,
    tag_name: &str,
) -> bool {
    raw.get(raw_key).and_then(Value::as_bool).unwrap_or(false) || named_tag(tags, tag_name) != 0
}

fn elusive_targeting_evidence(tags: &Map<String, Value>) -> bool {
    named_tag(tags, "ELUSIVE") != 0
        || (named_tag(tags, "CANT_BE_TARGETED_BY_SPELLS") != 0
            && named_tag(tags, "CANT_BE_TARGETED_BY_HERO_POWERS") != 0)
}

fn mechanic_is_structurally_modeled(mechanic: &str, tags: &Map<String, Value>) -> bool {
    GENERIC_MECHANICS.contains(&mechanic)
        || (matches!(
            mechanic,
            "elusive" | "cant_be_targeted_by_spells" | "cant_be_targeted_by_hero_powers"
        ) && elusive_targeting_evidence(tags))
}

/// Return true only when the entire visible text is a sequence of intrinsic
/// keywords whose live HDT flags/tags are present.  Text alone is never treated
/// as rules evidence: a missing flag keeps the card fail-closed.
fn intrinsic_keyword_text_is_structurally_covered(
    value: &str,
    raw: &Map<String, Value>,
    tags: &Map<String, Value>,
) -> bool {
    let normalized = normalize_card_text(value)
        .to_ascii_lowercase()
        .replace([',', '.', ';', ':', '/'], " ");
    let tokens = normalized.split_whitespace().collect::<Vec<_>>();
    if tokens.is_empty() {
        return false;
    }

    let mut index = 0usize;
    while index < tokens.len() {
        let (keyword, width) = if tokens[index..].starts_with(&["divine", "shield"]) {
            ("divine_shield", 2)
        } else if tokens[index..].starts_with(&["mega", "windfury"]) {
            ("mega_windfury", 2)
        } else {
            (tokens[index].trim_matches('-'), 1)
        };
        let covered = match keyword {
            "taunt" => raw_flag_or_tag(raw, tags, "has_taunt", "TAUNT"),
            "divine_shield" => raw_flag_or_tag(raw, tags, "has_divine_shield", "DIVINE_SHIELD"),
            "stealth" => raw_flag_or_tag(raw, tags, "has_stealth", "STEALTH"),
            "lifesteal" => raw_flag_or_tag(raw, tags, "has_lifesteal", "LIFESTEAL"),
            "poisonous" => raw_flag_or_tag(raw, tags, "has_poisonous", "POISONOUS"),
            "windfury" => raw_flag_or_tag(raw, tags, "has_windfury", "WINDFURY"),
            "mega-windfury" | "mega_windfury" => {
                raw_flag_or_tag(raw, tags, "has_mega_windfury", "MEGA_WINDFURY")
            }
            "rush" => raw_flag_or_tag(raw, tags, "has_rush", "RUSH"),
            "charge" => raw_flag_or_tag(raw, tags, "has_charge", "CHARGE"),
            "reborn" => raw_flag_or_tag(raw, tags, "has_reborn", "REBORN"),
            "immune" => raw_flag_or_tag(raw, tags, "is_immune", "IMMUNE"),
            "elusive" => elusive_targeting_evidence(tags),
            _ => false,
        };
        if !covered {
            return false;
        }
        index += width;
    }
    true
}

fn object<'a>(value: &'a Value, path: &str) -> Result<&'a Map<String, Value>, SolverError> {
    value
        .as_object()
        .ok_or_else(|| SolverError::schema(path, "must be an object"))
}

fn array<'a>(
    raw: &'a Map<String, Value>,
    key: &str,
    path: &str,
) -> Result<&'a [Value], SolverError> {
    match raw.get(key) {
        None => Ok(&[]),
        Some(value) => value
            .as_array()
            .map(Vec::as_slice)
            .ok_or_else(|| SolverError::schema(format!("{path}.{key}"), "must be an array")),
    }
}

fn text(raw: &Map<String, Value>, key: &str, fallback: &str) -> String {
    raw.get(key)
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .unwrap_or(fallback)
        .to_owned()
}

fn scalar_text(value: Option<&Value>, fallback: &str) -> String {
    match value {
        Some(Value::String(value)) => value.clone(),
        Some(Value::Number(value)) => value.to_string(),
        Some(Value::Bool(value)) => value.to_string(),
        _ => fallback.to_owned(),
    }
}

fn integer(raw: &Map<String, Value>, key: &str, fallback: i64) -> i64 {
    raw.get(key)
        .and_then(|value| {
            value
                .as_i64()
                .or_else(|| value.as_u64().and_then(|item| i64::try_from(item).ok()))
        })
        .unwrap_or(fallback)
}

fn nonnegative_u16(value: i64, path: &str) -> Result<u16, SolverError> {
    u16::try_from(value.max(0))
        .map_err(|_| SolverError::schema(path, "must fit an unsigned 16-bit gameplay value"))
}

fn scalar_map(value: Option<&Value>) -> Map<String, Value> {
    value
        .and_then(Value::as_object)
        .map(|raw| {
            raw.iter()
                .filter(|(_, item)| {
                    item.is_null() || item.is_boolean() || item.is_number() || item.is_string()
                })
                .map(|(key, item)| (key.clone(), item.clone()))
                .collect()
        })
        .unwrap_or_default()
}

fn numeric_tag_value(value: &Value) -> Option<i64> {
    match value {
        Value::Bool(value) => Some(i64::from(*value)),
        Value::Number(value) => value
            .as_i64()
            .or_else(|| value.as_u64().and_then(|item| i64::try_from(item).ok()))
            .or_else(|| {
                value.as_f64().and_then(|item| {
                    (item.is_finite() && item.fract() == 0.0).then_some(item as i64)
                })
            }),
        Value::String(value) => value.trim().parse().ok(),
        Value::Null | Value::Array(_) | Value::Object(_) => None,
    }
}

fn named_tag(tags: &Map<String, Value>, name: &str) -> i64 {
    tags.iter()
        .find(|(key, _)| key.eq_ignore_ascii_case(name))
        .and_then(|(_, value)| numeric_tag_value(value))
        .unwrap_or(0)
}

fn game_tag(tags: &Map<String, Value>, name: &str, enum_id: i64) -> Option<i64> {
    tags.iter()
        .find(|(key, _)| {
            key.eq_ignore_ascii_case(name)
                || key.as_str() == enum_id.to_string()
                || (name == "HERO_POWER_DOUBLE"
                    && key.eq_ignore_ascii_case("TAG_HERO_POWER_DOUBLE"))
        })
        .and_then(|(_, value)| numeric_tag_value(value))
}

fn normalized_zone_text(value: &str) -> String {
    value
        .trim()
        .to_ascii_uppercase()
        .replace(['_', '-', ' '], "")
}

fn normalized_zone_name(value: Option<&Value>) -> String {
    normalized_zone_text(value.and_then(Value::as_str).unwrap_or(""))
}

fn zone_container_hint(key: &str) -> &'static str {
    match key {
        "hand" => "HAND",
        "deck" => "DECK",
        "setaside" | "set_aside" => "SETASIDE",
        "secret" | "secrets" => "SECRET",
        _ => "",
    }
}

fn hidden_entity(raw: &Map<String, Value>, in_opponent: bool, zone_hint: &str) -> bool {
    if raw
        .get("visibility")
        .and_then(Value::as_str)
        .is_some_and(|value| value.trim().to_ascii_lowercase().contains("hidden"))
    {
        return true;
    }
    if !in_opponent {
        return false;
    }
    if HIDDEN_OPPONENT_ZONE_NAMES.contains(&normalized_zone_text(zone_hint).as_str())
        || HIDDEN_OPPONENT_ZONE_NAMES.contains(&normalized_zone_name(raw.get("zone")).as_str())
    {
        return true;
    }
    let zone_id = raw
        .get("zone_id")
        .or_else(|| {
            raw.get("tags")
                .and_then(Value::as_object)
                .and_then(|tags| tags.get("ZONE").or_else(|| tags.get("zone")))
        })
        .and_then(Value::as_i64);
    zone_id.is_some_and(|value| HIDDEN_OPPONENT_ZONE_IDS.contains(&value))
}

fn public_hidden_entity_location(raw: &Map<String, Value>) -> Value {
    let mut result = Map::new();
    for (key, value) in raw {
        if PUBLIC_ENTITY_LOCATION_KEYS
            .iter()
            .any(|allowed| key.eq_ignore_ascii_case(allowed))
        {
            result.insert(key.clone(), value.clone());
        }
    }
    if let Some(tags) = raw.get("tags").and_then(Value::as_object) {
        let safe_tags = tags
            .iter()
            .filter(|(key, value)| {
                PUBLIC_ENTITY_LOCATION_TAGS.contains(&key.to_ascii_uppercase().as_str())
                    && metadata_scalar(value)
            })
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect::<Map<_, _>>();
        if !safe_tags.is_empty() {
            result.insert("tags".to_owned(), Value::Object(safe_tags));
        }
    }
    result.insert("visibility".to_owned(), json!("hidden"));
    Value::Object(result)
}

fn redact_hidden_value(value: &Value, in_opponent: bool, zone_hint: &str) -> Value {
    match value {
        Value::Object(raw) => {
            if hidden_entity(raw, in_opponent, zone_hint) {
                return public_hidden_entity_location(raw);
            }
            Value::Object(
                raw.iter()
                    .map(|(key, child)| {
                        let normalized_key = key.to_ascii_lowercase();
                        let child_in_opponent = in_opponent || normalized_key == "opponent";
                        let child_zone = if child_in_opponent {
                            zone_container_hint(&normalized_key)
                        } else {
                            ""
                        };
                        (
                            key.clone(),
                            redact_hidden_value(child, child_in_opponent, child_zone),
                        )
                    })
                    .collect(),
            )
        }
        Value::Array(items) => Value::Array(
            items
                .iter()
                .map(|item| redact_hidden_value(item, in_opponent, zone_hint))
                .collect(),
        ),
        _ => value.clone(),
    }
}

fn redact_hidden_entities(value: &Value) -> Value {
    redact_hidden_value(value, false, "")
}

fn normalized_mechanic(value: &str) -> String {
    value.trim().to_ascii_lowercase().replace(['-', ' '], "_")
}

fn entity_id(raw: &Map<String, Value>, fallback: &str, path: &str) -> Result<String, SolverError> {
    let value = match raw.get("entity_id") {
        None => fallback.to_owned(),
        Some(Value::String(value)) => value.clone(),
        Some(Value::Number(value)) => value.to_string(),
        Some(_) => {
            return Err(SolverError::schema(
                format!("{path}.entity_id"),
                "must be an integer or string",
            ));
        }
    };
    Ok(if value == "0" || value.is_empty() {
        fallback.to_owned()
    } else {
        value
    })
}

#[allow(clippy::too_many_lines)]
fn adapt_entity(value: &Value, path: &str, fallback_entity_id: &str) -> Result<Value, SolverError> {
    let raw = object(value, path)?;
    let visibility = text(raw, "visibility", "");
    let hidden = visibility.eq_ignore_ascii_case("hidden");
    let raw_type = text(raw, "card_type", "UNKNOWN").to_ascii_uppercase();
    let card_type = match raw_type.as_str() {
        "HERO" | "MINION" | "SPELL" | "WEAPON" | "HERO_POWER" | "LOCATION" => raw_type,
        _ => "UNKNOWN".to_owned(),
    };
    let health_value = integer(raw, "health", 0);
    let damage_value = integer(raw, "damage", 0).max(0);
    let health = nonnegative_u16(health_value, &format!("{path}.health"))?;
    let damage = nonnegative_u16(damage_value, &format!("{path}.damage"))?;
    let current_health = health.saturating_sub(damage);
    let attack = nonnegative_u16(integer(raw, "attack", 0), &format!("{path}.attack"))?;
    let cost = nonnegative_u16(integer(raw, "cost", 0), &format!("{path}.cost"))?;
    let raw_durability = integer(raw, "durability", 0);
    let durability_value =
        if raw_durability == 0 && matches!(card_type.as_str(), "WEAPON" | "LOCATION") {
            // Live HDT entities expose weapon durability and Location charges as
            // HEALTH/DAMAGE even when no DURABILITY tag is present.
            health_value
        } else {
            raw_durability
        };
    let durability = nonnegative_u16(durability_value, &format!("{path}.durability"))?;
    let current_durability = durability.saturating_sub(damage);
    let tags = scalar_map(raw.get("tags"));
    let mechanics = raw
        .get("mechanics")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(normalized_mechanic)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let card_text = raw
        .get("english_text")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .or_else(|| raw.get("card_text").and_then(Value::as_str))
        .unwrap_or("")
        .to_owned();
    let keyword_text_covered = card_type != "HERO"
        && intrinsic_keyword_text_is_structurally_covered(&card_text, raw, &tags);
    let mut unsupported = mechanics
        .iter()
        .filter(|item| !mechanic_is_structurally_modeled(item, &tags))
        .cloned()
        .collect::<Vec<_>>();
    if !card_text.trim().is_empty() && card_type != "HERO" && !keyword_text_covered {
        unsupported.push("card_text_not_parsed".to_owned());
    }
    let mut seen = HashSet::new();
    unsupported.retain(|item| seen.insert(item.clone()));

    let mega_windfury = raw
        .get("has_mega_windfury")
        .and_then(Value::as_bool)
        .unwrap_or(false)
        || named_tag(&tags, "MEGA_WINDFURY") != 0;
    let windfury = mega_windfury
        || raw
            .get("has_windfury")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        || named_tag(&tags, "WINDFURY") != 0;
    let lifesteal = raw
        .get("has_lifesteal")
        .and_then(Value::as_bool)
        .unwrap_or(false)
        || game_tag(&tags, "LIFESTEAL", GAME_TAG_LIFESTEAL).unwrap_or(0) != 0;
    let dormant = raw
        .get("is_dormant")
        .and_then(Value::as_bool)
        .unwrap_or(false)
        || named_tag(&tags, "DORMANT") != 0;
    let immune = raw
        .get("is_immune")
        .and_then(Value::as_bool)
        .unwrap_or(false)
        || named_tag(&tags, "IMMUNE") != 0;
    let exhausted = raw
        .get("is_exhausted")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let frozen = raw
        .get("is_frozen")
        .and_then(Value::as_bool)
        .unwrap_or(false);
    let mut attack_limit = if mega_windfury {
        4
    } else if windfury {
        2
    } else {
        1
    };
    attack_limit += named_tag(&tags, "EXTRA_ATTACKS_THIS_TURN").max(0);
    let attacks_used = named_tag(&tags, "NUM_ATTACKS_THIS_TURN").max(0);
    let attacks_remaining_known = tags.iter().any(|(key, value)| {
        (key.eq_ignore_ascii_case("NUM_ATTACKS_THIS_TURN") || key.as_str() == "297")
            && numeric_tag_value(value).is_some()
    });
    let can_attack = matches!(card_type.as_str(), "HERO" | "MINION")
        && attack > 0
        && !exhausted
        && !frozen
        && !dormant
        && attack_limit > attacks_used;
    let attacks_remaining = if can_attack {
        u8::try_from(attack_limit - attacks_used).map_err(|_| {
            SolverError::schema(
                format!("{path}.tags"),
                "attack count exceeds supported range",
            )
        })?
    } else {
        0
    };
    let zone = text(raw, "zone", "").to_ascii_uppercase();
    let turns_in_play = tags
        .iter()
        .find(|(key, _)| key.eq_ignore_ascii_case("NUM_TURNS_IN_PLAY"))
        .and_then(|(_, value)| numeric_tag_value(value));
    let summoned_this_turn = card_type == "MINION" && zone == "PLAY" && turns_in_play == Some(0);
    let keyword_text_sha256 = if keyword_text_covered {
        normalized_text_sha256(&card_text)
    } else {
        String::new()
    };

    Ok(json!({
        "entity_id": entity_id(raw, fallback_entity_id, path)?,
        "card_id": text(raw, "card_id", "UNKNOWN"),
        "name": text(raw, "name", "Unknown card"),
        "card_type": card_type,
        "cost": cost,
        "attack": attack,
        "health": health,
        "current_health": current_health,
        "current_health_known": raw.get("health").is_some_and(Value::is_number)
            && raw.get("damage").is_some_and(Value::is_number),
        "playable": !hidden && raw.get("is_playable_card").and_then(Value::as_bool).unwrap_or(true),
        "can_attack": can_attack,
        "attacks_remaining": attacks_remaining,
        "attacks_remaining_known": attacks_remaining_known,
        "taunt": raw.get("has_taunt").and_then(Value::as_bool).unwrap_or(false),
        "divine_shield": raw.get("has_divine_shield").and_then(Value::as_bool).unwrap_or(false),
        "frozen": frozen,
        "stealth": raw.get("has_stealth").and_then(Value::as_bool).unwrap_or(false),
        "poisonous": raw.get("has_poisonous").and_then(Value::as_bool).unwrap_or(false),
        "lifesteal": lifesteal,
        "windfury": windfury,
        "mega_windfury": mega_windfury,
        "rush": raw.get("has_rush").and_then(Value::as_bool).unwrap_or(false) || named_tag(&tags, "RUSH") != 0,
        "charge": raw.get("has_charge").and_then(Value::as_bool).unwrap_or(false) || named_tag(&tags, "CHARGE") != 0,
        "reborn": raw.get("has_reborn").and_then(Value::as_bool).unwrap_or(false) || named_tag(&tags, "REBORN") != 0,
        "dormant": dormant,
        "immune": immune,
        "summoned_this_turn": summoned_this_turn,
        "durability": durability,
        "current_durability": current_durability,
        "effects": [],
        "effect_coverage": if unsupported.is_empty() { "generic" } else { "unsupported" },
        "unsupported_effects": unsupported,
        "prior_weight": 1.0,
        "tags": tags,
        "card_text": card_text,
        "rule_id": if keyword_text_covered { INTRINSIC_KEYWORD_RULE_ID } else { "" },
        "rule_version": if keyword_text_covered { INTRINSIC_KEYWORD_RULE_ID } else { "" },
        "rule_text_sha256": keyword_text_sha256,
        "visibility": visibility,
    }))
}

fn player_rule_tags(raw: &Map<String, Value>) -> (Map<String, Value>, bool) {
    let Some(tags) = raw
        .get("player_entity")
        .and_then(Value::as_object)
        .and_then(|entity| entity.get("tags"))
        .and_then(Value::as_object)
    else {
        return (Map::new(), false);
    };
    let mut selected = Map::new();
    for (name, id) in PUBLIC_PLAYER_RULE_TAGS {
        if let Some(value) = game_tag(tags, name, *id) {
            selected.insert((*name).to_owned(), json!(value));
        }
    }
    (selected, true)
}

fn hero_power_available(
    hero_power: Option<&Value>,
    raw_power: Option<&Value>,
    public_tags: &Map<String, Value>,
) -> bool {
    let (Some(_power), Some(raw)) = (hero_power, raw_power.and_then(Value::as_object)) else {
        return false;
    };
    let Some(tags) = raw.get("tags").and_then(Value::as_object) else {
        return false;
    };
    if game_tag(tags, "HAS_ACTIVATE_POWER", GAME_TAG_HAS_ACTIVATE_POWER).unwrap_or(0) == 0
        || raw
            .get("is_exhausted")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        || game_tag(tags, "EXHAUSTED", GAME_TAG_EXHAUSTED).unwrap_or(0) != 0
        || game_tag(tags, "HERO_POWER_DISABLED", GAME_TAG_HERO_POWER_DISABLED).unwrap_or(0) != 0
        || game_tag(
            public_tags,
            "HERO_POWER_DISABLED",
            GAME_TAG_HERO_POWER_DISABLED,
        )
        .unwrap_or(0)
            != 0
    {
        return false;
    }
    // Keep this flag as the public ready/not-exhausted state. Affordability is
    // checked separately when legal actions are enumerated. Conflating the two
    // loses a valid continuation when a card play changes a continuous aura and
    // makes the Hero Power free later in the same searched line.
    true
}

#[allow(clippy::too_many_lines)]
#[derive(Clone, Debug, Default)]
struct DeckIdentityRow {
    count: u16,
    origin: &'static str,
    card_type: String,
    cost: u16,
    name: String,
}

fn public_card_id(raw: &Map<String, Value>) -> Option<String> {
    raw.get("card_id")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty() && !value.eq_ignore_ascii_case("UNKNOWN"))
        .map(ToOwned::to_owned)
}

fn raw_entity_is_created(raw: &Map<String, Value>) -> bool {
    raw.get("is_created")
        .and_then(Value::as_bool)
        .unwrap_or(false)
}

fn add_deck_identity(
    rows: &mut BTreeMap<(String, &'static str), DeckIdentityRow>,
    card_id: String,
    origin: &'static str,
    count: u16,
    raw: Option<&Map<String, Value>>,
) {
    if count == 0 {
        return;
    }
    let entry = rows
        .entry((card_id, origin))
        .or_insert_with(|| DeckIdentityRow {
            origin,
            ..DeckIdentityRow::default()
        });
    entry.count = entry.count.saturating_add(count);
    if let Some(raw) = raw {
        entry.card_type = text(raw, "card_type", "UNKNOWN").to_ascii_uppercase();
        entry.cost = u16::try_from(integer(raw, "cost", 0).max(0)).unwrap_or(u16::MAX);
        entry.name = text(raw, "name", "");
    }
}

fn decrement_original_card(rows: &mut BTreeMap<String, u16>, raw: &Map<String, Value>) {
    if raw_entity_is_created(raw) {
        return;
    }
    let original = raw
        .get("original_card_id")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(ToOwned::to_owned)
        .or_else(|| public_card_id(raw));
    let Some(card_id) = original else {
        return;
    };
    if let Some(count) = rows.get_mut(&card_id) {
        *count = count.saturating_sub(1);
    }
}

fn known_deck_identity(
    raw: &Map<String, Value>,
    current_deck: Option<&Value>,
    deck_size: u16,
    path: &str,
) -> Result<(Vec<Value>, bool), SolverError> {
    let mut starting = BTreeMap::<String, u16>::new();
    let current_deck_known = current_deck
        .and_then(Value::as_object)
        .is_some_and(|deck| deck.get("is_known").and_then(Value::as_bool) == Some(true));
    if let Some(deck) = current_deck
        .and_then(Value::as_object)
        .filter(|_| current_deck_known)
    {
        let cards = deck
            .get("cards")
            .and_then(Value::as_array)
            .ok_or_else(|| SolverError::schema("state.current_deck.cards", "must be an array"))?;
        for (index, item) in cards.iter().enumerate() {
            let card = object(item, &format!("state.current_deck.cards[{index}]"))?;
            if card.get("is_sideboard").and_then(Value::as_bool) == Some(true) {
                continue;
            }
            let Some(card_id) = public_card_id(card) else {
                continue;
            };
            let count = nonnegative_u16(
                integer(card, "count", 0),
                &format!("state.current_deck.cards[{index}].count"),
            )?;
            let entry = starting.entry(card_id).or_default();
            *entry = entry.saturating_add(count);
        }
    }

    if !starting.is_empty() {
        for key in [
            "hand",
            "board",
            "graveyard",
            "secrets",
            "set_aside",
            "removed_from_game",
            "other_entities",
        ] {
            for item in array(raw, key, path)? {
                if let Some(entity) = item.as_object() {
                    let zone = normalized_zone_name(entity.get("zone"));
                    if zone != "DECK" {
                        decrement_original_card(&mut starting, entity);
                    }
                }
            }
        }
        for key in ["weapon"] {
            if let Some(entity) = raw.get(key).and_then(Value::as_object) {
                decrement_original_card(&mut starting, entity);
            }
        }
    }

    let mut rows = BTreeMap::<(String, &'static str), DeckIdentityRow>::new();
    for (card_id, count) in &starting {
        add_deck_identity(&mut rows, card_id.clone(), "started_in_deck", *count, None);
    }

    let mut visible_deck_counts =
        BTreeMap::<String, (u16, bool, Option<&Map<String, Value>>)>::new();
    for item in array(raw, "deck", path)? {
        let Some(entity) = item.as_object() else {
            continue;
        };
        let Some(card_id) = public_card_id(entity) else {
            continue;
        };
        let created = raw_entity_is_created(entity) || !starting.contains_key(&card_id);
        let entry = visible_deck_counts
            .entry(card_id)
            .or_insert((0, created, Some(entity)));
        entry.0 = entry.0.saturating_add(1);
        entry.1 |= created;
    }
    for (card_id, (count, created, entity)) in visible_deck_counts {
        if created {
            add_deck_identity(&mut rows, card_id, "generated", count, entity);
        }
    }

    // HDT may know identities without exposing a corresponding deck-zone entity.
    // Use them only when no selected deck list can already account for that ID,
    // avoiding a double count of ordinary constructed-deck cards.
    for (index, item) in array(raw, "known_cards_in_deck", path)?.iter().enumerate() {
        let card = object(item, &format!("{path}.known_cards_in_deck[{index}]"))?;
        let Some(card_id) = public_card_id(card) else {
            continue;
        };
        if starting.contains_key(&card_id) || rows.keys().any(|(known_id, _)| known_id == &card_id)
        {
            continue;
        }
        let count = nonnegative_u16(
            integer(card, "count", 0),
            &format!("{path}.known_cards_in_deck[{index}].count"),
        )?;
        add_deck_identity(&mut rows, card_id, "unknown", count, None);
    }

    let total = rows
        .values()
        .fold(0u32, |sum, row| sum.saturating_add(u32::from(row.count)));
    let complete = total == u32::from(deck_size) && (deck_size == 0 || !rows.is_empty());
    let values = rows
        .into_iter()
        .filter(|(_, row)| row.count > 0)
        .map(|((card_id, _), row)| {
            json!({
                "card_id": card_id,
                "count": row.count,
                "origin": row.origin,
                "card_type": match row.card_type.as_str() {
                    "HERO" | "MINION" | "SPELL" | "WEAPON" | "HERO_POWER" | "LOCATION" => row.card_type,
                    _ => "UNKNOWN".to_owned(),
                },
                "cost": row.cost,
                "name": row.name,
            })
        })
        .collect();
    Ok((values, complete))
}

#[allow(clippy::too_many_lines)]
fn adapt_player(
    value: &Value,
    path: &str,
    fallback_id: &str,
    current_deck: Option<&Value>,
) -> Result<Value, SolverError> {
    let raw = object(value, path)?;
    let (public_tags, public_tags_complete) = player_rule_tags(raw);
    let player_id = scalar_text(raw.get("player_id"), fallback_id);
    let player_id = if matches!(player_id.as_str(), "" | "0") {
        fallback_id.to_owned()
    } else {
        player_id
    };
    let raw_hero = raw
        .get("hero")
        .ok_or_else(|| SolverError::schema(format!("{path}.hero"), "a public hero is required"))?;
    let mut hero = adapt_entity(
        raw_hero,
        &format!("{path}.hero"),
        &format!("{fallback_id}-hero"),
    )?;
    hero["card_type"] = json!("HERO");
    let hand = array(raw, "hand", path)?
        .iter()
        .enumerate()
        .map(|(index, item)| {
            adapt_entity(
                item,
                &format!("{path}.hand[{index}]"),
                &format!("{fallback_id}-hand-{index}"),
            )
        })
        .collect::<Result<Vec<_>, _>>()?;
    let board = array(raw, "board", path)?
        .iter()
        .enumerate()
        .map(|(index, item)| {
            adapt_entity(
                item,
                &format!("{path}.board[{index}]"),
                &format!("{fallback_id}-board-{index}"),
            )
        })
        .collect::<Result<Vec<_>, _>>()?;
    let graveyard = array(raw, "graveyard", path)?
        .iter()
        .enumerate()
        .map(|(index, item)| {
            adapt_entity(
                item,
                &format!("{path}.graveyard[{index}]"),
                &format!("{fallback_id}-graveyard-{index}"),
            )
        })
        .collect::<Result<Vec<_>, _>>()?;
    let mut power = raw
        .get("hero_power")
        .filter(|value| !value.is_null())
        .map(|item| {
            adapt_entity(
                item,
                &format!("{path}.hero_power"),
                &format!("{fallback_id}-hero-power"),
            )
        })
        .transpose()?;
    if let Some(value) = &mut power {
        value["card_type"] = json!("HERO_POWER");
    }
    let weapon = raw
        .get("weapon")
        .filter(|value| !value.is_null())
        .map(|item| {
            adapt_entity(
                item,
                &format!("{path}.weapon"),
                &format!("{fallback_id}-weapon"),
            )
        })
        .transpose()?;
    let resources = raw
        .get("resources")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();
    let mana = nonnegative_u16(
        integer(&resources, "available", 0),
        &format!("{path}.resources.available"),
    )?;
    // HDT's Player.MaxMana is the rules cap (normally 10), not the number of
    // permanent crystals the player has unlocked this turn.  RESOURCES is the
    // authoritative public tag, including the meaningful pre-first-turn value
    // of zero.  Retain max_mana only as a legacy fallback for older snapshots
    // that do not carry resources.total at all.
    let permanent_mana = resources
        .get("total")
        .and_then(|value| {
            value
                .as_i64()
                .or_else(|| value.as_u64().and_then(|item| i64::try_from(item).ok()))
        })
        .or_else(|| {
            raw.get("max_mana").and_then(|value| {
                value
                    .as_i64()
                    .or_else(|| value.as_u64().and_then(|item| i64::try_from(item).ok()))
            })
        })
        .unwrap_or(0);
    let max_mana = nonnegative_u16(permanent_mana, &format!("{path}.resources.total"))?;
    let armor = nonnegative_u16(
        raw_hero
            .as_object()
            .map_or(0, |hero_raw| integer(hero_raw, "armor", 0)),
        &format!("{path}.hero.armor"),
    )?;
    let deck_size = nonnegative_u16(integer(raw, "deck_count", 0), &format!("{path}.deck_count"))?;
    let (known_deck, deck_identity_complete) =
        known_deck_identity(raw, current_deck, deck_size, path)?;
    let fatigue = nonnegative_u16(integer(raw, "fatigue", 0), &format!("{path}.fatigue"))?;
    let spell_power = nonnegative_u16(
        integer(&resources, "spell_power", 0),
        &format!("{path}.resources.spell_power"),
    )?;
    let available = hero_power_available(power.as_ref(), raw.get("hero_power"), &public_tags);
    if let Some(power) = &mut power {
        // HDT's Entity.IsPlayableCard is always false for real hero-power
        // entities.  Preserve availability separately and keep the canonical
        // card-state flag aligned with the oracle model instead of leaking that
        // transport quirk into simulated terminal states.
        power["playable"] = json!(true);
    }

    Ok(json!({
        "player_id": player_id,
        "hero": hero,
        "mana": mana,
        "max_mana": max_mana,
        "armor": armor,
        "hand": hand,
        "board": board,
        "graveyard": graveyard,
        "deck_size": deck_size,
        "known_deck": known_deck,
        "deck_identity_complete": deck_identity_complete,
        "fatigue": fatigue,
        "hero_power": power,
        "hero_power_available": available,
        "weapon": weapon,
        "spell_power": spell_power,
        "public_rule_tags": public_tags,
        "public_rule_tags_complete": public_tags_complete,
    }))
}

fn metadata_scalar(value: &Value) -> bool {
    value.is_null() || value.is_boolean() || value.is_number() || value.is_string()
}

/// Convert a raw HDT state snapshot to the native solver state.
///
/// # Errors
///
/// Returns a schema error when required public entities are absent, identifiers are
/// ambiguous, arrays have the wrong shape, or a gameplay value cannot be represented
/// without truncation.
pub fn adapt_hdt_state(value: &Value) -> Result<GameState, SolverError> {
    let redacted = redact_hidden_entities(value);
    let raw = object(&redacted, "state")?;
    let friendly = adapt_player(
        raw.get("player")
            .ok_or_else(|| SolverError::schema("state.player", "is required"))?,
        "state.player",
        "player",
        raw.get("current_deck"),
    )?;
    let opponent = adapt_player(
        raw.get("opponent")
            .ok_or_else(|| SolverError::schema("state.opponent", "is required"))?,
        "state.opponent",
        "opponent",
        None,
    )?;
    let friendly_id = friendly["player_id"]
        .as_str()
        .ok_or_else(|| SolverError::schema("state.player.player_id", "must be a string"))?;
    let opponent_id = opponent["player_id"]
        .as_str()
        .ok_or_else(|| SolverError::schema("state.opponent.player_id", "must be a string"))?;
    let active_label = text(raw, "active_player", "").to_ascii_lowercase();
    let active_id = match raw.get("is_local_player_turn").and_then(Value::as_bool) {
        Some(true) => friendly_id,
        Some(false) => opponent_id,
        None if [
            "player",
            "friendly",
            "local",
            &friendly_id.to_ascii_lowercase(),
        ]
        .contains(&active_label.as_str()) =>
        {
            friendly_id
        }
        None if ["opponent", "enemy", &opponent_id.to_ascii_lowercase()]
            .contains(&active_label.as_str()) =>
        {
            opponent_id
        }
        None => {
            return Err(SolverError::schema(
                "state.active_player",
                "could not determine the active player",
            ));
        }
    };
    let state_id = raw
        .get("state_id")
        .and_then(Value::as_str)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| SolverError::schema("state.state_id", "must be a non-empty string"))?;
    let turn = u32::try_from(integer(raw, "turn_number", 1).max(1))
        .map_err(|_| SolverError::schema("state.turn_number", "is too large"))?;
    let mut metadata = scalar_map(raw.get("metadata"));
    metadata.insert("adapter".to_owned(), json!("hdt-snapshot-v1"));
    for (key, source) in [
        ("format", "format"),
        ("format_type", "format_type"),
        ("game_mode", "game_mode"),
        ("game_type", "game_type"),
        ("environment_version", "environment_version"),
        ("hdt_version", "hdt_version"),
        ("game_id", "game_id"),
        ("snapshot_state_hash", "state_hash"),
        ("captured_at_utc", "captured_at_utc"),
    ] {
        metadata.insert(key.to_owned(), json!(scalar_text(raw.get(source), "")));
    }
    let snapshot_schema = raw
        .get("schema_version")
        .filter(|value| metadata_scalar(value))
        .cloned()
        .unwrap_or(json!(1));
    metadata.insert("snapshot_schema_version".to_owned(), snapshot_schema);
    metadata.insert(
        "snapshot_sequence".to_owned(),
        raw.get("snapshot_sequence")
            .filter(|value| value.as_i64().is_some())
            .cloned()
            .unwrap_or(json!(0)),
    );
    for (target, source) in [
        ("unsupported_feature_count", "unsupported_features"),
        ("unknown_data_count", "unknown_data"),
    ] {
        metadata.insert(
            target.to_owned(),
            json!(
                raw.get(source)
                    .and_then(Value::as_array)
                    .map_or(0, Vec::len)
            ),
        );
    }
    let mode = raw
        .get("game_mode")
        .or_else(|| raw.get("format"))
        .map_or_else(
            || "unknown".to_owned(),
            |item| scalar_text(Some(item), "unknown"),
        );
    let canonical = json!({
        "state_id": state_id,
        "turn": turn,
        "active_player_id": active_id,
        "perspective_player_id": friendly_id,
        "friendly": friendly,
        "opponent": opponent,
        "patch": scalar_text(raw.get("hearthstone_build"), "unknown"),
        "mode": mode,
        "rng_seed": 0,
        "belief": {},
        "metadata": metadata,
    });
    let mut state: GameState = serde_json::from_value(canonical)?;
    state.validate()?;
    Ok(state)
}

/// Parse either a native or raw-HDT solve request without weakening validation.
///
/// Raw HDT snapshots are detected only when the state contains `player` and
/// `opponent` and does not contain a native `friendly` object.
///
/// # Errors
///
/// Returns an error when JSON shapes are invalid or the resulting canonical request
/// violates the native solver schema.
pub fn solve_request_from_value(value: Value) -> Result<SolveRequest, SolverError> {
    let mut raw = value
        .as_object()
        .cloned()
        .ok_or_else(|| SolverError::schema("request", "must be an object"))?;
    let state_value = raw
        .get("state")
        .ok_or_else(|| SolverError::schema("request.state", "is required"))?;
    let is_hdt = state_value.as_object().is_some_and(|state| {
        state.contains_key("player")
            && state.contains_key("opponent")
            && !state.contains_key("friendly")
    });
    let adapted_hdt_state = if is_hdt {
        let state = adapt_hdt_state(state_value)?;
        raw.insert("state".to_owned(), serde_json::to_value(&state)?);
        Some(state)
    } else {
        None
    };
    let mut request: SolveRequest = serde_json::from_value(Value::Object(raw))?;
    if let Some(state) = adapted_hdt_state {
        // Card::attacks_remaining_known is intentionally omitted from public
        // state serialization. Restore the already validated HDT-adapted state
        // so that this internal evidence bit survives the top-level request parse.
        request.state = state;
    }
    request.validate()?;
    Ok(request)
}

#[cfg(test)]
mod tests {
    use crate::model::DeckCardOrigin;
    use crate::turnpair::advance_to_visible_opponent_start;

    use super::*;

    #[test]
    fn numeric_or_named_public_tags_are_selected() {
        let raw = json!({
            "player_entity": {"tags": {"383": "1", "HERO_POWER_DOUBLE": 0}}
        });
        let (tags, complete) = player_rule_tags(raw.as_object().expect("object"));
        assert!(complete);
        assert_eq!(tags["STEADY_SHOT_CAN_TARGET"], 1);
        assert_eq!(tags["HERO_POWER_DOUBLE"], 0);
    }

    #[test]
    fn hidden_entity_is_never_playable() {
        let card = adapt_entity(
            &json!({
                "entity_id": 20,
                "card_id": "SECRET",
                "card_type": "SPELL",
                "visibility": "hidden",
                "is_playable_card": true
            }),
            "state.player.hand[0]",
            "fallback",
        )
        .expect("adapter");
        assert_eq!(card["playable"], false);
    }

    #[test]
    fn lifesteal_uses_boolean_or_named_or_numeric_tag_evidence() {
        let cases = [
            ("boolean", json!({"has_lifesteal": true, "tags": {}}), true),
            (
                "named tag overrides false boolean",
                json!({"has_lifesteal": false, "tags": {"LIFESTEAL": 1}}),
                true,
            ),
            (
                "numeric tag overrides false boolean",
                json!({"has_lifesteal": false, "tags": {"685": "1"}}),
                true,
            ),
            (
                "zero tag",
                json!({"has_lifesteal": false, "tags": {"LIFESTEAL": 0}}),
                false,
            ),
            ("missing evidence", json!({"tags": {}}), false),
        ];
        for (label, extra, expected) in cases {
            let mut raw = extra.as_object().expect("object").clone();
            raw.insert("entity_id".to_owned(), json!(20));
            raw.insert("card_id".to_owned(), json!("CORE_ICC_055"));
            raw.insert("card_type".to_owned(), json!("SPELL"));
            let card = adapt_entity(&Value::Object(raw), "state.player.hand[0]", "fallback")
                .expect("adapter");
            assert_eq!(card["lifesteal"], expected, "{label}");
        }
    }

    #[test]
    fn intrinsic_keyword_only_text_uses_live_structural_evidence() {
        let cases = [
            (
                "rush",
                json!({
                    "entity_id": 30,
                    "card_id": "BAR_035t",
                    "card_type": "MINION",
                    "english_text": "Rush",
                    "has_rush": true,
                    "mechanics": ["RUSH"]
                }),
            ),
            (
                "multiple keywords",
                json!({
                    "entity_id": 31,
                    "card_id": "GDB_452",
                    "card_type": "MINION",
                    "english_text": "Taunt\nDivine Shield\nLifesteal",
                    "has_taunt": true,
                    "has_divine_shield": true,
                    "has_lifesteal": true,
                    "mechanics": ["TAUNT", "DIVINE_SHIELD", "LIFESTEAL"]
                }),
            ),
            (
                "elusive",
                json!({
                    "entity_id": 32,
                    "card_id": "CATA_558",
                    "card_type": "MINION",
                    "english_text": "Elusive",
                    "mechanics": ["ELUSIVE"],
                    "tags": {
                        "ELUSIVE": 1,
                        "CANT_BE_TARGETED_BY_SPELLS": 1,
                        "CANT_BE_TARGETED_BY_HERO_POWERS": 1
                    }
                }),
            ),
        ];
        for (label, raw) in cases {
            let card =
                adapt_entity(&raw, "state.player.board[0]", "fallback").expect("keyword adapter");
            assert_eq!(card["effect_coverage"], "generic", "{label}");
            assert_eq!(card["unsupported_effects"], json!([]), "{label}");
            assert_eq!(card["rule_id"], INTRINSIC_KEYWORD_RULE_ID, "{label}");
            assert_eq!(
                card["rule_text_sha256"].as_str().map(str::len),
                Some(64),
                "{label}"
            );
        }
    }

    #[test]
    fn keyword_text_never_overrides_missing_evidence_or_extra_rules_text() {
        let cases = [
            (
                "missing elusive hero-power restriction",
                json!({
                    "entity_id": 33,
                    "card_id": "ELUSIVE_WITHOUT_TAGS",
                    "card_type": "MINION",
                    "english_text": "Elusive",
                    "mechanics": ["ELUSIVE"],
                    "tags": {"CANT_BE_TARGETED_BY_SPELLS": 1}
                }),
            ),
            (
                "compound text",
                json!({
                    "entity_id": 34,
                    "card_id": "RUSH_BATTLECRY",
                    "card_type": "MINION",
                    "english_text": "Rush. Battlecry: Draw a card.",
                    "has_rush": true,
                    "mechanics": ["RUSH", "BATTLECRY"]
                }),
            ),
        ];
        for (label, raw) in cases {
            let card = adapt_entity(&raw, "state.player.board[0]", "fallback")
                .expect("fail-closed keyword adapter");
            assert_eq!(card["effect_coverage"], "unsupported", "{label}");
            assert!(
                card["unsupported_effects"]
                    .as_array()
                    .is_some_and(|items| items
                        .iter()
                        .any(|item| { item.as_str() == Some("card_text_not_parsed") })),
                "{label}"
            );
            assert_eq!(card["rule_id"], "", "{label}");
        }
    }

    #[test]
    fn frozen_state_is_preserved_in_the_canonical_card() {
        let card = adapt_entity(
            &json!({
                "entity_id": 21,
                "card_id": "FROZEN_MINION",
                "card_type": "MINION",
                "attack": 3,
                "health": 3,
                "is_exhausted": false,
                "is_frozen": true,
                "zone": "PLAY"
            }),
            "state.player.board[0]",
            "fallback",
        )
        .expect("adapter");
        assert_eq!(card["frozen"], true);
        assert_eq!(card["can_attack"], false);
        assert_eq!(card["attacks_remaining"], 0);
    }

    #[test]
    fn weapon_health_and_damage_are_adapted_as_public_durability() {
        let card = adapt_entity(
            &json!({
                "entity_id": 23,
                "card_id": "WEAPON",
                "card_type": "WEAPON",
                "attack": 2,
                "health": 3,
                "damage": 1,
                "durability": 0,
                "zone": "PLAY"
            }),
            "state.player.weapon",
            "fallback",
        )
        .expect("adapter");
        assert_eq!(card["durability"], 3);
        assert_eq!(card["current_durability"], 2);
    }

    #[test]
    fn resource_total_beats_hdt_rules_cap_and_drives_the_next_turn_refresh() {
        let mut state = adapt_hdt_state(&json!({
            "state_id": "mana-source",
            "turn_number": 1,
            "active_player": "player",
            "is_local_player_turn": true,
            "player": {
                "player_id": 1,
                "max_mana": 10,
                "resources": {"available": 1, "total": 1},
                "hero": {
                    "entity_id": 1,
                    "card_id": "HERO_F",
                    "card_type": "HERO",
                    "health": 30,
                    "damage": 0
                }
            },
            "opponent": {
                "player_id": 2,
                "max_mana": 10,
                "resources": {"available": 0, "total": 0},
                "hero": {
                    "entity_id": 2,
                    "card_id": "HERO_O",
                    "card_type": "HERO",
                    "health": 30,
                    "damage": 0
                }
            }
        }))
        .expect("adapt live HDT mana tags");

        assert_eq!(state.friendly.max_mana, 1);
        assert_eq!(state.opponent.max_mana, 0);

        state.active_player_id = state.opponent.player_id.clone();
        let response = advance_to_visible_opponent_start(&state).expect("response refresh");
        assert_eq!(response.opponent.max_mana, 1);
        assert_eq!(response.opponent.mana, 1);
    }

    #[test]
    fn selected_deck_minus_visible_zones_builds_complete_remaining_identity() {
        let state = adapt_hdt_state(&json!({
            "state_id": "known-deck",
            "turn_number": 3,
            "active_player": "1",
            "current_deck": {
                "is_known": true,
                "cards": [
                    {"card_id": "DECK_A", "count": 2},
                    {"card_id": "DECK_B", "count": 1},
                    {"card_id": "DECK_C", "count": 1}
                ]
            },
            "player": {
                "player_id": 1,
                "hero": {"entity_id": 1, "card_id": "HERO_F", "card_type": "HERO", "health": 30, "damage": 0},
                "resources": {"available": 3, "total": 3},
                "deck_count": 2,
                "hand": [
                    {"entity_id": 10, "card_id": "DECK_A", "original_card_id": "DECK_A", "card_type": "MINION", "zone": "HAND", "health": 2, "damage": 0}
                ],
                "board": [
                    {"entity_id": 11, "card_id": "DECK_B", "original_card_id": "DECK_B", "card_type": "MINION", "zone": "PLAY", "health": 2, "damage": 0}
                ],
                "graveyard": [
                    {"entity_id": 12, "card_id": "DECK_A", "original_card_id": "DECK_A", "card_type": "SPELL", "zone": "GRAVEYARD"}
                ],
                "deck": [
                    {"entity_id": 13, "card_id": "GENERATED_ECHO", "card_type": "SPELL", "cost": 0, "name": "Echo", "zone": "DECK", "is_created": true}
                ]
            },
            "opponent": {
                "player_id": 2,
                "hero": {"entity_id": 2, "card_id": "HERO_O", "card_type": "HERO", "health": 30, "damage": 0},
                "resources": {"available": 0, "total": 3},
                "deck_count": 25
            }
        }))
        .expect("adapt selected deck identity");

        assert_eq!(state.friendly.graveyard.len(), 1);
        assert!(state.friendly.deck_identity_complete);
        assert_eq!(state.friendly.deck_size, 2);
        assert_eq!(state.friendly.known_deck.len(), 2);
        let mut remaining = state
            .friendly
            .known_deck
            .iter()
            .map(|card| (card.card_id.as_ref(), card.count, card.origin))
            .collect::<Vec<_>>();
        remaining.sort_by_key(|(card_id, _, _)| *card_id);
        assert_eq!(
            remaining,
            vec![
                ("DECK_C", 1, DeckCardOrigin::StartedInDeck),
                ("GENERATED_ECHO", 1, DeckCardOrigin::Generated),
            ]
        );
    }

    #[test]
    fn summoned_state_requires_an_explicit_turns_in_play_tag() {
        let card = |tags: Value| {
            adapt_entity(
                &json!({
                    "entity_id": 22,
                    "card_id": "BOARD_MINION",
                    "card_type": "MINION",
                    "attack": 2,
                    "health": 2,
                    "is_exhausted": true,
                    "zone": "PLAY",
                    "tags": tags
                }),
                "state.player.board[0]",
                "fallback",
            )
            .expect("adapter")
        };
        assert_eq!(card(json!({}))["summoned_this_turn"], false);
        assert_eq!(
            card(json!({"NUM_TURNS_IN_PLAY": 0}))["summoned_this_turn"],
            true
        );
        assert_eq!(
            card(json!({"NUM_TURNS_IN_PLAY": 1}))["summoned_this_turn"],
            false
        );
    }

    #[test]
    fn recursive_redaction_covers_private_opponent_zones_and_explicit_hidden_entities() {
        let private = json!({
            "entity_id": 99,
            "card_id": "SECRET_INTERNAL_CARD",
            "name": "Secret internal name",
            "card_text": "Secret internal text",
            "mechanics": ["SECRET_INTERNAL_MECHANIC"],
            "cost": 9,
            "zone": "HAND",
            "zone_position": 2,
            "controller_id": 2,
            "tags": {
                "ZONE": 3,
                "ZONE_POSITION": 2,
                "CONTROLLER": 2,
                "COST": 9,
                "DBF_ID": 987654
            }
        });
        let mut raw = json!({
            "state": {
                "player": {"board": []},
                "opponent": {
                    "hand": [private.clone()],
                    "deck": [private.clone()],
                    "set_aside": [private.clone()],
                    "secrets": [private.clone()],
                    "board": []
                }
            }
        });
        raw["state"]["player"]["board"] = json!([{
            "entity_id": 100,
            "card_id": "FRIENDLY_HIDDEN_CARD",
            "card_text": "Friendly hidden text",
            "visibility": "partially-hidden",
            "zone": "PLAY",
            "tags": {"ZONE": 1, "COST": 7}
        }]);
        raw["state"]["opponent"]["board"] = json!([{
            "entity_id": 101,
            "card_id": "OPPONENT_ZONE_HIDDEN_CARD",
            "card_text": "Opponent zone hidden text",
            "zone_id": 7,
            "tags": {"ZONE_POSITION": 1, "COST": 8}
        }]);

        let redacted = redact_hidden_entities(&raw);
        let serialized = serde_json::to_string(&redacted).expect("redacted JSON");
        for secret in [
            "SECRET_INTERNAL_CARD",
            "Secret internal name",
            "Secret internal text",
            "SECRET_INTERNAL_MECHANIC",
            "FRIENDLY_HIDDEN_CARD",
            "Friendly hidden text",
            "OPPONENT_ZONE_HIDDEN_CARD",
            "Opponent zone hidden text",
            "987654",
        ] {
            assert!(!serialized.contains(secret), "leaked {secret}");
        }
        for zone in ["hand", "deck", "set_aside", "secrets", "board"] {
            let entity = redacted["state"]["opponent"][zone][0]
                .as_object()
                .expect("redacted opponent entity");
            assert_eq!(entity["visibility"], "hidden", "{zone}");
            assert!(
                entity
                    .keys()
                    .all(|key| PUBLIC_ENTITY_LOCATION_KEYS.contains(&key.as_str()) || key == "tags"),
                "{zone}"
            );
            assert!(
                entity
                    .get("tags")
                    .and_then(Value::as_object)
                    .is_none_or(|tags| tags.keys().all(|key| {
                        PUBLIC_ENTITY_LOCATION_TAGS.contains(&key.to_ascii_uppercase().as_str())
                    })),
                "{zone}"
            );
        }
    }
}
