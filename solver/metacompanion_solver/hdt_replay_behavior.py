from __future__ import annotations

import copy
import hashlib
import json
import os
import re
import tempfile
import zipfile
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

from .behavior import BehaviorRecord, BehaviorValidationError, create_behavior_record
from .decision_frame import (
    DecisionFrameRecord,
    DecisionFrameValidationError,
    create_decision_frame_record,
)
from .logging_store import JsonlTrainingLogger, TRAJECTORY_SCHEMA_ID
from .errors import SchemaError
from .hdt_card_defs import HdtCardDefsError, load_hdt_card_defs, public_card_ids
from .schemas import GameState, Observation


REPLAY_IMPORT_SCHEMA_ID = "hdt-replay-behavior-import-v1"
REPLAY_TRANSCRIPT_SCHEMA_ID = "hdt-replay-public-transcript-v1"
REPLAY_CAPTURE_CONTRACT = "hdt_replay_public_power_v1"
BEHAVIOR_OUTPUT_FILENAME = "behavior-v1.jsonl"
RESULT_OUTPUT_FILENAME = "training-v2-results.jsonl"
DECISION_FRAME_OUTPUT_FILENAME = "advisor-decision-frame-v1.jsonl"
MANIFEST_OUTPUT_FILENAME = "hdt-replay-import-v1.json"

MAX_REPLAY_FILES = 10_000
MAX_ARCHIVE_BYTES = 16 * 1024 * 1024
MAX_LOG_BYTES = 128 * 1024 * 1024
MAX_TOTAL_ARCHIVE_BYTES = 2 * 1024 * 1024 * 1024

_SUPPORTED_PUBLIC_CARD_TYPES = {
    "HERO",
    "MINION",
    "SPELL",
    "WEAPON",
    "HERO_POWER",
    "LOCATION",
}
_ARENA_GAME_TYPES = {"GT_ARENA", "GT_UNDERGROUND_ARENA"}
_UNKNOWN_PLAYER_NAME = "UNKNOWN HUMAN PLAYER"
_SAFE_BUILD = re.compile(r"^[0-9]+$")
_POWER_PREFIX = "DebugPrintPower() - "

_DEBUG_GAME_FIELD = re.compile(
    r"DebugPrintGame\(\) - (?P<key>BuildNumber|GameType|FormatType|ScenarioID)="
    r"(?P<value>\S+)"
)
_DEBUG_GAME_PLAYER = re.compile(
    r"DebugPrintGame\(\) - PlayerID=(?P<player>\d+), PlayerName=(?P<name>[^\r\n]*)"
)
_PLAYER_ENTITY = re.compile(
    r"Player EntityID=(?P<entity>\d+) PlayerID=(?P<player>\d+) GameAccountId="
)
_GAME_ENTITY = re.compile(r"GameEntity EntityID=(?P<entity>\d+)")
_FULL_ENTITY_CREATE = re.compile(
    r"FULL_ENTITY - Creating ID=(?P<entity>\d+) CardID=(?P<card>\S*)"
)
_ENTITY_UPDATE = re.compile(
    r"(?:FULL_ENTITY|SHOW_ENTITY|CHANGE_ENTITY) - Updating (?:Entity=)?(?P<entity>.+?) "
    r"CardID=(?P<card>\S*)$"
)
_HIDE_ENTITY = re.compile(r"HIDE_ENTITY - .*?\bid=(?P<entity>\d+)\b")
_TAG_CHANGE = re.compile(
    r"TAG_CHANGE Entity=(?P<entity>.+?) tag=(?P<tag>[A-Za-z0-9_]+) "
    r"value=(?P<value>\S+)"
)
_IMPLICIT_TAG = re.compile(r"^\s+tag=(?P<tag>[A-Za-z0-9_]+) value=(?P<value>\S+)")
_BLOCK_START = re.compile(
    r"BLOCK_START BlockType=(?P<type>[A-Za-z0-9_]+) Entity=(?P<entity>.+?) "
    r"EffectCardId=.*? EffectIndex=\S+ Target=(?P<target>.+?) "
    r"SubOption=(?P<sub_option>\S+)"
)
_OPTIONS_HEADER = re.compile(r"DebugPrintOptions\(\) - id=(?P<id>\d+)")
_OPTION_LINE = re.compile(
    r"DebugPrintOptions\(\) -\s+option (?P<option>\d+) type=(?P<type>\S+) "
    r"mainEntity=(?P<entity>.*?) error=(?P<error>\S+)"
)
_TARGET_LINE = re.compile(
    r"DebugPrintOptions\(\) -\s+target (?P<target>\d+) "
    r"entity=(?P<entity>.*?) error=(?P<error>\S+)"
)
_SUB_OPTION_LINE = re.compile(
    r"DebugPrintOptions\(\) -\s+subOption (?P<sub_option>\d+) "
    r"entity=(?P<entity>.*?) error=(?P<error>\S+)"
)
_SEND_OPTION = re.compile(
    r"SendOption\(\) - selectedOption=(?P<option>-?\d+) "
    r"selectedSubOption=(?P<sub>-?\d+) selectedTarget=(?P<target>-?\d+) "
    r"selectedPosition=(?P<position>-?\d+)"
)
_ENTITY_ID = re.compile(r"\bid=(?P<value>\d+)\b")
_ENTITY_PLAYER = re.compile(r"\bplayer=(?P<value>\d+)\b")
_ENTITY_ZONE = re.compile(r"\bzone=(?P<value>[A-Za-z0-9_]+)\b")
_ENTITY_ZONE_POSITION = re.compile(r"\bzonePos=(?P<value>\d+)\b")
_ENTITY_CARD_ID = re.compile(r"\bcardId=(?P<value>[^\s\]]*)")
_ENTITY_CARD_TYPE = re.compile(r"\bcardType=(?P<value>[A-Za-z0-9_]+)\b")


class ReplayImportError(RuntimeError):
    def __init__(self, code: str, detail: str = ""):
        super().__init__(f"{code}: {detail}" if detail else code)
        self.code = code
        self.detail = detail


class SnapshotError(ReplayImportError):
    pass


@dataclass(frozen=True)
class ReplayScan:
    path: Path
    build: str
    game_type: str
    format_type: str
    scenario_id: str
    mode: str
    local_controller: int | None
    opponent_controller: int | None
    local_resolution: str
    archive_bytes: int
    log_bytes: int
    error: str = ""


@dataclass
class ReplayEntity:
    entity_id: int
    card_id: str = ""
    tags: dict[str, str] = field(default_factory=dict)

    @property
    def controller(self) -> int | None:
        return _positive_int(self.tags.get("CONTROLLER"))

    @property
    def player_id(self) -> int | None:
        return _positive_int(self.tags.get("PLAYER_ID"))

    @property
    def zone(self) -> str:
        return str(self.tags.get("ZONE") or "").upper()

    @property
    def card_type(self) -> str:
        value = str(self.tags.get("CARDTYPE") or "UNKNOWN").upper()
        return value if value in _SUPPORTED_PUBLIC_CARD_TYPES else "UNKNOWN"

    @property
    def zone_position(self) -> int:
        return max(0, _integer(self.tags.get("ZONE_POSITION"), 0))


@dataclass
class PendingPowerAction:
    block_type: str
    source_entity_id: int
    target_entity_id: int
    source_controller: int | None
    source_zone: str
    source_card_type: str
    source_card_id: str
    pre_state: dict[str, Any]
    board_position: int | None = None
    option_binding: OptionRootBinding | None = None


@dataclass(frozen=True)
class ReplayOptionEntity:
    entity_id: int
    controller: int | None
    zone: str
    card_id: str
    card_type: str


@dataclass(frozen=True)
class ReplayOptionItem:
    item_index: int
    entity: ReplayOptionEntity
    error: str


@dataclass
class ReplayOption:
    option_id: int
    option_type: str
    source: ReplayOptionEntity
    error: str
    targets: dict[int, ReplayOptionItem] = field(default_factory=dict)
    sub_options: dict[int, ReplayOptionItem] = field(default_factory=dict)


@dataclass
class ReplayOptionFrame:
    frame_id: int
    options: dict[int, ReplayOption] = field(default_factory=dict)
    current_option_id: int | None = None
    malformed_reasons: set[str] = field(default_factory=set)


@dataclass
class PendingOptionSelection:
    selection_serial: int
    frame_id: int
    option_id: int
    option_type: str
    source_entity_id: int
    selected_sub_option: int
    selected_target_entity_id: int
    selected_position: int
    frame: ReplayOptionFrame


@dataclass(frozen=True)
class OptionRootBinding:
    selection: PendingOptionSelection
    legal_candidates: tuple[dict[str, Any], ...] | None
    board_position: int | None


@dataclass
class PendingEndTurn:
    actor_controller: int
    pre_state: dict[str, Any]
    option_binding: OptionRootBinding | None = None


@dataclass
class BehaviorDraft:
    actor_side: str
    actor_player_id: str
    identity_status: str
    visibility_status: str
    action: dict[str, Any]
    pre_state: dict[str, Any]
    post_state: dict[str, Any]


@dataclass
class DecisionFrameDraft:
    behavior_draft_index: int
    frame_id: int
    selected_action: dict[str, Any]
    legal_candidates: tuple[dict[str, Any], ...]
    pre_state: dict[str, Any]
    post_state: dict[str, Any]


@dataclass
class ParsedReplay:
    public_digest_sha256: str
    game_id: str
    build: str
    mode: str
    game_type: str
    format_type: str
    result: str
    records: list[BehaviorRecord]
    decision_frames: list[DecisionFrameRecord]
    terminal_state_id: str
    abstentions: Counter[str]
    candidate_count: int
    options_frame_count: int
    send_option_count: int
    decision_rejections: Counter[str]
    decision_diagnostics: Counter[str]


def default_hdt_replay_directory() -> Path | None:
    appdata = os.environ.get("APPDATA")
    if not appdata:
        return None
    return Path(appdata) / "HearthstoneDeckTracker" / "Replays"


def _integer(value: Any, default: int = 0) -> int:
    try:
        return int(str(value))
    except (TypeError, ValueError):
        return default


def _positive_int(value: Any) -> int | None:
    parsed = _integer(value, 0)
    return parsed if parsed > 0 else None


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _utc_now_text() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _synthetic_observed_at(sequence: int) -> str:
    # Historical replay logs only carry a local time-of-day.  Persisting archive
    # timestamps would leak a play schedule, while inventing a real date would be
    # misleading.  A deterministic ordering clock satisfies the behavior contract
    # and is explicitly identified as synthetic in the import manifest.
    value = datetime(2000, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=sequence)
    return value.isoformat(timespec="microseconds").replace("+00:00", "Z")


def _anonymous_public_game_id(public_digest: str) -> str:
    material = f"{REPLAY_TRANSCRIPT_SCHEMA_ID}|{public_digest}".encode("utf-8")
    return "anon-" + _sha256_bytes(material)[:16]


def _state_with_id(value: Mapping[str, Any], game_id: str) -> dict[str, Any]:
    state = copy.deepcopy(dict(value))
    state.pop("state_id", None)
    digest = _sha256_bytes(game_id.encode("utf-8") + b"|" + _canonical_bytes(state))
    state["state_id"] = "s1-" + digest[:32]
    return state


def _mode(game_type: str, format_type: str) -> str:
    game = game_type.upper()
    format_name = format_type.upper()
    if game in _ARENA_GAME_TYPES:
        return "arena"
    if game == "GT_TAVERNBRAWL":
        return "tavern_brawl"
    if format_name == "FT_STANDARD":
        return "standard"
    if format_name == "FT_WILD":
        return "wild"
    return (game.removeprefix("GT_").lower() or "unknown")


