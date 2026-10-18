namespace Dokimos.Core

open System.Text.Json
open Dokimos.Core.CompletionVerification

/// Strict JSON intake and stable receipt projection for independent completion
/// verification. Unknown and duplicate members are refused before semantics.
module CompletionWire =
    let private exact required (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error $"expected object, found {element.ValueKind}"
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            match names |> List.groupBy id |> List.tryFind (fun (_, copies) -> copies.Length > 1) with
            | Some(name, _) -> Error $"duplicate JSON member '{name}'"
            | None ->
                let allowed = Set.ofList required
                match names |> List.tryFind (fun name -> not (Set.contains name allowed)) with
                | Some name -> Error $"unknown JSON member '{name}'"
                | None ->
                    match required |> List.tryFind (fun name -> not (List.contains name names)) with
                    | Some name -> Error $"missing required field '{name}'"
                    | None -> Ok()

    let private boolean: Json.Decoder<bool> =
        fun element ->
            match element.ValueKind with
            | JsonValueKind.True -> Ok true
            | JsonValueKind.False -> Ok false
            | other -> Error $"expected boolean, found {other}"

    let private validationStatus =
        Json.oneOf [ "passed", Passed; "failed", Failed; "unavailable", Unavailable ]

    let private evidenceStatus =
        Json.oneOf [ "passed", EvidencePassed; "failed", EvidenceFailed; "unavailable", EvidenceUnavailable ]

    let private evidenceClaim: Json.Decoder<EvidenceClaim> =
        fun element ->
            Json.result {
                do! exact [ "Id"; "Digest"; "Revision" ] element
                let! id = Json.field "Id" Json.nonEmptyString element
                let! digest = Json.field "Digest" Json.nonEmptyString element
                let! revision = Json.field "Revision" Json.nonEmptyString element
                return { Id = id; Digest = digest; Revision = revision }
            }

    let private validationClaim: Json.Decoder<ValidationClaim> =
        fun element ->
            Json.result {
                do! exact [ "Name"; "Status"; "EvidenceIds" ] element
                let! name = Json.field "Name" Json.nonEmptyString element
                let! status = Json.field "Status" validationStatus element
                let! evidence = Json.field "EvidenceIds" (Json.list Json.nonEmptyString) element
                return { Name = name; Status = status; EvidenceIds = evidence }
            }

    let private packet: Json.Decoder<CompletionPacket> =
        fun element ->
            Json.result {
                do! exact
                        [ "SchemaVersion"; "WorkItemId"; "ClaimToken"; "FencingGeneration"; "Commit"
                          "VerificationRevision"; "OrdoContractVersion"; "OrdoContractDigest"; "RequirementIds"
                          "DecisionIds"; "ConflictResolutionIds"; "Validations"; "Evidence" ] element
                let! schema = Json.field "SchemaVersion" Json.nonEmptyString element
                let! workItem = Json.field "WorkItemId" Json.nonEmptyString element
                let! claim = Json.field "ClaimToken" Json.nonEmptyString element
                let! generation = Json.field "FencingGeneration" Json.int64 element
                let! commit = Json.field "Commit" Json.nonEmptyString element
                let! revision = Json.field "VerificationRevision" Json.nonEmptyString element
                let! ordoVersion = Json.field "OrdoContractVersion" Json.nonEmptyString element
                let! ordoDigest = Json.field "OrdoContractDigest" Json.nonEmptyString element
                let! requirements = Json.field "RequirementIds" (Json.list Json.nonEmptyString) element
                let! decisions = Json.field "DecisionIds" (Json.list Json.nonEmptyString) element
                let! resolutions = Json.field "ConflictResolutionIds" (Json.list Json.nonEmptyString) element
                let! validations = Json.field "Validations" (Json.list validationClaim) element
                let! evidence = Json.field "Evidence" (Json.list evidenceClaim) element
                return
                    { SchemaVersion = schema; WorkItemId = workItem; ClaimToken = claim; FencingGeneration = generation
                      Commit = commit; VerificationRevision = revision; OrdoContractVersion = ordoVersion
                      OrdoContractDigest = ordoDigest; RequirementIds = requirements; DecisionIds = decisions
                      ConflictResolutionIds = resolutions; Validations = validations; Evidence = evidence }
            }

    let private observation: Json.Decoder<EvidenceObservation> =
        fun element ->
            Json.result {
                do! exact [ "Id"; "Digest"; "Revision"; "Status"; "Reusable" ] element
                let! id = Json.field "Id" Json.nonEmptyString element
                let! digest = Json.field "Digest" Json.nonEmptyString element
                let! revision = Json.field "Revision" Json.nonEmptyString element
                let! status = Json.field "Status" evidenceStatus element
                let! reusable = Json.field "Reusable" boolean element
                return { Id = id; Digest = digest; Revision = revision; Status = status; Reusable = reusable }
            }

    let private authority: Json.Decoder<VerificationAuthority> =
        fun element ->
            Json.result {
                do! exact
                        [ "SchemaVersion"; "WorkItemId"; "ClaimToken"; "FencingGeneration"; "ExpectedCommit"
                          "ExpectedVerificationRevision"; "SupportedOrdoContractVersions"; "ExpectedOrdoContractDigest"
                          "RequiredRequirementIds"; "RequiredDecisionIds"; "RequiredConflictResolutionIds"
                          "RequiredValidationNames"; "Evidence"; "ConsumedPacketDigests" ] element
                let! schema = Json.field "SchemaVersion" Json.nonEmptyString element
                let! workItem = Json.field "WorkItemId" Json.nonEmptyString element
                let! claim = Json.field "ClaimToken" Json.nonEmptyString element
                let! generation = Json.field "FencingGeneration" Json.int64 element
                let! commit = Json.field "ExpectedCommit" Json.nonEmptyString element
                let! revision = Json.field "ExpectedVerificationRevision" Json.nonEmptyString element
                let! versions = Json.field "SupportedOrdoContractVersions" (Json.list Json.nonEmptyString) element
                let! ordoDigest = Json.field "ExpectedOrdoContractDigest" Json.nonEmptyString element
                let! requirements = Json.field "RequiredRequirementIds" (Json.list Json.nonEmptyString) element
                let! decisions = Json.field "RequiredDecisionIds" (Json.list Json.nonEmptyString) element
                let! resolutions = Json.field "RequiredConflictResolutionIds" (Json.list Json.nonEmptyString) element
                let! validations = Json.field "RequiredValidationNames" (Json.list Json.nonEmptyString) element
                let! evidence = Json.field "Evidence" (Json.list observation) element
                let! consumed = Json.field "ConsumedPacketDigests" (Json.list Json.nonEmptyString) element
                return
                    { SchemaVersion = schema; WorkItemId = workItem; ClaimToken = claim; FencingGeneration = generation
                      ExpectedCommit = commit; ExpectedVerificationRevision = revision
                      SupportedOrdoContractVersions = Set.ofList versions; ExpectedOrdoContractDigest = ordoDigest
                      RequiredRequirementIds = Set.ofList requirements; RequiredDecisionIds = Set.ofList decisions
                      RequiredConflictResolutionIds = Set.ofList resolutions; RequiredValidationNames = Set.ofList validations
                      Evidence = evidence; ConsumedPacketDigests = Set.ofList consumed }
            }

    let private read decoder text =
        Json.parse decoder text |> Result.mapError (sprintf "%A")

    let readPacket text = read packet text
    let readAuthority text = read authority text

    let toWire (receipt: VerificationReceipt) =
        {| Contract = "dokimos.completion-verification"
           SchemaVersion = receipt.SchemaVersion
           DokimosVersion = DokimosInfo.version
           Verdict = receipt.Verdict
           ExitCode = receipt.ExitCode
           WorkItemId = receipt.WorkItemId
           Commit = receipt.Commit
           VerificationRevision = receipt.VerificationRevision
           PacketDigest = receipt.PacketDigest
           RequirementCount = receipt.RequirementCount
           EvidenceDigests = receipt.EvidenceDigests
           ReusedEvidenceIds = receipt.ReusedEvidenceIds
           Diagnostics = receipt.Diagnostics |}
