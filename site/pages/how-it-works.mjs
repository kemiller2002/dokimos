import { html } from "../lib/html.mjs";
import { pageIntro, sectionHeading, stateBadge } from "../lib/components.mjs";

export const page = {
  path: "/how-it-works/",
  title: "How a Dokimos analysis works",
  description:
    "The Dokimos analysis lifecycle: collect source and Git history, measure, write an immutable snapshot, correlate, compare compatible snapshots, and evaluate policy — with evidence kept separate from judgment.",
};

const source = (site, file) => html`<a href="${site.repository}/blob/main/${file}"><code>${file}</code></a>`;

const steps = (site) => [
  {
    title: "Collect source and history",
    body: html`<p>Dokimos reads source files under a directory (F# today; analyzers are adapters, and the domain is language-neutral) and a Git history exported with <code>git log --numstat --find-renames</code>. History is an input, not an afterthought: churn, commit frequency, first and last change, and renames come from it.</p><p>Source: ${source(site, "src/Dokimos.Core/GitHistory.fs")}, ${source(site, "src/Dokimos.Core/Temporal.fs")}.</p>`,
  },
  {
    title: "Measure",
    body: html`<p>Analyzers produce observations per file: size, mutable bindings, broad catch-all patterns, a lexical complexity proxy, type-weakening and scaffolding indicators. Each observation names its metric, the metric's definition version, its unit, and its scope.</p><p>A measurement is <em>available</em> with a value, <em>unavailable</em> with a reason, or <em>failed</em> with an error code. These are different states, and none of them is zero.</p>`,
  },
  {
    title: "Write an immutable snapshot",
    body: html`<p>Observations are written into a canonical, schema-versioned snapshot identified by repository, revision, ref, collection time, and collector version. Snapshots are never edited. Re-running with a newer analyzer creates new evidence instead of rewriting old evidence.</p><p>Schema: ${source(site, "schemas/dokimos-snapshot.schema.json")}.</p>`,
  },
  {
    title: "Correlate structural and temporal evidence",
    body: html`<p>Correlation rules combine independent signals. A maintainability hotspot requires structural complexity <em>and</em> temporal change pressure; neither alone is enough. Each resulting finding carries the signals that produced it, so it can be decomposed rather than trusted.</p><p>Source: ${source(site, "src/Dokimos.Core/Correlation.fs")}.</p>`,
  },
  {
    title: "Compare compatible snapshots",
    body: html`<p><code>dokimos compare</code> reads a baseline snapshot and a current snapshot. For every metric it reports added, removed, improved, deteriorated, unchanged, changed, or not comparable; for every finding it reports introduced, resolved, or persistent. Metrics without an inherent direction, such as complexity, are reported as <em>changed</em>: whether lower is better there is a policy decision, not an analyzer assumption.</p><p>Observations are keyed by metric, definition version, and scope, so a definition change starts a new series instead of producing a false trend. Source: ${source(site, "src/Dokimos.Core/CanonicalComparison.fs")}.</p>`,
  },
  {
    title: "Evaluate policy",
    body: html`<p>Thresholds and ratchets are applied to observations, producing ${stateBadge("Pass")}, ${stateBadge("Warning")}, ${stateBadge("Failure")}, or ${stateBadge("NotEvaluated")}. Missing evidence is never evaluated as passing. The policy logic is implemented and tested; wiring it into CI as a gate against the accepted baseline is the next planned step.</p><p>Source: ${source(site, "src/Dokimos.Core/Policy.fs")}, policy: ${source(site, "config/dokimos-policy.json")}.</p>`,
  },
  {
    title: "Accept a new baseline deliberately",
    body: html`<p>Baselines only move when someone authorises it. In Dokimos's own CI, a new canonical snapshot is promoted to <code>baselines/accepted-snapshot.json</code> only when an explicit marker file is present and the run succeeds. The acceptance is a commit, so the history of accepted states is itself evidence.</p>`,
  },
];

