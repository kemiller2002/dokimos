/// `dokimos behavior verify` against real Git repositories. Each candidate
/// is a small F# project committed to a fresh repository; Dokimos
/// materializes the commit from Git objects, builds it, drives the frozen
/// adapter in a separate process and judges the observations itself.
module Dokimos.Cli.Tests.BehaviorVerifyCliTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Xunit
open Dokimos.Cli.Tests.Support

let private oracle = repoPath (Path.Combine("oracles", "construction", "time-entry-timer", "oracle-adapter.json"))
let private sources = repoPath (Path.Combine("tests", "fixtures", "construction", "time-entry-timer", "sources"))
let private oracleDigest () = "sha256:" + (File.ReadAllBytes oracle |> SHA256.HashData |> Convert.ToHexString).ToLowerInvariant()
let private project = "src/Pilot/Pilot.fsproj"

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

let private projectFile =
    """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><Compile Include="Adapter.fs" /></ItemGroup>
</Project>
"""

/// A correct reference implementation of the frozen adapter contract.
let private reference =
    """namespace TimerPilot

module TimerOracleAdapter =
    let decide (state: string) (command: string) : Result<string * string list, string> =
        match state, command with
        | "NoTimer", "start" -> Ok("Running", [])
        | "Running", "pause" -> Ok("Paused", [])
        | "Paused", "resume" -> Ok("Running", [])
        | ("Running" | "Paused"), "stop" -> Ok("NoTimer", [ "complete-entry" ])
        | _ -> Error(sprintf "%s is not legal from %s" command state)

    let recordedMinutes (segments: (int64 * int64) list) : int option =
        let seconds = segments |> List.sumBy (fun (s, e) -> e - s)
        if seconds < 30L then None else Some(int ((seconds + 30L) / 60L))
"""

let private replace (oldText: string) (newText: string) (code: string) =
    if not (code.Contains oldText) then failwithf "fixture edit does not apply: %s" oldText
    code.Replace(oldText, newText)

/// A repository whose HEAD commits `adapter` as the candidate's Adapter.fs.
let private candidateRepo (adapter: string) =
    let repo = tempDirectory ()
    git repo [ "init"; "-q"; "-b"; "main" ] |> ignore
    git repo [ "config"; "user.email"; "candidate@example.invalid" ] |> ignore
    git repo [ "config"; "user.name"; "candidate" ] |> ignore
    write repo project projectFile |> ignore
    write repo "src/Pilot/Adapter.fs" adapter |> ignore
    git repo [ "add"; "." ] |> ignore
    git repo [ "commit"; "-q"; "-m"; "candidate" ] |> ignore
    repo

let private verifyArgs repo extra =
    [ "behavior"; "verify"
      "--oracle"; oracle
      "--expected-oracle-digest"; oracleDigest ()
      "--repository"; repo
      "--commit"; "HEAD"
      "--project"; project
      "--sources"; sources
      "--work-item"; "WI-0006"
      "--json" ]
    @ extra

let private parse (output: Dokimos.Cli.Output) =
    match output.Stdout with
    | Some json ->
        assertSchemaValid "dokimos-behavior-verification.schema.json" json
        JsonDocument.Parse(json).RootElement
    | None -> failwithf "no report; stderr: %A" output.Stderr

let private codes (report: JsonElement) = [ for f in report.GetProperty("Findings").EnumerateArray() -> f.GetProperty("Code").Text ]

let private caseVerdicts (report: JsonElement) =
    [ for c in report.GetProperty("Cases").EnumerateArray() -> c.GetProperty("Id").Text, c.GetProperty("Verdict").Text ] |> Map.ofList

let private failedCases report = caseVerdicts report |> Map.filter (fun _ v -> v = "failed") |> Map.keys |> Seq.toList

let private requirement (report: JsonElement) (suffix: string) =
    report.GetProperty("Requirements").EnumerateArray()
    |> Seq.find (fun r -> r.GetProperty("RequirementKey").Text.EndsWith(suffix, StringComparison.Ordinal))
    |> fun r -> r.GetProperty("IndependentlyVerified").Text

