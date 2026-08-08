from __future__ import annotations

import hashlib
import json
import math
import os
from collections import Counter
from pathlib import Path
from typing import Any, Mapping, Sequence

from .behavior_learning import BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID
from .behavior_prior import load_and_validate_behavior_imitation
from .card_rules import StructuredCardRuleBundle, default_structured_card_rule_path
from .schemas import Action, ActionKind, Card, CardType, GameState
from .simulator import enumerate_legal_actions


BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY_SCHEMA_ID = (
    "behavior-candidate-alignment-policy-v1"
)
BEHAVIOR_CANDIDATE_ALIGNMENT_REPORT_SCHEMA_ID = (
    "behavior-candidate-alignment-report-v1"
)

DEFAULT_BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY: dict[str, float | int] = {
    "min_train_eligible_games": 30,
    "min_validation_eligible_games": 10,
    "min_test_eligible_games": 10,
    "min_train_eligible_records": 250,
    "min_validation_eligible_records": 50,
    "min_test_eligible_records": 50,
    "min_local_exact_alignment_rate": 1.0,
    "min_local_candidate_set_eligible_rate": 1.0,
}

ALIGNMENT_STATUSES = ("exact", "target_mismatch", "not_generated")
SPLITS = ("train", "validation", "test")
ACTOR_SIDES = ("local", "opponent")
ACTION_KINDS = (
    "attack",
    "end_turn",
    "hero_power",
    "location_activate",
    "play_card",
)

APPROVED_USES = [
    "candidate_coverage_audit",
    "local_behavior_cloning_eligibility_filter",
    "opponent_behavior_observation_analysis",
]
PROHIBITED_USES = [
    "action_generation",
    "direct_live_policy",
    "direct_rl_trajectory",
    "optimal_action_ground_truth",
    "hidden_opponent_card_reconstruction",
]

_PUBLIC_COMBAT_BOOLEAN_EVIDENCE_KEYS = (
    "current_health_known",
    "can_attack",
    "taunt",
    "divine_shield",
    "stealth",
    "poisonous",
    "lifesteal",
    "windfury",
    "mega_windfury",
    "rush",
    "charge",
    "reborn",
    "dormant",
    "immune",
    "summoned_this_turn",
    "frozen",
)
_PUBLIC_COMBAT_INTEGER_EVIDENCE_KEYS = (
    "attack",
    "health",
    "current_health",
    "attacks_remaining",
)
_PUBLIC_RULE_TAG_KEYS = {
    "STEADY_SHOT_CAN_TARGET",
    "CURRENT_HEROPOWER_DAMAGE_BONUS",
    "HERO_POWER_DOUBLE",
    "HEROPOWER_DAMAGE",
    "HERO_POWER_DISABLED",
}


class BehaviorCandidateAlignmentError(ValueError):
    pass


def _canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def behavior_candidate_alignment_policy_sha256(
    policy: Mapping[str, float | int],
) -> str:
    return _sha256_bytes(
        _canonical_json(
            {
                "schema": BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY_SCHEMA_ID,
                "thresholds": dict(sorted(policy.items())),
            }
        )
    )


def load_behavior_candidate_alignment_policy(
    path: str | Path | None,
) -> dict[str, float | int]:
    policy = dict(DEFAULT_BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY)
    if path is None:
        return policy
    source = Path(path)
    try:
        raw = json.loads(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise BehaviorCandidateAlignmentError(
            "invalid behavior candidate alignment policy"
        ) from exc
    if not isinstance(raw, Mapping) or set(raw) != {"schema", "thresholds"}:
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment policy fields do not match the contract"
        )
    if raw.get("schema") != BEHAVIOR_CANDIDATE_ALIGNMENT_POLICY_SCHEMA_ID:
        raise BehaviorCandidateAlignmentError(
            "unsupported behavior candidate alignment policy schema"
        )
    thresholds = raw.get("thresholds")
    if not isinstance(thresholds, Mapping) or set(thresholds) != set(policy):
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment thresholds do not match the contract"
        )
    for key in policy:
        value = thresholds[key]
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise BehaviorCandidateAlignmentError(
                f"policy.{key} must be numeric"
            )
        if key.endswith("_rate"):
            number = float(value)
            if not math.isfinite(number) or not 0 <= number <= 1:
                raise BehaviorCandidateAlignmentError(
                    f"policy.{key} must be between 0 and 1"
                )
            policy[key] = number
        else:
            if not isinstance(value, int) or value < 0:
                raise BehaviorCandidateAlignmentError(
                    f"policy.{key} must be a non-negative integer"
                )
            policy[key] = value
    return policy


