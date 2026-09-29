// Dokimos results UI (DOK-OPS-019).
//
// Renders the `dokimos.results` contract into static, zero-JavaScript pages
// built from pinned Forma 0.3.0 patterns. This module only formats: every
// value, state and judgment shown here is read from the contract produced by
// the Dokimos CLI. Nothing is recomputed, reclassified or defaulted to zero.
//
// Usage: node results-ui/render.mjs <results.json> <output-directory>

import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

import { html, raw, toString } from "../site/lib/html.mjs";

export const CONTRACT = "dokimos.results";
export const SUPPORTED_SCHEMAS = ["1.0.0"];

export const PAGES = [
  { file: "index.html", label: "Overview" },
  { file: "changes.html", label: "Changes" },
  { file: "hotspots.html", label: "Hotspots" },
  { file: "findings.html", label: "Findings" },
  { file: "trends.html", label: "Trends" },
  { file: "drilldown.html", label: "Files and metrics" },
  { file: "provenance.html", label: "Provenance" },
];

// --- vocabulary: contract tags -> Forma status lozenge states + words -------

const DISPOSITIONS = {
  passed: { state: "ok", label: "Passed" },
  "passed-with-warnings": { state: "attention", label: "Passed with warnings" },
  failed: { state: "blocked", label: "Failed" },
  "required-evidence-unavailable": { state: "unknown", label: "Required evidence unavailable" },
  "incompatible-snapshots": { state: "unknown", label: "Incompatible snapshots" },
};

const OUTCOME_STATES = {
  passed: { state: "ok", label: "Passed" },
  warning: { state: "attention", label: "Warning" },
  failed: { state: "blocked", label: "Failed" },
  "observe-only": { state: "unknown", label: "Observed only" },
  "not-evaluated": { state: "unknown", label: "Not evaluated" },
  unavailable: { state: "unknown", label: "Unavailable" },
  incompatible: { state: "unknown", label: "Incompatible" },
  "collection-failed": { state: "blocked", label: "Collection failed" },
};

const LIFECYCLE = {
  introduced: { state: "attention", label: "Introduced" },
  persistent: { state: "attention", label: "Persistent" },
  resolved: { state: "ok", label: "Resolved" },
  resurfaced: { state: "blocked", label: "Resurfaced" },
  absent: { state: "ok", label: "Absent" },
  unavailable: { state: "unknown", label: "Unavailable" },
  uncertain: { state: "unknown", label: "Uncertain identity" },
};

const lozenge = (table, tag) => {
  const entry = table[tag] ?? { state: "unknown", label: tag };
  return html`<span class="ef-status-lozenge" data-state="${entry.state}">${entry.label}</span>`;
};

// --- formatting -------------------------------------------------------------

export const shortRevision = (revision) => (/^[0-9a-f]{40}$/.test(revision) ? revision.slice(0, 7) : revision);
export const date = (timestamp) => (timestamp ? timestamp.slice(0, 19).replace("T", " ") + " UTC" : "—");
export const signed = (value) => (value === null || value === undefined ? "—" : value > 0 ? `+${value}` : String(value));

/** A measurement as words: numbers only when the evidence is available. */
export const measurementText = (measurement, unit = "") => {
  if (!measurement) return "absent";
  if (measurement.State === "available") return unit ? `${measurement.Value} ${unit}` : String(measurement.Value);
  const reason = [measurement.Reason, measurement.Detail].filter(Boolean).join(": ");
  return reason ? `${measurement.State} (${reason})` : measurement.State;
};

const slug = (text) => text.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "");
const seriesId = (series) => `series-${slug(`${series.MetricId}-${series.Scope}`)}`;

const dataGrid = (label, headers, rows) => html`
<div class="ef-data-grid" role="region" tabindex="0" aria-label="${label}">
  <table>
    <caption class="ef-visually-hidden">${label}</caption>
    <thead><tr>${headers.map((h) => html`<th scope="col">${h}</th>`)}</tr></thead>
    <tbody>${rows.map((cells) => html`<tr>${cells.map((cell, i) => html`<td data-label="${headers[i]}">${cell}</td>`)}</tr>`)}</tbody>
  </table>
</div>`;

const emptyState = (title, text) => html`
<section class="ef-empty-state" aria-labelledby="${slug(title)}-empty">
  <h3 id="${slug(title)}-empty">${title}</h3>
  <p>${text}</p>
</section>`;

