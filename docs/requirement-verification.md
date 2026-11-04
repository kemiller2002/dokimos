# Manifest-driven requirement verification

`dokimos verification requirements` verifies an application against a Praxis
verification manifest (`praxis.verification-manifest/1`, compiled by `praxis
requirements manifest compile`). It produces `dokimos.requirement-verification/1`.

The manifest is compiled from requirements alone, so the obligations are fixed
before the application is looked at. Dokimos never lets the application, its
tests or an agent decide a verdict. Each verdict comes from three independent
kinds of evidence:

| Evidence | Used for | Source |
|---|---|---|
| Application source | V1 `mapped`: every mapped file must carry `data-requirement-id="<ID>"` | `--root` |
| Browser observations | every other check: facts a probe recorded (tag, role, accessible name, visibility, disabled, values read back) | `--observations` FILE or DIR (`dokimos.requirement-observations/1`) |
| Executed test results | proof that the probe's test ran and passed | `--results` (Playwright JSON) |

```bash
dokimos verification requirements \
  --manifest verification-manifest.json \
  --observations test-results/requirement-observations \
  --results test-results/playwright.json \
  --root . --candidate "$(git rev-parse HEAD)" \
  [--screen investigation-start] [--requirement INV-006]... [--fact name=true|false]... \
  --out requirement-verification.json [--ordo-evidence DIR --run-id ID]
```

Exit codes:

- `0`: accepted. Every mandatory, applicable obligation in scope is PASS.
- `4`: not accepted.
- `3`: the manifest is unavailable.
- `2`: bad arguments.

## Statuses

| Status | Meaning |
|---|---|
| `PASS` | The test executed and passed, and the recorded facts satisfy the manifest's rule for the check. |
| `FAIL` | The facts contradict the rule, or the test failed. |
| `BLOCKED` | The evidence is unusable: observations or results missing, stale against another manifest or commit, or an applicability fact not recorded. |
| `NOT_RUN` | No executed test exercised the obligation; its test was skipped or never ran; or the probe did not exercise a rule (for example, never tried a value longer than `maxLength`). |
| `NOT_APPLICABLE` | A declared manifest fact has another value. Never inferred from missing implementation. |

A requirement takes the worst status of its obligations: FAIL, then BLOCKED,
then NOT_RUN, then PASS. A failed requirement names its class from the
lowest failed level:

- `structural` (V1–V2);
- `interaction` (V3);
- `behavioural` (V4);
- `lifecycle` (V5).

The summary keeps three counts apart:

- **implemented**: every V1 obligation passed;
- **verified**: the requirement passed outright;
- **applicable**: the requirements in scope that apply.

`verifiedPercent` is verified ÷ applicable, never implemented ÷ applicable.

## What each check requires

| Check | Requires |
|---|---|
| `exists` | Exactly one element in the probed state carries the ID, or distinct `data-requirement-instance` keys for a repeated requirement. Each element must be a kind the component allows (`RequirementJudging.componentMatches`). An ID on a hidden `<span>` does not satisfy a `text-input`. |
| `visible` | `exists`, and every carrying element is visible. |
| `accessible` | `visible`, plus a non-empty accessible name and a semantic role. |
| `editable` | `visible`, not disabled and not read-only. |
| `enter` / `select` | `editable`; each valid value tried reads back unchanged. A select must offer exactly the manifest's options. |
| `activate` | Visible and enabled, and the observed outcome equals the expected outcome. |
| `validation` | Every value tried is accepted exactly when the manifest's rules accept it. At least one valid value and one violation of each rule (`minLength`, `maxLength`, `pattern`, `options`) must be tried. |
| `rejects` | Every invalid value is rejected, each rule is violated at least once, and state is unchanged. |
| `outcome`, `save`, `reload`, `update`, `isolation` | The recorded expected value equals the observed value: what the store holds, or what the page shows after reload. |

### Which elements satisfy a component

`exists` accepts an element only when its kind matches the requirement's
`component` (`RequirementJudging.componentMatches`, mirroring Forma's
`RequirementIdentity`). A component with no entry here fails every element.

| Component | Elements that satisfy it |
|---|---|
| `text-input` | `<input>` of type text, search, email, url, tel, number, date, datetime-local, password, or no type |
| `url-input` | `<input>` of type `url`, `text`, or no type |
| `textarea` | `<textarea>` |
| `select` | `<select>` |
| `checkbox` | `<input type=checkbox>` |
| `radio-group` | `<fieldset>`, or any element with role `radiogroup` |
| `button` | `<button>`, or `<input>` of type submit or button |
| `link` | `<a>` |
| `display` | any element except input, textarea, select, button, script, style, template |
| `region` | section, form, main, nav, aside, dialog, div, fieldset |

