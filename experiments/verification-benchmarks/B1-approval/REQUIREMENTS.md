# Benchmark 1: approval workflow (stateful)

A small web application for approving requests. These requirements and `CONTRACT.md` are the only inputs. The implementation and the executable tests are produced during the benchmark, by separate agents.

## APR-001 Legal states

A request is in one of `Draft`, `Submitted`, `Approved` or `Rejected`. A new request starts in `Draft`.

| State | Command | Next |
|---|---|---|
| Draft | Submit | Submitted |
| Submitted | Approve note | Approved |
| Submitted | Reject note | Rejected |
| Rejected | Revise | Draft |

- **APR-001-O1:** each of the four legal transitions reaches exactly the next state in the table, and increments the version by one.
- **APR-001-O2:** every other (state, command) pair fails closed with `InvalidTransition(state, command)`. The request, including its state, version and note, is unchanged.

## APR-002 Confirmed save

The store writes a request as one JSON file and reports it saved only after reading the file back.

- **APR-002-O1:** a request that is saved and then loaded is equal to the request that was saved: id, title, state, version and note.
- **APR-002-O2:** when the read-back differs from the written bytes, `save` returns `Error ReadBackMismatch` and never `Ok`.

## APR-003 Version conflicts

Every save names the version it expects to replace.

- **APR-003-O1:** saving with an expected version that differs from the stored version returns `Error(Conflict(expected, actual))`.
- **APR-003-O2:** after a conflict, the stored request is byte-for-byte unchanged.

## APR-004 Decision meaning survives

The note given with `Approve` or `Reject` is part of the decision.

- **APR-004-O1:** after `Approve note` or `Reject note`, save and load, the loaded request carries that exact note (including non-ASCII text).

## APR-005 Browser workflow

The browser workflow crosses the real UI, HTTP, domain and store boundaries.

- **APR-005-O1:** in a real browser, create a request, submit it and approve it with a note. After a full page reload, the page shows state `Approved` and the note.
- **APR-005-O2:** the page offers only the commands that are legal in the request's current state. In `Draft` there is no Approve button; in `Approved` there is no command button at all.
- **APR-005-O3:** two tabs open the same request, and the first tab submits it. When the second tab then submits, the page reports a conflict, and after reload the request is still exactly once `Submitted` at version 1.

## APR-006 Credential confinement

The server is configured with a secret `APPROVAL_TOKEN` (environment variable).

- **APR-006-O1:** the token's value never appears in any HTTP response body, response header or stored request file, including error pages.
