namespace Dokimos.Core

open System
open Dokimos.Domain

type MetricCountsDto =
    { Available: int
      Unavailable: int
      Failed: int }

type SnapshotEntryDto =
    { SnapshotId: string
      SchemaVersion: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      DokimosVersion: string option
      Metrics: MetricCountsDto
      Findings: int
      IsBaseline: bool
      Performance: PerformanceDto option }

type PointDto =
    { SnapshotId: string
      Revision: string
      CollectedAt: DateTimeOffset
      MetricVersion: int
      Measurement: MeasurementDto }

type SeriesDto =
    { MetricId: string
      Scope: string
      Unit: string
      Preference: string
      Points: PointDto list
      FirstSeen: PointDto option
      LastSeen: PointDto option
      Latest: PointDto option
      Best: PointDto option
      Baseline: PointDto option
      BaselineDistance: decimal option
      IncompatibleVersions: int list
      Notes: string list }

type TimelineEntryDto =
    { SnapshotId: string
      Revision: string
      CollectedAt: DateTimeOffset
      State: string
      Note: string option
      Evidence: SignalDto list }

type FindingHistoryDto =
    { FindingId: string
      Kind: string
      Scope: string
      FirstSeen: string option
      LastSeen: string option
      Current: string
      Timeline: TimelineEntryDto list }

type HistoryQueryDto =
    { Kind: string
      Metric: string option
      Scope: string option
      File: string option }

type HistoryDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Store: string
      Repository: string option
      Query: HistoryQueryDto
      Baseline: SnapshotRefDto option
      Snapshots: SnapshotEntryDto list
      Series: SeriesDto list
      Findings: FindingHistoryDto list
      Notes: string list }

module HistoryContract =
    [<Literal>]
    let Contract = "dokimos.history"

    [<Literal>]
    let SchemaVersion = "1.0.0"

    let preferenceTag =
        function
        | PreferLower -> "lower-is-better"
        | PreferHigher -> "higher-is-better"
        | Contextual -> "contextual"

    let lifecycleTag =
        function
        | LifecycleIntroduced -> "introduced", None
        | LifecyclePersistent -> "persistent", None
        | LifecycleResolved -> "resolved", None
        | LifecycleResurfaced -> "resurfaced", None
        | LifecycleAbsent -> "absent", None
        | LifecycleUnavailable -> "unavailable", None
        | LifecycleUncertain note -> "uncertain", Some note

    let private pointDto (p: MetricPoint) : PointDto =
        { SnapshotId = p.SnapshotId
          Revision = p.Revision
          CollectedAt = p.CollectedAt
          MetricVersion = p.MetricVersion
          Measurement = Contracts.measurement p.Measurement }

    let seriesDto (s: MetricSeries) : SeriesDto =
        { MetricId = s.MetricId
          Scope = s.Scope
          Unit = s.Unit
          Preference = preferenceTag s.Preference
          Points = s.Points |> List.map pointDto
          FirstSeen = s.FirstSeen |> Option.map pointDto
          LastSeen = s.LastSeen |> Option.map pointDto
          Latest = s.Latest |> Option.map pointDto
          Best = s.Best |> Option.map pointDto
          Baseline = s.Baseline |> Option.map pointDto
          BaselineDistance = s.BaselineDistance
          IncompatibleVersions = s.IncompatibleVersions
          Notes =
            [ if s.Preference = Contextual then "Contextual metric: it changes but has no better direction, so no best demonstrated state is claimed."
              if not s.IncompatibleVersions.IsEmpty then "Points with other definition versions are listed but excluded from best state and baseline distance."
              if s.Baseline.IsNone then "No compatible baseline value; baseline distance is unavailable."
              if s.FirstSeen.IsNone then "The metric was never available in the stored evidence." ] }

    let findingDto (f: FindingHistory) : FindingHistoryDto =
        { FindingId = f.FindingId
          Kind = f.Kind
          Scope = f.Scope
          FirstSeen = f.FirstSeen
          LastSeen = f.LastSeen
          Current = fst (lifecycleTag f.Current)
          Timeline =
            f.Timeline
            |> List.map (fun e ->
                let state, note = lifecycleTag e.State
                { SnapshotId = e.SnapshotId
                  Revision = e.Revision
                  CollectedAt = e.CollectedAt
                  State = state
                  Note = note
                  Evidence = e.Evidence |> List.map Wire.signal }) }

    let entryDto (baselineId: string option) (s: CanonicalSnapshot) : SnapshotEntryDto =
        let count predicate = s.Metrics |> List.filter (fun m -> predicate m.Measurement) |> List.length
        { SnapshotId = s.SnapshotId
          SchemaVersion = s.SchemaVersion
          Revision = s.Revision
          Ref = s.Ref
          CollectedAt = s.CollectedAt
          DokimosVersion = s.Producer |> Option.map _.DokimosVersion
          Metrics =
            { Available = count (function Available _ -> true | _ -> false)
              Unavailable = count (function Unavailable _ -> true | _ -> false)
              Failed = count (function Failed _ -> true | _ -> false) }
          Findings = s.Findings.Length
          IsBaseline = baselineId = Some s.SnapshotId
          Performance =
            s.Performance
            |> Option.map (fun p ->
                { TotalMilliseconds = p.TotalMilliseconds
                  FileCount = p.FileCount
                  ObservationCount = p.ObservationCount
                  UnavailableCollectors = p.UnavailableCollectors
                  FailedCollectors = p.FailedCollectors }) }
