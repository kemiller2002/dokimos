// Dokimos printable report (DOK-OPS-020).
//
// Renders the same `dokimos.results` contract as the results UI into one
// printable HTML document built from Folio print primitives (pinned by
// commit). Like the UI, it only formats: no value or judgment is computed
// here. Provenance is carried into the document; renderer name/version and
// output hash belong to whoever exports the PDF.
//
// Usage: node results-ui/report.mjs <results.json> <output-directory>

import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

import { html, toString } from "../site/lib/html.mjs";
import { assertContract, measurementText, shortRevision, date, signed } from "./render.mjs";

const DISPOSITION_LABEL = {
  passed: "Passed",
  "passed-with-warnings": "Passed with warnings",
  failed: "Failed",
  "required-evidence-unavailable": "Required evidence unavailable",
  "incompatible-snapshots": "Incompatible snapshots",
};

/** Folio integrity status, read directly from the contract's own states. */
export const integrityStatus = (results) => {
  switch (results.Overview.Disposition) {
    case "incompatible-snapshots":
      return "not-comparable";
    case "required-evidence-unavailable":
      return "insufficient";
    default:
      return results.Overview.MetricsUnavailable + results.Overview.MetricsFailed === 0 ? "complete" : "partial";
  }
};

const table = (caption, headers, rows) => html`
<ef-print-table>
  <table>
    <caption>${caption}</caption>
    <thead><tr>${headers.map((h) => html`<th scope="col">${h}</th>`)}</tr></thead>
    <tbody>${rows.map((cells) => html`<tr>${cells.map((c) => html`<td>${c}</td>`)}</tr>`)}</tbody>
  </table>
</ef-print-table>`;

const none = (text) => html`<p>${text}</p>`;

const changeRows = (changes) => changes.map((c) => [c.MetricId, c.Scope, measurementText(c.Before), measurementText(c.After), signed(c.Delta)]);

