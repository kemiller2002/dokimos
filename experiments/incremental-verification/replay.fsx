// EII Workstream G: replays a real commit sequence of this repository against
// three verification strategies and measures what each one executes.
//
//   full    every test project at every commit
//   select  dependency-aware selection only: a test project runs when a changed
//           path is inside its dependency closure (ProjectReference graph plus
//           declared data inputs), when a global input changed, or when a changed
//           path is covered by no mapping; nothing is recorded
//   reuse   selection plus safe evidence reuse: `dokimos evidence plan --store`
//           decides, and every run, passing or failing, is published with
//           `dokimos evidence record`
//
// usage (build src/Dokimos.Core and src/Dokimos.Cli in Release first; the
// script loads the Release Dokimos.Core assembly, and --tool names a copy of
// the Release Dokimos.Cli.dll so the replay never runs the tool under test):
//   dotnet fsi replay.fsx prepare   --source REPO --work DIR --commits N --defects DIR
//   dotnet fsi replay.fsx run       --work DIR --tool DLL --sequence clean|defects --strategy full|select|reuse [--final-release]
//
// Optional (Echelon development speed optimization, Priority Four):
//   prepare --windows D3:2-3,D1:5-7,D2:9-11   defect windows for a shorter sequence
//   run --final-release                       the last step is the release checkpoint:
//                                             select runs every unit, reuse plans with
//                                             `--checkpoint release` (mandatory final verification)
//   dotnet fsi replay.fsx summarize --work DIR --out FILE.json
//
// Everything is measured: wall-clock comes from a Stopwatch around each real
// process, test counts from each run's TRX counters. Nothing is estimated.
let args = fsi.CommandLineArgs |> Array.skip 1
#r "../../src/Dokimos.Core/bin/Release/net8.0/Dokimos.Core.dll"
open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Xml.Linq
open Dokimos.Core
open Dokimos.Core.EvidenceReuse

type Proc = { Exit: int; Out: string; Err: string; Seconds: float }

let exec (cwd: string) (file: string) (argv: string list) =
    let info = ProcessStartInfo(file, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd)
    argv |> List.iter info.ArgumentList.Add
    info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    info.Environment["DOTNET_NOLOGO"] <- "1"
    let clock = Stopwatch.StartNew()
    use p = Process.Start info
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    p.WaitForExit()
    { Exit = p.ExitCode; Out = out.Result; Err = err.Result; Seconds = clock.Elapsed.TotalSeconds }

let gitOut cwd argv =
    let r = exec cwd "git" argv
    if r.Exit <> 0 then failwithf "git %A failed: %s" argv r.Err
    r.Out.Trim()

let opt name = args |> Array.tryFindIndex ((=) ("--" + name)) |> Option.map (fun i -> args.[i + 1])
let req name = opt name |> Option.defaultWith (fun () -> failwithf "missing --%s" name)

// ---- declared verification units --------------------------------------------------------------------------------

type VerificationUnit = { Id: string; Project: string; Data: string list }

let units =
    [ { Id = "domain-tests"; Project = "tests/Dokimos.Domain.Tests/Dokimos.Domain.Tests.fsproj"; Data = [] }
      { Id = "core-tests"; Project = "tests/Dokimos.Core.Tests/Dokimos.Core.Tests.fsproj"
        Data = [ "tests/fixtures/**"; "oracles/**"; "config/**" ] }
      { Id = "cli-tests"; Project = "tests/Dokimos.Cli.Tests/Dokimos.Cli.Tests.fsproj"
        Data = [ "tests/fixtures/**"; "oracles/**"; "config/**"; "schemas/**"; "quality/**"; "baselines/**"; "ros.json" ] } ]

/// Inputs of every unit: build configuration shared by all projects.
let globalInputs = [ "Directory.Build.props"; "Dokimos.sln"; "global.json"; "NuGet.config"; ".editorconfig" ]

/// Paths declared to affect no unit. Anything else that no unit maps runs every unit.
let ignoredPaths =
    [ ".ros/**"; ".praxis/**"; ".sde/**"; ".github/**"; ".visual-engineering/**"; "docs/**"; "experiments/**"; "research/**"
      "requirements/**"; "context/**"; "missions/**"; "observations/**"; "framework/**"; "registries/**"; "telemetry/**"
      "echelon/**"; "site/**"; "results-ui/**"; "release/**"; "**/*.md"; "LICENSE"; "NOTICE"; "CNAME"; "package.json"
      "package-lock.json" ]

