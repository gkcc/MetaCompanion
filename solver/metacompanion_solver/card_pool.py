from __future__ import annotations

import hashlib
import json
import math
import os
import re
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Mapping

from .schemas import GameState


MAX_MANIFEST_BYTES = 2 * 1024 * 1024
MAX_POOL_BYTES = 20 * 1024 * 1024
MAX_CARD_DEFS_BYTES = 128 * 1024 * 1024
DEFAULT_MAX_AGE_HOURS = 72.0
MAX_CONFIGURED_AGE_HOURS = 24.0 * 30.0
FUTURE_CLOCK_SKEW = timedelta(minutes=5)
MAX_AGE_ENVIRONMENT_VARIABLE = "METACOMPANION_OFFICIAL_CARD_POOL_MAX_AGE_HOURS"
_SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
_CARD_DEFS_BUILD_PATTERN = re.compile(
    rb"<CardDefs(?=[\s>])[^>]*\bbuild\s*=\s*['\"]([^'\"]+)['\"]",
    re.IGNORECASE,
)


class CardPoolError(ValueError):
    def __init__(self, message: str, *, reason: str = "snapshot_invalid") -> None:
        super().__init__(message)
        self.reason = reason


@dataclass(frozen=True)
class PoolCardRecord:
    card_id: str
    dbf_id: int
    name: str
    card_type_id: int | None
    mana_cost: int | None
    card_set_id: int | None
    class_ids: frozenset[int]
    spell_school_id: int | None
    minion_type_ids: frozenset[int]
    rarity_id: int | None
    keyword_ids: frozenset[int]
    attack: int | None
    health: int | None
    durability: int | None
    text: str


def _optional_integer(value: Any, field_name: str) -> int | None:
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise CardPoolError(f"official card-pool {field_name} must be a non-negative integer or null")
    return value


def _integer_set(value: Any, field_name: str) -> frozenset[int]:
    if value is None:
        return frozenset()
    if not isinstance(value, list):
        raise CardPoolError(f"official card-pool {field_name} must be an array or null")
    result: set[int] = set()
    for item in value:
        parsed = _optional_integer(item, field_name)
        if parsed is None:
            raise CardPoolError(f"official card-pool {field_name} contains null")
        result.add(parsed)
    return frozenset(result)


def _read_json_object(path: Path, maximum_bytes: int) -> Mapping[str, Any]:
    try:
        size = path.stat().st_size
    except OSError as exc:
        raise CardPoolError(
            f"missing official card-pool file: {path.name}",
            reason="snapshot_file_missing",
        ) from exc
    if size <= 0 or size > maximum_bytes:
        raise CardPoolError(
            f"official card-pool file has invalid size: {path.name}",
            reason="snapshot_file_size_invalid",
        )
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise CardPoolError(
            f"invalid official card-pool JSON: {path.name}",
            reason="snapshot_json_invalid",
        ) from exc
    if not isinstance(raw, Mapping):
        raise CardPoolError(f"official card-pool root must be an object: {path.name}")
    return raw


def _sha256(
    path: Path,
    *,
    reason: str = "snapshot_file_unreadable",
    description: str = "official card-pool file",
) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(chunk)
    except OSError as exc:
        raise CardPoolError(
            f"could not hash {description}",
            reason=reason,
        ) from exc
    return digest.hexdigest()


def _normalized_hash(value: Any) -> str:
    return str(value or "").strip().lower()