def _rate(numerator: int, denominator: int) -> float:
    return round(numerator / denominator, 6) if denominator else 0.0


def _check(
    name: str,
    actual: int | float,
    operator: str,
    expected: int | float,
) -> dict[str, Any]:
    if operator == ">=":
        passed = actual >= expected
    else:  # pragma: no cover - all current checks are lower bounds.
        raise AssertionError(operator)
    return {
        "name": name,
        "actual": actual,
        "operator": operator,
        "expected": expected,
        "passed": passed,
    }


def _empty_bucket() -> dict[str, int]:
    return {
        "record_count": 0,
        "candidate_count_total": 0,
        "exact_count": 0,
        "target_mismatch_count": 0,
        "not_generated_count": 0,
        "candidate_set_eligible_count": 0,
    }


def _add_bucket(
    bucket: dict[str, int],
    *,
    status: str,
    candidate_count: int,
    eligible: bool,
) -> None:
    bucket["record_count"] += 1
    bucket["candidate_count_total"] += candidate_count
    bucket[f"{status}_count"] += 1
    bucket["candidate_set_eligible_count"] += int(eligible)


def _finish_bucket(bucket: Mapping[str, int]) -> dict[str, int | float]:
    records = int(bucket["record_count"])
    result: dict[str, int | float] = dict(bucket)
    result["average_candidate_count"] = (
        round(int(bucket["candidate_count_total"]) / records, 6)
        if records
        else 0.0
    )
    result["exact_rate"] = _rate(int(bucket["exact_count"]), records)
    result["target_mismatch_rate"] = _rate(
        int(bucket["target_mismatch_count"]), records
    )
    result["not_generated_rate"] = _rate(
        int(bucket["not_generated_count"]), records
    )
    result["candidate_set_eligible_rate"] = _rate(
        int(bucket["candidate_set_eligible_count"]), records
    )
    return result


def _build_matches(state_patch: str, rules: StructuredCardRuleBundle) -> bool:
    return bool(
        rules.available
        and rules.source_card_defs_build
        and state_patch.strip() == rules.source_card_defs_build.strip()
    )


def _candidate_identity(action: Action) -> tuple[str, str, str, str, int]:
    return (
        action.kind.value,
        action.source_entity_id,
        action.target_entity_id,
        action.card_id,
        action.board_position,
    )


def _observed_identity(action: Mapping[str, Any]) -> tuple[str, str, str, str, int]:
    raw_position = action.get("board_position", 0)
    board_position = (
        raw_position
        if isinstance(raw_position, int) and not isinstance(raw_position, bool)
        else -1
    )
    return (
        str(action.get("kind") or ""),
        str(action.get("source_entity_id") or ""),
        str(action.get("target_entity_id") or ""),
        str(action.get("card_id") or ""),
        board_position,
    )


def _alignment_status(
    observed: Mapping[str, Any], candidates: Sequence[Action]
) -> str:
    identity = _observed_identity(observed)
    candidate_identities = {_candidate_identity(action) for action in candidates}
    if identity in candidate_identities:
        return "exact"
    kind, source_id, _, card_id, board_position = identity
    if any(
        candidate.kind.value == kind
        and candidate.source_entity_id == source_id
        and candidate.card_id == card_id
        and candidate.board_position == board_position
        for candidate in candidates
    ):
        return "target_mismatch"
    return "not_generated"


