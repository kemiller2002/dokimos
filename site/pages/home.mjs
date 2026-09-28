import { html } from "../lib/html.mjs";
import { trajectoryChart, hotspotChart, quadrantLabel } from "../lib/charts.mjs";
import { stateBadge, statusBadge, demoLabel, realLabel, sectionHeading, signed, stateLabel } from "../lib/components.mjs";

export const page = {
  path: "/",
  title: "Dokimos — know whether your codebase is getting better",
  description:
    "Dokimos is Echelon Foundry's code-quality assurance system. It keeps versioned quality evidence for every commit, so teams can see regressions, improvements, ratchets, and hotspots — and the evidence behind each conclusion.",
};

const hero = ({ site, trajectory }) => html`
<section class="hero" aria-labelledby="page-title">
  <div class="hero-grid">
    <div>
      <p class="eyebrow">Dokimos · Code-quality assurance from Echelon Foundry</p>
      <h1 id="page-title">Know whether your codebase is getting better.</h1>
      <p class="lead">Dokimos records code-quality evidence at every commit and keeps it. It shows how quality changed, where risk is concentrating, and which observations and policies produced each conclusion.</p>
      <div class="actions">
        <a class="button primary" href="/metrics/">Explore what Dokimos measures</a>
        <a class="button" href="${site.repository}">View the project on GitHub</a>
      </div>
    </div>
    <aside class="hero-readout" aria-labelledby="hero-readout-title">
      <p class="eyebrow" id="hero-readout-title">One file, eight commits</p>
      <dl class="readout-list">
        <div><dt>Baseline <span class="mono">(${trajectory.baseline.commit})</span></dt><dd>${trajectory.baseline.value}</dd></div>
        <div><dt>Best demonstrated <span class="mono">(${trajectory.best.commit})</span></dt><dd>${trajectory.best.value}</dd></div>
        <div class="is-current"><dt>Current <span class="mono">(${trajectory.current.commit})</span></dt><dd>${trajectory.current.value}</dd></div>
      </dl>
      <p class="readout-verdict">Better than where it started. Worse than it has already proven it can be.</p>
      <p class="readout-note">Complexity of <code>InvoiceRules.fs</code> in a demonstration repository. <a href="#quality-over-time">See the full trajectory</a>.</p>
    </aside>
  </div>
</section>`;

const problem = ({ trajectory, demo }) => html`
<section aria-labelledby="problem">
  ${sectionHeading({ id: "problem", eyebrow: "The problem", title: "A single report cannot tell you which way you are moving.", note: "Same file, same commit, two readings." })}
  <div class="prose-columns">
    <p>A static-analysis run answers one question: does this code break a rule right now? It cannot say whether the code is better or worse than last month, whether the team already proved it could do better, or whether a quiet number hides a file that is rewritten every week.</p>
    <p>Those are the questions that decide where engineering attention should go. Answering them requires history, and history is only trustworthy if every observation is kept with the revision, analyzer, and metric definition that produced it.</p>
  </div>
  <div class="table-scroll" role="region" aria-labelledby="problem-table-caption" tabindex="0">
    <table class="compare-table">
      <caption id="problem-table-caption">What each approach reports for <code>${demo.focus.scope}</code> at commit ${trajectory.current.commit} (demonstration data)</caption>
      <thead><tr><th scope="col">Question</th><th scope="col">Point-in-time report</th><th scope="col">Dokimos</th></tr></thead>
      <tbody>
        <tr><th scope="row">Is complexity acceptable?</th><td>${trajectory.current.value} is under the warn threshold of ${demo.policy.threshold.warn}. Pass.</td><td>${stateBadge(trajectory.threshold.kind)} against the threshold, and ${stateBadge(trajectory.ratchet.kind)} against the ratchet: ${signed(trajectory.sinceBest.delta)} points worse than the best demonstrated state (${trajectory.best.value} at ${trajectory.best.commit}).</td></tr>
        <tr><th scope="row">Which way is it moving?</th><td>Not answerable.</td><td>${signed(trajectory.sincePrevious.delta)} since the previous snapshot; ${signed(trajectory.sinceBaseline.delta)} since the baseline.</td></tr>
        <tr><th scope="row">What happened when the analyzer failed?</th><td>No report, or a report that silently omits the file.</td><td>Commit ${trajectory.missing.map((point) => point.commit).join(", ")} is recorded as ${stateBadge("Failed")}. Nothing was evaluated, and nothing was recorded as zero.</td></tr>
        <tr><th scope="row">Is this file risky?</th><td>Not answerable.</td><td>Complex and changed in ${trajectory.current.windowCommits} commits in ${demo.policy.hotspot.windowDays} days: a hotspot, with the same region rewritten repeatedly.</td></tr>
      </tbody>
    </table>
  </div>
</section>`;

