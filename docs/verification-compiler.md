# Verification compiler (`dokimos verification`)

Turns a frozen verification plan into test-writing work, and reports, at any moment, which obligations are verified. "Verified" means: every bound test identity exists, is structurally real, ran in a suite that executes it, and passed in results recorded for the candidate SHA.

The application-specific test bodies are written after kickoff (by agents). Dokimos generates the work and the skeletons, and it refuses to count anything it cannot prove.

## Commands

| Command | Input | Output | Exit |
|---|---|---|---|
| `compile --indy-execution-plan F --indy-verification-plan F [--out F]` | the frozen Indy Init plan, read as it is | `dokimos.verification-obligations/1` | 0; 4 with `dokimos.verification-refusals/1` when an obligation has no requirement, criterion or bound identity |
| `compile --obligations F` | an obligation set | the set, validated | as above |
| `scaffold --obligations F [--root .] [--first REQ,...] [--write true] [--append true] [--import M] [--work F]` | the obligation set and the source tree | `dokimos.verification-work/1`: ranked work packages and `unmapped` identities; with `--write true`, skeleton files | 0 |
| `coverage --obligations F [--root .] [--results MANIFEST] [--candidate SHA] [--probes F]... [--scope REQ,...] [--require-verified true] [--out F]` | the set, the tree, `dokimos.verification-results/1`, and `dokimos.fault-probe-results/1` | `dokimos.verification-coverage/1` | 0; with `--require-verified true`, 4 unless every in-scope obligation is verified |
| `probe --spec F --sha SHA [--root .] [--timeout-seconds N] [--out F]` | `dokimos.fault-probes/1` | `dokimos.fault-probe-results/1` | 0 when every fault is detected, 4 otherwise |

## Identities

- `.NET`: `dotnet:<TestMethod className>.<TestMethod name>`, the identity TRX records.
- Playwright: `playwright:<file relative to the suite test root> > <describe titles…> > <title> [<project>]`.
- Anything else with a scheme prefix, such as `praxis:independent-review`, is **record evidence**. Dokimos reports it as `blocked`, for its producer, and never judges it.

## Statuses

Identity statuses:

| Status | Meaning |
|---|---|
| `not-generated` | No test with the exact identity. Near-miss names are listed as probable drift. |
| `invalid` | The test exists but cannot count. Reasons: placeholder (`DOKIMOS-UNIMPLEMENTED`, not implemented); skipped (static `Skip=`, `test.skip`/`fixme`, a skip inside the body, or a skip at run time); no assertion; only trivially-true assertions; excluded from execution (not compiled by its `.fsproj`, outside the runner paths, not selected by `--filter`, wrong `--project`); does not reach the suite's `requiredBoundary` (exercises only helpers); declared twice. |
| `generated` | Valid, but its suite has no result at the candidate. |
| `not-executed` | The suite ran at the candidate, but this identity has no result. |
| `passed` / `failed` | The result at the candidate. A flaky or retried test counts as failed. |
| `blocked` | Record evidence, or results that exist only for another SHA. |

Obligation statuses, by precedence: `failed` > `invalid` > `not-generated` > `blocked` > `not-run`; `verified` only when every bound identity passed.

Quality is reported separately:
- `fault-sensitive`: a probe at the candidate was detected.
- `passing (fault sensitivity not demonstrated)`: no probe was run.
- A probe that **survived** withdraws `verified` and makes the obligation `invalid`.

Results are admitted only when `sourceSha` equals the candidate. No result is carried forward from another SHA.

## Scheduling

`scaffold` groups the identities that still need work by target file: one package per file, carrying only the criteria of that file's obligations. It ranks them in this order:

1. The `--first` (vertical-slice) requirements.
2. Criticality: security, then integration, live-provider, browser, unit.
3. Suite dependency depth.
4. Size.

Re-run it after each merge and only the remaining work comes back. There is no global barrier: implementation proceeds while later packages are written.

Skeletons fail by construction, and `coverage` classifies them as placeholders, so a scaffold can never be counted as verified. It shows as `invalid`, never as a silent *not run*.

## Fault probes

```json
{ "schema": "dokimos.fault-probes/1",
  "probes": [ { "id": "P-SAVE-BODY", "obligations": ["SAVE-001-O1"], "file": "src/App/Save.fs",
                "find": "Body = command.Body", "replace": "Body = \"\"",
                "command": ["dotnet", "test", "tests/App.Tests", "--filter", "FullyQualifiedName~SAVE001"] } ] }
```

Each probe runs on a disposable copy of the repository: first an unfaulted control, which must pass, then the faulted copy, which must fail. The anchor must occur exactly once in the file. The working tree is never modified.

## Evidence: Rehearsal 8 regression

Inputs: the frozen v4.1.1 plan, compiled with 0 refusals (613 obligations, 800 identities, 11 suites), and the Rehearsal 8 repository at its final application code (5e27176), read-only.

**Source scan only (1.5 s, no CI):** the per-suite results reproduce the hand-built T34 table exactly.

| Suite | Present and valid | Not generated | T34 |
|---|---|---|---|
| SU-OWN-DOTNET | 113 | 24 | 113 pass / 24 not run |
| SU-DOTNET-DETERMINISTIC | 71 | 45 | 71 / 45 |
| SU-DOTNET-INVARIANTS | 21 | 0 | 20 pass + 1 fail |
| SU-BROWSER-CHROMIUM | 88 | 193 | 88 / 193 |
| SU-BROWSER-WEBKIT | 23 | 49 | 23 / 49 |
| SU-DOTNET-LIVE | 60 | 81 | 60 / 81 |
| SU-BROWSER-LIVE | 0 | 17 | 0 / 17 |

**With the T34 CI results** (re-encoded as TRX and Playwright JSON at 5e27176):
- 203 verified, 1 failed (INV04), 334 not generated, 75 blocked.
- 63 of the blocked obligations lack only the `praxis:independent-review` record that T34 credited from the T33 review: 203 + 63 = **266**, T34's count.
- The same results relabelled with the previous candidate's SHA verify nothing: all 7 runs are rejected.

**Scaffold:** 131 packages, 409 identities, a median of 2 obligations per package, in 1.2 s.

## Known limitations

- Scanning works on lexical views (`SourceLexing`, `JavaScriptLexing`) plus indentation and brace scope. It is not a compiler:
  - A test produced by a helper or loop is not seen, and reads as `not-generated`.
  - An assertion made inside a helper is recognised only by a verb name (`assert*`, `check*`, `verify*`, `expect*`, `ensure*`, `should*`) or through a file-local definition that asserts.
- Semantic adequacy is not proven by structure. Fault probes are the evidence for it, and they are opt-in per obligation.
- `requiredBoundary` is declared per suite. Without it, a helper-only test is not detected.
- Only TRX, Playwright JSON and Playwright list logs are read.
