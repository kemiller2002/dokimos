// Dokimos-local observations the forma-conformance helpers do not provide
// (yet). Each cites its rule and reports PASS / FAIL / UNKNOWN, never a
// vacuous PASS. Candidates for upstreaming to forma-conformance.

import { STATUS, settleFontsAndFrames } from "./forma-conformance/index.mjs";

// ULI-L06/L05: a short label word (not an identifier or path) is never split
// across lines inside a table cell or header.
export const measureWordIntegrity = async (page) => {
  await settleFontsAndFrames(page);
  const broken = await page.evaluate(() => {
    const out = [];
    for (const cell of document.querySelectorAll("th, td")) {
      if (!cell.checkVisibility()) continue;
      const walker = document.createTreeWalker(cell, NodeFilter.SHOW_TEXT);
      for (let text = walker.nextNode(); text; text = walker.nextNode()) {
        const words = /(?<![A-Za-z0-9._\/-])[A-Za-z]{3,12}(?![A-Za-z0-9._\/-])/g;
        for (let match = words.exec(text.textContent); match; match = words.exec(text.textContent)) {
          const range = document.createRange();
          range.setStart(text, match.index);
          range.setEnd(text, match.index + match[0].length);
          const lines = new Set([...range.getClientRects()].filter((rect) => rect.width > 0).map((rect) => Math.round(rect.top)));
          if (lines.size > 1) out.push(match[0]);
        }
      }
    }
    return [...new Set(out)].slice(0, 12);
  });
  const tables = await page.locator("table").count();
  if (tables === 0) return { ruleId: "ULI-L06", status: STATUS.UNKNOWN, notApplicable: true, reason: "not applicable: no table on this page" };
  return broken.length === 0
    ? { ruleId: "ULI-L06", status: STATUS.PASS, reason: "no label word is split across lines" }
    : { ruleId: "ULI-L06", status: STATUS.FAIL, reason: `words split across lines: ${broken.join(", ")}` };
};

// ULI-L09: content keeps an inline gutter (no text or control touches a
// viewport edge outside a contained scroller).
export const measureGutter = async (page) => {
  await settleFontsAndFrames(page);
  const touching = await page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    const scrolled = (element) => {
      for (let node = element.parentElement; node && node !== document.body; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowX;
        if (overflow === "auto" || overflow === "scroll") return true;
      }
      return false;
    };
    return [...document.querySelectorAll("body *")]
      .filter((element) => element.checkVisibility() && element.children.length === 0 && !element.closest(".skip-link") && !scrolled(element))
      .filter((element) => element.textContent.trim() || element.matches("input, select, textarea, button"))
      .map((element) => [element, element.getBoundingClientRect()])
      .filter(([, rect]) => rect.width > 0 && (rect.left < 1 || rect.right > width - 1))
      .map(([element, rect]) => `${element.tagName.toLowerCase()} "${element.textContent.trim().slice(0, 30)}" ${rect.left.toFixed(0)}..${rect.right.toFixed(0)}`)
      .slice(0, 8);
  });
  return touching.length === 0
    ? { ruleId: "ULI-L09", status: STATUS.PASS, reason: "no rendered text or control touches a viewport edge" }
    : { ruleId: "ULI-L09", status: STATUS.FAIL, reason: `content touches a viewport edge: ${touching.join("; ")}` };
};

// ULI-A01: every in-page link names an element that exists.
export const measureInPageDestinations = async (page) => {
  const missing = await page.evaluate(() =>
    [...new Set([...document.querySelectorAll('a[href^="#"]')].map((link) => link.getAttribute("href")))].filter(
      (href) => href.length > 1 && !document.getElementById(decodeURIComponent(href.slice(1))),
    ),
  );
  return missing.length === 0
    ? { ruleId: "ULI-A01", status: STATUS.PASS, reason: "every in-page link names an existing element" }
    : { ruleId: "ULI-A01", status: STATUS.FAIL, reason: `in-page links without a destination: ${missing.join(", ")}` };
};
