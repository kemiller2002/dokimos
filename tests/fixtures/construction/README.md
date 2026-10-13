# Construction verification fixtures

Pinned, read-only copies of Ordo's construction fixtures, used as independent
inputs to `dokimos construction verify` tests. Do not edit them to make a test
pass; adversarial variants are derived from them inside the tests.

Provenance: `kemiller2002/ordo` at commit
`66c31bf` (branch `feat/gh-69-construction-package`, which carries the
construction engine `ordo.construction/1`).

| Here | Ordo path |
|---|---|
| `time-entry-timer/{source-manifest,blueprint,plan}.json`, `sources/` | `tests/fixtures/construction/time-entry-timer/` |
| `time-entry-timer/generated/Timer/` | `tests/Ordo.Construction.Tests/Golden/time-entry-timer/generated/Timer/` |
| `door-synthetic/{source-manifest,blueprint,plan}.json`, `sources/` | `tests/fixtures/construction/door-synthetic/` |
| `door-synthetic/generated/Door/` | `tests/Ordo.Construction.Tests/Golden/door-synthetic/generated/Door/` |

The generated files and `construction-manifest.json` are Ordo's own output.
Their `structuralChecks` are derived from the plan and are **not** independent
acceptance evidence. The independent behavioral oracle for the timer is
`oracles/construction/time-entry-timer/oracle.json`, written by Dokimos from
`time-entry-timer/sources/` only.

## `pilot-01-b1/` (CONSTRUCT-008 mechanical-first checks)

- `candidate.bundle` and `plan.json` are byte-for-byte copies of
  `kemiller2002/research-documents`
  `research/construction-by-design/time-entry-timer-01/results/B1/candidate.bundle`
  and `.../B1/outbox/plan.json` (sha256 `ab1355d6...` and `bf26bd20...`). The
  bundle holds the pilot skeleton (`4f1e14c`), the Praxis host's apply commit
  (`f7f477f`), the handwritten commit (`35ead6f`) and two earlier `try`
  applications of the same plan (`0cbcdd6`, `87d2248`), whose generated files
  are the same blobs as `f7f477f`.
- `prepared.json`, `decisions.json`, `dispositions.json` and
  `host-observations.json` are **synthetic**, written by Dokimos to exercise
  the mechanical-first checks: pilot 01 predates the prepared artifact. They
  follow the field list in `docs/construction-verification.md` and include an
  unknown field to prove it is ignored. They are not evidence about what the
  B1 agent decided.

## `time-entry-timer-v3/` (CONSTRUCT-010 decision completeness)

Source: `kemiller2002/ordo` PR #78 at commit `4d3c827` (CONSTRUCT-010 phase 4,
vocabulary 3). The timer `source-manifest.json` and `blueprint.json` there are
byte-identical to `time-entry-timer/`.

- `ordo-golden/` holds byte-for-byte copies of Ordo's fixtures and goldens:
  `construction-decisions-v2.json`, `plan.v3-open.json`, `plan.v2-remain.json`,
  `plan.v2-proceed.json` (`tests/fixtures/construction/time-entry-timer/`) and
  `generated/TimerOpen/` (`tests/Ordo.Construction.Tests/Golden/time-entry-timer-v3-open/`).
- `ordo-output/` is Ordo's own output, produced by running Ordo at `4d3c827`
  (`prepareCohort` with authorized `DEC-TIMER-STATES`, `generateDefaultPlan`
  vocabulary 3, `ConstructionClassification.classify`, `Construction.construct`,
  `buildLedger` with no evidence) over `ordo-golden/construction-decisions-v2.json`:
  `default-plan.json`, `classification.json`, `ledger.json`, `generated/Timer/`.
  Dokimos does not read `classification.json` at runtime; tests use it only to
  prove that Dokimos's own classification matches.
- `ordo-output-open/` is the same pipeline over
  `synthetic/construction-decisions-v2-open.json`, which differs from the v2
  records only in `"unlisted": {"disposition": "undecided"}` (authored by
  Dokimos to obtain Ordo's undecided-pair output).
- `synthetic/` holds Dokimos-authored wrappers: `prepared*.json` (the
  `praxis.construction-prepared/1` shape around Ordo's default plan),
  `dispositions*.json` and `approvals*.json`. The approvals are **not** a real
  operator approval.

## `time-entry-expanded-01/` (frozen expanded ECIR)

Byte-for-byte copies from `kemiller2002/research-documents` at `f6778c2`,
`research/construction-by-design/time-entry-expanded-01/harness/`:
`ecir/{source-manifest,blueprint,construction-decisions}.json`,
`ecir/generated/{construction-plan,default-plan,ledger}.json` (Ordo 1.9.0,
vocabulary 2) and `operator-approval.json`.
