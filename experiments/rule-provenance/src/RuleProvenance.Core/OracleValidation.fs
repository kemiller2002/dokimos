namespace RuleProvenance

/// VC-004 R4: the oracle may only block once it has been validated against
/// counterexamples. A small reference model of the invoice slice, written from
/// the handoff (REQ-INV-001/002, DEC-FIN-001, Q-FIN-001) and NOT from the Ordo
/// implementation, must hold every check; each seeded mutant must be caught.
module OracleValidation =
    type RefState =
        { Name: string
          Outstanding: decimal
          Payments: decimal list }

    /// Knobs that turn the reference into a mutant. The reference has all knobs off.
    type Variant =
        { Limit: decimal option
          ExemptCustomer: string -> bool
          AllowOverpayment: bool
          VoidFromPartiallyPaid: bool
          VoidFromPaid: bool
          PaidAfterAnyPayment: bool }

    let private model (refusalCode: string) (questionId: string) (v: Variant) : Probe<RefState> =
        let pay (s: RefState) customer amount =
            if amount <= 0m then Error "PAYMENT_NOT_POSITIVE"
            elif (match v.Limit with Some l -> amount > l && not (v.ExemptCustomer customer) | None -> false) then Error refusalCode
            elif amount > s.Outstanding && not v.AllowOverpayment then Error "PAYMENT_EXCEEDS_OUTSTANDING"
            else
                let outstanding = s.Outstanding - amount
                let name = if outstanding <= 0m || v.PaidAfterAnyPayment then "Paid" else "PartiallyPaid"
                Ok { Name = name; Outstanding = outstanding; Payments = s.Payments @ [ amount ] }
        let step (s: RefState) (c: ProbeCommand) =
            match s.Name, c with
            | "Draft", Issue -> Ok { s with Name = "Issued" }
            | ("Draft" | "Issued"), Void -> Ok { s with Name = "Voided" }
            | "PartiallyPaid", Void when v.VoidFromPartiallyPaid -> Ok { s with Name = "Voided" }
            | "PartiallyPaid", Void -> Error("REFUSED_PENDING_" + questionId)
            | "Paid", Void when v.VoidFromPaid -> Ok { s with Name = "Voided" }
            | ("Issued" | "PartiallyPaid"), Pay(customer, amount) -> pay s customer amount
            | ("Paid" | "Voided"), _ -> Error "INVOICE_TERMINAL"
            | _ -> Error "INVALID_TRANSITION"
        { Start = fun total -> { Name = "Draft"; Outstanding = total; Payments = [] }
          Step = step
          StateName = fun s -> s.Name
          Outstanding = fun s -> s.Outstanding
          PaymentsApplied = fun s -> s.Payments }

    let private referenceVariant limit =
        { Limit = Some limit
          ExemptCustomer = fun _ -> false
          AllowOverpayment = false
          VoidFromPartiallyPaid = false
          VoidFromPaid = false
          PaidAfterAnyPayment = false }

    /// (name, what it seeds, the variant).
    let mutants (limit: decimal) =
        let r = referenceVariant limit
        [ "drop-limit", "no single-payment limit", { r with Limit = None }
          "superseded-limit", "the superseded 25,000.00 limit (DEC-FIN-000 reused)", { r with Limit = Some 25000.00m }
          "str-exemption", "STR- customers exempt from the limit (OBS-LEGACY-001 reused)", { r with ExemptCustomer = fun c -> c.StartsWith "STR-" }
          "allow-overpayment", "payments above the outstanding balance accepted", { r with AllowOverpayment = true }
          "void-from-partially-paid", "Void allowed on a partially paid invoice (Q-FIN-001 decided)", { r with VoidFromPartiallyPaid = true }
          "void-from-paid", "Void allowed on a paid invoice", { r with VoidFromPaid = true }
          "paid-with-balance", "Paid after any payment, balance remaining", { r with PaidAfterAnyPayment = true } ]

    type MutantOutcome =
        { Name: string
          Seeds: string
          Killed: bool
          ViolatedChecks: string list }

    type Validation =
        { ReferenceHolds: bool
          ReferenceViolations: string list
          Mutants: MutantOutcome list
          Killed: int
          Total: int
          /// True when the reference holds and every mutant is killed.
          Validated: bool
          Note: string }

    let violated (run: OracleRun) =
        run.Results |> List.choose (fun r -> match r.Verdict with Violated _ -> Some r.Id | _ -> None)

    let reference (spec: OracleSpec) =
        match spec.Limit with
        | Some(_, limit, code) -> Some(model (Option.defaultValue "PAYMENT_LIMIT_EXCEEDED" code) (Option.defaultValue "Q-UNSPECIFIED" spec.PendingVoidQuestion) (referenceVariant limit))
        | None -> None

    let mutant (spec: OracleSpec) (name: string) =
        match spec.Limit with
        | Some(_, limit, code) ->
            mutants limit
            |> List.tryFind (fun (n, _, _) -> n = name)
            |> Option.map (fun (_, _, v) -> model (Option.defaultValue "PAYMENT_LIMIT_EXCEEDED" code) (Option.defaultValue "Q-UNSPECIFIED" spec.PendingVoidQuestion) v)
        | None -> None

    let validate (spec: OracleSpec) (bound: Bound) : Validation =
        match spec.Limit with
        | None ->
            { ReferenceHolds = false
              ReferenceViolations = []
              Mutants = []
              Killed = 0
              Total = 0
              Validated = false
              Note = "not validated: the handoff carries no accepted policy-limit decision to build the reference from" }
        | Some(_, limit, code) ->
            let build = model (Option.defaultValue "PAYMENT_LIMIT_EXCEEDED" code) (Option.defaultValue "Q-UNSPECIFIED" spec.PendingVoidQuestion)
            let refViolations = Oracle.run spec bound (build (referenceVariant limit)) |> violated
            let outcomes =
                mutants limit
                |> List.map (fun (name, seeds, v) ->
                    let checks = Oracle.run spec bound (build v) |> violated
                    { Name = name; Seeds = seeds; Killed = not checks.IsEmpty; ViolatedChecks = checks })
            let killed = outcomes |> List.filter (fun m -> m.Killed) |> List.length
            let ok = refViolations.IsEmpty && killed = outcomes.Length
            { ReferenceHolds = refViolations.IsEmpty
              ReferenceViolations = refViolations
              Mutants = outcomes
              Killed = killed
              Total = outcomes.Length
              Validated = ok
              Note =
                (if ok then $"validated: reference model holds every check; {killed}/{outcomes.Length} seeded mutants killed"
                 else
                     let joined = System.String.Join(", ", refViolations)
                     $"NOT validated: reference violations [{joined}]; {killed}/{outcomes.Length} seeded mutants killed") }