/// The unit's dependency closure, read from the ProjectReference graph of the
/// checked-out tree: each project directory in it is a source pattern.
let rec closure (root: string) (project: string) : string list =
    let directory = Path.GetDirectoryName project
    let references =
        XDocument.Load(Path.Combine(root, project)).Descendants(XName.Get "ProjectReference")
        |> Seq.map (fun e -> e.Attribute(XName.Get "Include").Value.Replace('\\', '/'))
        |> Seq.map (fun inc -> Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, directory, inc))).Replace('\\', '/'))
        |> Seq.toList
    project :: (references |> List.collect (closure root)) |> List.distinct

let sourcesOf root (u: VerificationUnit) =
    (closure root u.Project |> List.map (fun p -> Path.GetDirectoryName(p).Replace('\\', '/') + "/**")) @ u.Data

let configuration =
    Map [ "Configuration", "Debug"; "runtime.rid", Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier; "runtime.framework", "net8.0" ]

let mappingOf root toolVersion (u: VerificationUnit) =
    {| Sources = sourcesOf root u; TestContract = u.Project; Configuration = configuration; ToolVersion = toolVersion |}

let requestJson revision (changed: string list option) (tests: obj list) (priors: JsonNode list) =
    let d = Collections.Generic.Dictionary<string, obj>()
    d["SchemaVersion"] <- "dokimos.evidence-reuse-request/1"
    d["Revision"] <- revision
    changed |> Option.iter (fun c -> d["ChangedPaths"] <- c)
    d["GlobalInputs"] <- globalInputs
    d["IgnoredPaths"] <- ignoredPaths
    d["Tests"] <- tests
    d["PriorEvidence"] <- priors
    JsonSerializer.Serialize d

// ---- prepare ----------------------------------------------------------------------------------------------------

type Step = { Step: int; Sha: string; Original: string; Defects: string[] }

/// Defect windows: (patch-file prefix, first step, last step). Each window
/// applies the patch to the tree of every real commit in it, so the defect is
/// present for several consecutive commits and removed at the next one.
let windows =
    match opt "windows" with
    | None -> [ "D3", 2, 4; "D1", 11, 13; "D2", 16, 18 ]
    | Some text ->
        text.Split(',', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun w ->
            match w.Split(':') with
            | [| id; range |] ->
                match range.Split('-') with
                | [| a; b |] -> id, int a, int b
                | _ -> failwithf "window %s: expected ID:FIRST-LAST" w
            | _ -> failwithf "window %s: expected ID:FIRST-LAST" w)
        |> Array.toList

let stepsJson (steps: Step list) = JsonSerializer.Serialize(Array.ofList steps, JsonSerializerOptions(WriteIndented = true))

let prepare () =
    let work = Path.GetFullPath(req "work")
    let source = Path.Combine(work, "source")
    let defects = Path.GetFullPath(req "defects")
    let n = int (req "commits")
    Directory.CreateDirectory work |> ignore
    if not (Directory.Exists source) then gitOut work [ "clone"; "-q"; "--no-local"; Path.GetFullPath(req "source"); source ] |> ignore
    let shas = (gitOut source [ "rev-list"; "--first-parent"; "-n"; string n; "origin/main" ]).Split('\n') |> Array.rev |> Array.toList
    let clean = shas |> List.mapi (fun i sha -> { Step = i + 1; Sha = sha; Original = sha; Defects = [||] })
    let patchFor id = Directory.GetFiles(defects, id + "-*.patch") |> Array.exactlyOne
    let variant (s: Step) =
        match windows |> List.filter (fun (_, a, b) -> s.Step >= a && s.Step <= b) with
        | [] -> s
        | active ->
            gitOut source [ "checkout"; "-q"; "--force"; "--detach"; s.Original ] |> ignore
            for id, _, _ in active do gitOut source [ "apply"; patchFor id ] |> ignore
            gitOut source [ "-c"; "user.name=replay"; "-c"; "user.email=replay@example.invalid"; "commit"; "-q"; "-am"
                            "seeded defect " + String.Join(",", active |> List.map (fun (id, _, _) -> id)) ] |> ignore
            // Tag the variant so clones of the source receive it.
            gitOut source [ "tag"; "-f"; $"replay-defect-step-{s.Step}" ] |> ignore
            { s with Sha = gitOut source [ "rev-parse"; "HEAD" ]; Defects = active |> List.map (fun (id, _, _) -> id) |> Array.ofList }
    let withDefects = clean |> List.map variant
    gitOut source [ "checkout"; "-q"; "--force"; "--detach"; "origin/main" ] |> ignore
    File.WriteAllText(Path.Combine(work, "sequence-clean.json"), stepsJson clean)
    File.WriteAllText(Path.Combine(work, "sequence-defects.json"), stepsJson withDefects)
    let describe (s: Step) = string s.Step + ":" + String.Join(",", s.Defects)
    printfn "prepared %d steps; defect steps: %s" clean.Length (String.Join(" ", withDefects |> List.filter (fun s -> not (Array.isEmpty s.Defects)) |> List.map describe))

