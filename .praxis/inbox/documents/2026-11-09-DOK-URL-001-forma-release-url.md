# Pending Praxis intake: DOK-URL-001 the pinned Forma release URL returns 404

**Status:** Unprocessed source material. This is **not** a canonical requirement record, work item, approved decision, or proof of implementation.

- Submitted: 2026-11-09
- Source: an audit of Echelon brand-file consumers for EDS-ADR-002 (owner directive, 2026-11-09)

## Finding

`package.json` and `package-lock.json` pin `@echelon-foundry/design-system` (Forma) 0.3.0 at `https://github.com/kemiller2002/echelon-design-system/releases/download/v0.3.0/...`. Forma's repository used to be named `echelon-design-system`, and GitHub redirected the old URL. On 2026-11-09 a new repository with that name was created for the Echelon design standard, which ended the redirect, so the URL now returns 404 and `npm ci` cannot fetch the package.

## Requirement

**DOK-URL-001.1** Pin the identical artifact at its canonical URL, `https://github.com/kemiller2002/forma/releases/download/v0.3.0/echelon-foundry-design-system-0.3.0.tgz`. The lockfile's sha512 integrity is unchanged, because it is the same tarball. Upgrading Forma is out of scope.
