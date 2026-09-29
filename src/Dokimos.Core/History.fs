namespace Dokimos.Core

open System
open Dokimos.Domain

type MetricPoint =
    { SnapshotId: string
      Revision: string
      CollectedAt: DateTimeOffset
      MetricVersion: int
      Measurement: Measurement }

/// The best demonstrated state is computed only over the latest
/// definition version; older incompatible versions are never joined in.
type MetricSeries =
    { MetricId: string
      Scope: string
      Unit: string
      Preference: MetricPreference
      Points: MetricPoint list
      FirstSeen: MetricPoint option
      LastSeen: MetricPoint option
      Latest: MetricPoint option
      Best: MetricPoint option
      Baseline: MetricPoint option
      BaselineDistance: decimal option
      IncompatibleVersions: int list }

type FindingLifecycleState =
    | LifecycleIntroduced
    | LifecyclePersistent
    | LifecycleResolved
    | LifecycleResurfaced
    | LifecycleAbsent
    | LifecycleUnavailable
    | LifecycleUncertain of note: string

type FindingTimelineEntry =
    { SnapshotId: string
      Revision: string
      CollectedAt: DateTimeOffset
      State: FindingLifecycleState
      Evidence: EvidenceSignal list }

type FindingHistory =
    { FindingId: string
      Kind: string
      Scope: string
      FirstSeen: string option
      LastSeen: string option
      Current: FindingLifecycleState
      Timeline: FindingTimelineEntry list }