// ---- run --------------------------------------------------------------------------------------------------------

let readSteps (work: string) name =
    JsonSerializer.Deserialize<Step[]>(File.ReadAllText(Path.Combine(work, $"sequence-{name}.json"))) |> Array.toList

let trxCounts (path: string) =
    if File.Exists path then
        let c = XDocument.Load(path).Descendants() |> Seq.find (fun e -> e.Name.LocalName = "Counters")
        int (c.Attribute(XName.Get "total").Value), int (c.Attribute(XName.Get "failed").Value)
    else 0, -1

/// The adversarial reuse battery: for each unit, a positive control (a valid
/// passing record at the current identity, which must be reused) and eleven
/// incorrect reuse attempts, which must all be refused. The request carries no
/// ChangedPaths, so only the reuse rules, not path selection, decide.
let battery (wc: string) (tool: string) revision previous (planDoc: JsonDocument) toolVersion =
    let current =
        planDoc.RootElement.GetProperty("Tests").EnumerateArray()
        |> Seq.choose (fun t ->
            let inputs = t.GetProperty("Inputs")
            if inputs.ValueKind <> JsonValueKind.Object then None
            else
                let node = JsonNode.Parse(inputs.GetRawText())
                let record = JsonObject()
                record["SchemaVersion"] <- JsonValue.Create ProvenanceSchemaVersion
                record["EvidenceId"] <- JsonValue.Create "x"
                record["TestId"] <- JsonValue.Create "x"
                record["Outcome"] <- JsonValue.Create "passed"
                record["Inputs"] <- node
                match Json.parse EvidenceReuseWire.provenanceRecord (record.ToJsonString()) with
                | Ok r -> Some(t.GetProperty("TestId").GetString(), r.Inputs.Value)
                | Error e -> failwithf "inputs: %A" e)
        |> Seq.toList
    let tests = Collections.Generic.List<obj>()
    let priors = Collections.Generic.List<JsonNode>()
    let expected = Collections.Generic.List<string * string * bool>()
    let record id test outcome freshness revisionAt (inputs: IdentityInputs) claimed =
        let r =
            { EvidenceId = id; TestId = test; Outcome = outcome; ProducedAtRevision = Some revisionAt; Freshness = Some freshness
              EvidenceIdentity = Some(defaultArg claimed (identity inputs)); Inputs = Some inputs }
        JsonSerializer.SerializeToNode(EvidenceReuseWire.recordWire r)
    for unitId, inputs in current do
        let u = units |> List.find (fun u -> u.Id = unitId)
        let mapping = mappingOf wc toolVersion u
        let case name freshness category (records: JsonNode list) shouldReuse =
            let id = unitId + "~" + name
            tests.Add {| TestId = id; Freshness = freshness; Category = category; Inputs = mapping |}
            for r in records do
                r["TestId"] <- JsonValue.Create id
                r["EvidenceId"] <- JsonValue.Create(id + "#" + string priors.Count)
                priors.Add r
            expected.Add(id, name, shouldReuse)
        let pass () = record "" "" OutcomePassed ContentAddressed previous inputs None
        case "control" "content-addressed" "functional" [ pass () ] true
        case "tampered-identity" "content-addressed" "functional" [ record "" "" OutcomePassed ContentAddressed previous inputs (Some(identity { inputs with ToolVersion = inputs.ToolVersion + "-forged" })) ] false
        case "other-tool-version" "content-addressed" "functional" [ record "" "" OutcomePassed ContentAddressed previous { inputs with ToolVersion = inputs.ToolVersion + "-other" } None ] false
        case "other-runtime" "content-addressed" "functional" [ record "" "" OutcomePassed ContentAddressed previous { inputs with Configuration = Map.add "runtime.rid" "osx-arm64" inputs.Configuration } None ] false
        case "changed-source" "content-addressed" "functional"
            [ record "" "" OutcomePassed ContentAddressed previous { inputs with Sources = inputs.Sources |> Map.map (fun _ _ -> "sha256:" + String('0', 64)) } None ] false
        case "live" "live" "functional" [ record "" "" OutcomePassed Live previous inputs None ] false
        case "security" "content-addressed" "security" [ pass () ] false
        case "independent-acceptance" "content-addressed" "independent-acceptance" [ pass () ] false
        case "per-commit-other-revision" "per-commit" "functional" [ record "" "" OutcomePassed PerCommit previous inputs None ] false
        case "conflicting-failure" "content-addressed" "functional" [ pass (); record "" "" OutcomeFailed ContentAddressed previous inputs None ] false
        case "failed-only" "content-addressed" "functional" [ record "" "" OutcomeFailed ContentAddressed previous inputs None ] false
        let incomplete = pass () :?> JsonObject
        incomplete.Remove "Inputs" |> ignore
        case "incomplete-provenance" "content-addressed" "functional" [ incomplete ] false
    let file = Path.Combine(Path.GetTempPath(), $"battery-{Guid.NewGuid():N}.json")
    File.WriteAllText(file, requestJson revision None (List.ofSeq tests) (List.ofSeq priors))
    let r = exec wc "dotnet" [ tool; "evidence"; "plan"; "--request"; file; "--root"; wc ]
    File.Delete file
    if r.Exit <> 0 then failwithf "battery plan failed: %s" r.Err
    use doc = JsonDocument.Parse r.Out
    let decided =
        doc.RootElement.GetProperty("Tests").EnumerateArray()
        |> Seq.map (fun t -> t.GetProperty("TestId").GetString(), t.GetProperty("Decision").GetString())
        |> Map.ofSeq
    expected
    |> Seq.map (fun (id, name, shouldReuse) -> {| Case = name; Test = id; ShouldReuse = shouldReuse; Reused = (decided[id] = "reuse") |})
    |> Seq.toList

