namespace Dokimos.Core

open System
open Dokimos.Domain

/// The application-facing results contract (dokimos.results 1.0.0). It is a
/// projection of existing evidence and judgments: every value here is taken
/// from a snapshot, comparison, evaluation or history result. Presentation
/// layers (Forma UI, Folio report) consume it and never recompute quality.

type HotspotSignalDto =
    { Kind: string
      Dimension: string
      Value: int option }

type HotspotDto =
    { FindingId: string
      Scope: string
      Kind: string
      Explanation: string
      Dimensions: string list
      Signals: HotspotSignalDto list
      Lifecycle: string }

type ChangeDto =
    { MetricId: string
      Scope: string
      Kind: string
      Before: MeasurementDto option
      After: MeasurementDto option
      Delta: decimal option }

type UnavailableDto =
    { Subject: string
      Scope: string
      State: string
      Reason: string option
      Explanation: string }

type ProvenanceDto =
    { Repository: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      DokimosVersion: string option
      ConfigurationId: string option
      AnalyzedScope: string list
      Analyzers: AnalyzerRunDto list
      Policy: PolicyRefDto
      Performance: PerformanceDto option }

type OverviewDto =
    { Disposition: string
      ExitCode: int
      Summary: ComparisonSummaryDto
      OutcomeCounts: Map<string, int>
      MetricsAvailable: int
      MetricsUnavailable: int
      MetricsFailed: int
      Findings: int
      Hotspots: int
      StoredSnapshots: int }

type ResultsDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Overview: OverviewDto
      Current: SnapshotRefDto
      Baseline: SnapshotRefDto
      Outcomes: OutcomeDto list
      Improvements: ChangeDto list
      Regressions: ChangeDto list
      Findings: FindingHistoryDto list
      Hotspots: HotspotDto list
      Trends: SeriesDto list
      Provenance: ProvenanceDto
      Unavailable: UnavailableDto list }

