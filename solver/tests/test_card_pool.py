from __future__ import annotations

import hashlib
import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

import _path  # noqa: F401

from metacompanion_solver.card_pool import CardPoolError, OfficialCardPoolBundle
from metacompanion_solver.schemas import Card, CardType

from helpers import state


NOW_UTC = datetime(2026, 7, 30, 12, 0, 0, tzinfo=timezone.utc)
GENERATED_AT_UTC = NOW_UTC - timedelta(hours=24)
FETCHED_AT_UTC = NOW_UTC - timedelta(hours=23)


def _write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _refresh_manifest_marker(latest: Path) -> None:
    manifest_path = latest / "manifest.json"
    publish_path = latest / "publish-complete.json"
    publish = json.loads(publish_path.read_text(encoding="utf-8"))
    publish["manifest_sha256"] = _sha256(manifest_path)
    _write_json(publish_path, publish)


def _bundle(root: Path, *, card_defs_path: Path | None = None) -> Path:
    latest = root / "latest"
    latest.mkdir(parents=True)
    run_id = "run-1"
    card_defs_path = card_defs_path or (root / "CardDefs.base.xml")
    card_defs_path.parent.mkdir(parents=True, exist_ok=True)
    card_defs_path.write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<CardDefs build="247416">\n'
        '  <Entity CardID="STD_CARD" ID="1" />\n'
        '  <Entity CardID="ARENA_CARD" ID="2" />\n'
        '</CardDefs>\n',
        encoding="utf-8",
    )
    pool_records = []
    for format_name, card_id, dbf_id in (
        ("standard", "STD_CARD", 1),
        ("arena", "ARENA_CARD", 2),
    ):
        pool = {
            "schema_version": 1,
            "format": format_name,
            "run_id": run_id,
            "declared_count": 1,
            "coverage": {"rules_coverage": False},
            "cards": [
                {
                    "card_id": card_id,
                    "dbf_id": dbf_id,
                    "collectible": True,
                    "name": card_id,
                    "card_set_id": 10,
                    "class_id": 4 if format_name == "standard" else 8,
                    "multi_class_ids": [],
                    "card_type_id": 5 if format_name == "standard" else 4,
                    "spell_school_id": 1 if format_name == "standard" else None,
                    "minion_type_id": 24 if format_name == "arena" else None,
                    "multi_type_ids": [],
                    "keyword_ids": [8] if format_name == "arena" else [],
                    "rarity_id": 1,
                    "mana_cost": 8 if format_name == "standard" else 3,
                    "attack": 0,
                    "health": 0,
                    "durability": 0,
                    "text": "",
                }
            ],
        }
        path = latest / f"{format_name}.json"
        _write_json(path, pool)
        pool_records.append(
            {
                "format": format_name,
                "file": path.name,
                "bytes": path.stat().st_size,
                "sha256": _sha256(path),
                "declared_count": 1,
                "unique_card_ids": 1,
                "unique_dbf_ids": 1,
                "fetched_at_utc": FETCHED_AT_UTC.isoformat(),
                "pages": [
                    {
                        "page": 1,
                        "fetched_at_utc": FETCHED_AT_UTC.isoformat(),
                    }
                ],
            }
        )
    manifest = {
        "schema_version": 1,
        "status": "complete",
        "run_id": run_id,
        "generated_at_utc": GENERATED_AT_UTC.isoformat(),
        "source": {
            "provider": "Blizzard",
            "authentication": "none",
            "browser_required": False,
        },
        "coverage": {"rules_coverage": False},
        "card_defs": {
            "file_name": "CardDefs.base.xml",
            "build": "247416",
            "entities": 2,
            "bytes": card_defs_path.stat().st_size,
            "sha256": _sha256(card_defs_path),
        },
        "pools": pool_records,
    }
    manifest_path = latest / "manifest.json"
    _write_json(manifest_path, manifest)
    _write_json(
        latest / "publish-complete.json",
        {
            "schema_version": 1,
            "run_id": run_id,
            "manifest_sha256": _sha256(manifest_path),
        },
    )
    return latest


