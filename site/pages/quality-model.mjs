import { html } from "../lib/html.mjs";
import { pageIntro, sectionHeading, stateBadge, statusBadge } from "../lib/components.mjs";

export const page = {
  path: "/quality-model/",
  title: "The Dokimos quality model",
  description:
    "How Dokimos separates observations, evidence, derived assessments, policies, and findings — including measurement states, versioned definitions, finding lifecycle, baselines, and ratchets.",
};

const measurementStates = [
  { state: "Available", meaning: "A value and unit were measured.", example: "complexity.proxy-cyclomatic = 19 points" },
  { state: "Unavailable", meaning: "No value exists, for a stated reason: unsupported, not configured, or insufficient evidence.", example: "No coverage tool configured for this repository." },
  { state: "Failed", meaning: "Collection was attempted and failed, with an error code and message.", example: "Analyzer process exited before producing output." },
];

const findingStates = [
  { state: "Introduced", rule: "Absent in the previous snapshot, present now." },
  { state: "Persistent", rule: "Present in both, with the same magnitude (or no magnitude)." },
  { state: "Improved", rule: "Present in both, with a smaller magnitude now." },
  { state: "Regressed", rule: "Present in both, with a larger magnitude now." },
  { state: "Resolved", rule: "Present previously, absent now." },
  { state: "Resurfaced", rule: "Present again after having been resolved." },
];

const gateResults = [
  { state: "Pass", rule: "Evidence available and within the limit, or the disposition is observe-only." },
  { state: "Warning", rule: "Evidence exceeds the limit and the disposition is warn. Reports the actual value and the limit." },
  { state: "Failure", rule: "Evidence exceeds the limit and the disposition is fail. Reports the actual value and the limit." },
  { state: "NotEvaluated", rule: "Evidence is unavailable or collection failed. Missing evidence never passes and never fails a gate by being read as zero." },
];

const correlations = [
  { kind: "Maintainability hotspot", requires: "Structural complexity + frequent change or high churn" },
  { kind: "Unstable public surface", requires: "API instability + frequent change or a repeatedly modified region" },
  { kind: "Untested high change", requires: "No corresponding test change + high churn or frequent change" },
  { kind: "Agent-generated risk pattern", requires: "Type weakening or dependency additions + no corresponding test change. Does not assert authorship." },
  { kind: "Persistent debt hotspot", requires: "Aged debt + frequent change or a repeatedly modified region" },
];

const baselines = [
  { kind: "Accepted baseline", status: "implemented", note: "BASELINE-0001: promoted only after verified green CI; stored as baselines/accepted-snapshot.json." },
  { kind: "Best-demonstrated ratchet", status: "implemented", note: "A metric may not deteriorate beyond its best accepted value (Policy.evaluateRatchet)." },
  { kind: "Fixed baseline", status: "experimental", note: "Any compatible snapshot can be compared with dokimos compare; there is no named fixed-baseline registry yet." },
  { kind: "Branch baseline", status: "planned", note: "Compare a branch with its own accepted state (R4)." },
  { kind: "Release baseline", status: "planned", note: "Compare release to release (R4, R5)." },
];

