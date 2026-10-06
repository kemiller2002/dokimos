# Dokimos handoff

## Objective

Operate Dokimos as Echelon Foundry's longitudinal code-quality evidence system and complete its standardized Echelon distribution path.

## Current state

- Dokimos is operational: self-measurement, immutable snapshots, comparison, policy evaluation, durable evidence history, quality gates, results projection, Forma UI, and Folio output are implemented.
- Release `dokimos-v0.1.0` is published and was proven in a second repository (Aegis).
- Dokimos is registered in Echelon Registry.
- Praxis CI batching and end-biased agent CI checks were merged in PR #7.
- `dokimos-v0.2.0` is published (commit `e641048`), cataloged in Registry (echelon-registry#21), declares `echelon.repository-lifecycle` v1, and passed Conditor's clean-host proof with zero drift on linux-x64 (conditor#23, run 37003356848). Do not duplicate Conditor's resolver inside Dokimos.

## Validation

Run:

```bash
./praxis registry check
./praxis validate
dotnet restore Dokimos.sln
dotnet build Dokimos.sln --configuration Release
dotnet test Dokimos.sln --configuration Release --no-build
```

Release validation additionally runs Dokimos's own gate, package installation, native publishing, archive checks, Registry release-contract generation, checksums, and GitHub provenance attestation.

## Ownership boundary

- `.dokimos/policy.json`: created if missing, then user-owned and preserved by upgrade.
- `.github/workflows/dokimos.yml`: Dokimos-owned only when it carries the Dokimos ownership header; upgrade may replace it.
- `.dokimos/installation.json`: Dokimos-owned lifecycle record; upgrade may replace it.
- Unknown ownership fails closed rather than overwriting user content.

## Next action

1. Merge echelon-registry#21 and conditor#23 (merge commits keep the proof's pinned Registry commit reachable).
2. Owner decision: add Dokimos to an `echelon-engineering` successor profile version, or keep it optional.
3. Add a macOS clean-host proof when infrastructure exists.
4. Provenance adoption remains issue #12.
