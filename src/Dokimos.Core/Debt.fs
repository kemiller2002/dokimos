namespace Dokimos.Core

open System

type DebtKind =
    | Todo
    | Fixme
    | WarningSuppression
    | Other of string

type DebtMarker =
    { Path: string
      Kind: DebtKind
      Anchor: string
      FirstSeen: DateTimeOffset
      LastSeen: DateTimeOffset
      Present: bool }

type DebtAge =
    { Marker: DebtMarker
      AgeDays: int
      StalenessDays: int }

module Debt =
    let age asOf marker =
        { Marker = marker
          AgeDays = max 0 (int (asOf - marker.FirstSeen).TotalDays)
          StalenessDays = max 0 (int (asOf - marker.LastSeen).TotalDays) }

type TestDistribution =
    { ProductionFiles: int
      TestFiles: int
      ProductionChanged: int
      TestsChanged: int
      UntestedChangeIndicator: bool }

module Tests =
    let distribution productionFiles testFiles productionChanged testsChanged =
        { ProductionFiles = productionFiles
          TestFiles = testFiles
          ProductionChanged = productionChanged
          TestsChanged = testsChanged
          UntestedChangeIndicator = productionChanged > 0 && testsChanged = 0 }
