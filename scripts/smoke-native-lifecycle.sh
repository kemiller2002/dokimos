#!/usr/bin/env bash
# Smoke-tests a packaged Dokimos executable against the Echelon repository
# lifecycle contract (echelon.repository-lifecycle v1):
#   scripts/smoke-native-lifecycle.sh <dokimos-executable> <version> <commit>
# Verifies the version identity, then init/verify/status/doctor on a clean
# repository, and proves a second init and upgrade change nothing.
set -euo pipefail
exe="$1"; version="$2"; commit="$3"

DOKIMOS_IDENTITY=$("$exe" version) python3 - "$version" "$commit" <<'PY'
import json, os, sys
version, commit = sys.argv[1], sys.argv[2]
doc = {k.lower(): v for k, v in json.loads(os.environ["DOKIMOS_IDENTITY"]).items()}
expected = {"systemid": "dokimos", "repository": "kemiller2002/dokimos", "executable": "dokimos",
            "releaseversion": version, "sourcecommit": commit}
wrong = {k: (doc.get(k), v) for k, v in expected.items() if doc.get(k) != v}
if wrong:
    sys.exit(f"version identity mismatch: {wrong}")
print(f"identity ok: dokimos {version} @ {commit}")
PY

root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
snapshot() { (cd "$root" && find . -type f -print0 | sort -z | xargs -0 sha256sum); }

"$exe" init --root "$root" >/dev/null
"$exe" verify --root "$root" >/dev/null
"$exe" status --root "$root" >/dev/null
"$exe" doctor --root "$root" >/dev/null
first=$(snapshot)

second_init=$("$exe" init --root "$root")
upgrade=$("$exe" upgrade --root "$root")
"$exe" verify --root "$root" >/dev/null
[ "$first" = "$(snapshot)" ] || { echo "second application changed repository state" >&2; exit 1; }
python3 - <<'PY' "$second_init" "$upgrade"
import json, sys
for label, text in zip(("init", "upgrade"), sys.argv[1:]):
    outcomes = {f["Path"]: f["Outcome"] for f in json.loads(text)["Files"]}
    changed = {p: o for p, o in outcomes.items() if o not in ("unchanged", "preserved-user-owned", "preserved-existing")}
    if changed:
        sys.exit(f"second {label} reported changes: {changed}")
print("second application ok: no repository drift")
PY
