module RuleProvenance.Tests.OracleTests

open System.IO
open Xunit
open RuleProvenance
open RuleProvenance.Cli
open RuleProvenance.Tests.Support

let private handoff =
    match Inputs.decodeHandoff (File.ReadAllBytes handoffPath) with
    | Ok h -> h
    | Error es -> failwithf "%A" es

let private spec = Oracle.specOf handoff
let private bound = Oracle.defaultBound

let private violated (run: OracleRun) = OracleValidation.violated run |> Set.ofList

// ---------------------------------------------------------------- an independent test-local model
// Written separately from OracleValidation so the oracle is validated against two
// independently written references. States are a union, not strings.

type private TState =
    | TDraft of total: decimal
    | TIssued of total: decimal
    | TPartial of outstanding: decimal * payments: decimal list
    | TPaid of outstanding: decimal * payments: decimal list
    | TVoided of outstanding: decimal * payments: decimal list

type private Knobs =
    { NoLimit: bool
      StrExempt: bool
      Overpay: bool
      VoidAfterPayment: bool
      PaidEarly: bool }

let private noKnobs = { NoLimit = false; StrExempt = false; Overpay = false; VoidAfterPayment = false; PaidEarly = false }

let private testModel (k: Knobs) : Probe<TState> =
    let apply (outstanding: decimal) (payments: decimal list) (customer: string) (amount: decimal) =
        let overLimit = amount > 10000.00m && not k.NoLimit && not (k.StrExempt && customer.StartsWith "STR-")
        if amount <= 0m then Error "NOT_POSITIVE"
        elif overLimit then Error "PAYMENT_LIMIT_EXCEEDED"
        elif amount > outstanding && not k.Overpay then Error "OVER_BALANCE"
        else
            let left = outstanding - amount
            let ps = payments @ [ amount ]
            if left = 0m || k.PaidEarly then Ok(TPaid(left, ps)) else Ok(TPartial(left, ps))
    let outstanding =
        function
        | TDraft t | TIssued t -> t
        | TPartial(o, _) | TVoided(o, _) | TPaid(o, _) -> o
    { Start = TDraft
      Step =
        fun s c ->
            match s, c with
            | TDraft t, Issue -> Ok(TIssued t)
            | TDraft t, Void | TIssued t, Void -> Ok(TVoided(t, []))
            | TIssued t, Pay(cu, a) -> apply t [] cu a
            | TPartial(o, ps), Pay(cu, a) -> apply o ps cu a
            | TPartial(o, ps), Void when k.VoidAfterPayment -> Ok(TVoided(o, ps))
            | TPaid(o, ps), Void when k.VoidAfterPayment -> Ok(TVoided(o, ps))
            | TPartial _, Void -> Error "REFUSED_PENDING_Q-FIN-001"
            | _ -> Error "REFUSED"
      StateName =
        function
        | TDraft _ -> "Draft"
        | TIssued _ -> "Issued"
        | TPartial _ -> "PartiallyPaid"
        | TPaid _ -> "Paid"
        | TVoided _ -> "Voided"
      Outstanding = outstanding
      PaymentsApplied =
        function
        | TDraft _ | TIssued _ -> []
        | TPartial(_, ps) | TPaid(_, ps) | TVoided(_, ps) -> ps }

[<Fact>]
let ``oracle reports the test-local reference model as holding every check`` () =
    let run = Oracle.run spec bound (testModel noKnobs)
    Assert.Empty(violated run)
    Assert.Equal<string list>([ "INV-INV-001"; "INV-INV-002"; "INV-INV-003"; "INV-INV-004"; "DEC-FIN-001"; "Q-FIN-001" ], run.Results |> List.map (fun r -> r.Id))

[<Theory>]
[<InlineData("drop-limit")>]
[<InlineData("str-exemption")>]
[<InlineData("overpayment")>]
[<InlineData("void-after-payment")>]
[<InlineData("paid-with-balance")>]
let ``oracle kills every test-local mutant`` (name: string) =
    let knobs =
        match name with
        | "drop-limit" -> { noKnobs with NoLimit = true }
        | "str-exemption" -> { noKnobs with StrExempt = true }
        | "overpayment" -> { noKnobs with Overpay = true }
        | "void-after-payment" -> { noKnobs with VoidAfterPayment = true }
        | _ -> { noKnobs with PaidEarly = true }
    let v = violated (Oracle.run spec bound (testModel knobs))
    Assert.NotEmpty v
    match name with
    | "drop-limit"
    | "str-exemption" ->
        Assert.Contains("DEC-FIN-001", v)
        Assert.Contains("INV-INV-003", v)
    | "overpayment" -> Assert.Contains("INV-INV-003", v)
    | "void-after-payment" ->
        Assert.Contains("INV-INV-004", v)
        Assert.Contains("Q-FIN-001", v)
    | _ -> Assert.Contains("INV-INV-002", v)

// ---------------------------------------------------------------- the built-in checker validation (VC-004 R4)

[<Fact>]
let ``built-in checker validation: reference holds and 7 of 7 seeded mutants are killed`` () =
    let v = OracleValidation.validate spec bound
    Assert.True(v.ReferenceHolds, sprintf "%A" v.ReferenceViolations)
    Assert.Equal(7, v.Total)
    Assert.Equal(7, v.Killed)
    Assert.True v.Validated
    let byName n = v.Mutants |> List.find (fun m -> m.Name = n)
    Assert.Contains("DEC-FIN-001", (byName "drop-limit").ViolatedChecks)
    Assert.Contains("DEC-FIN-001", (byName "superseded-limit").ViolatedChecks)
    Assert.Contains("DEC-FIN-001", (byName "str-exemption").ViolatedChecks)
    Assert.Contains("INV-INV-001", (byName "allow-overpayment").ViolatedChecks)
    Assert.Contains("Q-FIN-001", (byName "void-from-partially-paid").ViolatedChecks)
    Assert.Contains("INV-INV-004", (byName "void-from-paid").ViolatedChecks)
    Assert.Contains("INV-INV-002", (byName "paid-with-balance").ViolatedChecks)

