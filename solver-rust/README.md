# MetaCompanion Rust solver core

This crate is the fail-closed Rust search worker used behind MetaCompanion's
managed HDT adapter. HDT's plugin loader requires a managed .NET Framework
assembly whose plugin type implements `Hearthstone_Deck_Tracker.Plugins.IPlugin`,
so HDT still loads the C# adapter. This executable owns the performance-critical
simulation/search path as an authenticated localhost child process and never
becomes an in-process HDT plugin. Auto mode prefers this worker when a gated
binary is installed; Python remains a compatibility fallback and an offline
training/evaluation tool.

- `combat-v1` has a release floor of 7 exact `oracle-turn-v1` fixtures.
- `full` has a release floor of 40 fixtures. The current suite has 51: 7 exact
  `oracle-turnpair-v1` fixtures, 43 raw-HDT exact fixtures, and 1 raw-HDT
  scoped-lethal fixture.
- `visible-response-v1` has a release floor of 3 partial raw-HDT fixtures.
- Within its supported slice, the full profile models turn refresh, mana and
  attack resets, fatigue, a complete friendly turn, a legal worst response,
  integer minimax utility,
  scoped clean lethal, targeted point damage/healing, hero powers, and spell
  power. The visible-response path additionally models reviewed random-target
  effects as exact rational Chance branches and ranks them with visible
  expectiminimax; it stops the displayed route at a real random outcome and
  recomputes from the next HDT state.
- Unknown spells, weapons, hero powers, target contracts, and actionable effects
  are rejected instead of being invented. An otherwise unsupported minion may
  enter only the explicit vanilla-minion approximation path, with its entity
  dependency reported as partial coverage.
- `production_ready=true` means the versioned worker/API/parity contract passed;
  it does **not** claim complete Hearthstone rules, calibrated win probability,
  reinforcement-learning quality, or globally optimal match play.

The hot path uses owned game structures with shared immutable strings/effects,
cheap `Clone`, and an explicit BLAKE3 state key.  JSON serialization is confined
to process/API boundaries and is never used to build inner-loop transposition
keys.

## Build and verify

```powershell
cd solver-rust
cargo fmt --all -- --check
cargo test --locked --all-targets --all-features
cargo clippy --locked --all-targets --all-features -- -D warnings
cargo build --locked --release
```

The cross-language gate invokes the real binary once per fixture:

```powershell
python ..\solver\tools\rust_parity_gate.py `
  --profile combat-v1 `
  --binary .\target\release\metacompanion-solver.exe `
  --require-binary

python ..\solver\tools\rust_parity_gate.py `
  --profile full `
  --binary .\target\release\metacompanion-solver.exe `
  --require-binary

python ..\solver\launch_solver.py evaluate-visible-response `
  --fixtures ..\solver\fixtures\visible-response-v1.json `
  --binary .\target\release\metacompanion-solver.exe
```

The full gate sends raw Advisor/HDT snapshots to Rust.  Its independent Python
oracle compares gameplay semantics and ignores only an explicit allowlist of
transport/provenance fields.  Health, armor, resources, fatigue, card combat
state, attack counts, action lines, and minimax utility remain strict.

These focused commands are useful during development, but they are not a final
promotion. From the repository root, a releasable binary must be supplied
explicitly to the unified gate,
which enforces the `7 / 40 / 3` fixture floors and binds all reports and package
contents to one SHA-256:

```powershell
.\tools\Invoke-ReleaseGate.ps1 `
  -RustSolverBinaryPath .\solver-rust\target\release\metacompanion-solver.exe
```

Deploy only with `artifacts\release-gate\<timestamp>\package-root\Install-MetaCompanion.ps1`
from a run whose `release-gate.md` says `PASS`. The legacy
`Invoke-HdtClientSmoke.ps1` invokes the source-tree installer and must not be
used for this Rust rollout because it can replace the verified worker.

After installing that package and restarting HDT, verify the live process from
the repository root without driving the user's mouse:

```powershell
.\tools\Invoke-HdtAdvisorRuntimeSmoke.ps1 `
  -ExpectedPluginDll .\artifacts\release-gate\<timestamp>\package-root\MetaCompanion.dll `
  -ExpectedRustBinary .\artifacts\release-gate\<timestamp>\package-root\solver\metacompanion-solver.exe
