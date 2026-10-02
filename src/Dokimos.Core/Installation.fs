namespace Dokimos.Core

open System
open System.Text.RegularExpressions

/// What `dokimos init` establishes in a consumer repository. Every value is
/// explicit and pinned; nothing floats to "latest".
type InstallationRequest =
    { DokimosVersion: string
      ActionRef: string
      PackageSha256: string option
      Sources: string list
      BuildTarget: string option
      EvidenceBranch: string
      DefaultBranch: string }

type InstallationFile =
    { Path: string
      Content: string }

type InstallationCheck =
    { Check: string
      Passed: bool
      Detail: string
      Remediation: string option }

module Installation =
    [<Literal>]
    let ConfigurationVersion = 2

    [<Literal>]
    let PolicyPath = ".dokimos/policy.json"

    [<Literal>]
    let RecordPath = ".dokimos/installation.json"

    [<Literal>]
    let WorkflowPath = ".github/workflows/dokimos.yml"

    [<Literal>]
    let ActionPath = "kemiller2002/dokimos/actions/quality-gate"

    let private sha = Regex("^[0-9a-f]{40}$")
    let private semver = Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")

    /// The release and source revision this Dokimos build came from, parsed
    /// from "<version>+<commit>".
    let releaseOf (informationalVersion: string) =
        match informationalVersion.Split('+', 2) with
        | [| version; commit |] when sha.IsMatch commit -> version, Some commit
        | parts -> parts[0], None

    let validate (request: InstallationRequest) =
        [ if not (semver.IsMatch request.DokimosVersion) then $"version '{request.DokimosVersion}' is not an exact release version"
          if not (sha.IsMatch request.ActionRef) then $"action ref '{request.ActionRef}' must be a full 40-character commit SHA"
          match request.PackageSha256 with
          | Some digest when not (Regex.IsMatch(digest, "^[0-9a-f]{64}$")) -> "package SHA-256 must be 64 lowercase hex characters"
          | _ -> ()
          if request.Sources.IsEmpty then "at least one source directory is required"
          if request.Sources |> List.exists (fun s -> s.Contains ' ') then "source directories must not contain spaces" ]

    let defaultPolicy =
        Contracts.serialize
            {| ``$schema`` = "https://echelonfoundry.com/dokimos/schemas/policy"
               schemaVersion = "1.1.0"
               baseline = "default"
               requiredEvidence = [ "build.compiler-errors"; "tests.total"; "tests.failed" ]
               ratchets =
                [ {| metricId = "build.compiler-errors"; metricVersion = 1; bestAccepted = 0; preference = "lower-is-better"; disposition = "fail" |}
                  {| metricId = "tests.failed"; metricVersion = 1; bestAccepted = 0; preference = "lower-is-better"; disposition = "fail" |} ]
               thresholds = ([]: int list)
               regressions = {| disposition = "warn" |}
               introducedFindings = {| disposition = "warn" |}
               observedNotRatcheted = [ "tests.total"; "tests.passed"; "build.compiler-warnings" ]
               unavailableBehavior = "not-evaluated"
               suppressions = ([]: int list) |}

    let workflow (request: InstallationRequest) =
        let target = request.BuildTarget |> Option.map (fun t -> " " + t) |> Option.defaultValue ""
        let sha256 =
            match request.PackageSha256 with
            | Some digest -> $"\n          package-sha256: {digest}"
            | None -> ""
        String.concat
            "\n"
            [ "# Managed by Dokimos (installed by `dokimos init`; Dokimos " + request.DokimosVersion + "). Pinned: do not float these versions."
              "name: Dokimos"
              ""
              "on:"
              "  push:"
              $"    branches: [{request.DefaultBranch}]"
              "  pull_request:"
              "  workflow_dispatch:"
              "    inputs:"
              "      accept-baseline:"
              "        description: Accept this run's snapshot as the Dokimos baseline."
              "        type: boolean"
              "        default: false"
              "      baseline-reason:"
              "        description: Why this snapshot is accepted as the baseline."
              "        type: string"
              "        default: ''"
              ""
              "permissions:"
              "  contents: write"
              ""
              "jobs:"
              "  quality:"
              "    runs-on: ubuntu-latest"
              "    steps:"
              "      - uses: actions/checkout@v4"
              "        with:"
              "          fetch-depth: 0"
              "      - uses: actions/setup-dotnet@v4"
              "        with:"
              "          dotnet-version: '8.0.x'"
              "      - name: Build (log captured as evidence)"
              "        shell: bash"
              "        run: |"
              "          set -o pipefail"
              "          mkdir -p artifacts/dokimos"
              $"          dotnet build{target} --configuration Release 2>&1 | tee artifacts/dokimos/build.log"
              "      - name: Test (TRX captured as evidence)"
              "        continue-on-error: true"
              $"        run: dotnet test{target} --configuration Release --no-build --logger trx --results-directory artifacts/dokimos/test-results"
              $"      - uses: {ActionPath}@{request.ActionRef}"
              "        with:"
              $"          version: {request.DokimosVersion}{sha256}"
              $"          source: {String.Join(' ', request.Sources)}"
              $"          policy: {PolicyPath}"
              "          build-log: artifacts/dokimos/build.log"
              "          test-results: artifacts/dokimos/test-results"
              $"          evidence-branch: {request.EvidenceBranch}"
              $"          persist: ${{{{ (github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/{request.DefaultBranch}') && 'true' || 'auto' }}}}"
              "          accept-baseline: ${{ inputs.accept-baseline == true && 'true' || 'false' }}"
              "          baseline-reason: ${{ inputs.baseline-reason }}"
              "" ]

    let record (request: InstallationRequest) =
        Contracts.serialize
            {| Contract = "dokimos.installation"
               SchemaVersion = "1.1.0"
               System = "dokimos"
               DokimosVersion = request.DokimosVersion
               ActionRef = request.ActionRef
               PackageSha256 = request.PackageSha256
               ConfigurationVersion = ConfigurationVersion
               Policy = PolicyPath
               Workflow = WorkflowPath
               Sources = request.Sources
               BuildTarget = request.BuildTarget
               EvidenceBranch = request.EvidenceBranch
               DefaultBranch = request.DefaultBranch
               Ownership =
                {| Policy = "created-if-missing; user-owned after creation"
                   Workflow = "dokimos-owned"
                   InstallationRecord = "dokimos-owned" |}
               InitialBaseline = "The first default-branch run persists a snapshot; accept it with workflow_dispatch accept-baseline=true." |}

    let files request =
        [ { Path = PolicyPath; Content = defaultPolicy }
          { Path = WorkflowPath; Content = workflow request }
          { Path = RecordPath; Content = record request } ]

    let isManagedRecord (text: string) =
        text.Contains("\"Contract\": \"dokimos.installation\"", StringComparison.Ordinal)
        && (text.Contains("\"SchemaVersion\": \"1.0.0\"", StringComparison.Ordinal)
            || text.Contains("\"SchemaVersion\": \"1.1.0\"", StringComparison.Ordinal))

    let isManagedWorkflow (text: string) =
        text.StartsWith("# Installed by `dokimos init`", StringComparison.Ordinal)
        || text.StartsWith("# Managed by Dokimos", StringComparison.Ordinal)

    /// Checks an installation from file contents (None = file absent).
    let verify (read: string -> string option) =
        let check name passed detail remediation =
            { Check = name; Passed = passed; Detail = detail; Remediation = if passed then None else Some remediation }
        let recordText = read RecordPath
        let recordCheck =
            match recordText with
            | None -> check "installation-record" false $"{RecordPath} is missing" "Run `dokimos init`."
            | Some text ->
                let ok = isManagedRecord text
                check "installation-record" ok $"{RecordPath} present with a supported Dokimos installation contract" "Re-run `dokimos init` or `dokimos upgrade` with a supported Dokimos release."
        let policyCheck =
            match read PolicyPath with
            | None -> check "policy" false $"{PolicyPath} is missing" "Run `dokimos init` or restore the policy file."
            | Some text ->
                match Contracts.readPolicy text with
                | Ok policy -> check "policy" true $"{PolicyPath} is valid policy schema {policy.SchemaVersion}" ""
                | Error e -> check "policy" false $"{PolicyPath}: {e}" "Fix the policy so it validates against schemas/dokimos-policy.schema.json."
        let workflowCheck =
            match read WorkflowPath with
            | None -> check "workflow" false $"{WorkflowPath} is missing" "Run `dokimos init`."
            | Some text ->
                let pinnedAction = Regex.IsMatch(text, Regex.Escape ActionPath + "@[0-9a-f]{40}\\b")
                let pinnedVersion = Regex.IsMatch(text, @"^\s+version: \d+\.\d+\.\d+", RegexOptions.Multiline)
                check "workflow" (pinnedAction && pinnedVersion)
                    (if pinnedAction && pinnedVersion then $"{WorkflowPath} pins the action by commit and Dokimos by exact version"
                     else $"{WorkflowPath} does not pin both the action commit and the Dokimos version")
                    "Pin `uses:` to a full commit SHA and `version:` to an exact release."
        [ recordCheck; policyCheck; workflowCheck ]
