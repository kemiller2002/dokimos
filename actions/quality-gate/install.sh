#!/usr/bin/env bash
# Installs a pinned Dokimos release as a .NET tool, or uses an explicit command.
# Never installs "latest".
set -euo pipefail

if [ -n "${DOKIMOS_COMMAND:-}" ]; then
  echo "command=${DOKIMOS_COMMAND}" >> "$GITHUB_OUTPUT"
  exit 0
fi

if ! [[ "${DOKIMOS_VERSION:-}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "::error title=Dokimos::input 'version' must be an exact release version (got '${DOKIMOS_VERSION:-}')."
  exit 2
fi

package="EchelonFoundry.Dokimos.Cli"
release="https://github.com/kemiller2002/dokimos/releases/download/dokimos-v${DOKIMOS_VERSION}"
feed="${RUNNER_TEMP}/dokimos-feed"
tools="${RUNNER_TEMP}/dokimos-tool"
mkdir -p "$feed" "$tools"
nupkg="${feed}/${package}.${DOKIMOS_VERSION}.nupkg"

curl --fail --silent --show-error --location --retry 3 -o "$nupkg" "${release}/${package}.${DOKIMOS_VERSION}.nupkg"
actual=$(sha256sum "$nupkg" | cut -d' ' -f1)
if [ -n "${DOKIMOS_PACKAGE_SHA256:-}" ]; then
  expected="${DOKIMOS_PACKAGE_SHA256}"
else
  curl --fail --silent --show-error --location --retry 3 -o "${feed}/dokimos-checksums.txt" "${release}/dokimos-checksums.txt"
  expected=$(grep " ${package}.${DOKIMOS_VERSION}.nupkg\$" "${feed}/dokimos-checksums.txt" | cut -d' ' -f1)
  echo "::notice title=Dokimos::Verified against the release checksum file; pin package-sha256 for a stronger guarantee."
fi
if [ "$actual" != "$expected" ]; then
  echo "::error title=Dokimos::Package checksum mismatch (expected ${expected:-none}, got ${actual})."
  exit 3
fi

# Install with a NuGet configuration of our own, from outside the consumer's
# workspace: it clears every source and names only the release feed whose
# package was just verified. The consumer's NuGet.config never applies, so its
# sources cannot supply the tool, and its package source mapping (which makes
# NuGet refuse --add-source) cannot break the install.
config="${RUNNER_TEMP}/dokimos-nuget.config"
cat > "$config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="dokimos-release" value="${feed}" />
  </packageSources>
</configuration>
XML
(cd "$RUNNER_TEMP" && dotnet tool install "$package" --version "$DOKIMOS_VERSION" --configfile "$config" --tool-path "$tools" >/dev/null)
reported=$("${tools}/dokimos" version | sed -n 's/.*"DokimosVersion": "\([^"+]*\).*/\1/p')
if [ "$reported" != "$DOKIMOS_VERSION" ]; then
  echo "::error title=Dokimos::Installed Dokimos reports version '${reported}', expected '${DOKIMOS_VERSION}'."
  exit 3
fi
echo "command=${tools}/dokimos" >> "$GITHUB_OUTPUT"
