module Dokimos.Cli.Tests.CompletionVerifyCliTests

open System
open System.Text.Json
open System.Text.Json.Nodes
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

let private edit (json: string) (change: JsonObject -> unit) =
    match JsonNode.Parse json with
    | :? JsonObject as root ->
        change root
        root.ToJsonString()
    | _ -> failwith "expected a JSON object"

let private node (value: obj) : JsonNode | null = JsonSerializer.SerializeToNode value

let private first (root: JsonObject) (name: string) =
    match root[name] with
    | :? JsonArray as items ->
        match items[0] with
        | :? JsonObject as item -> item
        | _ -> failwith "expected object item"
    | _ -> failwith "expected array"

let private validPacket () = packet (digest 'd')
let private validAuthority () = authority []

/// Each case: (packet, authority) documents for one independently established property.
let private cliCase name =
    let p = validPacket ()
    let a = validAuthority ()
    let onPacket change = edit p change, a
    let onAuthority change = p, edit a change
    match name with
    | "missing-packet-evidence" -> onPacket (fun root -> root["Evidence"] <- JsonArray())
    | "missing-host-observation" -> onAuthority (fun root -> root["Evidence"] <- JsonArray())
    | "stale-commit" -> onPacket (fun root -> root["Commit"] <- node (String('e', 40)))
    | "wrong-work-item" -> onPacket (fun root -> root["WorkItemId"] <- node "DOKIMOS-REL-002")
    | "lower-fence" -> onPacket (fun root -> root["FencingGeneration"] <- node 6L)
    | "higher-fence" -> onPacket (fun root -> root["FencingGeneration"] <- node 8L)
    | "contract-version" -> onPacket (fun root -> root["OrdoContractVersion"] <- node "ordo.constructibility-contract/2")
    | "contract-digest" -> onPacket (fun root -> root["OrdoContractDigest"] <- node (digest 'e'))
    | "verification-revision" -> onPacket (fun root -> root["VerificationRevision"] <- node (String('e', 40)))
    | "host-failed-test" -> onAuthority (fun root -> (first root "Evidence")["Status"] <- node "failed")
    | "claimant-failed-test" -> onPacket (fun root -> (first root "Validations")["Status"] <- node "failed")
    | "missing-required-test" -> onPacket (fun root -> root["Validations"] <- JsonArray())
    | "evidence-other-revision" ->
        edit p (fun root -> (first root "Evidence")["Revision"] <- node (String('e', 40))),
        edit a (fun root -> (first root "Evidence")["Revision"] <- node (String('e', 40)))
    | "malformed-evidence-digest" ->
        edit p (fun root -> (first root "Evidence")["Digest"] <- node "sha256:dd"),
        edit a (fun root -> (first root "Evidence")["Digest"] <- node "sha256:dd")
    | "malformed-consumed-digest" -> p, authority [ "sha256:dd" ]
    | "evidence-before-claim" ->
        onAuthority (fun root ->
            root["ClaimAcquiredAt"] <- node "2026-10-19T10:00:00Z"
            (first root "Evidence")["Reusable"] <- node false
            (first root "Evidence")["ObservedAt"] <- node "2026-10-19T09:00:00Z")
    | "expired-lease" ->
        onAuthority (fun root ->
            root["ClaimAcquiredAt"] <- node "2026-10-19T10:00:00Z"
            root["LeaseExpiresAt"] <- node "2026-10-19T11:00:00Z"
            root["EvaluatedAt"] <- node "2026-10-19T11:30:00Z"
            (first root "Evidence")["ObservedAt"] <- node "2026-10-19T10:30:00Z")
    | "malformed-commit" ->
        edit p (fun root -> root["Commit"] <- node "abc"), edit a (fun root -> root["ExpectedCommit"] <- node "abc")
    | "evidence-for-other-validation" -> onAuthority (fun root -> (first root "Evidence")["Validation"] <- node "lint")
    | other -> failwith $"unknown case {other}"