def _actionable_hand_cards(state: GameState) -> list[Card]:
    actor = state.player(state.active_player_id)
    result: list[Card] = []
    for card in actor.hand:
        if not card.playable or card.cost > actor.mana:
            continue
        if card.card_type in {CardType.MINION, CardType.LOCATION} and len(actor.board) >= 7:
            continue
        result.append(card)
    return result


def _public_combat_entity_evidence_complete(
    value: Any, *, weapon: bool = False
) -> bool:
    if not isinstance(value, Mapping):
        return False
    for key in _PUBLIC_COMBAT_INTEGER_EVIDENCE_KEYS:
        item = value.get(key)
        if isinstance(item, bool) or not isinstance(item, int) or item < 0:
            return False
    for key in _PUBLIC_COMBAT_BOOLEAN_EVIDENCE_KEYS:
        if not isinstance(value.get(key), bool):
            return False
    if value.get("current_health_known") is not True:
        return False
    if weapon:
        for key in ("durability", "current_durability"):
            item = value.get(key)
            if isinstance(item, bool) or not isinstance(item, int) or item < 0:
                return False
    return True


def _board_combat_evidence_complete(record: Mapping[str, Any]) -> bool:
    """Check explicit raw evidence without trusting GameState's false defaults."""

    pre_state = record.get("pre_state")
    if not isinstance(pre_state, Mapping):
        return False
    for role in ("friendly", "opponent"):
        player = pre_state.get(role)
        if not isinstance(player, Mapping):
            return False
        tags = player.get("public_rule_tags")
        if player.get("public_rule_tags_complete") is not True or not isinstance(
            tags, Mapping
        ):
            return False
        if any(
            key not in _PUBLIC_RULE_TAG_KEYS
            or isinstance(item, bool)
            or not isinstance(item, int)
            for key, item in tags.items()
        ):
            return False
        if not _public_combat_entity_evidence_complete(player.get("hero")):
            return False
        weapon = player.get("weapon")
        if weapon is not None and not _public_combat_entity_evidence_complete(
            weapon, weapon=True
        ):
            return False
        board = player.get("board")
        if not isinstance(board, Sequence) or isinstance(
            board, (str, bytes, bytearray)
        ):
            return False
        for entity in board:
            if not isinstance(entity, Mapping):
                return False
            if entity.get("card_type") == CardType.MINION.value and not (
                _public_combat_entity_evidence_complete(entity)
            ):
                return False
    return True


def _record_blockers(
    record: Mapping[str, Any],
    state: GameState,
    candidates: Sequence[Action],
    *,
    status: str,
    rules_build_matches: bool,
) -> set[str]:
    blockers: set[str] = set()
    actor = state.player(state.active_player_id)
    enemy = state.other_player(state.active_player_id)
    observed = record["action"]

    if record["actor_side"] != "local":
        blockers.add("opponent_actions_excluded_from_candidate_set_training")
        if actor.hand:
            blockers.add("opponent_hidden_hand_unavailable")
    if not rules_build_matches:
        blockers.add("structured_rules_build_mismatch")
    if status != "exact":
        blockers.add("observed_action_not_exactly_generated")
    if len(candidates) < 2:
        blockers.add("fewer_than_two_candidates")

    actionable_cards = _actionable_hand_cards(state)
    if any(card.effect_coverage != "exact" for card in actionable_cards):
        blockers.add("actionable_card_rules_unverified")
    expected_positions = set(range(1, len(actor.board) + 2))
    for card in actionable_cards:
        if card.card_type != CardType.MINION:
            continue
        position_groups: dict[str, set[int]] = {}
        for candidate in candidates:
            if (
                candidate.kind == ActionKind.PLAY_CARD
                and candidate.source_entity_id == card.entity_id
            ):
                position_groups.setdefault(candidate.target_entity_id, set()).add(
                    candidate.board_position
                )
        if any(positions != expected_positions for positions in position_groups.values()):
            blockers.add("minion_board_positions_not_modeled")
            break

    power = actor.hero_power
    if (
        power is not None
        and actor.hero_power_available
        and power.cost <= actor.mana
        and power.effect_coverage != "exact"
    ):
        blockers.add("hero_power_rule_unverified")

    board_minions = [
        card
        for card in (*actor.board, *enemy.board)
        if card.card_type == CardType.MINION
    ]
    if board_minions and not _board_combat_evidence_complete(record):
        # Missing raw fields must not inherit GameState's false defaults: absence
        # is unknown evidence, not proof that a keyword or restriction is false.
        blockers.add("board_combat_rules_unverified")

    actor_locations = [
        card for card in actor.board if card.card_type == CardType.LOCATION
    ]
    if actor_locations:
        blockers.add("board_locations_not_modeled")
        blockers.add("actionable_location_state_unverified")
    if observed.get("kind") == "location_activate":
        blockers.add("location_activation_not_generated")

    if (
        observed.get("choice_status") not in {None, "none", "not_observed"}
        or observed.get("sub_option") not in {None, -1}
        or observed.get("choices")
    ):
        blockers.add("action_choices_not_modeled")
    return blockers


