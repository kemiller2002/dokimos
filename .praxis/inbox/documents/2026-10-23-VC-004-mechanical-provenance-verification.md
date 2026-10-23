# VC-004: Mechanical provenance verification and targeted review

## Goal
Replace indiscriminate blocking reviews with mechanical checks for provable constraints and targeted non-blocking owner review for uncertain rule authority.

## Requirements
1. Prototype static checks that connect business-rule branches, constants, constructors and transitions to Praxis/Ordo requirement or accepted-decision provenance.
2. Detect missing, stale or superseded provenance, unauthorized defaults and untraceable exemptions; distinguish lack of provenance from proven policy violation.
3. Provide positive/negative fixtures including VAC-002-P3 code-reuse errors. Do not silently fix or delete existing rules.
4. Mechanical checks may block only when a formally specified invariant is violated and the checker is validated against authorized counterexamples. Ambiguity findings must be non-blocking owner questions.
5. Support negative compile tests and deterministic, reproducible reports, with no default blocking gate until precision/recall and false-rejection rates are measured.
6. Run an independent targeted review comparing existing rules reused by a change against their claimed authority; measure additional detections, false alarms, tokens and review cost.
7. Maintain compatibility with current Dokimos and Praxis verified-state evidence contracts.

## Acceptance
Experimental checker, fixtures, machine-readable findings with source locations and authority references, tests for false rejection, and reproducible pilot metrics. No production default until VC-001 supports adoption.
