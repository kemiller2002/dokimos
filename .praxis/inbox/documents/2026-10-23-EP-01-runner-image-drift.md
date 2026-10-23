# EP-01: Site validation fails on the ubuntu-26.04 runner image

Priority: P1
Owner system: Dokimos (CI)
Target repositories: dokimos
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Program: kemiller2002/cohort-based-engineering `program/IMPLEMENTATION-PLAN.md` EP-01; open decision OD-5 in
`program/EXECUTION-STATE.md`.

## Finding

- **The failure.** The site `validate` job fails at "Browser accessibility and responsive audit". The log reads:
  `npx playwright install --with-deps chromium` → "Playwright does not support chromium on ubuntu26.04-x64"
  (Playwright 1.56.1).
- **Where it appears.** Job 120443747212 on PR #52. The same failure keeps Deploy site red on main
  (runs 39885212834, 39846221453).
- **Why it matters.** The cause is the hosted runner image moving, not a code change. Until it is fixed, the
  results-UI render and browser audit (DOK-OPS-019) are skipped on every PR.

## Requirements (proposed)

- **EP-01-R1.** Restore the browser audit on a supported platform. Pick one:
  - (a) upgrade Playwright to a release that supports ubuntu-26.04, and re-baseline any visual expectations
    deliberately;
  - (b) pin `runs-on` for the browser jobs to a supported image, with a dated revisit note.
  The owner picks under OD-5.
- **EP-01-R2.** Never skip or disable the audit to get green.
- **EP-01-R3.** Record the runner image in the job's evidence, so a future image change is visible as the cause
  (RELEASE-GATES.md §4, environment assumptions).

## Acceptance evidence

The site `validate` job and Deploy site are green on main, with the audit step executed (not skipped).