`url-input` also accepts `type=text` and a missing type (which renders as
text) because a URL field rendered as text with pattern validation is common.
No Forma or Praxis record in this repository constrains the element type
further; whether the value is a valid URL is the manifest's `pattern` rule,
judged by `validation` and `rejects`, not by the element kind.

`required` makes a requirement's obligations mandatory. It does not make the
value non-empty: emptiness is decided by `minLength` alone.

## Writing probes

A probe records facts and never asserts the requirement itself. A missing
field is then a recorded fact (no element) that reaches Dokimos as FAIL. It
does not show up as an aborted run, which would only read as NOT_RUN.
Probe values come from the manifest: the rules to violate are the manifest's.
See `tests/browser/requirements/` in `kemiller2002/indy-init-rehearsal-008`
for a complete example.

Observation files carry the run ID, the source commit and the manifest's
`contentHash`. Several per-test files in one directory are merged; files from
different runs, manifests or commits are refused.

## Ordo

`--ordo-evidence DIR` writes one `causa.acceptance-evidence/1` result per
applicable requirement, the shape `ordo verification schedule
--evidence-manifest` already ingests, with the failed obligation IDs.

## Adversarial cases

`tests/Dokimos.Core.Tests/RequirementVerificationTests.fs` gives the verifier
each defect below and requires that it is not accepted:

- a removed field;
- a removed test;
- the ID on an unrelated hidden element;
- a disabled input;
- input that is not saved;
- the wrong value saved;
- a duplicated ID;
- an invalid manifest;
- evidence from an unexecuted test;
- stale observations;
- a tampered manifest;
- another commit;
- a test-infrastructure failure.

## Adopting it in another Echelon application

Four steps, one per system, with nothing application-specific in the tools.
The example application needs a required "Customer name" field.

**1. Requirements repository: declare the requirement.** Add it to a
`praxis.requirement-catalog/1` file that lives with the authoritative
requirements, not with the application:

```json
{ "id": "CUS-001", "type": "ui-field", "screen": "customer-create", "name": "Customer name",
  "component": "text-input", "required": true, "editable": true,
  "validation": { "minLength": 1, "maxLength": 120 },
  "verification": ["mapped", "exists", "visible", "accessible", "editable", "enter", "validation", "rejects", "save", "reload"],
  "sources": [{ "document": "docs/SCREENS.md", "requirement": "S03" }] }
```

`praxis requirements manifest compile` now creates ten mandatory obligations,
`CUS-001/V1/mapped` through `CUS-001/V5/reload`. Until each one passes, the
application is not accepted.

**2. Application: render the identity on the control itself.** With Forma
markup in F#:

```fsharp
RequirementIdentity.identify TextInput "CUS-001" (field "customer-name" "Customer name" input)
```

With hand-written HTML:

```html
<input data-requirement-id="CUS-001" …>
```

Then map it in a `praxis.requirement-implementation-map/1`:

```json
{ "requirement": "CUS-001", "location": "web/index.html" }
```

**3. Application: probe it.** Copy `tests/browser/requirements/support/probe.js`
from kemiller2002/indy-init-rehearsal-008. Then write a Playwright test that
drives the real page and records facts per obligation:

- `probe.elements(page, "CUS-001", ["exists", "visible", "accessible", "editable"])`;
- the values it entered and read back (`enter`);
- the manifest's own boundary values (`validation`, `rejects`; take them from
  `requirement("CUS-001").validation`);
- what the store held after save, and what the page showed after reload.

The test records and does not assert, and it flushes in `finally`.

**4. CI: compile, probe, verify, schedule.** Copy
`.github/workflows/requirement-verification.yml` from the same repository and
change the catalog pin. It compiles the manifest from the pinned
requirements commit and runs the probes. Then it verifies with this command,
publishes the evidence and fails acceptance unless every mandatory obligation
passed. `tools/requirement-mutations.fsx` shows how to prove that the
pipeline catches a removed, hidden, disabled or duplicated field and a
removed test.

Nothing in steps 2 to 4 can shrink the obligations. Leaving out the field,
the mapping or the probe turns obligations into FAIL or NOT_RUN, never into
PASS.
