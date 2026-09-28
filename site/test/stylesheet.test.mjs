// The Echelon Foundry stylesheet is vendored verbatim. Its checksum is recorded
// here and in docs/website/SITE.md; update both when re-vendoring a new version.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import { createHash } from "node:crypto";

const ECHELON_FOUNDRY_CSS_SHA256 = "a3627edd9932077e9ab6798d1f4a954fc809541af6cdb1846987fc8073509375";
const read = (file) => fs.readFileSync(new URL(`../assets/css/${file}`, import.meta.url));

test("echelon-foundry.css is the unmodified Echelon Foundry stylesheet", () => {
  assert.equal(createHash("sha256").update(read("echelon-foundry.css")).digest("hex"), ECHELON_FOUNDRY_CSS_SHA256);
});

test("dokimos.css does not restyle what Echelon Foundry owns", () => {
  const css = read("dokimos.css").toString("utf8");
  [/(^|\n)\s*body\s*\{/, /body::before\s*\{[^}]*background/, /\.site-header\s*\{/, /\.site-footer\s*\{/, /\.button\s*\{/, /(^|\n)h[12][,\s{]/, /--ef-[a-z-]+:/].forEach((pattern) =>
    assert.doesNotMatch(css, pattern, `dokimos.css overrides an Echelon Foundry rule: ${pattern}`),
  );
});
