module Dokimos.Cli.Tests.EvidencePlanCliTests

open System
open System.IO
open System.Text.Json
open Xunit
open Dokimos.Cli.Tests.Support

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

let private commitAll repo message =
    git repo [ "add"; "-A" ] |> ignore
    git repo [ "-c"; "user.name=t"; "-c"; "user.email=t@example.invalid"; "commit"; "-q"; "-m"; message ] |> ignore
    git repo [ "rev-parse"; "HEAD" ]

let private repository () =
    let repo = tempDirectory ()
    git repo [ "init"; "-q" ] |> ignore
    write repo "src/Store.fs" "module Store\nlet save x = x\n" |> ignore
    write repo "src/View.fs" "module View\nlet render x = string x\n" |> ignore
    write repo "tests/contracts/store.json" "{ \"test\": \"store-roundtrip\" }\n" |> ignore
    write repo "tests/contracts/view.json" "{ \"test\": \"view-render\" }\n" |> ignore
    write repo "tests/contracts/live.json" "{ \"test\": \"live-write\" }\n" |> ignore
    write repo "packages.lock.json" "{ \"FSharp.Core\": \"9.0.100\" }\n" |> ignore
    write repo "docs/notes.md" "notes\n" |> ignore
    repo, commitAll repo "initial"

let private json (value: obj) = JsonSerializer.Serialize value

let private mapping (sources: string list) (contract: string) =
    json
        {| Sources = sources
           TestContract = contract
           Dependencies = Map [ "FSharp.Core", "9.0.100" ]
           Configuration = Map [ "Configuration", "Release" ]
           ToolVersion = "dotnet-sdk 10.0.402; xunit 2.9.2" |}

let private test (id: string) (freshness: string) (inputs: string option) =
    let head = $"\"TestId\":{json id},\"Freshness\":{json freshness}"
    match inputs with
    | Some i -> "{" + head + ",\"Inputs\":" + i + "}"
    | None -> "{" + head + "}"

let private request (revision: string) (changed: string list option) (priors: string list) =
    let changedMember = changed |> Option.map (fun c -> $"\"ChangedPaths\":{json c},") |> Option.defaultValue ""
    let tests =
        [ test "store-roundtrip" "content-addressed" (Some(mapping [ "src/Store.fs" ] "tests/contracts/store.json"))
          test "view-render" "content-addressed" (Some(mapping [ "src/View.fs" ] "tests/contracts/view.json"))
          test "live-write" "live" (Some(mapping [ "src/Store.fs" ] "tests/contracts/live.json"))
          test "unmapped" "content-addressed" None ]
    "{\"SchemaVersion\":\"dokimos.evidence-reuse-request/1\",\"Revision\":" + json revision + ","
    + changedMember
    + "\"GlobalInputs\":[\"packages.lock.json\"],\"IgnoredPaths\":[\"docs/**\"],"
    + "\"Tests\":[" + String.Join(",", tests) + "],"
    + "\"PriorEvidence\":[" + String.Join(",", priors) + "]}"

let private plan repo requestJson =
    let dir = tempDirectory ()
    let path = write dir "request.json" requestJson
    assertSchemaValid "dokimos-evidence-reuse-request.schema.json" requestJson
    run [ "evidence"; "plan"; "--request"; path; "--root"; repo ]

let private stdout (output: Dokimos.Cli.Output) = output.Stdout |> Option.defaultValue ""

let private decisions (json: string) =
    use document = JsonDocument.Parse json
    document.RootElement.GetProperty("Tests").EnumerateArray()
    |> Seq.map (fun t -> t.GetProperty("TestId").Text, t.GetProperty("Decision").Text)
    |> Map.ofSeq

