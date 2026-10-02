namespace Dokimos.Cli

open System
open System.IO
open System.Text.Json
open Dokimos.Core

/// The result of one CLI invocation. Canonical data goes to stdout,
/// diagnostics to stderr; the exit code is the machine-readable disposition.
type Output =
    { Stdout: string option
      Stderr: string option
      ExitCode: int }

module Output =
    let data (json: string) = { Stdout = Some json; Stderr = None; ExitCode = ExitCodes.Continue }

    let dataWithCode code (json: string) = { Stdout = Some json; Stderr = None; ExitCode = code }

    let error code diagnosticCode message =
        { Stdout = None
          Stderr = Some(Contracts.serialize (Contracts.diagnostic diagnosticCode message))
          ExitCode = code }

    let invalid diagnosticCode message = error ExitCodes.InvalidInvocation diagnosticCode message

    let unavailable diagnosticCode message = error ExitCodes.EvidenceUnavailable diagnosticCode message

/// `--name value` options (repeatable) and positional arguments.
type Arguments =
    { Positionals: string list
      Options: Map<string, string list> }

module Arguments =
    let parse (args: string list) =
        let rec go positionals options remaining =
            match remaining with
            | [] -> Ok { Positionals = List.rev positionals; Options = options |> Map.map (fun _ v -> List.rev v) }
            | (name: string) :: value :: rest when name.StartsWith("--", StringComparison.Ordinal) && not (value.StartsWith("--", StringComparison.Ordinal)) ->
                let key = name.Substring 2
                go positionals (options |> Map.change key (fun existing -> Some(value :: defaultArg existing []))) rest
            | name :: _ when name.StartsWith("--", StringComparison.Ordinal) -> Error $"option {name} requires a value"
            | value :: rest -> go (value :: positionals) options rest
        go [] Map.empty args

    let tryOne name args = args.Options |> Map.tryFind name |> Option.bind List.tryLast

    let many name args = args.Options |> Map.tryFind name |> Option.defaultValue []

    let required name args =
        match tryOne name args with
        | Some value -> Ok value
        | None -> Error $"missing required option --{name}"

    let allowOnly (known: string list) args =
        match args.Options |> Map.keys |> Seq.filter (fun k -> not (List.contains k known)) |> Seq.tryHead with
        | Some unknown -> Error $"unknown option --{unknown}"
        | None -> Ok args

