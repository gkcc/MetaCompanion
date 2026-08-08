from __future__ import annotations

import hashlib
import json
import math
import os
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Mapping, Sequence

from .behavior_prior import _template_label
from .decision_frame import (
    DecisionFrameRecord,
    DecisionFrameValidationError,
    _jsonl_values,
    _read_bounded,
    audit_decision_frame_file,
)
from .logging_store import deterministic_game_split


DECISION_RANKER_SCHEMA_ID = "advisor-decision-ranker-v1"
DECISION_RANKER_MODEL_ID = "sparse-listwise-logistic-v1"
DECISION_RANKER_FEATURES_ID = "public-decision-candidate-features-v1"
DECISION_RANKER_POLICY_SCHEMA_ID = "advisor-decision-ranker-policy-v1"

DEFAULT_DECISION_RANKER_POLICY: dict[str, float | int] = {
    "min_train_games": 30,
    "min_validation_games": 8,
    "min_test_games": 8,
    "min_train_records": 250,
    "min_validation_records": 50,
    "min_test_records": 50,
    "min_validation_top1_lift_over_uniform": 0.03,
    "min_validation_top3_lift_over_uniform": 0.03,
    "max_validation_log_loss_excess": -0.02,
    "max_validation_unseen_selected_template_rate": 0.50,
}

_INTEGER_POLICY_KEYS = {
    "min_train_games",
    "min_validation_games",
    "min_test_games",
    "min_train_records",
    "min_validation_records",
    "min_test_records",
}
_SPLITS = ("train", "validation", "test")
_TEMPERATURE_GRID = (0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 10.0)
_MAX_EPOCHS = 20
_LEARNING_RATE = 0.35
_WEIGHT_DECAY = 0.00001


class DecisionRankerError(ValueError):
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


def _strict_json_object(payload: bytes, label: str) -> Mapping[str, Any]:
    def pairs_hook(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise DecisionRankerError(f"{label} 包含重复字段：{key}")
            result[key] = value
        return result

    try:
        value = json.loads(payload.decode("utf-8"), object_pairs_hook=pairs_hook)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise DecisionRankerError(f"{label} 不是有效的 UTF-8 JSON") from exc
    if not isinstance(value, Mapping):
        raise DecisionRankerError(f"{label} 根节点必须是对象")
    return value


def _load_policy(path: str | Path | None) -> dict[str, float | int]:
    if path is None:
        return dict(DEFAULT_DECISION_RANKER_POLICY)
    try:
        payload = Path(path).read_bytes()
    except OSError as exc:
        raise DecisionRankerError("无法读取决策排序训练门槛") from exc
    raw = _strict_json_object(payload, "决策排序训练门槛")
    if set(raw) != {"schema", "thresholds"}:
        raise DecisionRankerError("决策排序训练门槛字段不符合合同")
    if raw.get("schema") != DECISION_RANKER_POLICY_SCHEMA_ID:
        raise DecisionRankerError("不支持的决策排序训练门槛版本")
    thresholds = raw.get("thresholds")
    if not isinstance(thresholds, Mapping) or set(thresholds) != set(
        DEFAULT_DECISION_RANKER_POLICY
    ):
        raise DecisionRankerError("决策排序训练门槛项不完整")
    result: dict[str, float | int] = {}
    for key in sorted(DEFAULT_DECISION_RANKER_POLICY):
        value = thresholds[key]
        if key in _INTEGER_POLICY_KEYS:
            if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
                raise DecisionRankerError(f"门槛 {key} 必须是正整数")
            result[key] = value
            continue
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise DecisionRankerError(f"门槛 {key} 必须是数字")
        number = float(value)
        if not math.isfinite(number):
            raise DecisionRankerError(f"门槛 {key} 必须是有限数字")
        if key.endswith("rate") and not 0.0 <= number <= 1.0:
            raise DecisionRankerError(f"门槛 {key} 必须位于 0 到 1")
        result[key] = number
    return result


def decision_ranker_policy_sha256(policy: Mapping[str, float | int]) -> str:
    return _sha256_bytes(
        _canonical_json(
            {
                "schema": DECISION_RANKER_POLICY_SCHEMA_ID,
                "thresholds": dict(sorted(policy.items())),
            }
        )
    )


def _load_frames(path: str | Path) -> tuple[list[DecisionFrameRecord], bytes]:
    payload = _read_bounded(path, label="decision_frame_input")
    values = _jsonl_values(payload, label="decision_frame_input")
    records: list[DecisionFrameRecord] = []
    for index, value in enumerate(values, start=1):
        try:
            records.append(DecisionFrameRecord.from_dict(value))
        except DecisionFrameValidationError as exc:
            raise DecisionRankerError(
                f"决策帧第 {index} 行未通过合同校验（{exc.code}）"
            ) from exc
    if not records:
        raise DecisionRankerError("决策帧文件为空")
    return records, payload


def _integer(value: Any, maximum: int) -> int:
    if isinstance(value, bool):
        return -1
    try:
        return max(0, min(maximum, int(value)))
    except (TypeError, ValueError):
        return -1


def _card_id(value: Any) -> str:
    return str(value.get("card_id") or "unknown") if isinstance(value, Mapping) else "unknown"


def _entities(state: Mapping[str, Any]) -> dict[str, tuple[str, str, Mapping[str, Any]]]:
    result: dict[str, tuple[str, str, Mapping[str, Any]]] = {}
    for side in ("friendly", "opponent"):
        player = state[side]
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping):
                entity_id = str(entity.get("entity_id") or "")
                if entity_id:
                    result[entity_id] = (side, zone, entity)
        for zone in ("hand", "board"):
            for entity in player.get(zone, []):
                entity_id = str(entity.get("entity_id") or "")
                if entity_id:
                    result[entity_id] = (side, zone, entity)
    return result


