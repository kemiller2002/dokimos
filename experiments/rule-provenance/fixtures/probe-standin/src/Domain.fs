module Invoice.Slice.Domain

[<Authority("REQ-INV-001")>]
type InvoiceState =
    | Draft
    | Issued
    | PartiallyPaid
    | Paid
    | Voided

type Invoice =
    { State: InvoiceState
      Currency: string
      Outstanding: decimal
      Payments: decimal list }

[<Authority("DEC-FIN-001")>]
let singlePaymentLimit = 10000.00m

[<Authority("DEC-FIN-001")>]
let limitRefusal = "PAYMENT_LIMIT_EXCEEDED"

[<Authority("Q-FIN-001")>]
let pendingVoidRefusal = "REFUSED_PENDING_Q-FIN-001"

[<Authority("REQ-INV-001")>]
let create (total: decimal) =
    { State = Draft; Currency = "USD"; Outstanding = total; Payments = [] }

[<Authority("REQ-INV-001")>]
let issue (invoice: Invoice) =
    match invoice.State with
    | Draft -> Ok { invoice with State = Issued }
    | Paid | Voided -> Error "INVOICE_TERMINAL"
    | Issued | PartiallyPaid -> Error "INVALID_TRANSITION"

[<Authority("REQ-INV-002", "DEC-FIN-001")>]
let pay (amount: decimal) (invoice: Invoice) =
    match invoice.State with
    | Issued | PartiallyPaid ->
        if amount <= 0m then Error "PAYMENT_NOT_POSITIVE"
        elif amount > singlePaymentLimit then Error limitRefusal
        elif amount > invoice.Outstanding then Error "PAYMENT_EXCEEDS_OUTSTANDING"
        else
            let outstanding = invoice.Outstanding - amount
            let state = if outstanding = 0m then Paid else PartiallyPaid
            Ok { invoice with State = state; Outstanding = outstanding; Payments = invoice.Payments @ [ amount ] }
    | Paid | Voided -> Error "INVOICE_TERMINAL"
    | Draft -> Error "INVALID_TRANSITION"

[<Authority("REQ-INV-001", "Q-FIN-001")>]
let voidInvoice (invoice: Invoice) =
    match invoice.State with
    | Draft | Issued -> Ok { invoice with State = Voided }
    | PartiallyPaid -> Error pendingVoidRefusal
    | Paid | Voided -> Error "INVOICE_TERMINAL"
