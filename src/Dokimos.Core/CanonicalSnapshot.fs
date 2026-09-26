namespace Dokimos.Core

open System

type CanonicalMetric =
    { MetricId: string
      MetricVersion: int
      Scope: string
      State: string
      Value: decimal option
      Unit: string
      Source: string }

type CanonicalFinding =
    { FindingId: string
      Scope: string
      Kind: string
      State: string
      Evidence: SignalDto list
      Explanation: string }

type CanonicalSnapshot =
    { SchemaVersion: string
      SnapshotId: string
      Repository: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      Collector: string
      Metrics: CanonicalMetric list
      Findings: CanonicalFinding list }

module CanonicalSnapshot =
    let private metric id scope value unit source =
        { MetricId=id; MetricVersion=1; Scope=scope; State="available"; Value=Some(decimal value); Unit=unit; Source=source }

    let fromAnalysis repository revision refName collectedAt (analysis: RepositoryAnalysis) =
        let sourceMetrics =
            analysis.Sources
            |> List.collect (fun source ->
                [ metric "source.lines" source.Path source.Structural.Lines "lines" "structural"
                  metric "source.nonblank-lines" source.Path source.Structural.NonBlankLines "lines" "structural"
                  metric "complexity.proxy-cyclomatic" source.Path source.Complexity.ProxyCyclomatic "points" "complexity"
                  metric "source.mutable-bindings" source.Path source.Structural.MutableBindings "count" "structural"
                  metric "source.broad-catch-indicators" source.Path source.Structural.BroadCatchIndicators "count" "structural"
                  metric "quality.type-weakening-indicators" source.Path source.Agent.TypeWeakeningIndicators "count" "agent-quality"
                  metric "quality.scaffolding-indicators" source.Path source.Agent.UnresolvedScaffoldingIndicators "count" "agent-quality" ])
        let findings =
            analysis.Sources
            |> List.collect _.Correlations
            |> List.map (fun finding ->
                { FindingId = "correlation:" + Wire.correlationKind finding.Kind + ":" + finding.Path
                  Scope=finding.Path
                  Kind=Wire.correlationKind finding.Kind
                  State="present"
                  Evidence=finding.Signals |> List.map Wire.signal
                  Explanation=finding.Explanation })
        { SchemaVersion="1.0.0"
          SnapshotId=repository + ":" + revision
          Repository=repository
          Revision=revision
          Ref=refName
          CollectedAt=collectedAt
          Collector="dokimos-cli/1"
          Metrics=sourceMetrics
          Findings=findings }
