# VAC-002-P3: Rule provenance audit and targeted independent review

Target: Dokimos; coordinate with Ordo and cohort-based-engineering.
Evidence: https://github.com/kemiller2002/cohort-based-engineering/pull/21 ; research/experiments/VAC-002/P3/RECOMMENDATIONS.md

## R-PROV-1 — Experiment with rule provenance audit
Design a source-aware audit of business-rule functions, constants and branches. For each rule, attempt to trace provenance to an accepted requirement or decision. Flag untraceable exemptions, defaults, superseded rules and rules inherited from existing code. Surface findings to an owner as questions; do not silently modify behavior or make this a default blocking gate.

Evaluate precision, recall, false positives, owner workload, cost, and whether agent decisions improve when provenance is made explicit. Include independently seeded valid and invalid code rules. Avoid equating source code with authorization.

## R-REV-1 — Test targeted independent provenance review
Evaluate an independent reviewer that asks: Which existing rules are reused by this change, and what is each rule's authority? Findings are non-blocking and routed to owners. Compare against no review and against mechanical provenance checks; measure errors found, false alarms, and token cost.

## Evidence and restrictions
Every one of 36 confirmed unauthorized decisions across P3 stages reused an existing code-embedded rule. This is a strong mechanism hypothesis, not yet proof that a provenance audit prevents errors. P2's blocking review caused harm; do not turn this into a blocking default before experimental validation.

Acceptance: preregister experiment, use blinded oracle, preserve provenance references and run artifacts, and publish measured results before production adoption.
