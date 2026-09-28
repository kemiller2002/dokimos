# Dokimos website

The public site for Dokimos, served at `https://dokimos.echelonfoundry.com`.
Requirements: [`requirements/WEBSITE-REQUIREMENTS.md`](../../requirements/WEBSITE-REQUIREMENTS.md).
Deployment and external settings: [`DEPLOYMENT.md`](DEPLOYMENT.md).

## Commands

| Command | Purpose |
| --- | --- |
| `npm ci` | Install the pinned site toolchain (Node 22+). |
| `npm run build` | Validate all sources and write the complete site to `dist/`. |
| `npm run serve` | Build, then preview at `http://localhost:4000/` with GitHub Pages URL semantics. |
| `npm test` | Unit and output tests: data schemas, derivations, links, metadata, headings, contrast, determinism. |
| `npm run audit` | Browser audit of `dist/`: axe-core WCAG 2.1 A/AA + best practice, five viewports, 200% zoom, no-JavaScript rendering, forced colours, keyboard skip link, 404 handling. Needs Chromium (`npx playwright install chromium`). |
| `npm run echelon:visual:verify` | Visual Engineering installation verification (`init` first on a fresh clone). |
| `npm run ros:validate` | ROS repository validation. |

The F# system is unaffected: it still builds with `dotnet` and is validated by `.github/workflows/ci.yml`.

## Architecture

```
config/metric-catalog.json ─┐
config/dokimos-policy.json ─┤
baselines/accepted-snapshot.json ─┤   real Dokimos evidence
site/data/measurement-status.json ─┤   the site's claims about implementation status
site/data/demonstration.json ─┤   labelled demonstration history
site/data/site.json ─┘
            │
            ▼
   site/build.mjs         the only module with filesystem/process effects
     ├─ validate          lib/evidence.mjs, lib/demo.mjs — pure; any diagnostic fails the build
     ├─ derive            lib/demo.mjs mirrors Policy.fs, Trend.fs, Findings.fs
     └─ render            lib/layout.mjs, lib/charts.mjs, pages/*.mjs — pure functions to HTML
            │
            ▼
          dist/           static HTML, two CSS files, two images, CNAME, robots.txt, sitemap.xml
```

- No framework and no client-side JavaScript. The only `<script>` is JSON-LD metadata.
- Charts are SVG rendered at build time. Each has a title, a description, a caption, and a data table.
- Pages are plain modules exporting `page` (path, title, description) and `render(context)`.
- Clean URLs: `/metrics/` is written as `dist/metrics/index.html`. All internal URLs are root-relative, so nothing depends on a GitHub Pages project base path.

## Truthfulness rules enforced by the build

- Every metric in `config/metric-catalog.json` must have a site status; every status must name a known metric.
- A measurement marked *implemented* must appear in `baselines/accepted-snapshot.json` (as a metric or a finding), or the build fails.
- *Planned* measurements may not cite an implementation source; others must.
- Demonstration data must declare `"kind": "demonstration"`, carry a disclaimer, use catalogued metric IDs, and keep unavailable/failed observations free of values (unknown is not zero). The hotspot view and trajectory must describe the same system.
- `CNAME` must match the configured origin.

## Determinism

For a given source tree the output is byte-for-byte identical (tested by building twice). The only build metadata is:

| Value | Source | Override |
| --- | --- | --- |
| Source revision (footer link) | `git rev-parse HEAD` | `DOKIMOS_SITE_REVISION` |
| Copyright year | commit date of that revision | `DOKIMOS_SITE_YEAR` |

Wall-clock time is never used. The stylesheet URL carries a content hash for cache busting.

## Assets

- `site/assets/images/favicon.svg`: the Dokimos mark (a trajectory above a dashed best-state line).
- `site/assets/images/social-card.png`: 1200×630 Open Graph image, rendered once with Chromium from a static HTML composition and committed. It shows illustrative data and says so.

## Visual Engineering report

- Context: `@echelon-foundry/visual-engineering` 1.0.0 (context 1.0.0), installed via `npx visual-engineering init`.
- Principles applied: one primary path per page; emphasis proportional to consequence (ratchet gap and current value carry the strongest marks); grouping by meaning (observation / derived / policy); status never by colour alone (word + glyph + border style); native HTML (`details`, tables, lists) instead of widgets; recomposition rather than shrinking at narrow widths; local horizontal scrolling only for tables and charts.
- Verification performed: `npm run audit` (axe WCAG 2.1 AA, keyboard skip link, 320/414/768/1280/1920 px, 200% zoom, forced colours with reduced motion, JavaScript disabled) and `site/test/contrast.test.mjs` (4.5:1 text, 3:1 functional borders, read from the stylesheet).
- Deviation: charts keep a 600 px minimum width inside a labelled, focusable scroll region on small screens. Shrinking them further made axis text unreadable; every chart's data table remains fully readable without scrolling the chart.

## Communication Engineering

`@echelon-foundry/communication-engineering` is published only as 0.1.0, which ships research documents and no verification command. The copy follows its *Writing and Style Guide for Agents* (lead with the point, concrete claims, explicit uncertainty), but there is no automated Communication Engineering gate to run. Add one to `.github/actions/site-gate/action.yml` when a CLI is released.

## Stylesheets

| File | Owner | Contents |
| --- | --- | --- |
| `site/assets/css/echelon-foundry.css` | Echelon Foundry | `assets/css/style.css` from `kemiller2002/echelon-foundry` at `cb638dc`, copied **verbatim**: palette, typography, the `body::before` background grid, header, navigation, buttons, section headings, page intros, footer, reduced-motion and forced-colour rules. SHA-256 `a3627edd9932077e9ab6798d1f4a954fc809541af6cdb1846987fc8073509375`. |
| `site/assets/css/dokimos.css` | Dokimos | Only the evidence components Echelon Foundry lacks (charts, status badges, data tables, readouts, lifecycle, drill-down, provenance labels), built on the `--ef-*` tokens plus a few `--dk-*` additions. |

The header and footer use Echelon Foundry's markup (`.site-header`, `.nav-shell`, `.brand`, `.pill-link`, `.site-footer`, `.footer-grid`, `.footer-note`) so the shared stylesheet renders them exactly as on echelonfoundry.com.

`site/test/stylesheet.test.mjs` fails if the vendored file changes or if `dokimos.css` restyles something Echelon Foundry owns (body, background, header, footer, buttons, headings, `--ef-*` tokens). To take a newer Echelon Foundry stylesheet, copy it over, update the checksum in that test and in this table, and run `npm test && npm run build && npm run audit`.
