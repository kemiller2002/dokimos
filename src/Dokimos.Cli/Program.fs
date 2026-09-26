namespace Dokimos.Cli

open System
open System.Globalization
open System.IO
open System.Text.Json
open Dokimos.Core

type MeasureOptions =
    { Path: string
      Declaration: ActorDeclaration
      RunId: string option
      MeasuredAt: string option
      DerivedFrom: string list
      SubjectProvenance: string option }

module Program =
    let options = JsonSerializerOptions(WriteIndented = true)

    let sourceFiles root =
        Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> not (p.Contains(string Path.DirectorySeparatorChar + "obj" + string Path.DirectorySeparatorChar)))
        |> Seq.filter (fun p -> not (p.Contains(string Path.DirectorySeparatorChar + "bin" + string Path.DirectorySeparatorChar)))
        |> Seq.sort
        |> Seq.toList

    let private usage =
        String.concat
            Environment.NewLine
            [ "Usage: dokimos measure <source-file> [options]"
              "       dokimos analyze <source-directory> [--git-history <numstat-file>]"
              ""
              "Provenance options (R14; explicit declarations only, otherwise 'unknown'):"
              "  --actor-json JSON          Praxis actor object for the measuring actor"
              "  --actor-kind KIND          agent | human | automation | unknown | x-..."
              "  --actor-id ID              stable actor id"
              "  --provider NAME            --model NAME            --runtime NAME"
              "  --execution KEY            invoking execution (EXE-... or EXT-<system>.<run-id>)"
              "  --run-id ID                Dokimos run id; key EXT-dokimos.<run-id> when no execution is declared"
              "  --measured-at TIMESTAMP    ISO-8601 UTC time of the measurement (default: now)"
              "  --derived-from REF         lineage reference, e.g. git:commit/<sha> (repeatable)"
              "  --subject-provenance FILE  the measured artifact's recorded praxis.provenance block"
              ""
              "Environment: ROS_ACTOR_KIND, ROS_ACTOR, ROS_TELEMETRY_PROVIDER, ROS_TELEMETRY_MODEL,"
              "ROS_TELEMETRY_RUNTIME, ROS_EXECUTION_ID. Flags win over the environment." ]

    let rec private parseOptions (options: MeasureOptions) (args: string list) : Result<MeasureOptions, string> =
        let flags = options.Declaration

        match args with
        | [] -> Ok options
        | "--actor-json" :: value :: rest -> parseOptions { options with Declaration = { flags with ActorJson = Some value } } rest
        | "--actor-kind" :: value :: rest -> parseOptions { options with Declaration = { flags with Kind = Some value } } rest
        | "--actor-id" :: value :: rest -> parseOptions { options with Declaration = { flags with Id = Some value } } rest
        | "--provider" :: value :: rest -> parseOptions { options with Declaration = { flags with Provider = Some value } } rest
        | "--model" :: value :: rest -> parseOptions { options with Declaration = { flags with Model = Some value } } rest
        | "--runtime" :: value :: rest -> parseOptions { options with Declaration = { flags with Runtime = Some value } } rest
        | "--execution" :: value :: rest -> parseOptions { options with Declaration = { flags with Execution = Some value } } rest
        | "--run-id" :: value :: rest -> parseOptions { options with RunId = Some value } rest
        | "--measured-at" :: value :: rest -> parseOptions { options with MeasuredAt = Some value } rest
        | "--derived-from" :: value :: rest -> parseOptions { options with DerivedFrom = options.DerivedFrom @ [ value ] } rest
        | "--subject-provenance" :: value :: rest -> parseOptions { options with SubjectProvenance = Some value } rest
        | option :: _ -> Error $"unknown or incomplete option '{option}'"

    let private environment (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | value -> Some value

    /// A fresh Dokimos run id: it identifies this run only, like an EXE id.
    let private newRunId (now: DateTimeOffset) =
        let stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        $"measure-{stamp}-{suffix}"

    let private measuredAt (options: MeasureOptions) (now: DateTimeOffset) =
        match options.MeasuredAt with
        | None -> Ok now
        | Some text ->
            match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, value -> Ok value
            | _ -> Error $"--measured-at '{text}' is not an ISO-8601 timestamp"

    let private subject (options: MeasureOptions) : Result<Json option, string> =
        match options.SubjectProvenance with
        | None -> Ok None
        | Some path when not (File.Exists path) -> Error $"--subject-provenance file '{path}' does not exist"
        | Some path ->
            Json.parse (File.ReadAllText path)
            |> Result.mapError (fun message -> $"--subject-provenance is {message}")
            |> Result.bind (fun block -> RecordAttribution.receive "--subject-provenance" (Some block) |> Result.map (fun _ -> Some block))

    let private measure (options: MeasureOptions) =
        let now = DateTimeOffset.UtcNow
        let declaration = ActorDeclaration.overriding (ActorDeclaration.fromEnvironment environment) options.Declaration
        let runId = options.RunId |> Option.defaultWith (fun () -> newRunId now)

        let provenance =
            measuredAt options now
            |> Result.bind (fun at ->
                MeasurementAttribution.resolve declaration runId
                |> Result.map (fun (actor, key) -> MeasurementAttribution.contribution actor key at (Some "Dokimos structural measurement")))
            |> Result.bind (fun contribution -> MeasurementAttribution.block contribution options.DerivedFrom)

        match provenance, subject options with
        | Ok block, Ok subjectBlock ->
            let source = File.ReadAllText options.Path
            let structural = Structural.measure options.Path source
            let quality = FSharpQuality.measure options.Path source
            let complexity = Complexity.measure options.Path source
            // Heuristic indicators only; they never attribute authorship (R14.6).
            let agent = AgentQuality.fromMetrics structural quality
            let result = {| path = options.Path; structural = structural; quality = quality; complexity = complexity; agent = agent |}

            match Json.parse (JsonSerializer.Serialize result) with
            | Ok measured ->
                let withSubject value =
                    match subjectBlock with
                    | Some item -> Json.setField "subjectProvenance" item value
                    | None -> value

                Console.WriteLine(measured |> Json.setField "provenance" block |> withSubject |> Json.serializeIndented)
                0
            | Error message ->
                Console.Error.WriteLine $"dokimos: could not serialize the measurement: {message}"
                1
        | Error message, _
        | _, Error message ->
            Console.Error.WriteLine $"dokimos: {message}"
            2

    [<EntryPoint>]
    let main args =
        match args |> Array.toList with
        | "measure" :: path :: rest when File.Exists path ->
            let initial =
                { Path = path
                  Declaration = ActorDeclaration.empty
                  RunId = None
                  MeasuredAt = None
                  DerivedFrom = []
                  SubjectProvenance = None }

            match parseOptions initial rest with
            | Ok options -> measure options
            | Error message ->
                Console.Error.WriteLine $"dokimos: {message}"
                Console.Error.WriteLine usage
                2
        | ["analyze"; root] when Directory.Exists root ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let result = RepositoryAnalysis.analyze 6 Map.empty sources
            Console.WriteLine(JsonSerializer.Serialize(result, options))
            0
        | ["analyze"; root; "--git-history"; historyPath] when Directory.Exists root && File.Exists historyPath ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let temporal = File.ReadAllText(historyPath) |> GitHistory.parse |> GitHistory.summarize
            let result = RepositoryAnalysis.analyze 6 temporal sources
            Console.WriteLine(JsonSerializer.Serialize(result, options))
            0
        | _ ->
            Console.Error.WriteLine usage
            2
