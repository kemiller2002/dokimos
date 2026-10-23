namespace RuleProvenance

open System
open System.Globalization

/// The probe command set, mirroring Invoice.Slice.Probe.ProbeCommand.
type ProbeCommand =
    | Issue
    | Pay of customerId: string * amount: decimal
    | Void

/// The Probe API as a record of functions. The oracle depends only on this,
/// never on the implementation: a reflection adapter fills it from
/// Invoice.Slice.dll, and the validation suite fills it from a reference model
/// and seeded mutants.
type Probe<'S> =
    { Start: decimal -> 'S
      Step: 'S -> ProbeCommand -> Result<'S, string>
      StateName: 'S -> string
      Outstanding: 'S -> decimal
      PaymentsApplied: 'S -> decimal list }

type Bound =
    { Depth: int
      Totals: decimal list
      Amounts: decimal list
      Customers: string list }

/// What the oracle checks, taken from the handoff (never from the implementation).
type OracleSpec =
    { /// Invariant IDs present in the handoff (checks for absent IDs are skipped).
      InvariantIds: string list
      /// The accepted single-payment limit decision: id, limit, refusal code.
      Limit: (string * decimal * string option) option
      /// An unresolved question whose permitted outcome is refuse-and-record and which
      /// governs voiding a partially paid invoice.
      PendingVoidQuestion: string option }

type CheckResult =
    { Id: string
      Verdict: InvariantVerdict }

type OracleRun =
    { Results: CheckResult list
      Bound: Bound
      SequencesExplored: int
      StatesVisited: int }

/// Bounded exhaustive exploration (breadth-first) of the Probe API.
module Oracle =
    let private inv (d: decimal) = d.ToString(CultureInfo.InvariantCulture)

    let defaultBound =
        { Depth = 4
          Totals = [ 0.01m; 9999.99m; 10000.00m; 15000.00m; 30000.00m ]
          Amounts = [ -1m; 0m; 0.01m; 5000m; 9999.99m; 10000.00m; 10000.01m; 25000.00m; 30000.00m ]
          Customers = [ "C-001"; "STR-001" ] }

    let describeBound (b: Bound) =
        let list xs = "[" + String.Join(", ", xs |> List.map inv) + "]"
        let customers = "[" + String.Join(", ", b.Customers) + "]"
        $"depth={b.Depth}; totals={list b.Totals}; amounts={list b.Amounts}; customers={customers}; commands per step={2 + b.Amounts.Length * b.Customers.Length}"

    let commandText =
        function
        | Issue -> "Issue"
        | Void -> "Void"
        | Pay(c, a) -> $"Pay({c}, {inv a})"

    let specOf (h: Handoff) : OracleSpec =
        let limit =
            h.Decisions
            |> List.filter (fun d -> d.Status = Accepted && d.Kind = "policy-limit")
            |> List.sortBy (fun d -> d.Id)
            |> List.tryPick (fun d ->
                d.Parameters
                |> List.tryFind (fun (k, _) -> k = "limit")
                |> Option.bind (fun (_, v) -> Scanner.literalValue v)
                |> Option.map (fun v -> d.Id, v, d.Parameters |> List.tryFind (fun (k, _) -> k = "refusalCode") |> Option.map snd))
        let question =
            h.PolicyQuestions
            |> List.filter (fun q -> q.PermittedOutcome = "refuse-and-record")
            |> List.sortBy (fun q -> q.Id)
            |> List.tryHead
            |> Option.map (fun q -> q.Id)
        { InvariantIds = h.Invariants |> List.map (fun i -> i.Id)
          Limit = limit
          PendingVoidQuestion = question }

    let private normalizeId (s: string) = String(s.ToUpperInvariant() |> Seq.filter Char.IsLetterOrDigit |> Array.ofSeq)

    /// Every exception from the target is converted to a refusal code so one bad
    /// call cannot abort the exploration; the code says so.
    let private safeStep (probe: Probe<'S>) (s: 'S) (c: ProbeCommand) =
        try probe.Step s c with ex -> Error("PROBE_EXCEPTION:" + ex.GetType().Name)

    type private Node<'S> = { State: 'S; History: string list }

    /// Run all checks. Results are reported in a fixed order.
    let run (spec: OracleSpec) (bound: Bound) (probe: Probe<'S>) : OracleRun =
        let has id = spec.InvariantIds |> List.contains id
        let limitId = spec.Limit |> Option.map (fun (id, _, _) -> id)
        let checkIds =
            [ "INV-INV-001"; "INV-INV-002"; "INV-INV-003"; "INV-INV-004" ] |> List.filter has
            |> fun ids -> ids @ Option.toList limitId @ Option.toList spec.PendingVoidQuestion
        let commands =
            [ yield Issue
              yield Void
              for c in bound.Customers do
                  for a in bound.Amounts do
                      yield Pay(c, a) ]
        let describeState (s: 'S) =
            let payments = String.Join(", ", probe.PaymentsApplied s |> List.map inv)
            $"state={probe.StateName s} outstanding={inv (probe.Outstanding s)} payments=[{payments}]"

        // Checks on a reached state.
        let stateViolations (s: 'S) =
            let outstanding = probe.Outstanding s
            let payments = probe.PaymentsApplied s
            [ if outstanding < 0m then yield "INV-INV-001", "outstanding balance is negative"
              let paid = probe.StateName s = "Paid"
              let settled = outstanding = 0m && not payments.IsEmpty
              if paid <> settled then yield "INV-INV-002", (if paid then "state is Paid but balance is not zero or no payment applied" else "balance is zero after a payment but state is not Paid")
              match spec.Limit with
              | Some(_, limit, _) ->
                  if payments |> List.exists (fun p -> p <= 0m || p > limit) then yield "INV-INV-003", "an applied payment is non-positive or above the limit"
              | None ->
                  if payments |> List.exists (fun p -> p <= 0m) then yield "INV-INV-003", "an applied payment is non-positive" ]

        // Checks on a transition.
        let transitionViolations (s: 'S) (cmd: ProbeCommand) (result: Result<'S, string>) =
            let name = probe.StateName s
            let before = probe.Outstanding s
            [ match result with
              | Ok s2 ->
                  if name = "Paid" || name = "Voided" then yield "INV-INV-004", $"{commandText cmd} accepted in terminal state {name}"
                  match cmd with
                  | Pay(_, amount) ->
                      let p1 = probe.PaymentsApplied s
                      let p2 = probe.PaymentsApplied s2
                      if p2.Length > p1.Length then
                          let applied = List.sum p2 - List.sum p1
                          if applied > before then yield "INV-INV-003", $"applied {inv applied} exceeds the balance {inv before} outstanding before it"
                          if applied <= 0m then yield "INV-INV-003", $"applied amount {inv applied} is not positive"
                      match spec.Limit with
                      | Some(id, limit, _) when amount > limit -> yield id, $"payment of {inv amount} above the limit {inv limit} accepted"
                      | _ -> ()
                  | Void ->
                      match spec.PendingVoidQuestion with
                      | Some q when name = "PartiallyPaid" -> yield q, $"Void of a partially paid invoice accepted; {q} is unresolved and its permitted outcome is refuse-and-record"
                      | _ -> ()
                  | Issue -> ()
              | Error code ->
                  match cmd with
                  | Void ->
                      match spec.PendingVoidQuestion with
                      | Some q when name = "PartiallyPaid" && not ((normalizeId code).Contains(normalizeId q)) ->
                          yield q, $"Void of a partially paid invoice refused with '{code}', which does not record {q}"
                      | _ -> ()
                  | Pay(_, amount) ->
                      match spec.Limit with
                      | Some(id, limit, Some refusal) when (name = "Issued" || name = "PartiallyPaid") && amount > limit && amount <= before && code <> refusal ->
                          yield id, $"payment of {inv amount} (within the balance, above the limit) refused with '{code}', not '{refusal}'"
                      | _ -> ()
                  | Issue -> () ]

        let mutable sequences = 0
        let mutable states = 0
        let found = Collections.Generic.Dictionary<string, string list>()
        let record (id: string) (history: string list) =
            if not (found.ContainsKey id) then found[id] <- history
        let allFound () = checkIds |> List.forall found.ContainsKey

        let startNodes =
            bound.Totals
            |> List.map (fun t ->
                let s = probe.Start t
                states <- states + 1
                let history = [ $"start(total={inv t})" ]
                for (id, why) in stateViolations s do
                    record id (history @ [ $"=> {describeState s}"; $"violates: {why}" ])
                { State = s; History = history })

        let rec explore (frontier: Node<'S> list) depth =
            if depth >= bound.Depth || frontier.IsEmpty || allFound () then ()
            else
                let next =
                    [ for node in frontier do
                          for cmd in commands do
                              sequences <- sequences + 1
                              let result = safeStep probe node.State cmd
                              let history = node.History @ [ commandText cmd ]
                              let outcome =
                                  match result with
                                  | Ok s2 -> $"=> accepted; {describeState s2}"
                                  | Error code -> $"=> refused '{code}' in {describeState node.State}"
                              for (id, why) in transitionViolations node.State cmd result do
                                  record id (history @ [ outcome; $"violates: {why}" ])
                              match result with
                              | Ok s2 ->
                                  states <- states + 1
                                  for (id, why) in stateViolations s2 do
                                      record id (history @ [ outcome; $"violates: {why}" ])
                                  yield { State = s2; History = history }
                              | Error _ -> () ]
                explore next (depth + 1)
        explore startNodes 0

        // Direct check of the limit decision, independent of the bound's amount set:
        // a payment one cent above the limit, within the balance, is refused for every customer.
        match spec.Limit with
        | Some(id, limit, refusal) ->
            let total = (bound.Totals @ [ limit * 3m ]) |> List.max
            for customer in bound.Customers do
                let s0 = probe.Start total
                let amount = limit + 0.01m
                let history = [ $"start(total={inv total})"; "Issue"; commandText (Pay(customer, amount)) ]
                match safeStep probe s0 Issue with
                | Error code -> record id (history.[0..1] @ [ $"=> refused '{code}'"; "violates: cannot reach Issued to test the limit" ])
                | Ok s1 ->
                    match safeStep probe s1 (Pay(customer, amount)) with
                    | Ok s2 -> record id (history @ [ $"=> accepted; {describeState s2}"; $"violates: payment above {inv limit} accepted for {customer}" ])
                    | Error code ->
                        match refusal with
                        | Some r when code <> r -> record id (history @ [ $"=> refused '{code}'"; $"violates: expected refusal code '{r}'" ])
                        | _ -> ()
        | None -> ()

        { Results =
            checkIds
            |> List.map (fun id ->
                { Id = id
                  Verdict =
                    match found.TryGetValue id with
                    | true, cx -> Violated cx
                    | _ -> HoldsWithinBound })
          Bound = bound
          SequencesExplored = sequences
          StatesVisited = states }
