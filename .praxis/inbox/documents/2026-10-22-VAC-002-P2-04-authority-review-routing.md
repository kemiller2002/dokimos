# VAC-002-P2-04: Independent authority review: route ambiguity to owners, block only unauthorized behaviour

Priority: P2
Owner system: Dokimos
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Experimental finding: `kemiller2002/cohort-based-engineering` → `research/experiments/VAC-002/P2/`
(`PILOT-RESULTS.md` §5.3, §6; `RECOMMENDATIONS.md` R-DOK-1, R-DOK-2).

## Finding

**Arm E's independent reviewer:**

- read the plan and the change against the repository's sources of authority;
- made 16 calls, which cost +84% over the arm without it (D);
- found the corpus's one real authority ambiguity (S12: an approval qualifier the system cannot represent);
- found low-stakes undeclared choices.

**The cost of the ambiguity finding.** It marked the ambiguity *blocking*. The implementer then refused the required
behaviour and failed the work item.

**The authority-record checks:**

- the checks: no implementer-written approvals, verification records or accepted decisions;
- the result: 0 false positives on validated alternatives, 0 events in 60 executions.

## Requirements (proposed; additional experiment required)

- **VAC-002-P2-04-R1.** In a review against authority, classify findings as *unauthorized behaviour* (blocking) or
  *ambiguity for the owner* (routed as a recorded question, non-blocking). Test this in P3's review arm.
- **VAC-002-P2-04-R2.** Keep the authority-record checks (RC1–RC3) as a candidate gate check. Adopt them only when an
  experiment observes the behaviour they catch.

## Evidence class

The reviewer surfaced an ambiguity that no other arm surfaced (1 event), and a blocking finding caused a functional
failure (1 event): **Supported but preliminary**. Prevention: **Inconclusive**.
