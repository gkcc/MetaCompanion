from __future__ import annotations

import hashlib
import json
import math
import os
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Mapping, Sequence

from .behavior_prior import (
    _evaluate_split,
    _template_label,
    behavior_prior_policy_sha256,
    load_and_validate_behavior_imitation,
    load_behavior_prior,
)
from .decision_frame import (
    DecisionFrameRecord,
    DecisionFrameValidationError,
    _jsonl_values,
    _read_bounded,
    audit_decision_frame_file,
)
from .decision_ranker import (
    _probabilities as _ranker_probabilities,
    decision_candidate_features,
    load_decision_ranker,
)
from .logging_store import deterministic_game_split


OBSERVED_POLICY_EVALUATION_SCHEMA_ID = "observed-policy-evaluation-v1"
OBSERVED_POLICY_EVALUATION_POLICY_SCHEMA_ID = (
    "observed-policy-evaluation-policy-v1"
)

DEFAULT_OBSERVED_POLICY_EVALUATION_POLICY: dict[str, float | int] = {
    "min_candidate_train_games": 30,
    "min_candidate_validation_games": 8,
    "min_candidate_test_games": 8,
    "min_candidate_train_records": 250,
    "min_candidate_validation_records": 50,
    "min_candidate_test_records": 50,
    "max_validation_candidate_log_loss_excess": 0.10,
    "min_validation_candidate_top3_accuracy": 0.50,
    "max_validation_unseen_selected_template_rate": 0.50,
    "min_opponent_train_games": 30,
    "min_opponent_validation_games": 8,
    "min_opponent_test_games": 8,
    "min_opponent_train_records": 200,
    "min_opponent_validation_records": 50,
    "min_opponent_test_records": 50,
    "max_validation_opponent_kind_log_loss_excess": 0.10,
    "max_validation_opponent_seen_template_log_loss_excess": 0.15,
    "max_validation_opponent_unseen_template_rate": 0.50,
}

_INTEGER_POLICY_KEYS = {
    key
    for key in DEFAULT_OBSERVED_POLICY_EVALUATION_POLICY
    if key.startswith("min_")
    and not key.endswith("accuracy")
}
_RATE_POLICY_KEYS = set(DEFAULT_OBSERVED_POLICY_EVALUATION_POLICY) - _INTEGER_POLICY_KEYS
_SPLITS = ("train", "validation", "test")


class ObservedPolicyEvaluationError(ValueError):
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
                raise ObservedPolicyEvaluationError(
                    f"{label} 包含重复字段：{key}"
                )
            result[key] = value
        return result

    try:
        value = json.loads(payload.decode("utf-8"), object_pairs_hook=pairs_hook)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ObservedPolicyEvaluationError(f"{label} 不是有效的 UTF-8 JSON") from exc
    if not isinstance(value, Mapping):
        raise ObservedPolicyEvaluationError(f"{label} 根节点必须是对象")
    return value


def _read_policy(path: str | Path | None) -> dict[str, float | int]:
    if path is None:
        return dict(DEFAULT_OBSERVED_POLICY_EVALUATION_POLICY)
    source = Path(path)
    try:
        payload = source.read_bytes()
    except OSError as exc:
        raise ObservedPolicyEvaluationError("无法读取观察策略评估门槛") from exc
    raw = _strict_json_object(payload, "观察策略评估门槛")
    if set(raw) != {"schema", "thresholds"}:
        raise ObservedPolicyEvaluationError("观察策略评估门槛字段不符合合同")
    if raw.get("schema") != OBSERVED_POLICY_EVALUATION_POLICY_SCHEMA_ID:
        raise ObservedPolicyEvaluationError("不支持的观察策略评估门槛版本")
    thresholds = raw.get("thresholds")
    if not isinstance(thresholds, Mapping) or set(thresholds) != set(
        DEFAULT_OBSERVED_POLICY_EVALUATION_POLICY
    ):
        raise ObservedPolicyEvaluationError("观察策略评估门槛项不完整")
    result: dict[str, float | int] = {}
    for key in sorted(DEFAULT_OBSERVED_POLICY_EVALUATION_POLICY):
        value = thresholds[key]
        if key in _INTEGER_POLICY_KEYS:
            if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
                raise ObservedPolicyEvaluationError(f"门槛 {key} 必须是正整数")
            result[key] = value
            continue
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise ObservedPolicyEvaluationError(f"门槛 {key} 必须是数字")
        number = float(value)
        if not math.isfinite(number) or number < 0.0:
            raise ObservedPolicyEvaluationError(f"门槛 {key} 超出支持范围")
        if (
            key.endswith("accuracy")
            or key.endswith("rate")
        ) and number > 1.0:
            raise ObservedPolicyEvaluationError(f"门槛 {key} 必须位于 0 到 1")
        result[key] = number
    return result


