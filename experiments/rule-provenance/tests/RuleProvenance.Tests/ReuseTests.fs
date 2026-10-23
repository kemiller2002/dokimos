module RuleProvenance.Tests.ReuseTests

open System.IO
open Xunit
open RuleProvenance
open RuleProvenance.Tests.Support

let private baseline: Validity =
    { SourceDigest = "sha256:" + String.replicate 64 "1"
      HandoffDigest = "sha256:" + String.replicate 64 "2"
      TraceDigest = Digest.empty
      ToolName = Tool.Name
      ToolVersion = Tool.Version
      ConfigDigest = "sha256:" + String.replicate 64 "3"
      SdkVersion = "10.0.402"
      DependencyDigest = None }

let private codes (r: Result<unit, InvalidationReason list>) =
    match r with
    | Ok() -> []
    | Error rs -> rs |> List.map Reuse.code

[<Fact>]
let ``unchanged inputs allow reuse`` () =
    Assert.Equal(Ok(), Reuse.isReusable baseline baseline)

[<Fact>]
let ``each changed assumption invalidates reuse with a distinct reason`` () =
    let changes =
        [ "source-changed", { baseline with SourceDigest = "sha256:" + String.replicate 64 "9" }
          "handoff-changed", { baseline with HandoffDigest = "sha256:" + String.replicate 64 "9" }
          "trace-changed", { baseline with TraceDigest = "sha256:" + String.replicate 64 "9" }
          "tool-name-changed", { baseline with ToolName = "other-tool" }
          "tool-version-changed", { baseline with ToolVersion = "0.2.0" }
          "config-changed", { baseline with ConfigDigest = "sha256:" + String.replicate 64 "9" }
          "sdk-version-changed", { baseline with SdkVersion = "10.0.500" }
          "dependency-changed", { baseline with DependencyDigest = Some("sha256:" + String.replicate 64 "9") } ]
    for (expected, current) in changes do
        Assert.Equal<string list>([ expected ], codes (Reuse.isReusable baseline current))
    let distinct = changes |> List.map fst |> List.distinct
    Assert.Equal(changes.Length, distinct.Length)

[<Fact>]
let ``several changes report every reason`` () =
    let current = { baseline with ToolVersion = "0.2.0"; SdkVersion = "11.0.100" }
    Assert.Equal<string list>([ "tool-version-changed"; "sdk-version-changed" ], codes (Reuse.isReusable baseline current))

/// End to end through the CLI: audit, then ask whether the result is reusable.
[<Fact>]
let ``CLI reuse check: same inputs reusable; source, handoff, trace, config and SDK changes are not`` () =
    let dir = tempDir ()
    let clean = path "fixtures/constructed/clean"
    File.Copy(Path.Combine(clean, "InvoiceRules.fs"), Path.Combine(dir, "InvoiceRules.fs"))
    File.Copy(Path.Combine(clean, "construction-trace.json"), Path.Combine(dir, "construction-trace.json"))
    File.Copy(handoffPath, Path.Combine(dir, "handoff.json"))
    let inputs sdk =
        [ "--root"; dir; "--handoff"; Path.Combine(dir, "handoff.json"); "--source"; Path.Combine(dir, "InvoiceRules.fs")
          "--trace"; Path.Combine(dir, "construction-trace.json"); "--sdk-version"; sdk ]
    let prior = Path.Combine(dir, "prior.json")
    Assert.Equal(0, (cli ([ "audit" ] @ inputs "10.0.402" @ [ "--out"; prior ])).Exit)
    let reuse extra sdk = cli ([ "reuse"; "--prior"; prior ] @ inputs sdk @ extra)
    let reasons (r: RuleProvenance.Cli.CliResult) =
        (json r).GetProperty("invalidatedBy").EnumerateArray() |> Seq.map (fun e -> str e "code") |> List.ofSeq
    let same = reuse [] "10.0.402"
    Assert.Equal(0, same.Exit)
    Assert.True((json same).GetProperty("reusable").GetBoolean())
    Assert.Equal<string list>([ "sdk-version-changed" ], reasons (reuse [] "10.0.500"))
    Assert.Equal<string list>([ "config-changed" ], reasons (reuse [ "--block-on-proven-provenance" ] "10.0.402"))
    Assert.Equal(ExitCode.PolicyFailure, (reuse [ "--depth"; "3" ] "10.0.402").Exit)
    File.AppendAllText(Path.Combine(dir, "construction-trace.json"), "\n")
    Assert.Equal<string list>([ "trace-changed" ], reasons (reuse [] "10.0.402"))
    File.AppendAllText(Path.Combine(dir, "handoff.json"), "\n")
    Assert.Equal<string list>([ "handoff-changed"; "trace-changed" ], reasons (reuse [] "10.0.402"))
    File.AppendAllText(Path.Combine(dir, "InvoiceRules.fs"), "\n")
    Assert.Contains("source-changed", reasons (reuse [] "10.0.402"))