def _power_payload(line: str) -> str | None:
    marker = _POWER_PREFIX
    index = line.find(marker)
    return None if index < 0 else line[index + len(marker) :]


def _entity_field(pattern: re.Pattern[str], raw: str) -> str:
    match = pattern.search(raw)
    return match.group("value") if match else ""


def _entity_id(raw: str) -> int:
    text = raw.strip()
    if text == "0" or not text:
        return 0
    if text.isdigit():
        return int(text)
    return _integer(_entity_field(_ENTITY_ID, text), 0)


def _option_entity(raw: str) -> ReplayOptionEntity:
    card_type = _entity_field(_ENTITY_CARD_TYPE, raw).upper()
    return ReplayOptionEntity(
        entity_id=_entity_id(raw),
        controller=_positive_int(_entity_field(_ENTITY_PLAYER, raw)),
        zone=_entity_field(_ENTITY_ZONE, raw).upper(),
        card_id=_entity_field(_ENTITY_CARD_ID, raw),
        card_type="" if card_type == "INVALID" else card_type,
    )


def _read_replay_log(path: Path) -> bytes:
    try:
        stat = path.stat()
    except OSError as exc:
        raise ReplayImportError("archive_unreadable", type(exc).__name__) from exc
    if not path.is_file():
        raise ReplayImportError("archive_not_file")
    if stat.st_size > MAX_ARCHIVE_BYTES:
        raise ReplayImportError("archive_too_large")
    try:
        with zipfile.ZipFile(path) as archive:
            names = archive.namelist()
            if names != ["output_log.txt"]:
                raise ReplayImportError("archive_layout_invalid")
            info = archive.getinfo("output_log.txt")
            if info.file_size > MAX_LOG_BYTES:
                raise ReplayImportError("power_log_too_large")
            return archive.read(info)
    except ReplayImportError:
        raise
    except (OSError, KeyError, zipfile.BadZipFile, RuntimeError) as exc:
        raise ReplayImportError("archive_invalid", type(exc).__name__) from exc


def _decode_log(raw: bytes) -> str:
    try:
        return raw.decode("utf-8-sig", errors="strict")
    except UnicodeDecodeError as exc:
        raise ReplayImportError("power_log_not_utf8") from exc


def _metadata(text: str) -> tuple[dict[str, str], dict[int, str]]:
    fields: dict[str, str] = {}
    for match in _DEBUG_GAME_FIELD.finditer(text):
        fields.setdefault(match.group("key"), match.group("value"))
    players: dict[int, str] = {}
    conflicts: set[int] = set()
    for match in _DEBUG_GAME_PLAYER.finditer(text):
        player_id = int(match.group("player"))
        name = match.group("name").strip()
        existing = players.get(player_id)
        if existing is not None and existing != name:
            conflicts.add(player_id)
        players[player_id] = name
    for player_id in conflicts:
        players.pop(player_id, None)
    return fields, players


def _option_controller_votes(text: str) -> Counter[int]:
    votes: Counter[int] = Counter()
    options: dict[int, int] = {}
    pending_send = False
    for line in text.splitlines():
        if _OPTIONS_HEADER.search(line):
            options = {}
            pending_send = False
            continue
        option = _OPTION_LINE.search(line)
        if option:
            player = _positive_int(_entity_field(_ENTITY_PLAYER, option.group("entity")))
            if player is not None:
                options[int(option.group("option"))] = player
            continue
        sent = _SEND_OPTION.search(line)
        if sent:
            selected = int(sent.group("option"))
            player = options.get(selected)
            if selected > 0 and player is not None:
                votes[player] += 1
                pending_send = False
            else:
                pending_send = selected > 0
            continue
        if pending_send:
            payload = _power_payload(line)
            root = _BLOCK_START.search(payload or "")
            if root and root.group("type") in {"PLAY", "ATTACK"}:
                player = _positive_int(
                    _entity_field(_ENTITY_PLAYER, root.group("entity"))
                )
                if player is not None:
                    votes[player] += 1
                pending_send = False
    return votes


def _resolve_controllers(
    text: str, players: Mapping[int, str]
) -> tuple[int | None, int | None, str]:
    named = sorted(
        player
        for player, name in players.items()
        if name and name != _UNKNOWN_PLAYER_NAME
    )
    unknown = sorted(
        player for player, name in players.items() if name == _UNKNOWN_PLAYER_NAME
    )
    name_candidate = (
        named[0]
        if len(named) == 1 and len(unknown) == 1 and named[0] != unknown[0]
        else None
    )
    votes = _option_controller_votes(text)
    vote_candidate = None
    if votes:
        most_common = votes.most_common()
        if len(most_common) == 1 or most_common[0][1] > most_common[1][1]:
            vote_candidate = most_common[0][0]
    if (
        name_candidate is not None
        and vote_candidate is not None
        and name_candidate != vote_candidate
    ):
        return None, None, "controller_evidence_conflict"
    local = name_candidate if name_candidate is not None else vote_candidate
    if local is None:
        return None, None, "controller_unresolved"
    controllers = set(players)
    for match in _PLAYER_ENTITY.finditer(text):
        controllers.add(int(match.group("player")))
    others = sorted(player for player in controllers if player != local)
    if len(others) != 1:
        return None, None, "opponent_controller_unresolved"
    resolution = (
        "exact_debug_print_game"
        if name_candidate is not None
        else "exact_send_option"
    )
    return local, others[0], resolution


def scan_hdt_replay(path: str | Path) -> ReplayScan:
    source = Path(path)
    archive_bytes = source.stat().st_size if source.is_file() else 0
    try:
        raw = _read_replay_log(source)
        text = _decode_log(raw)
        fields, players = _metadata(text)
        local, opponent, resolution = _resolve_controllers(text, players)
        game_type = fields.get("GameType", "")
        format_type = fields.get("FormatType", "")
        return ReplayScan(
            path=source,
            build=fields.get("BuildNumber", ""),
            game_type=game_type,
            format_type=format_type,
            scenario_id=fields.get("ScenarioID", ""),
            mode=_mode(game_type, format_type),
            local_controller=local,
            opponent_controller=opponent,
            local_resolution=resolution,
            archive_bytes=archive_bytes,
            log_bytes=len(raw),
        )
    except ReplayImportError as exc:
        return ReplayScan(
            path=source,
            build="",
            game_type="",
            format_type="",
            scenario_id="",
            mode="unknown",
            local_controller=None,
            opponent_controller=None,
            local_resolution="unresolved",
            archive_bytes=archive_bytes,
            log_bytes=0,
            error=exc.code,
        )


def discover_hdt_replays(directory: str | Path) -> list[Path]:
    root = Path(directory)
    if not root.is_dir():
        raise ReplayImportError("replay_directory_missing")
    files = sorted(
        (item for item in root.iterdir() if item.is_file() and item.suffix.lower() == ".hdtreplay"),
        key=lambda item: item.name.casefold(),
    )
    if len(files) > MAX_REPLAY_FILES:
        raise ReplayImportError("too_many_replay_files")
    total = sum(item.stat().st_size for item in files)
    if total > MAX_TOTAL_ARCHIVE_BYTES:
        raise ReplayImportError("replay_directory_too_large")
    return files


