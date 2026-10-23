# Rule-provenance audit and invariant oracle (EXPERIMENTAL)

**Status: experimental.** This is the Dokimos side of the Echelon "verified construction" vertical slice. It is
not in `Dokimos.sln`, not on the default `dokimos` CLI path and not in any CI gate. Nothing here blocks anything
by default. The precision and recall of the analysis have not been measured (see [LIMITATIONS.md](LIMITATIONS.md)).

It answers two questions about a change:

1. **Provenance (VAC-002-P3-03-R1, VC-004 R1–R3).** Does every business-rule symbol, constant and branch trace to
   an accepted requirement or an accepted decision? Which rules have no traceable authority (missing, stale,
   superseded or self-authorized provenance, unauthorized defaults, untraceable exemptions)? The audit tells
   proven facts apart from questions that only the rule's owner can answer. Legacy code is reported, never changed.
2. **Invariants (VC-004 R4).** Within a stated bound, do the handoff's invariants hold for the constructed model?
   The audit uses an oracle that was first validated against a reference model and seeded mutants.

Its output is `echelon.provenance-audit/0.1`, the Dokimos-owned contract in
`contracts/schemas/echelon-provenance-audit-0.1.schema.json`. The schema is copied unchanged from
`cohort-based-engineering/program/contracts` at commit 63941ce.

## Layout

| Path | What |
|---|---|
| `src/RuleProvenance.Core` | Pure F# core: lexer, scanner, provenance analysis, oracle, checker validation, report, reuse check |
| `src/RuleProvenance.Cli` | Thin CLI (`rule-provenance`) and the reflection adapter for `Invoice.Slice.dll` |
| `tests/RuleProvenance.Tests` | xunit tests (59) |
| `contracts/` | Frozen copy of the shared schemas and the invoice-slice fixture (handoff sha256 pinned in a test) |
| `fixtures/constructed/*` | Own constructed samples: one clean sample and four negative samples |
| `fixtures/legacy-reuse/*` | VAC-002-P3-style code-reuse errors: a silent 0.00 default, and the C06 exemption framed as fact |
| `fixtures/probe-standin` | A hand-written `Invoice.Slice.dll` stand-in that implements the Probe contract, with markers and a trace |
| `tools/validate_json.py` | JSON Schema validation (python3 + jsonschema) used by the tests and `run.sh` |
| `run.sh` | Build, test, run on the fixture, `integrate` mode |

## How to run

```sh
./run.sh                 # build + 59 tests + run on the shared fixture (reports in out/)
./run.sh test
./run.sh fixture
./run.sh integrate --ordo-src <dir> --trace <construction-trace.json> --dll <Invoice.Slice.dll> \
                   [--handoff <handoff.json>] [--legacy <path>]... [--out <file>] [--root <dir>]
```

`integrate` defaults `--handoff` to the pinned fixture handoff and `--legacy` to the fixture's legacy file. Pass
`--root <ordo experiment dir>` so that reported paths match the trace's `file` fields. Direct CLI use:

```sh
dotnet src/RuleProvenance.Cli/bin/Debug/net10.0/rule-provenance.dll audit \
  --handoff H [--trace T] [--source P]... [--legacy P]... [--root DIR] \
  [--dll Invoice.Slice.dll | --probe-model reference|mutant:<name>] [--depth N] \
  [--block-on-proven-provenance] [--exclude GLOB]... [--out F] [--validation-out F]
rule-provenance reuse --prior AUDIT.json <the same inputs>        # A2 evidence reuse check
rule-provenance validate-checker --handoff H                     # checker validation report (kill rate)
```

Exit codes follow Dokimos: 0 = pass or pass-with-owner-questions (for `reuse`: reusable), 2 = invalid input,
3 = evidence unavailable (for example, the probe assembly cannot be loaded), 4 = fail (for `reuse`: not reusable),
1 = unexpected fault.

## Analysis method

**Provenance: a lexical and structural scan of F# source.** No compiler is involved. The tokenizer follows
`src/Dokimos.Core/SourceLexing.fs` but keeps the contents of string literals, and it drops comments. Above the
tokens, the scan builds an indentation-based declaration tree: `let`, `member`, `type`, union cases, record
fields, nested modules and their qualified names. It attaches `[<Authority("ID", ...)>]` markers to the next
declaration and assigns each rule-bearing construct to the innermost declaration that contains it. These
constructs count as rule-bearing:

* decimal or money literals other than 0 and 1;
* comparisons against a literal or a named constant;
* string prefix, suffix and containment checks (`StartsWith "STR-"`), and string case patterns;
* `if`, `elif`, `match`, `function` and `when` branches;
* wildcard branches that return a literal (silent defaults);
* `Error "CODE"` refusals.

FSharp.Compiler.Service was not used. The marker form is fixed text, and the hard part is deciding what counts
as business policy, which an untyped AST does not solve. FCS would add a heavy dependency that is coupled to the
compiler and SDK versions. It would improve declaration extents and symbol resolution, and it is the natural next
step if this graduates.

