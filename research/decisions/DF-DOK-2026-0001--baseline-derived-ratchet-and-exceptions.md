---
id: DF-DOK-2026-0001
title: Quality limits come from an accepted, tighten-only baseline; exceptions are separate, owned and expiring
status: accepted
type: decision-record
created: 2026-10-05
updated: 2026-10-05
tags: [quality, ratchet, policy, exceptions, evidence]
---

# DF-DOK-2026-0001

## Context

Issue #16 and the 2026-10-04 Echelon engineering-quality audit asked Dokimos
for a change-quality ratchet: inherited debt may remain, but new or worse
debt requires an explicit exception. Praxis needs to consume the result as
completion evidence.

The existing gate had four gaps:
- Ratchet limits were literals in the policy (`bestAccepted: 0`).
- Ratchets judged only repository-scope metrics.
- Waivers covered introduced findings only, and their expiry was optional.
- `Architecture.fs` was not used by any production code.

## Decision

1. **Observation is separate from judgement.**
   - `QualitySignals` measures each rule independently and records
     `NotMeasured` instead of zero when it cannot measure.
   - `QualityRatchet` is a pure judgement over the baseline, the exceptions and
     the measurement.
2. **Limits come from the accepted baseline (`quality/baseline.json`).**
   - The baseline is created once by `ratchet baseline init`. That records
     inherited debt honestly.
   - It is lowered only by `ratchet baseline update`, which keeps
     `min(accepted, current)` and never reads exceptions.
   - `ratchet baseline diff` makes any loosening fail in CI. Historical debt
     therefore cannot raise the baseline. The measurement configuration
     (sources, generated globs, size threshold, layer map) is part of the
     baseline, so weakening it is a loosening too.
   - The gate's own ratchets can now take `bestAccepted: "baseline"`
     (policy 1.2.0).
3. **Exceptions are first-class, separate from the baseline.**
   - They live in `quality/exceptions.json`. Each needs an owner, a rationale,
     evidence, a creation date, and either an expiry or a review condition.
   - Each matches one rule in one scope, up to an `allowedValue`.
   - Expired or incomplete exceptions fail verification (exit 6).
   - Policy-embedded suppressions are replaced by DOK-G001 exceptions.
4. **Stable rule ids** (DOK-R001 to DOK-R008, DOK-G001) and a versioned report
   contract (`dokimos.ratchet` 1.0.0) with stable exit codes:
   - 0 `pass`
   - 4 `regression`
   - 6 `invalid-exceptions`
   - 3 `unavailable`

   `unavailable` is never a pass.
5. **Generated code is declared, never inferred.** Excluded files are listed in
   the report.
6. **Size is a review signal.** DOK-R005 reports growth of files already over a
   threshold. It does not impose a hard line limit on new code.

## Alternatives considered

- **Keep hand-typed `bestAccepted` and add more literals.** Rejected. Limits
  drift from reality and never tighten.
- **Let exceptions lower or raise baseline values.** Rejected. That mixes
  acceptance of debt with the record of debt, and an exception would become a
  permanent baseline change.
- **Infer generated files from headers or paths.** Rejected. Inference can
  hide handwritten code silently.
- **Fail on any large file.** Rejected. Without semantic locality evidence,
  size is only a signal (#16, DOK-QUAL-001).

## Consequences

- Each new kind of debt needs either a fix or a reviewed exception in the same
  pull request.
- Renames and moves appear as a resolved scope plus a new scope, so the new
  path regresses from 0. Rename continuity is tracked debt (inventory
  DOK-F6).
- The analysis covers F# only and is lexical. Other languages are out of
  scope until they have analyzers with explicit capability and
  unavailability.
