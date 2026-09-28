// Demonstration dataset: validation and pure derivations.
//
// Derivations deliberately mirror the Dokimos F# domain so the site cannot
// illustrate behaviour the implementation does not have:
//   Policy.evaluateThreshold / Policy.evaluateRatchet  -> evaluateThreshold / evaluateRatchet
//   Trend.evaluate                                      -> direction
//   Findings.transition / Findings.resurfaced           -> findingLifecycle

const MEASUREMENT_STATES = ["available", "unavailable", "failed"];
const PREFERENCES = ["lower-is-better", "higher-is-better"];
const DISPOSITIONS = ["ObserveOnly", "Warn", "Fail"];

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);
const isNonEmptyString = (value) => typeof value === "string" && value.trim().length > 0;
const isCount = (value) => Number.isInteger(value) && value >= 0;
const isMeasure = (value) => typeof value === "number" && Number.isFinite(value) && value >= 0;
const duplicates = (values) => values.filter((value, index) => values.indexOf(value) !== index);
const check = (condition, message) => (condition ? [] : [message]);

const validateCommits = (commits) =>
  !Array.isArray(commits) || commits.length < 2
    ? ["commits: expected at least two commits"]
    : [
        ...commits.flatMap((commit, index) => [
          ...check(isNonEmptyString(commit?.id), `commits[${index}].id: required`),
          ...check(/^[0-9a-f]{7}$/.test(commit?.sha ?? ""), `commits[${index}].sha: expected 7 lowercase hex characters`),
          ...check(/^\d{4}-\d{2}-\d{2}$/.test(commit?.date ?? ""), `commits[${index}].date: expected YYYY-MM-DD`),
          ...check(isNonEmptyString(commit?.summary), `commits[${index}].summary: required`),
          ...check(index === 0 || (commits[index - 1]?.date ?? "") <= (commit?.date ?? ""), `commits[${index}].date: commits must be in chronological order`),
        ]),
        ...duplicates(commits.map((commit) => commit?.id)).map((id) => `commits: duplicate id ${id}`),
      ];

const validateObservation = (observation, index, commitIds) => {
  const at = `focus.observations[${index}]`;
  const state = observation?.state;
  return [
    ...check(observation?.commit === commitIds[index], `${at}.commit: expected ${commitIds[index]} (one observation per commit, in order)`),
    ...check(MEASUREMENT_STATES.includes(state), `${at}.state: expected one of ${MEASUREMENT_STATES.join(", ")}`),
    ...check(isCount(observation?.windowCommits), `${at}.windowCommits: expected a non-negative integer`),
    ...(state === "available"
      ? check(isMeasure(observation?.value), `${at}.value: available observations require a finite non-negative value`)
      : [
          ...check(!("value" in (observation ?? {})), `${at}.value: ${state} observations must not carry a value (unknown is not zero)`),
          ...check(isNonEmptyString(observation?.reason), `${at}.reason: ${state} observations must explain why`),
        ]),
  ];
};

const validateFocus = (focus, commitIds, knownMetric) =>
  !isObject(focus)
    ? ["focus: required object"]
    : [
        ...check(isNonEmptyString(focus.scope), "focus.scope: required"),
        ...check(knownMetric(focus.metricId, focus.metricVersion), `focus.metricId: ${focus.metricId} v${focus.metricVersion} is not in config/metric-catalog.json`),
        ...check(isNonEmptyString(focus.unit), "focus.unit: required"),
        ...check(commitIds.includes(focus.baselineCommit), "focus.baselineCommit: must name a commit"),
        ...(Array.isArray(focus.observations) && focus.observations.length === commitIds.length
          ? [
              ...focus.observations.flatMap((observation, index) => validateObservation(observation, index, commitIds)),
              ...check(
                focus.observations.find((observation) => observation?.commit === focus.baselineCommit)?.state === "available",
                "focus.baselineCommit: the baseline observation must be available",
              ),
              ...check(focus.observations.at(-1)?.state === "available", "focus.observations: the current (last) observation must be available"),
            ]
          : ["focus.observations: expected exactly one observation per commit"]),
      ];

const validatePolicy = (policy) =>
  !isObject(policy)
    ? ["policy: required object"]
    : [
        ...check(isMeasure(policy.threshold?.warn) && isMeasure(policy.threshold?.fail), "policy.threshold: warn and fail must be numbers"),
        ...check((policy.threshold?.warn ?? 0) < (policy.threshold?.fail ?? 0), "policy.threshold: warn must be below fail"),
        ...check(DISPOSITIONS.includes(policy.ratchet?.disposition), `policy.ratchet.disposition: expected one of ${DISPOSITIONS.join(", ")}`),
        ...check(PREFERENCES.includes(policy.ratchet?.preference), `policy.ratchet.preference: expected one of ${PREFERENCES.join(", ")} (direction is a policy declaration, not a metric property)`),
        ...["minimumComplexity", "minimumWindowCommits", "minimumRegionCommits", "windowDays"].flatMap((key) =>
          check(isCount(policy.hotspot?.[key]) && policy.hotspot[key] > 0, `policy.hotspot.${key}: expected a positive integer`),
        ),
      ];