| Code | Finding | Certainty |
|---|---|---|
| constructed | marker cites a superseded record | `superseded-authority`, proven |
| constructed | marker cites a proposed or rejected decision | `self-authorized`, proven |
| constructed | marker cites an observation, an unknown ID or nothing; rule-bearing construct with no marker and no traced authority | `missing-provenance`, proven |
| constructed | construct whose only authority is an unresolved question, yet marked `authorized` in the trace | `unresolved-question-encoded-as-rule`, proven |
| constructed | construct cites a question, but no refusal naming it is found in its body or within 4 reference hops | `unresolved-question-encoded-as-rule`, **uncertain**, routed to the question owner |
| constructed + trace | handoff digest or ID differs; traced symbol not in source; marked symbol not traced; marker and trace IDs differ; output digest stale | `trace-mismatch`, proven |
| legacy | rule-bearing code at an observation's symbol, plus unclaimed helpers it references | `unverified-inherited-rule` (exemption, default, inherited rule) or `superseded-authority` (superseded-rule), **uncertain** |
| legacy | rule-bearing code that matches no observation | `unverified-inherited-rule`, or `superseded-authority` when a literal equals a superseded decision parameter; **uncertain** |
| probe | invariant counterexample from a validated checker | `invariant-violation`, proven, blocking |

For a legacy finding, the owner is the owner of the affected decision. The affected decisions are the IDs named
in the observation, any decision whose parameter equals a literal in the code (25000.00 = DEC-FIN-000.limit,
10000.00 = DEC-FIN-001.limit), and the observation's question. Accepted decisions take precedence. When nothing
matches, the owner falls back to `--unmatched-owner` (default `praxis-intake`). Legacy files are read only. A test
checks that their bytes and modification times are unchanged.

Generated files (an `<auto-generated` header or a `.g.fs` name) take their provenance from the trace. A trace
symbol written `Decl:Row` resolves to `Decl`. A trace entry with `technical` disposition covers its constructs
without a marker.

**Invariants: bounded exhaustive exploration** of `Invoice.Slice.Probe`, done breadth-first, so each
counterexample is a shortest one. The default bound:

* depth 4;
* totals {0.01, 9999.99, 10000.00, 15000.00, 30000.00};
* amounts {-1, 0, 0.01, 5000, 9999.99, 10000.00, 10000.01, 25000.00, 30000.00};
* customers {C-001, STR-001};
* 20 commands per step.

A refused command does not extend a sequence, because refusal leaves the state unchanged. The checks are:

* INV-INV-001: outstanding is never negative.
* INV-INV-002: the state is Paid if and only if outstanding = 0 and at least one payment was applied.
* INV-INV-003: every applied payment is > 0, at most the balance before it, and at most the limit.
* INV-INV-004: Paid and Voided refuse every command.
* DEC-FIN-001: a payment above the limit is never accepted. A payment within the balance but above the limit is
  refused with `PAYMENT_LIMIT_EXCEEDED`. A direct check pays 10,000.01 for every customer, including STR-001,
  regardless of the bound.
* Q-FIN-001: Void on a partially paid invoice is refused, with a code that names the question.

The limit, refusal code and question come from the handoff, never from the implementation. The oracle works over
a record of functions (`Probe<'S>`). `ProbeLoader` fills that record by reflection from any assembly that exposes
the agreed `Invoice.Slice.Probe` API.

**Checker validation (VC-004 R4).** Each audit that runs the oracle first runs it against a reference model of the
handoff and 7 seeded mutants:

* drop the limit;
* the superseded 25,000.00 limit;
* the STR- exemption;
* overpayment allowed;
* Void allowed from PartiallyPaid;
* Void allowed from Paid;
* Paid while a balance remains.

An invariant violation blocks only when the reference holds and every mutant is killed. Otherwise it is reported
as non-blocking. The tests also validate the oracle against a separately written test-local model and 5 mutants.

## Blocking policy (VC-004 R4, R5)

* `uncertain-authority` never blocks, including with the flag below.
* Proven provenance findings do **not** block by default, because precision and recall are unmeasured.
  `--block-on-proven-provenance` makes them blocking. The flag is part of `configDigest`.
* Only an `invariant-violation` found by a validated checker blocks by default.
* Verdict: `fail` if any finding blocks (exit 4); otherwise `pass-with-owner-questions` if there are findings;
  otherwise `pass`. So a proven but non-blocking provenance finding yields `pass-with-owner-questions`. Read
  `summary.proven` and each finding's `certainty` rather than the verdict alone.

## Output and evidence reuse

The audit writes deterministic JSON: fixed property order, findings sorted by path, line, class and symbol,
sequential IDs, LF line endings, a trailing newline, and no timestamps. A test confirms that reruns are
byte-identical and that input order does not change the result. The `validity` block holds:

