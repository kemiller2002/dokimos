# VAC-DOK-001: Chained regression verification, re-verifying earlier accepted requirements at each completion with per-requirement isolation

Priority: P3. This is a measurement capability; its benefit is **not yet demonstrated**.
Owner system: Dokimos (completion verification); Praxis passes the accepted-requirement set
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Existing work items: REL-DOKIMOS-001 (independent evidence and contract verification) verifies the item being
completed. This input adds verification of **earlier accepted** requirements at the same completion. It does not
duplicate REL-DOKIMOS-001.

Experimental finding: kemiller2002/cohort-based-engineering, branch `claude/verified-agent-continuity-vfjrrx`:
- `research/verified-agent-continuity/research-protocol.md` (amendment A2)
- `research/verified-agent-continuity/error-propagation-results.md`
- `research/experiments/VAC-001/harness/vac.sh` (`verify`)

## Finding

VAC-001 verified every change of a chain against the hidden oracles of **all** requirements accepted so far.

- **Isolation is necessary.** The first harness compiled those oracles together, and one task's compile failure
  hid every other task's result (amendment A2). Per-requirement isolation fixed it.
- **No regressions occurred.** Across 96 units, no earlier accepted requirement regressed, so the capability
  caught nothing. Its value is that it *can* show propagation, which completing-item verification alone cannot.

## Requirements (proposed)

- **VAC-DOK-001-R1.** On request, `completion verify` re-runs the checks of a supplied set of previously accepted
  requirement IDs against the completing revision, and reports each requirement's result separately.
- **VAC-DOK-001-R2.** Each requirement's checks are built and run in isolation. A build failure in one
  requirement's checks is reported for that requirement only and never masks another's.
- **VAC-DOK-001-R3.** A previously accepted requirement that fails is reported as `regressed`, with the completing
  revision. Praxis can then refuse verified completion, or record it as a regression, by policy.

## Acceptance evidence

- A fixture with two accepted requirements and a third change that breaks the first, giving one `regressed`
  result.
- A fixture where one requirement's checks fail to compile, with the other requirements still reported correctly.
- Canonical work items created by Praxis with provenance linking to this input. Do not use GitHub Issues.

## Not requested

Making chained verification mandatory. No VAC-001 measurement justifies its cost yet.
