namespace Dokimos.Core

open System
open System.Globalization
open Dokimos.Core.CompletionAttestation

/// Attested-mode judgement (run-02 contract I6): what one evidence
/// observation's attestation must prove about the verifying host's authority.
/// Pure: the caller supplies every expectation, including the evaluation
/// instant, so equivalent inputs always yield the same findings.
module CompletionAttestationBinding =
    /// The authority-side expectations an attested receipt must bind.
    type Expectations =
        { WorkItemId: string
          FencingGeneration: int64
          ExpectedCommit: string
          ExpectedVerificationRevision: string
          ExpectedOrdoContractDigest: string
          ValidationPolicyDigest: string option
          ClaimAcquiredAt: DateTimeOffset option
          LeaseExpiresAt: DateTimeOffset option
          EvaluatedAt: DateTimeOffset option
          TrustedVerifierKeys: TrustedVerifierKey list }

    /// The host observation that cites the attestation.
    type Observed =
        { Id: string
          Digest: string
          Validation: string option
          Attestation: string option }

    /// A refusal as (stable code, detail); the caller uses the evidence ID as subject.
    type Finding = string * string

    let private instant (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

    let private bindingMismatch name (expected: string) (actual: string) : Finding =
        "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH", $"{name}: expected '{expected}', found '{actual}'"

    let private bind comparison name expected actual =
        if String.Equals(expected, actual, comparison) then [] else [ bindingMismatch name expected actual ]

    /// Identifiers compare exactly.
    let private bindText = bind StringComparison.Ordinal

    /// Hex-valued members (digests, object IDs) compare ignoring case.
    let private bindHex = bind StringComparison.OrdinalIgnoreCase

    let private bindings (expected: Expectations) (observed: Observed) (attested: AttestedReceipt) =
        let receipt = attested.Payload
        [ yield! bindHex "Digest" attested.Identity observed.Digest
          yield! bindText "workItemId" expected.WorkItemId receipt.WorkItemId
          if receipt.FencingGeneration <> expected.FencingGeneration then
              yield bindingMismatch "fencingGeneration" (string expected.FencingGeneration) (string receipt.FencingGeneration)
          yield! bindHex "commit" expected.ExpectedVerificationRevision receipt.Commit
          yield! bindHex "commit" expected.ExpectedCommit receipt.Commit
          yield! bindHex "ordoContractDigest" expected.ExpectedOrdoContractDigest receipt.OrdoContractDigest
          match expected.ValidationPolicyDigest with
          | Some policy -> yield! bindHex "validationPolicyDigest" policy receipt.ValidationPolicyDigest
          | None ->
              yield
                  "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH",
                  $"validationPolicyDigest: the authority declares no ValidationPolicyDigest, found '{receipt.ValidationPolicyDigest}'"
          match observed.Validation with
          | Some validation -> yield! bindText "validation" validation receipt.Validation
          | None ->
              yield
                  "DOK.COMPLETION.ATTESTATION_BINDING_MISMATCH",
                  $"validation: the observation declares no Validation, found '{receipt.Validation}'"
          yield! bindText "evidenceId" observed.Id receipt.EvidenceId ]

    let private status (attested: AttestedReceipt) : Finding list =
        match attested.Payload.Status with
        | ReceiptPassed -> []
        | ReceiptFailed -> [ "DOK.COMPLETION.ATTESTATION_NOT_PASSED", "the verifier observed the validation failed" ]
        | ReceiptUnavailable -> [ "DOK.COMPLETION.ATTESTATION_NOT_PASSED", "the verifier observed the validation as unavailable" ]

    /// `observedAt` must lie in [ClaimAcquiredAt, LeaseExpiresAt) when those
    /// are present and must not follow EvaluatedAt.
    let private freshness (expected: Expectations) (attested: AttestedReceipt) : Finding list =
        let observed = attested.Payload.ObservedAt
        let stale detail = "DOK.COMPLETION.ATTESTATION_STALE", detail
        [ match expected.ClaimAcquiredAt with
          | Some acquired when observed < acquired ->
              yield stale $"receipt observed at {instant observed} precedes claim acquisition {instant acquired}"
          | _ -> ()
          match expected.LeaseExpiresAt with
          | Some expires when observed >= expires ->
              yield stale $"receipt observed at {instant observed} at or after lease expiry {instant expires}"
          | _ -> ()
          match expected.EvaluatedAt with
          | Some evaluated when observed > evaluated ->
              yield stale $"receipt observed at {instant observed} after evaluation {instant evaluated}"
          | _ -> () ]

    /// Every refusal for one observation that satisfies a required validation.
    let judge (expected: Expectations) (observed: Observed) : Finding list =
        match observed.Attestation with
        | None ->
            [ "DOK.COMPLETION.EVIDENCE_UNATTESTED",
              "attested mode requires a trusted verifier receipt for evidence satisfying a required validation" ]
        | Some envelope ->
            match verify expected.TrustedVerifierKeys envelope with
            | Error(Malformed problem) -> [ "DOK.COMPLETION.ATTESTATION_MALFORMED", problem ]
            | Error(UntrustedKey problem) -> [ "DOK.COMPLETION.ATTESTATION_UNTRUSTED_KEY", problem ]
            | Error(InvalidSignature problem) -> [ "DOK.COMPLETION.ATTESTATION_INVALID_SIGNATURE", problem ]
            | Ok attested -> bindings expected observed attested @ status attested @ freshness expected attested
