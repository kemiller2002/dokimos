# ESO-P4: Mandatory final verification for incremental verification

Priority: P1
Owner system: Dokimos (evidence reuse)
Target repositories: dokimos
Programme: Echelon development speed optimization, Priority Four (incremental verification)
Intake: Praxis document ingestion. Source material, not a canonical requirement.

## Evidence (observed)

- Dokimos `main` (aae5413, after dokimos#58) covers change-impact analysis (`PATH_AFFECTED`, `UNMAPPED_CHANGE`),
  dependency-aware selection (declared mappings; project-reference closure in
  `experiments/incremental-verification/replay.fsx`), content-addressed identity (`dokimos.evidence-identity/1`),
  freshness policies, failure-aware reuse, and refusals for changed source, contract, dependency, configuration,
  runtime, tool version, live, security and independent acceptance, each with a negative test.
- **Missing: mandatory final verification.** Nothing distinguished a release checkpoint from an incremental one, so
  a release run could carry evidence forward exactly as a development run does.

## Requirements (proposed)

- **ESO-P4-R1.** A reuse request may declare `Checkpoint: incremental | release` (optional, absent = incremental,
  additive to `dokimos.evidence-reuse-request/1`). At `release` every test runs with
  `REUSE.FINAL_VERIFICATION_REQUIRED`, whatever evidence exists.
- **ESO-P4-R2.** `dokimos evidence plan --checkpoint release` raises the checkpoint; the command line can never
  lower a request's `release`. Unknown values are refused.
- **ESO-P4-R3.** The plan states its `Checkpoint` (additive member of `dokimos.evidence-reuse-plan/1`).
- **ESO-P4-R4.** Reuse remains opt-in: nothing reuses evidence unless a host calls `dokimos evidence plan`.
- **ESO-P4-R5.** A replay compares full verification, dependency-aware selection and selection plus reuse; metrics
  not measured (CI minutes, tokens) are reported as unavailable.

## Acceptance criteria

- Negative tests: valid content-addressed and per-commit evidence is refused at `release` with only
  `REUSE.FINAL_VERIFICATION_REQUIRED`; CLI raise, no-lower, and unknown-value refusals; request reader strictness.
- Full Dokimos test suite passes; counts reported exactly.
