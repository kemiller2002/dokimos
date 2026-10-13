/// CONSTRUCT-010 phase 4 through `dokimos construction verify`, on candidates
/// built from the timer cohort as ordo#78 generates it (vocabulary 3). The
/// generated files are Ordo's own output; the prepared artifact, dispositions
/// and approvals are synthetic wrappers (tests/fixtures/construction/README.md).
/// Every check reads the candidate from Git objects only.
module Dokimos.Cli.Tests.DecisionCompletenessCliTests

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Dokimos.Cli.Tests.Support

let private timer relative = repoPath (Path.Combine("tests", "fixtures", "construction", "time-entry-timer", relative))
let private v3 relative = repoPath (Path.Combine("tests", "fixtures", "construction", "time-entry-timer-v3", relative))

/// The two vocabulary-3 variants: unlisted pairs refused (decisions v2 as
/// ordo#78 ships it) and unlisted pairs undecided.
type private Variant =
    { Output: string
      Suffix: string
      Decisions: string
      PlanDigest: string }

let private refused =
    { Output = "ordo-output"; Suffix = ""; Decisions = v3 "ordo-golden/construction-decisions-v2.json"
      PlanDigest = "sha256:c22c92183f36162eb7be93dfbbf0e55f4d9dc47c2cf7b7b9e802bf30bed2371a" }

let private undecided =
    { Output = "ordo-output-open"; Suffix = "-open"; Decisions = v3 "synthetic/construction-decisions-v2-open.json"
      PlanDigest = "sha256:59659d5e57d95f378482143c8094c34f90ab7e2f240e15ba2b2dafad2068509f" }

let private git (repo: string) (args: string list) =
    let info = Diagnostics.ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    "-C" :: repo :: args |> List.iter info.ArgumentList.Add
    use proc =
        match Diagnostics.Process.Start info with
        | null -> failwith "git did not start"
        | p -> p
    let out = proc.StandardOutput.ReadToEnd()
    proc.WaitForExit()
    if proc.ExitCode <> 0 then failwithf "git %A failed: %s" args (proc.StandardError.ReadToEnd())
    out.Trim()

let private commit repo message =
    git repo [ "add"; "-A" ] |> ignore
    git repo [ "commit"; "-q"; "-m"; message ] |> ignore
    git repo [ "rev-parse"; "HEAD" ]

let private copyGenerated (variant: Variant) (repo: string) =
    let source = v3 (Path.Combine(variant.Output, "generated", "Timer"))
    let target = Path.Combine(repo, "generated", "Timer")
    Directory.CreateDirectory target |> ignore
    for file in Directory.EnumerateFiles source do
        File.Copy(file, Path.Combine(target, Path.GetFileName file |> string), true)

type private Candidate = { Repo: string; Base: string; Apply: string; Head: string; Replica: string }

/// base (a placeholder port file) -> apply (Ordo's generated files) ->
/// handwritten (the CurrentTime port), plus a replica apply from the same base.
let private candidate (variant: Variant) (handwritten: string -> unit) =
    let repo = Path.Combine(tempDirectory (), "candidate")
    Directory.CreateDirectory(Path.Combine(repo, "app")) |> ignore
    git repo [ "init"; "-q" ] |> ignore
    git repo [ "config"; "user.email"; "verifier@example.invalid" ] |> ignore
    git repo [ "config"; "user.name"; "verifier" ] |> ignore
    File.WriteAllText(Path.Combine(repo, "app", "TimerPorts.fs"), "module App.TimerPorts\n")
    let baseCommit = commit repo "base"
    copyGenerated variant repo
    let apply = commit repo "apply"
    File.WriteAllText(Path.Combine(repo, "app", "TimerPorts.fs"), "module App.TimerPorts\nlet currentTime () = System.DateTimeOffset.UtcNow\n")
    handwritten repo
    let head = commit repo "handwritten"
    git repo [ "checkout"; "-q"; "-b"; "replica"; baseCommit ] |> ignore
    copyGenerated variant repo
    let replica = commit repo "replica apply"
    git repo [ "checkout"; "-q"; head ] |> ignore
    { Repo = repo; Base = baseCommit; Apply = apply; Head = head; Replica = replica }

