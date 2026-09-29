namespace Dokimos.Core

open Dokimos.Domain

type MetricChangeKind =
    | MetricAdded
    | MetricRemoved
    | MetricImproved
    | MetricDeteriorated
    | MetricUnchanged
    | MetricChanged
    | MetricIncompatible
    | MetricUnavailable

/// A metric compared across two snapshots. Before/After are None when the
/// metric is absent from that snapshot; otherwise they carry the measurement,
/// so unavailable or failed evidence is never shown as a number.
type CanonicalMetricChange =
    { MetricId: string
      Scope: string
      Unit: string
      BeforeVersion: int option
      AfterVersion: int option
      Before: Measurement option
      After: Measurement option
      Delta: decimal option
      Kind: MetricChangeKind }

type FindingChangeKind =
    | FindingIntroduced
    | FindingResolved
    | FindingPersistent
    | FindingUnavailable

type CanonicalFindingChange =
    { FindingId: string
      Scope: string
      FindingKind: string
      Kind: FindingChangeKind
      Evidence: EvidenceSignal list }

type CanonicalComparison =
    { BeforeSnapshotId: string
      AfterSnapshotId: string
      BeforeRevision: string
      AfterRevision: string
      MetricChanges: CanonicalMetricChange list
      FindingChanges: CanonicalFindingChange list }

module CanonicalComparison =
    let private key (m: CanonicalMetric) = m.MetricId, m.Scope

    let private direction metricId version =
        Capabilities.tryDefinition metricId
        |> Option.filter (fun d -> d.Version = version)
        |> Option.map _.Preference
        |> Option.defaultValue Contextual

    let private classify (previous: CanonicalMetric) (current: CanonicalMetric) =
        if previous.MetricVersion <> current.MetricVersion || previous.Unit <> current.Unit then
            MetricIncompatible, None
        else
            match previous.Measurement, current.Measurement with
            | Available (x, _), Available (y, _) ->
                let kind =
                    match y - x, direction current.MetricId current.MetricVersion with
                    | 0m, _ -> MetricUnchanged
                    | _, Contextual -> MetricChanged
                    | delta, PreferLower -> if delta < 0m then MetricImproved else MetricDeteriorated
                    | delta, PreferHigher -> if delta > 0m then MetricImproved else MetricDeteriorated
                kind, Some(y - x)
            | _ -> MetricUnavailable, None

    let private change (previous: CanonicalMetric option) (current: CanonicalMetric option) =
        let exemplar = Option.orElse previous current |> Option.get
        let kind, delta =
            match previous, current with
            | Some p, Some c -> classify p c
            | None, Some _ -> MetricAdded, None
            | _ -> MetricRemoved, None
        { MetricId = exemplar.MetricId
          Scope = exemplar.Scope
          Unit = exemplar.Unit
          BeforeVersion = previous |> Option.map _.MetricVersion
          AfterVersion = current |> Option.map _.MetricVersion
          Before = previous |> Option.map _.Measurement
          After = current |> Option.map _.Measurement
          Delta = delta
          Kind = kind }

    /// A finding's absence is only a resolution when the analyzers that would
    /// have reported it ran. Explicitly missing analyzers make it unavailable.
    let private findingEvidenceComplete snapshot =
        Capabilities.requiredForFindings
        |> List.forall (fun analyzer -> CanonicalSnapshot.analyzerRan snapshot analyzer <> Some false)

    let compare (before: CanonicalSnapshot) (after: CanonicalSnapshot) =
        let b = before.Metrics |> List.map (fun x -> key x, x) |> Map.ofList
        let a = after.Metrics |> List.map (fun x -> key x, x) |> Map.ofList
        let metricChanges =
            Set.union (b |> Map.keys |> Set.ofSeq) (a |> Map.keys |> Set.ofSeq)
            |> Set.toList
            |> List.map (fun k -> change (Map.tryFind k b) (Map.tryFind k a))

        let beforeFindings = before.Findings |> List.map (fun f -> f.FindingId, f) |> Map.ofList
        let afterFindings = after.Findings |> List.map (fun f -> f.FindingId, f) |> Map.ofList
        let beforeComplete = findingEvidenceComplete before
        let afterComplete = findingEvidenceComplete after
        let findingChanges =
            Set.union (beforeFindings |> Map.keys |> Set.ofSeq) (afterFindings |> Map.keys |> Set.ofSeq)
            |> Set.toList
            |> List.map (fun id ->
                let previous = Map.tryFind id beforeFindings
                let current = Map.tryFind id afterFindings
                let exemplar = Option.orElse previous current |> Option.get
                let kind =
                    match previous, current with
                    | Some _, Some _ -> FindingPersistent
                    | None, Some _ when beforeComplete -> FindingIntroduced
                    | Some _, None when afterComplete -> FindingResolved
                    | _ -> FindingUnavailable
                { FindingId = id
                  Scope = exemplar.Scope
                  FindingKind = exemplar.Kind
                  Kind = kind
                  Evidence = exemplar.Evidence })

        { BeforeSnapshotId = before.SnapshotId
          AfterSnapshotId = after.SnapshotId
          BeforeRevision = before.Revision
          AfterRevision = after.Revision
          MetricChanges = metricChanges
          FindingChanges = findingChanges }
