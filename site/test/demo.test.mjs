import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";

import { validateDemonstration, trajectory, findingLifecycle, hotspots, transition, evaluateRatchet, evaluateThreshold, direction } from "../lib/demo.mjs";

const readJson = (relative) => JSON.parse(fs.readFileSync(new URL(relative, import.meta.url), "utf8"));
const demo = readJson("../data/demonstration.json");
const catalog = readJson("../../config/metric-catalog.json");
const knownMetric = (id, version) => catalog.metrics.some((metric) => metric.id === id && metric.version === version);
const clone = (value) => structuredClone(value);
const withChange = (change) => {
  const copy = clone(demo);
  change(copy);
  return copy;
};

test("the demonstration data conforms to its schema", () => {
  assert.deepEqual(validateDemonstration(demo, knownMetric), []);
});

const malformed = [
  ["an unavailable observation carrying a value", (d) => Object.assign(d.focus.observations[4], { value: 0 }), /unknown is not zero/],
  ["a failed observation without a reason", (d) => delete d.focus.observations[4].reason, /must explain why/],
  ["an available observation without a value", (d) => delete d.focus.observations[2].value, /require a finite non-negative value/],
  ["data not declared as demonstration", (d) => (d.kind = "measurement"), /kind/],
  ["a disclaimer that does not say demonstration", (d) => (d.disclaimer = "Sample."), /disclaimer/],
  ["a metric that is not in the catalog", (d) => (d.focus.metricId = "complexity.invented"), /not in config\/metric-catalog.json/],
  ["a missing observation for a commit", (d) => d.focus.observations.pop(), /exactly one observation per commit/],
  ["commits out of order", (d) => (d.commits[3].date = "2025-01-01"), /chronological/],
  ["a hotspot file inconsistent with the trajectory", (d) => (d.files[0].complexity = 12), /describe the same system|must equal/],
  ["a region changed more often than its file", (d) => (d.files[0].region.commits = 99), /no larger than windowCommits/],
  ["an unknown measurement state", (d) => (d.focus.observations[1].state = "good"), /state/],
];

for (const [name, change, pattern] of malformed) {
  test(`validation rejects ${name}`, () => {
    const diagnostics = validateDemonstration(withChange(change), knownMetric);
    assert.ok(diagnostics.length > 0, "expected diagnostics");
    assert.ok(diagnostics.some((line) => pattern.test(line)), diagnostics.join("\n"));
  });
}

test("validation rejects a non-object document", () => {
  assert.deepEqual(validateDemonstration(null, knownMetric), ["demonstration: expected a JSON object"]);
});

test("trajectory derives baseline, best, current, and policy results", () => {
  const t = trajectory(demo);
  assert.equal(t.baseline.value, 42);
  assert.equal(t.best.value, 31);
  assert.equal(t.best.commit, "D");
  assert.equal(t.current.value, 37);
  assert.equal(t.previous.commit, "G");
  assert.deepEqual(t.threshold, { kind: "Pass", actual: 37, limit: 45 });
  assert.deepEqual(t.ratchet, { kind: "Warning", actual: 37, limit: 31 });
  assert.deepEqual(t.sinceBest, { delta: 6, direction: "Deteriorated" });
  assert.deepEqual(t.sinceBaseline, { delta: -5, direction: "Improved" });
  assert.deepEqual(t.missing.map((point) => point.commit), ["E"]);
});

test("missing evidence is never evaluated as passing (Policy.fs semantics)", () => {
  const failed = { state: "failed", reason: "boom" };
  assert.equal(evaluateRatchet(31, "lower-is-better", "Fail", failed).kind, "NotEvaluated");
  assert.equal(evaluateThreshold({ warn: 45, fail: 60 }, "lower-is-better", { state: "unavailable", reason: "x" }).kind, "NotEvaluated");
  assert.equal(direction("lower-is-better", null, 3), "NotComparable");
});

test("ratchet dispositions mirror Policy.evaluateRatchet", () => {
  const observation = { state: "available", value: 37 };
  assert.equal(evaluateRatchet(31, "lower-is-better", "ObserveOnly", observation).kind, "Pass");
  assert.equal(evaluateRatchet(31, "lower-is-better", "Warn", observation).kind, "Warning");
  assert.equal(evaluateRatchet(31, "lower-is-better", "Fail", observation).kind, "Failure");
  assert.equal(evaluateRatchet(40, "higher-is-better", "Fail", observation).kind, "Failure");
  assert.equal(evaluateRatchet(37, "lower-is-better", "Fail", observation).kind, "Pass");
});

test("finding transitions mirror Findings.transition", () => {
  const present = (magnitude) => ({ kind: "Present", magnitude });
  const absent = { kind: "Absent" };
  const unknown = { kind: "Unknown" };
  assert.equal(transition(absent, present(3)), "Introduced");
  assert.equal(transition(present(3), absent), "Resolved");
  assert.equal(transition(present(3), present(2)), "Improved");
  assert.equal(transition(present(3), present(4)), "Regressed");
  assert.equal(transition(present(3), present(3)), "Persistent");
  assert.equal(transition(absent, absent), null);
  assert.equal(transition(unknown, present(3)), null);
  assert.equal(transition(present(3), unknown), null);
});

test("the demonstration finding lifecycle uses only domain states", () => {
  const states = findingLifecycle(demo).map((entry) => entry.state);
  assert.deepEqual(states, [null, "Improved", "Improved", "Resolved", null, "Resurfaced", "Persistent", "Regressed"]);
});

test("hotspot classification separates complexity from change frequency", () => {
  const byPath = Object.fromEntries(hotspots(demo).map((file) => [file.path, file]));
  assert.equal(byPath["src/Billing/InvoiceRules.fs"].quadrant, "hotspot");
  assert.equal(byPath["src/Billing/InvoiceRules.fs"].repeatedRegion, true);
  assert.equal(byPath["src/Billing/TaxTables.fs"].quadrant, "complex-stable");
  assert.equal(byPath["src/Api/Routes.fs"].quadrant, "active-simple");
  assert.equal(byPath["src/Api/Routes.fs"].repeatedRegion, false);
  assert.equal(byPath["src/Core/Money.fs"].quadrant, "quiet");
});
