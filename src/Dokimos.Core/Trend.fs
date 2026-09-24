namespace Dokimos.Core

open Dokimos.Domain

type Direction =
    | Improved
    | Unchanged
    | Deteriorated
    | NotComparable

type Preference =
    | LowerIsBetter
    | HigherIsBetter

type Trend =
    { Delta: decimal option
      Direction: Direction
      Compatibility: ComparisonCompatibility }

module Trend =
    let evaluate preference (before: Observation) (after: Observation) =
        let compatibility = Comparison.compatibility before after
        let delta = Comparison.delta before after

        let direction =
            match delta with
            | None -> NotComparable
            | Some 0m -> Unchanged
            | Some value ->
                match preference with
                | LowerIsBetter when value < 0m -> Improved
                | LowerIsBetter -> Deteriorated
                | HigherIsBetter when value > 0m -> Improved
                | HigherIsBetter -> Deteriorated

        { Delta = delta
          Direction = direction
          Compatibility = compatibility }
