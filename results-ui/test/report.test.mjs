import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { report, integrityStatus } from "../report.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const load = (name) => JSON.parse(fs.readFileSync(path.join(here, "fixtures", name), "utf8"));
const text = (h) => h.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");

for (const name of ["regression-results.json", "self-results.json"]) {
  const results = load(name);
  const doc = report(results);

  test(`${name}: report is one Folio document with every section`, () => {
    assert.match(doc, /<ef-print-document data-document-kind="dokimos-quality-report">/);
    for (const id of ["summary", "outcomes", "changes", "hotspots", "findings", "trends", "provenance"]) {
      assert.match(doc, new RegExp(`<ef-print-section id="${id}"`));
    }
    assert.doesNotMatch(doc, /undefined|NaN|\[object Object\]/);
  });

  test(`${name}: provenance and unavailable evidence are carried into print`, () => {
    const t = text(doc);
    assert.ok(t.includes(results.Provenance.Revision));
    assert.ok(t.includes(results.Provenance.Policy.Identity));
    assert.match(t, new RegExp(`Unavailable evidence \\(${results.Unavailable.length}\\)`));
  });

  test(`${name}: every hotspot prints its signals and dimensions`, () => {
    const t = text(doc);
    for (const h of results.Hotspots) assert.ok(t.includes(h.Dimensions.join(", ")), h.FindingId);
  });
}

test("integrity status mirrors the contract's own evidence states", () => {
  const base = load("self-results.json");
  const overview = (o) => ({ ...base, Overview: { ...base.Overview, ...o } });
  assert.equal(integrityStatus(overview({ Disposition: "passed", MetricsUnavailable: 0, MetricsFailed: 0 })), "complete");
  assert.equal(integrityStatus(overview({ Disposition: "passed", MetricsUnavailable: 3 })), "partial");
  assert.equal(integrityStatus(overview({ Disposition: "required-evidence-unavailable" })), "insufficient");
  assert.equal(integrityStatus(overview({ Disposition: "incompatible-snapshots" })), "not-comparable");
});
