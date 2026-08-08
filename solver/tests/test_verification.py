from __future__ import annotations

import copy
import tempfile
import unittest
from pathlib import Path

import _path  # noqa: F401

from metacompanion_solver.trajectory import audit_trajectory_file
from metacompanion_solver.verification import (
    TrajectoryVerificationError,
    promote_trajectory_file,
    validate_trajectory_verification,
)


FIXTURES = Path(__file__).resolve().parents[1] / "fixtures"
TRAJECTORY = FIXTURES / "trajectory-readiness-v1.jsonl"
POLICY = FIXTURES / "trajectory-readiness-policy-v1.json"


class TrajectoryVerificationTests(unittest.TestCase):
    def test_promotion_writes_a_separate_hash_bound_allowlist(self) -> None:
        source_before = TRAJECTORY.read_bytes()
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "verified.jsonl"
            manifest_path = Path(directory) / "verified.manifest.json"
            manifest = promote_trajectory_file(
                TRAJECTORY,
                output,
                manifest_path,
                policy_path=POLICY,
            )
            verified_bytes = output.read_bytes()
            fresh_audit = audit_trajectory_file(output, policy_path=POLICY)
            validation = validate_trajectory_verification(
                manifest,
                dataset_bytes=verified_bytes,
                audit_report=fresh_audit,
            )

        self.assertEqual(source_before, TRAJECTORY.read_bytes())
        self.assertEqual("trajectory-verification-manifest-v1", manifest["schema"])
        self.assertEqual(2, validation["verified_transition_count"])
        self.assertEqual(
            fresh_audit["verified_transitions"], manifest["verified_transitions"]
        )

    def test_verification_rejects_a_changed_dataset(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "verified.jsonl"
            manifest_path = Path(directory) / "verified.manifest.json"
            manifest = promote_trajectory_file(
                TRAJECTORY,
                output,
                manifest_path,
                policy_path=POLICY,
            )
            verified_bytes = output.read_bytes()
            fresh_audit = audit_trajectory_file(output, policy_path=POLICY)

        with self.assertRaisesRegex(TrajectoryVerificationError, "dataset hash"):
            validate_trajectory_verification(
                manifest,
                dataset_bytes=verified_bytes + b"\n",
                audit_report=fresh_audit,
            )

    def test_verification_rejects_a_missing_transition(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "verified.jsonl"
            manifest_path = Path(directory) / "verified.manifest.json"
            manifest = promote_trajectory_file(
                TRAJECTORY,
                output,
                manifest_path,
                policy_path=POLICY,
            )
            verified_bytes = output.read_bytes()
            fresh_audit = audit_trajectory_file(output, policy_path=POLICY)

        stale = copy.deepcopy(manifest)
        stale["verified_transitions"] = stale["verified_transitions"][:-1]
        with self.assertRaisesRegex(TrajectoryVerificationError, "allowlist"):
            validate_trajectory_verification(
                stale,
                dataset_bytes=verified_bytes,
                audit_report=fresh_audit,
            )


if __name__ == "__main__":
    unittest.main()