let private args (variant: Variant) (c: Candidate) (overrides: Map<string, string>) =
    let value name fallback = Map.tryFind name overrides |> Option.defaultValue fallback
    [ "construction"; "verify"
      "--source-manifest"; timer "source-manifest.json"
      "--sources"; timer "sources"
      "--blueprint"; timer "blueprint.json"
      "--expected-plan-digest"; variant.PlanDigest
      "--expected-generator"; "ordo.construction/3"
      "--repository"; c.Repo
      "--commit"; c.Head
      "--base"; c.Base
      "--generated-commit"; c.Apply
      "--replica-commit"; c.Replica
      "--manifest-path"; "generated/Timer/construction-manifest.json"
      "--prepared"; value "prepared" (v3 $"synthetic/prepared{variant.Suffix}.json")
      "--dispositions"; value "dispositions" (v3 $"synthetic/dispositions{variant.Suffix}.json")
      "--decisions"; variant.Decisions
      "--approvals"; value "approvals" (v3 $"synthetic/approvals{variant.Suffix}.json")
      "--work-item"; "WI-0008" ]
    @ (match Map.tryFind "ledger" overrides with
       | Some "" -> []
       | Some path -> [ "--ledger"; path ]
       | None -> [ "--ledger"; v3 $"{variant.Output}/ledger.json" ])

let private parse (output: Dokimos.Cli.Output) =
    match output.Stdout with
    | Some json ->
        assertSchemaValid "dokimos-construction-verification.schema.json" json
        JsonDocument.Parse(json).RootElement
    | None -> failwithf "no report; stderr: %A" output.Stderr

let private findings (report: JsonElement) =
    [ for f in report.GetProperty("Findings").EnumerateArray() -> f.GetProperty("Code").Text, f.GetProperty("Subject").Text ]

let private check (report: JsonElement) (name: string) =
    (report.GetProperty("MechanicalFirst")[0]).GetProperty("Checks").EnumerateArray()
    |> Seq.find (fun c -> c.GetProperty("Name").Text = name)
    |> fun c -> c.GetProperty("Status").Text

let private decisionChecks =
    [ "decision-completeness"; "requirement-disposition"; "closed-world"; "unsupported-operations"; "unresolved-obligations"; "failure-semantics" ]

let private openItems (report: JsonElement) =
    [ for i in (report.GetProperty("MechanicalFirst")[0]).GetProperty("Decisions").GetProperty("OpenItems").EnumerateArray() ->
        i.GetProperty("Subject").Text ]

