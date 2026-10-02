# Dokimos handoff

## Objective

Operate Dokimos as Echelon Foundry's longitudinal code-quality evidence system and complete its standardized Echelon distribution path.

## Current state

- Dokimos is operational: self-measurement, immutable snapshots, comparison, policy evaluation, durable evidence history, quality gates, results projection, Forma UI, and Folio output are implemented.
- Release `dokimos-v0.1.0` is published and was proven in a second repository (Aegis).
- Dokimos is registered in Echelon Registry.
- Praxis CI batching and end-biased agent CI checks were merged in PR #7.
- The next release candidate is 0.2.0. It adopts Registry's shared `echelon.release/v2` contract, adds `linux-musl-x64`, richer executable identity, read-only `status`, and ownership-aware `upgrade`.
- Conditor's Registry-driven distribution resolver remains external work in `kemiller2002/conditor#12`; do not duplicate that resolver inside Dokimos.

## Validation

Run:

```bash
./ros registry check
./ros validate
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

1. Get the 0.2.0 distribution-contract change green on CI and merge it.
2. Publish `dokimos-v0.2.0`.
3. Register the immutable release facts and digests in Echelon Registry.
4. Use Conditor's Registry-driven installer, once issue #12 lands, to prove clean-host install, verify, and idempotent reinstall.
