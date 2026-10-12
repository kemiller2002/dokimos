namespace Dokimos.Core

open System

/// Containment as Dokimos can establish it from host evidence alone.
type ContainmentAssessment =
    { Status: string
      Reason: string
      HostId: string
      Mechanism: string
      UnavailableProofs: string list
      HostRecordedDenials: int
      UnenforcedProtectedWrites: int }

/// Artifact digest results: (path, status) where status is verified,
/// modified, missing or non-regular.
type OwnershipResult =
    { Findings: ConstructionFinding list
      Artifacts: (string * string) list
      Mount: string
      Root: string }

[<RequireQualifiedAccess>]
module ConstructionEvidenceChecks =
    let private f = ConstructionChecks.finding

    let private blobOf (snapshot: CandidateSnapshot) path = Map.tryFind path snapshot.Entries

    /// Ownership and byte identity of every generated artifact at the
    /// candidate commit, against the manifest and an optional pinned
    /// reference manifest (the trusted generator's own output).
    let ownership (manifestPath: string) (manifest: ConstructionManifest) (reference: ConstructionManifest option)
                  (expectedPlan: string) (expectedGenerator: string) (candidate: CandidateSnapshot) =
        let suffix = manifest.OwnershipRoot + "construction-manifest.json"
        let located = manifestPath.EndsWith(suffix, StringComparison.Ordinal) && manifest.OwnershipRoot <> ""
        let mount = if located then manifestPath.Substring(0, manifestPath.Length - suffix.Length) else ""
        let root = mount + manifest.OwnershipRoot
        let artifactResults =
            manifest.Artifacts
            |> List.map (fun a ->
                let path = mount + a.Path
                let status =
                    match blobOf candidate path, Map.tryFind path candidate.Contents with
                    | None, _ | _, None -> "missing"
                    | Some entry, _ when entry.Mode <> "100644" -> "non-regular"
                    | Some _, Some bytes when ConstructionJson.sha256Bytes bytes <> a.Sha256 -> "modified"
                    | Some _, Some _ -> "verified"
                a, path, status)
        let artifactPaths = artifactResults |> List.map (fun (_, p, _) -> p) |> Set.ofList
        let verifiedPaths = artifactResults |> List.choose (fun (_, p, s) -> if s = "verified" then Some p else None) |> Set.ofList
        let inRoot = candidate.Entries |> Map.toList |> List.map fst |> List.filter (fun p -> root <> "" && p.StartsWith(root, StringComparison.Ordinal))
        let findings =
            [ if not located then
                  f ConstructionCodes.ManifestLocationMismatch manifestPath [] ("The manifest is not at its own ownership root " + manifest.OwnershipRoot)
              for a, path, status in artifactResults do
                  if not (a.Path.StartsWith(manifest.OwnershipRoot, StringComparison.Ordinal)) || a.Ownership <> "generated" then
                      f ConstructionCodes.ArtifactOutsideOwnershipRoot path a.RequirementKeys "A manifest artifact lies outside the generated ownership root or is not generator-owned"
                  match status with
                  | "missing" -> f ConstructionCodes.GeneratedArtifactMissing path a.RequirementKeys "The manifest names an artifact the candidate commit does not contain"
                  | "non-regular" -> f ConstructionCodes.NonRegularGeneratedEntry path a.RequirementKeys "A generated artifact is a symlink, submodule or executable entry"
                  | "modified" -> f ConstructionCodes.GeneratedArtifactModified path a.RequirementKeys "Committed bytes differ from the manifest's SHA-256"
                  | _ -> ()
                  match Map.tryFind path candidate.Contents |> Option.bind ConstructionChecks.marker with
                  | Some(generator, _) when generator <> expectedGenerator ->
                      f ConstructionCodes.GeneratorVersionChanged path a.RequirementKeys $"The file header names generator {generator}"
                  | Some(_, digest) when digest <> expectedPlan ->
                      f ConstructionCodes.PlanDigestChanged path a.RequirementKeys $"The file header names plan {digest}"
                  | _ -> ()
              match reference with
              | None -> ()
              | Some r ->
                  for ra in r.Artifacts do
                      match manifest.Artifacts |> List.tryFind (fun a -> a.Path = ra.Path) with
                      | Some a when a.Sha256 = ra.Sha256 -> ()
                      | _ -> f ConstructionCodes.GeneratedArtifactModified (mount + ra.Path) ra.RequirementKeys "Differs from the pinned generator's output for this plan"
                  for a in manifest.Artifacts do
                      if not (r.Artifacts |> List.exists (fun ra -> ra.Path = a.Path)) then
                          f ConstructionCodes.InventedBehavior (mount + a.Path) a.RequirementKeys "An artifact the pinned generator does not produce for this plan"
                  if ConstructionCanonical.canonicalText manifest.Root <> ConstructionCanonical.canonicalText r.Root then
                      f ConstructionCodes.GeneratedArtifactModified manifestPath [] "The manifest differs from the pinned generator's manifest for this plan"
              for path in inRoot do
                  if path <> manifestPath && not (Set.contains path artifactPaths) then
                      f ConstructionCodes.UndocumentedGeneratedFile path [] "A file inside the generated ownership root that the manifest does not list"
              for path in candidate.MarkerPaths do
                  if not (Set.contains path verifiedPaths) then
                      f ConstructionCodes.HandwrittenClaimsGenerated path [] "Carries the generated marker but is not a manifest artifact with matching bytes" ]
        { Findings = findings
          Artifacts = artifactResults |> List.map (fun (_, p, s) -> p, s)
          Mount = mount
          Root = root }

    let dependencies (authorization: ConstructionAuthorization) (candidate: CandidateSnapshot) (baseline: BaseSnapshot) =
        ConstructionEvidence.dependencyChanges baseline.ProjectFiles candidate.ProjectFiles
        |> List.filter (ConstructionEvidence.isAuthorized authorization >> not)
        |> List.map (fun c ->
            f ConstructionCodes.UnauthorizedDependency
                $"{c.Reference.Project}:{c.Reference.Kind}:{c.Reference.Include}" []
                $"{c.Reference.Kind} reference {c.Reference.Include} {c.Reference.Version} was {c.Change} without authorization")

    let private hostRecorded (o: HostObservation) = o.RecordedBy = "host"

    /// Host denial, containment and protected-write evidence. Only an
    /// observation the host recorded counts; a clean diff never does.
    let host (host: HostEvidence option) (claims: ConstructionClaims option) (candidate: CandidateSnapshot) (baseline: BaseSnapshot option) =
        let observations = host |> Option.map _.Observations |> Option.defaultValue []
        let denied (attemptId: string) (path: string) =
            observations
            |> List.exists (fun o ->
                hostRecorded o && o.Outcome = "denied"
                && ((attemptId <> "" && o.AttemptId = attemptId) || (attemptId = "" && o.Path = path)))
        let unenforced = observations |> List.filter (fun o -> o.Protected && o.Kind = "write" && o.Outcome <> "denied")
        let cleanAt path =
            baseline |> Option.map (fun b -> Map.tryFind path b.Entries = Map.tryFind path candidate.Entries)
        let findings =
            [ for d in claims |> Option.map _.Denials |> Option.defaultValue [] do
                  if not (denied d.AttemptId d.Path) then
                      f ConstructionCodes.HostDenialWithoutEvidence (if d.AttemptId <> "" then d.AttemptId else d.Path) []
                          $"Denial of a write to {d.Path} is claimed with '{d.Evidence}' evidence, but no host-recorded denial exists"
              for o in unenforced do
                  f ConstructionCodes.ProtectedWriteAllowed o.Path []
                      $"{o.RecordedBy} recorded a write to protected {o.Path} with outcome '{o.Outcome}', not a denial"
                  if cleanAt o.Path = Some true then
                      f ConstructionCodes.CleanDiffNotDenial o.Path []
                          "The post-hoc diff of this protected path is clean, but the write was attempted and not denied; a clean diff is not denial evidence" ]
        let proofs = host |> Option.map _.PlatformProofs |> Option.defaultValue []
        let unavailable = proofs |> List.filter (fun p -> p.Status <> "available") |> List.map (fun p -> p.Name + ": " + p.Status)
        let status, reason =
            match host with
            | None -> "unknown", "No host observations were supplied"
            | Some h when h.Recorder <> "host" -> "unknown", "The observations were not recorded by the host"
            | Some h when h.Enforcement = "unprotected" -> "not-confined", "The host records that it did not enforce containment"
            | Some h when h.Enforcement <> "enforced" -> "unknown", "The host does not state enforced containment"
            | Some h when String.IsNullOrWhiteSpace h.Mechanism -> "unknown", "The host names no containment mechanism"
            | Some _ when not unenforced.IsEmpty -> "not-confined", "A protected write was not denied"
            | Some _ when proofs.IsEmpty -> "unknown", "The host supplied no platform proof of its mechanism"
            | Some _ when not unavailable.IsEmpty -> "unknown", "Platform proofs are unavailable: " + String.concat "; " unavailable
            | Some _ -> "confined", "Host-enforced containment with available platform proofs"
        let claimFindings =
            match claims with
            | Some c when c.Confinement = "confined" && status <> "confined" ->
                [ f ConstructionCodes.ConfinementClaimUnsupported "confinement" [] ("Confinement is claimed but host evidence says: " + reason) ]
            | _ -> []
        findings @ claimFindings,
        { Status = status
          Reason = reason
          HostId = host |> Option.map _.HostId |> Option.defaultValue ""
          Mechanism = host |> Option.map _.Mechanism |> Option.defaultValue ""
          UnavailableProofs = unavailable
          HostRecordedDenials = observations |> List.filter (fun o -> hostRecorded o && o.Outcome = "denied") |> List.length
          UnenforcedProtectedWrites = unenforced.Length }

    /// Claimed test results are checked against the run Dokimos observed and
    /// never counted as verification.
    let claimedTests (oracle: ConstructionOracle option) (verdicts: Map<string, CaseVerdict>) (executed: bool) (tests: TestClaim list) =
        let ids = oracle |> Option.map (fun o -> o.Cases |> List.map _.Id |> Set.ofList) |> Option.defaultValue Set.empty
        [ for t in tests do
            if t.Outcome = "passed" then
                if not (Set.contains t.Id ids) then
                    f ConstructionCodes.FakeTestResult t.Id [] $"A {t.Source} pass that is not keyed to any frozen oracle case"
                elif not executed then
                    f ConstructionCodes.FakeTestResult t.Id [] $"A {t.Source} pass with no oracle run observed by Dokimos"
                else
                    match Map.tryFind t.Id verdicts with
                    | Some CasePassed -> ()
                    | Some(CaseFailed reason) -> f ConstructionCodes.FakeTestResult t.Id [] ("Claimed passed; Dokimos observed: " + reason)
                    | _ -> f ConstructionCodes.FakeTestResult t.Id [] "Claimed passed; Dokimos observed no result for this case" ]

    /// Whether the oracle may be used at all, and why not.
    let oracleIntegrity (oracle: ConstructionOracle) (expectedDigest: string) (expectedGenerator: string) (planWorkflow: string)
                        (sourceDigests: Map<string, string>) (candidate: CandidateSnapshot) (ownershipRoot: string) =
        let rootBlobs =
            candidate.Contents |> Map.toList
            |> List.filter (fun (p, _) -> ownershipRoot <> "" && p.StartsWith(ownershipRoot, StringComparison.Ordinal))
            |> List.map (snd >> ConstructionJson.sha256Bytes)
        let blocking =
            [ if oracle.Digest <> expectedDigest then
                  f ConstructionCodes.OracleDigestMismatch oracle.OracleId [] $"The oracle hashes to {oracle.Digest}, not the pinned {expectedDigest}"
              match oracle.FrozenAt with
              | Some frozen when frozen <= candidate.CommittedAt -> ()
              | Some frozen -> f ConstructionCodes.OracleNotFrozen oracle.OracleId [] $"Frozen at {frozen:O}, after the candidate commit at {candidate.CommittedAt:O}"
              | None -> f ConstructionCodes.OracleNotFrozen oracle.OracleId [] "The oracle records no freeze time"
              if oracle.ProducerKind = "generator" || oracle.ProducerSystem = expectedGenerator || oracle.ProducerSystem = "ordo"
                 || List.contains oracle.Digest rootBlobs then
                  f ConstructionCodes.OracleNotIndependent oracle.OracleId [] "The oracle was produced by, or ships with, the generator under evaluation"
              if oracle.Workflow <> planWorkflow then
                  f ConstructionCodes.OracleSourceMismatch oracle.OracleId [] $"The oracle describes workflow {oracle.Workflow}; the plan defines {planWorkflow}" ]
        let sourceFindings =
            [ for src in oracle.Sources do
                  match Map.tryFind src.RequirementKey sourceDigests with
                  | None -> f ConstructionCodes.OracleSourceMismatch src.RequirementKey [ src.RequirementKey ] "The oracle cites a requirement the pinned source manifest does not contain"
                  | Some digest when digest <> src.ContentDigest ->
                      f ConstructionCodes.OracleSourceMismatch src.RequirementKey [ src.RequirementKey ] "The oracle was derived from different requirement text than the pinned manifest"
                  | Some _ -> ()
              for c in oracle.Cases do
                  for key in c.RequirementKeys do
                      if not (Map.containsKey key sourceDigests) then
                          f ConstructionCodes.OracleSourceMismatch c.Id [ key ] "An oracle case cites a requirement outside the pinned source manifest" ]
        blocking, sourceFindings