const validateFiles = (files) =>
  !Array.isArray(files) || files.length === 0
    ? ["files: expected at least one file"]
    : [
        ...files.flatMap((file, index) => [
          ...check(isNonEmptyString(file?.path), `files[${index}].path: required`),
          ...check(isMeasure(file?.complexity), `files[${index}].complexity: expected a non-negative number`),
          ...check(isCount(file?.windowCommits), `files[${index}].windowCommits: expected a non-negative integer`),
          ...check(isCount(file?.churn), `files[${index}].churn: expected a non-negative integer`),
          ...(file?.region === null
            ? []
            : [
                ...check(isNonEmptyString(file?.region?.anchor), `files[${index}].region.anchor: required when region is present`),
                ...check(isCount(file?.region?.commits) && file.region.commits <= (file?.windowCommits ?? -1), `files[${index}].region.commits: must be an integer no larger than windowCommits`),
                ...check(typeof file?.region?.confidence === "number" && file.region.confidence > 0 && file.region.confidence <= 1, `files[${index}].region.confidence: expected (0, 1]`),
              ]),
        ]),
        ...duplicates(files.map((file) => file?.path)).map((path) => `files: duplicate path ${path}`),
      ];

const validateConsistency = (data) => {
  const current = data.focus?.observations?.at(-1);
  const file = Array.isArray(data.files) ? data.files.find((candidate) => candidate?.path === data.focus?.scope) : undefined;
  return file === undefined
    ? ["files: must include the focus scope so the hotspot view and the trajectory describe the same system"]
    : [
        ...check(file.complexity === current?.value, "files: focus file complexity must equal the current focus observation"),
        ...check(file.windowCommits === current?.windowCommits, "files: focus file windowCommits must equal the current focus observation"),
      ];
};

// Returns a list of diagnostics. An empty list means the data is valid.
export const validateDemonstration = (data, knownMetric) =>
  !isObject(data)
    ? ["demonstration: expected a JSON object"]
    : (() => {
        const structural = [
          ...check(data.schemaVersion === "1.0.0", "schemaVersion: expected 1.0.0"),
          ...check(data.kind === "demonstration", "kind: must be \"demonstration\" so the data can never be mistaken for real measurements"),
          ...check(isNonEmptyString(data.disclaimer) && /demonstration/i.test(data.disclaimer), "disclaimer: must state that the data is demonstration data"),
          ...check(isNonEmptyString(data.repository), "repository: required"),
          ...check(isNonEmptyString(data.collector), "collector: required"),
          ...validateCommits(data.commits),
        ];
        const commitIds = Array.isArray(data.commits) ? data.commits.map((commit) => commit?.id) : [];
        const detailed = [...validateFocus(data.focus, commitIds, knownMetric), ...validatePolicy(data.policy), ...validateFiles(data.files)];
        return structural.length + detailed.length > 0 ? [...structural, ...detailed] : validateConsistency(data);
      })();

// ---------------------------------------------------------------------------
// Pure derivations

const worse = (preference) => (actual, limit) => (preference === "higher-is-better" ? actual < limit : actual > limit);

const valueOf = (observation) => (observation?.state === "available" ? observation.value : null);

const notEvaluated = (observation) => ({
  kind: "NotEvaluated",
  reason: observation?.state === "failed" ? `Collection failed: ${observation.reason}` : "Metric unavailable.",
});

// Mirrors Policy.evaluateThreshold, with the demonstration policy expressing warn and fail limits.
export const evaluateThreshold = (threshold, preference, observation) => {
  const actual = valueOf(observation);
  const exceeds = worse(preference);
  return actual === null
    ? notEvaluated(observation)
    : exceeds(actual, threshold.fail)
      ? { kind: "Failure", actual, limit: threshold.fail }
      : exceeds(actual, threshold.warn)
        ? { kind: "Warning", actual, limit: threshold.warn }
        : { kind: "Pass", actual, limit: threshold.warn };
};

