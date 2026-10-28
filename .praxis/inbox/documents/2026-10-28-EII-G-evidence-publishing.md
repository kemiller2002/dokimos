# EII-G: Independent publication of passing and failing verification evidence

Priority: P1
Owner system: Dokimos (evidence reuse, evidence store)
Target repositories: dokimos
Programme: Echelon Engineering Improvement Initiative, Workstream G (incremental verification)
Intake: Praxis document ingestion. Source material, not a canonical requirement.

## Evidence (observed)

- Dokimos 0.8.0 has content-addressed verification identity (`dokimos.evidence-identity/1`), reuse rules,
  freshness classes (content-addressed, per-commit, live) and path-based test selection in
  `dokimos evidence plan` (`src/Dokimos.Core/EvidenceReuse.fs`, 53 tests).
- **No durable store for provenance records.** `EvidenceStore` / `FileSystemStore`
  (`src/Dokimos.Core/EvidenceStore.fs`) holds quality snapshots and baselines only. Rehearsal 5 keeps
  `dokimos.evidence-provenance/1` records in the GitHub Actions cache, which is evictable, not durable.
- **Failing runs are never recorded.** Rehearsal 5 writes provenance only with `--outcome passed`
  (`.github/workflows/build-and-test.yml` ~line 104), so `REUSE.CONFLICTING_PRIOR` (flaky result) can never
  fire and a failure can never contradict earlier passing evidence for the same identity.
- **Capability not declared.** `release/echelon.release-input.json` (Dokimos 0.8.0) does not list the
  evidence-plan capability in `provides`.

## Requirements (proposed)

- **EII-G-R1.** Passing and failing outcomes are recorded independently as `dokimos.evidence-provenance/1`
  records in the existing Dokimos evidence store (additive, versioned store layout; no new ledger). A failure
  for one identity does not hide or invalidate valid evidence for any other identity; a failure for the same
  identity blocks reuse of earlier passing evidence (`REUSE.CONFLICTING_PRIOR`).
- **EII-G-R2.** Reuse only when the full verification identity matches (sources, dependencies, test contract,
  configuration including runtime environment where relevant, tool version). Live evidence is never reused;
  security-category and independent-acceptance evidence is never reused; stale evidence is rejected. Each
  incorrect reuse attempt has a negative-control test.
- **EII-G-R3.** The evidence-plan capability is declared in `release/echelon.release-input.json`. No release
  is published by this work.
- **EII-G-R4.** A measured replay of real commits compares full verification, dependency-aware selection, and
  selection plus safe reuse: tests executed, repeated tests, wall-clock time, cache hit rate, incorrect reuse
  attempts accepted (must be 0), and detection of at least three seeded defects. Unmeasurable quantities
  (for example token consumption) are reported as unavailable, never as zero.
- **EII-G-R5.** Documentation states what changed, which failures are prevented and how agents consume it.

## Acceptance criteria

- Store tests: a failing record is stored and read back; immutability and identity checks hold for records.
- Plan tests: a stored failing record at the current identity yields `REUSE.CONFLICTING_PRIOR`; a failure at
  another identity or another test does not block an otherwise valid reuse.
- Negative controls for: changed source, dependency, contract, configuration, runtime, tool version; live;
  security; acceptance; per-commit across commits; tampered identity; incomplete provenance; failing prior.
- Full Dokimos test suite passes; counts reported exactly.
