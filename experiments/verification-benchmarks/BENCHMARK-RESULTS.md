# Verification-compiler benchmarks: results (2026-11-02)

Two deliberately different synthetic applications ran through the improved process. The goal is to prove the generic mechanism, not to predict Causa: these applications are about 50 times smaller.

## Protocol and provenance

Commits on branch `claude/verification-benchmarks`, oldest first:

| Step | Commit / time (UTC) | Who |
|---|---|---|
| Requirements, frozen interface contract and obligation set written by hand | 01:24:51 → | coordinator |
| `dokimos verification compile` + `scaffold --write true` (0.77 s and 0.63 s); test project files | `307fdbc` 01:26:56 | Dokimos (skeletons are unedited tool output) |
| B2 test bodies, from REQUIREMENTS.md and CONTRACT.md only | `21249ef` 01:30:19 | test agent; it did not read `src/` |
| B2 implementation, from REQUIREMENTS.md and CONTRACT.md only | `6584876` 01:30:20 | implementation agent; it did not read `tests/` |
| B1 test bodies, from REQUIREMENTS.md and CONTRACT.md only | `89ef1dd` 01:33:27 | test agent. It listed `src/` once to see whether the project existed and opened no source file. |
| B1 implementation, from REQUIREMENTS.md and CONTRACT.md only | `b63bb3c` 01:33:27 | implementation agent; it did not read `tests/` |
| Test runs, coverage, fault probes | at `b63bb3c` | coordinator |

- The four agents ran concurrently, with no shared files and no barrier between test writing and implementation.
- No test body was written before the benchmark.
- At kickoff, Dokimos coverage reported every identity `invalid` (placeholder): B1 11/11, B2 9/9 (`evidence/coverage-kickoff.json`).
- The fault probes were written by the coordinator, acting as verifier, after the implementation existed (they name code anchors).

## Benchmark 1: approval workflow (stateful, browser)

This is the vertical slice: a real Chromium browser, the HTTP server, the domain, a file store and a page reload. The server is the real executable, started by Playwright's `webServer`.

| Measure | Result |
|---|---|
| Obligations | 11 (6 requirements) |
| Generated identities | 8 .NET (xUnit) and 3 Playwright, all exact |
| Execution | .NET 8/8 passed in 11 s; Chromium 3/3 passed in 8 s (`evidence/results.json`, `playwright.json`) |
| Coverage at `b63bb3c` | **11/11 verified**, 6/6 requirements, `--require-verified` gate passed (exit 0) |
| Fault probes (86 s) | 6/6 detected; every faulted copy built first |
| Fault-sensitive obligations | 7/11; the other 4 are unprobed and labelled "fault sensitivity not demonstrated" |

Faults injected:
- **P-B1-BODY:** the decision note (command body) is dropped on save. Detected by APR-004-O1.
- **P-B1-READBACK:** save reports success without the read-back. Detected by APR-002-O2.
- **P-B1-ILLEGAL:** Approve is accepted from Draft. Detected by APR-001-O2.
- **P-B1-STALE:** a stale expected version overwrites the stored request. Detected by APR-003.
- **P-B1-LEAK:** the credential appears in a response header. Detected by APR-006-O1.
- **P-B1-UI:** every command is offered whatever the state. Detected by APR-005-O2 in the browser.

## Benchmark 2: evidence collection (provider boundary, security)

| Measure | Result |
|---|---|
| Obligations | 9 (5 requirements) |
| Generated identities | 9 .NET, using a real HTTP provider (HttpListener) on 127.0.0.1 |
| Execution | 9/9 passed in 14 s |
| Coverage at `b63bb3c` | **9/9 verified**, 5/5 requirements, gate passed |
| Fault probes (66 s) | 6/6 detected; every faulted copy built first |
| Fault-sensitive obligations | 6/9 |
| Calibration | a behaviour-neutral wording change **survived** (`evidence/calibration-*.json`), so the probes discriminate |

Faults injected:
- **P-B2-STALE:** tampered content is returned without the hash check.
- **P-B2-LEAK-DIAG:** the credential is written to a diagnostic line.
- **P-B2-UNAUTH:** 401/403 is reported as not found.
- **P-B2-TRUNCATE:** a read-back larger than 2 MiB is truncated to 1 MiB.
- **P-B2-TOKEN-URL:** the token is sent in the query string.
- **P-B2-ATTRIBUTION:** a record without a source is accepted.

`evidence/praxis-matrix.json` is the `praxis.verification-matrix/1` that Praxis's `work complete` gate consumes.

## Defects in the mechanism found by the benchmark, and fixed

1. **A fault that breaks compilation was counted as "detected".** Probes now declare a `build`. A faulted copy that does not build is a probe error, never a detection. The B2 probes were re-run with builds declared: still 6/6 detected.
2. **SHA binding held:** B2's probe results were recorded at a later repository SHA than its test results, and Dokimos credited 0 of the 6 detections until the tests were re-run at the same SHA. This is the intended behaviour, and it shows that results from different candidates are never combined.

## Resource use

| Item | Value |
|---|---|
| Elapsed time, requirements to both benchmarks fully verified | about 10 minutes (01:24:51 to about 01:35; probes and coverage followed) |
| Test-writing and implementation agents (harness-reported totals) | B1 tests 77,856 tokens / 225 s; B1 implementation 75,860 / 203 s; B2 tests 72,597 / 144 s; B2 implementation 65,568 / 118 s |
| Token buckets (input, output, cache read, cache write) | unavailable; the harness reports totals only |
| Coordinator tokens | unavailable (not separable in this session) |
| Cost | unavailable (no price table supplied) |
| Provider API calls | none (local HTTP provider) |

## Limits of this evidence

- Both applications are small, the contracts were written by the same coordinator who verified them, and the agents share a model family.
- Benchmark 1 uses ASP.NET Core server-rendered pages, not Limen/WebAssembly. WebKit is not installed in this container, so only Chromium ran.
- The real-provider path is a local HTTP listener, not GitHub.
- 7 of the 20 obligations were not probed; their fault sensitivity is not demonstrated.
