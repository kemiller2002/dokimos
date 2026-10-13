namespace Dokimos.Core

open System
open System.Text

/// The CONSTRUCT-008 mechanical-first checks. Every check is computed from
/// Git trees, the frozen inputs and the artifacts' declared JSON; the plan is
/// never its own proof.
[<RequireQualifiedAccess>]
module ConstructionMechanical =
    let private f = ConstructionChecks.finding

    module Names =
        let Provenance = "plan-provenance"
        let Coverage = "requirement-coverage"
        let Determinism = "deterministic-generation"
        let Obligations = "obligation-tracking"
        let Mutations = "file-mutations"
        let Boundary = "extension-boundary"
        let Architecture = "architecture-compliance"
        let Rejections = "rejected-operations"
        let all = [ Provenance; Coverage; Determinism; Obligations; Mutations; Boundary; Architecture; Rejections ]

    /// Build, solution, project and package files a handwritten extension
    /// may never touch.
    let isBuildOrDependencyFile (path: string) =
        let name = (IO.Path.GetFileName path |> string).ToLowerInvariant()
        ConstructionEvidence.isProjectFile path
        || name.EndsWith ".sln" || name.EndsWith ".slnx" || name = "nuget.config" || name = "global.json"
        || name = "packages.lock.json" || name = "paket.lock" || name = "directory.packages.props"

    /// Paths whose entry (mode or blob) differs between two trees.
    let changedPaths (before: Map<string, TreeEntry>) (after: Map<string, TreeEntry>) =
        Set.union (before |> Map.keys |> Set.ofSeq) (after |> Map.keys |> Set.ofSeq)
        |> Set.filter (fun p -> Map.tryFind p before <> Map.tryFind p after)

    /// A check and the findings it raised; the report lists their codes.
    let private check name (findings: ConstructionFinding list) (unknown: string option) (passed: string) =
        let status = if not findings.IsEmpty then "failed" elif unknown.IsSome then "unknown" else "passed"
        { Name = name
          Status = status
          Detail =
            match status, unknown with
            | "failed", _ -> $"{findings.Length} finding(s)" + (unknown |> Option.map (fun u -> "; also " + u) |> Option.defaultValue "")
            | "unknown", Some u -> u
            | _ -> passed
          Findings = findings |> List.map (fun x -> x.Code + ":" + x.Subject) |> List.distinct },
        findings

    let private architectureCodes =
        set [ ConstructionCodes.GeneratedArtifactModified; ConstructionCodes.GeneratedArtifactMissing; ConstructionCodes.ManifestPlanDivergence
              ConstructionCodes.InventedBehavior; ConstructionCodes.MissingRequiredGuard; ConstructionCodes.UndocumentedGeneratedFile
              ConstructionCodes.HandwrittenClaimsGenerated; ConstructionCodes.NonRegularGeneratedEntry; ConstructionCodes.ManifestLocationMismatch
              ConstructionCodes.ArtifactOutsideOwnershipRoot; ConstructionCodes.BindingMismatch; ConstructionCodes.PlanDigestChanged
              ConstructionCodes.GeneratorVersionChanged; ConstructionCodes.GeneratorSelfAttestation; ConstructionCodes.UnauthorizedDependency
              ConstructionCodes.CandidateCompileFailed; ConstructionCodes.OracleBehaviorMismatch ]

    /// The existing structural and oracle checks, summarized; it adds no
    /// finding of its own, so nothing is counted twice.
    let private architecture (ctx: MechanicalContext) =
        let relevant = ctx.Existing |> List.filter (fun x -> Set.contains x.Code architectureCodes)
        { Name = Names.Architecture
          Status = (if relevant.IsEmpty then "passed" else "failed")
          Detail =
            if relevant.IsEmpty then "The existing structural, ownership, binding and dependency checks (DOK-ORACLE-TIMER-1 when supplied) report no finding"
            else $"{relevant.Length} finding(s) of the existing structural, ownership, binding, dependency or oracle checks"
          Findings = relevant |> List.map (fun x -> x.Code + ":" + x.Subject) |> List.distinct },
        ([]: ConstructionFinding list)

    let private provenance (m: MechanicalInputs) (ctx: MechanicalContext) (p: PreparedArtifact) =
        let code = MechanicalCodes.ProvenanceDigestMismatch
        let recomputed = ConstructionCanonical.planDigest p.Plan
        let blueprintDigests = [ ConstructionCanonical.blueprintDigest ctx.Blueprint; ConstructionJson.sha256Text ctx.BlueprintText ]
        let sourceDigests = [ ConstructionJson.text "digest" ctx.SourceManifest; ConstructionJson.sha256Text ctx.SourceManifestText ] |> List.filter ((<>) "")
        let declared key = Map.tryFind key p.InputDigests |> Option.defaultValue ""
        let decisions =
            m.DecisionRecordsBytes
            |> Option.map (fun bytes ->
                let raw = ConstructionJson.sha256Bytes bytes
                match ConstructionJson.parse "decision records" (Encoding.UTF8.GetString bytes) with
                | Error e -> raw, [ f code "decisionRecords" [] e ]
                | Ok d ->
                    let canonical = ConstructionJson.sha256Text (ConstructionCanonical.canonicalText d)
                    let recordIds = ConstructionJson.array "records" d |> List.map (ConstructionJson.text "nodeId") |> Set.ofList
                    let declaredRecords = declared "decisionRecords"
                    let schema = ConstructionJson.text "schemaVersion" d
                    let approvedAgainst = ConstructionJson.text "blueprintDigest" d
                    let cohort = ConstructionJson.text "cohortId" d
                    raw,
                    [ if not (List.contains declaredRecords [ raw; canonical ]) then
                          f code "provenance.inputDigests.decisionRecords" []
                              $"The prepared artifact declares decision records {declaredRecords}; the supplied file hashes to {raw} (canonical {canonical})"
                      if schema <> MechanicalWire.DecisionsSchema then
                          f code "decisionRecords.schemaVersion" [] $"The decision records are '{schema}', not {MechanicalWire.DecisionsSchema}"
                      if not (List.contains approvedAgainst blueprintDigests) then
                          f code "decisionRecords.blueprintDigest" [] $"The decision records were approved against blueprint {approvedAgainst}, not the supplied blueprint"
                      if cohort <> ctx.Plan.CohortId then
                          f code "decisionRecords.cohortId" [] $"The decision records name cohort '{cohort}'; the plan is bound to {ctx.Plan.CohortId}"
                      for id in ctx.Plan.DecisionIds do
                          if not (Set.contains id recordIds) then
                              f ConstructionCodes.BindingMismatch ("decisionRecords:" + id) [] "The plan binds a decision that has no approved decision record" ])
        let declaredBlueprint = declared "blueprint"
        let declaredSource = declared "sourceManifest"
        let suppliedBlueprint = String.concat " or " blueprintDigests
        let suppliedSource = String.concat " or " sourceDigests
        let findings =
            [ if recomputed <> p.DeclaredPlanDigest then
                  f code "defaultPlan.planDigest" [] $"The prepared plan recomputes to {recomputed}; the artifact declares {p.DeclaredPlanDigest}"
              if recomputed <> ctx.ExpectedPlanDigest then
                  f code "defaultPlan.plan" [] $"The prepared plan recomputes to {recomputed}, not the pinned {ctx.ExpectedPlanDigest}"
              if recomputed <> ctx.Plan.RecomputedDigest then
                  f code "plan" [] $"The verified plan recomputes to {ctx.Plan.RecomputedDigest}; the prepared artifact's plan to {recomputed}"
              if p.GeneratorId <> MechanicalWire.DefaultPlanGenerator then
                  f code "provenance.generatorId" [] $"The plan was produced by '{p.GeneratorId}', not {MechanicalWire.DefaultPlanGenerator}"
              if not (List.contains declaredBlueprint blueprintDigests) then
                  f code "provenance.inputDigests.blueprint" []
                      $"The prepared artifact declares blueprint {declaredBlueprint}; the supplied blueprint is {suppliedBlueprint} (canonical or file digest)"
              if not (List.contains declaredSource sourceDigests) then
                  f code "provenance.inputDigests.sourceManifest" []
                      $"The prepared artifact declares source manifest {declaredSource}; the supplied manifest is {suppliedSource} (declared or file digest)"
              yield! decisions |> Option.map snd |> Option.defaultValue [] ]
        let unknown = if m.DecisionRecordsBytes.IsNone then Some "decision records were not supplied (--decisions); their declared digest is unverified" else None
        check Names.Provenance findings unknown
            "The prepared plan, its declared digest, the pinned digest and every declared input digest agree with the supplied files",
        decisions |> Option.map fst |> Option.defaultValue ""

    let private coverage (ctx: MechanicalContext) (p: PreparedArtifact) =
        let s = ConstructionJson.text
        let cohortKeys =
            ConstructionJson.array "nodes" ctx.Blueprint
            |> List.filter (fun n -> s "id" n = ctx.Plan.CohortId)
            |> List.collect (ConstructionJson.strings "requirementKeys") |> Set.ofList
        let sourceKeys = ConstructionJson.array "requirements" ctx.SourceManifest |> List.map (s "key") |> Set.ofList
        let obligationKeys = p.Obligations |> List.collect _.RequirementKeys |> Set.ofList
        let findings =
            [ if cohortKeys.IsEmpty then
                  f ConstructionCodes.RequirementSilentlyOmitted ctx.Plan.CohortId [] "The approved blueprint has no requirement keys for the plan's cohort"
              for key in Set.difference cohortKeys (Set.union ctx.Plan.TracedKeys obligationKeys) do
                  f ConstructionCodes.RequirementSilentlyOmitted key [ key ] "A cohort requirement is covered by neither a plan operation nor an obligation"
              for o in p.Obligations do
                  for key in o.RequirementKeys do
                      if not (Set.contains key sourceKeys) then
                          f ConstructionCodes.InventedBehavior (o.Id + "@" + key) [ key ] "An obligation cites a requirement absent from the original intake"
                      elif not (Set.contains key cohortKeys) && o.Kind <> "conflict" then
                          f ConstructionCodes.InventedBehavior (o.Id + "@" + key) [ key ] $"A {o.Kind} obligation cites a requirement outside the cohort; only conflict obligations may" ]
        check Names.Coverage findings None $"All {cohortKeys.Count} cohort requirement(s) are covered by a plan operation or an obligation"

    let private resolve (ctx: MechanicalContext) (path: string) =
        let mount = ctx.Ownership.Mount
        if mount = "" || path.StartsWith(mount, StringComparison.Ordinal) then path else mount + path

    let private determinism (m: MechanicalInputs) (ctx: MechanicalContext) (p: PreparedArtifact) (generated: Set<string>) =
        let code = MechanicalCodes.NondeterministicGeneration
        let prepared, preparedNote =
            match m.PreparedReplicaBytes with
            | None -> [], None
            | Some bytes ->
                match MechanicalWire.readPrepared bytes with
                | Error e -> [ f MechanicalCodes.PreparedArtifactInvalid "prepared-replica" [] (String.concat "; " e) ], Some "compared"
                | Ok r when r.CacheKey <> p.CacheKey ->
                    [], Some $"the replica prepared artifact has a different cache key ({r.CacheKey}); different inputs prove nothing about determinism"
                | Ok r ->
                    [ if r.Digest <> p.Digest then
                          f code "prepared" [] $"Two prepared artifacts with cache key {p.CacheKey} differ: {p.Digest} vs {r.Digest}" ],
                    Some "compared"
        let trees, treeNote =
            match m.Replica with
            | None -> [], None
            | Some replica ->
                let ours = m.GeneratedCommit |> Option.map _.Entries |> Option.defaultValue ctx.Candidate.Entries
                [ for path in generated do
                    match Map.tryFind path ours, Map.tryFind path replica.Entries with
                    | Some a, Some b when a = b -> ()
                    | Some _, Some _ -> f code path [] $"The generated file differs from the replica built from identical inputs at {replica.Commit}"
                    | None, _ -> f code path [] "The generated file is missing from the candidate"
                    | _, None -> f code path [] $"The generated file is missing from the replica at {replica.Commit}" ],
                Some "compared"
        let unknown =
            match preparedNote, treeNote with
            | None, None -> Some "no replica was supplied (--prepared-replica or --replica-commit); determinism is unobserved"
            | Some n, None when n <> "compared" -> Some n
            | _ -> None
        check Names.Determinism (prepared @ trees) unknown
            "Replicas from identical inputs are byte-identical (prepared artifact and/or every generated file)"

    let private obligationTracking (m: MechanicalInputs) (dispositions: Result<DispositionRecord, string list> option)
                                   (p: PreparedArtifact) (handwritten: Set<string>) (permitted: Set<string>) (closed: Set<string>) =
        let ids = p.Obligations |> List.map _.Id
        let kindOf id = p.Obligations |> List.tryFind (fun o -> o.Id = id) |> Option.map _.Kind |> Option.defaultValue ""
        let handoffFindings =
            match p.HandoffObligationIds with
            | None -> []
            | Some handed ->
                [ for id in ids do
                    if not (List.contains id handed) then
                        f MechanicalCodes.ObligationDropped id [] "The agent handoff omits an obligation of the default plan"
                  for id in handed do
                    if not (List.contains id ids) && not (MechanicalObligations.isAuxiliaryId id) then
                        f MechanicalCodes.ObligationDispositionInvalid id [] "The agent handoff names an obligation the default plan does not carry" ]
        let keysOf id = p.Obligations |> List.tryFind (fun o -> o.Id = id) |> Option.map _.RequirementKeys |> Option.defaultValue []
        let invalid = MechanicalCodes.ObligationDispositionInvalid
        match dispositions with
        | None ->
            let unknown = if ids.IsEmpty then None else Some "no obligation dispositions were supplied (--dispositions)"
            check Names.Obligations handoffFindings unknown "The default plan carries no obligation", []
        | Some(Error e) ->
            check Names.Obligations (handoffFindings @ [ f invalid "dispositions" [] (String.concat "; " e) ]) None "", []
        | Some(Ok record) ->
            let byId = record.Dispositions |> List.groupBy _.ObligationId |> Map.ofList
            let findings =
                [ yield! handoffFindings
                  for id in ids do
                      match Map.tryFind id byId |> Option.defaultValue [] with
                      | [] -> f MechanicalCodes.ObligationDropped id (keysOf id) "The obligation has no recorded disposition (implemented or escalated)"
                      | entries when (entries |> List.map _.Status |> List.distinct |> List.length) > 1 ->
                          f invalid id (keysOf id) "The obligation has conflicting dispositions"
                      | d :: _ ->
                          if not (Set.contains d.Status MechanicalWire.dispositionStatuses) then
                              f invalid id (keysOf id) $"Disposition '{d.Status}' is not implemented or escalated"
                          if d.Status = "escalated" && String.IsNullOrWhiteSpace d.Note then
                              f invalid id (keysOf id) "An escalated obligation must record its unresolved question in note"
                          if d.Status = "implemented" && Set.contains (kindOf id) MechanicalObligations.unresolvedKinds && not (Set.contains id closed) then
                              f MechanicalCodes.UnresolvedObligationClaimedImplemented id (keysOf id)
                                  $"A {kindOf id} obligation records an undecided question; it may only be escalated unless an approved decision record closes it"
                          if d.Status = "implemented" && handwritten.IsEmpty then
                              f invalid id (keysOf id) "The obligation is claimed implemented but the candidate changes no permitted extension file"
                          for file in d.Files do
                              if not (Set.contains file permitted) then
                                  f invalid (id + "@" + file) (keysOf id) "The disposition names a file outside the permitted extension files"
                  for KeyValue(id, _) in byId do
                      if not (List.contains id ids) && not (MechanicalObligations.isAuxiliaryId id) then
                          f invalid id [] "A disposition for an obligation the default plan does not carry" ]
            let traces =
                p.Obligations
                |> List.map (fun o ->
                    { Id = o.Id; Kind = o.Kind; RequirementKeys = o.RequirementKeys
                      Disposition = Map.tryFind o.Id byId |> Option.bind List.tryHead |> Option.map _.Status |> Option.defaultValue "missing" })
            check Names.Obligations findings None $"All {ids.Length} obligation(s) are dispositioned (implemented or escalated)", traces

    let private mutations (m: MechanicalInputs) (ctx: MechanicalContext) (allowed: Set<string>) (generated: Set<string>) =
        match ctx.Base with
        | None -> check Names.Mutations [] (Some "no base commit; the candidate's changes cannot be computed") ""
        | Some b ->
            let changed = changedPaths b.Entries ctx.Candidate.Entries
            let applyCommit =
                match m.GeneratedCommit with
                | None -> []
                | Some g ->
                    [ for path in changedPaths b.Entries g.Entries do
                        if not (Set.contains path generated) then
                            f MechanicalCodes.UnauthorizedFileMutation path []
                                $"The generated apply commit {g.Commit} changes a path outside the generated layout" ]
            let findings =
                [ for path in changed do
                    if not (Set.contains path allowed) then
                        f MechanicalCodes.UnauthorizedFileMutation path []
                            $"Changed between {b.Commit} and {ctx.Candidate.Commit}; it is neither a generated path nor a permitted extension file"
                  yield! applyCommit ]
            check Names.Mutations findings None $"{changed.Count} changed path(s), all generated or permitted extension files"

    let private boundary (m: MechanicalInputs) (ctx: MechanicalContext) (p: PreparedArtifact) (generated: Set<string>)
                         (permitted: Set<string>) (handwritten: Set<string>) =
        let code = MechanicalCodes.ExtensionBoundaryViolation
        let root = ctx.Ownership.Root
        let isGenerated (path: string) = Set.contains path generated || (root <> "" && path.StartsWith(root, StringComparison.Ordinal))
        let policy =
            [ for path in permitted do
                if isGenerated path then f code path [] "The prepared artifact permits handwritten edits to a generated path"
                elif isBuildOrDependencyFile path then f code path [] "The prepared artifact permits handwritten edits to a project, build or package file" ]
        let edits =
            [ for path in handwritten do
                if isGenerated path then
                    f MechanicalCodes.GeneratedFileEdited path [] "A handwritten change edits a generated file"
                elif isBuildOrDependencyFile path then
                    f code path [] "A handwritten change edits a project, build or package/dependency file"
                elif not (Set.contains path permitted) then
                    f code path [] "A handwritten change lies outside the permitted extension files" ]
        let layoutBytes =
            [ for l in p.GeneratedLayout do
                match l.Sha256, Map.tryFind (resolve ctx l.Path) ctx.Candidate.Contents with
                | Some expected, Some bytes when ConstructionJson.sha256Bytes bytes <> expected ->
                    f ConstructionCodes.GeneratedArtifactModified (resolve ctx l.Path) [] "Committed bytes differ from the SHA-256 the prepared layout declares"
                | _ -> () ]
        let unknown =
            if m.GeneratedCommit.IsNone && ctx.Base.IsNone then Some "neither --generated-commit nor a base commit; handwritten changes cannot be isolated"
            else None
        check Names.Boundary (policy @ edits @ layoutBytes) unknown
            $"{handwritten.Count} handwritten path(s), all permitted; no generated, project or dependency file was edited by hand"

    let private rejections (dispositions: Result<DispositionRecord, string list> option) (ctx: MechanicalContext)
                           (allowed: Set<string>) (changed: Set<string>) =
        let reported = match dispositions with Some(Ok d) -> d.RejectedOperations | _ -> []
        match ctx.Host with
        | None ->
            let findings =
                [ for r in reported ->
                    f MechanicalCodes.RejectedOperationUnrecorded r.Path [] "The agent reports a host refusal, but no host refusal record was supplied" ]
            check Names.Rejections findings (Some "no host refusal record was supplied (--host-observations)") ""
        | Some host ->
            let recorded = host.Observations |> List.filter (fun o -> o.RecordedBy = "host")
            let denied = recorded |> List.filter (fun o -> o.Outcome = "denied")
            let findings =
                [ for r in reported do
                    if not (denied |> List.exists (fun o -> o.Path = r.Path)) then
                        f MechanicalCodes.RejectedOperationUnrecorded r.Path [] "The agent reports a host refusal that the host did not record"
                  for o in denied do
                    if Set.contains o.Path changed && not (Set.contains o.Path allowed) then
                        f MechanicalCodes.RejectedOperationApplied o.Path [] $"The host recorded refusal {o.Id}, yet the candidate contains the change"
                  for o in recorded do
                    if o.Outcome = "allowed" && o.Path <> "" && not (Set.contains o.Path allowed) then
                        f MechanicalCodes.UnauthorizedFileMutation o.Path [] $"The host recorded write {o.Id} outside the permitted paths as allowed; it must be refused and recorded" ]
            check Names.Rejections findings None $"{denied.Length} host-recorded refusal(s); every reported refusal is recorded and none was applied"

    /// Runs every mechanical-first check. Returns new findings and the report.
    let assess (m: MechanicalInputs) (ctx: MechanicalContext) : ConstructionFinding list * MechanicalReport =
        let dispositions = m.DispositionsJson |> Option.map MechanicalWire.readDispositions
        let changed = ctx.Base |> Option.map (fun b -> changedPaths b.Entries ctx.Candidate.Entries) |> Option.defaultValue Set.empty
        match MechanicalWire.readPrepared m.PreparedBytes with
        | Error errors ->
            let findings = errors |> List.map (fun e -> f MechanicalCodes.PreparedArtifactInvalid "prepared" [] e)
            let unknown name = { Name = name; Status = "unknown"; Detail = "The prepared artifact is invalid"; Findings = [] }
            let checks =
                Names.all |> List.map (fun n ->
                    if n = Names.Provenance then fst (check n findings None "")
                    elif n = Names.Architecture then fst (architecture ctx)
                    else unknown n)
            findings,
            { PreparedDigest = ConstructionJson.sha256Bytes m.PreparedBytes; CacheKey = ""; GeneratorId = ""; GeneratorVersion = ""
              VocabularyVersion = ""; DecisionRecordsDigest = ""; GeneratedCommit = ""; ReplicaCommit = ""
              GeneratedPaths = []; PermittedExtensionFiles = []; HandwrittenPaths = []; Obligations = []; Checks = checks }
        | Ok p ->
            let manifestArtifacts = ctx.Manifest.Artifacts |> List.map (fun a -> ctx.Ownership.Mount + a.Path) |> Set.ofList
            let layout = p.GeneratedLayout |> List.map (fun l -> resolve ctx l.Path) |> Set.ofList
            let generated = Set.unionMany [ layout; manifestArtifacts; set [ ctx.ManifestPath ] ]
            let permitted = Set.ofList (p.PermittedExtensionFiles @ (p.PermittedExtensionSlots |> List.map snd))
            let allowed = Set.union generated permitted
            let handwritten =
                match m.GeneratedCommit with
                | Some g -> changedPaths g.Entries ctx.Candidate.Entries
                | None -> Set.difference changed generated
            let layoutFindings =
                [ for path in Set.difference (Set.remove ctx.ManifestPath layout) manifestArtifacts do
                    f MechanicalCodes.GeneratedLayoutMismatch path [] "The prepared layout names a generated file the construction manifest does not list"
                  for path in Set.difference manifestArtifacts layout do
                    f MechanicalCodes.GeneratedLayoutMismatch path [] "The construction manifest lists a generated file the prepared layout does not name"
                  for path in layout do
                    if not (Map.containsKey path ctx.Candidate.Entries) then
                        f ConstructionCodes.GeneratedArtifactMissing path [] "The prepared layout names a generated file the candidate commit does not contain" ]
            let (provenanceCheck, provenanceFindings), decisionsDigest = provenance m ctx p
            let (obligationCheck, obligationFindings), traces = obligationTracking m dispositions p handwritten permitted (MechanicalObligations.closedObligations m p)
            let boundaryCheck, boundaryFindings = boundary m ctx p generated permitted handwritten
            let boundaryWithLayout =
                if layoutFindings.IsEmpty then boundaryCheck
                else
                    { boundaryCheck with
                        Status = "failed"
                        Findings = boundaryCheck.Findings @ (layoutFindings |> List.map (fun x -> x.Code + ":" + x.Subject))
                        Detail = (if boundaryCheck.Status = "failed" then boundaryCheck.Detail + "; " else "") + "the prepared layout and the construction manifest disagree" }
            let results =
                [ (provenanceCheck, provenanceFindings)
                  coverage ctx p
                  determinism m ctx p generated
                  (obligationCheck, obligationFindings)
                  mutations m ctx allowed generated
                  (boundaryWithLayout, boundaryFindings @ layoutFindings)
                  architecture ctx
                  rejections dispositions ctx allowed changed ]
            (results |> List.collect snd |> List.distinct),
            { PreparedDigest = p.Digest; CacheKey = p.CacheKey; GeneratorId = p.GeneratorId; GeneratorVersion = p.GeneratorVersion
              VocabularyVersion = p.VocabularyVersion; DecisionRecordsDigest = decisionsDigest
              GeneratedCommit = m.GeneratedCommit |> Option.map _.Commit |> Option.defaultValue ""
              ReplicaCommit = m.Replica |> Option.map _.Commit |> Option.defaultValue ""
              GeneratedPaths = generated |> Set.toList
              PermittedExtensionFiles = permitted |> Set.toList
              HandwrittenPaths = handwritten |> Set.toList
              Obligations = traces
              Checks = results |> List.map fst }
