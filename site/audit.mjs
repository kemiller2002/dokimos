// Browser audit of the built site: axe-core WCAG 2.1 AA rules, page-level
// horizontal overflow at each required viewport, 200% zoom reflow, keyboard
// skip link, and rendering without JavaScript.
//
// Usage: npm run build && npm run audit

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import { chromium } from "playwright";

import { createServer } from "./serve.mjs";

const require = createRequire(import.meta.url);
const axeSource = fs.readFileSync(require.resolve("axe-core/axe.min.js"), "utf8");
const distDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "dist");

const PAGES = ["/", "/how-it-works/", "/quality-model/", "/metrics/", "/architecture/", "/404.html"];
const VIEWPORTS = [
  { name: "small mobile", width: 320, height: 640 },
  { name: "large mobile", width: 414, height: 896 },
  { name: "tablet", width: 768, height: 1024 },
  { name: "desktop", width: 1280, height: 800 },
  { name: "wide desktop", width: 1920, height: 1080 },
];

const listen = (server) => new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve(server.address().port)));

const axeViolations = async (page) => {
  await page.addScriptTag({ content: axeSource });
  const result = await page.evaluate(() =>
    window.axe.run(document, { runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "best-practice"] } }),
  );
  return result.violations.map((violation) => `${violation.id} (${violation.impact}): ${violation.nodes.length} node(s) — ${violation.nodes[0]?.target.join(" ")}`);
};

const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

const auditPage = async (browser, origin, pagePath) => {
  const failures = [];
  const context = await browser.newContext({ viewport: VIEWPORTS[3] });
  const page = await context.newPage();
  const response = await page.goto(`${origin}${pagePath}`, { waitUntil: "load" });
  if (response.status() !== 200) failures.push(`HTTP ${response.status()}`);
  (await axeViolations(page)).forEach((line) => failures.push(`axe desktop: ${line}`));

  await page.keyboard.press("Tab");
  const skip = await page.evaluate(() => {
    const active = document.activeElement;
    const box = active?.getBoundingClientRect();
    return { className: active?.className, visible: Boolean(box && box.top >= 0 && box.height > 0) };
  });
  if (skip.className !== "skip-link" || !skip.visible) failures.push("first Tab does not reveal the skip link");

  for (const viewport of VIEWPORTS) {
    await page.setViewportSize(viewport);
    const extra = await overflow(page);
    if (extra > 0) failures.push(`${viewport.name} (${viewport.width}px): page scrolls horizontally by ${extra}px`);
  }

  await page.setViewportSize({ width: 375, height: 812 });
  (await axeViolations(page)).forEach((line) => failures.push(`axe mobile: ${line}`));

  // 200% zoom of a 1280px window reflows like a 640px viewport at 2x.
  const zoomed = await browser.newContext({ viewport: { width: 640, height: 400 }, deviceScaleFactor: 2 });
  const zoomPage = await zoomed.newPage();
  await zoomPage.goto(`${origin}${pagePath}`, { waitUntil: "load" });
  const zoomOverflow = await overflow(zoomPage);
  if (zoomOverflow > 0) failures.push(`200% zoom: page scrolls horizontally by ${zoomOverflow}px`);
  await zoomed.close();

  const noScript = await browser.newContext({ javaScriptEnabled: false, viewport: VIEWPORTS[3] });
  const noScriptPage = await noScript.newPage();
  await noScriptPage.goto(`${origin}${pagePath}`, { waitUntil: "load" });
  const text = await noScriptPage.evaluate(() => document.querySelector("main")?.innerText.trim().length ?? 0);
  if (text < 200) failures.push("main content is missing without JavaScript");
  await noScript.close();

  const reduced = await browser.newContext({ reducedMotion: "reduce", forcedColors: "active", viewport: VIEWPORTS[3] });
  const reducedPage = await reduced.newPage();
  await reducedPage.goto(`${origin}${pagePath}`, { waitUntil: "load" });
  const reducedOverflow = await overflow(reducedPage);
  if (reducedOverflow > 0) failures.push(`forced colours: page scrolls horizontally by ${reducedOverflow}px`);
  await reduced.close();

  await context.close();
  return { pagePath, failures };
};

const main = async () => {
  const server = createServer(distDir);
  const port = await listen(server);
  const origin = `http://127.0.0.1:${port}`;
  const browser = await chromium.launch(process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {});
  try {
    const results = [];
    for (const pagePath of PAGES) results.push(await auditPage(browser, origin, pagePath));
    const missing = (await fetch(`${origin}/does-not-exist/`)).status;
    if (missing !== 404) results.push({ pagePath: "/does-not-exist/", failures: [`expected 404, received ${missing}`] });
    results.forEach(({ pagePath, failures }) =>
      console.log(failures.length === 0 ? `ok   ${pagePath}` : `FAIL ${pagePath}\n${failures.map((line) => `     - ${line}`).join("\n")}`),
    );
    const failed = results.filter(({ failures }) => failures.length > 0);
    console.log(`\n${results.length - failed.length}/${results.length} pages passed the browser audit.`);
    process.exitCode = failed.length > 0 ? 1 : 0;
  } finally {
    await browser.close();
    server.close();
  }
};

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