const sees = ({ dimensions }) => html`
<section aria-labelledby="what-dokimos-sees">
  ${sectionHeading({ id: "what-dokimos-sees", eyebrow: "What Dokimos inspects", title: "Quality dimensions, each labelled with how real it is today.", note: "Status is checked against the accepted baseline snapshot at build time." })}
  <ul class="dimension-list">
    ${dimensions.map(
      (dimension) => html`<li class="dimension">
      <div class="dimension-head"><h3>${dimension.name}</h3>${statusBadge(dimension.status)}</div>
      <p>${dimension.question}</p>
    </li>`,
    )}
  </ul>
  <p class="section-foot">Implemented measurements are emitted in Dokimos's own accepted baseline. Experimental ones have tested logic that is not yet collected into snapshots. Planned ones are requirements only. <a href="/metrics/">Read every metric definition and its limitations</a>.</p>
</section>`;

const trajectoryTable = (trajectory) => html`
<div class="table-scroll" role="region" aria-labelledby="trajectory-table-caption" tabindex="0">
  <table class="data-table">
    <caption id="trajectory-table-caption">Complexity observations by commit (demonstration data)</caption>
    <thead><tr><th scope="col">Commit</th><th scope="col">Date</th><th scope="col">Change</th><th scope="col">Measurement</th><th scope="col" class="num">Value</th><th scope="col">Note</th></tr></thead>
    <tbody>
      ${trajectory.points.map(
        (point) => html`<tr${point.commit === trajectory.current.commit ? html` class="is-current"` : ""}>
        <th scope="row"><span class="mono">${point.commit} · ${point.sha}</span></th>
        <td class="nowrap">${point.date}</td>
        <td>${point.summary}</td>
        <td>${stateBadge(point.state === "available" ? "Available" : point.state === "failed" ? "Failed" : "Unavailable")}</td>
        <td class="num">${point.value ?? "—"}</td>
        <td>${[
          point.commit === trajectory.baseline.commit ? "Baseline" : null,
          point.commit === trajectory.best.commit ? "Best demonstrated" : null,
          point.commit === trajectory.current.commit ? "Current" : null,
          point.value === null ? point.reason : null,
        ]
          .filter(Boolean)
          .join(". ")}</td>
      </tr>`,
      )}
    </tbody>
  </table>
</div>`;

