namespace Dokimos.Core

open System

module ExitCodesRatchet =
    /// Ratchet verdict invalid-exceptions: an exception is malformed,
    /// incomplete or expired. Distinct from a regression (4).
    [<Literal>]
    let InvalidExceptions = 6

module QualityRatchet =
    [<Literal>]
    let BaselineSchemaVersion = "1.0.0"

    let private semantics ruleId = QualityRules.semanticsOf ruleId |> Option.defaultValue CountMustNotGrow

    let private ruleName ruleId =
        QualityRules.tryFind ruleId |> Option.map _.Name |> Option.defaultValue ruleId

    // --- exceptions ----------------------------------------------------------

    let expiryOf (e: QualityException) =
        match e.Review with
        | Expires d
        | ExpiresOrReview (d, _) -> Some d
        | ReviewCondition _ -> None

    /// An exception is valid through its expiry date and expired after it.
    let statusAt (asOf: DateTimeOffset) (e: QualityException) =
        match expiryOf e with
        | Some d when DateOnly.FromDateTime(asOf.UtcDateTime) > d -> ExceptionExpired
        | _ -> ExceptionActive

    /// Problems that make an exception unusable regardless of date.
    let validate (asOf: DateTimeOffset) (all: QualityException list) (e: QualityException) =
        let today = DateOnly.FromDateTime(asOf.UtcDateTime)
        [ match QualityRules.tryFind e.RuleId with
          | None -> $"ruleId '{e.RuleId}' is not in the rule catalog"
          | Some rule ->
              match rule.Engine, e.AllowedValue with
              | RatchetEngine _, None -> $"allowedValue is required for ratchet rule {e.RuleId}"
              | RatchetEngine _, Some v when v < 0m -> "allowedValue must not be negative"
              | _ -> ()
              match rule.ScopeKind with
              | RepositoryScoped when e.Scope <> QualitySignals.RepositoryScope -> $"{e.RuleId} is repository-scoped; scope must be \"repository\""
              | _ -> ()
          if String.IsNullOrWhiteSpace e.Id then "id is required"
          if String.IsNullOrWhiteSpace e.Scope then "scope is required"
          if String.IsNullOrWhiteSpace e.Rationale then "rationale is required"
          if String.IsNullOrWhiteSpace e.Owner then "owner is required"
          if e.Evidence.IsEmpty || e.Evidence |> List.exists String.IsNullOrWhiteSpace then "evidence must list at least one non-empty reference"
          if e.Created > today then "created " + e.Created.ToString("yyyy-MM-dd") + " is in the future"
          match expiryOf e with
          | Some d when d < e.Created -> "expires is before created"
          | _ -> ()
          match e.Review with
          | ReviewCondition c
          | ExpiresOrReview (_, c) when String.IsNullOrWhiteSpace c -> "reviewCondition must not be empty"
          | _ -> ()
          if all |> List.filter (fun other -> other.Id = e.Id) |> List.length > 1 then $"duplicate exception id '{e.Id}'" ]

    /// Partitions decoded exceptions into usable and invalid at `asOf`.
    let classify asOf (decoded: ExceptionSet) =
        let problems = decoded.Valid |> List.map (fun e -> e, validate asOf decoded.Valid e)
        { Valid = problems |> List.filter (snd >> List.isEmpty) |> List.map fst
          Invalid =
            decoded.Invalid
            @ (problems |> List.filter (snd >> List.isEmpty >> not) |> List.map (fun (e, p) -> { Id = e.Id; Problems = p })) }

    // --- comparison ----------------------------------------------------------

    let private describe ruleId scope before after recorded =
        match semantics ruleId with
        | SizeOverThresholdMustNotGrow when not recorded ->
            $"{scope} crossed the large-file threshold: {after} lines (threshold {before})."
        | SizeOverThresholdMustNotGrow -> $"{scope} is over the large-file threshold and grew from {before} to {after} lines."
        | CountMustNotGrow -> $"{ruleName ruleId} at {scope} increased from {before} to {after}."

    /// Pure judgement of one enforced rule against the baseline.
    let private compareRule (configuration: RatchetConfiguration) ruleId (accepted: Map<string, decimal>) (current: Map<string, ScopeMeasure>) =
        let scopes = Set.union (accepted |> Map.keys |> Set.ofSeq) (current |> Map.keys |> Set.ofSeq)
        let threshold = decimal configuration.LargeFileLines
        scopes
        |> Seq.choose (fun scope ->
            let recorded = accepted |> Map.tryFind scope
            let measured = current |> Map.tryFind scope
            let after = measured |> Option.map _.Value |> Option.defaultValue 0m
            let evidence = measured |> Option.map _.Evidence |> Option.defaultValue []
            let before =
                match recorded, semantics ruleId with
                | Some v, _ -> v
                | None, SizeOverThresholdMustNotGrow -> threshold
                | None, CountMustNotGrow -> 0m
            let finding kind degraded =
                { RuleId = ruleId
                  Scope = scope
                  Kind = kind
                  Before = before
                  After = after
                  BaselineRecorded = recorded.IsSome
                  Degraded = degraded
                  Evidence = evidence
                  ExceptionId = None }
            if after > before then Some(finding Regression (describe ruleId scope before after recorded.IsSome))
            elif after < before && recorded.IsSome then
                Some(finding Improvement $"{ruleName ruleId} at {scope} improved from {before} to {after}; run `dokimos ratchet baseline update` to tighten the baseline.")
            else None)
        |> List.ofSeq

    let private applyExceptions (active: QualityException list) (finding: RatchetFinding) =
        match finding.Kind with
        | Regression ->
            match active |> List.tryFind (fun e -> e.RuleId = finding.RuleId && e.Scope = finding.Scope) with
            | Some e when finding.After <= defaultArg e.AllowedValue finding.After ->
                { finding with Kind = ExceptedRegression; ExceptionId = Some e.Id }
            | Some e ->
                { finding with
                    Degraded = $"{finding.Degraded} Exception {e.Id} allows at most {defaultArg e.AllowedValue 0m}; the excess is a regression." }
            | None -> finding
        | _ -> finding

    let check (asOf: DateTimeOffset) (baseline: RatchetBaseline) (exceptions: ExceptionSet) (measurement: QualityMeasurement) =
        let classified = classify asOf exceptions
        let ratchetExceptions = classified.Valid |> List.filter (fun e -> List.contains e.RuleId QualityRules.ratchetRuleIds)
        let active, expired = ratchetExceptions |> List.partition (fun e -> statusAt asOf e = ExceptionActive)
        let ruleStates =
            QualityRules.ratchetRuleIds
            |> List.map (fun id ->
                match baseline.Rules |> Map.tryFind id, measurement.Rules |> Map.tryFind id with
                | None, _ -> id, RuleNotEnforced
                | Some _, Some (Measured _) -> id, RuleMeasured
                | Some _, Some (NotMeasured reason) -> id, RuleNotMeasured reason
                | Some _, None -> id, RuleNotMeasured "no analyzer produced this rule")
        let findings =
            baseline.Rules
            |> Map.toList
            |> List.collect (fun (id, accepted) ->
                match measurement.Rules |> Map.tryFind id with
                | Some (Measured current) -> compareRule baseline.Configuration id accepted current
                | _ -> [])
            |> List.map (applyExceptions active)
        let used = findings |> List.choose _.ExceptionId |> Set.ofList
        let unavailable =
            ruleStates |> List.choose (function id, RuleNotMeasured reason -> Some $"{id}: {reason}" | _ -> None)
        let invalidReasons =
            (classified.Invalid |> List.map (fun i -> "exception " + i.Id + " is invalid: " + String.Join("; ", i.Problems)))
            @ (expired
               |> List.map (fun e ->
                   let expiry = expiryOf e |> Option.map (fun d -> d.ToString("yyyy-MM-dd")) |> Option.defaultValue "?"
                   $"exception {e.Id} expired on {expiry}; renew it with fresh review or fix the regression"))
        let unknownRules =
            baseline.Rules |> Map.keys |> Seq.filter (fun id -> not (List.contains id QualityRules.ratchetRuleIds)) |> Seq.map (fun id -> $"baseline enforces unknown rule {id}") |> List.ofSeq
        let regressions = findings |> List.filter (fun f -> f.Kind = Regression)
        let verdict, reasons =
            if not unavailable.IsEmpty || not unknownRules.IsEmpty then RatchetUnavailable, unavailable @ unknownRules
            elif not invalidReasons.IsEmpty then RatchetInvalidExceptions, invalidReasons
            elif not regressions.IsEmpty then RatchetRegression, regressions |> List.map (fun f -> $"{f.RuleId} {f.Scope}: {f.Before} -> {f.After}")
            else RatchetPass, []
        { Verdict = verdict
          Reasons = reasons
          CheckedAt = asOf
          Baseline = baseline
          RuleStates = ruleStates
          Findings = findings |> List.sortBy (fun f -> f.RuleId, f.Scope)
          ActiveExceptions = active
          ExpiredExceptions = expired
          InvalidExceptions = classified.Invalid
          UnusedExceptions = active |> List.map _.Id |> List.filter (fun id -> not (used.Contains id))
          Measurement = measurement }

    /// A verdict that is not RatchetUnavailable when the baseline itself
    /// cannot be read is impossible: callers use this for missing inputs.
    let exitCode =
        function
        | RatchetPass -> ExitCodes.Continue
        | RatchetRegression -> ExitCodes.PolicyFailure
        | RatchetUnavailable -> ExitCodes.EvidenceUnavailable
        | RatchetInvalidExceptions -> ExitCodesRatchet.InvalidExceptions

    let verdictTag =
        function
        | RatchetPass -> "pass"
        | RatchetRegression -> "regression"
        | RatchetInvalidExceptions -> "invalid-exceptions"
        | RatchetUnavailable -> "unavailable"

    // --- baseline lifecycle --------------------------------------------------

    let private recordable (configuration: RatchetConfiguration) ruleId (current: Map<string, ScopeMeasure>) =
        current
        |> Map.filter (fun _ m ->
            match semantics ruleId with
            | SizeOverThresholdMustNotGrow -> m.Value > decimal configuration.LargeFileLines
            | CountMustNotGrow -> m.Value > 0m || ruleId = QualityRules.CompilerWarnings || ruleId = QualityRules.ArchitectureViolations)
        |> Map.map (fun _ m -> m.Value)

    /// First acceptance: records the measured state, inherited debt included,
    /// for the selected rules. Every selected rule must have been measured.
    let initialize asOf repository configuration (rules: string list) (measurement: QualityMeasurement) =
        let missing =
            rules
            |> List.choose (fun id ->
                match measurement.Rules |> Map.tryFind id with
                | Some (Measured _) -> None
                | Some (NotMeasured reason) -> Some $"{id}: {reason}"
                | None -> Some $"{id}: unknown rule")
        if not missing.IsEmpty then Error missing
        else
            Ok
                { SchemaVersion = BaselineSchemaVersion
                  Repository = repository
                  AcceptedAt = asOf
                  Configuration = configuration
                  Rules =
                    rules
                    |> List.choose (fun id ->
                        match measurement.Rules |> Map.tryFind id with
                        | Some (Measured current) -> Some(id, recordable configuration id current)
                        | _ -> None)
                    |> Map.ofList }

    /// Tightening only: every accepted value becomes min(accepted, current);
    /// scopes that are now clean are removed. Worse current values never
    /// raise the baseline; they are reported as refused. Exceptions are not
    /// an input, so they cannot change the baseline.
    let update asOf (baseline: RatchetBaseline) (measurement: QualityMeasurement) =
        let unmeasured =
            baseline.Rules
            |> Map.keys
            |> Seq.choose (fun id ->
                match measurement.Rules |> Map.tryFind id with
                | Some (Measured _) -> None
                | Some (NotMeasured reason) -> Some $"{id}: {reason}"
                | None -> Some $"{id}: no analyzer produced this rule")
            |> List.ofSeq
        if not unmeasured.IsEmpty then Error unmeasured
        else
            let perRule =
                baseline.Rules
                |> Map.toList
                |> List.map (fun (id, accepted) ->
                    let current =
                        match measurement.Rules |> Map.tryFind id with
                        | Some (Measured c) -> recordable baseline.Configuration id c
                        | _ -> Map.empty
                    let kept =
                        accepted
                        |> Map.toList
                        |> List.choose (fun (scope, before) ->
                            match current |> Map.tryFind scope with
                            | Some now when now < before -> Some(scope, now, Some { RuleId = id; Scope = scope; Before = before; After = Some now; Kind = ScopeTightened })
                            | Some _ -> Some(scope, before, None)
                            | None -> None)
                    let removed =
                        accepted
                        |> Map.toList
                        |> List.filter (fun (scope, _) -> not (current.ContainsKey scope))
                        |> List.map (fun (scope, before) -> { RuleId = id; Scope = scope; Before = before; After = None; Kind = ScopeRemoved })
                    let refused =
                        current
                        |> Map.toList
                        |> List.choose (fun (scope, now) ->
                            match accepted |> Map.tryFind scope with
                            | Some before when now > before ->
                                Some { RuleId = id; Scope = scope; Accepted = Some before; Current = now; Reason = "current is worse than the accepted baseline; historical debt cannot raise the baseline" }
                            | None ->
                                Some { RuleId = id; Scope = scope; Accepted = None; Current = now; Reason = "scope is not in the accepted baseline; new debt cannot be added by an update" }
                            | _ -> None)
                    (id, kept |> List.map (fun (s, v, _) -> s, v) |> Map.ofList),
                    (kept |> List.choose (fun (_, _, c) -> c)) @ removed,
                    refused)
            Ok
                { Updated =
                    { baseline with
                        AcceptedAt = asOf
                        Rules = perRule |> List.map (fun (r, _, _) -> r) |> Map.ofList }
                  Changes = perRule |> List.collect (fun (_, c, _) -> c)
                  Refused = perRule |> List.collect (fun (_, _, r) -> r) }

    /// Compares two accepted baselines. Any raised value, added scope,
    /// dropped rule, or weakened configuration is a loosening.
    let diff (older: RatchetBaseline) (newer: RatchetBaseline) =
        let ruleIds = Set.union (older.Rules |> Map.keys |> Set.ofSeq) (newer.Rules |> Map.keys |> Set.ofSeq)
        let deltas =
            ruleIds
            |> Seq.collect (fun id ->
                match older.Rules |> Map.tryFind id, newer.Rules |> Map.tryFind id with
                | Some _, None -> [ { RuleId = id; Scope = "*"; From = None; To = None }, true ]
                | None, Some _ -> [ { RuleId = id; Scope = "*"; From = None; To = None }, false ]
                | None, None -> []
                | Some a, Some b ->
                    Set.union (a |> Map.keys |> Set.ofSeq) (b |> Map.keys |> Set.ofSeq)
                    |> Seq.choose (fun scope ->
                        let from, into = a |> Map.tryFind scope, b |> Map.tryFind scope
                        let delta = { RuleId = id; Scope = scope; From = from; To = into }
                        match from, into with
                        | Some x, Some y when y > x -> Some(delta, true)
                        | Some x, Some y when y < x -> Some(delta, false)
                        | None, Some _ -> Some(delta, true)
                        | Some _, None -> Some(delta, false)
                        | _ -> None)
                    |> List.ofSeq)
            |> List.ofSeq
        let oc, nc = older.Configuration, newer.Configuration
        let added a b = b |> List.filter (fun x -> not (List.contains x a))
        let configLoosened =
            [ for g in added oc.Generated nc.Generated -> $"generated glob added: {g}"
              for s in added nc.Sources oc.Sources -> $"source root removed: {s}"
              if nc.LargeFileLines > oc.LargeFileLines then yield $"largeFileLines raised from {oc.LargeFileLines} to {nc.LargeFileLines}"
              for r in added nc.ForbiddenReferences oc.ForbiddenReferences -> $"forbidden reference removed: {r.From} -> {r.To}" ]
        let configTightened =
            [ for g in added nc.Generated oc.Generated -> $"generated glob removed: {g}"
              for s in added oc.Sources nc.Sources -> $"source root added: {s}"
              if nc.LargeFileLines < oc.LargeFileLines then yield $"largeFileLines lowered from {oc.LargeFileLines} to {nc.LargeFileLines}"
              for r in added oc.ForbiddenReferences nc.ForbiddenReferences -> $"forbidden reference added: {r.From} -> {r.To}" ]
        { Loosened = deltas |> List.filter snd |> List.map fst
          Tightened = deltas |> List.filter (snd >> not) |> List.map fst
          ConfigurationLoosened = configLoosened
          ConfigurationTightened = configTightened }

    let isLoosening (d: BaselineDiff) = not d.Loosened.IsEmpty || not d.ConfigurationLoosened.IsEmpty

    /// Unifies waivers: DOK-G001 exceptions (scope = finding id) become the
    /// suppressions `dokimos evaluate` applies to introduced findings. Any
    /// invalid or expired exception in the file is an error, as in `check`.
    let gateSuppressions (asOf: DateTimeOffset) (exceptions: ExceptionSet) =
        let classified = classify asOf exceptions
        let expired = classified.Valid |> List.filter (fun e -> statusAt asOf e = ExceptionExpired)
        let problems =
            (classified.Invalid |> List.map (fun i -> "exception " + i.Id + " is invalid: " + String.Join("; ", i.Problems)))
            @ (expired |> List.map (fun e -> $"exception {e.Id} has expired"))
        if not problems.IsEmpty then Error problems
        else
            classified.Valid
            |> List.filter (fun e -> e.RuleId = QualityRules.IntroducedFinding)
            |> List.map (fun e ->
                { FindingId = e.Scope
                  Reason = $"{e.Rationale} (exception {e.Id}, owner {e.Owner})"
                  Scope = e.Scope
                  Actor = Some e.Owner
                  Created = DateTimeOffset(e.Created.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                  // Valid through the expiry date: it lapses at the next UTC midnight.
                  Expires = expiryOf e |> Option.map (fun d -> DateTimeOffset(d.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
                  Status = SuppressionActive })
            |> Ok