class _ReplayStateMachine:
    def __init__(
        self,
        *,
        text: str,
        build: str,
        mode: str,
        game_type: str,
        format_type: str,
        local_controller: int,
        opponent_controller: int,
        player_names: Mapping[int, str],
    ):
        self.text = text
        self.build = build
        self.mode = mode
        self.game_type = game_type
        self.format_type = format_type
        self.local_controller = local_controller
        self.opponent_controller = opponent_controller
        self.player_names = dict(player_names)
        self.entities: dict[int, ReplayEntity] = {}
        self.player_entity_by_controller: dict[int, int] = {}
        self.player_name_to_entity: dict[str, int] = {}
        self.game_entity_id = 0
        self.implicit_entity_id = 0
        self.block_stack: list[PendingPowerAction | None] = []
        self.pending_power: PendingPowerAction | None = None
        self.option_frame: ReplayOptionFrame | None = None
        self.pending_option_selection: PendingOptionSelection | None = None
        self.pending_end_turn: PendingEndTurn | None = None
        self.drafts: list[BehaviorDraft] = []
        self.decision_drafts: list[DecisionFrameDraft] = []
        self.abstentions: Counter[str] = Counter()
        self.decision_rejections: Counter[str] = Counter()
        self.decision_diagnostics: Counter[str] = Counter()
        self.options_frame_count = 0
        self.send_option_count = 0
        self.selection_serial = 0
        self.resolved_decision_selections: set[int] = set()
        self.candidate_count = 0
        self.main_action_count = 0
        self.last_snapshot: dict[str, Any] | None = None

    def entity(self, entity_id: int) -> ReplayEntity:
        entity = self.entities.get(entity_id)
        if entity is None:
            entity = ReplayEntity(entity_id)
            self.entities[entity_id] = entity
        return entity

    def _resolve_entity(self, raw: str) -> int:
        text = raw.strip()
        if text == "GameEntity":
            return self.game_entity_id
        parsed = _entity_id(text)
        if parsed > 0:
            return parsed
        controller = None
        for player_id, player_name in self.player_names.items():
            if text == player_name:
                controller = player_id
                break
        if controller is not None:
            return self.player_entity_by_controller.get(controller, 0)
        known = self.player_name_to_entity.get(text, 0)
        if known > 0:
            return known
        # HDT deliberately prints the remote player as UNKNOWN HUMAN PLAYER in
        # DebugPrintGame, while later player-level TAG_CHANGE lines can contain the
        # actual BattleTag.  In a two-player traditional match there is exactly one
        # such anonymous controller, so learn that raw alias only in memory and never
        # persist or hash it.
        if text and "[" not in text and "=" not in text:
            anonymous_controllers = [
                player_id
                for player_id, player_name in self.player_names.items()
                if player_name == _UNKNOWN_PLAYER_NAME
            ]
            if len(anonymous_controllers) == 1:
                entity_id = self.player_entity_by_controller.get(
                    anonymous_controllers[0], 0
                )
                if entity_id > 0:
                    self.player_name_to_entity[text] = entity_id
                    return entity_id
        return 0

    def _apply_descriptor(
        self,
        raw: str,
        *,
        include_mutable_tags: bool = True,
        fill_missing_mutable_tags: bool = False,
    ) -> int:
        entity_id = _entity_id(raw)
        if entity_id <= 0:
            return 0
        entity = self.entity(entity_id)
        card_id = _entity_field(_ENTITY_CARD_ID, raw)
        zone = _entity_field(_ENTITY_ZONE, raw)
        player = _entity_field(_ENTITY_PLAYER, raw)
        zone_position = _entity_field(_ENTITY_ZONE_POSITION, raw)
        card_type = _entity_field(_ENTITY_CARD_TYPE, raw)
        if card_id:
            entity.card_id = card_id
        # TAG_CHANGE descriptors describe the entity immediately before the
        # individual tag mutation. HDT commonly repeats that stale descriptor
        # for the following mutations in the same power packet. Reapplying its
        # mutable fields can therefore undo a just-observed HAND -> PLAY or
        # PLAY -> GRAVEYARD transition. Full entity/update descriptors remain
        # authoritative; TAG_CHANGE callers only use immutable identity fields.
        if include_mutable_tags or fill_missing_mutable_tags:
            if zone and (include_mutable_tags or "ZONE" not in entity.tags):
                entity.tags["ZONE"] = zone
            if player and (include_mutable_tags or "CONTROLLER" not in entity.tags):
                entity.tags["CONTROLLER"] = player
            if zone_position and (
                include_mutable_tags or "ZONE_POSITION" not in entity.tags
            ):
                entity.tags["ZONE_POSITION"] = zone_position
        if card_type and card_type != "INVALID":
            entity.tags["CARDTYPE"] = card_type
        return entity_id

    def _apply_tag(self, entity_id: int, tag: str, value: str) -> None:
        if entity_id <= 0:
            self.abstentions["tag_entity_unresolved"] += 1
            return
        self.entity(entity_id).tags[tag.upper()] = value

    def _player_entity(self, controller: int) -> ReplayEntity:
        entity_id = self.player_entity_by_controller.get(controller, 0)
        if entity_id <= 0:
            raise SnapshotError("player_entity_missing")
        return self.entity(entity_id)

    def _active_controller(self) -> int:
        active = []
        for controller in (self.local_controller, self.opponent_controller):
            try:
                player = self._player_entity(controller)
            except SnapshotError:
                continue
            if _integer(player.tags.get("CURRENT_PLAYER"), 0) == 1:
                active.append(controller)
        if len(active) != 1:
            raise SnapshotError("active_player_unresolved")
        return active[0]

    def _public_entity(self, entity: ReplayEntity) -> dict[str, Any]:
        value: dict[str, Any] = {
            "entity_id": str(entity.entity_id),
            "card_id": entity.card_id,
            "card_type": entity.card_type,
        }
        numeric_fields = {
            "COST": "cost",
            "ATK": "attack",
            "HEALTH": "health",
            "DURABILITY": "durability",
        }
        for tag, field_name in numeric_fields.items():
            if tag in entity.tags:
                value[field_name] = max(0, _integer(entity.tags[tag], 0))
        if "HEALTH" in entity.tags:
            value["current_health"] = max(
                0,
                _integer(entity.tags.get("HEALTH"), 0)
                - _integer(entity.tags.get("DAMAGE"), 0),
            )
        if "DURABILITY" in entity.tags:
            value["current_durability"] = max(
                0,
                _integer(entity.tags.get("DURABILITY"), 0)
                - _integer(entity.tags.get("DAMAGE"), 0),
            )
        return value

    def _controlled_entities(self, controller: int) -> list[ReplayEntity]:
        return [
            entity
            for entity in self.entities.values()
            if entity.controller == controller
        ]

    def _single_zone_entity(
        self, controller: int, card_type: str
    ) -> ReplayEntity | None:
        values = [
            entity
            for entity in self._controlled_entities(controller)
            if entity.zone == "PLAY" and entity.card_type == card_type
        ]
        if not values:
            return None
        values.sort(key=lambda item: (item.zone_position, item.entity_id))
        return values[-1]

    def _spell_power(self, controller: int) -> int:
        total = 0
        doublers = 0
        for entity in self._controlled_entities(controller):
            if entity.zone != "PLAY":
                continue
            total += max(0, _integer(entity.tags.get("SPELLPOWER"), 0))
            if _integer(entity.tags.get("SPELLPOWER_DOUBLE"), 0) > 0:
                doublers += 1
        return total * (2**doublers)

    def _public_player(self, controller: int, role: str) -> dict[str, Any]:
        player_entity = self._player_entity(controller)
        hero = self._single_zone_entity(controller, "HERO")
        if hero is None or not hero.card_id:
            raise SnapshotError("hero_missing")
        hero_power = self._single_zone_entity(controller, "HERO_POWER")
        weapon = self._single_zone_entity(controller, "WEAPON")
        controlled = self._controlled_entities(controller)
        hand_entities = sorted(
            (item for item in controlled if item.zone == "HAND"),
            key=lambda item: (item.zone_position, item.entity_id),
        )
        board_entities = sorted(
            (
                item
                for item in controlled
                if item.zone == "PLAY" and item.card_type in {"MINION", "LOCATION"}
            ),
            key=lambda item: (item.zone_position, item.entity_id),
        )
        if role == "opponent":
            hand = [
                {"entity_id": str(item.entity_id), "visibility": "hidden"}
                for item in hand_entities
            ]
        else:
            hand = [self._public_entity(item) for item in hand_entities]
        resources = max(0, _integer(player_entity.tags.get("RESOURCES"), 0))
        temporary = max(0, _integer(player_entity.tags.get("TEMP_RESOURCES"), 0))
        used = max(0, _integer(player_entity.tags.get("RESOURCES_USED"), 0))
        result: dict[str, Any] = {
            "player_id": role,
            "hero": self._public_entity(hero),
            "hero_power": self._public_entity(hero_power) if hero_power else None,
            "weapon": self._public_entity(weapon) if weapon else None,
            "hand": hand,
            "board": [self._public_entity(item) for item in board_entities],
            "mana": max(0, resources + temporary - used),
            "max_mana": resources,
            "armor": max(0, _integer(hero.tags.get("ARMOR"), 0)),
            "deck_size": sum(1 for item in controlled if item.zone == "DECK"),
            "fatigue": max(0, _integer(player_entity.tags.get("FATIGUE"), 0)),
            "hero_power_available": bool(
                hero_power is not None
                and _integer(hero_power.tags.get("EXHAUSTED"), 0) == 0
            ),
            "spell_power": self._spell_power(controller),
        }
        return result

    def snapshot(self) -> dict[str, Any]:
        active = self._active_controller()
        game = self.entity(self.game_entity_id) if self.game_entity_id > 0 else None
        turn = _integer(game.tags.get("TURN"), 0) if game is not None else 0
        turn = max(1, turn, self.main_action_count)
        value = {
            "turn": turn,
            "active_player_id": (
                "friendly" if active == self.local_controller else "opponent"
            ),
            "perspective_player_id": "friendly",
            "friendly": self._public_player(self.local_controller, "friendly"),
            "opponent": self._public_player(self.opponent_controller, "opponent"),
            "patch": self.build,
            "mode": self.mode,
        }
        self.last_snapshot = copy.deepcopy(value)
        return value

    @staticmethod
    def _find_public_entity(
        state: Mapping[str, Any], entity_id: int
    ) -> tuple[str, str, Mapping[str, Any]] | None:
        wanted = str(entity_id)
        for role in ("friendly", "opponent"):
            player = state[role]
            for zone in ("hero", "hero_power", "weapon"):
                entity = player.get(zone)
                if isinstance(entity, Mapping) and entity.get("entity_id") == wanted:
                    return role, zone, entity
            for zone in ("hand", "board"):
                for entity in player.get(zone, []):
                    if entity.get("entity_id") == wanted:
                        return role, zone, entity
        return None

    def _reject_decision(
        self, selection: PendingOptionSelection | None, reason: str
    ) -> None:
        if selection is None:
            self.decision_rejections[reason] += 1
            return
        if selection.selection_serial in self.resolved_decision_selections:
            return
        self.resolved_decision_selections.add(selection.selection_serial)
        self.decision_rejections[reason] += 1

    def _accept_decision(self, selection: PendingOptionSelection) -> bool:
        if selection.selection_serial in self.resolved_decision_selections:
            self.decision_diagnostics["selection_resolved_more_than_once"] += 1
            return False
        self.resolved_decision_selections.add(selection.selection_serial)
        return True

    @staticmethod
    def _decision_action(action: Mapping[str, Any]) -> dict[str, Any]:
        return {
            "kind": str(action.get("kind") or ""),
            "source_entity_id": str(action.get("source_entity_id") or ""),
            "target_entity_id": str(action.get("target_entity_id") or ""),
            "card_id": str(action.get("card_id") or ""),
            "board_position": max(0, _integer(action.get("board_position"), 0)),
        }

    @staticmethod
    def _indices_are_contiguous(values: Mapping[int, Any]) -> bool:
        if not values:
            return True
        return sorted(values) == list(range(max(values) + 1))

    def _build_decision_candidates(
        self,
        selection: PendingOptionSelection,
        pre_state: Mapping[str, Any],
    ) -> tuple[tuple[dict[str, Any], ...] | None, str]:
        frame = selection.frame
        if frame.malformed_reasons:
            return None, "options_frame_malformed"
        if selection.selected_sub_option != -1:
            return None, "selected_choice_branch_unsupported"
        if not self._indices_are_contiguous(frame.options):
            return None, "option_ids_not_contiguous"
        end_turn = frame.options.get(0)
        if (
            end_turn is None
            or end_turn.option_type != "END_TURN"
            or end_turn.source.entity_id != 0
        ):
            return None, "end_turn_option_missing_or_invalid"
        candidates: list[dict[str, Any]] = [
            {
                "option_id": 0,
                "action": {
                    "kind": "end_turn",
                    "source_entity_id": "",
                    "target_entity_id": "",
                    "card_id": "",
                    "board_position": 0,
                },
                "target_evidence": "not_applicable",
                "position_evidence": "not_applicable",
            }
        ]
        for option_id in sorted(frame.options):
            option = frame.options[option_id]
            if option_id == 0 or option.error != "NONE":
                continue
            if option.option_type != "POWER":
                return None, "legal_option_type_unsupported"
            if not self._indices_are_contiguous(option.targets):
                return None, "target_ids_not_contiguous"
            if not self._indices_are_contiguous(option.sub_options):
                return None, "sub_option_ids_not_contiguous"
            if any(item.error == "NONE" for item in option.sub_options.values()):
                return None, "choice_branch_target_domain_ambiguous"
            source_id = option.source.entity_id
            located = self._find_public_entity(pre_state, source_id)
            if located is None:
                return None, "legal_source_not_public_pre_state"
            role, zone, public_source = located
            if role != "friendly":
                return None, "legal_source_not_friendly"
            if (
                option.source.controller is not None
                and option.source.controller != self.local_controller
            ):
                return None, "legal_source_controller_mismatch"
            card_id = str(public_source.get("card_id") or "")
            card_type = str(public_source.get("card_type") or "UNKNOWN").upper()
            if not card_id:
                return None, "legal_source_card_id_missing"
            if zone == "hand" and card_type in {
                "HERO",
                "MINION",
                "SPELL",
                "WEAPON",
                "LOCATION",
            }:
                kind = "play_card"
            elif zone == "hero_power" and card_type == "HERO_POWER":
                kind = "hero_power"
            elif zone == "board" and card_type == "LOCATION":
                kind = "location_activate"
            elif zone in {"hero", "board"} and card_type in {"HERO", "MINION"}:
                internal = self.entities.get(source_id)
                if (
                    card_type == "MINION"
                    and internal is not None
                    and any(
                        str(tag).upper().startswith("TITAN_ABILITY_USED_")
                        for tag in internal.tags
                    )
                ):
                    return None, "titan_action_kind_ambiguous"
                kind = "attack"
            else:
                return None, "legal_source_action_kind_unresolved"
            legal_targets = [
                item for item in option.targets.values() if item.error == "NONE"
            ]
            if kind == "attack" and not legal_targets:
                return None, "attack_target_domain_empty"
            target_ids: list[str] = []
            if legal_targets:
                seen_targets: set[int] = set()
                for item in legal_targets:
                    target_id = item.entity.entity_id
                    if target_id <= 0 or target_id in seen_targets:
                        return None, "legal_target_identity_invalid"
                    seen_targets.add(target_id)
                    target = self._find_public_entity(pre_state, target_id)
                    if target is None:
                        return None, "legal_target_not_public_pre_state"
                    target_role, target_zone, _ = target
                    expected_controller = (
                        self.local_controller
                        if target_role == "friendly"
                        else self.opponent_controller
                    )
                    if (
                        item.entity.controller is not None
                        and item.entity.controller != expected_controller
                    ):
                        return None, "legal_target_controller_mismatch"
                    if target_role == "opponent" and target_zone == "hand":
                        return None, "opponent_hidden_target_not_public"
                    if kind == "attack" and (
                        target_role != "opponent"
                        or target_zone not in {"hero", "board"}
                    ):
                        return None, "attack_target_domain_invalid"
                    target_ids.append(str(target_id))
            else:
                target_ids.append("")
            positions = [0]
            position_evidence = "not_applicable"
            if kind == "play_card" and card_type in {"MINION", "LOCATION"}:
                board = pre_state["friendly"].get("board", [])
                if len(board) >= 7:
                    return None, "legal_placement_card_on_full_board"
                positions = list(range(1, min(7, len(board) + 1) + 1))
                position_evidence = "core_board_slots_v1"
            target_evidence = (
                "hdt_error_none" if legal_targets else "hdt_no_legal_target"
            )
            for target_id in target_ids:
                for position in positions:
                    candidates.append(
                        {
                            "option_id": option_id,
                            "action": {
                                "kind": kind,
                                "source_entity_id": str(source_id),
                                "target_entity_id": target_id,
                                "card_id": card_id,
                                "board_position": position,
                            },
                            "target_evidence": target_evidence,
                            "position_evidence": position_evidence,
                        }
                    )
                    if len(candidates) > 512:
                        return None, "candidate_count_exceeds_limit"
        action_keys = [_canonical_bytes(item["action"]) for item in candidates]
        if len(set(action_keys)) != len(action_keys):
            return None, "duplicate_candidate_action"
        return tuple(candidates), ""

    def _capture_decision(
        self,
        *,
        behavior_draft_index: int,
        binding: OptionRootBinding | None,
        action: Mapping[str, Any],
        pre_state: dict[str, Any],
        post_state: dict[str, Any],
    ) -> None:
        if binding is None or binding.legal_candidates is None:
            return
        selected_action = self._decision_action(action)
        matches = [
            item
            for item in binding.legal_candidates
            if item["option_id"] == binding.selection.option_id
            and item["action"] == selected_action
        ]
        if len(matches) != 1:
            self._reject_decision(
                binding.selection, "selected_action_not_in_exact_option_candidates"
            )
            return
        if not self._accept_decision(binding.selection):
            return
        self.decision_drafts.append(
            DecisionFrameDraft(
                behavior_draft_index=behavior_draft_index,
                frame_id=binding.selection.frame_id,
                selected_action=selected_action,
                legal_candidates=binding.legal_candidates,
                pre_state=pre_state,
                post_state=post_state,
            )
        )

    def _reject_pending_power_decision(
        self, pending: PendingPowerAction, reason: str
    ) -> None:
        if pending.option_binding is not None:
            self._reject_decision(pending.option_binding.selection, reason)

    def _complete_power(self, post_state: dict[str, Any]) -> None:
        pending = self.pending_power
        self.pending_power = None
        if pending is None:
            return
        source = self.entities.get(pending.source_entity_id)
        if source is None:
            self.abstentions["action_source_missing"] += 1
            self._reject_pending_power_decision(pending, "behavior_action_source_missing")
            return
        actor_side = (
            "local"
            if pending.source_controller == self.local_controller
            else "opponent"
            if pending.source_controller == self.opponent_controller
            else "unknown"
        )
        actor_player_id = "friendly" if actor_side == "local" else "opponent"
        if actor_side == "unknown":
            self.abstentions["action_controller_unresolved"] += 1
            self._reject_pending_power_decision(
                pending, "behavior_action_controller_unresolved"
            )
            return
        if pending.pre_state["active_player_id"] != actor_player_id:
            self.abstentions["action_actor_not_active"] += 1
            self._reject_pending_power_decision(pending, "behavior_actor_not_active")
            return
        if pending.block_type == "ATTACK":
            kind = "attack"
        elif pending.source_zone == "HAND":
            kind = "play_card"
        elif pending.source_zone == "PLAY" and pending.source_card_type == "HERO_POWER":
            kind = "hero_power"
        elif pending.source_zone == "PLAY" and pending.source_card_type == "LOCATION":
            kind = "location_activate"
        else:
            self.abstentions[
                "non_player_power_source_zone:" + (pending.source_zone or "unknown")
            ] += 1
            self._reject_pending_power_decision(
                pending, "selected_root_action_kind_unresolved"
            )
            return
        pre_source = self._find_public_entity(
            pending.pre_state, pending.source_entity_id
        )
        if pre_source is None:
            self.abstentions["action_source_not_public_pre_state"] += 1
            self._reject_pending_power_decision(
                pending, "selected_source_not_public_pre_state"
            )
            return
        if kind == "attack" and pre_source[1] not in {"hero", "board"}:
            self.abstentions["attack_source_not_public_character"] += 1
            self._reject_pending_power_decision(
                pending, "selected_attack_source_not_character"
            )
            return
        if kind == "attack" and pending.target_entity_id <= 0:
            self.abstentions["attack_target_missing"] += 1
            self._reject_pending_power_decision(pending, "selected_attack_target_missing")
            return
        if pending.target_entity_id > 0 and self._find_public_entity(
            pending.pre_state, pending.target_entity_id
        ) is None:
            self.abstentions["action_target_not_public_pre_state"] += 1
            self._reject_pending_power_decision(
                pending, "selected_target_not_public_pre_state"
            )
            return
        card_id = source.card_id or pending.source_card_id
        if not (actor_side == "opponent" and kind == "play_card"):
            public_card = str(pre_source[2].get("card_id") or "")
            card_id = public_card or card_id
        if not card_id:
            self.abstentions["action_card_id_missing"] += 1
            self._reject_pending_power_decision(pending, "selected_card_id_missing")
            return
        if kind == "attack" and isinstance(pre_source[2], dict):
            # The isolated HDT ATTACK root is direct public evidence that this
            # source had at least one attack available at the pre-action
            # boundary. Historical entity dumps do not consistently repeat
            # EXHAUSTED/NUM_ATTACKS_THIS_TURN for every character, so preserve
            # the fact proven by the observed action instead of guessing the
            # readiness of any other character in the state.
            previous_post = (
                self.drafts[-1].post_state if self.drafts else None
            )
            shared_boundary = (
                previous_post is not None
                and previous_post == pending.pre_state
            )
            pre_source[2]["can_attack"] = True
            pre_source[2]["attacks_remaining"] = max(
                1, _integer(pre_source[2].get("attacks_remaining"), 0)
            )
            if shared_boundary and previous_post is not None:
                previous_source = self._find_public_entity(
                    previous_post, pending.source_entity_id
                )
                if previous_source is not None and isinstance(
                    previous_source[2], dict
                ):
                    # Adjacent post/pre snapshots describe the same public
                    # boundary and must remain content-identical after adding
                    # evidence learned from the following ATTACK root.
                    previous_source[2]["can_attack"] = True
                    previous_source[2]["attacks_remaining"] = max(
                        1,
                        _integer(
                            previous_source[2].get("attacks_remaining"), 0
                        ),
                    )
        identity_status = "exact_public_entity"
        visibility_status = "public_pre_state"
        if actor_side == "opponent" and kind == "play_card":
            identity_status = "revealed_after_action"
            visibility_status = "revealed_post_action"
        action = {
            "kind": kind,
            "source_entity_id": str(pending.source_entity_id),
            "target_entity_id": (
                str(pending.target_entity_id)
                if pending.target_entity_id > 0
                else ""
            ),
            "card_id": card_id,
        }
        if pending.board_position is not None:
            action["choice_status"] = "none"
            action["board_position"] = pending.board_position
        behavior_draft_index = len(self.drafts)
        self.drafts.append(
            BehaviorDraft(
                actor_side=actor_side,
                actor_player_id=actor_player_id,
                identity_status=identity_status,
                visibility_status=visibility_status,
                action=action,
                pre_state=pending.pre_state,
                post_state=post_state,
            )
        )
        self._capture_decision(
            behavior_draft_index=behavior_draft_index,
            binding=pending.option_binding,
            action=action,
            pre_state=pending.pre_state,
            post_state=post_state,
        )

    def _close_pending_power(self, *, stable: bool) -> None:
        if self.pending_power is None:
            return
        if not stable:
            self.abstentions["power_action_boundary_unstable"] += 1
            self._reject_pending_power_decision(
                self.pending_power, "selected_transition_boundary_unstable"
            )
            self.pending_power = None
            return
        try:
            post = self.snapshot()
        except SnapshotError as exc:
            self.abstentions["power_post_state:" + exc.code] += 1
            self._reject_pending_power_decision(
                self.pending_power, "selected_post_state_unavailable"
            )
            self.pending_power = None
            return
        self._complete_power(post)

    def _begin_end_turn(self) -> None:
        self._close_pending_power(stable=True)
        if self.pending_end_turn is not None:
            self.abstentions["end_turn_boundary_overlapped"] += 1
            if self.pending_end_turn.option_binding is not None:
                self._reject_decision(
                    self.pending_end_turn.option_binding.selection,
                    "end_turn_boundary_overlapped",
                )
            self.pending_end_turn = None
        try:
            actor = self._active_controller()
            pre = self.snapshot()
        except SnapshotError as exc:
            self.abstentions["end_turn_pre_state:" + exc.code] += 1
            if self.pending_option_selection is not None:
                self._reject_decision(
                    self.pending_option_selection, "end_turn_pre_state_unavailable"
                )
                self.pending_option_selection = None
            return
        self.candidate_count += 1
        binding: OptionRootBinding | None = None
        selection = self.pending_option_selection
        if selection is not None:
            self.pending_option_selection = None
            if selection.option_id != 0 or selection.option_type != "END_TURN":
                self._reject_decision(selection, "send_option_end_turn_mismatch")
            elif actor != self.local_controller:
                self._reject_decision(selection, "send_option_non_local_end_turn")
            else:
                candidates, reason = self._build_decision_candidates(selection, pre)
                if candidates is None:
                    self._reject_decision(selection, reason)
                binding = OptionRootBinding(
                    selection=selection,
                    legal_candidates=candidates,
                    board_position=None,
                )
        self.pending_end_turn = PendingEndTurn(
            actor_controller=actor,
            pre_state=pre,
            option_binding=binding,
        )

    def _close_end_turn(self) -> None:
        pending = self.pending_end_turn
        self.pending_end_turn = None
        if pending is None:
            return
        actor = pending.actor_controller
        pre = pending.pre_state
        try:
            post = self.snapshot()
        except SnapshotError as exc:
            self.abstentions["end_turn_post_state:" + exc.code] += 1
            if pending.option_binding is not None:
                self._reject_decision(
                    pending.option_binding.selection, "end_turn_post_state_unavailable"
                )
            return
        actor_side = "local" if actor == self.local_controller else "opponent"
        actor_player_id = "friendly" if actor_side == "local" else "opponent"
        if pre["active_player_id"] != actor_player_id:
            self.abstentions["end_turn_actor_not_active"] += 1
            if pending.option_binding is not None:
                self._reject_decision(
                    pending.option_binding.selection, "end_turn_actor_not_active"
                )
            return
        if post["active_player_id"] == actor_player_id:
            self.abstentions["end_turn_did_not_change_active_player"] += 1
            if pending.option_binding is not None:
                self._reject_decision(
                    pending.option_binding.selection,
                    "end_turn_active_player_did_not_change",
                )
            return
        action = {
            "kind": "end_turn",
            "source_entity_id": "",
            "target_entity_id": "",
            "card_id": "",
        }
        behavior_draft_index = len(self.drafts)
        self.drafts.append(
            BehaviorDraft(
                actor_side=actor_side,
                actor_player_id=actor_player_id,
                identity_status="event_only",
                visibility_status="public_pre_state",
                action=action,
                pre_state=pre,
                post_state=post,
            )
        )
        self._capture_decision(
            behavior_draft_index=behavior_draft_index,
            binding=pending.option_binding,
            action=action,
            pre_state=pre,
            post_state=post,
        )

    def _begin_options_frame(self, match: re.Match[str]) -> None:
        if self.option_frame is not None:
            self.decision_diagnostics["options_frame_without_send"] += 1
        if self.pending_option_selection is not None:
            self.abstentions["send_option_replaced_by_new_frame"] += 1
            self._reject_decision(
                self.pending_option_selection, "send_option_replaced_by_new_frame"
            )
        self.options_frame_count += 1
        self.option_frame = ReplayOptionFrame(frame_id=int(match.group("id")))
        self.pending_option_selection = None

    def _record_option(self, match: re.Match[str]) -> None:
        frame = self.option_frame
        if frame is None:
            self.abstentions["option_without_frame"] += 1
            self.decision_diagnostics["option_without_frame"] += 1
            return
        option_id = int(match.group("option"))
        if option_id in frame.options:
            frame.malformed_reasons.add("duplicate_option_id")
            return
        frame.options[option_id] = ReplayOption(
            option_id=option_id,
            option_type=match.group("type").upper(),
            source=_option_entity(match.group("entity")),
            error=match.group("error").upper(),
        )
        frame.current_option_id = option_id

    def _record_option_item(
        self, match: re.Match[str], *, sub_option: bool
    ) -> None:
        frame = self.option_frame
        if frame is None or frame.current_option_id is None:
            self.decision_diagnostics[
                "sub_option_without_option" if sub_option else "target_without_option"
            ] += 1
            if frame is not None:
                frame.malformed_reasons.add("item_without_option")
            return
        option = frame.options.get(frame.current_option_id)
        if option is None:
            frame.malformed_reasons.add("item_option_missing")
            return
        group_name = "sub_option" if sub_option else "target"
        item_index = int(match.group(group_name))
        destination = option.sub_options if sub_option else option.targets
        if item_index in destination:
            frame.malformed_reasons.add(
                "duplicate_sub_option_id" if sub_option else "duplicate_target_id"
            )
            return
        destination[item_index] = ReplayOptionItem(
            item_index=item_index,
            entity=_option_entity(match.group("entity")),
            error=match.group("error").upper(),
        )

    def _record_send_option(self, match: re.Match[str]) -> None:
        self.send_option_count += 1
        self.selection_serial += 1
        if self.pending_option_selection is not None:
            self.abstentions["send_option_replaced_before_root"] += 1
            self._reject_decision(
                self.pending_option_selection, "send_option_replaced_before_root"
            )
        self.pending_option_selection = None
        frame = self.option_frame
        self.option_frame = None
        option_id = int(match.group("option"))
        if frame is None:
            self.abstentions["send_option_source_unresolved"] += 1
            self._reject_decision(None, "send_option_without_options_frame")
            return
        option = frame.options.get(option_id)
        if option_id < 0 or option is None:
            self.abstentions["send_option_source_unresolved"] += 1
            self._reject_decision(None, "selected_option_missing")
            return
        selected_sub_option = int(match.group("sub"))
        selected_target = int(match.group("target"))
        selected_position = int(match.group("position"))
        if (
            selected_sub_option < -1
            or selected_target < 0
            or not 0 <= selected_position <= 7
        ):
            self.abstentions["send_option_selection_invalid"] += 1
            self._reject_decision(None, "send_option_selection_invalid")
            return
        if option_id == 0:
            valid_option = (
                option.option_type == "END_TURN"
                and option.source.entity_id == 0
                and selected_sub_option == -1
                and selected_target == 0
                and selected_position == 0
            )
        else:
            valid_option = (
                option.option_type == "POWER"
                and option.error == "NONE"
                and option.source.entity_id > 0
            )
        if not valid_option:
            self.abstentions["send_option_selection_invalid"] += 1
            self._reject_decision(None, "selected_option_contract_invalid")
            return
        self.pending_option_selection = PendingOptionSelection(
            selection_serial=self.selection_serial,
            frame_id=frame.frame_id,
            option_id=option_id,
            option_type=option.option_type,
            source_entity_id=option.source.entity_id,
            selected_sub_option=selected_sub_option,
            selected_target_entity_id=selected_target,
            selected_position=selected_position,
            frame=frame,
        )

    def _consume_option_root(
        self,
        *,
        block_type: str,
        block_sub_option: int,
        source: ReplayEntity,
        source_entity_id: int,
        target_entity_id: int,
        pre_state: Mapping[str, Any],
    ) -> OptionRootBinding | None:
        selection = self.pending_option_selection
        self.pending_option_selection = None
        if selection is None:
            return None
        if selection.option_type != "POWER":
            self.abstentions["send_option_type_mismatch"] += 1
            self._reject_decision(selection, "send_option_root_type_mismatch")
            return None
        if selection.source_entity_id != source_entity_id:
            self.abstentions["send_option_source_mismatch"] += 1
            self._reject_decision(selection, "send_option_source_mismatch")
            return None
        if selection.selected_target_entity_id != target_entity_id:
            self.abstentions["send_option_target_mismatch"] += 1
            self._reject_decision(selection, "send_option_target_mismatch")
            return None
        if selection.selected_sub_option != block_sub_option:
            self.abstentions["send_option_sub_option_mismatch"] += 1
            self._reject_decision(selection, "send_option_sub_option_mismatch")
            return None
        if source.controller != self.local_controller:
            self.abstentions["send_option_non_local_root"] += 1
            self._reject_decision(selection, "send_option_non_local_root")
            return None
        board_position: int | None = None
        if block_type != "PLAY" or source.zone != "HAND":
            if selection.selected_position != 0:
                self.abstentions["send_option_non_play_position"] += 1
                self._reject_decision(selection, "send_option_non_play_position")
                return None
        elif source.card_type not in {"MINION", "LOCATION"}:
            if selection.selected_position != 0:
                self.abstentions["send_option_non_board_card_position"] += 1
                self._reject_decision(
                    selection, "send_option_non_board_card_position"
                )
                return None
        else:
            friendly = pre_state.get("friendly")
            board = friendly.get("board", []) if isinstance(friendly, Mapping) else []
            maximum = min(7, len(board) + 1)
            if len(board) >= 7 or not 1 <= selection.selected_position <= maximum:
                self.abstentions["send_option_board_position_out_of_range"] += 1
                self._reject_decision(
                    selection, "send_option_board_position_out_of_range"
                )
                return None
            board_position = selection.selected_position
        candidates, reason = self._build_decision_candidates(selection, pre_state)
        if candidates is None:
            self._reject_decision(selection, reason)
        return OptionRootBinding(
            selection=selection,
            legal_candidates=candidates,
            board_position=board_position,
        )

    def _begin_power_action(self, match: re.Match[str]) -> PendingPowerAction | None:
        self._close_pending_power(stable=True)
        self.candidate_count += 1
        source_raw = match.group("entity")
        target_raw = match.group("target")
        # BLOCK_START repeats the entity descriptor captured when HDT first
        # learned about the entity.  A location activation can therefore still
        # say ``zone=HAND`` even though intervening TAG_CHANGE lines have moved
        # it to PLAY.  The state machine's observed tags are authoritative at an
        # action boundary; the block descriptor is used only to resolve stable
        # identity fields and must never rewind zone/controller/position.
        source_id = self._apply_descriptor(
            source_raw,
            include_mutable_tags=False,
            fill_missing_mutable_tags=True,
        )
        target_id = self._apply_descriptor(
            target_raw,
            include_mutable_tags=False,
            fill_missing_mutable_tags=True,
        )
        if source_id <= 0:
            self.abstentions["root_action_source_unresolved"] += 1
            if self.pending_option_selection is not None:
                self._reject_decision(
                    self.pending_option_selection, "root_action_source_unresolved"
                )
            self.pending_option_selection = None
            return None
        source = self.entity(source_id)
        try:
            pre = self.snapshot()
        except SnapshotError as exc:
            self.abstentions["power_pre_state:" + exc.code] += 1
            if self.pending_option_selection is not None:
                self._reject_decision(
                    self.pending_option_selection, "selected_pre_state_unavailable"
                )
            self.pending_option_selection = None
            return None
        option_binding = self._consume_option_root(
            block_type=match.group("type"),
            block_sub_option=_integer(match.group("sub_option"), -1),
            source=source,
            source_entity_id=source_id,
            target_entity_id=target_id,
            pre_state=pre,
        )
        return PendingPowerAction(
            block_type=match.group("type"),
            source_entity_id=source_id,
            target_entity_id=target_id,
            source_controller=source.controller,
            source_zone=source.zone,
            source_card_type=source.card_type,
            source_card_id=source.card_id,
            pre_state=pre,
            board_position=(
                option_binding.board_position if option_binding is not None else None
            ),
            option_binding=option_binding,
        )

    def _handle_tag_change(self, match: re.Match[str]) -> None:
        raw_entity = match.group("entity")
        tag = match.group("tag").upper()
        value = match.group("value")
        if tag == "STEP" and value == "MAIN_END" and not self.block_stack:
            self._begin_end_turn()
        entity_id = self._resolve_entity(raw_entity)
        self._apply_descriptor(raw_entity, include_mutable_tags=False)
        self._apply_tag(entity_id, tag, value)
        if tag == "STEP" and value == "MAIN_ACTION" and not self.block_stack:
            self.main_action_count += 1
            self._close_end_turn()
        elif tag == "STEP" and value in {"FINAL_WRAPUP", "FINAL_GAMEOVER"}:
            self._close_pending_power(stable=True)
            if value == "FINAL_GAMEOVER" and self.pending_end_turn is not None:
                self._close_end_turn()

    def parse(self) -> None:
        for line in self.text.splitlines():
            options_header = _OPTIONS_HEADER.search(line)
            if options_header:
                self._close_pending_power(stable=True)
                self._begin_options_frame(options_header)
                continue

            option = _OPTION_LINE.search(line)
            if option:
                self._record_option(option)
                continue

            target = _TARGET_LINE.search(line)
            if target:
                self._record_option_item(target, sub_option=False)
                continue

            sub_option = _SUB_OPTION_LINE.search(line)
            if sub_option:
                self._record_option_item(sub_option, sub_option=True)
                continue

            sent = _SEND_OPTION.search(line)
            if sent:
                self._record_send_option(sent)
                continue

            payload = _power_payload(line)
            if payload is None:
                continue
            stripped = payload.lstrip()

            game = _GAME_ENTITY.search(stripped)
            if game:
                self.game_entity_id = int(game.group("entity"))
                entity = self.entity(self.game_entity_id)
                entity.tags["CARDTYPE"] = "GAME"
                self.implicit_entity_id = self.game_entity_id
                continue

            player = _PLAYER_ENTITY.search(stripped)
            if player:
                entity_id = int(player.group("entity"))
                controller = int(player.group("player"))
                entity = self.entity(entity_id)
                entity.tags["CARDTYPE"] = "PLAYER"
                entity.tags["PLAYER_ID"] = str(controller)
                entity.tags["CONTROLLER"] = str(controller)
                self.player_entity_by_controller[controller] = entity_id
                name = self.player_names.get(controller)
                if name:
                    self.player_name_to_entity[name] = entity_id
                self.implicit_entity_id = entity_id
                continue

            created = _FULL_ENTITY_CREATE.search(stripped)
            if created:
                entity_id = int(created.group("entity"))
                entity = self.entity(entity_id)
                if created.group("card"):
                    entity.card_id = created.group("card")
                self.implicit_entity_id = entity_id
                continue

            updated = _ENTITY_UPDATE.search(stripped)
            if updated:
                entity_id = self._apply_descriptor(updated.group("entity"))
                if entity_id <= 0:
                    entity_id = self._resolve_entity(updated.group("entity"))
                if entity_id > 0:
                    self.entity(entity_id).card_id = updated.group("card")
                    self.implicit_entity_id = entity_id
                else:
                    self.abstentions["entity_update_unresolved"] += 1
                continue

            hidden = _HIDE_ENTITY.search(stripped)
            if hidden:
                self.implicit_entity_id = int(hidden.group("entity"))
                continue

            tag_change = _TAG_CHANGE.search(stripped)
            if tag_change:
                self._handle_tag_change(tag_change)
                self.implicit_entity_id = 0
                continue

            implicit = _IMPLICIT_TAG.search(payload)
            if implicit and self.implicit_entity_id > 0:
                self._apply_tag(
                    self.implicit_entity_id,
                    implicit.group("tag"),
                    implicit.group("value"),
                )
                continue

            block = _BLOCK_START.search(stripped)
            if block:
                root = not self.block_stack
                action = None
                if root and block.group("type") in {"PLAY", "ATTACK"}:
                    action = self._begin_power_action(block)
                elif root and self.pending_option_selection is not None:
                    self.abstentions["send_option_root_type_mismatch"] += 1
                    self._reject_decision(
                        self.pending_option_selection,
                        "selected_root_block_type_unsupported",
                    )
                    self.pending_option_selection = None
                self.block_stack.append(action)
                self.implicit_entity_id = 0
                continue

            if stripped.startswith("BLOCK_END"):
                if not self.block_stack:
                    self.abstentions["block_end_without_start"] += 1
                    continue
                action = self.block_stack.pop()
                if action is not None:
                    if self.pending_power is not None:
                        self.abstentions["power_action_boundary_overlapped"] += 1
                    self.pending_power = action
                self.implicit_entity_id = 0

        if self.block_stack:
            self.abstentions["unclosed_power_blocks"] += len(self.block_stack)
            for action in self.block_stack:
                if action is not None:
                    self._reject_pending_power_decision(
                        action, "unclosed_selected_power_block"
                    )
        if self.option_frame is not None:
            self.decision_diagnostics["options_frame_without_send"] += 1
            self.option_frame = None
        if self.pending_option_selection is not None:
            self.abstentions["send_option_without_root"] += 1
            self._reject_decision(
                self.pending_option_selection, "send_option_without_observed_root"
            )
            self.pending_option_selection = None
        self._close_pending_power(stable=False)
        if self.pending_end_turn is not None:
            self.abstentions["unclosed_end_turn"] += 1
            if self.pending_end_turn.option_binding is not None:
                self._reject_decision(
                    self.pending_end_turn.option_binding.selection,
                    "unclosed_end_turn",
                )
            self.pending_end_turn = None
        accounted = len(self.decision_drafts) + sum(self.decision_rejections.values())
        if accounted != self.send_option_count:
            self.decision_diagnostics["send_option_accounting_mismatch"] += abs(
                self.send_option_count - accounted
            )

    def outcome(self) -> str:
        try:
            player = self._player_entity(self.local_controller)
        except SnapshotError:
            return ""
        value = str(player.tags.get("PLAYSTATE") or "").upper()
        return {"WON": "win", "LOST": "loss", "TIED": "tie"}.get(value, "")


