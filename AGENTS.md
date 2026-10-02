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

## CI observation discipline

Keep incremental commits, pushes, and durable checkpoints at coherent recovery
boundaries, but do not wait for remote CI after every push. Continue the next
independent in-scope slice while debounced CI batches or runs. Run local checks
when they inform implementation; inspect remote build/CI status at the final
implementation boundary by default. Inspect it earlier only when its result
gates the next action, protects a high-risk boundary, or is required for
merge/release/publication. Never treat queued, cancelled, unavailable, or
unobserved CI as passing.

<!-- BEGIN echelon:visual-engineering -->
## Visual Engineering UI research

Managed by `npx @echelon-foundry/visual-engineering`. Do not edit inside this block.

Before designing, implementing, or reviewing UI:

1. Run `npx @echelon-foundry/visual-engineering verify` and stop if it reports a failure.
2. Read `.visual-engineering/AGENT-INSTRUCTIONS.md`.
3. Read `.visual-engineering/UI-FOUNDATIONS.md`.
4. Read `.visual-engineering/UI-DECISION-CHECKLIST.md`.
5. Read `.visual-engineering/UI-ANTI-PATTERNS.md`.
6. Consult `.visual-engineering/RESEARCH-INDEX.md` for provenance and deeper evidence.
7. Inspect the product and its existing design system.
8. Apply the research as decision criteria, not as a visual style.
9. Report the context version, source commit, principles applied, verification
   performed, and justified deviations.

Do not copy Visual Engineering research into this repository by hand.
<!-- END echelon:visual-engineering -->
