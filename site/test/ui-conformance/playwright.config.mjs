// `npm run ui:conformance`: every Dokimos browser surface against
// visual-engineering/UI-LAYOUT-INTERACTION@1.0.0 (rule IDs cited, never
// restated). The site is built into dist/ first (package script); the
// results UI and the printable report are rendered here from the committed
// dokimos.results fixtures produced by the real CLI.
import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: ".",
  testMatch: "*.spec.mjs",
  // A shared machine and a shared CI runner: at most two browser workers.
  workers: 2,
  fullyParallel: true,
  retries: 0,
  timeout: 120_000,
  globalSetup: "./global-setup.mjs",
  reporter: [["list"], ["./evidence-reporter.mjs"]],
  use: {
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {},
  },
  outputDir: process.env.UI_CONFORMANCE_ARTIFACTS ?? "../../../test-results/ui-conformance",
});
