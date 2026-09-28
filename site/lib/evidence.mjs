// Real Dokimos evidence consumed by the site: the metric catalog, the accepted
// self-analysis baseline, the accepted policy, and the site's measurement-status
// claims. Validation fails the build when the site's claims drift from evidence.

const STATUSES = ["implemented", "experimental", "planned"];
const CLASSES = ["observation", "derived"];
const RANK = { implemented: 0, experimental: 1, planned: 2 };

const isNonEmptyString = (value) => typeof value === "string" && value.trim().length > 0;
const check = (condition, message) => (condition ? [] : [message]);
const duplicates = (values) => values.filter((value, index) => values.indexOf(value) !== index);

export const validateCatalog = (catalog) =>
  !Array.isArray(catalog?.metrics)
    ? ["config/metric-catalog.json: expected a metrics array"]
    : [
        ...catalog.metrics.flatMap((metric, index) => [
          ...check(isNonEmptyString(metric?.id), `metric-catalog.metrics[${index}].id: required`),
          ...check(Number.isInteger(metric?.version) && metric.version >= 1, `metric-catalog.metrics[${index}].version: expected an integer >= 1`),
          ...check(isNonEmptyString(metric?.definition), `metric-catalog.metrics[${index}].definition: required`),
          ...check(isNonEmptyString(metric?.unit), `metric-catalog.metrics[${index}].unit: required`),
        ]),
        ...duplicates(catalog.metrics.map((metric) => metric?.id)).map((id) => `metric-catalog: duplicate id ${id}`),
      ];

export const validateSnapshot = (snapshot) => [
  ...check(snapshot?.SchemaVersion === "1.0.0", "baselines/accepted-snapshot.json: expected SchemaVersion 1.0.0"),
  ...check(/^[0-9a-f]{40}$/.test(snapshot?.Revision ?? ""), "baselines/accepted-snapshot.json: Revision must be a full commit SHA"),
  ...check(isNonEmptyString(snapshot?.Repository), "baselines/accepted-snapshot.json: Repository required"),
  ...check(!Number.isNaN(Date.parse(snapshot?.CollectedAt ?? "")), "baselines/accepted-snapshot.json: CollectedAt must be a timestamp"),
  ...check(Array.isArray(snapshot?.Metrics) && snapshot.Metrics.length > 0, "baselines/accepted-snapshot.json: Metrics must be a non-empty array"),
  ...check(Array.isArray(snapshot?.Findings), "baselines/accepted-snapshot.json: Findings must be an array"),
  ...(Array.isArray(snapshot?.Metrics)
    ? snapshot.Metrics.flatMap((metric, index) =>
        ["available", "unavailable", "failed"].includes(metric?.State)
          ? check(metric.State !== "available" || typeof metric.Value === "number", `accepted-snapshot.Metrics[${index}]: available metrics require a numeric Value`)
          : [`accepted-snapshot.Metrics[${index}].State: unknown state ${metric?.State}`],
      )
    : []),
];

// Identifiers the accepted snapshot proves are produced today.
export const snapshotEvidenceIds = (snapshot) =>
  new Set([
    ...snapshot.Metrics.map((metric) => metric.MetricId),
    ...snapshot.Findings.map((finding) => `correlation.${finding.Kind}`),
  ]);

