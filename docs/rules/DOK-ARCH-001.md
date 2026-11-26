# DOK-ARCH-001 — Thin API boundary

Status: implemented (`dokimos architecture check`; requirement RQ-DOKIMOS-ARCH-001, portfolio requirement
EAR-API-001).

## Activation

Active only when `.echelon/reliability.json` declares `continuousAvailability` `applicable` or
`partially-applicable`; otherwise `not-applicable` (never a pass). An undecodable declaration is `unavailable`.

## Configuration

The API projects and their contracts are declared in the repository's existing Dokimos baseline,
`quality/baseline.json`, next to the `forbiddenReferences` layer map:

```json
"configuration": {
  "apiBoundaries": [ { "api": "App.Api", "contracts": ["App.Contracts"] } ]
}
```

Names are project names (project file name without extension), as in `forbiddenReferences`. The field is
optional and omitted when empty, so existing baselines keep their digest. `dokimos ratchet baseline diff` treats
removing a boundary or adding a contract as a loosening (exit 4).

With the tenet active and no boundary configured, the rule is `not-configured`: reported, not a pass, and not a
failure. A configured API project that does not exist among the analyzed project files, or a baseline that cannot
be read, makes the rule `unavailable` (exit 3).

## Rule

Every `<ProjectReference>` of a configured API project must target one of its declared contract projects. Any
other project reference is a finding with scope `<ApiProject>-><TargetProject>` (exit 4). Package references are
not judged by this rule.

## Accepting a reference

An exception in `quality/exceptions.json` with `ruleId: "DOK-ARCH-001"`, the exact scope and `evidence` naming a
`DF-`/`ADR-` decision record. Prefer declaring a genuine contract project in `apiBoundaries` instead.

## What a pass does not establish

That the contract projects are themselves thin, that the API host contains no domain logic in its own source, or
any runtime property. It checks project-reference edges only.
