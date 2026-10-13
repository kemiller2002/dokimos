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
(`dokimos.construction-verification` 1.2.0; 1.1.0 only adds the optional `MechanicalFirst` block, and
1.2.0 only adds the CONSTRUCT-010 decision checks and `MechanicalFirst[].Decisions`).

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
| `prepared-artifact-invalid`, `provenance-digest-mismatch`, `nondeterministic-generation`, `obligation-dropped`, `obligation-disposition-invalid`, `unauthorized-file-mutation`, `generated-file-edited`, `extension-boundary-violation`, `generated-layout-mismatch`, `rejected-operation-unrecorded`, `rejected-operation-applied`, `unresolved-obligation-claimed-implemented` | Mechanical-first checks (see below), only with `--prepared` |
| `ecir-*` | Any other ECIR source-conservation finding, prefixed |

Every finding has a `Subject`, its original requirement keys and the
`--work-item` link. An unexpired exception with a reason and an owner marks a
finding `excepted`. The finding stays in the report, and `Exceptions.Applied`
counts it. An expired exception does not apply.

## Mechanical-first (arm C) checks

Tracking: CONSTRUCT-008 (`WI-0007`). In the mechanical-first flow, Ordo
generates a default plan with typed obligations, Praxis prepares and applies
it (`praxis.construction-prepared/1`), and an agent writes only the permitted
handwritten files plus one disposition per obligation. `--prepared` turns on
eight additional checks (and the six CONSTRUCT-010 decision checks below). Without `--prepared` the command behaves exactly as
before (the B-arm invocation is unchanged; its report carries an empty
`MechanicalFirst` array).

Dokimos references no Ordo or Praxis code. Every check is computed from Git
trees and blobs, the frozen oracle files and the declared JSON of the supplied
artifacts. The prepared plan is never its own proof: its digest is
recomputed, compared with the pinned `--expected-plan-digest`, and the
candidate is still checked structurally and by the frozen oracle.

### Additional options

| Option | Meaning |
|---|---|
| `--prepared F` | The prepared artifact. With it, `--plan` is optional and defaults to `defaultPlan.plan` (if `--plan` is also given, both must recompute to the same digest) |
| `--dispositions F` | The agent's obligation dispositions (`dispositions.json`) |
| `--decisions F` | The approved `ordo.construction-decisions/1` file the plan was generated from |
| `--generated-commit REV` | The host's apply commit that carried only generated output; isolates handwritten changes (`REV..--commit`) |
| `--replica-commit REV` [`--replica-repository DIR`] | A second candidate built from identical inputs; generated files must be byte-identical (same Git blob) |
| `--prepared-replica F` | A second prepared artifact; with an equal `cacheKey` it must be byte-identical |
| `--base REV` | As before; for arm C pass the pre-construction commit |
| `--host-observations F` | As before (`dokimos.host-observations/1`); host refusal records |

`--dispositions`, `--decisions`, `--prepared-replica`, `--generated-commit`,
`--replica-commit` and `--replica-repository` without `--prepared` exit 2.

### Fields read from `praxis.construction-prepared/1`

Only these fields are read. Every other field (host binding stamp, the rest of
the handoff, the whole-default-plan digest, and so on) is ignored. Required
fields are validated strictly, and every violation is reported as its own
`prepared-artifact-invalid` finding.