def _add(features: defaultdict[str, float], name: str, value: float = 1.0) -> None:
    if value:
        features[name] += float(value)


def decision_candidate_features(
    pre_state: Mapping[str, Any],
    mode: str,
    action: Mapping[str, Any],
    legal_actions: Sequence[Mapping[str, Any]],
) -> dict[str, float]:
    """Build public, version-stable features for one caller-supplied candidate."""

    kind = str(action.get("kind") or "")
    friendly = pre_state["friendly"]
    opponent = pre_state["opponent"]
    entities = _entities(pre_state)
    source = entities.get(str(action.get("source_entity_id") or ""))
    target = entities.get(str(action.get("target_entity_id") or ""))
    turn = _integer(pre_state.get("turn"), 30)
    mana = _integer(friendly.get("mana"), 10)
    normalized_mode = str(mode or pre_state.get("mode") or "unknown").lower()
    kinds = [str(candidate.get("kind") or "") for candidate in legal_actions]
    friendly_hero = _card_id(friendly.get("hero"))
    opponent_hero = _card_id(opponent.get("hero"))
    features: defaultdict[str, float] = defaultdict(float)

    for name in (
        "bias",
        f"kind={kind}",
        f"kind_mode={kind}|{normalized_mode}",
        f"kind_turn={kind}|{turn // 2}",
        f"kind_mana={kind}|{mana}",
        f"kind_boards={kind}|{len(friendly.get('board', []))}|{len(opponent.get('board', []))}",
        f"kind_hand={kind}|{min(10, len(friendly.get('hand', [])))}",
        f"kind_heroes={kind}|{friendly_hero}|{opponent_hero}",
        f"kind_candidate_count={kind}|{min(20, len(legal_actions))}",
        f"kind_candidate_mix={kind}|attack={int('attack' in kinds)}|play={int('play_card' in kinds)}",
        f"kind_position={kind}|{int(action.get('board_position') or 0)}",
    ):
        _add(features, name)

    if source is not None:
        _, source_zone, entity = source
        card_id = str(action.get("card_id") or entity.get("card_id") or "unknown")
        card_type = str(entity.get("card_type") or "unknown").upper()
        cost = _integer(entity.get("cost"), 20)
        target_role = "none" if target is None else f"{target[0]}_{target[1]}"
        for name in (
            f"kind_zone={kind}|{source_zone}",
            f"kind_type={kind}|{card_type}",
            f"kind_card={kind}|{card_id}",
            f"mode_card={normalized_mode}|{card_id}",
            f"kind_cost={kind}|{cost}",
            f"card_target={card_id}|{target_role}",
        ):
            _add(features, name)
        _add(features, f"numeric_cost={kind}", cost / 10.0)
        _add(features, f"numeric_mana_after={kind}", (mana - cost) / 10.0)
        _add(
            features,
            f"numeric_source_attack={kind}",
            _integer(entity.get("attack"), 20) / 10.0,
        )
        _add(
            features,
            f"numeric_source_health={kind}",
            _integer(entity.get("current_health"), 30) / 10.0,
        )

    if target is not None:
        target_side, target_zone, entity = target
        for name in (
            f"kind_target={kind}|{target_side}_{target_zone}",
            f"kind_target_card={kind}|{_card_id(entity)}",
        ):
            _add(features, name)
        _add(
            features,
            f"numeric_target_attack={kind}",
            _integer(entity.get("attack"), 20) / 10.0,
        )
        _add(
            features,
            f"numeric_target_health={kind}",
            _integer(entity.get("current_health"), 30) / 10.0,
        )
    return dict(features)


