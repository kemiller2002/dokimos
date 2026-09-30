# Operational evidence

Real runs that close the default-branch, release, registration and
second-repository requirements in
[`OPERATIONAL-REQUIREMENTS.md`](../../requirements/OPERATIONAL-REQUIREMENTS.md).
Every entry names a run, commit or file that can be inspected; nothing here is
a local-only claim.

## Dokimos on itself (DOK-OPS-003)

| Step | Evidence |
|---|---|
| Operationalization merged | PR #5 → `main` at `2923d18` |
| Gate on `main` push | CI run 218: build, tests, package validation, snapshot, compare, evaluate, persist |
| Evidence persisted | branch `dokimos-evidence` at `26233ad`: imported `BASELINE-0001` acceptance and snapshot `kemiller2002/dokimos@2923d18…:4d577cdc88d2d8ff` |
| First schema-2.0.0 baseline | CI dispatch run 219 (`accept-baseline=true`): `baselines/default/20260929T172241981Z.json`, evidence branch `9405431` |

## Release (DOK-OPS-009)

Release run #1, dispatched from `main` at `2923d18`, published `dokimos-v0.1.0`:
`EchelonFoundry.Dokimos.Cli.0.1.0.nupkg`, five native archives,
SHA-256 checksums, `dokimos.release.json` and `package-validation.json`, with build
provenance attestation. The run built, tested, gated Dokimos on itself and
validated a clean package install before publishing. nuget.org publication
stays off until `DOKIMOS_NUGET_PUBLISH` and Trusted Publishing are configured.

## Registration (DOK-OPS-012, DOK-OPS-027)

`echelon/dokimos.system.json` is registered in the authoritative registry:
kemiller2002/echelon-registry PR #4, merged as `9fe59a5`
(`registry/systems.json`, `examples/dokimos.system.json`).

## Second repository: aegis (DOK-OPS-010, DOK-OPS-025)

Installed with the reusable action and the released package, following aegis
governance (ROS work item AEG-DOK-001).

| Capability | Evidence |
|---|---|
| Installation | kemiller2002/aegis PR #5, merged as `e1c1380`: `.github/workflows/dokimos.yml` pins `actions/quality-gate@2923d18`, version `0.1.0` and the package SHA-256; `.dokimos/policy.json`, `.dokimos/installation.json` |
| Version reporting | PR run 36605479285: installed `0.1.0` from the release with the checksum verified |
| Analysis, snapshot, comparison, evaluation, CI disposition | PR run 36605479285: gate `passed`, exit 0, no baseline yet; 19 evidence files uploaded (artifact 11050878974). Re-run on the merged head: run 36611260371 green |
| Durable storage | `main` push run 36614746656: created branch `dokimos-evidence` (`d9f2022`) with snapshot `kemiller2002/aegis@e1c1380…:8b809bd30e272c84` (schema 2.0.0, 224 metrics, 14 findings) |
| Baseline | dispatch run 36683592855 (`accept-baseline=true`, actor `kemiller2002`): `baselines/default/20260930T072615169Z.json`, evidence branch `74e1edf`; the re-collected snapshot had the same identity, so the store accepted it without conflict |
| History retrieval | `dokimos history --store <aegis dokimos-evidence> --repository kemiller2002/aegis` returns the snapshot and the accepted baseline; `--metric tests.total` returns the series (311 tests at `e1c1380`) |

The prerequisite Limen installation record fix in aegis (PR #6, `dff14fa`)
was separate from the adoption and landed first.

## Still external

- DOK-OPS-026: Conditor has no lifecycle distribution kind for .NET-tool /
  GitHub-release components (kemiller2002/conditor).
- nuget.org publication is an owner decision. The repository is licensed MIT (`LICENSE`).
