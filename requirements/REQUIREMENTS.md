---
title: Dokimos Requirements
provenance:
  contributions:
    EXE-20260926T081023859Z-75aea576:
      operations: [modified]
      at: 2026-09-26T08:14:32.491Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Add R14 provenance of measurements and findings (FEAT-ECHELON-PROVENANCE)"
    EXE-20260926T085550201Z-2f24ec7e:
      operations: [modified]
      at: 2026-09-26T09:05:39.435Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "R14.3 and R14.11 updated for Praxis provenance contract revision 1.1"
---

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

## R14 — Provenance of measurements and findings

Praxis owns the Echelon agent identity and provenance model (DF-ROS-2026-A036, DF-ROS-2026-A037; RQ-ROS-2026-A001 through RQ-ROS-2026-A019). Dokimos carries that model through its own records. It does not redefine what an actor, execution, contribution, operation, or "unknown" means, and it does not fork the Praxis schemas. This requirement refines R0.7, R3 (suppression author/actor), R8 (Praxis/ROS integration), and R10 (preservable unknown fields).

R14.1 Four distinct roles. Dokimos SHALL keep these roles separate, each expressed with the Praxis operation vocabulary (RQ-ROS-2026-A014):
- **artifact author**: the recorded originator (`created`) of the measured artifact (source file, commit, requirement);
- **measurement actor**: the actor and execution that produced a snapshot or observation (`created` + `measured` on the snapshot's own provenance);
- **remediation actor**: whoever changed code to address a finding (`remediated` on the finding's provenance);
- **validation actor**: whoever confirmed a finding's state (`validated`), with `reviewed` and `resolved` recorded the same way.

No role implies another. A measurement SHALL NOT be attributed to the author of the measured code because an agent performed the measurement, and the author of the code SHALL NOT be recorded as its measurer.

R14.2 Interchange block. Snapshot and finding provenance SHALL be a `praxis.provenance/1` block (Praxis `schemas/provenance-interchange.schema.json`) embedded unchanged. Received blocks SHALL be classified with the Praxis receiving rules (RQ-ROS-2026-A015): *supported* blocks are preserved including unknown fields and tolerated operation codes; *unsupported* major versions are carried verbatim and never merged into; *malformed* blocks are rejected at the boundary with a clear error, never dropped or repaired.

R14.3 Execution identity. Every measurement contribution SHALL be keyed by the execution that produced it (RQ-ROS-2026-A002, RQ-ROS-2026-A013): the declared invoking execution (`--execution` or `ROS_EXECUTION_ID`, an `EXE-…` or `EXT-…` key) when one is declared, otherwise the Dokimos run itself as `EXT-dokimos.<run-id>`. `ROS_EXECUTION_ID` from the environment SHALL be honoured only when the process also declares an identity (a kind, an id, or `--actor-json`); a process with no declared identity SHALL NOT inherit a run from its environment (Praxis contract revision 1.1). Two runs of the same agent SHALL produce two distinct keys.

R14.4 Measurement actor from explicit declarations only (RQ-ROS-2026-A006, RQ-ROS-2026-A016). The measurement actor SHALL come from `--actor-json`, `--actor-kind`/`--actor-id`/`--provider`/`--model`/`--runtime`, or the environment variables `ROS_ACTOR_KIND`, `ROS_ACTOR`, `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_MODEL`, and `ROS_TELEMETRY_RUNTIME`; explicit flags win over the environment. Otherwise the actor SHALL be recorded as `unknown`. Dokimos SHALL NOT guess an actor from ambient signals and SHALL NOT require Praxis to be installed.

R14.5 Artifact authorship only from recorded provenance (RQ-ROS-2026-A008). The author of a measured artifact SHALL be read only from that artifact's own recorded provenance: an observation's `subjectProvenance` block, or a lineage reference in `derivedFrom` (for example `git:commit/<sha>` or a requirement id) resolved to that record's own recorded originator. It SHALL NOT be inferred from the measurement actor, Git author metadata, style, timestamps, file names, or any heuristic. Lineage is not authorship.

R14.6 Heuristics are not provenance. Correlation kinds such as `AgentGeneratedRiskPattern` describe evidence patterns. They SHALL NOT produce, imply, or change provenance or authorship.

R14.7 Finding lifecycle attribution (RQ-ROS-2026-A004). A finding lifecycle transition MAY carry a declared actor and execution. When it does, Dokimos SHALL append the matching `discovered`, `remediated`, `validated`, `resolved`, or `reviewed` contribution to the finding's provenance. The history is append-only: Dokimos SHALL NOT re-attribute a key, remove or reorder entries, or add a second or late `created`. A transition without a declared actor SHALL record nothing rather than invent one. `resolved` SHALL be recorded only for a `Resolved` transition and `discovered` only for an `Introduced` one.

R14.8 Immutability and legacy evidence (RQ-ROS-2026-A007; R0.10). Historical snapshots and observations SHALL NOT be rewritten or backfilled with provenance. Snapshot documents with `schemaVersion` `1.0.0` remain valid and read as *unattributed*. Dokimos SHALL refuse to attach provenance to a `1.0.0` document.

R14.9 Schema evolution. The snapshot schema `1.1.0` adds an optional root `provenance` block and an optional per-observation `subjectProvenance` block. `schemaVersion` SHALL accept both `1.0.0` and `1.1.0`, and a `1.0.0` document SHALL NOT carry root `provenance`. The existing collector and observation `provenance` fields keep their meaning: they describe the tool and method that calculated a metric, not the actor.

R14.10 No credentials and no authority (RQ-ROS-2026-A010, RQ-ROS-2026-A017, RQ-ROS-2026-A019). Provenance SHALL NOT carry credentials; a credential-like value makes a block or declaration malformed. Recorded identity is self-reported. It SHALL NOT be treated as authentication, and it SHALL NOT change thresholds, gates, ratchets, finding severity, or the weight of evidence.

R14.11 Conformance (RQ-ROS-2026-A018). Dokimos SHALL vendor the Praxis conformance fixtures unchanged, record their source commit and SHA-256, verify the hashes in tests, and prove its codec reaches the reference verdict and warning count for every case. The codec SHALL meet Praxis contract revision 1.1: exact (whole-string) matching of keys, codes, kinds, and schema tags; calendar-valid timestamps (years 0001-9999) ordered at millisecond precision; JSON `null` never read as an absent field; every append producing a block that itself classifies as supported (no credential, no contribution before the creation, no second originator); and same-key merges that keep incoming unknown fields, advance `last` to the later time, and refuse an actor of unknown identity extending an entry held by a known actor. Dokimos reads only identity variables listed in the vendored `identity-environment.json`.

R14.12 Suppressions. When suppressions are implemented (R3), the suppression author SHALL be recorded as a contribution on the suppression's own provenance, using the same actor and execution rules as R14.3 and R14.4.

### R14 traceability

| Requirement | Implementation | Tests |
|---|---|---|
| R14.1 | `src/Dokimos.Domain/Attribution.fs` (`ProvenanceRole`), `src/Dokimos.Core/SnapshotProvenance.fs`, `src/Dokimos.Core/FindingProvenance.fs` | `tests/Dokimos.Core.Tests/SnapshotProvenanceTests.fs`, `FindingProvenanceTests.fs` |
| R14.2, R14.11 | `src/Dokimos.Core/ProvenanceInterchange.fs`, `tests/fixtures/praxis-provenance/` | `tests/Dokimos.Core.Tests/ProvenanceInterchangeTests.fs` |
| R14.3, R14.4, R14.10 | `src/Dokimos.Core/MeasurementAttribution.fs`, `src/Dokimos.Cli/Program.fs` | `tests/Dokimos.Core.Tests/MeasurementAttributionTests.fs` |
| R14.5 | `src/Dokimos.Core/SnapshotProvenance.fs` (`Authorship`) | `SnapshotProvenanceTests.fs`, `ProvenanceInterchangeTests.fs` (chain replay) |
| R14.6 | `src/Dokimos.Core/Correlation.fs` | `tests/Dokimos.Core.Tests/CorrelationTests.fs` |
| R14.7 | `src/Dokimos.Core/FindingProvenance.fs` | `FindingProvenanceTests.fs` |
| R14.8, R14.9 | `schemas/dokimos-snapshot.schema.json`, `src/Dokimos.Core/SnapshotProvenance.fs` | `SnapshotProvenanceTests.fs` |
| R14.12 | Not implemented yet; no suppression type exists | None yet |
