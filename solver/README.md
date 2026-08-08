# Meta Companion local solver MVP

This directory contains a runnable, dependency-free Python MVP for local turn-line
advice. It accepts a public HDT snapshot, enumerates actions covered by its generic
rules, and performs a time-bounded PUCT/MCTS-style search for up to three complete
current-turn lines.

It is **not** a complete Hearthstone engine, a trained reinforcement-learning policy,
or evidence of globally optimal play. A currently actionable unknown card text,
random outcome, Discover, Choose One, or other unstructured mechanic makes ranked
advice fail closed and abstain instead of treating the action as blank. The narrow
exception is a clean terminal lethal that uses no unsupported action; only that proven
line is returned. The service never sends input to a remote endpoint and never
controls the game or mouse.

## Start the service

Python 3.10 or newer is required. No runtime packages are required.

```powershell
$env:METACOMPANION_SOLVER_TOKEN = "a-random-per-session-token-of-at-least-16-characters"
$env:METACOMPANION_SOLVER_PORT = "17853"
$env:METACOMPANION_SOLVER_DATA_DIR = "$env:APPDATA\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker"
python .\solver\launch_solver.py serve --config .\solver\config.default.json
```

The equivalent installed entrypoint is:

```powershell
cd .\solver
python -m pip install .
metacompanion-solver serve
```

`run_solver.ps1` is a source-tree launcher. The process emits one JSON `ready` line
after the socket is bound. If the caller supplied a token, that line does not repeat
it. If no token was supplied, a generated token appears once so a manual caller can
connect.

Runtime environment variables and equivalent flags are:

| Environment variable | Flag | Meaning |
|---|---|---|
| `METACOMPANION_SOLVER_TOKEN` | `--session-token` | Per-process API token. |
| `METACOMPANION_SOLVER_HOST` | `--host` | Must be exactly `127.0.0.1`. |
| `METACOMPANION_SOLVER_PORT` | `--port` | Loopback TCP port; `0` selects an ephemeral port. |
| `METACOMPANION_SOLVER_DATA_DIR` | `--data-dir` | Stores `training-v2.jsonl`; also probes `AdvisorData/Arena/latest` and legacy `AdvisorData/latest`. |
| `METACOMPANION_SOLVER_CONFIG` | `--config` | JSON config path. |
| `METACOMPANION_SOLVER_NO_TRAINING_LOG=1` | `--no-training-log` | Disable all training JSONL writes. |

Explicit flags override environment values and config. `--training-log PATH` selects
an exact log file, while `training_log_path: null` also disables logging. Health
reports the effective setting as `training_log_enabled`.
The versioned default deliberately leaves a pre-v2 `training.jsonl` untouched, so
new `advisor-training-log-v2` records never append to a legacy corpus.
Failed local writes do not fail a solve/observation request; `logged` becomes `false`
and health reports `training_log_healthy: false`.

## HTTP contract

All endpoints are versioned, bind only to `127.0.0.1`, and require either
`Authorization: Bearer <token>` or `X-Advisor-Token: <token>`.

### `GET /v1/health`

Returns readiness, API/worker/model versions, active solve count, conservative
capability flags, `training_log_enabled`, official-card-pool status, and structured
card-rule status. A correctly packaged current worker reports
`capabilities.hdt_visible_point_effects_v1: true` and reports rule/card-ID counts plus
the matching contract under `structured_card_rules`.

### `POST /v1/solve`

The call is synchronous. The threaded server still accepts `/v1/cancel` while a solve
is running.

```json
{
  "api_version": "1.0",
  "request_id": "turn-42-sequence-3",
  "state": {
    "state_id": "state-hash-or-sequence",
    "turn": 8,
    "active_player_id": "1",
    "perspective_player_id": "1",
    "friendly": {},
    "opponent": {}
  },
  "options": {
    "time_budget_ms": 2500,
    "max_iterations": 5000,
    "max_depth": 24,
    "top_k": 3
  }
}
```

