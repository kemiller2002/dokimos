# Content-addressed evidence reuse and affected-test selection

`dokimos evidence plan` decides, for each test or evidence producer, whether
recorded evidence can be carried forward to the current commit or the test
must run. It extends the existing completion-verification evidence contract
(`dokimos.completion-authority/1`, see
[completion-verification.md](completion-verification.md#reusing-evidence)); it
is not a second evidence system.

```text
dokimos evidence plan --request request.json [--root CHECKOUT]
```

| Contract | Schema |
| --- | --- |
| `dokimos.evidence-reuse-request/1` (input) | [`dokimos-evidence-reuse-request.schema.json`](../schemas/dokimos-evidence-reuse-request.schema.json) |
| `dokimos.evidence-provenance/1` (recorded runs, inside the request) | same file, `$defs/provenance` |
| `dokimos.evidence-reuse-plan/1` (output) | [`dokimos-evidence-reuse-plan.schema.json`](../schemas/dokimos-evidence-reuse-plan.schema.json) |
| `Provenance` on an authority evidence observation (additive) | [`dokimos-completion-authority.schema.json`](../schemas/dokimos-completion-authority.schema.json) |

Exit codes: `0` plan written; `2` invalid invocation; `3` request missing,
malformed (unknown or duplicate members, duplicate `TestId` or `EvidenceId`,
bad digest or revision), or the checkout is not exactly the request revision
with no tracked modifications.

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
| `Configuration` | configuration key to value |
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
