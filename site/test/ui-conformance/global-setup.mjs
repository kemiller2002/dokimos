// Serves dist/ (the built site) and a freshly rendered results UI on
// loopback; the URLs reach the workers through the environment.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { createServer } from "../../serve.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
const listen = (server) => new Promise((resolve) => server.listen(0, "127.0.0.1", () => resolve(server.address().port)));

export default async () => {
  const dist = path.join(root, "dist");
  if (!fs.existsSync(path.join(dist, "index.html"))) throw new Error("dist/ is missing: run npm run build first (npm run ui:conformance does)");
  const results = fs.mkdtempSync(path.join(os.tmpdir(), "dokimos-ui-conformance-"));
  const fixture = path.join(root, "results-ui", "test", "fixtures", "regression-results.json");
  execFileSync(process.execPath, [path.join(root, "results-ui", "render.mjs"), fixture, results], { stdio: "inherit" });
  execFileSync(process.execPath, [path.join(root, "results-ui", "report.mjs"), fixture, results], { stdio: "inherit" });
  const site = createServer(dist);
  const resultsServer = createServer(results);
  process.env.UI_SITE_ORIGIN = `http://127.0.0.1:${await listen(site)}`;
  process.env.UI_RESULTS_ORIGIN = `http://127.0.0.1:${await listen(resultsServer)}`;
  return async () => {
    site.close();
    resultsServer.close();
    fs.rmSync(results, { recursive: true, force: true });
  };
};