```

The UI check is evidence only when the advisor panel is actually visible;
`not_exercised`/exit code 2 must not be treated as a pass.

For one canonical request, use:

```powershell
Get-Content envelope.json -Raw |
  .\target\release\metacompanion-solver.exe parity-one --profile combat-v1
```

The envelope schema is `metacompanion-rust-parity-request-v1`; the request
inside it is the existing API `1.0` `SolveRequest`.  `parity-jsonl` accepts the
same envelopes one per line.

## Authenticated worker

The executable accepts the same worker launch shape already reserved by the
C# process manager:

```powershell
$env:METACOMPANION_SOLVER_TOKEN = '<per-process token of at least 16 chars>'
.\target\release\metacompanion-solver.exe serve `
  --host 127.0.0.1 --port 17853 --data-dir .\data --no-training-log
```

Training-log path precedence is: `--no-training-log` (disabled), an explicit
`--training-log <path>`, `<data-dir>/training-v2.jsonl`, then
`training_log_path` from the JSON config. With none of those settings, Rust
keeps logging disabled. Relative config paths are resolved beside the config
file. The managed HDT launch supplies `--data-dir`, so its normal destination is
`AdvisorWorker/training-v2.jsonl`.

The same `--data-dir` derives an optional `AdvisorWorker/behavior-prior-v1.json`.
This is not raw behavior data or an RL policy: the Rust loader revalidates the
hash-bound imitation dataset metadata, game-level split counts, policy hash, held-out
quality checks, model count totals, and fixed safety flags. A valid model may only
reorder actions already generated as legal by the solver. It cannot add candidates,
override tactical scores, or change a completed exhaustive result. The file is
checked for atomic replacement before health and solve operations, so a gated update
hot-reloads without restarting HDT. Missing, malformed, not-ready, cross-patch, or
runtime-rejected models fall back to the deterministic base order; health reports the
state without exposing a local path.

Every route requires the bearer/session token.  `/v1/health` reports the narrow
capability truthfully; `/v1/cancel` supports cooperative cancellation by
`request_id` or `state_id`.  `/v1/solve` accepts both canonical requests and raw
HDT snapshots. It returns a strict C#-verified turn-pair recommendation or clean
scoped lethal when proved; otherwise, supported basic visible combat may return
an explicitly partial `visible-response-v1` ranking. Such partial results never
claim exact counterplay, safety, minimax proof, portfolio optimality, complete
Hearthstone rules, RL quality, or global optimality. Other unsupported positions
return a structured error. Cancellation is non-final and never fabricates
verified candidates.

`/v1/solve` and `/v1/observe` write privacy-sanitized
`advisor-training-log-v2` JSONL when logging is enabled. Game identifiers are
anonymized before disk, one game has a deterministic train/validation/test
split, credentials/deck identity/wall-clock fields are removed, and hidden
opponent zones retain location evidence only. A write error is soft for the API:
`logged=false` and dynamic health reports `training_log_healthy=false` without
exposing a filesystem path. Concurrent writers share one append lock, so every
line is complete JSON.

Terminal `kind=result` observations are content-addressed separately from ordinary
append-only action observations. Identical retries, including after a worker restart,
return `duplicate` with the same `result-<64 hex>` and do not append another line;
different terminal content for the same anonymous game fails closed with HTTP 409.

HDT GameEvents transition candidates remain
`partial_hdt_transition_candidate_v1`, are cross-checked against their raw
snapshot hashes/sequence IDs, and are forcibly stored with
`training_eligible=false`. Rust logging therefore makes current captures
auditable; it does not turn an unverified candidate into an exact or replayable
training action. Exact promotion remains an offline trajectory-auditor decision.

PowerLog-backed observations use the separate
`hdt_power_action_identity_v1` evidence tier. Rust preserves `sub_option`,
`board_position`, `option_id`, `frame_id`, Power start/end watermarks, and the
structured `choices` array without changing the simulator's canonical action
identity. This tier must prove a local pre-state source/target binding, a numeric
option/frame, an isolated choice-free input (`sub_option=-1`, empty choices),
and consistent pre/post snapshot evidence. It is still clamped to
`simulator_status=not_replayed` and `training_eligible=false`; only detached
offline replay may create a verified training corpus.
