// Output-level invariants for the built site. The site is built twice into
// temporary directories; every assertion reads the generated files.

import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { build, loadSources, validateSources } from "../build.mjs";

const environment = { DOKIMOS_SITE_REVISION: "0123456789abcdef0123456789abcdef01234567", DOKIMOS_SITE_YEAR: "2026" };
const tempDir = (label) => fs.mkdtempSync(path.join(os.tmpdir(), `dokimos-site-${label}-`));

const first = await build({ outDir: tempDir("a"), environment });
const second = await build({ outDir: tempDir("b"), environment });
const outDir = first.outDir;

const listFiles = (root) =>
  fs
    .readdirSync(root, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => path.relative(root, path.join(entry.parentPath ?? entry.path, entry.name)).split(path.sep).join("/"))
    .toSorted();

const read = (relative) => fs.readFileSync(path.join(outDir, relative), "utf8");
const htmlFiles = listFiles(outDir).filter((file) => file.endsWith(".html"));
const documents = htmlFiles.map((file) => ({ file, html: read(file) }));
const indexable = documents.filter((doc) => doc.file !== "404.html");

const all = (pattern, text) => [...text.matchAll(pattern)];
const attribute = (tag, name) => tag.match(new RegExp(`\\s${name}="([^"]*)"`))?.[1];
const metaContent = (html, key, value) => html.match(new RegExp(`<meta ${key}="${value}" content="([^"]*)"`))?.[1];
const stripTags = (fragment) => fragment.replace(/<[^>]+>/g, "").replace(/&[a-z#0-9]+;/gi, "x").trim();

const EXPECTED_PAGES = ["index.html", "how-it-works/index.html", "quality-model/index.html", "metrics/index.html", "architecture/index.html", "404.html"];

test("the build produces every expected page", () => {
  EXPECTED_PAGES.forEach((file) => assert.ok(fs.existsSync(path.join(outDir, file)), `${file} missing`));
});

test("the build is deterministic for identical inputs", () => {
  const files = listFiles(outDir);
  assert.deepEqual(listFiles(second.outDir), files);
  files.forEach((file) => assert.ok(fs.readFileSync(path.join(outDir, file)).equals(fs.readFileSync(path.join(second.outDir, file))), `${file} differs between builds`));
});

test("dist contains only deployable files", () => {
  const allowed = /^(CNAME|robots\.txt|sitemap\.xml|404\.html|index\.html|[a-z-]+\/index\.html|assets\/css\/[a-z-]+\.css|assets\/images\/[a-z-]+\.(svg|png))$/;
  listFiles(outDir).forEach((file) => assert.match(file, allowed, `unexpected file in dist: ${file}`));
});

test("CNAME survives the build", () => {
  assert.equal(read("CNAME"), "dokimos.echelonfoundry.com\n");
});

test("no template placeholders or rendering accidents remain", () => {
  documents.forEach(({ file, html: document }) => {
    const html = document.replace(/<script type="application\/ld\+json">[\s\S]*?<\/script>/g, "");
    ["{{", "}}", "${", "undefined", "[object Object]", "NaN", "&lt;span", "&lt;div"].forEach((token) =>
      assert.ok(!html.includes(token), `${file} contains ${token}`),
    );
  });
});

test("every internal link and asset resolves, including fragments", () => {
  const ids = new Map(documents.map(({ file, html }) => [file, new Set(all(/\sid="([^"]+)"/g, html).map((match) => match[1]))]));
  const targetFile = (urlPath) => (urlPath.endsWith("/") ? `${urlPath.slice(1)}index.html` : urlPath.slice(1));
  documents.forEach(({ file, html }) => {
    all(/\s(?:href|src)="([^"]+)"/g, html)
      .map((match) => match[1])
      .filter((url) => url.startsWith("/") || url.startsWith("#"))
      .forEach((url) => {
        const [pathPart, fragment] = url.split("#");
        const target = pathPart === "" ? file : targetFile(pathPart.split("?")[0]);
        assert.ok(fs.existsSync(path.join(outDir, target)), `${file}: ${url} does not resolve`);
        if (fragment) assert.ok(ids.get(target)?.has(fragment), `${file}: ${url} names a missing fragment`);
      });
  });
});

test("internal links are root-relative so no Pages base path can leak", () => {
  documents.forEach(({ file, html }) => {
    all(/\s(?:href|src)="([^"]+)"/g, html)
      .map((match) => match[1])
      .filter((url) => !/^(https?:|mailto:|#|\/)/.test(url))
      .forEach((url) => assert.fail(`${file}: relative URL ${url}`));
    assert.ok(!html.includes("github.io"), `${file} references a github.io URL`);
  });
});

test("navigation targets exist and mark the current page", () => {
  const { site } = loadSources();
  site.navigation.forEach((item) => {
    const doc = read(`${item.path.slice(1)}index.html`);
    assert.match(doc, new RegExp(`<a href="${item.path}" aria-current="page">`), `${item.path} does not mark itself current`);
  });
});

test("every page has unique title, description, canonical, and social metadata", () => {
  const titles = indexable.map(({ html }) => html.match(/<title>([^<]+)<\/title>/)?.[1]);
  const descriptions = indexable.map(({ html }) => metaContent(html, "name", "description"));
  assert.equal(new Set(titles).size, titles.length, "duplicate titles");
  assert.equal(new Set(descriptions).size, descriptions.length, "duplicate descriptions");
  indexable.forEach(({ file, html }) => {
    const expectedPath = file === "index.html" ? "/" : `/${file.replace(/index\.html$/, "")}`;
    const canonical = html.match(/<link rel="canonical" href="([^"]+)">/)?.[1];
    assert.equal(canonical, `https://dokimos.echelonfoundry.com${expectedPath}`, `${file} canonical`);
    assert.equal(metaContent(html, "property", "og:url"), canonical, `${file} og:url`);
    ["og:title", "og:description", "og:image", "og:type", "og:site_name"].forEach((key) => assert.ok(metaContent(html, "property", key), `${file} missing ${key}`));
    ["twitter:card", "twitter:title", "twitter:description", "twitter:image"].forEach((key) => assert.ok(metaContent(html, "name", key), `${file} missing ${key}`));
    const image = new URL(metaContent(html, "property", "og:image")).pathname;
    assert.ok(fs.existsSync(path.join(outDir, image.slice(1))), `${file}: og:image ${image} is not in dist`);
    assert.ok((metaContent(html, "name", "description") ?? "").length >= 70, `${file} description too short`);
    assert.match(html, /Echelon Foundry/, `${file} does not identify Echelon Foundry`);
  });
  assert.match(read("404.html"), /<meta name="robots" content="noindex">/);
});

test("robots.txt and sitemap.xml describe the indexable pages", () => {
  assert.match(read("robots.txt"), /Sitemap: https:\/\/dokimos\.echelonfoundry\.com\/sitemap\.xml/);
  const locations = all(/<loc>([^<]+)<\/loc>/g, read("sitemap.xml")).map((match) => match[1]);
  assert.deepEqual(locations.toSorted(), ["/", "/architecture/", "/how-it-works/", "/metrics/", "/quality-model/"].map((p) => `https://dokimos.echelonfoundry.com${p}`).toSorted());
});

test("documents are semantic: language, landmarks, skip link, one h1, no skipped heading levels", () => {
  documents.forEach(({ file, html }) => {
    assert.match(html, /<html lang="en">/, `${file} lang`);
    assert.match(html, /<a class="skip-link" href="#main-content">/, `${file} skip link`);
    assert.match(html, /<main id="main-content"/, `${file} main`);
    assert.match(html, /<header class="site-header">/, `${file} header`);
    assert.match(html, /<footer class="site-footer">/, `${file} footer`);
    const levels = all(/<h([1-6])[\s>]/g, html).map((match) => Number(match[1]));
    assert.equal(levels.filter((level) => level === 1).length, 1, `${file} must have exactly one h1`);
    assert.equal(levels[0], 1, `${file} must start with h1`);
    levels.slice(1).forEach((level, index) => assert.ok(level <= levels[index] + 1, `${file} skips from h${levels[index]} to h${level}`));
  });
});

test("links have meaningful text", () => {
  documents.forEach(({ file, html }) => {
    all(/<a\s[^>]*>([\s\S]*?)<\/a>/g, html).forEach((match) => {
      const text = stripTags(match[1]).toLowerCase();
      const label = attribute(match[0], "aria-label");
      assert.ok(text.length > 0 || label, `${file}: empty link ${match[0].slice(0, 80)}`);
      assert.ok(!["click here", "here", "read more", "more", "link"].includes(text), `${file}: vague link text "${text}"`);
    });
  });
});

test("charts have text alternatives and data tables", () => {
  documents.forEach(({ file, html }) => {
    all(/<svg class="chart[^"]*"[^>]*>/g, html).forEach((match) => {
      assert.equal(attribute(match[0], "role"), "img", `${file}: chart must be role=img`);
      const labelled = attribute(match[0], "aria-labelledby")?.split(" ") ?? [];
      assert.equal(labelled.length, 2, `${file}: chart must reference a title and a description`);
      labelled.forEach((id) => assert.match(html, new RegExp(`id="${id}">[^<]{10,}<`), `${file}: chart text ${id} missing or empty`));
    });
  });
  const home = read("index.html");
  assert.equal(all(/<svg class="chart/g, home).length, 2, "homepage should render the trajectory and hotspot charts");
  assert.equal(all(/Data table for this chart/g, home).length, 2, "each homepage chart needs a data table");
});

test("status is never conveyed by colour alone", () => {
  documents.forEach(({ file, html }) => {
    const opened = all(/<span class="(?:state|status) /g, html).length;
    const worded = all(/<span class="(?:state|status) [^"]*" data-(?:state|status)="[^"]+"><span class="state-glyph" aria-hidden="true">[^<]*<\/span>([^<]*)<\/span>/g, html);
    assert.equal(worded.length, opened, `${file}: every status badge must follow the glyph-plus-word structure`);
    worded.forEach((match) => assert.match(match[1], /[A-Za-z]{3,}/, `${file}: status badge without a word`));
  });
});

test("demonstration data is labelled wherever it is rendered, and real evidence is attributed", () => {
  const home = read("index.html");
  const demoSections = all(/<section[^>]*>[\s\S]*?<\/section>/g, home)
    .map((match) => match[0])
    .filter((section) => /InvoiceRules\.fs/.test(section) && !/class="hero"/.test(section));
  demoSections.forEach((section) => assert.match(section, /demonstration/i, `unlabelled demonstration section: ${section.slice(0, 120)}`));
  assert.match(home, /Real Dokimos evidence\./);
  assert.match(home, /kemiller2002\/dokimos/);
});

test("scrollable regions are keyboard-reachable and labelled", () => {
  documents.forEach(({ file, html }) => {
    all(/<div class="(?:table-scroll|chart-scroll)"[^>]*>/g, html).forEach((match) => {
      assert.equal(attribute(match[0], "tabindex"), "0", `${file}: scroll region must be focusable`);
      assert.ok(attribute(match[0], "aria-label") || attribute(match[0], "aria-labelledby"), `${file}: scroll region must be labelled`);
    });
    all(/<table[^>]*>[\s\S]*?<\/table>/g, html).forEach((match) =>
      assert.ok(/<caption/.test(match[0]) || /aria-label/.test(html.slice(Math.max(0, match.index - 300), match.index)), `${file}: table without caption or label`),
    );
  });
});

test("no client-side script is required to read the site", () => {
  documents.forEach(({ file, html }) => {
    const scripts = all(/<script([^>]*)>/g, html).map((match) => match[1]);
    scripts.forEach((attributes) => assert.match(attributes, /type="application\/ld\+json"/, `${file}: unexpected executable script`));
  });
});

test("malformed source data fails validation with a useful diagnostic", () => {
  const sources = loadSources();
  const broken = structuredClone(sources);
  broken.demo.focus.observations[4] = { commit: "E", state: "failed", windowCommits: 5, value: 0 };
  const diagnostics = validateSources(broken);
  assert.ok(diagnostics.some((line) => /focus\.observations\[4\]/.test(line)), diagnostics.join("\n"));
  const wrongDomain = { ...structuredClone(sources), cname: "example.com" };
  assert.ok(validateSources(wrongDomain).some((line) => /CNAME/.test(line)));
});
