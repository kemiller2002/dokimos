---
id: RQ-DOKIMOS-ARCH-001
title: Objectively verifiable architectural rules for reliability and cache readiness
status: draft
version: 0.1.0
owners: [dokimos-maintainers]
created: 2026-11-25
updated: 2026-11-25
priority: high
related_documents:
  - RQ-DOKIMOS-REL-001
  - RQ-ROS-2026-A038
tags: [architecture, reliability, cache-readiness, verification, rules]
provenance:
  contributions:
    EXE-20261125T201448769Z-55568c41:
      operations: [created]
      at: 2026-11-25T20:14:54.071Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0022: INIT-012 architectural verification rules (draft)"
---

# Objectively verifiable architectural rules for reliability and cache readiness

**Status: draft (proposed under INIT-012; owner-approved tenets in echelon-organization-administration #12 and
#14).** Nothing here is implemented. Portfolio requirements (EAR-*): [INIT-012 index](https://github.com/kemiller2002/echelon-organization-administration/blob/claude/echelon-availability-architecture-ezhitq/application-governance/availability-architecture/00-INDEX.md).

## Scope boundary

Dokimos verifies **static, reproducible evidence** about code and dependencies. It SHALL NOT report runtime
reliability, availability or recovery as established. Its findings feed the per-repository declaration
(Praxis RQ-ROS-2026-A038) only as evidence of class `automated`.

| Verification class | Example | Dokimos role |
|---|---|---|
| Automatically verifiable | No cache-provider dependency; API project does not reference persistence internals | **Verify** (rules below) |
| Integration-test verifiable | Duplicate submission yields one command; deploy under load drops nothing | Record test results as evidence (DOK-REL-*); do not judge |
| Operationally verifiable | Failover drill, restore drill, SLO attainment | Out of scope |
| Architectural review | Request class correctness; honesty of receipt copy | Out of scope |

## Rules (candidate)

Each rule activates only in repositories whose `.echelon/reliability.json` declares the corresponding tenet
`applicable`; elsewhere it reports `not-applicable`, never a pass.

- **DOK-ARCH-001 Thin API boundary (EAR-API-001).** Configured API projects reference domain logic only through
  declared contract projects; any other project reference is a finding. Uses the existing project/package-reference
  boundary analysis (`Dokimos.Core.Architecture`).
- **DOK-ARCH-002 Execution module independence (UNB-AVL-019, ADR-EAR-004).** The configured execution project
  references no application domain project.
- **DOK-ARCH-003 Declared request class (EAR-API-002).** Every operation listed in the repository's published
  operation registry snapshot carries exactly one request class from the controlled vocabulary.
- **DOK-ARCH-004 Declared consistency class (EAR-QCS-001).** Every query in the published query registry snapshot
  declares a consistency class.
- **DOK-ARCH-005 Cache dependency ban (EAR-QRY-008).** No cache-provider or distributed-cache package (for example
  `StackExchange.Redis`, `Microsoft.Extensions.Caching.StackExchangeRedis`, `Microsoft.Extensions.Caching.Memory`
  in application projects) unless an allow-list entry names an accepted decision record.
- **DOK-ARCH-006 Infrastructure dependency ban (INIT-012 restrictions).** No broker or distributed-lock client
  packages (Kafka, RabbitMQ, Redis-based locks) unless an allow-list entry names an accepted decision record.
- **DOK-ARCH-007 Parameterized persistence (EAR-SEC-005, SEC-DB-001).** No SQL text built by string concatenation or
  interpolation in configured persistence modules (heuristic; reported with confidence, never as proof of absence).
- **DOK-ARCH-008 Ratchet.** Findings for DOK-ARCH-001..007 may not increase relative to the recorded baseline
  (existing ratchet mechanism, DOK-R00N).

## Acceptance (for each rule, before it leaves candidate status)

Deterministic fixtures with a known-violating and a known-clean project; false-positive rate recorded on at least
one real repository; output conforms to the existing findings schema; documentation states what a pass does
**not** establish.