| Field | Meaning |
|---|---|
| `sourceDigest` | sha256 over the sorted files, each framed as `role\npath\nlength\ncontent\n` |
| `handoffDigest` | sha256 of the handoff file bytes |
| `traceDigest` | sha256 of the trace bytes, or **the sha256 of the empty string** (`e3b0c442...`) when no trace is supplied |
| `toolName`, `toolVersion` | the tool's name and version |
| `configDigest` | sha256 of the canonical effective configuration: flag, owners, excludes, repository, revision, probe label, bound |
| `sdkVersion` | `--sdk-version`, or the runtime description; `run.sh` passes `dotnet --version` |
| `dependencyDigest` | sha256 of the probe assembly, when one was loaded |

`Reuse.isReusable : PriorAudit -> CurrentInputs -> Result<unit, InvalidationReason list>` compares every one of
these. Each mismatch has its own reason code (`source-changed`, `handoff-changed`, `trace-changed`,
`tool-name-changed`, `tool-version-changed`, `config-changed`, `sdk-version-changed`, `dependency-changed`). The
CLI exposes it as `reuse`.

## Requirement mapping and evidence

| Item | Status | Evidence |
|---|---|---|
| P3-03-R1 every rule traces to an accepted record | Implemented (lexical) | `Provenance.constructed`, `ProvenanceTests` (5 constructed fixtures) |
| P3-03-R1 untraceable exemption, default or superseded rule reported to its owner, never silently fixed | Implemented | `Provenance.legacy`; the shared fixture yields exactly OBS-LEGACY-001 and OBS-LEGACY-002 for finance-controller; P3 fixtures yield one owner question each; legacy-bytes-unchanged test |
| P3-03-R3 non-blocking review of reused rules | Output usable for it; **not run** (no paid review) | Each finding carries `location.symbol`, `authorityRefs` and `owner`, which is what a reviewer asking "which existing rules does this change reuse, and from what source?" needs |
| VC-004 R1 static checks linking branches, constants, constructors and transitions to provenance | Implemented (lexical) | Scanner construct kinds; trace cross-checks in `TraceTests` |
| VC-004 R2 missing, stale or superseded provenance, unauthorized defaults, untraceable exemptions; lack of provenance distinguished from a proven violation | Implemented | `certainty` proven vs uncertain-authority; trace staleness checks; silent-default and C06 fixtures |
| VC-004 R3 positive and negative fixtures incl. P3 code-reuse errors; never fix or delete rules | Implemented | `fixtures/constructed`, `fixtures/legacy-reuse`; legacy files read only |
| VC-004 R4 block only on a formally specified invariant with a validated checker; ambiguity is a non-blocking owner question | Implemented | `Report.isBlocking`; `OracleValidation` (7/7 killed); test that an unvalidated checker does not block; drop-limit mutant gives a blocking `invariant-violation` (exit 4) |
| VC-004 R5 deterministic, reproducible reports; no default blocking until measured | Implemented | Determinism tests; `--block-on-proven-provenance` off by default |
| VC-004 R6 | Not in this brief | — |
| VC-004 R7 compatible with Dokimos and Praxis evidence contracts | Partial | Conforms to `echelon.provenance-audit/0.1` (jsonschema test); Dokimos exit codes; Dokimos-style framed sha256 digests. It does **not** emit `dokimos.results 1.0.0`, and no Praxis evidence ingestion was attempted |
| Slice outcome 4 | Implemented and tested locally | `ProvenanceTests` (shared legacy fixture) |
| Slice outcome 5 | Implemented and tested locally against the reference model, mutants and the stand-in DLL | `OracleTests`; `run.sh fixture` checks that the drop-limit mutant exits 4 |

**What is proven** (in the narrow mechanical sense): a citation resolves, or fails to resolve, to a handoff record
of a given status; the trace and source markers agree or disagree; and, for invariants, a concrete counterexample
sequence exists, or none exists **within the stated bound**. **What is uncertain:** whether a construct is
business policy at all (lexical classification); whether a legacy rule is wanted (always the owner's call); and
whether a question-citing construct really refuses and records the question when no lexical evidence was found.

## Dry run against the in-progress Ordo slice (informal)

Before integration, a read-only dry run of `integrate` against `/home/user/ordo/experiments/verified-construction`
(work in progress at the time) found:

* all six oracle checks holding within the bound;
* the two legacy owner questions;
* 8 unmarked handwritten or generated `match` helpers (`missing-provenance`, proven, non-blocking);
* 1 trace row with no matching declaration;
* 1 lexically unresolved question citation (uncertain).

An earlier build of that slice returned `UNCODED_REFUSAL` for Void on a partially paid invoice, and the
Q-FIN-001 check caught it. That run motivated the generated-file, row-symbol and uncertain-question refinements.
These numbers are anecdotal and not a precision measurement. Nothing in the Ordo tree was modified.
