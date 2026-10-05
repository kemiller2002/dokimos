# Operating Dokimos in a repository

This is the Dokimos-side contract for installing and running Dokimos anywhere
(requirements `DOK-OPS-007` … `DOK-OPS-012`, `DOK-OPS-025`).

## The loop

```
build (log) + test (TRX) + git history
  → dokimos snapshot            immutable, schema 2.0.0, content-derived identity
  → evidence store              branch `dokimos-evidence`, never the product branch
  → accepted baseline           immutable acceptance records in the store
  → dokimos compare             schema dokimos.comparison 1.0.0
  → dokimos evaluate            schema dokimos.evaluation 1.0.0, exit code = CI result
  → persist + upload            store commit on default-branch pushes; run artifact always
  → dokimos history             schema dokimos.history 1.0.0
```

## Consumer installation (reusable action)

The smallest pinned declaration:

```yaml
- uses: kemiller2002/dokimos/actions/quality-gate@<full-commit-sha>
  with:
    version: 0.1.0                  # exact release; never floats
    package-sha256: <sha256>        # recommended; from dokimos-checksums.txt
    source: src
    policy: .dokimos/policy.json
    build-log: artifacts/dokimos/build.log
    test-results: artifacts/dokimos/test-results
```

`dokimos init` writes a complete pinned workflow (`.github/workflows/dokimos.yml`),
a default policy (`.dokimos/policy.json`) and an installation record
(`.dokimos/installation.json`). It is idempotent and never overwrites an
existing file. `dokimos status`, `dokimos verify`, and `dokimos doctor` are
read-only health checks. `dokimos upgrade` changes only Dokimos-owned lifecycle
files and preserves the policy, which becomes user-owned after creation.

These operations implement Echelon Registry's generic repository lifecycle
contract `echelon.repository-lifecycle` v1, declared in the release `provides`.
A Registry-driven installer such as Conditor invokes only
`dokimos version` and `dokimos status|init|verify|doctor|upgrade --root <repo>`;
it never reads or writes `.dokimos/` or the generated workflow itself.

The action:

1. installs `EchelonFoundry.Dokimos.Cli` at the exact version from the GitHub
   release `dokimos-v<version>`, verifying its SHA-256, and checks that the tool
   reports that version;
2. unshallows the checkout when needed and captures Git history;
3. snapshots the configured sources with the supplied build/test/coverage evidence
   (absent evidence is recorded as unavailable, never zero);
4. checks out the evidence branch into a separate worktree;
5. resolves the baseline: store acceptance → `initial-baseline` file → the run
   itself (first run, reported explicitly);
6. compares, evaluates, writes a job summary with every non-passing outcome;
7. on pushes to the default branch (or `persist: true`) stores the snapshot,
   optionally accepts it as baseline, commits and pushes the evidence branch;
8. uploads all evidence files as a run artifact, even when later stages fail;
9. exits with the Dokimos exit code (`enforce: true`).

The consumer job needs `permissions: contents: write` to persist evidence.
Pull requests never persist.

### Initial baseline

The first default-branch run stores a snapshot and compares it with itself.
Accept it deliberately: run the workflow manually with `accept-baseline: true`
and a reason. Acceptances are immutable files under `baselines/<name>/` in the
store; the newest acceptance is the current baseline.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Evaluation completed and policy permits continuation (passed or passed with warnings) |
| 1 | Unexpected operational fault (filesystem, process); diagnostic on stderr |
| 2 | Invalid invocation or configuration (arguments, policy file) |
| 3 | Required evidence unavailable or invalid; incompatible snapshots |
| 4 | Policy failure |
| 5 | Evidence-store identity conflict (immutability violation) |
| 6 | Invalid, incomplete or expired quality exception (`ratchet check`, `evaluate/results --exceptions`) |

Canonical JSON is written to stdout; `dokimos.diagnostic` JSON to stderr.

## Change-quality ratchet

`dokimos ratchet check --build-log <log> --json` judges the working tree
against the accepted `quality/baseline.json` and `quality/exceptions.json`:

- 0 is `pass`;
- 4 is `regression`;
- 6 is `invalid-exceptions`;
- 3 is `unavailable`.

The output is the `dokimos.ratchet` 1.0.0 contract. Adopt it with
`dokimos ratchet baseline init --repository <owner/name> --write`. The rule
catalog, contract, exception process and waiver migration are in
[`docs/quality/RATCHET-RULES.md`](../quality/RATCHET-RULES.md).

`dokimos identity` checks that `ros.json` identifies the repository it is in:

- 0 is consistent;
- 4 is a mismatch;
- 3 is undetermined.

