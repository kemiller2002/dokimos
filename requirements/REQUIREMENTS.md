# Dokimos Requirements

Status: initial approved baseline

## R0 — Longitudinal evidence

R0.1 Every analysis SHALL create an immutable snapshot identified by repository, revision, branch/ref when known, timestamp, analyzer versions, rule-set version, configuration identity, and analyzed scope.

R0.2 Raw measurements SHALL be preserved independently from thresholds, grades, summaries, and policy judgments.

R0.3 Dokimos SHALL compare any compatible snapshot with a baseline or prior snapshot and classify changes as introduced, persistent, improved, resolved, regressed, resurfaced, or unavailable.

R0.4 Metric definitions and analyzer versions SHALL be versioned. A definition change MUST NOT silently rewrite historical meaning.

R0.5 Missing, unsupported, failed, and zero measurements SHALL be distinct states.

R0.6 Historical evidence SHALL be queryable at repository, project/module, namespace/component, file, type, and member levels where analyzers support those scopes.

R0.7 Dokimos SHALL retain sufficient provenance to explain where a metric came from and how it was calculated.

R0.8 Trend views SHALL support absolute value, delta, rate/direction, baseline distance, rolling history, and best-demonstrated state.

R0.9 Quality gates SHALL support ratcheting: a repository may prohibit deterioration beyond its best accepted demonstrated state.

R0.10 Re-analysis with a newer analyzer SHALL create new evidence rather than mutate old snapshots.

### Measurement provenance

These clauses refine R0.7 for *who* measured, compared, remediated, validated, or accepted evidence. They reference, and do not restate, the Praxis agent-provenance contract: actor identity (Praxis `RQ-ROS-2026-A001`), execution-keyed append-only contributions (`RQ-ROS-2026-A004`), lineage kept separate from authorship (`RQ-ROS-2026-A008`), the versioned interchange record `praxis.provenance-record` (`RQ-ROS-2026-A013`), execution propagation (`RQ-ROS-2026-A014`), and no silent stripping (`RQ-ROS-2026-A015`). Provenance is self-reported identity: it is not authentication, authorization, or evidence weight. Decision: `DF-GOV-012`.

R0.11 Every canonical snapshot and comparison Dokimos produces SHALL carry a Praxis provenance interchange record whose single `created` contribution is the measuring (for a comparison, the comparing) actor, keyed by the propagated Praxis execution (`ROS_EXECUTION_ID`) when present and otherwise by Dokimos's own run as `EXE-dokimos.<run>` (the GitHub Actions run and attempt in CI, else a generated local run). Dokimos SHALL NOT mint a Praxis-shaped `EXE-<timestamp>-<random>` identifier.

R0.12 The measured artifact and the measurement SHALL be distinct subjects. The measured revision SHALL appear only as lineage (`derivedFrom`, `git:commit/<revision>` unless the supplied record names the change). When the measured change's own provenance record is supplied, Dokimos SHALL carry it verbatim under `sources`; the code author SHALL appear only there and SHALL NOT become a contributor to, or the actor of, the measurement. A supplied record that is malformed or describes a different revision SHALL be refused, not dropped.

R0.13 The measuring, comparing, remediating, validating, and accepting actor SHALL be resolved only from explicit CLI declarations, the whitelisted non-secret variables `ROS_ACTOR_KIND`, `ROS_ACTOR`, `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_MODEL`, `ROS_TELEMETRY_RUNTIME` (and `ROS_EXECUTION_ID` for the run), or GitHub Actions detection (recorded as `automation`); otherwise it SHALL be recorded as `unknown`. It SHALL NOT be derived from Git history, commit authors or trailers, file content, or code-pattern heuristics, and SHALL NOT carry credentials.

R0.14 Remediation (`x-remediated`) and validation (`x-validated`) of a finding or comparison SHALL be recorded as additional contributions by their own actors, appended without removing, rewriting, or re-attributing any existing contribution. The measuring contribution SHALL remain the originator; a follow-up SHALL NOT claim `created`. Snapshots remain immutable (R0.10): follow-ups are appended to a finding's own record (derived from its snapshot's provenance) or to a comparison, never to the snapshot file.

R0.15 A comparison's record SHALL derive from both snapshots and SHALL carry each snapshot's provenance record verbatim under `sources`, so each measuring actor remains attributable to its own snapshot. A snapshot without provenance (schema 1.0.0) SHALL be named in lineage only; no provenance SHALL be invented for it.

R0.16 Accepting a snapshot as a baseline SHALL be recorded as its own record (`created` and `approved` by the accepting actor) deriving from the accepted snapshot and carrying its provenance verbatim. Acceptance SHALL NOT modify the accepted snapshot.