def _dot(weights: Mapping[str, float], features: Mapping[str, float]) -> float:
    return sum(float(weights.get(key, 0.0)) * value for key, value in features.items())


def _probabilities(
    weights: Mapping[str, float],
    candidates: Sequence[Mapping[str, float]],
    temperature: float,
) -> list[float]:
    if not candidates:
        return []
    scores = [_dot(weights, features) / temperature for features in candidates]
    maximum = max(scores)
    exponentials = [math.exp(max(-60.0, score - maximum)) for score in scores]
    total = sum(exponentials)
    return [value / total for value in exponentials]


def _training_example(record: DecisionFrameRecord) -> dict[str, Any]:
    value = record.value
    candidates = list(value["legal_candidates"])
    actions = [candidate["action"] for candidate in candidates]
    selected = [
        index
        for index, candidate in enumerate(candidates)
        if candidate["candidate_id"] == value["selected_candidate_id"]
    ]
    if len(selected) != 1:
        raise DecisionRankerError("决策帧真实选择无法唯一定位")
    return {
        "decision_frame_id": record.decision_frame_id,
        "game_id": record.game_id,
        "mode": str(value["mode"]),
        "patch": str(value["pre_state"].get("patch") or value["client_build"]),
        "candidate_ids": [str(candidate["candidate_id"]) for candidate in candidates],
        "features": [
            decision_candidate_features(value["pre_state"], value["mode"], action, actions)
            for action in actions
        ],
        "selected_index": selected[0],
        "selected_template": _template_label(
            {
                "pre_state": value["pre_state"],
                "actor_side": "local",
                "actor_player_id": "friendly",
                "action": value["selected_action"],
            }
        ),
        "selected_kind": str(value["selected_action"]["kind"]),
    }


def _train_epoch(
    examples: Sequence[Mapping[str, Any]],
    weights: dict[str, float],
    accumulators: defaultdict[str, float],
    epoch: int,
) -> None:
    ordered = sorted(
        examples,
        key=lambda example: hashlib.sha256(
            f"{example['decision_frame_id']}:{epoch}".encode("utf-8")
        ).hexdigest(),
    )
    for example in ordered:
        features = example["features"]
        selected = int(example["selected_index"])
        probabilities = _probabilities(weights, features, 1.0)
        gradient: defaultdict[str, float] = defaultdict(float)
        for key, value in features[selected].items():
            gradient[key] += value
        for index, candidate in enumerate(features):
            probability = probabilities[index]
            for key, value in candidate.items():
                gradient[key] -= probability * value
        for key, value in gradient.items():
            accumulators[key] += value * value
            decayed = weights.get(key, 0.0) * (1.0 - _WEIGHT_DECAY)
            weights[key] = decayed + (
                _LEARNING_RATE * value / math.sqrt(accumulators[key] + 1e-8)
            )


