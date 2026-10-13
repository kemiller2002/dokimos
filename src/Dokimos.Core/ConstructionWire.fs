namespace Dokimos.Core

/// The `dokimos.construction-verification` 1.1.0 wire contract (1.1.0 adds the
/// optional `MechanicalFirst` block; every 1.0.0 field is unchanged)
/// (schemas/dokimos-construction-verification.schema.json).
[<RequireQualifiedAccess>]
module ConstructionWire =
    [<Literal>]
    let Contract = "dokimos.construction-verification"

    [<Literal>]
    let SchemaVersion = "1.1.0"

    let private orEmpty (value: string option) = value |> Option.defaultValue ""

    let toWire (inputs: ConstructionInputs) (baseCommit: string) (report: ConstructionVerificationReport) =
        let expectedPlan, recomputedPlan, manifestPlan = report.PlanDigest
        let ownership = report.Ownership
        let manifestGenerator =
            report.Findings |> List.exists (fun f -> f.Code = ConstructionCodes.GeneratorVersionChanged && f.Subject = "construction-manifest")
        {| Contract = Contract
           SchemaVersion = SchemaVersion
           DokimosVersion = DokimosInfo.version
           CheckedAt = inputs.CheckedAt.ToString("O")
           WorkItem = inputs.WorkItem
           Verdict = report.Verdict
           ExitCode = report.ExitCode
           Reasons = report.Reasons
           Inputs =
            {| SourceManifestDigest = ConstructionJson.parse "source manifest" inputs.SourceManifestJson |> Result.map (ConstructionJson.text "digest") |> Result.defaultValue ""
               BlueprintDigest = ConstructionJson.parse "blueprint" inputs.BlueprintJson |> Result.map ConstructionCanonical.blueprintDigest |> Result.defaultValue ""
               Plan = {| Expected = expectedPlan; Recomputed = recomputedPlan; Manifest = manifestPlan |}
               Generator = {| Expected = inputs.ExpectedGenerator; ManifestMatches = not manifestGenerator |}
               Candidate =
                {| Commit = inputs.Candidate.Commit
                   CommittedAt = inputs.Candidate.CommittedAt.ToString("O")
                   Base = baseCommit
                   ManifestPath = inputs.ManifestPath
                   OwnershipRoot = ownership |> Option.map _.Root |> orEmpty |}
               ReferenceManifest = (if inputs.ReferenceManifestJson.IsSome then "supplied" else "absent")
               Oracle =
                {| Id = report.Oracle |> Option.map _.OracleId |> orEmpty
                   Digest = report.Oracle |> Option.map _.Digest |> orEmpty
                   ExpectedDigest = inputs.ExpectedOracleDigest
                   FrozenAt = report.Oracle |> Option.bind _.FrozenAt |> Option.map (fun t -> t.ToString("O")) |> orEmpty
                   Producer = report.Oracle |> Option.map _.ProducerKind |> orEmpty |}
               SourceDocuments = report.SourceDocumentStatus |}
           SourceConservation =
            report.SourceConservation
            |> Option.map (fun e ->
                {| Contract = "dokimos.ecir-audit/1"
                   Imported = e.Imported
                   Represented = e.Represented
                   Modeled = e.Modeled
                   Deferred = e.Deferred
                   Unresolved = e.Unresolved
                   Rejected = e.Rejected
                   Superseded = e.Superseded
                   Findings = e.Findings |> List.map (fun f -> {| Code = f.Code; RequirementKey = f.RequirementKey; Detail = f.Detail |}) |})
            |> Option.toList
           StructuralConformance =
            {| Status = report.StructuralStatus
               Artifacts = ownership |> Option.map _.Artifacts |> Option.defaultValue [] |> List.map (fun (p, s) -> {| Path = p; Status = s |})
               GeneratorStructuralChecks =
                {| Independence = "derived-from-plan"
                   ConsistentWithPlan = report.GeneratorChecksConsistentWithPlan
                   CountsAsAcceptance = false |}
               Compile = report.CompileStatus |}
           BehavioralAcceptance =
            {| Status = report.BehaviorStatus
               Reason = report.BehaviorReason
               Run =
                report.Run
                |> Option.map (fun r ->
                    {| ObservedBy = "dokimos"
                       Runner = r.Runner
                       RunnerVersion = r.RunnerVersion
                       CommandLine = r.CommandLine
                       StartedAt = r.StartedAt.ToString("O")
                       FinishedAt = r.FinishedAt.ToString("O")
                       ExitCode = r.ExitCode
                       StdoutDigest = r.StdoutDigest
                       HarnessDigest = r.HarnessDigest
                       CandidateCommit = r.CandidateCommit
                       OracleDigest = r.OracleDigest |})
                |> Option.toList
               Cases = report.Cases
               EscapedConstraints = report.EscapedConstraints |}
           Containment =
            {| Status = report.Containment.Status
               Reason = report.Containment.Reason
               Host = report.Containment.HostId
               Mechanism = report.Containment.Mechanism
               UnavailableProofs = report.Containment.UnavailableProofs
               HostRecordedDenials = report.Containment.HostRecordedDenials
               UnenforcedProtectedWrites = report.Containment.UnenforcedProtectedWrites |}
           Dependencies = {| Status = report.DependencyStatus |}
           Requirements = report.Requirements
           Counts = report.Counts
           Exceptions = {| Applied = report.ExceptionsApplied; Offered = inputs.Authorization.Exceptions.Length |}
           Findings = report.Findings
           MechanicalFirst =
            report.Mechanical
            |> Option.map (fun m ->
                {| PreparedDigest = m.PreparedDigest
                   CacheKey = m.CacheKey
                   Provenance =
                    {| GeneratorId = m.GeneratorId
                       GeneratorVersion = m.GeneratorVersion
                       VocabularyVersion = m.VocabularyVersion
                       DecisionRecordsDigest = m.DecisionRecordsDigest |}
                   GeneratedCommit = m.GeneratedCommit
                   ReplicaCommit = m.ReplicaCommit
                   GeneratedPaths = m.GeneratedPaths
                   PermittedExtensionFiles = m.PermittedExtensionFiles
                   HandwrittenPaths = m.HandwrittenPaths
                   Obligations = m.Obligations
                   Checks = m.Checks |})
            |> Option.toList
           Limitations = report.Limitations |}
