from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.behavior import BehaviorRecord, create_behavior_record
from metacompanion_solver.decision_frame import (
    DecisionFrameRecord,
    create_decision_frame_record,
)
from metacompanion_solver.decision_solver_evaluation import (
    DECISION_SOLVER_EVALUATION_SCHEMA_ID,
    DecisionSolverEvaluationError,
    _apply_historical_weapon_state_adapter,
    _audit_historical_weapon_state_adapter,
    evaluate_decision_solver_coverage_files,
    write_decision_solver_evaluation,
)
from metacompanion_solver.rust_worker_client import RustWorkerHttpError
from tools.observed_policy_fixture import generate_fixture


_BINARY_IDENTITY = {"bytes": 1234, "sha256": "a" * 64}
_ROOTS = [
    "attack:f-minion:o-hero",
    "attack:f-minion:o-minion",
    "end_turn::",
]


def _normalized_root_id(action_id: str) -> str:
    return "end_turn" if action_id == "end_turn::" else action_id


def _wire_root_id(action_id: str) -> str:
    return "end_turn::" if action_id == "end_turn" else action_id


def _candidate_root_id(candidate: dict) -> str:
    action = candidate["action"]
    base = (
        f"{action['kind']}:{action.get('source_entity_id', '')}:"
        f"{action.get('target_entity_id', '')}"
    )
    position = int(action.get("board_position") or 0)
    action_id = f"{base}:position={position}" if position > 0 else base
    return _normalized_root_id(action_id)


def _response(request, roots=None, *, evaluated_roots=None, exact=True, status=None):
    independent_ids = {
        _normalized_root_id(item) for item in (_ROOTS if roots is None else roots)
    }
    hdt_binding = request["hdt_root_candidates"]
    hdt_ids = {
        _candidate_root_id(candidate) for candidate in hdt_binding["candidates"]
    }
    evaluated_ids = {
        _normalized_root_id(item)
        for item in (hdt_ids if evaluated_roots is None else evaluated_roots)
    }
    matched_ids = independent_ids & hdt_ids
    counterplay = {
        "search_complete": bool(exact),
        "root_action_coverage_complete": bool(exact),
        "portfolio_optimality_proven": bool(exact),
        "legal_first_action_count": len(hdt_ids),
        "legal_first_action_ids": sorted(hdt_ids),
        "generated_first_action_count": len(evaluated_ids),
        "generated_first_action_ids": sorted(evaluated_ids),
    }
    recommendations = [
        {
            "actions": [{"action_id": _wire_root_id(action_id)}],
            "verified_portfolio_regret": (
                0 if exact and index < 2 else (2 if exact else None)
            ),
        }
        for index, action_id in enumerate(sorted(evaluated_ids))
    ]
    return {
        "request_id": request["request_id"],
        "state_id": request["state"]["state_id"],
        "status": status or ("ok" if exact else "partial"),
        "recommendations": recommendations,
        "coverage": {
            "exact": exact,
            "exact_scope": "visible_generic_v2" if exact else "visible-response-v1",
            "independent_generated_root_coverage": {
                "contract": "solver_independent_root_generation_v1",
                "available": True,
                "generated_count": len(independent_ids),
                "generated_action_ids": sorted(independent_ids),
                "matched_hdt_count": len(matched_ids),
                "matched_hdt_action_ids": sorted(matched_ids),
                "hdt_candidate_count": len(hdt_ids),
                "hdt_recall": len(matched_ids) / len(hdt_ids),
                "exact_match": independent_ids == hdt_ids,
                "false_exact": False,
                "live_policy_eligible": False,
                "rl_training_eligible": False,
                "global_optimality_verified": False,
            },
            "hdt_supplied_root_portfolio_coverage": {
                "contract": "hdt_complete_main_action_options_v1",
                "available": True,
                "state_bound": True,
                "frame_id": hdt_binding["frame_id"],
                "collector_epoch": hdt_binding["collector_epoch"],
                "candidate_set_complete": True,
                "candidate_count": len(hdt_ids),
                "legal_action_ids": sorted(hdt_ids),
                "evaluated_count": len(evaluated_ids),
                "evaluated_action_ids": sorted(evaluated_ids),
                "evaluated_coverage": len(evaluated_ids) / len(hdt_ids),
                "effect_simulation_complete": False,
                "root_legality_source": "hdt_debug_print_options",
                "hidden_response_generation_allowed": False,
                "live_policy_eligible": False,
                "rl_training_eligible": False,
                "global_optimality_verified": False,
            },
            "details": {"counterplay": copy.deepcopy(counterplay)},
            "counterplay": counterplay,
        },
    }


