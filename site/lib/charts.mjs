// Build-time SVG charts. Each chart answers one stated question and is always
// paired with a textual equivalent by the page that renders it.

import { html } from "./html.mjs";

const round = (value) => Math.round(value * 10) / 10;

const scale = (domainMin, domainMax, rangeMin, rangeMax) => (value) =>
  round(rangeMin + ((value - domainMin) / (domainMax - domainMin)) * (rangeMax - rangeMin));

const floorTo = (step, value) => Math.floor(value / step) * step;
const ceilTo = (step, value) => Math.ceil(value / step) * step;

// Split points into contiguous runs of available values so missing evidence
// renders as a gap, never as an interpolated or zero value.
const runs = (points) =>
  points
    .reduce((acc, point) => (point.value === null ? [...acc, []] : [...acc.slice(0, -1), [...acc.at(-1), point]]), [[]])
    .filter((run) => run.length > 0);

export const trajectoryChart = ({ id, trajectory, threshold, unit, title, description, caption }) => {
  const width = 760;
  const height = 340;
  const margin = { top: 24, right: 188, bottom: 64, left: 52 };
  const values = trajectory.points.map((point) => point.value).filter((value) => value !== null);
  const yMin = floorTo(5, Math.min(...values) - 4);
  const yMax = ceilTo(5, Math.max(...values, threshold.warn) + 2);
  const x = scale(0, trajectory.points.length - 1, margin.left, width - margin.right);
  const y = scale(yMin, yMax, height - margin.bottom, margin.top);
  const ticks = Array.from({ length: (yMax - yMin) / 5 + 1 }, (_, index) => yMin + index * 5);
  const indexOf = (commit) => trajectory.points.findIndex((point) => point.commit === commit);
  const currentX = x(trajectory.points.length - 1);
  const reference = (className, value, label) => html`
    <g class="chart-reference ${className}">
      <line x1="${margin.left}" x2="${width - margin.right}" y1="${y(value)}" y2="${y(value)}" />
      <text x="${width - margin.right + 10}" y="${y(value) + 4}">${label}</text>
    </g>`;
  const failAbove = threshold.fail > yMax;

  return html`
<figure class="chart-figure" aria-labelledby="${id}-caption">
  <div class="chart-scroll" role="region" aria-labelledby="${id}-title" tabindex="0">
  <svg class="chart trajectory-chart" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="${id}-title ${id}-desc" focusable="false">
    <title id="${id}-title">${title}</title>
    <desc id="${id}-desc">${description}</desc>
    <g class="chart-grid" aria-hidden="true">
      ${ticks.map((tick) => html`<line x1="${margin.left}" x2="${width - margin.right}" y1="${y(tick)}" y2="${y(tick)}" /><text class="chart-tick" x="${margin.left - 10}" y="${y(tick) + 4}" text-anchor="end">${tick}</text>`)}
    </g>
    <g aria-hidden="true">
      ${reference("is-threshold", threshold.warn, `Warn threshold ${threshold.warn}`)}
      ${reference("is-baseline", trajectory.baseline.value, `Baseline (${trajectory.baseline.commit}) ${trajectory.baseline.value}`)}
      ${reference("is-best", trajectory.best.value, `Best demonstrated (${trajectory.best.commit}) ${trajectory.best.value}`)}
      <g class="chart-gap">
        <line x1="${currentX}" x2="${currentX}" y1="${y(trajectory.best.value)}" y2="${y(trajectory.current.value)}" />
        <text x="${currentX + 10}" y="${round((y(trajectory.best.value) + y(trajectory.current.value)) / 2) + 4}">+${trajectory.sinceBest.delta} over best</text>
      </g>
      ${runs(trajectory.points).map(
        (run) => html`<polyline class="chart-series" points="${run.map((point) => `${x(indexOf(point.commit))},${y(point.value)}`).join(" ")}" />`,
      )}
      ${trajectory.points.map((point, index) =>
        point.value === null
          ? html`<g class="chart-missing"><line x1="${x(index)}" x2="${x(index)}" y1="${margin.top}" y2="${height - margin.bottom}" /><text x="${x(index) + 6}" y="${margin.top + 12}">no value (failed)</text></g>`
          : html`<g class="chart-point${point.commit === trajectory.current.commit ? " is-current" : ""}"><circle cx="${x(index)}" cy="${y(point.value)}" r="${point.commit === trajectory.current.commit ? 6 : 4.5}" /><text x="${x(index)}" y="${y(point.value) - 12}" text-anchor="middle">${point.value}</text></g>`,
      )}
      ${trajectory.points.map(
        (point, index) => html`<text class="chart-axis-label" x="${x(index)}" y="${height - margin.bottom + 22}" text-anchor="middle">${point.commit}</text><text class="chart-axis-sub" x="${x(index)}" y="${height - margin.bottom + 38}" text-anchor="middle">${point.sha}</text>`,
      )}
      <text class="chart-axis-title" x="${margin.left}" y="${height - 6}">Commit (oldest to newest)</text>
      <text class="chart-axis-title" x="${margin.left - 44}" y="${margin.top - 10}">${unit}</text>
    </g>
  </svg>
  </div>
  <figcaption id="${id}-caption">${caption}${failAbove ? ` The fail threshold (${threshold.fail}) lies above the plotted range.` : ""}</figcaption>
</figure>`;
};

