namespace Dokimos.Core.Tests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.CompletionVerification

open Dokimos.Core.Tests.CompletionAttestationFixtures

/// Attested completion mode: every refusal code, each binding member alone,
/// mixed packets, and 0.6.0 parity without trusted keys.
module CompletionAttestationTests =
    let private attestationCases name : VerificationAuthority * CompletionPacket =
        match name with
        | "no-attestation" ->
            let authority, packet = valid ()
            reauthor authority (fun item -> { item with Attestation = None }), packet
        | "untrusted-key" -> scenario ignore (signedBy otherKey)
        | "signed-by-other-key-under-trusted-keyid" ->
            scenario ignore (fun payload -> envelopeText payloadType payload [ verifierKey.KeyId, sign otherKey payload ])
        | "bad-signature" ->
            scenario ignore (fun payload -> envelopeText payloadType payload [ verifierKey.KeyId, Array.create 64 7uy ])
        | "short-signature" ->
            scenario ignore (fun payload -> envelopeText payloadType payload [ verifierKey.KeyId, (sign verifierKey payload).[.. 62] ])
        | "der-signature" ->
            scenario ignore (fun payload ->
                let der = verifierKey.Key.SignData(paeOf payloadType payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                envelopeText payloadType payload [ verifierKey.KeyId, der ])
        | "tampered-payload" ->
            scenario ignore (fun payload ->
                let tampered =
                    Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("\"claimant\":\"anthropic/claude-code\"", "\"claimant\":\"forger\""))
                Assert.NotEqual<byte[]>(payload, tampered)
                envelopeText payloadType tampered [ verifierKey.KeyId, sign verifierKey payload ])
            |> fun (authority, packet) ->
                // The observation and packet digest the tampered bytes actually carried.
                let carried =
                    match authority.Evidence.Head.Attestation with
                    | Some envelope ->
                        match (objectOf (JsonNode.Parse envelope)).["payload"] with
                        | null -> failwith "expected payload"
                        | node -> node.GetValue<string>() |> Convert.FromBase64String |> identity
                    | None -> failwith "expected attestation"
                let authority = reauthor authority (fun item -> { item with Digest = carried })
                authority, { packet with Evidence = packet.Evidence |> List.map (fun claim -> { claim with Digest = carried }) }
        | "pae-type-substitution" ->
            scenario ignore (fun payload ->
                let signature =
                    verifierKey.Key.SignData(paeOf "application/json" payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                envelopeText payloadType payload [ verifierKey.KeyId, signature ])
        | "envelope-empty-object" -> scenario ignore (fun _ -> "{}")
        | "envelope-unknown-member" ->
            scenario ignore (fun payload ->
                let node = objectOf (JsonNode.Parse(signedBy verifierKey payload))
                node["extra"] <- JsonValue.Create true
                node.ToJsonString())
        | "envelope-payload-not-base64" ->
            scenario ignore (fun payload ->
                let node = objectOf (JsonNode.Parse(signedBy verifierKey payload))
                node["payload"] <- JsonValue.Create "%%% not base64 %%%"
                node.ToJsonString())
        | "envelope-signature-not-base64" ->
            scenario ignore (fun payload ->
                let node = objectOf (JsonNode.Parse(signedBy verifierKey payload))
                (objectOf (arrayOf node["signatures"]).[0]).["sig"] <- JsonValue.Create "%%%"
                node.ToJsonString())
        | "envelope-no-signatures" -> scenario ignore (fun payload -> envelopeText payloadType payload [])
        | "envelope-wrong-payload-type" ->
            scenario ignore (fun payload ->
                let signature =
                    verifierKey.Key.SignData(paeOf "application/json" payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                envelopeText "application/json" payload [ verifierKey.KeyId, signature ])
        | "payload-not-json" ->
            let bytes = Encoding.UTF8.GetBytes "not json"
            let observation = observationOf "EV-1" "tests" bytes (signedBy verifierKey bytes)
            authorityFor [ observation ], packetFor [ observation ]
        | "payload-missing-member" -> withPayload (fun root -> root.Remove "observedAt" |> ignore)
        | "payload-duplicate-member" ->
            let raw = Encoding.UTF8.GetString(payloadFor "EV-1" "tests" ignore)
            let bytes = Encoding.UTF8.GetBytes(raw.Replace("{\"schemaVersion\"", "{\"workItemId\":\"WORK-1\",\"schemaVersion\""))
            let observation = observationOf "EV-1" "tests" bytes (signedBy verifierKey bytes)
            authorityFor [ observation ], packetFor [ observation ]
        | "payload-wrong-schema" -> withPayload (put "schemaVersion" (text "echelon.verification-receipt/2"))
        | "payload-unknown-status" -> withPayload (put "status" (text "skipped"))
        | "payload-offsetless-instant" -> withPayload (put "observedAt" (text "2026-10-19T10:30:00.000"))
        | "digest-mismatch" ->
            let authority, packet = valid ()
            reauthor authority (fun item -> { item with Digest = digest 'e' }),
            { packet with Evidence = packet.Evidence |> List.map (fun claim -> { claim with Digest = digest 'e' }) }
        | "work-item" -> withPayload (put "workItemId" (text "WORK-2"))
        | "generation" -> withPayload (put "fencingGeneration" (JsonValue.Create 6L))
        | "commit" -> withPayload (put "commit" (text (String('e', 40))))
        | "contract-digest" -> withPayload (put "ordoContractDigest" (text (digest 'e')))
        | "policy-digest" -> withPayload (put "validationPolicyDigest" (text (digest 'e')))
        | "authority-without-policy-digest" ->
            let authority, packet = valid ()
            { authority with ValidationPolicyDigest = None }, packet
        | "validation" -> withPayload (put "validation" (text "lint"))
        | "observation-without-validation" ->
            let authority, packet = valid ()
            reauthor authority (fun item -> { item with Validation = None }), packet
        | "evidence-id" -> withPayload (put "evidenceId" (text "EV-2"))
        | "failed-status" -> withPayload (put "status" (text "failed"))
        | "unavailable-status" -> withPayload (fun root -> put "status" (text "unavailable") root; put "exitCode" null root)
        | "observed-before-acquisition" -> withPayload (put "observedAt" (text "2026-10-19T09:59:59.999Z"))
        | "observed-at-lease-expiry" -> withPayload (put "observedAt" (text "2026-10-19T11:00:00.000Z"))
        | "observed-after-evaluation" ->
            let authority, packet = withPayload (put "observedAt" (text "2026-10-19T10:50:00.000Z"))
            { authority with LeaseExpiresAt = None }, packet
        | other -> failwith $"unknown case {other}"

    [<Theory>]
    [<InlineData("no-attestation", "DOK.COMPLETION.EVIDENCE_UNATTESTED", "")>]
    [<InlineData("untrusted-key", "DOK.COMPLETION.ATTESTATION_UNTRUSTED_KEY", "")>]
    [<InlineData("signed-by-other-key-under-trusted-keyid", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", "")>]
    [<InlineData("bad-signature", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", "")>]
    [<InlineData("short-signature", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", "")>]
    [<InlineData("der-signature", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", "")>]
    [<InlineData("tampered-payload", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", "")>]
    [<InlineData("pae-type-substitution", "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", "")>]
    [<InlineData("envelope-empty-object", "DOK.COMPLETION.ATTESTATION_MALFORMED", "envelope")>]
    [<InlineData("envelope-unknown-member", "DOK.COMPLETION.ATTESTATION_MALFORMED", "unknown JSON member")>]
    [<InlineData("envelope-payload-not-base64", "DOK.COMPLETION.ATTESTATION_MALFORMED", "base64")>]
    [<InlineData("envelope-signature-not-base64", "DOK.COMPLETION.ATTESTATION_MALFORMED", "base64")>]
    [<InlineData("envelope-no-signatures", "DOK.COMPLETION.ATTESTATION_MALFORMED", "signature")>]
    [<InlineData("envelope-wrong-payload-type", "DOK.COMPLETION.ATTESTATION_MALFORMED", "payloadType")>]
    [<InlineData("payload-not-json", "DOK.COMPLETION.ATTESTATION_MALFORMED", "payload")>]
    [<InlineData("payload-missing-member", "DOK.COMPLETION.ATTESTATION_MALFORMED", "observedAt")>]
    [<InlineData("payload-duplicate-member", "DOK.COMPLETION.ATTESTATION_MALFORMED", "duplicate")>]
    [<InlineData("payload-wrong-schema", "DOK.COMPLETION.ATTESTATION_MALFORMED", "schemaVersion")>]
    [<InlineData("payload-unknown-status", "DOK.COMPLETION.ATTESTATION_MALFORMED", "status")>]
    [<InlineData("payload-offsetless-instant", "DOK.COMPLETION.ATTESTATION_MALFORMED", "observedAt")>]
    [<InlineData("digest-mismatch", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "Digest:")>]
    [<InlineData("work-item", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "workItemId:")>]
    [<InlineData("generation", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "fencingGeneration:")>]
    [<InlineData("commit", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "commit:")>]
    [<InlineData("contract-digest", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "ordoContractDigest:")>]
    [<InlineData("policy-digest", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "validationPolicyDigest:")>]
    [<InlineData("authority-without-policy-digest", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "validationPolicyDigest:")>]
    [<InlineData("validation", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "validation:")>]
    [<InlineData("observation-without-validation", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "validation:")>]
    [<InlineData("evidence-id", "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", "evidenceId:")>]
    [<InlineData("failed-status", "DOK.COMPLETION.ATTESTATION_NOT_PASSED", "failed")>]
    [<InlineData("unavailable-status", "DOK.COMPLETION.ATTESTATION_NOT_PASSED", "unavailable")>]
    [<InlineData("observed-before-acquisition", "DOK.COMPLETION.ATTESTATION_STALE", "precedes claim acquisition")>]
    [<InlineData("observed-at-lease-expiry", "DOK.COMPLETION.ATTESTATION_STALE", "lease expiry")>]
    [<InlineData("observed-after-evaluation", "DOK.COMPLETION.ATTESTATION_STALE", "after evaluation")>]
    let ``each attestation property is refused alone with its specific code`` (case: string) (code: string) (detail: string) =
        let authority, packet = attestationCases case
        let receipt = verify authority packet
        Assert.Equal("rejected", receipt.Verdict)
        Assert.Equal(4, receipt.ExitCode)
        Assert.NotEmpty(receipt.Diagnostics)
        // Isolation: the only refusal is the attestation property under test.
        for diagnostic in receipt.Diagnostics do
            Assert.Equal(code, diagnostic.Code)
            Assert.Equal("EV-1", diagnostic.Subject)
        Assert.Contains(receipt.Diagnostics, fun diagnostic -> diagnostic.Detail.Contains(detail, StringComparison.Ordinal))

    [<Fact>]
    let ``a correctly signed and bound receipt verifies`` () =
        let authority, packet = valid ()
        let receipt = verify authority packet
        Assert.True(receipt.Diagnostics.IsEmpty, sprintf "%A" receipt.Diagnostics)
        Assert.Equal("verified", receipt.Verdict)
        Assert.Equal(0, receipt.ExitCode)
        Assert.Equal<string list>([ authority.Evidence.Head.Digest ], receipt.EvidenceDigests)

    [<Fact>]
    let ``attested receipts are deterministic and hex case of digests does not matter`` () =
        let authority, packet = valid ()
        let render authority packet = verify authority packet |> CompletionWire.toWire |> Contracts.serialize
        Assert.Equal(render authority packet, render authority packet)
        let upperKeys =
            { authority with
                TrustedVerifierKeys = [ { trusted verifierKey with KeyId = "sha256:" + verifierKey.KeyId.Substring(7).ToUpperInvariant() } ]
                ValidationPolicyDigest = Some("sha256:" + policyDigest.Substring(7).ToUpperInvariant()) }
        Assert.Equal("verified", (verify upperKeys packet).Verdict)

    [<Fact>]
    let ``any trusted signature that verifies suffices alongside untrusted ones`` () =
        let authority, packet =
            scenario ignore (fun payload ->
                envelopeText payloadType payload [ otherKey.KeyId, sign otherKey payload; verifierKey.KeyId, sign verifierKey payload ])
        Assert.Equal("verified", (verify authority packet).Verdict)
        let bothTrusted = { authority with TrustedVerifierKeys = [ trusted otherKey; trusted verifierKey ] }
        Assert.Equal("verified", (verify bothTrusted packet).Verdict)

    [<Fact>]
    let ``a mixed packet with one valid and one invalid observation is refused for the invalid one only`` () =
        let goodPayload = payloadFor "EV-1" "tests" ignore
        let good = observationOf "EV-1" "tests" goodPayload (signedBy verifierKey goodPayload)
        let badPayload = payloadFor "EV-2" "lint" (put "commit" (text (String('e', 40))))
        let bad = observationOf "EV-2" "lint" badPayload (signedBy verifierKey badPayload)
        let receipt = verify (authorityFor [ good; bad ]) (packetFor [ good; bad ])
        Assert.Equal("rejected", receipt.Verdict)
        Assert.All(receipt.Diagnostics, fun diagnostic ->
            Assert.Equal("DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", diagnostic.Code)
            Assert.Equal("EV-2", diagnostic.Subject))
        let unattested = { bad with Attestation = None }
        let mixed = verify (authorityFor [ good; unattested ]) (packetFor [ good; unattested ])
        Assert.Equal<string list>([ "DOK.COMPLETION.EVIDENCE_UNATTESTED" ], mixed.Diagnostics |> List.map _.Code)
        Assert.Equal("EV-2", mixed.Diagnostics.Head.Subject)
        let bothValidPayload = payloadFor "EV-2" "lint" ignore
        let second = observationOf "EV-2" "lint" bothValidPayload (signedBy verifierKey bothValidPayload)
        Assert.Equal("verified", (verify (authorityFor [ good; second ]) (packetFor [ good; second ])).Verdict)

    [<Fact>]
    let ``without trusted keys attestations are ignored and the receipt is unchanged`` () =
        let attestedAuthority, packet = valid ()
        let legacy = { attestedAuthority with TrustedVerifierKeys = []; ValidationPolicyDigest = None }
        let withoutAttestation = reauthor legacy (fun item -> { item with Attestation = None })
        let render authority = verify authority packet |> CompletionWire.toWire |> Contracts.serialize
        Assert.Equal("verified", (verify legacy packet).Verdict)
        Assert.Equal(render withoutAttestation, render legacy)
        // Even a forged attestation is not consulted outside attested mode.
        let forged = reauthor legacy (fun item -> { item with Attestation = Some "{}" })
        Assert.Equal(render withoutAttestation, render forged)
