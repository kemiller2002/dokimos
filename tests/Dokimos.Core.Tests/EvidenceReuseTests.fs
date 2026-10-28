namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core
open Dokimos.Core.EvidenceReuse

/// Positive and negative controls for content-addressed evidence reuse.
/// Each identity input is changed in turn and reuse must be refused.
module EvidenceReuseTests =
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

    // ---- positive control ------------------------------------------------------------------------------------------

    [<Fact>]
    let ``identical inputs reuse evidence from an earlier revision`` () =
        let d = planFor ContentAddressed revB None inputs [ record inputs ] |> single
        Assert.False(isRun d, $"%A{d.Reasons}")
        Assert.Empty d.Reasons
        match d.Decision with
        | Reuse prior -> Assert.Equal("EV-store-1", prior.EvidenceId)
        | Run -> failwith "expected reuse"
        Assert.Equal(Some(identity inputs), d.CurrentIdentity)

    [<Fact>]
    let ``identity is independent of map order and framed against concatenation`` () =
        let reordered = { inputs with Sources = Map(List.rev (Map.toList inputs.Sources)) }
        Assert.Equal(identity inputs, identity reordered)
        let a = { inputs with Configuration = Map [ "a", "bc" ] }
        let b = { inputs with Configuration = Map [ "ab", "c" ] }
        Assert.NotEqual<string>(identity a, identity b)
        Assert.True(isSha256 (identity inputs))

    // ---- negative controls: each identity input ----------------------------------------------------------------

    [<Fact>]
    let ``changing a source file refuses reuse`` () =
        let current = { inputs with Sources = Map.add "src/Store.fs" (h '9') inputs.Sources }
        let d = planFor ContentAddressed revB None current [ record inputs ] |> single
        assertRun "REUSE.INPUT_CHANGED" d
        Assert.Contains(d.Reasons, fun r -> r.Subject = "EV-store-1:source:src/Store.fs")

    [<Fact>]
    let ``adding or removing a source file refuses reuse`` () =
        let added = { inputs with Sources = Map.add "src/New.fs" (h '8') inputs.Sources }
        let removed = { inputs with Sources = Map.remove "src/Model.fs" inputs.Sources }
        for current in [ added; removed ] do
            planFor ContentAddressed revB None current [ record inputs ] |> single |> assertRun "REUSE.INPUT_CHANGED"

    [<Fact>]
    let ``changing a dependency version refuses reuse`` () =
        let current = { inputs with Dependencies = Map.add "Thoth.Json" "10.3.0" inputs.Dependencies }
        let d = planFor ContentAddressed revB None current [ record inputs ] |> single
        assertRun "REUSE.INPUT_CHANGED" d
        Assert.Contains(d.Reasons, fun r -> r.Subject = "EV-store-1:dependency:Thoth.Json")

    [<Fact>]
    let ``changing the test contract refuses reuse`` () =
        let d = planFor ContentAddressed revB None { inputs with TestContract = h '4' } [ record inputs ] |> single
        assertRun "REUSE.INPUT_CHANGED" d
        Assert.Contains(d.Reasons, fun r -> r.Subject = "EV-store-1:test-contract:test-contract")

    [<Fact>]
    let ``changing configuration refuses reuse`` () =
        let current = { inputs with Configuration = Map.add "Configuration" "Debug" inputs.Configuration }
        let d = planFor ContentAddressed revB None current [ record inputs ] |> single
        assertRun "REUSE.INPUT_CHANGED" d
        Assert.Contains(d.Reasons, fun r -> r.Subject = "EV-store-1:configuration:Configuration")

    [<Fact>]
    let ``changing the verification tool version refuses reuse`` () =
        let d = planFor ContentAddressed revB None { inputs with ToolVersion = "dotnet-sdk 10.0.500; xunit 2.9.2" } [ record inputs ] |> single
        assertRun "REUSE.INPUT_CHANGED" d
        Assert.Contains(d.Reasons, fun r -> r.Subject = "EV-store-1:tool-version:tool-version")

    // ---- provenance and integrity ------------------------------------------------------------------------------

    [<Fact>]
    let ``evidence with missing provenance is rejected`` () =
        let r = record inputs
        let variants =
            [ { r with Inputs = None }
              { r with EvidenceIdentity = None }
              { r with ProducedAtRevision = None }
              { r with Freshness = None }
              { r with Inputs = Some { inputs with ToolVersion = "" }; EvidenceIdentity = Some(identity { inputs with ToolVersion = "" }) }
              { r with Inputs = Some { inputs with TestContract = "" }; EvidenceIdentity = Some(identity { inputs with TestContract = "" }) }
              { r with Inputs = Some { inputs with Sources = Map.empty }; EvidenceIdentity = Some(identity { inputs with Sources = Map.empty }) }
              { r with ProducedAtRevision = Some "main" } ]
        for prior in variants do
            planFor ContentAddressed revB None inputs [ prior ] |> single |> assertRun "REUSE.PROVENANCE_MISSING"

    [<Fact>]
    let ``a tampered evidence identity is rejected`` () =
        let prior = { record inputs with EvidenceIdentity = Some(h 'f') }
        planFor ContentAddressed revB None inputs [ prior ] |> single |> assertRun "REUSE.IDENTITY_TAMPERED"

    [<Fact>]
    let ``a tampered recorded source digest is rejected even when it matches the current tree`` () =
        // Someone rewrites the record's inputs to look current, but the
        // identity was computed over the inputs that actually ran.
        let ran = { inputs with Sources = Map.add "src/Store.fs" (h '7') inputs.Sources }
        let forged = { record ran with Inputs = Some inputs }
        planFor ContentAddressed revB None inputs [ forged ] |> single |> assertRun "REUSE.IDENTITY_TAMPERED"

    // ---- freshness policy ---------------------------------------------------------------------------------------

    [<Fact>]
    let ``live evidence is never reused even with identical inputs`` () =
        let prior = { record inputs with Freshness = Some Live }
        for revision in [ revA; revB ] do
            planFor Live revision None inputs [ prior ] |> single |> assertRun "REUSE.LIVE_NEVER_REUSED"

    [<Fact>]
    let ``per-commit evidence is reused only at the commit that produced it`` () =
        let prior = { record inputs with Freshness = Some PerCommit }
        let same = planFor PerCommit revA None inputs [ prior ] |> single
        Assert.False(isRun same, $"%A{same.Reasons}")
        planFor PerCommit revB None inputs [ prior ] |> single |> assertRun "REUSE.PER_COMMIT_REVISION_CHANGED"

    [<Fact>]
    let ``a changed freshness policy refuses reuse`` () =
        let prior = { record inputs with Freshness = Some PerCommit }
        planFor ContentAddressed revA None inputs [ prior ] |> single |> assertRun "REUSE.FRESHNESS_CHANGED"

    [<Fact>]
    let ``failing evidence is not carried forward and contradicts a passing run`` () =
        let failed = { record inputs with EvidenceId = "EV-store-0"; Outcome = OutcomeFailed }
        planFor ContentAddressed revB None inputs [ failed ] |> single |> assertRun "REUSE.PRIOR_NOT_PASSED"
        planFor ContentAddressed revB None inputs [ failed; record inputs ] |> single |> assertRun "REUSE.CONFLICTING_PRIOR"

    [<Fact>]
    let ``no recorded evidence means run`` () =
        planFor ContentAddressed revB None inputs [] |> single |> assertRun "REUSE.NO_PRIOR_EVIDENCE"

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

    // ---- affected-test selection --------------------------------------------------------------------------------

    [<Fact>]
    let ``unknown mapping runs`` () =
        let unknown = { request ContentAddressed inputs with Patterns = None; Inputs = Error "no test-to-input mapping is declared" }
        let d = plan revB (Some [ "README.md" ]) [] [ "**/*.md" ] [ unknown ] [ record inputs ] |> single
        assertRun "REUSE.MAPPING_UNKNOWN" d
        Assert.Equal(None, d.PathAffected)

    [<Fact>]
    let ``a changed path inside the mapping selects the test even if identity looks unchanged`` () =
        let d = planFor ContentAddressed revB (Some [ "src/Store.fs" ]) inputs [ record inputs ] |> single
        assertRun "REUSE.PATH_AFFECTED" d
        Assert.Equal(Some true, d.PathAffected)

    [<Fact>]
    let ``an ignored change leaves an unchanged test reusing evidence`` () =
        let p = plan revB (Some [ "docs/notes.md" ]) [] [ "docs/**" ] [ request ContentAddressed inputs ] [ record inputs ]
        let d = single p
        Assert.False(isRun d, $"%A{d.Reasons}")
        Assert.Equal(Some false, d.PathAffected)
        Assert.Empty p.UnmappedChangedPaths

    [<Fact>]
    let ``a change no mapping covers runs every test`` () =
        let p = plan revB (Some [ "Directory.Build.props" ]) [] [ "docs/**" ] [ request ContentAddressed inputs ] [ record inputs ]
        Assert.Equal<string list>([ "Directory.Build.props" ], p.UnmappedChangedPaths)
        single p |> assertRun "REUSE.UNMAPPED_CHANGE"

    [<Fact>]
    let ``a global input change selects every mapped test`` () =
        let p = plan revB (Some [ "packages.lock.json" ]) [ "packages.lock.json" ] [] [ request ContentAddressed inputs ] [ record inputs ]
        single p |> assertRun "REUSE.PATH_AFFECTED"

    [<Fact>]
    let ``source drift without a matching changed path is flagged as an incomplete mapping`` () =
        let current = { inputs with Sources = Map.add "src/Store.fs" (h '9') inputs.Sources }
        let d = plan revB (Some [ "docs/x.md" ]) [] [ "docs/**" ] [ request ContentAddressed current ] [ record inputs ] |> single
        assertRun "REUSE.INPUT_CHANGED" d
        Assert.Contains("REUSE.MAPPING_INCOMPLETE", codes d)
        Assert.Equal(Some false, d.PathAffected)

    [<Fact>]
    let ``selection runs only the tests whose identity changed`` () =
        let other = { inputs with Sources = Map [ "src/View.fs", h '5' ] }
        let otherRecord = { record other with EvidenceId = "EV-view-1"; TestId = "view-render" }
        let otherRequest = { request ContentAddressed other with TestId = "view-render"; Patterns = Some [ "src/View.fs" ] }
        let changed = { inputs with Sources = Map.add "src/Store.fs" (h '9') inputs.Sources }
        let storeRequest = { request ContentAddressed changed with Patterns = Some [ "src/Store.fs"; "src/Model.fs" ] }
        let p = plan revB (Some [ "src/Store.fs" ]) [] [] [ storeRequest; otherRequest ] [ record inputs; otherRecord ]
        let byId = p.Tests |> List.map (fun t -> t.TestId, isRun t) |> Map.ofList
        Assert.True(byId["store-roundtrip"])
        Assert.False(byId["view-render"])

    [<Theory>]
    [<InlineData("src/**", "src/a/b.fs", true)>]
    [<InlineData("src/**", "srcx/a.fs", false)>]
    [<InlineData("src/*.fs", "src/a/b.fs", false)>]
    [<InlineData("**/*.md", "README.md", true)>]
    [<InlineData("**/*.md", "docs/x/y.md", true)>]
    [<InlineData("docs/", "docs/x/y.md", true)>]
    [<InlineData("a.fs", "a.fs", true)>]
    [<InlineData("a?fs", "a/fs", false)>]
    let ``glob matching`` (pattern: string, path: string, expected: bool) =
        Assert.Equal(expected, matches [ pattern ] path)

    // ---- wire: provenance written by a plan round-trips into a reuse ------------------------------------------

    [<Fact>]
    let ``a provenance record written from a plan is reused by the next plan`` () =
        let decision = planFor ContentAddressed revA None inputs [] |> single
        let written = EvidenceReuseWire.provenanceOf revA "EV-new" OutcomePassed decision |> Option.get
        let json = Contracts.serialize written
        let parsed =
            match Json.parse EvidenceReuseWire.provenanceRecord json with
            | Ok record -> record
            | Error e -> failwith $"%A{e}"
        let next = planFor ContentAddressed revB None inputs [ parsed ] |> single
        Assert.False(isRun next, $"%A{next.Reasons}")

    [<Fact>]
    let ``a provenance record without identity members is still read and then refused`` () =
        let json = """{ "SchemaVersion": "dokimos.evidence-provenance/1", "EvidenceId": "EV-x", "TestId": "store-roundtrip", "Outcome": "passed" }"""
        match Json.parse EvidenceReuseWire.provenanceRecord json with
        | Ok prior -> planFor ContentAddressed revB None inputs [ prior ] |> single |> assertRun "REUSE.PROVENANCE_MISSING"
        | Error e -> failwith $"%A{e}"

    [<Fact>]
    let ``a malformed recorded digest is refused at intake`` () =
        let json = """{ "SchemaVersion": "dokimos.evidence-provenance/1", "EvidenceId": "EV-x", "TestId": "t", "Outcome": "passed", "EvidenceIdentity": "sha256:zz" }"""
        Assert.True(Result.isError (Json.parse EvidenceReuseWire.provenanceRecord json))

    // ---- completion verification: reuse stays visible and policy-bound ----------------------------------------

    module Completion =
        open Dokimos.Core.CompletionVerification

        let private packet =
            { SchemaVersion = PacketSchemaVersion
              WorkItemId = "WORK-1"
              ClaimToken = "claim"
              FencingGeneration = 1L
              Commit = revB
              VerificationRevision = revB
              OrdoContractVersion = "ordo.constructibility-contract/1"
              OrdoContractDigest = h 'c'
              RequirementIds = [ "RQ-1" ]
              DecisionIds = []
              ConflictResolutionIds = []
              Validations = [ { Name = "tests"; Status = Passed; EvidenceIds = [ "EV-1" ] } ]
              Evidence = [ { Id = "EV-1"; Digest = h 'd'; Revision = revB } ] }

        let private authorityWith reusable provenance =
            { SchemaVersion = AuthoritySchemaVersion
              WorkItemId = "WORK-1"
              ClaimToken = "claim"
              FencingGeneration = 1L
              ExpectedCommit = revB
              ExpectedVerificationRevision = revB
              SupportedOrdoContractVersions = set [ packet.OrdoContractVersion ]
              ExpectedOrdoContractDigest = h 'c'
              RequiredRequirementIds = set [ "RQ-1" ]
              RequiredDecisionIds = Set.empty
              RequiredConflictResolutionIds = Set.empty
              RequiredValidationNames = set [ "tests" ]
              Evidence =
                [ { Id = "EV-1"; Digest = h 'd'; Revision = revB; Status = EvidencePassed; Reusable = reusable
                    ObservedAt = None; Validation = None; Attestation = None; Provenance = provenance } ]
              ConsumedPacketDigests = Set.empty
              ClaimAcquiredAt = None
              LeaseExpiresAt = None
              EvaluatedAt = None
              TrustedVerifierKeys = []
              ValidationPolicyDigest = None }

        let private carried freshness = Some { EvidenceIdentity = h 'e'; Freshness = freshness; ProducedAtRevision = revA }
        let private codesOf receipt = receipt.Diagnostics |> List.map _.Code |> Set.ofList

        [<Fact>]
        let ``carried content-addressed evidence verifies and is listed as reused`` () =
            let receipt = verify (authorityWith true (carried ContentAddressed)) packet
            Assert.Equal("verified", receipt.Verdict)
            Assert.Equal<string list>([ "EV-1" ], receipt.ReusedEvidenceIds)

        [<Fact>]
        let ``carried live evidence is refused`` () =
            let receipt = verify (authorityWith true (carried Live)) packet
            Assert.Equal("rejected", receipt.Verdict)
            Assert.Contains("DOK.COMPLETION.REUSE_LIVE_EVIDENCE", codesOf receipt)
            Assert.Empty receipt.ReusedEvidenceIds

        [<Fact>]
        let ``live evidence marked reusable at its own revision is refused`` () =
            let same = Some { EvidenceIdentity = h 'e'; Freshness = Live; ProducedAtRevision = revB }
            let receipt = verify (authorityWith true same) packet
            Assert.Contains("DOK.COMPLETION.REUSE_LIVE_EVIDENCE", codesOf receipt)
            let fresh = verify (authorityWith false same) packet
            Assert.Equal("verified", fresh.Verdict)
            Assert.Empty fresh.ReusedEvidenceIds

        [<Fact>]
        let ``carried per-commit evidence is refused`` () =
            let receipt = verify (authorityWith true (carried PerCommit)) packet
            Assert.Contains("DOK.COMPLETION.REUSE_STALE_COMMIT", codesOf receipt)

        [<Fact>]
        let ``carried evidence not declared reusable is refused`` () =
            let receipt = verify (authorityWith false (carried ContentAddressed)) packet
            Assert.Contains("DOK.COMPLETION.REUSE_UNDECLARED", codesOf receipt)
