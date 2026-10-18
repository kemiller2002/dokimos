namespace Dokimos.Core

open System
open System.Security.Cryptography
open System.Text

/// Independent verification of a Praxis completion claim. The claimant's
/// packet is only a claim; every authority and evidence observation below is
/// supplied by the verifying host.
module CompletionVerification =
    [<Literal>]
    let PacketSchemaVersion = "praxis.completion-packet/1"

    [<Literal>]
    let AuthoritySchemaVersion = "dokimos.completion-authority/1"

    [<Literal>]
    let ReceiptSchemaVersion = "dokimos.completion-verification/1"

    type ClaimedValidationStatus =
        | Passed
        | Failed
        | Unavailable

    type ObservedEvidenceStatus =
        | EvidencePassed
        | EvidenceFailed
        | EvidenceUnavailable

    type EvidenceClaim =
        { Id: string
          Digest: string
          Revision: string }

    type ValidationClaim =
        { Name: string
          Status: ClaimedValidationStatus
          EvidenceIds: string list }

    type CompletionPacket =
        { SchemaVersion: string
          WorkItemId: string
          ClaimToken: string
          FencingGeneration: int64
          Commit: string
          VerificationRevision: string
          OrdoContractVersion: string
          OrdoContractDigest: string
          RequirementIds: string list
          DecisionIds: string list
          ConflictResolutionIds: string list
          Validations: ValidationClaim list
          Evidence: EvidenceClaim list }

    type EvidenceObservation =
        { Id: string
          Digest: string
          Revision: string
          Status: ObservedEvidenceStatus
          Reusable: bool }

    type VerificationAuthority =
        { SchemaVersion: string
          WorkItemId: string
          ClaimToken: string
          FencingGeneration: int64
          ExpectedCommit: string
          ExpectedVerificationRevision: string
          SupportedOrdoContractVersions: Set<string>
          ExpectedOrdoContractDigest: string
          RequiredRequirementIds: Set<string>
          RequiredDecisionIds: Set<string>
          RequiredConflictResolutionIds: Set<string>
          RequiredValidationNames: Set<string>
          Evidence: EvidenceObservation list
          ConsumedPacketDigests: Set<string> }

    type Diagnostic =
        { Code: string
          Subject: string
          Detail: string }

    type VerificationReceipt =
        { SchemaVersion: string
          Verdict: string
          ExitCode: int
          WorkItemId: string
          Commit: string
          VerificationRevision: string
          PacketDigest: string
          RequirementCount: int
          EvidenceDigests: string list
          ReusedEvidenceIds: string list
          Diagnostics: Diagnostic list }

    let private statusName = function
        | Passed -> "passed"
        | Failed -> "failed"
        | Unavailable -> "unavailable"

    let private frame (value: string) =
        let text = if Object.ReferenceEquals(value, null) then "" else value
        string (Encoding.UTF8.GetByteCount text) + ":" + text

    let private framedList values =
        let ordered = List.sort values
        frame (string ordered.Length) + (ordered |> List.map frame |> String.concat "")

    /// Digest of the normalized typed packet, independent of JSON member and
    /// set-like array order. Duplicates remain visible and are also refused.
    let packetDigest (packet: CompletionPacket) =
        let validations =
            packet.Validations
            |> List.sortBy (fun validation -> validation.Name, statusName validation.Status, List.sort validation.EvidenceIds)
            |> List.map (fun validation ->
                frame validation.Name + frame (statusName validation.Status) + framedList validation.EvidenceIds)
        let evidence =
            packet.Evidence
            |> List.sortBy (fun item -> item.Id, item.Digest, item.Revision)
            |> List.map (fun item -> frame item.Id + frame item.Digest + frame item.Revision)
        let canonical =
            [ frame packet.SchemaVersion
              frame packet.WorkItemId
              frame packet.ClaimToken
              frame (string packet.FencingGeneration)
              frame packet.Commit
              frame packet.VerificationRevision
              frame packet.OrdoContractVersion
              frame packet.OrdoContractDigest
              framedList packet.RequirementIds
              framedList packet.DecisionIds
              framedList packet.ConflictResolutionIds
              frame (string validations.Length)
              String.concat "" validations
              frame (string evidence.Length)
              String.concat "" evidence ]
            |> String.concat ""
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        "sha256:" + Convert.ToHexString(hash).ToLowerInvariant()

    let private duplicateValues values =
        values
        |> List.groupBy id
        |> List.choose (fun (value, copies) -> if copies.Length > 1 then Some value else None)
        |> List.sort

    let private firstBy key values =
        values
        |> List.fold (fun found value -> if Map.containsKey (key value) found then found else Map.add (key value) value found) Map.empty

    let private isHexLength length (value: string) =
        not (Object.ReferenceEquals(value, null))
        && value.Length = length
        && value |> Seq.forall Uri.IsHexDigit

    let private isImmutableRevision value =
        isHexLength 40 value || isHexLength 64 value

    let private isSha256 (value: string) =
        not (Object.ReferenceEquals(value, null))
        && value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && isHexLength 64 (value.Substring 7)

    let verify (authority: VerificationAuthority) (packet: CompletionPacket) =
        let findings = ResizeArray<Diagnostic>()
        let add code subject detail = findings.Add { Code = code; Subject = subject; Detail = detail }
        let mismatch code subject expected actual =
            if expected <> actual then add code subject $"expected '{expected}', found '{actual}'"
        let compareSet missingCode unknownCode label expected actual =
            for value in Set.difference expected actual do add missingCode value $"required {label} is absent"
            for value in Set.difference actual expected do add unknownCode value $"{label} is not authorized by the verification authority"

        mismatch "DOK.COMPLETION.UNSUPPORTED_PACKET_VERSION" "schemaVersion" PacketSchemaVersion packet.SchemaVersion
        mismatch "DOK.COMPLETION.UNSUPPORTED_AUTHORITY_VERSION" "authority.schemaVersion" AuthoritySchemaVersion authority.SchemaVersion
        mismatch "DOK.COMPLETION.WORK_ITEM_MISMATCH" packet.WorkItemId authority.WorkItemId packet.WorkItemId
        mismatch "DOK.COMPLETION.STALE_CLAIM" "claimToken" authority.ClaimToken packet.ClaimToken
        if packet.FencingGeneration <> authority.FencingGeneration then
            add "DOK.COMPLETION.STALE_FENCE" "fencingGeneration"
                $"expected generation {authority.FencingGeneration}, found {packet.FencingGeneration}"
        if packet.FencingGeneration < 0L || authority.FencingGeneration < 0L then
            add "DOK.COMPLETION.INVALID_FENCE" "fencingGeneration" "fencing generations cannot be negative"
        mismatch "DOK.COMPLETION.STALE_COMMIT" packet.Commit authority.ExpectedCommit packet.Commit
        mismatch "DOK.COMPLETION.STALE_VERIFICATION" packet.VerificationRevision authority.ExpectedVerificationRevision packet.VerificationRevision
        mismatch "DOK.COMPLETION.ORDO_CONTRACT_DIGEST_MISMATCH" packet.OrdoContractDigest authority.ExpectedOrdoContractDigest packet.OrdoContractDigest
        if not (isImmutableRevision packet.Commit) then
            add "DOK.COMPLETION.INVALID_COMMIT" packet.Commit "completion commit must be a full 40- or 64-hex Git object ID"
        if not (isImmutableRevision packet.VerificationRevision) then
            add "DOK.COMPLETION.INVALID_VERIFICATION_REVISION" packet.VerificationRevision "verification revision must be immutable"
        if not (isSha256 packet.OrdoContractDigest) then
            add "DOK.COMPLETION.INVALID_ORDO_CONTRACT_DIGEST" packet.OrdoContractDigest "Ordo contract digest must be sha256:<64 hex>"

        if not (Set.contains packet.OrdoContractVersion authority.SupportedOrdoContractVersions) then
            add "DOK.COMPLETION.UNSUPPORTED_ORDO_CONTRACT" packet.OrdoContractVersion
                ("supported versions: " + String.concat ", " (Set.toList authority.SupportedOrdoContractVersions))

        for value in duplicateValues packet.RequirementIds do add "DOK.COMPLETION.DUPLICATE_REQUIREMENT" value "requirement is repeated"
        for value in duplicateValues packet.DecisionIds do add "DOK.COMPLETION.DUPLICATE_DECISION" value "decision is repeated"
        for value in duplicateValues packet.ConflictResolutionIds do add "DOK.COMPLETION.DUPLICATE_CONFLICT_RESOLUTION" value "conflict resolution is repeated"
        for value in packet.Validations |> List.map _.Name |> duplicateValues do add "DOK.COMPLETION.DUPLICATE_VALIDATION" value "validation is repeated"
        for value in packet.Evidence |> List.map _.Id |> duplicateValues do add "DOK.COMPLETION.DUPLICATE_EVIDENCE" value "evidence claim is repeated"
        for value in authority.Evidence |> List.map _.Id |> duplicateValues do add "DOK.COMPLETION.DUPLICATE_OBSERVATION" value "evidence observation is repeated"

        compareSet "DOK.COMPLETION.MISSING_REQUIREMENT" "DOK.COMPLETION.UNKNOWN_REQUIREMENT" "requirement"
            authority.RequiredRequirementIds (Set.ofList packet.RequirementIds)
        compareSet "DOK.COMPLETION.MISSING_DECISION" "DOK.COMPLETION.UNKNOWN_DECISION" "decision"
            authority.RequiredDecisionIds (Set.ofList packet.DecisionIds)
        compareSet "DOK.COMPLETION.MISSING_CONFLICT_RESOLUTION" "DOK.COMPLETION.UNKNOWN_CONFLICT_RESOLUTION" "conflict resolution"
            authority.RequiredConflictResolutionIds (Set.ofList packet.ConflictResolutionIds)
        compareSet "DOK.COMPLETION.MISSING_VALIDATION" "DOK.COMPLETION.UNKNOWN_VALIDATION" "validation"
            authority.RequiredValidationNames (packet.Validations |> List.map _.Name |> Set.ofList)

        let claims = firstBy (fun (claim: EvidenceClaim) -> claim.Id) packet.Evidence
        let observations = firstBy (fun (observation: EvidenceObservation) -> observation.Id) authority.Evidence

        for validation in packet.Validations do
            match validation.Status with
            | Passed -> ()
            | Failed -> add "DOK.COMPLETION.VALIDATION_FAILED" validation.Name "claimant reports the validation failed"
            | Unavailable -> add "DOK.COMPLETION.VALIDATION_UNAVAILABLE" validation.Name "claimant reports the validation was unavailable"
            if validation.EvidenceIds.IsEmpty then
                add "DOK.COMPLETION.VALIDATION_WITHOUT_EVIDENCE" validation.Name "a validation must cite immutable evidence"
            for id in duplicateValues validation.EvidenceIds do
                add "DOK.COMPLETION.DUPLICATE_VALIDATION_EVIDENCE" (validation.Name + ":" + id) "validation repeats an evidence reference"
            for id in validation.EvidenceIds do
                if not (Map.containsKey id claims) then
                    add "DOK.COMPLETION.MISSING_EVIDENCE_CLAIM" (validation.Name + ":" + id) "validation cites evidence absent from the packet"

        for KeyValue(id, claim) in claims do
            if not (isSha256 claim.Digest) then
                add "DOK.COMPLETION.INVALID_EVIDENCE_DIGEST" id "evidence digest must be sha256:<64 hex>"
            if claim.Revision <> packet.VerificationRevision then
                add "DOK.COMPLETION.STALE_EVIDENCE" id
                    $"packet evidence revision '{claim.Revision}' does not equal verification revision '{packet.VerificationRevision}'"
            match Map.tryFind id observations with
            | None -> add "DOK.COMPLETION.EVIDENCE_UNOBSERVED" id "the verifying host supplied no independent observation"
            | Some observation ->
                mismatch "DOK.COMPLETION.EVIDENCE_DIGEST_MISMATCH" id observation.Digest claim.Digest
                mismatch "DOK.COMPLETION.EVIDENCE_REVISION_MISMATCH" id observation.Revision claim.Revision
                match observation.Status with
                | EvidencePassed -> ()
                | EvidenceFailed -> add "DOK.COMPLETION.EVIDENCE_FAILED" id "independent evidence failed"
                | EvidenceUnavailable -> add "DOK.COMPLETION.EVIDENCE_UNAVAILABLE" id "independent evidence is unavailable"

        let digest = packetDigest packet
        if Set.contains digest authority.ConsumedPacketDigests then
            add "DOK.COMPLETION.REPLAYED_PACKET" digest "this exact completion packet was already consumed"

        let diagnostics = findings |> Seq.distinct |> Seq.sortBy (fun item -> item.Code, item.Subject, item.Detail) |> Seq.toList
        let acceptedEvidence =
            claims
            |> Map.toList
            |> List.choose (fun (id, claim) ->
                match Map.tryFind id observations with
                | Some observation when observation.Status = EvidencePassed && observation.Digest = claim.Digest && observation.Revision = claim.Revision -> Some claim.Digest
                | _ -> None)
            |> List.distinct
            |> List.sort
        let reused =
            claims
            |> Map.toList
            |> List.choose (fun (id, claim) ->
                match Map.tryFind id observations with
                | Some observation when observation.Reusable && observation.Status = EvidencePassed
                                             && observation.Digest = claim.Digest && observation.Revision = claim.Revision -> Some id
                | _ -> None)
            |> List.sort
        { SchemaVersion = ReceiptSchemaVersion
          Verdict = if diagnostics.IsEmpty then "verified" else "rejected"
          ExitCode = if diagnostics.IsEmpty then 0 else 4
          WorkItemId = packet.WorkItemId
          Commit = packet.Commit
          VerificationRevision = packet.VerificationRevision
          PacketDigest = digest
          RequirementCount = Set.count (Set.ofList packet.RequirementIds)
          EvidenceDigests = acceptedEvidence
          ReusedEvidenceIds = reused
          Diagnostics = diagnostics }
