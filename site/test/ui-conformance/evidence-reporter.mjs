// Assembles the "ui-evidence" attachments into one evidence document for
// visual-engineering's ui-conformance.schema.json (standardVersion 1.0.0),
// validates it, and writes it to UI_CONFORMANCE_EVIDENCE (default
// test-results/ui-conformance/evidence.json). Invalid evidence fails the run.

import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { exceptions as declaredExceptions } from "./surfaces.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, "..", "..", "..");
const schema = JSON.parse(fs.readFileSync(path.join(here, "ui-conformance.schema.json"), "utf8"));

// The JSON Schema subset the evidence schema uses; any other keyword is an
// error, so a schema change cannot pass silently.
const ANNOTATIONS = new Set(["$schema", "$id", "title", "description", "default", "$defs"]);
const SUPPORTED = new Set(["type", "required", "properties", "additionalProperties", "enum", "const", "pattern", "minLength", "minItems", "uniqueItems", "minimum", "items", "$ref", "allOf", "if", "then"]);
const kindOf = (value) => (value === null ? "null" : Array.isArray(value) ? "array" : typeof value === "number" ? "number" : typeof value);

export const validate = (node, value, at = "$") => {
  const errors = Object.keys(node).filter((key) => !ANNOTATIONS.has(key) && !SUPPORTED.has(key)).map((key) => `${at}: unsupported schema keyword ${key}`);
  const add = (message) => errors.push(`${at}: ${message}`);
  if (node.$ref) errors.push(...validate(schema.$defs[node.$ref.replace("#/$defs/", "")], value, at));
  if (node.type) {
    const kind = kindOf(value);
    const ok = kind === node.type || (node.type === "integer" && Number.isInteger(value));
    if (!ok) add(`expected ${node.type}, found ${kind}`);
  }
  if (node.enum && !node.enum.some((option) => JSON.stringify(option) === JSON.stringify(value))) add(`${JSON.stringify(value)} is not one of ${JSON.stringify(node.enum)}`);
  if ("const" in node && JSON.stringify(node.const) !== JSON.stringify(value)) add(`must be ${JSON.stringify(node.const)}`);
  if (typeof value === "string") {
    if (node.pattern && !new RegExp(node.pattern, "u").test(value)) add(`"${value}" does not match ${node.pattern}`);
    if (node.minLength !== undefined && value.length < node.minLength) add(`shorter than ${node.minLength}`);
  }
  if (typeof value === "number" && node.minimum !== undefined && value < node.minimum) add(`below ${node.minimum}`);
  if (Array.isArray(value)) {
    if (node.minItems !== undefined && value.length < node.minItems) add(`fewer than ${node.minItems} items`);
    if (node.uniqueItems && new Set(value.map((item) => JSON.stringify(item))).size !== value.length) add("items are not unique");
    if (node.items) value.forEach((item, index) => errors.push(...validate(node.items, item, `${at}[${index}]`)));
  }
  if (kindOf(value) === "object") {
    (node.required ?? []).filter((name) => !(name in value)).forEach((name) => add(`missing required ${name}`));
    if (node.additionalProperties === false && node.properties) Object.keys(value).filter((name) => !(name in node.properties)).forEach((name) => add(`unexpected property ${name}`));
    Object.entries(node.properties ?? {}).filter(([name]) => name in value).forEach(([name, sub]) => errors.push(...validate(sub, value[name], `${at}.${name}`)));
  }
  (node.allOf ?? []).forEach((branch) => errors.push(...validate(branch, value, at)));
  if (node.if && node.then && validate(node.if, value, at).length === 0) errors.push(...validate(node.then, value, at));
  return errors;
};

const revision = () => {
  try {
    return execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim();
  } catch {
    return "unknown-revision";
  }
};

const worst = (statuses) =>
  statuses.includes("fail") ? "fail" : statuses.includes("unknown") || statuses.length === 0 ? "unknown" : statuses.includes("exception") ? "exception" : "pass";

export default class EvidenceReporter {
  constructor() {
    this.records = [];
  }

  onTestEnd(test, result) {
    result.attachments
      .filter((attachment) => attachment.name === "ui-evidence" && attachment.body)
      .forEach((attachment) => this.records.push(JSON.parse(attachment.body.toString("utf8"))));
  }

