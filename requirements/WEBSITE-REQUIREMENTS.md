# Dokimos Website Requirements

Status: accepted for implementation (work item `DOK-WEB-001`)
Recorded: 2026-09-28

These requirements govern the public Dokimos website. They extend, and never
override, `REQUIREMENTS.md`. Where this document and the Dokimos domain
disagree, the domain (source code, metric catalog, accepted baseline) wins and
the site must be corrected.

## W0 — Purpose and truthfulness

W0.1 The site SHALL explain what Dokimos is, what it measures, how its evidence
model works, and why longitudinal measurement differs from a one-time
static-analysis report.

W0.2 The site SHALL NOT describe planned measurements as implemented. Every
measurement shown SHALL be classified as implemented, experimental, or planned
using the criteria published on `/metrics/`.

W0.3 Demonstration data SHALL be visibly labelled as demonstration data on every
surface that renders it. Real Dokimos evidence (the accepted self-analysis
baseline) SHALL be labelled with its repository, revision, and collection time.

W0.4 Unknown is not good. Missing, unavailable, or failed evidence SHALL render
as its own state and SHALL NOT be converted into a passing or numeric value.

W0.5 Domain vocabulary SHALL match the implementation: finding states
(`Introduced`, `Persistent`, `Improved`, `Resolved`, `Regressed`, `Resurfaced`),
dispositions (`ObserveOnly`, `Warn`, `Fail`), gate results (`Pass`, `Warning`,
`Failure`, `NotEvaluated`), and measurement states (`Available`, `Unavailable`,
`Failed`).

## W1 — Positioning and boundaries

W1.1 The central idea — code quality is a trajectory, not a snapshot — SHALL be
prominent on the homepage.

W1.2 The site SHALL distinguish raw observation, derived assessment, and policy
judgment, and SHALL never present them as one concept.

W1.3 The site SHALL state the Echelon boundaries recorded in `REQUIREMENTS.md`
R8/R13: Ordo (state methodology), ROS/Praxis (work and process telemetry),
Tutela (security authority), Aegis (unexpected operational faults), Forma
(results UI), Folio (printable reports). Dokimos SHALL NOT be presented as a
linter, a security scanner, or Praxis.

## W2 — Content

W2.1 Pages: `/`, `/how-it-works/`, `/quality-model/`, `/metrics/`,
`/architecture/`. No empty placeholder pages.

W2.2 The homepage SHALL progressively answer: what Dokimos is; the problem; what
it inspects; why history matters; what evidence it retains; measurement versus
judgment; how to inspect a finding; the Echelon boundary; how to start.

W2.3 The site SHALL explain ratcheting against the best demonstrated state,
finding lifecycle, and hotspots (structural complexity × change frequency ×
repeated modification, with region evidence stronger than file churn).

W2.4 The metrics page SHALL be generated from `config/metric-catalog.json` so the
site cannot drift from the catalog; a catalog metric without a site status, or a
status for an unknown metric, SHALL fail the build.

## W3 — Visualisation

W3.1 Charts SHALL show trajectories (history, baseline, best state, thresholds,
current) rather than isolated numbers, and every chart SHALL answer a stated
question.

W3.2 Every chart SHALL have a textual equivalent (summary sentence and data
table). State SHALL never be conveyed by colour alone.

## W4 — Architecture and build

W4.1 Static output only: no server runtime, database, SSR, or client framework.
JavaScript only where behaviour requires it; primary content SHALL render with
JavaScript disabled.

W4.2 `npm run build` SHALL produce the complete site in `dist/` from a clean
checkout. The build SHALL be deterministic except for documented build metadata
(source revision and its commit year) and SHALL fail loudly on invalid data.

W4.3 `npm run serve` SHALL build and preview locally.

W4.4 `CNAME` (`dokimos.echelonfoundry.com`) SHALL be copied into `dist/CNAME`.

W4.5 Clean URLs (`/metrics/`) SHALL work at the custom domain, on nested direct
loads, and after refresh; no GitHub Pages project base path may leak into URLs.

## W5 — Quality gates and deployment

W5.1 Pull requests SHALL validate (ROS validation, Visual Engineering
verification, site tests, build, accessibility, internal links, data) and SHALL
NOT deploy.

W5.2 Pushes to `main` and authorised `workflow_dispatch` SHALL rebuild from a
clean checkout, run the same gate, upload `dist/` as the Pages artifact, and
deploy with GitHub's Pages actions using the `github-pages` environment,
`pages` concurrency, and `contents: read / pages: write / id-token: write`.

W5.3 A failed gate SHALL prevent deployment. Deployment SHALL NOT mutate source.

W5.4 Optional Echelon systems being absent SHALL NOT break the site build.

## W6 — Accessibility, responsiveness, performance, metadata

W6.1 Semantic HTML, one `h1` per page with no skipped heading levels, skip link,
keyboard navigation, visible focus, WCAG AA contrast, meaningful link text,
reduced-motion and forced-colour support, usable at 200% zoom.

W6.2 Layouts SHALL recompose (not merely shrink) across small mobile, large
mobile, tablet, desktop, and wide desktop. Horizontal scrolling SHALL be
confined to components that need it (tables, code).

W6.3 No third-party scripts, no autoplay media, no oversized assets.

W6.4 Every page SHALL have a unique title, description, canonical URL, Open
Graph and Twitter metadata. The build SHALL emit `robots.txt` and `sitemap.xml`.

## W7 — Tests

Automated tests SHALL verify: build success; expected pages; no unresolved
placeholders; `dist/CNAME`; internal links and navigation targets; metadata;
unique titles; demonstration-data schema (including rejection of malformed
data); status not conveyed by colour alone; palette contrast; no source-only
files in `dist/`.

## W8 — External actions not performable from the repository

Recorded in `docs/website/DEPLOYMENT.md`: GitHub Pages source setting, custom
domain setting, DNS `CNAME` record, HTTPS enforcement, and post-deploy public
verification.