[<Fact>]
let ``a correct reference implementation passes every scored case on the first observed run`` () =
    let repo = candidateRepo reference
    let output = run (verifyArgs repo [])
    let report = parse output
    Assert.Equal(0, output.ExitCode)
    Assert.Equal("passed", report.GetProperty("FirstPassVerdict").Text)
    Assert.Empty(codes report)
    Assert.Equal("passed", report.GetProperty("Compile").GetProperty("Status").Text)
    let counts = report.GetProperty "Counts"
    Assert.Equal(21, counts.GetProperty("Scored").GetInt32())
    Assert.Equal(21, counts.GetProperty("Passed").GetInt32())
    Assert.Equal(3, counts.GetProperty("Unscored").GetInt32())
    // Unscored conflict cases are reported with the candidate's own answer, outside the counts.
    let conflicts = [ for c in report.GetProperty("ConflictObservations").EnumerateArray() -> c ]
    Assert.Equal(3, conflicts.Length)
    for c in conflicts do
        Assert.Equal("unresolved-conflict", c.GetProperty("Classification").Text)
        Assert.Equal("CONFLICT-TIMER-SHORT-STOP", c.GetProperty("Conflict").Text)
        Assert.Equal("None (nothing recorded)", c.GetProperty("Observed").Text)
        Assert.Contains("agent-decided", c.GetProperty("Note").Text, StringComparison.OrdinalIgnoreCase)
    Assert.DoesNotContain("TIMER-SHORT-001", caseVerdicts report |> Map.keys)
    Assert.Equal("verified", requirement report "state-model.md#L116-L129")
    Assert.Equal("partial", requirement report "state-model.md#L131-L144")
    Assert.Equal("unresolved-conflict", requirement report "beyond-csharp.md#L113-L114")
    Assert.Equal("not-observed", requirement report "state-model.md#L145-L148")
    // The run record is what Dokimos itself observed.
    let runRecord = report.GetProperty("Run")[0]
    Assert.Equal("dokimos", runRecord.GetProperty("ObservedBy").Text)
    Assert.Equal(git repo [ "rev-parse"; "HEAD" ], runRecord.GetProperty("CandidateCommit").Text)
    Assert.Equal(git repo [ "rev-parse"; "HEAD^{tree}" ], runRecord.GetProperty("CandidateTree").Text)
    Assert.Equal(oracleDigest (), runRecord.GetProperty("OracleDigest").Text)
    Assert.False(String.IsNullOrWhiteSpace(runRecord.GetProperty("Sdk").Text))
    let commands = [ for c in runRecord.GetProperty("Commands").EnumerateArray() -> c.GetProperty("CommandLine").Text ]
    Assert.Contains("dotnet build src/Pilot/Pilot.fsproj -c Release --nologo -nodeReuse:false", commands)
    Assert.Contains("dotnet fsi --quiet driver.fsx input.json", commands)
    Assert.Equal("committed-before-candidate", report.GetProperty("Oracle").GetProperty("Integrity").GetProperty("History").Text)

[<Fact>]
let ``letting pause succeed from NoTimer fails exactly that case`` () =
    let repo = candidateRepo (reference |> replace "| \"NoTimer\", \"start\" -> Ok(\"Running\", [])" "| \"NoTimer\", \"start\" -> Ok(\"Running\", [])\n        | \"NoTimer\", \"pause\" -> Ok(\"Paused\", [])")
    let output = run (verifyArgs repo [])
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    Assert.Equal("failed", report.GetProperty("FirstPassVerdict").Text)
    Assert.Equal<string list>([ "TIMER-ORACLE-002" ], failedCases report)
    Assert.Equal<string list>([ "oracle-behavior-mismatch" ], codes report)
    Assert.Equal("failed", requirement report "state-model.md#L116-L129")
    Assert.Equal("failed", requirement report "REQUIREMENTS.md#L367-L368")
    Assert.Equal("partial", requirement report "state-model.md#L131-L144")

[<Fact>]
let ``rounding down instead of to the nearest minute fails`` () =
    let repo = candidateRepo (reference |> replace "Some(int ((seconds + 30L) / 60L))" "Some(int (seconds / 60L))")
    let report = parse (run (verifyArgs repo []))
    Assert.Equal("failed", report.GetProperty("FirstPassVerdict").Text)
    Assert.Equal<string list>([ "TIMER-MINUTES-003"; "TIMER-MINUTES-007" ], failedCases report |> List.sort)
    Assert.Equal("failed", requirement report "state-model.md#L131-L144")
    Assert.Equal("verified", requirement report "state-model.md#L116-L129")

