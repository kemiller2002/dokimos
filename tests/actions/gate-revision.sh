#!/usr/bin/env bash
# Regression test for actions/quality-gate/gate.sh's `revision` input: evidence
# identifies GITHUB_SHA by default, the caller's revision when one is given
# (the pull-request head an unattended merge is authorized for), and a
# malformed revision is refused before anything is measured.
set -euo pipefail

gate="$(cd "$(dirname "$0")/../.." && pwd)/actions/quality-gate/gate.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git init -q "$work/repo"
git -C "$work/repo" -c user.name=test -c user.email=test@example.invalid commit -q --allow-empty -m init

cat > "$work/fake-dokimos" <<'FAKE'
#!/usr/bin/env bash
echo "$*" >> "$FAKE_LOG"
case "$1" in
  version) echo '{"DokimosVersion": "0.0.0-test"}' ;;
  snapshot) exit 7 ;;
esac
FAKE
chmod +x "$work/fake-dokimos"

run() {
  : > "$work/log"
  : > "$work/output"
  (cd "$work/repo" && env -i PATH="$PATH" HOME="$work" FAKE_LOG="$work/log" DOKIMOS="$work/fake-dokimos" \
    GITHUB_OUTPUT="$work/output" GITHUB_REPOSITORY=owner/repo GITHUB_SHA="$1" DOKIMOS_REVISION="$2" \
    DOKIMOS_OUT="$work/artifacts" RUNNER_TEMP="$work" bash "$gate" > /dev/null 2>&1) || true
}

fail() { echo "FAIL $1" >&2; exit 1; }

merge_commit="$(printf 'a%.0s' $(seq 40))"
head_commit="$(printf 'b%.0s' $(seq 40))"

run "$merge_commit" ""
grep -q -- "--revision $merge_commit" "$work/log" || fail "without a revision the evidence identifies GITHUB_SHA"
echo "PASS default revision is GITHUB_SHA"

run "$merge_commit" "$head_commit"
grep -q -- "--revision $head_commit" "$work/log" || fail "the given revision is the one the evidence identifies"
grep -q -- "--revision $merge_commit" "$work/log" && fail "GITHUB_SHA must not be used when a revision is given"
echo "PASS an explicit revision is the one measured"

run "$merge_commit" "not-a-commit"
grep -q "^snapshot" "$work/log" && fail "a malformed revision must be refused before measuring"
grep -q "disposition=revision-invalid" "$work/output" || fail "a malformed revision reports revision-invalid"
echo "PASS a malformed revision is refused"
