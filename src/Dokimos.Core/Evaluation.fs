namespace Dokimos.Core

open System
open Dokimos.Domain

/// Where a ratchet's limit comes from. Policy 1.2.0 derives it from the
/// accepted baseline snapshot instead of a hand-typed number.
type RatchetBound =
    | FixedBound of decimal
    | AcceptedBaseline

type RatchetRule =
    { MetricId: string
      MetricVersion: int
      Bound: RatchetBound
      Preference: Preference
      Disposition: ThresholdDisposition }

type ThresholdRule =
    { MetricId: string
      MetricVersion: int
      Maximum: decimal
      Disposition: ThresholdDisposition }

type SuppressionStatus =
    | SuppressionActive
    | SuppressionRevoked

/// A policy-context waiver for one finding. It never removes the finding; it
/// only changes how policy judges it, and only until it expires.
type Suppression =
    { FindingId: string
      Reason: string
      Scope: string
      Actor: string option
      Created: DateTimeOffset
      Expires: DateTimeOffset option
      Status: SuppressionStatus }

type QualityPolicy =
    { SchemaVersion: string
      /// Content digest of the policy document, recorded as provenance.
      Identity: string
      Baseline: string
      Ratchets: RatchetRule list
      Thresholds: ThresholdRule list
      Regressions: ThresholdDisposition option
      IntroducedFindings: ThresholdDisposition option
      RequiredEvidence: string list
      ObservedNotRatcheted: string list
      Suppressions: Suppression list }

type RuleKind =
    | RatchetRuleKind
    | ThresholdRuleKind
    | RegressionRuleKind
    | IntroducedFindingRuleKind
    | RequiredEvidenceRuleKind

type SuppressionApplication =
    | Suppressed of Suppression
    | SuppressionExpired of Suppression

type RuleOutcome =
    { Rule: RuleKind
      Subject: string
      Scope: string
      Result: GateResult
      Baseline: decimal option
      Suppression: SuppressionApplication option
      Explanation: string }

type EvaluationDisposition =
    | GatePassed
    | GatePassedWithWarnings
    | GateFailed
    | GateEvidenceUnavailable
    | GateIncompatibleSnapshots of reason: string

type ComparisonSummary =
    { Improved: int
      Deteriorated: int
      Unchanged: int
      Changed: int
      Added: int
      Removed: int
      Incompatible: int
      Unavailable: int
      FindingsIntroduced: int
      FindingsResolved: int
      FindingsPersistent: int
      FindingsUnavailable: int }

type PolicyEvaluation =
    { Disposition: EvaluationDisposition
      Baseline: CanonicalSnapshot
      Current: CanonicalSnapshot
      Policy: QualityPolicy
      EvaluatedAt: DateTimeOffset
      Comparison: CanonicalComparison
      Summary: ComparisonSummary
      Outcomes: RuleOutcome list }

module ExitCodes =
    [<Literal>]
    let Continue = 0

    [<Literal>]
    let UnexpectedFault = 1

    [<Literal>]
    let InvalidInvocation = 2

    [<Literal>]
    let EvidenceUnavailable = 3

    [<Literal>]
    let PolicyFailure = 4

    [<Literal>]
    let StoreConflict = 5