[<Fact>]
let ``counting paused time fails`` () =
    let repo =
        candidateRepo (
            reference
            |> replace "let seconds = segments |> List.sumBy (fun (s, e) -> e - s)"
                       "let seconds = (segments |> List.map snd |> List.max) - (segments |> List.map fst |> List.min)")
    let report = parse (run (verifyArgs repo []))
    Assert.Equal("failed", report.GetProperty("FirstPassVerdict").Text)
    Assert.Equal<string list>([ "TIMER-MINUTES-005"; "TIMER-MINUTES-006"; "TIMER-MINUTES-007" ], failedCases report |> List.sort)
    Assert.Equal("failed", requirement report "beyond-csharp.md#L105-L107")

[<Fact>]
let ``a project that does not compile reports compile failure, never a pass`` () =
    let repo = candidateRepo (reference |> replace "Some(int ((seconds + 30L) / 60L))" "Some(seconds + \"minutes\")")
    let output = run (verifyArgs repo [])
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    Assert.Equal("failed", report.GetProperty("Compile").GetProperty("Status").Text)
    Assert.Contains("error", report.GetProperty("Compile").GetProperty("Diagnostics").Text)
    Assert.Equal<string list>([ "candidate-compile-failed" ], codes report)
    Assert.Equal("failed", report.GetProperty("FirstPassVerdict").Text)
    Assert.All(caseVerdicts report |> Map.values, fun v -> Assert.Equal("unknown", v))
    Assert.Equal(0, report.GetProperty("Counts").GetProperty("Passed").GetInt32())

[<Fact>]
let ``a missing adapter or a wrong signature reports its finding code and fails`` () =
    let missing = parse (run (verifyArgs (candidateRepo (reference |> replace "module TimerOracleAdapter" "module SomethingElse")) []))
    Assert.Equal<string list>([ "adapter-missing" ], codes missing |> List.distinct)
    Assert.Equal("failed", missing.GetProperty("FirstPassVerdict").Text)
    let adapter = missing.GetProperty("Adapter")[0]
    Assert.Equal("missing", adapter.GetProperty("Module").Text)
    Assert.All(caseVerdicts missing |> Map.values, fun v -> Assert.Equal("failed", v))
    let misSigned =
        parse (run (verifyArgs (candidateRepo (reference |> replace "(segments: (int64 * int64) list) : int option" "(segments: (int * int) list) : int option" |> replace "30L" "30" |> replace "60L" "60")) []))
    Assert.Equal<string list>([ "adapter-signature-mismatch" ], codes misSigned)
    let verdicts = caseVerdicts misSigned
    Assert.Equal("passed", verdicts["TIMER-ORACLE-001"])
    Assert.Equal("failed", verdicts["TIMER-MINUTES-001"])
    Assert.Equal("failed", misSigned.GetProperty("FirstPassVerdict").Text)

[<Fact>]
let ``a project path the commit does not contain is reported, not built`` () =
    let report = parse (run (verifyArgs (candidateRepo reference) [] |> List.map (fun a -> if a = project then "src/Other/Other.fsproj" else a)))
    Assert.Equal<string list>([ "adapter-project-missing" ], codes report)
    Assert.Equal("not-run", report.GetProperty("Compile").GetProperty("Status").Text)
    Assert.Equal("failed", report.GetProperty("FirstPassVerdict").Text)

[<Fact>]
let ``a forged claimed pass is not counted`` () =
    let repo = candidateRepo (reference |> replace "| \"NoTimer\", \"start\" -> Ok(\"Running\", [])" "| \"NoTimer\", \"start\" -> Ok(\"Running\", [])\n        | \"NoTimer\", \"pause\" -> Ok(\"Paused\", [])")
    let claims =
        write (tempDirectory ()) "claims.json"
            """{"schema":"dokimos.construction-claims/1","source":"candidate","confinement":"unknown","denials":[],"testResults":[{"id":"TIMER-ORACLE-002","outcome":"passed"},{"id":"TIMER-SHORT-001","outcome":"passed"},{"id":"TIMER-ORACLE-001","outcome":"passed"}]}"""
    let report = parse (run (verifyArgs repo [ "--claims"; claims ]))
    let forged = [ for f in report.GetProperty("Findings").EnumerateArray() do if f.GetProperty("Code").Text = "fake-test-result" then f.GetProperty("Subject").Text ]
    Assert.Equal<string list>([ "TIMER-ORACLE-002"; "TIMER-SHORT-001" ], List.sort forged)
    Assert.Equal("failed", caseVerdicts report |> Map.find "TIMER-ORACLE-002")
    Assert.Equal(20, report.GetProperty("Counts").GetProperty("Passed").GetInt32())
    Assert.Equal("failed", report.GetProperty("FirstPassVerdict").Text)