module Results =
    [<Literal>]
    let Contract = "dokimos.results"

    [<Literal>]
    let SchemaVersion = "1.0.0"

    /// Which independent evidence dimension a signal belongs to.
    let dimension =
        function
        | StructuralComplexity _
        | TypeWeakening _ -> "structural"
        | FrequentChange _
        | HighChurn _
        | RepeatedRegion _ -> "temporal"
        | ActiveFindings _
        | AgedDebt _ -> "debt"
        | ApiInstability _ -> "api"
        | DependencyAdditions _ -> "dependency"
        | MissingTestChange -> "test"

    let private change (c: MetricChangeDto) : ChangeDto =
        { MetricId = c.MetricId; Scope = c.Scope; Kind = c.Kind; Before = c.Before; After = c.After; Delta = c.Delta }

    /// Trend series shown by default: repository-scope metrics plus the
    /// structural and temporal metrics of every hotspot file.
    let private trendKeys (current: CanonicalSnapshot) (hotspotScopes: string list) =
        current.Metrics
        |> List.filter (fun m ->
            m.Scope = CanonicalSnapshot.RepositoryScope
            || (List.contains m.Scope hotspotScopes
                && List.contains m.MetricId [ "complexity.proxy-cyclomatic"; "change.file-churn"; "change.file-commits"; "source.nonblank-lines" ]))
        |> List.map (fun m -> m.MetricId, m.Scope)
        |> List.distinct

    let build (evaluation: PolicyEvaluation) (history: CanonicalSnapshot list) =
        let evaluationDto = Contracts.evaluationDto evaluation
        let comparisonDto = Contracts.comparisonDto evaluation.Baseline evaluation.Current evaluation.Comparison
        let current = evaluation.Current
        let ordered =
            (if history |> List.exists (fun s -> s.SnapshotId = current.SnapshotId) then history else history @ [ current ])
            |> History.ordered (Some current.Repository)
        let lifecycles = History.findingHistories ordered |> List.map (fun f -> f.FindingId, f) |> Map.ofList
        let lifecycleOf id =
            lifecycles |> Map.tryFind id |> Option.map (fun f -> fst (HistoryContract.lifecycleTag f.Current)) |> Option.defaultValue "introduced"
        let hotspots =
            current.Findings
            |> List.map (fun f ->
                let signals =
                    f.Evidence
                    |> List.map (fun s ->
                        let wire = Wire.signal s
                        { Kind = wire.Kind; Dimension = dimension s; Value = wire.Value })
                { FindingId = f.FindingId
                  Scope = f.Scope
                  Kind = f.Kind
                  Explanation = f.Explanation
                  Dimensions = signals |> List.map _.Dimension |> List.distinct
                  Signals = signals
                  Lifecycle = lifecycleOf f.FindingId })
        let count predicate = current.Metrics |> List.filter (fun m -> predicate m.Measurement) |> List.length
        let unavailable =
            [ for m in current.Metrics do
                  match m.Measurement with
                  | Unavailable _
                  | Failed _ ->
                      let dto = Contracts.measurement m.Measurement
                      let why =
                          match m.Measurement with
                          | Unavailable Unsupported -> $"No analyzer in this Dokimos release collects {m.MetricId}."
                          | Unavailable NotConfigured -> $"{m.MetricId} was not collected because its input was not supplied."
                          | Unavailable (InsufficientEvidence detail) -> $"{m.MetricId} could not be determined: {detail}."
                          | Failed failure -> $"Collecting {m.MetricId} failed ({failure.Code}): {failure.Message}"
                          | Available _ -> ""
                      { Subject = m.MetricId; Scope = m.Scope; State = dto.State; Reason = dto.Reason; Explanation = why }
                  | Available _ -> () ]
        let trends =
            trendKeys current (hotspots |> List.map _.Scope)
            |> List.choose (fun (id, scope) -> History.metricSeries (Some evaluation.Baseline) id scope ordered)
            |> List.map HistoryContract.seriesDto
        { Contract = Contract
          SchemaVersion = SchemaVersion
          DokimosVersion = DokimosInfo.version
          Overview =
            { Disposition = evaluationDto.Disposition
              ExitCode = evaluationDto.ExitCode
              Summary = evaluationDto.Summary
              OutcomeCounts = evaluationDto.OutcomeCounts
              MetricsAvailable = count (function Available _ -> true | _ -> false)
              MetricsUnavailable = count (function Unavailable _ -> true | _ -> false)
              MetricsFailed = count (function Failed _ -> true | _ -> false)
              Findings = current.Findings.Length
              Hotspots = hotspots |> List.filter (fun h -> h.Dimensions.Length >= 2) |> List.length
              StoredSnapshots = ordered.Length }
          Current = evaluationDto.Current
          Baseline = evaluationDto.Baseline
          Outcomes = evaluationDto.Outcomes
          Improvements = comparisonDto.MetricChanges |> List.filter (fun c -> c.Kind = "improved") |> List.map change
          Regressions = comparisonDto.MetricChanges |> List.filter (fun c -> c.Kind = "deteriorated") |> List.map change
          Findings = lifecycles |> Map.toList |> List.map (snd >> HistoryContract.findingDto)
          Hotspots = hotspots
          Trends = trends
          Provenance =
            { Repository = current.Repository
              Revision = current.Revision
              Ref = current.Ref
              CollectedAt = current.CollectedAt
              DokimosVersion = current.Producer |> Option.map _.DokimosVersion
              ConfigurationId = current.Producer |> Option.map _.ConfigurationId
              AnalyzedScope = current.Producer |> Option.map _.AnalyzedScope |> Option.defaultValue []
              Analyzers = (Contracts.snapshotDto current).Producer |> Option.map _.Analyzers |> Option.defaultValue []
              Policy = evaluationDto.Policy
              Performance = (Contracts.snapshotDto current).Performance }
          Unavailable = unavailable }
