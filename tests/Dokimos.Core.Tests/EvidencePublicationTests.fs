namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core
open Dokimos.Core.EvidenceReuse
open Dokimos.Core.EvidenceCheckpoint
open Dokimos.Core.FinalVerification

/// EII Workstream G negative controls: categories that are never reused,
/// the runtime environment as an identity input, and how a published failure
/// affects reuse (only at its own identity).
module EvidencePublicationTests =
    let private revA = String('a', 40)
    let private revB = String('b', 40)
    let private h (c: char) = "sha256:" + String(c, 64)

    let private inputs: IdentityInputs =
        { Sources = Map [ "src/Store.fs", h '1'; "src/Model.fs", h '2' ]
          Dependencies = Map [ "FSharp.Core", "9.0.100"; "Thoth.Json", "10.2.0" ]
          TestContract = h '3'
          Configuration = Map [ "Configuration", "Release"; "Browser", "chromium" ]
          ToolVersion = "dotnet-sdk 10.0.402; xunit 2.9.2" }

    let private record (i: IdentityInputs) : PriorEvidence =
        { EvidenceId = "EV-store-1"
          TestId = "store-roundtrip"
          Outcome = OutcomePassed
          ProducedAtRevision = Some revA
          Freshness = Some ContentAddressed
          EvidenceIdentity = Some(identity i)
          Inputs = Some i }

    let private request freshness (current: IdentityInputs) : TestRequest =
        { TestId = "store-roundtrip"
          Freshness = freshness
          Category = Functional
          Patterns = Some [ "src/**"; "tests/contracts/store.json" ]
          Inputs = Ok current }

    let private planFor freshness revision changed current priors =
        plan revision changed [] [] [ request freshness current ] priors

    let private single (p: Plan) = Assert.Single p.Tests

    let private codes (d: TestDecision) = d.Reasons |> List.map _.Code |> Set.ofList

    let private assertRun code (d: TestDecision) =
        Assert.True(isRun d, $"expected run, reasons: %A{d.Reasons}")
        Assert.Contains(code, codes d)

    // ---- EII-G: independent publication and the categories that are never reused ---------------------------------

    [<Fact>]
    let ``changing the declared runtime environment refuses reuse`` () =
        let ran = { inputs with Configuration = inputs.Configuration |> Map.add "runtime.os" "linux-x64" |> Map.add "runtime.framework" "net8.0" }
        for key, value in [ "runtime.os", "osx-arm64"; "runtime.framework", "net10.0" ] do
            let current = { ran with Configuration = Map.add key value ran.Configuration }
            let d = planFor ContentAddressed revB None current [ record ran ] |> single
            assertRun "REUSE.INPUT_CHANGED" d
            Assert.Contains(d.Reasons, fun r -> r.Subject = "EV-store-1:configuration:" + key)

    [<Fact>]
    let ``security evidence is never reused even with identical inputs`` () =
        let security = { request ContentAddressed inputs with Category = Security }
        let d = plan revB None [] [] [ security ] [ record inputs ] |> single
        assertRun "REUSE.SECURITY_NEVER_REUSED" d
        Assert.Equal(Some(identity inputs), d.CurrentIdentity)

    [<Fact>]
    let ``independent acceptance evidence is never reused even at the same revision`` () =
        let acceptance = { request ContentAddressed inputs with Category = IndependentAcceptance }
        for revision in [ revA; revB ] do
            plan revision None [] [] [ acceptance ] [ record inputs ] |> single |> assertRun "REUSE.ACCEPTANCE_NEVER_REUSED"

    [<Fact>]
    let ``a failure at the same identity blocks reuse whichever was recorded first`` () =
        let failed outcome id = { record inputs with EvidenceId = id; Outcome = outcome }
        for priors in [ [ failed OutcomeFailed "EV-a"; record inputs ]
                        [ record inputs; failed OutcomeFailed "EV-z" ]
                        [ record inputs; failed OutcomeUnavailable "EV-z" ] ] do
            let d = planFor ContentAddressed revB None inputs priors |> single
            assertRun "REUSE.CONFLICTING_PRIOR" d

    [<Fact>]
    let ``a failure at another identity of the same test does not hide valid evidence`` () =
        let other = { inputs with Sources = Map.add "src/Store.fs" (h '9') inputs.Sources }
        let failedElsewhere = { record other with EvidenceId = "EV-store-2"; Outcome = OutcomeFailed }
        let d = planFor ContentAddressed revB None inputs [ record inputs; failedElsewhere ] |> single
        Assert.False(isRun d, $"%A{d.Reasons}")
        match d.Decision with
        | Reuse prior -> Assert.Equal("EV-store-1", prior.EvidenceId)
        | Run -> failwith "expected reuse"

    [<Fact>]
    let ``a failure of another test does not hide or invalidate this test's evidence`` () =
        let view = { inputs with Sources = Map [ "src/View.fs", h '5' ] }
        let viewFailed = { record view with EvidenceId = "EV-view-1"; TestId = "view-render"; Outcome = OutcomeFailed }
        let viewRequest = { request ContentAddressed view with TestId = "view-render"; Patterns = Some [ "src/View.fs" ] }
        let p = plan revB None [] [] [ request ContentAddressed inputs; viewRequest ] [ record inputs; viewFailed ]
        let byId = p.Tests |> List.map (fun t -> t.TestId, t) |> Map.ofList
        let store = byId["store-roundtrip"]
        Assert.False(isRun store, $"%A{store.Reasons}")
        assertRun "REUSE.PRIOR_NOT_PASSED" byId["view-render"]

    [<Fact>]
    let ``a forged failing record cannot block reuse but a genuine one can`` () =
        // A failing record whose identity is not the digest of its inputs is
        // not evidence about this identity, so it contradicts nothing.
        let forged = { record inputs with EvidenceId = "EV-forged"; Outcome = OutcomeFailed; Inputs = Some { inputs with ToolVersion = "other" } }
        let d = planFor ContentAddressed revB None inputs [ record inputs; forged ] |> single
        Assert.False(isRun d, $"%A{d.Reasons}")

    [<Fact>]
    let ``only complete, self-consistent records can be published`` () =
        let r = record inputs
        Assert.Equal(None, recordProblem r)
        Assert.Equal(None, recordProblem { r with Outcome = OutcomeFailed })
        for incomplete in [ { r with Inputs = None }; { r with ProducedAtRevision = Some "main" }; { r with Freshness = None }
                            { r with Inputs = Some { inputs with ToolVersion = "" }; EvidenceIdentity = Some(identity { inputs with ToolVersion = "" }) } ] do
            match recordProblem incomplete with
            | Some(RecordIncomplete _) -> ()
            | other -> failwith $"expected incomplete, got %A{other}"
        match recordProblem { r with EvidenceIdentity = Some(h 'f') } with
        | Some(RecordIdentityNotDerived(claimed, derived)) ->
            Assert.Equal(h 'f', claimed)
            Assert.Equal(identity inputs, derived)
        | other -> failwith $"expected identity mismatch, got %A{other}"

    // ---- Echelon development speed optimization, Priority Four: mandatory final verification ---------------------

    [<Fact>]
    let ``the release checkpoint refuses reuse of otherwise valid evidence`` () =
        // Control: identical inputs, valid passing record: reused during development.
        let incremental = planFor ContentAddressed revB None inputs [ record inputs ] |> single
        Assert.False(isRun incremental, $"%A{incremental.Reasons}")
        let release (freshness, revision, prior) =
            planAt Release revision None [] [] [ request freshness inputs ] [ prior ]
        for case in [ ContentAddressed, revB, record inputs
                      // per-commit evidence at its own revision is otherwise reusable too
                      PerCommit, revA, { record inputs with Freshness = Some PerCommit } ] do
            let p = release case
            Assert.Equal(Release, p.Checkpoint)
            let d = single p
            assertRun "REUSE.FINAL_VERIFICATION_REQUIRED" d
            // The only reason is the checkpoint: the evidence itself was valid.
            Assert.Equal<Set<string>>(Set [ "REUSE.FINAL_VERIFICATION_REQUIRED" ], codes d)

    [<Fact>]
    let ``plan is the incremental checkpoint and the request checkpoint is read strictly`` () =
        Assert.Equal(Incremental, (planFor ContentAddressed revB None inputs [ record inputs ]).Checkpoint)
        let requestJson (checkpoint: string option) =
            let member' = checkpoint |> Option.map (fun c -> $"\"Checkpoint\":\"{c}\",") |> Option.defaultValue ""
            "{\"SchemaVersion\":\"dokimos.evidence-reuse-request/1\"," + member' + "\"Revision\":\"" + revB + "\",\"Tests\":[],\"PriorEvidence\":[]}"
        let checkpointOf c = EvidenceReuseWire.readRequest (requestJson c) |> Result.map _.Checkpoint
        Assert.Equal(Ok Incremental, checkpointOf None)
        Assert.Equal(Ok Incremental, checkpointOf (Some "incremental"))
        Assert.Equal(Ok Release, checkpointOf (Some "release"))
        Assert.True(checkpointOf (Some "final") |> Result.isError)