def _materialize_replay(
    *,
    machine: _ReplayStateMachine,
) -> ParsedReplay:
    outcome = machine.outcome()
    transcript = {
        "schema": REPLAY_TRANSCRIPT_SCHEMA_ID,
        "build": machine.build,
        "mode": machine.mode,
        "game_type": machine.game_type,
        "format_type": machine.format_type,
        "local_result": outcome,
        "actions": [
            {
                "actor_side": draft.actor_side,
                "actor_player_id": draft.actor_player_id,
                "identity_status": draft.identity_status,
                "visibility_status": draft.visibility_status,
                "action": draft.action,
                "pre_state": draft.pre_state,
                "post_state": draft.post_state,
            }
            for draft in machine.drafts
        ],
    }
    public_digest = _sha256_bytes(_canonical_bytes(transcript))
    game_id = _anonymous_public_game_id(public_digest)
    records: list[BehaviorRecord] = []
    behavior_by_draft_index: dict[int, BehaviorRecord] = {}
    abstentions = Counter(machine.abstentions)
    terminal_state_id = "terminal-" + public_digest[:32]
    sequence = 0
    for draft_index, draft in enumerate(machine.drafts):
        pre = _state_with_id(draft.pre_state, game_id)
        post = _state_with_id(draft.post_state, game_id)
        terminal_state_id = post["state_id"]
        try:
            # A replay action is useful for imitation only when both public
            # boundaries also satisfy the production solver state contract.
            # Invalid boundaries are counted and skipped instead of biasing the
            # behavior prior.
            GameState.from_dict(pre, "behavior.pre_state")
            GameState.from_dict(post, "behavior.post_state")
        except SchemaError as exc:
            if exc.path.endswith(".hand") and "more than 10" in exc.message:
                reason = "hand_capacity_exceeded"
            elif exc.path.endswith(".board") and "more than 7" in exc.message:
                reason = "board_capacity_exceeded"
            elif exc.path.endswith(".mana"):
                reason = "mana_invalid"
            elif exc.path.endswith(".active_player_id"):
                reason = "active_player_invalid"
            elif exc.path.endswith(".perspective_player_id"):
                reason = "perspective_player_invalid"
            elif exc.path == "state" and "entity_id values must be unique" in exc.message:
                reason = "duplicate_entity_id"
            else:
                reason = "schema_invalid"
            abstentions["solver_state_contract:" + reason] += 1
            continue
        sequence += 1
        try:
            record = create_behavior_record(
                    game_id=game_id,
                    behavior_sequence=sequence,
                    observed_at_utc=_synthetic_observed_at(sequence),
                    actor_side=draft.actor_side,
                    actor_player_id=draft.actor_player_id,
                    actor_evidence="hdt_replay_power",
                    identity_status=draft.identity_status,
                    visibility_status=draft.visibility_status,
                    boundary_status="isolated",
                    source_event="hdt_replay_power",
                    action=draft.action,
                    pre_state=pre,
                    post_state=post,
                    rl_training_eligible=False,
                )
            records.append(record)
            behavior_by_draft_index[draft_index] = record
        except BehaviorValidationError as exc:
            abstentions[f"behavior_contract:{exc.code}"] += 1
    decision_frames: list[DecisionFrameRecord] = []
    decision_rejections = Counter(machine.decision_rejections)
    decision_sequence = 0
    for draft in machine.decision_drafts:
        behavior = behavior_by_draft_index.get(draft.behavior_draft_index)
        if behavior is None:
            decision_rejections["selected_behavior_record_unavailable"] += 1
            continue
        selected_action = machine._decision_action(behavior.value["action"])
        if selected_action != draft.selected_action:
            decision_rejections["selected_behavior_action_mismatch"] += 1
            continue
        decision_sequence += 1
        try:
            decision_frames.append(
                create_decision_frame_record(
                    game_id=game_id,
                    decision_sequence=decision_sequence,
                    observed_at_utc=_synthetic_observed_at(decision_sequence),
                    client_build=machine.build,
                    mode=machine.mode,
                    selected_behavior_id=behavior.behavior_id,
                    hdt_frame_id=draft.frame_id,
                    pre_state=behavior.value["pre_state"],
                    post_state=behavior.value["post_state"],
                    selected_action=draft.selected_action,
                    legal_candidates=draft.legal_candidates,
                )
            )
        except DecisionFrameValidationError as exc:
            decision_rejections[f"decision_frame_contract:{exc.code}"] += 1
    return ParsedReplay(
        public_digest_sha256=public_digest,
        game_id=game_id,
        build=machine.build,
        mode=machine.mode,
        game_type=machine.game_type,
        format_type=machine.format_type,
        result=outcome,
        records=records,
        decision_frames=decision_frames,
        terminal_state_id=terminal_state_id,
        abstentions=abstentions,
        candidate_count=machine.candidate_count,
        options_frame_count=machine.options_frame_count,
        send_option_count=machine.send_option_count,
        decision_rejections=decision_rejections,
        decision_diagnostics=Counter(machine.decision_diagnostics),
    )