def observed_policy_evaluation_policy_sha256(
    policy: Mapping[str, float | int],
) -> str:
    return _sha256_bytes(
        _canonical_json(
            {
                "schema": OBSERVED_POLICY_EVALUATION_POLICY_SCHEMA_ID,
                "thresholds": dict(sorted(policy.items())),
            }
        )
    )


def _load_decision_frames(path: str | Path) -> tuple[list[DecisionFrameRecord], bytes]:
    payload = _read_bounded(path, label="decision_frame_input")
    values = _jsonl_values(payload, label="decision_frame_input")
    records: list[DecisionFrameRecord] = []
    for index, value in enumerate(values, start=1):
        try:
            records.append(DecisionFrameRecord.from_dict(value))
        except DecisionFrameValidationError as exc:
            raise ObservedPolicyEvaluationError(
                f"决策帧第 {index} 行未通过合同校验（{exc.code}）"
            ) from exc
    if not records:
        raise ObservedPolicyEvaluationError("决策帧文件为空")
    return records, payload


def _check(name: str, actual: int | float, operator: str, expected: int | float) -> dict[str, Any]:
    if operator == ">=":
        passed = actual >= expected
    elif operator == "<=":
        passed = actual <= expected
    else:  # pragma: no cover - callers use a fixed operator vocabulary.
        raise ObservedPolicyEvaluationError("内部质量门槛运算符无效")
    return {
        "name": name,
        "actual": actual,
        "operator": operator,
        "expected": expected,
        "passed": passed,
    }