[<Theory>]
[<InlineData("missing-packet-evidence", "DOK.COMPLETION.MISSING_EVIDENCE_CLAIM")>]
[<InlineData("missing-host-observation", "DOK.COMPLETION.EVIDENCE_UNOBSERVED")>]
[<InlineData("stale-commit", "DOK.COMPLETION.STALE_COMMIT")>]
[<InlineData("wrong-work-item", "DOK.COMPLETION.WORK_ITEM_MISMATCH")>]
[<InlineData("lower-fence", "DOK.COMPLETION.STALE_FENCE")>]
[<InlineData("higher-fence", "DOK.COMPLETION.STALE_FENCE")>]
[<InlineData("contract-version", "DOK.COMPLETION.UNSUPPORTED_ORDO_CONTRACT")>]
[<InlineData("contract-digest", "DOK.COMPLETION.ORDO_CONTRACT_DIGEST_MISMATCH")>]
[<InlineData("verification-revision", "DOK.COMPLETION.STALE_VERIFICATION")>]
[<InlineData("host-failed-test", "DOK.COMPLETION.EVIDENCE_FAILED")>]
[<InlineData("claimant-failed-test", "DOK.COMPLETION.VALIDATION_FAILED")>]
[<InlineData("missing-required-test", "DOK.COMPLETION.MISSING_VALIDATION")>]
[<InlineData("evidence-other-revision", "DOK.COMPLETION.STALE_EVIDENCE")>]
[<InlineData("malformed-evidence-digest", "DOK.COMPLETION.INVALID_EVIDENCE_DIGEST")>]
[<InlineData("malformed-consumed-digest", "DOK.COMPLETION.INVALID_CONSUMED_PACKET_DIGEST")>]
[<InlineData("evidence-before-claim", "DOK.COMPLETION.EVIDENCE_STALE")>]
[<InlineData("expired-lease", "DOK.COMPLETION.STALE_LEASE")>]
[<InlineData("evidence-for-other-validation", "DOK.COMPLETION.EVIDENCE_VALIDATION_MISMATCH")>]
[<InlineData("malformed-commit", "DOK.COMPLETION.INVALID_COMMIT")>]
let ``CLI rejects each negative completion case with its specific code and exit 4`` (case: string) (code: string) =
    let packetJson, authorityJson = cliCase case
    let result = invoke packetJson authorityJson
    Assert.Equal(4, result.ExitCode)
    assertSchemaValid "dokimos-completion-verification.schema.json" result.Stdout
    use document = JsonDocument.Parse result.Stdout
    Assert.Equal("rejected", document.RootElement.GetProperty("Verdict").GetString())
    let codes =
        document.RootElement.GetProperty("Diagnostics").EnumerateArray()
        |> Seq.map (fun item -> item.GetProperty("Code").GetString() |> string)
        |> Set.ofSeq
    Assert.Contains(code, codes)

[<Fact>]
let ``CLI replay protection uses the receipt packet digest regardless of hex case`` () =
    let packetJson = validPacket ()
    let first = invoke packetJson (validAuthority ())
    Assert.Equal(0, first.ExitCode)
    use document = JsonDocument.Parse first.Stdout
    let recorded = document.RootElement.GetProperty("PacketDigest").GetString() |> string
    for consumed in [ recorded; "sha256:" + recorded.Substring(7).ToUpperInvariant() ] do
        let replay = invoke packetJson (authority [ consumed ])
        Assert.Equal(4, replay.ExitCode)
        Assert.Contains("DOK.COMPLETION.REPLAYED_PACKET", replay.Stdout)

[<Fact>]
let ``CLI receipts are byte-stable for equivalent inputs`` () =
    let packetJson = validPacket ()
    let reordered =
        edit packetJson (fun root ->
            let requirements = root["RequirementIds"]
            root.Remove "RequirementIds" |> ignore
            root["RequirementIds"] <- JsonArray(node "DOK-REL-002", node "DOK-REL-001")
            ignore requirements)
    let one = invoke packetJson (validAuthority ())
    let two = invoke reordered (validAuthority ())
    Assert.Equal(0, one.ExitCode)
    Assert.Equal(one.Stdout, two.Stdout)
    let rejectedOne = invoke (packet (digest 'f')) (validAuthority ())
    let rejectedTwo = invoke (packet (digest 'f')) (validAuthority ())
    Assert.Equal(rejectedOne.Stdout, rejectedTwo.Stdout)