const metricCard = (label, value, context) => html`
<article class="ef-metric-card">
  <div class="ef-metric-card__label">${label}</div>
  <div class="ef-metric-card__value">${value}</div>
  ${context ? html`<div class="ef-metric-card__context">${context}</div>` : ""}
</article>`;

// --- charts: geometry only; the table beside each chart carries the data -----

export const sparkline = (series) => {
  const width = 240;
  const height = 48;
  const pad = 4;
  const values = series.Points.map((p) => (p.Measurement.State === "available" ? p.Measurement.Value : null));
  const present = values.filter((v) => v !== null);
  const id = seriesId(series);
  const summary =
    present.length === 0
      ? `${series.MetricId} was never available in ${series.Points.length} stored snapshots.`
      : `${series.MetricId} across ${series.Points.length} snapshots; latest ${measurementText(series.Latest?.Measurement, series.Unit)}. The table below lists every point.`;
  if (present.length === 0) return html`<p>${summary}</p>`;
  const min = Math.min(...present);
  const max = Math.max(...present);
  const x = (i) => (series.Points.length === 1 ? width / 2 : pad + (i * (width - 2 * pad)) / (series.Points.length - 1));
  const y = (v) => (max === min ? height / 2 : height - pad - ((v - min) * (height - 2 * pad)) / (max - min));
  // Contiguous runs of available points; unavailable evidence is a gap.
  const runs = values.reduce((acc, v, i) => (v === null ? [...acc, []] : [...acc.slice(0, -1), [...acc.at(-1), [x(i), y(v)]]]), [[]]).filter((r) => r.length);
  return html`
<svg class="dokimos-sparkline" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="${id}-chart" focusable="false">
  <title id="${id}-chart">${summary}</title>
  ${runs.map((run) =>
    run.length === 1
      ? html`<circle cx="${run[0][0]}" cy="${run[0][1]}" r="2.5" />`
      : html`<polyline points="${run.map(([a, b]) => `${a.toFixed(1)},${b.toFixed(1)}`).join(" ")}" fill="none" />`,
  )}
</svg>`;
};

// --- pages ------------------------------------------------------------------

const overview = (r) => {
  const d = DISPOSITIONS[r.Overview.Disposition] ?? { label: r.Overview.Disposition };
  const notPassed = r.Outcomes.filter((o) => o.State !== "passed");
  return html`
<header class="dokimos-record-header">
  <p class="ef-eyebrow">${r.Current.Repository} at ${shortRevision(r.Current.Revision)}${r.Current.Ref ? ` (${r.Current.Ref})` : ""}</p>
  <h1>Quality gate: ${d.label}</h1>
  <p>${lozenge(DISPOSITIONS, r.Overview.Disposition)} Exit code ${r.Overview.ExitCode}. Compared with baseline ${shortRevision(r.Baseline.Revision)} collected ${date(r.Baseline.CollectedAt)}.</p>
</header>
<section aria-labelledby="attention-title">
  <h2 id="attention-title">Outcomes that did not pass (${notPassed.length})</h2>
  ${notPassed.length === 0
    ? emptyState("Every rule passed", "No policy rule reported a warning, failure, or missing evidence.")
    : dataGrid(
        "Policy outcomes that did not pass",
        ["Rule", "Subject", "Scope", "State", "Explanation"],
        notPassed.map((o) => [o.Rule, o.Subject, o.Scope, lozenge(OUTCOME_STATES, o.State), o.Explanation]),
      )}
</section>
<section aria-labelledby="evidence-title">
  <h2 id="evidence-title">Evidence in this snapshot</h2>
  <div class="dokimos-cards">
    ${metricCard("Available observations", r.Overview.MetricsAvailable)}
    ${metricCard("Unavailable observations", r.Overview.MetricsUnavailable, html`<a href="provenance.html#unavailable">Why each is unavailable</a>`)}
    ${metricCard("Failed collections", r.Overview.MetricsFailed)}
    ${metricCard("Findings", r.Overview.Findings, html`<a href="findings.html">Finding lifecycle</a>`)}
    ${metricCard("Multi-dimension hotspots", r.Overview.Hotspots, html`<a href="hotspots.html">What makes each a hotspot</a>`)}
    ${metricCard("Stored snapshots", r.Overview.StoredSnapshots, html`<a href="trends.html">Trends</a>`)}
  </div>
</section>
<section aria-labelledby="counts-title">
  <h2 id="counts-title">Outcome and comparison counts</h2>
  ${dataGrid("Outcome counts by state", ["State", "Count"], Object.entries(r.Overview.OutcomeCounts).map(([k, v]) => [lozenge(OUTCOME_STATES, k), v]))}
  ${dataGrid("Comparison with the baseline", ["Change", "Count"], Object.entries(r.Overview.Summary).map(([k, v]) => [k, v]))}
</section>`;
};

