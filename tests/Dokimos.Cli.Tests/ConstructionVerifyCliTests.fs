/// `dokimos construction verify` against real Git repositories. The
/// candidate is read from Git objects and the frozen oracle runs against the
/// committed generated code under a harness Dokimos itself starts.
module Dokimos.Cli.Tests.ConstructionVerifyCliTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open Dokimos.Cli.Tests.Support

let private fixture relative = repoPath (Path.Combine("tests", "fixtures", "construction", "time-entry-timer", relative))
let private oracle = repoPath (Path.Combine("oracles", "construction", "time-entry-timer", "oracle.json"))
let private oracleDigest () = "sha256:" + (File.ReadAllBytes oracle |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()
let private planDigest = "sha256:860dd8fe579b4932207e77457373bb833180653f368376669090fdb5eb29fba8"
let private manifestPath = "generated/Timer/construction-manifest.json"

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

let private message =
    String.concat "\n"
        [ "Construct generated/Timer/ from cohort COH-TIMER"; ""
          "Construction-Plan: " + planDigest
          "Construction-Generator: ordo.construction/1"
          "Construction-Cohort: COH-TIMER"
          "Construction-Decisions: DEC-TIMER-STATES" ]

/// A repository whose HEAD is a construction commit of the timer, optionally
/// edited before committing by `edit` (which receives the generated folder).
let private constructionRepo (edit: string -> unit) =
    let repo = tempDirectory ()
    git repo [ "init"; "-q"; "-b"; "main" ] |> ignore
    git repo [ "config"; "user.email"; "verifier@example.invalid" ] |> ignore
    git repo [ "config"; "user.name"; "verifier" ] |> ignore
    write repo "app/App.fsproj" """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="FSharp.Core" Version="8.0.0" /></ItemGroup></Project>""" |> ignore
    git repo [ "add"; "." ] |> ignore
    git repo [ "commit"; "-q"; "-m"; "base" ] |> ignore
    let generated = Path.Combine(repo, "generated", "Timer")
    Directory.CreateDirectory generated |> ignore
    for file in Directory.EnumerateFiles(fixture (Path.Combine("generated", "Timer"))) do
        File.Copy(file, Path.Combine(generated, Path.GetFileName file |> string))
    edit generated
    git repo [ "add"; "." ] |> ignore
    git repo [ "commit"; "-q"; "-m"; message ] |> ignore
    repo

let private sha256 (path: string) = "sha256:" + (File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()

/// Edits one generated file and re-pins the manifest to the new bytes.
let private selfConsistentEdit (file: string) (change: string -> string) (generated: string) =
    let path = Path.Combine(generated, file)
    let before = sha256 path
    File.WriteAllText(path, change (File.ReadAllText path))
    let manifest = Path.Combine(generated, "construction-manifest.json")
    File.WriteAllText(manifest, (File.ReadAllText manifest).Replace(before, sha256 path))

let private verifyArgs repo extra =
    [ "construction"; "verify"
      "--source-manifest"; fixture "source-manifest.json"
      "--blueprint"; fixture "blueprint.json"
      "--plan"; fixture "plan.json"
      "--sources"; fixture "sources"
      "--expected-plan-digest"; planDigest
      "--expected-generator"; "ordo.construction/1"
      "--repository"; repo
      "--commit"; "HEAD"
      "--manifest-path"; manifestPath
      "--oracle"; oracle
      "--expected-oracle-digest"; oracleDigest ()
      "--work-item"; "GH-30" ]
    @ extra

let private withRunner = [ "--oracle-runner"; "dotnet" ]

let private parse (output: Dokimos.Cli.Output) =
    match output.Stdout with
    | Some json ->
        assertSchemaValid "dokimos-construction-verification.schema.json" json
        JsonDocument.Parse(json).RootElement
    | None -> failwithf "no report; stderr: %A" output.Stderr

let private findingCodes (report: JsonElement) =
    [ for f in report.GetProperty("Findings").EnumerateArray() -> f.GetProperty("Code").Text ]

[<Fact>]
let ``a valid construction verifies with traceable source to artifact to observed behavior`` () =
    let repo = constructionRepo ignore
    let output = run (verifyArgs repo withRunner)
    let report = parse output
    Assert.Equal(0, output.ExitCode)
    Assert.Equal("verified", report.GetProperty("Verdict").Text)
    Assert.Empty(findingCodes report)
    let behavior = report.GetProperty "BehavioralAcceptance"
    Assert.Equal("passed", behavior.GetProperty("Status").Text)
    let run = behavior.GetProperty("Run")[0]
    Assert.Equal("dokimos", run.GetProperty("ObservedBy").Text)
    Assert.Equal(git repo [ "rev-parse"; "HEAD" ], run.GetProperty("CandidateCommit").Text)
    Assert.Equal(oracleDigest (), run.GetProperty("OracleDigest").Text)
    let transitions =
        report.GetProperty("Requirements").EnumerateArray()
        |> Seq.find (fun r -> r.GetProperty("RequirementKey").Text.EndsWith("state-model.md#L116-L129", StringComparison.Ordinal))
    Assert.Equal("verified", transitions.GetProperty("IndependentlyVerified").Text)
    Assert.Contains("generated/Timer/Timer.Transition.g.fs", [ for a in transitions.GetProperty("Artifacts").EnumerateArray() -> a.Text ])
    Assert.Contains("transition:Running/Stop", [ for a in transitions.GetProperty("Operations").EnumerateArray() -> a.Text ])
    Assert.Contains("TIMER-ORACLE-008", [ for a in transitions.GetProperty("OracleCases").EnumerateArray() -> a.Text ])
    let counts = report.GetProperty "Counts"
    Assert.Equal(2, counts.GetProperty("IndependentlyVerified").GetInt32())
    Assert.Equal(4, counts.GetProperty("PartiallyVerified").GetInt32())
    Assert.Equal(1, counts.GetProperty("Unresolved").GetInt32())
    Assert.Equal("unknown", report.GetProperty("Containment").GetProperty("Status").Text)

[<Fact>]
let ``altered generated domain behavior fails the independent oracle although the generator's checks pass`` () =
    // decide keeps the previous state on every accepted command. The legal
    // table, the exhaustive target match, the manifest digests and the
    // generator's structuralChecks all still agree with the plan.
    let repo = constructionRepo (selfConsistentEdit "Timer.Transition.g.fs" (fun code -> code.Replace("Next = next;", "Next = state;")))
    let output = run (verifyArgs repo withRunner)
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    Assert.Equal("rejected", report.GetProperty("Verdict").Text)
    let structural = report.GetProperty "StructuralConformance"
    Assert.True(structural.GetProperty("GeneratorStructuralChecks").GetProperty("ConsistentWithPlan").GetBoolean())
    Assert.Equal("passed", structural.GetProperty("Compile").Text)
    let codes = findingCodes report
    Assert.Contains("oracle-behavior-mismatch", codes)
    Assert.DoesNotContain("generated-artifact-modified", codes)
    Assert.DoesNotContain("manifest-plan-divergence", codes)
    let behavior = report.GetProperty "BehavioralAcceptance"
    Assert.Equal("failed", behavior.GetProperty("Status").Text)
    Assert.True(behavior.GetProperty("EscapedConstraints").GetInt32() > 0)
    let failed = [ for f in report.GetProperty("Findings").EnumerateArray() do if f.GetProperty("Code").Text = "oracle-behavior-mismatch" then f ]
    Assert.All(failed, fun f -> Assert.Equal("GH-30", f.GetProperty("WorkItem").Text))
    // Per requirement, not one collapsed score: the transition requirement
    // fails, while command rejection (all refusals) is still observed to hold.
    let status suffix =
        report.GetProperty("Requirements").EnumerateArray()
        |> Seq.find (fun r -> r.GetProperty("RequirementKey").Text.EndsWith(suffix, StringComparison.Ordinal))
        |> fun r -> r.GetProperty("IndependentlyVerified").Text
    Assert.Equal("failed", status "state-model.md#L116-L129")
    Assert.Equal("verified", status "REQUIREMENTS.md#L367-L368")
    Assert.Equal(1, report.GetProperty("Counts").GetProperty("IndependentlyVerified").GetInt32())

[<Fact>]
let ``a candidate that does not compile is a compile failure, not a behavioral pass`` () =
    let repo = constructionRepo (selfConsistentEdit "Timer.Transition.g.fs" (fun code -> code.Replace("let decide", "let decide = 1\n    let broken")))
    let report = parse (run (verifyArgs repo withRunner))
    Assert.Contains("candidate-compile-failed", findingCodes report)
    Assert.Equal("unknown", report.GetProperty("BehavioralAcceptance").GetProperty("Status").Text)
    Assert.Equal("failed", report.GetProperty("StructuralConformance").GetProperty("Compile").Text)

[<Fact>]
let ``without an observed run, behavior is unknown and claimed passes are fake`` () =
    let repo = constructionRepo ignore
    let trx = write (tempDirectory ()) "claimed.trx" """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="TIMER-ORACLE-001" outcome="Passed" /></Results></TestRun>"""
    let output = run (verifyArgs repo [ "--claimed-results"; trx ])
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    Assert.Contains("fake-test-result", findingCodes report)
    Assert.Equal("unknown", report.GetProperty("BehavioralAcceptance").GetProperty("Status").Text)
    let clean = run (verifyArgs repo [])
    Assert.Equal(3, clean.ExitCode)
    Assert.Equal("incomplete", (parse clean).GetProperty("Verdict").Text)

[<Fact>]
let ``the candidate is read from Git objects, not the working tree`` () =
    let repo = constructionRepo ignore
    let transition = Path.Combine(repo, "generated", "Timer", "Timer.Transition.g.fs")
    File.WriteAllText(transition, (File.ReadAllText transition).Replace("Next = next;", "Next = state;"))
    File.WriteAllText(Path.Combine(repo, "generated", "Timer", "Untracked.fs"), "module Untracked\n")
    let report = parse (run (verifyArgs repo withRunner))
    Assert.Equal("verified", report.GetProperty("Verdict").Text)

[<Fact>]
let ``an unauthorized dependency added in the construction commit is reported`` () =
    let repo = constructionRepo (fun generated ->
        let project = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName generated |> string) |> string, "app", "App.fsproj")
        File.WriteAllText(project, (File.ReadAllText project).Replace("</ItemGroup>", """<PackageReference Include="Left.Pad" Version="1.0.0" /></ItemGroup>""")))
    Assert.Contains("unauthorized-dependency", findingCodes (parse (run (verifyArgs repo []))))