def _average(values: Sequence[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def _game_macro(values: Mapping[str, Sequence[float]]) -> float:
    return _average([_average(items) for items in values.values() if items])


def _candidate_metrics(
    records: Sequence[DecisionFrameRecord],
    ranker: Mapping[str, Any],
    train_templates: set[str],
) -> dict[str, Any]:
    if not records:
        return {
            "status": "NO_DATA",
            "record_count": 0,
            "game_count": 0,
            "candidate_count": 0,
            "multi_candidate_record_count": 0,
            "top1_accuracy": 0.0,
            "top3_accuracy": 0.0,
            "log_loss": 0.0,
            "uniform_log_loss": 0.0,
            "log_loss_excess": 0.0,
            "unseen_selected_template_count": 0,
            "unseen_selected_template_rate": 0.0,
        }

    losses: list[float] = []
    uniform_losses: list[float] = []
    top1: list[float] = []
    top3: list[float] = []
    reciprocal_ranks: list[float] = []
    uniform_top1: list[float] = []
    uniform_top3: list[float] = []
    game_losses: dict[str, list[float]] = defaultdict(list)
    game_top1: dict[str, list[float]] = defaultdict(list)
    game_top3: dict[str, list[float]] = defaultdict(list)
    multi_losses: list[float] = []
    multi_top1: list[float] = []
    multi_top3: list[float] = []
    unseen = 0
    template_records = 0
    candidate_count = 0
    action_kinds: Counter[str] = Counter()
    modes: Counter[str] = Counter()

    for record in records:
        value = record.value
        candidates = list(value["legal_candidates"])
        actions = [candidate["action"] for candidate in candidates]
        parameters = ranker["model"]
        features = [
            decision_candidate_features(
                value["pre_state"], value["mode"], action, actions
            )
            for action in actions
        ]
        probabilities = _ranker_probabilities(
            parameters["weights"],
            features,
            float(parameters["temperature"]),
        )
        if len(probabilities) != len(candidates) or not probabilities:
            raise ObservedPolicyEvaluationError("行为先验没有为完整候选集返回有效概率")
        selected_id = str(value["selected_candidate_id"])
        selected_indexes = [
            index
            for index, candidate in enumerate(candidates)
            if candidate["candidate_id"] == selected_id
        ]
        if len(selected_indexes) != 1:
            raise ObservedPolicyEvaluationError("决策帧的真实选择无法唯一定位")
        selected_index = selected_indexes[0]
        selected_probability = max(1e-12, float(probabilities[selected_index]))
        ranked = sorted(
            range(len(candidates)),
            key=lambda index: (
                -float(probabilities[index]),
                str(candidates[index]["candidate_id"]),
            ),
        )
        rank = ranked.index(selected_index) + 1
        loss = -math.log(selected_probability)
        hit1 = float(rank == 1)
        hit3 = float(rank <= 3)
        game_id = record.game_id
        losses.append(loss)
        uniform_losses.append(math.log(len(candidates)))
        top1.append(hit1)
        top3.append(hit3)
        reciprocal_ranks.append(1.0 / rank)
        uniform_top1.append(1.0 / len(candidates))
        uniform_top3.append(min(3, len(candidates)) / len(candidates))
        game_losses[game_id].append(loss)
        game_top1[game_id].append(hit1)
        game_top3[game_id].append(hit3)
        if len(candidates) >= 2:
            multi_losses.append(loss)
            multi_top1.append(hit1)
            multi_top3.append(hit3)
        candidate_count += len(candidates)
        kind = str(value["selected_action"]["kind"])
        action_kinds[kind] += 1
        modes[str(value["mode"])] += 1
        if kind != "end_turn":
            template_records += 1
            template = _template_label(
                {
                    "pre_state": value["pre_state"],
                    "actor_side": "local",
                    "actor_player_id": "friendly",
                    "action": value["selected_action"],
                }
            )
            if template not in train_templates:
                unseen += 1

    return {
        "status": "EVALUATED",
        "record_count": len(records),
        "game_count": len({record.game_id for record in records}),
        "candidate_count": candidate_count,
        "average_candidate_count": candidate_count / len(records),
        "multi_candidate_record_count": len(multi_losses),
        "top1_accuracy": _average(top1),
        "top3_accuracy": _average(top3),
        "mean_reciprocal_rank": _average(reciprocal_ranks),
        "log_loss": _average(losses),
        "uniform_top1_expected_accuracy": _average(uniform_top1),
        "uniform_top3_expected_accuracy": _average(uniform_top3),
        "uniform_log_loss": _average(uniform_losses),
        "log_loss_excess": _average(losses) - _average(uniform_losses),
        "game_macro_top1_accuracy": _game_macro(game_top1),
        "game_macro_top3_accuracy": _game_macro(game_top3),
        "game_macro_log_loss": _game_macro(game_losses),
        "multi_candidate_top1_accuracy": _average(multi_top1),
        "multi_candidate_top3_accuracy": _average(multi_top3),
        "multi_candidate_log_loss": _average(multi_losses),
        "selected_template_record_count": template_records,
        "unseen_selected_template_count": unseen,
        "unseen_selected_template_rate": (
            unseen / template_records if template_records else 0.0
        ),
        "selected_action_kind_counts": dict(sorted(action_kinds.items())),
        "mode_counts": dict(sorted(modes.items())),
        "tie_breaker": "probability_desc_then_candidate_id_asc",
        "caveat_zh": "这里只衡量对真实玩家选择的模仿能力，不衡量胜率或最优性。",
    }


def _source_identity(path: str | Path, payload: bytes) -> dict[str, Any]:
    return {
        "name": Path(path).name,
        "bytes": len(payload),
        "sha256": _sha256_bytes(payload),
    }


def _action_core(value: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "kind": str(value.get("kind") or ""),
        "source_entity_id": str(value.get("source_entity_id") or ""),
        "target_entity_id": str(value.get("target_entity_id") or ""),
        "card_id": str(value.get("card_id") or ""),
        "board_position": int(value.get("board_position") or 0),
    }


def evaluate_observed_policy_files(
    decision_frame_path: str | Path,
    behavior_path: str | Path,
    imitation_path: str | Path,
    manifest_path: str | Path,
    prior_path: str | Path,
    decision_ranker_path: str | Path,
    *,
    policy_path: str | Path | None = None,
) -> dict[str, Any]:
    resolved = [
        Path(decision_frame_path).resolve(),
        Path(behavior_path).resolve(),
        Path(imitation_path).resolve(),
        Path(manifest_path).resolve(),
        Path(prior_path).resolve(),
        Path(decision_ranker_path).resolve(),
    ]
    if policy_path is not None:
        resolved.append(Path(policy_path).resolve())
    if len(set(resolved)) != len(resolved):
        raise ObservedPolicyEvaluationError("评估输入、门槛和模型文件必须彼此独立")

    decision_audit = audit_decision_frame_file(
        decision_frame_path,
        behavior_path=behavior_path,
    )
    if decision_audit.get("passed") is not True:
        raise ObservedPolicyEvaluationError("决策帧与行为语料联审未通过")
    frames, frame_bytes = _load_decision_frames(decision_frame_path)
    imitation_records, identity = load_and_validate_behavior_imitation(
        imitation_path,
        manifest_path,
    )
    prior = load_behavior_prior(prior_path, require_ready=True)
    ranker = load_decision_ranker(decision_ranker_path, require_ready=True)
    policy = _read_policy(policy_path)

    if (
        prior["source_dataset"]["sha256"] != identity["dataset_sha256"]
        or prior["source_manifest"]["sha256"] != identity["manifest_sha256"]
    ):
        raise ObservedPolicyEvaluationError("行为先验没有绑定本次模仿语料和清单")
    if (
        ranker["source_decision_frames"]["sha256"] != _sha256_bytes(frame_bytes)
        or ranker["source_behavior"]["sha256"]
        != decision_audit["behavior_input"]["sha256"]
    ):
        raise ObservedPolicyEvaluationError("决策排序模型没有绑定本次决策帧与行为语料")
    manifest = identity["manifest"]
    behavior_identity = decision_audit.get("behavior_input")
    if not isinstance(behavior_identity, Mapping) or (
        manifest["source_behavior"]["sha256"] != behavior_identity.get("sha256")
    ):
        raise ObservedPolicyEvaluationError("决策帧与模型训练所用行为语料不是同一份")

    imitation_by_source = {
        str(record["source_behavior_id"]): record for record in imitation_records
    }
    if len(imitation_by_source) != len(imitation_records):
        raise ObservedPolicyEvaluationError("模仿语料包含重复的行为来源")
    for frame in frames:
        value = frame.value
        imitation = imitation_by_source.get(str(value["selected_behavior_id"]))
        if imitation is None:
            raise ObservedPolicyEvaluationError("决策帧真实选择没有进入模型训练语料")
        if (
            imitation["actor_side"] != "local"
            or imitation["game_id"] != frame.game_id
            or _action_core(imitation["action"])
            != _action_core(value["selected_action"])
            or imitation["pre_state"] != value["pre_state"]
            or imitation["split"] != deterministic_game_split(frame.game_id)
        ):
            raise ObservedPolicyEvaluationError("决策帧与模仿样本的选择或局面不一致")

    split_frames = {
        split: [
            frame
            for frame in frames
            if deterministic_game_split(frame.game_id) == split
        ]
        for split in _SPLITS
    }
    train_templates = {
        _template_label(
            {
                "pre_state": frame.value["pre_state"],
                "actor_side": "local",
                "actor_player_id": "friendly",
                "action": frame.value["selected_action"],
            }
        )
        for frame in split_frames["train"]
    }
    candidate_evaluation = {
        split: _candidate_metrics(split_frames[split], ranker, train_templates)
        for split in _SPLITS
    }
    opponent_records = [
        record for record in imitation_records if record["actor_side"] == "opponent"
    ]
    opponent_splits = {
        split: [record for record in opponent_records if record["split"] == split]
        for split in _SPLITS
    }
    opponent_evaluation = {
        split: _evaluate_split(opponent_splits[split], prior["models"])
        for split in _SPLITS
    }

    candidate_checks: list[dict[str, Any]] = []
    for split in _SPLITS:
        metrics = candidate_evaluation[split]
        candidate_checks.extend(
            [
                _check(
                    f"candidate_{split}_game_count",
                    int(metrics["game_count"]),
                    ">=",
                    int(policy[f"min_candidate_{split}_games"]),
                ),
                _check(
                    f"candidate_{split}_record_count",
                    int(metrics["record_count"]),
                    ">=",
                    int(policy[f"min_candidate_{split}_records"]),
                ),
            ]
        )
    validation_candidate = candidate_evaluation["validation"]
    candidate_checks.extend(
        [
            _check(
                "validation_candidate_log_loss_excess",
                float(validation_candidate["log_loss_excess"]),
                "<=",
                float(policy["max_validation_candidate_log_loss_excess"]),
            ),
            _check(
                "validation_candidate_top3_accuracy",
                float(validation_candidate["top3_accuracy"]),
                ">=",
                float(policy["min_validation_candidate_top3_accuracy"]),
            ),
            _check(
                "validation_unseen_selected_template_rate",
                float(validation_candidate["unseen_selected_template_rate"]),
                "<=",
                float(policy["max_validation_unseen_selected_template_rate"]),
            ),
        ]
    )

    opponent_checks: list[dict[str, Any]] = []
    for split in _SPLITS:
        metrics = opponent_evaluation[split]
        opponent_checks.extend(
            [
                _check(
                    f"opponent_{split}_game_count",
                    int(metrics["game_count"]),
                    ">=",
                    int(policy[f"min_opponent_{split}_games"]),
                ),
                _check(
                    f"opponent_{split}_record_count",
                    int(metrics["record_count"]),
                    ">=",
                    int(policy[f"min_opponent_{split}_records"]),
                ),
            ]
        )
    validation_opponent = opponent_evaluation["validation"]
    opponent_checks.extend(
        [
            _check(
                "validation_opponent_kind_log_loss_excess",
                float(validation_opponent.get("kind_log_loss_excess", 1_000_000.0)),
                "<=",
                float(policy["max_validation_opponent_kind_log_loss_excess"]),
            ),
            _check(
                "validation_opponent_seen_template_log_loss_excess",
                float(
                    validation_opponent.get(
                        "seen_template_log_loss_excess", 1_000_000.0
                    )
                ),
                "<=",
                float(
                    policy[
                        "max_validation_opponent_seen_template_log_loss_excess"
                    ]
                ),
            ),
            _check(
                "validation_opponent_unseen_template_rate",
                float(validation_opponent["unseen_template_rate"]),
                "<=",
                float(policy["max_validation_opponent_unseen_template_rate"]),
            ),
        ]
    )

    candidate_ready = all(check["passed"] for check in candidate_checks)
    opponent_ready = all(check["passed"] for check in opponent_checks)
    ready = candidate_ready and opponent_ready
    prior_payload = Path(prior_path).read_bytes()
    ranker_payload = Path(decision_ranker_path).read_bytes()
    return {
        "schema": OBSERVED_POLICY_EVALUATION_SCHEMA_ID,
        "status": "READY" if ready else "NOT_READY",
        "source_decision_frames": {
            **_source_identity(decision_frame_path, frame_bytes),
            "record_count": len(frames),
            "game_count": len({frame.game_id for frame in frames}),
        },
        "source_behavior": {
            "name": Path(behavior_path).name,
            "bytes": int(behavior_identity["bytes"]),
            "sha256": str(behavior_identity["sha256"]),
        },
        "source_imitation_dataset": {
            "name": Path(imitation_path).name,
            "bytes": len(identity["dataset_bytes"]),
            "sha256": identity["dataset_sha256"],
            "record_count": len(imitation_records),
            "opponent_record_count": len(opponent_records),
        },
        "source_manifest": _source_identity(
            manifest_path, identity["manifest_bytes"]
        ),
        "source_prior": {
            **_source_identity(prior_path, prior_payload),
            "schema": prior["schema"],
            "training_policy_sha256": behavior_prior_policy_sha256(prior["policy"]),
        },
        "source_decision_ranker": {
            **_source_identity(decision_ranker_path, ranker_payload),
            "schema": ranker["schema"],
            "model_type": ranker["model_type"],
        },
        "policy": policy,
        "policy_sha256": observed_policy_evaluation_policy_sha256(policy),
        "decision_frame_contract_passed": True,
        "source_binding_passed": True,
        "game_level_split": True,
        "training_split_only_updates_model": True,
        "candidate_ranking": candidate_evaluation,
        "opponent_behavior": opponent_evaluation,
        "candidate_quality_checks": candidate_checks,
        "opponent_quality_checks": opponent_checks,
        "candidate_ranking_evaluation_ready": candidate_ready,
        "opponent_behavior_modeling_ready": opponent_ready,
        "search_ordering_prior_ready": ready,
        "candidate_generation_allowed": False,
        "live_policy_eligible": False,
        "rl_training_eligible": False,
        "optimality_verified": False,
        "outcome_used_as_action_optimality": False,
        "approved_uses": [
            "rerank_caller_supplied_legal_candidates",
            "opponent_public_behavior_prior",
            "offline_behavior_cloning_evaluation",
        ],
        "prohibited_uses": [
            "action_generation",
            "optimal_action_ground_truth",
            "direct_rl_trajectory",
            "hidden_opponent_card_reconstruction",
        ],
        "caveats_zh": [
            "本方指标只回答模型能否在 HDT 给出的完整合法候选中复现玩家选择，不证明该选择最优。",
            "对手没有本机 Options 候选集，因此只评估公开动作策略，不伪造对手合法候选。",
            "胜负字段不参与模型训练，也不把单步行为升级为强化学习真值。",
        ],
    }


def write_observed_policy_evaluation(
    report: Mapping[str, Any], path: str | Path
) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(report, ensure_ascii=False, indent=2).encode("utf-8") + b"\n"
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
