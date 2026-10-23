// Existing code (synthetic VAC-002-P3-style fixture). Observed behaviour, not authority.
// Code-reuse error: a silent 0.00 default decides every currency nobody listed.
module Legacy.CurrencyLimits

/// Single-payment limit per invoice currency.
let singlePaymentLimitFor (currency: string) =
    match currency with
    | "USD" -> 10000.00m
    | _ -> 0.00m
