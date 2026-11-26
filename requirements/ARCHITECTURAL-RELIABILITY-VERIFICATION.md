---
id: RQ-DOKIMOS-ARCH-001
title: Objectively verifiable architectural rules for reliability and cache readiness
status: draft
version: 0.2.0
owners: [dokimos-maintainers]
created: 2026-11-25
updated: 2026-11-26
priority: high
related_documents:
  - RQ-DOKIMOS-REL-001
  - RQ-ROS-2026-A038
tags: [architecture, reliability, cache-readiness, verification, rules]
provenance:
  contributions:
    EXE-20261125T201448769Z-55568c41:
      operations: [created, modified]
      at: 2026-11-25T20:14:54.071Z
      last: 2026-11-26T00:34:18.742Z
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
#14).**

**Implementation status (2026-11-26, WI-0022):** DOK-ARCH-001, DOK-ARCH-005 and DOK-ARCH-006 are **implemented**
(`dokimos architecture check`; rule docs `docs/rules/DOK-ARCH-001.md`, `DOK-ARCH-005.md`, `DOK-ARCH-006.md`).
DOK-ARCH-002, -003, -004, -007 and -008 remain **candidate** (not implemented). Portfolio requirements (EAR-*): [INIT-012 index](https://github.com/kemiller2002/echelon-organization-administration/blob/claude/echelon-availability-architecture-ezhitq/application-governance/availability-architecture/00-INDEX.md).

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

## Implementation (DOK-ARCH-001, -005, -006)

| Rule | Status | Activating tenet | Implementation |
|---|---|---|---|
| DOK-ARCH-001 | implemented | `continuousAvailability` | `ReliabilityArchitecture` (thin-API check over `ProjectReference` edges); boundaries in `quality/baseline.json` `configuration.apiBoundaries` |
| DOK-ARCH-002 | candidate | | |
| DOK-ARCH-003 | candidate | | |
| DOK-ARCH-004 | candidate | | |
| DOK-ARCH-005 | implemented | `cacheReadiness` | `ReliabilityArchitecture.cacheProviders` over `PackageReference`/`GlobalPackageReference` |
| DOK-ARCH-006 | implemented | `continuousAvailability` | `ReliabilityArchitecture.infrastructureClients` over the same references |
| DOK-ARCH-007 | candidate | | |
| DOK-ARCH-008 | candidate | | |

- **Activation.** A rule is active when `.echelon/reliability.json` declares its tenet `applicable` or
  `partially-applicable`. `not-applicable` or no declaration reports `not-applicable`; an undecodable declaration
  reports `unavailable`. When no rule is active the verdict is `not-evaluated`, never `pass`.
- **Configuration and allow-list** reuse the existing Dokimos repository configuration: `apiBoundaries` sits beside
  `forbiddenReferences` in `quality/baseline.json` (loosening is caught by `ratchet baseline diff`), and accepted
  dependencies are `quality/exceptions.json` entries (`ruleId` `DOK-ARCH-00N`, exact scope) whose `evidence` must
  name a decision record id (`DF-...`/`ADR-...`); otherwise the exception is invalid (exit 6). Dokimos checks that a
  decision record is named, not that it is accepted.
- **Purity.** Detection is a pure function of parsed project files, the declaration, the configuration and the
  exceptions (`ReliabilityArchitecture.check`); file-system reads are confined to `Dokimos.Cli.ArchitectureCommand`.
- **Output.** `dokimos.architecture` 1.0.0 report (`schemas/dokimos-architecture.schema.json`); exit 0 pass or
  not-evaluated, 4 violation, 6 invalid/expired exceptions, 3 unavailable. Exceptions are reported with the existing
  ratchet exception DTO.
- **Tests.** Known-violating and known-clean fixtures plus not-applicable, not-configured, unavailable and exception
  cases: `tests/Dokimos.Core.Tests/ReliabilityArchitectureTests.fs`, `tests/Dokimos.Cli.Tests/ArchitectureCliTests.fs`.

### False-positive measurement (2026-11-26)

`dokimos architecture check` was run against 13 real .NET repositories with a scratch declaration making every
tenet `applicable` (no repository carries a real `.echelon/reliability.json` yet): praxis `3a1db3c`, and under
`ro/`: aegis `ba9c463`, arca `f4e1ddd`, conditor `219b873`, echelon-diagnostic-framework `710d54c`, fides `762fff0`,
forma `767b061`, forma-studio `d688756`, limen `e6337ef`, ordo `bc63c41`, strata `61f5ec9`, tutela `0634875`,
vigila `bb6dabd`.

| Rule | Project files | Package-reference lines | Findings | False positives |
|---|---|---|---|---|
| DOK-ARCH-005 | 144 | 95 | 0 | 0 |
| DOK-ARCH-006 | 144 | 95 | 0 | 0 |
| DOK-ARCH-001 | fides (`Fides.Hosting.Aws` -> contract `Fides.Hosting`), vigila (`Vigila.Host.GitHub` -> contract `Vigila.Application`) | | 0 | 0 |

Every package id in these repositories was reviewed by hand against the ban lists; none is a cache provider,
broker or distributed-lock client, so zero findings is also the expected true count (no false negatives among
direct references). Near-miss ids such as `AWSSDK.SecretsManager` and `Npgsql` were correctly not flagged. The
sample contains no true positive, so detection of real violations rests on the fixtures; the DOK-ARCH-001 boundaries
were chosen for the measurement, not declared by those repositories' owners.

