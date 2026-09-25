namespace Dokimos.Core

open System

type FileChange =
    { Path: string
      Commit: string
      ChangedAt: DateTimeOffset
      Additions: int
      Deletions: int
      PreviousPath: string option }

type TemporalEvidence =
    { Path: string
      CommitCount: int
      Additions: int
      Deletions: int
      Churn: int
      FirstChange: DateTimeOffset
      LastChange: DateTimeOffset
      RenameCount: int }

module Temporal =
    let summarize (path: string) (changes: FileChange list) =
        let relevant =
            changes
            |> List.filter (fun change -> change.Path = path || change.PreviousPath = Some path)
            |> List.sortBy _.ChangedAt

        match relevant with
        | [] -> None
        | first :: _ ->
            let last = List.last relevant
            Some
                { Path = path
                  CommitCount = relevant |> List.map _.Commit |> List.distinct |> List.length
                  Additions = relevant |> List.sumBy _.Additions
                  Deletions = relevant |> List.sumBy _.Deletions
                  Churn = relevant |> List.sumBy (fun x -> x.Additions + x.Deletions)
                  FirstChange = first.ChangedAt
                  LastChange = last.ChangedAt
                  RenameCount = relevant |> List.sumBy (fun x -> if x.PreviousPath.IsSome then 1 else 0) }

type StructuralEvidence =
    { Complexity: decimal option
      Size: decimal option
      FindingCount: int option }

type HotspotEvidence =
    { Path: string
      Temporal: TemporalEvidence
      Structural: StructuralEvidence
      Reasons: string list }

module Hotspots =
    let explain (temporal: TemporalEvidence) (structural: StructuralEvidence) =
        let reasons =
            [ if temporal.CommitCount >= 5 then "frequently-changed"
              if temporal.Churn >= 100 then "high-churn"
              match structural.Complexity with
              | Some value when value >= 10m -> "high-complexity"
              | _ -> ()
              match structural.FindingCount with
              | Some value when value > 0 -> "active-findings"
              | _ -> () ]

        { Path = temporal.Path
          Temporal = temporal
          Structural = structural
          Reasons = reasons }
