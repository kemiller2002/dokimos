# Independent recognition audit (`dokimos ecir recognition-audit`)

Tracking: WI-0008 (ORDO-PATTERN-001; Ordo
[kemiller2002/ordo#77](https://github.com/kemiller2002/ordo/pull/77)).

Before generating anything, Ordo's pattern recognition gives every
requirement in a cohort one outcome:
- `constructible`
- `decisionRequired`
- `delegate`
- `blocked`

Requirements outside construction are listed as `excluded`. Unsupported work
goes to an agent as bounded delegation packets (`ordo.delegation-contract/1`).

Dokimos checks that classification and those handoff bounds **without calling
Ordo**. It reads:
- the pinned manifest;
- the blueprint;
- the approved decision records;
- the operator's approval list;
- the report (`ordo.pattern-recognition/1`);
- the packet files.

From these it re-derives the precision rules. A report that calls a
requirement constructible must earn it from these inputs.

```
dokimos ecir recognition-audit --manifest FILE --blueprint FILE --report FILE \
  [--decisions FILE] [--approvals FILE] [--packets DIR]
```

Output contract: `dokimos.recognition-audit/1`. Any finding fails the
command as `recognition-audit-failed`. The audit checks classification and
bounds only; behavior is verified separately. Delegated code gets the same
standard as generated code: the frozen oracle through `dokimos behavior
verify`.

## Findings

| Code | Meaning |
|---|---|
| `recognition-false-constructible` | Called constructible, but one of these holds: no approved state-model transition cites it; a port cites it (part of its behavior is handwritten); a conflict owns it; an owning decision or the state-model record is unapproved or not on a decision node; or its disposition is not `modeled`. |
| `recognition-silent-default` | Constructible or delegated although an effect-failure disposition is undecided or an owning decision is unapproved. |
| `recognition-conflict-ignored` | Constructible or delegated although an open conflict owns it. |
| `recognition-requirement-missing` / `-duplicated` / `-invented` | Not every assigned requirement appears exactly once. |
| `recognition-manifest-mismatch` | The report is bound to another source manifest. |
| `recognition-decisions-digest-mismatch` | Decision records were supplied, but the report used none. |
| `recognition-cohort-missing` | The report's cohort is not a cohort of the blueprint. |
| `packet-missing` / `packet-unlisted` / `packet-digest-mismatch` / `packet-keys-mismatch` | Packet files and the report disagree. The digest is SHA-256 over the file bytes. |
| `delegation-uncovered` / `delegation-duplicated` | A delegated requirement is in no packet, or in more than one. |
| `packet-scope-violation` | A packet hands over a requirement that is neither delegated nor escalation-required. |
| `packet-boundary-overlap` | An allowed path overlaps a forbidden (generated) path. |
| `packet-generated-root-unprotected` | Generated code exists, but the packet forbids no path. |
| `packet-decision-context-missing` | An approved decision owning a packet requirement is not in the packet (OPT-15). |
| `packet-verification-missing` | A verification obligation owning a packet requirement is not in the packet. |
| `packet-escalation-missing` | A conflict owns a packet requirement, but escalation is not required. |

`DecisionsBinding` reports whether the decision file's bytes match the digest
the report binds:
- Ordo binds the canonical encoding, so a non-canonical file reports
  `not byte-identical` without being different records.
- This is reported, not failed.
- Every content check uses the supplied file either way.

## Evidence

- Fixture: `tests/fixtures/recognition/timer`, produced by Ordo.
- Tests: `tests/Dokimos.Core.Tests/RecognitionAuditTests.fs`. A clean report
  passes. Each check is shown to fire on a tampered report or packet.
- On the four frozen benchmark cohorts (timer, expanded time entry, Chrona,
  Summa), Ordo's reports and packets pass with no findings. Harness:
  `research-documents/research/pattern-construction/harness/recognize-slices.sh`.
