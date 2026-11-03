namespace Dokimos.Cli

open System
open System.Diagnostics
open System.IO
open Dokimos.Core
open Dokimos.Core.Verification

/// `dokimos verification compile|scaffold|coverage|probe`: the verification compiler. The pure
/// decisions live in Dokimos.Core.Verification; this module only reads and writes files and runs
/// processes.
module VerificationCommand =
    let private withArgs known args k =
        match Arguments.parse args |> Result.bind (Arguments.allowOnly known) with
        | Ok parsed -> k parsed
        | Error message -> Output.invalid "invalid-arguments" message

    let private readFile (path: string) =
        if File.Exists path then Ok(File.ReadAllText path) else Error $"{path} does not exist"

    let private relative (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let private excludedDirectory (path: string) =
        let name = Path.GetFileName path
        name = "bin" || name = "obj" || name = "node_modules" || name = ".git"

    let rec private filesUnder (dir: string) : string list =
        if not (Directory.Exists dir) then []
        else
            [ yield! Directory.GetFiles dir
              for sub in Directory.GetDirectories dir do
                  if not (excludedDirectory sub) then yield! filesUnder sub ]

    let private loadObligations (parsed: Arguments) =
        Arguments.required "obligations" parsed
        |> Result.bind readFile
        |> Result.bind ObligationSet.parse

    // ---------------------------------------------------------------- scan

    let private projectOf (root: string) (file: string) =
        let rec up (dir: string) =
            if String.IsNullOrEmpty dir || not (dir.StartsWith root) then None
            else
                match Directory.GetFiles(dir, "*.fsproj") |> Array.tryHead with
                | Some project -> Some project
                | None -> up (Path.GetDirectoryName dir |> Option.ofObj |> Option.defaultValue "")
        up (Path.GetDirectoryName file |> Option.ofObj |> Option.defaultValue "")

    /// Scans every suite's paths once and determines .NET compile membership.
    let scan (root: string) (set: ObligationSet) : ScanInput =
        let root = Path.GetFullPath root

        let tests =
            set.Suites
            |> List.collect (fun suite ->
                match suite.Runner with
                | DotnetTest ->
                    suite.Paths
                    |> List.collect (fun p -> filesUnder (Path.Combine(root, p)))
                    |> List.filter (fun f -> f.EndsWith ".fs")
                    |> List.map (fun f -> relative root f, f)
                | PlaywrightTest ->
                    let testRoot = suite.TestRoot |> Option.defaultValue ""
                    suite.Paths
                    |> List.collect (fun p -> filesUnder (Path.Combine(root, p)))
                    |> List.filter (fun f -> f.EndsWith ".spec.js" || f.EndsWith ".spec.ts" || f.EndsWith ".spec.mjs" || f.EndsWith ".test.js")
                    |> List.map (fun f -> relative (Path.Combine(root, testRoot)) f, f)
                | EvidenceRecord -> [])
            |> List.distinctBy snd
            |> List.collect (fun (rel, full) ->
                let text = File.ReadAllText full
                if full.EndsWith ".fs" then TestScan.scanFSharp rel text else TestScan.scanPlaywright rel text)

        let compiled =
            let cache = Collections.Concurrent.ConcurrentDictionary<string, string list>()
            fun (file: string) ->
                let full = Path.Combine(root, file)
                match projectOf root full with
                | None -> false
                | Some project ->
                    let includes = cache.GetOrAdd(project, fun p -> TestScan.compiledFiles (File.ReadAllText p))
                    TestScan.isCompiled includes (relative (Path.GetDirectoryName project |> nonNull) full)

        { Tests = tests; Compiled = compiled }

    // ---------------------------------------------------------------- results

    let private loadResults (set: ObligationSet) (parsed: Arguments) (root: string) : Result<ResultsInput, string> =
        let candidate = Arguments.tryOne "candidate" parsed
        match Arguments.tryOne "results" parsed with
        | None -> Ok(Coverage.admit (candidate |> Option.defaultValue "unknown") [])
        | Some manifestPath ->
            readFile manifestPath
            |> Result.bind TestResults.parseManifest
            |> Result.bind (fun manifest ->
                let candidate = candidate |> Option.defaultValue manifest.Candidate
                let baseDir = Path.GetDirectoryName(Path.GetFullPath manifestPath) |> nonNull
                manifest.Runs
                |> List.map (fun run ->
                    let path = if Path.IsPathRooted run.Path then run.Path else Path.Combine(baseDir, run.Path)
                    let testRoot = set.Suites |> List.tryFind (fun s -> s.Id = run.Suite) |> Option.bind _.TestRoot |> Option.defaultValue "tests"
                    readFile path
                    |> Result.bind (fun text ->
                        match run.Format with
                        | Trx -> TestResults.parseTrx text
                        | PlaywrightJson -> TestResults.parsePlaywrightJson text
                        | PlaywrightList -> Ok(TestResults.parsePlaywrightList testRoot text))
                    |> Result.map (fun outcomes -> run, outcomes))
                |> List.fold
                    (fun acc item ->
                        match acc, item with
                        | Ok items, Ok x -> Ok(x :: items)
                        | Error e, _ | _, Error e -> Error e)
                    (Ok [])
                |> Result.map (List.rev >> Coverage.admit candidate))

    let private loadProbes (parsed: Arguments) =
        Arguments.many "probes" parsed
        |> List.map (fun p -> readFile p |> Result.bind FaultProbe.parseResults)
        |> List.fold (fun acc r -> match acc, r with | Ok a, Ok b -> Ok(a @ b) | Error e, _ | _, Error e -> Error e) (Ok [])

    let private write (path: string option) (json: string) =
        match path with
        | Some p ->
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath p) |> nonNull) |> ignore
            File.WriteAllText(p, json)
        | None -> ()

    // ---------------------------------------------------------------- commands

    /// `compile`: the frozen Indy Init plan (or a dokimos obligation set) -> a validated
    /// `dokimos.verification-obligations/1`. Exit 4 with the refusals when any obligation is
    /// ambiguous or incomplete.
    let compile args =
        withArgs [ "indy-execution-plan"; "indy-verification-plan"; "obligations"; "out" ] args (fun parsed ->
            let set =
                match Arguments.tryOne "indy-execution-plan" parsed, Arguments.tryOne "indy-verification-plan" parsed with
                | Some exec, Some verif ->
                    readFile exec |> Result.bind (fun e -> readFile verif |> Result.bind (IndyInitPlan.read e))
                | None, None -> loadObligations parsed
                | _ -> Error "--indy-execution-plan and --indy-verification-plan go together"
            match set with
            | Error e -> Output.unavailable "obligations-unreadable" e
            | Ok set ->
                match ObligationSet.refusals set with
                | [] ->
                    let json = ObligationSet.toJson set
                    write (Arguments.tryOne "out" parsed) json
                    Output.data json
                | refusals ->
                    Output.dataWithCode
                        ExitCodes.PolicyFailure
                        (Contracts.serialize
                            {| schema = "dokimos.verification-refusals/1"
                               refused = refusals.Length
                               refusals = refusals |> List.map (fun r -> {| subject = r.Subject; reason = r.Reason |}) |}))

    /// `coverage`: exact per-obligation status at the candidate. With `--require-verified` it is
    /// a gate: exit 4 unless every in-scope obligation is verified.
    let coverage args =
        withArgs [ "obligations"; "root"; "results"; "candidate"; "probes"; "scope"; "require-verified"; "out"; "praxis-matrix"; "work-item"; "dimensions" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            match loadObligations parsed with
            | Error e -> Output.unavailable "obligations-unreadable" e
            | Ok set ->
                match loadResults set parsed root, loadProbes parsed with
                | Error e, _ | _, Error e -> Output.unavailable "results-unreadable" e
                | Ok results, Ok probes ->
                    let scope = Arguments.many "scope" parsed |> List.collect (fun s -> s.Split(',') |> List.ofArray)
                    let report = Coverage.reconcile set (scan root set) results probes scope
                    let json = Coverage.toJson set report
                    write (Arguments.tryOne "out" parsed) json
                    match Arguments.tryOne "praxis-matrix" parsed, Arguments.tryOne "work-item" parsed with
                    | Some path, Some workItem ->
                        let dimensions = Arguments.tryOne "dimensions" parsed |> Option.map (fun d -> d.Split(',') |> List.ofArray) |> Option.defaultValue [ "happy-path" ]
                        write (Some path) (Coverage.toPraxisMatrix set report workItem dimensions)
                    | _ -> ()
                    let gate = Arguments.tryOne "require-verified" parsed = Some "true"
                    Output.dataWithCode (if gate && not (Coverage.allVerified report) then ExitCodes.PolicyFailure else ExitCodes.Continue) json)

    /// `scaffold`: ranked work packages for every missing or invalid identity; with `--write true`
    /// creates the missing files (never overwrites; `--append true` adds missing tests to an
    /// existing file).
    let scaffold args =
        withArgs [ "obligations"; "root"; "first"; "write"; "append"; "import"; "work" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            match loadObligations parsed with
            | Error e -> Output.unavailable "obligations-unreadable" e
            | Ok set ->
                let report = Coverage.reconcile set (scan root set) (Coverage.admit "unknown" []) [] []
                let first = Arguments.many "first" parsed |> List.collect (fun s -> s.Split(',') |> List.ofArray)
                let packages = Scaffold.plan set report first
                let import = Arguments.tryOne "import" parsed |> Option.defaultValue "@playwright/test"
                let append = Arguments.tryOne "append" parsed = Some "true"
                if Arguments.tryOne "write" parsed = Some "true" then
                    for package in packages do
                        let full = Path.Combine(root, package.File)
                        let existing = if File.Exists full then Some(File.ReadAllText full) else None
                        match existing, append with
                        | Some _, false -> ()
                        | _ ->
                            match Scaffold.render set import existing package with
                            | Some(CreateFile(path, content)) ->
                                let target = Path.Combine(root, path)
                                Directory.CreateDirectory(Path.GetDirectoryName target |> nonNull) |> ignore
                                File.WriteAllText(target, content + "\n")
                            | Some(AppendToFile(path, content)) -> File.AppendAllText(Path.Combine(root, path), content + "\n")
                            | None -> ()
                let json = Scaffold.workJson set packages (Scaffold.unmapped set report)
                write (Arguments.tryOne "work" parsed) json
                Output.data json)

    // ---------------------------------------------------------------- probes

    let rec private copyTree (source: string) (target: string) =
        Directory.CreateDirectory target |> ignore
        for file in Directory.GetFiles source do
            File.Copy(file, Path.Combine(target, Path.GetFileName file |> nonNull), true)
        for dir in Directory.GetDirectories source do
            let name = Path.GetFileName dir |> nonNull
            if name <> ".git" && name <> "bin" && name <> "obj" then
                copyTree dir (Path.Combine(target, name))

    let private run (directory: string) (command: string list) (timeout: TimeSpan) =
        let info = ProcessStartInfo(command.Head, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = directory)
        command.Tail |> List.iter info.ArgumentList.Add
        use proc =
            match Process.Start info with
            | null -> raise (InvalidOperationException $"{command.Head} did not start")
            | p -> p
        let out = proc.StandardOutput.ReadToEndAsync()
        let err = proc.StandardError.ReadToEndAsync()
        if proc.WaitForExit(int timeout.TotalMilliseconds) then
            out.Wait()
            err.Wait()
            proc.ExitCode
        else
            proc.Kill true
            124

    let private withCopy (root: string) (action: string -> 'a) =
        let copy = Path.Combine(Path.GetTempPath(), "dokimos-probe-" + Guid.NewGuid().ToString("N"))
        copyTree (Path.GetFullPath root) copy
        try action copy
        finally
            try
                Directory.Delete(copy, true)
            with
            | :? IOException
            | :? UnauthorizedAccessException -> () // a leftover temporary copy is harmless; the result stands

    /// `probe`: for each fault probe, a disposable copy runs the command unfaulted (control) and
    /// faulted; the bound tests must pass the control and fail the fault. The working tree is
    /// never modified.
    let probe args =
        withArgs [ "spec"; "root"; "sha"; "out"; "timeout-seconds" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            let timeout = Arguments.tryOne "timeout-seconds" parsed |> Option.bind (fun s -> match Int32.TryParse s with | true, v -> Some v | _ -> None) |> Option.defaultValue 600 |> float |> TimeSpan.FromSeconds
            match Arguments.required "spec" parsed |> Result.bind readFile |> Result.bind FaultProbe.parse, Arguments.required "sha" parsed with
            | Error e, _ | _, Error e -> Output.invalid "invalid-arguments" e
            | Ok probes, Ok sha ->
                let controls = Collections.Generic.Dictionary<string list, int>(HashIdentity.Structural)
                let results =
                    probes
                    |> List.map (fun p ->
                        let control =
                            match controls.TryGetValue p.Command with
                            | true, code -> code
                            | _ ->
                                let code = withCopy root (fun copy -> run copy p.Command timeout)
                                controls[p.Command] <- code
                                code
                        let outcome =
                            withCopy root (fun copy ->
                                let target = Path.Combine(copy, p.File)
                                match (if File.Exists target then Ok(File.ReadAllText target) else Error $"{p.Id}: {p.File} does not exist") |> Result.bind (FaultProbe.apply p) with
                                | Error e -> ProbeError e
                                | Ok faulted ->
                                    File.WriteAllText(target, faulted)
                                    let built = if p.Build.IsEmpty then 0 else run copy p.Build timeout
                                    FaultProbe.classifyBuilt control built (if built = 0 then run copy p.Command timeout else 1))
                        { ProbeId = p.Id; Obligations = p.Obligations; SourceSha = sha; Outcome = outcome })
                let json = FaultProbe.resultsJson sha results
                write (Arguments.tryOne "out" parsed) json
                let failed = results |> List.exists (fun r -> r.Outcome <> Detected)
                Output.dataWithCode (if failed then ExitCodes.PolicyFailure else ExitCodes.Continue) json)

    // ---------------------------------------------------------------- requirements

    /// `dokimos verification requirements`: verify a Praxis verification manifest against
    /// application source, browser observations and executed test results. Exit 0 accepted,
    /// 4 not accepted, 3 the manifest itself is unavailable, 2 bad arguments.
    let private requirements args =
        withArgs [ "manifest"; "observations"; "results"; "root"; "candidate"; "screen"; "requirement"; "fact"; "out"; "text"; "ordo-evidence"; "run-id" ] args (fun parsed ->
            let stopwatch = Stopwatch.StartNew()

            match Arguments.required "manifest" parsed |> Result.bind readFile |> Result.bind RequirementVerification.parseManifest with
            | Error message -> Output.unavailable "manifest-unavailable" message
            | Ok manifest ->
                let root = Arguments.tryOne "root" parsed |> Option.defaultValue "." |> Path.GetFullPath

                let source (location: string) =
                    let full = Path.GetFullPath(Path.Combine(root, location))
                    if full.StartsWith(root, StringComparison.Ordinal) && File.Exists full then Some(File.ReadAllText full) else None

                let observations =
                    match Arguments.tryOne "observations" parsed with
                    | None -> Error "no --observations file was given"
                    | Some path when Directory.Exists path ->
                        // One file per test: parse each, then merge (same run, manifest and commit only).
                        Directory.GetFiles(path, "*.json")
                        |> Array.sort
                        |> Array.toList
                        |> List.map (fun file -> readFile file |> Result.bind RequirementVerification.parseObservations |> Result.mapError (fun e -> $"{file}: {e}"))
                        |> List.fold (fun acc next -> acc |> Result.bind (fun xs -> next |> Result.map (fun x -> xs @ [ x ]))) (Ok [])
                        |> Result.bind RequirementVerification.mergeObservations
                    | Some path -> readFile path |> Result.bind RequirementVerification.parseObservations

                let results =
                    match Arguments.tryOne "results" parsed with
                    | None -> Error "no --results file was given"
                    | Some path ->
                        readFile path
                        |> Result.bind TestResults.parsePlaywrightJson
                        |> Result.map (fun outcomes ->
                            // A test identity seen twice with different outcomes (two projects share
                            // nothing, so this is a retry) keeps its worst outcome.
                            outcomes
                            |> List.groupBy fst
                            |> List.map (fun (identity, seen) ->
                                let all = seen |> List.map snd |> List.distinct
                                identity, (match all with [ one ] -> one | _ -> Failed "outcomes differed between attempts"))
                            |> Map.ofList)

                let facts =
                    Arguments.many "fact" parsed
                    |> List.choose (fun text ->
                        match text.Split('=', 2) with
                        | [| name; "true" |] -> Some(name, true)
                        | [| name; "false" |] -> Some(name, false)
                        | _ -> None)
                    |> Map.ofList

                let evidence: Evidence =
                    { Observations = observations
                      Results = results
                      Source = source
                      Candidate = Arguments.tryOne "candidate" parsed
                      Facts = facts }

                let screens =
                    Arguments.many "screen" parsed |> List.collect (fun s -> s.Split(',', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

                let scope =
                    Arguments.many "requirement" parsed |> List.collect (fun s -> s.Split(',', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)

                let report = RequirementVerification.verifyScoped manifest screens scope evidence
                let json = RequirementVerification.toJson report
                write (Arguments.tryOne "out" parsed) json

                match Arguments.tryOne "ordo-evidence" parsed with
                | None -> ()
                | Some directory ->
                    Directory.CreateDirectory directory |> ignore
                    let runId = Arguments.tryOne "run-id" parsed |> Option.orElse report.RunId |> Option.defaultValue "unrecorded"

                    let evidenceFiles = RequirementOrdo.ordoEvidence report runId

                    for name, content in evidenceFiles do
                        File.WriteAllText(Path.Combine(directory, name), content)

                    File.WriteAllText(Path.Combine(directory, "evidence-manifest.json"), RequirementOrdo.ordoEvidenceManifest evidenceFiles)
                    File.WriteAllText(Path.Combine(directory, "verification-plan.json"), RequirementOrdo.ordoPlan report manifest.CatalogId)

                eprintfn "%s" (RequirementVerification.toText report)
                eprintfn "verified in %d ms" stopwatch.ElapsedMilliseconds
                Output.dataWithCode (if report.Accepted then ExitCodes.Continue else ExitCodes.PolicyFailure) json)

    let dispatch (args: string list) =
        match args with
        | "requirements" :: rest -> requirements rest
        | "compile" :: rest -> compile rest
        | "coverage" :: rest -> coverage rest
        | "scaffold" :: rest -> scaffold rest
        | "probe" :: rest -> probe rest
        | _ ->
            Output.invalid
                "invalid-arguments"
                "usage: dokimos verification requirements --manifest F [--observations F] [--results PLAYWRIGHT_JSON] [--root .] [--candidate SHA] [--screen S]... [--requirement ID]... [--fact NAME=true|false]... [--out F] [--ordo-evidence DIR] [--run-id ID]\n       dokimos verification compile (--indy-execution-plan F --indy-verification-plan F | --obligations F) [--out F]\n       dokimos verification scaffold --obligations F [--root .] [--first REQ,...] [--write true] [--append true] [--import MODULE] [--work F]\n       dokimos verification coverage --obligations F [--root .] [--results MANIFEST] [--candidate SHA] [--probes F]... [--scope REQ,...] [--require-verified true] [--out F] [--praxis-matrix F --work-item ID [--dimensions happy-path,...]]\n       dokimos verification probe --spec F --sha SHA [--root .] [--timeout-seconds N] [--out F]"
