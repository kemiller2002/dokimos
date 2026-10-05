namespace Dokimos.Core

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Dokimos.Domain

// ---------------------------------------------------------------------------
// Wire DTOs. Only these records are ever serialized. They contain strings,
// numbers, options and lists; every domain union is mapped to a stable tag.
// ---------------------------------------------------------------------------

type MeasurementDto =
    { State: string
      Value: decimal option
      Reason: string option
      Detail: string option }

type MetricDto =
    { MetricId: string
      MetricVersion: int
      Scope: string
      State: string
      Value: decimal option
      Unit: string
      Reason: string option
      Detail: string option
      Source: string }

type FindingDto =
    { FindingId: string
      Scope: string
      Kind: string
      State: string
      Evidence: SignalDto list
      Explanation: string }

type AnalyzerRunDto =
    { AnalyzerId: string
      Version: string
      State: string
      Reason: string option
      DurationMilliseconds: int64 option }

type ProducerDto =
    { DokimosVersion: string
      ConfigurationId: string
      AnalyzedScope: string list
      Analyzers: AnalyzerRunDto list }

type PerformanceDto =
    { TotalMilliseconds: int64
      FileCount: int
      ObservationCount: int
      UnavailableCollectors: string list
      FailedCollectors: string list }

type SnapshotDto =
    { Contract: string
      SchemaVersion: string
      SnapshotId: string
      Repository: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      Collector: string
      Producer: ProducerDto option
      Performance: PerformanceDto option
      Metrics: MetricDto list
      Findings: FindingDto list }

type SnapshotRefDto =
    { SnapshotId: string
      SchemaVersion: string
      Repository: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      DokimosVersion: string option }

type MetricChangeDto =
    { MetricId: string
      Scope: string
      Unit: string
      Kind: string
      BeforeVersion: int option
      AfterVersion: int option
      Before: MeasurementDto option
      After: MeasurementDto option
      Delta: decimal option }

type FindingChangeDto =
    { FindingId: string
      Scope: string
      FindingKind: string
      Kind: string
      Evidence: SignalDto list }

type ComparisonSummaryDto =
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

type ComparisonDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Before: SnapshotRefDto
      After: SnapshotRefDto
      Summary: ComparisonSummaryDto
      MetricChanges: MetricChangeDto list
      FindingChanges: FindingChangeDto list }

type SuppressionDto =
    { FindingId: string
      Reason: string
      Scope: string
      Actor: string option
      Created: DateTimeOffset
      Expires: DateTimeOffset option
      Status: string
      Application: string }

type OutcomeDto =
    { Rule: string
      Subject: string
      Scope: string
      State: string
      Actual: decimal option
      Limit: decimal option
      Baseline: decimal option
      Reason: string option
      Suppression: SuppressionDto option
      Explanation: string }

type PolicyRefDto =
    { SchemaVersion: string
      Identity: string
      Baseline: string }

type EvaluationDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      EvaluatedAt: DateTimeOffset
      Disposition: string
      ExitCode: int
      Reason: string option
      Baseline: SnapshotRefDto
      Current: SnapshotRefDto
      Policy: PolicyRefDto
      Summary: ComparisonSummaryDto
      OutcomeCounts: Map<string, int>
      Outcomes: OutcomeDto list }

type DiagnosticDto =
    { Contract: string
      SchemaVersion: string
      State: string
      Code: string
      Message: string }

