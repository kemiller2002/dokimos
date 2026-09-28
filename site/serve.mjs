// Local preview server for dist/ with GitHub Pages URL semantics:
// "/path/" serves "/path/index.html", "/path" redirects to "/path/", unknown paths serve 404.html.

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const distDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "dist");
const port = Number(process.env.PORT ?? 4000);

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".xml": "application/xml",
  ".txt": "text/plain; charset=utf-8",
};

const isFile = (candidate) => fs.existsSync(candidate) && fs.statSync(candidate).isFile();
const isDirectory = (candidate) => fs.existsSync(candidate) && fs.statSync(candidate).isDirectory();

export const resolveRequest = (root, urlPath) => {
  const decoded = decodeURIComponent(urlPath.split("?")[0]);
  const target = path.normalize(path.join(root, decoded));
  return !target.startsWith(root)
    ? { status: 403 }
    : isFile(target)
      ? { status: 200, file: target }
      : isDirectory(target) && decoded.endsWith("/") && isFile(path.join(target, "index.html"))
        ? { status: 200, file: path.join(target, "index.html") }
        : isDirectory(target)
          ? { status: 301, location: `${decoded}/` }
          : { status: 404, file: path.join(root, "404.html") };
};

const respond = (response, resolution) =>
  resolution.location
    ? response.writeHead(resolution.status, { Location: resolution.location }).end()
    : resolution.file && isFile(resolution.file)
      ? fs.createReadStream(resolution.file).pipe(
          response.writeHead(resolution.status, { "Content-Type": TYPES[path.extname(resolution.file)] ?? "application/octet-stream" }),
        )
      : response.writeHead(resolution.status).end();

export const createServer = (root = distDir) =>
  http.createServer((request, response) => respond(response, resolveRequest(root, request.url ?? "/")));

const isEntryPoint = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (isEntryPoint) {
  createServer().listen(port, () => console.log(`Serving ${distDir} at http://localhost:${port}/`));
}
