/// CONSTRUCT-008 mechanical-first checks of `dokimos construction verify`,
/// run against the real Git objects of pilot-01 run B1 (a Praxis host apply
/// commit followed by a handwritten commit). The prepared artifact,
/// decision records and dispositions are synthetic fixtures; every check
/// still reads the candidate from Git objects only.
module Dokimos.Cli.Tests.MechanicalFirstCliTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Dokimos.Cli.Tests.Support

let private b1 relative = repoPath (Path.Combine("tests", "fixtures", "construction", "pilot-01-b1", relative))
let private timer relative = repoPath (Path.Combine("tests", "fixtures", "construction", "time-entry-timer", relative))
let private oracle = repoPath (Path.Combine("oracles", "construction", "time-entry-timer", "oracle.json"))
let private sha (bytes: byte[]) = "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()
let private planDigest = "sha256:1993df015678980493b7e9c7e8a602205c75bf2eb40c4aaa8ac4541f4afbcfa8"
let private manifestPath = "pilot/Timer/generated/Timer/construction-manifest.json"

// Commits recorded in the pilot-01 B1 bundle.
let private skeleton = "4f1e14cb4e0273b53c178511c822c4f209f92cec"
let private applyCommit = "f7f477f0081212cf81f48b28dbb9c970ad2effdf"
let private candidateCommit = "35ead6f26da6f3c0473bafa3823a7b348b4a9629"
/// An earlier `try` apply of the same plan by the same host: an independent replica.
let private tryApply = "0cbcdd6cbf659c030c1f74cae4e64337b1abdf57"

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

/// A fresh clone of the B1 bundle with the candidate checked out.
let private clone () =
    let repo = Path.Combine(tempDirectory (), "b1")
    git (Path.GetTempPath()) [ "clone"; "-q"; "--no-checkout"; b1 "candidate.bundle"; repo ] |> ignore
    git repo [ "fetch"; "-q"; b1 "candidate.bundle"; "+refs/heads/*:refs/remotes/bundle/*" ] |> ignore
    git repo [ "checkout"; "-q"; "-B"; "work"; candidateCommit ] |> ignore
    git repo [ "config"; "user.email"; "verifier@example.invalid" ] |> ignore
    git repo [ "config"; "user.name"; "verifier" ] |> ignore
    repo

/// Commits `edit` on top of the given revision and returns the new commit.
let private commitOn (repo: string) (revision: string) (message: string) (edit: string -> unit) =
    git repo [ "checkout"; "-q"; "-B"; "work"; revision ] |> ignore
    edit repo
    git repo [ "add"; "-A" ] |> ignore
    git repo [ "commit"; "-q"; "-m"; message ] |> ignore
    git repo [ "rev-parse"; "HEAD" ]

let private appendTo (relative: string) (text: string) (repo: string) =
    let path = Path.Combine(repo, relative)
    File.WriteAllText(path, File.ReadAllText path + text)