The native typed schema is defined in `metacompanion_solver/schemas.py`. The parser
also auto-detects the plugin's HDT snapshot schema (`schema_version`, `turn_number`,
`player`, `opponent`, entity lists, resources, and data-gap fields). HDT option names
`time_budget_milliseconds`, `initial_budget_milliseconds`, `max_recommendations`,
`search_seed`, `allow_approximate_effects`, and `environment_version` are accepted.

Each recommendation contains:

- a stable `line_id`, rank, visit count, uncalibrated heuristic state value,
  heuristic interval/search stability, and a complete legal modeled action list;
- optional `is_proven_lethal`, `proof_kind`, and `proof_scope` fields when the
  deterministic visible-combat planner proves a line inside its declared model;
- `summary`, `risks`, and `approximate_effects` for the overlay;
- both solver-native fields and the HDT bridge's snake-case wire fields.

The top-level response includes `status` (`ok`, `partial`, or `cancelled`), elapsed
time, iterations, progress snapshots, model/environment versions, and coverage. Each
recommendation uses `score_kind: counterplay_tactical_state_value`. The legacy 0..1
`expected_win_probability`/`worst_case_score` values are monotonic mappings of the
tactical evaluator, not calibrated match win rates. `minimax_value` is the auditable
worst-response tactical utility, while `is_response_verified`,
`is_safe_after_response`, `response_scope`, and `opponent_response` state exactly what
visible response search was checked.

### `POST /v1/cancel`

```json
{
  "api_version": "1.0",
  "request_id": "turn-42-sequence-3",
  "state_id": "state-hash-or-sequence"
}
```

At least one ID is required. Matching active searches receive a cancellation event;
the in-flight solve returns its best available lines with `status: "cancelled"`.

### `POST /v1/observe`

This endpoint appends authenticated local observations. The HDT bridge waits for two
equal, stable detached captures and records a self-contained **unverified candidate**.
The `pre_state` and `post_state` objects below are abbreviated; the wire request carries
the full public snapshots:

```json
{
  "api_version": "1.0",
  "kind": "action",
  "state_id": "state-42",
  "game_id": "private-per-game-alias",
  "observed_at_utc": "2026-07-29T12:34:56Z",
  "action": {
    "kind": "play_card",
    "source_entity_id": 64,
    "target_entity_id": "",
    "card_id": "CARD_ID"
  },
  "pre_state": {"state_id": "state-42"},
  "post_state": {"state_id": "state-43"},
  "metadata": {
    "trajectory_schema": "trajectory-readiness-v1",
    "decision_id": "state-42",
    "action_sequence": 1,
    "pre_state_id": "state-42",
    "post_state_id": "state-43",
    "pre_snapshot_sequence": 12,
    "post_snapshot_sequence": 14,
    "capture_contract": "partial_hdt_transition_candidate_v1",
    "transition_status": "post_state_candidate_unverified",
    "transition_verification": "producer_candidate_unverified",
    "boundary_status": "isolated",
    "intervening_action_count": 0,
    "source_entity_resolution": "unique_card_id_match",
    "target_entity_resolution": "not_observed_by_hdt_gameevents",
    "completeness": "partial_hdt_gameevents_v1",
    "training_eligible": false
  }
}
```

For a result observation, use `kind: "result"` and `result` equal to `win`, `loss`,
`tie`, or `unknown`; trusted terminal results use `completeness: "terminal_result"`
and `training_eligible: true`. Ordinary action observations remain append-only. Terminal
results are content-addressed and restart-safe: an identical retry returns `duplicate`
without adding a second JSONL line, while different terminal content for the same
anonymous game returns HTTP 409. Example responses are:

```json
{"api_version":"1.0","status":"ok","kind":"action","state_id":"state-42","logged":true}
{"api_version":"1.0","status":"duplicate","kind":"result","state_id":"state-99","logged":false,"duplicate":true,"result_id":"result-<64 hex>","game_id":"anon-<16 hex>","result":"win"}
```

