# EP-A2a: Evidence validity conditions on Dokimos results and completion receipts

Priority: P1
Owner system: Dokimos (quality and completion-verification results)
Target repositories: dokimos (producer); Praxis consumes it via EP-A2b
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Program: kemiller2002/cohort-based-engineering `program/IMPLEMENTATION-PLAN.md` A2, `program/CONTRACTS.md` C3/C4,
`program/RELEASE-GATES.md` §4.

## Finding

- **Today's reuse check is narrow.** Reuse of a completion verification requires the host-set `Reusable` flag plus a
  matching digest and revision (`src/Dokimos.Core/CompletionVerification.fs:375-382`). Nothing invalidates a result
  when the tool version, configuration, SDK, dependencies or runner image change.
- **Those changes break checks in practice,** with no source change at all. Runner-image drift on 2026-10-23 (git 2.55,
  ubuntu-26.04) broke checks in Praxis and in Dokimos's own site job.
- **The prototype already exists.** The experimental audit (`experiments/rule-provenance/`, PR #53) has a
  `validity` block and an `isReusable` check that gives a distinct invalidation reason for each changed digest.

## Requirements (proposed)

- **EP-A2a-R1.** `dokimos.results` and `dokimos.completion-verification` gain an optional, additive `validity` block:
  - `sourceDigest` over the declared closure;
  - `toolName`, `toolVersion`, `configDigest`, `sdkVersion`;
  - `dependencyDigest` over the lock files;
  - `environment`, for checks declared environment-sensitive.
- **EP-A2a-R2.** `dokimos reuse-check --prior FILE` recomputes every digest and returns *reusable* (exit 0) only when
  all match. Otherwise it returns a list of reason codes, one per mismatched condition, each covered by a test.
- **EP-A2a-R3.** Digests add conditions and never remove one: the host `Reusable` flag and the revision rules still
  apply. Receipts without `validity` behave exactly as today.
- **EP-A2a-R4.** Never-deferred checks (security, data integrity, authorization boundary, release acceptance) are
  marked non-reusable whatever their digests say.

## Acceptance evidence

- Schema change: an additive minor.
- Tests for each invalidation reason, plus one confirming that a result with unchanged inputs is reusable.
- Completion-verification compatibility tests against Praxis's consumer.
- No change to exit-code meanings.

## Related records (not duplicates)

- DOK-REL-001..007: completion verification.
- Praxis DF-ROS-2026-A067: exact-identity cache reuse for checklists.
- Praxis EP-A2b: the consumer side.
