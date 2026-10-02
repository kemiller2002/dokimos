# Current State

Updated: 2026-10-02

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

Standard Echelon distribution (DOK-OPS-026 … 029) is proven:

- `dokimos-v0.2.0` published from `main` commit `e641048` with six self-contained native archives and a shared-contract `echelon.release/v2` manifest; it declares Registry's generic `echelon.repository-lifecycle` v1 contract.
- Cataloged in Echelon Registry (kemiller2002/echelon-registry#21) with the `dokimos-proof` 0.1.0 profile.
- Conditor's generic Registry lifecycle path (kemiller2002/conditor#23) installed, initialized, verified and re-applied Dokimos 0.2.0 on a clean linux-x64 host with zero drift (Actions run 37003356848). Conditor contains no Dokimos-specific code.

Open, external or owner decisions:

- Stable-profile membership: Dokimos is selected only by `dokimos-proof`; adding it to an `echelon-engineering` successor version is an owner decision. The frozen Indy Init 2026 profile is not modified.
- A macOS clean-host proof is not yet run.
- Provenance contract adoption is separate work (issue #12).
- nuget.org publication needs Trusted Publishing (`DOKIMOS_NUGET_PUBLISH`, `DOKIMOS_NUGET_USER`, environment `nuget`).
- Licensed MIT (`LICENSE`, package `PackageLicenseExpression`). Later versions can be relicensed by the copyright holder; released versions stay MIT.

## Resume point

Issue #10 is closed: echelon-registry#21 and conditor#23 are merged, and the proof's pinned commits are on their `main` branches.

1. Decide stable-profile membership for Dokimos.
2. Bump consumers' pinned version, action ref and package SHA-256 to 0.2.0 where wanted.