// Mirrors Policy.evaluateRatchet.
export const evaluateRatchet = (bestAccepted, preference, disposition, observation) => {
  const actual = valueOf(observation);
  return actual === null
    ? notEvaluated(observation)
    : !worse(preference)(actual, bestAccepted) || disposition === "ObserveOnly"
      ? { kind: "Pass", actual, limit: bestAccepted }
      : { kind: disposition === "Fail" ? "Failure" : "Warning", actual, limit: bestAccepted };
};

// Mirrors Trend.evaluate: a missing side makes the comparison NotComparable, never zero.
export const direction = (preference, before, after) =>
  before === null || after === null
    ? "NotComparable"
    : after === before
      ? "Unchanged"
      : worse(preference)(after, before)
        ? "Deteriorated"
        : "Improved";

const bestOf = (preference, values) =>
  values.reduce((best, value) => (best === null || worse(preference)(best, value) ? value : best), null);

export const trajectory = (data) => {
  const { focus, commits } = data;
  const { preference } = data.policy.ratchet;
  const points = focus.observations.map((observation, index) => ({
    ...observation,
    sha: commits[index].sha,
    date: commits[index].date,
    summary: commits[index].summary,
    value: valueOf(observation),
  }));
  const current = points.at(-1);
  const previous = points.slice(0, -1).findLast((point) => point.value !== null) ?? null;
  const baseline = points.find((point) => point.commit === focus.baselineCommit);
  const bestValue = bestOf(preference, points.map((point) => point.value).filter((value) => value !== null));
  const best = points.find((point) => point.value === bestValue);
  const threshold = evaluateThreshold(data.policy.threshold, preference, current);
  const ratchet = evaluateRatchet(best.value, preference, data.policy.ratchet.disposition, current);
  return {
    points,
    current,
    previous,
    baseline,
    best,
    threshold,
    ratchet,
    sincePrevious: { delta: previous ? current.value - previous.value : null, direction: direction(preference, previous?.value ?? null, current.value) },
    sinceBaseline: { delta: current.value - baseline.value, direction: direction(preference, baseline.value, current.value) },
    sinceBest: { delta: current.value - best.value, direction: direction(preference, best.value, current.value) },
    missing: points.filter((point) => point.value === null),
  };
};

// Finding presence for the demonstration maintainability-hotspot rule.
export const presence = (hotspotPolicy, observation) =>
  valueOf(observation) === null
    ? { kind: "Unknown" }
    : observation.value >= hotspotPolicy.minimumComplexity && observation.windowCommits >= hotspotPolicy.minimumWindowCommits
      ? { kind: "Present", magnitude: observation.value }
      : { kind: "Absent" };

// Mirrors Findings.transition.
export const transition = (previous, current) =>
  previous.kind === "Unknown" || current.kind === "Unknown"
    ? null
    : previous.kind === "Absent" && current.kind === "Present"
      ? "Introduced"
      : previous.kind === "Present" && current.kind === "Absent"
        ? "Resolved"
        : previous.kind === "Absent"
          ? null
          : current.magnitude < previous.magnitude
            ? "Improved"
            : current.magnitude > previous.magnitude
              ? "Regressed"
              : "Persistent";

// Lifecycle across the whole history. Resurfaced (Findings.resurfaced) applies to
// the first presence after a resolution; unknown evidence produces no transition.
export const findingLifecycle = (data) =>
  data.focus.observations.reduce(
    (acc, observation, index) => {
      const current = presence(data.policy.hotspot, observation);
      const previous = index === 0 ? { kind: "Unknown" } : acc.previous;
      const resurfaces = acc.resolved && current.kind === "Present";
      const state = resurfaces ? "Resurfaced" : transition(previous, current);
      const entry = { commit: observation.commit, sha: data.commits[index].sha, presence: current, state, observation };
      return {
        entries: [...acc.entries, entry],
        previous: current,
        resolved: state === "Resolved" ? true : resurfaces ? false : acc.resolved,
      };
    },
    { entries: [], previous: { kind: "Unknown" }, resolved: false },
  ).entries;

export const classifyFile = (hotspotPolicy, file) => {
  const complex = file.complexity >= hotspotPolicy.minimumComplexity;
  const active = file.windowCommits >= hotspotPolicy.minimumWindowCommits;
  const repeatedRegion = file.region !== null && file.region.commits >= hotspotPolicy.minimumRegionCommits;
  const quadrant = complex && active ? "hotspot" : complex ? "complex-stable" : active ? "active-simple" : "quiet";
  return { ...file, complex, active, repeatedRegion, quadrant };
};

export const hotspots = (data) =>
  data.files
    .map((file) => classifyFile(data.policy.hotspot, file))
    .toSorted((a, b) => b.complexity * b.windowCommits - a.complexity * a.windowCommits || a.path.localeCompare(b.path));
