// Exports report.html to PDF with Chromium (Folio P2: deterministic Chromium)
// and records export-time provenance beside it: renderer, version, source
// contract identity and output hash.
//
// Usage: node results-ui/export-pdf.mjs <report-directory>

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { chromium } from "playwright";

import { createServer } from "../site/serve.mjs";

const listen = (server) => new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve(server.address().port)));

const dir = process.argv[2];
if (!dir) {
  console.error("usage: node results-ui/export-pdf.mjs <report-directory>");
  process.exit(2);
}

const server = createServer(path.resolve(dir));
const port = await listen(server);
const browser = await chromium.launch();
const page = await browser.newPage();
await page.goto(`http://127.0.0.1:${port}/report.html`, { waitUntil: "networkidle" });
await page.evaluate(() => document.fonts.ready);
await page.emulateMedia({ media: "print" });
const pdf = await page.pdf({ format: "A4", printBackground: true, preferCSSPageSize: true, displayHeaderFooter: false });
const title = await page.title();
const version = browser.version();
await browser.close();
server.close();

const out = path.join(dir, "report.pdf");
fs.writeFileSync(out, pdf);
const pages = (pdf.toString("latin1").match(/\/Type\s*\/Page[^s]/g) ?? []).length;
const provenance = {
  contract: "dokimos.report-export",
  schemaVersion: "1.0.0",
  document: title,
  renderer: { name: "chromium", version, tier: "P2 deterministic Chromium", media: "print", format: "A4", printBackground: true },
  output: { file: "report.pdf", bytes: pdf.length, pages, sha256: crypto.createHash("sha256").update(pdf).digest("hex") },
};
fs.writeFileSync(path.join(dir, "report.export.json"), JSON.stringify(provenance, null, 2) + "\n");
if (pages < 2) {
  console.error(`expected a multi-page report, got ${pages} page(s)`);
  process.exit(1);
}
console.log(JSON.stringify(provenance.output));
