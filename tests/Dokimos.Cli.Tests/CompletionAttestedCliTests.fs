module Dokimos.Cli.Tests.CompletionAttestedCliTests

open System
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Dokimos.Cli.Tests.Support

let private revision = String('b', 40)
let private digest character = "sha256:" + String(character, 64)

let private packet evidenceDigest =
    JsonSerializer.Serialize(
        {| SchemaVersion = "praxis.completion-packet/1"
           WorkItemId = "DOKIMOS-REL-001"
           ClaimToken = "claim-current"
           FencingGeneration = 7L
           Commit = revision
           VerificationRevision = revision
           OrdoContractVersion = "ordo.constructibility-contract/1"
           OrdoContractDigest = digest 'c'
           RequirementIds = [ "DOK-REL-001" ]
           DecisionIds = List.empty<string>
           ConflictResolutionIds = List.empty<string>
           Validations = [ {| Name = "unit-tests"; Status = "passed"; EvidenceIds = [ "EV-1" ] |} ]
           Evidence = [ {| Id = "EV-1"; Digest = evidenceDigest; Revision = revision |} ] |})

let private authority () =
    JsonSerializer.Serialize(
        {| SchemaVersion = "dokimos.completion-authority/1"
           WorkItemId = "DOKIMOS-REL-001"
           ClaimToken = "claim-current"
           FencingGeneration = 7L
           ExpectedCommit = revision
           ExpectedVerificationRevision = revision
           SupportedOrdoContractVersions = [ "ordo.constructibility-contract/1" ]
           ExpectedOrdoContractDigest = digest 'c'
           RequiredRequirementIds = [ "DOK-REL-001" ]
           RequiredDecisionIds = List.empty<string>
           RequiredConflictResolutionIds = List.empty<string>
           RequiredValidationNames = [ "unit-tests" ]
           Evidence = [ {| Id = "EV-1"; Digest = digest 'd'; Revision = revision; Status = "passed"; Reusable = false |} ]
           ConsumedPacketDigests = List.empty<string> |})

let private invoke packetJson authorityJson =
    let directory = tempDirectory ()
    let packetPath = write directory "packet.json" packetJson
    let authorityPath = write directory "authority.json" authorityJson
    execute [ "completion"; "verify"; "--packet"; packetPath; "--authority"; authorityPath ]

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

// Attested mode (run-02 I4/I6). The verifier key is generated for this test
// run only; no private key is committed.
let private attestedKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)
let private attestedSpki = attestedKey.ExportSubjectPublicKeyInfo()
let private sha256Of (bytes: byte[]) =
    "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData bytes).ToLowerInvariant()
let private attestedKeyId = sha256Of attestedSpki
let private policyDigest = digest '9'
let private receiptType = "application/vnd.echelon.verification-receipt.v1+json"

