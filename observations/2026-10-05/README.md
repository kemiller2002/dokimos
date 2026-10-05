# Observations — 2026-10-05 — repository identity correction (issue #17)

## What was wrong

`ros.json` declared this repository as `echelon-design-system` /
"Echelon Design System". It was copied from another project when ROS 3.1.4
was installed: `.ros/installation.json` records `ros.json` with disposition
`adopted-identical`, while the same installation records
`"project_slug": "dokimos"`. The defect was first noted on 2026-09-28
(`observations/2026-09-28/README.md`) but not corrected then.

## What changed

- `ros.json` now declares `name: dokimos`, `project: Dokimos`,
  `repository.id: dokimos` (a shared, user-editable ROS file; `ros validate`
  passes after the change).
- Nothing under `.ros/events/`, `.ros/telemetry/` or `.ros/context/` was edited.

## How to read historical records

Records written **before this correction** carry the copied identity and are
kept byte-identical as historical evidence. Read their `repository` value
`echelon-design-system` as `dokimos`. The exact records are listed in
[`repository-identity-correction.json`](repository-identity-correction.json):

- 54 of 55 events in `.ros/events/events.jsonl` (all except the first,
  install event, which already said `dokimos`);
- 28 of 28 execution records in `.ros/telemetry/executions/`;
- `.ros/context/current.json` (managed context; left for the tool to refresh).

Dokimos' own quality evidence is not affected: snapshots take their
repository identity from the `--repository` argument (`kemiller2002/dokimos`).

## Prevention

`dokimos identity [--root .] [--expected id]` compares `ros.json` with the Git
remote name, an optional expected id and `.ros/installation.json`
`project_slug` (exit 0 consistent, 4 mismatch, 3 undetermined). CI runs it, and
`tests/Dokimos.Cli.Tests/IdentityCliTests.fs` pins this repository's identity,
so a copied identity is caught at adoption rather than weeks later.

The same class of defect exists elsewhere in the portfolio (for example Ordo's
`ros.json` names `state-directed-engineering`); the check is repository-neutral
and can be run there unchanged.
