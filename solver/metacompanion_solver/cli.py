from __future__ import annotations

import argparse
import json
import os
import secrets
import signal
import sys
import threading
from dataclasses import replace
from pathlib import Path
from typing import Sequence

from .api import create_server
from .arena_import import default_arena_drafts_path, parse_arena_drafts, write_jsonl
from .behavior_learning import (
    audit_behavior_learning_files,
    audit_runtime_behavior_learning,
    promote_behavior_imitation_file,
    write_behavior_learning_report,
)
from .behavior_candidate_alignment import (
    audit_behavior_candidate_alignment_files,
    write_behavior_candidate_alignment_report,
)
from .behavior_prior import train_behavior_prior_file
from .card_pool import OfficialCardPoolBundle, default_official_card_pool_directory
from .card_rules import StructuredCardRuleBundle, default_structured_card_rule_path
from .config import SolverConfig, load_config, training_log_path_for_data_dir
from .decision_frame import (
    DecisionFrameValidationError,
    audit_decision_frame_file,
)
from .decision_ranker import DecisionRankerError, train_decision_ranker_file
from .decision_solver_evaluation import (
    DecisionSolverEvaluationError,
    evaluate_decision_solver_binary,
    write_decision_solver_evaluation,
)
from .evaluation import evaluate_suite, write_evaluation_report
from .hdt_rule_evaluation import evaluate_hdt_rule_suite, write_hdt_rule_report
from .hdt_replay_behavior import (
    ReplayImportError,
    audit_hdt_replays,
    default_hdt_replay_directory,
    import_hdt_replays,
    write_replay_audit_report,
)
from .logging_store import JsonlTrainingLogger
from .models import HeuristicActionPrior, default_advisor_data_directory, load_mode_card_priors
from .offline import benchmark_file, replay_file
from .observed_policy_evaluation import (
    ObservedPolicyEvaluationError,
    evaluate_observed_policy_files,
    write_observed_policy_evaluation,
)
from .search import PuctTurnSearcher
from .selfplay import SelfPlaySettings, run_generic_self_play
from .service import SolverService
from .training import train_file
from .trajectory import (
    SOURCE_KIND_DIRECT_AUDIT,
    SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
    SOURCE_KIND_SYNTHETIC_FIXTURE,
    audit_runtime_trajectory,
    audit_trajectory_file,
    write_trajectory_report,
)
from .turnpair_evaluation import evaluate_turnpair_suite
from .verification import promote_trajectory_file
from .visible_response_evaluation import (
    evaluate_visible_response_binary,
    write_visible_response_report,
)


def _env_int(name: str) -> int | None:
    value = os.environ.get(name)
    if not value:
        return None
    try:
        return int(value)
    except ValueError:
        return None


_REPLAY_ERROR_MESSAGES = {
    "archive_unreadable": "无法读取回放文件",
    "archive_not_file": "回放路径不是文件",
    "archive_too_large": "单个回放文件过大",
    "archive_layout_invalid": "回放压缩包结构不受支持",
    "power_log_too_large": "回放中的 Power 日志过大",
    "archive_invalid": "回放文件已损坏或不是有效的 HDT 回放",
    "power_log_not_utf8": "回放中的 Power 日志编码无效",
    "replay_directory_missing": "未找到 HDT 回放目录",
    "too_many_replay_files": "回放文件数量超过安全上限",
    "replay_directory_too_large": "回放目录总大小超过安全上限",
    "controller_evidence_conflict": "无法可靠判定本方：回放中的行动方证据互相冲突",
    "controller_unresolved": "无法可靠判定本方玩家",
    "opponent_controller_unresolved": "无法可靠判定对手玩家",
    "build_missing_or_invalid": "回放缺少有效的客户端 build",
    "game_mode_metadata_missing": "回放缺少模式或卡牌格式信息",
    "requested_build_invalid": "--build 必须是 latest、all 或数字 build",
    "selected_modes_empty": "至少需要选择一种对局模式",
    "output_exists": "输出文件已存在；确认后可使用 --replace 显式替换",
    "result_log_write_failed": "终局语料写入失败",
    "replay_audit_not_passed": "回放审计未通过，已停止导入",
    "public_digest_conflict": "检测到相同公开轨迹对应不同内容，已停止导入",
    "player_entity_missing": "回放缺少玩家实体",
    "active_player_unresolved": "无法从公开状态判定当前行动方",
    "hero_missing": "回放缺少公开英雄实体",
}

