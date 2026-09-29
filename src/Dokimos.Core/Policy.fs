namespace Dokimos.Core

open Dokimos.Domain

/// The judgment of one policy rule against one piece of evidence. Every case
/// is a distinct state: unavailable, failed collection, and incompatible
/// definitions are never folded into Pass.
type GateResult =
    | Pass
    | Warning of actual: decimal * maximum: decimal
    | Failure of actual: decimal * maximum: decimal
    | ObservedOnly of actual: decimal * maximum: decimal
    | NotEvaluated of reason: string
    | EvidenceUnavailable of UnavailabilityReason
    | CollectionFailed of Failure
    | Incompatible of expectedVersion: int * actualVersion: int

type Ratchet =
    { Metric: MetricId
      BestAccepted: decimal
      Preference: Preference
      Disposition: ThresholdDisposition }

module Policy =
    let private judge disposition actual limit =
        match disposition with
        | ObserveOnly -> ObservedOnly(actual, limit)
        | Warn -> Warning(actual, limit)
        | Fail -> Failure(actual, limit)

    let private evaluate metricsMatch mismatch (observation: Observation) judgeAvailable =
        if not metricsMatch then
            NotEvaluated mismatch
        else
            match observation.Measurement with
            | Unavailable reason -> EvidenceUnavailable reason
            | Failed failure -> CollectionFailed failure
            | Available (actual, _) -> judgeAvailable actual

    let evaluateThreshold (threshold: Threshold) (observation: Observation) =
        evaluate (threshold.Metric = observation.Metric) "Threshold and observation metrics differ." observation (fun actual ->
            if actual <= threshold.Maximum then Pass else judge threshold.Disposition actual threshold.Maximum)

    let evaluateRatchet (ratchet: Ratchet) (observation: Observation) =
        evaluate (ratchet.Metric = observation.Metric) "Ratchet and observation metrics differ." observation (fun actual ->
            let deteriorated =
                match ratchet.Preference with
                | LowerIsBetter -> actual > ratchet.BestAccepted
                | HigherIsBetter -> actual < ratchet.BestAccepted

            if deteriorated then judge ratchet.Disposition actual ratchet.BestAccepted else Pass)