export const render = () => html`
${pageIntro({
  eyebrow: "Quality model",
  title: "Five objects, never blurred together.",
  lead: "Dokimos keeps what was measured apart from what it implies and apart from what an organisation decided. Thresholds can change tomorrow without falsifying anything recorded today.",
})}

<section aria-labelledby="layers">
  ${sectionHeading({ id: "layers", eyebrow: "The layers", title: "Observation, evidence, derived signal, policy, finding.", note: "Every aggregate must decompose into these." })}
  <div class="definition-grid">
    <article><h3>Observation</h3><p>One measured fact: metric, definition version, scope (repository, project, component, file, type, or member), measurement, and provenance — collector, collector version, configuration, and collection time.</p></article>
    <article><h3>Evidence</h3><p>Observations held in an immutable snapshot for a repository revision. Evidence is appended, never edited. New analyzer versions create new evidence.</p></article>
    <article><h3>Derived signal</h3><p>A calculation over evidence: a delta, a direction, a correlation. It records which observations it used, so it can always be taken apart.</p></article>
    <article><h3>Policy</h3><p>An explicit, versioned rule — threshold or ratchet — with a disposition: observe only, warn, or fail. Policy reads evidence; it never rewrites it.</p></article>
    <article><h3>Finding</h3><p>A durable record with a stable identity, rule, location, evidence, disposition, first-seen and last-seen snapshots, and a lifecycle state.</p></article>
  </div>
</section>

<section aria-labelledby="measurement-states">
  ${sectionHeading({ id: "measurement-states", eyebrow: "Measurement states", title: "Unknown is not good. Unknown is not zero.", note: "Domain type: Measurement in src/Dokimos.Domain/Domain.fs." })}
  <div class="table-scroll" role="region" aria-labelledby="measurement-states-caption" tabindex="0">
    <table class="data-table">
      <caption id="measurement-states-caption">The three measurement states</caption>
      <thead><tr><th scope="col">State</th><th scope="col">Meaning</th><th scope="col">Example</th></tr></thead>
      <tbody>${measurementStates.map((row) => html`<tr><th scope="row">${stateBadge(row.state)}</th><td>${row.meaning}</td><td>${row.example}</td></tr>`)}</tbody>
    </table>
  </div>
  <p class="section-foot">A tool that prints nothing when it fails, or records zero warnings when the build never ran, turns missing evidence into false reassurance. Dokimos refuses to: unavailable and failed evidence are carried forward as themselves, and policy evaluates them as not evaluated.</p>
</section>

<section aria-labelledby="versions">
  ${sectionHeading({ id: "versions", eyebrow: "Versioned definitions", title: "A changed definition starts a new series.", note: "Domain type: ComparisonCompatibility." })}
  <div class="prose-columns">
    <p>Every metric has a durable identifier and an integer definition version. Two observations are compatible only when they share the metric, the version, and the unit. Only compatible observations produce a delta.</p>
    <p>If the definition of complexity changes from version 1 to version 2, Dokimos reports the comparison as incompatible rather than drawing a line between numbers that mean different things. History keeps its original meaning.</p>
  </div>
</section>

<section aria-labelledby="derived">
  ${sectionHeading({ id: "derived", eyebrow: "Derived assessments", title: "Trends and correlations, each decomposable.", note: "Sources: Trend.fs, Correlation.fs." })}
  <div class="prose-columns">
    <p>A trend compares two compatible observations under a declared preference and reports its delta and a direction: ${stateBadge("Improved")}, ${stateBadge("Unchanged")}, ${stateBadge("Deteriorated")}, or ${stateBadge("NotComparable")} when either side is missing or incompatible.</p>
    <p>Correlations require at least two independent kinds of evidence. The table lists the rules the domain defines today; the <a href="/metrics/">metrics page</a> shows which of their input signals are collected.</p>
  </div>
  <div class="table-scroll" role="region" aria-labelledby="correlations-caption" tabindex="0">
    <table class="data-table">
      <caption id="correlations-caption">Correlation rules and the evidence each requires</caption>
      <thead><tr><th scope="col">Correlation</th><th scope="col">Requires</th></tr></thead>
      <tbody>${correlations.map((row) => html`<tr><th scope="row">${row.kind}</th><td>${row.requires}</td></tr>`)}</tbody>
    </table>
  </div>
</section>

<section aria-labelledby="policy">
  ${sectionHeading({ id: "policy", eyebrow: "Policy", title: "Thresholds, ratchets, and their results.", note: "Source: src/Dokimos.Core/Policy.fs." })}
  <div class="prose-columns">
    <p>A <strong>threshold</strong> is a fixed limit with a disposition. It answers: is this value acceptable by an organisational standard?</p>
    <p>A <strong>ratchet</strong> is a limit set by the best accepted value the repository has demonstrated, with a declared preference (lower or higher is better) and a disposition. It answers: has this code slipped from a state it already proved it could reach?</p>
  </div>
  <div class="table-scroll" role="region" aria-labelledby="gate-caption" tabindex="0">
    <table class="data-table">
      <caption id="gate-caption">Gate results</caption>
      <thead><tr><th scope="col">Result</th><th scope="col">When</th></tr></thead>
      <tbody>${gateResults.map((row) => html`<tr><th scope="row">${stateBadge(row.state)}</th><td>${row.rule}</td></tr>`)}</tbody>
    </table>
  </div>
</section>

<section aria-labelledby="findings">
  ${sectionHeading({ id: "findings", eyebrow: "Finding lifecycle", title: "Findings have a past, not just a present.", note: "Domain type: FindingState. Transitions: Findings.fs." })}
  <div class="table-scroll" role="region" aria-labelledby="findings-caption" tabindex="0">
    <table class="data-table">
      <caption id="findings-caption">Finding lifecycle states</caption>
      <thead><tr><th scope="col">State</th><th scope="col">Transition rule</th></tr></thead>
      <tbody>${findingStates.map((row) => html`<tr><th scope="row">${stateBadge(row.state)}</th><td>${row.rule}</td></tr>`)}</tbody>
    </table>
  </div>
  <p class="section-foot">If either snapshot's evidence is unknown, no transition is recorded. Finding identities use fingerprints of rule, semantic scope, and location; when code moves and identity becomes uncertain, the match is recorded as uncertain instead of asserted. Suppressions and accepted exceptions — with reason, scope, author, and expiry — are required (R3) but not yet implemented.</p>
</section>

<section aria-labelledby="baselines">
  ${sectionHeading({ id: "baselines", eyebrow: "Baselines", title: "Legacy debt can be baselined without permitting new debt.", note: "Requirement R4." })}
  <div class="table-scroll" role="region" aria-labelledby="baselines-caption" tabindex="0">
    <table class="data-table">
      <caption id="baselines-caption">Baseline kinds and their implementation status</caption>
      <thead><tr><th scope="col">Baseline</th><th scope="col">Status</th><th scope="col">Detail</th></tr></thead>
      <tbody>${baselines.map((row) => html`<tr><th scope="row">${row.kind}</th><td>${statusBadge(row.status)}</td><td>${row.note}</td></tr>`)}</tbody>
    </table>
  </div>
  <p class="section-foot">See ratcheting in action on the <a href="/#quality-over-time">overview's demonstration trajectory</a>.</p>
</section>`;