const overTime = ({ demo, trajectory }) => html`
<section class="band" aria-labelledby="quality-over-time">
  ${sectionHeading({ id: "quality-over-time", eyebrow: "Quality over time", title: "Code quality is a trajectory, not a snapshot.", note: "Question answered: is this file better or worse than it has been, and does policy care?" })}
  ${demoLabel(demo.disclaimer)}
  <div class="readouts" role="list">
    <div class="readout" role="listitem"><p class="readout-label">Current (${trajectory.current.commit})</p><p class="readout-value">${trajectory.current.value}</p><p>${demo.focus.unit}, lexical complexity proxy</p></div>
    <div class="readout" role="listitem"><p class="readout-label">Since previous (${trajectory.previous.commit})</p><p class="readout-value">${signed(trajectory.sincePrevious.delta)}</p><p>${stateBadge(trajectory.sincePrevious.direction)}</p></div>
    <div class="readout" role="listitem"><p class="readout-label">Since baseline (${trajectory.baseline.commit})</p><p class="readout-value">${signed(trajectory.sinceBaseline.delta)}</p><p>${stateBadge(trajectory.sinceBaseline.direction)}</p></div>
    <div class="readout" role="listitem"><p class="readout-label">Since best (${trajectory.best.commit})</p><p class="readout-value">${signed(trajectory.sinceBest.delta)}</p><p>${stateBadge(trajectory.sinceBest.direction)}</p></div>
  </div>
  ${trajectoryChart({
    id: "trajectory",
    trajectory,
    threshold: demo.policy.threshold,
    unit: demo.focus.unit,
    title: `Complexity of ${demo.focus.scope} across commits A to H`,
    description: `Complexity fell from ${trajectory.baseline.value} at the baseline to a best of ${trajectory.best.value} at commit ${trajectory.best.commit}, has no value at commit ${trajectory.missing.map((point) => point.commit).join(", ")} because collection failed, and rose to ${trajectory.current.value} at commit ${trajectory.current.commit}. The warn threshold is ${demo.policy.threshold.warn}.`,
    caption: `The line breaks at commit ${trajectory.missing.map((point) => point.commit).join(", ")}: collection failed, so there is no value to draw. The vertical bar at ${trajectory.current.commit} is the distance from the best demonstrated state.`,
  })}
  <details class="data-disclosure" open>
    <summary>Data table for this chart</summary>
    ${trajectoryTable(trajectory)}
  </details>
  <div class="ratchet" aria-labelledby="ratcheting">
    <div>
      <h3 id="ratcheting">Ratcheting: the best state you have shown becomes the expectation.</h3>
      <p>A universal threshold says ${trajectory.current.value} is fine because it is under ${demo.policy.threshold.warn}. But this file reached ${trajectory.best.value} at commit ${trajectory.best.commit}. A ratchet keeps that demonstrated state as the bar, so drifting back toward the old condition is visible instead of silently permitted.</p>
      <p>The ratchet does not rewrite history or punish the baseline. Legacy debt can stay baselined while new deterioration is still caught. The disposition — observe only, warn, or fail — is policy, and it is chosen per metric.</p>
    </div>
    <dl class="gate-list">
      <div><dt>Threshold: warn above ${demo.policy.threshold.warn}, fail above ${demo.policy.threshold.fail}</dt><dd>${stateBadge(trajectory.threshold.kind)} <span>${trajectory.current.value} ≤ ${demo.policy.threshold.warn}</span></dd></div>
      <div><dt>Ratchet: best demonstrated ${trajectory.best.value}, disposition ${demo.policy.ratchet.disposition}</dt><dd>${stateBadge(trajectory.ratchet.kind)} <span>${trajectory.current.value} &gt; ${trajectory.best.value}</span></dd></div>
    </dl>
  </div>
</section>`;

const judgment = ({ trajectory, demo }) => html`
<section aria-labelledby="measurement-to-judgment">
  ${sectionHeading({ id: "measurement-to-judgment", eyebrow: "From measurement to judgment", title: "An observation is not a judgment.", note: "Policies can change without falsifying history." })}
  ${demoLabel("Examples use the fictional repository from the trajectory above.")}
  <ol class="pipeline">
    <li><span class="pipeline-step">Observation</span><p>A fact measured under a versioned definition: <code>${demo.focus.metricId}</code> v${demo.focus.metricVersion} = ${trajectory.current.value} for <code>InvoiceRules.fs</code> at ${trajectory.current.sha}.</p></li>
    <li><span class="pipeline-step">Evidence</span><p>Observations held in an immutable snapshot with provenance: repository, revision, collector, configuration, time.</p></li>
    <li><span class="pipeline-step">Derived signal</span><p>A calculation over evidence: ${signed(trajectory.sincePrevious.delta)} since ${trajectory.previous.commit}; complexity plus change pressure forms a hotspot.</p></li>
    <li><span class="pipeline-step">Policy</span><p>An explicit rule applied to the evidence: ratchet at ${trajectory.best.value}, disposition ${demo.policy.ratchet.disposition}.</p></li>
    <li><span class="pipeline-step">Finding</span><p>A durable object with a stable identity, a lifecycle state, and links back to all of the above.</p></li>
  </ol>
  <div class="triad">
    <article class="triad-item">
      <p class="eyebrow">Raw observation</p>
      <h3>What was measured</h3>
      <p>Directly observed and never reinterpreted.</p>
      <ul><li>complexity = ${trajectory.current.value}</li><li>file changed ${trajectory.current.windowCommits} times in ${demo.policy.hotspot.windowDays} days</li><li>region <code>applyDiscounts</code> changed in 6 commits</li><li>collection failed at commit ${trajectory.missing[0].commit}</li></ul>
    </article>
    <article class="triad-item">
      <p class="eyebrow">Derived assessment</p>
      <h3>What the evidence implies</h3>
      <p>Calculated from observations, and decomposable back into them.</p>
      <ul><li>deteriorated since ${trajectory.previous.commit}</li><li>improved since the baseline</li><li>emerging maintainability hotspot</li><li>finding resurfaced after resolution</li></ul>
    </article>
    <article class="triad-item">
      <p class="eyebrow">Policy judgment</p>
      <h3>What the organisation decided</h3>
      <p>An explicit rule with a named disposition.</p>
      <ul><li>threshold: ${stateLabel(trajectory.threshold.kind)}</li><li>ratchet: ${stateLabel(trajectory.ratchet.kind)}</li><li>commit ${trajectory.missing[0].commit}: ${stateLabel("NotEvaluated")}</li><li>accepted exception (planned)</li></ul>
    </article>
  </div>
  <p class="section-foot"><a href="/quality-model/">Read the full quality model</a>, including measurement states, comparison compatibility, and baselines.</p>
</section>`;

