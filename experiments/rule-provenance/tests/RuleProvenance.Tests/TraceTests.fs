module RuleProvenance.Tests.TraceTests

open System.IO
open Xunit
open RuleProvenance.Tests.Support

let private cleanDir = path "fixtures/constructed/clean"
let private cleanTrace = File.ReadAllText(Path.Combine(cleanDir, "construction-trace.json"))

/// Audit the clean sample against a modified trace and return the findings.
let private withTrace (trace: string) =
    let dir = tempDir ()
    File.Copy(Path.Combine(cleanDir, "InvoiceRules.fs"), Path.Combine(dir, "InvoiceRules.fs"))
    File.WriteAllText(Path.Combine(dir, "construction-trace.json"), trace)
    let r = audit [ "--root"; dir; "--source"; Path.Combine(dir, "InvoiceRules.fs"); "--trace"; Path.Combine(dir, "construction-trace.json") ]
    findings (json r)

let private classes fs = fs |> List.map (fun f -> str f "class")

[<Fact>]
let ``trace authority that disagrees with the source marker is a trace-mismatch`` () =
    let fs = withTrace (cleanTrace.Replace("\"symbol\": \"Sample.Invoice.Rules.singlePaymentLimit\", \"kind\": \"constant\", \"file\": \"InvoiceRules.fs\", \"disposition\": \"authorized\", \"authority\": [ { \"kind\": \"decision\", \"id\": \"DEC-FIN-001\" } ]", "\"symbol\": \"Sample.Invoice.Rules.singlePaymentLimit\", \"kind\": \"constant\", \"file\": \"InvoiceRules.fs\", \"disposition\": \"authorized\", \"authority\": [ { \"kind\": \"requirement\", \"id\": \"REQ-INV-002\" } ]"))
    Assert.Equal<string list>([ "trace-mismatch" ], classes fs)
    Assert.Equal("proven", str fs.Head "certainty")

[<Fact>]
let ``a traced symbol missing from source and a marked symbol missing from the trace are both reported`` () =
    let fs = withTrace (cleanTrace.Replace("Sample.Invoice.Rules.validatePayment", "Sample.Invoice.Rules.validatePaymentRenamed"))
    Assert.Equal<string list>([ "trace-mismatch"; "trace-mismatch" ], classes fs)
    let messages = fs |> List.map (fun f -> str f "message") |> String.concat "\n"
    Assert.Contains("is not declared in the audited source", messages)
    Assert.Contains("absent from the construction trace", messages)

[<Fact>]
let ``a trace built from another handoff is stale`` () =
    let fs = withTrace (cleanTrace.Replace("ec5d0e6a4343b1774d40760c04af200a9b71147940305d1ddbabbc5c6db68bcd", "0000000000000000000000000000000000000000000000000000000000000000"))
    Assert.Equal<string list>([ "trace-mismatch" ], classes fs)
    Assert.Contains("stale", str fs.Head "message")

[<Fact>]
let ``a trace that marks a question-governed construct authorized encodes the question as a rule`` () =
    let fs = withTrace (cleanTrace.Replace("\"disposition\": \"deferred-pending-question\"", "\"disposition\": \"authorized\""))
    Assert.Contains("unresolved-question-encoded-as-rule", classes fs)

[<Fact>]
let ``a trace citing a superseded decision is reported even when the source is clean`` () =
    let fs = withTrace (cleanTrace.Replace("\"symbol\": \"Sample.Invoice.Rules.singlePaymentLimit\", \"kind\": \"constant\", \"file\": \"InvoiceRules.fs\", \"disposition\": \"authorized\", \"authority\": [ { \"kind\": \"decision\", \"id\": \"DEC-FIN-001\" } ]", "\"symbol\": \"Sample.Invoice.Rules.singlePaymentLimit\", \"kind\": \"constant\", \"file\": \"InvoiceRules.fs\", \"disposition\": \"authorized\", \"authority\": [ { \"kind\": \"decision\", \"id\": \"DEC-FIN-000\" } ]"))
    Assert.Contains("superseded-authority", classes fs)
    Assert.Contains("trace-mismatch", classes fs)

[<Fact>]
let ``stale output digests in the trace are reported`` () =
    let dir = tempDir ()
    let src = path "fixtures/probe-standin/src"
    Directory.CreateDirectory(Path.Combine(dir, "src")) |> ignore
    for f in Directory.GetFiles src do
        File.Copy(f, Path.Combine(dir, "src", Path.GetFileName f |> nonNull))
    File.AppendAllText(Path.Combine(dir, "src", "Probe.fs"), "\nlet unused = ()\n")
    File.Copy(path "fixtures/probe-standin/construction-trace.json", Path.Combine(dir, "construction-trace.json"))
    let r = audit [ "--root"; dir; "--source"; Path.Combine(dir, "src"); "--trace"; Path.Combine(dir, "construction-trace.json") ]
    let fs = findings (json r)
    Assert.Equal<string list>([ "trace-mismatch" ], classes fs)
    Assert.Equal("src/Probe.fs", str (fs.Head.GetProperty "location") "path")

[<Fact>]
let ``stand-in source agrees with its trace (technical ports need no marker)`` () =
    let s = path "fixtures/probe-standin"
    let r = audit [ "--root"; s; "--source"; Path.Combine(s, "src"); "--trace"; Path.Combine(s, "construction-trace.json") ]
    Assert.Empty(findings (json r))
    // Without the trace the Probe ports' branches are unmarked rule-bearing constructs.
    let r2 = audit [ "--root"; s; "--source"; Path.Combine(s, "src") ]
    let symbols = findings (json r2) |> List.map (fun f -> str (f.GetProperty "location") "symbol") |> List.sort
    Assert.Equal<string list>([ "Invoice.Slice.Probe.stateName"; "Invoice.Slice.Probe.step" ], symbols)
