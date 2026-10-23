# Limitations (EXPERIMENTAL)

## v2 (`ast`, the default): untyped parse trees

**What the untyped tree gives.** Exact expression structure, so a condition spread over several lines, a comparison
in any boolean expression, union-case patterns and comparisons, integer constants and match keys are all seen.
Declaration extents come from the parser, not from indentation. Comments and doc comments are kept per declaration.

**What a typed check would add, and v2 does not have:**

* **Union cases are recognised by shape.** A capitalised identifier that is not an F# built-in or a BCL sentinel is
  treated as a category value. A capitalised value or a module constant (`Limits.Default`) is misread as a category;
  a union case reached through a lowercase alias is missed.
* **Name-based resolution.** A category predicate is found by its declared name across the audited files only. A call
  through a pipeline (`x |> isGold`), a partial application, a predicate in an unaudited file, or two predicates with
  the same name in different modules are not resolved correctly.
* **No types for literals.** Integral versus fractional form is read from the literal's syntax, not from its type.
* **No data flow.** A constant's *uses* are not followed. In the development set, `dueDate`, `lateFee`, `reminderAt`
  and `Fines.fineFor` use a superseded constant but are not reported themselves; only the constant is.
* Typed checking was not attempted because it needs each file's full compilation closure (project order, sibling
  projects, packages), which an audit over individual (often legacy) files does not have.

**Classification risks:**

* **Plain branches are no longer rule-bearing.** An `if`/`match` on a computed condition with no literal, union case or
  category test is not reported (v1 reported every branch). Policy expressed purely as control flow over computed
  values (`if a.Age > limit` where `limit` is a parameter) is missed unless the compared constant is itself declared
  with a literal in the audited files.
* **0 and 1** count only in value positions. A threshold of 1 used as a comparison operand (`count >= 1`) is missed.
* The `Math.Round`/`Decimal.Round` digits argument is never counted, so rounding precision is not audited.
* Policy expressed through data (lookup tables, maps, configuration files, JSON) or through types alone is not seen.

**Authority-matching risks (false support, i.e. missed questions):**

* **Coincidental values.** Any accepted decision parameter with the same value supports a literal, even an unrelated
  one (a list ordinal such as "3." in a decision body becomes a parameter under the EXP-3 handoff rules). Same-form
  preference and citation precedence reduce this but do not remove it.
* **Category naming is textual.** A category is supported when an accepted record's statement contains it as a whole
  word (case names case-insensitively) or a parameter key segment equals it. A common word used as a case name (`Paid`,
  `Active`) is easily "named". Keys are matched independently of the values in the same clause: in the development set
  a region key was supported by the tax decision's key while its value matched the shipping decision.
* **A cited accepted record without parameters** supports any numeric value in the declaration (rule (c) as
  specified), unless another cited record declares a mismatching parameter.
* **Exemptions** need a cited accepted record or an accepted `exception`-kind record naming the category. A cited
  record that mentions the category for an unrelated reason would still support it.
* Comment citations are trusted as far as they match: a stale comment that cites an accepted record and happens to
  match the value is not questioned.

**Constructed mode** keeps v1's marker-or-trace contract; matching does not excuse constructed code.

## Development-set measurement only

v2 was developed on the VAC-002-P3 corpus that EXP-3 phase 1 had already scored, using its misses and false
questions. Its development-set result (recall 13/13, false questions 0/15; README) is **biased upward** and is not a
confirmatory result: the rules were chosen while looking at these 13 planted rules and 3 code shapes per domain. The
corpus is also small and repetitive (one billing base file recurs in 13 scenarios). A **held-out confirmation** on a
corpus v2 was not tuned on, with a preregistered protocol, is required before any claim about its precision or recall
and before any default gate. Until then proven provenance findings stay non-blocking and the tool stays out of the
default CLI and CI.

## v1 (`lexical`): lexical analysis, not compilation

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

v2 replaces these heuristics with an untyped parse tree; the v1 list above applies only with `--analyzer lexical`.
EXP-3 phase 1 measured v1 at recall 5/13 and a false-question rate of 57/63 on VAC-002-P3.

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

## No confirmatory precision and recall

The only labelled corpus (VAC-002-P3, via EXP-3) is now a development set for v2. No confirmatory precision, recall
or false-rejection rate exists for v2. For that reason:

* proven provenance findings are non-blocking by default (`--block-on-proven-provenance` is off);
* the tool is not wired into the Dokimos CLI or CI;
* the Ordo dry run in the README is anecdotal.

A confirmatory measurement needs a held-out labelled corpus (new scenarios or domains, labels and handoff rules fixed
before running v2) and a preregistered protocol, before any default gate is considered.

## No paid review run

VAC-002-P3-03-R3 (a targeted, non-blocking independent review of reused rules) was not executed. The output is
shaped for it (symbol, authority references, owner per finding), but no agent or human review was run or
measured.

## Contract and environment notes

* `RuleProvenance.Core` now depends on FSharp.Compiler.Service 43.12.400 (a large package coupled to the F# compiler
  release). Parsing uses one lazily created `FSharpChecker` per process; the parse result is a deterministic function
  of file name and text, but the core is no longer free of that shared parser instance.

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
