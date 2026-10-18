module Dokimos.Cli.Tests.CompletionVerifyCliTests

open System
open System.Text.Json
open Xunit
open Dokimos.Cli.Tests.Support

let private commit = String('a', 40)
let private revision = String('b', 40)
let private digest character = "sha256:" + String(character, 64)

let private packet evidenceDigest =
    JsonSerializer.Serialize(
        {| SchemaVersion = "praxis.completion-packet/1"
           WorkItemId = "DOKIMOS-REL-001"
           ClaimToken = "claim-current"
           FencingGeneration = 7L
           Commit = commit
           VerificationRevision = revision
           OrdoContractVersion = "ordo.constructibility-contract/1"
           OrdoContractDigest = digest 'c'
           RequirementIds = [ "DOK-REL-001"; "DOK-REL-002" ]
           DecisionIds = [ "DF-REL-001" ]
           ConflictResolutionIds = [ "CONFLICT-REL-001" ]
           Validations =
            [ {| Name = "unit-tests"
                 Status = "passed"
                 EvidenceIds = [ "EV-1" ] |} ]
           Evidence =
            [ {| Id = "EV-1"
                 Digest = evidenceDigest
                 Revision = revision |} ] |})

let private authority consumed =
    JsonSerializer.Serialize(
        {| SchemaVersion = "dokimos.completion-authority/1"
           WorkItemId = "DOKIMOS-REL-001"
           ClaimToken = "claim-current"
           FencingGeneration = 7L
           ExpectedCommit = commit
           ExpectedVerificationRevision = revision
           SupportedOrdoContractVersions = [ "ordo.constructibility-contract/1" ]
           ExpectedOrdoContractDigest = digest 'c'
           RequiredRequirementIds = [ "DOK-REL-001"; "DOK-REL-002" ]
           RequiredDecisionIds = [ "DF-REL-001" ]
           RequiredConflictResolutionIds = [ "CONFLICT-REL-001" ]
           RequiredValidationNames = [ "unit-tests" ]
           Evidence =
            [ {| Id = "EV-1"
                 Digest = digest 'd'
                 Revision = revision
                 Status = "passed"
                 Reusable = true |} ]
           ConsumedPacketDigests = consumed |})

let private invoke packetJson authorityJson =
    let directory = tempDirectory ()
    let packetPath = write directory "packet.json" packetJson
    let authorityPath = write directory "authority.json" authorityJson
    execute [ "completion"; "verify"; "--packet"; packetPath; "--authority"; authorityPath ]

[<Fact>]
let ``CLI emits a schema-valid compact receipt for independently verified completion`` () =
    let packetJson = packet (digest 'd')
    let authorityJson = authority []
    assertSchemaValid "praxis-completion-packet.schema.json" packetJson
    assertSchemaValid "dokimos-completion-authority.schema.json" authorityJson
    let result = invoke packetJson authorityJson
    Assert.Equal(0, result.ExitCode)
    Assert.Equal("", result.Stderr)
    assertSchemaValid "dokimos-completion-verification.schema.json" result.Stdout
    use document = JsonDocument.Parse result.Stdout
    let receipt = document.RootElement
    Assert.Equal("verified", receipt.GetProperty("Verdict").GetString())
    Assert.Equal("DOKIMOS-REL-001", receipt.GetProperty("WorkItemId").GetString())
    Assert.Equal(1, receipt.GetProperty("ReusedEvidenceIds").GetArrayLength())
    Assert.Equal(0, receipt.GetProperty("Diagnostics").GetArrayLength())

[<Fact>]
let ``CLI refuses forged evidence with stable machine-readable diagnostics`` () =
    let result = invoke (packet (digest 'f')) (authority [])
    Assert.Equal(4, result.ExitCode)
    assertSchemaValid "dokimos-completion-verification.schema.json" result.Stdout
    Assert.Contains("DOK.COMPLETION.EVIDENCE_DIGEST_MISMATCH", result.Stdout)

[<Fact>]
let ``CLI refuses strict-wire violations and unavailable inputs`` () =
    let valid = packet (digest 'd')
    let extra = "{\"Unexpected\":true," + valid.Substring(1)
    let invalid = invoke extra (authority [])
    Assert.Equal(3, invalid.ExitCode)
    Assert.Contains("unknown JSON member", invalid.Stderr)

    let missing = execute [ "completion"; "verify"; "--packet"; "/not/present/packet.json"; "--authority"; "/not/present/authority.json" ]
    Assert.Equal(3, missing.ExitCode)
    Assert.Contains("completion-packet-unavailable", missing.Stderr)
