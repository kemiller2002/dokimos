#!/usr/bin/env bash
# EXPERIMENTAL rule-provenance audit (VAC-002-P3-03-R1, VC-004). Not part of the
# default Dokimos CLI or CI.
#
#   ./run.sh                 build, test, and run on the shared invoice fixture (writes out/)
#   ./run.sh build|test|fixture
#   ./run.sh dev             DEVELOPMENT-SET scoring on the VAC-002-P3 corpus (needs the
#                            cohort-based-engineering checkout; biased, not confirmatory)
#   ./run.sh integrate --ordo-src DIR --trace FILE --dll FILE \
#                      [--handoff FILE] [--legacy PATH]... [--out FILE] [extra audit options]
#
# Exit codes follow Dokimos: 0 pass / pass-with-owner-questions, 2 invalid input,
# 3 evidence unavailable, 4 fail.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"
if [ -x /root/.dotnet/dotnet ] && ! command -v dotnet >/dev/null 2>&1; then
  export PATH=/root/.dotnet:$PATH DOTNET_ROOT=/root/.dotnet
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

FIXTURE=contracts/fixtures/invoice-slice
CLI=src/RuleProvenance.Cli/bin/Debug/net10.0/rule-provenance.dll
SDK="$(dotnet --version)"

build() { dotnet build tests/RuleProvenance.Tests -nologo -v quiet; }
cli() { dotnet "$CLI" "$@"; }

run_tests() { dotnet test tests/RuleProvenance.Tests --no-build -nologo; }

fixture() {
  mkdir -p out
  echo "== legacy audit of the shared fixture + oracle on the built-in reference model"
  cli audit --root "$FIXTURE" --handoff "$FIXTURE/handoff.json" --legacy "$FIXTURE/legacy" \
      --probe-model reference --sdk-version "$SDK" \
      --out out/fixture-audit.json --validation-out out/checker-validation.json
  echo "== seeded mutant (drop the limit): expect a proven, blocking invariant-violation (exit 4)"
  set +e
  cli audit --root "$FIXTURE" --handoff "$FIXTURE/handoff.json" --legacy "$FIXTURE/legacy" \
      --probe-model mutant:drop-limit --sdk-version "$SDK" --out out/mutant-drop-limit-audit.json
  code=$?
  set -e
  [ "$code" -eq 4 ] || { echo "expected exit 4 for the mutant, got $code" >&2; exit 1; }
  echo "== reuse check of the fixture audit against unchanged inputs (expect reusable)"
  cli reuse --prior out/fixture-audit.json --root "$FIXTURE" --handoff "$FIXTURE/handoff.json" \
      --legacy "$FIXTURE/legacy" --probe-model reference --sdk-version "$SDK" --out out/reuse-check.json
  if command -v python3 >/dev/null 2>&1 && python3 -I -c 'import jsonschema' 2>/dev/null; then
    python3 -I tools/validate_json.py contracts/schemas/echelon-provenance-audit-0.1.schema.json \
      out/fixture-audit.json out/mutant-drop-limit-audit.json
  else
    echo "(python3 jsonschema unavailable: schema validation of out/ skipped)"
  fi
  echo "reports in $HERE/out/"
}

integrate() {
  local handoff="$FIXTURE/handoff.json" has_legacy=0 args=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --handoff) handoff="$2"; shift 2 ;;
      --legacy) has_legacy=1; args+=("$1" "$2"); shift 2 ;;
      *) args+=("$1"); shift ;;
    esac
  done
  [ "$has_legacy" -eq 1 ] || args+=(--legacy "$FIXTURE/legacy")
  [ -f "$CLI" ] || build
  cli integrate --handoff "$handoff" --sdk-version "$SDK" "${args[@]}"
}

case "${1:-all}" in
  all) build; run_tests; fixture ;;
  build) build ;;
  test) build; run_tests ;;
  fixture) [ -f "$CLI" ] || build; fixture ;;
  integrate) shift; integrate "$@" ;;
  dev) shift; [ -f "$CLI" ] || build; python3 -I dev/score_dev.py --summary dev/dev-scores.json "$@" ;;
  -h|--help) sed -n '2,14p' "$0" ;;
  *) echo "unknown mode: $1" >&2; exit 2 ;;
esac
