# BASELINE-0001 — Initial accepted Dokimos baseline

Status: provisional until the temporal/region slice is green.

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

This baseline becomes accepted after the temporal and repeated-region implementation reaches a green CI run and that run is recorded as an immutable observation.
