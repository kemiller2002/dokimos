namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core

/// `dokimos ratchet ...`: the baseline-derived change-quality ratchet.
/// File-system effects stay here; judgement is QualityRatchet (pure).
module RatchetCommand =
    let private flags = [ "--json"; "--write" ]

    let private split (args: string list) =
        let present = args |> List.filter (fun a -> List.contains a flags) |> Set.ofList
        present, args |> List.filter (fun a -> not (List.contains a flags))

    let private withArgs known args k =
        let present, rest = split args
        match Arguments.parse rest |> Result.bind (Arguments.allowOnly known) with
        | Ok parsed -> k present parsed
        | Error message -> Output.invalid "invalid-arguments" message

    let private under (root: string) (path: string) =
        if Path.IsPathRooted path then path else Path.Combine(root, path)

    let private relative (root: string) (path: string) =
        Path.GetRelativePath(Path.GetFullPath root, Path.GetFullPath path).Replace('\\', '/')

    let private asOfFrom parsed =
        match Arguments.tryOne "at" parsed with
        | None -> Ok DateTimeOffset.UtcNow
        | Some text ->
            match DateTimeOffset.TryParse(text, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
            | true, value -> Ok value
            | _ -> Error $"--at '{text}' is not a timestamp"

    let private isCandidate (path: string) =
        QualitySignals.isFSharpSource path || QualitySignals.isProjectFile path

    /// Reads every candidate file under the configured source roots plus the
    /// repository-level MSBuild props. Missing roots are reported, not skipped.
    let private readFiles root (configuration: RatchetConfiguration) =
        let missing = configuration.Sources |> List.filter (fun s -> not (Directory.Exists(under root s)))
        if not missing.IsEmpty then Error(missing |> List.map (fun s -> $"source root '{s}' does not exist under {root}"))
        else
            let inRoots =
                configuration.Sources
                |> List.collect (fun s -> Directory.EnumerateFiles(under root s, "*", SearchOption.AllDirectories) |> List.ofSeq)
            let rootProps =
                [ "Directory.Build.props"; "Directory.Packages.props" ]
                |> List.map (under root)
                |> List.filter File.Exists
            (inRoots @ rootProps)
            |> List.map (relative root)
            |> List.filter isCandidate
            |> List.distinct
            |> List.sort
            |> List.map (fun path -> ({ Path = path; Content = File.ReadAllText(under root path) }: SourceFile))
            |> Ok

    let private readBuildLog root parsed =
        match Arguments.tryOne "build-log" parsed with
        | None -> Ok None
        | Some path when File.Exists(under root path) -> Ok(Some(File.ReadAllText(under root path)))
        | Some path -> Error $"build log {path} does not exist"

    let private measureWith root parsed configuration =
        match readFiles root configuration, readBuildLog root parsed with
        | Ok files, Ok log -> Ok(QualitySignals.measure configuration { Files = files; BuildLog = log })
        | Error reasons, _ -> Error reasons
        | _, Error reason -> Error [ reason ]

    let private emit (json: bool) code (data: string) (text: string) =
        Output.dataWithCode code (if json then data else text)

    // --- check ---------------------------------------------------------------

    let private humanReport (r: RatchetReportDto) =
        String.concat
            "\n"
            [ yield $"dokimos ratchet: {r.Verdict} (exit {r.ExitCode})"
              yield "baseline " + r.Baseline.Path + " " + defaultArg r.Baseline.Digest "(unavailable)"
              yield! r.Reasons |> List.map (fun reason -> "  reason: " + reason)
              for f in r.Findings do
                  yield $"  [{f.Kind}] {f.RuleId} {f.RuleName} {f.Scope}: {f.Before} -> {f.After}"
                  yield $"      {f.Degraded}"
                  if f.Kind = "regression" then
                      yield $"      remediation: {f.Remediation}"
                      yield $"      exception: {f.ExceptionProcess}"
                  for e in f.Evidence |> List.truncate 5 do
                      yield $"      at {e}"
              yield
                  $"regressions {r.Summary.Regressions}, excepted {r.Summary.Excepted}, improvements {r.Summary.Improvements}, rules measured {r.Summary.RulesMeasured}, unavailable {r.Summary.RulesUnavailable}, files {r.Summary.FilesAnalyzed}, generated excluded {r.Summary.GeneratedFilesExcluded}" ]

    let private reportOutput json (report: RatchetReportDto) =
        emit json report.ExitCode (Contracts.serialize report) (humanReport report)

    let check args =
        withArgs [ "root"; "baseline"; "exceptions"; "build-log"; "at" ] args (fun present parsed ->
            let json = present.Contains "--json"
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            let baselineArg = Arguments.tryOne "baseline" parsed |> Option.defaultValue QualityRules.BaselinePath
            let exceptionsArg = Arguments.tryOne "exceptions" parsed
            let exceptionsPath = exceptionsArg |> Option.defaultValue QualityRules.ExceptionsPath
            match asOfFrom parsed with
            | Error message -> Output.invalid "invalid-arguments" message
            | Ok asOf ->
                let unavailable reasons = reportOutput json (RatchetContract.unavailableReport asOf baselineArg exceptionsPath reasons)
                let baselineFile = under root baselineArg
                if not (File.Exists baselineFile) then
                    unavailable [ $"baseline {baselineArg} does not exist; create it with `dokimos ratchet baseline init`" ]
                else
                    match RatchetContract.readBaseline (File.ReadAllText baselineFile) with
                    | Error reason -> unavailable [ reason ]
                    | Ok baseline ->
                        let exceptionsFile = under root exceptionsPath
                        let exceptions, digest =
                            if File.Exists exceptionsFile then
                                let text = File.ReadAllText exceptionsFile
                                RatchetContract.readExceptions text, Some("sha256:" + Contracts.sha256 text)
                            elif exceptionsArg.IsSome then
                                { Valid = []; Invalid = [ { Id = "(file)"; Problems = [ $"exceptions file {exceptionsPath} does not exist" ] } ] }, None
                            else RatchetContract.emptyExceptions, None
                        match measureWith root parsed baseline.Configuration with
                        | Error reasons -> unavailable reasons
                        | Ok measurement ->
                            let result = QualityRatchet.check asOf baseline exceptions measurement
                            reportOutput json (RatchetContract.report baselineArg exceptionsPath digest result))

    // --- baseline lifecycle --------------------------------------------------

    let private parseForbidden (text: string) =
        match text.Split('=', 2) with
        | [| from; into |] when from.Trim() <> "" && into.Trim() <> "" -> Ok { From = from.Trim(); To = into.Trim() }
        | _ -> Error $"--forbid '{text}' must be From=To"

    let private sequence (results: Result<'a, string> list) =
        List.foldBack (fun r acc -> match r, acc with | Ok x, Ok xs -> Ok(x :: xs) | Error e, _ | _, Error e -> Error e) results (Ok [])

    let private writeIfRequested write path (baseline: RatchetBaseline) =
        if write then
            match Path.GetDirectoryName(Path.GetFullPath path) with
            | null -> ()
            | directory -> Directory.CreateDirectory directory |> ignore
            File.WriteAllText(path, RatchetContract.writeBaseline baseline)

    let init args =
        let known = [ "root"; "baseline"; "repository"; "source"; "generated"; "large-file-lines"; "forbid"; "rule"; "build-log"; "at" ]
        withArgs known args (fun present parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            let baselineArg = Arguments.tryOne "baseline" parsed |> Option.defaultValue QualityRules.BaselinePath
            let baselineFile = under root baselineArg
            let write = present.Contains "--write"
            let sources = match Arguments.many "source" parsed with [] -> [ "src" ] | s -> s
            let generated = match Arguments.many "generated" parsed with [] -> [ "**/bin/**"; "**/obj/**" ] | g -> g
            let rules = match Arguments.many "rule" parsed with [] -> QualityRules.ratchetRuleIds | r -> r
            let large =
                match Arguments.tryOne "large-file-lines" parsed with
                | None -> Ok 400
                | Some text ->
                    match Int32.TryParse text with
                    | true, n when n > 0 -> Ok n
                    | _ -> Error $"--large-file-lines '{text}' must be a positive integer"
            let unknownRules = rules |> List.filter (fun r -> not (List.contains r QualityRules.ratchetRuleIds))
            match Arguments.required "repository" parsed, large, sequence (Arguments.many "forbid" parsed |> List.map parseForbidden), asOfFrom parsed with
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Output.invalid "invalid-arguments" e
            | _ when not unknownRules.IsEmpty -> Output.invalid "unknown-rule" ("not ratchet rules: " + String.Join(", ", unknownRules))
            | _ when write && File.Exists baselineFile ->
                Output.invalid "baseline-exists" $"{baselineArg} exists; the baseline can only be tightened with `dokimos ratchet baseline update`"
            | Ok repository, Ok largeFileLines, Ok forbidden, Ok asOf ->
                let configuration = { Sources = sources; Generated = generated; LargeFileLines = largeFileLines; ForbiddenReferences = forbidden }
                match measureWith root parsed configuration with
                | Error reasons -> Output.unavailable "ratchet-measurement-unavailable" (String.Join("; ", reasons))
                | Ok measurement ->
                    match QualityRatchet.initialize asOf repository configuration rules measurement with
                    | Error reasons -> Output.unavailable "ratchet-measurement-unavailable" (String.Join("; ", reasons))
                    | Ok baseline ->
                        writeIfRequested write baselineFile baseline
                        let update = { Updated = baseline; Changes = []; Refused = [] }
                        Output.data (Contracts.serialize (RatchetContract.updateDto "init" write baselineArg None update)))

    let update args =
        withArgs [ "root"; "baseline"; "build-log"; "at" ] args (fun present parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            let baselineArg = Arguments.tryOne "baseline" parsed |> Option.defaultValue QualityRules.BaselinePath
            let baselineFile = under root baselineArg
            let write = present.Contains "--write"
            match asOfFrom parsed with
            | Error message -> Output.invalid "invalid-arguments" message
            | Ok _ when not (File.Exists baselineFile) ->
                Output.unavailable "baseline-not-found" $"{baselineArg} does not exist; create it with `dokimos ratchet baseline init`"
            | Ok asOf ->
                match RatchetContract.readBaseline (File.ReadAllText baselineFile) with
                | Error reason -> Output.invalid "baseline-invalid" reason
                | Ok baseline ->
                    match measureWith root parsed baseline.Configuration with
                    | Error reasons -> Output.unavailable "ratchet-measurement-unavailable" (String.Join("; ", reasons))
                    | Ok measurement ->
                        match QualityRatchet.update asOf baseline measurement with
                        | Error reasons -> Output.unavailable "ratchet-measurement-unavailable" (String.Join("; ", reasons))
                        | Ok result ->
                            let changed = not result.Changes.IsEmpty
                            writeIfRequested (write && changed) baselineFile result.Updated
                            let dto = RatchetContract.updateDto "update" (write && changed) baselineArg (Some(RatchetContract.baselineDigest baseline)) result
                            Output.data (Contracts.serialize dto))

    let diff args =
        withArgs [ "from"; "to" ] args (fun _ parsed ->
            match Arguments.required "from" parsed, Arguments.required "to" parsed with
            | Ok fromPath, Ok toPath ->
                let read label path =
                    if not (File.Exists path) then Error $"{label} baseline {path} does not exist"
                    else RatchetContract.readBaseline (File.ReadAllText path) |> Result.mapError (fun e -> $"{label}: {e}")
                match read "from" fromPath, read "to" toPath with
                | Ok older, Ok newer ->
                    let dto = RatchetContract.diffDto older newer (QualityRatchet.diff older newer)
                    Output.dataWithCode dto.ExitCode (Contracts.serialize dto)
                | Error e, _
                | _, Error e -> Output.unavailable "baseline-unavailable" e
            | Error e, _
            | _, Error e -> Output.invalid "invalid-arguments" (e + "; usage: dokimos ratchet baseline diff --from old.json --to new.json"))

    let usage =
        String.concat
            "\n"
            [ "usage: dokimos ratchet <command>"
              "  check [--root .] [--baseline quality/baseline.json] [--exceptions quality/exceptions.json] [--build-log f] [--at t] [--json]"
              "  baseline init --repository r [--source dir]... [--generated glob]... [--large-file-lines n] [--forbid From=To]... [--rule id]... [--build-log f] [--write]"
              "  baseline update [--root .] [--baseline path] [--build-log f] [--write]   tighten only; never raises a value"
              "  baseline diff --from old.json --to new.json   exit 4 when the newer baseline loosens anything"
              "  rules                                          the rule catalog"
              "check exit codes: 0 pass, 4 regression, 6 invalid or expired exceptions, 3 unavailable, 2 invalid invocation, 1 unexpected fault" ]

    let dispatch (args: string list) =
        match args with
        | "check" :: rest -> check rest
        | "baseline" :: "init" :: rest -> init rest
        | "baseline" :: "update" :: rest -> update rest
        | "baseline" :: "diff" :: rest -> diff rest
        | [ "rules" ] -> Output.data (Contracts.serialize (RatchetContract.catalogDto ()))
        | _ -> Output.invalid "invalid-arguments" usage