let private signedEnvelope (status: string) =
    let payload =
        JsonSerializer.SerializeToUtf8Bytes(
            {| schemaVersion = "echelon.verification-receipt/1"
               receiptId = "0b7d4f3e-9a51-4c1b-8f0e-2d6c7a9b1e44"
               workItemId = "DOKIMOS-REL-001"
               claimant = "anthropic/claude-code"
               fencingGeneration = 7L
               commit = revision
               treeId = String('f', 40)
               ordoContractDigest = digest 'c'
               validationPolicyId = "policy-1"
               validationPolicyDigest = policyDigest
               validation = "unit-tests"
               evidenceId = "EV-1"
               status = status
               exitCode = 0
               command = [ "dotnet"; "test" ]
               outputSha256 = String('0', 64)
               startedAt = "2026-10-19T10:29:00.000Z"
               observedAt = "2026-10-19T10:30:00.000Z"
               durationMs = 60000L
               verifier = {| id = "praxis-verifier"; version = "3.17.0"; keyId = attestedKeyId |} |})
    let typeBytes = Text.Encoding.UTF8.GetBytes receiptType
    let pae =
        Array.concat
            [ Text.Encoding.ASCII.GetBytes $"DSSEv1 {typeBytes.Length} "
              typeBytes
              Text.Encoding.ASCII.GetBytes $" {payload.Length} "
              payload ]
    let signature =
        attestedKey.SignData(
            pae,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    let envelope =
        {| payloadType = receiptType
           payload = Convert.ToBase64String payload
           signatures = [ {| keyid = attestedKeyId; ``sig`` = Convert.ToBase64String signature |} ] |}
    sha256Of payload, JsonSerializer.SerializeToNode envelope

/// Packet and authority for one attested validation; commit = verification revision.
let private attestedCase (status: string) (changeAuthority: JsonObject -> unit) =
    let receiptDigest, envelope = signedEnvelope status
    let packetJson =
        packet receiptDigest
    let authorityJson =
        edit (authority ()) (fun root ->
            root["ClaimAcquiredAt"] <- node "2026-10-19T10:00:00Z"
            root["LeaseExpiresAt"] <- node "2026-10-19T11:00:00Z"
            root["EvaluatedAt"] <- node "2026-10-19T10:45:00Z"
            root["ValidationPolicyDigest"] <- node policyDigest
            root["TrustedVerifierKeys"] <-
                node [ {| KeyId = attestedKeyId; Algorithm = "ecdsa-p256-sha256"; PublicKey = Convert.ToBase64String attestedSpki |} ]
            let observation = first root "Evidence"
            observation["Digest"] <- node receiptDigest
            observation["Reusable"] <- node false
            observation["ObservedAt"] <- node "2026-10-19T10:30:00Z"
            observation["Validation"] <- node "unit-tests"
            observation["Attestation"] <- envelope
            changeAuthority root)
    packetJson, authorityJson

let private diagnosticCodes (stdout: string) =
    use document = JsonDocument.Parse stdout
    document.RootElement.GetProperty("Diagnostics").EnumerateArray()
    |> Seq.map (fun item -> item.GetProperty("Code").GetString() |> string)
    |> Set.ofSeq

[<Fact>]
let ``CLI verifies a completion whose evidence carries a trusted signed receipt`` () =
    let packetJson, authorityJson = attestedCase "passed" ignore
    assertSchemaValid "praxis-completion-packet.schema.json" packetJson
    assertSchemaValid "dokimos-completion-authority.schema.json" authorityJson
    let result = invoke packetJson authorityJson
    Assert.Equal("", result.Stderr)
    Assert.Equal(0, result.ExitCode)
    assertSchemaValid "dokimos-completion-verification.schema.json" result.Stdout
    Assert.Equal(result.Stdout, (invoke packetJson authorityJson).Stdout)

[<Theory>]
[<InlineData("unattested", "DOK.COMPLETION.EVIDENCE_UNATTESTED")>]
[<InlineData("forged-signature", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE")>]
[<InlineData("other-policy", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH")>]
[<InlineData("failed", "DOK.COMPLETION.ATTESTATION_NOT_PASSED")>]
[<InlineData("malformed-envelope", "DOK.COMPLETION.ATTESTATION_MALFORMED")>]
let ``CLI refuses attested evidence that does not hold with exit 4`` (case: string) (code: string) =
    let packetJson, authorityJson =
        match case with
        | "unattested" -> attestedCase "passed" (fun root -> (first root "Evidence").Remove "Attestation" |> ignore)
        | "forged-signature" ->
            attestedCase "passed" (fun root ->
                match (first root "Evidence")["Attestation"] with
                | :? JsonObject as envelope ->
                    match envelope["signatures"] with
                    | :? JsonArray as signatures ->
                        match signatures[0] with
                        | :? JsonObject as signature -> signature["sig"] <- node (Convert.ToBase64String(Array.create 64 1uy))
                        | _ -> failwith "expected signature"
                    | _ -> failwith "expected signatures"
                | _ -> failwith "expected envelope")
        | "other-policy" -> attestedCase "passed" (fun root -> root["ValidationPolicyDigest"] <- node (digest 'e'))
        | "failed" -> attestedCase "failed" ignore
        | "malformed-envelope" -> attestedCase "passed" (fun root -> (first root "Evidence")["Attestation"] <- JsonObject())
        | other -> failwith $"unknown case {other}"
    assertSchemaValid "dokimos-completion-authority.schema.json" authorityJson
    let result = invoke packetJson authorityJson
    Assert.Equal(4, result.ExitCode)
    assertSchemaValid "dokimos-completion-verification.schema.json" result.Stdout
    Assert.Contains(code, diagnosticCodes result.Stdout)

[<Fact>]
let ``CLI refuses an untrustworthy trusted-key declaration at intake with exit 3`` () =
    let packetJson, authorityJson =
        attestedCase "passed" (fun root ->
            root["TrustedVerifierKeys"] <-
                node [ {| KeyId = digest 'e'; Algorithm = "ecdsa-p256-sha256"; PublicKey = Convert.ToBase64String attestedSpki |} ])
    let result = invoke packetJson authorityJson
    Assert.Equal(3, result.ExitCode)
    Assert.Equal("", result.Stdout)
    Assert.Contains("completion-authority-invalid", result.Stderr)
