namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core
open Dokimos.Core.CompletionVerification

module CompletionVerificationTests =
    let private digest value = "sha256:" + String(value, 64)
    let private commit = String('a', 40)
    let private verificationRevision = String('b', 40)

    let private packet =
        { SchemaVersion = PacketSchemaVersion
          WorkItemId = "WORK-1"
          ClaimToken = "claim-current"
          FencingGeneration = 7L
          Commit = commit
          VerificationRevision = verificationRevision
          OrdoContractVersion = "ordo.constructibility-contract/1"
          OrdoContractDigest = digest 'c'
          RequirementIds = [ "RQ-2"; "RQ-1" ]
          DecisionIds = [ "DEC-1" ]
          ConflictResolutionIds = [ "RESOLVE-1" ]
          Validations = [ { Name = "tests"; Status = Passed; EvidenceIds = [ "EV-1" ] } ]
          Evidence = [ { Id = "EV-1"; Digest = digest 'd'; Revision = verificationRevision } ] }

    let private authority =
        { SchemaVersion = AuthoritySchemaVersion
          WorkItemId = packet.WorkItemId
          ClaimToken = packet.ClaimToken
          FencingGeneration = packet.FencingGeneration
          ExpectedCommit = packet.Commit
          ExpectedVerificationRevision = packet.VerificationRevision
          SupportedOrdoContractVersions = set [ packet.OrdoContractVersion ]
          ExpectedOrdoContractDigest = packet.OrdoContractDigest
          RequiredRequirementIds = set packet.RequirementIds
          RequiredDecisionIds = set packet.DecisionIds
          RequiredConflictResolutionIds = set packet.ConflictResolutionIds
          RequiredValidationNames = set [ "tests" ]
          Evidence =
            [ { Id = "EV-1"; Digest = digest 'd'; Revision = verificationRevision
                Status = EvidencePassed; Reusable = true } ]
          ConsumedPacketDigests = Set.empty }

    let private codes receipt = receipt.Diagnostics |> List.map _.Code |> Set.ofList
    let private has code receipt = Assert.Contains(code, codes receipt)

    [<Fact>]
    let ``exact independently observed completion produces a compact reusable receipt`` () =
        let receipt = verify authority packet
        Assert.Equal("verified", receipt.Verdict)
        Assert.Equal(0, receipt.ExitCode)
        Assert.Empty(receipt.Diagnostics)
        Assert.Equal<string list>([ digest 'd' ], receipt.EvidenceDigests)
        Assert.Equal<string list>([ "EV-1" ], receipt.ReusedEvidenceIds)
        Assert.Equal(2, receipt.RequirementCount)
        Assert.Equal(packetDigest packet, receipt.PacketDigest)

    [<Fact>]
    let ``packet digest is canonical but retains duplicate semantics`` () =
        let reordered =
            { packet with
                RequirementIds = List.rev packet.RequirementIds
                Validations = packet.Validations |> List.map (fun item -> { item with EvidenceIds = List.rev item.EvidenceIds })
                Evidence = List.rev packet.Evidence }
        Assert.Equal(packetDigest packet, packetDigest reordered)
        Assert.NotEqual<string>(packetDigest packet, packetDigest { packet with RequirementIds = "RQ-1" :: packet.RequirementIds })

    [<Fact>]
    let ``stale and concurrent claimants cannot complete`` () =
        let staleClaim = verify authority { packet with ClaimToken = "claim-old" }
        has "DOK.COMPLETION.STALE_CLAIM" staleClaim
        let staleFence = verify authority { packet with FencingGeneration = 6L }
        has "DOK.COMPLETION.STALE_FENCE" staleFence
        let invalidFence = verify { authority with FencingGeneration = -1L } { packet with FencingGeneration = -1L }
        has "DOK.COMPLETION.INVALID_FENCE" invalidFence
        let staleCommit = verify authority { packet with Commit = String('e', 40) }
        has "DOK.COMPLETION.STALE_COMMIT" staleCommit

    [<Fact>]
    let ``obsolete and forged evidence fails closed`` () =
        let stale =
            { packet with Evidence = [ { packet.Evidence.Head with Revision = String('e', 40) } ] }
            |> verify authority
        has "DOK.COMPLETION.STALE_EVIDENCE" stale
        has "DOK.COMPLETION.EVIDENCE_REVISION_MISMATCH" stale

        let forged =
            { packet with Evidence = [ { packet.Evidence.Head with Digest = digest 'f' } ] }
            |> verify authority
        has "DOK.COMPLETION.EVIDENCE_DIGEST_MISMATCH" forged

        let missing =
            { packet with Evidence = [] }
            |> verify authority
        has "DOK.COMPLETION.MISSING_EVIDENCE_CLAIM" missing

    [<Fact>]
    let ``unsupported Ordo contracts and incorrect decision references are refused`` () =
        verify authority { packet with OrdoContractVersion = "ordo.constructibility-contract/2" }
        |> has "DOK.COMPLETION.UNSUPPORTED_ORDO_CONTRACT"
        verify authority { packet with DecisionIds = [ "DEC-FORGED" ] }
        |> fun receipt ->
            has "DOK.COMPLETION.MISSING_DECISION" receipt
            has "DOK.COMPLETION.UNKNOWN_DECISION" receipt
        verify authority { packet with ConflictResolutionIds = [] }
        |> has "DOK.COMPLETION.MISSING_CONFLICT_RESOLUTION"

    [<Fact>]
    let ``failed unavailable and unobserved validations never pass`` () =
        verify authority { packet with Validations = [ { packet.Validations.Head with Status = Failed } ] }
        |> has "DOK.COMPLETION.VALIDATION_FAILED"
        verify { authority with Evidence = [] } packet
        |> has "DOK.COMPLETION.EVIDENCE_UNOBSERVED"
        let failedObservation =
            { authority with Evidence = [ { authority.Evidence.Head with Status = EvidenceFailed } ] }
        verify failedObservation packet |> has "DOK.COMPLETION.EVIDENCE_FAILED"

    [<Fact>]
    let ``an already consumed packet is rejected as a replay`` () =
        let replayAuthority = { authority with ConsumedPacketDigests = set [ packetDigest packet ] }
        verify replayAuthority packet |> has "DOK.COMPLETION.REPLAYED_PACKET"

    [<Fact>]
    let ``wire intake is strict and receipt projection remains machine readable`` () =
        let invalid = """{"SchemaVersion":"praxis.completion-packet/1","SchemaVersion":"praxis.completion-packet/1"}"""
        Assert.True(CompletionWire.readPacket invalid |> Result.isError)
        let json = CompletionWire.toWire (verify authority packet) |> Contracts.serialize
        Assert.Contains("dokimos.completion-verification", json)
        Assert.Contains("\"Diagnostics\"", json)
