namespace Dokimos.Core

open System
open System.Text.Json.Nodes

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
      Findings: CanonicalFinding list
      /// Schema 1.1.0 (R0.11, DF-DOK-001): the Praxis provenance interchange
      /// record of the measurement, carried as raw JSON so fields Dokimos does
      /// not model survive. `None` for 1.0.0 snapshots: absence is never
      /// replaced by invented history.
      Provenance: JsonObject option }

module CanonicalSnapshot =
    /// The schema version Dokimos writes. 1.1.0 adds the optional
    /// `Provenance` member; every 1.x snapshot remains readable.
    [<Literal>]
    let SchemaVersion = "1.1.0"

    /// Readers accept any minor/patch of major 1 (1.0.0 has no provenance).
    let isSupportedSchema (version: string) =
        not (String.IsNullOrEmpty version) && Text.RegularExpressions.Regex.IsMatch(version, "^1\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")

    /// The provenance subject reference of a snapshot.
    let subject (snapshotId: string) = "dokimos:snapshot/" + snapshotId

    let private metric id scope value unit source =
        { MetricId=id; MetricVersion=1; Scope=scope; State="available"; Value=Some(decimal value); Unit=unit; Source=source }

    /// Metric source "agent-quality" names a heuristic family, not an author
    /// (R0.18, DOK-PROV-001).
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
        { SchemaVersion=SchemaVersion
          SnapshotId=repository + ":" + revision
          Repository=repository
          Revision=revision
          Ref=refName
          CollectedAt=collectedAt
          Collector="dokimos-cli/1"
          Metrics=sourceMetrics
          Findings=findings
          Provenance=None }
