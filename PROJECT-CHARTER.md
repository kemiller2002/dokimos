# Dokimos Project Charter

## Mission

Build an evidence-based code-quality system that makes quality change observable, attributable, comparable, and actionable over the lifetime of a codebase.

## Success

Dokimos succeeds when a developer or agent can determine not merely whether code passes today's rules, but how its quality changed, where risk is accumulating, why a conclusion was reached, and what evidence supports it.

## Constraints

- Metrics over time are foundational.
- Evidence is never fabricated.
- Missing data is never treated as zero.
- Measurement definitions are versioned.
- Historical evidence is immutable.
- Aggregate scores never replace underlying evidence.
- Security authority remains outside Dokimos.
- The domain remains language-neutral.
- F# is the implementation language for the core system.
- External effects remain explicit.

## Initial vertical slice

1. Normalize observations.
2. Persist immutable snapshots.
3. Compare compatible snapshots.
4. Detect introduced deterioration and improvement.
5. Track Git file churn.
6. Combine churn with structural evidence into explainable hotspots.
7. Emit schema-versioned JSON.
8. Run Dokimos against itself.
9. Establish the first accepted baseline.
10. Add a results UI only after the evidence pipeline is trustworthy.
