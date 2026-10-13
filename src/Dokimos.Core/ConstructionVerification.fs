namespace Dokimos.Core

open System

/// Separate, independently pinned inputs. None of them is derived from another.
type ConstructionInputs =
    { CheckedAt: DateTimeOffset
      WorkItem: string
      SourceManifestJson: string
      /// Original excerpt bytes by file name, when supplied.
      SourceDocuments: Map<string, byte[]> option
      BlueprintJson: string
      PlanJson: string
      ExpectedPlanDigest: string
      ExpectedGenerator: string
      ManifestPath: string
      Candidate: CandidateSnapshot
      Base: BaseSnapshot option
      ReferenceManifestJson: string option
      OracleBytes: byte[] option
      ExpectedOracleDigest: string
      OracleRun: OracleRun
      Host: HostEvidence option
      Claims: ConstructionClaims option
      ClaimedTests: TestClaim list
      Authorization: ConstructionAuthorization }

type ConstructionVerificationReport =
    { Verdict: string
      ExitCode: int
      Reasons: string list
      SourceConservation: EcirAuditReport option
      PlanDigest: string * string * string
      Ownership: OwnershipResult option
      StructuralStatus: string
      GeneratorChecksConsistentWithPlan: bool
      CompileStatus: string
      BehaviorStatus: string
      BehaviorReason: string
      Oracle: ConstructionOracle option
      Run: OracleRunRecord option
      Cases: CaseResult list
      EscapedConstraints: int
      DependencyStatus: string
      Containment: ContainmentAssessment
      SourceDocumentStatus: string
      Requirements: RequirementStatus list
      Counts: RequirementCounts
      Findings: ReportedFinding list
      ExceptionsApplied: int
      /// CONSTRUCT-008 mechanical-first checks, when a prepared artifact was supplied.
      Mechanical: MechanicalReport option
      Limitations: string list }

