// Browser audit of rendered Dokimos results pages (DOK-OPS-019):
// axe-core WCAG 2.1 AA rules, horizontal overflow at 320px to 1920px,
// 200% zoom reflow, a keyboard-reachable skip link, and forced colors.
//
// Usage: node results-ui/audit.mjs <rendered-directory> [screenshot-directory]

import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { chromium } from "playwright";

import { createServer } from "../site/serve.mjs";
import { PAGES } from "./render.mjs";

const require = createRequire(import.meta.url);
const axeSource = fs.readFileSync(require.resolve("axe-core/axe.min.js"), "utf8");

const VIEWPORTS = [
  { name: "small mobile", width: 320, height: 640 },
  { name: "tablet", width: 768, height: 1024 },
  { name: "desktop", width: 1280, height: 800 },
  { name: "wide desktop", width: 1920, height: 1080 },
];

const listen = (server) => new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve(server.address().port)));

const axeViolations = async (page) => {
  await page.addScriptTag({ content: axeSource });
  const result = await page.evaluate(() =>
    window.axe.run(document, { runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] } }),
  );
  return result.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s) — ${v.nodes[0]?.target.join(" ")}`);
};

const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

const auditPage = async (browser, origin, file, shots) => {
  const failures = [];
  const context = await browser.newContext({ viewport: VIEWPORTS[2], javaScriptEnabled: true });
  const page = await context.newPage();
  const response = await page.goto(`${origin}/${file}`, { waitUntil: "load" });
  if (response?.status() !== 200) failures.push(`HTTP ${response?.status()}`);
  (await axeViolations(page)).forEach((line) => failures.push(`axe: ${line}`));

  await page.keyboard.press("Tab");
  const skip = await page.evaluate(() => document.activeElement?.className ?? "");
  if (!String(skip).includes("ef-skip-link")) failures.push("first Tab does not reach the skip link");

  for (const viewport of VIEWPORTS) {
    await page.setViewportSize(viewport);
    const extra = await overflow(page);
    if (extra > 0) failures.push(`${viewport.name} (${viewport.width}px): page scrolls horizontally by ${extra}px`);
    if (shots && (viewport.width === 320 || viewport.width === 1280)) {
      await page.screenshot({ path: path.join(shots, `${file.replace(".html", "")}-${viewport.width}.png`), fullPage: false });
    }
  }

  await page.setViewportSize({ width: 640, height: 800 });
  await page.evaluate(() => { document.documentElement.style.fontSize = "200%"; });
  const zoomed = await overflow(page);
  if (zoomed > 0) failures.push(`200% text: page scrolls horizontally by ${zoomed}px`);
  await context.close();

  const forced = await browser.newContext({ viewport: VIEWPORTS[2], forcedColors: "active" });
  const forcedPage = await forced.newPage();
  await forcedPage.goto(`${origin}/${file}`, { waitUntil: "load" });
  const lozengeText = await forcedPage.evaluate(() =>
    [...document.querySelectorAll(".ef-status-lozenge")].every((el) => el.textContent.trim().length > 0),
  );
  if (!lozengeText) failures.push("forced colors: a status lozenge has no text label");
  await forced.close();

  return failures.map((f) => `${file}: ${f}`);
};

const [root, shots] = process.argv.slice(2);
if (!root) {
  console.error("usage: node results-ui/audit.mjs <rendered-directory> [screenshot-directory]");
  process.exit(2);
}
if (shots) fs.mkdirSync(shots, { recursive: true });

const server = createServer(path.resolve(root));
const port = await listen(server);
const browser = await chromium.launch();
const failures = (await Promise.all(PAGES.map((p) => auditPage(browser, `http://127.0.0.1:${port}`, p.file, shots)))).flat();
await browser.close();
server.close();

if (failures.length) {
  console.error(`Results UI audit failed:\n  - ${failures.join("\n  - ")}`);
  process.exit(1);
}
console.log(`Results UI audit passed for ${PAGES.length} pages at ${VIEWPORTS.map((v) => v.width).join(", ")}px, 200% text and forced colors.`);
