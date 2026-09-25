namespace Dokimos.Core

type RegionIdentity =
    { Path: string
      Anchor: string
      Confidence: decimal }

type RegionChange =
    { Identity: RegionIdentity
      Commit: string
      AddedLines: int
      DeletedLines: int }

type RegionHistory =
    { Identity: RegionIdentity
      DistinctCommits: int
      Modifications: int
      Churn: int }

module Regions =
    let summarize (identity: RegionIdentity) (changes: RegionChange list) =
        let matches =
            changes
            |> List.filter (fun change ->
                change.Identity.Path = identity.Path &&
                change.Identity.Anchor = identity.Anchor)

        if List.isEmpty matches then None
        else
            Some
                { Identity = identity
                  DistinctCommits = matches |> List.map _.Commit |> List.distinct |> List.length
                  Modifications = List.length matches
                  Churn = matches |> List.sumBy (fun x -> x.AddedLines + x.DeletedLines) }

    let isRepeated (minimumCommits: int) (history: RegionHistory) =
        history.DistinctCommits >= minimumCommits