def parse_hdt_replay(path: str | Path) -> ParsedReplay:
    source = Path(path)
    raw = _read_replay_log(source)
    text = _decode_log(raw)
    fields, players = _metadata(text)
    local, opponent, resolution = _resolve_controllers(text, players)
    if local is None or opponent is None:
        raise ReplayImportError(resolution)
    build = fields.get("BuildNumber", "")
    game_type = fields.get("GameType", "")
    format_type = fields.get("FormatType", "")
    if not _SAFE_BUILD.fullmatch(build):
        raise ReplayImportError("build_missing_or_invalid")
    if not game_type or not format_type:
        raise ReplayImportError("game_mode_metadata_missing")
    machine = _ReplayStateMachine(
        text=text,
        build=build,
        mode=_mode(game_type, format_type),
        game_type=game_type,
        format_type=format_type,
        local_controller=local,
        opponent_controller=opponent,
        player_names=players,
    )
    machine.parse()
    return _materialize_replay(machine=machine)


def _requested_builds(scans: Sequence[ReplayScan], requested: str) -> set[str]:
    value = requested.strip().lower()
    available = sorted(
        {scan.build for scan in scans if _SAFE_BUILD.fullmatch(scan.build)},
        key=int,
    )
    if value == "all":
        return set(available)
    if value == "latest":
        if not available:
            return set()
        return {available[-1]}
    if not _SAFE_BUILD.fullmatch(value):
        raise ReplayImportError("requested_build_invalid")
    return {value}


