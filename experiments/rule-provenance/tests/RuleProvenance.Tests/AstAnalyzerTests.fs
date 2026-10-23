module RuleProvenance.Tests.AstAnalyzerTests

open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open RuleProvenance
open RuleProvenance.Tests.Support

// The v2 (`ast`) analyzer on synthetic legacy code. The domain, identifiers and values
// here are deliberately unlike the VAC-002-P3 development corpus.

let private decision (id: string) (status: string) (statement: string) (parameters: (string * string) list) (supersededBy: string option) =
    let d = JsonObject()
    d["id"] <- JsonValue.Create id
    d["statement"] <- JsonValue.Create statement
    d["status"] <- JsonValue.Create status
    d["kind"] <- JsonValue.Create "business-rule"
    d["owner"] <- JsonValue.Create "warehouse-owner"
    if status = "accepted" then
        let by = JsonObject()
        by["kind"] <- JsonValue.Create "human"
        by["id"] <- JsonValue.Create "warehouse-owner"
        by["role"] <- JsonValue.Create "decision-owner"
        d["authorizedBy"] <- by
        d["authorizedAt"] <- JsonValue.Create "2026-01-01T00:00:00Z"
    d["requirementIds"] <- JsonArray()
    let src = JsonObject()
    src["path"] <- JsonValue.Create "docs/decisions.md"
    src["locator"] <- JsonValue.Create("# " + id)
    src["revision"] <- JsonValue.Create "test"
    src["sha256"] <- JsonValue.Create("sha256:" + String.replicate 64 "0")
    d["source"] <- src
    d["supersedes"] <- JsonArray()
    supersededBy |> Option.iter (fun s -> d["supersededBy"] <- JsonValue.Create s)
    if not parameters.IsEmpty then
        let p = JsonObject()
        for (k, v) in parameters do
            p[k] <- JsonValue.Create v
        d["parameters"] <- p
    d

/// The shared fixture handoff with its decisions replaced by warehouse decisions and
/// no observations (so the audit must find every rule itself).
let private warehouseHandoff (dir: string) =
    let doc = JsonNode.Parse(File.ReadAllText handoffPath) |> nonNull
    let decisions = JsonArray()
    for d in
        [ decision "WH-DEC-1" "accepted" "Orders are dispatched within 48 hours of payment." [ "hours", "48" ] None
          decision "WH-DEC-0" "superseded" "Orders are dispatched within 24 hours of payment." [ "hours", "24" ] (Some "WH-DEC-1")
          decision "WH-DEC-2" "accepted" "Orders are packed within 10 days." [ "days", "10" ] None
          decision "WH-DEC-3" "accepted" "A courier collects only paid orders." [] None
          decision "WH-DEC-4" "proposed" "Bronze accounts are never billed." [] None ] do
        decisions.Add d
    doc["decisions"] <- decisions
    doc["observations"] <- JsonArray()
    let p = Path.Combine(dir, "handoff.json")
    File.WriteAllText(p, doc.ToJsonString())
    p

let private auditFiles (analyzer: string) (files: (string * string) list) =
    let dir = tempDir ()
    let handoff = warehouseHandoff dir
    let paths =
        files
        |> List.map (fun (name, text) ->
            let p = Path.Combine(dir, name)
            File.WriteAllText(p, text)
            p)
    let r =
        cli ([ "audit"; "--root"; dir; "--handoff"; handoff; "--sdk-version"; "test-sdk"; "--analyzer"; analyzer ]
             @ (paths |> List.collect (fun p -> [ "--legacy"; p ])))
    Assert.True((r.Exit = ExitCode.Continue), $"exit {r.Exit}: {r.Stderr}")
    r

let private bySymbol (r: RuleProvenance.Cli.CliResult) =
    findings (json r) |> List.map (fun f -> str (f.GetProperty "location") "symbol", f) |> Map.ofList

let private header = "module Warehouse.Rules\n\nopen System\n\n"

