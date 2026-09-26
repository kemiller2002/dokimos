# DOK-COMPLEXITY-001 — Lexical complexity proxy

Status: experimental indicator

## Purpose

Provide deterministic early evidence about branching-shaped source while Dokimos develops an AST-aware F# complexity analyzer.

## Evidence dimensions

The proxy exposes decision points, boolean operators, match branches, nesting peak, and a composite proxy value. The dimensions remain available independently.

## Known limitation

F# discriminated-union and pattern-matching code can contain many match arms while remaining explicit, exhaustive, and maintainable. Therefore a high lexical proxy value is not by itself evidence of poor code quality.

The first Dokimos self-analysis demonstrated this limitation: `src/Dokimos.Domain/Domain.fs` produced a proxy value of 30 and temporal churn of 116. The temporal evidence makes the file worth inspection, but the lexical score does not establish that the domain model is over-complex.

## Policy

- Do not gate on this proxy alone.
- Correlations using this proxy remain experimental.
- Preserve the contributing dimensions and source scope.
- Prefer AST-aware function/member complexity before promoting a complexity correlation to an enforceable rule.
- Pattern-match exhaustiveness and declarative union handling must not be penalized merely for having multiple cases.

## Promotion evidence

Promotion requires an AST-aware implementation, representative F# fixtures, and evidence that the metric distinguishes behavioral branching from declarative pattern matching with acceptable false-positive rates.
