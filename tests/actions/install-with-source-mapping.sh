#!/usr/bin/env bash
# Regression test for actions/quality-gate/install.sh: a consumer whose
# NuGet.config declares package source mapping (as Conditor's release-asset
# feed does) must still get the pinned Dokimos tool. Runs the real script
# against the published release named by DOKIMOS_VERSION.
set -euo pipefail

script="$(cd "$(dirname "$0")/../.." && pwd)/actions/quality-gate/install.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

consumer="$work/consumer"
mkdir -p "$consumer" "$work/runner-temp"
cat > "$consumer/NuGet.config" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="echelon-vendor" value="vendor/nuget" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="echelon-vendor">
      <package pattern="EchelonFoundry.Arca.Core" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML

output="$work/github-output"
(
  cd "$consumer"
  RUNNER_TEMP="$work/runner-temp" GITHUB_OUTPUT="$output" DOKIMOS_VERSION="${DOKIMOS_VERSION:?set DOKIMOS_VERSION}" bash "$script"
)

command="$(sed -n 's/^command=//p' "$output")"
if [ ! -x "$command" ]; then
  echo "install.sh did not report an installed dokimos command (got '$command')" >&2
  exit 1
fi
"$command" version >/dev/null
echo "installed with package source mapping in the consumer workspace: $command"
