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
        Output.data (
            Contracts.serialize
                {| Contract = "dokimos.version"
                   SchemaVersion = "1.0.0"
                   DokimosVersion = DokimosInfo.version
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
        | _ -> Output.invalid "invalid-arguments" usage

    /// Operational fault boundary: filesystem, process and other unexpected
    /// failures become a stable fault diagnostic and exit code 1, never a
    /// stack trace on stdout and never a quality outcome.
    let run (args: string list) =
        try
            dispatch args
        with
        | :? IOException as e -> Output.error ExitCodes.UnexpectedFault "io-fault" e.Message
        | :? UnauthorizedAccessException as e -> Output.error ExitCodes.UnexpectedFault "access-fault" e.Message
        | e -> Output.error ExitCodes.UnexpectedFault "unexpected-fault" (e.GetType().Name + ": " + e.Message)

    [<EntryPoint>]
    let main args =
        let output = run (List.ofArray args)
        output.Stdout |> Option.iter Console.Out.WriteLine
        output.Stderr |> Option.iter Console.Error.WriteLine
        output.ExitCode
