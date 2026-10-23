# Invoice vertical-slice fixture (synthetic)

Shared test input for the verified-construction vertical slice (IMPLEMENTATION-PLAN.md wave B). It is not the
requirements of any product, and `DEC-FIN-*` stand in for approvals; they are test inputs, not approvals.

| File | Role in the slice | What it exercises |
|---|---|---|
| `sources/invoicing-requirements.md` | accepted requirements REQ-INV-001..003 | invoice states, payment validation, persistence |
| `sources/finance-decisions.md` | decisions DEC-FIN-000 (superseded), DEC-FIN-001, DEC-FIN-002 (accepted) | a financial policy limit; supersession; an authorization boundary |
| `sources/open-questions.md` | Q-FIN-001 | an unresolved policy question that must not become a rule |
| `legacy/LegacyPaymentRules.fs` | existing code, observed behaviour only | an inherited, unauthorized exemption (OBS-LEGACY-001, the P3 C06 mechanism) and a superseded limit still in code (OBS-LEGACY-002) |
| `handoff.json` | the Praxis-side handoff (`echelon.authority-handoff/0.1`) | what Praxis would export; hand-authored because no Praxis exporter exists yet (CURRENT-STATE.md B1) |

Expected slice outcomes (frozen acceptance for the slice, IMPLEMENTATION-PLAN.md B2–B4):

1. The constructed model refuses payments above 10,000.00 for **every** customer (DEC-FIN-001). It does not carry the
   STR- exemption or the 25,000.00 limit.
2. `Void` on a partially paid invoice is refused with a code that names Q-FIN-001, and the trace marks it
   `deferred-pending-question`. It is not encoded as either "allowed" or "forbidden by policy".
3. A payment cannot be persisted unless it is a validated, applied payment written under the payments-service
   capability; violating code does not compile.
4. Dokimos reports OBS-LEGACY-001 and OBS-LEGACY-002 as `uncertain-authority` owner questions (non-blocking) routed to
   finance-controller, and leaves the legacy code unchanged.
5. INV-INV-001..004 hold within the stated bound, and a seeded mutant that drops the limit is caught as a proven,
   blocking `invariant-violation`.

`handoff.json` SHA-256 at fixture-v1: `ec5d0e6a4343b1774d40760c04af200a9b71147940305d1ddbabbc5c6db68bcd`.
