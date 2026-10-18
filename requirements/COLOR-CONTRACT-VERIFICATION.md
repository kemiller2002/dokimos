---
id: RQ-DOKIMOS-COLOR-001
title: Independently verify Forma color-token contracts
status: draft
version: 1.0.0
owners: [dokimos-maintainers]
created: 2026-10-18
updated: 2026-10-18
priority: critical
related_documents:
  - https://github.com/kemiller2002/dokimos/issues/41
tags: [css, color, verification, evidence]
derived_from: [INPUT-a129ad77c69af1be]
provenance:
  contributions:
    EXE-20261018T124842385Z-b72bbb9b:
      operations: [created]
      at: 2026-10-18T12:53:00.522Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Canonical ingestion of claimed requirement source"
---

# Independently verify Forma color-token contracts

This draft canonically records Dokimos issue #41. Ingestion does not prove
the analyzer exists or approve any product palette.

## Finding

Summa references unsupported Forma 0.6.1 properties
`--ef-color-action-primary`, `--ef-color-text-link`,
`--ef-color-focus`, and `--ef-color-text-danger`. Browser fallback colors
mask the missing contract. Current conformance checks prohibit redefining
shared tokens but do not prove that referenced shared tokens exist.

## Requirements

- **DOKIMOS-COLOR-001.1:** Add a deterministic static and contract analyzer for CSS and supported embedded styling that resolves `var(--ef-*)` references against the pinned installed Forma public-token manifest. Reject undefined public names and unknown/deprecated aliases without an approved migration.
- **DOKIMOS-COLOR-001.2:** Identify unauthorized literal color/fallback values, duplicated component color rules, and semantic overrides that bypass approved product-specific roles. Permit non-Forma application-owned variables and explicitly approved exceptions.
- **DOKIMOS-COLOR-001.3:** Detect dangerous light/dark/forced-colors differences where static evidence is sufficient, then invoke or consume real-browser computed-color/contrast evidence for dynamic mixes and surfaces. Static analysis MUST NOT claim to prove runtime contrast or aesthetic suitability.
- **DOKIMOS-COLOR-001.4:** Report file/line, token, release/version, rule ID, affected usage, severity, and supported alternatives. Produce machine-readable evidence suitable for Praxis acceptance gates and Visual Engineering review.
- **DOKIMOS-COLOR-001.5:** Add adversarial and mutation fixtures proving each guard fails on wrong-but-plausible agent code and passes valid product palettes.

## Acceptance criteria

- Baseline Summa fixtures reject all four real unsupported references even when CSS fallbacks are supplied; a valid current Forma reference passes.
- Negative tests avoid false positives on `--summa-*`, correct non-brand functional fallbacks, approved custom roles, and unrelated CSS.
- Unevaluated themes or surfaces never yield PASS; unknown and inconclusive cases are explicit.
- Analysis runs locally and in repository CI with no hidden cloud dependency or unnecessary runtime dependency.

## Dependencies and authority

Dependencies are Forma issues
[#159](https://github.com/kemiller2002/forma/issues/159) and
[#160](https://github.com/kemiller2002/forma/issues/160), and Visual
Engineering [#49](https://github.com/kemiller2002/visual-engineering/issues/49).
Independent verification MUST NOT trust the coding agent's checklist as proof.
Implementation remains governed by Dokimos
[#41](https://github.com/kemiller2002/dokimos/issues/41).
