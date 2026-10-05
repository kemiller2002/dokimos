namespace Dokimos.Core

open System.Text.Json
open Dokimos.Domain
open Dokimos.Core.Json

/// Decoders for the policy document's rule entries (dokimos-policy 1.0.0 to
/// 1.2.0). Contracts.policyDecoder assembles them.
module PolicyDecoders =
    let disposition =
        oneOf [ "observe-only", ObserveOnly; "warn", Warn; "fail", Fail ]

    let preference =
        oneOf [ "lower-is-better", LowerIsBetter; "higher-is-better", HigherIsBetter ]

    /// Policy 1.2.0 accepts "baseline": the limit is the accepted baseline
    /// snapshot's value for the same metric and scope. Earlier schemas take
    /// only a number.
    let bound baselineBound : Decoder<RatchetBound> =
        fun v ->
            if baselineBound && v.ValueKind = JsonValueKind.String then oneOf [ "baseline", AcceptedBaseline ] v
            else decimal v |> Result.map FixedBound

    let ratchet baselineBound : Decoder<RatchetRule> =
        fun e ->
            result {
                let! id = field "metricId" nonEmptyString e
                let! version = field "metricVersion" int e
                let! best = field "bestAccepted" (bound baselineBound) e
                let! preference = field "preference" preference e
                let! disposition = field "disposition" disposition e
                return { MetricId = id; MetricVersion = version; Bound = best; Preference = preference; Disposition = disposition }
            }

    let threshold: Decoder<ThresholdRule> =
        fun e ->
            result {
                let! id = field "metricId" nonEmptyString e
                let! version = field "metricVersion" int e
                let! maximum = field "maximum" decimal e
                let! disposition = field "disposition" disposition e
                return { MetricId = id; MetricVersion = version; Maximum = maximum; Disposition = disposition }
            }

    let suppression: Decoder<Suppression> =
        fun e ->
            result {
                let! id = field "findingId" nonEmptyString e
                let! reason = field "reason" nonEmptyString e
                let! scope = field "scope" nonEmptyString e
                let! actor = optionalField "actor" string e
                let! created = field "created" dateTimeOffset e
                let! expires = optionalField "expires" dateTimeOffset e
                let! status = field "status" (oneOf [ "active", SuppressionActive; "revoked", SuppressionRevoked ]) e
                return { FindingId = id; Reason = reason; Scope = scope; Actor = actor; Created = created; Expires = expires; Status = status }
            }

    /// Policy 1.2.0 moves waivers to the exceptions file (DOK-G001); a 1.2.0
    /// policy carrying `suppressions` is rejected rather than silently ignored.
    let noSuppressionsFrom120 schema (e: JsonElement) =
        match schema, e.TryGetProperty "suppressions" with
        | "1.2.0", (true, _) -> Error "suppressions moved to quality/exceptions.json (DOK-G001) in policy 1.2.0"
        | _ -> Ok()