[<Fact>]
let ``an oracle digest mismatch refuses without building or executing the candidate`` () =
    let repo = candidateRepo reference
    let wrong = "sha256:" + String('0', 64)
    let output = run (verifyArgs repo [] |> List.map (fun a -> if a = oracleDigest () then wrong else a))
    let report = parse output
    Assert.Equal(4, output.ExitCode)
    Assert.Equal("refused", report.GetProperty("FirstPassVerdict").Text)
    Assert.Contains("oracle-digest-mismatch", codes report)
    Assert.Equal(0, report.GetProperty("Run").GetArrayLength())
    Assert.Equal("unknown", report.GetProperty("Compile").GetProperty("Status").Text)
    Assert.All(caseVerdicts report |> Map.values, fun v -> Assert.Equal("unknown", v))

[<Fact>]
let ``a candidate that ships the oracle, or predates it, is refused`` () =
    let shipping = candidateRepo reference
    File.Copy(oracle, Path.Combine(shipping, "oracle-copy.json"))
    git shipping [ "add"; "." ] |> ignore
    git shipping [ "commit"; "-q"; "-m"; "peek" ] |> ignore
    let report = parse (run (verifyArgs shipping []))
    Assert.Equal("refused", report.GetProperty("FirstPassVerdict").Text)
    Assert.Contains("oracle-not-independent", codes report)
    let early = tempDirectory ()
    git early [ "init"; "-q"; "-b"; "main" ] |> ignore
    git early [ "config"; "user.email"; "c@example.invalid" ] |> ignore
    git early [ "config"; "user.name"; "c" ] |> ignore
    write early project projectFile |> ignore
    write early "src/Pilot/Adapter.fs" reference |> ignore
    git early [ "add"; "." ] |> ignore
    let info = Diagnostics.ProcessStartInfo("git", UseShellExecute = false)
    [ "-C"; early; "commit"; "-q"; "-m"; "backdated" ] |> List.iter info.ArgumentList.Add
    info.Environment["GIT_COMMITTER_DATE"] <- "2020-01-01T00:00:00Z"
    info.Environment["GIT_AUTHOR_DATE"] <- "2020-01-01T00:00:00Z"
    use proc =
        match Diagnostics.Process.Start info with
        | null -> failwith "git did not start"
        | p -> p
    proc.WaitForExit()
    let backdated = parse (run (verifyArgs early []))
    Assert.Equal("refused", backdated.GetProperty("FirstPassVerdict").Text)
    Assert.Contains("oracle-not-frozen-before-candidate", codes backdated)

[<Fact>]
let ``the candidate is built from Git objects, not the working tree`` () =
    let repo = candidateRepo reference
    File.WriteAllText(Path.Combine(repo, "src", "Pilot", "Adapter.fs"), "this does not compile")
    let report = parse (run (verifyArgs repo []))
    Assert.Equal("passed", report.GetProperty("FirstPassVerdict").Text)

[<Fact>]
let ``the frozen adapter oracle conforms to its published schema`` () =
    assertSchemaValid "dokimos-adapter-oracle.schema.json" (File.ReadAllText oracle)
    Assert.NotEmpty(schemaErrors "dokimos-adapter-oracle.schema.json" ((File.ReadAllText oracle).Replace("\"independent-evaluator\"", "\"generator\"")))
    Assert.NotEmpty(schemaErrors "dokimos-adapter-oracle.schema.json" ((File.ReadAllText oracle).Replace("\"kind\": \"adapter\"", "\"kind\": \"ordo-naming\"")))

[<Fact>]
let ``missing required options are refused`` () =
    Assert.Equal(2, (run [ "behavior"; "verify"; "--oracle"; oracle ]).ExitCode)
    let unpinned = verifyArgs (tempDirectory ()) [] |> List.filter (fun a -> a <> "--expected-oracle-digest" && a <> oracleDigest ())
    Assert.Equal(2, (run unpinned).ExitCode)