const changeRows = (changes) =>
  changes.map((c) => [c.MetricId, c.Scope, measurementText(c.Before), measurementText(c.After), signed(c.Delta)]);

const changes = (r) => html`
<h1>Changes against the baseline</h1>
<p>Only metrics with a direction (lower or higher is better) can improve or deteriorate. Contextual metrics change without a judgment.</p>
<section aria-labelledby="regressions-title">
  <h2 id="regressions-title">Regressions (${r.Regressions.length})</h2>
  ${r.Regressions.length === 0
    ? emptyState("No regressions", "No directional metric deteriorated against the baseline.")
    : dataGrid("Regressions", ["Metric", "Scope", "Baseline", "Current", "Delta"], changeRows(r.Regressions))}
</section>
<section aria-labelledby="improvements-title">
  <h2 id="improvements-title">Improvements (${r.Improvements.length})</h2>
  ${r.Improvements.length === 0
    ? emptyState("No improvements", "No directional metric improved against the baseline.")
    : dataGrid("Improvements", ["Metric", "Scope", "Baseline", "Current", "Delta"], changeRows(r.Improvements))}
</section>`;

const hotspots = (r) => html`
<h1>Hotspots</h1>
<p>A hotspot is identified only when independent kinds of evidence coincide. Each lists the dimensions and signal values that caused it; there is no composite score.</p>
${r.Hotspots.length === 0
  ? emptyState("No hotspots", "No file combines the signals any hotspot rule requires.")
  : r.Hotspots.map(
      (h) => html`
<section class="dokimos-hotspot" aria-labelledby="${slug(h.FindingId)}">
  <h2 id="${slug(h.FindingId)}">${h.Scope}</h2>
  <p>${h.Kind}: ${h.Explanation} ${lozenge(LIFECYCLE, h.Lifecycle)}</p>
  <p>Dimensions: ${h.Dimensions.join(", ")}.</p>
  ${dataGrid(`Signals behind ${h.Scope}`, ["Signal", "Dimension", "Value"], h.Signals.map((s) => [s.Kind, s.Dimension, s.Value === null ? "present (no magnitude)" : s.Value]))}
</section>`,
    )}`;

const findings = (r) => html`
<h1>Findings</h1>
<p>Lifecycle is derived from every stored snapshot. Absence counts as resolution only when the analyzers that report the finding ran; otherwise it is unavailable.</p>
${r.Findings.length === 0
  ? emptyState("No findings", "No finding has been recorded in the stored evidence.")
  : html`${dataGrid(
      "Findings and their current lifecycle state",
      ["Finding", "Scope", "Current", "First seen", "Last seen"],
      r.Findings.map((f) => [f.Kind, f.Scope, lozenge(LIFECYCLE, f.Current), f.FirstSeen ?? "—", f.LastSeen ?? "—"]),
    )}
    ${r.Findings.map(
      (f) => html`
<details class="dokimos-timeline">
  <summary>Timeline: ${f.Kind} at ${f.Scope}</summary>
  <ol>${f.Timeline.map((e) => html`<li>${date(e.CollectedAt)} · ${shortRevision(e.Revision)} · ${lozenge(LIFECYCLE, e.State)}${e.Note ? html` · ${e.Note}` : ""}</li>`)}</ol>
</details>`,
    )}`}`;

const seriesTable = (s) =>
  dataGrid(
    `${s.MetricId} at ${s.Scope}, every stored point`,
    ["Collected", "Revision", "Definition", "Value"],
    s.Points.map((p) => [date(p.CollectedAt), shortRevision(p.Revision), `v${p.MetricVersion}`, measurementText(p.Measurement, s.Unit)]),
  );

