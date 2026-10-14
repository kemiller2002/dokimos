# Pending Praxis intake: DOKIMOS-COLOR-001

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted for later Praxis ingestion: 2026-10-14
- Target repository: `kemiller2002/dokimos`
- Source: https://github.com/kemiller2002/dokimos/issues/41
- Source issue number: #41
- Source issue title: DOKIMOS-COLOR-001: independently reject undefined color tokens, unsafe fallbacks, and theme drift
- Proposed scope identifier: `DOKIMOS-COLOR-001` (preserve actual requirement IDs below; do not assume a work-item ID has been allocated)

## Ingestion instructions

Use the supported `praxis inbox claim`, `praxis inbox derive`, and `praxis inbox complete` lifecycle when a Praxis runtime is available. Derive and version canonical requirements, explicit unknowns/decisions, and repository-local tracked work items according to this repository's installed governance policy. Preserve each numbered requirement and acceptance criterion below and their source provenance. Link derived work back to the original issue and input claim, check for existing/duplicate work first, and preserve cross-repository dependencies. Do not mark this input processed, or mark work accepted/complete, until derived records are reconciled and validated. Ingestion is separate from implementation and visual design approval.

## Original requirements and acceptance criteria from GitHub issue #41

## Finding
[Summa's application stylesheet](https://github.com/kemiller2002/summa/blob/main/app/summa-app.css) references unsupported Forma 0.6.1 properties including `--ef-color-action-primary`, `--ef-color-text-link`, `--ef-color-focus`, and `--ef-color-text-danger`. The browser substitutes literal fallback colors. Summa's existing conformance tests prohibit redefining shared tokens but do not check whether *referenced* shared tokens exist.

## Requirements
- **DOKIMOS-COLOR-001.1:** Add a deterministic static and contract analyzer for CSS and supported embedded styling that resolves `var(--ef-*)` references against the *pinned* installed Forma public-token manifest (see [Forma #159](https://github.com/kemiller2002/forma/issues/159)). Reject undefined public names and unknown/deprecated aliases without approved migration.
- **DOKIMOS-COLOR-001.2:** Identify unauthorized literal color/fallback values, duplicated component color rules, and semantic overrides that bypass approved product-specific token roles; permit non-Forma application-owned CSS variables and explicitly approved exceptions.
- **DOKIMOS-COLOR-001.3:** Detect dangerous light/dark/forced-colors differences where static evidence is sufficient, then invoke or consume real-browser computed-color/contrast evidence for dynamic mixes and surfaces; do not claim static analysis proves runtime contrast or aesthetic suitability.
- **DOKIMOS-COLOR-001.4:** Report file/line, token, release/version, rule ID, affected usage, severity, and supported alternatives. Create machine-readable evidence suitable for Praxis acceptance gates and Visual Engineering review.
- **DOKIMOS-COLOR-001.5:** Add adversarial/mutation fixtures to prove each guard actually fails on wrong-but-plausible agent code and passes valid product palettes.

## Acceptance criteria
- Baseline Summa fixtures reject each of the four real unsupported references even though each supplies a CSS fallback; a valid current Forma reference passes.
- Negative tests avoid false positives on `--summa-*`, correct `var()` fallbacks for non-brand functional values, approved custom roles, and unrelated CSS.
- No 'PASS' for unevaluated themes or surfaces: unknown/inconclusive cases are explicit.
- Analysis can be run locally and in repository CI, with no hidden cloud dependency and no unnecessary runtime dependencies.

## Dependencies
[Forma #159 public tokens](https://github.com/kemiller2002/forma/issues/159); [Forma #160 semantic/contrast tests](https://github.com/kemiller2002/forma/issues/160); [Visual Engineering #49](https://github.com/kemiller2002/visual-engineering/issues/49). Independent verification should not trust the coding agent's own checklist as proof.