module Contracts =
    [<Literal>]
    let SnapshotContract = "dokimos.snapshot"

    [<Literal>]
    let ComparisonContract = "dokimos.comparison"

    [<Literal>]
    let ComparisonSchemaVersion = "1.0.0"

    [<Literal>]
    let EvaluationContract = "dokimos.evaluation"

    [<Literal>]
    let EvaluationSchemaVersion = "1.0.0"

    [<Literal>]
    let DiagnosticContract = "dokimos.diagnostic"

    let supportedSnapshotSchemas = [ "1.0.0"; CanonicalSnapshot.CurrentSchemaVersion ]

    let supportedPolicySchemas = [ "1.0.0"; "1.1.0"; "1.2.0" ]

    // Relaxed escaping keeps '+' and non-ASCII text readable. The output is a
    // data contract, never embedded in HTML.
    let private indented =
        JsonSerializerOptions(WriteIndented = true, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let private compact =
        JsonSerializerOptions(WriteIndented = false, Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    let serialize (value: 'a) = JsonSerializer.Serialize(value, indented)

    let sha256 (text: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString |> _.ToLowerInvariant()

    // --- measurement tags ---------------------------------------------------

    let private unavailabilityTag =
        function
        | Unsupported -> "unsupported", None
        | NotConfigured -> "not-configured", None
        | InsufficientEvidence detail -> "insufficient-evidence", Some detail

    let measurement (m: Measurement) : MeasurementDto =
        match m with
        | Available (value, _) -> { State = "available"; Value = Some value; Reason = None; Detail = None }
        | Unavailable reason ->
            let tag, detail = unavailabilityTag reason
            { State = "unavailable"; Value = None; Reason = Some tag; Detail = detail }
        | Failed failure -> { State = "failed"; Value = None; Reason = Some failure.Code; Detail = Some failure.Message }

    let private decodeMeasurement unit (state: string) (value: decimal option) (reason: string option) (detail: string option) =
        match state, value, reason with
        | "available", Some v, _ -> Ok(Available(v, unit))
        | "available", None, _ -> Error "available metrics require a numeric Value"
        | "unavailable", None, Some "unsupported" -> Ok(Unavailable Unsupported)
        | "unavailable", None, Some "not-configured" -> Ok(Unavailable NotConfigured)
        | "unavailable", None, Some "insufficient-evidence" -> Ok(Unavailable(InsufficientEvidence(defaultArg detail "")))
        | "unavailable", None, None -> Ok(Unavailable(InsufficientEvidence "no reason recorded"))
        | "unavailable", None, Some other -> Error $"unknown unavailability reason '{other}'"
        | "failed", None, Some code -> Ok(Failed { Code = code; Message = defaultArg detail "" })
        | "failed", None, None -> Error "failed metrics require a Reason code"
        | ("unavailable" | "failed"), Some _, _ -> Error $"{state} metrics must not carry a Value"
        | other, _, _ -> Error $"unknown metric state '{other}'"

    // --- signals ------------------------------------------------------------

    let tryParseSignal (dto: SignalDto) =
        match dto.Kind, dto.Value with
        | "structural-complexity", Some v -> Ok(StructuralComplexity v)
        | "frequent-change", Some v -> Ok(FrequentChange v)
        | "high-churn", Some v -> Ok(HighChurn v)
        | "repeated-region", Some v -> Ok(RepeatedRegion v)
        | "active-findings", Some v -> Ok(ActiveFindings v)
        | "aged-debt", Some v -> Ok(AgedDebt v)
        | "api-instability", Some v -> Ok(ApiInstability v)
        | "dependency-additions", Some v -> Ok(DependencyAdditions v)
        | "missing-test-change", None -> Ok MissingTestChange
        | "type-weakening", Some v -> Ok(TypeWeakening v)
        | kind, _ -> Error $"unknown or malformed evidence signal '{kind}'"

    // --- snapshot encode ----------------------------------------------------

    let private metricDto (m: CanonicalMetric) : MetricDto =
        let dto = measurement m.Measurement
        { MetricId = m.MetricId
          MetricVersion = m.MetricVersion
          Scope = m.Scope
          State = dto.State
          Value = dto.Value
          Unit = m.Unit
          Reason = dto.Reason
          Detail = dto.Detail
          Source = m.Source }

    let private findingDto (f: CanonicalFinding) : FindingDto =
        { FindingId = f.FindingId
          Scope = f.Scope
          Kind = f.Kind
          State = "present"
          Evidence = f.Evidence |> List.map Wire.signal
          Explanation = f.Explanation }

    let private analyzerDto (run: AnalyzerRun) : AnalyzerRunDto =
        let state, reason =
            match run.State with
            | AnalyzerRan -> "ran", None
            | AnalyzerNotRun reason -> "not-run", Some reason
            | AnalyzerFailed failure -> "failed", Some(failure.Code + ": " + failure.Message)
        { AnalyzerId = run.AnalyzerId; Version = run.Version; State = state; Reason = reason; DurationMilliseconds = run.DurationMilliseconds }

    let private producerDto (p: SnapshotProducer) : ProducerDto =
        { DokimosVersion = p.DokimosVersion
          ConfigurationId = p.ConfigurationId
          AnalyzedScope = p.AnalyzedScope
          Analyzers = p.Analyzers |> List.map analyzerDto }

    let private performanceDto (p: CollectionPerformance) : PerformanceDto =
        { TotalMilliseconds = p.TotalMilliseconds
          FileCount = p.FileCount
          ObservationCount = p.ObservationCount
          UnavailableCollectors = p.UnavailableCollectors
          FailedCollectors = p.FailedCollectors }

    let snapshotDto (s: CanonicalSnapshot) : SnapshotDto =
        { Contract = SnapshotContract
          SchemaVersion = s.SchemaVersion
          SnapshotId = s.SnapshotId
          Repository = s.Repository
          Revision = s.Revision
          Ref = s.Ref
          CollectedAt = s.CollectedAt
          Collector = s.Collector
          Producer = s.Producer |> Option.map producerDto
          Performance = s.Performance |> Option.map performanceDto
          Metrics = s.Metrics |> List.map metricDto
          Findings = s.Findings |> List.map findingDto }

    /// Digest of the evidence content: everything except collection time,
    /// durations and the identity derived from this digest.
    let evidenceDigest (s: CanonicalSnapshot) =
        let producer =
            s.Producer
            |> Option.map (fun p -> { producerDto p with Analyzers = p.Analyzers |> List.map (fun a -> { analyzerDto a with DurationMilliseconds = None }) })
        let content =
            {| Repository = s.Repository
               Revision = s.Revision
               Ref = s.Ref
               SchemaVersion = s.SchemaVersion
               Producer = producer
               Metrics = s.Metrics |> List.map metricDto
               Findings = s.Findings |> List.map findingDto |}
        JsonSerializer.Serialize(content, compact) |> sha256

    // --- snapshot decode ----------------------------------------------------

    open Json

    let private signalDecoder: Decoder<EvidenceSignal> =
        fun e ->
            result {
                let! kind = field "Kind" string e
                let! value = optionalField "Value" int e
                return! tryParseSignal { Kind = kind; Value = value }
            }

    let private metricDecoder: Decoder<CanonicalMetric> =
        fun e ->
            result {
                let! metricId = field "MetricId" nonEmptyString e
                let! version = field "MetricVersion" int e
                let! scope = field "Scope" nonEmptyString e
                let! state = field "State" string e
                let! value = optionalField "Value" decimal e
                let! unit = field "Unit" string e
                let! reason = optionalField "Reason" string e
                let! detail = optionalField "Detail" string e
                let! source = field "Source" string e
                let! m = decodeMeasurement unit state value reason detail |> Result.mapError (fun x -> $"{metricId}@{scope}: {x}")
                return { MetricId = metricId; MetricVersion = version; Scope = scope; Unit = unit; Measurement = m; Source = source }
            }

    let private findingDecoder: Decoder<CanonicalFinding> =
        fun e ->
            result {
                let! id = field "FindingId" nonEmptyString e
                let! scope = field "Scope" string e
                let! kind = field "Kind" string e
                let! state = field "State" string e
                do! if state = "present" then Ok() else Error $"finding {id}: unknown state '{state}'"
                let! evidence = field "Evidence" (list signalDecoder) e
                let! explanation = field "Explanation" string e
                return { FindingId = id; Scope = scope; Kind = kind; Evidence = evidence; Explanation = explanation }
            }

    let private analyzerDecoder: Decoder<AnalyzerRun> =
        fun e ->
            result {
                let! id = field "AnalyzerId" nonEmptyString e
                let! version = field "Version" string e
                let! state = field "State" string e
                let! reason = optionalField "Reason" string e
                let! duration = optionalField "DurationMilliseconds" int64 e
                let! runState =
                    match state with
                    | "ran" -> Ok AnalyzerRan
                    | "not-run" -> Ok(AnalyzerNotRun(defaultArg reason ""))
                    | "failed" ->
                        let text = defaultArg reason ""
                        let code, message =
                            match text.IndexOf(": ", StringComparison.Ordinal) with
                            | -1 -> text, ""
                            | i -> text.Substring(0, i), text.Substring(i + 2)
                        Ok(AnalyzerFailed { Code = code; Message = message })
                    | other -> Error $"unknown analyzer state '{other}'"
                return { AnalyzerId = id; Version = version; State = runState; DurationMilliseconds = duration }
            }

    let private producerDecoder: Decoder<SnapshotProducer> =
        fun e ->
            result {
                let! version = field "DokimosVersion" nonEmptyString e
                let! config = field "ConfigurationId" string e
                let! scope = field "AnalyzedScope" (list string) e
                let! analyzers = field "Analyzers" (list analyzerDecoder) e
                return { DokimosVersion = version; ConfigurationId = config; AnalyzedScope = scope; Analyzers = analyzers }
            }

    let private performanceDecoder: Decoder<CollectionPerformance> =
        fun e ->
            result {
                let! total = field "TotalMilliseconds" int64 e
                let! files = field "FileCount" int e
                let! observations = field "ObservationCount" int e
                let! unavailable = field "UnavailableCollectors" (list string) e
                let! failed = field "FailedCollectors" (list string) e
                return { TotalMilliseconds = total; FileCount = files; ObservationCount = observations; UnavailableCollectors = unavailable; FailedCollectors = failed }
            }

    /// Decodes a snapshot of a supported schema version. Schema 1.0.0 predates
    /// producer provenance and performance; those stay explicitly absent.
    let snapshotDecoder: Decoder<CanonicalSnapshot> =
        fun e ->
            result {
                let! schema = field "SchemaVersion" string e
                do!
                    if List.contains schema supportedSnapshotSchemas then Ok()
                    else Error("unsupported-snapshot-schema:" + schema)
                let! contract = optionalField "Contract" string e
                do!
                    match schema, contract with
                    | "1.0.0", (None | Some SnapshotContract) -> Ok()
                    | _, Some SnapshotContract -> Ok()
                    | _, other -> Error $"expected Contract '{SnapshotContract}', found {other}"
                let! id = field "SnapshotId" nonEmptyString e
                let! repository = field "Repository" nonEmptyString e
                let! revision = field "Revision" nonEmptyString e
                let! refName = field "Ref" string e
                let! collectedAt = field "CollectedAt" dateTimeOffset e
                let! collector = field "Collector" string e
                let! producer = if schema = "1.0.0" then Ok None else field "Producer" producerDecoder e |> Result.map Some
                let! performance = optionalField "Performance" performanceDecoder e
                let! metrics = field "Metrics" (list metricDecoder) e
                let! findings = field "Findings" (list findingDecoder) e
                return
                    { SchemaVersion = schema
                      SnapshotId = id
                      Repository = repository
                      Revision = revision
                      Ref = refName
                      CollectedAt = collectedAt
                      Collector = collector
                      Producer = producer
                      Performance = performance
                      Metrics = metrics
                      Findings = findings }
            }

    /// Stable, machine-readable snapshot read errors used at every boundary.
    let readSnapshot (text: string) =
        match parse snapshotDecoder text with
        | Ok snapshot -> Ok snapshot
        | Error NullDocument -> Error "snapshot-deserialized-to-null"
        | Error (Malformed _) -> Error "malformed-snapshot-json"
        | Error (Invalid message) when message.StartsWith("unsupported-snapshot-schema:", StringComparison.Ordinal) -> Error message
        | Error (Invalid message) -> Error("invalid-snapshot: " + message)

    // --- comparison encode --------------------------------------------------

    let snapshotRef (s: CanonicalSnapshot) : SnapshotRefDto =
        { SnapshotId = s.SnapshotId
          SchemaVersion = s.SchemaVersion
          Repository = s.Repository
          Revision = s.Revision
          Ref = s.Ref
          CollectedAt = s.CollectedAt
          DokimosVersion = s.Producer |> Option.map _.DokimosVersion }

    let metricChangeTag =
        function
        | MetricAdded -> "added"
        | MetricRemoved -> "removed"
        | MetricImproved -> "improved"
        | MetricDeteriorated -> "deteriorated"
        | MetricUnchanged -> "unchanged"
        | MetricChanged -> "changed"
        | MetricIncompatible -> "incompatible"
        | MetricUnavailable -> "unavailable"

    let findingChangeTag =
        function
        | FindingIntroduced -> "introduced"
        | FindingResolved -> "resolved"
        | FindingPersistent -> "persistent"
        | FindingUnavailable -> "unavailable"

    let summaryDto (s: ComparisonSummary) : ComparisonSummaryDto =
        { Improved = s.Improved
          Deteriorated = s.Deteriorated
          Unchanged = s.Unchanged
          Changed = s.Changed
          Added = s.Added
          Removed = s.Removed
          Incompatible = s.Incompatible
          Unavailable = s.Unavailable
          FindingsIntroduced = s.FindingsIntroduced
          FindingsResolved = s.FindingsResolved
          FindingsPersistent = s.FindingsPersistent
          FindingsUnavailable = s.FindingsUnavailable }

    let comparisonDto (before: CanonicalSnapshot) (after: CanonicalSnapshot) (c: CanonicalComparison) : ComparisonDto =
        { Contract = ComparisonContract
          SchemaVersion = ComparisonSchemaVersion
          DokimosVersion = DokimosInfo.version
          Before = snapshotRef before
          After = snapshotRef after
          Summary = Evaluation.summarize c |> summaryDto
          MetricChanges =
            c.MetricChanges
            |> List.map (fun m ->
                { MetricId = m.MetricId
                  Scope = m.Scope
                  Unit = m.Unit
                  Kind = metricChangeTag m.Kind
                  BeforeVersion = m.BeforeVersion
                  AfterVersion = m.AfterVersion
                  Before = m.Before |> Option.map measurement
                  After = m.After |> Option.map measurement
                  Delta = m.Delta })
          FindingChanges =
            c.FindingChanges
            |> List.map (fun f ->
                { FindingId = f.FindingId
                  Scope = f.Scope
                  FindingKind = f.FindingKind
                  Kind = findingChangeTag f.Kind
                  Evidence = f.Evidence |> List.map Wire.signal }) }

    // --- evaluation encode --------------------------------------------------

    let gateTag =
        function
        | Pass -> "passed"
        | Warning _ -> "warning"
        | Failure _ -> "failed"
        | ObservedOnly _ -> "observe-only"
        | NotEvaluated _ -> "not-evaluated"
        | EvidenceUnavailable _ -> "unavailable"
        | Incompatible _ -> "incompatible"
        | CollectionFailed _ -> "collection-failed"

    /// Every outcome state, in a stable order, for counts and schemas.
    let gateTags = [ "passed"; "warning"; "failed"; "observe-only"; "not-evaluated"; "unavailable"; "incompatible"; "collection-failed" ]

    let ruleTag =
        function
        | RatchetRuleKind -> "ratchet"
        | ThresholdRuleKind -> "threshold"
        | RegressionRuleKind -> "regression"
        | IntroducedFindingRuleKind -> "introduced-finding"
        | RequiredEvidenceRuleKind -> "required-evidence"

    let dispositionTag =
        function
        | GatePassed -> "passed"
        | GatePassedWithWarnings -> "passed-with-warnings"
        | GateFailed -> "failed"
        | GateEvidenceUnavailable -> "required-evidence-unavailable"
        | GateIncompatibleSnapshots _ -> "incompatible-snapshots"

    let private suppressionDto (applied: SuppressionApplication) : SuppressionDto =
        let s, application =
            match applied with
            | Suppressed s -> s, "applied"
            | SuppressionExpired s -> s, "expired"
        { FindingId = s.FindingId
          Reason = s.Reason
          Scope = s.Scope
          Actor = s.Actor
          Created = s.Created
          Expires = s.Expires
          Status = (match s.Status with SuppressionActive -> "active" | SuppressionRevoked -> "revoked")
          Application = application }

    let private outcomeDto (o: RuleOutcome) : OutcomeDto =
        let actual, limit, reason =
            match o.Result with
            | Warning (a, l) | Failure (a, l) | ObservedOnly (a, l) -> Some a, Some l, None
            | Pass
            | Incompatible _ -> None, None, None
            | NotEvaluated reason -> None, None, Some reason
            | EvidenceUnavailable r -> None, None, Some(fst (unavailabilityTag r))
            | CollectionFailed f -> None, None, Some f.Code
        { Rule = ruleTag o.Rule
          Subject = o.Subject
          Scope = o.Scope
          State = gateTag o.Result
          Actual = actual
          Limit = limit
          Baseline = o.Baseline
          Reason = reason
          Suppression = o.Suppression |> Option.map suppressionDto
          Explanation = o.Explanation }

    let evaluationDto (e: PolicyEvaluation) : EvaluationDto =
        let counts =
            gateTags
            |> List.map (fun tag -> tag, e.Outcomes |> List.filter (fun o -> gateTag o.Result = tag) |> List.length)
            |> Map.ofList
        { Contract = EvaluationContract
          SchemaVersion = EvaluationSchemaVersion
          DokimosVersion = DokimosInfo.version
          EvaluatedAt = e.EvaluatedAt
          Disposition = dispositionTag e.Disposition
          ExitCode = Evaluation.exitCode e.Disposition
          Reason = (match e.Disposition with GateIncompatibleSnapshots reason -> Some reason | _ -> None)
          Baseline = snapshotRef e.Baseline
          Current = snapshotRef e.Current
          Policy = { SchemaVersion = e.Policy.SchemaVersion; Identity = e.Policy.Identity; Baseline = e.Policy.Baseline }
          Summary = summaryDto e.Summary
          OutcomeCounts = counts
          Outcomes = e.Outcomes |> List.map outcomeDto }

    let diagnostic code message : DiagnosticDto =
        { Contract = DiagnosticContract; SchemaVersion = "1.0.0"; State = "error"; Code = code; Message = message }

    // --- policy decode ------------------------------------------------------

    let private dispositionRule = field "disposition" PolicyDecoders.disposition

    let policyDecoder identity : Decoder<QualityPolicy> =
        fun e ->
            result {
                let! schema = field "schemaVersion" string e
                do!
                    if List.contains schema supportedPolicySchemas then Ok()
                    else Error("unsupported-policy-schema:" + schema)
                let! baseline = field "baseline" nonEmptyString e
                let! ratchets = fieldOr "ratchets" [] (list (PolicyDecoders.ratchet (schema = "1.2.0"))) e
                let! observed = fieldOr "observedNotRatcheted" [] (list string) e
                let! unavailable = fieldOr "unavailableBehavior" "not-evaluated" string e
                do! if unavailable = "not-evaluated" then Ok() else Error $"unsupported unavailableBehavior '{unavailable}'"
                let v11 = schema = "1.1.0" || schema = "1.2.0"
                let! thresholds = if v11 then fieldOr "thresholds" [] (list PolicyDecoders.threshold) e else Ok []
                let! regressions = if v11 then optionalField "regressions" dispositionRule e else Ok None
                let! findings = if v11 then optionalField "introducedFindings" dispositionRule e else Ok None
                let! required = if v11 then fieldOr "requiredEvidence" [] (list nonEmptyString) e else Ok []
                do! PolicyDecoders.noSuppressionsFrom120 schema e
                let! suppressions = if schema = "1.1.0" then fieldOr "suppressions" [] (list PolicyDecoders.suppression) e else Ok []
                return
                    { SchemaVersion = schema
                      Identity = identity
                      Baseline = baseline
                      Ratchets = ratchets
                      Thresholds = thresholds
                      Regressions = regressions
                      IntroducedFindings = findings
                      RequiredEvidence = required
                      ObservedNotRatcheted = observed
                      Suppressions = suppressions }
            }

    let readPolicy (text: string) =
        match parse (policyDecoder ("sha256:" + sha256 text)) text with
        | Ok policy -> Ok policy
        | Error NullDocument -> Error "policy-deserialized-to-null"
        | Error (Malformed _) -> Error "malformed-policy-json"
        | Error (Invalid message) -> Error("invalid-policy: " + message)