def _average(values: Sequence[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def _evaluate(
    examples: Sequence[Mapping[str, Any]],
    weights: Mapping[str, float],
    temperature: float,
    train_templates: set[str],
) -> dict[str, Any]:
    if not examples:
        return {
            "status": "NO_DATA",
            "record_count": 0,
            "game_count": 0,
            "candidate_count": 0,
            "top1_accuracy": 0.0,
            "top3_accuracy": 0.0,
            "log_loss": 0.0,
            "uniform_top1_expected_accuracy": 0.0,
            "uniform_top3_expected_accuracy": 0.0,
            "uniform_log_loss": 0.0,
            "top1_lift_over_uniform": 0.0,
            "top3_lift_over_uniform": 0.0,
            "log_loss_excess": 0.0,
            "unseen_selected_template_rate": 0.0,
        }
    losses: list[float] = []
    top1: list[float] = []
    top3: list[float] = []
    reciprocal: list[float] = []
    uniform_loss: list[float] = []
    uniform_top1: list[float] = []
    uniform_top3: list[float] = []
    unseen = 0
    kinds: Counter[str] = Counter()
    candidate_count = 0
    for example in examples:
        probabilities = _probabilities(weights, example["features"], temperature)
        selected = int(example["selected_index"])
        ranked = sorted(
            range(len(probabilities)),
            key=lambda index: (
                -probabilities[index],
                str(example["candidate_ids"][index]),
            ),
        )
        rank = ranked.index(selected) + 1
        losses.append(-math.log(max(1e-12, probabilities[selected])))
        top1.append(float(rank == 1))
        top3.append(float(rank <= 3))
        reciprocal.append(1.0 / rank)
        count = len(probabilities)
        candidate_count += count
        uniform_loss.append(math.log(count))
        uniform_top1.append(1.0 / count)
        uniform_top3.append(min(3, count) / count)
        unseen += str(example["selected_template"]) not in train_templates
        kinds[str(example["selected_kind"])] += 1
    top1_value = _average(top1)
    top3_value = _average(top3)
    loss_value = _average(losses)
    uniform_top1_value = _average(uniform_top1)
    uniform_top3_value = _average(uniform_top3)
    uniform_loss_value = _average(uniform_loss)
    return {
        "status": "EVALUATED",
        "record_count": len(examples),
        "game_count": len({str(example["game_id"]) for example in examples}),
        "candidate_count": candidate_count,
        "average_candidate_count": candidate_count / len(examples),
        "top1_accuracy": top1_value,
        "top3_accuracy": top3_value,
        "mean_reciprocal_rank": _average(reciprocal),
        "log_loss": loss_value,
        "uniform_top1_expected_accuracy": uniform_top1_value,
        "uniform_top3_expected_accuracy": uniform_top3_value,
        "uniform_log_loss": uniform_loss_value,
        "top1_lift_over_uniform": top1_value - uniform_top1_value,
        "top3_lift_over_uniform": top3_value - uniform_top3_value,
        "log_loss_excess": loss_value - uniform_loss_value,
        "unseen_selected_template_count": unseen,
        "unseen_selected_template_rate": unseen / len(examples),
        "selected_action_kind_counts": dict(sorted(kinds.items())),
        "tie_breaker": "probability_desc_then_candidate_id_asc",
    }


def _check(name: str, actual: int | float, operator: str, expected: int | float) -> dict[str, Any]:
    passed = actual >= expected if operator == ">=" else actual <= expected
    return {
        "name": name,
        "actual": actual,
        "operator": operator,
        "expected": expected,
        "passed": passed,
    }


def train_decision_ranker_file(
    decision_frame_path: str | Path,
    behavior_path: str | Path,
    output_path: str | Path,
    *,
    policy_path: str | Path | None = None,
    max_epochs: int = _MAX_EPOCHS,
) -> dict[str, Any]:
    paths = [
        Path(decision_frame_path).resolve(),
        Path(behavior_path).resolve(),
        Path(output_path).resolve(),
    ]
    if policy_path is not None:
        paths.append(Path(policy_path).resolve())
    if len(set(paths)) != len(paths):
        raise DecisionRankerError("决策帧、行为语料、门槛和模型输出必须彼此独立")
    if isinstance(max_epochs, bool) or not 1 <= max_epochs <= 100:
        raise DecisionRankerError("训练轮数必须位于 1 到 100")
    audit = audit_decision_frame_file(decision_frame_path, behavior_path=behavior_path)
    if audit.get("passed") is not True:
        raise DecisionRankerError("决策帧与行为语料联审未通过")
    records, frame_bytes = _load_frames(decision_frame_path)
    policy = _load_policy(policy_path)
    examples = [_training_example(record) for record in records]
    split_examples = {
        split: [
            example
            for example in examples
            if deterministic_game_split(str(example["game_id"])) == split
        ]
        for split in _SPLITS
    }
    if not split_examples["train"] or not split_examples["validation"]:
        raise DecisionRankerError("决策帧缺少 train 或 validation 分割")

    weights: dict[str, float] = {}
    accumulators: defaultdict[str, float] = defaultdict(float)
    best_weights: dict[str, float] | None = None
    best_epoch = 0
    best_validation_loss = math.inf
    training_curve: list[dict[str, Any]] = []
    train_templates = {
        str(example["selected_template"]) for example in split_examples["train"]
    }
    for epoch in range(1, max_epochs + 1):
        _train_epoch(split_examples["train"], weights, accumulators, epoch)
        validation = _evaluate(
            split_examples["validation"], weights, 1.0, train_templates
        )
        validation_loss = float(validation["log_loss"])
        training_curve.append(
            {
                "epoch": epoch,
                "validation_log_loss": validation_loss,
                "validation_top1_accuracy": float(validation["top1_accuracy"]),
                "validation_top3_accuracy": float(validation["top3_accuracy"]),
            }
        )
        if validation_loss < best_validation_loss - 1e-12:
            best_validation_loss = validation_loss
            best_epoch = epoch
            best_weights = dict(weights)
    if best_weights is None:  # pragma: no cover - a validation split is required above.
        raise DecisionRankerError("训练没有产生可评估模型")

    temperature_results = []
    for temperature in _TEMPERATURE_GRID:
        metrics = _evaluate(
            split_examples["validation"], best_weights, temperature, train_templates
        )
        temperature_results.append(
            {
                "temperature": temperature,
                "validation_log_loss": float(metrics["log_loss"]),
            }
        )
    selected_temperature = min(
        temperature_results,
        key=lambda item: (item["validation_log_loss"], item["temperature"]),
    )["temperature"]
    evaluation = {
        split: _evaluate(
            split_examples[split], best_weights, selected_temperature, train_templates
        )
        for split in _SPLITS
    }

    checks: list[dict[str, Any]] = []
    for split in _SPLITS:
        metrics = evaluation[split]
        checks.extend(
            [
                _check(
                    f"{split}_game_count",
                    int(metrics["game_count"]),
                    ">=",
                    int(policy[f"min_{split}_games"]),
                ),
                _check(
                    f"{split}_record_count",
                    int(metrics["record_count"]),
                    ">=",
                    int(policy[f"min_{split}_records"]),
                ),
            ]
        )
    validation = evaluation["validation"]
    checks.extend(
        [
            _check(
                "validation_top1_lift_over_uniform",
                float(validation["top1_lift_over_uniform"]),
                ">=",
                float(policy["min_validation_top1_lift_over_uniform"]),
            ),
            _check(
                "validation_top3_lift_over_uniform",
                float(validation["top3_lift_over_uniform"]),
                ">=",
                float(policy["min_validation_top3_lift_over_uniform"]),
            ),
            _check(
                "validation_log_loss_excess",
                float(validation["log_loss_excess"]),
                "<=",
                float(policy["max_validation_log_loss_excess"]),
            ),
            _check(
                "validation_unseen_selected_template_rate",
                float(validation["unseen_selected_template_rate"]),
                "<=",
                float(policy["max_validation_unseen_selected_template_rate"]),
            ),
        ]
    )
    ready = all(check["passed"] for check in checks)
    behavior_identity = audit["behavior_input"]
    artifact: dict[str, Any] = {
        "schema": DECISION_RANKER_SCHEMA_ID,
        "model_type": DECISION_RANKER_MODEL_ID,
        "feature_contract": DECISION_RANKER_FEATURES_ID,
        "source_decision_frames": {
            "name": Path(decision_frame_path).name,
            "bytes": len(frame_bytes),
            "sha256": _sha256_bytes(frame_bytes),
            "record_count": len(records),
            "game_count": len({record.game_id for record in records}),
        },
        "source_behavior": {
            "name": Path(behavior_path).name,
            "bytes": int(behavior_identity["bytes"]),
            "sha256": str(behavior_identity["sha256"]),
        },
        "policy": policy,
        "policy_sha256": decision_ranker_policy_sha256(policy),
        "training": {
            "split": "train",
            "game_level_split": True,
            "max_epochs": max_epochs,
            "selected_epoch": best_epoch,
            "learning_rate": _LEARNING_RATE,
            "weight_decay": _WEIGHT_DECAY,
            "optimizer": "deterministic_adagrad",
            "example_order": "sha256_decision_frame_id_epoch",
            "record_count": len(split_examples["train"]),
            "game_count": len(
                {str(example["game_id"]) for example in split_examples["train"]}
            ),
            "validation_selected_temperature": selected_temperature,
            "temperature_grid": list(_TEMPERATURE_GRID),
            "training_curve": training_curve,
            "outcome_used": False,
            "opponent_candidates_used": False,
        },
        "evaluation": evaluation,
        "quality_checks": checks,
        "model": {
            "temperature": selected_temperature,
            "weights": {key: best_weights[key] for key in sorted(best_weights)},
            "weight_count": len(best_weights),
            "supported_modes": sorted({str(example["mode"]) for example in split_examples["train"]}),
            "supported_patches": sorted({str(example["patch"]) for example in split_examples["train"]}),
        },
        "candidate_ranking_training_complete": True,
        "candidate_ranking_ready": ready,
        "user_visible_behavior_reference_eligible": ready,
        "candidate_generation_allowed": False,
        "live_policy_eligible": False,
        "rl_training_eligible": False,
        "optimality_verified": False,
        "outcome_used_as_action_optimality": False,
        "approved_uses": [
            "rerank_hdt_supplied_legal_candidates",
            "offline_top_k_behavior_cloning",
            "user_visible_hdt_legal_behavior_reference",
        ],
        "prohibited_uses": [
            "action_generation",
            "optimal_action_ground_truth",
            "direct_rl_trajectory",
            "opponent_candidate_reconstruction",
        ],
        "caveat_zh": "该模型学习玩家在完整合法候选中实际选择了什么；它能给备选排序，但不证明任何选择最优。",
    }
    validated = validate_decision_ranker_artifact(artifact)
    payload = json.dumps(validated, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
    destination = Path(output_path)
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
    return validated


def validate_decision_ranker_artifact(value: Any) -> dict[str, Any]:
    if not isinstance(value, Mapping):
        raise DecisionRankerError("决策排序模型必须是对象")
    serialized = _canonical_json(value)
    if any(
        marker in serialized
        for marker in (b"anon-", b'\"game_id\"', b'\"state_id\"', b'\"entity_id\"')
    ):
        raise DecisionRankerError("决策排序模型包含不应保存的对局或实体标识")
    if value.get("schema") != DECISION_RANKER_SCHEMA_ID:
        raise DecisionRankerError("不支持的决策排序模型版本")
    if value.get("model_type") != DECISION_RANKER_MODEL_ID:
        raise DecisionRankerError("不支持的决策排序模型类型")
    if value.get("feature_contract") != DECISION_RANKER_FEATURES_ID:
        raise DecisionRankerError("决策排序特征合同不匹配")
    policy = value.get("policy")
    if not isinstance(policy, Mapping) or set(policy) != set(DEFAULT_DECISION_RANKER_POLICY):
        raise DecisionRankerError("决策排序模型门槛字段不完整")
    normalized_policy = _load_policy_from_mapping(policy)
    if value.get("policy_sha256") != decision_ranker_policy_sha256(normalized_policy):
        raise DecisionRankerError("决策排序模型门槛哈希不匹配")
    model = value.get("model")
    if not isinstance(model, Mapping):
        raise DecisionRankerError("决策排序模型参数缺失")
    temperature = model.get("temperature")
    if (
        isinstance(temperature, bool)
        or not isinstance(temperature, (int, float))
        or not math.isfinite(float(temperature))
        or float(temperature) <= 0.0
    ):
        raise DecisionRankerError("决策排序温度参数无效")
    weights = model.get("weights")
    if not isinstance(weights, Mapping) or not weights:
        raise DecisionRankerError("决策排序权重为空")
    for key, weight in weights.items():
        if (
            not isinstance(key, str)
            or not key
            or len(key) > 512
            or isinstance(weight, bool)
            or not isinstance(weight, (int, float))
            or not math.isfinite(float(weight))
        ):
            raise DecisionRankerError("决策排序权重字段无效")
    if model.get("weight_count") != len(weights):
        raise DecisionRankerError("决策排序权重数量不匹配")
    checks = value.get("quality_checks")
    if not isinstance(checks, Sequence) or isinstance(checks, (str, bytes)) or not checks:
        raise DecisionRankerError("决策排序质量门禁缺失")
    computed_ready = all(
        isinstance(check, Mapping) and check.get("passed") is True for check in checks
    )
    if value.get("candidate_ranking_training_complete") is not True:
        raise DecisionRankerError("决策排序训练没有完成")
    if value.get("candidate_ranking_ready") is not computed_ready:
        raise DecisionRankerError("决策排序就绪标记与质量门禁不一致")
    if value.get("user_visible_behavior_reference_eligible") is not computed_ready:
        raise DecisionRankerError("历史打法参考标记与质量门禁不一致")
    for field in (
        "candidate_generation_allowed",
        "live_policy_eligible",
        "rl_training_eligible",
        "optimality_verified",
        "outcome_used_as_action_optimality",
    ):
        if value.get(field) is not False:
            raise DecisionRankerError(f"安全字段 {field} 必须固定为 false")
    return json.loads(json.dumps(value, ensure_ascii=False))


def _load_policy_from_mapping(value: Mapping[str, Any]) -> dict[str, float | int]:
    result: dict[str, float | int] = {}
    for key in sorted(DEFAULT_DECISION_RANKER_POLICY):
        item = value[key]
        if key in _INTEGER_POLICY_KEYS:
            if isinstance(item, bool) or not isinstance(item, int) or item <= 0:
                raise DecisionRankerError(f"模型门槛 {key} 必须是正整数")
            result[key] = item
        else:
            if isinstance(item, bool) or not isinstance(item, (int, float)):
                raise DecisionRankerError(f"模型门槛 {key} 必须是数字")
            number = float(item)
            if not math.isfinite(number):
                raise DecisionRankerError(f"模型门槛 {key} 必须是有限数字")
            result[key] = number
    return result


def load_decision_ranker(path: str | Path, *, require_ready: bool = True) -> dict[str, Any]:
    try:
        payload = Path(path).read_bytes()
    except OSError as exc:
        raise DecisionRankerError("无法读取决策排序模型") from exc
    artifact = validate_decision_ranker_artifact(
        _strict_json_object(payload, "决策排序模型")
    )
    if require_ready and artifact["candidate_ranking_ready"] is not True:
        raise DecisionRankerError("决策排序模型未通过质量门禁")
    return artifact


def score_legal_decision_candidates(
    artifact: Mapping[str, Any],
    *,
    pre_state: Mapping[str, Any],
    mode: str,
    actions: Sequence[Mapping[str, Any]],
) -> list[float]:
    model = validate_decision_ranker_artifact(artifact)
    if model["candidate_ranking_ready"] is not True:
        raise DecisionRankerError("决策排序模型未通过质量门禁")
    if not actions:
        return []
    normalized_mode = str(mode or pre_state.get("mode") or "unknown")
    patch = str(pre_state.get("patch") or "unknown")
    parameters = model["model"]
    if (
        normalized_mode not in parameters["supported_modes"]
        or patch not in parameters["supported_patches"]
    ):
        return [1.0 / len(actions)] * len(actions)
    features = [
        decision_candidate_features(pre_state, normalized_mode, action, actions)
        for action in actions
    ]
    return _probabilities(
        parameters["weights"], features, float(parameters["temperature"])
    )
