#!/usr/bin/env python3
"""Build SQL-backed, bounded datasets for the card-modeling technical report."""

from __future__ import annotations

import argparse
import json
import sqlite3
from pathlib import Path
from typing import Any, Iterable


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INPUT = ROOT / "artifacts" / "card-modeling" / "current"

HEADLINE_SQL = """
SELECT
    c.meta_share_pct / 100.0 AS meta_share,
    c.selected_decks,
    c.core_evidence_decks,
    c.unique_cards,
    c.standard_pool_cards,
    SUM(s.execution_readiness = 'existing_verified_rule') AS verified_rules,
    SUM(s.execution_readiness = 'existing_verified_rule') * 1.0 / COUNT(*) AS verified_rule_rate,
    SUM(s.execution_readiness = 'template_candidate_requires_review') AS template_candidates,
    SUM(s.execution_readiness = 'template_candidate_requires_review') * 1.0 / COUNT(*) AS template_candidate_rate,
    SUM(s.execution_readiness = 'manual_ir_rule_required') AS manual_ir,
    SUM(s.execution_readiness = 'manual_ir_rule_required') * 1.0 / COUNT(*) AS manual_ir_rate,
    SUM(s.has_stochasticity) AS stochastic_cards
FROM corpus_summary AS c
CROSS JOIN semantic_cards AS s
GROUP BY
    c.meta_share_pct,
    c.selected_decks,
    c.core_evidence_decks,
    c.unique_cards,
    c.standard_pool_cards
""".strip()

READINESS_SQL = """
SELECT
    CASE execution_readiness
        WHEN 'existing_verified_rule' THEN '已有验证规则'
        WHEN 'template_candidate_requires_review' THEN '通用模板候选'
        ELSE '手工 IR / 事件模型'
    END AS readiness,
    COUNT(*) AS cards,
    COUNT(*) * 1.0 / (SELECT COUNT(*) FROM semantic_cards) AS share,
    CASE execution_readiness
        WHEN 'template_candidate_requires_review' THEN 1
        WHEN 'manual_ir_rule_required' THEN 2
        ELSE 3
    END AS rank,
    CASE execution_readiness
        WHEN 'existing_verified_rule' THEN '严格 CardID、文本指纹和显式结构化效果规则'
        WHEN 'template_candidate_requires_review' THEN '可由通用原语表达，但目标、顺序和边界仍需审核'
        ELSE '动态文本、嵌套事件、持续或替代效果、随机池或隐藏信息'
    END AS definition
FROM semantic_cards
GROUP BY execution_readiness
ORDER BY rank
""".strip()

RISK_SQL = """
WITH risk_rows(axis, cards, required_model, evidence) AS (
    SELECT '随机或玩家选择', SUM(has_stochasticity), 'Chance / Choice 节点与结果分布', '随机目标、随机生成、Discover、洗牌、选择分支' FROM semantic_cards
    UNION ALL
    SELECT '引用 GameTag', SUM(has_referenced_game_tags), '关键词规则注册表', 'HearthDb ReferencedTags' FROM semantic_cards
    UNION ALL
    SELECT '历史依赖', SUM(has_history_dependency), '版本化事件历史与计数器', 'this turn / this game / died / cast / played' FROM semantic_cards
    UNION ALL
    SELECT '隐藏信息或未知池', SUM(has_hidden_information), '卡池快照与 belief 更新', '对手手牌或牌库、Discover 和随机生成池' FROM semantic_cards
    UNION ALL
    SELECT '动态或拼接文本', SUM(has_text_quality_flags), '人工 IR 与运行时标量绑定', '占位符、升级态、多个文本版本' FROM semantic_cards
    UNION ALL
    SELECT 'Entourage 实体依赖', SUM(has_entourage_card_ids), '不能依赖 CardDefs 自动闭包', '主流卡的 EntourageCardIds' FROM semantic_cards
)
SELECT
    axis,
    cards,
    cards * 1.0 / (SELECT COUNT(*) FROM semantic_cards) AS share,
    required_model,
    evidence
FROM risk_rows
ORDER BY cards DESC, axis
""".strip()

OPERATIONS_SQL = """
SELECT
    operation,
    COUNT(DISTINCT card_id) AS cards,
    COUNT(DISTINCT card_id) * 1.0 / (SELECT COUNT(*) FROM semantic_cards) AS share,
    DENSE_RANK() OVER (ORDER BY COUNT(DISTINCT card_id) DESC) AS frequency_rank
FROM semantic_operations
GROUP BY operation
ORDER BY cards DESC, operation
LIMIT 10
""".strip()


