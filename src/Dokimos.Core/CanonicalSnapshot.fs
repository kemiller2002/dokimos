namespace Dokimos.Core

open System
open Dokimos.Domain

/// One observation in a canonical snapshot. Scope is a stable location key
/// ("repository" or a repository-relative file path). The measurement keeps
/// available, unavailable and failed evidence distinct.
type CanonicalMetric =
    { MetricId: string
      MetricVersion: int
      Scope: string
      Unit: string
      Measurement: Measurement
      Source: string }

/// A finding present in a snapshot. Identity is the stable FindingId; the
/// contributing signals keep the finding decomposable.
type CanonicalFinding =
    { FindingId: string
      Scope: string
      Kind: string
      Evidence: EvidenceSignal list
      Explanation: string }

type AnalyzerRunState =
    | AnalyzerRan
    | AnalyzerNotRun of reason: string
    | AnalyzerFailed of Failure

type AnalyzerRun =
    { AnalyzerId: string
      Version: string
      State: AnalyzerRunState
      DurationMilliseconds: int64 option }

/// Who and what produced the evidence. Absent only on schema 1.0.0 evidence,
/// which predates producer provenance; it is never back-filled.
type SnapshotProducer =
    { DokimosVersion: string
      ConfigurationId: string
      AnalyzedScope: string list
      Analyzers: AnalyzerRun list }

type CollectionPerformance =
    { TotalMilliseconds: int64
      FileCount: int
      ObservationCount: int
      UnavailableCollectors: string list
      FailedCollectors: string list }

type CanonicalSnapshot =
    { SchemaVersion: string
      SnapshotId: string
      Repository: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      Collector: string
      Producer: SnapshotProducer option
      Performance: CollectionPerformance option
      Metrics: CanonicalMetric list
      Findings: CanonicalFinding list }

module CanonicalMetric =
    let available metricId scope unit (value: decimal) source =
        { MetricId = metricId; MetricVersion = 1; Scope = scope; Unit = unit; Measurement = Available(value, unit); Source = source }

    let unavailable metricId scope unit reason source =
        { MetricId = metricId; MetricVersion = 1; Scope = scope; Unit = unit; Measurement = Unavailable reason; Source = source }

    let failed metricId scope unit failure source =
        { MetricId = metricId; MetricVersion = 1; Scope = scope; Unit = unit; Measurement = Failed failure; Source = source }

    let value metric =
        match metric.Measurement with
        | Available (value, _) -> Some value
        | Unavailable _
        | Failed _ -> None

module CanonicalSnapshot =
    [<Literal>]
    let CurrentSchemaVersion = "2.0.0"

    [<Literal>]
    let RepositoryScope = "repository"

    let analyzerRan snapshot analyzerId =
        match snapshot.Producer with
        | None -> None
        | Some producer ->
            producer.Analyzers
            |> List.tryFind (fun run -> run.AnalyzerId = analyzerId)
            |> Option.map (fun run -> run.State = AnalyzerRan)

    /// Structural + temporal source metrics for every analyzed file. Temporal
    /// metrics are unavailable (not zero) for files without history evidence
    /// when history was not supplied at all.
    let sourceMetrics (temporalAvailable: bool) (temporal: Map<string, TemporalEvidence>) (analysis: RepositoryAnalysis) =
        analysis.Sources
        |> List.collect (fun source ->
            let m id unit (value: int) analyzer = CanonicalMetric.available id source.Path unit (decimal value) analyzer
            let temporalMetrics =
                match temporalAvailable, Map.tryFind source.Path temporal with
                | true, Some evidence ->
                    [ m "change.file-commits" "count" evidence.CommitCount Capabilities.GitTemporal
                      m "change.file-churn" "lines" evidence.Churn Capabilities.GitTemporal ]
                | true, None ->
                    // History was read and contains no change to this path.
                    [ m "change.file-commits" "count" 0 Capabilities.GitTemporal
                      m "change.file-churn" "lines" 0 Capabilities.GitTemporal ]
                | false, _ ->
                    let reason = InsufficientEvidence "git history not supplied"
                    [ CanonicalMetric.unavailable "change.file-commits" source.Path "count" reason Capabilities.GitTemporal
                      CanonicalMetric.unavailable "change.file-churn" source.Path "lines" reason Capabilities.GitTemporal ]
            [ m "source.lines" "lines" source.Structural.Lines Capabilities.Structural
              m "source.nonblank-lines" "lines" source.Structural.NonBlankLines Capabilities.Structural
              m "source.public-declarations" "count" source.Structural.PublicDeclarations Capabilities.Structural
              m "complexity.proxy-cyclomatic" "points" source.Complexity.ProxyCyclomatic Capabilities.Complexity
              m "source.mutable-bindings" "count" source.Structural.MutableBindings Capabilities.Structural
              m "source.broad-catch-indicators" "count" source.Structural.BroadCatchIndicators Capabilities.Structural
              m "quality.type-weakening-indicators" "count" source.Agent.TypeWeakeningIndicators Capabilities.AgentQuality
              m "quality.scaffolding-indicators" "count" source.Agent.UnresolvedScaffoldingIndicators Capabilities.AgentQuality ]
            @ temporalMetrics)

    /// Catalogued metrics no analyzer in this release collects, stated
    /// explicitly so their absence is never read as zero.
    let unsupportedMetrics () =
        Capabilities.unsupportedMetrics
        |> List.map (fun d -> CanonicalMetric.unavailable d.MetricId RepositoryScope d.Unit Unsupported "dokimos")

    let findings (analysis: RepositoryAnalysis) =
        analysis.Sources
        |> List.collect _.Correlations
        |> List.map (fun finding ->
            { FindingId = "correlation:" + Wire.correlationKind finding.Kind + ":" + finding.Path
              Scope = finding.Path
              Kind = Wire.correlationKind finding.Kind
              Evidence = finding.Signals
              Explanation = finding.Explanation })

    let duplicationMetric (analysis: RepositoryAnalysis) =
        CanonicalMetric.available "duplication.block-occurrences" RepositoryScope "count"
            (analysis.DuplicateBlocks |> List.sumBy _.Occurrences |> decimal) Capabilities.Duplication

    /// Snapshot identity: one snapshot per distinct evidence content for a
    /// revision. Identical re-analysis yields the same identity; different
    /// evidence for the same revision (e.g. a newer analyzer) yields a new one.
    let identity repository revision (digest: string) =
        repository + "@" + revision + ":" + digest.Substring(0, 16)
