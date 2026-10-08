# Forma icon usage audit (Dokimos)

The first-pass audit is an opt-in, dependency-free **static HTML** check. It does not load a web app or evaluate executable templates. The consumer must provide the **exact version-pinned Forma registry**, normally the released package's `dist/icons/registry.json`, and a directory of built HTML files.

```sh
node tools/icon-usage-audit.mjs --root site-dist --registry node_modules/@echelon-foundry/design-system/dist/icons/registry.json --json
npm run test:icons
```

It reports `file`, one-indexed `line`, `rule`, `severity`, and `message`. Errors exit 1; findings requiring review are warnings; incorrect arguments or missing assets exit 2.

Checks include:

- **icon-unknown**: static `data-ef-icon` values and local `/icons/name.svg` are absent from the pinned registry.
- **icon-deprecated**: optional map marks an obsolete name with an explicit current replacement (no implicit behavior migration).
- **icon-control-name**: static native `button` or `a` containing icons has no readable text, `aria-label`, or resolvable nonempty `aria-labelledby`.
- **icon-link-target**: a native icon link lacks an `href`.
- **icon-size**: an explicit pixel `--ef-icon-size` is not 16, 20, 24, 32 or 48.
- **icon-dynamic** / **icon-size-review**: values that cannot be proven statically are reported as warnings, not pretended to be valid.

Optional configuration (e.g. `--deprecated icon-audit-policy.json`):

```json
{ "deprecated": { "legacy-mail": "email" } }
```

This example illustrates the format, not an actual deprecated Forma icon. Never invent deprecations not present in the released contract.

The audit is **read-only**, never fetches a CDN and never changes application state. It does not purport to inspect JSX/TSX/Vue source expressions, compiled F# strings, custom-element runtime state, or the browser accessibility tree. Use Playwright/axe and native role/name locators for those cases, and independently test F# editor event legality. Do not make warnings disappear by inserting arbitrary labels; use accurately named controls. Include the exact Forma package version and CLI output when reporting evidence.

Follow-ups: integrate a version-pinned, optional audit job into the appropriate application CI pipeline after the released package is available. Work item: [Dokimos #25](https://github.com/kemiller2002/dokimos/issues/25).
