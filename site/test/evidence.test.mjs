import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";

import { validateCatalog, validateSnapshot, validateStatus, validatePolicy, dimensionsWithStatus, catalogRows } from "../lib/evidence.mjs";

const readJson = (relative) => JSON.parse(fs.readFileSync(new URL(relative, import.meta.url), "utf8"));
const catalog = readJson("../../config/metric-catalog.json");
const snapshot = readJson("../../baselines/accepted-snapshot.json");
const policy = readJson("../../config/dokimos-policy.json");
const status = readJson("../data/measurement-status.json");

test("repository evidence validates", () => {
  assert.deepEqual(validateCatalog(catalog), []);
  assert.deepEqual(validateSnapshot(snapshot), []);
  assert.deepEqual(validatePolicy(policy, catalog), []);
  assert.deepEqual(validateStatus(status, catalog, snapshot), []);
});

test("a measurement claimed as implemented must appear in the accepted baseline", () => {
  const claimed = structuredClone(status);
  claimed.measurements.find((entry) => entry.id === "duplication.block-occurrences").status = "implemented";
  assert.ok(validateStatus(claimed, catalog, snapshot).some((line) => /absent from the accepted baseline/.test(line)));
});

test("every catalog metric needs a site status", () => {
  const missing = structuredClone(status);
  missing.measurements = missing.measurements.filter((entry) => entry.id !== "api.added");
  assert.ok(validateStatus(missing, catalog, snapshot).some((line) => /api.added has no site status/.test(line)));
});

test("planned measurements cannot cite an implementation", () => {
  const cited = structuredClone(status);
  cited.measurements.find((entry) => entry.status === "planned").source = "src/Dokimos.Core/Complexity.fs";
  assert.ok(validateStatus(cited, catalog, snapshot).some((line) => /planned measurements must not cite/.test(line)));
});

test("a failed snapshot state must not carry a fabricated pass", () => {
  const broken = structuredClone(snapshot);
  broken.Metrics[0].State = "good";
  assert.ok(validateSnapshot(broken).some((line) => /unknown state/.test(line)));
});

test("dimension status is derived from its measurements, never asserted", () => {
  const dimensions = dimensionsWithStatus(status);
  const find = (name) => dimensions.find((dimension) => dimension.name === name);
  assert.equal(find("Structural complexity").status, "implemented");
  assert.equal(find("Duplication").status, "experimental");
  assert.equal(find("Coupling and cohesion").status, "planned");
});

test("catalog rows keep catalog definitions and flag uncatalogued measurements", () => {
  const rows = catalogRows(catalog, status);
  assert.equal(rows.filter((row) => row.catalogued).length, catalog.metrics.length);
  assert.ok(rows.some((row) => row.id === "quality.type-weakening-indicators" && !row.catalogued));
});