const commands = [
  { command: "dokimos measure <source-file>", output: "Structural, quality, complexity, and agent-pattern measurements for one file.", exit: "0 on success; 2 on invalid arguments." },
  { command: "dokimos analyze <source-directory> [--git-history <numstat-file>]", output: "Repository analysis: per-file measurements, correlations, and duplicate blocks.", exit: "0 on success; 2 on invalid arguments." },
  { command: "dokimos snapshot <source-directory> --git-history <file> --repository <owner/repo> --revision <sha> --ref <ref>", output: "A canonical schema-versioned snapshot with metrics and findings.", exit: "0 on success; 2 on invalid arguments." },
  { command: "dokimos compare <before-snapshot> <after-snapshot>", output: "A comparison of two canonical snapshots.", exit: "0 on success; 3 with a JSON unavailable result when a snapshot is malformed or uses an unsupported schema; 2 on invalid arguments." },
];

export const render = ({ site }) => html`
${pageIntro({
  eyebrow: "How it works",
  title: "Evidence first. Judgment second.",
  lead: "A Dokimos run collects observations, writes them into an immutable snapshot, and only then compares and evaluates them. Each stage can be inspected on its own, which is what makes a conclusion explainable.",
})}

<section aria-labelledby="lifecycle">
  ${sectionHeading({ id: "lifecycle", eyebrow: "Analysis lifecycle", title: "Seven stages from source to decision.", note: "Stages 1–5 and 7 run in Dokimos's own CI today. Stage 6 is implemented and tested but not yet a CI gate." })}
  <ol class="steps">
    ${steps(site).map((step) => html`<li><div><h3>${step.title}</h3>${step.body}</div></li>`)}
  </ol>
</section>

<section aria-labelledby="cli">
  ${sectionHeading({ id: "cli", eyebrow: "Command line", title: "Deterministic, non-interactive, machine-readable.", note: `Usage as printed by src/Dokimos.Cli/Program.fs.` })}
  <div class="table-scroll" role="region" aria-labelledby="cli-caption" tabindex="0">
    <table class="data-table">
      <caption id="cli-caption">Dokimos CLI commands. All output is indented JSON on standard output.</caption>
      <thead><tr><th scope="col">Command</th><th scope="col">Produces</th><th scope="col">Exit codes</th></tr></thead>
      <tbody>
        ${commands.map((row) => html`<tr><th scope="row"><code>${row.command}</code></th><td>${row.output}</td><td>${row.exit}</td></tr>`)}
      </tbody>
    </table>
  </div>
  <p class="section-foot">Run from source with <code>dotnet run --project src/Dokimos.Cli -- &lt;command&gt;</code>. Dokimos targets .NET 8 and treats compiler warnings as errors.</p>
</section>

<section aria-labelledby="ci">
  ${sectionHeading({ id: "ci", eyebrow: "In continuous integration", title: "When a prerequisite fails, the evidence says so.", note: "From .github/workflows/ci.yml in this repository." })}
  <div class="prose-columns">
    <p>Dokimos's own pipeline builds and tests the solution, measures every F# source file, captures Git history, analyzes the repository, writes a canonical snapshot, and compares it with the accepted baseline. All of it is uploaded as a build artifact tied to the commit.</p>
    <p>If the build fails, the pipeline does not skip the evidence or write zeros. It writes an explicit record such as <code>{"state":"unavailable","reason":"snapshot-prerequisite-failed"}</code>. A later reader can see that evidence is missing and why, instead of mistaking silence for health.</p>
  </div>
  <pre class="code-block" tabindex="0"><code>dotnet build Dokimos.sln --configuration Release
dotnet test Dokimos.sln --configuration Release --no-build
git log --numstat --find-renames --format='commit %H %aI' -- src &gt; artifacts/dokimos/git-history.txt
dotnet run --project src/Dokimos.Cli/Dokimos.Cli.fsproj -- snapshot src \\
  --git-history artifacts/dokimos/git-history.txt \\
  --repository "$GITHUB_REPOSITORY" --revision "$GITHUB_SHA" --ref "$GITHUB_REF_NAME" \\
  &gt; artifacts/dokimos/snapshot.json
dotnet run --project src/Dokimos.Cli/Dokimos.Cli.fsproj -- compare \\
  baselines/accepted-snapshot.json artifacts/dokimos/snapshot.json &gt; artifacts/dokimos/comparison.json</code></pre>
  <p class="section-foot">Next: <a href="/quality-model/">the quality model</a> defines each object these stages produce.</p>
</section>`;
