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
