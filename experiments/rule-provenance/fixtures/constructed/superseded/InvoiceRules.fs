// Constructed sample (Dokimos rule-provenance experiment fixture; synthetic).
// Negative fixture: the limit cites the superseded DEC-FIN-000.
module Sample.Invoice.Rules

[<Authority("REQ-INV-001")>]
type InvoiceState =
    | Draft
    | Issued
    | PartiallyPaid
    | Paid
    | Voided

[<Authority("DEC-FIN-000")>]
let singlePaymentLimit = 25000.00m

[<Authority("REQ-INV-002", "DEC-FIN-001")>]
let validatePayment (outstanding: decimal) (amount: decimal) =
    if amount <= 0m then Error "PAYMENT_NOT_POSITIVE"
    elif amount > singlePaymentLimit then Error "PAYMENT_LIMIT_EXCEEDED"
    elif amount > outstanding then Error "PAYMENT_EXCEEDS_OUTSTANDING"
    else Ok amount

[<Authority("REQ-INV-001", "Q-FIN-001")>]
let voidInvoice (state: InvoiceState) =
    match state with
    | Draft | Issued -> Ok Voided
    | PartiallyPaid -> Error "REFUSED_PENDING_Q-FIN-001"
    | Paid | Voided -> Error "INVOICE_TERMINAL"