const trend = (s) => html`
<section class="dokimos-series" id="${seriesId(s)}" aria-labelledby="${seriesId(s)}-title">
  <h2 id="${seriesId(s)}-title">${s.MetricId} <small>at ${s.Scope}</small></h2>
  ${sparkline(s)}
  <dl class="ef-facts">
    <div class="ef-facts__item"><dt>Direction</dt><dd>${s.Preference}</dd></div>
    <div class="ef-facts__item"><dt>Latest</dt><dd>${measurementText(s.Latest?.Measurement, s.Unit)}</dd></div>
    <div class="ef-facts__item"><dt>Best demonstrated</dt><dd>${s.Best ? `${measurementText(s.Best.Measurement, s.Unit)} at ${shortRevision(s.Best.Revision)}` : "not claimed"}</dd></div>
    <div class="ef-facts__item"><dt>Baseline distance</dt><dd>${s.BaselineDistance === null ? "unavailable" : signed(s.BaselineDistance)}</dd></div>
  </dl>
  ${s.Notes.length ? html`<ul>${s.Notes.map((n) => html`<li>${n}</li>`)}</ul>` : ""}
  ${seriesTable(s)}
</section>`;

const trends = (r) => html`
<h1>Trends</h1>
<p>Repository metrics and the structural and change metrics of every hotspot file, across stored snapshots. Each chart is followed by a table of the same points.</p>
${r.Trends.length === 0 ? emptyState("No trends", "The store holds no comparable history yet.") : r.Trends.map(trend)}`;

const drilldown = (r) => {
  const byScope = r.Trends.reduce((acc, s) => ({ ...acc, [s.Scope]: [...(acc[s.Scope] ?? []), s] }), {});
  const scopes = Object.keys(byScope).sort((a, b) => (a === "repository" ? -1 : b === "repository" ? 1 : a.localeCompare(b)));
  return html`
<h1>Files and metrics</h1>
<p>Every file and metric with stored history. Select a metric for its full series.</p>
${scopes.map(
  (scope) => html`
<section aria-labelledby="scope-${slug(scope)}">
  <h2 id="scope-${slug(scope)}">${scope}</h2>
  ${dataGrid(
    `Metrics at ${scope}`,
    ["Metric", "Latest", "Baseline distance", "History"],
    byScope[scope].map((s) => [
      s.MetricId,
      measurementText(s.Latest?.Measurement, s.Unit),
      s.BaselineDistance === null ? "unavailable" : signed(s.BaselineDistance),
      html`<a href="trends.html#${seriesId(s)}">${s.Points.length} points</a>`,
    ]),
  )}
</section>`,
)}`;
};

const provenance = (r) => {
  const p = r.Provenance;
  return html`
<h1>Provenance</h1>
<section class="ef-provenance-trail" aria-labelledby="trail-title">
  <h2 id="trail-title">How this result was produced</h2>
  <ol>
    <li><span class="ef-provenance-trail__kind">Source</span><strong>${p.Repository}</strong><small>Revision ${p.Revision}${p.Ref ? ` on ${p.Ref}` : ""}</small></li>
    <li><span class="ef-provenance-trail__kind">Collection</span><strong>Dokimos ${p.DokimosVersion ?? "version not recorded"}</strong><small>${date(p.CollectedAt)}; configuration ${p.ConfigurationId ?? "not recorded"}; scope ${p.AnalyzedScope.join(", ") || "not recorded"}</small></li>
    <li><span class="ef-provenance-trail__kind">Policy</span><strong>Schema ${p.Policy.SchemaVersion}, baseline ${p.Policy.Baseline}</strong><small>${p.Policy.Identity}</small></li>
    <li><span class="ef-provenance-trail__kind">Presentation</span><strong>${r.Contract} ${r.SchemaVersion}</strong><small>Rendered by Dokimos results UI with Forma 0.3.0; nothing recomputed</small></li>
  </ol>
</section>
<section aria-labelledby="analyzers-title">
  <h2 id="analyzers-title">Analyzers</h2>
  ${dataGrid(
    "Analyzer runs",
    ["Analyzer", "Version", "State", "Reason", "Duration"],
    p.Analyzers.map((a) => [a.AnalyzerId, a.Version, a.State, a.Reason ?? "—", a.DurationMilliseconds === null ? "not measured separately" : `${a.DurationMilliseconds} ms`]),
  )}
</section>
<section aria-labelledby="performance-title">
  <h2 id="performance-title">Collection performance</h2>
  ${p.Performance
    ? html`<dl class="ef-facts">
    <div class="ef-facts__item"><dt>Total</dt><dd>${p.Performance.TotalMilliseconds} ms</dd></div>
    <div class="ef-facts__item"><dt>Files</dt><dd>${p.Performance.FileCount}</dd></div>
    <div class="ef-facts__item"><dt>Observations</dt><dd>${p.Performance.ObservationCount}</dd></div>
    <div class="ef-facts__item"><dt>Collectors not run</dt><dd>${p.Performance.UnavailableCollectors.join(", ") || "none"}</dd></div>
    <div class="ef-facts__item"><dt>Collectors failed</dt><dd>${p.Performance.FailedCollectors.join(", ") || "none"}</dd></div>
  </dl>`
    : html`<p>Not recorded for this snapshot.</p>`}
</section>
<section id="unavailable" aria-labelledby="unavailable-title">
  <h2 id="unavailable-title">Unavailable evidence (${r.Unavailable.length})</h2>
  <p>Unavailable and failed evidence is never shown as zero.</p>
  ${r.Unavailable.length === 0
    ? emptyState("All evidence available", "Every catalogued observation was collected.")
    : dataGrid("Unavailable evidence", ["Metric", "Scope", "State", "Explanation"], r.Unavailable.map((u) => [u.Subject, u.Scope, u.State, u.Explanation]))}
</section>`;
};