[<Fact>]
let ``a multi-line exemption condition is found (the compared literal sits on its own line)`` () =
    let source =
        header
        + "/// WH-DEC-3: a courier collects only paid orders.\n"
        + "let collectable (order: Order) =\n"
        + "    order.Paid\n"
        + "    || (order.Channel =\n"
        + "            \"wholesale\")\n"
    let ast = bySymbol (auditFiles "ast" [ "Rules.fs", source ])
    let f = ast["Warehouse.Rules.collectable"]
    Assert.Equal("unverified-inherited-rule", str f "class")
    Assert.Equal("uncertain-authority", str f "certainty")
    Assert.False(f.GetProperty("blocking").GetBoolean())
    let message = str f "message"
    Assert.Contains("exemption shape '||' on \"wholesale\"", message)
    Assert.Contains("an exemption from WH-DEC-3 needs its own decision record", message)
    // The lexical analyzer computes the condition context per line and misses it.
    let lexical = bySymbol (auditFiles "lexical" [ "Rules.fs", source ])
    Assert.False(lexical.ContainsKey "Warehouse.Rules.collectable")

[<Fact>]
let ``union-case exemptions are found in comparisons and in match clauses`` () =
    let source =
        header
        + "let billable (account: Account) = account.Tier <> Gold && account.Balance > 0m\n\n"
        + "let fastTrack (account: Account) =\n"
        + "    match account.Tier with\n"
        + "    | Gold -> true\n"
        + "    | Some _ -> account.Paid\n"
        + "    | None -> false\n"
    let fs = bySymbol (auditFiles "ast" [ "Rules.fs", source ])
    let billable = str fs["Warehouse.Rules.billable"] "message"
    Assert.Contains("exemption shape '&& not' on Gold", billable)
    Assert.Contains("category test '<> Gold'", billable)
    let fastTrack = str fs["Warehouse.Rules.fastTrack"] "message"
    Assert.Contains("case keyed on Gold", fastTrack)
    // Option cases are F# built-ins, not business categories.
    Assert.DoesNotContain("keyed on Some", fastTrack)
    Assert.DoesNotContain("keyed on None", fastTrack)

[<Fact>]
let ``an integer horizon constant used in date arithmetic is rule-bearing`` () =
    let source = header + "let holdDays = 14\n\nlet releaseOn (placed: DateOnly) = placed.AddDays holdDays\n"
    let fs = bySymbol (auditFiles "ast" [ "Rules.fs", source ])
    let f = fs["Warehouse.Rules.holdDays"]
    Assert.Equal("unverified-inherited-rule", str f "class")
    Assert.Contains("numeric literal 14", str f "message")
    Assert.Contains("equals no declared parameter", str f "message")
    Assert.False(fs.ContainsKey "Warehouse.Rules.releaseOn")
    let lexical = bySymbol (auditFiles "lexical" [ "Rules.fs", source ])
    Assert.False(lexical.ContainsKey "Warehouse.Rules.holdDays")

[<Fact>]
let ``a value equal to an accepted decision parameter is supported and raises no question`` () =
    let source =
        header
        + "let dispatchWindowHours = 48\n\n"
        + "let windowFor (express: bool) = if express then 48 else 72\n"
    let fs = bySymbol (auditFiles "ast" [ "Rules.fs", source ])
    Assert.False(fs.ContainsKey "Warehouse.Rules.dispatchWindowHours")
    // A question that remains lists the constructs that matching supported, and how.
    let message = str fs["Warehouse.Rules.windowFor"] "message"
    Assert.Contains("numeric literal 72", message)
    Assert.Contains("numeric literal 48 (line 7): (b) parameter, value 48 = WH-DEC-1.hours (accepted, same form)", message)