[<RequireQualifiedAccess>]
module ConstructionVerification =
    let private f = ConstructionChecks.finding

    let private parseAll (inputs: ConstructionInputs) =
        match ConstructionJson.parse "source manifest" inputs.SourceManifestJson,
              ConstructionJson.parse "blueprint" inputs.BlueprintJson,
              ConstructionModels.readPlan inputs.PlanJson,
              (match Map.tryFind inputs.ManifestPath inputs.Candidate.Contents with
               | None -> Error("construction manifest not found at " + inputs.ManifestPath + " in commit " + inputs.Candidate.Commit)
               | Some bytes -> ConstructionModels.readManifest (Text.Encoding.UTF8.GetString bytes)) with
        | Ok source, Ok blueprint, Ok plan, Ok manifest -> Ok(source, blueprint, plan, manifest)
        | a, b, c, d ->
            Error([ a |> Result.map ignore; b |> Result.map ignore; c |> Result.map ignore; d |> Result.map ignore ]
                  |> List.choose (function Error e -> Some e | Ok () -> None))

    let private exceptionApplies (at: DateTimeOffset) (finding: ConstructionFinding) (e: FindingException) =
        e.Code = finding.Code && e.Subject = finding.Subject
        && not (String.IsNullOrWhiteSpace e.Reason) && not (String.IsNullOrWhiteSpace e.Owner)
        && (e.Expires |> Option.exists (fun expires -> expires > at))

    let private behavior (inputs: ConstructionInputs) (oracle: ConstructionOracle option) usable =
        match oracle, inputs.OracleRun with
        | None, _ -> "unknown", "No independent oracle was supplied", [], None, "unknown"
        | Some _, _ when not usable -> "unknown", "The oracle failed its integrity checks; its results are not used", [], None, "unknown"
        | Some _, NotExecuted reason -> "unknown", reason, [], None, "unknown"
        | Some _, CompileFailed(run, _) -> "unknown", "The candidate's generated code did not compile under the oracle harness", [], Some run, "failed"
        | Some o, Executed(run, observations) ->
            let verdicts =
                o.Cases |> List.map (fun c -> c, ConstructionOracle.judge o c (observations |> List.tryFind (fun x -> x.CaseId = c.Id)))
            let status =
                if verdicts |> List.exists (snd >> function CaseFailed _ -> true | _ -> false) then "failed"
                elif verdicts |> List.forall (snd >> (=) CasePassed) then "passed"
                else "unknown"
            status, $"Dokimos ran {o.Cases.Length} frozen oracle cases against the candidate", verdicts, Some run, "passed"

    /// Verifies one construction commit; with `mechanical`, also the
    /// CONSTRUCT-008 mechanical-first checks.
    let verifyWith (mechanical: MechanicalInputs option) (inputs: ConstructionInputs) : Result<ConstructionVerificationReport, string list> =
        parseAll inputs
        |> Result.bind (fun parsed -> ConstructionJson.parse "plan" inputs.PlanJson |> Result.mapError List.singleton |> Result.map (fun p -> parsed, p))
        |> Result.map (fun ((source, blueprint, plan, manifest), planJson) ->
            let s = ConstructionJson.text
            let ecir = EcirAudit.audit inputs.SourceManifestJson inputs.BlueprintJson |> Result.toOption
            let sourceRows = ConstructionJson.array "requirements" source
            let sourceDigests = sourceRows |> List.map (fun r -> s "key" r, s "contentDigest" r) |> Map.ofList
            let blueprintRows =
                ConstructionJson.array "requirements" blueprint
                |> List.map (fun r -> (ConstructionJson.get "source" r |> Option.map (s "key") |> Option.defaultValue ""), r)
            let nodes = ConstructionJson.array "nodes" blueprint
            let cohortNodes = nodes |> List.filter (fun n -> s "kind" n = "cohort") |> List.map (s "id")
            let cohortKeys = nodes |> List.filter (fun n -> s "id" n = plan.CohortId) |> List.collect (ConstructionJson.strings "requirementKeys") |> Set.ofList
            let disposition key =
                blueprintRows |> List.tryFind (fst >> (=) key)
                |> Option.map (fun (_, r) -> ConstructionJson.get "disposition" r |> Option.map (s "kind") |> Option.defaultValue "")
                |> Option.defaultValue "missing"
            let assigned = cohortKeys |> Set.filter (fun k -> disposition k = "modeled")
            let blueprintDigest = ConstructionCanonical.blueprintDigest blueprint
            let sourceDigest = s "digest" source
            let reference = inputs.ReferenceManifestJson |> Option.bind (ConstructionModels.readManifest >> Result.toOption)
            let ownership =
                ConstructionEvidenceChecks.ownership inputs.ManifestPath manifest reference inputs.ExpectedPlanDigest inputs.ExpectedGenerator inputs.Candidate
            let artifactCode tier =
                manifest.Artifacts |> List.tryFind (fun a -> a.Tier = tier)
                |> Option.bind (fun a -> Map.tryFind (ownership.Mount + a.Path) inputs.Candidate.Contents)
                |> Option.map Text.Encoding.UTF8.GetString
            let oracle = inputs.OracleBytes |> Option.map ConstructionOracle.read
            let oracleValue = oracle |> Option.bind Result.toOption
            let oracleBlocking, oracleSources =
                match oracleValue with
                | None -> [], []
                | Some o ->
                    ConstructionEvidenceChecks.oracleIntegrity o inputs.ExpectedOracleDigest inputs.ExpectedGenerator plan.Workflow
                        sourceDigests inputs.Candidate ownership.Root
            let behaviorStatus, behaviorReason, verdicts, run, compile = behavior inputs oracleValue oracleBlocking.IsEmpty
            let verdictMap = verdicts |> List.map (fun (c, v) -> c.Id, v) |> Map.ofList
            let executed = match run, compile with | Some _, "passed" -> true | _ -> false
            let generatorChecksConsistent = List.sort manifest.StructuralPairs = List.sort (ConstructionModels.expectedPairs plan)
            let sourceDocFindings, sourceDocStatus =
                match inputs.SourceDocuments, oracleValue with
                | Some docs, Some o ->
                    [ for src in o.Sources do
                        match Map.tryFind src.Excerpt docs, Map.tryFind src.RequirementKey sourceDigests with
                        | Some bytes, Some digest when ConstructionJson.sha256Bytes bytes <> digest ->
                            f ConstructionCodes.SourceContentMismatch src.Excerpt [ src.RequirementKey ] "The excerpt's bytes do not hash to the pinned contentDigest"
                        | None, _ -> f ConstructionCodes.SourceContentMismatch src.Excerpt [ src.RequirementKey ] "A cited excerpt is missing from the supplied source documents"
                        | _ -> () ], "checked"
                | Some _, None -> [], "unknown: no oracle names the excerpts"
                | None, _ -> [], "unknown: source documents not supplied"
            let ecirFindings =
                ecir |> Option.map _.Findings |> Option.defaultValue []
                |> List.map (fun e ->
                    match e.Code with
                    | "missing-requirement" | "unmapped-requirement" ->
                        f ConstructionCodes.RequirementSilentlyOmitted e.RequirementKey [ e.RequirementKey ] ("ECIR audit: " + e.Detail)
                    | "invented-requirement" -> f ConstructionCodes.InventedBehavior e.RequirementKey [ e.RequirementKey ] ("ECIR audit: " + e.Detail)
                    | code -> f ("ecir-" + code) e.RequirementKey [ e.RequirementKey ] ("ECIR audit: " + e.Detail))
            let planScope =
                [ for key in Set.difference assigned plan.TracedKeys do
                      f ConstructionCodes.RequirementSilentlyOmitted key [ key ] "A modeled cohort requirement is traced by no plan operation"
                  for key in plan.TracedKeys do
                      if not (Map.containsKey key sourceDigests) then
                          f ConstructionCodes.InventedBehavior key [ key ] "The plan traces to a requirement absent from the original intake"
                      elif not (Set.contains key cohortKeys) then
                          f ConstructionCodes.InventedBehavior key [ key ] "The plan traces to a requirement outside its bound cohort" ]
            let hostFindings, containment = ConstructionEvidenceChecks.host inputs.Host inputs.Claims inputs.Candidate inputs.Base
            let dependencyFindings, dependencyStatus =
                match inputs.Base with
                | Some b -> ConstructionEvidenceChecks.dependencies inputs.Authorization inputs.Candidate b, "checked against " + b.Commit
                | None -> [], "unknown: no base commit"
            let claimed = inputs.ClaimedTests @ (inputs.Claims |> Option.map _.Tests |> Option.defaultValue [])
            let behaviorFindings =
                [ match inputs.OracleRun with
                  | CompileFailed(_, diagnostics) when oracleBlocking.IsEmpty && oracleValue.IsSome ->
                      f ConstructionCodes.CandidateCompileFailed inputs.Candidate.Commit [] diagnostics
                  | _ -> ()
                  match oracle with
                  | Some(Error reason) -> f ConstructionCodes.UnsupportedInput "oracle" [] reason
                  | _ -> ()
                  for c, v in verdicts do
                      match v with
                      | CaseFailed reason -> f ConstructionCodes.OracleBehaviorMismatch c.Id c.RequirementKeys reason
                      | _ -> () ]
            let existing =
                ecirFindings
                @ ConstructionChecks.binding inputs.ExpectedPlanDigest inputs.ExpectedGenerator sourceDigest blueprintDigest cohortNodes plan manifest
                @ planScope
                @ ConstructionChecks.manifestAgainstPlan plan assigned manifest
                @ ownership.Findings
                @ ConstructionChecks.generatedCode plan (artifactCode "transition") (artifactCode "semantic-model")
                @ ConstructionChecks.trailers inputs.ExpectedPlanDigest inputs.ExpectedGenerator plan
                    [ sourceDigest; ConstructionJson.sha256Text inputs.SourceManifestJson ]
                    [ blueprintDigest; ConstructionJson.sha256Text inputs.BlueprintJson ] inputs.Candidate.Message
                @ dependencyFindings @ hostFindings @ oracleBlocking @ oracleSources @ sourceDocFindings @ behaviorFindings
                @ ConstructionEvidenceChecks.claimedTests oracleValue verdictMap executed claimed
                |> List.distinct
            let mechanicalFindings, mechanicalReport =
                match mechanical with
                | None -> [], None
                | Some m ->
                    let acceptance =
                        sourceRows
                        |> List.map (fun r ->
                            let key = s "key" r
                            let covering = verdicts |> List.filter (fun (c, _) -> List.contains key c.RequirementKeys) |> List.map snd
                            key,
                            (if not executed || not oracleBlocking.IsEmpty then "unknown"
                             elif covering.IsEmpty then "none"
                             elif covering |> List.exists (function CaseFailed _ -> true | _ -> false) then "failed"
                             elif covering |> List.forall ((=) CasePassed) then "passed"
                             else "unknown"))
                        |> Map.ofList
                    let context =
                        { SourceManifest = source; SourceManifestText = inputs.SourceManifestJson
                          Blueprint = blueprint; BlueprintText = inputs.BlueprintJson
                          Plan = plan; ExpectedPlanDigest = inputs.ExpectedPlanDigest
                          ManifestPath = inputs.ManifestPath; Manifest = manifest; Ownership = ownership
                          Candidate = inputs.Candidate; Base = inputs.Base; Host = inputs.Host; Existing = existing
                          PlanJson = planJson; Acceptance = acceptance }
                    let found, report = ConstructionMechanical.assess m context
                    found, Some report
            let raw = existing @ mechanicalFindings |> List.distinct
            let reported =
                raw
                |> List.map (fun x ->
                    let excepted = inputs.Authorization.Exceptions |> List.exists (exceptionApplies inputs.CheckedAt x)
                    { Code = x.Code; Subject = x.Subject; RequirementKeys = x.RequirementKeys; Detail = x.Detail
                      Disposition = (if excepted then "excepted" else "open"); WorkItem = inputs.WorkItem })
                |> List.sortBy (fun x -> x.Code, x.Subject)
            let requirements =
                ConstructionRequirements.statuses
                    sourceRows blueprintRows cohortKeys plan manifest ownership oracleValue verdictMap
                    (behaviorStatus <> "unknown" || not verdicts.IsEmpty) inputs.Authorization reported
            let openFindings = reported |> List.filter (fun x -> x.Disposition = "open")
            let structural =
                let structuralCodes = set [ ConstructionCodes.GeneratedArtifactModified; ConstructionCodes.GeneratedArtifactMissing; ConstructionCodes.ManifestPlanDivergence
                                            ConstructionCodes.InventedBehavior; ConstructionCodes.MissingRequiredGuard; ConstructionCodes.UndocumentedGeneratedFile
                                            ConstructionCodes.HandwrittenClaimsGenerated; ConstructionCodes.NonRegularGeneratedEntry ]
                if openFindings |> List.exists (fun x -> Set.contains x.Code structuralCodes) then "nonconformant" else "conformant"
            let escaped =
                if generatorChecksConsistent then
                    verdicts |> List.filter (fun (c, v) -> c.Kind <> "port-surface" && (match v with CaseFailed _ -> true | _ -> false)) |> List.length
                else 0
            let unknownChecks =
                mechanicalReport |> Option.map _.Checks |> Option.defaultValue [] |> List.filter (fun c -> c.Status = "unknown")
            let openItems = mechanicalReport |> Option.map _.Decisions.OpenItems |> Option.defaultValue []
            let incompleteReasons =
                [ if behaviorStatus <> "passed" then yield "Independent behavioral acceptance is " + behaviorStatus + ": " + behaviorReason
                  for c in unknownChecks do yield $"Mechanical-first check {c.Name} is unknown: {c.Detail}"
                  if not openItems.IsEmpty then
                      let byClass =
                          openItems |> List.countBy _.Class |> List.map (fun (k, n) -> $"{n} {k}") |> String.concat ", "
                      let subjects = openItems |> List.map _.Subject |> String.concat ", "
                      yield $"Intentionally incomplete: {openItems.Length} open decision item(s) ({byClass}) may not be generated as completed construction: {subjects}" ]
            let verdict, code, reasons =
                if not openFindings.IsEmpty then "rejected", 4, [ $"{openFindings.Length} open finding(s)" ]
                elif not incompleteReasons.IsEmpty then "incomplete", 3, incompleteReasons
                else
                    let partial = requirements |> List.filter (fun r -> r.IndependentlyVerified = "partial") |> List.length
                    let pending = requirements |> List.filter (fun r -> r.Implemented = "unknown") |> List.length
                    "verified", 0,
                    [ "No open findings and every frozen oracle case passed under a Dokimos-observed run"
                      $"This verifies the construction, not feature completion: {partial} requirement(s) are only partially observable by the oracle and {pending} depend on unassessed handwritten obligations" ]
            { Verdict = verdict
              ExitCode = code
              Reasons = reasons
              SourceConservation = ecir
              PlanDigest = inputs.ExpectedPlanDigest, plan.RecomputedDigest, manifest.PlanDigest
              Ownership = Some ownership
              StructuralStatus = structural
              GeneratorChecksConsistentWithPlan = generatorChecksConsistent
              CompileStatus = compile
              BehaviorStatus = behaviorStatus
              BehaviorReason = behaviorReason
              Oracle = oracleValue
              Run = run
              Cases =
                verdicts
                |> List.map (fun (c, v) ->
                    let verdict, detail = match v with CasePassed -> "passed", "" | CaseFailed r -> "failed", r | CaseUnknown r -> "unknown", r
                    { Id = c.Id; RequirementKeys = c.RequirementKeys; Verdict = verdict; Detail = detail; Citation = c.Citation })
              EscapedConstraints = escaped
              DependencyStatus = dependencyStatus
              Containment = containment
              SourceDocumentStatus = sourceDocStatus
              Requirements = requirements
              Counts = ConstructionRequirements.counts requirements sourceRows.Length
              Findings = reported
              ExceptionsApplied = reported |> List.filter (fun x -> x.Disposition = "excepted") |> List.length
              Mechanical = mechanicalReport
              Limitations =
                [ "Generated-code structure checks read the published ordo.construction/1 file shape; another generator version needs its own reader."
                  "The oracle harness drives Tier 2 decide and the ports interface by reflection under dotnet fsi; guard/capability rule records and handwritten Tier 4 hosts are not driven."
                  "Handwritten extension contracts (IMPLEMENT obligations) are not assessed; requirements that depend on them report implemented=unknown."
                  "Host observations and authorization are taken from the supplied files; Dokimos checks their consistency, not their cryptographic authenticity." ] })

    let verify (inputs: ConstructionInputs) = verifyWith None inputs
