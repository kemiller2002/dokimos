namespace Dokimos.Core

open System

/// One process Dokimos itself started during a behavior run.
type CommandRecord =
    { Purpose: string
      CommandLine: string
      WorkingDirectory: string
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset
      /// completed | timeout | could-not-start
      Outcome: string
      ExitCode: int option
      StdoutDigest: string
      StderrDigest: string }

/// What Dokimos observed about one run: the processes it started, and the
/// digests of everything that crossed the process boundary.
type BehaviorRunRecord =
    { Sdk: string
      Commands: CommandRecord list
      CandidateCommit: string
      CandidateTree: string
      MaterializedFiles: int
      MaterializedDigest: string
      Project: string
      Assembly: string
      AssemblyDigest: string
      DriverDigest: string
      DriverInputDigest: string
      ObservationsDigest: string
      OracleDigest: string
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset }

type BehaviorRun =
    /// Dokimos did not start the candidate (refused, or not requested).
    | NotRun of reason: string
    /// A run was attempted but could not produce observations.
    | RunUnavailable of record: BehaviorRunRecord * reason: string
    | ProjectMissing of record: BehaviorRunRecord
    | BuildFailed of record: BehaviorRunRecord * diagnostics: string
    | Driven of record: BehaviorRunRecord * probe: AdapterProbe option * observations: AdapterObservation list

/// When the oracle's exact bytes first appear in its own Git history.
type OracleHistory =
    | HistoryUnavailable of reason: string
    | FirstCommitted of commit: string * at: DateTimeOffset

type BehaviorCandidate =
    { Commit: string
      CommittedAt: DateTimeOffset
      /// Paths at the candidate commit whose bytes are the oracle's bytes.
      OracleCopies: string list }

type BehaviorInputs =
    { CheckedAt: DateTimeOffset
      WorkItem: string
      OracleBytes: byte[]
      ExpectedOracleDigest: string
      OracleHistory: OracleHistory
      Candidate: BehaviorCandidate
      Project: string
      SourceDocuments: Map<string, byte[]> option
      Run: BehaviorRun
      ClaimedTests: TestClaim list }

type BehaviorCaseResult =
    { Id: string
      Kind: string
      RequirementKeys: string list
      Verdict: string
      Detail: string
      Observed: string
      Citation: string }

/// The candidate's answer on a case the sources leave unsettled. Reported
/// as agent-decided behavior, never counted as pass or fail.
type ConflictObservation =
    { Id: string
      Classification: string
      Conflict: string
      RequirementKeys: string list
      Input: string
      Observed: string
      Status: string
      Citation: string
      Note: string }

type BehaviorRequirementStatus =
    { RequirementKey: string
      Coverage: string
      Remainder: string
      IndependentlyVerified: string
      ScoredCases: string list
      FailedCases: string list
      UnknownCases: string list
      UnscoredCases: string list }

type BehaviorCounts =
    { Scored: int
      Passed: int
      Failed: int
      Unknown: int
      Unscored: int }

type OracleIntegrity =
    { Usable: bool
      DeclaredFrozenAt: string
      History: string
      HistoryCommit: string
      HistoryCommittedAt: string
      SourceDocuments: string }

type BehaviorReport =
    { FirstPassVerdict: string
      ExitCode: int
      Reasons: string list
      Oracle: AdapterOracle
      Integrity: OracleIntegrity
      Compile: string
      CompileDiagnostics: string
      Probe: AdapterProbe option
      Cases: BehaviorCaseResult list
      Counts: BehaviorCounts
      ConflictObservations: ConflictObservation list
      Requirements: BehaviorRequirementStatus list
      Findings: ReportedFinding list
      Run: BehaviorRunRecord option
      Limitations: string list }

/// Stable finding codes specific to adapter-driven behavior runs. Codes
/// shared with construction verification come from `ConstructionCodes`.
[<RequireQualifiedAccess>]
module BehaviorCodes =
    let AdapterProjectMissing = "adapter-project-missing"
    let AdapterMissing = "adapter-missing"
    let AdapterSignatureMismatch = "adapter-signature-mismatch"

