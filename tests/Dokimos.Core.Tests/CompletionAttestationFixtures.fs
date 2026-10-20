namespace Dokimos.Core.Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.CompletionVerification

/// Shared fixtures for attested completion mode (run-02 contracts I4/I6).
/// Every key is generated in-process for the test run; no private key is
/// committed.
module CompletionAttestationFixtures =
    let digest value = "sha256:" + String(value, 64)
    let revision = String('a', 40)
    let policyDigest = digest '9'
    let at (text: string) = Some(DateTimeOffset.Parse(text, Globalization.CultureInfo.InvariantCulture))

    let hex (bytes: byte[]) = Convert.ToHexString(bytes).ToLowerInvariant()
    let identity (bytes: byte[]) = "sha256:" + hex (SHA256.HashData bytes)

    type Signer =
        { Key: ECDsa
          KeyId: string
          PublicKey: string }

    let newSigner (curve: ECCurve) =
        let key = ECDsa.Create curve
        let spki = key.ExportSubjectPublicKeyInfo()
        { Key = key; KeyId = identity spki; PublicKey = Convert.ToBase64String spki }

    let verifierKey = newSigner ECCurve.NamedCurves.nistP256
    let otherKey = newSigner ECCurve.NamedCurves.nistP256

    let trusted (signer: Signer) : CompletionAttestation.TrustedVerifierKey =
        { KeyId = signer.KeyId; Algorithm = "ecdsa-p256-sha256"; PublicKey = signer.PublicKey }

    /// DSSE v1 PAE, written independently of the production implementation.
    let paeOf (payloadType: string) (payload: byte[]) =
        let typeBytes = Encoding.UTF8.GetBytes payloadType
        Array.concat
            [ Encoding.ASCII.GetBytes $"DSSEv1 {typeBytes.Length} "
              typeBytes
              Encoding.ASCII.GetBytes $" {payload.Length} "
              payload ]

    let payloadType = "application/vnd.echelon.verification-receipt.v1+json"

    let sign (signer: Signer) (payload: byte[]) =
        signer.Key.SignData(paeOf payloadType payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)

    let envelopeText (payloadTypeText: string) (payload: byte[]) (signatures: (string * byte[]) list) =
        let root = JsonObject()
        root["payloadType"] <- JsonValue.Create payloadTypeText
        root["payload"] <- JsonValue.Create(Convert.ToBase64String payload)
        let items = JsonArray()
        for keyId, signature in signatures do
            let item = JsonObject()
            item["keyid"] <- JsonValue.Create keyId
            item["sig"] <- JsonValue.Create(Convert.ToBase64String signature)
            items.Add item
        root["signatures"] <- items
        root.ToJsonString()

    let objectOf (node: JsonNode | null) =
        match node with
        | :? JsonObject as value -> value
        | _ -> failwith "expected a JSON object"

    let arrayOf (node: JsonNode | null) =
        match node with
        | :? JsonArray as value -> value
        | _ -> failwith "expected a JSON array"

    let signedBy (signer: Signer) payload = envelopeText payloadType payload [ signer.KeyId, sign signer payload ]

    let payloadFor (evidenceId: string) (validation: string) (edit: JsonObject -> unit) =
        let text (value: string) : JsonNode | null = JsonValue.Create value
        let root = JsonObject()
        root["schemaVersion"] <- text "echelon.verification-receipt/1"
        root["receiptId"] <- text "6f1c2c1e-6c63-4f7e-9d43-0f5b0f6f8e11"
        root["workItemId"] <- text "WORK-1"
        root["claimant"] <- text "anthropic/claude-code"
        root["fencingGeneration"] <- JsonValue.Create 7L
        root["commit"] <- text revision
        root["treeId"] <- text (String('f', 40))
        root["ordoContractDigest"] <- text (digest 'c')
        root["validationPolicyId"] <- text "policy-1"
        root["validationPolicyDigest"] <- text policyDigest
        root["validation"] <- text validation
        root["evidenceId"] <- text evidenceId
        root["status"] <- text "passed"
        root["exitCode"] <- JsonValue.Create 0
        root["command"] <- JsonArray(JsonValue.Create "dotnet", JsonValue.Create "test")
        root["outputSha256"] <- text (String('0', 64))
        root["startedAt"] <- text "2026-10-19T10:29:00.000Z"
        root["observedAt"] <- text "2026-10-19T10:30:00.000Z"
        root["durationMs"] <- JsonValue.Create 60000L
        let verifier = JsonObject()
        verifier["id"] <- text "praxis-verifier"
        verifier["version"] <- text "3.17.0"
        verifier["keyId"] <- text verifierKey.KeyId
        root["verifier"] <- verifier
        edit root
        Encoding.UTF8.GetBytes(root.ToJsonString())

    let observationOf id validation (payload: byte[]) envelope =
        { Id = id
          Digest = identity payload
          Revision = revision
          Status = EvidencePassed
          Reusable = false
          ObservedAt = at "2026-10-19T10:30:00Z"
          Validation = Some validation
          Attestation = Some envelope }

    let packetFor (observations: EvidenceObservation list) =
        { SchemaVersion = PacketSchemaVersion
          WorkItemId = "WORK-1"
          ClaimToken = "claim-current"
          FencingGeneration = 7L
          Commit = revision
          VerificationRevision = revision
          OrdoContractVersion = "ordo.constructibility-contract/1"
          OrdoContractDigest = digest 'c'
          RequirementIds = [ "RQ-1" ]
          DecisionIds = []
          ConflictResolutionIds = []
          Validations =
            observations
            |> List.map (fun item -> { Name = item.Validation.Value; Status = Passed; EvidenceIds = [ item.Id ] })
          Evidence = observations |> List.map (fun item -> { Id = item.Id; Digest = item.Digest; Revision = revision }) }

    let authorityFor (observations: EvidenceObservation list) =
        { SchemaVersion = AuthoritySchemaVersion
          WorkItemId = "WORK-1"
          ClaimToken = "claim-current"
          FencingGeneration = 7L
          ExpectedCommit = revision
          ExpectedVerificationRevision = revision
          SupportedOrdoContractVersions = set [ "ordo.constructibility-contract/1" ]
          ExpectedOrdoContractDigest = digest 'c'
          RequiredRequirementIds = set [ "RQ-1" ]
          RequiredDecisionIds = Set.empty
          RequiredConflictResolutionIds = Set.empty
          RequiredValidationNames = observations |> List.map _.Validation.Value |> Set.ofList
          Evidence = observations
          ConsumedPacketDigests = Set.empty
          ClaimAcquiredAt = at "2026-10-19T10:00:00Z"
          LeaseExpiresAt = at "2026-10-19T11:00:00Z"
          EvaluatedAt = at "2026-10-19T10:45:00Z"
          TrustedVerifierKeys = [ trusted verifierKey ]
          ValidationPolicyDigest = Some policyDigest }

    /// One attested validation. The packet always claims what the observation
    /// carries, so only the attestation itself can be at fault.
    let scenario (editPayload: JsonObject -> unit) (makeEnvelope: byte[] -> string) =
        let payload = payloadFor "EV-1" "tests" editPayload
        let observation = observationOf "EV-1" "tests" payload (makeEnvelope payload)
        authorityFor [ observation ], packetFor [ observation ]

    let valid () = scenario ignore (signedBy verifierKey)
    let withPayload edit = scenario edit (signedBy verifierKey)

    let put (name: string) (value: JsonNode | null) (root: JsonObject) = root[name] <- value
    let text (value: string) : JsonNode | null = JsonValue.Create value

    let reauthor (authority: VerificationAuthority) (change: EvidenceObservation -> EvidenceObservation) =
        let evidence = authority.Evidence |> List.map change
        { authority with Evidence = evidence }
