from __future__ import annotations

import json
import os
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any, Iterable


MAX_XML_BYTES = 32 * 1024 * 1024


def default_arena_drafts_path() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return Path(appdata) / "HearthstoneDeckTracker" / "ArenaLastDrafts.xml"


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _children(element: ET.Element, name: str) -> list[ET.Element]:
    return [child for child in element if _local_name(child.tag) == name]


def _text(element: ET.Element | None) -> str:
    return (element.text or "").strip() if element is not None else ""


def _integer(value: str, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def _number(value: str) -> float | None:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def parse_arena_drafts(path: str | Path) -> tuple[list[dict[str, Any]], list[str]]:
    source = Path(path)
    if not source.is_file():
        raise FileNotFoundError(source)
    if source.stat().st_size > MAX_XML_BYTES:
        raise ValueError(f"Arena draft XML exceeds {MAX_XML_BYTES} bytes")
    raw_bytes = source.read_bytes()
    if b"<!DOCTYPE" in raw_bytes.upper() or b"<!ENTITY" in raw_bytes.upper():
        raise ValueError("DTD/entity declarations are not accepted in arena draft XML")
    try:
        root = ET.fromstring(raw_bytes)
    except ET.ParseError as exc:
        raise ValueError(f"invalid arena draft XML: {exc}") from exc

    records: list[dict[str, Any]] = []
    warnings: list[str] = []
    drafts = [item for item in root.iter() if _local_name(item.tag) == "Draft"]
    for draft_index, draft in enumerate(drafts):
        # Deliberately do not hash Player/DeckId/StartTime: a stable hash remains an
        # identity-derived correlator. The ID is only a run-local ordinal.
        draft_id = f"draft-{draft_index + 1:04d}"
        for pick_index, pick in enumerate(_children(draft, "Pick")):
            choices = [_text(item) for item in _children(pick, "Choice") if _text(item)]
            picked = _text(next(iter(_children(pick, "Picked")), None))
            slot = _integer(_text(next(iter(_children(pick, "Slot")), None)), pick_index)
            if not choices and not picked:
                warnings.append(f"draft {draft_index} pick {pick_index}: no card IDs")
                continue
            scores: dict[str, float] = {}
            score_parents = _children(pick, "ArenasmithScores")
            if score_parents:
                for score in _children(score_parents[0], "ArenasmithScore"):
                    card_id = score.attrib.get("Card", "")
                    value = _number(score.attrib.get("Score", ""))
                    if card_id and value is not None:
                        scores[card_id] = value
            picked_before = [_text(item) for item in _children(pick, "PickedCards") if _text(item)]
            packages: dict[str, list[str]] = {}
            package_parents = _children(pick, "Packages")
            if package_parents:
                for package in _children(package_parents[0], "Package"):
                    key_card = package.attrib.get("KeyCard", "")
                    if key_card:
                        packages[key_card] = [_text(item) for item in _children(package, "Card") if _text(item)]
            time_on_choice = _integer(_text(next(iter(_children(pick, "TimeOnChoice")), None)), 0)
            records.append(
                {
                    "kind": "arena_draft_pick",
                    "schema_version": 1,
                    "draft_id": draft_id,
                    "pick_index": pick_index,
                    "slot": slot,
                    "offered_card_ids": choices,
                    "picked_card_id": picked,
                    "arenasmith_scores": scores,
                    "picked_card_ids_before": picked_before,
                    "packages": packages,
                    "time_on_choice_ms": max(0, time_on_choice),
                    "arenasmith_available": _text(
                        next(iter(_children(pick, "ArenasmithAvailable")), None)
                    ).lower()
                    == "true",
                    "is_underground": draft.attrib.get("IsUnderground", "false").lower() == "true",
                    "source": "HDT ArenaLastDrafts.xml",
                }
            )
    return records, warnings


def write_jsonl(records: Iterable[dict[str, Any]], path: str | Path, *, append: bool = False) -> int:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    mode = "a" if append else "w"
    count = 0
    with destination.open(mode, encoding="utf-8", newline="\n") as handle:
        for record in records:
            handle.write(json.dumps(record, ensure_ascii=False, separators=(",", ":"), sort_keys=True))
            handle.write("\n")
            count += 1
    return count
