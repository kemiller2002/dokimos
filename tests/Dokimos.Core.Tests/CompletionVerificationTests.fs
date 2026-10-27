namespace Dokimos.Core.Tests

open System
open System.Text.Json
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
                Status = EvidencePassed; Reusable = true; ObservedAt = None; Validation = None; Attestation = None; Provenance = None } ]
          ConsumedPacketDigests = Set.empty
          ClaimAcquiredAt = None
          LeaseExpiresAt = None
          EvaluatedAt = None
          TrustedVerifierKeys = []
          ValidationPolicyDigest = None }

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

    let private rejectedWith code receipt =
        has code receipt
        Assert.Equal("rejected", receipt.Verdict)
        Assert.Equal(4, receipt.ExitCode)

    let private observation = authority.Evidence.Head
    let private at (text: string) = Some(DateTimeOffset.Parse(text, Globalization.CultureInfo.InvariantCulture))

    /// One named negative case per independently established property.
    let private negativeCase name =
        match name with
        | "missing-packet-evidence" -> authority, { packet with Evidence = [] }
        | "missing-host-observation" -> { authority with Evidence = [] }, packet
        | "stale-commit" -> authority, { packet with Commit = String('e', 40) }
        | "replayed-claim" -> { authority with ConsumedPacketDigests = set [ packetDigest packet ] }, packet
        | "replayed-claim-uppercase-digest" ->
            let consumed = (packetDigest packet).ToUpperInvariant().Replace("SHA256:", "sha256:")
            { authority with ConsumedPacketDigests = set [ consumed ] }, packet
        | "malformed-consumed-digest" -> { authority with ConsumedPacketDigests = set [ "sha256:not-hex" ] }, packet
        | "wrong-work-item" -> authority, { packet with WorkItemId = "WORK-2" }
        | "stale-claim-token" -> authority, { packet with ClaimToken = "claim-old" }
        | "lower-fence" -> authority, { packet with FencingGeneration = 6L }
        | "higher-fence" -> authority, { packet with FencingGeneration = 8L }
        | "contract-version" -> authority, { packet with OrdoContractVersion = "ordo.constructibility-contract/2" }
        | "contract-digest" -> authority, { packet with OrdoContractDigest = digest 'e' }
        | "malformed-contract-digest" ->
            { authority with ExpectedOrdoContractDigest = "sha256:cc" }, { packet with OrdoContractDigest = "sha256:cc" }
        | "verification-revision" -> authority, { packet with VerificationRevision = String('e', 40) }
        | "claimant-failed-test" -> authority, { packet with Validations = [ { packet.Validations.Head with Status = Failed } ] }
        | "host-failed-test" -> { authority with Evidence = [ { observation with Status = EvidenceFailed } ] }, packet
        | "host-unavailable-test" -> { authority with Evidence = [ { observation with Status = EvidenceUnavailable } ] }, packet
        | "missing-required-test" -> authority, { packet with Validations = [] }
        | "evidence-other-revision" ->
            { authority with Evidence = [ { observation with Revision = String('e', 40) } ] },
            { packet with Evidence = [ { packet.Evidence.Head with Revision = String('e', 40) } ] }
        | "observation-other-revision" -> { authority with Evidence = [ { observation with Revision = String('e', 40) } ] }, packet
        | "forged-evidence-digest" -> authority, { packet with Evidence = [ { packet.Evidence.Head with Digest = digest 'f' } ] }
        | "malformed-evidence-digest" ->
            { authority with Evidence = [ { observation with Digest = "sha256:dd" } ] },
            { packet with Evidence = [ { packet.Evidence.Head with Digest = "sha256:dd" } ] }
        | "evidence-before-claim" ->
            { authority with
                ClaimAcquiredAt = at "2026-10-19T10:00:00Z"
                Evidence = [ { observation with Reusable = false; ObservedAt = at "2026-10-19T09:59:59Z" } ] }, packet
        | "evidence-after-lease" ->
            { authority with
                ClaimAcquiredAt = at "2026-10-19T10:00:00Z"
                LeaseExpiresAt = at "2026-10-19T11:00:00Z"
                EvaluatedAt = at "2026-10-19T10:59:00Z"
                Evidence = [ { observation with ObservedAt = at "2026-10-19T11:00:00Z" } ] }, packet
        | "expired-lease" ->
            { authority with
                ClaimAcquiredAt = at "2026-10-19T10:00:00Z"
                LeaseExpiresAt = at "2026-10-19T11:00:00Z"
                EvaluatedAt = at "2026-10-19T11:00:00Z"
                Evidence = [ { observation with ObservedAt = at "2026-10-19T10:30:00Z" } ] }, packet
        | "lease-without-evaluation" ->
            { authority with
                LeaseExpiresAt = at "2026-10-19T11:00:00Z"
                Evidence = [ { observation with ObservedAt = at "2026-10-19T10:30:00Z" } ] }, packet
        | "window-without-observed-at" -> { authority with ClaimAcquiredAt = at "2026-10-19T10:00:00Z" }, packet
        | "inverted-window" ->
            { authority with
                ClaimAcquiredAt = at "2026-10-19T11:00:00Z"
                LeaseExpiresAt = at "2026-10-19T10:00:00Z"
                EvaluatedAt = at "2026-10-19T09:00:00Z" }, packet
        | "evidence-for-other-validation" -> { authority with Evidence = [ { observation with Validation = Some "lint" } ] }, packet
        | "validation-cites-unbound-evidence" ->
            let bound = { observation with Id = "EV-2"; Digest = digest 'e'; Validation = Some "tests" }
            { authority with Evidence = [ observation; bound ] },
            { packet with Evidence = packet.Evidence @ [ { Id = "EV-2"; Digest = digest 'e'; Revision = verificationRevision } ] }
        | other -> failwith $"unknown case {other}"

    [<Theory>]
    [<InlineData("missing-packet-evidence", "DOK.COMPLETION.MISSING_EVIDENCE_CLAIM")>]
    [<InlineData("missing-host-observation", "DOK.COMPLETION.EVIDENCE_UNOBSERVED")>]
    [<InlineData("stale-commit", "DOK.COMPLETION.STALE_COMMIT")>]
    [<InlineData("replayed-claim", "DOK.COMPLETION.REPLAYED_PACKET")>]
    [<InlineData("replayed-claim-uppercase-digest", "DOK.COMPLETION.REPLAYED_PACKET")>]
    [<InlineData("malformed-consumed-digest", "DOK.COMPLETION.INVALID_CONSUMED_PACKET_DIGEST")>]
    [<InlineData("wrong-work-item", "DOK.COMPLETION.WORK_ITEM_MISMATCH")>]
    [<InlineData("stale-claim-token", "DOK.COMPLETION.STALE_CLAIM")>]
    [<InlineData("lower-fence", "DOK.COMPLETION.STALE_FENCE")>]
    [<InlineData("higher-fence", "DOK.COMPLETION.STALE_FENCE")>]
    [<InlineData("contract-version", "DOK.COMPLETION.UNSUPPORTED_ORDO_CONTRACT")>]
    [<InlineData("contract-digest", "DOK.COMPLETION.ORDO_CONTRACT_DIGEST_MISMATCH")>]
    [<InlineData("malformed-contract-digest", "DOK.COMPLETION.INVALID_ORDO_CONTRACT_DIGEST")>]
    [<InlineData("verification-revision", "DOK.COMPLETION.STALE_VERIFICATION")>]
    [<InlineData("claimant-failed-test", "DOK.COMPLETION.VALIDATION_FAILED")>]
    [<InlineData("host-failed-test", "DOK.COMPLETION.EVIDENCE_FAILED")>]
    [<InlineData("host-unavailable-test", "DOK.COMPLETION.EVIDENCE_UNAVAILABLE")>]
    [<InlineData("missing-required-test", "DOK.COMPLETION.MISSING_VALIDATION")>]
    [<InlineData("evidence-other-revision", "DOK.COMPLETION.STALE_EVIDENCE")>]
    [<InlineData("observation-other-revision", "DOK.COMPLETION.EVIDENCE_REVISION_MISMATCH")>]
    [<InlineData("forged-evidence-digest", "DOK.COMPLETION.EVIDENCE_DIGEST_MISMATCH")>]
    [<InlineData("malformed-evidence-digest", "DOK.COMPLETION.INVALID_EVIDENCE_DIGEST")>]
    [<InlineData("evidence-before-claim", "DOK.COMPLETION.EVIDENCE_STALE")>]
    [<InlineData("evidence-after-lease", "DOK.COMPLETION.EVIDENCE_STALE")>]
    [<InlineData("expired-lease", "DOK.COMPLETION.STALE_LEASE")>]
    [<InlineData("lease-without-evaluation", "DOK.COMPLETION.FRESHNESS_UNDETERMINED")>]
    [<InlineData("window-without-observed-at", "DOK.COMPLETION.FRESHNESS_UNDETERMINED")>]
    [<InlineData("inverted-window", "DOK.COMPLETION.INVALID_FRESHNESS_WINDOW")>]
    [<InlineData("evidence-for-other-validation", "DOK.COMPLETION.EVIDENCE_VALIDATION_MISMATCH")>]
    [<InlineData("validation-cites-unbound-evidence", "DOK.COMPLETION.VALIDATION_UNBOUND")>]
    let ``each independently established property fails closed with its specific code`` (case: string) (code: string) =
        let caseAuthority, casePacket = negativeCase case
        verify caseAuthority casePacket |> rejectedWith code

    [<Fact>]
    let ``fresh in-lease evidence and revision-matched reuse from before the claim both verify`` () =
        let windowed =
            { authority with
                ClaimAcquiredAt = at "2026-10-19T10:00:00Z"
                LeaseExpiresAt = at "2026-10-19T11:00:00Z"
                EvaluatedAt = at "2026-10-19T10:45:00Z"
                Evidence = [ { observation with Reusable = false; ObservedAt = at "2026-10-19T10:30:00+00:00"; Validation = Some "tests" } ] }
        let fresh = verify windowed packet
        Assert.Equal("verified", fresh.Verdict)
        Assert.Empty(fresh.Diagnostics)
        let reused =
            verify { windowed with Evidence = [ { observation with Reusable = true; ObservedAt = at "2026-10-18T08:00:00Z" } ] } packet
        Assert.Equal("verified", reused.Verdict)
        Assert.Equal<string list>([ "EV-1" ], reused.ReusedEvidenceIds)

    [<Fact>]
    let ``receipts are byte-stable for equivalent inputs and carry the replay digest`` () =
        let second =
            { packet with
                RequirementIds = List.rev packet.RequirementIds
                Evidence = List.rev packet.Evidence }
        let failing = { packet with Commit = String('e', 40); FencingGeneration = 8L; WorkItemId = "WORK-2" }
        let render authority packet = verify authority packet |> CompletionWire.toWire |> Contracts.serialize
        Assert.Equal(render authority packet, render authority second)
        Assert.Equal(render authority failing, render authority { failing with RequirementIds = List.rev failing.RequirementIds })
        Assert.Contains(packetDigest packet, render authority packet)
        let replay = { authority with ConsumedPacketDigests = set [ (verify authority packet).PacketDigest ] }
        verify replay second |> rejectedWith "DOK.COMPLETION.REPLAYED_PACKET"

    /// A v1 authority document with optional top-level and observation members.
    let private authorityJson (observationMembers: (string * (Nodes.JsonNode | null)) list) (members: (string * (Nodes.JsonNode | null)) list) =
        let observationNode = Nodes.JsonObject()
        observationNode["Id"] <- Nodes.JsonValue.Create "EV-1"
        observationNode["Digest"] <- Nodes.JsonValue.Create(digest 'd')
        observationNode["Revision"] <- Nodes.JsonValue.Create verificationRevision
        observationNode["Status"] <- Nodes.JsonValue.Create "passed"
        observationNode["Reusable"] <- Nodes.JsonValue.Create true
        for name, value in observationMembers do observationNode[name] <- value
        let empty () = Nodes.JsonArray() :> Nodes.JsonNode
        let root = Nodes.JsonObject()
        root["SchemaVersion"] <- Nodes.JsonValue.Create AuthoritySchemaVersion
        root["WorkItemId"] <- Nodes.JsonValue.Create "WORK-1"
        root["ClaimToken"] <- Nodes.JsonValue.Create "claim-current"
        root["FencingGeneration"] <- Nodes.JsonValue.Create 7L
        root["ExpectedCommit"] <- Nodes.JsonValue.Create commit
        root["ExpectedVerificationRevision"] <- Nodes.JsonValue.Create verificationRevision
        root["SupportedOrdoContractVersions"] <- Nodes.JsonArray(Nodes.JsonValue.Create "ordo.constructibility-contract/1")
        root["ExpectedOrdoContractDigest"] <- Nodes.JsonValue.Create(digest 'c')
        root["RequiredRequirementIds"] <- empty ()
        root["RequiredDecisionIds"] <- empty ()
        root["RequiredConflictResolutionIds"] <- empty ()
        root["RequiredValidationNames"] <- empty ()
        root["Evidence"] <- Nodes.JsonArray(observationNode)
        root["ConsumedPacketDigests"] <- empty ()
        for name, value in members do root[name] <- value
        root.ToJsonString()

    let private text (value: string) : Nodes.JsonNode | null = Nodes.JsonValue.Create value

    [<Fact>]
    let ``v1 authority without optional members reads unchanged and optional members are strict`` () =
        match CompletionWire.readAuthority (authorityJson [] []) with
        | Ok value ->
            Assert.Equal(None, value.ClaimAcquiredAt)
            Assert.Equal(None, value.LeaseExpiresAt)
            Assert.Equal(None, value.EvaluatedAt)
            Assert.Equal(None, value.Evidence.Head.ObservedAt)
            Assert.Equal(None, value.Evidence.Head.Validation)
        | Error problem -> failwith problem
        let accepted =
            authorityJson
                [ "ObservedAt", text "2026-10-19T10:30:00Z"; "Validation", text "tests" ]
                [ "ClaimAcquiredAt", text "2026-10-19T10:00:00+02:00"
                  "LeaseExpiresAt", text "2026-10-19T11:00:00.5Z"
                  "EvaluatedAt", text "2026-10-19T10:45:00Z" ]
        match CompletionWire.readAuthority accepted with
        | Ok value -> Assert.Equal(DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), value.ClaimAcquiredAt.Value)
        | Error problem -> failwith problem
        let refused =
            [ authorityJson [] [ "Unexpected", text "x" ]
              authorityJson [ "Unexpected", text "x" ] []
              authorityJson [] [ "EvaluatedAt", null ]
              authorityJson [] [ "EvaluatedAt", text "2026-10-19T10:45:00" ]
              authorityJson [] [ "EvaluatedAt", text "2026-10-19" ]
              authorityJson [ "ObservedAt", text "yesterday" ] []
              authorityJson [ "Validation", text "" ] []
              authorityJson [ "Status", text "Passed" ] []
              authorityJson [ "Status", text "skipped" ] []
              authorityJson [ "Reusable", text "true" ] [] ]
        for json in refused do
            Assert.True(CompletionWire.readAuthority json |> Result.isError, json)