[<Fact>]
let ``CLI accepts the additive freshness members when evidence is fresh`` () =
    let authorityJson =
        edit (validAuthority ()) (fun root ->
            root["ClaimAcquiredAt"] <- node "2026-10-19T10:00:00Z"
            root["LeaseExpiresAt"] <- node "2026-10-19T11:00:00Z"
            root["EvaluatedAt"] <- node "2026-10-19T10:45:00Z"
            (first root "Evidence")["ObservedAt"] <- node "2026-10-19T10:30:00Z"
            (first root "Evidence")["Validation"] <- node "unit-tests")
    assertSchemaValid "dokimos-completion-authority.schema.json" authorityJson
    let result = invoke (validPacket ()) authorityJson
    Assert.Equal(0, result.ExitCode)
    assertSchemaValid "dokimos-completion-verification.schema.json" result.Stdout

[<Theory>]
[<InlineData("packet-unknown-member", "completion-packet-invalid")>]
[<InlineData("packet-nested-unknown-member", "completion-packet-invalid")>]
[<InlineData("authority-unknown-member", "completion-authority-invalid")>]
[<InlineData("observation-unknown-member", "completion-authority-invalid")>]
[<InlineData("unknown-validation-status", "completion-packet-invalid")>]
[<InlineData("unknown-observation-status", "completion-authority-invalid")>]
[<InlineData("offsetless-timestamp", "completion-authority-invalid")>]
[<InlineData("null-optional-member", "completion-authority-invalid")>]
[<InlineData("malformed-json", "completion-packet-invalid")>]
[<InlineData("fractional-fence", "completion-packet-invalid")>]
let ``CLI refuses malformed inputs with exit 3 and no receipt`` (case: string) (code: string) =
    let p = validPacket ()
    let a = validAuthority ()
    let packetJson, authorityJson =
        match case with
        | "packet-unknown-member" -> edit p (fun root -> root["Unexpected"] <- node true), a
        | "packet-nested-unknown-member" -> edit p (fun root -> (first root "Evidence")["Unexpected"] <- node true), a
        | "authority-unknown-member" -> p, edit a (fun root -> root["Unexpected"] <- node true)
        | "observation-unknown-member" -> p, edit a (fun root -> (first root "Evidence")["Unexpected"] <- node true)
        | "unknown-validation-status" -> edit p (fun root -> (first root "Validations")["Status"] <- node "skipped"), a
        | "unknown-observation-status" -> p, edit a (fun root -> (first root "Evidence")["Status"] <- node "Passed")
        | "offsetless-timestamp" -> p, edit a (fun root -> root["EvaluatedAt"] <- node "2026-10-19T10:45:00")
        | "null-optional-member" -> p, edit a (fun root -> root["LeaseExpiresAt"] <- null)
        | "malformed-json" -> p.Substring(0, p.Length - 1), a
        | "fractional-fence" -> edit p (fun root -> root["FencingGeneration"] <- node 7.5), a
        | other -> failwith $"unknown case {other}"
    let result = invoke packetJson authorityJson
    Assert.Equal(3, result.ExitCode)
    Assert.Equal("", result.Stdout)
    Assert.Contains(code, result.Stderr)

[<Fact>]
let ``CLI fails closed when an input path is not a readable file`` () =
    let directory = tempDirectory ()
    let authorityPath = write directory "authority.json" (validAuthority ())
    let result = execute [ "completion"; "verify"; "--packet"; directory; "--authority"; authorityPath ]
    Assert.Equal(3, result.ExitCode)
    Assert.Equal("", result.Stdout)
    Assert.Contains("completion-packet-unavailable", result.Stderr)
