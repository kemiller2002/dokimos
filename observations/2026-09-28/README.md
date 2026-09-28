# Observations — 2026-09-28 — public website (DOK-WEB-001)

Recorded while implementing the Dokimos website. These are observations about
repository state and neighbouring evidence, not quality measurements.

## Governance state found before work began

- `./ros status` reported ROS 3.1.4 as `not-installed` and `./ros validate`
  failed with 9 errors (8 stale/missing generated registries, missing
  `telemetry/metrics.json`). `ros init` (ROS's own idempotent installer at the
  pinned 3.1.4) was run; it preserved all 46 existing files, added the missing
  managed artifacts, and validation passed. Work was then attributed to
  `DOK-WEB-001` through `ros work capture/ready/start`.
- `ros` lacked the executable bit, so the generated `ros-validation.yml`
  (`./ros validate`) would have failed. The bit is now set in Git.
- `ros.json` still names the repository `echelon-design-system`; it was copied
  from another project. Left unchanged (outside this work item); worth fixing
  under a mechanical work item.

## Evidence drift discovered by the site build

- The accepted baseline snapshot emits `quality.type-weakening-indicators` and
  `quality.scaffolding-indicators`, but neither is defined in
  `config/metric-catalog.json`. The site shows them as implemented and
  "not in the metric catalog" rather than hiding the gap. Next step: publish
  catalog definitions (a Dokimos change, not a site change).
- The catalog marks `complexity.proxy-cyclomatic` as `contextual`, and
  `CanonicalComparison` reports its changes as `MetricChanged` without a
  direction. The site's demonstration therefore declares "lower is better" as
  a ratchet policy, not as a property of the metric.

## Boundary wording conflict

- `AGENTS.md` says "Aegis/Tutors owns security-specific authority", while
  `REQUIREMENTS.md` R8 says security routes to **Tutela** and "Aegis is not the
  security authority; it owns unexpected operational-fault handling" (also R13).
  The site follows the requirements (more specific and later). `AGENTS.md` was
  not edited because it is a shared ROS-managed file; the conflict should be
  resolved by its owner.

## Echelon tooling availability

- `@echelon-foundry/visual-engineering` 1.0.0 is installed and verified.
- `@echelon-foundry/communication-engineering` exists on npm only as 0.1.0 with
  no CLI. Echelon Foundry's `package.json` pins `1.0.0`, which is not
  published, and its bootstrap also calls `limen`, which is not on npm. Those
  pins were treated as evidence of a pattern, not as versions to copy.
- The Echelon Foundry bootstrap installs Praxis/Ordo through a native
  `echelon` toolchain; Dokimos pins ROS 3.1.4 and Ordo/SDE 1.3.0 directly, and
  those pins were used.

## Deployment state

- GitHub Pages source, custom domain, DNS, and HTTPS are repository/DNS
  settings this work could not change. They are listed with verification steps
  in `docs/website/DEPLOYMENT.md`. No public-site verification has been
  performed yet because the site has not been deployed.