| Field | Required | Rule |
|---|---|---|
| `schemaVersion` | yes | `praxis.construction-prepared/1` |
| `cacheKey` | yes | non-empty string |
| `defaultPlan.plan` (alias `defaultPlan.draft`) | yes | an `ordo.construction-plan/1` object |
| `defaultPlan.planDigest` | yes | `sha256:<64 hex>` |
| `defaultPlan.obligations` | yes | an array, or an `ordo.construction-obligations/1` object with an `obligations` array; each item has `id` (unique), `kind` (`extensionBehavior`, `undecidedEffectFailure`, `conflict`, `uncoveredRequirement`, `unsupportedVocabulary`), `requirementKeys` (array) and optionally `nodeIds` |
| `defaultPlan.generatedLayout` | yes | an array (or `{files: [...]}`) of path strings or `{path, sha256?}` objects; a path relative to the manifest's mount point is resolved against it |
| `defaultPlan.provenance.generatorId` (alias `generator`) | yes | must be `ordo.default-plan` |
| `defaultPlan.provenance.generatorVersion`, `.vocabularyVersion` | yes | non-empty strings |
| `defaultPlan.provenance.inputDigests` (alias `inputs`) `.blueprint`, `.sourceManifest`, `.decisionRecords` (each also accepted with a `Digest` suffix) | yes | `sha256:<64 hex>` |
| `defaultPlan.obligations[].extensionSurface` | no | strings; an `undecidedEffectFailure` names its command here (`IXPorts.Perform/Stop` or `port:transitionEffect/Stop`) |
| `permittedExtensionFiles` | yes | an array of repository paths (strings or `{path}`) |
| `permittedExtensionSlots` | no | objects with a required `path` and an optional `kind` (e.g. `acceptanceAdapter`); every slot path is a permitted handwritten path. Other slot fields are ignored. Project, generated and dependency files stay forbidden even in a slot |
| `handoff.obligationIds` or `handoff.obligations[].id` | no | when present, must name every default-plan obligation; `IMPLEMENT:*` and `VERIFY:*` ids are auxiliary and ignored |

### Fields read from the dispositions file

`schemaVersion` is optional and only echoed. `dispositions` is required, either
as an array of `{obligationId (or id), disposition (or status), note, files}`
or as an object keyed by obligation id whose values are a status string or
that object. `disposition` must be `implemented` or `escalated`; an
`escalated` disposition needs a non-empty `note`; `files` must be permitted
extension files. The optional `rejectedOperations` array lists
`{path, reason}` host refusals the agent received. Unknown fields are ignored.

### Checks and their findings

| Check | Passes when | Finding codes |
|---|---|---|
| `plan-provenance` | The prepared plan recomputes to its declared `planDigest`, to the pinned digest and to `--plan`; `generatorId` is `ordo.default-plan`; the declared blueprint digest equals the supplied blueprint's canonical or file digest; the source-manifest digest equals its declared or file digest; the decision-records digest equals the `--decisions` file's or canonical digest, the records are `ordo.construction-decisions/1` for the same blueprint and cohort, and every bound decision has a record. Unknown without `--decisions` | `provenance-digest-mismatch`, `binding-mismatch`, `prepared-artifact-invalid` |
| `requirement-coverage` | Every requirement key of the plan's cohort node is traced by a plan operation or cited by an obligation; obligation keys exist in the intake, and only `conflict` obligations cite keys outside the cohort | `requirement-silently-omitted`, `invented-behavior` |
| `deterministic-generation` | Every generated path is the same Git blob in the candidate (or `--generated-commit`) and in `--replica-commit`; two prepared artifacts with the same `cacheKey` are byte-identical. Unknown without a replica | `nondeterministic-generation` |
| `obligation-tracking` | Every obligation has exactly one valid disposition, the handoff drops none, no disposition names an unknown obligation (`IMPLEMENT:*`/`VERIFY:*` ids are ignored), and an `implemented` claim comes with at least one changed permitted file. A `conflict`, `undecidedEffectFailure` or `uncoveredRequirement` obligation may only be `escalated` unless the `--decisions` file (matching the declared digest) closes it: an `undecidedEffectFailure` is closed when a `portContract` record of one of its `nodeIds` has a `transitionEffect` port for its command with a non-null `onFailure`; `conflict` and `uncoveredRequirement` are never closed today (no approved record kind resolves them). Unknown without `--dispositions` when obligations exist | `obligation-dropped`, `obligation-disposition-invalid`, `unresolved-obligation-claimed-implemented` |
| `file-mutations` | Every path changed between `--base` and `--commit` is a generated path (layout, manifest artifacts, the manifest) or a permitted extension file; the apply commit changes only generated paths | `unauthorized-file-mutation` |
| `extension-boundary` | Handwritten changes (`--generated-commit..--commit`, otherwise base changes outside the generated paths) touch only permitted files; no generated file, project, solution, build or package/dependency file; the host did not permit such a file; layout `sha256` values match the committed bytes; the layout and the construction manifest agree | `generated-file-edited`, `extension-boundary-violation`, `generated-layout-mismatch`, `generated-artifact-missing`, `generated-artifact-modified` |
| `architecture-compliance` | The existing structural, ownership, binding, dependency and oracle checks above report nothing (no new finding is added, so nothing is counted twice) | the existing codes |
| `rejected-operations` | Every refusal the agent reports was recorded by the host (`recordedBy: host`, `outcome: denied`, same path); no host-denied path is changed in the candidate; the host allowed no write outside the permitted paths. Unknown without `--host-observations` | `rejected-operation-unrecorded`, `rejected-operation-applied`, `unauthorized-file-mutation` |

