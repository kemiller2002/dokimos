// WCAG 2.x contrast for the colour pairs the stylesheet actually uses.
// Colours are read from the stylesheet so the test cannot drift from it.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";

const css = fs.readFileSync(new URL("../assets/css/dokimos.css", import.meta.url), "utf8");
const root = css.slice(css.indexOf(":root {"), css.indexOf("}", css.indexOf(":root {")));
const declarations = Object.fromEntries([...root.matchAll(/--([a-z0-9-]+):\s*([^;]+);/g)].map((match) => [match[1], match[2].trim()]));

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

// Normal text requires 4.5:1 (WCAG 1.4.3). Surfaces: primary, raised, secondary (table highlight).
const lightSurfaces = ["surface-primary", "surface-raised", "surface-secondary"];
const textOnLight = ["text-primary", "text-heading", "text-secondary", "accent", "link", "tone-good", "tone-bad", "tone-warn", "tone-unknown", "tone-neutral"];
const textOnDark = ["text-inverse", "text-inverse-secondary", "dk-verdigris-light", "dk-bronze-light", "dk-ochre-light"];

for (const surface of lightSurfaces) {
  for (const text of textOnLight) {
    test(`--${text} on --${surface} meets 4.5:1`, () => {
      const ratio = contrast(resolve(text), resolve(surface));
      assert.ok(ratio >= 4.5, `${ratio.toFixed(2)}:1`);
    });
  }
}

for (const text of textOnDark) {
  test(`--${text} on --surface-inverse meets 4.5:1`, () => {
    const ratio = contrast(resolve(text), resolve("surface-inverse"));
    assert.ok(ratio >= 4.5, `${ratio.toFixed(2)}:1`);
  });
}

// Non-text UI boundaries (WCAG 1.4.11): functional borders and focus ring need 3:1.
test("functional borders and the focus ring meet 3:1 against the page", () => {
  ["border-functional", "focus-ring"].forEach((name) => {
    const ratio = contrast(resolve(name), resolve("surface-primary"));
    assert.ok(ratio >= 3, `--${name}: ${ratio.toFixed(2)}:1`);
  });
});

test("the raw Echelon accents stay prohibited as small text on stone", () => {
  assert.ok(contrast(resolve("ef-verdigris"), resolve("ef-stone")) < 4.5);
  assert.ok(contrast(resolve("ef-oxide-bronze"), resolve("ef-stone")) < 4.5);
});
