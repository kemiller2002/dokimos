# Dokimos results UI (DOK-OPS-019)

`results-ui/render.mjs` renders the `dokimos.results` 1.0.0 contract
(`dokimos results`, `schemas/dokimos-results.schema.json`) into seven static,
zero-JavaScript pages: Overview, Changes, Hotspots, Findings, Trends, Files and
metrics (drill-down), and Provenance.

```bash
dokimos results --baseline baseline.json --current snapshot.json --policy policy.json --store <store> > results.json
npm run results:render -- results.json out/
npm run results:audit -- out/
```

Dokimos CI renders it from every run's evidence and uploads it as the
`dokimos-results-ui-*` artifact. Site validation renders and audits the
committed fixtures, which are real CLI output.

## Boundaries

- **No quality logic in JavaScript.** The renderer only formats. Dispositions,
  outcome states, lifecycle states, best states, baseline distances and
  unavailable explanations all come from the contract. Sparkline geometry is
  presentation only, and every chart is followed by a table of the same
  points.
- **Forma 0.3.0** is pinned through its immutable release tarball
  (`package.json`, integrity in `package-lock.json`) and copied into the output,
  never vendored. The patterns used are `navigation-shell`, `container`,
  `data-grid`, `metric-card`, `status-lozenge`, `facts`, `provenance-trail`,
  `empty-state` and `skip-link`. `results-ui/results.css` holds only the
  sparkline, card grid, page gutter and identifier wrapping, using Forma
  tokens.
- Unsupported `dokimos.results` versions are rejected before rendering.

## Visual Engineering report

- **Context:** Visual Engineering 1.0.0, source commit
  `0be73c83d8c257794d53cb9ddc9a6db69f843fbf`. `visual-engineering verify`
  passed.
- **Principles applied:**
  - The primary path goes disposition, then the outcomes that did not pass,
    then evidence counts. The failing reasons come before the cards.
  - State is never carried by colour alone. Every status lozenge has a text
    label and a symbol from Forma.
  - Recognition over recall: persistent navigation, and the current page is
    marked.
  - Every chart has a textual equivalent.
  - Missing data is explicit: "unavailable" or "failed" with a reason, never
    zero, and gaps in sparklines.
  - Native HTML (`table`, `details`, `nav`, `dl`) is used throughout. There
    are no custom components.
  - Responsive recomposition comes from Forma's navigation shell and data grid
    at 48rem and 320px.
- **Verification** (`results-ui/audit.mjs`, both fixtures, all seven pages):
  - axe-core WCAG 2.0/2.1 A and AA: no violations.
  - No horizontal page scrolling at 320, 768, 1280 and 1920px, or at 200%
    text.
  - The first Tab reaches the skip link.
  - Under forced colours, every lozenge keeps a text label.
  - Screenshots at 320 and 1280px were inspected for first-glance hierarchy.
- **Deviations:**
  - Forma has no sparkline pattern, so a local 12-line SVG treatment is used.
    This should be recorded as a Forma gap if other applications need trend
    lines.
  - Forma's gutter lives on the marketing `.ef-site__main`. The application
    page applies the same `--ef-layout-gutter` token locally.
- **Open questions:** interactive filtering and sorting of large file lists
  would need Limen behaviour. It is not implemented, because the static
  tables satisfy the current tasks.

## Printable report (DOK-OPS-020)

`results-ui/report.mjs` renders the same `dokimos.results` contract into one
printable document built from Folio primitives:

- `ef-print-document`, `ef-print-title-page`, `ef-print-section` and
  `ef-print-toc`
- `ef-print-metric` and `ef-print-integrity`
- `ef-print-table`, `ef-print-finding`, `ef-print-note`, `ef-print-footer`
  and `ef-print-page-number`

It uses no separate calculation logic.

Folio has no published release yet. It is therefore pinned the way other
Folio consumers pin it: by exact commit (`github:kemiller2002/folio#8fd8516…`,
package 0.3.0, integrity recorded in `package-lock.json`).

```bash
npm run results:report -- results.json out/
npm run results:pdf -- out/        # Chromium PDF + out/report.export.json
```

**Provenance.** The document carries the repository, revision, ref,
collection time, Dokimos version, configuration, analyzed scope, policy
identity, analyzer runs and unavailable evidence. The PDF export adds
`report.export.json` with the renderer name and version, Folio tier "P2
deterministic Chromium", page count and SHA-256, because the document itself
must not invent export-time facts.

**Integrity.** `ef-print-integrity` status is taken straight from contract
states:

| Contract state | Integrity status |
|---|---|
| All evidence available | `complete` |
| Some evidence unavailable | `partial` |
| Required evidence unavailable | `insufficient` |
| Incompatible snapshots | `not-comparable` |

**Verification.** Both fixtures export as multi-page A4 PDFs (9 and 15
pages) in the site gate. The rendered pages were inspected.