const hotspotSection = ({ demo, hotspots }) => html`
<section aria-labelledby="hotspots">
  ${sectionHeading({ id: "hotspots", eyebrow: "Hotspots", title: "Complexity is a risk only where the code keeps changing.", note: "Question answered: where is risk concentrated right now?" })}
  ${demoLabel("The same fictional repository, at commit H.")}
  <div class="prose-columns">
    <p>Complex code that nobody touches is a cost you pay rarely. Simple code that changes every week is cheap to change. The files that deserve attention are complex <em>and</em> repeatedly modified. Dokimos keeps both dimensions visible instead of collapsing them into one opaque score.</p>
    <p>File-level churn is coarse: a routes file may change often because new routes are appended. When Git evidence allows, Dokimos tracks repeated changes to the same region, with a confidence value, because rewriting the same function again and again is stronger evidence than a busy file.</p>
  </div>
  ${hotspotChart({
    id: "hotspot-chart",
    files: hotspots,
    policy: demo.policy.hotspot,
    title: "Complexity against change frequency for five files",
    description: hotspots.map((file) => `${file.path}: complexity ${file.complexity}, ${file.windowCommits} commits, ${quadrantLabel(file.quadrant).toLowerCase()}`).join("; "),
  })}
  <details class="data-disclosure" open>
    <summary>Data table for this chart</summary>
    <div class="table-scroll" role="region" aria-labelledby="hotspot-table-caption" tabindex="0">
      <table class="data-table">
        <caption id="hotspot-table-caption">Structural and temporal evidence per file (demonstration data)</caption>
        <thead><tr><th scope="col">File</th><th scope="col" class="num">Complexity</th><th scope="col" class="num">Commits (${demo.policy.hotspot.windowDays} days)</th><th scope="col" class="num">Churn (lines)</th><th scope="col">Most-changed region</th><th scope="col">Classification</th></tr></thead>
        <tbody>
          ${hotspots.map(
            (file) => html`<tr>
            <th scope="row"><code class="path">${file.path}</code></th>
            <td class="num">${file.complexity}</td>
            <td class="num">${file.windowCommits}</td>
            <td class="num">${file.churn}</td>
            <td>${file.region ? html`<code>${file.region.anchor}</code>: ${file.region.commits} commits (confidence ${file.region.confidence})${file.repeatedRegion ? html` — <strong>repeated</strong>` : ""}` : "No region evidence"}</td>
            <td><strong>${quadrantLabel(file.quadrant)}</strong></td>
          </tr>`,
          )}
        </tbody>
      </table>
    </div>
  </details>
</section>`;

const lifecycleSection = ({ lifecycle, demo }) => html`
<section aria-labelledby="finding-lifecycle">
  ${sectionHeading({ id: "finding-lifecycle", eyebrow: "Findings have histories", title: "A finding is a durable record, not a console message.", note: "States are the ones defined in the Dokimos domain." })}
  ${demoLabel(`Maintainability-hotspot finding for ${demo.focus.scope}: present when complexity ≥ ${demo.policy.hotspot.minimumComplexity} and the file changed in ≥ ${demo.policy.hotspot.minimumWindowCommits} commits in ${demo.policy.hotspot.windowDays} days.`)}
  <ol class="lifecycle">
    ${lifecycle.map(
      (entry) => html`<li class="lifecycle-step">
      <p class="lifecycle-commit mono">${entry.commit} · ${entry.sha}</p>
      <p>${entry.state ? stateBadge(entry.state) : entry.presence.kind === "Unknown" ? stateBadge("NotEvaluated") : html`<span class="state tone-neutral" data-state="Observed"><span class="state-glyph" aria-hidden="true">●</span>First observed</span>`}</p>
      <p class="lifecycle-evidence">${entry.presence.kind === "Unknown"
        ? "No complexity value: no transition is recorded."
        : `Complexity ${entry.observation.value}, ${entry.observation.windowCommits} commits.`}${entry.state === null && entry.presence.kind !== "Unknown" ? " No earlier snapshot, so no transition is claimed." : ""}</p>
    </li>`,
    )}
  </ol>
  <p class="section-foot">Dokimos does not claim the finding was introduced at ${lifecycle[0].commit}: there is no earlier evidence. At ${lifecycle.find((entry) => entry.presence.kind === "Unknown")?.commit} the evidence is missing, so the state is not evaluated rather than assumed resolved.</p>
</section>`;

