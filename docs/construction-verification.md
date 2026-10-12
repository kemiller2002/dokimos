# Independent construction verification (`dokimos construction verify`)

Tracking: [dokimos#30](https://github.com/kemiller2002/dokimos/issues/30)
(CONSTRUCT-005). Reuses the ECIR source-conservation audit from
[dokimos#28](https://github.com/kemiller2002/dokimos/issues/28).

Ordo turns an approved construction plan into generated F#. Praxis applies
that output as one Git commit. Dokimos is the evaluator. It checks that the
restricted construction removed forbidden actions **and** kept the required
behavior. It never accepts the generator's self-attestation, a constructor's
summary or a claimed test result as proof.

For candidates that were not produced by a generator, such as the baseline
arm of the timer experiment, see
[adapter-driven behavioral verification](behavior-verification.md)
(`dokimos behavior verify`). It judges every arm through one frozen adapter
with the same oracle-integrity checks.

## Inputs: separate and independently pinned

| Input | Option | How Dokimos pins it |
|---|---|---|
| Original requirement manifest | `--source-manifest` | Its framed digest is recomputed by the ECIR audit |
| Original requirement excerpts | `--sources DIR` (optional) | Each excerpt is hashed against the manifest's `contentDigest` |
| Approved ECIR blueprint | `--blueprint` | Its canonical digest is recomputed and must equal the plan's binding |
| Construction plan | `--plan`, `--expected-plan-digest` | Its canonical digest is recomputed and must equal the pinned value |
| Generator version | `--expected-generator` | Compared with the manifest and with every generated file header |
| Candidate commit | `--repository`, `--commit`, `--manifest-path` | Every file is read from Git objects at that commit (`git ls-tree`, `git cat-file`, `git grep <commit>`), never from the working tree |
| Base commit | `--base` (default `<commit>^`) | Used for the dependency and protected-path diffs |
| Pinned generator output | `--reference-manifest` (optional) | The trusted host's or Ordo's own manifest for this plan |
| Behavioral oracle | `--oracle`, `--expected-oracle-digest` | File SHA-256; it must be frozen before the candidate commit and not produced by the generator |
| Host observations | `--host-observations` | Only records with `recordedBy: host` count |
| Claims | `--claims`, `--claimed-results TRX` | Verified, never counted |
| Authorization and exceptions | `--authorization` | Exact dependency changes, waivers (reason and owner), expiring exceptions |

Input formats are in
[`schemas/dokimos-construction-inputs.schema.json`](../schemas/dokimos-construction-inputs.schema.json).
The report is
[`schemas/dokimos-construction-verification.schema.json`](../schemas/dokimos-construction-verification.schema.json)
(`dokimos.construction-verification` 1.0.0).

The plan and blueprint digests are recomputed by Dokimos's own
implementation of the canonical forms that Ordo publishes in
`docs/architecture/construction-plan-v1.md`. Dokimos references no Ordo or
Praxis code. Tests check that this implementation reproduces Ordo's pinned
digests for both checked-in fixtures.

## What is reported, separately

- **Source conservation**: the `dokimos.ecir-audit/1` report, unchanged.
  It is reused, not reimplemented.
- **Structural conformance**: artifact bytes against the manifest, ownership,
  manifest against plan, and the committed generated code against the plan.
  The code check covers transitions, guards and union cases in the published
  `ordo.construction/1` shape. The generator's own `structuralChecks` are
  reported as `derived-from-plan` with `CountsAsAcceptance: false`. They are
  never acceptance evidence.
- **Compile**: whether the committed generated files compiled under the
  oracle harness.
- **Behavioral acceptance**: `passed`, `failed` or `unknown`, per frozen
  oracle case, from a run that Dokimos itself started and watched.
  `EscapedConstraints` counts oracle failures on candidates whose generator
  checks agree with the plan, that is, behavior the generator's own checks
  would have passed.
- **Containment**: `confined`, `not-confined` or `unknown`. `confined`
  requires host-recorded, enforced containment with a named mechanism,
  available platform proofs and no undenied protected write. Unavailable
  OS-specific proofs are listed by name.
- **Per original requirement**: `Represented`, `Modeled`, `Generated`,
  `Implemented`, `IndependentlyVerified`, `Deferred`, `Unresolved` and
  `Waived`, each on its own, with the requirement's artifacts, plan
  operations, oracle cases, open obligations and finding codes. `Counts` adds
  them up. Unknown values are counted as unknown. Nothing is reduced to one
  percentage.

`Verdict` is `verified` (exit 0), `rejected` (exit 4, at least one open
finding) or `incomplete` (exit 3, no open finding but behavior unknown).
`verified` means the construction was verified. It does not mean the feature
is complete: requirements that are only partly observable, or that depend on
handwritten obligations, stay `partial` and `unknown`.

## The behavioral oracle

[`oracles/construction/time-entry-timer/oracle.json`](../oracles/construction/time-entry-timer/oracle.json)
(`DOK-ORACLE-TIMER-1`) was written by hand from the original excerpts in
`tests/fixtures/construction/time-entry-timer/sources/`. It cites each one
and records its `contentDigest`. It was committed before the verifier and
before any candidate existed. It was not derived from the plan, the generated
code, the manifest or Ordo's tests. Each case has a stable ID, the requirement
keys it was derived from and a quoted citation. Each source records how much
the oracle can observe (`full`, `partial` or `none`) and what remains outside
it. For example, elapsed-time arithmetic is handwritten and cannot be observed
in generated code.

### How Dokimos observes behavior

Execution is opt-in: pass `--oracle-runner dotnet`. Dokimos then:

1. copies the manifest's generated `.fs` files, taken from Git objects at the
   candidate commit, into a fresh temporary directory;
2. prepends `#load` lines to its own harness
   (`src/Dokimos.Core/ConstructionHarness.fsx`, an embedded resource);
3. runs `dotnet fsi --quiet harness.fsx cases.json` and waits for it to
   finish.

The harness drives Tier 2 `decide` and inspects the ports interface by
reflection, through the oracle's frozen name binding. It never receives the
expectations, so it cannot grade itself. Dokimos judges its JSON-lines output
in-process. The run record (runner version, command line, start and finish
times, exit code, stdout digest, harness digest, candidate commit and oracle
digest) is in the report, under `ObservedBy: dokimos`.

Without `--oracle-runner`, behavior is `unknown` and the verdict cannot be
`verified`. A claimed pass, from TRX or claims, is a `fake-test-result` in
three cases: it is not keyed to an oracle case ID, Dokimos observed no run,
or Dokimos observed a different result.

**Limits of this slice.** The harness supports `decide state command`. A
`decide` that takes rule records and held capabilities is reported as
`unsupported` (`unknown`), not as passed. Guards in that shape are checked
structurally (`missing-required-guard`), not behaviorally. Handwritten
Tier 4 hosts and extension contracts are not executed.

## Stable finding codes

| Code | Meaning |
|---|---|
| `generated-artifact-modified` | Committed bytes differ from the manifest SHA-256, or the manifest or an artifact differs from the pinned generator output (`--reference-manifest`) |
| `generated-artifact-missing` | The manifest names an artifact the commit lacks |
| `missing-required-guard` | A plan guard is absent from the guard table, from `decide`, or from the manifest's guard contracts |
| `fake-test-result` | A claimed pass that was not produced by the Dokimos-observed oracle run, or that does not match an oracle ID |
| `generator-version-changed` | The manifest, a file header or a trailer names a generator other than the pinned one |
| `plan-digest-changed` | The plan, manifest, file header or trailer differs from the pinned plan digest |
| `undocumented-generated-file` | A file in the ownership root that the manifest does not list |
| `handwritten-claims-generated` | A file carries the generated marker but is not a manifest artifact with matching bytes |
| `requirement-silently-omitted` | A requirement was dropped by the blueprint, the plan or the manifest, or an unresolved question lost its obligation |
| `invented-behavior` | A generated transition, case, guard, operation or requirement trace that is not in the plan or intake |
| `unauthorized-dependency` | A package or project reference was added, removed or re-versioned without an exact authorization entry |
| `host-denial-without-evidence` | A claimed denial with no host-recorded denial. A clean diff, path check or checklist does not count |
| `clean-diff-not-denial` | The host recorded a protected write that was not denied, and the diff is clean: write and revert |
| `protected-write-allowed` | A host-recorded write to a protected path that was not denied |
| `confinement-claim-unsupported` | Confinement is claimed but host evidence does not establish it |
| `oracle-behavior-mismatch` | An oracle case failed in the Dokimos-observed run |
| `candidate-compile-failed` | The generated files did not compile under the harness |
| `oracle-digest-mismatch`, `oracle-not-frozen-before-candidate`, `oracle-not-independent`, `oracle-source-mismatch` | The oracle cannot be used as independent evidence |
| `binding-mismatch`, `manifest-plan-divergence`, `generator-self-attestation` | The plan, blueprint, manifest and generator checks disagree, or the generator claims independence for its own checks |
| `manifest-location-mismatch`, `artifact-outside-ownership-root`, `non-regular-generated-entry` | Ownership-root violations |
| `provenance-trailer-mismatch` | A `Construction-*` commit trailer contradicts the pinned inputs |
| `source-content-mismatch` | A cited excerpt does not hash to the pinned `contentDigest` |
| `ecir-*` | Any other ECIR source-conservation finding, prefixed |

Every finding has a `Subject`, its original requirement keys and the
`--work-item` link. An unexpired exception with a reason and an owner marks a
finding `excepted`. The finding stays in the report, and `Exceptions.Applied`
counts it. An expired exception does not apply.

## Example

```bash
dokimos construction verify \
  --source-manifest tests/fixtures/construction/time-entry-timer/source-manifest.json \
  --sources tests/fixtures/construction/time-entry-timer/sources \
  --blueprint tests/fixtures/construction/time-entry-timer/blueprint.json \
  --plan tests/fixtures/construction/time-entry-timer/plan.json \
  --expected-plan-digest sha256:860dd8fe579b4932207e77457373bb833180653f368376669090fdb5eb29fba8 \
  --expected-generator ordo.construction/1 \
  --repository ../app --commit HEAD --manifest-path generated/Timer/construction-manifest.json \
  --oracle oracles/construction/time-entry-timer/oracle.json \
  --expected-oracle-digest sha256:b08518c9932932d93e48268488b43889446b56052deebfbd8a0d78e2f9437a98 \
  --oracle-runner dotnet --work-item GH-30
```

On Ordo's golden timer, the report shows these counts: 7 imported, 7
represented, 6 modeled, 6 generated, 3 implemented (3 unknown because of open
handwritten obligations), 2 independently verified (state transitions and
command rejection), 4 partially verified and 1 unresolved (stop under thirty
seconds). Containment is `unknown` because no host evidence was supplied.