export const report = (results) => {
  const r = assertContract(results);
  const p = r.Provenance;
  const disposition = DISPOSITION_LABEL[r.Overview.Disposition] ?? r.Overview.Disposition;
  const notPassed = r.Outcomes.filter((o) => o.State !== "passed");
  const repositoryTrends = r.Trends.filter((s) => s.Scope === "repository");
  const reportId = `${r.Current.Repository}@${shortRevision(r.Current.Revision)}`;
  return `<!doctype html>
${toString(html`<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Dokimos quality report · ${reportId}</title>
<link rel="stylesheet" href="assets/print.css">
<script type="module" src="assets/register.js"></script>
</head>
<body data-print-mode="color">
<ef-print-document data-document-kind="dokimos-quality-report">
  <ef-print-title-page>
    <div data-report-block>
      <p>Dokimos · Code-quality evidence report</p>
      <h1>${r.Current.Repository}: quality gate ${disposition}</h1>
      <p>Revision ${r.Current.Revision}${r.Current.Ref ? ` on ${r.Current.Ref}` : ""}, collected ${date(r.Current.CollectedAt)}.</p>
      <p>Compared with baseline ${r.Baseline.Revision}, collected ${date(r.Baseline.CollectedAt)}.</p>
      <p>Exit code ${r.Overview.ExitCode}. Report contract ${r.Contract} ${r.SchemaVersion}; Dokimos ${r.DokimosVersion}.</p>
    </div>
  </ef-print-title-page>

  <ef-print-section id="contents">
    <h1>Contents</h1>
    <ef-print-toc>
      <nav aria-label="Report contents">
        <ol>
          <li><a href="#summary">Summary and integrity</a></li>
          <li><a href="#outcomes">Outcomes that did not pass</a></li>
          <li><a href="#changes">Changes against the baseline</a></li>
          <li><a href="#hotspots">Hotspots</a></li>
          <li><a href="#findings">Finding lifecycle</a></li>
          <li><a href="#trends">Repository trends</a></li>
          <li><a href="#provenance">Provenance and unavailable evidence</a></li>
        </ol>
      </nav>
    </ef-print-toc>
  </ef-print-section>

  <ef-print-section id="summary" break-before="page">
    <h1>Summary</h1>
    <ef-print-metric emphasis="strong"><span data-label>Quality gate</span><strong data-value>${disposition}</strong><span data-detail>Exit code ${r.Overview.ExitCode}</span></ef-print-metric>
    <ef-print-metric><span data-label>Outcomes not passed</span><strong data-value>${notPassed.length}</strong><span data-detail>of ${r.Outcomes.length} evaluated</span></ef-print-metric>
    <ef-print-metric><span data-label>Hotspots</span><strong data-value>${r.Overview.Hotspots}</strong><span data-detail>multi-dimension</span></ef-print-metric>
    <ef-print-metric><span data-label>Stored snapshots</span><strong data-value>${r.Overview.StoredSnapshots}</strong><span data-detail>longitudinal history</span></ef-print-metric>
    <ef-print-integrity status="${integrityStatus(r)}" aria-labelledby="integrity-title">
      <h2 id="integrity-title">Evidence integrity</h2>
      <dl>
        <div><dt>Available observations</dt><dd>${r.Overview.MetricsAvailable}</dd></div>
        <div><dt>Unavailable observations</dt><dd>${r.Overview.MetricsUnavailable}</dd></div>
        <div><dt>Failed collections</dt><dd>${r.Overview.MetricsFailed}</dd></div>
        <div><dt>Baseline</dt><dd>${shortRevision(r.Baseline.Revision)}</dd></div>
      </dl>
      <p data-limitations><strong>Unavailable is not zero.</strong> Evidence that was not collected is listed with its reason under Provenance and never counted as a passing or zero value.</p>
    </ef-print-integrity>
    ${table("Comparison with the baseline", ["Change", "Count"], Object.entries(r.Overview.Summary).map(([k, v]) => [k, v]))}
  </ef-print-section>

  <ef-print-section id="outcomes" break-before="page">
    <h1>Outcomes that did not pass (${notPassed.length})</h1>
    ${notPassed.length === 0
      ? none("Every policy rule passed.")
      : table("Policy outcomes that did not pass", ["Rule", "Subject", "Scope", "State", "Explanation"], notPassed.map((o) => [o.Rule, o.Subject, o.Scope, o.State, o.Explanation]))}
  </ef-print-section>

  <ef-print-section id="changes" break-before="page">
    <h1>Changes against the baseline</h1>
    <h2>Regressions (${r.Regressions.length})</h2>
    ${r.Regressions.length === 0 ? none("No directional metric deteriorated.") : table("Regressions", ["Metric", "Scope", "Baseline", "Current", "Delta"], changeRows(r.Regressions))}
    <h2>Improvements (${r.Improvements.length})</h2>
    ${r.Improvements.length === 0 ? none("No directional metric improved.") : table("Improvements", ["Metric", "Scope", "Baseline", "Current", "Delta"], changeRows(r.Improvements))}
  </ef-print-section>

  <ef-print-section id="hotspots" break-before="page">
    <h1>Hotspots (${r.Hotspots.length})</h1>
    ${r.Hotspots.length === 0
      ? none("No file combines the signals any hotspot rule requires.")
      : r.Hotspots.map(
          (h) => html`
    <ef-print-finding compact>
      <h2>${h.Scope}</h2>
      <dl>
        <div><dt>Observation</dt><dd>${h.Signals.map((s) => `${s.Kind} (${s.Dimension})${s.Value === null ? "" : ` = ${s.Value}`}`).join("; ")}</dd></div>
        <div><dt>Implication</dt><dd>${h.Explanation}</dd></div>
        <div><dt>Dimensions</dt><dd>${h.Dimensions.join(", ")}</dd></div>
        <div><dt>Lifecycle</dt><dd>${h.Lifecycle}</dd></div>
      </dl>
    </ef-print-finding>`,
        )}
  </ef-print-section>

  <ef-print-section id="findings" break-before="page">
    <h1>Finding lifecycle</h1>
    ${r.Findings.length === 0
      ? none("No finding has been recorded in the stored evidence.")
      : table("Findings", ["Finding", "Scope", "Current", "First seen", "Last seen"], r.Findings.map((f) => [f.Kind, f.Scope, f.Current, f.FirstSeen ?? "—", f.LastSeen ?? "—"]))}
  </ef-print-section>

  <ef-print-section id="trends" break-before="page">
    <h1>Repository trends</h1>
    ${repositoryTrends.length === 0
      ? none("No repository-scope history is stored yet.")
      : table(
          "Repository metrics across stored snapshots",
          ["Metric", "Latest", "Best demonstrated", "Baseline distance", "Points"],
          repositoryTrends.map((s) => [
            s.MetricId,
            measurementText(s.Latest?.Measurement, s.Unit),
            s.Best ? `${measurementText(s.Best.Measurement, s.Unit)} at ${shortRevision(s.Best.Revision)}` : "not claimed",
            s.BaselineDistance === null ? "unavailable" : signed(s.BaselineDistance),
            s.Points.length,
          ]),
        )}
  </ef-print-section>

  <ef-print-section id="provenance" break-before="page">
    <h1>Provenance</h1>
    <dl>
      <div><dt>Repository</dt><dd>${p.Repository}</dd></div>
      <div><dt>Revision</dt><dd>${p.Revision}${p.Ref ? ` (${p.Ref})` : ""}</dd></div>
      <div><dt>Collected</dt><dd>${date(p.CollectedAt)}</dd></div>
      <div><dt>Dokimos</dt><dd>${p.DokimosVersion ?? "not recorded"}</dd></div>
      <div><dt>Configuration</dt><dd>${p.ConfigurationId ?? "not recorded"}</dd></div>
      <div><dt>Analyzed scope</dt><dd>${p.AnalyzedScope.join(", ") || "not recorded"}</dd></div>
      <div><dt>Policy</dt><dd>schema ${p.Policy.SchemaVersion}, baseline ${p.Policy.Baseline}, ${p.Policy.Identity}</dd></div>
    </dl>
    ${table("Analyzer runs", ["Analyzer", "Version", "State", "Reason"], p.Analyzers.map((a) => [a.AnalyzerId, a.Version, a.State, a.Reason ?? "—"]))}
    <h2>Unavailable evidence (${r.Unavailable.length})</h2>
    ${r.Unavailable.length === 0 ? none("Every catalogued observation was collected.") : table("Unavailable evidence", ["Metric", "Scope", "State", "Explanation"], r.Unavailable.map((u) => [u.Subject, u.Scope, u.State, u.Explanation]))}
    <ef-print-note role="note">Renderer name, version and output hash are export-time provenance recorded by whoever exports this document; they are not invented here.</ef-print-note>
    <ef-print-footer>
      <span slot="left">${reportId}</span>
      <span slot="center">Dokimos quality report</span>
      <span slot="right"><ef-print-page-number format="page-of-pages">Page numbers are supplied by P1/P2 renderers</ef-print-page-number></span>
    </ef-print-footer>
  </ef-print-section>
</ef-print-document>
</body>
</html>`)}
`;
};

export const writeReport = (results, outDir) => {
  const require = createRequire(import.meta.url);
  fs.mkdirSync(path.join(outDir, "assets"), { recursive: true });
  fs.copyFileSync(require.resolve("@echelon-foundry/print-components/print.css"), path.join(outDir, "assets", "print.css"));
  fs.copyFileSync(require.resolve("@echelon-foundry/print-components"), path.join(outDir, "assets", "register.js"));
  fs.writeFileSync(path.join(outDir, "report.html"), report(results));
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [input, outDir] = process.argv.slice(2);
  if (!input || !outDir) {
    console.error("usage: node results-ui/report.mjs <results.json> <output-directory>");
    process.exit(2);
  }
  writeReport(JSON.parse(fs.readFileSync(input, "utf8")), outDir);
}