const explainability = ({ demo, trajectory }) => html`
<section aria-labelledby="explainability">
  ${sectionHeading({ id: "explainability", eyebrow: "Explainability", title: "Every conclusion opens down to its evidence.", note: "Open each level to follow the chain." })}
  ${demoLabel("A drill-down for the ratchet warning at commit H.")}
  <details class="drill" open>
    <summary><span class="drill-level">Summary</span> ${stateBadge(trajectory.ratchet.kind)} Ratchet warning on <code>InvoiceRules.fs</code> at ${trajectory.current.commit}</summary>
    <div class="drill-body">
      <details class="drill" open>
        <summary><span class="drill-level">Policy judgment</span> Ratchet for <code>${demo.focus.metricId}</code>, disposition ${demo.policy.ratchet.disposition}</summary>
        <div class="drill-body">
          <p>Rule: the value must not exceed the best demonstrated state (${trajectory.best.value}, commit ${trajectory.best.commit}, ${trajectory.best.sha}). Actual ${trajectory.current.value}. Result ${stateLabel(trajectory.ratchet.kind)}. The universal threshold (warn above ${demo.policy.threshold.warn}) independently evaluates to ${stateLabel(trajectory.threshold.kind)}.</p>
          <details class="drill">
            <summary><span class="drill-level">Derived assessment</span> ${signed(trajectory.sinceBest.delta)} from best, ${signed(trajectory.sincePrevious.delta)} from previous</summary>
            <div class="drill-body">
              <p>Compared only with compatible observations (same metric, definition version ${demo.focus.metricVersion}, same unit). Commit ${trajectory.missing[0].commit} is excluded because collection failed; it is not treated as zero.</p>
              <details class="drill">
                <summary><span class="drill-level">Observations</span> ${trajectory.points.length - trajectory.missing.length} available, ${trajectory.missing.length} failed</summary>
                <div class="drill-body">
                  <div class="table-scroll" role="region" aria-label="Observations supporting the ratchet warning" tabindex="0">
                    <table class="data-table evidence-table">
                      <thead><tr><th scope="col">Revision</th><th scope="col">Metric</th><th scope="col">Scope</th><th scope="col">State</th><th scope="col" class="num">Value</th><th scope="col">Collector</th></tr></thead>
                      <tbody>
                        ${[trajectory.best, trajectory.missing[0], trajectory.previous, trajectory.current].map(
                          (point) => html`<tr><th scope="row"><span class="mono">${point.commit} · ${point.sha}</span></th><td><code>${demo.focus.metricId}</code> v${demo.focus.metricVersion}</td><td><code class="path">${demo.focus.scope}</code></td><td>${stateBadge(point.value === null ? "Failed" : "Available")}</td><td class="num">${point.value ?? "—"}</td><td>${demo.collector}</td></tr>`,
                        )}
                      </tbody>
                    </table>
                  </div>
                </div>
              </details>
            </div>
          </details>
        </div>
      </details>
    </div>
  </details>
</section>`;

