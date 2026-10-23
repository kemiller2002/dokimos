# Limitations (EXPERIMENTAL)

## Lexical analysis, not compilation

The provenance scan tokenizes F# and infers structure from indentation. It does not type-check, resolve names or
expand computation expressions.

**False-positive risks** (rule-bearing code reported when it is not policy):

* Every `if`, `match`, `function` and `when` in an audited file counts as rule-bearing. Plumbing such as
  `Probe.stateName`, refusal-to-string mappers and option unwrapping is therefore reported unless it is marked,
  traced as `technical`, or excluded with `--exclude`.
* A comparison with a literal, such as `s.Length = 3`, is treated as a rule.
* Matching is by name. A reference to `limit` matches every declaration named `limit` in the audited set, which
  affects legacy closure and the transitive search for question naming.
* Trace symbols are matched by dot-aligned suffix. Two declarations with the same suffix in different modules can
  be confused. The trace's `file` field breaks ties.

**False-negative risks** (policy code missed):

* Integer literals outside comparisons are not counted. A business threshold written as `25000` with no `m`
  suffix and used in arithmetic is missed.
* A comparison is recognised only when its operator is surrounded by spaces, or is `<=`, `>=` or `<>`. So
  `a>limit` is missed, which is the price of not confusing `Result<int, string>` with a comparison.
* Policy expressed through data (lookup tables, maps, configuration files, JSON) or through types alone is not
  seen.
* A rule can decide an unresolved question without citing it, for example by allowing Void from PartiallyPaid
  under a marker that cites only REQ-INV-001. The scan does not see this. Only the oracle's Q-FIN-001 check
  catches it, and only at runtime within the bound.
* "Names the question" is lexical. Any identifier or string whose letters and digits contain `QFIN001` counts,
  including a misleading name. The 4-hop search follows identifiers, not resolved symbols.
* Legacy closure attributes a referenced helper to an observation only when the helper is in the same file.
* Comments are dropped. An exemption framed as fact in a comment (the C06 pattern) is found from the code, the
  prefix branch inside a symbol carrying an accepted decision's value, not from the comment's wording.

FSharp.Compiler.Service, untyped or typed, would remove the indentation heuristics and the name-based resolution.
It would not decide what counts as business policy.

## Bounded exploration is not proof

`holds-within-bound` means no counterexample exists among the explored sequences:

* depth 4;
* 5 start totals;
* 9 amounts;
* 2 customers;
* refused commands not extended.

Behaviour that needs longer sequences, other amounts (for example rounding at 3 decimal places), other currencies
or other customer IDs is not covered. The direct DEC-FIN-001 check adds one amount, the limit plus 0.01, for every
customer, independent of the amount set. The oracle assumes `step` is deterministic and pure. An exception from
the target is recorded as a refusal code `PROBE_EXCEPTION:<type>`, not as a separate failure class.

The checker validation shows that the oracle distinguishes the reference from 7 specific seeded mutants (plus 5
in the tests). It does not show the oracle catches every defect. Each mutant was designed with one invariant in
mind, so the kill rate is a sanity check, not a mutation score.

## Unmeasured precision and recall

No labelled corpus exists. Precision, recall and the false-rejection rate are **not measured**. For that reason:

* proven provenance findings are non-blocking by default (`--block-on-proven-provenance` is off);
* the tool is not wired into the Dokimos CLI or CI;
* the Ordo dry run in the README is anecdotal.

A measurement plan would label findings from several constructed and legacy samples and count true and false
positives per class before considering any default gate.

## No paid review run

VAC-002-P3-03-R3 (a targeted, non-blocking independent review of reused rules) was not executed. The output is
shaped for it (symbol, authority references, owner per finding), but no agent or human review was run or
measured.

## Contract and environment notes

* `invariant`-kind authority (for example INV-INV-003) is accepted when every record it derives from is
  accepted. The brief listed only requirements, decisions and questions; the Ordo trace uses invariants, so this
  was added.
* The verdict enum has no "pass with proven non-blocking findings" value. Such reports read
  `pass-with-owner-questions`, with `summary.proven > 0`.
* The projects target `net10.0`, overriding the repository's `net8.0`, because only the .NET 10 runtime is
  installed here. The reflection adapter loads `net8.0` probe assemblies into this host.
* The output is `echelon.provenance-audit/0.1` only. No `dokimos.results 1.0.0` or Praxis evidence envelope is
  produced.
* Relative paths are computed from `--root` (default: the current directory). Moving the root changes the
  reported paths and `sourceDigest`.
