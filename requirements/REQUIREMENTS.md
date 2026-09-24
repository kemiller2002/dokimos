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

Security-specific analysis SHALL route to Aegis/Tutors rather than creating a competing security authority.

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
