# BASELINE-0001 — Initial accepted Dokimos baseline

Status: accepted.

Accepted evidence: GitHub Actions run `36186590248`, commit `90ec92ff153c936f29d5bf4e0a5d965a3e966da6`.

Acceptance state: build succeeded with 0 warnings and 0 errors; 20/20 tests passed; CI self-measurement build succeeded.

## Purpose

This baseline establishes ratcheting policy from demonstrated evidence rather than arbitrary aspirational numbers.

## Demonstrated state before temporal slice

- compiler errors: 0
- compiler warnings: 0
- executed tests: 12 passed / 12 total
- known F# record-inference finding: resolved in Domain, later resurfaced in Temporal and therefore not eligible to be considered permanently eliminated yet

## Initial ratchets

- `build.compiler-errors`: best accepted = 0, lower is better, fail on deterioration.
- `build.compiler-warnings`: best accepted = 0, lower is better, fail on deterioration.
- Test counts are observed but are not used as a naive higher-is-better quality ratchet.
- Unavailable evidence never satisfies a ratchet.

## Promotion rule

Promotion condition satisfied by GitHub Actions run `36186590248`. Future changes are evaluated against the accepted ratchets and versioned metric semantics; unavailable evidence never counts as satisfying a ratchet.
