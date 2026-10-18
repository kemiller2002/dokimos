---
id: RQ-DOKIMOS-REL-001
title: Independent evidence and contract verification
status: draft
version: 1.0.0
owners: [dokimos-maintainers]
created: 2026-10-18
updated: 2026-10-18
priority: critical
related_documents:
  - RQ-SDE-2026-0002
tags: [reliability, evidence, verification, contracts]
derived_from: [INPUT-f1620e8ac38269ea]
provenance:
  contributions:
    EXE-20261018T124842385Z-b72bbb9b:
      operations: [created, modified]
      at: 2026-10-18T12:53:00.740Z
      last: 2026-10-18T12:54:02.508Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Canonical ingestion of claimed requirement source"
---

# Independent evidence and contract verification

This draft canonically ingests REL-DOKIMOS-001. Ingestion creates the
obligation but does not establish that the verification behavior exists.

## Requirements

- **DOK-REL-001:** Verify completion packets independently from the agent claiming success.
- **DOK-REL-002:** Validate requirement coverage, correct decision/conflict-resolution references, expected commit, test outcomes, and immutable evidence digests.
- **DOK-REL-003:** Reject evidence generated against an obsolete revision or incompatible Ordo contract version.
- **DOK-REL-004:** Expose structured pass/fail diagnostics that Praxis can enforce before completing work.
- **DOK-REL-005:** Include deterministic fixtures for stale evidence, forged/missing references, replayed packets, resolved conflicts, and concurrent work completion.
- **DOK-REL-006:** Produce compact, machine-readable verification receipts for audits and experimental measurements.
- **DOK-REL-007:** Avoid duplicate full test runs when existing revision-matched evidence is sufficient; document which checks can be reused.

## Acceptance criteria

A work item cannot pass independent verification with missing, forged, or
stale evidence. Verification is deterministic, revision-specific, and
traceable.

## Dependency

The canonical Ordo requirement RQ-SDE-2026-0002 (source family
REL-ORDO-001) is a prerequisite.
