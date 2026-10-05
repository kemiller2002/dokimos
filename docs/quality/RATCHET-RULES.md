# Dokimos change-quality ratchet: rule catalog and contract

Status: implemented (issue #16; requirements DOK-QUAL-001, -002, -005 and -008
in PR #15). Decision record: `research/decisions/DF-DOK-2026-0001--baseline-derived-ratchet-and-exceptions.md`.

The ratchet compares a fresh measurement of the repository with an **accepted
baseline** (`quality/baseline.json`). Inherited debt may stay. New or worse
debt fails unless an approved exception names it. Improvements are reported,
and the baseline can only be lowered, using `dokimos ratchet baseline update`.

Observation and judgement stay separate:
- `QualitySignals` measures, and each rule is measured independently.
- `QualityRatchet` judges the measurement against the baseline and the exceptions.

Unavailable evidence is the verdict `unavailable`. It is never a pass.

## Commands

```text
dokimos ratchet check [--root .] [--baseline quality/baseline.json] [--exceptions quality/exceptions.json]
                      [--build-log build.log] [--at <timestamp>] [--json]
dokimos ratchet baseline init --repository <owner/name> [--source dir]... [--generated glob]...
                      [--large-file-lines 400] [--forbid From=To]... [--rule DOK-Rnnn]... [--build-log f] [--write]
dokimos ratchet baseline update [--root .] [--baseline path] [--build-log f] [--write]
dokimos ratchet baseline diff --from old.json --to new.json
dokimos ratchet rules
```

- **`check`** prints a human summary by default. `--json` emits the
  `dokimos.ratchet` contract on stdout.
- **`baseline init`** records the current state once, inherited debt included.
  It refuses to overwrite an existing baseline.
- **`baseline update`** tightens only. For each value it keeps
  `min(accepted, current)` and removes clean scopes. It reports the changes it
  refused, which are worse values and new scopes. It never reads exceptions, so
  an exception cannot change the baseline.
  - Without `--write` it is a dry run.
  - Its output (`dokimos.ratchet-baseline-update`) and the Git diff of the file
    are what reviewers look at.
- **`baseline diff`** exits 4 when the newer baseline loosens anything:
  - a raised value or a newly added scope;
  - a rule dropped from enforcement;
  - a generated glob added;
  - a source root removed;
  - a raised `largeFileLines`;
  - a removed forbidden reference.

  CI runs it against the base revision, so historical debt cannot raise the
  baseline through a hand edit either.

## Exit codes (stable)

| Verdict | Exit | Meaning |
|---|---|---|
| `pass` | 0 | No unexcepted regression. Improvements may be listed. |
| `regression` | 4 | At least one rule is worse than the baseline in some scope, and no valid exception covers it. |
| `invalid-exceptions` | 6 | An exception is malformed or incomplete, names an unknown rule, or has **expired**. Expired exceptions fail; they do not just warn. |
| `unavailable` | 3 | The baseline is missing or unreadable, a source root is missing, or an enforced rule could not be measured (for example DOK-R001 without `--build-log`). Never a pass. |
| invalid invocation | 2 | Bad arguments. A `dokimos.diagnostic` is written to stderr and nothing to stdout. |
| unexpected fault | 1 | An Aegis fault on stderr. |

When more than one verdict applies, the most severe is reported, in this order:
`unavailable` > `invalid-exceptions` > `regression` > `pass`.

## `dokimos.ratchet` 1.0.0 report (for Praxis and other consumers)

Schema: `schemas/dokimos-ratchet.schema.json`. All keys are PascalCase, as in
every Dokimos output contract.

| Field | Content |
|---|---|
| `Contract`, `SchemaVersion` | `"dokimos.ratchet"`, `"1.0.0"` |
| `DokimosVersion`, `CheckedAt`, `Repository` | Provenance. `Repository` is the value recorded in the baseline. |
| `Verdict`, `ExitCode` | `pass` with 0, `regression` with 4, `invalid-exceptions` with 6, `unavailable` with 3 |
| `Reasons` | Why the verdict is not `pass` |
| `Baseline` | `Path`, `Digest` (`sha256:` of the canonical baseline), `SchemaVersion`, `AcceptedAt` |
| `Exceptions` | `Path`, `Digest`, `Active[]`, `Expired[]`, `Invalid[]` (`Id`, `Problems[]`), `Unused[]` |
| `Summary` | `Regressions`, `Excepted`, `Improvements`, `RulesMeasured`, `RulesUnavailable`, `RulesNotEnforced`, `FilesAnalyzed`, `GeneratedFilesExcluded` |
| `Rules[]` | `RuleId`, `Name`, `State` (`measured`, `unavailable` or `not-enforced`), `Reason` |
| `Generated` | The declared `Globs` and the `Files` they excluded |
| `Findings[]` | See below |

Each `Findings[]` entry has these fields:
- `RuleId`, `RuleName` and `Scope`. Scope is `repository` or a repository-relative path.
- `Kind`: `regression`, `excepted-regression` or `improvement`.
- `Degraded`: what changed.
- `Before` and `After`. `BaselineRecorded` is false when `Before` is the rule's default.
- `Description` and `Remediation`.
- `ExceptionProcess`: how to request an exception.
- `ExceptionId`: set when an exception covers the finding.
- `Evidence[]`: `path:line: text`, at most 20 entries.

To consume the report:
1. Run `dokimos ratchet check --build-log <log> --json`.
2. Branch on the exit code, or on `Verdict`, which carries the same information.
3. Treat `unavailable` as "not verified".

Dokimos does not depend on Praxis, and Praxis does not reinterpret findings.

## Rules

Rule IDs are permanent. They are never renamed or reused.

All F# signals are line-based. They read an explicit lexical view of each line
(`SourceLexing`): string and character literal contents are blanked, and
comments are separated out. As a result, detector strings and test fixtures
are never mistaken for code or comments. The analysis is lexical, not a
parser.

Only `*.fs` files are analyzed. `*.fsi`, `*.fsx` and non-F# sources are out of
scope.

| Rule | Name | Scope | Signal | Rationale | Remediation |
|---|---|---|---|---|---|
| DOK-R001 | compiler-warnings | repository | MSBuild `N Warning(s)` summary of `--build-log` | Warnings are deferred defects; a growing count hides new ones | Fix the warnings; do not suppress them (that is R002) |
| DOK-R002 | warning-suppressions | file | `#nowarn`, `#pragma warning disable`, `[<SuppressMessage>]`; project `<NoWarn>` (other than an inherited `$(NoWarn)`), `<WarningsNotAsErrors>`, `NoWarn="..."`, `TreatWarningsAsErrors=false` | A suppression silences evidence for every future change | Fix the underlying warning, or except it with the false-positive rationale |
| DOK-R003 | skipped-tests | file | `Skip = "..."`, `[<Ignore>]`, `[<Explicit>]`, Expecto `ptest*`, `Skip.If/IfNot`, `Assert.Skip` | A skipped test reports success while verifying nothing | Make it pass or delete it with a reason; quarantine only through a dated exception |
| DOK-R004 | debt-markers | file | Comment lines with the whole words TODO, FIXME or HACK | Growth means work declared done while incomplete | Finish it, or track it in the work queue and remove the marker |
| DOK-R005 | large-file-growth | file | Line count of handwritten `.fs` files over `largeFileLines` (default 400) | Growth of an already-large file concentrates responsibility; size is a review signal, not a verdict | Extract a cohesive responsibility first, or except it with a cohesion rationale |
| DOK-R006 | broad-exception-catches | file | try/with handlers catching everything (`_`, an identifier, `:? exn`, `:? Exception`) that are wildcards or never use the exception (same line or the next code line) | Catch-all-and-discard turns failures into silent defaults | Catch specific exceptions and return typed errors, or let faults reach the fault boundary |
| DOK-R007 | package-references | file | `<PackageReference Include=...>` per `.fsproj` / `Directory.Build.props` / `Directory.Packages.props` | Each dependency adds supply-chain, upgrade and boundary cost | Remove it, or except it with justification and owner |
| DOK-R008 | architecture-violations | repository | `<ProjectReference>` edges forbidden by the baseline's `forbiddenReferences` layer map (`Architecture.boundaryViolations`) | Dependency direction is the architecture | Move the shared concept down a layer or invert the dependency |
| DOK-G001 | introduced-finding | finding id | Correlation finding introduced since the baseline snapshot (`dokimos evaluate`) | One validated waiver mechanism for the gate too | Reduce the contributing signals |

### Semantics and scopes

- **Counts (R001 to R004 and R006 to R008).** A scope absent from the baseline
  counts as 0, so any debt in a new file is a regression from 0. A scope
  absent from the measurement counts as 0, so deleting the debt is an
  improvement.
- **Size (R005).** Only files strictly over the threshold are recorded.
  - A recorded file must not grow.
  - An unrecorded file must not cross the threshold. In that case `Before` is
    the threshold.
- **Generated files** are excluded only through the baseline's
  `configuration.generated` globs (`**` crosses directories; `*` and `?` stay
  within one segment).
  - Nothing is inferred. `bin/` and `obj/` are excluded because the baseline
    declares them.
  - Every excluded file is listed in the report, so a glob cannot silently
    hide handwritten code.
  - Adding a glob is a loosening that `baseline diff` rejects.

## Exceptions

File: `quality/exceptions.json`. Schema:
`schemas/dokimos-quality-exceptions.schema.json`
(`contract: dokimos.quality-exceptions`, `schemaVersion: 1.0.0`).

```json
{
  "contract": "dokimos.quality-exceptions",
  "schemaVersion": "1.0.0",
  "exceptions": [
    {
      "id": "EXC-0001",
      "ruleId": "DOK-R005",
      "scope": "src/Dokimos.Core/Contracts.fs",
      "rationale": "Why this specific regression is acceptable now.",
      "owner": "github-handle",
      "created": "2026-10-05",
      "expires": "2026-12-31",
      "reviewCondition": "optional alternative or addition to expires",
      "evidence": ["link to issue, PR or decision"],
      "allowedValue": 712
    }
  ]
}
```

Validation:
- These fields are required: `id`, `ruleId` (a known rule), `scope`,
  `rationale`, `owner`, `created` (not in the future) and `evidence` (at least
  one entry).
- Either `expires` or `reviewCondition` is required. Both may be given.
- `allowedValue` is required for DOK-R rules. It accepts the regression only up
  to that value. Any further worsening is a regression again.
- Repository-scoped rules need `scope: "repository"`.
- Ids must be unique.

Lifecycle and effect:
- An exception is valid through its `expires` date. On the next day the check
  fails with `invalid-exceptions`.
- An exception matches exactly one `(ruleId, scope)`. It changes the judgement
  of that one regression and never changes the baseline.
- Exceptions that match no regression are listed under `Unused` so they can be
  removed.

**Process.** Add the entry in the same pull request as the regression, link
the evidence, and get it reviewed like code. To renew an exception, change its
dates and record fresh evidence. Do not edit the baseline.

### Migration from policy suppressions (waivers)

Policy schema 1.1.0 carried `suppressions[]` inside `config/dokimos-policy.json`.
They applied only to introduced findings, and their `expires` was optional.

Policy 1.2.0 rejects an embedded `suppressions` field. Waivers now live in the
exceptions file:
1. For each active suppression, add an exception with `ruleId: "DOK-G001"`.
   Set `scope` to the suppression's `findingId`, `rationale` to its `reason`,
   and `owner` to its `actor`.
2. Add `created` and an `expires` or `reviewCondition`. Expiry is now
   mandatory.
3. Add `evidence`.
4. Run `dokimos evaluate|results ... --exceptions quality/exceptions.json`.

An invalid or expired exception refuses the evaluation with exit 6. Policies
at 1.0.0 and 1.1.0 still decode their suppressions unchanged.

Dokimos had no suppressions (`suppressions: []`), so its own migration was
empty.

## Gate ratchets are baseline-derived too

In policy 1.2.0 a ratchet's `bestAccepted` may be `"baseline"`. The limit is
then the accepted baseline snapshot's value for the same metric and scope,
instead of a hand-typed number. This applies to every scope, not only the
repository scope.

If a scope has no accepted value, the result is evidence-unavailable (exit 3),
never a pass. Dokimos' own `config/dokimos-policy.json` uses this setting.