[<RequireQualifiedAccess>]
module BehaviorVerification =
    let private f = ConstructionChecks.finding

    let private stamp (t: DateTimeOffset) = t.ToString("O")

    /// Findings that make the oracle unusable. When any exists, Dokimos does
    /// not build or execute the candidate at all.
    let integrity (oracle: AdapterOracle) (expectedDigest: string) (history: OracleHistory) (candidate: BehaviorCandidate) =
        [ if oracle.Digest <> expectedDigest then
              f ConstructionCodes.OracleDigestMismatch oracle.OracleId [] $"The oracle hashes to {oracle.Digest}, not the pinned {expectedDigest}"
          match oracle.FrozenAt with
          | Some frozen when frozen <= candidate.CommittedAt -> ()
          | Some frozen ->
              f ConstructionCodes.OracleNotFrozen oracle.OracleId [] $"Declared frozen at {stamp frozen}, after the candidate commit at {stamp candidate.CommittedAt}"
          | None -> f ConstructionCodes.OracleNotFrozen oracle.OracleId [] "The oracle records no freeze time"
          match history with
          | FirstCommitted(commit, at) when at > candidate.CommittedAt ->
              f ConstructionCodes.OracleNotFrozen oracle.OracleId []
                  $"These oracle bytes were first committed in {commit} at {stamp at}, after the candidate commit at {stamp candidate.CommittedAt}"
          | _ -> ()
          if oracle.ProducerKind = "generator" || oracle.ProducerSystem = "ordo" then
              f ConstructionCodes.OracleNotIndependent oracle.OracleId [] "The oracle was produced by a generator under evaluation"
          for path in candidate.OracleCopies do
              f ConstructionCodes.OracleNotIndependent path [] "The candidate commit contains the oracle itself, so its expectations were available to the candidate" ]

    let private sourceDocuments (oracle: AdapterOracle) (documents: Map<string, byte[]> option) =
        match documents with
        | None -> [], "unknown: source documents not supplied"
        | Some docs ->
            [ for src in oracle.Sources do
                match Map.tryFind src.Excerpt docs with
                | Some bytes when ConstructionJson.sha256Bytes bytes <> src.ContentDigest ->
                    f ConstructionCodes.SourceContentMismatch src.Excerpt [ src.RequirementKey ] "The excerpt's bytes do not hash to the oracle's contentDigest"
                | None -> f ConstructionCodes.SourceContentMismatch src.Excerpt [ src.RequirementKey ] "A cited excerpt is missing from the supplied source documents"
                | Some _ -> () ],
            "checked"

    let private describeInput (case: AdapterCase) =
        match case.Body with
        | DecideSteps(state, steps) -> state + ": " + (steps |> List.map _.Command |> String.concat ", ")
        | RecordedMinutes(segments, _) -> "[" + (segments |> List.map (fun (a, b) -> $"({a}, {b})") |> String.concat "; ") + "]"

    let private describeObserved (case: AdapterCase) (observation: AdapterObservation option) =
        match observation with
        | None -> "not observed"
        | Some o when o.Status <> "observed" -> o.Status + ": " + o.Reason
        | Some o ->
            match case.Body with
            | DecideSteps _ ->
                o.Steps
                |> List.map (fun st -> if st.Outcome = "accepted" then sprintf "Ok (%s, [%s])" st.Next (String.concat "; " st.Effects) else "Error")
                |> String.concat " -> "
            | RecordedMinutes _ -> BehaviorOracle.describeMinutes o.Minutes

    let private probeFindings (oracle: AdapterOracle) (probe: AdapterProbe option) =
        let subject = oracle.Binding.Namespace + "." + oracle.Binding.Module
        match probe with
        | None -> []
        | Some p when p.Assembly <> "loaded" ->
            [ f BehaviorCodes.AdapterMissing subject [] ("The built assembly could not be loaded: " + p.Detail) ]
        | Some p when p.Module = "missing" ->
            [ f BehaviorCodes.AdapterMissing subject [] ("The built assembly has no F# module " + subject) ]
        | Some p ->
            [ for name, status in p.Functions do
                  let signature =
                      oracle.Binding.Functions |> List.tryFind (fun x -> x.Name = name) |> Option.map _.Signature |> Option.defaultValue ""
                  match status with
                  | "missing" -> f BehaviorCodes.AdapterMissing (subject + "." + name) [] ("The adapter does not define " + name + " : " + signature)
                  | "signature-mismatch" ->
                      f BehaviorCodes.AdapterSignatureMismatch (subject + "." + name) [] ($"Expected {name} : {signature}; " + p.Detail)
                  | _ -> () ]

    let private requirementStatus (oracle: AdapterOracle) (verdicts: (AdapterCase * CaseVerdict) list) (source: OracleSource) =
        let mine = oracle.Cases |> List.filter (fun c -> List.contains source.RequirementKey c.RequirementKeys)
        let scored = verdicts |> List.filter (fun (c, _) -> List.contains source.RequirementKey c.RequirementKeys)
        let ids pick = scored |> List.filter (snd >> pick) |> List.map (fun (c, _) -> c.Id)
        let failed = ids (function CaseFailed _ -> true | _ -> false)
        let unknown = ids (function CaseUnknown _ -> true | _ -> false)
        let unscored = mine |> List.filter (fun c -> c.Scoring <> Scored) |> List.map _.Id
        let status =
            if scored.IsEmpty && not unscored.IsEmpty then "unresolved-conflict"
            elif scored.IsEmpty then "not-observed"
            elif not failed.IsEmpty then "failed"
            elif not unknown.IsEmpty then "unknown"
            elif source.Coverage = "full" then "verified"
            else "partial"
        { RequirementKey = source.RequirementKey
          Coverage = source.Coverage
          Remainder = source.Remainder
          IndependentlyVerified = status
          ScoredCases = scored |> List.map (fun (c, _) -> c.Id)
          FailedCases = failed
          UnknownCases = unknown
          UnscoredCases = unscored }

    /// Claimed results are checked against the Dokimos-observed run and are
    /// never counted. An unscored conflict case cannot be claimed as passed.
    let private claims (oracle: AdapterOracle) (verdicts: Map<string, CaseVerdict>) (driven: bool) (tests: TestClaim list) =
        let unscored = oracle.Cases |> List.filter (fun c -> c.Scoring <> Scored) |> List.map _.Id |> Set.ofList
        [ for t in tests do
            if t.Outcome = "passed" then
                if Set.contains t.Id unscored then
                    f ConstructionCodes.FakeTestResult t.Id [] $"A {t.Source} pass for an unscored unresolved-conflict case, which has no pass or fail"
                elif not (Map.containsKey t.Id verdicts) then
                    f ConstructionCodes.FakeTestResult t.Id [] $"A {t.Source} pass that is not keyed to any scored oracle case"
                elif not driven then
                    f ConstructionCodes.FakeTestResult t.Id [] $"A {t.Source} pass with no adapter run observed by Dokimos"
                else
                    match Map.tryFind t.Id verdicts with
                    | Some CasePassed -> ()
                    | Some(CaseFailed reason) -> f ConstructionCodes.FakeTestResult t.Id [] ("Claimed passed; Dokimos observed: " + reason)
                    | _ -> f ConstructionCodes.FakeTestResult t.Id [] "Claimed passed; Dokimos observed no result for this case" ]

    let limitations =
        [ "A run compiles and executes candidate code. Run it only where executing the candidate is acceptable (CI or a disposable environment). It is not containment evidence: the candidate is not sandboxed by Dokimos."
          "The driver runs the candidate's code in its own process; hostile candidate code could tamper with that process. Dokimos judges the driver's observations in-process and never receives a verdict from the driver, but the observations themselves share the candidate's process."
          "The adapter is the only surface observed. Whether the candidate's own host paths (for example its stop handler) use the same logic it exposes through the adapter is not observed."
          "Build uses the .NET SDK resolved in the materialized candidate directory (a candidate global.json applies); the SDK version is recorded, not enforced. Package restore may use the network."
          "Commit and freeze times are Git and oracle metadata, which are self-reported; they are compared, not authenticated." ]

    let private scoredCases (oracle: AdapterOracle) = oracle.Cases |> List.filter (fun c -> c.Scoring = Scored)

    let verify (inputs: BehaviorInputs) : Result<BehaviorReport, string> =
        BehaviorOracle.read inputs.OracleBytes
        |> Result.map (fun oracle ->
            let blocking = integrity oracle inputs.ExpectedOracleDigest inputs.OracleHistory inputs.Candidate
            let sourceFindings, sourceStatus = sourceDocuments oracle inputs.SourceDocuments
            let record, compile, diagnostics, probe, observations =
                match inputs.Run with
                | NotRun _ -> None, "unknown", "", None, None
                | RunUnavailable(r, _) -> Some r, "unknown", "", None, None
                | ProjectMissing r -> Some r, "not-run", "", None, None
                | BuildFailed(r, d) -> Some r, "failed", d, None, None
                | Driven(r, p, o) -> Some r, "passed", "", p, Some o
            let notDrivenReason =
                match inputs.Run with
                | NotRun reason -> reason
                | RunUnavailable(_, reason) -> "the run could not complete: " + reason
                | ProjectMissing _ -> "the candidate commit does not contain the project " + inputs.Project
                | BuildFailed _ -> "the candidate did not build"
                | Driven _ -> ""
            let lookup id = observations |> Option.bind (List.tryFind (fun o -> o.CaseId = id))
            let verdicts =
                scoredCases oracle
                |> List.map (fun c ->
                    match observations with
                    | None -> c, CaseUnknown notDrivenReason
                    | Some _ -> c, BehaviorOracle.judge c (lookup c.Id))
            let verdictMap = verdicts |> List.map (fun (c, v) -> c.Id, v) |> Map.ofList
            let runFindings =
                [ match inputs.Run with
                  | ProjectMissing _ ->
                      f BehaviorCodes.AdapterProjectMissing inputs.Project [] $"The candidate commit {inputs.Candidate.Commit} has no project file {inputs.Project}"
                  | BuildFailed(_, d) -> f ConstructionCodes.CandidateCompileFailed inputs.Candidate.Commit [] d
                  | _ -> ()
                  yield! probeFindings oracle probe
                  for c, v in verdicts do
                      match v, lookup c.Id with
                      | CaseFailed _, Some o when o.Status = "adapter-missing" || o.Status = "signature-mismatch" -> ()
                      | CaseFailed reason, _ -> f ConstructionCodes.OracleBehaviorMismatch c.Id c.RequirementKeys reason
                      | _ -> () ]
            let raw =
                blocking @ sourceFindings @ runFindings
                @ claims oracle verdictMap observations.IsSome inputs.ClaimedTests
                |> List.distinct
            let findings =
                raw
                |> List.map (fun x ->
                    { Code = x.Code; Subject = x.Subject; RequirementKeys = x.RequirementKeys; Detail = x.Detail
                      Disposition = "open"; WorkItem = inputs.WorkItem })
                |> List.sortBy (fun x -> x.Code, x.Subject)
            let count pick = verdicts |> List.filter (snd >> pick) |> List.length
            let counts =
                { Scored = verdicts.Length
                  Passed = count ((=) CasePassed)
                  Failed = count (function CaseFailed _ -> true | _ -> false)
                  Unknown = count (function CaseUnknown _ -> true | _ -> false)
                  Unscored = oracle.Cases.Length - verdicts.Length }
            let verdict, code, reasons =
                if not blocking.IsEmpty then
                    "refused", 4, [ "The oracle failed its integrity checks; Dokimos did not build or execute the candidate" ]
                elif not findings.IsEmpty then "failed", 4, [ $"{findings.Length} open finding(s)" ]
                elif counts.Scored > 0 && counts.Passed = counts.Scored && compile = "passed" then
                    "passed", 0,
                    [ $"Every one of the {counts.Scored} scored cases passed on the first Dokimos-observed run"
                      $"{counts.Unscored} unscored conflict case(s) are reported separately and not counted" ]
                else "unknown", 3, [ "Behavior was not fully observed: " + (if notDrivenReason <> "" then notDrivenReason else $"{counts.Unknown} scored case(s) unknown") ]
            { FirstPassVerdict = verdict
              ExitCode = code
              Reasons = reasons
              Oracle = oracle
              Integrity =
                { Usable = blocking.IsEmpty
                  DeclaredFrozenAt = oracle.FrozenAt |> Option.map stamp |> Option.defaultValue ""
                  History =
                    match inputs.OracleHistory with
                    | HistoryUnavailable reason -> "unavailable: " + reason
                    | FirstCommitted(_, at) when at <= inputs.Candidate.CommittedAt -> "committed-before-candidate"
                    | FirstCommitted _ -> "committed-after-candidate"
                  HistoryCommit = match inputs.OracleHistory with FirstCommitted(c, _) -> c | HistoryUnavailable _ -> ""
                  HistoryCommittedAt = match inputs.OracleHistory with FirstCommitted(_, at) -> stamp at | HistoryUnavailable _ -> ""
                  SourceDocuments = sourceStatus }
              Compile = compile
              CompileDiagnostics = diagnostics
              Probe = probe
              Cases =
                verdicts
                |> List.map (fun (c, v) ->
                    let verdict, detail = match v with CasePassed -> "passed", "" | CaseFailed r -> "failed", r | CaseUnknown r -> "unknown", r
                    { Id = c.Id; Kind = c.Kind; RequirementKeys = c.RequirementKeys; Verdict = verdict; Detail = detail
                      Observed = describeObserved c (lookup c.Id); Citation = c.Citation })
              Counts = counts
              ConflictObservations =
                oracle.Cases
                |> List.choose (fun c ->
                    match c.Scoring with
                    | Scored -> None
                    | Unscored(classification, conflict) ->
                        let o = lookup c.Id
                        Some
                            { Id = c.Id
                              Classification = classification
                              Conflict = conflict
                              RequirementKeys = c.RequirementKeys
                              Input = describeInput c
                              Observed = (match observations with None -> "not observed: " + notDrivenReason | Some _ -> describeObserved c o)
                              Status = (match o with Some x when x.Status = "observed" -> "observed" | Some x -> x.Status | None -> "unobserved")
                              Citation = c.Citation
                              Note = "Agent-decided behavior on an unresolved conflict; reported, never counted as pass or fail" })
              Requirements = oracle.Sources |> List.map (requirementStatus oracle verdicts)
              Findings = findings
              Run = record
              Limitations = limitations })
