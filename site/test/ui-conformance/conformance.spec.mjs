// Every Dokimos browser surface against visual-engineering/UI-LAYOUT-INTERACTION@1.0.0:
// the public site (dist/), the results UI and the printable report rendered
// from the committed regression fixture. Each test attaches its observations
// as "ui-evidence"; evidence-reporter.mjs assembles and validates them.
//
// Actions: every enabled action the page renders is inventoried (role and
// accessible name as the browser computes them) and exercised through its
// role and name with an explicit pre/post state:
//   - in-page link     -> the URL targets the fragment, and the target exists;
//   - same-origin link -> the destination URL loads and answers 200;
//   - external link    -> its exact https destination is verified; the
//                         external service is not exercised (ULI-A02);
//   - disclosure       -> the <details> it controls opens.

import { test, expect } from "@playwright/test";

import {
  STATUS,
  CONTRACT_WIDTHS,
  inventoryActions,
  runActionAssertion,
  measureAlignment,
  measureOverflow,
  measureLabelIntegrity,
  scaleDefaultText,
  formatReport,
  ACTION_SELECTOR,
} from "./forma-conformance/index.mjs";
import { measureWordIntegrity, measureGutter, measureInPageDestinations } from "./local-checks.mjs";
import { surfaces, covering } from "./surfaces.mjs";

const lower = (status) => status.toLowerCase();

const attach = (record) => test.info().attach("ui-evidence", { body: JSON.stringify(record), contentType: "application/json" });

// A check's recorded status: a covered FAIL is "exception"; a documented
// absence is "not-applicable"; otherwise the measured status.
const checkRecord = (surface, check, report, width) => {
  const exception = report.status === STATUS.FAIL ? covering(surface.id, check, report, width) : undefined;
  const status = exception ? "exception" : report.notApplicable ? "not-applicable" : lower(report.status);
  return { type: "check", surface: surface.id, ruleId: report.ruleId, check, width, status, reason: report.reason, escapes: report.escapes ?? [], exception: exception && { ruleId: exception.ruleId, check: exception.check, owner: exception.owner } };
};

const urlOf = (surface) => `${process.env[surface.origin]}${surface.path}`;