def _write_card_defs(path: Path, build: str = "246003") -> None:
    path.write_text(
        "\n".join(
            [
                '<?xml version="1.0" encoding="utf-8"?>',
                f'<CardDefs build="{build}">',
                '  <Entity CardID="F_MINION" ID="1">',
                '    <Tag name="CARDNAME" type="LocString"><enUS>Friendly Minion</enUS></Tag>',
                '    <Tag name="CARDTEXT" type="LocString"><enUS>Public rules text.</enUS></Tag>',
                '    <Tag name="CARDTYPE" type="Int" value="4"/>',
                "  </Entity>",
                "</CardDefs>",
                "",
            ]
        ),
        encoding="utf-8",
    )


def _rewrite_fixture_build(root: Path, build: str) -> tuple[Path, Path]:
    behavior_path = root / "behavior-v1.jsonl"
    frame_path = root / "advisor-decision-frame-v1.jsonl"
    original_behavior = BehaviorRecord.from_dict(
        json.loads(behavior_path.read_text(encoding="utf-8").splitlines()[0])
    )
    behavior_value = original_behavior.value
    pre_state = copy.deepcopy(behavior_value["pre_state"])
    post_state = copy.deepcopy(behavior_value["post_state"])
    pre_state["patch"] = build
    post_state["patch"] = build
    behavior = create_behavior_record(
        game_id=behavior_value["game_id"],
        behavior_sequence=behavior_value["behavior_sequence"],
        observed_at_utc=behavior_value["observed_at_utc"],
        actor_side=behavior_value["actor_side"],
        actor_player_id=behavior_value["actor_player_id"],
        actor_evidence=behavior_value["actor_evidence"],
        identity_status=behavior_value["identity_status"],
        visibility_status=behavior_value["visibility_status"],
        boundary_status=behavior_value["boundary_status"],
        source_event=behavior_value["source_event"],
        action=behavior_value["action"],
        pre_state=pre_state,
        post_state=post_state,
    )
    original_frame = DecisionFrameRecord.from_dict(
        json.loads(frame_path.read_text(encoding="utf-8").splitlines()[0])
    ).value
    frame = create_decision_frame_record(
        game_id=behavior.game_id,
        decision_sequence=1,
        observed_at_utc=original_frame["observed_at_utc"],
        client_build=build,
        mode=original_frame["mode"],
        selected_behavior_id=behavior.behavior_id,
        hdt_frame_id=original_frame["hdt_frame_id"],
        pre_state=pre_state,
        post_state=post_state,
        selected_action=original_frame["selected_action"],
        legal_candidates=[
            {
                "option_id": candidate["option_id"],
                "action": candidate["action"],
                "target_evidence": candidate["target_evidence"],
                "position_evidence": candidate["position_evidence"],
            }
            for candidate in original_frame["legal_candidates"]
        ],
    )
    behavior_path.write_text(
        json.dumps(behavior.to_dict(), ensure_ascii=False, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    frame_path.write_text(
        json.dumps(frame.to_dict(), ensure_ascii=False, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    return frame_path, behavior_path


class DecisionSolverEvaluationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        generate_fixture(self.root)
        self.frames = self.root / "advisor-decision-frame-v1.jsonl"
        self.behavior = self.root / "behavior-v1.jsonl"

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def evaluate(self, solve, **overrides):
        return evaluate_decision_solver_coverage_files(
            self.frames,
            self.behavior,
            solve,
            binary_identity=_BINARY_IDENTITY,
            max_frames=overrides.pop("max_frames", 0),
            time_budget_ms=overrides.pop("time_budget_ms", 100),
            max_iterations=overrides.pop("max_iterations", 10_000),
            max_depth=overrides.pop("max_depth", 8),
            top_k=overrides.pop("top_k", 10),
            worker_capabilities={"root_action_portfolio_v1": True},
            **overrides,
        )

    def test_exact_full_hdt_alignment_creates_only_solver_scope_evidence(self) -> None:
        requests = []

        def solve(request):
            requests.append(copy.deepcopy(request))
            return _response(request)

        report = self.evaluate(solve)
        self.assertTrue(report["passed"], report)
        self.assertEqual(DECISION_SOLVER_EVALUATION_SCHEMA_ID, report["schema"])
        self.assertEqual("AUDITED", report["status"])
        self.assertEqual(3, report["metrics"]["sampled_frame_count"])
        self.assertEqual(9, report["metrics"]["hdt_candidate_count"])
        self.assertEqual(1.0, report["metrics"]["hdt_candidate_recall"])
        self.assertEqual(3, report["metrics"]["complete_candidate_set_match_count"])
        self.assertEqual(
            9, report["metrics"]["hdt_supplied_evaluated_count"]
        )
        self.assertEqual(0, report["metrics"]["hdt_supplied_omitted_count"])
        self.assertEqual(
            3,
            report["metrics"][
                "hdt_supplied_root_portfolio_fully_modeled_frame_count"
            ],
        )
        self.assertEqual(0, report["metrics"]["false_exact_count"])
        self.assertEqual(3, report["metrics"]["solver_scope_verified_frame_count"])
        self.assertEqual(3, report["metrics"]["verified_multi_alternative_frame_count"])
        self.assertEqual(3, report["metrics"]["verified_cooptimal_frame_count"])
        self.assertEqual(3, report["metrics"]["observed_choice_top1_agreement_count"])
        self.assertFalse(report["observed_choice_used_as_optimality_label"])
        self.assertFalse(report["counterfactual_dataset_written"])
        self.assertFalse(report["live_policy_eligible"])
        self.assertFalse(report["rl_training_eligible"])
        self.assertFalse(report["global_optimality_verified"])
        self.assertTrue(report["privacy_contract_passed"])
        self.assertEqual(3, len(requests))
        for request in requests:
            supplied = request["hdt_root_candidates"]
            self.assertEqual(
                "hdt_complete_main_action_options_v1", supplied["contract"]
            )
            self.assertEqual(request["state"]["state_id"], supplied["state_id"])
            self.assertEqual(3, len(supplied["candidates"]))
            self.assertNotIn("candidate_id", supplied["candidates"][0])
        serialized = json.dumps(report, ensure_ascii=False).lower()
        for forbidden in (
            "anon-",
            "decision-frame-",
            "behavior-",
            "state_id",
            "entity_id",
            "request_id",
            str(self.root).lower(),
        ):
            self.assertNotIn(forbidden, serialized)

    def test_exact_claim_with_missing_hdt_root_is_rejected_as_false_exact(self) -> None:
        report = self.evaluate(
            lambda request: _response(
                request,
                roots=["attack:f-minion:o-hero", "end_turn::"],
                exact=True,
            )
        )
        self.assertFalse(report["passed"], report)
        self.assertEqual("REVIEW_REQUIRED", report["status"])
        self.assertEqual(3, report["metrics"]["false_exact_count"])
        self.assertEqual(
            3,
            report["metrics"]["false_exact_reason_counts"][
                "exact_hdt_candidate_mismatch"
            ],
        )
        self.assertEqual(0, report["metrics"]["solver_scope_verified_frame_count"])
        self.assertEqual("F_MINION", report["top_uncovered_public_cards"][0]["card_id"])

    def test_exact_claim_with_unevaluated_hdt_root_is_rejected_as_false_exact(self) -> None:
        report = self.evaluate(
            lambda request: _response(
                request,
                roots=_ROOTS,
                evaluated_roots=["attack:f-minion:o-hero", "end_turn::"],
                exact=True,
            )
        )

        self.assertFalse(report["passed"], report)
        self.assertEqual(3, report["metrics"]["false_exact_count"])
        self.assertEqual(
            3,
            report["metrics"]["false_exact_reason_counts"][
                "exact_hdt_portfolio_not_fully_evaluated"
            ],
        )
        self.assertEqual(0, report["metrics"]["solver_scope_verified_frame_count"])

    def test_honest_partial_and_unsupported_results_remain_useful_diagnostics(self) -> None:
        partial = self.evaluate(
            lambda request: _response(
                request,
                roots=["end_turn::"],
                evaluated_roots=["end_turn::"],
                exact=False,
                status="partial",
            )
        )
        self.assertTrue(partial["passed"], partial)
        self.assertEqual(3, partial["metrics"]["frame_outcome_counts"]["partial"])
        self.assertEqual(0, partial["metrics"]["false_exact_count"])
        self.assertEqual(0, partial["metrics"]["solver_scope_verified_frame_count"])
        self.assertGreater(partial["metrics"]["missing_candidate_count"], 0)
        self.assertEqual(3, partial["metrics"]["hdt_supplied_evaluated_count"])
        self.assertEqual(6, partial["metrics"]["hdt_supplied_omitted_count"])
        self.assertEqual(
            0,
            partial["metrics"][
                "hdt_supplied_root_portfolio_fully_modeled_frame_count"
            ],
        )
        self.assertEqual(
            6,
            partial["metrics"]["hdt_supplied_omitted_action_kind_counts"]["attack"],
        )
        self.assertEqual(
            "F_MINION",
            partial["top_hdt_supplied_omitted_public_cards"][0]["card_id"],
        )

        def unsupported(_request):
            raise RustWorkerHttpError(422, "unsupported_scope")

        unsupported_report = self.evaluate(unsupported)
        self.assertTrue(unsupported_report["passed"], unsupported_report)
        self.assertEqual(
            3,
            unsupported_report["metrics"]["frame_outcome_counts"]["unsupported"],
        )
        self.assertEqual(
            3,
            unsupported_report["metrics"]["solver_error_code_counts"][
                "unsupported_scope"
            ],
        )
        self.assertEqual(
            0, unsupported_report["metrics"]["hdt_supplied_evaluated_count"]
        )
        self.assertEqual(
            9, unsupported_report["metrics"]["hdt_supplied_omitted_count"]
        )

    def test_structured_rule_diagnostics_are_aggregated_without_entity_ids(self) -> None:
        def solve(request):
            response = _response(request)
            response["coverage"]["structured_card_rules"] = {
                "available": True,
                "ruleset_id": "fixture-rules-v1",
                "matched": [
                    {
                        "entity_id": "private-match-entity",
                        "card_id": "F_MINION",
                        "rule_id": "fixture-match-v1",
                        "text_sha256": "b" * 64,
                    }
                ],
                "mismatches": [
                    {
                        "entity_id": "private-mismatch-entity",
                        "card_id": "F_MINION",
                        "rule_id": "fixture-mismatch-v1",
                        "reason": "english_text_sha256_mismatch",
                        "actual_text_sha256": "c" * 64,
                    }
                ],
            }
            return response

        report = self.evaluate(solve)
        metrics = report["metrics"]
        self.assertEqual(3, metrics["structured_rule_assessment_available_frame_count"])
        self.assertEqual(0, metrics["structured_rule_assessment_unavailable_frame_count"])
        self.assertEqual(0, metrics["structured_rule_assessment_invalid_frame_count"])
        self.assertEqual(3, metrics["structured_rule_match_count"])
        self.assertEqual(3, metrics["structured_rule_mismatch_count"])
        self.assertEqual(
            {
                "card_id": "F_MINION",
                "rule_id": "fixture-match-v1",
                "match_count": 3,
                "frame_count": 3,
            },
            report["top_structured_rule_matches"][0],
        )
        mismatch = report["top_structured_rule_mismatches"][0]
        self.assertEqual("english_text_sha256_mismatch", mismatch["reason"])
        self.assertEqual("c" * 64, mismatch["actual_text_sha256"])
        serialized = json.dumps(report, ensure_ascii=False)
        self.assertNotIn("private-match-entity", serialized)
        self.assertNotIn("private-mismatch-entity", serialized)
        self.assertTrue(report["privacy_contract_passed"])

    def test_same_build_card_defs_overlay_is_public_hash_bound_and_non_mutating(self) -> None:
        frames, behavior = _rewrite_fixture_build(self.root, "246003")
        card_defs = self.root / "CardDefs.base.xml"
        _write_card_defs(card_defs)
        requests = []

        def solve(request):
            requests.append(copy.deepcopy(request))
            return _response(request)

        report = evaluate_decision_solver_coverage_files(
            frames,
            behavior,
            solve,
            binary_identity=_BINARY_IDENTITY,
            max_frames=0,
            time_budget_ms=100,
            max_iterations=10_000,
            max_depth=8,
            top_k=10,
            worker_capabilities={"root_action_portfolio_v1": True},
            card_defs_path=card_defs,
        )

        self.assertTrue(report["passed"], report)
        overlay = report["public_card_defs_overlay"]
        self.assertTrue(overlay["enabled"])
        self.assertEqual("246003", overlay["card_defs"]["build"])
        self.assertEqual(64, len(overlay["card_defs"]["sha256"]))
        self.assertTrue(overlay["decision_frame_payload_unchanged"])
        self.assertFalse(overlay["action_legality_evidence"])
        self.assertEqual(1, len(requests))
        public_minion = requests[0]["state"]["friendly"]["board"][0]
        self.assertEqual("Public rules text.", public_minion["english_text"])
        stored_frame = json.loads(frames.read_text(encoding="utf-8"))
        self.assertNotIn(
            "english_text", stored_frame["pre_state"]["friendly"]["board"][0]
        )
        self.assertNotIn(str(card_defs), json.dumps(report, ensure_ascii=False))

    def test_card_defs_build_mismatch_is_rejected_before_solve(self) -> None:
        frames, behavior = _rewrite_fixture_build(self.root, "246003")
        card_defs = self.root / "CardDefs.base.xml"
        _write_card_defs(card_defs, build="247416")
        with self.assertRaisesRegex(DecisionSolverEvaluationError, "CardDefs"):
            evaluate_decision_solver_coverage_files(
                frames,
                behavior,
                lambda _request: self.fail("solve must not run"),
                binary_identity=_BINARY_IDENTITY,
                card_defs_path=card_defs,
            )

    def test_sampling_is_deterministic_and_report_writer_is_atomic(self) -> None:
        first = self.evaluate(_response, max_frames=2)
        second = self.evaluate(_response, max_frames=2)
        self.assertEqual(first["sample"], second["sample"])
        output = self.root / "coverage.json"
        write_decision_solver_evaluation(first, output)
        loaded = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual(first, loaded)
        self.assertFalse(output.with_suffix(".json.tmp").exists())

    def test_invalid_limits_fail_before_any_solve(self) -> None:
        with self.assertRaisesRegex(DecisionSolverEvaluationError, "max_frames"):
            self.evaluate(_response, max_frames=-1)
        with self.assertRaisesRegex(DecisionSolverEvaluationError, "Rust"):
            self.evaluate(_response, top_k=256)

    def test_historical_weapon_mapping_requires_repeated_zero_conflict_transitions(self) -> None:
        def transition(index: int, *, post_current: int = 1) -> dict:
            role = "friendly" if index % 2 == 0 else "opponent"
            actor_side = "local" if role == "friendly" else "opponent"
            other = "opponent" if role == "friendly" else "friendly"
            hero = {
                "entity_id": f"hero-{index}",
                "card_type": "HERO",
                "can_attack": True,
                "attacks_remaining": 1,
            }
            weapon = {
                "entity_id": f"weapon-{index}",
                "card_type": "WEAPON",
                "health": 3,
                "current_health": 2,
            }
            post_weapon = dict(weapon, current_health=post_current)
            empty_player = {"hero": {"entity_id": f"other-{index}"}, "weapon": None}
            pre = {
                role: {"hero": hero, "weapon": weapon},
                other: copy.deepcopy(empty_player),
            }
            post = {
                role: {"hero": copy.deepcopy(hero), "weapon": post_weapon},
                other: copy.deepcopy(empty_player),
            }
            return {
                "actor_side": actor_side,
                "behavior_eligible": True,
                "boundary_status": "isolated",
                "action": {
                    "kind": "attack",
                    "source_entity_id": hero["entity_id"],
                },
                "pre_state": pre,
                "post_state": post,
            }

        evidence = _audit_historical_weapon_state_adapter(
            [transition(index) for index in range(32)]
        )
        self.assertTrue(evidence["enabled"], evidence)
        self.assertEqual(
            32,
            evidence["evidence"]["same_weapon_decrement_by_one_count"],
        )
        self.assertEqual(16, evidence["evidence"]["local_attack_count"])
        self.assertEqual(16, evidence["evidence"]["opponent_attack_count"])
        self.assertFalse(
            evidence["attack_count_evidence"]["exact_multi_attack_quota_available"]
        )

        conflicted = [transition(index) for index in range(31)]
        conflicted.append(transition(31, post_current=2))
        rejected = _audit_historical_weapon_state_adapter(conflicted)
        self.assertFalse(rejected["enabled"], rejected)
        self.assertEqual(
            1, rejected["evidence"]["conflicting_attack_transition_count"]
        )

    def test_historical_weapon_mapping_is_narrow_and_preserves_explicit_fields(self) -> None:
        state = {
            "friendly": {
                "weapon": {
                    "card_type": "WEAPON",
                    "health": 4,
                    "current_health": 2,
                }
            },
            "opponent": {
                "weapon": {
                    "card_type": "WEAPON",
                    "health": 5,
                    "current_health": 4,
                    "durability": 7,
                    "current_durability": 6,
                }
            },
        }
        self.assertEqual(
            1, _apply_historical_weapon_state_adapter(state, enabled=True)
        )
        self.assertEqual(4, state["friendly"]["weapon"]["durability"])
        self.assertEqual(2, state["friendly"]["weapon"]["current_durability"])
        self.assertEqual(7, state["opponent"]["weapon"]["durability"])
        self.assertEqual(6, state["opponent"]["weapon"]["current_durability"])


if __name__ == "__main__":
    unittest.main()
