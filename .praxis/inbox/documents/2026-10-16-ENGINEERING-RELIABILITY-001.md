# REL-DOKIMOS-001: Independent evidence and contract verification

Priority: P0
Owner system: Dokimos
Dependencies: Ordo contract REL-ORDO-001

## Requirements
- DOK-REL-001: Verify completion packets independently from the agent claiming success.
- DOK-REL-002: Validate requirement coverage, correct decision/conflict resolution references, expected commit, test outcomes and immutable evidence digests.
- DOK-REL-003: Reject evidence generated against an obsolete revision or incompatible Ordo contract version.
- DOK-REL-004: Expose structured pass/fail diagnostics that Praxis can enforce before completing work.
- DOK-REL-005: Include deterministic fixtures for stale evidence, forged/missing references, replayed packets, resolved conflicts and concurrent work completion.
- DOK-REL-006: Produce compact, machine-readable verification receipts for audits and experimental measurements.
- DOK-REL-007: Avoid duplicate full test runs when existing revision-matched evidence is sufficient; document which checks can be reused.

## Acceptance
A work item cannot pass independent verification with missing, forged or stale evidence. Verification is deterministic, revision-specific and traceable.