_DECISION_FRAME_ERROR_MESSAGES = {
    "file_unreadable": "无法读取决策帧或行为文件",
    "file_size_invalid": "决策帧或行为文件大小不符合安全限制",
    "file_not_utf8": "决策帧或行为文件不是有效的 UTF-8",
    "blank_line": "决策帧或行为文件包含空行",
    "line_too_large": "决策帧或行为文件存在过大的单行",
    "invalid_json": "决策帧或行为文件包含无效 JSON",
}


def _replay_error_message(error: ReplayImportError) -> str:
    message = _REPLAY_ERROR_MESSAGES.get(error.code, "HDT 回放处理失败")
    return f"{message}（{error.code}）"


def _decision_frame_error_message(error: DecisionFrameValidationError) -> str:
    message = _DECISION_FRAME_ERROR_MESSAGES.get(
        error.code, "决策帧合同校验失败"
    )
    return f"{message}（{error.code}）"


def _env_truthy(name: str) -> bool:
    return os.environ.get(name, "").strip().lower() in {"1", "true", "yes", "on"}


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="metacompanion-solver")
    subparsers = parser.add_subparsers(dest="command", required=True)

    serve = subparsers.add_parser("serve", help="run the authenticated loopback HTTP solver")
    serve.add_argument(
        "--config",
        type=Path,
        default=Path(os.environ["METACOMPANION_SOLVER_CONFIG"])
        if os.environ.get("METACOMPANION_SOLVER_CONFIG")
        else None,
    )
    serve.add_argument("--session-token", default=os.environ.get("METACOMPANION_SOLVER_TOKEN", ""))
    serve.add_argument("--host", choices=("127.0.0.1",), default=os.environ.get("METACOMPANION_SOLVER_HOST"))
    serve.add_argument("--port", type=int, default=_env_int("METACOMPANION_SOLVER_PORT"))
    serve.add_argument(
        "--data-dir",
        type=Path,
        default=Path(os.environ["METACOMPANION_SOLVER_DATA_DIR"])
        if os.environ.get("METACOMPANION_SOLVER_DATA_DIR")
        else None,
    )
    logging_group = serve.add_mutually_exclusive_group()
    logging_group.add_argument("--training-log", type=Path)
    logging_group.add_argument(
        "--no-training-log",
        action="store_true",
        default=False,
    )
    serve.add_argument("--advisor-data", type=Path)

    replay = subparsers.add_parser("replay", help="solve versioned JSON/JSONL snapshots offline")
    replay.add_argument("--input", type=Path, required=True)
    replay.add_argument("--output", type=Path, required=True)
    replay.add_argument("--config", type=Path)

    benchmark = subparsers.add_parser("benchmark", help="measure latency and line legality")
    benchmark.add_argument("--input", type=Path, required=True)
    benchmark.add_argument("--config", type=Path)

    evaluate = subparsers.add_parser(
        "evaluate",
        help="run the deterministic oracle-turn-v1 quality and promotion gate",
    )
    evaluate.add_argument("--fixtures", type=Path, required=True)
    evaluate.add_argument("--output", type=Path)
    evaluate.add_argument("--baseline", type=Path)
    evaluate.add_argument("--config", type=Path)
    evaluate.add_argument("--seed", type=int)

    evaluate_turnpair = subparsers.add_parser(
        "evaluate-turnpair",
        help="run the independent oracle-turnpair-v1 counterplay quality gate",
    )
    evaluate_turnpair.add_argument("--fixtures", type=Path, required=True)
    evaluate_turnpair.add_argument("--output", type=Path)
    evaluate_turnpair.add_argument("--config", type=Path)
    evaluate_turnpair.add_argument("--seed", type=int)

    evaluate_hdt_rules = subparsers.add_parser(
        "evaluate-hdt-rules",
        help="run the independent raw-HDT point-effect rules quality gate",
    )
    evaluate_hdt_rules.add_argument("--fixtures", type=Path, required=True)
    evaluate_hdt_rules.add_argument("--output", type=Path)
    evaluate_hdt_rules.add_argument("--config", type=Path)
    evaluate_hdt_rules.add_argument("--seed", type=int)

    evaluate_visible_response = subparsers.add_parser(
        "evaluate-visible-response",
        help=(
            "run the independent raw-HDT hidden-information partial-response gate "
            "against a release Rust worker"
        ),
    )
    evaluate_visible_response.add_argument("--fixtures", type=Path, required=True)
    evaluate_visible_response.add_argument("--binary", type=Path, required=True)
    evaluate_visible_response.add_argument("--output", type=Path)
    evaluate_visible_response.add_argument(
        "--startup-timeout-seconds", type=float, default=10.0
    )

    audit_trajectories = subparsers.add_parser(
        "audit-trajectories",
        help="audit anonymized solve/action/result joins, replayability, splits, and privacy",
    )
    audit_trajectories.add_argument("--input", type=Path, required=True)
    audit_trajectories.add_argument("--output", type=Path)
    audit_trajectories.add_argument("--policy", type=Path)
    audit_trajectories.add_argument(
        "--source-kind",
        choices=(
            SOURCE_KIND_DIRECT_AUDIT,
            SOURCE_KIND_SYNTHETIC_FIXTURE,
            SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
        ),
        default=SOURCE_KIND_DIRECT_AUDIT,
    )

    audit_runtime = subparsers.add_parser(
        "audit-runtime-trajectories",
        help=(
            "snapshot and audit the real HDT training-v2.jsonl with the production policy; "
            "reports READY, NOT_READY, or NO_DATA"
        ),
    )
    audit_runtime.add_argument("--input", type=Path)
    audit_runtime.add_argument("--output", type=Path)
    audit_runtime.add_argument("--snapshot-dir", type=Path)
    audit_runtime.add_argument("--policy", type=Path)

    promote_trajectories = subparsers.add_parser(
        "promote-trajectories",
        help=(
            "offline-replay exact HDT Power identities, then write a separate "
            "verified corpus and hash-bound manifest"
        ),
    )
    promote_trajectories.add_argument("--input", type=Path, required=True)
    promote_trajectories.add_argument("--output", type=Path, required=True)
    promote_trajectories.add_argument("--manifest", type=Path, required=True)
    promote_trajectories.add_argument("--policy", type=Path)

    audit_behavior = subparsers.add_parser(
        "audit-behavior-learning",
        help=(
            "join behavior-v1 with terminal results and audit imitation-learning "
            "readiness without promoting records to RL"
        ),
    )
    audit_behavior.add_argument("--behavior", type=Path, required=True)
    audit_behavior.add_argument("--trajectory", type=Path, required=True)
    audit_behavior.add_argument("--output", type=Path)
    audit_behavior.add_argument("--policy", type=Path)
    audit_behavior.add_argument(
        "--source-kind",
        choices=(
            SOURCE_KIND_DIRECT_AUDIT,
            SOURCE_KIND_SYNTHETIC_FIXTURE,
            SOURCE_KIND_LIVE_RUNTIME_SNAPSHOT,
        ),
        default=SOURCE_KIND_DIRECT_AUDIT,
    )

    audit_runtime_behavior = subparsers.add_parser(
        "audit-runtime-behavior-learning",
        help=(
            "snapshot and jointly audit the real behavior-v1 and terminal-result logs; "
            "reports READY, NOT_READY, or NO_DATA"
        ),
    )
    audit_runtime_behavior.add_argument("--behavior", type=Path)
    audit_runtime_behavior.add_argument("--trajectory", type=Path)
    audit_runtime_behavior.add_argument("--output", type=Path)
    audit_runtime_behavior.add_argument("--snapshot-dir", type=Path)
    audit_runtime_behavior.add_argument("--policy", type=Path)

    promote_behavior = subparsers.add_parser(
        "promote-behavior-imitation",
        help=(
            "write a separate hash-bound imitation corpus from eligible observed "
            "behavior joined to terminal results"
        ),
    )
    promote_behavior.add_argument("--behavior", type=Path, required=True)
    promote_behavior.add_argument("--trajectory", type=Path, required=True)
    promote_behavior.add_argument("--output", type=Path, required=True)
    promote_behavior.add_argument("--manifest", type=Path, required=True)
    promote_behavior.add_argument("--policy", type=Path)

    train_behavior_prior = subparsers.add_parser(
        "train-behavior-prior",
        help=(
            "train and held-out evaluate a hash-bound observed-behavior prior; "
            "the artifact may order caller-supplied legal actions but never generate them"
        ),
    )
    train_behavior_prior.add_argument("--input", type=Path, required=True)
    train_behavior_prior.add_argument("--manifest", type=Path, required=True)
    train_behavior_prior.add_argument("--output", type=Path, required=True)
    train_behavior_prior.add_argument("--policy", type=Path)

    audit_behavior_candidates = subparsers.add_parser(
        "audit-behavior-candidates",
        help=(
            "audit whether observed local/opponent actions align with a complete, "
            "provably legal candidate set before candidate-ranking training"
        ),
    )
    audit_behavior_candidates.add_argument("--input", type=Path, required=True)
    audit_behavior_candidates.add_argument("--manifest", type=Path, required=True)
    audit_behavior_candidates.add_argument("--output", type=Path)
    audit_behavior_candidates.add_argument("--policy", type=Path)
    audit_behavior_candidates.add_argument("--rules", type=Path)

    train = subparsers.add_parser("train", help="build an offline baseline artifact")
    train.add_argument("--input", type=Path, required=True)
    train.add_argument("--output", type=Path, required=True)
    train.add_argument("--backend", choices=("frequency", "torch"), default="frequency")
    train.add_argument("--epochs", type=int, default=100)
    train.add_argument("--verification-manifest", type=Path)

    arena = subparsers.add_parser("import-arena", help="anonymize HDT ArenaLastDrafts.xml to JSONL")
    arena.add_argument("--input", type=Path)
    arena.add_argument("--output", type=Path, required=True)
    arena.add_argument("--append", action="store_true")

    audit_replays = subparsers.add_parser(
        "audit-hdt-replays",
        help="只读审计 HDT 历史回放中的双方公开行为、终局和隐私边界",
    )
    audit_replays.add_argument("--input", type=Path)
    audit_replays.add_argument("--output", type=Path)
    audit_replays.add_argument(
        "--build",
        default="latest",
        help="客户端 build；默认 latest，也可填数字或 all",
    )
    audit_replays.add_argument(
        "--mode",
        action="append",
        choices=("standard", "arena", "wild", "tavern_brawl"),
        help="可重复指定；默认 standard 和 arena",
    )

    audit_decision_frames = subparsers.add_parser(
        "audit-decision-frames",
        help="联审完整 HDT 决策帧与其绑定的双方行为语料",
    )
    audit_decision_frames.add_argument("--input", type=Path, required=True)
    audit_decision_frames.add_argument("--behavior", type=Path, required=True)
    audit_decision_frames.add_argument("--output", type=Path)

    audit_decision_solver = subparsers.add_parser(
        "audit-decision-solver-coverage",
        help="用真实 HDT 决策帧审计 Rust 根动作覆盖、exact 诚实性与可复核备选",
    )
    audit_decision_solver.add_argument(
        "--decision-frames", type=Path, required=True
    )
    audit_decision_solver.add_argument("--behavior", type=Path, required=True)
    audit_decision_solver.add_argument("--binary", type=Path, required=True)
    audit_decision_solver.add_argument(
        "--card-defs",
        type=Path,
        help="可选：与决策帧 build 完全一致的 CardDefs XML，用于公开卡牌文本补全",
    )
    audit_decision_solver.add_argument("--output", type=Path)
    audit_decision_solver.add_argument("--max-frames", type=int, default=256)
    audit_decision_solver.add_argument("--time-budget-ms", type=int, default=250)
    audit_decision_solver.add_argument("--max-iterations", type=int, default=100_000)
    audit_decision_solver.add_argument("--max-depth", type=int, default=8)
    audit_decision_solver.add_argument("--top-k", type=int, default=10)
    audit_decision_solver.add_argument(
        "--startup-timeout-seconds", type=float, default=10.0
    )

    evaluate_observed_policy = subparsers.add_parser(
        "evaluate-observed-policy",
        help=(
            "用 HDT 完整候选评估本方选择排序，并独立评估对手公开行为模型"
        ),
    )
    evaluate_observed_policy.add_argument(
        "--decision-frames", type=Path, required=True
    )
    evaluate_observed_policy.add_argument("--behavior", type=Path, required=True)
    evaluate_observed_policy.add_argument("--imitation", type=Path, required=True)
    evaluate_observed_policy.add_argument("--manifest", type=Path, required=True)
    evaluate_observed_policy.add_argument("--prior", type=Path, required=True)
    evaluate_observed_policy.add_argument("--ranker", type=Path, required=True)
    evaluate_observed_policy.add_argument("--output", type=Path)
    evaluate_observed_policy.add_argument("--policy", type=Path)

    train_decision_ranker = subparsers.add_parser(
        "train-decision-ranker",
        help="只用 train 对局训练 HDT 完整候选 listwise 排序基线",
    )
    train_decision_ranker.add_argument(
        "--decision-frames", type=Path, required=True
    )
    train_decision_ranker.add_argument("--behavior", type=Path, required=True)
    train_decision_ranker.add_argument("--output", type=Path, required=True)
    train_decision_ranker.add_argument("--policy", type=Path)
    train_decision_ranker.add_argument("--epochs", type=int, default=20)

    import_replays = subparsers.add_parser(
        "import-hdt-replays",
        help="把合格 HDT 回放导入独立、脱敏、非 RL 的双方行为语料",
    )
    import_replays.add_argument("--input", type=Path)
    import_replays.add_argument("--output-dir", type=Path, required=True)
    import_replays.add_argument(
        "--build",
        default="latest",
        help="客户端 build；默认 latest，也可填数字或 all",
    )
    import_replays.add_argument(
        "--mode",
        action="append",
        choices=("standard", "arena", "wild", "tavern_brawl"),
        help="可重复指定；默认 standard 和 arena",
    )
    import_replays.add_argument(
        "--replace",
        action="store_true",
        help="显式替换同目录中的旧导入产物",
    )
    import_replays.add_argument(
        "--card-defs",
        type=Path,
        help="可选：与所选回放 build 完全一致的 CardDefs XML；只登记公开卡牌元数据",
    )

    self_play = subparsers.add_parser(
        "self-play",
        help="generate bounded generic PUCT trajectories (does not train an RL model)",
    )
    self_play.add_argument("--input", type=Path, required=True)
    self_play.add_argument("--output-dir", type=Path, required=True)
    self_play.add_argument("--episodes", type=int, default=10)
    self_play.add_argument("--max-turns", type=int, default=40)
    self_play.add_argument("--time-limit-seconds", type=float, default=3600.0)
    self_play.add_argument("--search-budget-ms", type=int, default=100)
    self_play.add_argument("--max-iterations", type=int, default=200)
    self_play.add_argument("--max-depth", type=int, default=24)
    self_play.add_argument("--checkpoint-every", type=int, default=1)
    self_play.add_argument("--seed", type=int, default=0)
    self_play.add_argument("--resume", action="store_true")
    return parser


