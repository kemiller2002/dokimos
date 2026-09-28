import { html } from "../lib/html.mjs";
import { pageIntro, sectionHeading, statusBadge } from "../lib/components.mjs";

export const page = {
  path: "/architecture/",
  title: "Dokimos architecture and Echelon boundaries",
  description:
    "The Dokimos evidence pipeline, its F# projects and design rules, and the boundaries between Dokimos and Praxis/ROS, Ordo, Tutela, Aegis, Forma, and Folio.",
};

const pipeline = [
  { stage: "Source + version-control evidence", detail: "Files and Git history. Reading them is an explicit external effect." },
  { stage: "Analyzer adapters", detail: "Language-specific collectors with declared capabilities. They cannot reinterpret history." },
  { stage: "Normalized observations", detail: "Language-neutral: metric, version, scope, measurement, provenance." },
  { stage: "Immutable snapshot store", detail: "Versioned JSON artifacts today, behind a storage boundary." },
  { stage: "Comparison and lifecycle engine", detail: "Compatible comparisons, trends, finding transitions." },
  { stage: "Policy and baseline evaluation", detail: "Thresholds, ratchets, dispositions, accepted baselines." },
  { stage: "Machine results · Forma UI · Folio report", detail: "Canonical JSON now; human interfaces later." },
];

const projects = [
  { name: "Dokimos.Domain", role: "Pure domain types: identifiers, scopes, measurements, provenance, snapshots, finding states, thresholds, compatibility." },
  { name: "Dokimos.Core", role: "Analysis, Git history, temporal and region evidence, correlation, comparison, trend, and policy." },
  { name: "Dokimos.Cli", role: "The command surface: measure, analyze, snapshot, compare." },
  { name: "Test projects", role: "Domain, core, and CLI invariants, run in CI with warnings treated as errors." },
];

const boundaries = [
  { system: "Dokimos", owns: "Code-quality evidence, history, comparison, and quality-policy evaluation.", relation: "This system.", status: "implemented" },
  { system: "Praxis / ROS", owns: "Engineering work items, execution attribution, provenance, and development telemetry.", relation: "Dokimos's repository is governed by ROS. Dokimos may correlate quality evidence with ROS telemetry through an explicit evidence boundary; it does not collect or re-own that telemetry.", status: "planned" },
  { system: "Ordo", owns: "Engineering and state methodology; legality of application state.", relation: "Dokimos follows Ordo/SDE conventions and models its own states as explicit types.", status: "implemented" },
  { system: "Tutela", owns: "Security assurance and security-specific authority.", relation: "Security-specific analysis routes to Tutela. Dokimos does not become a competing security scanner.", status: "planned" },
  { system: "Aegis", owns: "Unexpected operational faults at architectural boundaries.", relation: "Git, filesystem, process, and persistence faults are to be classified through Aegis. Expected outcomes — unavailable metrics, policy failures — stay typed Dokimos results.", status: "planned" },
  { system: "Forma", owns: "Reusable application presentation.", relation: "The interactive results UI will consume a pinned Forma release.", status: "planned" },
  { system: "Folio", owns: "Reusable print and report presentation.", relation: "Printable quality reports will consume a pinned Folio release.", status: "planned" },
];

export const render = ({ site }) => html`
${pageIntro({
  eyebrow: "Architecture",
  title: "A pipeline where every stage keeps its evidence.",
  lead: "Dokimos is an F# system built around one rule: an observation is not a judgment. External effects sit at the edges, domain states are explicit types, and each stage's output can be inspected without trusting the next one.",
})}

<section aria-labelledby="pipeline">
  ${sectionHeading({ id: "pipeline", eyebrow: "Pipeline", title: "From source to decision in seven layers.", note: "docs/architecture/ARCHITECTURE.md" })}
  <ol class="steps">
    ${pipeline.map((row) => html`<li><div><h3>${row.stage}</h3><p>${row.detail}</p></div></li>`)}
  </ol>
</section>

<section aria-labelledby="design-rules">
  ${sectionHeading({ id: "design-rules", eyebrow: "Design rules", title: "Invalid states should be hard to represent.", note: "From AGENTS.md and REQUIREMENTS.md R9." })}
  <div class="definition-grid">
    <article><h3>Explicit states</h3><p>Discriminated unions for measurement availability, finding lifecycle, threshold disposition, comparison compatibility, and gate results — not booleans or free-form strings.</p></article>
    <article><h3>Effects at the boundary</h3><p>Reading Git, invoking analyzers, and writing artifacts are explicit edges. Measurement, comparison, and policy are pure functions.</p></article>
    <article><h3>Immutable history</h3><p>Snapshots are never rewritten. New policy never edits old observations. Re-analysis creates new evidence.</p></article>
    <article><h3>Warnings are errors</h3><p>The F# build fails on any warning, and that zero-warning state is itself ratcheted in BASELINE-0001.</p></article>
  </div>
</section>

<section aria-labelledby="projects">
  ${sectionHeading({ id: "projects", eyebrow: "Projects", title: "Four projects, one direction of dependency.", note: "Dokimos.sln" })}
  <dl class="boundary-list">
    ${projects.map((row) => html`<div><dt>${row.name}</dt><dd>${row.role}</dd></div>`)}
  </dl>
</section>

<section aria-labelledby="boundaries">
  ${sectionHeading({ id: "boundaries", eyebrow: "Echelon boundaries", title: "Integrate through evidence, never by absorbing authority.", note: "REQUIREMENTS.md R8 and R13." })}
  <div class="table-scroll" role="region" aria-labelledby="boundaries-caption" tabindex="0">
    <table class="data-table">
      <caption id="boundaries-caption">Responsibilities and the state of each integration</caption>
      <thead><tr><th scope="col">System</th><th scope="col">Owns</th><th scope="col">Relationship to Dokimos</th><th scope="col">Integration</th></tr></thead>
      <tbody>${boundaries.map((row) => html`<tr><th scope="row">${row.system}</th><td>${row.owns}</td><td>${row.relation}</td><td>${statusBadge(row.status)}</td></tr>`)}</tbody>
    </table>
  </div>
  <p class="section-foot">Dokimos must stay independently installable. If an optional Echelon system is absent or unreachable, Dokimos records the related evidence as unavailable and carries on. It never fails, and never invents data, because a neighbour is missing.</p>
</section>

<section aria-labelledby="this-site">
  ${sectionHeading({ id: "this-site", eyebrow: "This website", title: "Built from the same evidence it describes.", note: "site/ in the Dokimos repository." })}
  <div class="prose-columns">
    <p>This site is static HTML and CSS produced by a small deterministic Node build. It reads the metric catalog, the accepted baseline snapshot, and the ratchet policy directly from the repository, and it refuses to build if the site's claims about implementation status disagree with that evidence.</p>
    <p>Demonstration data is validated against a schema, labelled on every surface, and derived with the same rules as the F# domain. Charts are rendered at build time with text and table equivalents; no JavaScript is required to read anything. <a href="${site.repository}/tree/main/site">Read the site source</a>.</p>
  </div>
</section>`;
