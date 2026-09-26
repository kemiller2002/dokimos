# DOK-PROV-001 — `agent-*` heuristic identifiers are not authorship claims

Status: accepted interpretation rule (requirement R0.18, decision DF-GOV-012).

## Identifiers covered

| Identifier | Where | What it actually measures |
|---|---|---|
| `agent-generated-risk-pattern` | finding kind / finding id prefix `correlation:agent-generated-risk-pattern:` | type-weakening or dependency-addition signals coinciding with production change that lacks test-change evidence |
| `agent-quality` | `Source` of `quality.type-weakening-indicators` and `quality.scaffolding-indicators` metrics | counts of `obj`/string-keyed map usage and TODO/FIXME markers |
| `AgentQualityIndicators`, `agent` | analysis/measure JSON (`Agent` field) | the same indicators per file |

## Rule

These names describe **code patterns** that are common in generated code. They are
computed from source text and change history, never from identity. They do not
say that an agent wrote the code, and a human-written file can match them as
readily as an agent-written one.

- Reports, UIs, and aggregations MUST NOT present these identifiers as evidence of
  agent (or any particular actor's) authorship, and MUST NOT group or rank them by
  presumed author.
- Authorship of measured code comes only from the measured change's provenance
  record, carried under the snapshot provenance `sources` (R0.12). The measuring
  actor is the snapshot's `created` contribution (R0.11). Neither is inferred.
- Analyses that want "quality of agent-authored changes" MUST join on those
  provenance records, not on these identifiers.

## Why the names are not changed

Finding ids, metric sources, and ratchets are stored in accepted snapshots
(`baselines/accepted-snapshot.json`). Renaming would break finding identity across
the history (R3) and silently rewrite historical meaning (R0.4, R10). A future
rename, if any, needs a new metric-definition version and an explicit migration.