let run () =
    let work = Path.GetFullPath(req "work")
    let tool = Path.GetFullPath(req "tool")
    let sequence = req "sequence"
    let strategy = req "strategy"
    let steps = readSteps work sequence |> fun all -> match opt "max-steps" with Some n -> List.truncate (int n) all | None -> all
    let label = sequence + "-" + strategy
    let wc = Path.Combine(work, "wc", label)
    let store = Path.Combine(work, "store", label)
    let results = Path.Combine(work, "results")
    let trxRoot = Path.Combine(work, "trx", label)
    for d in [ wc; store; trxRoot ] do
        if Directory.Exists d then Directory.Delete(d, true)
    Directory.CreateDirectory results |> ignore
    gitOut work [ "clone"; "-q"; "--no-local"; Path.Combine(work, "source"); wc ] |> ignore
    // The identity test compares ros.json against the origin remote.
    gitOut wc [ "remote"; "set-url"; "origin"; "https://github.com/kemiller2002/dokimos" ] |> ignore
    if strategy = "reuse" then
        let init = exec work "dotnet" [ tool; "store"; "init"; "--store"; store ]
        if init.Exit <> 0 then failwithf "store init: %s" init.Err
    let toolVersion = "dotnet-sdk " + (exec wc "dotnet" [ "--version" ]).Out.Trim()
    let output = Collections.Generic.List<obj>()
    let mutable previous: string option = None
    let finalRelease = args |> Array.contains "--final-release"
    let lastStep = (List.last steps).Step
    for s in steps do
        let release = finalRelease && s.Step = lastStep
        if strategy = "full" && sequence = "defects" && Array.isEmpty s.Defects then
            // Outside the defect windows the tree is the clean sequence's commit;
            // full verification of it is measured in the clean replay.
            output.Add {| Step = s.Step; Sha = s.Sha; Skipped = "identical to clean sequence" |}
        else
            let checkout = exec wc "git" [ "checkout"; "-q"; "--force"; "--detach"; s.Sha ]
            if checkout.Exit <> 0 then failwithf "checkout %s: %s" s.Sha checkout.Err
            let changed =
                match previous with
                | None -> None
                | Some p -> Some((gitOut wc [ "diff"; "--name-only"; p; s.Sha ]).Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList)
            let tests = units |> List.map (fun u -> box {| TestId = u.Id; Freshness = "content-addressed"; Inputs = mappingOf wc toolVersion u |})
            let planFile = Path.Combine(trxRoot, $"step-{s.Step:D2}-plan.json")
            Directory.CreateDirectory trxRoot |> ignore
            let requestFile = Path.Combine(trxRoot, $"step-{s.Step:D2}-request.json")
            let planChanged = if strategy = "full" then None else changed
            File.WriteAllText(requestFile, requestJson s.Sha planChanged tests [])
            let planArgs =
                [ tool; "evidence"; "plan"; "--request"; requestFile; "--root"; wc ]
                @ (if strategy = "reuse" then [ "--store"; store ] else [])
                @ (if release && strategy <> "full" then [ "--checkpoint"; "release" ] else [])
            let plan = exec wc "dotnet" planArgs
            if plan.Exit <> 0 then failwithf "plan at step %d: %s" s.Step plan.Err
            File.WriteAllText(planFile, plan.Out)
            use planDoc = JsonDocument.Parse plan.Out
            let decisions =
                planDoc.RootElement.GetProperty("Tests").EnumerateArray()
                |> Seq.map (fun t ->
                    t.GetProperty("TestId").GetString(),
                    {| Decision = t.GetProperty("Decision").GetString()
                       Identity = t.GetProperty("CurrentIdentity").GetString()
                       PathAffected = (match t.GetProperty("PathAffected").ValueKind with JsonValueKind.True -> Some true | JsonValueKind.False -> Some false | _ -> None)
                       Reasons = t.GetProperty("Reasons").EnumerateArray() |> Seq.map (fun r -> r.GetProperty("Code").GetString()) |> Seq.distinct |> Seq.toList
                       Reused = (match t.GetProperty("Reused").ValueKind with JsonValueKind.Object -> t.GetProperty("Reused").GetProperty("EvidenceId").GetString() | _ -> null) |})
                |> Map.ofSeq
            let unmapped = planDoc.RootElement.GetProperty("UnmappedChangedPaths").GetArrayLength()
            let selected =
                units
                |> List.filter (fun u ->
                    match strategy with
                    | "full" -> true
                    | "select" -> release || changed.IsNone || unmapped > 0 || decisions[u.Id].PathAffected = Some true
                    | _ -> decisions[u.Id].Decision = "run")
            let runs =
                selected
                |> List.map (fun u ->
                    let trx = $"step-{s.Step:D2}-{u.Id}.trx"
                    let r = exec wc "dotnet" [ "test"; u.Project; "--logger"; "trx;LogFileName=" + trx; "--results-directory"; trxRoot ]
                    let total, failed = trxCounts (Path.Combine(trxRoot, trx))
                    let passed = r.Exit = 0 && failed = 0 && total > 0
                    {| Test = u.Id; Identity = decisions[u.Id].Identity; Exit = r.Exit; Total = total; Failed = failed; Passed = passed; Seconds = r.Seconds |})
            let records =
                if strategy <> "reuse" then []
                else
                    runs
                    |> List.map (fun r ->
                        let id = $"EV-{r.Test}-{s.Step:D2}-{s.Sha.Substring(0, 12)}"
                        let x = exec wc "dotnet" [ tool; "evidence"; "record"; "--store"; store; "--plan"; planFile; "--test"; r.Test
                                                   "--outcome"; (if r.Passed then "passed" else "failed"); "--evidence-id"; id ]
                        if x.Exit <> 0 then failwithf "record %s: %s" id x.Err
                        x.Seconds)
            let attempts =
                if strategy = "reuse" then battery wc tool s.Sha (defaultArg previous (String('0', 40))) planDoc toolVersion else []
            let strategySeconds =
                checkout.Seconds + (if strategy = "full" then 0.0 else plan.Seconds) + (runs |> List.sumBy _.Seconds) + List.sum records
            output.Add
                {| Step = s.Step; Sha = s.Sha; Original = s.Original; Defects = s.Defects; ChangedPaths = changed; Release = release
                   UnmappedChangedPaths = unmapped; Decisions = decisions; Runs = runs
                   CheckoutSeconds = checkout.Seconds; PlanSeconds = plan.Seconds; RecordSeconds = List.sum records
                   StrategySeconds = strategySeconds; Battery = attempts |}
            printfn "%s step %d %s: ran %s (%.1fs)" label s.Step (s.Sha.Substring(0, 8)) (String.Join(",", runs |> List.map (fun r -> $"{r.Test}:{r.Total}/{r.Failed}"))) strategySeconds
            previous <- Some s.Sha
            File.WriteAllText(Path.Combine(results, label + ".json"), JsonSerializer.Serialize(output, JsonSerializerOptions(WriteIndented = true)))

