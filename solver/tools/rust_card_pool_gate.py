from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

SOLVER_ROOT = Path(__file__).resolve().parents[1]
if str(SOLVER_ROOT) not in sys.path:
    sys.path.insert(0, str(SOLVER_ROOT))

from metacompanion_solver.card_pool import OfficialCardPoolBundle  # noqa: E402


SCHEMA = "metacompanion-rust-official-card-pool-gate-v1"
CHECK_SCHEMA = "metacompanion-rust-official-card-pool-check-v1"


def _write_json(path: Path, value: Any) -> None:
    path.write_text(
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        + "\n",
        encoding="utf-8",
    )


def _read_json(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("fixture JSON root is not an object")
    return value


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def _timestamp(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _build_bundle(root: Path, now_utc: datetime) -> None:
    latest = root / "latest"
    latest.mkdir(parents=True)
    card_defs = root / "CardDefs.base.xml"
    card_defs.write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<CardDefs build="247416">\n'
        '  <Entity CardID="STD_CARD" ID="1" />\n'
        '  <Entity CardID="ARENA_CARD" ID="2" />\n'
        "</CardDefs>\n",
        encoding="utf-8",
    )
    run_id = "rust-card-pool-gate"
    fetched_at = _timestamp(now_utc - timedelta(minutes=30))
    records: list[dict[str, Any]] = []
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
                }
            ],
        }
        pool_path = latest / f"{format_name}.json"
        _write_json(pool_path, pool)
        records.append(
            {
                "format": format_name,
                "file": pool_path.name,
                "bytes": pool_path.stat().st_size,
                "sha256": _sha256(pool_path),
                "declared_count": 1,
                "unique_card_ids": 1,
                "unique_dbf_ids": 1,
                "fetched_at_utc": fetched_at,
                "pages": [{"page": 1, "fetched_at_utc": fetched_at}],
            }
        )
    manifest = {
        "schema_version": 1,
        "status": "complete",
        "run_id": run_id,
        "generated_at_utc": _timestamp(now_utc - timedelta(hours=1)),
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
            "bytes": card_defs.stat().st_size,
            "sha256": _sha256(card_defs),
        },
        "pools": records,
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


def _refresh_marker(root: Path) -> None:
    latest = root / "latest"
    manifest_path = latest / "manifest.json"
    marker_path = latest / "publish-complete.json"
    marker = _read_json(marker_path)
    marker["manifest_sha256"] = _sha256(manifest_path)
    _write_json(marker_path, marker)


def _refresh_pool(root: Path, format_name: str) -> None:
    latest = root / "latest"
    pool_path = latest / f"{format_name}.json"
    pool = _read_json(pool_path)
    manifest = _read_json(latest / "manifest.json")
    record = next(
        item for item in manifest["pools"] if item.get("format") == format_name
    )
    record["bytes"] = pool_path.stat().st_size
    record["sha256"] = _sha256(pool_path)
    record["declared_count"] = pool["declared_count"]
    _write_json(latest / "manifest.json", manifest)
    _refresh_marker(root)


def _run_rust(binary: Path, root: Path) -> tuple[int, dict[str, Any]]:
    completed = subprocess.run(
        [
            str(binary),
            "official-card-pool-check",
            "--root",
            str(root),
            "--card-defs",
            str(root / "CardDefs.base.xml"),
        ],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
    )
    if completed.returncode not in {0, 3}:
        raise RuntimeError(
            f"Rust card-pool checker exited with {completed.returncode}: "
            f"{completed.stderr.strip()}"
        )
    lines = [line for line in completed.stdout.splitlines() if line.strip()]
    if len(lines) != 1:
        raise RuntimeError("Rust card-pool checker did not return exactly one JSON line")
    payload = json.loads(lines[0])
    if not isinstance(payload, dict) or payload.get("schema") != CHECK_SCHEMA:
        raise RuntimeError("Rust card-pool checker returned an unexpected contract")
    return completed.returncode, payload


def _python_health(root: Path, now_utc: datetime) -> dict[str, Any]:
    return OfficialCardPoolBundle.load_optional(
        root,
        card_defs_path=root / "CardDefs.base.xml",
        max_age=timedelta(hours=72),
        now_utc=now_utc,
    ).health()