`logged` becomes `false` when training logging is disabled. A game receives one fixed
private alias in the C# extractor, and the logger hashes that alias to an idempotent
`anon-<16 hex>` key before disk. Game/match IDs, account IDs, battle tags,
player/opponent names, credentials, and exact wall-clock timestamps are removed or
anonymized. Hidden opponent hand/deck/set-aside/secret entities are scrubbed again at
schema and log boundaries. No browser password, cookie, or login state is read.

The logger independently sanitizes both snapshots and recomputes their canonical
SHA-256 hashes. Raw HDT snapshot hashes are provenance only. Ambiguous duplicate
CardIDs, overlapping callbacks, unstable captures, missing snapshots, and hash or
sequence mismatches remain partial evidence. A candidate is never promoted merely
because it has a post-state or because a producer changes an eligibility flag.

Offline observation training is fail-closed. An exact action must use
`complete_action_trace_v1`, `capture_contract: trajectory-readiness-v1`,
`transition_status: replayable_exact`, positive contiguous `action_sequence`, explicit
pre/post state IDs, and exact or not-applicable entity resolutions. The independent
auditor must then reproduce the recorded post-state with the production simulator.
`partial_hdt_gameevents_v1` is always excluded, even if another producer marks it
eligible. Terminal results remain trusted labels, but an action is not learned merely
because a result exists in the same file.

### `trajectory-readiness-v1` audit

Training JSONL uses `advisor-training-log-v2` records with a versioned `trajectory`
envelope. Initial and final searches share `(game_id, decision_id)`; the final search
is canonical, while a one-stage search uses `solve_stage: single`. Splits are assigned
deterministically at the anonymous game level, so decisions from one game cannot be
scattered across train/validation/test.

Run the read-only audit before any policy/value training:

```powershell
python .\solver\launch_solver.py audit-trajectories `
  --input training-v2.jsonl `
  --output trajectory-report.json
```

The production policy requires at least 100 games, 1,000 canonical decisions, 95
terminal-result games, a 95% solve/result join rate, 90% exact actions, 95% successful
transition replay, and at most 10% partial actions. Solve quality is measured across
**all** valid solve records, not only final/single canonical rows: the defaults allow at
most 25% explicit `unsupported`, 10% `cancelled`, 20% `partial`, and 30% total
non-`ok` solves. The report exposes `ok`/`partial`/`cancelled`/`unsupported`/`error`/
other counts and rates separately, so `unsupported_solve_rate` no longer means every
non-`ok` outcome. Structural/privacy validity is reported separately as
`contract_passed`; volume and quality readiness is `training_ready`. Exit code is `0`
only for the latter and `3` otherwise.

Every report binds the exact input bytes and effective policy with `input_sha256`,
`input_bytes`, and `policy_sha256`. Issue examples remain capped at 100 per category,
but `issues.reason_counts` and `issues.all_reason_counts` are complete; truncation can
no longer hide the dominant failure reason. Passing means only that the corpus
satisfies this data contract. It does not prove optimal labels, an unbiased sample, or
an existing RL policy.

The repository fixture can exercise the gate without pretending to be production
data:

```powershell
python .\solver\launch_solver.py audit-trajectories `
  --input .\solver\fixtures\trajectory-readiness-v1.jsonl `
  --policy .\solver\fixtures\trajectory-readiness-policy-v1.json `
  --source-kind synthetic_fixture
```

That command is an **auditor synthetic-fixture self-test**. Its PASS must never be
reported as runtime training readiness. Audit the real HDT history through the
snapshotting entry point instead:

```powershell
python .\solver\launch_solver.py audit-runtime-trajectories `
  --output .\artifacts\runtime-trajectory-readiness.json `
  --snapshot-dir .\artifacts\runtime-trajectory-snapshots
