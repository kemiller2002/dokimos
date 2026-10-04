# Dokimos Quality-Hardening Requirements

Status: proposed requirements
Date: 2026-10-04

These requirements make architectural shortcuts and unsafe failure defaults visible as evidence. Dokimos remains an evidence and policy system, not the semantic authority for application architecture.

## DOK-QUAL-001 - Responsibility concentration evidence
Priority: high

Measure and report modules/files that accumulate multiple unrelated responsibilities, external-effect categories, or repeated cross-tier references. The signal SHALL be decomposable and SHALL NOT reduce quality to a raw line-count rule.

Evidence SHOULD include file growth, public-surface growth, dependency fan-out, external-effect categories, churn, complexity, and change concentration.

## DOK-QUAL-002 - Four-tier boundary leakage analysis
Priority: high

Add rules capable of identifying likely Domain/Application/Infrastructure/CLI boundary leakage in repositories that declare the four-tier architecture. Examples include network/filesystem/process/credential APIs in CLI or Domain code, and application orchestration embedded in presentation adapters.

Rules SHALL be repository-configurable and trace findings to concrete references/imports.

## DOK-QUAL-003 - Unsafe persistence fallback findings
Priority: critical

Detect high-risk patterns where persistent required/control state is parsed inside a catch-all that returns empty/default/healthy state, where errors are swallowed, or where persistent JSON lacks a declared schema/version contract.

These are quality findings, not automatic semantic judgments. A repository may suppress with documented rationale and expiry.

## DOK-QUAL-004 - Stringly protocol and identity evidence
Priority: medium

Detect repeated provider/model/state identity decisions based on ad-hoc string contains/prefix comparisons when the repository declares typed identities or adapter boundaries. Distinguish protocol parsing at a boundary from stringly policy in the core.

## DOK-QUAL-005 - Change-quality ratchet
Priority: high

Provide a diff-oriented gate that can prohibit newly introduced architecture violations, unsafe fallback findings, excessive responsibility concentration, and new unversioned persistent-state surfaces while baselining legacy debt.

A change SHALL be able to improve legacy code incrementally without being forced to clean the whole repository.

## DOK-QUAL-006 - Failure-path test evidence
Priority: medium

Record whether changed high-risk persistence/boundary modules have tests exercising missing, malformed, unavailable, timeout, concurrency, compatibility, and recovery behavior where those categories are applicable. This SHALL be evidence with explicit limitations, not a claim that test names prove semantic correctness.

## DOK-QUAL-007 - Architecture hotspot reporting
Priority: medium

Combine structural findings with churn so teams can see modules that are both difficult and frequently modified. Highlight newly created hotspots before they become entrenched.

## DOK-QUAL-008 - Praxis-consumable quality profile
Priority: high

Publish a stable machine-readable quality-gate profile intended for Praxis completion evidence. It SHALL expose pass/fail/warn/unavailable plus exact findings and policy version, and SHALL preserve Dokimos as the owner of quality analysis.

## Policy principle

Dokimos should detect pressure toward poor structure early, but metrics are evidence. A 900-line cohesive parser may be acceptable; a 300-line CLI module owning HTTP, persistence, locking, provider policy, orchestration, and presentation is a stronger responsibility-concentration signal. Semantic review decides the disposition.
