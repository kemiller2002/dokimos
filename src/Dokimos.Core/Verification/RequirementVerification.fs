namespace Dokimos.Core.Verification

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Manifest-driven requirement verification (`dokimos verification requirements`): obligations,
/// requirement verdicts, acceptance and the reports. Inputs are decoded by RequirementEvidence;
/// single checks are judged by RequirementJudging.
[<RequireQualifiedAccess>]
module RequirementVerification =
    let ManifestSchema = RequirementEvidence.ManifestSchema
    let ObservationsSchema = RequirementEvidence.ObservationsSchema
    let ReportSchema = RequirementEvidence.ReportSchema
    let Attribute = RequirementEvidence.Attribute
    let statusText = RequirementEvidence.statusText
    let parseManifest = RequirementEvidence.parseManifest
    let parseObservations = RequirementEvidence.parseObservations
    let mergeObservations = RequirementEvidence.mergeObservations
    let componentMatches = RequirementJudging.componentMatches
    let accepts = RequirementJudging.accepts
    let private judge = RequirementJudging.judge

    /// The identity of Dokimos's static source-mapping check (V1 `mapped`).
    [<Literal>]
    let SourceMappingCheck = "dokimos:source-mapping"

    // ------------------------------------------------------------------ obligations

    let private mapped (evidence: Evidence) (requirement: ManifestRequirement) =
        let marker = $"{Attribute}=\"{requirement.Id}\""

        match requirement.Mappings with
        | [] -> Fail, "no implementation mapping names this requirement"
        | locations ->
            let problems =
                locations
                |> List.choose (fun location ->
                    match evidence.Source location with
                    | None -> Some $"mapped file '{location}' does not exist"
                    | Some text when not (text.Contains marker) -> Some $"mapped file '{location}' carries no {marker}"
                    | Some _ -> None)

            match problems with
            | [] -> Pass, $"""{String.Join(", ", locations)} carries {marker}"""
            | first :: _ -> Fail, first

    let private applicable (facts: Map<string, bool>) (requirement: ManifestRequirement) =
        match requirement.AppliesWhen with
        | None -> Ok true
        | Some conditions ->
            match conditions |> List.tryFind (fun (fact, _) -> not (facts.ContainsKey fact)) with
            | Some(fact, _) -> Error $"applicability fact '{fact}' was not recorded, so applicability cannot be decided"
            | None -> Ok(conditions |> List.forall (fun (fact, expected) -> facts[fact] = expected))

    let private fromProbes (evidence: Evidence) (requirement: ManifestRequirement) (obligation: ManifestObligation) (probes: Probe list) =
        match evidence.Results with
        | Error reason -> Blocked, $"test results unavailable: {reason}", []
        | Ok results ->
            let tests = probes |> List.map _.Test |> List.distinct

            let verdicts =
                probes
                |> List.map (fun probe ->
                    match results.TryFind probe.Test with
                    | None -> Error(NotRun, $"evidence came from '{probe.Test}', which has no executed test result; it is discarded")
                    | Some Outcome.Skipped
                    | Some Outcome.NotExecuted -> Error(NotRun, $"'{probe.Test}' did not execute; its evidence is discarded")
                    | Some(Outcome.Failed detail) -> Error(Fail, $"'{probe.Test}' failed: {detail}")
                    | Some Outcome.Passed -> judge requirement obligation.Check probe)

            // Worst verdict wins: FAIL > BLOCKED > NOT_RUN > PASS.
            let rank status =
                match status with
                | Fail -> 0
                | Blocked -> 1
                | NotRun -> 2
                | _ -> 3

            match verdicts |> List.choose (function Error e -> Some e | Ok() -> None) |> List.sortBy (fst >> rank) with
            | (status, reason) :: _ -> status, reason, tests
            | [] -> Pass, $"""verified by {String.Join(", ", tests)}""", tests

    let private obligationResult (evidence: Evidence) (facts: Map<string, bool>) (problems: string list) (requirement: ManifestRequirement) (obligation: ManifestObligation) =
        let result status reason tests =
            { Obligation = obligation
              Status = status
              Reason = reason
              Tests = tests }

        match applicable facts requirement with
        | Error reason -> result Blocked reason []
        | Ok false -> result NotApplicable "a declared applicability fact has another value" []
        | Ok true ->
            match obligation.Check with
            | "mapped" ->
                // Verified by Dokimos's own source check, which always runs: that check is the obligation's test.
                let status, reason = mapped evidence requirement
                result status reason [ SourceMappingCheck ]
            | _ ->
                match evidence.Observations, problems with
                | Error reason, _ -> result Blocked $"observations unavailable: {reason}" []
                | Ok _, problem :: _ -> result Blocked problem []
                | Ok observations, [] ->
                    match observations.Probes |> List.filter (fun p -> p.Obligation = obligation.Id) with
                    | [] -> result NotRun "no executed test exercised this obligation" []
                    | probes ->
                        let status, reason, tests = fromProbes evidence requirement obligation probes
                        result status reason tests

    let private aggregate (statuses: RequirementStatus list) =
        if statuses |> List.forall ((=) NotApplicable) then NotApplicable
        elif List.contains Fail statuses then Fail
        elif List.contains Blocked statuses then Blocked
        elif List.contains NotRun statuses then NotRun
        else Pass

    let private failureClass (obligations: ObligationResult list) =
        obligations
        |> List.filter (fun o -> o.Status = Fail)
        |> List.map _.Obligation.Level
        |> List.sort
        |> List.tryHead
        |> Option.map (fun level ->
            match level with
            | "V1"
            | "V2" -> "structural"
            | "V3" -> "interaction"
            | "V4" -> "behavioural"
            | _ -> "lifecycle")

    /// Verify the manifest's requirements on the given screens (all when empty), limited to the
    /// given requirement IDs (all when empty), against the evidence.
    let verifyScoped (manifest: RequirementManifest) (screens: string list) (scope: string list) (evidence: Evidence) : VerificationReport =
        let observations = evidence.Observations |> Result.toOption

        let problems =
            [ if not manifest.Intact then
                  "the manifest's content does not match its contentHash: it was edited after compilation"
              match observations with
              | Some o when o.ManifestHash <> manifest.ContentHash ->
                  $"observations were recorded against manifest {o.ManifestHash}, not {manifest.ContentHash}: they are stale"
              | _ -> ()
              match observations, evidence.Candidate with
              | Some o, Some candidate when o.SourceCommit <> candidate ->
                  $"observations were recorded at commit {o.SourceCommit}, not the candidate {candidate}"
              | _ -> () ]

        // Facts the run recorded, overridden by facts the caller states.
        let recorded = observations |> Option.map _.Facts |> Option.defaultValue Map.empty
        let facts = evidence.Facts |> Map.fold (fun acc k v -> Map.add k v acc) recorded

        let inScope (r: ManifestRequirement) =
            (screens.IsEmpty || List.contains r.Screen screens) && (scope.IsEmpty || List.contains r.Id scope)

        let requirements =
            manifest.Requirements
            |> List.filter inScope
            |> List.map (fun requirement ->
                let obligations = requirement.Obligations |> List.map (obligationResult evidence facts problems requirement)
                let status = aggregate (obligations |> List.map _.Status)

                let structural =
                    obligations |> List.filter (fun o -> o.Obligation.Check = "mapped" || o.Obligation.Check = "exists")

                { Requirement = requirement
                  Status = status
                  FailureClass = if status = Fail then failureClass obligations else None
                  Implemented = not structural.IsEmpty && structural |> List.forall (fun o -> o.Status = Pass)
                  Obligations = obligations })

        let blockers =
            requirements
            |> List.collect (fun r ->
                r.Obligations
                |> List.filter (fun o -> o.Obligation.Mandatory && o.Status <> Pass && o.Status <> NotApplicable)
                |> List.map (fun o -> o.Obligation.Id))

        let unknownScope = scope |> List.filter (fun id -> not (manifest.Requirements |> List.exists (fun r -> r.Id = id)))

        let problems =
            problems @ (unknownScope |> List.map (fun id -> $"requirement '{id}' is not in the manifest"))

        let accepted = manifest.Intact && problems.IsEmpty && blockers.IsEmpty && not requirements.IsEmpty

        { Manifest = manifest
          Candidate = evidence.Candidate
          RunId = observations |> Option.map _.RunId
          Screens = screens
          RequirementScope = scope
          Requirements = requirements
          EvidenceProblems = problems
          Accepted = accepted
          Blockers = blockers }

    /// Verify every requirement of the manifest (optionally only some screens) against the evidence.
    let verify (manifest: RequirementManifest) (screens: string list) (evidence: Evidence) : VerificationReport =
        verifyScoped manifest screens [] evidence

    // ------------------------------------------------------------------ rendering

    let private node (value: string) : JsonNode = JsonValue.Create value |> nonNull :> JsonNode
    let private optional (value: string option) : JsonNode | null =
        match value with
        | Some text -> node text
        | None -> null

    let private obj (members: (string * (JsonNode | null)) list) =
        let o = JsonObject()
        members |> List.iter (fun (k, v) -> o.Add(k, v))
        o

    let private arrayOf (items: JsonNode list) =
        let a = JsonArray()
        items |> List.iter a.Add
        a

    let private count (status: RequirementStatus) (requirements: RequirementResult list) =
        requirements |> List.filter (fun r -> r.Status = status) |> List.length

    /// Summary counts: applicable excludes NOT_APPLICABLE; implemented means every
    /// V1 obligation passed; verified means the requirement passed outright.
    let summary (report: VerificationReport) =
        let applicable = report.Requirements |> List.filter (fun r -> r.Status <> NotApplicable)
        let verified = count Pass applicable

        {| Applicable = applicable.Length
           Implemented = applicable |> List.filter _.Implemented |> List.length
           Verified = verified
           Failed = count Fail applicable
           Blocked = count Blocked applicable
           NotRun = count NotRun applicable
           NotApplicable = count NotApplicable report.Requirements
           Percent = if applicable.IsEmpty then 0.0 else Math.Floor(float verified * 1000.0 / float applicable.Length) / 10.0 |}

    let toJson (report: VerificationReport) : string =
        let s = summary report

        let body =
            obj
                [ "schema", node ReportSchema
                  "manifest",
                  obj
                      [ "catalogId", node report.Manifest.CatalogId
                        "contentHash", node report.Manifest.ContentHash
                        "sourceCommit", node report.Manifest.SourceCommit
                        "intact", JsonValue.Create report.Manifest.Intact ]
                  "candidate", optional report.Candidate
                  "runId", optional report.RunId
                  "screens", arrayOf (report.Screens |> List.map node)
                  "requirementScope", arrayOf (report.RequirementScope |> List.map node)
                  "summary",
                  obj
                      [ "applicable", JsonValue.Create s.Applicable
                        "implemented", JsonValue.Create s.Implemented
                        "verified", JsonValue.Create s.Verified
                        "failed", JsonValue.Create s.Failed
                        "blocked", JsonValue.Create s.Blocked
                        "notRun", JsonValue.Create s.NotRun
                        "notApplicable", JsonValue.Create s.NotApplicable
                        "verifiedPercent", JsonValue.Create s.Percent ]
                  "acceptance",
                  obj
                      [ "accepted", JsonValue.Create report.Accepted
                        "evidenceProblems", arrayOf (report.EvidenceProblems |> List.map node)
                        "blockers", arrayOf (report.Blockers |> List.map node) ]
                  "requirements",
                  arrayOf (
                      report.Requirements
                      |> List.map (fun r ->
                          obj
                              [ "id", node r.Requirement.Id
                                "type", node r.Requirement.Kind
                                "screen", node r.Requirement.Screen
                                "name", node r.Requirement.Name
                                "required", JsonValue.Create r.Requirement.Required
                                "status", node (statusText r.Status)
                                "failureClass", optional r.FailureClass
                                "implemented", JsonValue.Create r.Implemented
                                "obligations",
                                arrayOf (
                                    r.Obligations
                                    |> List.map (fun o ->
                                        obj
                                            [ "id", node o.Obligation.Id
                                              "level", node o.Obligation.Level
                                              "check", node o.Obligation.Check
                                              "mandatory", JsonValue.Create o.Obligation.Mandatory
                                              "status", node (statusText o.Status)
                                              "reason", node o.Reason
                                              "tests", arrayOf (o.Tests |> List.map node) ]
                                        :> JsonNode)
                                ) ]
                          :> JsonNode)
                  ) ]

        let options = JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
        body.ToJsonString(options).Replace("\r\n", "\n") + "\n"

    let toText (report: VerificationReport) : string =
        let s = summary report

        [ yield $"requirements {report.Manifest.CatalogId} manifest {report.Manifest.ContentHash}"
          if not report.RequirementScope.IsEmpty then
              yield $"""SCOPED to {String.Join(", ", report.RequirementScope)}: acceptance of this scope only, not of the catalog"""
          yield
              $"applicable {s.Applicable}  implemented {s.Implemented}  verified {s.Verified}  failed {s.Failed}  blocked {s.Blocked}  not-run {s.NotRun}  not-applicable {s.NotApplicable}  ({s.Percent}%% verified)"
          for problem in report.EvidenceProblems do
              yield $"EVIDENCE {problem}"
          for r in report.Requirements do
              yield $"""{statusText r.Status,-14} {r.Requirement.Id} {r.Requirement.Name}{(r.FailureClass |> Option.map (fun c -> $" [{c}]") |> Option.defaultValue "")}"""
              for o in r.Obligations |> List.filter (fun o -> o.Status <> Pass && o.Status <> NotApplicable) do
                  yield $"    {statusText o.Status,-14} {o.Obligation.Id}: {o.Reason}"
          yield (if report.Accepted then "ACCEPTED" else $"NOT ACCEPTED: {report.Blockers.Length} mandatory obligation(s) not verified") ]
        |> String.concat "\n"
