import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { render, PAGES, measurementText, assertContract } from "../render.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const load = (name) => JSON.parse(fs.readFileSync(path.join(here, "fixtures", name), "utf8"));
const fixtures = { regression: load("regression-results.json"), self: load("self-results.json") };
const page = (pages, file) => pages.find((p) => p.file === file).html;
const text = (htmlText) => htmlText.replace(/<[^>]+>/g, " ").replace(/\s+/g, " ");

for (const [name, results] of Object.entries(fixtures)) {
  const pages = render(results);

  test(`${name}: renders every page with navigation marking the current page`, () => {
    assert.deepEqual(pages.map((p) => p.file), PAGES.map((p) => p.file));
    for (const p of pages) {
      assert.match(p.html, new RegExp(`href="${p.file}" aria-current="page"`));
      assert.match(p.html, /<main class="ef-navigation-shell__content" id="content">/);
      assert.doesNotMatch(p.html, /undefined|NaN|\[object Object\]/);
    }
  });

  test(`${name}: overview states the disposition and exit code from the contract`, () => {
    const overview = text(page(pages, "index.html"));
    assert.match(overview, new RegExp(`Exit code ${results.Overview.ExitCode}\\.`));
    const notPassed = results.Outcomes.filter((o) => o.State !== "passed").length;
    assert.match(overview, new RegExp(`Outcomes that did not pass \\(${notPassed}\\)`));
  });

  test(`${name}: every chart has a table carrying the same points`, () => {
    const trends = page(pages, "trends.html");
    const charts = (trends.match(/class="dokimos-sparkline"/g) ?? []).length;
    const neverAvailable = results.Trends.filter((s) => s.Points.every((p) => p.Measurement.State !== "available")).length;
    assert.equal(charts + neverAvailable, results.Trends.length);
    const tables = (trends.match(/every stored point<\/caption>/g) ?? []).length;
    assert.equal(tables, results.Trends.length);
  });

  test(`${name}: every unavailable observation is explained, never shown as zero`, () => {
    const provenance = page(pages, "provenance.html");
    assert.match(text(provenance), new RegExp(`Unavailable evidence \\(${results.Unavailable.length}\\)`));
    for (const u of results.Unavailable) assert.ok(provenance.includes(u.Explanation.replaceAll("'", "&#39;")), u.Subject);
  });

  test(`${name}: each hotspot shows its dimensions and signal values`, () => {
    const hotspots = text(page(pages, "hotspots.html"));
    for (const h of results.Hotspots) {
      assert.ok(hotspots.includes(`Dimensions: ${h.Dimensions.join(", ")}.`), h.FindingId);
      for (const s of h.Signals) assert.ok(hotspots.includes(s.Kind), s.Kind);
    }
  });
}

test("regressions list baseline and current values exactly as reported", () => {
  const changes = text(page(render(fixtures.regression), "changes.html"));
  assert.ok(fixtures.regression.Regressions.length > 0);
  for (const r of fixtures.regression.Regressions) {
    assert.ok(changes.includes(`${r.MetricId} ${r.Scope} ${measurementText(r.Before)} ${measurementText(r.After)}`), r.MetricId);
  }
});

test("measurement text never turns unavailable or failed evidence into a number", () => {
  assert.equal(measurementText({ State: "available", Value: 0 }, "count"), "0 count");
  assert.equal(measurementText({ State: "unavailable", Value: null, Reason: "not-configured", Detail: null }), "unavailable (not-configured)");
  assert.equal(measurementText({ State: "failed", Value: null, Reason: "trx-malformed", Detail: "bad" }), "failed (trx-malformed: bad)");
  assert.equal(measurementText(null), "absent");
});

test("unsupported contract versions are rejected before rendering", () => {
  assert.throws(() => assertContract({ ...fixtures.self, SchemaVersion: "9.0.0" }), /unsupported dokimos.results schema 9.0.0/);
  assert.throws(() => assertContract({ ...fixtures.self, Contract: "dokimos.snapshot" }), /expected Contract/);
});