```

Without `--input`, it reads
`%APPDATA%\HearthstoneDeckTracker\MetaCompanion\AdvisorWorker\training-v2.jsonl`.
The mutable live file is read once and written with exclusive creation under a
content-addressed SHA-256 name; the auditor reads only that snapshot. The outer report
has exactly one state: `READY`, `NOT_READY`, or `NO_DATA`, and binds the snapshot bytes,
input SHA-256, and production-policy SHA-256. Exit codes are respectively `0`, `3`, and
`4`. `NO_DATA` is not training-ready, but it does not block an unrelated plugin release.

### Observed behavior learning audit

`behavior-v1.jsonl` is useful for behavior cloning and opponent modeling, but it is not
the replayable RL contract above. Audit it jointly with terminal results before using
it as any learning input:

Existing HDT `.hdtreplay` archives can seed the same observed-behavior workflow without
being copied into the live worker logs. `audit-hdt-replays` defaults to the latest client
build and Standard/Arena; `import-hdt-replays --output-dir <dir>` writes a separate
`behavior-v1.jsonl`, `advisor-decision-frame-v1.jsonl`, `training-v2-results.jsonl`, and
hash-bound import manifest. The decision-frame file contains only strict local main-action
frames: all HDT `error=NONE` options and targets, the end-turn option, every core board-slot
position, the exact `SendOption` selection, its bound PLAY/ATTACK/turn transition, and the
matching behavior ID. Choice branches, trades, duplicate semantic options, hidden targets,
and incomplete boundaries fail closed. These frames are eligible only for candidate-set
imitation; `optimality_verified` and `rl_training_eligible` are always false. Player
names, accounts, replay filenames, raw-log hashes, and exact wall-clock time are omitted.
Every imported action remains `rl_training_eligible=false`, and builds must be audited and
trained separately.

Run `audit-decision-frames --input <advisor-decision-frame-v1.jsonl> --behavior
<behavior-v1.jsonl>` before consuming the candidate corpus. READY requires strict hashes,
continuous per-game sequences, unique decision/behavior joins, and exact selected action plus
pre/post-state equality. READY still means only `candidate_imitation_ready=true`.

Mutable zone/controller/position values from stale `TAG_CHANGE` and root `BLOCK_START`
descriptors never overwrite the state machine's newer Power tags. Emitted pre/post states
must pass the production `GameState` contract; invalid capacity boundaries are skipped and
sequences are renumbered. Replay-specific learning checks require played sources to leave
the actor hand, end-turn actions to change the active player, and every observed attack to
carry the directly proven `can_attack=true`, `attacks_remaining>=1` source evidence. This
does not reconstruct complete card effects. HDT Options can prove the local interface
candidate set for accepted decision frames, but no equivalent opponent candidate set is
available. Replay imports therefore remain ineligible as solver-optimality ground truth
even when the candidate-imitation audit is ready. They may be used by
the separate coverage auditor below, where HDT candidates are a completeness reference for
root-action recall rather than a claim that the observed player choice was optimal.

```powershell
python .\solver\launch_solver.py audit-runtime-behavior-learning `
  --output .\artifacts\runtime-behavior-learning-readiness.json `
  --snapshot-dir .\artifacts\runtime-behavior-learning-snapshots
```

The command reads each live file once, persists two content-addressed snapshots, and
binds both input hashes plus the effective `behavior-learning-readiness-v1` policy.
It checks behavior integrity, privacy, file/sequence/timestamp order, local and
opponent coverage, action/identity/boundary quality, unique terminal joins, and stable
game-level splits. Its statuses are `READY`, `NOT_READY`, and `NO_DATA`; READY means
only `imitation_ready=true`. `rl_training_ready` is always false.

Promotion never mutates either live JSONL and strips exact observation timestamps:

```powershell
python .\solver\launch_solver.py promote-behavior-imitation `
  --behavior behavior-v1.jsonl `
  --trajectory training-v2.jsonl `
  --output behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json
```

