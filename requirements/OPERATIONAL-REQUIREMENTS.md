# Dokimos Operational Requirements

Status: accepted for implementation (work items `DOK-OPS-001` … `DOK-OPS-025`)
Recorded: 2026-09-29

These requirements close the Dokimos operating loop:

```
source → measurement → immutable snapshot → durable history → comparison
→ policy evaluation → CI gate → evidence retention → historical query
→ reusable external installation → second-repository proof
```

They extend, and never override, [`REQUIREMENTS.md`](REQUIREMENTS.md) (R0–R13).
Each requirement has a work item with the same identifier. A requirement is
complete only when the evidence named under "Done when" exists.

## Machine operational readiness (P0)

**DOK-OPS-001 — Canonical comparison wire contract.** Comparison results SHALL
be serialized through an explicit, versioned wire model that maps every domain
state to a stable string tag. F# unions SHALL NOT be serialized directly.
`schemas/dokimos-comparison.schema.json` SHALL describe the output.
Done when: `dokimos compare` emits schema-valid JSON for every comparison state
through the production serialization path, with tests.

**DOK-OPS-002 — Explicit policy evaluation.** An application boundary SHALL take
baseline snapshot + current snapshot + policy and produce an explicit evaluation
distinguishing `passed`, `warning`, `failed`, `observe-only`, `not-evaluated`,
`unavailable`, `incompatible`, and `collection-failed`. `dokimos evaluate`
SHALL expose it with stable exit codes: `0` continuation permitted, `2` invalid
invocation/configuration, `3` required evidence unavailable or invalid, `4`
policy failure.
Done when: tests verify both emitted JSON and exit codes.

**DOK-OPS-003 — Self-CI runs the real gate.** Dokimos CI SHALL run build → test
→ collect → analyze → snapshot → compare → evaluate → persist/upload, SHALL NOT
hide failures, SHALL NOT conflate unavailable with passing, SHALL retain
evidence when later stages fail, and SHALL derive its result from the Dokimos
policy evaluation once the product builds.
Done when: a real workflow run shows the complete loop.

**DOK-OPS-004 — End-to-end quality-gate proof.** A deterministic fixture SHALL
prove baseline → deterioration → regression → policy failure → exit `4`, and
baseline → improvement → improvement reported → exit `0`, through the real CLI.

## Durable history (P1)

**DOK-OPS-005 — Durable evidence store.** Snapshot persistence SHALL sit behind
an evidence-store abstraction independent of GitHub. Stored snapshots SHALL be
immutable; replacing an existing snapshot identity with different content SHALL
be rejected. The first implementation MAY be Git-backed on a dedicated evidence
branch; measurement commits SHALL NOT land on product `main`.

**DOK-OPS-006 — Historical queries.** `dokimos history` SHALL return ordered
snapshot history, per-metric trend, per-file history, first/last seen,
resolved/resurfaced findings, best demonstrated state, and baseline distance
where the evidence exists. Dimensions a collector does not support SHALL NOT be
fabricated.

## Product boundary and distribution (P1)

**DOK-OPS-007 — Stable CLI.** The CLI SHALL provide `measure`, `analyze`,
`snapshot`, `compare`, `evaluate`, `history`, `store`, and `version`; be
non-interactive; write canonical JSON to stdout and diagnostics to stderr; use
stable exit codes; identify schema versions; and reject malformed input cleanly.

**DOK-OPS-008 — Installable package.** Dokimos SHALL be packaged as a .NET tool
installable without cloning the repository. Package validation SHALL install the
produced package into a clean temporary environment and run `dokimos version`.

**DOK-OPS-009 — Release workflow.** A release workflow SHALL restore, build,
test, run Dokimos on itself, evaluate its own gate, pack, install/test the
package, and publish only under explicitly authorized release conditions, pinned
to an immutable source revision.

**DOK-OPS-010 — Reusable GitHub integration.** Consumers SHALL be able to adopt
Dokimos with a small pinned declaration (reusable action/workflow) that pins the
Dokimos version, fetches sufficient history, analyzes, snapshots, loads
baseline/history, compares, evaluates, exposes the CI result, persists evidence,
and uploads current-run evidence. It SHALL NOT install "latest".

**DOK-OPS-011 — Conditor integration.** Dokimos SHALL define the contract
Conditor needs to install it (pinned version, configuration, workflow, evidence
storage, initial baseline, system registration), following Conditor's current
contract.

**DOK-OPS-012 — Echelon administration registration.** Dokimos SHALL be
registered through the authoritative Echelon system-registration mechanism with
system id, version, repository, schema version, capabilities, and
installation/config version. No parallel registry SHALL be created.

## Evidence integrity (P1)

**DOK-OPS-013 — Analyzer capability declaration.** Each analyzer SHALL declare
id, version, language, metrics, scopes, limitations, required tools, and
determinism. Unsupported metrics SHALL be `unavailable`, never `0`.

**DOK-OPS-014 — Coherent v1 F#/.NET metric set.** Compiler diagnostics,
structural size, complexity, architecture, API, Git churn/frequency, repeated
change, hotspots, finding lifecycle, tests, and coverage SHALL each be either
collected or explicitly unavailable. No speculative analyzers.

**DOK-OPS-015 — Decomposable hotspots.** Hotspots SHALL combine temporal and
structural evidence and state which dimensions caused identification. No single
opaque score.