const BODIES = { "index.html": overview, "changes.html": changes, "hotspots.html": hotspots, "findings.html": findings, "trends.html": trends, "drilldown.html": drilldown, "provenance.html": provenance };

const layout = (page, body, results) => {
  const nav = html`${PAGES.map((p) => html`<a href="${p.file}"${p.file === page.file ? raw(' aria-current="page"') : ""}>${p.label}</a>`)}`;
  return `<!doctype html>
${toString(html`<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${page.label} · Dokimos results · ${results.Current.Repository}</title>
<link rel="stylesheet" href="assets/forma.css">
<link rel="stylesheet" href="assets/results.css">
</head>
<body>
<a class="ef-skip-link" href="#content">Skip to content</a>
<div class="ef-container dokimos-page" data-ef-width="wide">
<div class="ef-navigation-shell">
  <details class="ef-navigation-shell__compact">
    <summary>Navigation</summary>
    <nav aria-label="Results">${nav}</nav>
  </details>
  <nav class="ef-navigation-shell__persistent" aria-label="Results">${nav}</nav>
  <main class="ef-navigation-shell__content" id="content">
    ${body}
    <footer class="dokimos-footer"><p>${results.Contract} ${results.SchemaVersion} · Dokimos ${results.DokimosVersion}</p></footer>
  </main>
</div>
</div>
</body>
</html>`)}
`;
};

/** Validates the contract identity before rendering anything. */
export const assertContract = (results) => {
  if (results?.Contract !== CONTRACT) throw new Error(`expected Contract ${CONTRACT}, found ${results?.Contract}`);
  if (!SUPPORTED_SCHEMAS.includes(results.SchemaVersion)) throw new Error(`unsupported ${CONTRACT} schema ${results.SchemaVersion}`);
  return results;
};

/** Pure: results contract -> [{ file, html }]. */
export const render = (results) =>
  PAGES.map((page) => ({ file: page.file, html: layout(page, BODIES[page.file](assertContract(results)), results) }));

const here = path.dirname(fileURLToPath(import.meta.url));

export const writeSite = (results, outDir) => {
  const require = createRequire(import.meta.url);
  const forma = require.resolve("@echelon-foundry/design-system/all.css");
  fs.mkdirSync(path.join(outDir, "assets"), { recursive: true });
  fs.copyFileSync(forma, path.join(outDir, "assets", "forma.css"));
  fs.copyFileSync(path.join(here, "results.css"), path.join(outDir, "assets", "results.css"));
  render(results).forEach((page) => fs.writeFileSync(path.join(outDir, page.file), page.html));
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [input, outDir] = process.argv.slice(2);
  if (!input || !outDir) {
    console.error("usage: node results-ui/render.mjs <results.json> <output-directory>");
    process.exit(2);
  }
  writeSite(JSON.parse(fs.readFileSync(input, "utf8")), outDir);
}
