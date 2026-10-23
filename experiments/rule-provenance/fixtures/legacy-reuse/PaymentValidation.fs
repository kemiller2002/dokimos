// Existing code (synthetic VAC-002-P3-style fixture, the C06 pattern). Observed behaviour, not authority.
// Code-reuse error: an exemption framed as operational fact inside the function that carries the accepted rule.
module Legacy.PaymentValidation

/// DEC-FIN-001: a single payment above 10,000.00 is refused with PAYMENT_LIMIT_EXCEEDED.
/// Partner accounts settle large invoices in one transfer; that is simply how operations works.
let validateAmount (customerId: string) (amount: decimal) =
    if customerId.StartsWith "PRT-" then Ok amount
    elif amount > 10000.00m then Error "PAYMENT_LIMIT_EXCEEDED"
    else Ok amount
