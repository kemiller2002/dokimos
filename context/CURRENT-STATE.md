# Current State

Updated: 2026-09-29

## Established

- Project charter, comprehensive requirements, and architecture boundaries.
- Ordo/SDE 1.3.0 and ROS 3.1.4 canonical foundation artifacts.
- Requirement 0: longitudinal evidence is foundational.
- Versioned Dokimos snapshot schema and metric catalog.
- Immutable bootstrap observations from real CI history.
- Comparison/trend engine with unavailable and incompatible evidence semantics.
- Finding lifecycle including resurfacing.
- Baseline threshold and best-demonstrated-state ratchet policy.
- Git temporal model for commit frequency, churn, first/last change, and rename evidence.
- Confidence-bearing repeated-region history.
- Decomposable hotspot evidence without a universal opaque score.
- First conservative F# structural source analyzer.
- Dokimos CLI measurement surface.
- CI self-measurement and Git-history evidence artifact generation.
- Candidate rule DOK-FS-001 discovered from repeated F# record-inference failures.
- Snapshot-to-snapshot comparison pipeline.
- Forma, Folio, and Aegis integration requirements aligned.

## Website (DOK-WEB-001)

- Static public site in `site/`, built with `npm run build` into `dist/` and deployed by `.github/workflows/deploy-pages.yml` to `dokimos.echelonfoundry.com`.
- Pull requests run `.github/workflows/site-validation.yml` (ROS, Visual Engineering, site tests, build, browser audit) without deploying.
- Pending external actions: Pages source = GitHub Actions, custom domain, DNS CNAME, HTTPS enforcement, post-deploy verification. See `docs/website/DEPLOYMENT.md`.

## Operationalization (DOK-OPS, `requirements/OPERATIONAL-REQUIREMENTS.md`)

Branch `claude/dokimos-operationalization-miyxpa`, draft PR #5.

Implemented and tested locally (Domain 4, Core 100, CLI 37 tests; site 72; clean under .NET 8.0.131 and 10.0.112 SDKs):

- DOK-OPS-001 comparison wire contract (`Contracts.fs`, `schemas/dokimos-comparison.schema.json`); the `MetricChangeKind` serialization fault is gone.
- DOK-OPS-002 `dokimos evaluate` with exit codes 0/2/3/4 (plus 1 fault, 5 store conflict).
- DOK-OPS-004 end-to-end quality-gate proof tests (`tests/Dokimos.Cli.Tests/QualityGateProofTests.fs`).
- DOK-OPS-005 immutable evidence store (`EvidenceStore.fs`); Git-backed on branch `dokimos-evidence`.
- DOK-OPS-006 `dokimos history` (snapshots, metric, file, findings).
- DOK-OPS-008 .NET tool `EchelonFoundry.Dokimos.Cli` 0.1.0; `scripts/validate-package.sh` proves clean install.
- DOK-OPS-013 analyzer capabilities (`Capabilities.fs`, `dokimos capabilities`); catalog JSON test-checked.
- DOK-OPS-015 decomposable hotspots; DOK-OPS-018 `dokimos results` contract.
- Snapshot schema 2.0.0 with producer provenance and performance (DOK-OPS-022/023/024 in progress: code done, needs CI evidence).

Real CI evidence: PR #5 run 36598149081 (commit 2c1555e) passed build, tests, package-install validation and the Dokimos gate (`passed-with-warnings`, exit 0; evidence artifact 11047696428). DOK-OPS-021: the CLI fault boundary uses pinned `EchelonFoundry.Aegis.Core` 1.0.0 (FSharp.Core pinned to 10.1.400 solution-wide).

Written, awaiting default-branch/release evidence: DOK-OPS-003 persistence on `main` self-CI gate (`.github/workflows/ci.yml` + `actions/quality-gate`), DOK-OPS-009 release (`.github/workflows/release.yml`), DOK-OPS-010 reusable action, DOK-OPS-011 `init/verify/doctor`, DOK-OPS-012 `echelon/dokimos.system.json`.

External blockers:

- First release `dokimos-v0.1.0` needs PR #5 merged to `main` and a tag push or main dispatch (human decision). nuget.org needs Trusted Publishing configured (`DOKIMOS_NUGET_PUBLISH`, `DOKIMOS_NUGET_USER`, environment `nuget`). The repository has no LICENSE file; choose one before public package publication.
- DOK-OPS-025 second-repository CI proof (aegis) needs that release. A local proof in an aegis clone passed (install from package, init, verify, snapshot, store, baseline, regression warning, failing tests exit 4, history).
- DOK-OPS-026 Conditor distribution kind for .NET-tool lifecycle components (kemiller2002/conditor).
- DOK-OPS-027 add `dokimos` to `echelon-registry/registry/systems.json`: draft PR kemiller2002/echelon-registry#4 awaiting owner review.
- BASELINE-0001 remains the accepted baseline (schema 1.0.0). The first push to `main` imports it into the evidence store.

## Resume point

1. Check CI on PR #5; fix until green.
2. After merge: push tag `dokimos-v0.1.0` (or dispatch Release from main), then run the Dokimos CI workflow on main with `accept-baseline=true` to accept the first schema-2.0.0 baseline.
3. Open the aegis installation PR using `dokimos init --version 0.1.0 --action-ref <release commit> --package-sha256 <sha>`.
4. DOK-OPS-019 results UI is implemented (`results-ui/`, `docs/operations/RESULTS-UI.md`). DOK-OPS-020 Folio printable report is implemented (`results-ui/report.mjs`, PDF export with provenance). No DOK-OPS work item remains unstarted.
