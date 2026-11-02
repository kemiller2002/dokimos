namespace Dokimos.Core.Verification

open System
open Dokimos.Core

/// The state of one bound test identity at one candidate.
type IdentityStatus =
    /// No test with this exact identity exists in source. `nearMiss` names tests that look like
    /// it (same obligation id, or a small edit distance) — a probable name drift.
    | NotGenerated of nearMiss: string list
    /// The test exists but cannot count: placeholder, skipped, no or only trivial assertions,
    /// excluded from execution, does not reach the required boundary, or declared twice.
    | Invalid of reasons: string list
    /// Structurally valid; its suite produced no result at the candidate.
    | Generated
    /// Its suite ran at the candidate but this identity has no result (filtered or not compiled).
    | NotExecuted
    | IdentityPassed
    | IdentityFailed of detail: string
    /// Not judgeable here: record evidence, or results exist only for another SHA.
    | Blocked of reason: string

type ObligationStatus =
    | Verified
    | ObligationFailed
    | ObligationInvalid
    | ObligationNotGenerated
    | ObligationBlocked
    | ObligationNotRun

type ProbeOutcome =
    | Detected
    | Survived
    | ProbeError of string

type ProbeResult =
    { ProbeId: string
      Obligations: string list
      SourceSha: string
      Outcome: ProbeOutcome }

type IdentityRow =
    { Identity: string
      Suite: string
      Obligations: string list
      Status: IdentityStatus
      Location: string option }

type ObligationRow =
    { Obligation: string
      Requirement: string
      Status: ObligationStatus
      Quality: string
      Identities: IdentityRow list
      Reasons: string list }

type CoverageReport =
    { Candidate: string
      PlanId: string
      Scope: string list
      Identities: IdentityRow list
      Obligations: ObligationRow list
      RejectedRuns: (ResultRun * string) list
      UnplannedResults: int }

/// What the source scan saw, with the execution scope that only I/O can determine.
type ScanInput =
    { Tests: ScannedTest list
      /// Repository-relative path -> is it compiled by its test project? (.NET only)
      Compiled: string -> bool }

type ResultsInput =
    { Candidate: string
      Accepted: Map<string, Map<string, Outcome>>
      Rejected: (ResultRun * string) list }