def rows_as_dicts(cursor: sqlite3.Cursor) -> list[dict[str, Any]]:
    names = [column[0] for column in cursor.description]
    return [dict(zip(names, row, strict=True)) for row in cursor.fetchall()]


def _bool(value: Iterable[Any]) -> int:
    return int(bool(list(value)))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input-dir", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    root = args.input_dir.resolve()
    output = args.output or root / "report-datasets.json"
    manifest = json.loads((root / "corpus-manifest.json").read_text(encoding="utf-8-sig"))
    semantic_root = json.loads((root / "mainstream-card-semantics.json").read_text(encoding="utf-8-sig"))
    cards = semantic_root["cards"]
    standard_source = next(
        source for source in manifest["sources"] if source["name"] == "official_standard_pool"
    )
    standard_pool = json.loads(Path(standard_source["path"]).read_text(encoding="utf-8-sig"))

    connection = sqlite3.connect(":memory:")
    connection.execute(
        """
        CREATE TABLE corpus_summary (
            meta_share_pct REAL NOT NULL,
            selected_decks INTEGER NOT NULL,
            core_evidence_decks INTEGER NOT NULL,
            unique_cards INTEGER NOT NULL,
            standard_pool_cards INTEGER NOT NULL
        )
        """
    )
    connection.execute(
        """
        INSERT INTO corpus_summary VALUES (?, ?, ?, ?, ?)
        """,
        (
            manifest["scope"]["meta_share_pct"],
            manifest["counts"]["selected_decks"],
            manifest["counts"]["core_evidence_decks"],
            manifest["counts"]["unique_cards"],
            len(standard_pool["cards"]),
        ),
    )
    connection.execute(
        """
        CREATE TABLE semantic_cards (
            card_id TEXT PRIMARY KEY,
            execution_readiness TEXT NOT NULL,
            has_stochasticity INTEGER NOT NULL,
            has_hidden_information INTEGER NOT NULL,
            has_history_dependency INTEGER NOT NULL,
            has_text_quality_flags INTEGER NOT NULL,
            has_referenced_game_tags INTEGER NOT NULL,
            has_entourage_card_ids INTEGER NOT NULL
        )
        """
    )
    connection.executemany(
        "INSERT INTO semantic_cards VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
        [
            (
                card["card_id"],
                card["modeling"]["execution_readiness"],
                _bool(card["semantic_inventory"]["stochasticity"]),
                _bool(card["semantic_inventory"]["hidden_information"]),
                _bool(card["semantic_inventory"]["history_dependencies"]),
                _bool(card["modeling"]["text_quality_flags"]),
                _bool(card["referenced_game_tags"]),
                _bool(card["semantic_inventory"]["entourage_card_ids"]),
            )
            for card in cards
        ],
    )
    connection.execute(
        "CREATE TABLE semantic_operations (card_id TEXT NOT NULL, operation TEXT NOT NULL)"
    )
    connection.executemany(
        "INSERT INTO semantic_operations VALUES (?, ?)",
        [
            (card["card_id"], operation)
            for card in cards
            for operation in card["semantic_inventory"]["operations"]
        ],
    )

    queries = {
        "headline": HEADLINE_SQL,
        "readiness": READINESS_SQL,
        "risk_axes": RISK_SQL,
        "operations": OPERATIONS_SQL,
    }
    datasets = {
        name: rows_as_dicts(connection.execute(sql))
        for name, sql in queries.items()
    }
    connection.close()

    if len(datasets["headline"]) != 1 or len(datasets["readiness"]) != 3:
        raise ValueError("report aggregation returned an unexpected grain")
    if sum(row["cards"] for row in datasets["readiness"]) != len(cards):
        raise ValueError("readiness categories do not cover every semantic card")
    if datasets["headline"][0]["unique_cards"] != len(cards):
        raise ValueError("corpus and semantic card counts disagree")

    payload = {
        "schema_version": 1,
        "artifact_kind": "card-modeling-report-datasets-v1",
        "source_generated_at": semantic_root["generated_at_utc"],
        "queries": queries,
        "datasets": datasets,
    }
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(
        f"Report datasets built: headline={len(datasets['headline'])}, "
        f"readiness={len(datasets['readiness'])}, risk_axes={len(datasets['risk_axes'])}, "
        f"operations={len(datasets['operations'])}"
    )
    print(f"  Output: {output.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
