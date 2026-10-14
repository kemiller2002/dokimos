// Declared Dokimos UI surfaces, alignment groups and owned exceptions for
// visual-engineering/UI-LAYOUT-INTERACTION@1.0.0. Peers are declared here,
// never guessed from CSS coincidence.

export const WIDTHS = [320, 390, 768, 1280];

const SITE_PAGES = [
  ["site-home", "/"],
  ["site-how-it-works", "/how-it-works/"],
  ["site-quality-model", "/quality-model/"],
  ["site-metrics", "/metrics/"],
  ["site-architecture", "/architecture/"],
  ["site-not-found", "/404.html"],
];

const RESULTS_PAGES = ["index", "changes", "hotspots", "findings", "trends", "drilldown", "provenance"];

const primaryNavigation = ["How it works", "Quality model", "Metrics", "Architecture", "Source code on GitHub"].map((name) => ({ role: "link", name, nav: "Primary" }));

export const surfaces = [
  ...SITE_PAGES.map(([id, path]) => ({
    id,
    origin: "UI_SITE_ORIGIN",
    path,
    kind: id === "site-home" ? "marketing" : "other",
    groups: [
      { ruleId: "ULI-L10", id: "primary-navigation", edge: "center", widths: [1280], rationale: "the header's navigation is one row at desktop width", peers: primaryNavigation },
      ...(id === "site-home"
        ? [
            {
              ruleId: "ULI-L10",
              id: "hero-actions",
              edge: "top",
              widths: [768, 1280],
              rationale: "the hero's two calls to action share one row where they fit side by side",
              peers: [
                { role: "link", name: "Explore what Dokimos measures" },
                { role: "link", name: "View the project on GitHub" },
              ],
            },
          ]
        : []),
    ],
  })),
  ...RESULTS_PAGES.map((name) => ({
    id: `results-${name}`,
    origin: "UI_RESULTS_ORIGIN",
    path: `/${name}.html`,
    kind: "product",
    groups: [],
  })),
  { id: "results-report", origin: "UI_RESULTS_ORIGIN", path: "/report.html", kind: "print", groups: [], print: true },
];

const FORMA = "Forma (kemiller2002/forma); the results UI renders the pinned Forma 0.3.0 release";
const FOLIO = "Folio (kemiller2002/folio); the report renders @echelon-foundry/print-components 0.3.0";
const ECHELON = "Echelon Foundry (kemiller2002/echelon-foundry); site/assets/css/echelon-foundry.css is its unmodified, hash-pinned stylesheet";
const SITE = SITE_PAGES.map(([id]) => id);
const RESULTS = RESULTS_PAGES.map((name) => `results-${name}`);

// Owned, bounded deviations: each covers one rule, named surfaces and one
// check (optionally only some widths, or only a described kind of escape),
// with the reason, owner and the trigger that ends it. A covered FAIL is
// reported as "exception", never as "pass"; any other FAIL fails the run.
export const exceptions = [
  {
    ruleId: "ULI-L06",
    surfaces: RESULTS,
    check: "word integrity in tables",
    reason:
      "Forma sets `:where(td, th) { overflow-wrap: anywhere }`, so the automatic table layout squeezes data-grid columns until short words split across lines (\"require|d-eviden|ce\", \"reposit|ory\"). Data-grid wrapping is Forma's primitive (ULI-L03).",
    owner: FORMA,
    reviewTrigger: "the next Forma release the results UI pins: re-run ui:conformance and remove this exception when the check passes",
  },
  {
    ruleId: "ULI-L05",
    surfaces: RESULTS,
    check: "page overflow at 200% text",
    widths: [390, 768],
    reason:
      "At 200% text Forma's stacked data grid keeps its label track at `minmax(6.5rem, 0.4fr)`, leaving status lozenges no room, so they escape the viewport. The stacked data-grid composition is Forma's primitive (ULI-L03/L09).",
    owner: FORMA,
    reviewTrigger: "the next Forma release the results UI pins: re-run ui:conformance and remove this exception when the check passes",
  },
  {
    ruleId: "ULI-L05",
    surfaces: SITE,
    check: "page overflow at 200% text",
    widths: [390, 768],
    reason:
      "At 200% text the site header, navigation and section headings, styled by the shared Echelon Foundry stylesheet with px breakpoints and fixed `minmax(14rem, …)` tracks, do not recompose, so the page scrolls horizontally. Dokimos's own components now use em breakpoints and reflow; the shared stylesheet is hash-pinned and may not be patched here.",
    owner: ECHELON,
    reviewTrigger: "the next Echelon Foundry stylesheet release: re-pin it, re-run ui:conformance and remove this exception when the check passes",
  },
  {
    ruleId: "ULI-L05",
    surfaces: ["site-home"],
    check: "page overflow",
    widths: [320, 390],
    escapes: (escape) => escape.startsWith('text "'),
    reason:
      "False positive in the forma-conformance overflow helper: chart SVG labels sit inside the trajectory and hotspot charts' labelled, focusable `role=region` scroller (the contract's permitted contained scroller; the page itself does not scroll). The helper stops at the SVG's own `overflow: hidden` and never reaches that scroller, so it reports the labels as escaping.",
    owner: "Forma (kemiller2002/forma): @echelon-foundry/forma-conformance measureOverflow",
    reviewTrigger: "the next forma-conformance release: re-vendor it, re-run ui:conformance and remove this exception when the check passes",
  },
  {
    ruleId: "ULI-L05",
    surfaces: ["results-report"],
    check: "page overflow",
    widths: [320, 390],
    reason:
      "On a phone-width screen preview Folio's print stylesheet wraps tables in a contained scroller but leaves long unbreakable identifiers (sha256 digests, build versions) in the title page and integrity list unwrapped, so the preview scrolls horizontally. Printed A4 output and the PDF have no overflow (measured).",
    owner: FOLIO,
    reviewTrigger: "the next print-components release Dokimos pins: re-run ui:conformance and remove this exception when the check passes",
  },
  {
    ruleId: "ULI-L09",
    surfaces: ["results-report"],
    check: "inline gutter",
    reason:
      "Folio's `ef-print-document` gives the screen preview no inline padding (page margins exist only in print), so report text runs to both viewport edges at every screen width. Printed A4 output and the PDF keep their page margins.",
    owner: FOLIO,
    reviewTrigger: "the next print-components release Dokimos pins: re-run ui:conformance and remove this exception when the check passes",
  },
  {
    ruleId: "ULI-L06",
    surfaces: ["results-report"],
    check: "word integrity in tables",
    reason:
      "Folio's screen preview of `ef-print-table` lets the automatic table layout split short words across lines (\"reposit|ory\", \"unavail|able\"); table cell wrapping is Folio's print primitive.",
    owner: FOLIO,
    reviewTrigger: "the next print-components release Dokimos pins: re-run ui:conformance and remove this exception when the check passes",
  },
];

export const covering = (surfaceId, check, report, width) =>
  exceptions.find(
    (exception) =>
      exception.ruleId === report.ruleId &&
      exception.surfaces.includes(surfaceId) &&
      exception.check === check &&
      (!exception.widths || exception.widths.includes(width)) &&
      (!exception.escapes || ((report.escapes ?? []).length > 0 && report.escapes.every(exception.escapes))),
  );