def _sorted_counts(counter: Mapping[str, int]) -> dict[str, int]:
    return {key: int(counter[key]) for key in sorted(counter)}


def audit_behavior_candidate_alignment_files(
    dataset_path: str | Path,
    manifest_path: str | Path,
    *,
    policy_path: str | Path | None = None,
    rules_path: str | Path | None = None,
) -> dict[str, Any]:
    records, identity = load_and_validate_behavior_imitation(
        dataset_path, manifest_path
    )
    policy = load_behavior_candidate_alignment_policy(policy_path)
    policy_sha256 = behavior_candidate_alignment_policy_sha256(policy)

    rules_source = Path(
        rules_path if rules_path is not None else default_structured_card_rule_path()
    )
    try:
        rules_bytes = rules_source.read_bytes()
        rules = StructuredCardRuleBundle.load(rules_source)
    except (OSError, TypeError, ValueError) as exc:
        raise BehaviorCandidateAlignmentError(
            "invalid structured card rule bundle"
        ) from exc
    if not rules.available:
        raise BehaviorCandidateAlignmentError(
            "structured card rule bundle is unavailable"
        )

    overall = _empty_bucket()
    by_side = {side: _empty_bucket() for side in ACTOR_SIDES}
    by_kind = {kind: _empty_bucket() for kind in ACTION_KINDS}
    by_split = {split: _empty_bucket() for split in SPLITS}
    by_mode: dict[str, dict[str, int]] = {}
    blocker_counts: Counter[str] = Counter()
    blocker_counts_by_side: dict[str, Counter[str]] = {
        side: Counter() for side in ACTOR_SIDES
    }
    eligible_games: dict[str, set[str]] = {split: set() for split in SPLITS}
    eligible_records: Counter[str] = Counter()
    rule_mismatch_reasons: Counter[str] = Counter()
    build_match_records = 0
    build_mismatch_records = 0
    matched_entities = 0
    mismatched_entities = 0

    for record in records:
        try:
            state = GameState.from_dict(record["pre_state"])
        except (TypeError, ValueError) as exc:
            raise BehaviorCandidateAlignmentError(
                "imitation pre-state cannot be adapted for candidate enumeration"
            ) from exc
        build_matches = _build_matches(state.patch, rules)
        if build_matches:
            build_match_records += 1
            application = rules.apply(state)
            matched_entities += int(application["matched_entity_count"])
            mismatched_entities += int(application["mismatch_entity_count"])
            rule_mismatch_reasons.update(
                str(item.get("reason") or "unspecified")
                for item in application["mismatches"]
            )
        else:
            build_mismatch_records += 1

        candidates = enumerate_legal_actions(state)
        # Duplicate actions would make candidate-set size and model normalization
        # ambiguous, so count only the canonical unique action identities.
        unique_candidates = list(
            {action.action_id: action for action in candidates}.values()
        )
        status = _alignment_status(record["action"], unique_candidates)
        blockers = _record_blockers(
            record,
            state,
            unique_candidates,
            status=status,
            rules_build_matches=build_matches,
        )
        eligible = not blockers
        blocker_counts.update(blockers)

        side = str(record["actor_side"])
        blocker_counts_by_side[side].update(blockers)
        kind = str(record["action"]["kind"])
        split = str(record["split"])
        mode = str(record["pre_state"].get("mode") or "unknown")
        if mode not in by_mode:
            by_mode[mode] = _empty_bucket()
        for bucket in (
            overall,
            by_side[side],
            by_kind[kind],
            by_split[split],
            by_mode[mode],
        ):
            _add_bucket(
                bucket,
                status=status,
                candidate_count=len(unique_candidates),
                eligible=eligible,
            )
        if eligible:
            eligible_records[split] += 1
            eligible_games[split].add(str(record["game_id"]))

    local = _finish_bucket(by_side["local"])
    quality_checks = []
    for split in SPLITS:
        quality_checks.extend(
            (
                _check(
                    f"{split}_eligible_game_count",
                    len(eligible_games[split]),
                    ">=",
                    policy[f"min_{split}_eligible_games"],
                ),
                _check(
                    f"{split}_eligible_record_count",
                    eligible_records[split],
                    ">=",
                    policy[f"min_{split}_eligible_records"],
                ),
            )
        )
    quality_checks.extend(
        (
            _check(
                "local_exact_alignment_rate",
                float(local["exact_rate"]),
                ">=",
                policy["min_local_exact_alignment_rate"],
            ),
            _check(
                "local_candidate_set_eligible_rate",
                float(local["candidate_set_eligible_rate"]),
                ">=",
                policy["min_local_candidate_set_eligible_rate"],
            ),
        )
    )
    ready = all(bool(item["passed"]) for item in quality_checks)

    dataset_source = identity["dataset_path"]
    manifest_source = identity["manifest_path"]
    manifest = identity["manifest"]
    report: dict[str, Any] = {
        "schema": BEHAVIOR_CANDIDATE_ALIGNMENT_REPORT_SCHEMA_ID,
        "status": "READY" if ready else "NOT_READY",
        "source_dataset": {
            "name": dataset_source.name,
            "sha256": identity["dataset_sha256"],
            "bytes": len(identity["dataset_bytes"]),
            "record_count": len(records),
            "game_count": len({str(record["game_id"]) for record in records}),
            "manifest_bound": True,
        },
        "source_manifest": {
            "name": manifest_source.name,
            "sha256": identity["manifest_sha256"],
            "bytes": len(identity["manifest_bytes"]),
            "schema": BEHAVIOR_IMITATION_MANIFEST_SCHEMA_ID,
            "imitation_ready": True,
            "rl_training_ready": False,
            "source_dataset_sha256": manifest["imitation_dataset"]["sha256"],
        },
        "structured_rules": {
            "name": rules_source.name,
            "sha256": _sha256_bytes(rules_bytes),
            "bytes": len(rules_bytes),
            "ruleset_id": rules.ruleset_id,
            "source_card_defs_build": rules.source_card_defs_build,
            "rule_count": len(rules.rules),
            "build_match_record_count": build_match_records,
            "build_mismatch_record_count": build_mismatch_records,
            "matched_entity_count": matched_entities,
            "mismatch_entity_count": mismatched_entities,
            "mismatch_reason_counts": _sorted_counts(rule_mismatch_reasons),
            "cross_build_rule_application_allowed": False,
        },
        "policy": policy,
        "policy_sha256": policy_sha256,
        "metrics": {
            "overall": _finish_bucket(overall),
            "by_actor_side": {
                side: _finish_bucket(by_side[side]) for side in ACTOR_SIDES
            },
            "by_action_kind": {
                kind: _finish_bucket(by_kind[kind]) for kind in ACTION_KINDS
            },
            "by_mode": {
                mode: _finish_bucket(by_mode[mode]) for mode in sorted(by_mode)
            },
            "by_split": {
                split: _finish_bucket(by_split[split]) for split in SPLITS
            },
            "candidate_set_eligible_split_record_counts": {
                split: int(eligible_records[split]) for split in SPLITS
            },
            "candidate_set_eligible_split_game_counts": {
                split: len(eligible_games[split]) for split in SPLITS
            },
            "candidate_set_blocker_record_counts": _sorted_counts(blocker_counts),
            "candidate_set_blocker_record_counts_by_actor_side": {
                side: _sorted_counts(blocker_counts_by_side[side])
                for side in ACTOR_SIDES
            },
        },
        "quality_checks": quality_checks,
        "contract_passed": True,
        "candidate_set_audit_complete": True,
        "candidate_ranking_training_ready": ready,
        "candidate_generation_allowed": False,
        "live_policy_eligible": False,
        "rl_training_eligible": False,
        "optimality_verified": False,
        "approved_uses": APPROVED_USES,
        "prohibited_uses": PROHIBITED_USES,
        "caveat": (
            "双方公开动作是有价值的行为模仿与对手建模数据；只有本方观察动作被精确生成、"
            "至少存在两个候选且整套合法候选可证明完整的记录，才可进入候选排序训练。"
            "该审计不允许生成动作，不是强化学习轨迹，也不证明任何动作最优。"
        ),
    }
    validate_behavior_candidate_alignment_report(report)
    return report


