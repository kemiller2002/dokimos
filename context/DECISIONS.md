# Dokimos decisions

Material decisions use `DF-` records under `research/decisions/`. This compact
table is a navigation view, not a replacement for those records.

| Date | Decision | Status | Rationale | Record |
|---|---|---|---|---|
| 2026-09-28 | Use ROS 3.1.4 as a measured greenfield pilot. | provisional | Test portability and operational value on a real beginning project. | Not yet promoted to a `DF-` record |
| 2026-09-28 | Build the public website as a dependency-free static Node build in `site/`, deployed from `dist/` by GitHub Pages Actions. | accepted | Matches the Echelon Foundry build philosophy; reads real evidence from the repository and fails when site claims drift from it. | `requirements/WEBSITE-REQUIREMENTS.md`, `docs/website/SITE.md` |