const dogfood = ({ snapshot, policy }) => html`
<section class="band band-real" aria-labelledby="dogfooding">
  ${sectionHeading({ id: "dogfooding", eyebrow: "Dokimos on Dokimos", title: "The first longitudinal dataset is Dokimos itself.", note: "Rendered from the accepted baseline in this repository at build time." })}
  ${realLabel(html`Snapshot of <code>${snapshot.repository}</code> at <a href="https://github.com/${snapshot.repository}/commit/${snapshot.revision}"><span class="mono">${snapshot.shortRevision}</span></a> (${snapshot.ref}), collected ${snapshot.collectedOn} by <code>${snapshot.collector}</code>.`)}
  <div class="readouts" role="list">
    <div class="readout" role="listitem"><p class="readout-label">Observations</p><p class="readout-value">${snapshot.observationCount}</p><p>${snapshot.metricCount} metrics across ${snapshot.fileCount} F# files</p></div>
    <div class="readout" role="listitem"><p class="readout-label">Findings</p><p class="readout-value">${snapshot.findings.length}</p><p>${[...new Set(snapshot.findings.map((finding) => `${finding.kind} (${finding.state})`))].join(", ")}</p></div>
    <div class="readout" role="listitem"><p class="readout-label">Ratchets</p><p class="readout-value">${policy.ratchets.length}</p><p>${policy.ratchets.map((ratchet) => `${ratchet.metricId} ≤ ${ratchet.bestAccepted}`).join("; ")}</p></div>
  </div>
  <div class="table-scroll" role="region" aria-labelledby="dogfood-caption" tabindex="0">
    <table class="data-table">
      <caption id="dogfood-caption">Findings in the accepted Dokimos baseline</caption>
      <thead><tr><th scope="col">Scope</th><th scope="col">Finding</th><th scope="col">Evidence</th></tr></thead>
      <tbody>
        ${snapshot.findings.map(
          (finding) => html`<tr><th scope="row"><code class="path">${finding.scope}</code></th><td>${finding.kind} (${finding.state})</td><td>${finding.evidence.map((item) => `${item.kind} ${item.value}`).join(", ")}</td></tr>`,
        )}
      </tbody>
    </table>
  </div>
</section>`;

const ecosystem = () => html`
<section aria-labelledby="ecosystem">
  ${sectionHeading({ id: "ecosystem", eyebrow: "Echelon ecosystem", title: "Dokimos owns quality evidence, and nothing else.", note: "Integration happens through explicit evidence boundaries." })}
  <dl class="boundary-list">
    <div class="is-self"><dt>Dokimos</dt><dd>Code-quality evidence, history, comparison, and quality-policy evaluation.</dd></div>
    <div><dt>Praxis / ROS</dt><dd>Engineering work, execution attribution, provenance, and development telemetry. Dokimos may correlate with it; it does not re-own it.</dd></div>
    <div><dt>Tutela</dt><dd>Security assurance. Security-specific analysis routes there rather than into a competing Dokimos authority.</dd></div>
    <div><dt>Aegis</dt><dd>Unexpected operational faults at Git, filesystem, process, and persistence boundaries. Expected outcomes, such as unavailable metrics, stay typed Dokimos results.</dd></div>
    <div><dt>Ordo</dt><dd>Engineering and state methodology that Dokimos's repository follows.</dd></div>
  </dl>
  <p class="section-foot">Dokimos runs on its own. When an Echelon system is absent, Dokimos records that the evidence is unavailable; it never fails or fabricates data because a neighbour is missing. <a href="/architecture/">See the architecture and boundaries</a>.</p>
</section>`;

const start = ({ site }) => html`
<section class="closing" aria-labelledby="start">
  <p class="eyebrow">Start using Dokimos</p>
  <h2 id="start">Measure a repository from source today.</h2>
  <p>Dokimos is built from source with the .NET 8 SDK. The CLI is deterministic and emits schema-versioned JSON.</p>
  <pre class="code-block" tabindex="0"><code>git clone ${site.repository}.git
cd dokimos
dotnet build Dokimos.sln
git log --numstat --find-renames --format='commit %H %aI' -- src &gt; history.txt
dotnet run --project src/Dokimos.Cli -- snapshot src --git-history history.txt \\
  --repository owner/repo --revision "$(git rev-parse HEAD)" --ref main</code></pre>
  <div class="actions">
    <a class="button primary" href="/how-it-works/">See how an analysis runs</a>
    <a class="button" href="${site.repository}">Read the source on GitHub</a>
  </div>
</section>`;

export const render = (context) => html`
${hero(context)}
${problem(context)}
${sees(context)}
${overTime(context)}
${judgment(context)}
${hotspotSection(context)}
${lifecycleSection(context)}
${explainability(context)}
${dogfood(context)}
${ecosystem(context)}
${start(context)}`;
