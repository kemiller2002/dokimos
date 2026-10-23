module Invoice.Slice.Probe

open Invoice.Slice.Domain

type ProbeCommand =
    | Issue
    | Pay of customerId: string * amount: decimal
    | Void

type ProbeState = Invoice

let start (total: decimal) : ProbeState = create total

let step (state: ProbeState) (command: ProbeCommand) : Result<ProbeState, string> =
    match command with
    | Issue -> issue state
    | Pay(_, amount) -> pay amount state
    | Void -> voidInvoice state

let stateName (state: ProbeState) =
    match state.State with
    | Draft -> "Draft"
    | Issued -> "Issued"
    | PartiallyPaid -> "PartiallyPaid"
    | Paid -> "Paid"
    | Voided -> "Voided"

let outstanding (state: ProbeState) = state.Outstanding

let paymentsApplied (state: ProbeState) = state.Payments