def _assert_rejected(
    binary: Path,
    root: Path,
    now_utc: datetime,
    expected_reason: str,
) -> None:
    exit_code, payload = _run_rust(binary, root)
    rust_health = payload["official_card_pools"]
    python_health = _python_health(root, now_utc)
    if exit_code != 3 or payload.get("status") != "rejected":
        raise AssertionError("Rust checker accepted a tampered card-pool bundle")
    if rust_health.get("available") is not False:
        raise AssertionError("Rust checker claimed a rejected bundle was available")
    if rust_health.get("reason") != expected_reason:
        raise AssertionError(
            f"Rust rejection reason mismatch: {rust_health.get('reason')!r}"
        )
    if python_health.get("available") is not False:
        raise AssertionError("Python checker accepted a tampered card-pool bundle")
    if python_health.get("reason") != expected_reason:
        raise AssertionError(
            f"Python rejection reason mismatch: {python_health.get('reason')!r}"
        )
    serialized = json.dumps(payload, ensure_ascii=False)
    if str(root) in serialized:
        raise AssertionError("Rust rejection payload exposed a local fixture path")


def run_gate(binary: Path) -> dict[str, Any]:
    binary = binary.resolve(strict=True)
    now_utc = datetime.now(timezone.utc)
    checks: dict[str, bool] = {}
    with tempfile.TemporaryDirectory(prefix="MetaCompanion-RustCardPoolGate-") as temp:
        base = Path(temp)

        valid = base / "valid"
        _build_bundle(valid, now_utc)
        exit_code, rust = _run_rust(binary, valid)
        python = _python_health(valid, now_utc)
        comparable_fields = (
            "available",
            "run_id",
            "card_defs_build",
            "card_defs_sha256",
            "card_defs_bytes",
            "manifest_sha256",
            "standard_count",
            "arena_count",
            "rules_coverage",
            "source",
            "reason",
        )
        rust_health = rust["official_card_pools"]
        if exit_code != 0 or rust.get("status") != "pass":
            raise AssertionError("Rust checker rejected the valid fixture")
        for field in comparable_fields:
            if rust_health.get(field) != python.get(field):
                raise AssertionError(f"Rust/Python health mismatch for {field}")
        if rust.get("rules_coverage") is not False:
            raise AssertionError("Rust checker claimed rules coverage")
        if rust.get("enforces_action_legality") is not False:
            raise AssertionError("Rust checker claimed action-legality authority")
        checks["valid_python_rust_interop"] = True

        manifest_binding = base / "manifest-binding"
        _build_bundle(manifest_binding, now_utc)
        manifest = _read_json(manifest_binding / "latest" / "manifest.json")
        manifest["status"] = "changed-after-publish"
        _write_json(manifest_binding / "latest" / "manifest.json", manifest)
        _assert_rejected(binary, manifest_binding, now_utc, "snapshot_invalid")
        checks["publish_manifest_binding"] = True

        stale_page = base / "stale-page"
        _build_bundle(stale_page, now_utc)
        manifest = _read_json(stale_page / "latest" / "manifest.json")
        manifest["pools"][0]["pages"][0]["fetched_at_utc"] = _timestamp(
            now_utc - timedelta(hours=73)
        )
        _write_json(stale_page / "latest" / "manifest.json", manifest)
        _refresh_marker(stale_page)
        _assert_rejected(binary, stale_page, now_utc, "snapshot_stale")
        checks["page_freshness"] = True

        card_defs = base / "card-defs"
        _build_bundle(card_defs, now_utc)
        manifest = _read_json(card_defs / "latest" / "manifest.json")
        manifest["card_defs"]["sha256"] = "0" * 64
        _write_json(card_defs / "latest" / "manifest.json", manifest)
        _refresh_marker(card_defs)
        _assert_rejected(binary, card_defs, now_utc, "card_defs_hash_mismatch")
        checks["card_defs_binding"] = True

        duplicate = base / "duplicate"
        _build_bundle(duplicate, now_utc)
        pool_path = duplicate / "latest" / "standard.json"
        pool = _read_json(pool_path)
        pool["cards"].append(dict(pool["cards"][0]))
        pool["declared_count"] = 2
        _write_json(pool_path, pool)
        _refresh_pool(duplicate, "standard")
        _assert_rejected(binary, duplicate, now_utc, "snapshot_invalid")
        checks["duplicate_identity"] = True

    return {
        "schema": SCHEMA,
        "passed": all(checks.values()) and len(checks) == 5,
        "binary_sha256": _sha256(binary),
        "check_count": len(checks),
        "checks": checks,
        "standard_count": 1,
        "arena_count": 1,
        "rules_coverage": False,
        "enforces_action_legality": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = run_gate(args.binary)
    except Exception as error:  # release gate must retain a machine-readable failure
        report = {
            "schema": SCHEMA,
            "passed": False,
            "error": type(error).__name__,
            "message": str(error),
        }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    _write_json(args.output, report)
    print(json.dumps(report, ensure_ascii=False, sort_keys=True, separators=(",", ":")))
    return 0 if report.get("passed") is True else 1


if __name__ == "__main__":
    raise SystemExit(main())