def _load(root: Path, **kwargs) -> OfficialCardPoolBundle:
    kwargs.setdefault("max_age", timedelta(hours=72))
    return OfficialCardPoolBundle.load(
        root,
        card_defs_path=root / "CardDefs.base.xml",
        now_utc=NOW_UTC,
        **kwargs,
    )


def _load_optional(root: Path, **kwargs) -> OfficialCardPoolBundle:
    kwargs.setdefault("max_age", timedelta(hours=72))
    return OfficialCardPoolBundle.load_optional(
        root,
        card_defs_path=root / "CardDefs.base.xml",
        now_utc=NOW_UTC,
        **kwargs,
    )


class OfficialCardPoolTests(unittest.TestCase):
    def test_loads_hash_bound_standard_and_arena_bundle(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _bundle(root)
            bundle = _load(root)
            self.assertTrue(bundle.available)
            self.assertEqual({"STD_CARD"}, set(bundle.cards_by_format["standard"]))
            self.assertEqual({"ARENA_CARD"}, set(bundle.cards_by_format["arena"]))
            health = bundle.health()
            self.assertFalse(health["rules_coverage"])
            self.assertEqual("run-1", health["run_id"])
            self.assertEqual("247416", health["card_defs_build"])
            self.assertEqual(_sha256(root / "CardDefs.base.xml"), health["card_defs_sha256"])
            self.assertEqual((root / "CardDefs.base.xml").stat().st_size, health["card_defs_bytes"])
            self.assertEqual(_sha256(root / "latest" / "manifest.json"), health["manifest_sha256"])
            self.assertEqual(1, health["standard_count"])
            self.assertEqual(1, health["arena_count"])
            self.assertEqual("", health["reason"])
            self.assertEqual(72.0, health["max_age_hours"])
            self.assertEqual(300, health["future_clock_skew_seconds"])
            self.assertTrue(health["generation_pool_registry"])

    def test_generation_query_filters_structured_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _bundle(root)
            bundle = _load(root)
            self.assertEqual(
                ["STD_CARD"],
                bundle.query_card_ids(
                    "standard",
                    cost_min=8,
                    cost_max=8,
                    card_type_ids=frozenset({5}),
                    class_mode="specific",
                    class_ids=frozenset({4}),
                    spell_school_ids=frozenset({1}),
                ),
            )
            self.assertEqual(
                [],
                bundle.query_card_ids(
                    "arena",
                    cost_min=8,
                    card_type_ids=frozenset({5}),
                ),
            )

    def test_rejects_pool_changed_after_manifest_promotion(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            with (latest / "standard.json").open("a", encoding="utf-8") as handle:
                handle.write(" ")
            with self.assertRaisesRegex(CardPoolError, "size mismatch"):
                _load(root)

    def test_rejects_duplicate_or_extra_manifest_pool_records(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pools"][1]["format"] = "standard"
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)
            with self.assertRaisesRegex(CardPoolError, "duplicate format"):
                _load(root)

            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pools"].append(dict(manifest["pools"][0]))
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)
            with self.assertRaisesRegex(CardPoolError, "exactly Standard and Arena"):
                _load(root)

    def test_rejects_manifest_pool_size_count_and_duplicate_page_drift(self) -> None:
        mutations = (
            ("size", lambda record: record.__setitem__("bytes", record["bytes"] + 1), "size mismatch"),
            (
                "count",
                lambda record: record.__setitem__("declared_count", record["declared_count"] + 1),
                "count mismatch",
            ),
            (
                "page",
                lambda record: record["pages"].append(dict(record["pages"][0])),
                "duplicate page metadata",
            ),
        )
        for label, mutate, expected in mutations:
            with self.subTest(label=label), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                latest = _bundle(root)
                manifest_path = latest / "manifest.json"
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                mutate(manifest["pools"][0])
                _write_json(manifest_path, manifest)
                _refresh_manifest_marker(latest)
                with self.assertRaisesRegex(CardPoolError, expected):
                    _load(root)

    def test_optional_load_fails_closed_without_snapshot(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = OfficialCardPoolBundle.load_optional(
                directory,
                card_defs_path=Path(directory) / "CardDefs.base.xml",
                now_utc=NOW_UTC,
            )
            self.assertFalse(bundle.available)
            self.assertIn("missing", bundle.error)
            self.assertEqual("snapshot_file_missing", bundle.reason)

    def test_optional_load_fails_closed_on_malformed_scalar(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["schema_version"] = "not-an-integer"
            _write_json(manifest_path, manifest)
            publish_path = latest / "publish-complete.json"
            publish = json.loads(publish_path.read_text(encoding="utf-8"))
            publish["manifest_sha256"] = _sha256(manifest_path)
            _write_json(publish_path, publish)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertIn("schema_version", bundle.error)

    def test_state_assessment_is_provenance_only_not_action_legality(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _bundle(root)
            bundle = _load(root)
            game = state()
            game.mode = "Ranked"
            game.friendly.hand.extend(
                [
                    Card("in", "STD_CARD", "In pool", CardType.MINION),
                    Card("generated", "GENERATED_CARD", "Generated", CardType.MINION),
                ]
            )
            assessment = bundle.assess_state(game)
            self.assertEqual("standard", assessment["format"])
            self.assertEqual(1, assessment["visible_cards_in_pool_count"])
            self.assertEqual(1, assessment["visible_cards_outside_pool_count"])
            self.assertEqual("247416", assessment["card_defs_build"])
            self.assertEqual(_sha256(root / "CardDefs.base.xml"), assessment["card_defs_sha256"])
            self.assertEqual(1, assessment["pool_count"])
            self.assertFalse(assessment["rules_coverage"])
            self.assertFalse(assessment["enforces_action_legality"])

    def test_standard_format_metadata_handles_casual_mode(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _bundle(root)
            bundle = _load(root)
            game = state()
            game.mode = "Casual"
            game.metadata["format"] = "STANDARD"
            game.friendly.hand.append(
                Card("in", "STD_CARD", "In pool", CardType.MINION)
            )
            assessment = bundle.assess_state(game)
            self.assertEqual("standard", assessment["format"])
            self.assertTrue(assessment["membership_assessed"])
            self.assertEqual(1, assessment["visible_cards_in_pool_count"])

    def test_unavailable_bundle_does_not_label_visible_cards_outside_pool(self) -> None:
        bundle = OfficialCardPoolBundle.unavailable("offline")
        game = state()
        game.mode = "Ranked"
        game.friendly.hand.append(Card("a", "STD_CARD", "Card", CardType.MINION))
        assessment = bundle.assess_state(game)
        self.assertFalse(assessment["membership_assessed"])
        self.assertIsNone(assessment["visible_cards_in_pool_count"])
        self.assertIsNone(assessment["visible_cards_outside_pool_count"])
        self.assertEqual([], assessment["visible_cards_outside_pool"])
        self.assertEqual("snapshot_unavailable", assessment["reason"])

    def test_optional_load_rejects_stale_generated_at(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["generated_at_utc"] = (
                NOW_UTC - timedelta(hours=73)
            ).isoformat()
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_stale", bundle.reason)
            self.assertNotIn(str(root), bundle.error)

    def test_optional_load_rejects_future_generated_at(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["generated_at_utc"] = (
                NOW_UTC + timedelta(minutes=6)
            ).isoformat()
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_timestamp_in_future", bundle.reason)

    def test_optional_load_rejects_future_fetched_at(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pools"][0]["fetched_at_utc"] = (
                NOW_UTC + timedelta(minutes=6)
            ).isoformat()
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_timestamp_in_future", bundle.reason)
            self.assertNotIn(str(root), bundle.error)

    def test_optional_load_rejects_stale_fetched_at(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pools"][0]["fetched_at_utc"] = (
                NOW_UTC - timedelta(hours=73)
            ).isoformat()
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_stale", bundle.reason)

    def test_optional_load_rejects_future_page_fetched_at(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pools"][0]["pages"][0]["fetched_at_utc"] = (
                NOW_UTC + timedelta(minutes=6)
            ).isoformat()
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_timestamp_in_future", bundle.reason)

    def test_optional_load_rejects_stale_page_fetched_at(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["pools"][0]["pages"][0]["fetched_at_utc"] = (
                NOW_UTC - timedelta(hours=73)
            ).isoformat()
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_stale", bundle.reason)

    def test_accepts_timestamp_within_five_minute_clock_skew(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            accepted_future = (NOW_UTC + timedelta(minutes=5)).isoformat()
            manifest["generated_at_utc"] = accepted_future
            manifest["pools"][0]["fetched_at_utc"] = accepted_future
            manifest["pools"][0]["pages"][0]["fetched_at_utc"] = accepted_future
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load(root)
            self.assertTrue(bundle.available)
            self.assertEqual(300, bundle.health()["future_clock_skew_seconds"])

    def test_schema_v1_page_without_timestamp_inherits_validated_pool_time(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            del manifest["pools"][0]["pages"][0]["fetched_at_utc"]
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load(root)
            self.assertTrue(bundle.available)
            self.assertEqual(
                FETCHED_AT_UTC.isoformat().replace("+00:00", "Z"),
                bundle.oldest_fetched_at_utc,
            )

    def test_optional_load_rejects_card_defs_build_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["card_defs"]["build"] = "247417"
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("card_defs_build_mismatch", bundle.reason)

    def test_optional_load_fails_closed_when_current_card_defs_is_missing(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _bundle(root)
            (root / "CardDefs.base.xml").unlink()

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("card_defs_missing", bundle.reason)
            self.assertNotIn(str(root), bundle.error)

    def test_optional_load_rejects_card_defs_hash_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["card_defs"]["sha256"] = "0" * 64
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("card_defs_hash_mismatch", bundle.reason)

    def test_optional_load_rejects_card_defs_size_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            latest = _bundle(root)
            manifest_path = latest / "manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["card_defs"]["bytes"] += 1
            _write_json(manifest_path, manifest)
            _refresh_manifest_marker(latest)

            bundle = _load_optional(root)
            self.assertFalse(bundle.available)
            self.assertEqual("card_defs_size_mismatch", bundle.reason)

    def test_default_card_defs_discovery_uses_hdt_appdata_path(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            appdata = root / "appdata"
            pool_root = root / "pool"
            discovered_card_defs = (
                appdata
                / "HearthstoneDeckTracker"
                / "CardDefs"
                / "CardDefs.base.xml"
            )
            _bundle(pool_root, card_defs_path=discovered_card_defs)
            with patch.dict(
                os.environ,
                {
                    "APPDATA": str(appdata),
                    "METACOMPANION_OFFICIAL_CARD_POOL_MAX_AGE_HOURS": "72",
                },
            ):
                bundle = OfficialCardPoolBundle.load(pool_root, now_utc=NOW_UTC)
            self.assertTrue(bundle.available)
            self.assertEqual(_sha256(discovered_card_defs), bundle.card_defs_sha256)

    def test_environment_can_tighten_max_age(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _bundle(root)
            with patch.dict(
                os.environ,
                {"METACOMPANION_OFFICIAL_CARD_POOL_MAX_AGE_HOURS": "12"},
            ):
                bundle = OfficialCardPoolBundle.load_optional(
                    root,
                    card_defs_path=root / "CardDefs.base.xml",
                    now_utc=NOW_UTC,
                )
            self.assertFalse(bundle.available)
            self.assertEqual("snapshot_stale", bundle.reason)
            self.assertEqual(12.0, bundle.health()["max_age_hours"])


if __name__ == "__main__":
    unittest.main()
