# Dokimos decisions

Material decisions use `DF-` records under `research/decisions/`. This compact
table is a navigation view, not a replacement for those records.

| Date | Decision | Status | Rationale | Record |
|---|---|---|---|---|
| 2026-09-28 | Use ROS 3.1.4 as a measured greenfield pilot. | provisional | Test portability and operational value on a real beginning project. | Not yet promoted to a `DF-` record |
| 2026-09-28 | Build the public website as a dependency-free static Node build in `site/`, deployed from `dist/` by GitHub Pages Actions. | accepted | Matches the Echelon Foundry build philosophy; reads real evidence from the repository and fails when site claims drift from it. | `requirements/WEBSITE-REQUIREMENTS.md`, `docs/website/SITE.md` |
| 2026-09-29 | Serialize only explicit wire DTOs (`Contracts.fs`); decode with explicit field decoders (`Json.fs`). F# unions never reach System.Text.Json. | accepted | Fixes the `MetricChangeKind` serialization fault without weakening the domain or adding a union-serialization library. | `requirements/OPERATIONAL-REQUIREMENTS.md` DOK-OPS-001 |
| 2026-09-29 | Snapshot metrics carry `Dokimos.Domain.Measurement`; snapshot schema 2.0.0 adds `Contract`, `Producer` provenance and `Performance`. 1.0.0 stays readable with `Producer = None`; any other version is rejected. | accepted | Removes stringly-typed state from the core, keeps historical evidence readable without back-filling provenance. | DOK-OPS-022, DOK-OPS-023 |
| 2026-09-29 | Snapshot identity is `<repository>@<revision>:<evidence-digest-16>`; the digest excludes collection time and durations. | accepted | Identical re-analysis is idempotent; newer analyzers create new evidence (R0.10) instead of colliding. | DOK-OPS-005 |
| 2026-09-29 | Policy evaluation reuses `Policy.evaluateThreshold/evaluateRatchet`; `GateResult` gains `ObservedOnly`, `EvidenceUnavailable`, `CollectionFailed`, `Incompatible`. Exit codes: 0 continue, 1 fault, 2 invalid invocation/config, 3 evidence unavailable, 4 policy failure, 5 store conflict. | accepted | One policy vocabulary; unavailable evidence can never pass silently. | DOK-OPS-002 |
| 2026-09-29 | `dokimos evaluate` (not `gate`) is the CLI verb. | accepted | No existing `gate` command; one verb only. | DOK-OPS-002 |
| 2026-09-29 | The published `schemas/dokimos-snapshot.schema.json` described a never-implemented camelCase design; it is replaced by a schema of the real emitted contract (1.0.0 and 2.0.0). | accepted | Schemas must describe what Dokimos actually emits. | DOK-OPS-022 |
