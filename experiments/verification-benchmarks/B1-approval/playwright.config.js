// Benchmark infrastructure: runs the real server (UI -> HTTP -> domain -> file store) on a fresh data
// directory and drives it with Chromium. Reporters: list (log) and JSON (for dokimos coverage).
const { defineConfig } = require("@playwright/test");
const os = require("os");
const path = require("path");
const fs = require("fs");

const port = process.env.APPROVAL_PORT || "5180";
const data = fs.mkdtempSync(path.join(os.tmpdir(), "approval-data-"));

module.exports = defineConfig({
  testDir: "tests/browser",
  workers: 1,
  retries: 0,
  reporter: [["list"], ["json", { outputFile: "out/playwright.json" }]],
  use: { baseURL: `http://127.0.0.1:${port}` },
  projects: [{ name: "chromium", use: { browserName: "chromium" } }],
  webServer: {
    command: `dotnet run --project src/Approval --no-launch-profile -- --urls http://127.0.0.1:${port} --data ${data}`,
    url: `http://127.0.0.1:${port}/`,
    timeout: 180000,
    reuseExistingServer: false,
    env: { APPROVAL_TOKEN: process.env.APPROVAL_TOKEN || "b1-secret-token-7f3a9c", DOTNET_ROLL_FORWARD: "Major" }
  }
});
