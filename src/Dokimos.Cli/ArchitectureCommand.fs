namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core

/// `dokimos architecture check`: RQ-DOKIMOS-ARCH-001 rules. File-system
/// effects stay here; activation and detection are ReliabilityArchitecture
/// (pure).
module ArchitectureCommand =
    let private skippedDirectories = set [ "bin"; "obj"; ".git"; "node_modules" ]

    let private under (root: string) (path: string) =
        if Path.IsPathRooted path then path else Path.Combine(root, path)

    let private relative (root: string) (path: string) =
        Path.GetRelativePath(Path.GetFullPath root, Path.GetFullPath path).Replace('\\', '/')

    /// Every project file in the repository except build output, VCS data,
    /// node_modules, symbolic-link directories and declared generated globs.
    /// A dependency ban is not scoped by the ratchet's source roots.
    let rec private walk (directory: string) : string seq =
        seq {
            yield! Directory.EnumerateFiles directory
            for sub in Directory.EnumerateDirectories directory do
                let info = DirectoryInfo sub
                let isLink = match info.LinkTarget with null -> false | _ -> true
                if not (skippedDirectories.Contains info.Name) && not isLink then
                    yield! walk sub
        }

    let private projectFiles root (generated: string list) =
        walk root
        |> Seq.map (relative root)
        |> Seq.filter ReliabilityArchitecture.isProjectFile
        |> Seq.filter (fun path -> not (Glob.anyMatch generated path))
        |> Seq.sort
        |> Seq.map (fun path -> ({ Path = path; Content = File.ReadAllText(under root path) }: SourceFile))
        |> List.ofSeq

    let private readOptional path =
        if File.Exists path then Some(File.ReadAllText path) else None

    let private humanReport (r: ArchitectureReportDto) =
        String.concat
            "\n"
            [ yield $"dokimos architecture: {r.Verdict} (exit {r.ExitCode})"
              yield $"declaration {r.Declaration.Path}: {r.Declaration.State}"
              yield! r.Reasons |> List.map (fun reason -> "  reason: " + reason)
              for rule in r.Rules do
                  yield $"  {rule.RuleId} {rule.Name} [{rule.Tenet}]: {rule.State}" + (rule.Reason |> Option.map (fun x -> " - " + x) |> Option.defaultValue "")
              for f in r.Findings do
                  yield $"  [{f.Kind}] {f.RuleId} {f.Scope}"
                  yield $"      {f.Detail}"
                  if f.Kind = "violation" then
                      yield $"      remediation: {f.Remediation}"
                      yield $"      exception: {f.ExceptionProcess}"
              yield
                  $"violations {r.Summary.Violations}, accepted {r.Summary.Accepted}, evaluated {r.Summary.RulesEvaluated}, not applicable {r.Summary.RulesNotApplicable}, not configured {r.Summary.RulesNotConfigured}, unavailable {r.Summary.RulesUnavailable}, project files {r.Summary.ProjectFilesAnalyzed}" ]

    let private asOfFrom parsed =
        match Arguments.tryOne "at" parsed with
        | None -> Ok DateTimeOffset.UtcNow
        | Some text ->
            match DateTimeOffset.TryParse(text, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
            | true, value -> Ok value
            | _ -> Error $"--at '{text}' is not a timestamp"

    let check (args: string list) =
        let json = List.contains "--json" args
        let rest = args |> List.filter ((<>) "--json")
        match Arguments.parse rest |> Result.bind (Arguments.allowOnly [ "root"; "declaration"; "baseline"; "exceptions"; "at" ]) with
        | Error message -> Output.invalid "invalid-arguments" message
        | Ok parsed ->
            match asOfFrom parsed with
            | Error message -> Output.invalid "invalid-arguments" message
            | Ok asOf ->
                let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
                let declarationArg = Arguments.tryOne "declaration" parsed |> Option.defaultValue ReliabilityArchitectureContract.DeclarationPath
                let baselineArg = Arguments.tryOne "baseline" parsed |> Option.defaultValue QualityRules.BaselinePath
                let exceptionsArg = Arguments.tryOne "exceptions" parsed
                let exceptionsPath = exceptionsArg |> Option.defaultValue QualityRules.ExceptionsPath
                let baseline = readOptional (under root baselineArg) |> Option.map RatchetContract.readBaseline
                let boundaries, generated =
                    match baseline with
                    | None -> BoundariesConfigured [], []
                    | Some (Ok b) -> BoundariesConfigured b.Configuration.ApiBoundaries, b.Configuration.Generated
                    | Some (Error reason) -> BoundariesUnreadable $"{baselineArg}: {reason}", []
                let exceptionsText = readOptional (under root exceptionsPath)
                let exceptions =
                    match exceptionsText, exceptionsArg with
                    | Some text, _ -> RatchetContract.readExceptions text
                    | None, Some _ -> { Valid = []; Invalid = [ { Id = "(file)"; Problems = [ $"exceptions file {exceptionsPath} does not exist" ] } ] }
                    | None, None -> RatchetContract.emptyExceptions
                let inputs =
                    { Declaration = ReliabilityArchitectureContract.readDeclaration (readOptional (under root declarationArg))
                      ProjectFiles = projectFiles root generated
                      ApiBoundaries = boundaries
                      Exceptions = exceptions }
                let digest = exceptionsText |> Option.map (fun t -> "sha256:" + Contracts.sha256 t)
                let report =
                    ReliabilityArchitecture.check asOf inputs
                    |> ReliabilityArchitectureContract.report declarationArg exceptionsPath digest
                Output.dataWithCode report.ExitCode (if json then Contracts.serialize report else humanReport report)

    let usage =
        String.concat
            "\n"
            [ "usage: dokimos architecture check [--root .] [--declaration .echelon/reliability.json] [--baseline quality/baseline.json] [--exceptions quality/exceptions.json] [--at t] [--json]"
              "rules DOK-ARCH-001 (thin API boundary), DOK-ARCH-005 (cache dependency ban), DOK-ARCH-006 (infrastructure dependency ban); see docs/rules/"
              "exit codes: 0 pass or not-evaluated (not a pass), 4 violation, 6 invalid or expired exceptions, 3 unavailable, 2 invalid invocation" ]

    let dispatch (args: string list) =
        match args with
        | "check" :: rest -> check rest
        | _ -> Output.invalid "invalid-arguments" usage