The hash-bound manifest approves behavior cloning, opponent behavior modeling, and
search-ordering priors. Every example remains observed behavior with
`optimality_verified: false` and `rl_training_eligible: false`; a terminal win does
not turn the preceding human actions into proven optimal labels.

Train the first audit-only behavior-ordering baseline from the promoted bytes and
their manifest:

```powershell
python .\solver\launch_solver.py train-behavior-prior `
  --input behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json `
  --output behavior-prior-v1.json
```

The dependency-free hierarchical frequency model learns only from game-level `train`
records. Validation and test labels never enter action-kind or action-template counts,
and outcome fields are not features. Context backoff covers global, actor, mode, patch,
hero pair, and public-state buckets. Parent shrinkage is at least eight pseudo-counts and
scales to `0.5 * label_count`, preventing sparse card/target vocabularies from being
overfit by one-game context buckets without consulting held-out labels. It can only order legal candidates supplied by a
separate rule engine; it cannot generate actions, and an unseen mode or patch yields a
uniform ordering. The production policy requires at least 30/10/10 games and
250/50/50 records across train/validation/test, plus validation checks against the
global baseline and unseen-template rate. Even a ready artifact fixes
`live_policy_eligible`, `rl_training_eligible`, `optimality_verified`, and
`candidate_generation_allowed` to false. The online Rust worker accepts it only after
its independent production loader revalidates the artifact. It then uses the model
solely to order already-legal candidates; it cannot generate actions or override
tactical scores. Missing, stale, malformed, or runtime-rejected priors fail back to
the deterministic base order.

The release fixture and policy under `solver/fixtures/behavior-prior-readiness-*`
exercise all three splits and artifact tamper checks. They validate the trainer, not
production-data readiness or optimal play.

For HDT replays that contain a complete `Options -> SendOption -> Power` chain, train
the local-choice baseline directly on the recorded candidate set instead of asking the
solver to reconstruct that historical set from a different CardDefs build:

```powershell
python .\solver\launch_solver.py train-decision-ranker `
  --decision-frames advisor-decision-frame-v1.jsonl `
  --behavior behavior-v1.jsonl `
  --output decision-ranker-v1.json
```

`advisor-decision-ranker-v1` is a dependency-free sparse listwise logistic model. It
learns only from `train` games, selects its epoch and probability temperature on
`validation`, and reports untouched `test` Top-1, Top-3, mean reciprocal rank, log loss,
uniform baselines, and unseen selected-template rate. Features are restricted to the
public pre-state and caller-supplied legal candidates. The artifact contains no game,
state, or entity identifiers and permanently forbids candidate generation, live-policy
claims, RL labels, and optimality claims.

The two evidence paths are then reviewed together:

```powershell
python .\solver\launch_solver.py evaluate-observed-policy `
  --decision-frames advisor-decision-frame-v1.jsonl `
  --behavior behavior-v1.jsonl `
  --imitation behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json `
  --prior behavior-imitation-prior-v2.json `
  --ranker decision-ranker-v1.json `
  --output observed-policy-evaluation-v1.json
```

Local actions are evaluated only inside HDT's complete candidate sets. Opponent actions
are evaluated as a separate public behavior policy because HDT does not expose the
opponent's private `Options`; the evaluator never invents an opponent candidate set.

Audit the release Rust solver against the same historical HDT candidate sets with:

```powershell
python .\solver\launch_solver.py audit-decision-solver-coverage `
  --decision-frames advisor-decision-frame-v1.jsonl `
  --behavior behavior-v1.jsonl `
  --binary .\solver-rust\target\release\metacompanion-solver.exe `
  --output decision-solver-coverage.json `
  --max-frames 256 `
  --time-budget-ms 250 `
  --max-iterations 100000 `
  --max-depth 8 `
  --top-k 10
```