/// The provenance a host records after running every planned test (all pass).
let private provenance (planJson: string) (revision: string) =
    use document = JsonDocument.Parse planJson
    document.RootElement.GetProperty("Tests").EnumerateArray()
    |> Seq.filter (fun t -> t.GetProperty("Inputs").ValueKind = JsonValueKind.Object)
    |> Seq.map (fun t ->
        json
            {| SchemaVersion = "dokimos.evidence-provenance/1"
               EvidenceId = "EV-" + t.GetProperty("TestId").Text
               TestId = t.GetProperty("TestId").Text
               Outcome = "passed"
               ProducedAtRevision = revision
               Freshness = t.GetProperty("Freshness").Text
               EvidenceIdentity = t.GetProperty("CurrentIdentity").Text
               Inputs = t.GetProperty("Inputs").Clone() |})
    |> Seq.toList

[<Fact>]
let ``first plan runs everything and records schema-valid inputs`` () =
    let repo, revision = repository ()
    let output = plan repo (request revision None [])
    Assert.Equal(0, output.ExitCode)
    assertSchemaValid "dokimos-evidence-reuse-plan.schema.json" (stdout output)
    Assert.True(decisions (stdout output) |> Map.forall (fun _ d -> d = "run"))

[<Fact>]
let ``a docs-only change reuses unchanged tests and still runs live and unmapped tests`` () =
    let repo, first = repository ()
    let priors = provenance (stdout (plan repo (request first None []))) first
    write repo "docs/notes.md" "more notes\n" |> ignore
    let second = commitAll repo "docs"
    let output = plan repo (request second (Some [ "docs/notes.md" ]) priors)
    assertSchemaValid "dokimos-evidence-reuse-plan.schema.json" (stdout output)
    let d = decisions (stdout output)
    Assert.Equal("reuse", d["store-roundtrip"])
    Assert.Equal("reuse", d["view-render"])
    Assert.Equal("run", d["live-write"])
    Assert.Equal("run", d["unmapped"])
    use document = JsonDocument.Parse(stdout output)
    let reused =
        document.RootElement.GetProperty("Tests").EnumerateArray()
        |> Seq.find (fun t -> t.GetProperty("TestId").Text = "store-roundtrip")
    Assert.Equal(first, reused.GetProperty("Reused").GetProperty("ProducedAtRevision").Text)
    Assert.Equal(2, document.RootElement.GetProperty("Summary").GetProperty("Reuse").GetInt32())

[<Fact>]
let ``a source change runs only the tests mapped to it`` () =
    let repo, first = repository ()
    let priors = provenance (stdout (plan repo (request first None []))) first
    write repo "src/Store.fs" "module Store\nlet save x = x + 1\n" |> ignore
    let second = commitAll repo "store"
    let d = decisions (stdout (plan repo (request second (Some [ "src/Store.fs" ]) priors)))
    Assert.Equal("run", d["store-roundtrip"])
    Assert.Equal("reuse", d["view-render"])
    // Without a change list the content identity alone still refuses reuse.
    let d = decisions (stdout (plan repo (request second None priors)))
    Assert.Equal("run", d["store-roundtrip"])
    Assert.Equal("reuse", d["view-render"])

[<Fact>]
let ``a global input change runs every mapped test`` () =
    let repo, first = repository ()
    let priors = provenance (stdout (plan repo (request first None []))) first
    write repo "packages.lock.json" "{ \"FSharp.Core\": \"9.0.300\" }\n" |> ignore
    let second = commitAll repo "lock"
    let d = decisions (stdout (plan repo (request second None priors)))
    Assert.True(d |> Map.forall (fun _ v -> v = "run"))

[<Fact>]
let ``a checkout with tracked modifications or at another revision is refused`` () =
    let repo, revision = repository ()
    write repo "src/Store.fs" "edited\n" |> ignore
    let dirty = plan repo (request revision None [])
    Assert.Equal(3, dirty.ExitCode)
    Assert.Contains("evidence-checkout-invalid", dirty.Stderr |> Option.defaultValue "")
    let other = plan repo (request (String('c', 40)) None [])
    Assert.Equal(3, other.ExitCode)