[<Fact>]
let ``a value equal only to a superseded decision parameter is a superseded-authority question`` () =
    let fs = bySymbol (auditFiles "ast" [ "Rules.fs", header + "let dispatchWindowHours = 24\n" ])
    let f = fs["Warehouse.Rules.dispatchWindowHours"]
    Assert.Equal("superseded-authority", str f "class")
    Assert.Equal("uncertain-authority", str f "certainty")
    Assert.False(f.GetProperty("blocking").GetBoolean())
    Assert.Contains("WH-DEC-0", refs f)
    Assert.Contains("WH-DEC-1", refs f)
    Assert.Contains("value 24 = WH-DEC-0.hours (superseded by WH-DEC-1, same form)", str f "message")

[<Fact>]
let ``a comment citation supports a matching value but not a mismatching one`` () =
    let cited value = header + "/// WH-DEC-2: orders are packed in time.\n" + $"let packingDays = {value}\n"
    Assert.Empty(findings (json (auditFiles "ast" [ "Rules.fs", cited 10 ])))
    let fs = bySymbol (auditFiles "ast" [ "Rules.fs", cited 12 ])
    let f = fs["Warehouse.Rules.packingDays"]
    Assert.Equal("unverified-inherited-rule", str f "class")
    Assert.Contains("cited WH-DEC-2 declares days=10, not 12", str f "message")
    Assert.Contains("WH-DEC-2", refs f)

[<Fact>]
let ``a comment citing a proposed decision does not authorize a category test`` () =
    let source = header + "/// WH-DEC-4: bronze accounts are never billed.\nlet exempt (account: Account) = account.Tier = Bronze\n"
    let f = (bySymbol (auditFiles "ast" [ "Rules.fs", source ]))["Warehouse.Rules.exempt"]
    Assert.Contains("cites WH-DEC-4 (proposed), which authorizes nothing", str f "message")
    Assert.Contains("named only by WH-DEC-4 (proposed)", str f "message")

[<Fact>]
let ``ast reports are deterministic, order-independent and schema-valid`` () =
    let a = "A.fs", header + "let holdDays = 14\n"
    let b = "B.fs", "module Warehouse.More\n\nlet fee (region: string) =\n    match region with\n    | \"north\" -> 4.50m\n    | _ -> 0m\n"
    let r1 = auditFiles "ast" [ a; b ]
    let r2 = auditFiles "ast" [ a; b ]
    let r3 = auditFiles "ast" [ b; a ]
    // Each run uses a fresh temp root, so compare the findings rather than the whole report.
    let findingsText (r: RuleProvenance.Cli.CliResult) = (json r).GetProperty("findings").GetRawText()
    Assert.Equal(findingsText r1, findingsText r2)
    Assert.Equal(findingsText r1, findingsText r3)
    Assert.Contains("wildcard default", findingsText r1)
    let out = Path.Combine(tempDir (), "ast.json")
    File.WriteAllText(out, r1.Stdout)
    let code, output = validateSchema auditSchema [ out ]
    Assert.True((code = 0), output)

[<Fact>]
let ``v1 still runs behind --analyzer lexical, and the analyzer is part of the config digest`` () =
    let lexical = audit [ "--analyzer"; "lexical"; "--legacy"; path "fixtures/legacy-reuse/PaymentValidation.fs" ]
    let ast = audit [ "--legacy"; path "fixtures/legacy-reuse/PaymentValidation.fs" ]
    Assert.Equal(ExitCode.Continue, lexical.Exit)
    Assert.Contains("matches no observation and cites no authority", str (findings (json lexical)).Head "message")
    Assert.Contains("(ast analyzer)", str (findings (json ast)).Head "message")
    let digest (r: RuleProvenance.Cli.CliResult) = str ((json r).GetProperty "validity") "configDigest"
    Assert.NotEqual<string>(digest lexical, digest ast)
    Assert.Equal(digest ast, digest (audit [ "--analyzer"; "ast"; "--legacy"; path "fixtures/legacy-reuse/PaymentValidation.fs" ]))
    let config cfg = Audit.canonicalConfig { Config = cfg; HandoffBytes = [||]; Trace = None; Sources = []; Probe = None; Bound = Oracle.defaultBound; DependencyDigest = None; SdkVersion = "x" }
    Assert.Contains("analyzer=lexical/1", config { AuditConfig.defaults with Analyzer = Lexical })
    Assert.Contains($"analyzer=ast/2.0; fcs={AstScanner.FcsVersion}", config AuditConfig.defaults)
    Assert.Equal(ExitCode.InvalidInput, (audit [ "--analyzer"; "typed-maybe" ]).Exit)