def validate_behavior_candidate_alignment_report(value: Any) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment report must be an object"
        )
    if value.get("schema") != BEHAVIOR_CANDIDATE_ALIGNMENT_REPORT_SCHEMA_ID:
        raise BehaviorCandidateAlignmentError(
            "unsupported behavior candidate alignment report schema"
        )
    if value.get("status") not in {"READY", "NOT_READY"}:
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment report status is invalid"
        )
    for field in (
        "contract_passed",
        "candidate_set_audit_complete",
        "candidate_ranking_training_ready",
        "candidate_generation_allowed",
        "live_policy_eligible",
        "rl_training_eligible",
        "optimality_verified",
    ):
        if not isinstance(value.get(field), bool):
            raise BehaviorCandidateAlignmentError(
                f"behavior candidate alignment report {field} must be boolean"
            )
    if (
        value.get("contract_passed") is not True
        or value.get("candidate_set_audit_complete") is not True
        or value.get("candidate_generation_allowed") is not False
        or value.get("live_policy_eligible") is not False
        or value.get("rl_training_eligible") is not False
        or value.get("optimality_verified") is not False
    ):
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment safety flags drifted"
        )
    checks = value.get("quality_checks")
    if not isinstance(checks, Sequence) or isinstance(checks, (str, bytes)):
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment quality checks are invalid"
        )
    computed_ready = bool(checks) and all(
        isinstance(item, Mapping) and item.get("passed") is True for item in checks
    )
    if (
        value.get("candidate_ranking_training_ready") is not computed_ready
        or value.get("status") != ("READY" if computed_ready else "NOT_READY")
    ):
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment readiness is inconsistent"
        )
    if value.get("approved_uses") != APPROVED_USES:
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment approved uses drifted"
        )
    if value.get("prohibited_uses") != PROHIBITED_USES:
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment prohibited uses drifted"
        )
    caveat = value.get("caveat")
    if (
        not isinstance(caveat, str)
        or "不是强化学习轨迹" not in caveat
        or "不证明任何动作最优" not in caveat
    ):
        raise BehaviorCandidateAlignmentError(
            "behavior candidate alignment caveat is missing"
        )
    return dict(value)


def write_behavior_candidate_alignment_report(
    report: Mapping[str, Any], path: str | Path
) -> None:
    validated = validate_behavior_candidate_alignment_report(report)
    payload = json.dumps(validated, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    try:
        with temporary.open("wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, destination)
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