[<Fact>]
let ``a 10000.01 payment is refused for STR-001 as for every customer (direct DEC-FIN-001 check)`` () =
    // A bound without 10000.01 still catches the exemption through the direct check.
    let narrow = { bound with Amounts = [ 5000m ]; Depth = 1 }
    let exempt = (OracleValidation.mutant spec "str-exemption").Value
    let run = Oracle.run spec narrow exempt
    match (run.Results |> List.find (fun r -> r.Id = "DEC-FIN-001")).Verdict with
    | Violated cx -> Assert.Contains("Pay(STR-001, 10000.01)", cx)
    | other -> failwithf "expected violation, got %A" other

[<Fact>]
let ``counterexamples are shortest sequences (breadth-first)`` () =
    let run = Oracle.run spec bound (OracleValidation.mutant spec "drop-limit").Value
    match (run.Results |> List.find (fun r -> r.Id = "DEC-FIN-001")).Verdict with
    | Violated cx -> Assert.Equal<string list>([ "start(total=15000.00)"; "Issue"; "Pay(C-001, 10000.01)" ], cx |> List.take 3)
    | other -> failwithf "expected violation, got %A" other

// ---------------------------------------------------------------- reflection adapter and integration

[<Fact>]
let ``reflection adapter loads the stand-in Invoice.Slice.dll and every check holds`` () =
    match ProbeLoader.load standinDll with
    | Error es -> failwithf "%A" es
    | Ok probe ->
        let s = probe.Start 100m
        Assert.Equal("Draft", probe.StateName s)
        match probe.Step s Issue |> Result.bind (fun s -> probe.Step s (Pay("C-001", 40m))) with
        | Ok s2 ->
            Assert.Equal("PartiallyPaid", probe.StateName s2)
            Assert.Equal(60m, probe.Outstanding s2)
            Assert.Equal<decimal list>([ 40m ], probe.PaymentsApplied s2)
            Assert.Equal(Error "REFUSED_PENDING_Q-FIN-001", probe.Step s2 Void |> Result.map probe.StateName)
        | Error e -> failwith e
        Assert.Empty(violated (Oracle.run spec bound probe))

[<Fact>]
let ``integrate mode on the stand-in plus shared legacy code passes with the two owner questions`` () =
    let s = path "fixtures/probe-standin"
    let r = cli [ "integrate"; "--ordo-src"; Path.Combine(s, "src"); "--trace"; Path.Combine(s, "construction-trace.json"); "--dll"; standinDll; "--handoff"; handoffPath; "--legacy"; legacyPath; "--sdk-version"; "test-sdk" ]
    Assert.Equal(ExitCode.Continue, r.Exit)
    let doc = json r
    Assert.All(invariantVerdicts doc |> Map.toList, fun (_, v) -> Assert.Equal("holds-within-bound", v))
    Assert.Equal(2, (findings doc).Length)
    Assert.True(doc.GetProperty("validity").TryGetProperty("dependencyDigest") |> fst)

[<Fact>]
let ``a seeded drop-limit mutant is a proven, blocking invariant-violation (exit 4)`` () =
    let r = audit [ "--probe-model"; "mutant:drop-limit" ]
    Assert.Equal(ExitCode.PolicyFailure, r.Exit)
    let doc = json r
    let fs = findings doc
    Assert.NotEmpty fs
    for f in fs do
        Assert.Equal("invariant-violation", str f "class")
        Assert.Equal("proven", str f "certainty")
        Assert.True(f.GetProperty("blocking").GetBoolean())
    Assert.Equal("violated", (invariantVerdicts doc)["INV-INV-003"])
    Assert.Equal("fail", str (doc.GetProperty "summary") "verdict")

[<Fact>]
let ``a violation found by an unvalidated checker does not block`` () =
    // Without an accepted policy-limit decision the checker cannot be validated.
    let bytes = File.ReadAllText(handoffPath).Replace("\"kind\": \"policy-limit\"", "\"kind\": \"business-rule\"") |> System.Text.Encoding.UTF8.GetBytes
    let inputs =
        { Config = AuditConfig.defaults
          HandoffBytes = bytes
          Trace = None
          Sources = []
          Probe = Some(ProbeTarget.ofProbe "test:overpay" "test:overpay" (testModel { noKnobs with Overpay = true }))
          Bound = bound
          DependencyDigest = None
          SdkVersion = "test" }
    match Audit.run inputs with
    | Error es -> failwithf "%A" es
    | Ok(report, validation) ->
        Assert.False validation.Value.Validated
        let violations = report.Findings |> List.filter (fun f -> f.Class = InvariantViolation)
        Assert.NotEmpty violations
        Assert.All(violations, fun f -> Assert.False f.Blocking)
        Assert.Equal(ExitCode.Continue, Report.exitCode report)

[<Fact>]
let ``audit without a probe marks every handoff invariant not-checked`` () =
    let doc = json (audit [ "--legacy"; legacyPath ])
    Assert.Equal<string list>([ "INV-INV-001"; "INV-INV-002"; "INV-INV-003"; "INV-INV-004" ], invariantVerdicts doc |> Map.toList |> List.map fst)
    Assert.All(invariantVerdicts doc |> Map.toList, fun (_, v) -> Assert.Equal("not-checked", v))