`advisor-decision-solver-evaluation-v1` deterministically samples frames by content
SHA-256, starts one authenticated loopback worker with the session token only in its
environment, and compares HDT's complete root set with the worker's reported root
portfolio. It reports exact/partial/unsupported outcomes, micro and per-frame candidate
recall/precision, complete-set matches, false-exact reasons, verified multi-alternative
frames, observed-choice Top-1/Top-3 agreement, and the most frequent uncovered public
CardIDs/action kinds. It never emits game/state/entity/request IDs, timestamps, URLs,
tokens, or absolute paths.

A frame counts as solver-scope counterfactual evidence only when the response is `ok`,
`coverage.exact=true`, the exact scope is explicit, root coverage and search are complete,
portfolio optimality is proven, the response contract is internally consistent, and the
Rust/HDT root sets are identical. The report does not write a training dataset and always
keeps candidate generation, live policy, RL eligibility, and global optimality false.
Observed-choice agreement measures behavior only; neither the human action nor the match
outcome becomes an optimal label.

Before a promoted behavior corpus may enter the production prior updater, audit the
observed actions against the conservative legal-action enumerator:

```powershell
python .\solver\launch_solver.py audit-behavior-candidates `
  --input behavior-imitation-v1.jsonl `
  --manifest behavior-imitation-v1.manifest.json `
  --rules .\solver\metacompanion_solver\rules_data\hdt-visible-point-effects-v1.json `
  --output behavior-candidate-alignment-v1.json
```

The versioned report aggregates exact, target-mismatch, and not-generated counts by
side, action kind, mode, and split without emitting game, state, or entity IDs. Card
rules are applied only when the state patch exactly equals the rule bundle's CardDefs
build. A ranking-eligible record must be local, exactly generated, have at least two
candidates, and have complete card, hero-power, board-combat, location, placement,
and choice legality evidence. Opponent observations remain useful for opponent
modeling but hidden hands never qualify as complete local candidate sets. The default
policy requires 30/10/10 eligible games, 250/50/50 eligible records, and 100% local
exact/candidate-complete rates. All reports keep candidate generation, live policy,
RL, and optimality flags false. The fixed behavior-prior fixture is deliberately a
negative candidate-completeness fixture in the release gate.

Promotion is a separate, non-mutating step. It writes a new sanitized corpus and a
hash-bound per-transition allowlist; it never rewrites `training-v2.jsonl`:

```powershell
python .\solver\launch_solver.py promote-trajectories `
  --input training-v2.jsonl `
  --output training-verified-v1.jsonl `
  --manifest training-verified-v1.manifest.json
```

Training reads one immutable byte snapshot, reruns the production audit, verifies the
dataset and simulator hashes, and requires the allowlist before learning action/value
labels:

```powershell
python .\solver\launch_solver.py train `
  --input training-verified-v1.jsonl `
  --verification-manifest training-verified-v1.manifest.json `
  --output prior.json
```

## Generic simulator coverage

The MVP models generic mana spending, minion placement, attacks and taunt targeting,
hero powers, armor/health, divine shield, poisonous/lifesteal combat, deaths, fatigue,
and deterministic descriptors for damage, healing, armor, draw count, summon, and
attack/health buffs. Legal actions come from this model only; learned policies cannot
invent actions.

For HDT entities, numeric state, visibility, represented keywords, English card text,
and visible spell power are imported. Free-form card text is never parsed or guessed.
The versioned `hdt-visible-point-effects-v1` catalog currently contains 44 manually
reviewed visible-effect rules covering 202 explicit CardIDs. Besides point and group
damage, healing, self-damage, and tag-proven lifesteal, the catalog models conditional
targets, 58 enumerated The Coin variants, continuous hero-power cost auras, one-cost
card doubling, reviewed Location behavior, and Sleet Storm's separate selected and
uniform-random target segments. The CardID count also includes the enumerated
Fireblast and Steady Shot hero-skin aliases.