const QUADRANT_LABELS = {
  hotspot: "Complex and changing",
  "complex-stable": "Complex, stable",
  "active-simple": "Changing, simple",
  quiet: "Simple, stable",
};

export const hotspotChart = ({ id, files, policy, title, description }) => {
  const width = 760;
  const height = 420;
  const margin = { top: 28, right: 32, bottom: 60, left: 60 };
  const xMax = ceilTo(2, Math.max(...files.map((file) => file.windowCommits), policy.minimumWindowCommits) + 1);
  const yMax = ceilTo(10, Math.max(...files.map((file) => file.complexity), policy.minimumComplexity) + 4);
  const x = scale(0, xMax, margin.left, width - margin.right);
  const y = scale(0, yMax, height - margin.bottom, margin.top);
  const splitX = x(policy.minimumWindowCommits);
  const splitY = y(policy.minimumComplexity);
  const basename = (path) => path.split("/").at(-1);
  const labelAnchor = (file) => (x(file.windowCommits) > width - margin.right - 140 ? "end" : "start");
  const labelDx = (file) => (labelAnchor(file) === "end" ? -1 : 1) * (file.repeatedRegion ? 20 : 12);

  return html`
<figure class="chart-figure" aria-labelledby="${id}-caption">
  <div class="chart-scroll" role="region" aria-labelledby="${id}-title" tabindex="0">
  <svg class="chart hotspot-chart" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="${id}-title ${id}-desc" focusable="false">
    <title id="${id}-title">${title}</title>
    <desc id="${id}-desc">${description}</desc>
    <g aria-hidden="true">
      <rect class="quadrant is-hotspot" x="${splitX}" y="${margin.top}" width="${width - margin.right - splitX}" height="${splitY - margin.top}" />
      <line class="quadrant-rule" x1="${splitX}" x2="${splitX}" y1="${margin.top}" y2="${height - margin.bottom}" />
      <line class="quadrant-rule" x1="${margin.left}" x2="${width - margin.right}" y1="${splitY}" y2="${splitY}" />
      <line class="chart-axis" x1="${margin.left}" x2="${width - margin.right}" y1="${height - margin.bottom}" y2="${height - margin.bottom}" />
      <line class="chart-axis" x1="${margin.left}" x2="${margin.left}" y1="${margin.top}" y2="${height - margin.bottom}" />
      <text class="quadrant-label" x="${width - margin.right - 10}" y="${margin.top + 18}" text-anchor="end">${QUADRANT_LABELS.hotspot}</text>
      <text class="quadrant-label" x="${margin.left + 10}" y="${margin.top + 18}">${QUADRANT_LABELS["complex-stable"]}</text>
      <text class="quadrant-label" x="${width - margin.right - 10}" y="${height - margin.bottom - 12}" text-anchor="end">${QUADRANT_LABELS["active-simple"]}</text>
      <text class="quadrant-label" x="${margin.left + 10}" y="${height - margin.bottom - 12}">${QUADRANT_LABELS.quiet}</text>
      <text class="chart-axis-title" x="${width - margin.right}" y="${height - 14}" text-anchor="end">Commits touching the file in the last ${policy.windowDays} days →</text>
      <text class="chart-axis-title" x="${margin.left - 44}" y="${margin.top - 12}">Complexity ↑</text>
      <text class="chart-tick" x="${splitX}" y="${height - margin.bottom + 20}" text-anchor="middle">${policy.minimumWindowCommits}</text>
      <text class="chart-tick" x="${margin.left - 10}" y="${splitY + 4}" text-anchor="end">${policy.minimumComplexity}</text>
      ${files.map(
        (file) => html`
      <g class="hotspot-point is-${file.quadrant}">
        ${file.quadrant === "hotspot"
          ? html`<rect x="${x(file.windowCommits) - 7}" y="${y(file.complexity) - 7}" width="14" height="14" />`
          : html`<circle cx="${x(file.windowCommits)}" cy="${y(file.complexity)}" r="6" />`}
        ${file.repeatedRegion ? html`<circle class="region-ring" cx="${x(file.windowCommits)}" cy="${y(file.complexity)}" r="13" />` : ""}
        <text x="${x(file.windowCommits) + labelDx(file)}" y="${y(file.complexity) + 4}" text-anchor="${labelAnchor(file)}">${basename(file.path)}</text>
      </g>`,
      )}
    </g>
  </svg>
  </div>
  <figcaption id="${id}-caption">Squares mark files in the complex-and-changing quadrant. A ring marks a repeatedly modified region.</figcaption>
</figure>`;
};

export const quadrantLabel = (quadrant) => QUADRANT_LABELS[quadrant];
