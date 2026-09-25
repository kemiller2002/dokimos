namespace Dokimos.Core

open Dokimos.Domain

type MetricPreference =
    | PreferLower
    | PreferHigher
    | Contextual

type MetricDefinition =
    { Metric: MetricId
      Version: MetricVersion
      Unit: string
      Preference: MetricPreference }

type SnapshotComparison =
    { Before: SnapshotId
      After: SnapshotId
      Trends: (Observation * Observation * Trend) list
      AddedMetrics: Observation list
      MissingMetrics: Observation list }

module Snapshots =
    let compare definitions (before: Snapshot) (after: Snapshot) =
        let key (observation: Observation) =
            observation.Metric, observation.MetricVersion, observation.Scope

        let beforeMap = before.Observations |> List.map (fun x -> key x, x) |> Map.ofList
        let afterMap = after.Observations |> List.map (fun x -> key x, x) |> Map.ofList
        let definitionMap = definitions |> List.map (fun x -> (x.Metric, x.Version), x) |> Map.ofList

        let trends =
            afterMap
            |> Map.toList
            |> List.choose (fun (observationKey, current) ->
                match Map.tryFind observationKey beforeMap with
                | None -> None
                | Some previous ->
                    match Map.tryFind (current.Metric, current.MetricVersion) definitionMap with
                    | Some definition ->
                        let preference =
                            match definition.Preference with
                            | PreferLower -> Some LowerIsBetter
                            | PreferHigher -> Some HigherIsBetter
                            | Contextual -> None
                        preference |> Option.map (fun p -> previous, current, Trend.evaluate p previous current)
                    | None -> None)

        let added =
            afterMap |> Map.toList |> List.choose (fun (k,v) -> if Map.containsKey k beforeMap then None else Some v)
        let missing =
            beforeMap |> Map.toList |> List.choose (fun (k,v) -> if Map.containsKey k afterMap then None else Some v)

        { Before = before.Id; After = after.Id; Trends = trends; AddedMetrics = added; MissingMetrics = missing }
