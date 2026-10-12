# Adapter-driven behavioral verification (`dokimos behavior verify`)

Tracking: local work item `WI-0006`, a follow-up to
[dokimos#30](https://github.com/kemiller2002/dokimos/issues/30). It extends
[construction verification](construction-verification.md) to candidates that
were not produced by a generator.

The timer experiment (research-documents#6, time-entry-state-machine#4)
compares two arms:

- **baseline**: a general-purpose coding agent writes its own F# implementation;
- **treatment**: Ordo-generated code plus bounded handwritten code.

Both arms must be judged by the **same frozen oracle** through the **same
minimal interface**, so that neither arm is handed an architecture.
`dokimos construction verify` drives generated code by Ordo's naming
conventions (`Generated.Timer`, `TimerTransition.decide`), which the baseline
arm does not have. This command drives a declared adapter instead.

> **Executing candidate code.** A run builds the candidate and executes its
> code. Run it only where that is acceptable: CI or a disposable environment.
> Dokimos does not sandbox the candidate, and a run is **not containment
> evidence**.

## The frozen adapter contract (`dokimos.timer-oracle-adapter/1`)

A candidate F# project must expose exactly this module:

```fsharp
namespace TimerPilot

module TimerOracleAdapter =
    /// state: "NoTimer" | "Running" | "Paused"; command: "start" | "pause" | "resume" | "stop".
    /// Ok (nextState, effects) where effects are names from: "complete-entry"; Error reason when refused.
    val decide : state: string -> command: string -> Result<string * string list, string>
    /// Elapsed working time of closed segments (start, end) in whole seconds from an arbitrary epoch.
    /// None = nothing is recorded. Otherwise the whole minutes recorded.
    val recordedMinutes : segments: (int64 * int64) list -> int option
```

The contract is recorded in the oracle's `binding`
(`kind: "adapter"`, `contract`, `namespace`, `module`, `functions` with their
signatures). It says nothing about how the candidate is structured
internally. A candidate can expose the adapter over its own types, over
Ordo-generated code, or over anything else.

The driver accepts each function as a compiled module function with exactly
these parameter and return types, or as a module value of exactly that F#
function type. Anything else with the same name is a
`adapter-signature-mismatch`. A module with neither name is an
`adapter-missing`.

## The frozen oracle (`DOK-ORACLE-TIMER-ADAPTER-1`)

[`oracles/construction/time-entry-timer/oracle-adapter.json`](../oracles/construction/time-entry-timer/oracle-adapter.json)
(schema [`schemas/dokimos-adapter-oracle.schema.json`](../schemas/dokimos-adapter-oracle.schema.json),
`dokimos.adapter-oracle/1`) was committed on its own, before any of the runner
code and before any candidate. It was written from the original excerpts in
`tests/fixtures/construction/time-entry-timer/sources/` only.

- **TIMER-ORACLE-001..014** restate `DOK-ORACLE-TIMER-1`'s decide and
  sequence cases, with the same IDs, citations, requirement keys and
  expectations (a test checks this field by field).
- **TIMER-ORACLE-015** (port surface) is listed under `excludedCases`: the
  adapter deliberately exposes no ports.
- **TIMER-MINUTES-001..007** drive `recordedMinutes`. They come from
  `timer-stop-completion.txt` ("computes elapsed seconds from segments and
  rounds to the nearest whole minute ... recording is exact by the minute ...
  Six-minute rounding ... never at recording time") and
  `timer-paused-time-excluded.txt` ("time spent paused is excluded"). They
  cover summing several segments while excluding the gaps, rounding up and
  down to the nearest minute, summing before rounding, and no six-minute unit
  at recording time.
- **Tie rule.** The excerpts state no rule for an exact half-minute tie. No
  scored case has a total of whole minutes plus exactly 30 seconds, and a test
  enforces this.
- **TIMER-SHORT-001..003** are under thirty seconds of working time. The
  excerpt says nothing is recorded, but the approved ECIR blueprint records
  `CONFLICT-TIMER-SHORT-STOP` (refusal versus return to `NoTimer`). These
  cases carry `scored: false`, `classification: "unresolved-conflict"` and no
  expectation. The candidate's answer is reported under
  `ConflictObservations` as agent-decided behavior on an unresolved conflict.
  It is never counted as a pass or a fail. No scored case is short enough for
  either reading of the thirty-second rule to apply.

Each `derivedFrom` source records its coverage through the adapter (`full`,
`partial` or `none`) and what remains unobserved.

## Running it

```bash
dokimos behavior verify \
  --oracle oracles/construction/time-entry-timer/oracle-adapter.json \
  --expected-oracle-digest sha256:<pinned digest> \
  --repository ../candidate --commit <sha> --project src/Pilot/Pilot.fsproj \
  --sources tests/fixtures/construction/time-entry-timer/sources \
  --work-item WI-0006 --json
```

| Option | Meaning |
|---|---|
| `--oracle`, `--expected-oracle-digest` | Required. The oracle file and its pinned SHA-256 |
| `--repository`, `--commit` | The candidate repository (default `.`) and the revision to verify |
| `--project` | Required. The candidate's project file, relative to the repository root |
| `--sources DIR` | Optional. Each cited excerpt is hashed against the oracle's `contentDigest` |
| `--claims F`, `--claimed-results TRX` | Optional. Claimed results are checked, never counted |
| `--runner` | The `dotnet` executable (default `dotnet`) |
| `--work-item`, `--at` | Finding link and check time |
| `--json` | Accepted; the report is always JSON |

### What Dokimos does

1. **Integrity first.** Dokimos checks that the oracle hashes to the pinned
   digest, that its declared `frozenAt` is not after the candidate commit,
   that its exact bytes were first committed in its own repository no later
   than the candidate commit (when that history is available), that its
   producer is not a generator, and that the candidate commit does not contain
   the oracle. On any failure the verdict is `refused` and Dokimos does
   **not** build or execute the candidate.
2. **Materialize from Git objects.** Every regular file at the resolved
   commit is read with `git ls-tree` and `git cat-file --batch` and written to
   a fresh temporary directory. The working tree is never read. Symlinks and
   submodules are not materialized.
3. **Build with a pinned command.** `dotnet --version`, then
   `dotnet build <project> -c Release --nologo -nodeReuse:false`, then
   `dotnet msbuild <project> -getProperty:TargetPath -p:Configuration=Release -nologo`
   to locate the built assembly.
4. **Drive in a separate process.** Dokimos writes its embedded driver
   (`src/Dokimos.Core/BehaviorDriver.fsx`) and an input listing only what to
   call, then runs `dotnet fsi --quiet driver.fsx input.json`. The driver
   loads the built assembly, probes the adapter, calls it for every case (with
   a 10-second limit per call) and writes one JSON line per case. It never
   receives the expectations.
5. **Judge in-process.** Dokimos reads the observations and judges them
   against the oracle. A case reported twice is `unknown`, not whichever line
   came last.

### The report (`dokimos.behavior-verification` 1.0.0)

Schema: [`schemas/dokimos-behavior-verification.schema.json`](../schemas/dokimos-behavior-verification.schema.json).

- `Compile.Status`: `passed`, `failed` (with diagnostics), `not-run` (no such
  project) or `unknown`.
- `Adapter`: what the driver found (assembly, module, each function `ok`,
  `missing` or `signature-mismatch`).
- `Cases`: every scored case `passed`, `failed` or `unknown`, with what was
  observed.
- `Requirements`: per original requirement, `verified` (all scored cases
  passed and coverage is full), `partial` (all passed, coverage partial),
  `failed`, `unknown`, `not-observed` or `unresolved-conflict`.
- `ConflictObservations`: each unscored case's input and the candidate's
  answer.
- `FirstPassVerdict` and exit code: `passed` 0, `failed` 4, `unknown` 3,
  `refused` 4. "First pass" means one Dokimos-observed run, with no retries.
- `Run`: what Dokimos observed: SDK version, every command line with start
  and finish times, duration, exit code and output digests, the candidate
  commit and tree, a digest of the materialized files, the assembly digest,
  driver, input and observation digests, and the oracle digest.

### Finding codes

New codes: `adapter-project-missing`, `adapter-missing`,
`adapter-signature-mismatch`. Shared with construction verification:
`candidate-compile-failed`, `oracle-behavior-mismatch`, `fake-test-result`,
`oracle-digest-mismatch`, `oracle-not-frozen-before-candidate`,
`oracle-not-independent` and `source-content-mismatch`. A missing adapter, a
wrong signature or a build failure is never a pass.

## Limitations

- The candidate runs unsandboxed. Its code runs inside the driver process,
  so hostile code could tamper with that process's observations. Dokimos
  never accepts a verdict from the driver, but the observation channel shares
  the candidate's process.
- Only the adapter is observed. Whether the candidate's own host paths (for
  example its stop handler) use the logic it exposes through the adapter is
  not observed.
- The SDK used is the one resolved in the materialized candidate directory,
  so a candidate's `global.json` applies. The version is recorded, not
  enforced. Package restore may use the network.
- Commit and freeze times are Git and oracle metadata. They are self-reported:
  Dokimos compares them but does not authenticate them.
