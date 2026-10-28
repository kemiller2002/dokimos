namespace Dokimos.Core

open System
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

/// Provenance-safe, content-addressed evidence reuse and affected-test
/// selection. Everything here is pure: the host (or the CLI shell) resolves
/// file digests, and these functions decide, deterministically, which tests
/// must run and which may carry forward evidence whose identity is unchanged.
///
/// Reuse is never acceptance. A reuse decision says only "this test's inputs
/// are byte-identical to those of a recorded passing run"; the acceptor still
/// judges the evidence independently and sees every carried item.
module EvidenceReuse =
    [<Literal>]
    let IdentitySchemaVersion = "dokimos.evidence-identity/1"

    [<Literal>]
    let ProvenanceSchemaVersion = "dokimos.evidence-provenance/1"

    [<Literal>]
    let RequestSchemaVersion = "dokimos.evidence-reuse-request/1"

    [<Literal>]
    let PlanSchemaVersion = "dokimos.evidence-reuse-plan/1"

    /// How long evidence stays valid.
    /// - ContentAddressed: valid for as long as its identity inputs are unchanged.
    /// - PerCommit: valid only at the exact revision that produced it.
    /// - Live: depends on an external system; never reused.
    type Freshness =
        | ContentAddressed
        | PerCommit
        | Live

    let freshnessCases = [ "content-addressed", ContentAddressed; "per-commit", PerCommit; "live", Live ]

    let freshnessName =
        function
        | ContentAddressed -> "content-addressed"
        | PerCommit -> "per-commit"
        | Live -> "live"

    /// What kind of evidence a test produces. Only `Functional` evidence can
    /// be carried forward:
    /// - Security: security-specific authority belongs to Aegis/Tutors
    ///   (AGENTS.md, "Echelon boundaries"); Dokimos cannot certify that a
    ///   security result is still valid, so it is produced by every run.
    /// - IndependentAcceptance: reuse is not acceptance (docs/evidence-reuse.md);
    ///   acceptance evidence is always produced by the candidate run itself.
    type Category =
        | Functional
        | Security
        | IndependentAcceptance

    let categoryCases = [ "functional", Functional; "security", Security; "independent-acceptance", IndependentAcceptance ]

    /// Every input whose change must invalidate evidence. Sources are
    /// repository paths mapped to `sha256:<hex>` of their bytes. The runtime
    /// environment, where it can change a result, is declared as
    /// `Configuration` entries (for example `runtime.os`, `runtime.framework`).
    type IdentityInputs =
        { Sources: Map<string, string>
          Dependencies: Map<string, string>
          TestContract: string
          Configuration: Map<string, string>
          ToolVersion: string }

    /// Where, and under which identity, a completion evidence observation was
    /// produced (the optional `Provenance` member of an authority observation).
    type ObservationProvenance =
        { EvidenceIdentity: string
          Freshness: Freshness
          ProducedAtRevision: string }

    type Outcome =
        | OutcomePassed
        | OutcomeFailed
        | OutcomeUnavailable

    let outcomeCases = [ "passed", OutcomePassed; "failed", OutcomeFailed; "unavailable", OutcomeUnavailable ]

    /// A recorded run (`dokimos.evidence-provenance/1`). Provenance members are
    /// options so that a record missing them is refused for reuse with a
    /// reason, instead of failing the whole plan.
    type PriorEvidence =
        { EvidenceId: string
          TestId: string
          Outcome: Outcome
          ProducedAtRevision: string option
          Freshness: Freshness option
          EvidenceIdentity: string option
          Inputs: IdentityInputs option }

    /// One test the host wants judged. `Patterns` is the declared test-to-input
    /// mapping (None: no mapping). `Inputs` is the resolved current identity
    /// input set, or the reason it could not be resolved.
    type TestRequest =
        { TestId: string
          Freshness: Freshness
          Category: Category
          Patterns: string list option
          Inputs: Result<IdentityInputs, string> }

    type Reason = { Code: string; Subject: string; Detail: string }

    type Decision =
        | Run
        | Reuse of PriorEvidence

    type TestDecision =
        { TestId: string
          Freshness: Freshness
          Decision: Decision
          Reasons: Reason list
          CurrentIdentity: string option
          CurrentInputs: IdentityInputs option
          PathAffected: bool option }

    type Plan =
        { Revision: string
          Checkpoint: EvidenceCheckpoint.Checkpoint
          ChangedPaths: string list option
          UnmappedChangedPaths: string list
          Tests: TestDecision list }

    // ---- identity -------------------------------------------------------------------------------------------------

    let private frame (value: string) =
        let text = if Object.ReferenceEquals(value, null) then "" else value
        string (Encoding.UTF8.GetByteCount text) + ":" + text

    let private framedMap tag (values: Map<string, string>) =
        frame tag + frame (string values.Count) + (values |> Map.toList |> List.map (fun (k, v) -> frame k + frame v) |> String.concat "")

    let sha256Text (text: string) =
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes text)).ToLowerInvariant()

    let sha256Bytes (bytes: byte[]) =
        "sha256:" + Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

    let isSha256 (value: string) =
        not (Object.ReferenceEquals(value, null))
        && value.Length = 71
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.Substring 7 |> Seq.forall Uri.IsHexDigit

    let isImmutableRevision (value: string) =
        not (Object.ReferenceEquals(value, null))
        && (value.Length = 40 || value.Length = 64)
        && value |> Seq.forall Uri.IsHexDigit

    /// The evidence identity: one digest over every input that can change the
    /// result. Maps are ordinal-sorted and every value is length-framed, so no
    /// two distinct input sets share a canonical form.
    let identity (inputs: IdentityInputs) =
        [ frame IdentitySchemaVersion
          framedMap "sources" inputs.Sources
          framedMap "dependencies" inputs.Dependencies
          frame "test-contract" + frame inputs.TestContract
          framedMap "configuration" inputs.Configuration
          frame "tool-version" + frame inputs.ToolVersion ]
        |> String.concat ""
        |> sha256Text

    /// Input members whose absence makes a record's provenance incomplete.
    let missingInputs (inputs: IdentityInputs) =
        [ if inputs.Sources.IsEmpty then "Inputs.Sources"
          if String.IsNullOrWhiteSpace inputs.TestContract then "Inputs.TestContract"
          if String.IsNullOrWhiteSpace inputs.ToolVersion then "Inputs.ToolVersion" ]

    let private mapChanges kind (before: Map<string, string>) (after: Map<string, string>) =
        Set.union (before |> Map.keys |> Set.ofSeq) (after |> Map.keys |> Set.ofSeq)
        |> Set.toList
        |> List.choose (fun key ->
            match Map.tryFind key before, Map.tryFind key after with
            | Some b, Some a when b = a -> None
            | Some b, Some a -> Some(kind, key, $"'{b}' -> '{a}'")
            | Some _, None -> Some(kind, key, "removed")
            | None, Some _ -> Some(kind, key, "added")
            | None, None -> None)

    /// Which identity inputs differ, by kind and name.
    let changedInputs (before: IdentityInputs) (after: IdentityInputs) =
        mapChanges "source" before.Sources after.Sources
        @ mapChanges "dependency" before.Dependencies after.Dependencies
        @ (if before.TestContract = after.TestContract then [] else [ "test-contract", "test-contract", $"'{before.TestContract}' -> '{after.TestContract}'" ])
        @ mapChanges "configuration" before.Configuration after.Configuration
        @ (if before.ToolVersion = after.ToolVersion then [] else [ "tool-version", "tool-version", $"'{before.ToolVersion}' -> '{after.ToolVersion}'" ])

    /// Completion-verification refusals for one observation's provenance:
    /// live evidence is never reusable, per-commit evidence never crosses a
    /// commit, and evidence from another revision must be declared reusable.
    let completionRefusals (reusable: bool) (observedRevision: string) (provenance: ObservationProvenance option) =
        let refusals (p: ObservationProvenance) =
            let freshness, producedAt = p.Freshness, p.ProducedAtRevision
            let carried = not (String.Equals(producedAt, observedRevision, StringComparison.OrdinalIgnoreCase))
            [ if reusable && freshness = Live then
                  "DOK.COMPLETION.REUSE_LIVE_EVIDENCE", "live evidence is never reusable"
              if carried && not reusable then
                  "DOK.COMPLETION.REUSE_UNDECLARED", $"evidence produced at {producedAt} is observed at {observedRevision} without Reusable: true"
              if carried && freshness = PerCommit then
                  "DOK.COMPLETION.REUSE_STALE_COMMIT", $"per-commit evidence produced at {producedAt} cannot be carried to {observedRevision}"
              if carried && freshness = Live then
                  "DOK.COMPLETION.REUSE_LIVE_EVIDENCE", $"live evidence produced at {producedAt} cannot be carried to {observedRevision}" ]
        provenance |> Option.map refusals |> Option.defaultValue []

    // ---- path mapping -----------------------------------------------------------------------------------------------

    /// Glob over repository-relative '/' paths: `**` crosses directories, `*`
    /// and `?` do not; a pattern ending in '/' names a directory subtree.
    let globRegex (pattern: string) =
        let pattern = if pattern.EndsWith "/" then pattern + "**" else pattern
        let body = StringBuilder()
        let mutable i = 0
        while i < pattern.Length do
            match pattern.[i] with
            | '*' when i + 1 < pattern.Length && pattern.[i + 1] = '*' ->
                if i + 2 < pattern.Length && pattern.[i + 2] = '/' then
                    body.Append "(?:.*/)?" |> ignore
                    i <- i + 3
                else
                    body.Append ".*" |> ignore
                    i <- i + 2
            | '*' ->
                body.Append "[^/]*" |> ignore
                i <- i + 1
            | '?' ->
                body.Append "[^/]" |> ignore
                i <- i + 1
            | c ->
                body.Append(Regex.Escape(string c)) |> ignore
                i <- i + 1
        Regex("^" + body.ToString() + "$", RegexOptions.CultureInvariant)

    let matches (patterns: string list) (path: string) =
        patterns |> List.exists (fun pattern -> (globRegex pattern).IsMatch path)

    // ---- decisions ------------------------------------------------------------------------------------------------

    let private reason code subject detail = { Code = code; Subject = subject; Detail = detail }

    /// Why one recorded run may not be reused for the current inputs; [] means
    /// it may. Order of checks: provenance, integrity, policy, outcome, inputs.
    let judgePrior (revision: string) (freshness: Freshness) (current: IdentityInputs) (prior: PriorEvidence) : Reason list =
        let subject = prior.EvidenceId
        let missing =
            [ if prior.ProducedAtRevision.IsNone then "ProducedAtRevision"
              if prior.Freshness.IsNone then "Freshness"
              if prior.EvidenceIdentity.IsNone then "EvidenceIdentity"
              match prior.Inputs with
              | None -> "Inputs"
              | Some inputs -> yield! missingInputs inputs ]
        if not missing.IsEmpty then
            [ reason "REUSE.PROVENANCE_MISSING" subject ("recorded evidence lacks " + String.concat ", " missing) ]
        else
            let inputs = prior.Inputs.Value
            let claimed = prior.EvidenceIdentity.Value
            let derived = identity inputs
            if not (String.Equals(claimed, derived, StringComparison.OrdinalIgnoreCase)) then
                [ reason "REUSE.IDENTITY_TAMPERED" subject $"recorded identity {claimed} does not match its recorded inputs ({derived})" ]
            elif not (isImmutableRevision prior.ProducedAtRevision.Value) then
                [ reason "REUSE.PROVENANCE_MISSING" subject "ProducedAtRevision is not an immutable revision" ]
            else
                [ if prior.Freshness.Value <> freshness then
                      reason "REUSE.FRESHNESS_CHANGED" subject
                          $"recorded under '{freshnessName prior.Freshness.Value}', now '{freshnessName freshness}'"
                  if prior.Outcome <> OutcomePassed then
                      reason "REUSE.PRIOR_NOT_PASSED" subject "only passing evidence can be carried forward"
                  if freshness = PerCommit
                     && not (String.Equals(prior.ProducedAtRevision.Value, revision, StringComparison.OrdinalIgnoreCase)) then
                      reason "REUSE.PER_COMMIT_REVISION_CHANGED" subject
                          $"per-commit evidence from {prior.ProducedAtRevision.Value} is not valid at {revision}"
                  let changes = changedInputs inputs current
                  for kind, name, detail in changes do
                      reason "REUSE.INPUT_CHANGED" (subject + ":" + kind + ":" + name) detail
                  if changes.IsEmpty && not (String.Equals(derived, identity current, StringComparison.OrdinalIgnoreCase)) then
                      reason "REUSE.INPUT_CHANGED" (subject + ":identity") "identity differs" ]

    /// Why a recorded run cannot be published to the evidence store.
    type RecordProblem =
        | RecordIncomplete of reason: string
        | RecordIdentityNotDerived of claimed: string * derived: string

    /// A published record, passing or failing, must carry complete provenance
    /// whose identity is the digest of its own inputs. That is what lets a
    /// stored failure contradict a passing run at the same identity
    /// (`REUSE.CONFLICTING_PRIOR`) and keeps a failure at any other identity
    /// from touching it.
    let recordProblem (prior: PriorEvidence) : RecordProblem option =
        match prior.ProducedAtRevision, prior.Freshness, prior.EvidenceIdentity, prior.Inputs with
        | Some revision, Some _, Some claimed, Some inputs ->
            let derived = identity inputs
            match missingInputs inputs with
            | _ :: _ as missing -> Some(RecordIncomplete("recorded inputs lack " + String.concat ", " missing))
            | [] when not (String.Equals(claimed, derived, StringComparison.OrdinalIgnoreCase)) ->
                Some(RecordIdentityNotDerived(claimed, derived))
            | [] when not (isImmutableRevision revision) -> Some(RecordIncomplete "ProducedAtRevision is not an immutable revision")
            | [] -> None
        | _ -> Some(RecordIncomplete "a published record needs ProducedAtRevision, Freshness, EvidenceIdentity and Inputs")

    let private decideTest revision (priorsByTest: Map<string, PriorEvidence list>) (test: TestRequest) =
        let priors = Map.tryFind test.TestId priorsByTest |> Option.defaultValue [] |> List.sortBy _.EvidenceId
        match test.Inputs with
        | Error why ->
            Run, [ reason "REUSE.MAPPING_UNKNOWN" test.TestId why ], None, None
        | Ok current ->
            let currentIdentity = identity current
            match missingInputs current with
            | _ :: _ as missing ->
                Run, [ reason "REUSE.MAPPING_UNKNOWN" test.TestId ("current inputs lack " + String.concat ", " missing) ], Some currentIdentity, Some current
            | [] ->
                if test.Freshness = Live then
                    Run, [ reason "REUSE.LIVE_NEVER_REUSED" test.TestId "live evidence depends on an external system and is never reused" ], Some currentIdentity, Some current
                elif test.Category = Security then
                    Run, [ reason "REUSE.SECURITY_NEVER_REUSED" test.TestId "security evidence is produced by every run; Dokimos holds no security authority to carry it forward" ], Some currentIdentity, Some current
                elif test.Category = IndependentAcceptance then
                    Run, [ reason "REUSE.ACCEPTANCE_NEVER_REUSED" test.TestId "independent acceptance evidence is produced by the candidate run itself; reuse is not acceptance" ], Some currentIdentity, Some current
                elif priors.IsEmpty then
                    Run, [ reason "REUSE.NO_PRIOR_EVIDENCE" test.TestId "no recorded evidence for this test" ], Some currentIdentity, Some current
                else
                    let judged = priors |> List.map (fun prior -> prior, judgePrior revision test.Freshness current prior)
                    // A failing run at the very same identity contradicts any
                    // passing one (flaky or nondeterministic): run again.
                    let contradicted =
                        priors
                        |> List.filter (fun p ->
                            p.Outcome <> OutcomePassed
                            && p.EvidenceIdentity |> Option.exists (fun id -> String.Equals(id, currentIdentity, StringComparison.OrdinalIgnoreCase))
                            && p.Inputs |> Option.exists (fun inputs -> identity inputs = currentIdentity))
                    match judged |> List.tryFind (fun (_, reasons) -> reasons.IsEmpty), contradicted with
                    | Some(prior, _), [] -> Reuse prior, [], Some currentIdentity, Some current
                    | Some _, failing ->
                        Run,
                        failing |> List.map (fun p -> reason "REUSE.CONFLICTING_PRIOR" p.EvidenceId "a non-passing run has the same identity"),
                        Some currentIdentity,
                        Some current
                    | None, _ -> Run, judged |> List.collect snd, Some currentIdentity, Some current

    /// Builds the plan. `changedPaths` (if known) drives path-based selection;
    /// it can only add tests to the run set, never remove one, so a stale or
    /// incomplete change list cannot cause a wrong reuse. Unknown mapping,
    /// unmapped changes and live tests always run.
    let plan (revision: string) (changedPaths: string list option) (globalPatterns: string list) (ignoredPatterns: string list)
             (tests: TestRequest list) (priors: PriorEvidence list) : Plan =
        let priorsByTest = priors |> List.groupBy _.TestId |> Map.ofList
        let unmapped =
            match changedPaths with
            | None -> []
            | Some paths ->
                paths
                |> List.filter (fun path ->
                    not (matches ignoredPatterns path)
                    && not (matches globalPatterns path)
                    && not (tests |> List.exists (fun t -> t.Patterns |> Option.exists (fun ps -> matches ps path))))
                |> List.distinct
                |> List.sort
        let decisions =
            tests
            |> List.sortBy _.TestId
            |> List.map (fun test ->
                let decision, reasons, currentIdentity, currentInputs = decideTest revision priorsByTest test
                let affectedBy =
                    match changedPaths, test.Patterns with
                    | Some paths, Some patterns -> Some(paths |> List.filter (fun p -> matches (patterns @ globalPatterns) p) |> List.distinct |> List.sort)
                    | _ -> None
                let pathReasons =
                    [ match affectedBy with
                      | Some(_ :: _ as paths) -> reason "REUSE.PATH_AFFECTED" test.TestId ("changed: " + String.concat ", " paths)
                      | _ -> ()
                      if not unmapped.IsEmpty then
                          reason "REUSE.UNMAPPED_CHANGE" test.TestId ("changed paths no mapping covers: " + String.concat ", " unmapped)
                      // Source identity moved although no changed path touched
                      // the mapping: the change list or the mapping is incomplete.
                      match affectedBy, decision with
                      | Some [], Run when reasons |> List.exists (fun r -> r.Code = "REUSE.INPUT_CHANGED" && r.Subject.Contains ":source:") ->
                          reason "REUSE.MAPPING_INCOMPLETE" test.TestId "source inputs changed but no changed path matched the mapping"
                      | _ -> () ]
                let blocking = pathReasons |> List.exists (fun r -> r.Code <> "REUSE.MAPPING_INCOMPLETE")
                let decision = if blocking then Run else decision
                { TestId = test.TestId
                  Freshness = test.Freshness
                  Decision = decision
                  Reasons = reasons @ pathReasons |> List.sortBy (fun r -> r.Code, r.Subject, r.Detail)
                  CurrentIdentity = currentIdentity
                  CurrentInputs = currentInputs
                  PathAffected = affectedBy |> Option.map (List.isEmpty >> not) })
        { Revision = revision
          Checkpoint = EvidenceCheckpoint.Incremental
          ChangedPaths = changedPaths |> Option.map (List.distinct >> List.sort)
          UnmappedChangedPaths = unmapped
          Tests = decisions }

    let isRun (decision: TestDecision) =
        match decision.Decision with
        | Run -> true
        | Reuse _ -> false