/// Writes a variant of a JSON fixture to a temporary file.
let private variant (fixture: string) (edit: JsonNode -> unit) =
    let node =
        match JsonNode.Parse(File.ReadAllText fixture) with
        | null -> failwith "fixture is JSON null"
        | n -> n
    edit node
    write (tempDirectory ()) (Path.GetFileName fixture |> string) (node.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let private obj (node: JsonNode | null) (name: string) : JsonNode =
    match node with
    | null -> failwith "JSON null"
    | parent ->
        match parent[name] with
        | null -> failwithf "no %s" name
        | n -> n

let private obligationIds () =
    let prepared = JsonNode.Parse(File.ReadAllText(b1 "prepared.json"))
    [ for o in (obj (obj (obj prepared "defaultPlan") "obligations") "obligations").AsArray() -> (obj o "id").GetValue<string>() ]

let private args repo (extra: string list) (overrides: Map<string, string>) =
    let value name fallback = Map.tryFind name overrides |> Option.defaultValue fallback
    [ "construction"; "verify"
      "--source-manifest"; timer "source-manifest.json"
      "--sources"; timer "sources"
      "--blueprint"; timer "blueprint.json"
      "--expected-plan-digest"; planDigest
      "--expected-generator"; "ordo.construction/1"
      "--repository"; repo
      "--commit"; value "commit" candidateCommit
      "--base"; skeleton
      "--manifest-path"; manifestPath
      "--oracle"; oracle
      "--expected-oracle-digest"; sha (File.ReadAllBytes oracle)
      "--prepared"; value "prepared" (b1 "prepared.json")
      "--dispositions"; value "dispositions" (b1 "dispositions.json")
      "--decisions"; value "decisions" (b1 "decisions.json")
      "--generated-commit"; value "generated-commit" applyCommit
      "--replica-commit"; value "replica-commit" tryApply
      "--host-observations"; value "host-observations" (b1 "host-observations.json")
      "--work-item"; "WI-0007" ]
    @ extra

let private parse (output: Dokimos.Cli.Output) =
    match output.Stdout with
    | Some json ->
        assertSchemaValid "dokimos-construction-verification.schema.json" json
        JsonDocument.Parse(json).RootElement
    | None -> failwithf "no report; stderr: %A" output.Stderr

let private findings (report: JsonElement) =
    [ for f in report.GetProperty("Findings").EnumerateArray() -> f.GetProperty("Code").Text, f.GetProperty("Subject").Text, f.GetProperty("Detail").Text ]

let private check (report: JsonElement) (name: string) =
    let block = report.GetProperty("MechanicalFirst")
    let checks = block[0].GetProperty("Checks")
    let found = checks.EnumerateArray() |> Seq.find (fun (c: JsonElement) -> c.GetProperty("Name").Text = name)
    found.GetProperty("Status").Text

/// Asserts a refusal: exit 4, the finding with that code and subject, and
/// the named check failed.
let private refused (output: Dokimos.Cli.Output) code subject checkName =
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    Assert.Equal("rejected", report.GetProperty("Verdict").Text)
    let hit = findings report |> List.tryFind (fun (c, s, _) -> c = code && s = subject)
    Assert.True(hit.IsSome, sprintf "expected %s on %s; got %A" code subject (findings report))
    Assert.Equal("failed", check report checkName)
    hit |> Option.map (fun (_, _, d) -> d) |> Option.defaultValue ""

[<Fact>]
let ``pilot-01 B1 with a prepared artifact passes every mechanical-first check and the frozen oracle`` () =
    let repo = clone ()
    let output = run (args repo [ "--oracle-runner"; "dotnet" ] Map.empty)
    let report = parse output
    Assert.Empty(findings report)
    Assert.Equal(0, output.ExitCode)
    Assert.Equal("verified", report.GetProperty("Verdict").Text)
    Assert.Equal("1.1.0", report.GetProperty("SchemaVersion").Text)
    let mechanical = report.GetProperty("MechanicalFirst")[0]
    for c in mechanical.GetProperty("Checks").EnumerateArray() do
        Assert.Equal("passed", c.GetProperty("Status").Text)
    Assert.Equal(8, mechanical.GetProperty("Checks").GetArrayLength())
    Assert.Equal(sha (File.ReadAllBytes(b1 "prepared.json")), mechanical.GetProperty("PreparedDigest").Text)
    Assert.Equal(tryApply, mechanical.GetProperty("ReplicaCommit").Text)
    let handwritten = [ for p in mechanical.GetProperty("HandwrittenPaths").EnumerateArray() -> p.Text ]
    Assert.Equal<string list>([ "pilot/Timer/Completion.fs"; "pilot/Timer/Extensions.fs"; "pilot/Timer/TimerOracleAdapter.fs" ], handwritten)
    Assert.Equal("passed", report.GetProperty("BehavioralAcceptance").GetProperty("Status").Text)

[<Fact>]
let ``an unobserved mechanical-first check keeps an otherwise clean candidate incomplete`` () =
    let repo = clone ()
    let output = run (args repo [ "--oracle-runner"; "dotnet" ] Map.empty |> List.filter (fun a -> a <> "--replica-commit" && a <> tryApply))
    let report = parse output
    Assert.Equal(3, output.ExitCode)
    Assert.Equal("incomplete", report.GetProperty("Verdict").Text)
    Assert.Equal("unknown", check report "deterministic-generation")
    let reasons = report.GetProperty("Reasons")
    Assert.Contains("deterministic-generation", reasons[0].Text)

[<Fact>]
let ``without --prepared the B-arm invocation is unchanged and mechanical-only options are refused`` () =
    let repo = clone ()
    let bArm =
        [ "construction"; "verify"; "--source-manifest"; timer "source-manifest.json"; "--blueprint"; timer "blueprint.json"
          "--plan"; b1 "plan.json"; "--expected-plan-digest"; planDigest; "--expected-generator"; "ordo.construction/1"
          "--repository"; repo; "--commit"; candidateCommit; "--manifest-path"; manifestPath ]
    let report = parse (run bArm)
    Assert.Empty(findings report)
    Assert.Equal(0, report.GetProperty("MechanicalFirst").GetArrayLength())
    Assert.Equal(2, (run (bArm @ [ "--dispositions"; b1 "dispositions.json" ])).ExitCode)
    Assert.Equal(2, (run (bArm |> List.filter (fun a -> a <> "--plan" && a <> b1 "plan.json"))).ExitCode)

[<Fact>]
let ``a handwritten edit to a generated file is refused`` () =
    let repo = clone ()
    let edited = commitOn repo candidateCommit "edit generated" (appendTo "pilot/Timer/generated/Timer/Timer.Model.g.fs" "\n// tweak\n")
    let detail = refused (run (args repo [] (Map.ofList [ "commit", edited ]))) "generated-file-edited" "pilot/Timer/generated/Timer/Timer.Model.g.fs" "extension-boundary"
    Assert.Contains("generated file", detail)
    Assert.Contains("generated-artifact-modified", findings (parse (run (args repo [] (Map.ofList [ "commit", edited ])))) |> List.map (fun (c, _, _) -> c))

[<Fact>]
let ``a write outside the permitted extension files is refused`` () =
    let repo = clone ()
    let extra = commitOn repo candidateCommit "extra file" (fun r -> File.WriteAllText(Path.Combine(r, "pilot", "Timer", "Extra.fs"), "module Extra\n"))
    let output = run (args repo [] (Map.ofList [ "commit", extra ]))
    let detail = refused output "extension-boundary-violation" "pilot/Timer/Extra.fs" "extension-boundary"
    Assert.Contains("outside the permitted extension files", detail)
    refused output "unauthorized-file-mutation" "pilot/Timer/Extra.fs" "file-mutations" |> ignore

[<Fact>]
let ``a handwritten package reference is refused`` () =
    let repo = clone ()
    let withPackage =
        commitOn repo candidateCommit "add package" (fun r ->
            let project = Path.Combine(r, "pilot", "Timer", "Timer.fsproj")
            File.WriteAllText(project, (File.ReadAllText project).Replace("</Project>", "  <ItemGroup><PackageReference Include=\"Left.Pad\" Version=\"1.0.0\" /></ItemGroup>\n</Project>")))
    let output = run (args repo [] (Map.ofList [ "commit", withPackage ]))
    let detail = refused output "extension-boundary-violation" "pilot/Timer/Timer.fsproj" "extension-boundary"
    Assert.Contains("project, build or package/dependency file", detail)
    Assert.Contains("unauthorized-dependency", findings (parse output) |> List.map (fun (c, _, _) -> c))
    Assert.Equal("failed", check (parse output) "architecture-compliance")

[<Fact>]
let ``a dropped obligation is refused`` () =
    let repo = clone ()
    let dropped = obligationIds () |> List.last
    let dispositions =
        variant (b1 "dispositions.json") (fun n ->
            let list = (obj n "dispositions").AsArray()
            list.RemoveAt(list.Count - 1))
    let detail = refused (run (args repo [] (Map.ofList [ "dispositions", dispositions ]))) "obligation-dropped" dropped "obligation-tracking"
    Assert.Contains("no recorded disposition", detail)
    // The handoff, too, may not drop one.
    let prepared = variant (b1 "prepared.json") (fun n -> (obj (obj n "handoff") "obligationIds").AsArray().RemoveAt(0))
    let first = obligationIds () |> List.head
    Assert.Contains("handoff omits", refused (run (args repo [] (Map.ofList [ "prepared", prepared ]))) "obligation-dropped" first "obligation-tracking")

[<Fact>]
let ``an invalid disposition is refused with a precise reason`` () =
    let repo = clone ()
    let dispositions =
        variant (b1 "dispositions.json") (fun n ->
            let list = (obj n "dispositions").AsArray()
            (obj list[0] "disposition").ReplaceWith(JsonValue.Create "done")
            (obj list[1] "note").ReplaceWith(JsonValue.Create ""))
    let output = run (args repo [] (Map.ofList [ "dispositions", dispositions ]))
    let ids = obligationIds ()
    Assert.Contains("'done' is not implemented or escalated", refused output "obligation-disposition-invalid" ids[0] "obligation-tracking")
    Assert.Contains("must record its unresolved question", refused output "obligation-disposition-invalid" ids[1] "obligation-tracking")

[<Fact>]
let ``mismatched provenance digests are refused`` () =
    let repo = clone ()
    let other = "sha256:" + String('a', 64)
    let declaredPlan = variant (b1 "prepared.json") (fun n -> (obj (obj n "defaultPlan") "planDigest").ReplaceWith(JsonValue.Create other))
    Assert.Contains(other, refused (run (args repo [] (Map.ofList [ "prepared", declaredPlan ]))) "provenance-digest-mismatch" "defaultPlan.planDigest" "plan-provenance")
    let blueprint =
        variant (b1 "prepared.json") (fun n ->
            (obj (obj (obj (obj n "defaultPlan") "provenance") "inputDigests") "blueprint").ReplaceWith(JsonValue.Create other))
    refused (run (args repo [] (Map.ofList [ "prepared", blueprint ]))) "provenance-digest-mismatch" "provenance.inputDigests.blueprint" "plan-provenance" |> ignore
    let changed = variant (b1 "decisions.json") (fun n -> (obj n "cohortId").ReplaceWith(JsonValue.Create "COH-OTHER"))
    let output = run (args repo [] (Map.ofList [ "decisions", changed ]))
    refused output "provenance-digest-mismatch" "provenance.inputDigests.decisionRecords" "plan-provenance" |> ignore
    refused output "provenance-digest-mismatch" "decisionRecords.cohortId" "plan-provenance" |> ignore

[<Fact>]
let ``non-identical generation from identical inputs is refused`` () =
    let repo = clone ()
    let drifted = commitOn repo tryApply "drifted replica" (appendTo "pilot/Timer/generated/Timer/Timer.Transition.g.fs" "\n")
    refused (run (args repo [] (Map.ofList [ "replica-commit", drifted ]))) "nondeterministic-generation" "pilot/Timer/generated/Timer/Timer.Transition.g.fs" "deterministic-generation" |> ignore
    let replica = variant (b1 "prepared.json") (fun n -> (obj n "hostBinding").ReplaceWith(JsonValue.Create "different"))
    Assert.Contains("differ", refused (run (args repo [ "--prepared-replica"; replica ] Map.empty)) "nondeterministic-generation" "prepared" "deterministic-generation")

[<Fact>]
let ``rejected operations must be recorded by the host and must not be applied`` () =
    let repo = clone ()
    let reported =
        variant (b1 "dispositions.json") (fun n ->
            (obj n "rejectedOperations").AsArray().Add(JsonNode.Parse """{"path":"pilot/Timer/Timer.fsproj","reason":"not permitted"}"""))
    Assert.Contains("did not record", refused (run (args repo [] (Map.ofList [ "dispositions", reported ]))) "rejected-operation-unrecorded" "pilot/Timer/Timer.fsproj" "rejected-operations")
    let extra = commitOn repo candidateCommit "extra file" (fun r -> File.WriteAllText(Path.Combine(r, "pilot", "Timer", "Extra.fs"), "module Extra\n"))
    let host =
        variant (b1 "host-observations.json") (fun n ->
            (obj n "observations").AsArray().Add(JsonNode.Parse """{"id":"O1","attemptId":"A1","kind":"write","path":"pilot/Timer/Extra.fs","outcome":"denied","recordedBy":"host","protected":false}"""))
    refused (run (args repo [] (Map.ofList [ "commit", extra; "host-observations", host ]))) "rejected-operation-applied" "pilot/Timer/Extra.fs" "rejected-operations" |> ignore

[<Fact>]
let ``an invalid prepared artifact is refused field by field and unknown fields are ignored`` () =
    let repo = clone ()
    let prepared =
        variant (b1 "prepared.json") (fun n ->
            n.AsObject().Remove("permittedExtensionFiles") |> ignore
            (obj (obj (obj n "defaultPlan") "provenance") "generatorId").ReplaceWith(JsonValue.Create 3)
            n.AsObject().Add("anotherFutureField", JsonValue.Create true))
    let output = run (args repo [] (Map.ofList [ "prepared", prepared ]))
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    let details = findings report |> List.filter (fun (c, _, _) -> c = "prepared-artifact-invalid") |> List.map (fun (_, _, d) -> d)
    Assert.Contains("permittedExtensionFiles is required", details)
    Assert.Contains("defaultPlan.provenance.generatorId must be a non-empty string", details)
    Assert.Equal(2, details.Length)

let private obligationOfKind kind =
    let prepared = JsonNode.Parse(File.ReadAllText(b1 "prepared.json"))
    [ for o in (obj (obj (obj prepared "defaultPlan") "obligations") "obligations").AsArray() do
        if (obj o "kind").GetValue<string>() = kind then (obj o "id").GetValue<string>() ]
    |> List.head

/// Dispositions where the given obligation is claimed implemented.
let private claimImplemented (id: string) =
    variant (b1 "dispositions.json") (fun n ->
        for d in (obj n "dispositions").AsArray() do
            if (obj d "obligationId").GetValue<string>() = id then
                (obj d "disposition").ReplaceWith(JsonValue.Create "implemented"))

[<Fact>]
let ``an unresolved conflict claimed implemented is refused`` () =
    let repo = clone ()
    let id = obligationOfKind "conflict"
    let detail =
        refused (run (args repo [] (Map.ofList [ "dispositions", claimImplemented id ]))) "unresolved-obligation-claimed-implemented" id "obligation-tracking"
    Assert.Contains("may only be escalated", detail)

[<Fact>]
let ``an undecided effect failure claimed implemented is refused while the decision record leaves onFailure null`` () =
    let repo = clone ()
    let id = obligationOfKind "undecidedEffectFailure"
    refused (run (args repo [] (Map.ofList [ "dispositions", claimImplemented id ]))) "unresolved-obligation-claimed-implemented" id "obligation-tracking" |> ignore

[<Fact>]
let ``an uncovered requirement claimed implemented is refused`` () =
    let repo = clone ()
    let key = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L145-L148"
    let prepared =
        variant (b1 "prepared.json") (fun n ->
            (obj (obj (obj n "defaultPlan") "obligations") "obligations").AsArray()
                .Add(JsonNode.Parse("""{"id":"OBL-COH-TIMER-uncoveredRequirement-test","kind":"uncoveredRequirement","requirementKeys":[""" + "\"" + key + "\"" + """],"nodeIds":["VER-TIMER-SESSION"]}"""))
            (obj (obj n "handoff") "obligationIds").AsArray().Add(JsonValue.Create "OBL-COH-TIMER-uncoveredRequirement-test"))
    let dispositions =
        variant (b1 "dispositions.json") (fun n ->
            (obj n "dispositions").AsArray()
                .Add(JsonNode.Parse """{"obligationId":"OBL-COH-TIMER-uncoveredRequirement-test","disposition":"implemented","files":["pilot/Timer/Completion.fs"]}"""))
    let output = run (args repo [] (Map.ofList [ "prepared", prepared; "dispositions", dispositions ]))
    refused output "unresolved-obligation-claimed-implemented" "OBL-COH-TIMER-uncoveredRequirement-test" "obligation-tracking" |> ignore

[<Fact>]
let ``an undecided effect failure closed by a decided onFailure may be implemented`` () =
    let repo = clone ()
    let id = obligationOfKind "undecidedEffectFailure"
    let decisions =
        variant (b1 "decisions.json") (fun n ->
            for r in (obj n "records").AsArray() do
                if (obj r "nodeId").GetValue<string>() = "IFC-TIMER-PORTS" then
                    for port in (obj (obj r "content") "ports").AsArray() do
                        if (obj port "port").GetValue<string>() = "transitionEffect" then
                            (obj port "port").Parent |> Option.ofObj |> Option.iter (fun p -> p.AsObject()["onFailure"] <- JsonValue.Create "stay"))
    let prepared =
        variant (b1 "prepared.json") (fun n ->
            (obj (obj (obj (obj n "defaultPlan") "provenance") "inputDigests") "decisionRecords")
                .ReplaceWith(JsonValue.Create(sha (File.ReadAllBytes decisions))))
    let output = run (args repo [] (Map.ofList [ "prepared", prepared; "decisions", decisions; "dispositions", claimImplemented id ]))
    let report = parse output
    Assert.Empty(findings report)
    Assert.Equal("passed", check report "obligation-tracking")
    Assert.Equal("passed", check report "plan-provenance")
    // A decision record whose digest the prepared artifact does not declare closes nothing.
    let undeclared = run (args repo [] (Map.ofList [ "decisions", decisions; "dispositions", claimImplemented id ]))
    Assert.Contains("unresolved-obligation-claimed-implemented", findings (parse undeclared) |> List.map (fun (c, _, _) -> c))

[<Fact>]
let ``an adapter edit in an acceptanceAdapter slot passes the boundary check and auxiliary handoff ids are ignored`` () =
    let repo = clone ()
    let adapter = "pilot/Timer/TimerOracleAdapter.fs"
    let prepared =
        variant (b1 "prepared.json") (fun n ->
            let files = (obj n "permittedExtensionFiles").AsArray()
            let index = files |> Seq.findIndex (fun f -> (match f with null -> "" | f -> f.GetValue<string>()) = adapter)
            files.RemoveAt index
            n.AsObject().Add("permittedExtensionSlots", JsonNode.Parse("""[{"kind":"acceptanceAdapter","path":""" + "\"" + adapter + "\"" + ""","maxBytes":65536}]"""))
            let handed = (obj (obj n "handoff") "obligationIds").AsArray()
            handed.Add(JsonValue.Create "IMPLEMENT:ITimerPorts.Perform/Stop")
            handed.Add(JsonValue.Create "VERIFY:DOK-ORACLE-TIMER-ADAPTER-1"))
    let dispositions =
        variant (b1 "dispositions.json") (fun n ->
            (obj n "dispositions").AsArray().Add(JsonNode.Parse """{"obligationId":"VERIFY:DOK-ORACLE-TIMER-ADAPTER-1","disposition":"implemented"}"""))
    let report = parse (run (args repo [] (Map.ofList [ "prepared", prepared; "dispositions", dispositions ])))
    Assert.Empty(findings report)
    Assert.Equal("passed", check report "extension-boundary")
    Assert.Equal("passed", check report "file-mutations")
    Assert.Equal("passed", check report "obligation-tracking")
    // A slot never makes a project file writable.
    let projectSlot =
        variant (b1 "prepared.json") (fun n ->
            n.AsObject().Add("permittedExtensionSlots", JsonNode.Parse """[{"kind":"acceptanceAdapter","path":"pilot/Timer/Timer.fsproj"}]"""))
    let detail = refused (run (args repo [] (Map.ofList [ "prepared", projectSlot ]))) "extension-boundary-violation" "pilot/Timer/Timer.fsproj" "extension-boundary"
    Assert.Contains("permits handwritten edits to a project", detail)