**DOK-OPS-016 — Persistent finding lifecycle.** Finding identity and lifecycle
(`introduced`, `persistent`, `resolved`, `resurfaced`, `uncertain`,
`unavailable`) SHALL survive across stored snapshots. Uncertain identity SHALL
remain uncertain.

**DOK-OPS-017 — Suppressions and waivers.** Suppressions SHALL retain the
original finding and record reason, scope, actor when known, created, optional
expiry, and status. Expired suppressions SHALL be visible to policy evaluation.

**DOK-OPS-018 — Results data contract.** A stable application-facing results
contract SHALL be produced from real evidence (overview, disposition,
comparison, improvements, regressions, findings, hotspots, trends, baseline
distance, best state, provenance, unavailable explanations). UIs SHALL consume
it and SHALL NOT recompute quality judgments.

**DOK-OPS-019 — Forma results UI.** Built on a pinned Forma release after
DOK-OPS-018: Overview, Trends, Changes, Hotspots, Findings, drill-down,
Provenance; accessible chart equivalents; mobile support.

**DOK-OPS-020 — Folio report.** Printable/shareable output via Folio consuming
the DOK-OPS-018 contract with provenance preserved.

**DOK-OPS-021 — Aegis boundary.** Unexpected external failures (filesystem,
Git, persistence, network, analyzer crash) cross the Aegis fault boundary;
expected quality states remain Dokimos domain outcomes.

**DOK-OPS-022 — Schema discipline.** Snapshot, comparison, evaluation, history,
store, and capability contracts SHALL be versioned. Unsupported schema versions
SHALL fail explicitly; historical data SHALL NOT be silently reinterpreted.

**DOK-OPS-023 — Complete provenance.** Every snapshot/result SHALL be
attributable to repository, revision, ref, timestamp, Dokimos version, analyzer
versions, metric-definition versions, configuration/policy, and scope.

**DOK-OPS-024 — Operational performance evidence.** Dokimos SHALL record its own
total/collection/analyzer duration, file count, observation count, unavailable
collectors, and collector failures as longitudinal evidence.

## Proof (mandatory before declaring operational)

**DOK-OPS-025 — Second-repository proof.** Using a released package and the
reusable integration, Dokimos SHALL be installed in a separate Echelon
repository (following that repository's governance) and demonstrate
installation, version reporting, analysis, snapshot, durable storage, baseline,
comparison, policy evaluation, CI disposition, and history retrieval.


## Standard Echelon distribution (post-0.1.0)

**DOK-OPS-026 — Registry-driven native distribution.** Dokimos SHALL publish its
canonical release facts through the current shared Echelon Registry release
contract rather than a Dokimos-specific parallel manifest generator. The release
SHALL identify system id `dokimos`, semantic version, canonical repository,
immutable source commit/tag, stable/preview/nightly stage, lifecycle state,
distribution class `self-contained-native-cli`, executable identity, capability
contracts, supported platform artifacts, and SHA-256 digests. The released CLI
MUST NOT require a machine-wide .NET runtime.

Done when: a published Dokimos release has a schema-valid
`echelon.release/v2` document generated from the exact uploaded artifact bytes
by a pinned Registry release-contract action, and Registry can catalog it
without repository-specific interpretation.

**DOK-OPS-027 — Canonical Registry identity.** Registry SHALL project Dokimos as
one canonical system identity with repository `kemiller2002/dokimos`,
executable `dokimos`, native/repository-lifecycle distribution classes, and the
quality snapshot/compare/evaluate/history capability contracts. Historical
release facts SHALL remain immutable.

Done when: Registry's canonical system projection contains Dokimos and a
cataloged release resolves to the same executable/capability identity reported
by `dokimos version`.

**DOK-OPS-028 — Component-owned lifecycle and ownership.** Dokimos SHALL expose
non-interactive, machine-readable `version`, `status`, `verify`, `doctor`,
`init`, and `upgrade` operations with stable exit-code semantics. Initialization
and upgrade SHALL be idempotent. Upgrade SHALL mutate only resources explicitly
owned by Dokimos, preserve user-owned policy/configuration, and fail closed when
ownership is unknown.

Dokimos conforms to Echelon Registry's generic repository lifecycle contract
`echelon.repository-lifecycle` v1 (`spec/repository-lifecycle-contract.md` in
`kemiller2002/echelon-registry`) and declares it in its release `provides`.
Every operation accepts `--root <repository>` and needs no other argument: the
release version and pinned action commit default to the executable's own
stamped release identity.

Done when: tests prove healthy status/verify/doctor, idempotent upgrade, policy
preservation, and refusal to overwrite an unowned workflow; the release workflow
smoke-tests the packaged native executable's identity and idempotent second
application (`scripts/smoke-native-lifecycle.sh`); Conditor can invoke these
lifecycle operations without reproducing Dokimos installation internals.

**DOK-OPS-029 — Conditor clean-host proof.** After Conditor's Registry-driven
resolver is available, a supported clean host SHALL install a pinned Dokimos
native release from Registry metadata, verify executable identity and artifact
integrity, initialize repository state, verify the installation, and repeat the
same desired state with zero unintended drift.

Done when: Conditor evidence names the resolved Registry release, platform
artifact and digest, successful Dokimos lifecycle results, and an idempotent
second application. This requirement is externally blocked by
`kemiller2002/conditor#12`; Dokimos MUST NOT duplicate that resolver locally.