## Evidence store

Store schema 1.0.0 (`dokimos-store.json` marker):

```
snapshots/<sha256(snapshot-id)[0..24]>.json   one immutable file per snapshot
baselines/<name>/<accepted-at>.json            immutable baseline acceptances
```

Files are created with create-new semantics. Re-storing identical evidence is a
no-op; a different snapshot under an existing identity, or a snapshot whose
claimed identity does not match its content, is rejected (exit 5). Comparison,
evaluation and history depend only on the `EvidenceStore` record, so a
filesystem, object-storage, database or service implementation can replace the
Git-branch checkout without changing domain logic.

## Conditor

Conditor's lifecycle contract (`conditor/docs/component-contract.md`) requires
`init`, `verify` and `doctor`. Dokimos implements all three:

| Conditor operation | Dokimos command |
|---|---|
| init | `dokimos init --version <v> --action-ref <sha> [--package-sha256 <sha>] [--source <dir>]...` |
| status | `dokimos status` |
| verify | `dokimos verify` |
| doctor | `dokimos doctor` |
| upgrade | `dokimos upgrade --version <v> --action-ref <sha> [--package-sha256 <sha>] [--source <dir>]...` |

Proposed descriptor (`conditor/components/dokimos.component.json`):

```json
{
  "schemaVersion": 1,
  "id": "dokimos",
  "displayName": "Dokimos",
  "distribution": "nuget",
  "package": "EchelonFoundry.Dokimos.Cli",
  "defaultVersion": "0.1.0",
  "qualifiedVersions": ["0.1.0"],
  "command": "dokimos",
  "initArguments": ["init"],
  "verifyArguments": ["verify"],
  "doctorArguments": ["doctor"],
  "upgradeArguments": ["init"]
}
```

**Remaining external gap (DOK-OPS-026, owned by Conditor):** Registry now owns
the native distribution contract and Dokimos publishes self-contained artifacts
plus component-owned lifecycle commands. Conditor still needs its Registry-driven
distribution resolver (`kemiller2002/conditor#12`) to select the platform artifact,
verify its digest, execute the lifecycle operation, and record the verified
postcondition without repository-specific Dokimos download logic.

## Echelon registration

- System manifest: [`echelon/dokimos.system.json`](../../echelon/dokimos.system.json)
  (`echelon.system/v1`): id `dokimos`, repository, version, executable, and
  provided capabilities `quality.snapshot` (contract 2, snapshot schema 2.0.0),
  `quality.compare` (1), `quality.evaluate` (1), `quality.history` (1).
- Catalog entry: `echelon-registry/registry/systems.json` lists `dokimos`
  (kemiller2002/echelon-registry PR #4, `9fe59a5`; work item `DOK-OPS-027`),
  so `project-administration` accepts installations.
- Every release publishes `dokimos.release.json` (`echelon.release/v2`) through
  the pinned shared Echelon Registry release-contract action, including release
  stage, distribution class, executable identity, immutable source commit,
  platform artifact identities, and SHA-256 digests.
- Installations are recorded through `installation.register`
  (`project-administration`), never in a parallel registry. The payload for a
  repository installation:

```json
{
  "systemId": "dokimos",
  "systemVersion": "0.1.0",
  "target": { "kind": "repository", "id": "<owner>/<repo>" },
  "occurredAt": "<timestamp>",
  "source": { "repository": "kemiller2002/dokimos", "distribution": "github-release", "release": "dokimos-v0.1.0", "artifact": "EchelonFoundry.Dokimos.Cli.0.1.0.nupkg", "digest": "sha256:<hex>" },
  "evidence": [
    { "kind": "dokimos-installation", "reference": ".dokimos/installation.json", "digest": null },
    { "kind": "dokimos-policy", "reference": ".dokimos/policy.json", "digest": "<policy Identity from evaluation>" }
  ]
}
```

The installation record's `ConfigurationVersion` (currently 1) and the policy
`schemaVersion` identify the installation/config version.

## Releases

`.github/workflows/release.yml` publishes only for a tag `dokimos-v<Version>` on
a commit already on `main`, or a manual dispatch from `main` naming the exact
version declared in `Directory.Build.props`. It restores, builds, tests, runs
the Dokimos gate on itself, packs, installs the package into a clean tool path
(`scripts/validate-package.sh`), builds native CLIs, writes checksums and the
Echelon release manifest, attests build provenance, and creates the GitHub
release. nuget.org publication runs only when the repository variable
`DOKIMOS_NUGET_PUBLISH` is `true` and nuget.org Trusted Publishing is configured
(`DOKIMOS_NUGET_USER`, environment `nuget`).
