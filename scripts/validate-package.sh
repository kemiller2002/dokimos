#!/usr/bin/env bash
# Packs the Dokimos .NET tool and proves it installs and runs from the
# produced package alone, in a clean temporary tool path with an isolated
# NuGet configuration (no access to the source tree at run time).
#
#   scripts/validate-package.sh [output-dir]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:-$root/artifacts/package}"
version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")
package="EchelonFoundry.Dokimos.Cli"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$out"
dotnet pack "$root/src/Dokimos.Cli/Dokimos.Cli.fsproj" --configuration Release --output "$out" ${SOURCE_REVISION_ID:+-p:SourceRevisionId=$SOURCE_REVISION_ID} >/dev/null
nupkg="$out/$package.$version.nupkg"
[ -f "$nupkg" ] || { echo "package not produced: $nupkg" >&2; exit 1; }

# Isolated feed and config: only the produced package is visible.
mkdir -p "$work/feed" "$work/tools" "$work/consumer/src"
cp "$nupkg" "$work/feed/"
cat > "$work/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="local" value="$work/feed" /></packageSources></configuration>
XML
dotnet tool install "$package" --version "$version" --configfile "$work/nuget.config" --tool-path "$work/tools" >/dev/null

cd "$work/consumer"
reported=$("$work/tools/dokimos" version | sed -n 's/.*"DokimosVersion": "\([^"+]*\).*/\1/p')
[ "$reported" = "$version" ] || { echo "installed tool reports '$reported', expected '$version'" >&2; exit 1; }

# Exercise the installed tool end to end on a throwaway consumer.
printf 'module Consumer\n\nlet add x y = x + y\n' > src/Consumer.fs
printf '    0 Warning(s)\n    0 Error(s)\n' > build.log
"$work/tools/dokimos" snapshot src --repository consumer/test --revision 0000000 --build-log build.log > snapshot.json
printf '{"schemaVersion":"1.1.0","baseline":"self","requiredEvidence":["build.compiler-errors"],"ratchets":[{"metricId":"build.compiler-errors","metricVersion":1,"bestAccepted":0,"preference":"lower-is-better","disposition":"fail"}]}' > policy.json
"$work/tools/dokimos" evaluate --baseline snapshot.json --current snapshot.json --policy policy.json > evaluation.json
set +e
"$work/tools/dokimos" evaluate --baseline missing.json --current snapshot.json --policy policy.json > /dev/null 2>&1
missing=$?
set -e
[ "$missing" = "3" ] || { echo "expected exit 3 for missing baseline, got $missing" >&2; exit 1; }

sha=$(sha256sum "$nupkg" | cut -d' ' -f1)
echo "{\"package\":\"$package\",\"version\":\"$version\",\"file\":\"$(basename "$nupkg")\",\"sha256\":\"$sha\",\"installed\":true,\"reportedVersion\":\"$reported\"}"
