# Dokimos Architecture

## Boundary

Dokimos is a quality evidence and interpretation system. It does not own security policy, engineering workflow state, or application-domain legality.

## Pipeline

```
Source + VCS evidence
        |
   Analyzer adapters
        |
 Normalized observations
        |
 Immutable snapshot store
        |
 Comparison / lifecycle engine
        |
 Policy + baseline evaluation
        |
 Machine results / Forma UI / Folio report
```

## Evidence before judgment

The central architectural rule is that an observation is not a judgment.

An observation records a measured fact under a particular measurement definition. A policy evaluates observations. This allows thresholds and organizational policy to change without falsifying history.

## Identity

Snapshots have durable IDs. Metric definitions have durable IDs plus versions. Findings use stable fingerprints derived from rule identity, semantic scope, and location evidence; fingerprints carry confidence when source movement makes identity uncertain.

## Storage

The first implementation uses versioned JSON artifacts suitable for repository/CI storage. Storage is behind a boundary so a hosted database or service can be added later without changing the domain.

Large raw analyzer payloads may be referenced rather than embedded, but normalized evidence required to reproduce a Dokimos judgment must remain available.

## Temporal model

A comparison is explicit: current snapshot + comparison baseline + policy version -> comparison result.

Trend calculations operate over compatible observations. Incompatible metric-definition versions are not silently joined.

## Hotspots

Hotspots are derived from at least two independent dimensions:
1. structural quality/risk;
2. temporal change/churn.

The system preserves the contributing dimensions. Composite ranking may aid navigation but never replaces evidence.

## State direction

The implementation favors discriminated unions and constrained constructors for:
- available / unavailable / failed observations;
- finding lifecycle;
- threshold disposition;
- comparison compatibility;
- baseline selection;
- provenance completeness.

External effects such as reading Git history, invoking analyzers, and writing artifacts remain explicit boundaries.

## Initial projects

- `Dokimos.Domain`: pure domain types and rules.
- `Dokimos.Core`: orchestration, comparison, trend, policy.
- `Dokimos.Cli`: command surface.
- `Dokimos.Domain.Tests`: domain invariants.
- Later analyzer projects are isolated adapters.

## Evolution rule

EDF/Ordo research is still evolving. Dokimos therefore depends on narrow state/evidence principles rather than copying experimental framework internals into its public domain. New research can tighten invariants without forcing historical evidence rewrites.
