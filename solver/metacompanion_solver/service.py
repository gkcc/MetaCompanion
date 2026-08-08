from __future__ import annotations

import threading
from dataclasses import dataclass
from typing import Any

from . import API_VERSION, PACKAGE_VERSION
from .behavior import (
    BEHAVIOR_SCHEMA_ID,
    BehaviorCorpus,
    BehaviorCorpusError,
    BehaviorRecord,
    BehaviorValidationError,
    behavior_path_for_training_log,
    create_behavior_record,
)
from .card_pool import OfficialCardPoolBundle
from .card_rules import StructuredCardRuleBundle, default_structured_card_rule_path
from .config import SolverConfig
from .errors import DuplicateRequestError, SchemaError
from .logging_store import JsonlTrainingLogger
from .schemas import Annotation, Observation, SearchResult, SolveRequest
from .search import PuctTurnSearcher, SearchLimits
from .simulator import scan_state_coverage


@dataclass
class _ActiveSolve:
    state_id: str
    cancel_event: threading.Event


class SolverService:
    def __init__(
        self,
        config: SolverConfig,
        searcher: PuctTurnSearcher | None = None,
        logger: JsonlTrainingLogger | None = None,
        behavior_corpus: BehaviorCorpus | None = None,
        official_card_pools: OfficialCardPoolBundle | None = None,
        structured_card_rules: StructuredCardRuleBundle | None = None,
    ) -> None:
        self.config = config
        self.searcher = searcher or PuctTurnSearcher()
        self.logger = logger or JsonlTrainingLogger(config.training_log_path)
        behavior_path = behavior_path_for_training_log(config.training_log_path)
        self.behavior_corpus = behavior_corpus or (
            None if behavior_path is None else BehaviorCorpus(behavior_path)
        )
        self._behavior_last_error = (
            ""
            if self.behavior_corpus is None
            else self.behavior_corpus.startup_error
        )
        self.official_card_pools = official_card_pools or OfficialCardPoolBundle.unavailable()
        self.structured_card_rules = structured_card_rules or StructuredCardRuleBundle.load_optional(
            default_structured_card_rule_path()
        )
        self._active: dict[str, _ActiveSolve] = {}
        self._lock = threading.Lock()

    def health(self) -> dict[str, Any]:
        with self._lock:
            active_count = len(self._active)
        return {
            "api_version": API_VERSION,
            "status": "ready",
            "backend": "python",
            "parity_profile": "full",
            "production_ready": True,
            "package_version": PACKAGE_VERSION,
            "worker_version": PACKAGE_VERSION,
            "model_version": "counterplay-turnpair-v1",
            "message": (
                "Counterplay turn-pair solver is ready; full Hearthstone card rules are not loaded."
            ),
            "is_ready": True,
            "active_solves": active_count,
            "training_log_enabled": self.logger.enabled,
            "training_log_healthy": self.logger.healthy,
            "behavior_log_enabled": self.behavior_corpus is not None,
            "behavior_log_healthy": not bool(self._behavior_last_error),
            "official_card_pools": self.official_card_pools.health(),
            "structured_card_rules": self.structured_card_rules.health(),
            "capabilities": {
                "generic_simulator": True,
                "visible_combat_v2": True,
                "counterplay_turnpair_v1": True,
                "hdt_visible_point_effects_v1": self.structured_card_rules.available,
                "opponent_visible_best_response": True,
                "full_hearthstone_rules": False,
                "cancellation": True,
                "progress_snapshots": True,
            },
        }

    def _limits(self, request: SolveRequest) -> SearchLimits:
        options = request.options
        time_budget = options.time_budget_ms or self.config.default_time_budget_ms
        iterations = options.max_iterations or self.config.default_max_iterations
        depth = options.max_depth or self.config.max_depth
        top_k = options.top_k or self.config.top_k
        return SearchLimits(
            time_budget_ms=max(25, min(time_budget, self.config.max_time_budget_ms)),
            max_iterations=max(1, min(iterations, self.config.max_iterations)),
            max_depth=max(2, min(depth, self.config.max_depth)),
            top_k=max(1, min(top_k, 10)),
            exploration_constant=self.config.exploration_constant,
        )

    def solve(self, request: SolveRequest) -> SearchResult:
        rule_assessment = self.structured_card_rules.apply(request.state)
        if not request.options.allow_approximate_effects and scan_state_coverage(request.state):
            raise SchemaError(
                "request.options.allow_approximate_effects",
                "state contains effects that the generic simulator cannot model exactly",
            )
        active = _ActiveSolve(request.state.state_id, threading.Event())
        with self._lock:
            if request.request_id in self._active:
                raise DuplicateRequestError(f"request_id {request.request_id!r} is already active")
            self._active[request.request_id] = active
        try:
            compatibility_annotations: tuple[Annotation, ...] = ()
            if request.hdt_root_candidates is not None:
                compatibility_annotations = (
                    Annotation(
                        code="approximate_hdt_root_candidates_ignored",
                        detail=(
                            "The Python compatibility solver validated the supplied HDT "
                            "candidate set but did not use it for ranking; recommendations "
                            "are best-effort and carry no exact, verified, safety, or "
                            "optimality claim."
                        ),
                        severity="warning",
                    ),
                )
            search_arguments = (
                request.request_id,
                request.state,
                self._limits(request),
                active.cancel_event,
            )
            result = (
                self.searcher.search(
                    *search_arguments,
                    approximation_annotations=compatibility_annotations,
                )
                if compatibility_annotations
                else self.searcher.search(*search_arguments)
            )
            if request.hdt_root_candidates is not None:
                result.coverage["hdt_root_candidates"] = {
                    "contract": request.hdt_root_candidates["contract"],
                    "candidate_set_complete": True,
                    "candidate_count": len(request.hdt_root_candidates["candidates"]),
                    "schema_validated": True,
                    "used_for_ranking": False,
                    "status": "ignored_by_python_compatibility_solver",
                    "exact": False,
                    "optimality_verified": False,
                }
            result.coverage["official_card_pool"] = self.official_card_pools.assess_state(
                request.state
            )
            result.coverage["structured_card_rules"] = rule_assessment
            self.logger.append_solve(request, result)
            return result
        finally:
            with self._lock:
                self._active.pop(request.request_id, None)

    def cancel(self, request_id: str | None = None, state_id: str | None = None) -> dict[str, Any]:
        if not request_id and not state_id:
            raise SchemaError("request", "request_id or state_id is required")
        cancelled: list[str] = []
        with self._lock:
            for active_request_id, solve in self._active.items():
                if (request_id and active_request_id == request_id) or (state_id and solve.state_id == state_id):
                    solve.cancel_event.set()
                    cancelled.append(active_request_id)
        return {
            "api_version": API_VERSION,
            "status": "cancellation_requested" if cancelled else "not_found",
            "cancelled_request_ids": cancelled,
        }

    def observe(self, observation: Observation) -> dict[str, Any]:
        outcome = self.logger.append_observation_with_ack(observation)
        response = {
            "api_version": API_VERSION,
            "status": "duplicate" if outcome.duplicate else "ok",
            "kind": observation.kind,
            "state_id": observation.state_id,
            "logged": outcome.logged,
        }
        if observation.kind == "result":
            response.update(
                {
                    "duplicate": outcome.duplicate,
                    "result_id": outcome.result_id,
                    "game_id": outcome.game_id,
                    "result": outcome.result,
                }
            )
        return response

    def append_behavior(self, payload: dict[str, Any]) -> dict[str, Any]:
        required = {
            "schema",
            "game_id",
            "behavior_sequence",
            "observed_at_utc",
            "actor_side",
            "actor_player_id",
            "actor_evidence",
            "identity_status",
            "visibility_status",
            "boundary_status",
            "source_event",
            "action",
            "pre_state",
            "post_state",
            "behavior_eligible",
            "rl_training_eligible",
        }
        supplied = {str(key) for key in payload}
        unknown = sorted(supplied - required)
        missing = sorted(required - supplied)
        if unknown:
            raise BehaviorValidationError("unknown_field:" + unknown[0], "behavior")
        if missing:
            raise BehaviorValidationError("missing_field:" + missing[0], "behavior")
        if payload["schema"] != BEHAVIOR_SCHEMA_ID:
            raise BehaviorValidationError("wrong_schema", "behavior.schema")
        record: BehaviorRecord = create_behavior_record(
            game_id=payload["game_id"],
            behavior_sequence=payload["behavior_sequence"],
            observed_at_utc=payload["observed_at_utc"],
            actor_side=payload["actor_side"],
            actor_player_id=payload["actor_player_id"],
            actor_evidence=payload["actor_evidence"],
            identity_status=payload["identity_status"],
            visibility_status=payload["visibility_status"],
            boundary_status=payload["boundary_status"],
            source_event=payload["source_event"],
            action=payload["action"],
            pre_state=payload["pre_state"],
            post_state=payload["post_state"],
            behavior_eligible=payload["behavior_eligible"],
            rl_training_eligible=payload["rl_training_eligible"],
        )
        if self.behavior_corpus is None:
            return {
                "api_version": API_VERSION,
                "status": "disabled",
                "logged": False,
                "duplicate": False,
                "behavior_id": record.behavior_id,
                "game_id": record.game_id,
                "behavior_sequence": record.behavior_sequence,
            }
        try:
            logged = self.behavior_corpus.append(record)
            self._behavior_last_error = ""
        except BehaviorCorpusError as exc:
            self._behavior_last_error = exc.code
            raise
        return {
            "api_version": API_VERSION,
            "status": "ok",
            "logged": logged,
            "duplicate": not logged,
            "behavior_id": record.behavior_id,
            "game_id": record.game_id,
            "behavior_sequence": record.behavior_sequence,
        }
