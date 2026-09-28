// WCAG 2.x contrast for the colour pairs the site actually uses.
// Tokens are read from both stylesheets (Echelon Foundry first, then Dokimos),
// so the test cannot drift from the CSS that ships.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";

const rootBlock = (file) => {
  const css = fs.readFileSync(new URL(`../assets/css/${file}`, import.meta.url), "utf8");
  const start = css.indexOf(":root {");
  return css.slice(start, css.indexOf("}", start));
};

const declarations = Object.fromEntries(
  ["echelon-foundry.css", "dokimos.css"].flatMap((file) => [...rootBlock(file).matchAll(/--([a-z0-9-]+):\s*([^;]+);/g)].map((match) => [match[1], match[2].trim()])),
);

const resolve = (name, seen = new Set()) => {
  const value = declarations[name];
  if (value === undefined) throw new Error(`--${name} is not defined in :root`);
  if (seen.has(name)) throw new Error(`--${name} is circular`);
  const reference = value.match(/^var\(--([a-z0-9-]+)\)$/);
  return reference ? resolve(reference[1], new Set([...seen, name])) : value;
};

const luminance = (hex) => {
  const channels = hex.match(/^#([\da-f]{2})([\da-f]{2})([\da-f]{2})$/i).slice(1).map((value) => parseInt(value, 16) / 255);
  const [r, g, b] = channels.map((value) => (value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};

export const contrast = (foreground, background) => {
  const [lighter, darker] = [luminance(foreground), luminance(background)].toSorted((a, b) => b - a);
  return (lighter + 0.05) / (darker + 0.05);
};

// Normal text requires 4.5:1 (WCAG 1.4.3). Text, eyebrows, links, and status
// tones sit on the page (parchment) or on raised Dokimos surfaces.
const lightSurfaces = ["ef-surface-primary", "dk-surface-raised"];
const textOnLight = ["ef-text-primary", "ef-text-heading", "ef-text-secondary", "ef-accent-primary", "ef-accent-secondary", "dk-tone-good", "dk-tone-bad", "dk-tone-warn", "dk-tone-unknown", "dk-tone-neutral"];
const textOnDark = ["ef-text-inverse", "dk-stone-on-dark", "dk-verdigris-light", "dk-bronze-light"];

for (const surface of lightSurfaces) {
  for (const text of textOnLight) {
    test(`--${text} on --${surface} meets 4.5:1`, () => {
      const ratio = contrast(resolve(text), resolve(surface));
      assert.ok(ratio >= 4.5, `${ratio.toFixed(2)}:1`);
    });
  }
}

// The highlighted table row uses stone; only primary text and status badges
// (which carry their own raised background) appear on it.
test("--ef-text-primary on --ef-surface-secondary meets 4.5:1", () => {
  assert.ok(contrast(resolve("ef-text-primary"), resolve("ef-surface-secondary")) >= 4.5);
});

for (const text of textOnDark) {
  test(`--${text} on --ef-surface-inverse meets 4.5:1`, () => {
    const ratio = contrast(resolve(text), resolve("ef-surface-inverse"));
    assert.ok(ratio >= 4.5, `${ratio.toFixed(2)}:1`);
  });
}

// Non-text UI boundaries (WCAG 1.4.11): functional borders and focus ring need 3:1.
test("functional borders and the focus ring meet 3:1 against the page", () => {
  ["ef-border-functional", "ef-focus-ring"].forEach((name) => {
    const ratio = contrast(resolve(name), resolve("ef-surface-primary"));
    assert.ok(ratio >= 3, `--${name}: ${ratio.toFixed(2)}:1`);
  });
});