[<Fact>]
let ``malformed or duplicate requests are refused`` () =
    let repo, revision = repository ()
    let dir = tempDirectory ()
    let bad = write dir "bad.json" ((request revision None []).Replace("\"Tests\"", "\"Extra\":1,\"Tests\""))
    Assert.Equal(3, (run [ "evidence"; "plan"; "--request"; bad; "--root"; repo ]).ExitCode)
    Assert.Equal(2, (run [ "evidence"; "plan" ]).ExitCode)

module Completion =
    let private revision = String('b', 40)
    let private digest (c: char) = "sha256:" + String(c, 64)

    let private packet =
        json
            {| SchemaVersion = "praxis.completion-packet/1"; WorkItemId = "W-1"; ClaimToken = "c"; FencingGeneration = 1L
               Commit = revision; VerificationRevision = revision
               OrdoContractVersion = "ordo.constructibility-contract/1"; OrdoContractDigest = digest 'c'
               RequirementIds = [ "RQ-1" ]; DecisionIds = ([]: string list); ConflictResolutionIds = ([]: string list)
               Validations = [ {| Name = "tests"; Status = "passed"; EvidenceIds = [ "EV-1" ] |} ]
               Evidence = [ {| Id = "EV-1"; Digest = digest 'd'; Revision = revision |} ] |}

    let private authority (freshness: string) =
        json
            {| SchemaVersion = "dokimos.completion-authority/1"; WorkItemId = "W-1"; ClaimToken = "c"; FencingGeneration = 1L
               ExpectedCommit = revision; ExpectedVerificationRevision = revision
               SupportedOrdoContractVersions = [ "ordo.constructibility-contract/1" ]; ExpectedOrdoContractDigest = digest 'c'
               RequiredRequirementIds = [ "RQ-1" ]; RequiredDecisionIds = ([]: string list); RequiredConflictResolutionIds = ([]: string list)
               RequiredValidationNames = [ "tests" ]
               Evidence =
                [ {| Id = "EV-1"; Digest = digest 'd'; Revision = revision; Status = "passed"; Reusable = true
                     Provenance = {| EvidenceIdentity = digest 'e'; Freshness = freshness; ProducedAtRevision = String('a', 40) |} |} ]
               ConsumedPacketDigests = ([]: string list) |}

    let private verifyRaw authorityJson =
        let dir = tempDirectory ()
        run [ "completion"; "verify"; "--packet"; write dir "p.json" packet; "--authority"; write dir "a.json" authorityJson ]

    let private verify authorityJson =
        assertSchemaValid "dokimos-completion-authority.schema.json" authorityJson
        verifyRaw authorityJson

    [<Fact>]
    let ``completion accepts carried content-addressed evidence and lists it as reused`` () =
        let output = verify (authority "content-addressed")
        Assert.Equal(0, output.ExitCode)
        let receipt = stdout output
        assertSchemaValid "dokimos-completion-verification.schema.json" receipt
        Assert.Contains("\"EV-1\"", receipt)

    [<Fact>]
    let ``completion refuses carried live and per-commit evidence`` () =
        let live = verify (authority "live")
        Assert.Equal(4, live.ExitCode)
        Assert.Contains("DOK.COMPLETION.REUSE_LIVE_EVIDENCE", stdout live)
        let perCommit = verify (authority "per-commit")
        Assert.Equal(4, perCommit.ExitCode)
        Assert.Contains("DOK.COMPLETION.REUSE_STALE_COMMIT", stdout perCommit)

    [<Fact>]
    let ``completion refuses a malformed provenance at intake`` () =
        let malformed = (authority "content-addressed").Replace(String('a', 40), "main")
        Assert.NotEmpty(schemaErrors "dokimos-completion-authority.schema.json" malformed)
        let output = verifyRaw malformed
        Assert.Equal(3, output.ExitCode)