Each check reports `passed`, `failed` or `unknown` in
`MechanicalFirst[0].Checks`. Any open finding makes the verdict `rejected`;
otherwise a check that is `unknown` makes it `incomplete` (exit 3), so
`verified` with `--prepared` means all eight checks passed and the frozen
oracle passed. The report also lists the generated, permitted and handwritten
paths and each obligation with its recorded disposition.

**Limits.** The whole-default-plan digest (`provenance.defaultPlanDigest`) is
not recomputed, because its serialization is Ordo's; determinism is evidenced
by replicas instead. Without `--generated-commit`, a handwritten edit to a
generated file is caught by the manifest and layout SHA-256 checks, not by
the boundary diff. Host refusal records are read from
`dokimos.host-observations/1`; Dokimos checks their consistency, not their
authenticity.

## Decision completeness (CONSTRUCT-010 phase 4)

Tracking: CONSTRUCT-010 phase 4 (`WI-0008`), the Dokimos side of ordo#78
(`ordo.construction-vocabulary/3`, decision records
`ordo.construction-decisions/2`, obligation kinds `undecidedTransition` and
`undecidedExtensionDecision`). Dokimos references no Ordo or Praxis code: it
reads ordo#78's documented wire formats and re-derives every result.

### Classification, re-derived

Dokimos reads every semantic choice of the verified plan (states, every
(state, command) pair, invariants, ports, effect failures, capabilities,
extension behaviors and their decision points, open questions) in any of
vocabularies 1, 2 and 3, and classifies it itself, by the rules ordo#78
documents in `docs/architecture/decision-classification.md`:

| Class | Meaning | May be generated |
|---|---|---|
| `required` | fixed by the requirement text its trace cites | yes |
| `approved` | its trace cites a decision the plan binds | yes |
| `implied` | a documented rule: a cited pair rule, or a terminal state's role | yes |
| `unresolved` | stated as open (undecided pair, effect failure, decision point, question) | no |
| `unsupported` | decided by omission, because the vocabulary cannot avoid it | no |

Tests check that this re-derivation equals the `ordo.choice-classification/1`
Ordo publishes for the timer cohort (both pair-rule variants), and that the
canonical plan digest, now covering vocabularies 2 and 3, reproduces Ordo's
pinned digests. Ordo's classification is never an input.

### Additional options

| Option | Meaning |
|---|---|
| `--ledger F` | The requirements ledger, `ordo.requirements-ledger/1` |
| `--approvals F` | The operator's decision approvals: `approver` (required), `decisions` (ids, or `{id, closes[]}`), and optionally `cohort`, `blueprintDigest`, `decisionRecordsDigest`, which must match the supplied inputs. The expanded-slice harness's `operator-approval.json` is accepted as is |

