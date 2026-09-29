#!/usr/bin/env bash
# Writes the echelon.release/v1 manifest for a Dokimos release from the
# produced checksums, so installers resolve artifacts by digest.
#   scripts/release-manifest.sh <version> <commit> <dist-dir>
set -euo pipefail
version="$1"; commit="$2"; dist="$3"
python3 - "$version" "$commit" "$dist" <<'PY'
import json, re, sys, pathlib
version, commit, dist = sys.argv[1], sys.argv[2], pathlib.Path(sys.argv[3])
root = pathlib.Path.cwd()
system = json.loads((root / "echelon" / "dokimos.system.json").read_text())
artifacts = []
for line in (dist / "dokimos-checksums.txt").read_text().splitlines():
    digest, name = line.split(maxsplit=1)
    name = name.lstrip("*")
    m = re.match(r"dokimos-(.+)\.(tar\.gz|zip)$", name)
    artifacts.append({"name": name, "platform": m.group(1) if m else None, "sha256": digest})
manifest = {
    "schema": "echelon.release/v1",
    "systemId": "dokimos",
    "version": version,
    "repository": system["repository"],
    "tag": f"dokimos-v{version}",
    "commit": commit,
    "executable": system["executable"],
    "provides": system["provides"],
    "distributions": [
        {"channel": "github-release", "package": None, "url": f"https://github.com/{system['repository']}/releases/tag/dokimos-v{version}"},
        {"channel": "nuget", "package": "EchelonFoundry.Dokimos.Cli", "url": None},
    ],
    "artifacts": sorted(artifacts, key=lambda a: a["name"]),
}
(dist / "dokimos.release.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(json.dumps(manifest, indent=2))
PY
