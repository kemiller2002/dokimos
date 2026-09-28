// Dokimos website build.
//
//   source content + real Dokimos evidence  ->  validation  ->  pure rendering  ->  dist/
//
// All filesystem and process effects live in this file. Everything it calls is
// pure. The output is deterministic for a given source revision; the only build
// metadata is the source revision and its commit year (see docs/website/SITE.md).

import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { renderDocument } from "./lib/layout.mjs";
import { validateDemonstration, trajectory, findingLifecycle, hotspots } from "./lib/demo.mjs";
import {
  validateCatalog,
  validateSnapshot,
  validateStatus,
  validatePolicy,
  dimensionsWithStatus,
  catalogRows,
  snapshotSummary,
} from "./lib/evidence.mjs";

const siteDir = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(siteDir, "..");
const PAGE_MODULES = ["home", "how-it-works", "quality-model", "metrics", "architecture", "not-found"];

const readJson = (relativePath) => {
  const absolutePath = path.join(rootDir, relativePath);
  try {
    return JSON.parse(fs.readFileSync(absolutePath, "utf8"));
  } catch (error) {
    throw new BuildError([`${relativePath}: ${error.message}`]);
  }
};

class BuildError extends Error {
  constructor(diagnostics) {
    super(`Site build failed:\n${diagnostics.map((line) => `  - ${line}`).join("\n")}`);
    this.diagnostics = diagnostics;
  }
}

const git = (...args) => execFileSync("git", args, { cwd: rootDir, encoding: "utf8" }).trim();

export const buildMetadata = (environment = process.env) => {
  try {
    const revision = environment.DOKIMOS_SITE_REVISION ?? git("rev-parse", "HEAD");
    const year = environment.DOKIMOS_SITE_YEAR ?? git("show", "-s", "--format=%cI", revision).slice(0, 4);
    return { revision, year };
  } catch {
    throw new BuildError([
      "build metadata: could not read the source revision from Git. Build from a Git checkout, or set DOKIMOS_SITE_REVISION and DOKIMOS_SITE_YEAR.",
    ]);
  }
};

export const loadSources = () => {
  const site = readJson("site/data/site.json");
  const catalog = readJson("config/metric-catalog.json");
  const policy = readJson("config/dokimos-policy.json");
  const snapshot = readJson("baselines/accepted-snapshot.json");
  const status = readJson("site/data/measurement-status.json");
  const demo = readJson("site/data/demonstration.json");
  const cname = fs.readFileSync(path.join(rootDir, "CNAME"), "utf8").trim();
  return { site, catalog, policy, snapshot, status, demo, cname };
};

export const validateSources = ({ site, catalog, policy, snapshot, status, demo, cname }) => {
  const catalogErrors = validateCatalog(catalog);
  const snapshotErrors = validateSnapshot(snapshot);
  const knownMetric = (id, version) => catalog.metrics.some((metric) => metric.id === id && metric.version === version);
  const dependentErrors =
    catalogErrors.length + snapshotErrors.length > 0
      ? []
      : [...validateStatus(status, catalog, snapshot), ...validatePolicy(policy, catalog), ...validateDemonstration(demo, knownMetric)];
  const siteErrors = [
    ...(new URL(site.origin).hostname === cname ? [] : [`CNAME (${cname}) must match site.origin (${site.origin})`]),
    ...(site.navigation.every((item) => /^\/[a-z-]+\/$/.test(item.path)) ? [] : ["site.navigation: paths must be clean directory URLs such as /metrics/"]),
  ];
  return [...catalogErrors, ...snapshotErrors, ...dependentErrors, ...siteErrors];
};

export const deriveContext = ({ site, catalog, policy, snapshot, status, demo }, build) => ({
  site,
  build,
  demo,
  policy,
  catalog,
  statusCriteria: status.statusCriteria,
  trajectory: trajectory(demo),
  lifecycle: findingLifecycle(demo),
  hotspots: hotspots(demo),
  dimensions: dimensionsWithStatus(status),
  catalogRows: catalogRows(catalog, status),
  snapshot: snapshotSummary(snapshot),
});

const contentHash = (buffer) => createHash("sha256").update(buffer).digest("hex").slice(0, 12);

const outputPathFor = (pagePath) => (pagePath === "/404.html" ? "404.html" : path.join(pagePath.slice(1), "index.html"));

const sitemap = (site, pages) =>
  `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${pages
    .filter((page) => page.indexable !== false)
    .map((page) => `  <url><loc>${new URL(page.path, site.origin).href}</loc></url>`)
    .join("\n")}\n</urlset>\n`;

const robots = (site) => `User-agent: *\nAllow: /\n\nSitemap: ${new URL("/sitemap.xml", site.origin).href}\n`;

const loadPages = async () =>
  Promise.all(PAGE_MODULES.map((name) => import(pathToFileURL(path.join(siteDir, "pages", `${name}.mjs`)).href)));

export const build = async ({ outDir = path.join(rootDir, "dist"), environment = process.env } = {}) => {
  const sources = loadSources();
  const diagnostics = validateSources(sources);
  if (diagnostics.length > 0) throw new BuildError(diagnostics);

  const metadata = buildMetadata(environment);
  const context = deriveContext(sources, metadata);
  const stylesheet = fs.readFileSync(path.join(siteDir, "assets", "css", "dokimos.css"));
  const assets = {
    stylesheet: `/assets/css/dokimos.css?v=${contentHash(stylesheet)}`,
    favicon: "/assets/images/favicon.svg",
    socialImage: "/assets/images/social-card.png",
  };
  const modules = await loadPages();
  const rendered = modules.map(({ page, render }) => ({
    page,
    file: outputPathFor(page.path),
    document: renderDocument({ site: sources.site, build: metadata, assets, page, content: render(context) }),
  }));

  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(outDir, { recursive: true });
  fs.cpSync(path.join(siteDir, "assets"), path.join(outDir, "assets"), { recursive: true });
  fs.writeFileSync(path.join(outDir, "CNAME"), `${sources.cname}\n`);
  fs.writeFileSync(path.join(outDir, "robots.txt"), robots(sources.site));
  fs.writeFileSync(path.join(outDir, "sitemap.xml"), sitemap(sources.site, rendered.map(({ page }) => page)));
  rendered.forEach(({ file, document }) => {
    fs.mkdirSync(path.dirname(path.join(outDir, file)), { recursive: true });
    fs.writeFileSync(path.join(outDir, file), document);
  });

  return { outDir, pages: rendered.map(({ page, file }) => ({ path: page.path, file, title: page.title })), metadata };
};

const isEntryPoint = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (isEntryPoint) {
  build()
    .then(({ outDir, pages, metadata }) => {
      console.log(`Built ${pages.length} pages into ${path.relative(process.cwd(), outDir) || "."} from ${metadata.revision.slice(0, 7)}.`);
    })
    .catch((error) => {
      console.error(error.message);
      process.exitCode = 1;
    });
}
