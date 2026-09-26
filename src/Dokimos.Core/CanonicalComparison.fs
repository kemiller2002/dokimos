namespace Dokimos.Core

open System.Text.Json.Nodes

type MetricChangeKind =
    | MetricAdded
    | MetricRemoved
    | MetricImproved
    | MetricDeteriorated
    | MetricUnchanged
    | MetricChanged
    | MetricNotComparable

type CanonicalMetricChange =
    { MetricId: string
      MetricVersion: int
      Scope: string
      Before: decimal option
      After: decimal option
      Kind: MetricChangeKind }

type FindingChangeKind =
    | FindingIntroduced
    | FindingResolved
    | FindingPersistent

type CanonicalFindingChange =
    { FindingId: string
      Kind: FindingChangeKind }

type CanonicalComparison =
    { SchemaVersion: string
      BeforeSnapshotId: string
      AfterSnapshotId: string
      MetricChanges: CanonicalMetricChange list
      FindingChanges: CanonicalFindingChange list
      /// Schema 1.1.0: the comparing actor's own record, deriving from both
      /// snapshots and carrying their provenance verbatim (R0.15).
      Provenance: JsonObject option }

module CanonicalComparison =
    [<Literal>]
    let SchemaVersion = "1.1.0"

    let subject (before: CanonicalSnapshot) (after: CanonicalSnapshot) =
        "dokimos:comparison/" + before.SnapshotId + ".." + after.SnapshotId

    let private key (m: CanonicalMetric) = m.MetricId,m.MetricVersion,m.Scope
    let private directional id =
        match id with
        | "source.mutable-bindings"
        | "source.broad-catch-indicators"
        | "quality.type-weakening-indicators"
        | "quality.scaffolding-indicators" -> Some false
        | _ -> None

    let compare before after =
        let b = before.Metrics |> List.map (fun x -> key x,x) |> Map.ofList
        let a = after.Metrics |> List.map (fun x -> key x,x) |> Map.ofList
        let keys = Set.union (b |> Map.keys |> Set.ofSeq) (a |> Map.keys |> Set.ofSeq)
        let metricChanges =
            [ for k in keys do
                match Map.tryFind k b, Map.tryFind k a with
                | None,Some current ->
                    yield {MetricId=current.MetricId;MetricVersion=current.MetricVersion;Scope=current.Scope;Before=None;After=current.Value;Kind=MetricAdded}
                | Some previous,None ->
                    yield {MetricId=previous.MetricId;MetricVersion=previous.MetricVersion;Scope=previous.Scope;Before=previous.Value;After=None;Kind=MetricRemoved}
                | None,None -> ()
                | Some previous,Some current ->
                    let kind =
                        match previous.Value,current.Value,directional current.MetricId with
                        | Some x,Some y,_ when x=y -> MetricUnchanged
                        | Some x,Some y,Some lowerIsBetter ->
                            let improved = if lowerIsBetter then y>x else y<x
                            if improved then MetricImproved else MetricDeteriorated
                        | Some _,Some _,None -> MetricChanged
                        | _ -> MetricNotComparable
                    yield {MetricId=current.MetricId;MetricVersion=current.MetricVersion;Scope=current.Scope;Before=previous.Value;After=current.Value;Kind=kind} ]
        let beforeFindings = before.Findings |> List.map _.FindingId |> Set.ofList
        let afterFindings = after.Findings |> List.map _.FindingId |> Set.ofList
        let findingChanges =
            [ for id in Set.union beforeFindings afterFindings do
                yield { FindingId=id
                        Kind =
                            match Set.contains id beforeFindings,Set.contains id afterFindings with
                            | false,true -> FindingIntroduced
                            | true,false -> FindingResolved
                            | true,true -> FindingPersistent
                            | false,false -> failwith "unreachable" } ]
        { SchemaVersion=SchemaVersion;BeforeSnapshotId=before.SnapshotId;AfterSnapshotId=after.SnapshotId
          MetricChanges=metricChanges;FindingChanges=findingChanges;Provenance=None }

/// The serialized comparison. System.Text.Json cannot serialize F# union
/// cases, so change kinds are written as explicit, stable strings.
type CanonicalMetricChangeWire =
    { MetricId: string
      MetricVersion: int
      Scope: string
      Before: decimal option
      After: decimal option
      Kind: string }

type CanonicalFindingChangeWire = { FindingId: string; Kind: string }

type CanonicalComparisonWire =
    { SchemaVersion: string
      BeforeSnapshotId: string
      AfterSnapshotId: string
      MetricChanges: CanonicalMetricChangeWire list
      FindingChanges: CanonicalFindingChangeWire list
      Provenance: JsonObject option }

module CanonicalComparisonWire =
    let metricKind kind =
        match kind with
        | MetricAdded -> "added"
        | MetricRemoved -> "removed"
        | MetricImproved -> "improved"
        | MetricDeteriorated -> "deteriorated"
        | MetricUnchanged -> "unchanged"
        | MetricChanged -> "changed"
        | MetricNotComparable -> "not-comparable"

    let findingKind kind =
        match kind with
        | FindingIntroduced -> "introduced"
        | FindingResolved -> "resolved"
        | FindingPersistent -> "persistent"

    let ofComparison (comparison: CanonicalComparison) : CanonicalComparisonWire =
        { SchemaVersion = comparison.SchemaVersion
          BeforeSnapshotId = comparison.BeforeSnapshotId
          AfterSnapshotId = comparison.AfterSnapshotId
          MetricChanges =
            comparison.MetricChanges
            |> List.map (fun (change: CanonicalMetricChange) ->
                { MetricId = change.MetricId
                  MetricVersion = change.MetricVersion
                  Scope = change.Scope
                  Before = change.Before
                  After = change.After
                  Kind = metricKind change.Kind })
          FindingChanges =
            comparison.FindingChanges
            |> List.map (fun (change: CanonicalFindingChange) -> { FindingId = change.FindingId; Kind = findingKind change.Kind })
          Provenance = comparison.Provenance }