// ---- summarize --------------------------------------------------------------------------------------------------

let summarize () =
    let work = Path.GetFullPath(req "work")
    let load label =
        let path = Path.Combine(work, "results", label + ".json")
        if File.Exists path then Some(JsonDocument.Parse(File.ReadAllText path).RootElement.Clone()) else None
    let stepsOf (e: JsonElement) = e.EnumerateArray() |> Seq.filter (fun s -> not (s.TryGetProperty("Skipped") |> fst)) |> Seq.toList
    let runsOf (s: JsonElement) = s.GetProperty("Runs").EnumerateArray() |> Seq.toList
    // Ground truth by commit: the full strategy ran every unit there.
    let truth =
        [ "clean-full"; "defects-full" ]
        |> List.choose load
        |> List.collect stepsOf
        |> List.collect (fun s -> runsOf s |> List.map (fun r -> (s.GetProperty("Sha").GetString(), r.GetProperty("Test").GetString()), r.GetProperty("Passed").GetBoolean()))
        |> Map.ofList
    let metrics label =
        load label
        |> Option.map (fun e ->
            let steps = stepsOf e
            let runs = steps |> List.collect runsOf
            let decisions = steps |> List.collect (fun s -> s.GetProperty("Decisions").EnumerateObject() |> Seq.map (fun d -> s, d.Name, d.Value) |> Seq.toList)
            let reused = decisions |> List.filter (fun (_, _, d) -> d.GetProperty("Decision").GetString() = "reuse")
            // A repeated run executes a unit at an identity this strategy already saw pass.
            let repeated =
                steps
                |> List.fold
                    (fun (seen: Set<string * string>, projects, cases) s ->
                        runsOf s
                        |> List.fold
                            (fun (seen: Set<string * string>, projects, cases) r ->
                                let key = r.GetProperty("Test").GetString(), r.GetProperty("Identity").GetString()
                                let isRepeat = seen.Contains key
                                let seen = if r.GetProperty("Passed").GetBoolean() then seen.Add key else seen
                                seen, projects + (if isRepeat then 1 else 0), cases + (if isRepeat then r.GetProperty("Total").GetInt32() else 0))
                            (seen, projects, cases))
                    (Set.empty, 0, 0)
                |> fun (_, p, c) -> p, c
            let wrongReuse =
                reused
                |> List.filter (fun (s, test, _) ->
                    match Map.tryFind (s.GetProperty("Sha").GetString(), test) truth with
                    | Some passed -> not passed
                    | None -> false)
            let unverifiedReuse =
                reused |> List.filter (fun (s, test, _) -> not (truth.ContainsKey((s.GetProperty("Sha").GetString(), test))))
            let battery = steps |> List.collect (fun s -> s.GetProperty("Battery").EnumerateArray() |> Seq.toList)
            let incorrectAttempts = battery |> List.filter (fun b -> not (b.GetProperty("ShouldReuse").GetBoolean()))
            let controls = battery |> List.filter (fun b -> b.GetProperty("ShouldReuse").GetBoolean())
            let defectSteps = steps |> List.filter (fun s -> s.GetProperty("Defects").GetArrayLength() > 0)
            let detected =
                defectSteps
                |> List.filter (fun s -> runsOf s |> List.exists (fun r -> not (r.GetProperty("Passed").GetBoolean())))
            let byDefect =
                defectSteps
                |> List.collect (fun s -> s.GetProperty("Defects").EnumerateArray() |> Seq.map (fun d -> d.GetString(), s) |> Seq.toList)
                |> List.groupBy fst
                |> List.map (fun (d, ss) ->
                    let stepNo (s: JsonElement) = s.GetProperty("Step").GetInt32()
                    let failed (s: JsonElement) = runsOf s |> List.exists (fun r -> not (r.GetProperty("Passed").GetBoolean()))
                    let ordered = ss |> List.map snd |> List.sortBy stepNo
                    let first = ordered |> List.tryFind failed
                    // Strategy seconds from the start of the introducing step to the end of the first step that reports it.
                    let secondsToDetect =
                        first
                        |> Option.map (fun f ->
                            steps
                            |> List.filter (fun s -> stepNo s >= stepNo ordered.Head && stepNo s <= stepNo f)
                            |> List.sumBy (fun s -> s.GetProperty("StrategySeconds").GetDouble()))
                    {| Defect = d; Steps = ordered |> List.map stepNo
                       StepsWithFailureReported = ordered |> List.filter failed |> List.map stepNo
                       DetectedAtIntroduction = failed ordered.Head
                       FirstDetectedStep = first |> Option.map stepNo |> Option.toNullable
                       SecondsToDetect = secondsToDetect |> Option.toNullable
                       Escaped = first.IsNone |})
            {| Label = label
               Steps = steps.Length
               ProjectRuns = runs.Length
               TestsExecuted = runs |> List.sumBy (fun r -> r.GetProperty("Total").GetInt32())
               RepeatedProjectRuns = fst repeated
               RepeatedTests = snd repeated
               WallClockSeconds = steps |> List.sumBy (fun s -> s.GetProperty("StrategySeconds").GetDouble())
               TestSeconds = runs |> List.sumBy (fun r -> r.GetProperty("Seconds").GetDouble())
               PlanSeconds = steps |> List.sumBy (fun s -> s.GetProperty("PlanSeconds").GetDouble())
               RecordSeconds = steps |> List.sumBy (fun s -> s.GetProperty("RecordSeconds").GetDouble())
               UnitDecisions = decisions.Length
               ReuseDecisions = reused.Length
               CacheHitRate = (if label.EndsWith "reuse" && decisions.Length > 0 then Nullable(float reused.Length / float decisions.Length) else Nullable())
               ReusesContradictedByFullRun = wrongReuse.Length
               ReusesWithoutFullRunAtSameCommit = unverifiedReuse.Length
               IncorrectReuseAttempts = incorrectAttempts.Length
               IncorrectReuseAccepted = incorrectAttempts |> List.filter (fun b -> b.GetProperty("Reused").GetBoolean()) |> List.length
               IncorrectReuseAcceptedByCase =
                incorrectAttempts |> List.groupBy (fun b -> b.GetProperty("Case").GetString())
                |> List.map (fun (c, bs) -> c, {| Attempts = bs.Length; Accepted = bs |> List.filter (fun b -> b.GetProperty("Reused").GetBoolean()) |> List.length |})
                |> Map.ofList
               PositiveControls = controls.Length
               PositiveControlsReused = controls |> List.filter (fun b -> b.GetProperty("Reused").GetBoolean()) |> List.length
               DefectSteps = defectSteps.Length
               DefectStepsWithFailureReported = detected.Length
               DefectStepsMasked = defectSteps.Length - detected.Length
               ReleaseSteps =
                steps
                |> List.filter (fun s -> match s.TryGetProperty "Release" with | true, v -> v.GetBoolean() | _ -> false)
                |> List.map (fun s -> {| Step = s.GetProperty("Step").GetInt32(); ProjectRuns = (runsOf s).Length
                                         Tests = runsOf s |> List.sumBy (fun r -> r.GetProperty("Total").GetInt32())
                                         Seconds = s.GetProperty("StrategySeconds").GetDouble() |})
               Defects = byDefect |})
    let summary =
        {| Contract = "dokimos.experiment.incremental-verification"
           Strategies = [ "clean-full"; "clean-select"; "clean-reuse"; "defects-full"; "defects-select"; "defects-reuse" ] |> List.choose metrics
           TokenConsumption = "unavailable: the replay runs no model; agent token use was not measured" |}
    let json = JsonSerializer.Serialize(summary, JsonSerializerOptions(WriteIndented = true))
    File.WriteAllText(req "out", json)
    printfn "%s" json

match args |> Array.tryHead with
| Some "prepare" -> prepare ()
| Some "run" -> run ()
| Some "summarize" -> summarize ()
| _ -> eprintfn "usage: dotnet fsi replay.fsx prepare|run|summarize ..."; exit 2
