namespace Dokimos.Core

open Dokimos.Domain

type GateResult =
    | Pass
    | Warning of actual: decimal * maximum: decimal
    | Failure of actual: decimal * maximum: decimal
    | NotEvaluated of reason: string

type Ratchet =
    { Metric: MetricId
      BestAccepted: decimal
      Preference: Preference
      Disposition: ThresholdDisposition }

module Policy =
    let evaluateThreshold (threshold: Threshold) (observation: Observation) =
        if threshold.Metric <> observation.Metric then
            NotEvaluated "Threshold and observation metrics differ."
        else
            match observation.Measurement with
            | Unavailable _ -> NotEvaluated "Metric unavailable."
            | Failed failure -> NotEvaluated $"Collection failed: {failure.Code}."
            | Available (actual, _) when actual <= threshold.Maximum -> Pass
            | Available (actual, _) ->
                match threshold.Disposition with
                | ObserveOnly -> Pass
                | Warn -> Warning(actual, threshold.Maximum)
                | Fail -> Failure(actual, threshold.Maximum)

    let evaluateRatchet (ratchet: Ratchet) (observation: Observation) =
        if ratchet.Metric <> observation.Metric then
            NotEvaluated "Ratchet and observation metrics differ."
        else
            match observation.Measurement with
            | Unavailable _ -> NotEvaluated "Metric unavailable."
            | Failed failure -> NotEvaluated $"Collection failed: {failure.Code}."
            | Available (actual, _) ->
                let deteriorated =
                    match ratchet.Preference with
                    | LowerIsBetter -> actual > ratchet.BestAccepted
                    | HigherIsBetter -> actual < ratchet.BestAccepted

                if not deteriorated then Pass
                else
                    match ratchet.Disposition with
                    | ObserveOnly -> Pass
                    | Warn -> Warning(actual, ratchet.BestAccepted)
                    | Fail -> Failure(actual, ratchet.BestAccepted)
