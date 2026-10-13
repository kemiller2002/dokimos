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