module Coverage =
    /// Admits only runs recorded for the candidate SHA. A result from another SHA is never carried
    /// into this candidate (no equivalence proof exists in this contract).
    let admit (candidate: string) (runs: (ResultRun * (string * Outcome) list) list) : ResultsInput =
        let sameSha (a: string) (b: string) =
            let a, b = a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant()
            a.Length >= 7 && b.Length >= 7 && a = b

        let accepted, rejected = runs |> List.partition (fun (run, _) -> sameSha run.SourceSha candidate)

        { Candidate = candidate
          Accepted =
            accepted
            |> List.groupBy (fun (run, _) -> run.Suite)
            |> List.map (fun (suite, group) -> suite, group |> List.collect snd |> Map.ofList)
            |> Map.ofList
          Rejected = rejected |> List.map (fun (run, _) -> run, $"results are for {run.SourceSha}, candidate is {candidate}") }

    let private levenshtein (a: string) (b: string) =
        let d = Array2D.init (a.Length + 1) (b.Length + 1) (fun i j -> if i = 0 then j elif j = 0 then i else 0)
        for i in 1 .. a.Length do
            for j in 1 .. b.Length do
                let cost = if a[i - 1] = b[j - 1] then 0 else 1
                d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)
        d[a.Length, b.Length]

    let private render (t: ScannedTest) (project: string option) =
        match project with
        | Some p -> $"""playwright:{t.Container} > {String.Join(" > ", t.TitlePath)} [{p}]"""
        | None -> $"dotnet:{t.Container}.{List.last t.TitlePath}"

    let private underAny (paths: string list) (file: string) =
        paths.IsEmpty || paths |> List.exists (fun p -> file = p || file.StartsWith(p.TrimEnd('/') + "/"))

    let private structural (suite: Suite) (t: ScannedTest) =
        [ if t.Placeholder then "placeholder body (DOKIMOS-UNIMPLEMENTED or not implemented)"
          if t.Skipped then "skipped"
          if not t.Placeholder && not (TestScan.asserts t) then "no assertion"
          if t.Assertions > 0 && t.Assertions <= t.TrivialAssertions then "only trivially-true assertions"
          if not suite.RequiredBoundary.IsEmpty && not (TestScan.reaches suite.RequiredBoundary t) then
              $"""does not reach the suite boundary ({String.Join(", ", suite.RequiredBoundary)}): exercises only helpers or mocks""" ]

    let private identityRow (set: ObligationSet) (scan: ScanInput) (results: ResultsInput) (test: BoundTest) =
        let suite = set.Suites |> List.find (fun s -> s.Id = test.Suite)

        let candidates, project, executable =
            match test.Identity with
            | Dotnet(cls, m) ->
                let found = scan.Tests |> List.filter (fun t -> t.Container = cls && t.TitlePath = [ m ])
                let scope (t: ScannedTest) =
                    [ if not (scan.Compiled t.File) then "excluded from execution: not compiled by its test project"
                      if not (underAny suite.Paths t.File) then $"""excluded from execution: {t.File} is outside {String.Join(" ", suite.Paths)}"""
                      if not (TestScan.filterAdmits (TestScan.commandFilter suite.Command) $"{cls}.{m}") then
                          "excluded from execution: the suite's --filter does not select it" ]
                found, None, scope
            | Playwright(file, titles, p) ->
                let found = scan.Tests |> List.filter (fun t -> t.Container = file && t.TitlePath = titles)
                let root = suite.TestRoot |> Option.map (fun r -> r.TrimEnd('/') + "/") |> Option.defaultValue ""
                let projects = TestScan.commandProjects suite.Command
                let scope (_: ScannedTest) =
                    [ if not (underAny suite.Paths (root + file)) then $"""excluded from execution: {root}{file} is outside {String.Join(" ", suite.Paths)}"""
                      if not projects.IsEmpty && not (List.contains p projects) then $"excluded from execution: the suite runs project {String.Join(',', projects)}, not {p}" ]
                found, Some p, scope
            | Record _ -> [], None, (fun _ -> [])

        let status =
            match test.Identity, candidates with
            | Record kind, _ -> Blocked $"record evidence ({kind}): judged by its own producer, not by Dokimos"
            | _, [] ->
                let expected =
                    match test.Identity with
                    | Dotnet(cls, m) -> cls, m
                    | Playwright(file, titles, _) -> file, List.last titles
                    | Record k -> k, k
                let near =
                    scan.Tests
                    |> List.filter (fun t ->
                        let title = List.last t.TitlePath
                        (t.Container = fst expected || t.File.EndsWith(fst expected))
                        && (test.Obligations |> List.exists (fun o -> title.Contains o) || levenshtein title (snd expected) <= 3))
                    |> List.map (fun t -> render t project)
                NotGenerated near
            | _, [ t ] ->
                match structural suite t @ executable t with
                | _ :: _ as reasons -> Invalid reasons
                | [] ->
                    match results.Accepted |> Map.tryFind test.Suite with
                    | Some outcomes ->
                        match outcomes |> Map.tryFind test.Id with
                        | Some Outcome.Passed -> IdentityPassed
                        | Some(Outcome.Failed detail) -> IdentityFailed detail
                        | Some Outcome.Skipped -> Invalid [ "skipped at run time" ]
                        | Some Outcome.NotExecuted
                        | None -> IdentityStatus.NotExecuted
                    | None when results.Rejected |> List.exists (fun (run, _) -> run.Suite = test.Suite) ->
                        Blocked "results exist only for another SHA"
                    | None -> Generated
            | _, many -> Invalid [ $"declared {many.Length} times" ]

        { Identity = test.Id
          Suite = test.Suite
          Obligations = test.Obligations
          Status = status
          Location = candidates |> List.tryHead |> Option.map (fun t -> $"{t.File}:{t.Line}") }

    let private obligationStatus (rows: IdentityRow list) =
        let has f = rows |> List.exists (fun r -> f r.Status)
        if rows.IsEmpty then ObligationNotGenerated
        elif has (function IdentityFailed _ -> true | _ -> false) then ObligationFailed
        elif has (function Invalid _ -> true | _ -> false) then ObligationInvalid
        elif has (function NotGenerated _ -> true | _ -> false) then ObligationNotGenerated
        elif has (function Blocked _ -> true | _ -> false) then ObligationBlocked
        elif rows |> List.forall (fun r -> r.Status = IdentityPassed) then Verified
        else ObligationNotRun

    let identityStatusText =
        function
        | NotGenerated _ -> "not-generated"
        | Invalid _ -> "invalid"
        | Generated -> "generated"
        | NotExecuted -> "not-executed"
        | IdentityPassed -> "passed"
        | IdentityFailed _ -> "failed"
        | Blocked _ -> "blocked"

    let obligationStatusText =
        function
        | Verified -> "verified"
        | ObligationFailed -> "failed"
        | ObligationInvalid -> "invalid"
        | ObligationNotGenerated -> "not-generated"
        | ObligationBlocked -> "blocked"
        | ObligationNotRun -> "not-run"

    let private reasonsOf (row: IdentityRow) =
        match row.Status with
        | NotGenerated [] -> [ $"{row.Identity}: not generated" ]
        | NotGenerated near -> [ $"""{row.Identity}: not generated; similar: {String.Join("; ", near)}""" ]
        | Invalid reasons -> reasons |> List.map (fun r -> $"{row.Identity}: {r}")
        | Generated -> [ $"{row.Identity}: generated, no result at the candidate" ]
        | NotExecuted -> [ $"{row.Identity}: suite ran, identity has no result" ]
        | IdentityFailed d -> [ $"{row.Identity}: failed: {d}" ]
        | Blocked r -> [ $"{row.Identity}: blocked: {r}" ]
        | IdentityPassed -> []

    /// Reconciles the plan with the source scan, the admitted results and any fault probes.
    /// `scope` limits obligations to the named requirements (a capability gate); empty = all.
    let reconcile (set: ObligationSet) (scan: ScanInput) (results: ResultsInput) (probes: ProbeResult list) (scope: string list) =
        let identities = set.Tests |> List.map (identityRow set scan results)
        let byObligation = identities |> List.collect (fun r -> r.Obligations |> List.map (fun o -> o, r)) |> List.groupBy fst

        let obligations =
            set.Obligations
            |> List.filter (fun o -> scope.IsEmpty || List.contains o.Requirement scope)
            |> List.map (fun o ->
                let rows = byObligation |> List.tryFind (fst >> (=) o.Id) |> Option.map (snd >> List.map snd) |> Option.defaultValue []
                let status = obligationStatus rows
                let mine = probes |> List.filter (fun p -> List.contains o.Id p.Obligations && p.SourceSha = results.Candidate)
                let survived = mine |> List.filter (fun p -> p.Outcome = Survived)
                let status, probeReasons =
                    if status = Verified && not survived.IsEmpty then
                        ObligationInvalid, survived |> List.map (fun p -> $"fault probe {p.ProbeId} survived: the bound tests did not detect it")
                    else status, []
                let quality =
                    match status with
                    | Verified when mine |> List.exists (fun p -> p.Outcome = Detected) -> "fault-sensitive"
                    | Verified -> "passing (fault sensitivity not demonstrated)"
                    | _ -> "none"
                { Obligation = o.Id
                  Requirement = o.Requirement
                  Status = status
                  Quality = quality
                  Identities = rows
                  Reasons = probeReasons @ (rows |> List.collect reasonsOf) })

        let planned = set.Tests |> List.map _.Id |> Set.ofList

        { Candidate = results.Candidate
          PlanId = set.PlanId
          Scope = scope
          Identities = identities
          Obligations = obligations
          RejectedRuns = results.Rejected
          UnplannedResults =
            results.Accepted |> Map.toList |> List.sumBy (fun (_, m) -> m |> Map.keys |> Seq.filter (planned.Contains >> not) |> Seq.length) }

    let allVerified (report: CoverageReport) =
        report.Obligations |> List.forall (fun o -> o.Status = Verified)

    // ---------------------------------------------------------------- wire

    [<Literal>]
    let Schema = "dokimos.verification-coverage/1"

    let private countBy f items = items |> List.countBy f |> List.sortBy fst |> List.map (fun (k, n) -> k, n) |> dict

    let toJson (set: ObligationSet) (report: CoverageReport) =
        let requirements =
            report.Obligations
            |> List.groupBy _.Requirement
            |> List.map (fun (r, os) -> r, os |> List.forall (fun o -> o.Status = Verified))

        let causes =
            report.Identities
            |> List.filter (fun r -> r.Status <> IdentityPassed)
            |> List.collect (fun r ->
                let kind =
                    match r.Status with
                    | Invalid reasons -> reasons |> List.map (fun reason -> $"{r.Suite}: invalid: {reason.Split(':')[0]}")
                    | other -> [ $"{r.Suite}: {identityStatusText other}" ]
                kind |> List.map (fun k -> k, r.Obligations))
            |> List.groupBy fst
            |> List.map (fun (cause, items) -> {| cause = cause; obligations = items |> List.collect snd |> List.distinct |> List.length |})
            |> List.sortByDescending _.obligations

        Contracts.serialize
            {| schema = Schema
               candidate = report.Candidate
               planId = report.PlanId
               planDigest = set.PlanDigest
               scope = report.Scope
               verified = allVerified report
               summary =
                {| obligations = report.Obligations.Length
                   obligationStatus = report.Obligations |> countBy (fun o -> obligationStatusText o.Status)
                   identityStatus = report.Identities |> countBy (fun r -> identityStatusText r.Status)
                   requirements = requirements.Length
                   requirementsVerified = requirements |> List.filter snd |> List.length
                   faultSensitive = report.Obligations |> List.filter (fun o -> o.Quality = "fault-sensitive") |> List.length
                   unplannedResults = report.UnplannedResults |}
               bySuite =
                report.Identities
                |> List.groupBy _.Suite
                |> List.map (fun (suite, rows) -> {| suite = suite; identities = rows.Length; status = rows |> countBy (fun r -> identityStatusText r.Status) |})
               causes = causes
               rejectedRuns = report.RejectedRuns |> List.map (fun (run, why) -> {| suite = run.Suite; path = run.Path; sourceSha = run.SourceSha; reason = why |})
               obligations =
                report.Obligations
                |> List.map (fun o ->
                    {| id = o.Obligation
                       requirement = o.Requirement
                       status = obligationStatusText o.Status
                       quality = o.Quality
                       reasons = o.Reasons
                       identities =
                        o.Identities
                        |> List.map (fun r -> {| id = r.Identity; suite = r.Suite; status = identityStatusText r.Status; location = r.Location |> Option.toObj |}) |}) |}