Both require `--prepared`. `--decisions` accepts `ordo.construction-decisions/2`
for a vocabulary-3 plan and `/1` otherwise (ordo#78's pairing rule).
Dispositions may now also be `resolved`, with `decisionId` naming the decision
that closes the obligation.

### Checks and their findings

A citation **resolves** when every cited requirement key is in the original
intake, and every cited decision is a `decision` or `engineeringRationale`
node of the approved blueprint that the operator approved (`--approvals`).
Without `--approvals`, approved citations are `unknown`, never passed.

| Check | Passes when | Finding codes |
|---|---|---|
| `decision-completeness` | Every non-pair choice has a class; every generated choice's citation resolves; no unresolved or unsupported choice is claimed implemented (an `IMPLEMENT:` obligation for an extension with an open decision point, a generated effect disposition the plan leaves open, an `extensionBehavior` obligation `implemented` for an extension with no decision inventory); approvals bind the same cohort, blueprint and records | `fabricated-citation`, `unresolvable-citation`, `unapproved-decision-cited`, `unresolved-choice-claimed-implemented`, `unsupported-choice-claimed-implemented`, `approvals-invalid` |
| `requirement-disposition` | Every source-manifest key has exactly one ledger row and no row names an unknown key; an `implemented` row has passing acceptance that Dokimos itself observed (frozen oracle cases covering the key) and no open obligation; `rejected`/`superseded` rows cite an operator-approved closing decision. Unknown without `--ledger`, or when no oracle run was observed | `requirement-dropped`, `duplicate-ledger-row`, `invented-ledger-row`, `implemented-without-independent-acceptance`, `implemented-with-open-obligation`, `unapproved-resolution`, `ledger-invalid` |
| `closed-world` | Every (state, command) pair is an allowed transition, a refusal citing a requirement or an approved decision (explicit, by the pair rule, or by a terminal state), or undecided with both a `RESOLVE-PAIR` manifest obligation and an `undecidedTransition` obligation. The generated transition code (Git objects; the manifest's structural rows otherwise) must agree | `refusal-by-omission`, `undecided-pair-refused`, `pair-disposition-mismatch`, `undecided-choice-without-obligation`, `fabricated-citation` |
| `unsupported-operations` | No plan operation is outside its vocabulary; decision-record content the vocabulary cannot carry has an `unsupportedVocabulary` obligation; no such obligation is claimed implemented. Every unsupported choice is listed as an open item | `unsupported-operation-dropped`, `unsupported-choice-claimed-implemented` |
| `unresolved-obligations` | An agent's `resolved` (or decision-citing) disposition cites an operator-approved decision that the plan did not already apply (a bound decision cannot close what the generator left open) and whose `closes` list, if any, names the obligation; every open effect failure and decision point has its obligation. Unknown when a resolution is claimed without `--approvals` | `unapproved-resolution`, `undecided-choice-without-obligation` |
| `failure-semantics` | For every transition effect (plan and decision records), an unknown outcome maps to `reconcile` or `escalate` (`onUnknown`); a decided `onFailure` with no `onUnknown` also governs an unknown outcome and is refused (`remain`: assumed failure; `proceed`: assumed success); `retry` is a blind retry; `compensate` needs an approved decision; the generated orchestration applies the plan's disposition | `unknown-outcome-assumed`, `unapproved-compensation`, `effect-disposition-mismatch` |

Architecture boundaries stay with `architecture-compliance` above.

### Intentionally incomplete versus verified complete

`MechanicalFirst[0].Decisions` lists every choice with its class and citation,
a `Summary` by class, and `OpenItems`: each unresolved or unsupported choice,
each open obligation not tied to a choice, and each unsupported operation,
with the obligation ids that keep it visible. `Completeness` is `complete`,
`incomplete` (open items, nothing violated), `unknown` or `violated`.

Any open item makes an otherwise clean report `incomplete` (exit 3) with the
reason `Intentionally incomplete: N open decision item(s) ...`. `verified`
with `--prepared` therefore means the construction is verified **and**
complete: no choice was left open or decided by omission.

On ordo#78's timer cohort (decisions v2, unlisted pairs refused by
REQUIREMENTS.md L367-368) Dokimos classifies 6 required, 8 approved, 7 implied
and 3 unresolved choices, exactly as Ordo does, raises no finding, and reports
four open items: the Stop effect failure, the rounding tie, the uncovered
session requirement and the out-of-cohort conflict. With unlisted pairs
undecided it reports eleven, the seven pairs each with its two obligations.

**Consequence for vocabulary 1 and 2.** These vocabularies cannot state a pair
rule, so generated code refuses unlisted pairs by omission. The pilot-01 B1
candidate, which passes every CONSTRUCT-008 check and the frozen oracle, is now
`rejected` for seven `refusal-by-omission` findings and for claiming its stop
completion implemented although its extension has no decision inventory (the
OPT-14 rounding tie). That is the defect ordo#78 closes, not a regression.
Verdicts recorded for CONSTRUCT-008 and the expanded study stay bound to the
Dokimos version that produced them (0.3.0); they are not re-scored with this
version.

**Failure semantics before `onUnknown`.** Ordo's vocabulary-2 goldens with a
decided `remain` or `proceed` disposition fail `failure-semantics` today: the
generated orchestration handles an unknown outcome in the same arm as a
failure, which assumes failure (or success). Ordo's vocabulary 2-4 contract
says exactly that (`onFailure` governs Failed and Unknown alike), so this is a
stricter policy than Ordo's, by design. The finding names the fix: state the
unknown outcome separately, which Ordo's vocabulary 5 does with
`declareEffectPolicy {onFailed, onUnknown: reconcile | escalate | unresolved}`
(ordo#83, not yet merged; Dokimos does not read vocabulary 5 yet and refuses
it as unsupported input).

**Approvals are checked for consistency, not authenticity.** `--approvals`
must bind the same cohort, blueprint and decision records as the other inputs,
and every approved citation must name one of its decisions. Their authenticity
comes from the Praxis signed grant, which binds the decision-records digest
into the operator's grant; Dokimos does not authenticate approvals.

**Limits.** An obligation with a `planSubject` is matched to its choice exactly
(`pair:S/C`, `extension:effect/C/point:P`). ordo#78 emits none for
`undecidedTransition` and `undecidedExtensionDecision`, so those are matched by
Ordo's question wording (`command C does in state S`, the point name) until it
does; a test fails as soon as the pinned Ordo fixtures carry `planSubject`, so
the wording fallback is removed then (Ordo will add a typed `planSubject` with
its next default-plan generator version). Generated effect
dispositions are read from the orchestration's `onFailure` arms only.
Dokimos also reads an `onUnknown` member on a port and the value `compensate`;
Ordo emits neither in vocabularies 1-3 (vocabulary 5 moves both into
`declareEffectPolicy`), so for Ordo plans these paths are dormant.

### Example (pilot-01 B1 Git objects with synthetic prepared inputs)

```bash
dokimos construction verify \
  --source-manifest tests/fixtures/construction/time-entry-timer/source-manifest.json \
  --sources tests/fixtures/construction/time-entry-timer/sources \
  --blueprint tests/fixtures/construction/time-entry-timer/blueprint.json \
  --expected-plan-digest sha256:1993df015678980493b7e9c7e8a602205c75bf2eb40c4aaa8ac4541f4afbcfa8 \
  --expected-generator ordo.construction/1 \
  --repository b1 --base 4f1e14c --commit 35ead6f --generated-commit f7f477f --replica-commit 0cbcdd6 \
  --manifest-path pilot/Timer/generated/Timer/construction-manifest.json \
  --oracle oracles/construction/time-entry-timer/oracle.json \
  --expected-oracle-digest sha256:b08518c9932932d93e48268488b43889446b56052deebfbd8a0d78e2f9437a98 \
  --oracle-runner dotnet \
  --prepared tests/fixtures/construction/pilot-01-b1/prepared.json \
  --dispositions tests/fixtures/construction/pilot-01-b1/dispositions.json \
  --decisions tests/fixtures/construction/pilot-01-b1/decisions.json \
  --host-observations tests/fixtures/construction/pilot-01-b1/host-observations.json
```

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
