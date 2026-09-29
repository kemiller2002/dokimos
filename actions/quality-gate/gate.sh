#!/usr/bin/env bash
# The Dokimos operating loop for one CI run:
#   history -> snapshot -> evidence store -> baseline -> compare -> evaluate
#   -> persist -> summary.
# Every stage's exit code is recorded. Nothing is masked: a stage that cannot
# produce evidence yields Dokimos exit code 3 (evidence unavailable), a policy
# failure yields 4, and only an evaluation that permits continuation yields 0.
set -uo pipefail

out="${DOKIMOS_OUT:-artifacts/dokimos}"
mkdir -p "$out"
store="${RUNNER_TEMP:-/tmp}/dokimos-evidence-store"
branch="${DOKIMOS_EVIDENCE_BRANCH:-dokimos-evidence}"
repo="${GITHUB_REPOSITORY:?}"
sha="${GITHUB_SHA:?}"
ref="${GITHUB_REF_NAME:-}"

dok() { $DOKIMOS "$@"; }  # DOKIMOS may be "dotnet path/Dokimos.Cli.dll": split on purpose

finish() {
  local code="$1" disposition="$2" snapshot_id="${3:-}"
  {
    echo "exit-code=${code}"
    echo "disposition=${disposition}"
    echo "snapshot-id=${snapshot_id}"
    echo "dokimos-version=$(dok version 2>/dev/null | sed -n 's/.*"DokimosVersion": "\([^"]*\)".*/\1/p')"
  } >> "$GITHUB_OUTPUT"
  printf '{"disposition":"%s","exitCode":%s,"snapshotId":"%s"}\n' "$disposition" "$code" "$snapshot_id" > "$out/gate.json"
  exit 0
}

stage_failed() {
  local stage="$1" code="$2"
  echo "::error title=Dokimos ${stage}::stage exited ${code}; see ${out}/${stage}.stderr.json"
  {
    echo "### Dokimos: ${stage} could not produce evidence (exit ${code})"
    echo
    echo '```json'
    cat "$out/${stage}.stderr.json" 2>/dev/null
    echo '```'
  } >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
  # Invalid invocation stays 2; everything else is unavailable evidence.
  if [ "$code" = "2" ]; then finish 2 "${stage}-invalid"; else finish 3 "${stage}-unavailable"; fi
}

dok version > "$out/version.json" 2> "$out/version.stderr.json" || stage_failed version $?

# --- Git temporal evidence: needs full history -----------------------------
if [ "$(git rev-parse --is-shallow-repository)" = "true" ]; then
  git fetch --unshallow --quiet origin || echo "::warning title=Dokimos::Could not unshallow; temporal evidence reflects fetched history only."
fi
read -r -a sources <<< "${DOKIMOS_SOURCE:-src}"
git log --numstat --find-renames --format='commit %H %aI' -- "${sources[@]}" > "$out/git-history.txt" \
  || { echo "::warning title=Dokimos::git log failed; temporal evidence will be unavailable."; rm -f "$out/git-history.txt"; }

# --- Snapshot -----------------------------------------------------------------
args=(snapshot "${sources[@]}" --repository "$repo" --revision "$sha" --ref "$ref")
[ -f "$out/git-history.txt" ] && args+=(--git-history "$out/git-history.txt")
[ -n "${DOKIMOS_BUILD_LOG:-}" ] && args+=(--build-log "$DOKIMOS_BUILD_LOG")
[ -n "${DOKIMOS_TEST_RESULTS:-}" ] && [ -e "$DOKIMOS_TEST_RESULTS" ] && args+=(--test-results "$DOKIMOS_TEST_RESULTS")
[ -n "${DOKIMOS_COVERAGE:-}" ] && args+=(--coverage "$DOKIMOS_COVERAGE")
dok "${args[@]}" > "$out/snapshot.json" 2> "$out/snapshot.stderr.json" || stage_failed snapshot $?
snapshot_id=$(sed -n 's/^  "SnapshotId": "\(.*\)",$/\1/p' "$out/snapshot.json" | head -1)

# --- Evidence store (dedicated branch, never the product branch) -------------
rm -rf "$store"
git worktree prune
if git fetch --quiet origin "refs/heads/${branch}:refs/remotes/origin/${branch}" 2>/dev/null; then
  git worktree add --quiet --detach "$store" "origin/${branch}" || stage_failed store-checkout $?
else
  git worktree add --quiet --detach "$store" || stage_failed store-checkout $?
  git -C "$store" checkout --quiet --orphan "$branch"
  git -C "$store" rm -rf --quiet . >/dev/null 2>&1
  git -C "$store" clean -fdxq
fi
dok store init --store "$store" > /dev/null 2> "$out/store.stderr.json" || stage_failed store $?

# --- Baseline -----------------------------------------------------------------
baseline="$out/baseline.json"
if dok store baseline --store "$store" > "$baseline" 2> "$out/baseline.stderr.json"; then
  baseline_source="evidence-store:${branch}"
elif [ -n "${DOKIMOS_INITIAL_BASELINE:-}" ] && [ -f "$DOKIMOS_INITIAL_BASELINE" ]; then
  cp "$DOKIMOS_INITIAL_BASELINE" "$baseline"
  baseline_source="initial-baseline:${DOKIMOS_INITIAL_BASELINE}"
else
  echo "::notice title=Dokimos::No accepted baseline yet; this run is compared with itself and should be accepted as the first baseline."
  cp "$out/snapshot.json" "$baseline"
  baseline_source="self (no accepted baseline)"
fi
echo "$baseline_source" > "$out/baseline-source.txt"

# --- Compare and evaluate -------------------------------------------------------
dok compare "$baseline" "$out/snapshot.json" > "$out/comparison.json" 2> "$out/compare.stderr.json" || stage_failed compare $?
dok evaluate --baseline "$baseline" --current "$out/snapshot.json" --policy "$DOKIMOS_POLICY" > "$out/evaluation.json" 2> "$out/evaluate.stderr.json"
eval_code=$?
if [ ! -s "$out/evaluation.json" ]; then stage_failed evaluate "$eval_code"; fi
disposition=$(sed -n 's/^  "Disposition": "\(.*\)",$/\1/p' "$out/evaluation.json" | head -1)

# --- Persist ------------------------------------------------------------------
persist="${DOKIMOS_PERSIST:-auto}"
if [ "$persist" = "auto" ]; then
  if [ "${GITHUB_EVENT_NAME:-}" = "push" ] && [ "$ref" = "${DOKIMOS_DEFAULT_BRANCH:-main}" ]; then persist=true; else persist=false; fi
fi
persisted=false
if [ "$persist" = "true" ]; then
  if [[ "$baseline_source" == initial-baseline:* ]]; then
    # Import the repository's accepted baseline so the store owns history from here on.
    dok store put --store "$store" --snapshot "$baseline" > "$out/store-import.json" 2> "$out/store-import.stderr.json" || stage_failed store-import $?
    initial_id=$(sed -n 's/^  "SnapshotId": "\(.*\)",$/\1/p' "$baseline" | head -1)
    accepted_at=$(git log -1 --format=%aI -- "$DOKIMOS_INITIAL_BASELINE" 2>/dev/null || true)
    dok store accept-baseline --store "$store" --snapshot-id "$initial_id" --actor "repository:${DOKIMOS_INITIAL_BASELINE}" \
      --reason "Imported the repository's accepted baseline file" ${accepted_at:+--at "$accepted_at"} > /dev/null 2> "$out/store-import.stderr.json" || stage_failed store-import $?
  fi
  dok store put --store "$store" --snapshot "$out/snapshot.json" > "$out/store-put.json" 2> "$out/store-put.stderr.json" || stage_failed store-put $?
  if [ "${DOKIMOS_ACCEPT_BASELINE:-false}" = "true" ]; then
    dok store accept-baseline --store "$store" --snapshot-id "$snapshot_id" --actor "${GITHUB_ACTOR:-unknown}" \
      --reason "${DOKIMOS_BASELINE_REASON:-accepted via workflow run ${GITHUB_RUN_ID:-}}" > "$out/store-accept.json" 2> "$out/store-accept.stderr.json" || stage_failed store-accept $?
  fi
  git -C "$store" add -A
  if ! git -C "$store" diff --cached --quiet; then
    git -C "$store" -c user.name="dokimos-evidence" -c user.email="dokimos-evidence@users.noreply.github.com" \
      commit --quiet -m "Dokimos evidence for ${repo}@${sha}" -m "Snapshot: ${snapshot_id}" -m "Run: ${GITHUB_SERVER_URL:-}/${repo}/actions/runs/${GITHUB_RUN_ID:-}"
    pushed=false
    for attempt in 1 2 3; do
      if git -C "$store" push --quiet origin "HEAD:refs/heads/${branch}"; then pushed=true; break; fi
      # Another run appended evidence: files are distinct and immutable, so rebase cleanly.
      git -C "$store" fetch --quiet origin "refs/heads/${branch}" && git -C "$store" rebase --quiet FETCH_HEAD || break
      sleep $((attempt * 2))
    done
    [ "$pushed" = "true" ] || stage_failed store-push 3
  fi
  persisted=true
fi
dok history --store "$store" > "$out/history.json" 2> "$out/history.stderr.json" || echo "::warning title=Dokimos::history query failed (exit $?)"

# --- Inspectable summary ----------------------------------------------------------
if command -v jq >/dev/null 2>&1; then
  {
    echo "## Dokimos quality gate: \`${disposition}\` (exit ${eval_code})"
    echo
    echo "- Snapshot: \`${snapshot_id}\`"
    echo "- Baseline: \`$(jq -r .Baseline.SnapshotId "$out/evaluation.json")\` (${baseline_source})"
    echo "- Policy: \`$(jq -r '.Policy.Identity' "$out/evaluation.json")\`"
    echo "- Evidence persisted to \`${branch}\`: ${persisted}"
    echo
    echo "| Outcome | Count |"; echo "|---|---|"
    jq -r '.OutcomeCounts | to_entries[] | "| \(.key) | \(.value) |"' "$out/evaluation.json"
    echo
    echo "| Comparison | Count |"; echo "|---|---|"
    jq -r '.Summary | to_entries[] | "| \(.key) | \(.value) |"' "$out/evaluation.json"
    echo
    echo "### Outcomes that did not pass"
    echo
    echo "| Rule | Subject | Scope | State | Explanation |"; echo "|---|---|---|---|---|"
    jq -r '[.Outcomes[] | select(.State != "passed")] | .[0:60][] | "| \(.Rule) | \(.Subject) | \(.Scope) | \(.State) | \(.Explanation | gsub("\\|"; "/")) |"' "$out/evaluation.json"
  } >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
fi

finish "$eval_code" "$disposition" "$snapshot_id"
