namespace Dokimos.Domain

open System

[<Struct>]
type SnapshotId = private SnapshotId of Guid

module SnapshotId =
    let create () = SnapshotId(Guid.NewGuid())
    let value (SnapshotId value) = value

[<Struct>]
type MetricId = private MetricId of string

module MetricId =
    let tryCreate value =
        if String.IsNullOrWhiteSpace value then Error "Metric id cannot be empty."
        else Ok(MetricId(value.Trim()))

    let value (MetricId value) = value

[<Struct>]
type MetricVersion = private MetricVersion of int

module MetricVersion =
    let tryCreate value =
        if value < 1 then Error "Metric version must be at least 1."
        else Ok(MetricVersion value)

    let value (MetricVersion value) = value

type Scope =
    | Repository
    | Project of path: string
    | Component of name: string
    | File of path: string
    | Type of filePath: string * qualifiedName: string
    | Member of filePath: string * qualifiedName: string

type UnavailabilityReason =
    | Unsupported
    | NotConfigured
    | InsufficientEvidence of string

type Failure =
    { Code: string
      Message: string }

type Measurement =
    | Available of value: decimal * unitName: string
    | Unavailable of UnavailabilityReason
    | Failed of Failure

type Provenance =
    { Collector: string
      CollectorVersion: string
      ConfigurationId: string
      CollectedAt: DateTimeOffset }

type Observation =
    { Metric: MetricId
      MetricVersion: MetricVersion
      Scope: Scope
      Measurement: Measurement
      Provenance: Provenance }

type Revision =
    { Repository: string
      Commit: string
      Ref: string option }

type Snapshot =
    { Id: SnapshotId
      Revision: Revision
      CreatedAt: DateTimeOffset
      Observations: Observation list }

type FindingState =
    | Introduced
    | Persistent
    | Improved
    | Resolved
    | Regressed
    | Resurfaced

type ThresholdDisposition =
    | ObserveOnly
    | Warn
    | Fail

type Threshold =
    { Metric: MetricId
      Maximum: decimal
      Disposition: ThresholdDisposition }

type ComparisonCompatibility =
    | Compatible
    | IncompatibleMetricDefinition of metric: MetricId * leftVersion: MetricVersion * rightVersion: MetricVersion

module Comparison =
    let compatibility (left: Observation) (right: Observation) =
        if left.Metric <> right.Metric then
            invalidArg "right" "Observations must describe the same metric."

        if left.MetricVersion = right.MetricVersion then Compatible
        else IncompatibleMetricDefinition(left.Metric, left.MetricVersion, right.MetricVersion)

    let delta (left: Observation) (right: Observation) =
        match compatibility left right, left.Measurement, right.Measurement with
        | Compatible, Available (oldValue, oldUnit), Available (newValue, newUnit) when oldUnit = newUnit ->
            Some(newValue - oldValue)
        | _ -> None