[<Fact>]
let ``a prior lexical result is not reusable for an ast audit`` () =
    let dir = tempDir ()
    let prior = Path.Combine(dir, "prior.json")
    Assert.Equal(0, (audit [ "--analyzer"; "lexical"; "--legacy"; legacyPath; "--out"; prior ]).Exit)
    let r = cli [ "reuse"; "--prior"; prior; "--handoff"; handoffPath; "--sdk-version"; "test-sdk"; "--legacy"; legacyPath ]
    Assert.Equal(ExitCode.PolicyFailure, r.Exit)
    let reasons = (json r).GetProperty("invalidatedBy").EnumerateArray() |> Seq.map (fun e -> str e "code") |> List.ofSeq
    Assert.Equal<string list>([ "config-changed" ], reasons)

[<Fact>]
let ``unparsable source is invalid input under the ast analyzer, never a silent pass`` () =
    let dir = tempDir ()
    let file = Path.Combine(dir, "Broken.fs")
    File.WriteAllText(file, "module Broken\n\nlet f x =\n    match x with\n    | ->\n")
    let r = cli [ "audit"; "--root"; dir; "--handoff"; handoffPath; "--legacy"; file ]
    Assert.Equal(ExitCode.InvalidInput, r.Exit)
    Assert.Contains("parse error", r.Stderr)

/// Rules must be general: the analyzer source may not name identifiers, scenario
/// values or record ids from the VAC-002-P3 development corpus.
[<Fact>]
let ``the ast analyzer source contains no development-corpus tokens`` () =
    let files =
        [ "src/RuleProvenance.Core/AstScanner.fs"
          "src/RuleProvenance.Core/AuthorityMatch.fs"
          "src/RuleProvenance.Core/Provenance.fs"
          "src/RuleProvenance.Core/Audit.fs" ]
    let words =
        [ "k1"; "k2"; "Nonprofit"; "nonprofit"; "Premium"; "premium"; "paracetamol"; "Patel"; "walk-in"
          "Standard"; "Student"; "Staff"; "ON"; "BC"; "QC"; "AB"; "Clinic"; "Billing"; "Lending"; "clinician"
          "slotFree"; "chased"; "lateFeeExempt"; "dispensedWithoutCheck"; "mayDispense"; "visitFee"; "shippingCharge"
          "taxRate"; "reminderLeadHours"; "paymentTermDays"; "bookingHorizonDays"; "lateFeeRate"; "finePerDay"; "fineCap" ]
    let values = [ "0.02"; "0.015"; "0.25"; "0.20"; "10.00"; "5.00"; "60.00"; "15.00"; "25.00"; "18.00"; "0.13"; "0.12"; "0.05" ]
    let idShapes = [ @"DEC-[BC]\d"; @"DEC-0\d\d"; @"BILL-\d"; @"CLIN-\d"; @"LEND-L\d" ]
    let patterns =
        (words |> List.map (fun w -> @"(?<![\w-])" + Regex.Escape w + @"(?![\w-])"))
        @ (values |> List.map (fun v -> @"(?<![\w.])" + Regex.Escape v + @"(?![\w])"))
        @ idShapes
    let hits =
        [ for f in files do
              let text = File.ReadAllText(path f)
              for p in patterns do
                  for m in Regex.Matches(text, p) do
                      yield $"{f}: '{m.Value}'" ]
    Assert.True(hits.IsEmpty, "corpus-specific tokens in analyzer source: " + System.String.Join("; ", hits))
