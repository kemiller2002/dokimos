# DOK-SDK-001: The Dokimos-managed workflow honours the repository's `global.json`

Priority: P2
Owner system: Dokimos (installation / managed workflow)
Target repositories: dokimos
Intake: Praxis document ingestion. Source material, not a canonical requirement.

## Evidence (observed)

- **Defect D-06** in kemiller2002/indy-init-rehearsal-003 (`research/rehearsal-003-defect-analysis.md`):
  - The repository's `global.json` pinned SDK 10.0.402 with `rollForward: disable`.
  - The Dokimos quality gate then failed with `A compatible .NET SDK was not found. Requested SDK version: 10.0.402`;
    the runner had 10.0.401. Exit 155, run 40078381238.
  - The repository may not edit `dokimos.yml`, because Dokimos manages it.
- **Defect D-03** (same source): the managed workflow did not set the environment the repository's tests needed.
  The tests had to learn to fetch their own data.
- **The template ignores `global.json`.** It emits `actions/setup-dotnet@v4` with `dotnet-version: '8.0.x'`
  (`src/Dokimos.Core/Installation.fs` lines 114–116, `003204d`).
- **Each application works around it.** R3 loosened its own SDK policy (`latestFeature`) to accommodate a workflow
  it does not own.

## Requirements (proposed)

- **DOK-SDK-001-R1.** When the repository root has a `global.json`, the managed workflow's SDK setup uses it
  (`global-json-file: global.json`) in addition to Dokimos's own required SDK, so a repository's exact pin is
  installed.
- **DOK-SDK-001-R2.** When the pinned SDK cannot be installed, the gate fails before testing with a named diagnostic
  (`DOK.INSTALL.SDK_UNAVAILABLE` or similar), not as a test failure.
- **DOK-SDK-001-R3.** Without a `global.json`, behaviour is unchanged.

## Acceptance criteria

- A fixture repository with `global.json` at a pinned patch and `rollForward: disable` passes the managed gate.
- A fixture pinning a non-existent SDK fails with the named diagnostic.
- The generated workflow for a repository without `global.json` is byte-identical to today's output.