def _scan_inventory(scans: Sequence[ReplayScan]) -> dict[str, Any]:
    return {
        "archive_count": len(scans),
        "archive_bytes": sum(scan.archive_bytes for scan in scans),
        "power_log_bytes": sum(scan.log_bytes for scan in scans),
        "build_game_counts": dict(
            sorted(Counter(scan.build or "missing" for scan in scans).items())
        ),
        "mode_game_counts": dict(sorted(Counter(scan.mode for scan in scans).items())),
        "game_type_counts": dict(
            sorted(Counter(scan.game_type or "missing" for scan in scans).items())
        ),
        "format_type_counts": dict(
            sorted(Counter(scan.format_type or "missing" for scan in scans).items())
        ),
        "controller_resolution_counts": dict(
            sorted(Counter(scan.local_resolution for scan in scans).items())
        ),
        "scan_error_counts": dict(
            sorted(Counter(scan.error for scan in scans if scan.error).items())
        ),
    }


def _selection(
    scans: Sequence[ReplayScan], *, requested_build: str, modes: set[str]
) -> tuple[list[ReplayScan], set[str]]:
    build_candidates = [scan for scan in scans if scan.mode in modes and not scan.error]
    builds = _requested_builds(build_candidates, requested_build)
    selected = [
        scan
        for scan in build_candidates
        if scan.build in builds
    ]
    return selected, builds


