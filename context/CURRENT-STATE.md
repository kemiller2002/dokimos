# Current State

Updated: 2026-09-25

## Established

- Project charter, comprehensive requirements, and architecture boundaries.
- Ordo/SDE 1.3.0 and ROS 3.1.4 canonical foundation artifacts.
- Requirement 0: longitudinal evidence is foundational.
- Versioned Dokimos snapshot schema and metric catalog.
- Immutable bootstrap observations from real CI history.
- Comparison/trend engine with unavailable and incompatible evidence semantics.
- Finding lifecycle including resurfacing.
- Baseline threshold and best-demonstrated-state ratchet policy.
- Git temporal model for commit frequency, churn, first/last change, and rename evidence.
- Confidence-bearing repeated-region history.
- Decomposable hotspot evidence without a universal opaque score.
- First conservative F# structural source analyzer.
- Dokimos CLI measurement surface.
- CI self-measurement and Git-history evidence artifact generation.
- Candidate rule DOK-FS-001 discovered from repeated F# record-inference failures.
- Snapshot-to-snapshot comparison pipeline.
- Forma, Folio, and Aegis integration requirements aligned.

## Current evidence gate

The latest completed run built with 0 warnings and 0 errors but exposed one incorrect test expectation in StructuralTests. The analyzer reported three nonblank lines for a fixture containing three nonblank lines; the test expected four. This is classified as a test-fixture defect, not a production analyzer defect. The expectation has been corrected and CI is processing the correction plus the snapshot comparison slice.

BASELINE-0001 remains provisional until a post-correction green CI run is observed.

## Next sequence

1. Verify green CI after the corrected fixture and snapshot comparison.
2. Record the green snapshot and mark the fixture finding resolved.
3. Promote BASELINE-0001 from provisional to accepted.
4. Make CI emit a canonical Dokimos snapshot in addition to raw evidence artifacts.
5. Add policy evaluation against the accepted baseline.
6. Expand F#/.NET structural metrics conservatively.
7. Feed temporal + structural evidence into repository hotspot output.
8. Begin Forma results UI from the real self-analysis dataset.
9. Add Folio printable evidence report after the interactive result model stabilizes.