module Evaluation =
    let private preferenceText =
        function
        | LowerIsBetter -> "lower is better"
        | HigherIsBetter -> "higher is better"

    let private observation (rule: string) (version: int) (metric: CanonicalMetric) =
        match MetricId.tryCreate rule, MetricVersion.tryCreate version with
        | Ok id, Ok v ->
            Some
                { Metric = id
                  MetricVersion = v
                  Scope = if metric.Scope = CanonicalSnapshot.RepositoryScope then Repository else File metric.Scope
                  Measurement = metric.Measurement
                  Provenance =
                    { Collector = metric.Source
                      CollectorVersion = string metric.MetricVersion
                      ConfigurationId = ""
                      CollectedAt = DateTimeOffset.MinValue } }
        | _ -> None

    let private outcome rule subject scope result baseline explanation =
        { Rule = rule; Subject = subject; Scope = scope; Result = result; Baseline = baseline; Suppression = None; Explanation = explanation }

    let private absent metricId =
        EvidenceUnavailable(InsufficientEvidence $"{metricId} is not present in the current snapshot")

    let private unevaluatedExplanation metricId scope =
        function
        | EvidenceUnavailable _ -> $"{metricId} at {scope} is unavailable; the rule was not evaluated and did not pass."
        | CollectionFailed failure -> $"{metricId} at {scope} failed to collect ({failure.Code}); the rule was not evaluated and did not pass."
        | Incompatible (expected, actual) -> $"{metricId} at {scope} uses definition v{actual}; the rule targets v{expected}, so it was not evaluated."
        | _ -> ""

    let private metricsNamed metricId (snapshot: CanonicalSnapshot) =
        snapshot.Metrics |> List.filter (fun m -> m.MetricId = metricId)

    let private versioned ruleKind (metricId: string) (version: int) (metric: CanonicalMetric) evaluate describe =
        if metric.MetricVersion <> version then
            let result = Incompatible(version, metric.MetricVersion)
            outcome ruleKind metricId metric.Scope result None (unevaluatedExplanation metricId metric.Scope result)
        else
            match observation metricId version metric with
            | None -> outcome ruleKind metricId metric.Scope (NotEvaluated "invalid metric identity") None "The policy names an invalid metric identity."
            | Some obs ->
                let result = evaluate obs
                let text =
                    match result with
                    | EvidenceUnavailable _ | CollectionFailed _ | Incompatible _ -> unevaluatedExplanation metricId metric.Scope result
                    | _ -> describe result
                outcome ruleKind metricId metric.Scope result None text

    let private ratchetOutcome (rule: RatchetRule) (metric: CanonicalMetric) (bound: decimal) =
        match MetricId.tryCreate rule.MetricId with
        | Error e -> outcome RatchetRuleKind rule.MetricId metric.Scope (NotEvaluated e) None e
        | Ok id ->
            let ratchet = { Metric = id; BestAccepted = bound; Preference = rule.Preference; Disposition = rule.Disposition }
            let subject = if metric.Scope = CanonicalSnapshot.RepositoryScope then rule.MetricId else $"{rule.MetricId} at {metric.Scope}"
            let describe result =
                match result with
                | Pass -> $"{subject} does not deteriorate beyond the best accepted state {bound} ({preferenceText rule.Preference})."
                | _ ->
                    let actual = CanonicalMetric.value metric |> Option.map string |> Option.defaultValue "?"
                    $"{subject} is {actual}; the best accepted state is {bound} ({preferenceText rule.Preference}). Restore it to at least the best accepted state."
            { versioned RatchetRuleKind rule.MetricId rule.MetricVersion metric (Policy.evaluateRatchet ratchet) describe with
                Baseline = Some bound }

    /// A baseline-bound ratchet judges every scope the accepted baseline
    /// measured, against that scope's accepted value. A scope with no accepted
    /// value is reported unavailable, never passed.
    let private baselineBound (rule: RatchetRule) (baseline: CanonicalSnapshot) (metric: CanonicalMetric) =
        match baseline.Metrics |> List.tryFind (fun b -> b.MetricId = rule.MetricId && b.Scope = metric.Scope) |> Option.bind CanonicalMetric.value with
        | Some accepted -> ratchetOutcome rule metric accepted
        | None ->
            let result = EvidenceUnavailable(InsufficientEvidence $"the accepted baseline has no {rule.MetricId} value at {metric.Scope}")
            outcome RatchetRuleKind rule.MetricId metric.Scope result None
                $"{rule.MetricId} at {metric.Scope} has no accepted baseline value; the ratchet was not evaluated and did not pass. New scopes are judged by `dokimos ratchet`."

    let private ratchetOutcomes (policy: QualityPolicy) (baseline: CanonicalSnapshot) current =
        policy.Ratchets
        |> List.collect (fun rule ->
            let candidates =
                match rule.Bound with
                | FixedBound _ -> metricsNamed rule.MetricId current |> List.filter (fun m -> m.Scope = CanonicalSnapshot.RepositoryScope)
                | AcceptedBaseline -> metricsNamed rule.MetricId current
            match candidates, rule.Bound with
            | [], _ ->
                let result = absent rule.MetricId
                let limit = match rule.Bound with FixedBound b -> Some b | AcceptedBaseline -> None
                [ outcome RatchetRuleKind rule.MetricId CanonicalSnapshot.RepositoryScope result limit (unevaluatedExplanation rule.MetricId CanonicalSnapshot.RepositoryScope result) ]
            | metrics, FixedBound bound -> metrics |> List.map (fun metric -> ratchetOutcome rule metric bound)
            | metrics, AcceptedBaseline -> metrics |> List.map (baselineBound rule baseline))

    let private thresholdOutcomes (policy: QualityPolicy) current =
        policy.Thresholds
        |> List.collect (fun rule ->
            match metricsNamed rule.MetricId current with
            | [] ->
                let result = absent rule.MetricId
                [ outcome ThresholdRuleKind rule.MetricId CanonicalSnapshot.RepositoryScope result None (unevaluatedExplanation rule.MetricId CanonicalSnapshot.RepositoryScope result) ]
            | metrics ->
                metrics
                |> List.map (fun metric ->
                    match MetricId.tryCreate rule.MetricId with
                    | Error e -> outcome ThresholdRuleKind rule.MetricId metric.Scope (NotEvaluated e) None e
                    | Ok id ->
                        let threshold: Threshold = { Metric = id; Maximum = rule.Maximum; Disposition = rule.Disposition }
                        let describe result =
                            match result with
                            | Pass -> $"{rule.MetricId} at {metric.Scope} is within the maximum {rule.Maximum}."
                            | _ ->
                                let actual = CanonicalMetric.value metric |> Option.map string |> Option.defaultValue "?"
                                $"{rule.MetricId} at {metric.Scope} is {actual}, above the maximum {rule.Maximum}. Reduce it to {rule.Maximum} or less."
                        versioned ThresholdRuleKind rule.MetricId rule.MetricVersion metric (Policy.evaluateThreshold threshold) describe))

    let private judge disposition actual limit =
        match disposition with
        | ObserveOnly -> ObservedOnly(actual, limit)
        | Warn -> Warning(actual, limit)
        | Fail -> Failure(actual, limit)

    let private measurementText =
        function
        | Some (Available (v, _)) -> string v
        | Some (Unavailable _) -> "unavailable"
        | Some (Failed _) -> "failed"
        | None -> "absent"

    let private regressionOutcomes (policy: QualityPolicy) (comparison: CanonicalComparison) =
        match policy.Regressions with
        | None -> []
        | Some disposition ->
            comparison.MetricChanges
            |> List.choose (fun change ->
                match change.Kind, change.Before, change.After with
                | MetricDeteriorated, Some (Available (before, _)), Some (Available (after, _)) ->
                    Some(
                        outcome RegressionRuleKind change.MetricId change.Scope (judge disposition after before) (Some before)
                            $"{change.MetricId} at {change.Scope} deteriorated from {before} to {after} against the baseline. Return it to {before} or better."
                    )
                | MetricIncompatible, _, _ ->
                    let result = Incompatible(defaultArg change.BeforeVersion 0, defaultArg change.AfterVersion 0)
                    Some(outcome RegressionRuleKind change.MetricId change.Scope result None
                        $"{change.MetricId} at {change.Scope} changed definition or unit between baseline and current; it is not compared as a trend.")
                | MetricUnavailable, _, Some (Unavailable reason) ->
                    Some(outcome RegressionRuleKind change.MetricId change.Scope (EvidenceUnavailable reason) None
                        $"{change.MetricId} at {change.Scope} is unavailable in the current snapshot (baseline: {measurementText change.Before}); regression was not evaluated.")
                | MetricUnavailable, _, Some (Failed failure) ->
                    Some(outcome RegressionRuleKind change.MetricId change.Scope (CollectionFailed failure) None
                        $"{change.MetricId} at {change.Scope} failed to collect in the current snapshot; regression was not evaluated.")
                | MetricUnavailable, Some (Unavailable reason), _ ->
                    Some(outcome RegressionRuleKind change.MetricId change.Scope (EvidenceUnavailable reason) None
                        $"{change.MetricId} at {change.Scope} is unavailable in the baseline; regression was not evaluated.")
                | MetricUnavailable, Some (Failed failure), _ ->
                    Some(outcome RegressionRuleKind change.MetricId change.Scope (CollectionFailed failure) None
                        $"{change.MetricId} at {change.Scope} failed to collect in the baseline; regression was not evaluated.")
                | _ -> None)

    let private applicableSuppression (asOf: DateTimeOffset) (policy: QualityPolicy) findingId =
        policy.Suppressions
        |> List.filter (fun s -> s.FindingId = findingId && s.Status = SuppressionActive)
        |> List.sortByDescending _.Created
        |> List.tryHead
        |> Option.map (fun s ->
            match s.Expires with
            | Some expiry when expiry <= asOf -> SuppressionExpired s
            | _ -> Suppressed s)

    let private findingOutcomes asOf (policy: QualityPolicy) (comparison: CanonicalComparison) =
        match policy.IntroducedFindings with
        | None -> []
        | Some disposition ->
            comparison.FindingChanges
            |> List.filter (fun f -> f.Kind = FindingIntroduced)
            |> List.map (fun f ->
                let signals = f.Evidence |> List.map (Wire.signal >> fun s -> s.Kind) |> String.concat ", "
                match applicableSuppression asOf policy f.FindingId with
                | Some (Suppressed s as applied) ->
                    { outcome IntroducedFindingRuleKind f.FindingId f.Scope (ObservedOnly(1m, 0m)) None
                        $"{f.FindingKind} introduced at {f.Scope} ({signals}); suppressed: {s.Reason}. The finding remains recorded."
                        with Suppression = Some applied }
                | Some (SuppressionExpired s as applied) ->
                    { outcome IntroducedFindingRuleKind f.FindingId f.Scope (judge disposition 1m 0m) None
                        $"{f.FindingKind} introduced at {f.Scope} ({signals}); its suppression expired, so policy applies. Resolve the finding or renew the waiver."
                        with Suppression = Some applied }
                | None ->
                    outcome IntroducedFindingRuleKind f.FindingId f.Scope (judge disposition 1m 0m) None
                        $"{f.FindingKind} introduced at {f.Scope} ({signals}). Reduce the contributing signals or record a waiver.")

    let private requiredOutcomes (policy: QualityPolicy) current =
        policy.RequiredEvidence
        |> List.collect (fun metricId ->
            match metricsNamed metricId current with
            | [] ->
                let result = absent metricId
                [ outcome RequiredEvidenceRuleKind metricId CanonicalSnapshot.RepositoryScope result None $"Required evidence {metricId} is missing from the current snapshot." ]
            | metrics ->
                metrics
                |> List.map (fun m ->
                    match m.Measurement with
                    | Available _ -> outcome RequiredEvidenceRuleKind metricId m.Scope Pass None $"Required evidence {metricId} is available."
                    | Unavailable reason -> outcome RequiredEvidenceRuleKind metricId m.Scope (EvidenceUnavailable reason) None $"Required evidence {metricId} at {m.Scope} is unavailable."
                    | Failed failure -> outcome RequiredEvidenceRuleKind metricId m.Scope (CollectionFailed failure) None $"Required evidence {metricId} at {m.Scope} failed to collect ({failure.Code}: {failure.Message})."))

    let summarize (comparison: CanonicalComparison) =
        let metrics kind = comparison.MetricChanges |> List.filter (fun c -> c.Kind = kind) |> List.length
        let findings kind = comparison.FindingChanges |> List.filter (fun c -> c.Kind = kind) |> List.length
        { Improved = metrics MetricImproved
          Deteriorated = metrics MetricDeteriorated
          Unchanged = metrics MetricUnchanged
          Changed = metrics MetricChanged
          Added = metrics MetricAdded
          Removed = metrics MetricRemoved
          Incompatible = metrics MetricIncompatible
          Unavailable = metrics MetricUnavailable
          FindingsIntroduced = findings FindingIntroduced
          FindingsResolved = findings FindingResolved
          FindingsPersistent = findings FindingPersistent
          FindingsUnavailable = findings FindingUnavailable }

    let private dispositionOf outcomes =
        let any predicate = outcomes |> List.exists predicate
        let isFailure o = match o.Result with Failure _ -> true | _ -> false
        let isWarning o = match o.Result with Warning _ -> true | _ -> false
        let missingRequired o = o.Rule = RequiredEvidenceRuleKind && o.Result <> Pass
        // A baseline-bound ratchet with no accepted value has no limit at all.
        let unboundRatchet o =
            o.Rule = RatchetRuleKind && o.Baseline.IsNone && (match o.Result with EvidenceUnavailable _ -> true | _ -> false)
        if any isFailure then GateFailed
        elif any missingRequired || any unboundRatchet then GateEvidenceUnavailable
        elif any isWarning then GatePassedWithWarnings
        else GatePassed

    let evaluate (asOf: DateTimeOffset) (policy: QualityPolicy) (baseline: CanonicalSnapshot) (current: CanonicalSnapshot) =
        let comparison = CanonicalComparison.compare baseline current
        let outcomes =
            if baseline.Repository <> current.Repository then []
            else
                requiredOutcomes policy current
                @ ratchetOutcomes policy baseline current
                @ thresholdOutcomes policy current
                @ regressionOutcomes policy comparison
                @ findingOutcomes asOf policy comparison
        let disposition =
            if baseline.Repository <> current.Repository then
                GateIncompatibleSnapshots $"baseline repository {baseline.Repository} differs from current repository {current.Repository}"
            else dispositionOf outcomes
        { Disposition = disposition
          Baseline = baseline
          Current = current
          Policy = policy
          EvaluatedAt = asOf
          Comparison = comparison
          Summary = summarize comparison
          Outcomes = outcomes }

    let exitCode =
        function
        | GatePassed
        | GatePassedWithWarnings -> ExitCodes.Continue
        | GateFailed -> ExitCodes.PolicyFailure
        | GateEvidenceUnavailable
        | GateIncompatibleSnapshots _ -> ExitCodes.EvidenceUnavailable