R0.17 Provenance SHALL survive Dokimos: fields Dokimos does not model SHALL be preserved on read and re-write; malformed provenance SHALL be rejected rather than silently discarded; a record in an unsupported major version SHALL be carried verbatim and never extended; every 1.x snapshot schema SHALL remain readable, with provenance absent for 1.0.0. Dokimos SHALL implement the interchange codec locally without a dependency on Praxis code and SHALL run the vendored Praxis conformance fixtures against it.

R0.18 Heuristic identifiers are not authorship claims. The finding kind `agent-generated-risk-pattern`, the metric source `agent-quality`, and the `AgentQualityIndicators`/`agent` analysis fields name code patterns often associated with generated code (type weakening, scaffolding, change without test change). They SHALL NOT be presented, reported, or aggregated as evidence that an agent, or any particular actor, authored the code; authorship comes only from provenance records (R0.12). The identifiers are retained unchanged for continuity of stored snapshots, finding identities, and ratchets (R0.4, R10); see `docs/rules/DOK-PROV-001.md`.

#### Traceability

| Clause | Implementation | Tests |
|---|---|---|
| R0.11 | `MeasurementProvenance.forSnapshot`, `ActingIdentity.resolve`, `CanonicalSnapshot.Provenance` (schema 1.1.0), CLI `snapshot`/`compare` | `MeasurementProvenanceTests` (GitHub Actions, declared agent, unknown), `ProvenanceCliTests` (Praxis execution keying) |
| R0.12 | `MeasurementProvenance.forSnapshot`, CLI `--subject-provenance` | `MeasurementProvenanceTests` (reproduces Praxis `e2e/04-dokimos-measurement.json`; wrong-revision/malformed refused), `ProvenanceCliTests` |
| R0.13 | `IdentityEnvironment` (whitelist), `ActingIdentity`, `Program.processContext` | `ProvenanceCliTests` (Git history never populates the actor; only whitelisted variables read), `MeasurementProvenanceTests` (declarations, credentials refused) |
| R0.14 | `MeasurementProvenance.forFinding`/`recordFollowUp`, `ProvenanceJson.append`, CLI `provenance finding`/`provenance append` | `MeasurementProvenanceTests` (remediation/validation, refused `created` and re-attribution), `ProvenanceCliTests` (comparison validation; snapshot append refused) |
| R0.15 | `MeasurementProvenance.forComparison`, `CanonicalComparison.Provenance` | `MeasurementProvenanceTests`, `ProvenanceCliTests` (two measuring actors; legacy baseline lineage only) |
| R0.16 | `MeasurementProvenance.forBaselineAcceptance`, CLI `provenance accept-baseline` | `MeasurementProvenanceTests`, `ProvenanceCliTests` |
| R0.17 | `ProvenanceRecord.fs` (codec), `Program.tryReadSnapshot`, `tests/fixtures/praxis-provenance-record` | `ProvenanceConformanceTests`, `ProvenanceCliTests` (1.0.0 baseline, round trip with unknown fields, malformed rejected, unsupported major verbatim) |
| R0.18 | `docs/rules/DOK-PROV-001.md`; doc comments in `Correlation.fs`, `FSharpQuality.fs`, `CanonicalSnapshot.fs` | identifiers unchanged: existing `CanonicalSnapshotTests`/`CorrelationTests` |

## R1 — Measurements

Dokimos SHALL support extensible measurements including:
- cyclomatic and cognitive complexity;
- source/file/type/member size;
- duplication;
- coupling and cohesion indicators;
- dependency direction and architectural boundary violations;
- warnings and compiler/static-analysis diagnostics;
- dead/unreachable code where deterministically measurable;
- API/public-surface growth;
- error-handling quality indicators;
- test coverage when available;
- test distribution and test-quality proxies with explicit limitations;
- dependency count and dependency quality metadata;
- mutation/change frequency;
- line/file churn;
- repeated modification of the same files and hunks;
- ownership/concentration indicators when privacy policy permits;
- suppression/waiver counts and age;
- technical-debt introduction and removal.

Each metric SHALL publish definition, unit, scope, collection method, limitations, availability semantics, and version.

## R2 — Hotspots

Dokimos SHALL combine structural quality evidence with temporal change evidence.

A hotspot model SHALL be decomposable: users must be able to inspect the contributing measurements rather than receive only an opaque score.

Dokimos SHALL identify repeatedly modified files and, where Git evidence permits, repeatedly modified regions/hunks.

Hotspot history SHALL show whether risk is accumulating, stable, or being reduced.

## R3 — Findings

Findings SHALL have stable identities where technically possible.

A finding SHALL contain rule, location/scope, evidence, severity/policy disposition, first-seen snapshot, last-seen snapshot, and lifecycle state.