module History =
    let ordered repository (snapshots: CanonicalSnapshot list) =
        snapshots
        |> List.filter (fun s -> repository |> Option.forall ((=) s.Repository))
        |> List.sortBy (fun s -> s.CollectedAt, s.SnapshotId)

    let private point (s: CanonicalSnapshot) (m: CanonicalMetric) =
        { SnapshotId = s.SnapshotId; Revision = s.Revision; CollectedAt = s.CollectedAt; MetricVersion = m.MetricVersion; Measurement = m.Measurement }

    let private valueOf p =
        match p.Measurement with
        | Available (v, _) -> Some v
        | _ -> None

    let private isAvailable p = (valueOf p).IsSome

    let private best preference (points: MetricPoint list) =
        let available = points |> List.filter isAvailable
        match preference, available with
        | _, [] -> None
        | PreferLower, _ -> available |> List.minBy (valueOf >> Option.get) |> Some
        | PreferHigher, _ -> available |> List.maxBy (valueOf >> Option.get) |> Some
        | Contextual, _ -> None

    let metricSeries (baseline: CanonicalSnapshot option) metricId scope (snapshots: CanonicalSnapshot list) =
        let points =
            snapshots
            |> List.choose (fun s -> s.Metrics |> List.tryFind (fun m -> m.MetricId = metricId && m.Scope = scope) |> Option.map (fun m -> s, m))
        match points with
        | [] -> None
        | _ ->
            let unit = points |> List.last |> snd |> _.Unit
            let all = points |> List.map (fun (s, m) -> point s m)
            let latestVersion = (List.last all).MetricVersion
            let compatible = all |> List.filter (fun p -> p.MetricVersion = latestVersion)
            let preference =
                Capabilities.tryDefinition metricId
                |> Option.filter (fun d -> d.Version = latestVersion)
                |> Option.map _.Preference
                |> Option.defaultValue Contextual
            let baselinePoint =
                baseline
                |> Option.bind (fun b -> b.Metrics |> List.tryFind (fun m -> m.MetricId = metricId && m.Scope = scope) |> Option.map (point b))
                |> Option.filter (fun p -> p.MetricVersion = latestVersion)
            let latest = List.last all
            Some
                { MetricId = metricId
                  Scope = scope
                  Unit = unit
                  Preference = preference
                  Points = all
                  FirstSeen = all |> List.tryFind isAvailable
                  LastSeen = all |> List.tryFindBack isAvailable
                  Latest = Some latest
                  Best = best preference compatible
                  Baseline = baselinePoint
                  BaselineDistance =
                    match valueOf latest, baselinePoint |> Option.bind valueOf with
                    | Some current, Some b when latest.MetricVersion = latestVersion -> Some(current - b)
                    | _ -> None
                  IncompatibleVersions = all |> List.map _.MetricVersion |> List.distinct |> List.filter ((<>) latestVersion) }

    let metricHistory baseline metricId (scope: string option) snapshots =
        snapshots
        |> List.collect (fun (s: CanonicalSnapshot) -> s.Metrics |> List.filter (fun m -> m.MetricId = metricId) |> List.map _.Scope)
        |> List.distinct
        |> List.filter (fun sc -> scope |> Option.forall ((=) sc))
        |> List.sort
        |> List.choose (fun sc -> metricSeries baseline metricId sc snapshots)

    let fileHistory baseline (path: string) snapshots =
        snapshots
        |> List.collect (fun (s: CanonicalSnapshot) -> s.Metrics |> List.filter (fun m -> m.Scope = path) |> List.map _.MetricId)
        |> List.distinct
        |> List.sort
        |> List.choose (fun id -> metricSeries baseline id path snapshots)

    let private findingsComplete s =
        Capabilities.requiredForFindings |> List.forall (fun a -> CanonicalSnapshot.analyzerRan s a <> Some false)

    let private scopes (s: CanonicalSnapshot) = s.Metrics |> List.map _.Scope |> Set.ofList

    /// Replays presence across ordered snapshots with the existing
    /// Findings.transition rules. A resolution at a file that disappeared,
    /// coinciding with the same kind introduced at a new file, is marked
    /// uncertain (possible move) rather than asserted as either identity.
    let findingHistories (snapshots: CanonicalSnapshot list) =
        let ids =
            snapshots |> List.collect (fun s -> s.Findings |> List.map (fun f -> f.FindingId, f)) |> List.distinctBy fst
        let possibleMoves (previous: CanonicalSnapshot) (current: CanonicalSnapshot) =
            let gone = Set.difference (scopes previous) (scopes current)
            let added = Set.difference (scopes current) (scopes previous)
            let resolvedKinds = previous.Findings |> List.filter (fun f -> gone.Contains f.Scope) |> List.map _.Kind |> Set.ofList
            let introducedKinds = current.Findings |> List.filter (fun f -> added.Contains f.Scope) |> List.map _.Kind |> Set.ofList
            Set.intersect resolvedKinds introducedKinds
        ids
        |> List.map (fun (id, exemplar) ->
            let presence (s: CanonicalSnapshot) =
                match s.Findings |> List.tryFind (fun f -> f.FindingId = id) with
                | Some f -> Present None, f.Evidence
                | None when findingsComplete s -> Absent, []
                | None -> Unknown, []
            let timeline =
                snapshots
                |> List.fold
                    (fun (entries, previous, everResolved) (s: CanonicalSnapshot) ->
                        let current, evidence = presence s
                        let state =
                            match previous with
                            | None ->
                                match current with
                                | Present _ -> LifecycleIntroduced
                                | Absent -> LifecycleAbsent
                                | Unknown -> LifecycleUnavailable
                            | Some (prevSnapshot, prevPresence) ->
                                let moved () = (possibleMoves prevSnapshot s).Contains exemplar.Kind
                                match Findings.transition prevPresence current with
                                | Some Introduced ->
                                    match Findings.resurfaced everResolved current with
                                    | Some _ -> LifecycleResurfaced
                                    | None when moved () -> LifecycleUncertain "introduced at a new file while a same-kind finding disappeared with its file (possible move)"
                                    | None -> LifecycleIntroduced
                                | Some Resolved when moved () -> LifecycleUncertain "file disappeared while a same-kind finding appeared at a new file (possible move)"
                                | Some Resolved -> LifecycleResolved
                                | Some (Persistent | Improved | Regressed | Resurfaced) -> LifecyclePersistent
                                | None ->
                                    match current with
                                    | Unknown -> LifecycleUnavailable
                                    | _ -> LifecycleAbsent
                        let resolvedNow = everResolved || state = LifecycleResolved
                        let entry = { SnapshotId = s.SnapshotId; Revision = s.Revision; CollectedAt = s.CollectedAt; State = state; Evidence = evidence }
                        entry :: entries, (if current = Unknown then previous else Some(s, current)), resolvedNow)
                    ([], None, false)
                |> fun (entries, _, _) -> List.rev entries
            let present = timeline |> List.filter (fun e -> not e.Evidence.IsEmpty)
            { FindingId = id
              Kind = exemplar.Kind
              Scope = exemplar.Scope
              FirstSeen = present |> List.tryHead |> Option.map _.SnapshotId
              LastSeen = present |> List.tryLast |> Option.map _.SnapshotId
              Current = timeline |> List.tryLast |> Option.map _.State |> Option.defaultValue LifecycleUnavailable
              Timeline = timeline })
