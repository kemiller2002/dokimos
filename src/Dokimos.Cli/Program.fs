namespace Dokimos.Cli

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Dokimos.Core

/// The effects a command may consult. `Environment` is only ever asked for
/// the whitelisted `IdentityEnvironment.names` (R0.13).
type CliContext =
    { Environment: string -> string option
      Now: unit -> DateTimeOffset
      FreshRun: unit -> string }

type CliResult = { ExitCode: int; Output: string; ErrorOutput: string }

module Program =
    let options = JsonSerializerOptions(WriteIndented = true)

    let usage =
        "Usage: dokimos compare <before-snapshot> <after-snapshot> [actor options] | dokimos measure <source-file> | dokimos analyze <source-directory> [--git-history <numstat-file>] | dokimos snapshot <source-directory> --git-history <file> --repository <owner/repo> --revision <sha> --ref <ref> [--subject-provenance <record.json>] [actor options] | dokimos provenance identity [actor options] | dokimos provenance finding <snapshot> <finding-id> | dokimos provenance append <record-or-comparison.json> --operation <x-remediated|x-validated|reviewed|approved|x-...> [--reason <text>] [--evidence <ref>]... [actor options] | dokimos provenance accept-baseline <snapshot> [--reason <text>] [actor options]; actor options: --actor-kind --actor --provider --model --runtime"

    let actorOptions = [ "actor-kind"; "actor"; "provider"; "model"; "runtime" ]

    let sourceFiles root =
        Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> not (p.Contains(string Path.DirectorySeparatorChar + "obj" + string Path.DirectorySeparatorChar)))
        |> Seq.filter (fun p -> not (p.Contains(string Path.DirectorySeparatorChar + "bin" + string Path.DirectorySeparatorChar)))
        |> Seq.sort
        |> Seq.toList

    let private describeProblems (problems: ProvenanceProblem list) =
        problems |> List.map (fun item -> if item.Field = "" then item.Message else item.Field + ": " + item.Message) |> String.concat "; "

    /// Snapshot provenance is validated on read: malformed provenance is
    /// rejected (never silently dropped); an unsupported major is carried
    /// verbatim; a record for a different snapshot is refused.
    let private checkSnapshotProvenance (snapshot: CanonicalSnapshot) =
        match snapshot.Provenance with
        | None -> Ok snapshot
        | Some raw ->
            match ProvenanceJson.validate raw with
            | Error problems -> Error("malformed-snapshot-provenance:" + describeProblems problems)
            | Ok reading ->
                match ProvenanceJson.interpreted reading |> Option.bind _.Subject with
                | Some subject when subject <> CanonicalSnapshot.subject snapshot.SnapshotId -> Error("snapshot-provenance-subject-mismatch:" + subject)
                | _ -> Ok snapshot

    let tryReadSnapshot path =
        try
            match JsonSerializer.Deserialize<CanonicalSnapshot>(File.ReadAllText path, options) with
            | null -> Error "snapshot-deserialized-to-null"
            | snapshot when not (CanonicalSnapshot.isSupportedSchema snapshot.SchemaVersion) -> Error ("unsupported-snapshot-schema:" + snapshot.SchemaVersion)
            | snapshot -> checkSnapshotProvenance snapshot
        with
        | :? JsonException -> Error "malformed-snapshot-json"

    /// `positional` arguments and `--name value` options (repeatable).
    let parseArguments (allowed: string list) (args: string list) : Result<string list * Map<string, string list>, string> =
        let rec go positional (found: Map<string, string list>) remaining =
            match remaining with
            | [] -> Ok(List.rev positional, found)
            | (name: string) :: rest when name.StartsWith("--", StringComparison.Ordinal) ->
                let key = name.Substring 2

                match rest with
                | value :: tail when List.contains key allowed ->
                    go positional (found |> Map.add key ((found |> Map.tryFind key |> Option.defaultValue []) @ [ value ])) tail
                | _ :: _ -> Error("unknown-option:" + name)
                | [] -> Error("missing-option-value:" + name)
            | value :: rest -> go (value :: positional) found rest

        go [] Map.empty args

    let private single name (found: Map<string, string list>) = found |> Map.tryFind name |> Option.bind List.tryLast

    let private declaration found =
        { DeclaredKind = single "actor-kind" found
          DeclaredId = single "actor" found
          DeclaredProvider = single "provider" found
          DeclaredModel = single "model" found
          DeclaredRuntime = single "runtime" found }

    let private identity (context: CliContext) found =
        ActingIdentity.resolve (declaration found) (IdentityEnvironment.read context.Environment) (context.FreshRun())
        |> Result.mapError (fun message -> "provenance:" + message)

    let private usageResult = { ExitCode = 2; Output = ""; ErrorOutput = usage }
    let private serialize value = JsonSerializer.Serialize(value, options)
    let private ok output = { ExitCode = 0; Output = output; ErrorOutput = "" }
    let private refuse (reason: string) = { ExitCode = 3; Output = ""; ErrorOutput = serialize {| state = "unavailable"; reason = reason |} }
    let private render (node: JsonNode) = node.ToJsonString(options)
    let private provenanceError problems = "provenance:" + describeProblems problems

    let private readJsonObject path =
        try
            match JsonNode.Parse(File.ReadAllText path) with
            | :? JsonObject as document -> Ok document
            | _ -> Error "not-a-json-object"
        with
        | :? JsonException -> Error "malformed-json"

    let private finish (result: Result<string, string>) =
        match result with
        | Ok output -> ok output
        | Error reason -> refuse reason

    let private compareCommand context beforePath afterPath found =
        match tryReadSnapshot beforePath, tryReadSnapshot afterPath with
        | Ok before, Ok after ->
            let comparison = CanonicalComparison.compare before after

            identity context found
            |> Result.bind (fun acting -> MeasurementProvenance.forComparison acting (context.Now()) before after |> Result.mapError provenanceError)
            |> Result.map (fun record -> serialize (CanonicalComparisonWire.ofComparison { comparison with Provenance = Some record }))
            |> finish
        | beforeResult, afterResult ->
            let reason =
                match beforeResult, afterResult with
                | Error e, _ -> "before:" + e
                | _, Error e -> "after:" + e
                | _ -> "snapshot-validation-failed"
            refuse reason

    let private snapshotCommand context root found =
        match single "git-history" found, single "repository" found, single "revision" found, single "ref" found with
        | Some historyPath, Some repository, Some revision, Some refName when Directory.Exists root && File.Exists historyPath ->
            let subjectProvenance =
                match single "subject-provenance" found with
                | None -> Ok None
                | Some path when File.Exists path -> readJsonObject path |> Result.map Some |> Result.mapError (fun e -> "subject-provenance:" + e)
                | Some _ -> Error "subject-provenance:file-not-found"

            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let temporal = File.ReadAllText(historyPath) |> GitHistory.parse |> GitHistory.summarize
            let analysis = RepositoryAnalysis.analyze 6 temporal sources
            let snapshot = CanonicalSnapshot.fromAnalysis repository revision refName (context.Now()) analysis

            subjectProvenance
            |> Result.bind (fun subject ->
                identity context found
                |> Result.bind (fun acting -> MeasurementProvenance.forSnapshot acting snapshot subject |> Result.mapError provenanceError))
            |> Result.map (fun record -> serialize { snapshot with Provenance = Some record })
            |> finish
        | _ -> usageResult

    let private isSnapshotDocument (document: JsonObject) =
        document.ContainsKey "SnapshotId" && document.ContainsKey "Metrics"

    /// Appends to a bare record, or to the `Provenance` member of a
    /// comparison, preserving everything else. Snapshots are immutable
    /// evidence and are refused.
    let private appendCommand context path found =
        match single "operation" found |> Option.map (fun text -> text, ProvenanceOperation.tryParse text) with
        | None -> usageResult
        | Some(text, None) -> refuse ("unknown-operation:" + text)
        | Some(_, Some operation) ->
            readJsonObject path
            |> Result.bind (fun document ->
                identity context found
                |> Result.bind (fun acting ->
                    let appendTo raw =
                        MeasurementProvenance.recordFollowUp
                            acting
                            (context.Now())
                            operation
                            (single "reason" found)
                            (found |> Map.tryFind "evidence" |> Option.defaultValue [])
                            raw
                        |> Result.mapError provenanceError

                    if isSnapshotDocument document then
                        Error "snapshot-is-immutable-evidence:use 'provenance finding' or 'provenance accept-baseline'"
                    elif document.ContainsKey "contributions" || document.ContainsKey "contract" then
                        appendTo document |> Result.map render
                    else
                        match document["Provenance"] with
                        | :? JsonObject as raw ->
                            appendTo raw
                            |> Result.map (fun record ->
                                let updated = document.DeepClone().AsObject()
                                updated["Provenance"] <- record
                                render updated)
                        | _ -> Error "no-provenance-to-extend:absence-is-not-invented"))
            |> finish

    let private identityCommand context found =
        identity context found
        |> Result.map (fun acting ->
            let node = JsonObject()
            node["execution"] <- JsonValue.Create acting.ExecutionKey
            node["mechanism"] <- JsonValue.Create acting.Mechanism
            node["actor"] <- ProvenanceJson.actorNode acting.Actor
            render node)
        |> finish

    let private acceptBaselineCommand context snapshotPath found =
        tryReadSnapshot snapshotPath
        |> Result.bind (fun snapshot ->
            identity context found
            |> Result.bind (fun acting ->
                MeasurementProvenance.forBaselineAcceptance acting (context.Now()) (single "reason" found) snapshot
                |> Result.mapError provenanceError))
        |> Result.map render
        |> finish

    let execute (context: CliContext) (args: string list) : CliResult =
        match args with
        | "compare" :: rest ->
            match parseArguments actorOptions rest with
            | Ok([ beforePath; afterPath ], found) when File.Exists beforePath && File.Exists afterPath -> compareCommand context beforePath afterPath found
            | _ -> usageResult
        | [ "measure"; path ] when File.Exists path ->
            let source = File.ReadAllText path
            let structural = Structural.measure path source
            let quality = FSharpQuality.measure path source
            let complexity = Complexity.measure path source
            let agent = AgentQuality.fromMetrics structural quality
            ok (serialize {| path=path; structural=structural; quality=quality; complexity=complexity; agent=agent |})
        | [ "analyze"; root ] when Directory.Exists root ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            ok (serialize (Wire.repository (RepositoryAnalysis.analyze 6 Map.empty sources)))
        | [ "analyze"; root; "--git-history"; historyPath ] when Directory.Exists root && File.Exists historyPath ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let temporal = File.ReadAllText(historyPath) |> GitHistory.parse |> GitHistory.summarize
            ok (serialize (Wire.repository (RepositoryAnalysis.analyze 6 temporal sources)))
        | "snapshot" :: rest ->
            match parseArguments ([ "git-history"; "repository"; "revision"; "ref"; "subject-provenance" ] @ actorOptions) rest with
            | Ok([ root ], found) -> snapshotCommand context root found
            | _ -> usageResult
        | "provenance" :: "identity" :: rest ->
            match parseArguments actorOptions rest with
            | Ok([], found) -> identityCommand context found
            | _ -> usageResult
        | [ "provenance"; "finding"; snapshotPath; findingId ] when File.Exists snapshotPath ->
            tryReadSnapshot snapshotPath
            |> Result.bind (fun snapshot -> MeasurementProvenance.forFinding snapshot findingId |> Result.mapError provenanceError)
            |> Result.map render
            |> finish
        | "provenance" :: "append" :: rest ->
            match parseArguments ([ "operation"; "reason"; "evidence" ] @ actorOptions) rest with
            | Ok([ path ], found) when File.Exists path -> appendCommand context path found
            | _ -> usageResult
        | "provenance" :: "accept-baseline" :: rest ->
            match parseArguments ([ "reason" ] @ actorOptions) rest with
            | Ok([ snapshotPath ], found) when File.Exists snapshotPath -> acceptBaselineCommand context snapshotPath found
            | _ -> usageResult
        | _ -> usageResult

    /// A run id for `EXE-dokimos.<run>` when neither Praxis nor CI supplies one.
    let freshRun () =
        "local-"
        + DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", Globalization.CultureInfo.InvariantCulture)
        + "-"
        + Guid.NewGuid().ToString("N").Substring(0, 8)

    /// Only the whitelisted identity variables are ever read.
    let processContext =
        { Environment =
            fun name ->
                if List.contains name IdentityEnvironment.names then
                    Environment.GetEnvironmentVariable name |> Option.ofObj
                else
                    None
          Now = fun () -> DateTimeOffset.UtcNow
          FreshRun = freshRun }

    [<EntryPoint>]
    let main args =
        let result = execute processContext (Array.toList args)
        if result.Output <> "" then Console.WriteLine(result.Output)
        if result.ErrorOutput <> "" then Console.Error.WriteLine(result.ErrorOutput)
        result.ExitCode