for (const surface of surfaces) {
  test.describe(surface.id, () => {
    test(`${surface.id}: layout at every contract width and 200% text`, async ({ page }) => {
      const records = [];
      for (const width of CONTRACT_WIDTHS) {
        await page.setViewportSize({ width, height: 900 });
        await page.goto(urlOf(surface));
        records.push(checkRecord(surface, "page overflow", await measureOverflow(page), width));
        records.push(checkRecord(surface, "inline gutter", await measureGutter(page), width));
        records.push(checkRecord(surface, "word integrity in tables", await measureWordIntegrity(page), width));
        const nav = page.getByRole("navigation", { name: "Primary" });
        if ((await nav.count()) === 1 && (await nav.isVisible())) records.push(checkRecord(surface, "primary navigation label integrity", await measureLabelIntegrity(nav), width));
      }
      for (const width of [390, 768, 1280]) {
        const scaled = await page.context().newPage();
        await scaled.setViewportSize({ width, height: 900 });
        await scaled.goto(urlOf(surface));
        // scaleDefaultText cannot enlarge a page whose root font size is fixed
        // in absolute units: that is missing evidence (UNKNOWN), not a pass.
        const scaledText = await scaleDefaultText(scaled, 2).then(() => true, () => false);
        records.push(
          checkRecord(
            surface,
            "page overflow at 200% text",
            scaledText
              ? await measureOverflow(scaled)
              : { ruleId: "ULI-L05", status: STATUS.UNKNOWN, reason: "the root font size is fixed in absolute units, so the browser's default text size cannot enlarge this page; reflow at 200% text is unmeasured" },
            width,
          ),
        );
        await scaled.close();
      }
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(urlOf(surface));
      records.push(checkRecord(surface, "in-page destinations", await measureInPageDestinations(page), 1280));

      if (surface.print) {
        // ULI-L12: printed A4 geometry and a real PDF from the same document.
        await page.setViewportSize({ width: 794, height: 1123 });
        await page.emulateMedia({ media: "print" });
        const printed = await measureOverflow(page);
        records.push(checkRecord(surface, "print media A4 overflow", printed, 794));
        const pdf = await page.pdf({ format: "A4", printBackground: true });
        const pages = (pdf.toString("latin1").match(/\/Type\s*\/Page[^s]/g) ?? []).length;
        records.push({
          type: "check",
          surface: surface.id,
          ruleId: "ULI-L12",
          check: "PDF export",
          width: 794,
          status: pages > 0 ? "pass" : "fail",
          reason: `A4 PDF rendered by Chromium: ${pdf.length} bytes, ${pages} page(s)`,
          escapes: [],
        });
        await page.emulateMedia({ media: "screen" });
      }

      records.forEach(attach);
      const failing = records.filter((record) => record.status === "fail");
      expect(failing.map((record) => `${record.ruleId} @${record.width}px ${record.check}: ${record.reason} ${record.escapes.join(" | ")}`).join("\n")).toBe("");
    });

    for (const group of surface.groups) {
      test(`${surface.id}: alignment group ${group.id}`, async ({ page }) => {
        const evidence = [];
        const statuses = [];
        for (const width of group.widths) {
          await page.setViewportSize({ width, height: 900 });
          await page.goto(urlOf(surface));
          const peers = group.peers.map(({ role, name, nav }) => ({ role, name, ...(nav ? { scope: page.getByRole("navigation", { name: nav }) } : {}) }));
          const report = await measureAlignment(page, { ruleId: group.ruleId, id: group.id, edge: group.edge, toleranceCssPx: 1, peers });
          evidence.push(`@${width}px ${formatReport(report).replaceAll("\n", " | ")}`);
          statuses.push(report.status);
        }
        const status = statuses.includes(STATUS.FAIL) ? "fail" : statuses.includes(STATUS.UNKNOWN) ? "unknown" : "pass";
        await attach({ type: "group", surface: surface.id, id: group.id, relation: group.edge === "top" ? "top-edge" : group.edge === "bottom" ? "bottom-edge" : "centerline", rationale: group.rationale, targets: group.peers.map(({ role, name }) => ({ role, name })), evidence, status });
        expect(status, evidence.join("\n")).not.toBe("fail");
      });
    }

    test(`${surface.id}: every enabled action has an observable outcome`, async ({ page }) => {
      await page.setViewportSize({ width: 1280, height: 900 });
      await page.goto(urlOf(surface));
      const inventory = await inventoryActions(page);
      const counts = inventory.reduce((all, item) => ({ ...all, [`${item.role}|${item.name}`]: (all[`${item.role}|${item.name}`] ?? 0) + 1 }), {});
      const records = [];

      for (const item of inventory) {
        await page.goto(urlOf(surface));
        const pageUrl = new URL(page.url());
        const duplicate = counts[`${item.role}|${item.name}`] > 1;
        // A repeated name is scoped to the control's own parent element.
        const scope = duplicate ? page.locator(ACTION_SELECTOR).nth(item.index).locator("xpath=..") : undefined;
        const id = `${surface.id}#${item.index}`;
        const control = { role: item.role === "disclosure" ? "disclosure" : item.role, name: item.name };

        if (item.role === "disclosure") {
          // A disclosure toggles: one that starts open (an expanded summary)
          // is expected to close, one that starts closed to open.
          const initiallyOpen = await page.locator(ACTION_SELECTOR).nth(item.index).evaluate((summary) => Boolean(summary.parentElement?.open));
          const report = await runActionAssertion(page, { ruleId: "ULI-A02", action: { ...control, ...(scope ? { scope } : {}) }, outcome: { kind: "open", target: "controlled", expected: !initiallyOpen } });
          records.push({ id, control, intent: `${initiallyOpen ? "collapse" : "expand"} "${item.name}"`, expectedOutcome: `the details element it controls ${initiallyOpen ? "closes" : "opens"}`, status: lower(report.status), evidence: [formatReport(report)], resetStrategy: "reload the page" });
          continue;
        }

        const href = item.href ?? "";
        const destination = new URL(href, pageUrl);
        const external = destination.origin !== pageUrl.origin;

        if (external) {
          records.push({
            id,
            control,
            intent: `open ${href}`,
            expectedOutcome: `navigates to the external destination ${href} (destination verified; the external service is not exercised)`,
            status: destination.protocol === "https:" ? "pass" : "fail",
            evidence: [`ULI-A01 ${destination.protocol === "https:" ? "PASS" : "FAIL"}: the destination is ${destination.protocol === "https:" ? "an absolute https URL" : "not https"}: ${href}`],
            resetStrategy: "not activated; no state",
          });
          continue;
        }

        const expected = href.startsWith("#") ? `${pageUrl.origin}${pageUrl.pathname}${href}` : destination.href;
        // A link to the page being shown starts from a fragment of it, so its
        // activation still demonstrates a transition (the fragment is dropped).
        if (expected === page.url()) await page.goto(`${urlOf(surface)}#ui-conformance-start`);
        const report = await runActionAssertion(page, { ruleId: "ULI-A02", action: { ...control, ...(scope ? { scope } : {}) }, outcome: { kind: "url", expected } });
        const answered = href.startsWith("#") ? 200 : (await fetch(destination.href)).status;
        const status = report.status === STATUS.PASS && answered !== 200 ? "fail" : lower(report.status);
        records.push({
          id,
          control,
          intent: href.startsWith("#") ? `jump to ${href}` : `open ${destination.pathname}${destination.hash}`,
          expectedOutcome: href.startsWith("#") ? `the URL targets ${href} and the target exists` : `${destination.pathname} loads and answers 200`,
          status,
          evidence: [`${formatReport(report)} (pre=${JSON.stringify(report.pre?.value)}, post=${JSON.stringify(report.post?.value)}); destination HTTP ${answered}`],
          resetStrategy: "navigate back to the surface",
        });
      }

      await attach({ type: "actions", surface: surface.id, path: surface.path, kind: surface.kind, inventory: inventory.length, actions: records });
      // UNKNOWN is recorded in the evidence, never counted as a pass; only a
      // reproduced FAIL fails the run.
      const failing = records.filter((record) => record.status === "fail");
      expect(failing.map((record) => `${record.id} ${record.control.role} "${record.control.name}": ${record.evidence.join(" ")}`).join("\n")).toBe("");
    });
  });
}