[<Fact>]
let ``an unpinned oracle and missing inputs are refused`` () =
    let repo = constructionRepo ignore
    let unpinned = verifyArgs repo [] |> List.filter (fun a -> a <> "--expected-oracle-digest" && a <> oracleDigest ())
    Assert.Equal(2, (run unpinned).ExitCode)
    Assert.Equal(2, (run [ "construction"; "verify"; "--plan"; fixture "plan.json" ]).ExitCode)
    let missingCommit = verifyArgs repo [] |> List.map (fun a -> if a = "HEAD" then "0000000000000000000000000000000000000000" else a)
    Assert.Equal(3, (run missingCommit).ExitCode)

[<Fact>]
let ``the frozen oracle and example evidence conform to the published input schema`` () =
    assertSchemaValid "dokimos-construction-inputs.schema.json" (File.ReadAllText oracle)
    for example in
        [ """{"schema":"dokimos.host-observations/1","host":{"id":"h","recorder":"host","enforcement":"enforced","mechanism":"landlock","platformProofs":[{"name":"linux-landlock-abi","status":"available"}]},"observations":[{"id":"O1","attemptId":"A1","kind":"write","path":"quality/baseline.json","outcome":"denied","recordedBy":"host","protected":true}]}"""
          """{"schema":"dokimos.construction-claims/1","source":"praxis","confinement":"unknown","denials":[],"testResults":[{"id":"TIMER-ORACLE-001","outcome":"passed"}]}"""
          """{"schema":"dokimos.construction-authorization/1","authority":"DEC-X","dependencies":[],"waivers":[],"exceptions":[]}""" ] do
        assertSchemaValid "dokimos-construction-inputs.schema.json" example
    Assert.NotEmpty(schemaErrors "dokimos-construction-inputs.schema.json" ((File.ReadAllText oracle).Replace("\"independent-evaluator\"", "\"generator\"")))
