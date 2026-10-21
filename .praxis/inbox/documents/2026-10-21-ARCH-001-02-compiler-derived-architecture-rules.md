# ARCH-001-02: Compiler-derived architecture rules (successor to DOK-R008)

Priority: P2
Owner system: Dokimos
Intake: Praxis document ingestion
Evidence: kemiller2002/cohort-based-engineering `research/architectural-correctness/REPORT.md`, `RESULTS.json`, `BASELINE.md`, `ARCHITECTURE-RULES.md` (branch `claude/echelon-architectural-correctness-m4glhq`, PR #16). Reference implementation: `research/architectural-correctness/verifier/` (`archcheck`, FSharp.Compiler.Service 43.12.402, 22 selftest samples).
Related: DOK-R008, SDE-DOCTRINE-003 v0.2.1 (Four-Tier Architecture), AR-01..AR-29 (ARCHITECTURE-RULES.md), ARCH-001-01 (Praxis).

## Finding
DOK-R008, the only implemented architecture-dependency check in the ecosystem, has three limits:
- it is a regex over `.fsproj` `<ProjectReference>` lines;
- it cannot see `open`, type, function or package-level dependencies;
- it fails only when the violation count grows past a baseline.

No typed (type-checked) analysis exists in any Echelon repository, and no application declares which project belongs to which tier.

ARCH-001 built `archcheck`, which does three things:
- evaluates the MSBuild project and package graph;
- type-checks every production project with FCS, using the real compiler arguments from a design-time build;
- decides the rules from typed symbol uses: forbidden namespaces and symbols per tier, clock/random/GUID outside the composition root, and presentation boundary rules.

It detected every planted violation and produced no false positive on the reference solutions. Its reports are deterministic and bound to the commit and tree hash. Comments, strings and lookalike names never create findings. Its known limits are documented: indirect effects, unlisted wrappers, and semantic authority, which needs review.

## Requirement
1. Dokimos adds an architecture rule family evaluated from compiler-resolved structure:
   - an upward project reference;
   - a forbidden package or assembly per tier;
   - forbidden typed symbol uses per tier;
   - declared boundary rules.
   The policy file declares the project → tier map and the scopes. A production project absent from the map makes the run not evaluable; the tier is never guessed.
2. A **zero-tolerance** mode alongside the ratchet. Severity ≥ 2 deterministic findings fail regardless of baseline.
3. Findings are classed deterministic, heuristic or review. Heuristic findings never fail a check.
4. Reports carry the policy digest, the verifier digest, the commit and the tree hash. The policy must be supplied from outside the evaluated tree.
5. A DOK-R008 configuration (`forbiddenReferences`) is accepted unchanged and evaluated with the new engine.

## Acceptance criteria
- Sample repositories show each rule and the negative cases: forbidden names in comments and strings, a fully-qualified use without `open`, and type-alias resolution.
- Two runs on the same tree give byte-identical findings.
- An existing DOK-R008 baseline yields the same or more findings, never fewer.
- The docs state what the analysis cannot decide (ARCHITECTURE-RULES.md §3).