let private variantFile (path: string) (edit: JsonNode -> unit) =
    let node =
        match JsonNode.Parse(File.ReadAllText path) with
        | null -> failwith "JSON null"
        | n -> n
    edit node
    write (tempDirectory ()) (Path.GetFileName path |> string) (node.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let private hasFinding code subject report =
    Assert.True(findings report |> List.contains (code, subject), sprintf "expected %s on %s; got %A" code subject (findings report))

[<Fact>]
let ``the timer cohort as classified by ordo#78 is intentionally incomplete with typed open items, never verified`` () =
    let c = candidate refused ignore
    let output = run (args refused c Map.empty)
    let report = parse output
    Assert.Empty(findings report)
    Assert.Equal(3, output.ExitCode)
    Assert.Equal("incomplete", report.GetProperty("Verdict").Text)
    for name in decisionChecks do
        Assert.Equal("passed", check report name)
    let decisions = (report.GetProperty("MechanicalFirst")[0]).GetProperty("Decisions")
    Assert.Equal("incomplete", decisions.GetProperty("Completeness").Text)
    Assert.Equal<string list>(
        [ "effectFailure:Stop"; "extension:effect/Stop/point:RoundingTie"; "unresolved:UncoveredRequirementFfc2989b"
          "obligation:OBL-COH-TIMER-conflict-f569e9ac9d77" ],
        openItems report)
    let reasons = [ for r in report.GetProperty("Reasons").EnumerateArray() -> r.Text ]
    Assert.Contains(reasons, fun r -> r.StartsWith("Intentionally incomplete: 4 open decision item(s)", StringComparison.Ordinal))

[<Fact>]
let ``undecided pairs stay open items with their obligations`` () =
    let c = candidate undecided ignore
    let report = parse (run (args undecided c Map.empty))
    Assert.Empty(findings report)
    Assert.Equal("passed", check report "closed-world")
    Assert.Equal(11, (openItems report).Length)
    Assert.Equal(7, openItems report |> List.filter (fun s -> s.StartsWith("pair:", StringComparison.Ordinal)) |> List.length)

[<Fact>]
let ``an unobserved decision check keeps an otherwise clean candidate incomplete`` () =
    let c = candidate refused ignore
    let report = parse (run (args refused c (Map.ofList [ "ledger", "" ])))
    Assert.Empty(findings report)
    Assert.Equal("incomplete", report.GetProperty("Verdict").Text)
    Assert.Equal("unknown", check report "requirement-disposition")
    Assert.Contains(report.GetProperty("Reasons").EnumerateArray(), fun r -> r.Text.Contains "requirement-disposition is unknown")

[<Fact>]
let ``generated code that refuses an undecided pair is refused`` () =
    let mutate repo =
        let path = Path.Combine(repo, "generated", "Timer", "Timer.Transition.g.fs")
        File.WriteAllText(path, File.ReadAllText(path).Replace("| TimerState.Paused, TimerCommand.Start -> true", "| TimerState.Paused, TimerCommand.Start -> false"))
    let c = candidate undecided mutate
    let report = parse (run (args undecided c Map.empty))
    Assert.Equal("rejected", report.GetProperty("Verdict").Text)
    hasFinding "undecided-pair-refused" "pair:Paused/Start" report
    Assert.Equal("failed", check report "closed-world")

[<Fact>]
let ``a decision the operator did not approve is not a citation`` () =
    let c = candidate refused ignore
    let approvals = variantFile (v3 "synthetic/approvals.json") (fun n -> (n["decisions"] |> Option.ofObj |> Option.get).AsArray().Clear())
    let report = parse (run (args refused c (Map.ofList [ "approvals", approvals ])))
    hasFinding "unapproved-decision-cited" "state:Running" report
    hasFinding "unapproved-decision-cited" "pair:Running/Stop" report
    Assert.Equal("failed", check report "decision-completeness")

[<Fact>]
let ``an open decision point claimed implemented is refused`` () =
    let c = candidate refused ignore
    let dispositions =
        variantFile (v3 "synthetic/dispositions.json") (fun n ->
            for d in (n["dispositions"] |> Option.ofObj |> Option.get).AsArray() |> Seq.choose Option.ofObj do
                let id = (d["obligationId"] |> Option.ofObj |> Option.get).GetValue<string>()
                if id.Contains "undecidedExtensionDecision" then
                    d["disposition"] <- JsonValue.Create "implemented"
                    d["files"] <- JsonNode.Parse """["app/TimerPorts.fs"]""")
    let report = parse (run (args refused c (Map.ofList [ "dispositions", dispositions ])))
    hasFinding "unresolved-obligation-claimed-implemented" "OBL-COH-TIMER-undecidedExtensionDecision-73e53dd97e12" report
    // Claiming it resolved needs an operator-approved decision the plan did not apply.
    let resolved =
        variantFile (v3 "synthetic/dispositions.json") (fun n ->
            for d in (n["dispositions"] |> Option.ofObj |> Option.get).AsArray() |> Seq.choose Option.ofObj do
                if (d["obligationId"] |> Option.ofObj |> Option.get).GetValue<string>().Contains "undecidedExtensionDecision" then
                    d["disposition"] <- JsonValue.Create "resolved"
                    d["decisionId"] <- JsonValue.Create "DEC-TIMER-STATES")
    let report = parse (run (args refused c (Map.ofList [ "dispositions", resolved ])))
    hasFinding "unapproved-resolution" "OBL-COH-TIMER-undecidedExtensionDecision-73e53dd97e12" report

[<Fact>]
let ``a dropped requirement and a duplicated ledger row are refused`` () =
    let c = candidate refused ignore
    let mutable key = ""
    let dropped =
        variantFile (v3 "ordo-output/ledger.json") (fun n ->
            let rows = (n["rows"] |> Option.ofObj |> Option.get).AsArray()
            key <- ((rows[0] |> Option.ofObj |> Option.get)["requirement"] |> Option.ofObj |> Option.get)["key"] |> Option.ofObj |> Option.get |> _.GetValue<string>()
            rows.RemoveAt 0)
    let report = parse (run (args refused c (Map.ofList [ "ledger", dropped ])))
    hasFinding "requirement-dropped" key report
    Assert.Equal("failed", check report "requirement-disposition")
    let duplicated =
        variantFile (v3 "ordo-output/ledger.json") (fun n ->
            let rows = (n["rows"] |> Option.ofObj |> Option.get).AsArray()
            rows.Add(JsonNode.Parse((rows[1] |> Option.ofObj |> Option.get).ToJsonString())))
    let report = parse (run (args refused c (Map.ofList [ "ledger", duplicated ])))
    Assert.Contains(findings report, fun (code, _) -> code = "duplicate-ledger-row")

[<Fact>]
let ``decision-only options without --prepared are refused`` () =
    let bArm =
        [ "construction"; "verify"; "--source-manifest"; timer "source-manifest.json"; "--blueprint"; timer "blueprint.json"
          "--plan"; timer "plan.json"; "--expected-plan-digest"; refused.PlanDigest; "--expected-generator"; "ordo.construction/3"
          "--commit"; "HEAD"; "--manifest-path"; "generated/Timer/construction-manifest.json" ]
    Assert.Equal(2, (run (bArm @ [ "--ledger"; v3 "ordo-output/ledger.json" ])).ExitCode)
    Assert.Equal(2, (run (bArm @ [ "--approvals"; v3 "synthetic/approvals.json" ])).ExitCode)