def _config(path: Path | None) -> SolverConfig:
    config = load_config(path)
    if path and config.training_log_path and not Path(config.training_log_path).is_absolute():
        config = replace(config, training_log_path=str(path.resolve().parent / config.training_log_path))
    if path and config.advisor_data_path and not Path(config.advisor_data_path).is_absolute():
        config = replace(config, advisor_data_path=str(path.resolve().parent / config.advisor_data_path))
    return config


def _serve(args: argparse.Namespace) -> int:
    config = _config(args.config)
    if args.host is not None:
        config = replace(config, host=args.host)
        config.validate()
    if args.port is not None:
        config = replace(config, port=args.port)
        config.validate()
    if args.data_dir is not None:
        config = replace(
            config,
            training_log_path=training_log_path_for_data_dir(args.data_dir),
        )
    if args.training_log is not None:
        config = replace(config, training_log_path=str(args.training_log))
    if args.no_training_log or (
        args.training_log is None and _env_truthy("METACOMPANION_SOLVER_NO_TRAINING_LOG")
    ):
        config = replace(config, training_log_path=None)
    prior_path = args.advisor_data or (Path(config.advisor_data_path) if config.advisor_data_path else None)
    if prior_path is None and args.data_dir is not None:
        candidate = args.data_dir / "AdvisorData"
        if candidate.is_dir():
            prior_path = candidate
    if prior_path is None:
        prior_path = default_advisor_data_directory()
    card_priors_by_mode = load_mode_card_priors(prior_path) if prior_path else {}
    prior = HeuristicActionPrior(card_weights_by_mode=card_priors_by_mode)
    official_pool_path = (
        prior_path / "OfficialCardPools"
        if prior_path is not None
        else default_official_card_pool_directory()
    )
    official_card_pools = (
        OfficialCardPoolBundle.load_optional(official_pool_path)
        if official_pool_path is not None
        else OfficialCardPoolBundle.unavailable()
    )
    service = SolverService(
        config,
        searcher=PuctTurnSearcher(prior=prior),
        logger=JsonlTrainingLogger(config.training_log_path),
        official_card_pools=official_card_pools,
        structured_card_rules=StructuredCardRuleBundle.load_optional(
            default_structured_card_rule_path()
        ),
    )
    generated_token = not bool(args.session_token)
    token = args.session_token or secrets.token_urlsafe(32)
    server = create_server(service, token, config.host, config.port, config.max_request_bytes)
    startup = {
        "event": "ready",
        "host": config.host,
        "port": server.server_address[1],
        "api_version": service.health()["api_version"],
        "card_prior_count": sum(len(values) for values in card_priors_by_mode.values()),
        "card_prior_modes": sorted(card_priors_by_mode),
        "official_card_pools": official_card_pools.health(),
        "structured_card_rules": service.structured_card_rules.health(),
    }
    if generated_token:
        startup["session_token"] = token
    print(json.dumps(startup, separators=(",", ":")), flush=True)
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    try:
        if args.command == "serve":
            return _serve(args)
        if args.command == "replay":
            count = replay_file(args.input, args.output, _config(args.config))
            print(json.dumps({"replayed": count, "output": str(args.output)}))
            return 0
        if args.command == "benchmark":
            print(json.dumps(benchmark_file(args.input, _config(args.config)), indent=2))
            return 0
        if args.command == "evaluate":
            report = evaluate_suite(
                args.fixtures,
                _config(args.config),
                baseline_path=args.baseline,
                seed_override=args.seed,
            )
            if args.output is not None:
                write_evaluation_report(report, args.output)
            print(json.dumps(report, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "evaluate-turnpair":
            service = SolverService(
                _config(args.config),
                logger=JsonlTrainingLogger(None),
            )
            report = evaluate_turnpair_suite(
                args.fixtures,
                service.solve,
                seed_override=args.seed,
            )
            if args.output is not None:
                write_evaluation_report(report, args.output)
            print(json.dumps(report, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "evaluate-hdt-rules":
            service = SolverService(
                _config(args.config),
                logger=JsonlTrainingLogger(None),
            )
            report = evaluate_hdt_rule_suite(
                args.fixtures,
                service.solve,
                seed_override=args.seed,
            )
            if args.output is not None:
                write_hdt_rule_report(report, args.output)
            print(json.dumps(report, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "evaluate-visible-response":
            if args.startup_timeout_seconds <= 0:
                raise ValueError("--startup-timeout-seconds must be positive")
            report = evaluate_visible_response_binary(
                args.fixtures,
                args.binary,
                startup_timeout_seconds=args.startup_timeout_seconds,
            )
            if args.output is not None:
                write_visible_response_report(report, args.output)
            print(json.dumps(report, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "audit-trajectories":
            report = audit_trajectory_file(
                args.input,
                policy_path=args.policy,
                source_kind=args.source_kind,
            )
            if args.output is not None:
                write_trajectory_report(report, args.output)
            print(json.dumps(report, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "audit-runtime-trajectories":
            snapshot_directory = args.snapshot_dir
            if snapshot_directory is None:
                snapshot_directory = (
                    args.output.parent / "runtime-trajectory-snapshots"
                    if args.output is not None
                    else Path.cwd() / "artifacts" / "runtime-trajectory-audit" / "snapshots"
                )
            report = audit_runtime_trajectory(
                input_path=args.input,
                snapshot_directory=snapshot_directory,
                policy_path=args.policy,
            )
            if args.output is not None:
                write_trajectory_report(report, args.output)
            print(json.dumps(report, indent=2))
            if report["status"] == "READY":
                return 0
            return 4 if report["status"] == "NO_DATA" else 3
        if args.command == "promote-trajectories":
            manifest = promote_trajectory_file(
                args.input,
                args.output,
                args.manifest,
                policy_path=args.policy,
            )
            print(json.dumps(manifest, indent=2))
            return 0
        if args.command == "audit-behavior-learning":
            report = audit_behavior_learning_files(
                args.behavior,
                args.trajectory,
                policy_path=args.policy,
                source_kind=args.source_kind,
            )
            if args.output is not None:
                write_behavior_learning_report(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0 if report["imitation_ready"] else 3
        if args.command == "audit-runtime-behavior-learning":
            snapshot_directory = args.snapshot_dir
            if snapshot_directory is None:
                snapshot_directory = (
                    args.output.parent / "runtime-behavior-learning-snapshots"
                    if args.output is not None
                    else Path.cwd()
                    / "artifacts"
                    / "runtime-behavior-learning-audit"
                    / "snapshots"
                )
            report = audit_runtime_behavior_learning(
                behavior_path=args.behavior,
                trajectory_path=args.trajectory,
                snapshot_directory=snapshot_directory,
                policy_path=args.policy,
            )
            if args.output is not None:
                write_behavior_learning_report(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            if report["status"] == "READY":
                return 0
            return 4 if report["status"] == "NO_DATA" else 3
        if args.command == "promote-behavior-imitation":
            manifest = promote_behavior_imitation_file(
                args.behavior,
                args.trajectory,
                args.output,
                args.manifest,
                policy_path=args.policy,
            )
            print(json.dumps(manifest, ensure_ascii=False, indent=2))
            return 0
        if args.command == "train-behavior-prior":
            artifact = train_behavior_prior_file(
                args.input,
                args.manifest,
                args.output,
                policy_path=args.policy,
            )
            print(json.dumps(artifact, ensure_ascii=False, indent=2))
            return 0 if artifact["search_ordering_prior_ready"] else 3
        if args.command == "audit-behavior-candidates":
            report = audit_behavior_candidate_alignment_files(
                args.input,
                args.manifest,
                policy_path=args.policy,
                rules_path=args.rules,
            )
            if args.output is not None:
                write_behavior_candidate_alignment_report(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0 if report["candidate_ranking_training_ready"] else 3
        if args.command == "train":
            result = train_file(
                args.input,
                args.output,
                args.backend,
                args.epochs,
                args.verification_manifest,
            )
            print(json.dumps(result, indent=2))
            return 0
        if args.command == "import-arena":
            source = args.input or default_arena_drafts_path()
            if source is None:
                raise ValueError("--input is required when APPDATA is unavailable")
            records, warnings = parse_arena_drafts(source)
            count = write_jsonl(records, args.output, append=args.append)
            print(json.dumps({"imported": count, "warnings": warnings, "output": str(args.output)}))
            return 0
        if args.command == "audit-hdt-replays":
            source = args.input or default_hdt_replay_directory()
            if source is None:
                raise ValueError("未找到 HDT 回放目录，请显式提供 --input")
            report = audit_hdt_replays(
                source,
                requested_build=args.build,
                modes=args.mode or ("standard", "arena"),
            )
            if args.output is not None:
                write_replay_audit_report(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "audit-decision-frames":
            report = audit_decision_frame_file(
                args.input,
                behavior_path=args.behavior,
            )
            if args.output is not None:
                write_replay_audit_report(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "audit-decision-solver-coverage":
            report = evaluate_decision_solver_binary(
                args.decision_frames,
                args.behavior,
                args.binary,
                max_frames=args.max_frames,
                time_budget_ms=args.time_budget_ms,
                max_iterations=args.max_iterations,
                max_depth=args.max_depth,
                top_k=args.top_k,
                startup_timeout_seconds=args.startup_timeout_seconds,
                card_defs_path=args.card_defs,
            )
            if args.output is not None:
                write_decision_solver_evaluation(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0 if report["passed"] else 3
        if args.command == "evaluate-observed-policy":
            report = evaluate_observed_policy_files(
                args.decision_frames,
                args.behavior,
                args.imitation,
                args.manifest,
                args.prior,
                args.ranker,
                policy_path=args.policy,
            )
            if args.output is not None:
                write_observed_policy_evaluation(report, args.output)
            print(json.dumps(report, ensure_ascii=False, indent=2))
            return 0 if report["search_ordering_prior_ready"] else 3
        if args.command == "train-decision-ranker":
            artifact = train_decision_ranker_file(
                args.decision_frames,
                args.behavior,
                args.output,
                policy_path=args.policy,
                max_epochs=args.epochs,
            )
            print(json.dumps(artifact, ensure_ascii=False, indent=2))
            return 0 if artifact["candidate_ranking_ready"] else 3
        if args.command == "import-hdt-replays":
            source = args.input or default_hdt_replay_directory()
            if source is None:
                raise ValueError("未找到 HDT 回放目录，请显式提供 --input")
            manifest = import_hdt_replays(
                source,
                args.output_dir,
                requested_build=args.build,
                modes=args.mode or ("standard", "arena"),
                card_defs_path=args.card_defs,
                replace=args.replace,
            )
            print(json.dumps(manifest, ensure_ascii=False, indent=2))
            return 0
        if args.command == "self-play":
            cancel_event = threading.Event()
            previous_sigint = signal.getsignal(signal.SIGINT)
            previous_sigterm = signal.getsignal(signal.SIGTERM) if hasattr(signal, "SIGTERM") else None

            def request_cancel(signum, frame):
                cancel_event.set()

            signal.signal(signal.SIGINT, request_cancel)
            if hasattr(signal, "SIGTERM"):
                signal.signal(signal.SIGTERM, request_cancel)
            try:
                manifest = run_generic_self_play(
                    args.input,
                    args.output_dir,
                    SelfPlaySettings(
                        episodes=args.episodes,
                        max_turns=args.max_turns,
                        time_limit_seconds=args.time_limit_seconds,
                        search_budget_ms=args.search_budget_ms,
                        max_iterations=args.max_iterations,
                        max_depth=args.max_depth,
                        checkpoint_every=args.checkpoint_every,
                        seed=args.seed,
                    ),
                    resume=args.resume,
                    cancel_event=cancel_event,
                )
            finally:
                signal.signal(signal.SIGINT, previous_sigint)
                if hasattr(signal, "SIGTERM") and previous_sigterm is not None:
                    signal.signal(signal.SIGTERM, previous_sigterm)
            print(json.dumps(manifest, indent=2))
            return 0 if manifest["status"] == "completed" else 130
    except ReplayImportError as exc:
        print(f"错误：{_replay_error_message(exc)}", file=sys.stderr)
        return 2
    except DecisionFrameValidationError as exc:
        print(f"错误：{_decision_frame_error_message(exc)}", file=sys.stderr)
        return 2
    except DecisionSolverEvaluationError as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 2
    except ObservedPolicyEvaluationError as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 2
    except DecisionRankerError as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 2
    except (OSError, ValueError, RuntimeError) as exc:
        print(f"错误：{exc}", file=sys.stderr)
        return 2
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