def _rate(numerator: int, denominator: int) -> float:
    return round(numerator / denominator, 6) if denominator else 0.0


def _record_entity_location(
    state: Mapping[str, Any], entity_id: str
) -> tuple[str, str, Mapping[str, Any]] | None:
    if not entity_id:
        return None
    for role in ("friendly", "opponent"):
        player = state.get(role)
        if not isinstance(player, Mapping):
            continue
        for zone in ("hero", "hero_power", "weapon"):
            entity = player.get(zone)
            if isinstance(entity, Mapping) and str(entity.get("entity_id") or "") == entity_id:
                return role, zone, entity
        for zone in ("hand", "board"):
            entities = player.get(zone)
            if not isinstance(entities, Sequence) or isinstance(
                entities, (str, bytes, bytearray)
            ):
                continue
            for entity in entities:
                if isinstance(entity, Mapping) and str(entity.get("entity_id") or "") == entity_id:
                    return role, zone, entity
    return None


def _transition_quality(records: Sequence[BehaviorRecord]) -> dict[str, Any]:
    actor_active_count = 0
    play_count = 0
    play_source_hand_pre_count = 0
    play_source_left_hand_post_count = 0
    play_source_still_hand_post_count = 0
    play_source_still_hand_games: set[str] = set()
    attack_count = 0
    attack_readiness_explicit_count = 0
    end_turn_count = 0
    end_turn_active_changed_count = 0
    board_position_record_count = 0

    for record in records:
        value = record.value
        action = value["action"]
        kind = str(action["kind"])
        actor = str(value["actor_player_id"])
        pre = value["pre_state"]
        post = value["post_state"]
        if pre.get("active_player_id") == actor:
            actor_active_count += 1
        source_id = str(action.get("source_entity_id") or "")
        pre_source = _record_entity_location(pre, source_id)
        post_source = _record_entity_location(post, source_id)
        if kind == "play_card":
            play_count += 1
            if pre_source is not None and pre_source[:2] == (actor, "hand"):
                play_source_hand_pre_count += 1
            if post_source is not None and post_source[:2] == (actor, "hand"):
                play_source_still_hand_post_count += 1
                play_source_still_hand_games.add(record.game_id)
            else:
                play_source_left_hand_post_count += 1
            if action.get("board_position") is not None:
                board_position_record_count += 1
        elif kind == "attack":
            attack_count += 1
            if pre_source is not None and (
                "can_attack" in pre_source[2]
                or "attacks_remaining" in pre_source[2]
            ):
                attack_readiness_explicit_count += 1
        elif kind == "end_turn":
            end_turn_count += 1
            if post.get("active_player_id") != actor:
                end_turn_active_changed_count += 1

    record_count = len(records)
    return {
        "action_actor_active_pre_count": actor_active_count,
        "action_actor_active_pre_rate": _rate(actor_active_count, record_count),
        "play_card_record_count": play_count,
        "play_source_in_actor_hand_pre_count": play_source_hand_pre_count,
        "play_source_in_actor_hand_pre_rate": _rate(
            play_source_hand_pre_count, play_count
        ),
        "play_source_left_actor_hand_post_count": play_source_left_hand_post_count,
        "play_source_left_actor_hand_post_rate": _rate(
            play_source_left_hand_post_count, play_count
        ),
        "play_source_still_actor_hand_post_count": play_source_still_hand_post_count,
        "play_source_still_actor_hand_post_affected_game_count": len(
            play_source_still_hand_games
        ),
        "play_board_position_record_count": board_position_record_count,
        "play_board_position_record_rate": _rate(
            board_position_record_count, play_count
        ),
        "attack_record_count": attack_count,
        "attack_source_readiness_explicit_count": attack_readiness_explicit_count,
        "attack_source_readiness_explicit_rate": _rate(
            attack_readiness_explicit_count, attack_count
        ),
        "end_turn_record_count": end_turn_count,
        "end_turn_active_player_changed_count": end_turn_active_changed_count,
        "end_turn_active_player_changed_rate": _rate(
            end_turn_active_changed_count, end_turn_count
        ),
    }


def _aggregate_parsed(
    parsed: Sequence[ParsedReplay], parse_errors: Counter[str]
) -> dict[str, Any]:
    records = [record for game in parsed for record in game.records]
    decision_frames = [frame for game in parsed for frame in game.decision_frames]
    sides = Counter(str(record.value["actor_side"]) for record in records)
    kinds = Counter(str(record.value["action"]["kind"]) for record in records)
    decision_kinds = Counter(
        str(frame.value["selected_action"]["kind"]) for frame in decision_frames
    )
    modes = Counter(game.mode for game in parsed)
    results = Counter(game.result or "missing" for game in parsed)
    abstentions: Counter[str] = Counter(parse_errors)
    for game in parsed:
        abstentions.update(game.abstentions)
    decision_rejections: Counter[str] = Counter()
    decision_diagnostics: Counter[str] = Counter()
    for game in parsed:
        decision_rejections.update(game.decision_rejections)
        decision_diagnostics.update(game.decision_diagnostics)
    contract_abstentions = sum(
        count for reason, count in abstentions.items() if reason.startswith("behavior_contract:")
    )
    solver_state_abstentions = sum(
        count
        for reason, count in abstentions.items()
        if reason.startswith("solver_state_contract:")
    )
    decision_contract_rejections = sum(
        count
        for reason, count in decision_rejections.items()
        if reason.startswith("decision_frame_contract:")
    )
    options_frame_count = sum(game.options_frame_count for game in parsed)
    send_option_count = sum(game.send_option_count for game in parsed)
    decision_record_count = len(decision_frames)
    decision_rejection_count = sum(decision_rejections.values())
    local_behavior_count = sides.get("local", 0)
    legal_candidate_count = sum(
        len(frame.value["legal_candidates"]) for frame in decision_frames
    )
    duplicate_games = len(parsed) - len({game.public_digest_sha256 for game in parsed})
    metrics = {
        "parsed_game_count": len(parsed),
        "unique_public_game_count": len({game.public_digest_sha256 for game in parsed}),
        "duplicate_public_game_count": duplicate_games,
        "candidate_action_count": sum(game.candidate_count for game in parsed),
        "behavior_record_count": len(records),
        "behavior_eligible_record_count": sum(
            record.value["behavior_eligible"] is True for record in records
        ),
        "rl_training_eligible_record_count": sum(
            record.value["rl_training_eligible"] is True for record in records
        ),
        "options_frame_count": options_frame_count,
        "send_option_count": send_option_count,
        "decision_frame_record_count": decision_record_count,
        "decision_frame_game_count": len(
            {frame.game_id for frame in decision_frames}
        ),
        "decision_frame_legal_candidate_count": legal_candidate_count,
        "decision_frame_average_candidate_count": _rate(
            legal_candidate_count, decision_record_count
        ),
        "decision_frame_selected_action_kind_counts": dict(
            sorted(decision_kinds.items())
        ),
        "decision_frame_imitation_eligible_count": sum(
            frame.value["imitation_training_eligible"] is True
            for frame in decision_frames
        ),
        "decision_frame_rl_training_eligible_count": sum(
            frame.value["rl_training_eligible"] is True for frame in decision_frames
        ),
        "decision_frame_optimality_verified_count": sum(
            frame.value["optimality_verified"] is True for frame in decision_frames
        ),
        "decision_frame_rejection_count": decision_rejection_count,
        "decision_frame_contract_rejection_count": decision_contract_rejections,
        "decision_frame_rejection_reason_counts": dict(
            sorted(decision_rejections.items())
        ),
        "decision_frame_diagnostic_counts": dict(sorted(decision_diagnostics.items())),
        "decision_selection_accounted_count": (
            decision_record_count + decision_rejection_count
        ),
        "decision_selection_accounting_delta": (
            send_option_count - decision_record_count - decision_rejection_count
        ),
        "decision_frame_capture_rate": _rate(
            decision_record_count, send_option_count
        ),
        "decision_frame_local_behavior_coverage_rate": _rate(
            decision_record_count, local_behavior_count
        ),
        "actor_side_counts": dict(sorted(sides.items())),
        "action_kind_counts": dict(sorted(kinds.items())),
        "mode_game_counts": dict(sorted(modes.items())),
        "result_counts": dict(sorted(results.items())),
        "joined_result_game_count": sum(bool(game.result) for game in parsed),
        "behavior_contract_abstention_count": contract_abstentions,
        "solver_state_contract_abstention_count": solver_state_abstentions,
        "abstention_reason_counts": dict(sorted(abstentions.items())),
    }
    metrics.update(_transition_quality(records))
    metrics["candidate_action_retention_rate"] = _rate(
        len(records), metrics["candidate_action_count"]
    )
    metrics["solver_state_contract_acceptance_rate"] = _rate(
        len(records), len(records) + solver_state_abstentions
    )
    return metrics


def _quality_check(
    name: str, actual: int | float, operator: str, expected: int | float
) -> dict[str, Any]:
    if operator == "==":
        passed = actual == expected
    elif operator == "<=":
        passed = actual <= expected
    else:  # pragma: no cover - callers use only fixed operators.
        raise AssertionError(operator)
    return {
        "name": name,
        "actual": actual,
        "operator": operator,
        "expected": expected,
        "passed": passed,
    }