export const validateStatus = (status, catalog, snapshot) => {
  const catalogIds = new Set(catalog.metrics.map((metric) => metric.id));
  const evidenceIds = snapshotEvidenceIds(snapshot);
  const entries = Array.isArray(status?.measurements) ? status.measurements : [];
  const statusIds = new Set(entries.map((entry) => entry?.id));
  return [
    ...check(entries.length > 0, "measurement-status: measurements required"),
    ...check(STATUSES.every((key) => isNonEmptyString(status?.statusCriteria?.[key])), "measurement-status.statusCriteria: every status needs published criteria"),
    ...duplicates(entries.map((entry) => entry?.id)).map((id) => `measurement-status: duplicate id ${id}`),
    ...[...catalogIds].filter((id) => !statusIds.has(id)).map((id) => `measurement-status: catalog metric ${id} has no site status`),
    ...[...evidenceIds].filter((id) => !statusIds.has(id)).map((id) => `measurement-status: snapshot evidence ${id} has no site status`),
    ...entries.flatMap((entry) => {
      const at = `measurement-status.${entry?.id}`;
      return [
        ...check(STATUSES.includes(entry?.status), `${at}.status: expected one of ${STATUSES.join(", ")}`),
        ...check(CLASSES.includes(entry?.class), `${at}.class: expected one of ${CLASSES.join(", ")}`),
        ...check(isNonEmptyString(entry?.note), `${at}.note: required`),
        ...check(entry?.catalog === catalogIds.has(entry?.id), `${at}.catalog: says ${entry?.catalog} but the catalog ${catalogIds.has(entry?.id) ? "contains" : "does not contain"} it`),
        ...check(entry?.status !== "implemented" || evidenceIds.has(entry?.id), `${at}: marked implemented but absent from the accepted baseline snapshot`),
        ...check(entry?.status !== "planned" || entry?.source === null, `${at}: planned measurements must not cite an implementation source`),
        ...check(entry?.status === "planned" || isNonEmptyString(entry?.source), `${at}.source: implemented and experimental measurements must cite their source`),
      ];
    }),
    ...(Array.isArray(status?.dimensions) && status.dimensions.length > 0
      ? status.dimensions.flatMap((dimension, index) => [
          ...check(isNonEmptyString(dimension?.name) && isNonEmptyString(dimension?.question), `measurement-status.dimensions[${index}]: name and question required`),
          ...check(Array.isArray(dimension?.measurements) && dimension.measurements.length > 0, `measurement-status.dimensions[${index}].measurements: required`),
          ...(dimension?.measurements ?? []).filter((id) => !statusIds.has(id)).map((id) => `measurement-status.dimensions[${index}]: unknown measurement ${id}`),
        ])
      : ["measurement-status.dimensions: required"]),
  ];
};

export const validatePolicy = (policy, catalog) => {
  const catalogIds = new Set(catalog.metrics.map((metric) => metric.id));
  return [
    ...check(isNonEmptyString(policy?.baseline), "config/dokimos-policy.json: baseline required"),
    ...check(Array.isArray(policy?.ratchets), "config/dokimos-policy.json: ratchets required"),
    ...(policy?.ratchets ?? []).flatMap((ratchet, index) =>
      check(catalogIds.has(ratchet?.metricId), `dokimos-policy.ratchets[${index}]: ${ratchet?.metricId} is not in the metric catalog`),
    ),
  ];
};

// ---------------------------------------------------------------------------
// Pure derivations

const byId = (status) => new Map(status.measurements.map((entry) => [entry.id, entry]));

export const dimensionsWithStatus = (status) => {
  const index = byId(status);
  return status.dimensions.map((dimension) => {
    const members = dimension.measurements.map((id) => index.get(id));
    const best = members.reduce((acc, member) => (RANK[member.status] < RANK[acc] ? member.status : acc), "planned");
    return { ...dimension, members, status: best };
  });
};

export const catalogRows = (catalog, status) => {
  const index = byId(status);
  const catalogIds = new Set(catalog.metrics.map((metric) => metric.id));
  const fromCatalog = catalog.metrics.map((metric) => ({ ...metric, ...index.get(metric.id), catalogued: true }));
  const uncatalogued = status.measurements
    .filter((entry) => !catalogIds.has(entry.id))
    .map((entry) => ({ ...entry, catalogued: false }));
  return [...fromCatalog, ...uncatalogued].toSorted(
    (a, b) => RANK[a.status] - RANK[b.status] || a.id.localeCompare(b.id),
  );
};

export const snapshotSummary = (snapshot) => {
  const complexity = snapshot.Metrics.filter((metric) => metric.MetricId === "complexity.proxy-cyclomatic" && metric.State === "available");
  const scopes = new Set(snapshot.Metrics.map((metric) => metric.Scope));
  const states = snapshot.Metrics.reduce((acc, metric) => ({ ...acc, [metric.State]: (acc[metric.State] ?? 0) + 1 }), {});
  return {
    repository: snapshot.Repository,
    revision: snapshot.Revision,
    shortRevision: snapshot.Revision.slice(0, 7),
    ref: snapshot.Ref,
    collectedAt: snapshot.CollectedAt,
    collectedOn: snapshot.CollectedAt.slice(0, 10),
    collector: snapshot.Collector,
    observationCount: snapshot.Metrics.length,
    fileCount: scopes.size,
    metricCount: new Set(snapshot.Metrics.map((metric) => metric.MetricId)).size,
    states,
    topComplexity: complexity
      .toSorted((a, b) => b.Value - a.Value || a.Scope.localeCompare(b.Scope))
      .slice(0, 5)
      .map((metric) => ({ scope: metric.Scope, value: metric.Value })),
    findings: snapshot.Findings.map((finding) => ({
      id: finding.FindingId,
      scope: finding.Scope,
      kind: finding.Kind,
      state: finding.State,
      explanation: finding.Explanation,
      evidence: finding.Evidence.map((item) => ({ kind: item.Kind, value: item.Value })),
    })),
  };
};