A catalog rule is attached only when `card_id`, normalized EnglishText SHA-256,
`card_type`, required intrinsic mechanics, and declared context guards all match.
The three lifesteal rules require public intrinsic-mechanic evidence. Fireblast and
Steady Shot additionally require complete public player-rule tags and zero relevant
target/damage modifier tags; missing context evidence or an active modifier fails
closed. Backstab targets only a minion whose current health is explicitly known and
equals its maximum health. Coin mana is capped by the current turn's maximum mana.
HDT reports `IsPlayableCard=false`
for real hero-power entities, so availability instead requires public activation,
exhaustion/disable, live cost, and current-mana evidence. Missing or changed text, a
type mismatch, an unregistered card, unreviewed random behavior, Discover, Choose One,
and other unsupported scripts do not receive a guessed effect. If such an action is currently available, the solver abstains from
normal ranking; a clean modeled direct lethal that does not use the unknown action is
the only fail-closed exception. Structured damage from a `SPELL` adds the acting
player's visible spell power. Minion battlecries and hero powers do not.

A native caller may still provide structured `effects` and `effect_coverage`; set
`allow_approximate_effects: false` to reject a solve whose snapshot contains
unsupported modeled effects.

External hidden-card probabilities can be supplied through the typed `belief` state.
Optional normalized JSON files in the published `AdvisorData/Arena/latest` layout (and
the legacy `AdvisorData/latest` layout) affect action priors only, never rules.
Accepted containers include `cards`, `card_priors`, `card_stats`, `rows`, or `items`
with a card ID and a weight, score, pick rate, or win rate. Malformed/unknown files are
ignored. Website card/deck/class rankings and the versioned official Standard/Arena
card pools are likewise prior and coverage inputs only: they are not executable card
scripts, legal-action labels, reward labels, or reinforcement-learning training.

## Offline workflows

Replay solve requests or JSONL training records:

```powershell
python .\solver\launch_solver.py replay --input snapshots.jsonl --output results.jsonl
python .\solver\launch_solver.py benchmark --input snapshots.jsonl
```

Run the independent deterministic evaluation and promotion gate:

```powershell
python .\solver\launch_solver.py evaluate `
  --fixtures .\solver\fixtures\oracle-turn-v1.json `
  --output .\artifacts\advisor-eval\candidate.json

python .\solver\launch_solver.py evaluate `
  --fixtures .\solver\fixtures\oracle-turn-v1.json `
  --baseline .\artifacts\advisor-eval\baseline.json `
  --output .\artifacts\advisor-eval\candidate.json
```

Run the independent current-turn plus opponent-best-response gate with the real solver:

```powershell
python .\solver\launch_solver.py evaluate-turnpair `
  --fixtures .\solver\fixtures\oracle-turnpair-v1.json `
  --output .\artifacts\advisor-eval\turnpair-candidate.json `
  --seed 20260729
```

`evaluate-turnpair` checks friendly and reported opponent actions independently, then
measures Top-1/Top-3 first-action accuracy, minimax regret, false-safe claims, response
and lethal-proof contracts, abstention, and P95 latency. It exits with code `3` when
any threshold fails. The fixture oracle covers only its deterministic public combat
subset; it does not model hidden hands, unknown draws, or complete card scripts.

Run the independent raw-HDT structured-rule gate:

```powershell
python .\solver\launch_solver.py evaluate-hdt-rules `
  --fixtures .\solver\fixtures\oracle-hdt-cardrules-v1.json `
  --output .\artifacts\advisor-eval\hdt-cardrules-candidate.json
```