  onEnd() {
    const bySurface = Map.groupBy(this.records, (record) => record.surface);
    const surfaces = [...bySurface.entries()].map(([id, records]) => {
      const checks = records.filter((record) => record.type === "check");
      const groups = records.filter((record) => record.type === "group");
      const actionsRecord = records.find((record) => record.type === "actions");
      const actions = actionsRecord?.actions ?? [];
      const statuses = [...checks, ...groups, ...actions].map((record) => record.status);
      return {
        id,
        path: actionsRecord?.path ?? "/",
        kind: actionsRecord?.kind ?? "other",
        status: actionsRecord ? worst(statuses.filter((status) => status !== "not-applicable")) : "unknown",
        viewportWidths: [320, 390, 768, 1280],
        alignmentGroups: groups.map(({ id: groupId, relation, rationale, targets, evidence, status }) => ({
          id: groupId,
          relation,
          toleranceCssPx: 1,
          targets,
          rationale,
          evidence: [...evidence, `group status: ${status}`],
        })),
        actions,
        evidence: [
          ...checks.map((check) => `${check.ruleId} ${check.status.toUpperCase()} @${check.width}px ${check.check}: ${check.reason}${check.escapes.length ? ` (${check.escapes.join(" | ")})` : ""}`),
          ...(actionsRecord ? [`ULI-A10: ${actionsRecord.inventory} enabled action(s) inventoried; every one exercised or destination-verified`] : []),
        ],
        unknownScope: ["right-to-left and translated text: the UI ships English only; not exercised"],
      };
    });
    const applied = declaredExceptions.flatMap((exception) =>
      this.records
        .filter((record) => record.type === "check" && record.status === "exception" && exception.surfaces.includes(record.surface) && record.exception?.ruleId === exception.ruleId && record.exception?.check === exception.check && record.exception?.owner === exception.owner)
        .map((record) => record.surface)
        .filter((surface, index, all) => all.indexOf(surface) === index)
        .map((surface) => ({
          ruleId: exception.ruleId,
          surface,
          reason: exception.reason,
          owner: exception.owner,
          reviewTrigger: exception.reviewTrigger,
          evidence: this.records
            .filter((record) => record.surface === surface && record.status === "exception" && record.exception?.check === exception.check && record.exception?.owner === exception.owner)
            .map((record) => `@${record.width}px ${record.check}: ${record.reason}`),
        })),
    );
    const document = {
      standardVersion: "1.0.0",
      repository: "kemiller2002/dokimos",
      revision: revision(),
      classification: "app-ui",
      surfaces,
      exceptions: applied,
      notes:
        "Measured by site/test/ui-conformance (Playwright with @echelon-foundry/forma-conformance 0.1.0 vendored verbatim from forma PR #158). Surfaces: the public site (dist/, deployed to dokimos.echelonfoundry.com), the results UI and the printable report rendered from results-ui/test/fixtures/regression-results.json. Actions run at 1280 CSS px; layout at 320/390/768/1280 and 200% text (390, 1280); print at A4 with a Chromium PDF.",
    };
    const errors = validate(schema, document);
    const out = process.env.UI_CONFORMANCE_EVIDENCE ?? path.join(root, "test-results", "ui-conformance", "evidence.json");
    fs.mkdirSync(path.dirname(out), { recursive: true });
    fs.writeFileSync(out, `${JSON.stringify(document, null, 2)}\n`);
    const all = surfaces.flatMap((surface) => [...this.records.filter((record) => record.surface === surface.id && record.type !== "actions").map((record) => record.status), ...surface.actions.map((action) => action.status)]);
    const count = (status) => all.filter((value) => value === status).length;
    console.log(`\nui:conformance evidence ${out}: ${count("pass")} pass, ${count("fail")} fail, ${count("unknown")} unknown, ${count("not-applicable")} not-applicable, ${count("exception")} exception`);
    surfaces.forEach((surface) => console.log(`  ${surface.status.toUpperCase()} ${surface.id} (${surface.path}): ${surface.actions.length} action(s), ${surface.alignmentGroups.length} group(s)`));
    if (errors.length > 0) {
      console.error(`evidence does not validate against ui-conformance.schema.json:\n  ${errors.join("\n  ")}`);
      process.exitCode = 1;
      return { status: "failed" };
    }
    return undefined;
  }
}