def _required_integer(value: Any, field_name: str, *, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise CardPoolError(f"official card-pool {field_name} must be an integer >= {minimum}")
    return value


def _resolve_now(now_utc: datetime | None) -> datetime:
    value = now_utc if now_utc is not None else datetime.now(timezone.utc)
    if not isinstance(value, datetime) or value.tzinfo is None:
        raise CardPoolError(
            "official card-pool current time must include a timezone",
            reason="configuration_invalid",
        )
    return value.astimezone(timezone.utc)


def _resolve_max_age(max_age: timedelta | None) -> timedelta:
    if max_age is not None:
        if not isinstance(max_age, timedelta):
            raise CardPoolError(
                "official card-pool max age must be a duration",
                reason="configuration_invalid",
            )
        hours = max_age.total_seconds() / 3600.0
    else:
        raw = os.environ.get(MAX_AGE_ENVIRONMENT_VARIABLE, "").strip()
        if not raw:
            hours = DEFAULT_MAX_AGE_HOURS
        else:
            try:
                hours = float(raw)
            except ValueError as exc:
                raise CardPoolError(
                    "official card-pool max-age configuration is invalid",
                    reason="configuration_invalid",
                ) from exc
    if not math.isfinite(hours) or hours <= 0 or hours > MAX_CONFIGURED_AGE_HOURS:
        raise CardPoolError(
            "official card-pool max-age configuration is outside the allowed range",
            reason="configuration_invalid",
        )
    return timedelta(hours=hours)


def _parse_snapshot_timestamp(value: Any, field_name: str) -> datetime:
    if not isinstance(value, str) or not value.strip():
        raise CardPoolError(
            f"official card-pool {field_name} is missing or invalid",
            reason="snapshot_timestamp_invalid",
        )
    text = value.strip()
    if text.endswith(("Z", "z")):
        text = text[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError as exc:
        raise CardPoolError(
            f"official card-pool {field_name} is missing or invalid",
            reason="snapshot_timestamp_invalid",
        ) from exc
    if parsed.tzinfo is None:
        raise CardPoolError(
            f"official card-pool {field_name} must include a timezone",
            reason="snapshot_timestamp_invalid",
        )
    return parsed.astimezone(timezone.utc)


def _validate_snapshot_timestamp(
    container: Mapping[str, Any],
    field_name: str,
    *,
    now_utc: datetime,
    max_age: timedelta,
) -> datetime:
    value = container.get(f"{field_name}_utc")
    if value is None:
        value = container.get(field_name)
    parsed = _parse_snapshot_timestamp(value, field_name)
    if parsed - now_utc > FUTURE_CLOCK_SKEW:
        raise CardPoolError(
            f"official card-pool {field_name} exceeds the future clock-skew allowance",
            reason="snapshot_timestamp_in_future",
        )
    if now_utc - parsed > max_age:
        raise CardPoolError(
            f"official card-pool {field_name} is stale",
            reason="snapshot_stale",
        )
    return parsed


def _format_utc(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _validate_card_defs(
    manifest: Mapping[str, Any],
    card_defs_path: str | Path | None,
) -> tuple[str, int, str]:
    card_defs = manifest.get("card_defs")
    if not isinstance(card_defs, Mapping):
        raise CardPoolError(
            "official card-pool CardDefs metadata is missing",
            reason="card_defs_metadata_invalid",
        )
    if str(card_defs.get("file_name") or "") != "CardDefs.base.xml":
        raise CardPoolError(
            "official card-pool CardDefs file name is invalid",
            reason="card_defs_metadata_invalid",
        )
    build = str(card_defs.get("build") or "").strip()
    if not build or len(build) > 128:
        raise CardPoolError(
            "official card-pool CardDefs build is invalid",
            reason="card_defs_metadata_invalid",
        )
    declared_bytes = _required_integer(
        card_defs.get("bytes"), "CardDefs bytes", minimum=1
    )
    if declared_bytes > MAX_CARD_DEFS_BYTES:
        raise CardPoolError(
            "official card-pool CardDefs size is outside the allowed range",
            reason="card_defs_metadata_invalid",
        )
    declared_hash = _normalized_hash(card_defs.get("sha256"))
    if not _SHA256_PATTERN.fullmatch(declared_hash):
        raise CardPoolError(
            "official card-pool CardDefs hash is invalid",
            reason="card_defs_metadata_invalid",
        )

    selected_path = (
        Path(card_defs_path)
        if card_defs_path is not None
        else default_hdt_card_defs_path()
    )
    if selected_path is None or selected_path.name.lower() != "carddefs.base.xml":
        raise CardPoolError(
            "current HDT CardDefs is not available",
            reason="card_defs_missing",
        )
    try:
        actual_bytes = selected_path.stat().st_size
    except OSError as exc:
        raise CardPoolError(
            "current HDT CardDefs is not available",
            reason="card_defs_missing",
        ) from exc
    if actual_bytes != declared_bytes:
        raise CardPoolError(
            "current HDT CardDefs size does not match the official snapshot",
            reason="card_defs_size_mismatch",
        )
    actual_hash = _sha256(
        selected_path,
        reason="card_defs_unreadable",
        description="current HDT CardDefs",
    )
    if actual_hash != declared_hash:
        raise CardPoolError(
            "current HDT CardDefs hash does not match the official snapshot",
            reason="card_defs_hash_mismatch",
        )
    try:
        with selected_path.open("rb") as handle:
            prefix = handle.read(min(actual_bytes, 64 * 1024))
    except OSError as exc:
        raise CardPoolError(
            "current HDT CardDefs could not be inspected",
            reason="card_defs_unreadable",
        ) from exc
    match = _CARD_DEFS_BUILD_PATTERN.search(prefix)
    if match is None:
        raise CardPoolError(
            "current HDT CardDefs build could not be verified",
            reason="card_defs_build_invalid",
        )
    try:
        actual_build = match.group(1).decode("ascii").strip()
    except UnicodeDecodeError as exc:
        raise CardPoolError(
            "current HDT CardDefs build could not be verified",
            reason="card_defs_build_invalid",
        ) from exc
    if actual_build != build:
        raise CardPoolError(
            "current HDT CardDefs build does not match the official snapshot",
            reason="card_defs_build_mismatch",
        )
    return build, actual_bytes, actual_hash.upper()


@dataclass(frozen=True)
class OfficialCardPoolBundle:
    available: bool = False
    run_id: str = ""
    card_defs_build: str = ""
    card_defs_sha256: str = ""
    card_defs_bytes: int = 0
    manifest_sha256: str = ""
    generated_at_utc: str = ""
    oldest_fetched_at_utc: str = ""
    max_age_hours: float = DEFAULT_MAX_AGE_HOURS
    future_clock_skew_seconds: int = int(FUTURE_CLOCK_SKEW.total_seconds())
    cards_by_format: dict[str, frozenset[str]] = field(default_factory=dict)
    dbf_ids_by_format: dict[str, frozenset[int]] = field(default_factory=dict)
    records_by_format: dict[str, Mapping[str, PoolCardRecord]] = field(default_factory=dict)
    source: str = "Blizzard Hearthstone Card Library"
    reason: str = "snapshot_unavailable"
    error: str = ""

    @classmethod
    def unavailable(
        cls,
        error: str = "official card-pool snapshot is not installed",
        *,
        reason: str = "snapshot_unavailable",
        max_age_hours: float = DEFAULT_MAX_AGE_HOURS,
    ) -> "OfficialCardPoolBundle":
        return cls(reason=reason, error=error, max_age_hours=max_age_hours)

    @classmethod
    def load(
        cls,
        root: str | Path,
        *,
        card_defs_path: str | Path | None = None,
        max_age: timedelta | None = None,
        now_utc: datetime | None = None,
    ) -> "OfficialCardPoolBundle":
        validated_now = _resolve_now(now_utc)
        validated_max_age = _resolve_max_age(max_age)
        latest = Path(root) / "latest"
        publish_path = latest / "publish-complete.json"
        manifest_path = latest / "manifest.json"
        publish = _read_json_object(publish_path, MAX_MANIFEST_BYTES)
        manifest = _read_json_object(manifest_path, MAX_MANIFEST_BYTES)
        if (
            _required_integer(publish.get("schema_version"), "publish schema_version") != 1
            or _required_integer(manifest.get("schema_version"), "manifest schema_version") != 1
        ):
            raise CardPoolError("unsupported official card-pool schema version")
        run_id = str(manifest.get("run_id") or "")
        if not run_id or str(publish.get("run_id") or "") != run_id:
            raise CardPoolError("official card-pool run_id mismatch")
        manifest_sha256 = _sha256(manifest_path)
        if _normalized_hash(publish.get("manifest_sha256")) != manifest_sha256:
            raise CardPoolError("official card-pool manifest hash mismatch")
        if manifest.get("status") != "complete":
            raise CardPoolError("official card-pool manifest is not complete")
        generated_at = _validate_snapshot_timestamp(
            manifest,
            "generated_at",
            now_utc=validated_now,
            max_age=validated_max_age,
        )
        source = manifest.get("source")
        if not isinstance(source, Mapping) or source.get("provider") != "Blizzard":
            raise CardPoolError("official card-pool source provenance is invalid")
        if source.get("authentication") != "none" or source.get("browser_required") is not False:
            raise CardPoolError("official card-pool source must not require browser credentials")
        coverage = manifest.get("coverage")
        if not isinstance(coverage, Mapping) or coverage.get("rules_coverage") is not False:
            raise CardPoolError("official card-pool snapshot must not claim rules coverage")
        pool_records = manifest.get("pools")
        if not isinstance(pool_records, list):
            raise CardPoolError("official card-pool manifest pools must be an array")

        if len(pool_records) != 2:
            raise CardPoolError(
                "official card-pool bundle must contain exactly Standard and Arena"
            )
        records_by_format: dict[str, Mapping[str, Any]] = {}
        for item in pool_records:
            if not isinstance(item, Mapping):
                raise CardPoolError("official card-pool manifest contains an invalid pool record")
            format_name = str(item.get("format") or "").strip().lower()
            if format_name not in {"standard", "arena"}:
                raise CardPoolError("official card-pool manifest contains an unknown format")
            if format_name in records_by_format:
                raise CardPoolError("official card-pool manifest contains a duplicate format")
            records_by_format[format_name] = item
        if set(records_by_format) != {"standard", "arena"}:
            raise CardPoolError("official card-pool bundle must contain Standard and Arena")

        fetched_at_values: list[datetime] = []
        for format_name in ("standard", "arena"):
            record = records_by_format[format_name]
            record_fetched_at = _validate_snapshot_timestamp(
                record,
                "fetched_at",
                now_utc=validated_now,
                max_age=validated_max_age,
            )
            fetched_at_values.append(record_fetched_at)
            pages = record.get("pages")
            if not isinstance(pages, list) or not pages:
                raise CardPoolError(
                    f"official {format_name} pool pages must be a non-empty array"
                )
            page_numbers: set[int] = set()
            for page in pages:
                if not isinstance(page, Mapping):
                    raise CardPoolError(
                        f"official {format_name} pool contains invalid page metadata"
                    )
                page_number = _required_integer(
                    page.get("page"), f"{format_name} page number", minimum=1
                )
                if page_number in page_numbers:
                    raise CardPoolError(
                        f"official {format_name} pool contains duplicate page metadata"
                    )
                page_numbers.add(page_number)
                if page.get("fetched_at_utc") is not None or page.get("fetched_at") is not None:
                    fetched_at_values.append(
                        _validate_snapshot_timestamp(
                            page,
                            "fetched_at",
                            now_utc=validated_now,
                            max_age=validated_max_age,
                        )
                    )
                else:
                    # Schema-v1 snapshots published before page timestamps were added
                    # inherit the already validated enclosing pool fetch time.
                    fetched_at_values.append(record_fetched_at)

        card_defs_build, card_defs_bytes, card_defs_sha256 = _validate_card_defs(
            manifest,
            card_defs_path,
        )
        cards_by_format: dict[str, frozenset[str]] = {}
        dbf_ids_by_format: dict[str, frozenset[int]] = {}
        card_records_by_format: dict[str, Mapping[str, PoolCardRecord]] = {}
        for format_name in ("standard", "arena"):
            record = records_by_format[format_name]
            file_name = str(record.get("file") or "")
            if file_name != f"{format_name}.json":
                raise CardPoolError(f"unexpected official {format_name} file name")
            pool_path = latest / file_name
            declared_pool_bytes = _required_integer(
                record.get("bytes"), f"{format_name} bytes", minimum=1
            )
            if declared_pool_bytes > MAX_POOL_BYTES:
                raise CardPoolError(f"official {format_name} pool size is outside the allowed range")
            try:
                actual_pool_bytes = pool_path.stat().st_size
            except OSError as exc:
                raise CardPoolError(f"official {format_name} pool file is unavailable") from exc
            if actual_pool_bytes != declared_pool_bytes:
                raise CardPoolError(f"official {format_name} pool size mismatch")
            if _normalized_hash(record.get("sha256")) != _sha256(pool_path):
                raise CardPoolError(f"official {format_name} pool hash mismatch")
            pool = _read_json_object(pool_path, MAX_POOL_BYTES)
            if (
                _required_integer(pool.get("schema_version"), f"{format_name} schema_version") != 1
                or str(pool.get("format") or "").lower() != format_name
                or str(pool.get("run_id") or "") != run_id
            ):
                raise CardPoolError(f"official {format_name} pool contract mismatch")
            pool_coverage = pool.get("coverage")
            if not isinstance(pool_coverage, Mapping) or pool_coverage.get("rules_coverage") is not False:
                raise CardPoolError(f"official {format_name} pool must not claim rules coverage")
            cards = pool.get("cards")
            if not isinstance(cards, list):
                raise CardPoolError(f"official {format_name} cards must be an array")
            card_ids: set[str] = set()
            dbf_ids: set[int] = set()
            card_records: dict[str, PoolCardRecord] = {}
            for card in cards:
                if not isinstance(card, Mapping) or card.get("collectible") is not True:
                    raise CardPoolError(f"official {format_name} pool contains a non-collectible row")
                card_id = str(card.get("card_id") or "")
                dbf_id = card.get("dbf_id")
                if not card_id or isinstance(dbf_id, bool) or not isinstance(dbf_id, int) or dbf_id <= 0:
                    raise CardPoolError(f"official {format_name} pool contains an invalid card identity")
                if card_id in card_ids or dbf_id in dbf_ids:
                    raise CardPoolError(f"official {format_name} pool contains duplicate card identities")
                card_ids.add(card_id)
                dbf_ids.add(dbf_id)
                class_ids = set(_integer_set(card.get("multi_class_ids"), "multi_class_ids"))
                class_id = _optional_integer(card.get("class_id"), "class_id")
                if class_id is not None:
                    class_ids.add(class_id)
                minion_type_ids = set(_integer_set(card.get("multi_type_ids"), "multi_type_ids"))
                minion_type_id = _optional_integer(card.get("minion_type_id"), "minion_type_id")
                if minion_type_id is not None:
                    minion_type_ids.add(minion_type_id)
                card_records[card_id] = PoolCardRecord(
                    card_id=card_id,
                    dbf_id=dbf_id,
                    name=str(card.get("name") or card_id),
                    card_type_id=_optional_integer(card.get("card_type_id"), "card_type_id"),
                    mana_cost=_optional_integer(card.get("mana_cost"), "mana_cost"),
                    card_set_id=_optional_integer(card.get("card_set_id"), "card_set_id"),
                    class_ids=frozenset(class_ids),
                    spell_school_id=_optional_integer(
                        card.get("spell_school_id"), "spell_school_id"
                    ),
                    minion_type_ids=frozenset(minion_type_ids),
                    rarity_id=_optional_integer(card.get("rarity_id"), "rarity_id"),
                    keyword_ids=_integer_set(card.get("keyword_ids"), "keyword_ids"),
                    attack=_optional_integer(card.get("attack"), "attack"),
                    health=_optional_integer(card.get("health"), "health"),
                    durability=_optional_integer(card.get("durability"), "durability"),
                    text=str(card.get("text") or ""),
                )
            declared_count = pool.get("declared_count")
            if (
                _required_integer(declared_count, f"{format_name} declared_count") != len(cards)
                or _required_integer(
                    record.get("declared_count"), f"{format_name} manifest declared_count"
                )
                != len(cards)
                or _required_integer(
                    record.get("unique_card_ids"), f"{format_name} unique_card_ids"
                )
                != len(card_ids)
                or _required_integer(
                    record.get("unique_dbf_ids"), f"{format_name} unique_dbf_ids"
                )
                != len(dbf_ids)
            ):
                raise CardPoolError(f"official {format_name} pool count mismatch")
            cards_by_format[format_name] = frozenset(card_ids)
            dbf_ids_by_format[format_name] = frozenset(dbf_ids)
            card_records_by_format[format_name] = card_records

        return cls(
            available=True,
            run_id=run_id,
            card_defs_build=card_defs_build,
            card_defs_sha256=card_defs_sha256,
            card_defs_bytes=card_defs_bytes,
            manifest_sha256=manifest_sha256.upper(),
            generated_at_utc=_format_utc(generated_at),
            oldest_fetched_at_utc=_format_utc(min(fetched_at_values)),
            max_age_hours=validated_max_age.total_seconds() / 3600.0,
            cards_by_format=cards_by_format,
            dbf_ids_by_format=dbf_ids_by_format,
            records_by_format=card_records_by_format,
            reason="",
        )

    @classmethod
    def load_optional(
        cls,
        root: str | Path,
        *,
        card_defs_path: str | Path | None = None,
        max_age: timedelta | None = None,
        now_utc: datetime | None = None,
    ) -> "OfficialCardPoolBundle":
        validated_max_age: timedelta | None = None
        try:
            validated_max_age = _resolve_max_age(max_age)
            return cls.load(
                root,
                card_defs_path=card_defs_path,
                max_age=validated_max_age,
                now_utc=now_utc,
            )
        except CardPoolError as exc:
            return cls.unavailable(
                str(exc),
                reason=exc.reason,
                max_age_hours=(
                    validated_max_age.total_seconds() / 3600.0
                    if validated_max_age is not None
                    else DEFAULT_MAX_AGE_HOURS
                ),
            )
        except (TypeError, ValueError, OverflowError):
            return cls.unavailable(
                "official card-pool snapshot is invalid",
                reason="snapshot_invalid",
                max_age_hours=(
                    validated_max_age.total_seconds() / 3600.0
                    if validated_max_age is not None
                    else DEFAULT_MAX_AGE_HOURS
                ),
            )

    @staticmethod
    def _state_format(state: GameState) -> str:
        values = [state.mode, state.metadata.get("format", ""), state.metadata.get("game_mode", "")]
        normalized = " ".join(str(value or "").lower() for value in values)
        if "arena" in normalized:
            return "arena"
        if "standard" in normalized or "ranked" in normalized:
            return "standard"
        return ""

    def health(self) -> dict[str, Any]:
        return {
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
            "standard_count": len(self.cards_by_format.get("standard", ())),
            "arena_count": len(self.cards_by_format.get("arena", ())),
            "generation_pool_registry": self.available,
            "rules_coverage": False,
            "source": self.source,
            "reason": self.reason,
            "error": self.error,
        }

    def query_card_ids(
        self,
        format_name: str,
        *,
        cost_min: int | None = None,
        cost_max: int | None = None,
        card_type_ids: frozenset[int] = frozenset(),
        class_mode: str = "any",
        class_ids: frozenset[int] = frozenset(),
        controller_class_id: int | None = None,
        spell_school_ids: frozenset[int] = frozenset(),
        minion_type_ids: frozenset[int] = frozenset(),
        card_set_ids: frozenset[int] = frozenset(),
        rarity_ids: frozenset[int] = frozenset(),
        keyword_ids: frozenset[int] = frozenset(),
        exclude_card_ids: frozenset[str] = frozenset(),
    ) -> list[str]:
        """Resolve one explicit current-format card-pool constraint.

        Zone/history sources intentionally live outside this API; callers must
        not substitute this registry for the player's deck, hand, graveyard, or
        historical card sets.
        """

        records = self.records_by_format.get(format_name.lower())
        if not self.available or records is None:
            return []
        if class_mode not in {
            "any",
            "controller",
            "controller_or_neutral",
            "another_class",
            "specific",
        }:
            raise ValueError("unsupported class_mode")
        if class_mode in {"controller", "controller_or_neutral", "another_class"} and (
            controller_class_id is None
        ):
            return []

        def class_matches(record: PoolCardRecord) -> bool:
            if class_mode == "any":
                return True
            if class_mode == "controller":
                return controller_class_id in record.class_ids
            if class_mode == "controller_or_neutral":
                return controller_class_id in record.class_ids or 12 in record.class_ids
            if class_mode == "another_class":
                return bool(record.class_ids) and 12 not in record.class_ids and (
                    controller_class_id not in record.class_ids
                )
            return bool(record.class_ids.intersection(class_ids))

        excluded = {value.upper() for value in exclude_card_ids}
        result = [
            record.card_id
            for record in records.values()
            if (cost_min is None or (record.mana_cost is not None and record.mana_cost >= cost_min))
            and (cost_max is None or (record.mana_cost is not None and record.mana_cost <= cost_max))
            and (not card_type_ids or record.card_type_id in card_type_ids)
            and class_matches(record)
            and (not spell_school_ids or record.spell_school_id in spell_school_ids)
            and (
                not minion_type_ids
                or bool(record.minion_type_ids.intersection(minion_type_ids))
            )
            and (not card_set_ids or record.card_set_id in card_set_ids)
            and (not rarity_ids or record.rarity_id in rarity_ids)
            and (not keyword_ids or keyword_ids.issubset(record.keyword_ids))
            and record.card_id.upper() not in excluded
        ]
        return sorted(result)

    def assess_state(self, state: GameState) -> dict[str, Any]:
        format_name = self._state_format(state)
        card_pool = self.cards_by_format.get(format_name, frozenset())
        visible_cards = [
            *state.friendly.hand,
            *state.friendly.board,
            *state.opponent.board,
        ]
        known_collectible_ids = sorted(
            {
                card.card_id
                for card in visible_cards
                if card.card_id and not card.card_id.startswith("UNKNOWN")
            }
        )
        membership_assessed = self.available and bool(format_name)
        in_pool = (
            [card_id for card_id in known_collectible_ids if card_id in card_pool]
            if membership_assessed
            else []
        )
        outside_pool = (
            [card_id for card_id in known_collectible_ids if card_id not in card_pool]
            if membership_assessed
            else []
        )
        return {
            "available": self.available,
            "format": format_name or "unknown",
            "run_id": self.run_id,
            "card_defs_build": self.card_defs_build,
            "card_defs_sha256": self.card_defs_sha256,
            "card_defs_bytes": self.card_defs_bytes,
            "manifest_sha256": self.manifest_sha256,
            "generated_at_utc": self.generated_at_utc,
            "oldest_fetched_at_utc": self.oldest_fetched_at_utc,
            "max_age_hours": self.max_age_hours,
            "future_clock_skew_seconds": self.future_clock_skew_seconds,
            "pool_count": len(card_pool),
            "visible_known_card_count": len(known_collectible_ids),
            "membership_assessed": membership_assessed,
            "visible_cards_in_pool_count": len(in_pool) if membership_assessed else None,
            "visible_cards_outside_pool_count": len(outside_pool) if membership_assessed else None,
            "visible_cards_outside_pool": outside_pool[:20],
            "rules_coverage": False,
            "generated_entities_coverage": False,
            "enforces_action_legality": False,
            "source": self.source,
            "reason": self.reason,
            "error": self.error,
        }


def default_hdt_card_defs_path() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return (
        Path(appdata)
        / "HearthstoneDeckTracker"
        / "CardDefs"
        / "CardDefs.base.xml"
    )


def default_official_card_pool_directory() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return (
        Path(appdata)
        / "HearthstoneDeckTracker"
        / "MetaCompanion"
        / "AdvisorData"
        / "OfficialCardPools"
    )
