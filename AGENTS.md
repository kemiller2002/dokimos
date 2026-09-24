# Dokimos Agent Guide

Dokimos is Echelon Foundry's longitudinal code-quality evidence system.

## Start here

Read, in order:
1. `PROJECT-CHARTER.md`
2. `requirements/REQUIREMENTS.md`
3. `docs/architecture/ARCHITECTURE.md`
4. `context/CURRENT-STATE.md`

## Non-negotiable rules

- Never fabricate metrics, test results, analyzer availability, trends, or provenance.
- Never translate unavailable or failed collection into numeric zero.
- Never compare incompatible metric-definition versions as a continuous trend.
- Preserve raw observations separately from policy judgments.
- Historical snapshots are immutable evidence.
- Every aggregate conclusion must be decomposable into its contributing evidence.
- New quality policy must not rewrite historical observations.
- Prefer explicit F# domain states over booleans/stringly typed lifecycle state.
- External effects stay at boundaries.
- Treat warnings as errors.
- Dokimos must dogfood Dokimos.

## Echelon boundaries

- Ordo/SDE governs engineering/state methodology.
- ROS governs repository work and execution evidence.
- Praxis/ROS owns engineering-process telemetry.
- Aegis/Tutors owns security-specific authority.
- Forma owns reusable application presentation.
- Folio owns reusable print/report presentation.
- Dokimos owns code-quality evidence, history, comparison, and quality policy evaluation.

Do not duplicate another system's authority merely because its data is useful to a quality analysis. Integrate through explicit evidence boundaries.
