# DOK-FS-001 — Ambiguous F# record inference at evidence boundaries

Status: candidate rule discovered through Dokimos dogfooding.

## Observation

Dokimos has encountered the same compiler-inference failure in multiple modules where related records intentionally share field names. F# may infer the wrong record type before later field access disambiguates intent.

## Rule

At public/core transformation boundaries involving record types with overlapping labels, explicitly type parameters when inference would otherwise depend on later field access.

## Why this is a candidate quality rule

The rule is not "all parameters require annotations." It applies only when evidence demonstrates ambiguity risk. The purpose is to make the intended domain boundary explicit and prevent a known recurring failure mode.

## Evidence history

- First observed: `Dokimos.Domain.Comparison`.
- Resolved there with explicit `Observation` parameters.
- Resurfaced: `Dokimos.Core.Temporal`.
- Resurfaced again: `Dokimos.Core.Regions`.
- Region correction: explicit `RegionIdentity`, `RegionChange list`, and `RegionHistory` boundaries.

The rule remains candidate until an analyzer can identify the risky pattern without producing unacceptable false positives.
