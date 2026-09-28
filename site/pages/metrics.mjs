import { html } from "../lib/html.mjs";
import { pageIntro, sectionHeading, statusBadge, realLabel } from "../lib/components.mjs";

export const page = {
  path: "/metrics/",
  title: "Dokimos metrics: implemented, experimental, planned",
  description:
    "Every Dokimos measurement with its definition, unit, scope, version, and limitations — generated from the metric catalog and checked against Dokimos's own accepted baseline.",
};

const STATUS_ORDER = ["implemented", "experimental", "planned"];
const STATUS_TITLES = {
  implemented: "Implemented",
  experimental: "Experimental",
  planned: "Planned",
};

const sourceLink = (site, source) =>
  source === null
    ? html`<span>—</span>`
    : html`<a class="nowrap" href="${site.repository}/${source.endsWith("/") ? "tree" : "blob"}/main/${source}"><code>${source.split("/").filter(Boolean).at(-1)}${source.endsWith("/") ? "/" : ""}</code><span class="visually-hidden"> (${source})</span></a>`;

const metricRow = (site, row) => html`<tr>
  <th scope="row"><code class="metric-id">${row.id}</code>${row.catalogued ? "" : html`<br><span class="filter-note">Not in the metric catalog</span>`}</th>
  <td>${row.definition ?? row.note}${row.definition && row.note ? html`<br><span class="filter-note">${row.note}</span>` : ""}</td>
  <td>${row.class === "derived" ? "Derived" : "Observation"}</td>
  <td>${row.version ? `v${row.version}` : "—"}</td>
  <td>${row.unit ?? "—"}</td>
  <td>${row.scope ? row.scope.join(", ") : "—"}</td>
  <td>${row.preference ?? "—"}</td>
  <td>${sourceLink(site, row.source)}</td>
</tr>`;

const statusTable = (site, status, rows, criteria) => html`
<section aria-labelledby="status-${status}">
  ${sectionHeading({ id: `status-${status}`, eyebrow: `${rows.length} measurements`, title: STATUS_TITLES[status], note: criteria })}
  <div class="table-scroll" role="region" aria-labelledby="caption-${status}" tabindex="0">
    <table class="data-table metric-table">
      <caption id="caption-${status}">${statusBadge(status)} measurements</caption>
      <thead><tr><th scope="col">Metric</th><th scope="col">Definition and limitations</th><th scope="col">Kind</th><th scope="col">Version</th><th scope="col">Unit</th><th scope="col">Scope</th><th scope="col">Direction</th><th scope="col">Source</th></tr></thead>
      <tbody>${rows.map((row) => metricRow(site, row))}</tbody>
    </table>
  </div>
</section>`;

export const render = ({ site, catalogRows, statusCriteria, snapshot }) => {
  const byStatus = (status) => catalogRows.filter((row) => row.status === status);
  return html`
${pageIntro({
  eyebrow: "Metrics",
  title: "What Dokimos measures today, and what it does not yet.",
  lead: "Each measurement publishes a definition, unit, scope, and version. This page is generated from the metric catalog in the repository, and every measurement marked implemented is checked against Dokimos's own accepted baseline when the site is built.",
  children: html`<ul class="readouts" aria-label="Measurement counts by status">
    ${STATUS_ORDER.map((status) => html`<li class="readout"><p class="readout-label">${STATUS_TITLES[status]}</p><p class="readout-value">${byStatus(status).length}</p><p><a href="#status-${status}">See the list</a></p></li>`)}
  </ul>`,
})}

<section aria-labelledby="reading">
  ${sectionHeading({ id: "reading", eyebrow: "Reading this page", title: "Direction is not always a verdict.", note: "Direction values come from config/metric-catalog.json." })}
  <div class="prose-columns">
    <p><strong>Contextual</strong> metrics have no inherent good direction. A larger file or more commits is not automatically worse; the number becomes meaningful through history, correlation, and policy. Only a policy may declare that lower is better for a contextual metric.</p>
    <p><strong>Observations</strong> are measured directly. <strong>Derived</strong> measurements are correlations over several observations and always carry the signals that produced them. Lexical indicators are deliberately conservative: they point to places worth reading, not to proven defects.</p>
  </div>
</section>

${STATUS_ORDER.map((status) => statusTable(site, status, byStatus(status), statusCriteria[status]))}

<section class="band band-real" aria-labelledby="self-measurement">
  ${sectionHeading({ id: "self-measurement", eyebrow: "Dokimos on Dokimos", title: "Highest complexity in Dokimos's own source.", note: "complexity.proxy-cyclomatic v1, lexical proxy." })}
  ${realLabel(html`From the accepted baseline snapshot of <code>${snapshot.repository}</code> at <a href="https://github.com/${snapshot.repository}/commit/${snapshot.revision}"><span class="mono">${snapshot.shortRevision}</span></a>, collected ${snapshot.collectedOn}. ${snapshot.observationCount} observations; ${Object.entries(snapshot.states).map(([state, count]) => `${count} ${state}`).join(", ")}.`)}
  <div class="table-scroll" role="region" aria-labelledby="self-caption" tabindex="0">
    <table class="data-table">
      <caption id="self-caption">The five files with the highest complexity proxy</caption>
      <thead><tr><th scope="col">File</th><th scope="col" class="num">Complexity proxy (points)</th></tr></thead>
      <tbody>${snapshot.topComplexity.map((row) => html`<tr><th scope="row"><code class="path">${row.scope}</code></th><td class="num">${row.value}</td></tr>`)}</tbody>
    </table>
  </div>
  <p class="section-foot">These are real observations, not demonstration data. A single snapshot shows where complexity sits; the trajectory across later snapshots will show where it is going.</p>
</section>`;
};