module Program =
    let options = JsonSerializerOptions(WriteIndented = true)

    let private repositoryRelative (path: string) =
        Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath path).Replace('\\', '/')

    let sourceFiles root =
        let separator = string Path.DirectorySeparatorChar
        Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> not (p.Contains(separator + "obj" + separator)))
        |> Seq.filter (fun p -> not (p.Contains(separator + "bin" + separator)))
        |> Seq.map repositoryRelative
        |> Seq.sort
        |> Seq.toList

    let private readSources roots =
        roots |> List.collect sourceFiles |> List.distinct |> List.map (fun path -> path, File.ReadAllText path)

    let tryReadSnapshot path =
        if not (File.Exists path) then Error("snapshot-not-found:" + path)
        else Contracts.readSnapshot (File.ReadAllText path)

    let private readRequiredSnapshot label path k =
        match tryReadSnapshot path with
        | Ok snapshot -> k snapshot
        | Error reason -> Output.unavailable (label + "-snapshot-unavailable") $"{label}: {reason}"

    let private withArgs known args k =
        match Arguments.parse args |> Result.bind (Arguments.allowOnly known) with
        | Ok parsed -> k parsed
        | Error message -> Output.invalid "invalid-arguments" message

    let private optionalFile name args k =
        match Arguments.tryOne name args with
        | None -> k None
        | Some path when File.Exists path -> k (Some(File.ReadAllText path))
        | Some path -> Output.unavailable $"{name}-not-found" $"--{name} {path} does not exist"

    /// TRX inputs may be files or directories containing *.trx files.
    let private trxFiles (paths: string list) =
        paths
        |> List.collect (fun path ->
            if Directory.Exists path then Directory.EnumerateFiles(path, "*.trx", SearchOption.AllDirectories) |> Seq.sort |> Seq.toList
            else [ path ])

    let version () =
        let releaseVersion, sourceCommit = Installation.releaseOf DokimosInfo.version
        Output.data (
            Contracts.serialize
                {| Contract = "dokimos.version"
                   SchemaVersion = "1.1.0"
                   SystemId = "dokimos"
                   Repository = "kemiller2002/dokimos"
                   Executable = "dokimos"
                   CompatibilityAliases = ([]: string list)
                   DokimosVersion = DokimosInfo.version
                   ReleaseVersion = releaseVersion
                   SourceCommit = sourceCommit
                   Contracts =
                    {| Snapshot = Contracts.supportedSnapshotSchemas
                       Comparison = [ Contracts.ComparisonSchemaVersion ]
                       Evaluation = [ Contracts.EvaluationSchemaVersion ]
                       Policy = Contracts.supportedPolicySchemas |} |}
        )

    let capabilities () =
        let determinism =
            function
            | Deterministic -> "deterministic"
            | DeterministicGivenInputs inputs -> "deterministic-given: " + inputs
        let preference =
            function
            | PreferLower -> "lower-is-better"
            | PreferHigher -> "higher-is-better"
            | Contextual -> "contextual"
        Output.data (
            Contracts.serialize
                {| Contract = "dokimos.capabilities"
                   SchemaVersion = "1.0.0"
                   DokimosVersion = DokimosInfo.version
                   Analyzers =
                    Capabilities.analyzers
                    |> List.map (fun a ->
                        {| AnalyzerId = a.AnalyzerId
                           Version = a.Version
                           Language = a.Language
                           Metrics = a.Metrics
                           Scopes = a.Scopes
                           Limitations = a.Limitations
                           RequiredTools = a.RequiredTools
                           RequiredInputs = a.RequiredInputs
                           Determinism = determinism a.Determinism |})
                   Metrics =
                    Capabilities.metrics
                    |> List.map (fun m ->
                        {| MetricId = m.MetricId
                           MetricVersion = m.Version
                           Unit = m.Unit
                           Preference = preference m.Preference
                           Scopes = m.Scopes
                           Analyzer = m.Analyzer
                           Support = if m.Analyzer.IsSome then "collected" else "unavailable" |}) |}
        )

    let compare args =
        withArgs [] args (fun parsed ->
            match parsed.Positionals with
            | [ beforePath; afterPath ] ->
                readRequiredSnapshot "before" beforePath (fun before ->
                    readRequiredSnapshot "after" afterPath (fun after ->
                        let comparison = CanonicalComparison.compare before after
                        Output.data (Contracts.serialize (Contracts.comparisonDto before after comparison))))
            | _ -> Output.invalid "invalid-arguments" "usage: dokimos compare <before-snapshot> <after-snapshot>")

    let evaluate args =
        withArgs [ "baseline"; "current"; "policy"; "at" ] args (fun parsed ->
            match Arguments.required "baseline" parsed, Arguments.required "current" parsed, Arguments.required "policy" parsed with
            | Ok baselinePath, Ok currentPath, Ok policyPath ->
                let asOf =
                    match Arguments.tryOne "at" parsed with
                    | Some text -> DateTimeOffset.TryParse text |> function | true, v -> Ok v | _ -> Error text
                    | None -> Ok DateTimeOffset.UtcNow
                match asOf with
                | Error text -> Output.invalid "invalid-arguments" $"--at '{text}' is not a timestamp"
                | Ok asOf when not (File.Exists policyPath) -> Output.invalid "policy-not-found" $"policy {policyPath} does not exist"
                | Ok asOf ->
                    match Contracts.readPolicy (File.ReadAllText policyPath) with
                    | Error reason -> Output.invalid "policy-invalid" reason
                    | Ok policy ->
                        readRequiredSnapshot "baseline" baselinePath (fun baseline ->
                            readRequiredSnapshot "current" currentPath (fun current ->
                                let evaluation = Evaluation.evaluate asOf policy baseline current
                                let dto = Contracts.evaluationDto evaluation
                                Output.dataWithCode dto.ExitCode (Contracts.serialize dto)))
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Output.invalid "invalid-arguments" (e + "; usage: dokimos evaluate --baseline <snapshot> --current <snapshot> --policy <policy>"))

    let snapshot args =
        let known = [ "git-history"; "repository"; "revision"; "ref"; "build-log"; "test-results"; "coverage"; "collected-at" ]
        withArgs known args (fun parsed ->
            let roots = parsed.Positionals
            match roots |> List.tryFind (Directory.Exists >> not), Arguments.required "repository" parsed, Arguments.required "revision" parsed with
            | _ when List.isEmpty roots -> Output.invalid "invalid-arguments" "usage: dokimos snapshot <source-dir>... --repository <owner/repo> --revision <sha> [--ref <ref>] [--git-history <file>] [--build-log <file>] [--test-results <trx-file-or-dir>]... [--coverage <cobertura.xml>]"
            | Some missing, _, _ -> Output.invalid "source-not-found" $"source directory {missing} does not exist"
            | None, Error e, _
            | None, _, Error e -> Output.invalid "invalid-arguments" e
            | None, Ok repository, Ok revision ->
                optionalFile "git-history" parsed (fun history ->
                    optionalFile "build-log" parsed (fun buildLog ->
                        optionalFile "coverage" parsed (fun coverage ->
                            let trx = Arguments.many "test-results" parsed |> trxFiles
                            match trx |> List.tryFind (File.Exists >> not) with
                            | Some missing -> Output.unavailable "test-results-not-found" $"--test-results {missing} does not exist"
                            | None ->
                                let collectedAt =
                                    Arguments.tryOne "collected-at" parsed
                                    |> Option.bind (fun t -> match DateTimeOffset.TryParse t with | true, v -> Some v | _ -> None)
                                    |> Option.defaultValue DateTimeOffset.UtcNow
                                let request =
                                    { Repository = repository
                                      Revision = revision
                                      Ref = Arguments.tryOne "ref" parsed |> Option.defaultValue ""
                                      CollectedAt = collectedAt
                                      AnalyzedScope = roots |> List.map repositoryRelative
                                      Sources = readSources roots
                                      GitHistory = history
                                      BuildLog = buildLog
                                      TestResults = if List.isEmpty (Arguments.many "test-results" parsed) then None else Some(trx |> List.map File.ReadAllText)
                                      Coverage = coverage }
                                let snapshot = Collection.snapshot Timer.stopwatch request
                                Output.data (Contracts.serialize (Contracts.snapshotDto snapshot))))))

    let measure args =
        match args with
        | [ path ] when File.Exists path ->
            let source = File.ReadAllText path
            let structural = Structural.measure path source
            let quality = FSharpQuality.measure path source
            let complexity = Complexity.measure path source
            let agent = AgentQuality.fromMetrics structural quality
            Output.data (JsonSerializer.Serialize({| path = path; structural = structural; quality = quality; complexity = complexity; agent = agent |}, options))
        | [ path ] -> Output.invalid "source-not-found" $"source file {path} does not exist"
        | _ -> Output.invalid "invalid-arguments" "usage: dokimos measure <source-file>"

    let analyze args =
        withArgs [ "git-history" ] args (fun parsed ->
            match parsed.Positionals with
            | [ root ] when Directory.Exists root ->
                optionalFile "git-history" parsed (fun history ->
                    let temporal = history |> Option.map (GitHistory.parse >> GitHistory.summarize) |> Option.defaultValue Map.empty
                    let result = RepositoryAnalysis.analyze Collection.DuplicateBlockSize temporal (readSources [ root ])
                    Output.data (JsonSerializer.Serialize(Wire.repository result, options)))
            | [ root ] -> Output.invalid "source-not-found" $"source directory {root} does not exist"
            | _ -> Output.invalid "invalid-arguments" "usage: dokimos analyze <source-directory> [--git-history <numstat-file>]")

    let private storeError error =
        let code, message = EvidenceStore.describeError error
        Output.error (EvidenceStore.exitCode error) code message

    let private withStore parsed k =
        match Arguments.required "store" parsed with
        | Error e -> Output.invalid "invalid-arguments" e
        | Ok root ->
            match FileSystemStore.openStore root with
            | Ok store -> k store
            | Error e -> storeError e

    let private storeResult (store: EvidenceStore) outcome id =
        Output.data (
            Contracts.serialize
                {| Contract = "dokimos.store-result"
                   SchemaVersion = "1.0.0"
                   Store = store.Describe
                   Outcome = (match outcome with Stored -> "stored" | AlreadyStored -> "already-stored")
                   Subject = id |}
        )

    let store args =
        match args with
        | "init" :: rest ->
            withArgs [ "store" ] rest (fun parsed ->
                match Arguments.required "store" parsed with
                | Error e -> Output.invalid "invalid-arguments" e
                | Ok root ->
                    FileSystemStore.init root
                    withStore parsed (fun store -> storeResult store Stored root))
        | "put" :: rest ->
            withArgs [ "store"; "snapshot" ] rest (fun parsed ->
                withStore parsed (fun store ->
                    match Arguments.required "snapshot" parsed with
                    | Error e -> Output.invalid "invalid-arguments" e
                    | Ok path ->
                        readRequiredSnapshot "snapshot" path (fun snapshot ->
                            match store.Put snapshot with
                            | Ok outcome -> storeResult store outcome snapshot.SnapshotId
                            | Error e -> storeError e)))
        | "get" :: rest ->
            withArgs [ "store"; "id" ] rest (fun parsed ->
                withStore parsed (fun store ->
                    match Arguments.required "id" parsed with
                    | Error e -> Output.invalid "invalid-arguments" e
                    | Ok id ->
                        match store.TryGet id with
                        | Ok (Some snapshot) -> Output.data (Contracts.serialize (Contracts.snapshotDto snapshot))
                        | Ok None -> storeError (SnapshotNotFound id)
                        | Error e -> storeError e))
        | "accept-baseline" :: rest ->
            withArgs [ "store"; "snapshot-id"; "actor"; "reason"; "name"; "at" ] rest (fun parsed ->
                withStore parsed (fun store ->
                    match Arguments.required "snapshot-id" parsed, Arguments.required "actor" parsed, Arguments.required "reason" parsed with
                    | Ok id, Ok actor, Ok reason ->
                        let at =
                            Arguments.tryOne "at" parsed
                            |> Option.bind (fun t -> match DateTimeOffset.TryParse t with | true, v -> Some v | _ -> None)
                            |> Option.defaultValue DateTimeOffset.UtcNow
                        let acceptance = { Name = Arguments.tryOne "name" parsed |> Option.defaultValue "default"; SnapshotId = id; AcceptedAt = at; Actor = actor; Reason = reason }
                        match store.Accept acceptance with
                        | Ok outcome -> storeResult store outcome (acceptance.Name + "=" + id)
                        | Error e -> storeError e
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Output.invalid "invalid-arguments" e))
        | "baseline" :: rest ->
            withArgs [ "store"; "name" ] rest (fun parsed ->
                withStore parsed (fun store ->
                    let name = Arguments.tryOne "name" parsed |> Option.defaultValue "default"
                    match EvidenceStore.currentBaseline store name with
                    | Ok (Some (_, snapshot)) -> Output.data (Contracts.serialize (Contracts.snapshotDto snapshot))
                    | Ok None -> Output.unavailable "baseline-not-accepted" $"No baseline named '{name}' has been accepted in {store.Describe}."
                    | Error e -> storeError e))
        | _ -> Output.invalid "invalid-arguments" "usage: dokimos store init|put|get|accept-baseline|baseline --store <dir> ..."

    let history args =
        let findingsQuery, rest =
            match args with
            | "findings" :: rest -> true, rest
            | rest -> false, rest
        withArgs [ "store"; "repository"; "metric"; "scope"; "file"; "baseline-name" ] rest (fun parsed ->
            withStore parsed (fun store ->
                let repository = Arguments.tryOne "repository" parsed
                let baselineName = Arguments.tryOne "baseline-name" parsed |> Option.defaultValue "default"
                match store.List(), EvidenceStore.currentBaseline store baselineName with
                | Error e, _
                | _, Error e -> storeError e
                | Ok all, Ok baseline ->
                    let snapshots = History.ordered repository all
                    let baselineSnapshot = baseline |> Option.map snd
                    let metric = Arguments.tryOne "metric" parsed
                    let file = Arguments.tryOne "file" parsed
                    let scope = Arguments.tryOne "scope" parsed
                    let kind, series, findings =
                        match findingsQuery, metric, file with
                        | true, _, _ -> "findings", [], History.findingHistories snapshots
                        | _, Some id, _ -> "metric", History.metricHistory baselineSnapshot id scope snapshots, []
                        | _, None, Some path ->
                            "file", History.fileHistory baselineSnapshot path snapshots,
                            History.findingHistories snapshots |> List.filter (fun f -> f.Scope = path)
                        | _ -> "snapshots", [], []
                    let dto: HistoryDto =
                        { Contract = HistoryContract.Contract
                          SchemaVersion = HistoryContract.SchemaVersion
                          DokimosVersion = DokimosInfo.version
                          Store = store.Describe
                          Repository = repository
                          Query = { Kind = kind; Metric = metric; Scope = scope; File = file }
                          Baseline = baselineSnapshot |> Option.map Contracts.snapshotRef
                          Snapshots = snapshots |> List.map (HistoryContract.entryDto (baselineSnapshot |> Option.map _.SnapshotId))
                          Series = series |> List.map HistoryContract.seriesDto
                          Findings = findings |> List.map HistoryContract.findingDto
                          Notes =
                            [ if snapshots.IsEmpty then "The store holds no snapshots for this query."
                              if baseline.IsNone then $"No baseline named '{baselineName}' is accepted; baseline distance is unavailable."
                              if kind = "metric" && series.IsEmpty then "No stored snapshot contains this metric." ] }
                    Output.data (Contracts.serialize dto)))

    let private readRelative root (relative: string) =
        let path = Path.Combine(root, relative)
        if File.Exists path then Some(File.ReadAllText path) else None

    let private checksOutput command (checks: InstallationCheck list) =
        let healthy = checks |> List.forall _.Passed
        let json =
            Contracts.serialize
                {| Contract = "dokimos.installation-check"
                   SchemaVersion = "1.0.0"
                   Command = command
                   DokimosVersion = DokimosInfo.version
                   Healthy = healthy
                   Checks =
                    checks
                    |> List.map (fun c -> {| Check = c.Check; Passed = c.Passed; Detail = c.Detail; Remediation = c.Remediation |}) |}
        Output.dataWithCode (if healthy then ExitCodes.Continue else ExitCodes.EvidenceUnavailable) json

    /// Conditor lifecycle `init`: reaches the declared installed state
    /// idempotently. Existing files are never overwritten.
    let init args =
        withArgs [ "root"; "version"; "action-ref"; "package-sha256"; "source"; "build-target"; "evidence-branch"; "default-branch" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            let release, commit = Installation.releaseOf DokimosInfo.version
            let request =
                { DokimosVersion = Arguments.tryOne "version" parsed |> Option.defaultValue release
                  ActionRef = Arguments.tryOne "action-ref" parsed |> Option.orElse commit |> Option.defaultValue ""
                  PackageSha256 = Arguments.tryOne "package-sha256" parsed
                  Sources = (match Arguments.many "source" parsed with [] -> [ "src" ] | sources -> sources)
                  BuildTarget = Arguments.tryOne "build-target" parsed
                  EvidenceBranch = Arguments.tryOne "evidence-branch" parsed |> Option.defaultValue "dokimos-evidence"
                  DefaultBranch = Arguments.tryOne "default-branch" parsed |> Option.defaultValue "main" }
            match Installation.validate request with
            | [] when Directory.Exists root ->
                let results =
                    Installation.files request
                    |> List.map (fun file ->
                        let path = Path.Combine(root, file.Path)
                        match readRelative root file.Path with
                        | Some existing when existing = file.Content -> file.Path, "unchanged"
                        | Some _ -> file.Path, "preserved-existing"
                        | None ->
                            match Path.GetDirectoryName path with
                            | null -> ()
                            | directory -> Directory.CreateDirectory directory |> ignore
                            File.WriteAllText(path, file.Content)
                            file.Path, "created")
                Output.data (
                    Contracts.serialize
                        {| Contract = "dokimos.init-result"
                           SchemaVersion = "1.0.0"
                           DokimosVersion = request.DokimosVersion
                           ActionRef = request.ActionRef
                           Files = results |> List.map (fun (path, outcome) -> {| Path = path; Outcome = outcome |})
                           NextSteps =
                            [ "Commit the created files."
                              "The first default-branch run persists a snapshot to the evidence branch."
                              "Accept it: run the Dokimos workflow manually with accept-baseline=true." ] |}
                )
            | [] -> Output.invalid "root-not-found" $"{root} does not exist"
            | problems -> Output.invalid "invalid-installation-request" (String.Join("; ", problems)))

    let upgrade args =
        withArgs [ "root"; "version"; "action-ref"; "package-sha256"; "source"; "build-target"; "evidence-branch"; "default-branch" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            let release, commit = Installation.releaseOf DokimosInfo.version
            let request =
                { DokimosVersion = Arguments.tryOne "version" parsed |> Option.defaultValue release
                  ActionRef = Arguments.tryOne "action-ref" parsed |> Option.orElse commit |> Option.defaultValue ""
                  PackageSha256 = Arguments.tryOne "package-sha256" parsed
                  Sources = (match Arguments.many "source" parsed with [] -> [ "src" ] | sources -> sources)
                  BuildTarget = Arguments.tryOne "build-target" parsed
                  EvidenceBranch = Arguments.tryOne "evidence-branch" parsed |> Option.defaultValue "dokimos-evidence"
                  DefaultBranch = Arguments.tryOne "default-branch" parsed |> Option.defaultValue "main" }
            match Installation.validate request with
            | [] when not (Directory.Exists root) -> Output.invalid "root-not-found" $"{root} does not exist"
            | [] ->
                match readRelative root Installation.RecordPath with
                | None -> Output.unavailable "installation-not-found" $"No Dokimos installation record exists at {Installation.RecordPath}; run `dokimos init` first."
                | Some record when not (Installation.isManagedRecord record) ->
                    Output.unavailable "installation-ownership-conflict" $"{Installation.RecordPath} is not a Dokimos-owned installation record; refusing to overwrite it."
                | Some _ ->
                    match readRelative root Installation.WorkflowPath with
                    | Some workflow when not (Installation.isManagedWorkflow workflow) ->
                        Output.unavailable "installation-ownership-conflict" $"{Installation.WorkflowPath} is not marked as Dokimos-owned; refusing to overwrite it."
                    | _ ->
                        let outcomes =
                            Installation.files request
                            |> List.map (fun file ->
                                let path = Path.Combine(root, file.Path)
                                match readRelative root file.Path with
                                | Some existing when existing = file.Content -> file.Path, "unchanged"
                                | Some _ when file.Path = Installation.PolicyPath -> file.Path, "preserved-user-owned"
                                | existing ->
                                    let existed = existing.IsSome
                                    match Path.GetDirectoryName path with
                                    | null -> ()
                                    | directory -> Directory.CreateDirectory directory |> ignore
                                    File.WriteAllText(path, file.Content)
                                    file.Path, (if existed then "updated" else "created"))
                        Output.data (
                            Contracts.serialize
                                {| Contract = "dokimos.upgrade-result"
                                   SchemaVersion = "1.0.0"
                                   DokimosVersion = request.DokimosVersion
                                   ConfigurationVersion = Installation.ConfigurationVersion
                                   Files = outcomes |> List.map (fun (path, outcome) -> {| Path = path; Outcome = outcome |}) |}
                        )
            | problems -> Output.invalid "invalid-installation-request" (String.Join("; ", problems)))

    let verify command args =
        withArgs [ "root" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            checksOutput command (Installation.verify (readRelative root)))

    /// Application-facing results contract: the evaluation plus stored
    /// history, projected for UIs and reports without recomputation.
    let results args =
        withArgs [ "baseline"; "current"; "policy"; "store"; "at" ] args (fun parsed ->
            match Arguments.required "baseline" parsed, Arguments.required "current" parsed, Arguments.required "policy" parsed with
            | Ok baselinePath, Ok currentPath, Ok policyPath ->
                if not (File.Exists policyPath) then Output.invalid "policy-not-found" $"policy {policyPath} does not exist"
                else
                    match Contracts.readPolicy (File.ReadAllText policyPath) with
                    | Error reason -> Output.invalid "policy-invalid" reason
                    | Ok policy ->
                        readRequiredSnapshot "baseline" baselinePath (fun baseline ->
                            readRequiredSnapshot "current" currentPath (fun current ->
                                let asOf =
                                    Arguments.tryOne "at" parsed
                                    |> Option.bind (fun t -> match DateTimeOffset.TryParse t with | true, v -> Some v | _ -> None)
                                    |> Option.defaultValue DateTimeOffset.UtcNow
                                let evaluation = Evaluation.evaluate asOf policy baseline current
                                let emit history = Output.data (Contracts.serialize (Results.build evaluation history))
                                match Arguments.tryOne "store" parsed with
                                | None -> emit [ baseline ]
                                | Some root ->
                                    match FileSystemStore.openStore root |> Result.bind (fun store -> store.List()) with
                                    | Ok stored -> emit stored
                                    | Error e -> storeError e))
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Output.invalid "invalid-arguments" (e + "; usage: dokimos results --baseline b --current c --policy p [--store dir]"))

    let usage =
        String.concat
            "\n"
            [ "usage: dokimos <command>"
              "  version                                   report the Dokimos version and supported contracts"
              "  capabilities                              declare analyzers, metrics and limitations"
              "  measure <source-file>                     raw structural measurement of one file"
              "  analyze <source-dir> [--git-history f]    raw repository analysis"
              "  snapshot <source-dir>... --repository r --revision sha [options]"
              "  compare <before-snapshot> <after-snapshot>"
              "  evaluate --baseline b --current c --policy p"
              "  results --baseline b --current c --policy p [--store dir]   application-facing results contract"
              "  store init|put|get|accept-baseline|baseline --store dir ..."
              "  init [--root .] [--version v] [--action-ref sha] [--source dir]...   install Dokimos into a repository"
              "  status|verify|doctor [--root .]           read-only installation health checks"
              "  upgrade [--root .] [--version v] [--action-ref sha] [--source dir]...   upgrade Dokimos-owned repository state"
              "  history [findings] --store dir [--repository r] [--metric id [--scope s]] [--file path]"
              "exit codes: 0 continue, 1 unexpected fault, 2 invalid invocation, 3 evidence unavailable/invalid, 4 policy failure, 5 store conflict" ]

    /// Dispatches one invocation. Expected Dokimos outcomes are returned as
    /// values; only unexpected operational faults escape as exceptions.
    let dispatch (args: string list) =
        match args with
        | [ "version" ]
        | [ "--version" ] -> version ()
        | [ "capabilities" ] -> capabilities ()
        | "compare" :: rest -> compare rest
        | "evaluate" :: rest -> evaluate rest
        | "snapshot" :: rest -> snapshot rest
        | "measure" :: rest -> measure rest
        | "analyze" :: rest -> analyze rest
        | "store" :: rest -> store rest
        | "history" :: rest -> history rest
        | "init" :: rest -> init rest
        | "results" :: rest -> results rest
        | "status" :: rest -> verify "status" rest
        | "verify" :: rest -> verify "verify" rest
        | "doctor" :: rest -> verify "doctor" rest
        | "upgrade" :: rest -> upgrade rest
        | _ -> Output.invalid "invalid-arguments" usage

    /// Aegis configuration for the CLI process. Faults are written as Aegis
    /// events to stderr (never stdout, which carries only canonical data) and
    /// delivered before the process exits.
    let aegis =
        { Aegis.Aegis.configure "Dokimos" (Some DokimosInfo.version) [ Aegis.Sinks.standardError ] with
            Persistence = Aegis.Blocking }

    /// Classifies an unexpected operational failure that crossed a Dokimos
    /// boundary (filesystem, Git worktree, process). Expected quality states
    /// never reach this: they are Dokimos values with their own exit codes.
    let classify (scope: Aegis.Scope) (ex: exn) =
        let fault code category message =
            Aegis.Aegis.faultOf aegis scope (Aegis.FaultCode code) category Aegis.FaultSeverity.Error Aegis.OperationOnly
                Aegis.RequiresIntervention Aegis.ManualIntervention message ex
        match ex with
        | :? UnauthorizedAccessException -> fault "DOKIMOS.ACCESS.DENIED" Aegis.InfrastructureFailure "Dokimos could not access a file or directory it needs."
        | :? IOException -> fault "DOKIMOS.IO.FAILED" Aegis.InfrastructureFailure "Dokimos could not read or write evidence on disk."
        | :? System.Xml.XmlException
        | :? JsonException -> fault "DOKIMOS.DATA.UNREADABLE" Aegis.DataFailure "Dokimos could not parse an input it was given."
        | _ -> fault "DOKIMOS.UNEXPECTED" Aegis.UnknownFailure "Dokimos failed unexpectedly."

    /// Operational fault boundary (R13, DOK-OPS-021). Expected outcomes are
    /// returned as values; unexpected external failures become Aegis faults
    /// with a stable code and exit code 1. Programming defects are re-raised
    /// by Aegis so they fail loudly rather than masquerading as faults.
    let run (args: string list) =
        let command = args |> List.tryHead |> Option.defaultValue "usage"
        let scope = Aegis.Aegis.scope aegis ("Dokimos.Cli." + command) Map.empty
        match Aegis.Aegis.capture aegis scope classify (fun () -> dispatch args) with
        | Ok output -> output
        | Error fault -> Output.error ExitCodes.UnexpectedFault fault.Code.Value (fault.UserMessage + " Aegis fault " + fault.Id.Value)

    [<EntryPoint>]
    let main args =
        let output = run (List.ofArray args)
        output.Stdout |> Option.iter Console.Out.WriteLine
        output.Stderr |> Option.iter Console.Error.WriteLine
        output.ExitCode