`evaluate-hdt-rules` sends raw HDT-shaped Fireball, Fireblast, Steady Shot, battlecry,
healing, hero-power availability, context-modifier abstention, and spell-power fixtures
through the real service while an independent point-effect
oracle checks both players' actions. Its negative controls require abstention for
EnglishText hash drift, wrong card type, unregistered text, random effects, Discover,
and Choose One. The blocking report also checks Top-1/Top-3, minimax regret,
false-safe/false-exact claims, rule provenance, abstention violations, fixture
contracts, and P95 latency. Passing `oracle-hdt-cardrules-v1` proves only this 31-rule
deterministic slice; it does not establish complete Hearthstone rules or an RL policy.

The command returns exit code `3` when a fixture threshold or baseline promotion
check fails. Its report pins every fixture SHA-256 and the search seed, and includes
proven Top-1/Top-3 lethal rates, false-lethal and independently checked line-legality
rates, exact/approximate/abstain counts, and latency percentiles. The reference oracle
does not call the live simulator to validate a candidate line.

`oracle-turn-v1` is intentionally limited to small deterministic public-turn fixtures
such as vanilla combat, taunt, divine shield, exact damage effects, and mana ordering.
Passing it is not evidence of complete Hearthstone rules, calibrated win rates, or
globally optimal play. Equal results can pass the no-regression promotion safety check,
but the report keeps `quality_improvement_proven: false` unless a measured quality
metric is strictly better than the supplied baseline.

Import HDT's local arena draft history. Player/deck identifiers are never emitted:

```powershell
python .\solver\launch_solver.py import-arena `
  --input "$env:APPDATA\HearthstoneDeckTracker\ArenaLastDrafts.xml" `
  --output arena-picks.jsonl
```

The importer preserves offered/picked card IDs, Arenasmith scores when present,
previous picks, and package cards. Draft IDs are run-local ordinals and are never
derived from HDT's Player, DeckId, or StartTime attributes. It rejects DTD/entity
declarations and oversized XML.

Build a dependency-free frequency/card-prior baseline:

```powershell
python .\solver\launch_solver.py train --input training-v2.jsonl --output prior.json
```

Arena draft priors can still be built independently. Without a verified manifest the
frequency command produces only non-trajectory priors and reports why trajectory
labels were excluded. Action-kind weights are learned only when the immutable corpus
passes the production audit and its per-transition manifest validates; the Torch
skeleton requires the same gate. Solve Top 1 output is never silently reused as a
behavioral label.

Generate bounded generic PUCT trajectories from explicit full-state fixtures:

```powershell
python .\solver\launch_solver.py self-play `
  --input snapshots.jsonl `
  --output-dir self-play-run `
  --episodes 100 `
  --max-turns 40 `
  --time-limit-seconds 14400 `
  --search-budget-ms 100 `
  --checkpoint-every 5
```

`self-play` supports `--resume`, writes `trajectories.jsonl`, an atomic
`checkpoint.json`, and a final/cancelled `manifest.json`, and handles Ctrl+C through a
cancellation event. Episode count, half-turn count, wall time, per-search time,
iterations, and depth are all bounded. It uses an explicitly approximate between-turn
refresh because the MVP has no complete deck/rules engine.

This command is a **trajectory generator, not reinforcement-learning training**. Its
manifest sets `is_reinforcement_learning_model: false`. A real self-play RL pipeline
still requires full card scripts, stochastic/hidden-state transitions, policy/value
optimization, an opponent pool, held-out evaluation, and promotion gates.

An experimental value-network skeleton is available with `--backend torch` only when
PyTorch is separately installed. PyTorch is never imported by the server or default
training path, and the resulting skeleton is not automatically promoted into live
search.

## Tests

From the repository root:

```powershell
python -m unittest discover -s .\solver\tests -v
```

The suite covers schema/HDT adaptation, legality and deterministic transitions,
unsupported-effect reporting, PUCT line generation, cancellation, API auth,
observation sanitization, strict HDT rule matching and drift rejection, spell-power
damage, both independent rule gates, arena import, optional priors, logging
disablement, trajectory stage de-duplication, solve/result joins, replay, split leakage,
privacy failures, and CLI readiness exit codes.
