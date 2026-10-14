# Recognition audit fixture: timer with a settled conflict

Produced by Ordo (kemiller2002/ordo#88, `82930fe`) from Ordo's
`tests/fixtures/construction/time-entry-timer` inputs. It adds:
- `CONFLICT-SESSION-SCOPE` on the L145-L148 requirement;
- `DEC-SESSION-SCOPE` with a `conflictResolution` record settling that conflict.

`DEC-TIMER-STATES` and `DEC-SESSION-SCOPE` are approved, and open questions
are escalated. Tests tamper with copies in memory; these files are never
edited.