def _base_report(
    *,
    scans: Sequence[ReplayScan],
    selected: Sequence[ReplayScan],
    builds: set[str],
    modes: set[str],
    requested_build: str,
    parsed: Sequence[ParsedReplay],
    parse_errors: Counter[str],
) -> dict[str, Any]:
    metrics = _aggregate_parsed(parsed, parse_errors)
    transition_quality_checks = [
        _quality_check(
            "action_actor_active_pre_rate",
            metrics["action_actor_active_pre_rate"],
            "==",
            1.0,
        ),
        _quality_check(
            "play_source_in_actor_hand_pre_rate",
            metrics["play_source_in_actor_hand_pre_rate"],
            "==",
            1.0,
        ),
        _quality_check(
            "play_source_still_actor_hand_post_count",
            metrics["play_source_still_actor_hand_post_count"],
            "<=",
            0,
        ),
        _quality_check(
            "end_turn_active_player_changed_rate",
            metrics["end_turn_active_player_changed_rate"],
            "==",
            1.0,
        ),
    ]
    decision_frame_quality_checks = [
        _quality_check(
            "decision_selection_accounting_delta",
            metrics["decision_selection_accounting_delta"],
            "==",
            0,
        ),
        _quality_check(
            "decision_frame_contract_rejection_count",
            metrics["decision_frame_contract_rejection_count"],
            "==",
            0,
        ),
        _quality_check(
            "decision_frame_rl_training_eligible_count",
            metrics["decision_frame_rl_training_eligible_count"],
            "==",
            0,
        ),
        _quality_check(
            "decision_frame_optimality_verified_count",
            metrics["decision_frame_optimality_verified_count"],
            "==",
            0,
        ),
    ]
    passed = bool(selected) and metrics["behavior_record_count"] > 0
    passed = passed and metrics["rl_training_eligible_record_count"] == 0
    passed = passed and metrics["behavior_contract_abstention_count"] == 0
    passed = passed and all(item["passed"] for item in transition_quality_checks)
    passed = passed and all(item["passed"] for item in decision_frame_quality_checks)
    report = {
        "schema": REPLAY_IMPORT_SCHEMA_ID,
        "generated_at_utc": _utc_now_text(),
        "passed": passed,
        "ready_for_imitation_audit": bool(
            passed
            and metrics["actor_side_counts"].get("local", 0) > 0
            and metrics["actor_side_counts"].get("opponent", 0) > 0
            and metrics["joined_result_game_count"] > 0
        ),
        "ready_for_candidate_imitation_audit": bool(
            passed
            and metrics["decision_frame_record_count"] > 0
            and metrics["decision_frame_imitation_eligible_count"]
            == metrics["decision_frame_record_count"]
        ),
        "training_ready": False,
        "selection": {
            "requested_build": requested_build,
            "selected_builds": sorted(builds, key=int),
            "selected_modes": sorted(modes),
            "selected_archive_count": len(selected),
        },
        "inventory": _scan_inventory(scans),
        "metrics": metrics,
        "transition_quality_checks": transition_quality_checks,
        "decision_frame_quality_checks": decision_frame_quality_checks,
        "privacy": {
            "raw_archive_copied": False,
            "archive_filename_persisted": False,
            "player_name_persisted": False,
            "opponent_name_persisted": False,
            "game_account_id_persisted": False,
            "exact_archive_timestamp_persisted": False,
            "raw_log_sha256_persisted": False,
            "opponent_hidden_hand_identity_persisted": False,
            "game_id_basis": "privacy_safe_public_transcript_sha256",
            "ordering_clock": "deterministic_synthetic_utc",
        },
        "eligibility": {
            "behavior_eligible_meaning": "可用于行为模仿统计",
            "output_solver_state_contract_valid": True,
            "output_observed_transition_contract_valid": all(
                item["passed"] for item in transition_quality_checks
            ),
            "output_complete_decision_frame_contract_valid": all(
                item["passed"] for item in decision_frame_quality_checks
            ),
            "candidate_imitation_training_eligible": bool(
                metrics["decision_frame_record_count"] > 0
            ),
            "solver_evaluation_ready": False,
            "rl_training_eligible": False,
            "optimal_action_label": False,
            "online_policy_label": False,
            "outcome_used_as_action_optimality": False,
        },
        "caveats_zh": [
            "回放行为只证明当时实际发生了什么，不证明该动作最优。",
            "完整决策帧只保留可严格映射的本方主行动；抉择分支、交易和隐藏选项会失败关闭。",
            "候选集可用于模仿学习和离线排序评估，但始终不是最优动作或强化学习真值。",
            "终局只用于同局关联和离线评估，不会把行为动作升级为强化学习真值。",
            "默认按客户端 build 严格分组，避免把跨补丁行为直接用于当前环境。",
            "导入产物仍必须通过独立行为学习审计、三分割和先验门禁后才能用于排序。",
            "实际 ATTACK 只证明该来源当时至少可攻击一次；其他角色准备状态与完整卡牌效果仍未达到求解器评测合同。",
        ],
    }
    return report


def _scan_directory(directory: str | Path) -> list[ReplayScan]:
    return [scan_hdt_replay(path) for path in discover_hdt_replays(directory)]


def _parse_selected(
    selected: Sequence[ReplayScan],
) -> tuple[list[ParsedReplay], Counter[str]]:
    parsed: list[ParsedReplay] = []
    errors: Counter[str] = Counter()
    for scan in selected:
        try:
            parsed.append(parse_hdt_replay(scan.path))
        except ReplayImportError as exc:
            errors["parse_error:" + exc.code] += 1
    return parsed, errors


def audit_hdt_replays(
    directory: str | Path,
    *,
    requested_build: str = "latest",
    modes: Iterable[str] = ("standard", "arena"),
) -> dict[str, Any]:
    normalized_modes = {str(value).strip().lower() for value in modes if str(value).strip()}
    if not normalized_modes:
        raise ReplayImportError("selected_modes_empty")
    scans = _scan_directory(directory)
    selected, builds = _selection(
        scans,
        requested_build=requested_build,
        modes=normalized_modes,
    )
    parsed, errors = _parse_selected(selected)
    return _base_report(
        scans=scans,
        selected=selected,
        builds=builds,
        modes=normalized_modes,
        requested_build=requested_build,
        parsed=parsed,
        parse_errors=errors,
    )


def _jsonl_bytes(records: Sequence[BehaviorRecord | DecisionFrameRecord]) -> bytes:
    return b"".join(
        _canonical_bytes(record.to_dict()) + b"\n"
        for record in records
    )


def _write_bytes_atomic(path: Path, payload: bytes, *, replace: bool) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists() and not replace:
        raise ReplayImportError("output_exists", path.name)
    file_descriptor, temporary_name = tempfile.mkstemp(
        prefix=path.name + ".tmp-",
        dir=str(path.parent),
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(file_descriptor, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    except Exception:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
        raise


def _result_log_bytes(games: Sequence[ParsedReplay]) -> bytes:
    with tempfile.TemporaryDirectory(prefix="metacompanion-replay-results-") as directory:
        path = Path(directory) / RESULT_OUTPUT_FILENAME
        logger = JsonlTrainingLogger(path)
        for game in games:
            if game.result not in {"win", "loss", "tie"}:
                continue
            observation = Observation(
                kind="result",
                state_id=game.terminal_state_id,
                game_id=game.game_id,
                observed_at_utc="",
                result=game.result,
                metadata={
                    "trajectory_schema": TRAJECTORY_SCHEMA_ID,
                    "decision_id": game.terminal_state_id,
                    "completeness": "terminal_result",
                    "capture_contract": "terminal_result_v1",
                    "training_eligible": True,
                    "source_capture_contract": REPLAY_CAPTURE_CONTRACT,
                    "source": "hdt_replay_import_v1",
                },
            )
            outcome = logger.append_observation_with_ack(observation)
            if not outcome.logged:
                raise ReplayImportError("result_log_write_failed")
        return path.read_bytes() if path.is_file() else b""


def import_hdt_replays(
    directory: str | Path,
    output_directory: str | Path,
    *,
    requested_build: str = "latest",
    modes: Iterable[str] = ("standard", "arena"),
    card_defs_path: str | Path | None = None,
    replace: bool = False,
) -> dict[str, Any]:
    normalized_modes = {str(value).strip().lower() for value in modes if str(value).strip()}
    if not normalized_modes:
        raise ReplayImportError("selected_modes_empty")
    scans = _scan_directory(directory)
    selected, builds = _selection(
        scans,
        requested_build=requested_build,
        modes=normalized_modes,
    )
    parsed, errors = _parse_selected(selected)
    report = _base_report(
        scans=scans,
        selected=selected,
        builds=builds,
        modes=normalized_modes,
        requested_build=requested_build,
        parsed=parsed,
        parse_errors=errors,
    )
    if not report["passed"]:
        raise ReplayImportError("replay_audit_not_passed")

    unique: dict[str, ParsedReplay] = {}
    for game in parsed:
        existing = unique.get(game.public_digest_sha256)
        if existing is None:
            unique[game.public_digest_sha256] = game
        elif _canonical_bytes([item.to_dict() for item in existing.records]) != _canonical_bytes(
            [item.to_dict() for item in game.records]
        ):
            raise ReplayImportError("public_digest_conflict")
        elif _canonical_bytes(
            [item.to_dict() for item in existing.decision_frames]
        ) != _canonical_bytes([item.to_dict() for item in game.decision_frames]):
            raise ReplayImportError("public_digest_decision_frame_conflict")
    games = [unique[key] for key in sorted(unique)]
    behavior_records = [record for game in games for record in game.records]
    decision_frame_records = [
        record for game in games for record in game.decision_frames
    ]
    behavior_payload = _jsonl_bytes(behavior_records)
    decision_frame_payload = _jsonl_bytes(decision_frame_records)
    result_payload = _result_log_bytes(games)

    card_defs_summary: dict[str, Any] | None = None
    if card_defs_path is not None:
        public_states: list[Mapping[str, Any]] = []
        for record in behavior_records:
            public_states.append(record.value["pre_state"])
            post_state = record.value.get("post_state")
            if isinstance(post_state, Mapping):
                public_states.append(post_state)
        requested_public_cards = public_card_ids(public_states)
        try:
            card_defs = load_hdt_card_defs(
                card_defs_path,
                requested_card_ids=requested_public_cards,
                expected_builds={game.build for game in games},
            )
        except HdtCardDefsError as exc:
            raise ReplayImportError(exc.code, exc.detail) from exc
        card_defs_summary = card_defs.manifest_summary()

    output = Path(output_directory)
    behavior_path = output / BEHAVIOR_OUTPUT_FILENAME
    decision_frame_path = output / DECISION_FRAME_OUTPUT_FILENAME
    result_path = output / RESULT_OUTPUT_FILENAME
    manifest_path = output / MANIFEST_OUTPUT_FILENAME
    for path in (behavior_path, decision_frame_path, result_path, manifest_path):
        if path.exists() and not replace:
            raise ReplayImportError("output_exists", path.name)

    manifest = copy.deepcopy(report)
    manifest.update(
        {
            "outputs": {
                "behavior": {
                    "filename": BEHAVIOR_OUTPUT_FILENAME,
                    "bytes": len(behavior_payload),
                    "sha256": _sha256_bytes(behavior_payload),
                    "records": len(behavior_records),
                },
                "decision_frames": {
                    "filename": DECISION_FRAME_OUTPUT_FILENAME,
                    "bytes": len(decision_frame_payload),
                    "sha256": _sha256_bytes(decision_frame_payload),
                    "records": len(decision_frame_records),
                    "imitation_training_eligible": True,
                    "rl_training_eligible": False,
                    "optimal_action_label": False,
                },
                "terminal_results": {
                    "filename": RESULT_OUTPUT_FILENAME,
                    "bytes": len(result_payload),
                    "sha256": _sha256_bytes(result_payload),
                    "records": sum(bool(game.result) for game in games),
                },
            },
            "public_source_snapshot": {
                "schema": REPLAY_TRANSCRIPT_SCHEMA_ID,
                "game_count": len(games),
                "sha256": _sha256_bytes(
                    _canonical_bytes(
                        [
                            {
                                "public_digest_sha256": game.public_digest_sha256,
                                "build": game.build,
                                "mode": game.mode,
                                "behavior_records": len(game.records),
                                "decision_frame_records": len(game.decision_frames),
                                "has_result": bool(game.result),
                            }
                            for game in games
                        ]
                    )
                ),
            },
            "public_card_metadata_enrichment": {
                "enabled": card_defs_summary is not None,
                "contract": "same_build_public_card_defs_overlay_v1",
                "card_defs": card_defs_summary,
                "selected_replay_build_match_required": True,
                "public_card_ids_only": True,
                "hidden_opponent_hand_identity_enriched": False,
                "persisted_into_behavior_records": False,
                "persisted_into_decision_frames": False,
                "action_legality_evidence": False,
                "optimality_evidence": False,
                "evaluation_requires_explicit_same_hash_card_defs": True,
            },
        }
    )
    manifest_payload = json.dumps(
        manifest,
        ensure_ascii=False,
        sort_keys=True,
        indent=2,
    ).encode("utf-8") + b"\n"

    _write_bytes_atomic(behavior_path, behavior_payload, replace=replace)
    _write_bytes_atomic(
        decision_frame_path, decision_frame_payload, replace=replace
    )
    _write_bytes_atomic(result_path, result_payload, replace=replace)
    _write_bytes_atomic(manifest_path, manifest_payload, replace=replace)
    return manifest


def write_replay_audit_report(report: Mapping[str, Any], path: str | Path) -> None:
    payload = json.dumps(
        dict(report),
        ensure_ascii=False,
        sort_keys=True,
        indent=2,
    ).encode("utf-8") + b"\n"
    _write_bytes_atomic(Path(path), payload, replace=True)
