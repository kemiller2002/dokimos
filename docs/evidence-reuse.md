# Content-addressed evidence reuse and affected-test selection

`dokimos evidence plan` decides, for each test or evidence producer, whether
recorded evidence can be carried forward to the current commit or the test
must run. It extends the existing completion-verification evidence contract
(`dokimos.completion-authority/1`, see
[completion-verification.md](completion-verification.md#reusing-evidence)); it
is not a second evidence system.

```text
dokimos evidence plan --request request.json [--root CHECKOUT] [--store DIR]
dokimos evidence record --store DIR --plan plan.json --test ID --outcome passed|failed|unavailable --evidence-id ID
dokimos evidence record --store DIR --record provenance.json
```

| Contract | Schema |
| --- | --- |
| `dokimos.evidence-reuse-request/1` (input) | [`dokimos-evidence-reuse-request.schema.json`](../schemas/dokimos-evidence-reuse-request.schema.json) |
| `dokimos.evidence-provenance/1` (recorded runs, inside the request or published to the store) | same file, `$defs/provenance` |
| `dokimos.evidence-reuse-plan/1` (output) | [`dokimos-evidence-reuse-plan.schema.json`](../schemas/dokimos-evidence-reuse-plan.schema.json) |
| `dokimos.store-result` (output of `evidence record`) | [`dokimos-store-result.schema.json`](../schemas/dokimos-store-result.schema.json) |
| `Provenance` on an authority evidence observation (additive) | [`dokimos-completion-authority.schema.json`](../schemas/dokimos-completion-authority.schema.json) |

Exit codes of `plan`: `0` plan written; `2` invalid invocation; `3` request
missing, malformed (unknown or duplicate members, duplicate `TestId` or
`EvidenceId`, bad digest or revision, an unknown `Category`), the checkout is
not exactly the request revision with no tracked modifications, the store is
not initialized or holds an invalid record, or the request and the store
carry different records under one `EvidenceId`; `5` a stored record's identity
is not the digest of its inputs.

Exit codes of `record`: `0` stored or already stored (identical); `2` invalid
invocation; `3` the record or plan is missing or invalid, the plan reused the
test (nothing ran, so there is nothing to record), the test had no resolved
inputs, the record is incomplete, or the store is not initialized; `5` a
different record already exists under the `EvidenceId`, or the record's
identity is not the digest of its inputs.

## Why

Rehearsal 4 (`indy-init-rehearsal-004`, candidate `ab08b76`) reran four
workflows on every push, and one failing live test blocked all deployed
evidence. Evidence was already content-addressed after the fact
(`causa.acceptance-ledger/2` plus an evidence manifest of SHA-256 digests),
but nothing recorded what a result *depended on*, so nothing could say that
an earlier result was still valid.

## What existed (investigation)

- **Completion verification** already had reuse: an observation marked
  `Reusable: true`, independently observed `passed`, whose ID, digest and
  revision match the packet, is listed in the receipt's `ReusedEvidenceIds`.
  Reuse was bound to the same revision only, and `Reusable` was a bare host
  assertion with no recorded basis.
- **Evidence store** snapshots already derive their identity from content
  (`EvidenceStore.verifyIdentity` rejects a claimed identity that does not
  match).
- **R4 acceptance evidence** (`causa.acceptance-evidence/1` records,
  `causa.acceptance-ledger/2`) binds `source_sha`, the dependency lock digest
  and the interface manifest digest per run, not per check.
- **Praxis** parses the completion receipt with an exact member set, so the
  receipt cannot gain members without a coordinated Praxis change. This work
  therefore adds no receipt members: carried evidence stays visible through
  the existing `ReusedEvidenceIds`, the observation's `Provenance`, and the
  plan.

## Evidence identity

`dokimos.evidence-identity/1` is a SHA-256 digest over a length-framed
canonical form of:

| Input | Meaning |
| --- | --- |
| `Sources` | every file the test's mapping matches, plus `GlobalInputs`, as path to `sha256:` of its committed bytes |
| `Dependencies` | dependency name to version or identity digest |
| `TestContract` | `sha256:` of the test's contract file (its specification) |
| `Configuration` | configuration key to value; the runtime environment, where it can change a result, is declared here (for example `runtime.rid`, `runtime.framework`), so a run on another runtime has another identity |
| `ToolVersion` | verification tool name and version |

Maps are ordinal-sorted and every value is framed with its UTF-8 length, so
member order cannot change the identity and two distinct input sets cannot
share a canonical form. The plan emits each test's resolved `Inputs` and
`CurrentIdentity`; after running, the host records a
`dokimos.evidence-provenance/1` record from exactly those members plus the
outcome, revision and freshness.

## Freshness policies

| Policy | Reuse rule |
| --- | --- |
| `content-addressed` | reused while the identity is unchanged |
| `per-commit` | reused only at the revision that produced it |
| `live` | never reused |

These are intended to match the per-obligation freshness policies of the
Indy-init verification plan (`requirements-compiled/v1/verification-plan.json`).
That file was not yet on `claude/rehearsal-5-prep-qlbywr` when this was
written, so the mapping from its vocabulary to these three names still has to
be confirmed.

## Decisions and reasons

A test is reused only when a recorded passing run exists and none of the
following applies. Every reason is reported (`REUSE.*` codes, sorted):

| Code | Run because |
| --- | --- |
| `MAPPING_UNKNOWN` | no test-to-input mapping, a source pattern matches no file, or the test contract is missing |
| `LIVE_NEVER_REUSED` | the test is `live` |
| `SECURITY_NEVER_REUSED` | the test's `Category` is `security` |
| `ACCEPTANCE_NEVER_REUSED` | the test's `Category` is `independent-acceptance` |
| `NO_PRIOR_EVIDENCE` | nothing recorded for the test |
| `PROVENANCE_MISSING` | the record lacks `ProducedAtRevision`, `Freshness`, `EvidenceIdentity`, `Inputs`, sources, test contract or tool version, or its revision is not immutable |
| `IDENTITY_TAMPERED` | the recorded identity is not the digest of the recorded inputs |
| `FRESHNESS_CHANGED` | the record was made under another policy |
| `PRIOR_NOT_PASSED` | the record did not pass |
| `CONFLICTING_PRIOR` | a non-passing record has the current identity (flaky result) |
| `PER_COMMIT_REVISION_CHANGED` | per-commit evidence from another revision |
| `INPUT_CHANGED` | one per changed input, subject `<evidence>:<kind>:<name>` |
| `PATH_AFFECTED` | a changed path matches the mapping (or a global input) |
| `UNMAPPED_CHANGE` | a changed path is covered by no mapping, no global input and no ignored pattern: every test runs |
| `MAPPING_INCOMPLETE` | informational: source identity changed but no changed path matched the mapping |

Affected-test selection is conservative: `ChangedPaths` can only add tests to
the run set. Identity comparison alone already refuses reuse when an input
changed, so a missing or stale change list can never cause a wrong reuse.

### Categories that are never reused

A request test may declare `Category` (optional, added after 0.8.0; absent
means `functional`). Only functional evidence is ever carried forward:

- `security`: security-specific authority belongs to Aegis/Tutors
  (AGENTS.md, "Echelon boundaries": Dokimos must not duplicate another
  system's authority). Dokimos cannot certify that a security result is still
  valid, so the test runs every time (`REUSE.SECURITY_NEVER_REUSED`).
- `independent-acceptance`: reuse is not acceptance (below), and the
  Rehearsal 5 reuse configuration (`acceptance/reuse.json`) already states
  that acceptance evidence is always produced by the candidate run itself. The
  test runs every time (`REUSE.ACCEPTANCE_NEVER_REUSED`).

Both refusals hold even with a valid passing record at the identical identity
and revision.

## Publishing passing and failing evidence (EII Workstream G)

Before this change a provenance record existed only inside a request, so each
host kept its own copy (Rehearsal 5 kept them in the GitHub Actions cache,
which is evictable) and recorded only passing runs. A failure was never
recorded, so `CONFLICTING_PRIOR` could never fire and a failure could neither
contradict nor be told apart from earlier passing evidence.

Records now live in the existing Dokimos evidence store (no new ledger):

```text
<store>/provenance/<sha256(test-id)[0..24]>/<sha256(evidence-id)[0..24]>.json
```

The store marker moves to schema `1.1.0`; the change is additive, so a
`1.0.0` store opens unchanged and gains `provenance/` on its first record.
With the store checked out from the `dokimos-evidence` branch, records are as
durable as snapshots and baselines.

- **Independent publication.** Every run the plan scheduled is published with
  `dokimos evidence record`, whatever its outcome (`passed`, `failed`,
  `unavailable`), under its own `EvidenceId`. With `--plan` the record is built
  from the plan's own resolved inputs, so the host cannot misstate them. A test
  the plan reused cannot be recorded: nothing ran.
- **Immutability.** A record is created once. The same record again is
  `already-stored`; a different record under the same `EvidenceId` (for
  example a pass rewritten as a failure) is a store conflict (exit 5).
- **Only complete records.** Publication and reading both require
  `ProducedAtRevision` (immutable), `Freshness`, `EvidenceIdentity` and
  `Inputs` with sources, test contract and tool version, and the identity must
  be the digest of the inputs. A stored file whose inputs were edited is
  refused when read.
- **Isolation.** `dokimos evidence plan --store DIR` adds the store's records
  for the request's tests to `PriorEvidence`. A failure counts against reuse
  only at its own identity: a failure of another test, or of this test at
  another identity, leaves valid evidence reusable; a failure at the current
  identity, recorded before or after the pass, blocks it
  (`REUSE.CONFLICTING_PRIOR`). A failing record whose identity is not the
  digest of its inputs contradicts nothing.

### Failures this prevents

| Failure | Before | Now |
| --- | --- | --- |
| A test fails, then a later unrelated commit leaves it unselected | the failure disappears from later runs (only passes were recorded) | the failing record has the current identity: `PRIOR_NOT_PASSED`, the test runs again and the failure stays visible |
| Flaky test: fails and passes at one identity | the pass is reused | `CONFLICTING_PRIOR`: the test runs |
| Evidence evicted from a CI cache | reuse silently lost, or a record rebuilt by hand | records persist in the store and on the evidence branch |
| A pass rewritten as a fail, or the reverse, under the same `EvidenceId` | undetectable | store conflict |
| One test's failure | could block all deployed evidence (Rehearsal 4) | affects only that test's identity |

### How agents consume it

```text
dokimos store init --store .dokimos-evidence          # once (or check out the dokimos-evidence branch)
dokimos evidence plan --request req.json --root . --store .dokimos-evidence > plan.json
# run every test whose Decision is "run"; for each, whatever its outcome:
dokimos evidence record --store .dokimos-evidence --plan plan.json \
  --test core --outcome failed --evidence-id "EV-core-$RUN_ID"
```

Read `Decision` and `Reasons` per test from the plan; never treat `reuse` as
acceptance. Publish every run, including failures and `unavailable` ones, and
never only the passes. Mark security tests and independent acceptance with
`Category`. Declare runtime facts that can change a result in
`Configuration`.

## Reuse is not acceptance

The plan states it and the completion verifier enforces it. When the host
cites carried evidence in a completion authority it adds `Provenance`
(`EvidenceIdentity`, `Freshness`, `ProducedAtRevision`) to the observation:

- `DOK.COMPLETION.REUSE_UNDECLARED`: produced at another revision without
  `Reusable: true`.
- `DOK.COMPLETION.REUSE_STALE_COMMIT`: per-commit evidence carried across a
  commit.
- `DOK.COMPLETION.REUSE_LIVE_EVIDENCE`: live evidence marked reusable or
  carried. Live evidence is never listed in `ReusedEvidenceIds`.

Carried evidence still goes through every existing check (digest, revision
binding, status, freshness window, attested mode) and is listed in
`ReusedEvidenceIds`, so the acceptor sees exactly what was not rerun.

## Measurement (Rehearsal 4 replay)

[`experiments/evidence-reuse-r4/measure.fsx`](../experiments/evidence-reuse-r4/measure.fsx)
replays the 51 first-parent commits `e0cc043..ab08b76` of the frozen R4
repository through `EvidenceReuse.plan`. It uses real tree contents for source
identities, each commit's real changed paths, and the previous commit's
results as prior evidence (all assumed passing). The mappings are declared from
what each workflow reads and are deliberately broad.

| Granularity | Producers | Baseline runs | Selected | Reused |
| --- | --- | --- | --- | --- |
| Workflow (build-and-test, live suite, foundations, Praxis validation) | 4 | 204 | 185 | 19 (9.3%) |
| Suite (dotnet tests, core checks, negative control, browser, lock gate, site scan, live, foundations, Praxis validation) | 9 | 459 | 360 | 99 (21.6%) |

Per producer, at suite granularity: dotnet tests 40/51, core checks 38/51,
negative control 48/51, browser suite 43/51, lock gate 18/51, site scan 37/51,
live 51/51, foundations 34/51, Praxis validation 51/51.

What this does and does not show:

- These are counts of producer runs, not time or cost. No durations were
  measured.
- Commits stand in for pushes (R4 had 38 build-and-test runs for 51 commits).
- Praxis validation checks attribution of the pushed range, so it is
  per-commit by construction and never reusable. The R4 observation that it
  "reran unchanged" refers to its result, not its inputs.
- Echelon foundations scans application sources for usage, so with a
  conservative `src/**` mapping it ran on 34 of 51 commits. A narrower,
  verified mapping would save more, but has not been established.
- The live suite always runs. Separating it from deploy-gated evidence (an R4
  recommendation) is a workflow change, not something reuse can provide.

Reproduce (after `dotnet build src/Dokimos.Core`):

```text
dotnet fsi experiments/evidence-reuse-r4/measure.fsx /path/to/indy-init-rehearsal-004 e0cc043 ab08b76 suite
```

## Measurement (EII-G replay of this repository)

[`experiments/incremental-verification/replay.fsx`](../experiments/incremental-verification/replay.fsx)
replays the last 20 first-parent commits of Dokimos `main`
(`65c1c11..49a92b8`) in scratch clones and really runs this repository's own
test projects (`dotnet test`, TRX counters) under three strategies:

- **full**: all three test projects at every commit;
- **select**: dependency-aware selection only. A project runs when a changed
  path is inside its dependency closure (read from the `ProjectReference`
  graph at each commit, plus declared data inputs such as `tests/fixtures`,
  `config`, `schemas`), when a global input changed, or when a changed path is
  covered by no mapping. Nothing is recorded;
- **reuse**: selection plus safe reuse. `dokimos evidence plan --store`
  decides; every run, passing or failing, is published with
  `dokimos evidence record`.

A second sequence is the same 20 commits with three seeded defects
([`defects/`](../experiments/incremental-verification/defects)), each applied to
three consecutive real commits and removed at the next: D3 (data input,
`config/metric-catalog.json`) at steps 2 to 4, D1 (Core logic, snapshot
identity check disabled) at 11 to 13, D2 (CLI, unknown options accepted) at
16 to 18. Every reuse step also submits an adversarial battery to the real
CLI: per test project one positive control and eleven incorrect reuse
attempts.

Clean sequence (20 commits, 60 project decisions):

| Strategy | Project runs | Tests executed | Repeated tests (identity already passed) | Wall-clock (s) | Cache hit rate |
| --- | --- | --- | --- | --- | --- |
| full | 60 | 8,617 | 5,156 (38 runs) | 3,444 | n/a |
| select | 22 | 3,461 | 0 | 1,107 | n/a |
| select + reuse | 22 | 3,461 | 0 | 902 (incl. 8.6 s plan, 4.1 s record) | 63.3% (38/60) |

Defect sequence:

| Strategy | Project runs | Tests executed | Defect commits with the failure reported | Detected at introduction |
| --- | --- | --- | --- | --- |
| full (window commits only) | 27 | 3,837 | 9 of 9 | D1, D2, D3 |
| select | 30 | 5,108 | **4 of 9** (D3: 2,3; D1: 11; D2: 16) | D1, D2, D3 |
| select + reuse | 35 | 6,275 | **9 of 9** | D1, D2, D3 |

Incorrect reuse: 660 attempts per sequence (60 each of tampered identity,
other tool version, other runtime, changed source, live, security,
independent acceptance, per-commit from another revision, conflicting
failure, failed only, incomplete provenance): **0 accepted** in both
sequences; all 60 positive controls were reused. Of the 38 (clean) and 25
(defect) real reuse decisions, none was contradicted by the full run of the
same commit.

What this shows and does not show:

- Selection and reuse ran the same set on the clean history: with
  `ChangedPaths` from the previous commit, every unaffected project is skipped
  by selection already, so reuse saves no further runs there. Its 63% hit
  rate is the share of decisions satisfied by stored evidence.
- The difference is safety. Selection alone stops reporting a failure as soon
  as the next commit does not touch the failing project (5 of 9 defect commits
  looked green). Reuse refuses a failing record at the current identity
  (`PRIOR_NOT_PASSED`), reruns that project, and keeps the failure visible;
  that costs 5 extra project runs (1,167 tests).
- Wall-clock differences between select and select + reuse on the clean
  sequence (1,107 s versus 902 s for the same 22 runs) are machine noise, not
  a reuse effect: the host was shared with other agents. The full replay ran
  first under the heaviest contention (some commits took 370 s), so its
  wall-clock overstates the gap; compare test counts first.
- The full strategy observed one nondeterministic failure: at `828208f`
  `BehaviorVerifyCliTests."rounding down instead of to the nearest minute
  fails"` failed once and passed in the other two strategies. With every run
  published, such a result is exactly what `CONFLICTING_PRIOR` now catches.
- Granularity is the test project (3 units); finer mappings would select less.
  The defect windows sit in stretches where the real commits do not touch the
  defective file, which is where selection loses visibility; the sequence is
  one repository's history, not a sample of workloads.
- Token consumption is unavailable: the replay runs no model, and agent token
  use was not measured.

Results: [`results.json`](../experiments/incremental-verification/results.json)
(summary) and [`runs/`](../experiments/incremental-verification/runs) (every
step, decision, run and battery attempt). Reproduce:

```text
dotnet build src/Dokimos.Cli -c Release   # copy bin/Release/net8.0 to TOOL
dotnet fsi replay.fsx prepare --source . --work WORK --commits 20 --defects defects
dotnet fsi replay.fsx run --work WORK --tool TOOL/Dokimos.Cli.dll --sequence clean|defects --strategy full|select|reuse
dotnet fsi replay.fsx summarize --work WORK --out results.json
```

## Limitations

- Provenance records and the mapping are host-supplied. Dokimos checks that a
  record is complete and internally consistent (identity equals the digest of
  its inputs) and computes current source digests itself. It cannot prove that
  a declared mapping lists every real input; `UNMAPPED_CHANGE` and
  `MAPPING_INCOMPLETE` make gaps visible, and an over-broad mapping only
  costs reruns.
- Dependencies, configuration and tool versions are declared, not detected.
  Lock and project files can be added to `GlobalInputs` so their bytes are
  part of every identity.
- Source digests are read from a clean checkout at the request revision
  (HEAD must equal `Revision`, with no tracked modifications), not from Git
  objects.
- Records are not signed. In attested completion mode every receipt must bind
  the current commit, so evidence carried from an earlier commit is refused
  (`ATTESTATION_BINDING_MISMATCH`). Cross-commit reuse is therefore available
  only in non-attested mode until a reuse-aware attestation binding exists.
- Because records are unsigned, an edit to a stored record's inputs is
  detected (the identity no longer matches) but an edit to its `Outcome`
  alone is not. Store files are immutable by convention and, on the
  `dokimos-evidence` branch, by Git history; signing is future work.
- `Category` is declared by the host. A security or acceptance test declared
  `functional` would be reusable; the declaration is visible in the request.
- The completion verifier does not yet see `Category`: refusing carried
  security or acceptance evidence there needs an additive `Provenance` member.
