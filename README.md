# Dokimos

Dokimos is Echelon Foundry's evidence-based code-quality system.

It measures code quality, preserves observations over time, detects regressions and improvement, identifies change hotspots, and makes the evidence behind every quality judgment inspectable.

## Non-negotiable principle

**Metrics over time are Requirement 0.** A Dokimos run is not an isolated lint report. Every observation is attributable to source revision, analyzer/rule version, configuration, scope, and collection time. Historical observations are immutable; interpretations may evolve without rewriting history.

## Responsibilities

Dokimos owns code-quality measurement and longitudinal quality evidence. Security remains owned by Aegis/Tutors; engineering execution/process telemetry remains owned by Praxis/ROS; application state legality remains owned by Ordo.

## Initial architecture

- F# / .NET implementation.
- Explicit domain types for observations, snapshots, findings, baselines, thresholds, and trends.
- Analyzer adapters feed a normalized evidence model.
- Raw observations are preserved separately from derived judgments.
- Machine-readable results are the canonical interchange.
- Human results UI will use Forma; printable/shareable results will use Folio.
- Repository integration follows ROS and Ordo conventions.
- Language analyzers are adapters: Dokimos is not intrinsically F#-only.

See `requirements/REQUIREMENTS.md` and `docs/architecture/ARCHITECTURE.md`.
