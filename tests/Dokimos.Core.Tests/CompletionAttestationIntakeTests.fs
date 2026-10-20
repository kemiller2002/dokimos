namespace Dokimos.Core.Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.CompletionVerification

open Dokimos.Core.Tests.CompletionAttestationFixtures

/// Attested completion mode: DSSE PAE, trusted-key validation and strict
/// intake of the additive authority members.
module CompletionAttestationIntakeTests =
    [<Fact>]
    let ``DSSE v1 PAE matches the specification test vector`` () =
        let encoded = CompletionAttestation.pae "http://example.com/HelloWorld" (Encoding.UTF8.GetBytes "hello world")
        Assert.Equal("DSSEv1 29 http://example.com/HelloWorld 11 hello world", Encoding.UTF8.GetString encoded)
        let payload = payloadFor "EV-1" "tests" ignore
        Assert.Equal<byte[]>(paeOf payloadType payload, CompletionAttestation.pae payloadType payload)

    [<Fact>]
    let ``trusted keys are validated at intake`` () =
        let good = trusted verifierKey
        Assert.Equal(Ok(), CompletionAttestation.validateTrustedKey good)
        let p384 = newSigner ECCurve.NamedCurves.nistP384
        let rsa = RSA.Create 2048
        let rsaSpki = rsa.ExportSubjectPublicKeyInfo()
        let refused: CompletionAttestation.TrustedVerifierKey list =
            [ { good with Algorithm = "ecdsa-p384-sha384" }
              { good with KeyId = otherKey.KeyId }
              { good with KeyId = "sha256:abc" }
              { good with PublicKey = "%%%" }
              { good with PublicKey = Convert.ToBase64String(Array.append (Convert.FromBase64String good.PublicKey) [| 0uy |]) }
              { KeyId = p384.KeyId; Algorithm = "ecdsa-p256-sha256"; PublicKey = p384.PublicKey }
              { KeyId = identity rsaSpki; Algorithm = "ecdsa-p256-sha256"; PublicKey = Convert.ToBase64String rsaSpki } ]
        for key in refused do
            Assert.True(CompletionAttestation.validateTrustedKey key |> Result.isError, sprintf "%A" key)

    let private authorityJson (keys: JsonNode | null) (policy: JsonNode | null) (attestation: JsonNode | null) =
        let authority, _ = valid ()
        let observation = authority.Evidence.Head
        let item = JsonObject()
        item["Id"] <- JsonValue.Create observation.Id
        item["Digest"] <- JsonValue.Create observation.Digest
        item["Revision"] <- JsonValue.Create revision
        item["Status"] <- JsonValue.Create "passed"
        item["Reusable"] <- JsonValue.Create false
        item["ObservedAt"] <- JsonValue.Create "2026-10-19T10:30:00Z"
        item["Validation"] <- JsonValue.Create "tests"
        if not (isNull attestation) then item["Attestation"] <- attestation
        let root = JsonObject()
        root["SchemaVersion"] <- JsonValue.Create AuthoritySchemaVersion
        root["WorkItemId"] <- JsonValue.Create "WORK-1"
        root["ClaimToken"] <- JsonValue.Create "claim-current"
        root["FencingGeneration"] <- JsonValue.Create 7L
        root["ExpectedCommit"] <- JsonValue.Create revision
        root["ExpectedVerificationRevision"] <- JsonValue.Create revision
        root["SupportedOrdoContractVersions"] <- JsonArray(JsonValue.Create "ordo.constructibility-contract/1")
        root["ExpectedOrdoContractDigest"] <- JsonValue.Create(digest 'c')
        root["RequiredRequirementIds"] <- JsonArray(JsonValue.Create "RQ-1")
        root["RequiredDecisionIds"] <- JsonArray()
        root["RequiredConflictResolutionIds"] <- JsonArray()
        root["RequiredValidationNames"] <- JsonArray(JsonValue.Create "tests")
        root["Evidence"] <- JsonArray(item)
        root["ConsumedPacketDigests"] <- JsonArray()
        root["ClaimAcquiredAt"] <- JsonValue.Create "2026-10-19T10:00:00Z"
        root["LeaseExpiresAt"] <- JsonValue.Create "2026-10-19T11:00:00Z"
        root["EvaluatedAt"] <- JsonValue.Create "2026-10-19T10:45:00Z"
        if not (isNull keys) then root["TrustedVerifierKeys"] <- keys
        if not (isNull policy) then root["ValidationPolicyDigest"] <- policy
        root.ToJsonString()

    let private keyNode (key: CompletionAttestation.TrustedVerifierKey) : JsonNode | null =
        let node = JsonObject()
        node["KeyId"] <- JsonValue.Create key.KeyId
        node["Algorithm"] <- JsonValue.Create key.Algorithm
        node["PublicKey"] <- JsonValue.Create key.PublicKey
        node

    [<Fact>]
    let ``attested authority members round-trip through strict intake and verify`` () =
        let authority, packet = valid ()
        let envelope = authority.Evidence.Head.Attestation.Value
        let json =
            authorityJson (JsonArray(keyNode (trusted verifierKey))) (JsonValue.Create policyDigest) (JsonNode.Parse envelope)
        match CompletionWire.readAuthority json with
        | Error problem -> failwith problem
        | Ok read ->
            Assert.Equal<CompletionAttestation.TrustedVerifierKey list>([ trusted verifierKey ], read.TrustedVerifierKeys)
            Assert.Equal(Some policyDigest, read.ValidationPolicyDigest)
            Assert.Equal("verified", (verify read packet).Verdict)
        match CompletionWire.readAuthority (authorityJson null null null) with
        | Ok read ->
            Assert.Empty(read.TrustedVerifierKeys)
            Assert.Equal(None, read.ValidationPolicyDigest)
            Assert.Equal(None, read.Evidence.Head.Attestation)
        | Error problem -> failwith problem

    [<Fact>]
    let ``malformed attested authority members are refused at intake`` () =
        let key = trusted verifierKey
        let envelope = JsonNode.Parse((fst (valid ())).Evidence.Head.Attestation.Value)
        let keys (items: CompletionAttestation.TrustedVerifierKey list) : JsonNode | null =
            JsonArray(items |> List.map keyNode |> List.toArray)
        let withExtra =
            match keyNode key with
            | :? JsonObject as node ->
                node["Extra"] <- JsonValue.Create true
                JsonArray(node) :> JsonNode
            | _ -> failwith "expected object"
        let refused =
            [ authorityJson (keys [ { key with Algorithm = "rsa" } ]) null null
              authorityJson (keys [ { key with KeyId = otherKey.KeyId } ]) null null
              authorityJson (keys [ key; key ]) null null
              authorityJson withExtra null null
              authorityJson (JsonValue.Create "not-an-array") null null
              authorityJson null (JsonValue.Create "sha256:short") null
              authorityJson null null (JsonValue.Create "not-an-object")
              authorityJson null null (JsonArray()) ]
        for json in refused do
            Assert.True(CompletionWire.readAuthority json |> Result.isError, json)
        // JSON null for an optional member is refused rather than read as absent.
        let plain = authorityJson null null null
        Assert.True(CompletionWire.readAuthority plain |> Result.isOk)
        let nullAttestation = plain.Replace("\"Validation\":\"tests\"", "\"Validation\":\"tests\",\"Attestation\":null")
        Assert.NotEqual<string>(plain, nullAttestation)
        Assert.True(CompletionWire.readAuthority nullAttestation |> Result.isError)
        Assert.True(CompletionWire.readAuthority (authorityJson (keys [ key ]) null envelope) |> Result.isOk)