Moves/renames SHOULD preserve identity when confidence is sufficient; uncertain identity matches MUST be represented as uncertain rather than asserted.

Suppressions SHALL require reason, scope, author/actor when available, creation time, and optional expiry.

## R4 — Baselines and gates

Dokimos SHALL support fixed baselines, moving accepted baselines, branch baselines, release baselines, and best-demonstrated baselines.

Legacy debt MAY be baselined without allowing new debt.

Policies SHALL independently express warn, fail, observe-only, and unavailable behavior.

A gate failure SHALL identify the exact evidence and remediation direction. No gate may fail solely because an unsupported metric was represented as zero.

## R5 — Comparisons

Dokimos SHALL answer:
1. What improved?
2. What deteriorated?
3. What new debt appeared?
4. What debt was removed?
5. What remains problematic?
6. Which high-change areas have weak structural quality?
7. Did this change cross an accepted threshold or ratchet?

Comparisons SHALL be available commit-to-commit, branch-to-baseline, release-to-release, and over configurable time windows.

## R6 — Results UI and reports

Dokimos SHALL provide a mobile-friendly human results experience using Forma when UI implementation begins.

The UI SHALL include overview, trend charts/sparklines, regression/improvement views, hotspots, finding lifecycle, metric drill-down, commit comparison, provenance, and unavailable-data explanations.

Charts SHALL not be the sole representation of meaning.

Printable/shareable reports SHALL use Folio when appropriate and preserve provenance.

## R7 — Machine interfaces

Canonical results SHALL be machine-readable and schema-versioned.

CLI behavior SHALL be deterministic and non-interactive by default and SHALL support machine-readable output and meaningful exit codes.

Analyzer adapters SHALL not gain authority to reinterpret historical evidence.

## R8 — Echelon integration

Dokimos SHALL follow Ordo/SDE and ROS repository governance.

Dokimos SHALL integrate with Praxis/ROS execution telemetry without duplicating its ownership.

Security-specific analysis SHALL route to Tutela rather than creating a competing security authority. Aegis is not the security authority; it owns unexpected operational-fault handling at architectural boundaries.

UI SHALL consume Forma; printable reports SHALL consume Folio when those surfaces are implemented.

Dokimos SHOULD be installable by Conditor as part of the standard Echelon project bootstrap.

## R9 — Quality of Dokimos

Dokimos SHALL analyze itself. Its repository SHALL become the first longitudinal Dokimos dataset.

Dokimos SHALL not claim a metric, test, trend, or quality conclusion that was not actually collected.

Its core domain SHALL make invalid lifecycle states difficult or impossible to represent.

Warnings are errors in the F# build.

## R10 — Extensibility

The core evidence model SHALL be language-neutral.

Language/tool integrations SHALL be adapters with declared capabilities.

Adding a metric SHALL not require breaking existing historical snapshots.

Unknown future metric fields SHALL be preservable through schema evolution where practical.

## R11 — Performance and determinism

Repeated analysis of identical source, configuration, analyzer versions, and deterministic inputs SHOULD produce equivalent normalized evidence.

Collection cost and duration SHALL themselves be measurable.

Incremental analysis MAY be introduced, but it MUST NOT sacrifice provenance or comparison correctness.

## R12 — Trust

Every derived quality conclusion SHALL be traceable to observations and policy.

Dokimos SHALL distinguish observation, inference, policy judgment, and recommendation.

Aggregate scores, if introduced, MUST be decomposable and MUST NOT become the canonical source of truth.

## R13 — Shared Echelon application foundations

Dokimos SHALL use Aegis for unexpected operational failure at Git, filesystem, analyzer process, package/tool invocation, repository, network, persistence, and other external boundaries owned by its .NET/F# execution path. Expected quality findings, unavailable metrics, policy failures, threshold decisions, unsupported analyzers, and comparison outcomes SHALL remain typed Dokimos/Ordo domain outcomes rather than Aegis faults.

Aegis fault classification SHALL preserve stable codes, redaction, idempotency/recovery posture, and deterministic tests. Raw technology exceptions SHALL NOT cross declared integration boundaries.

When the human results UI is implemented, it SHALL consume a pinned Forma release and use existing Forma patterns/components/tokens before local equivalents. Application-specific quality meaning remains Dokimos-owned.

Any printable, PDF, paginated, or print-preview quality report SHALL consume a pinned Folio release and use existing Folio document primitives before local print implementations.

Aegis, Forma, and Folio dependencies SHALL be pinned to released versions or immutable artifacts. A missing shared capability SHALL be recorded as a gap in the owning shared repository rather than silently forked inside Dokimos.
