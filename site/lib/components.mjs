// Small shared presentation pieces. Every status carries its name as text; the
// glyph and colour only reinforce it.

import { html } from "./html.mjs";

const STATES = {
  // Gate results (Policy.fs)
  Pass: { tone: "good", glyph: "✓" },
  Warning: { tone: "warn", glyph: "!" },
  Failure: { tone: "bad", glyph: "✕" },
  NotEvaluated: { tone: "unknown", glyph: "?" },
  // Finding lifecycle (Domain.fs FindingState)
  Introduced: { tone: "bad", glyph: "+" },
  Persistent: { tone: "warn", glyph: "=" },
  Improved: { tone: "good", glyph: "↘" },
  Resolved: { tone: "good", glyph: "✓" },
  Regressed: { tone: "bad", glyph: "↗" },
  Resurfaced: { tone: "bad", glyph: "↺" },
  // Trend directions (Trend.fs)
  Deteriorated: { tone: "bad", glyph: "↗" },
  Unchanged: { tone: "neutral", glyph: "=" },
  NotComparable: { tone: "unknown", glyph: "?" },
  // Measurement states (Domain.fs Measurement)
  Available: { tone: "neutral", glyph: "●" },
  Unavailable: { tone: "unknown", glyph: "○" },
  Failed: { tone: "unknown", glyph: "✕" },
};

const LABELS = { NotEvaluated: "Not evaluated", NotComparable: "Not comparable" };

export const stateLabel = (state) => LABELS[state] ?? state;

export const stateBadge = (state) => {
  const known = STATES[state];
  if (!known) throw new Error(`Unknown state for badge: ${state}`);
  return html`<span class="state tone-${known.tone}" data-state="${state}"><span class="state-glyph" aria-hidden="true">${known.glyph}</span>${stateLabel(state)}</span>`;
};

const STATUS_GLYPHS = { implemented: "●", experimental: "◐", planned: "○" };

export const statusBadge = (status) => {
  if (!STATUS_GLYPHS[status]) throw new Error(`Unknown implementation status: ${status}`);
  return html`<span class="status status-${status}" data-status="${status}"><span class="state-glyph" aria-hidden="true">${STATUS_GLYPHS[status]}</span>${status[0].toUpperCase()}${status.slice(1)}</span>`;
};

export const demoLabel = (text) => html`<p class="provenance-label is-demo"><strong>Demonstration data.</strong> ${text}</p>`;

export const realLabel = (text) => html`<p class="provenance-label is-real"><strong>Real Dokimos evidence.</strong> ${text}</p>`;

export const sectionHeading = ({ id, eyebrow, title, note }) => html`
<div class="section-heading">
  <div>
    <p class="eyebrow">${eyebrow}</p>
    <h2 id="${id}">${title}</h2>
  </div>
  ${note ? html`<p class="section-note">${note}</p>` : ""}
</div>`;

// Signed numbers use a true minus sign so screen readers and readers agree.
export const signed = (value) => (value === null ? "—" : value > 0 ? `+${value}` : value < 0 ? `−${Math.abs(value)}` : "0");

export const pageIntro = ({ eyebrow, title, lead, children }) => html`
<section class="page-intro" aria-labelledby="page-title">
  <p class="eyebrow">${eyebrow}</p>
  <h1 id="page-title">${title}</h1>
  <p class="lead">${lead}</p>
  ${children ?? ""}
</section>`;
