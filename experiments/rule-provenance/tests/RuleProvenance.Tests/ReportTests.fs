module RuleProvenance.Tests.ReportTests

open System.IO
open System.Text.RegularExpressions
open Xunit
open RuleProvenance
open RuleProvenance.Tests.Support

/// A spread of reports covering every finding class and invariant verdict.
let private scenarios () =
    let s = path "fixtures/probe-standin"
    let constructed name = [ "--root"; path ("fixtures/constructed/" + name); "--source"; path ("fixtures/constructed/" + name + "/InvoiceRules.fs") ]
    [ "legacy-reference", [ "--legacy"; legacyPath; "--probe-model"; "reference" ]
      "mutant-drop-limit", [ "--legacy"; legacyPath; "--probe-model"; "mutant:drop-limit" ]
      "p3-legacy", [ "--legacy"; path "fixtures/legacy-reuse" ]
      "clean-traced", constructed "clean" @ [ "--trace"; path "fixtures/constructed/clean/construction-trace.json" ]
      "superseded", constructed "superseded"
      "unmarked", constructed "unmarked-constant"
      "question", constructed "question-as-rule"
      "observation", constructed "cites-observation"
      "standin-integrate", [ "--source"; Path.Combine(s, "src"); "--trace"; Path.Combine(s, "construction-trace.json"); "--dll"; standinDll; "--legacy"; legacyPath ] ]

[<Fact>]
let ``every report conforms to echelon-provenance-audit-0.1 (jsonschema)`` () =
    let dir = tempDir ()
    let files =
        scenarios ()
        |> List.map (fun (name, args) ->
            let out = Path.Combine(dir, name + ".json")
            let r = audit (args @ [ "--out"; out ])
            Assert.True(r.Exit = 0 || r.Exit = 4, $"{name}: exit {r.Exit} {r.Stderr}")
            out)
    let code, output = validateSchema auditSchema files
    Assert.True((code = 0), output)
    Assert.Equal(files.Length, Regex.Matches(output, "valid: ").Count)

[<Fact>]
let ``reruns are byte-identical, LF-only and carry no timestamps`` () =
    for (name, args) in scenarios () do
        let a = audit args
        let b = audit args
        Assert.True((a.Stdout = b.Stdout), $"{name} differs between runs")
        Assert.DoesNotContain("\r", a.Stdout)
        Assert.EndsWith("}\n", a.Stdout)
        Assert.False(Regex.IsMatch(a.Stdout, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}"), $"{name} contains a timestamp")

[<Fact>]
let ``input order does not change the report`` () =
    let a = audit [ "--legacy"; path "fixtures/legacy-reuse/CurrencyLimits.fs"; "--legacy"; path "fixtures/legacy-reuse/PaymentValidation.fs"; "--legacy"; legacyPath ]
    let b = audit [ "--legacy"; legacyPath; "--legacy"; path "fixtures/legacy-reuse/PaymentValidation.fs"; "--legacy"; path "fixtures/legacy-reuse/CurrencyLimits.fs" ]
    Assert.Equal(a.Stdout, b.Stdout)

[<Fact>]
let ``traceDigest is the digest of the empty string when no trace is supplied`` () =
    let v = (json (audit [ "--legacy"; legacyPath ])).GetProperty "validity"
    Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", str v "traceDigest")
    Assert.Equal("sha256:ec5d0e6a4343b1774d40760c04af200a9b71147940305d1ddbabbc5c6db68bcd", str v "handoffDigest")
    Assert.Equal(Tool.Version, str v "toolVersion")
    Assert.Equal("test-sdk", str v "sdkVersion")

[<Fact>]
let ``the checker validation report states the kill rate`` () =
    let r = cli [ "validate-checker"; "--handoff"; handoffPath ]
    Assert.Equal(ExitCode.Continue, r.Exit)
    let doc = json r
    Assert.Equal("7/7", str doc "killRate")
    Assert.True(doc.GetProperty("validated").GetBoolean())

[<Fact>]
let ``exit codes follow Dokimos conventions`` () =
    Assert.Equal(ExitCode.InvalidInput, (cli [ "audit" ]).Exit)
    Assert.Equal(ExitCode.InvalidInput, (cli [ "audit"; "--handoff"; path "missing.json" ]).Exit)
    Assert.Equal(ExitCode.InvalidInput, (audit [ "--bogus"; "x" ]).Exit)
    Assert.Equal(ExitCode.InvalidInput, (audit [ "--source"; path "fixtures/nope" ]).Exit)
    let bad = Path.Combine(tempDir (), "bad.json")
    File.WriteAllText(bad, "{ \"schema\": \"something-else\" }")
    Assert.Equal(ExitCode.InvalidInput, (cli [ "audit"; "--handoff"; bad ]).Exit)
    Assert.Equal(ExitCode.InvalidInput, (audit [ "--trace"; bad ]).Exit)
    Assert.Equal(ExitCode.EvidenceUnavailable, (audit [ "--dll"; path "fixtures/missing/Invoice.Slice.dll" ]).Exit)
    Assert.Equal(ExitCode.EvidenceUnavailable, (audit [ "--dll"; typeof<AuditConfig>.Assembly.Location ]).Exit)
    Assert.Equal(ExitCode.InvalidInput, (cli [ "integrate"; "--handoff"; handoffPath ]).Exit)
