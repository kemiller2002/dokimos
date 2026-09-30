# Current State

Updated: 2026-09-30

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

Dokimos is operational. PR #5 merged to `main` as `2923d18`; every DOK-OPS work
item is complete except DOK-OPS-026 (external). Run-by-run evidence is in
[`docs/operations/OPERATIONAL-EVIDENCE.md`](../docs/operations/OPERATIONAL-EVIDENCE.md).

- Self-CI on `main` runs the full loop and persists to branch `dokimos-evidence`; the first schema-2.0.0 baseline was accepted by dispatch (DOK-OPS-003).
- Release `dokimos-v0.1.0` published from `main` (DOK-OPS-009): .NET tool `EchelonFoundry.Dokimos.Cli`, native archives, checksums, release manifest, package validation.
- Registered in kemiller2002/echelon-registry (DOK-OPS-012/027).
- Second-repository proof in kemiller2002/aegis (DOK-OPS-010/025): pinned reusable action and released package; PR gate, `main` persistence, accepted baseline and history retrieval all shown by real runs.
- Results UI (Forma) and printable report (Folio) render from the `dokimos.results` contract (DOK-OPS-019/020). CLI faults go through Aegis (DOK-OPS-021).

Open, external or owner decisions:

- DOK-OPS-026: Conditor lifecycle distribution kind for .NET-tool / GitHub-release components (kemiller2002/conditor).
- nuget.org publication needs Trusted Publishing (`DOKIMOS_NUGET_PUBLISH`, `DOKIMOS_NUGET_USER`, environment `nuget`).
- Licensed MIT (`LICENSE`, package `PackageLicenseExpression`). Later versions can be relicensed by the copyright holder; released versions stay MIT.

## Resume point

1. DOK-OPS-026 when Conditor adds the distribution kind.
2. Next release: dispatch Release from `main` (or push a `dokimos-v*` tag), then bump consumers' pinned version, action ref and package SHA-256.
