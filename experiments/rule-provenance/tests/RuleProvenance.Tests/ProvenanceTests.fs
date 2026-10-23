module RuleProvenance.Tests.ProvenanceTests

open System.IO
open Xunit
open RuleProvenance
open RuleProvenance.Tests.Support

// ---------------------------------------------------------------- (a) shared invoice fixture, legacy mode

[<Fact>]
let ``shared legacy file yields exactly OBS-LEGACY-001 and OBS-LEGACY-002 as non-blocking owner questions for finance-controller`` () =
    let r = audit [ "--legacy"; legacyPath ]
    Assert.Equal(ExitCode.Continue, r.Exit)
    let doc = json r
    let fs = findings doc
    Assert.Equal(2, fs.Length)
    let byObs id = fs |> List.find (fun f -> refs f |> List.contains id)
    let exemption = byObs "OBS-LEGACY-001"
    let superseded = byObs "OBS-LEGACY-002"
    Assert.Equal("unverified-inherited-rule", str exemption "class")
    Assert.Equal("superseded-authority", str superseded "class")
    Assert.Contains("DEC-FIN-000", refs superseded)
    for f in fs do
        Assert.Equal("uncertain-authority", str f "certainty")
        Assert.False(f.GetProperty("blocking").GetBoolean())
        Assert.Equal("finance-controller", str f "owner")
    Assert.Equal("Legacy.PaymentRules.exceedsLimit", str (exemption.GetProperty "location") "symbol")
    Assert.Equal("Legacy.PaymentRules.singlePaymentLimit", str (superseded.GetProperty "location") "symbol")
    let summary = doc.GetProperty "summary"
    Assert.Equal("pass-with-owner-questions", str summary "verdict")
    Assert.Equal(0, summary.GetProperty("proven").GetInt32())
    Assert.Equal(2, summary.GetProperty("ownerQuestions").GetInt32())

[<Fact>]
let ``legacy files are never modified by the audit`` () =
    let files = [ legacyPath; path "fixtures/legacy-reuse/CurrencyLimits.fs"; path "fixtures/legacy-reuse/PaymentValidation.fs" ]
    let before = files |> List.map (fun f -> File.ReadAllBytes f)
    let stamps = files |> List.map File.GetLastWriteTimeUtc
    audit [ "--legacy"; legacyPath; "--legacy"; path "fixtures/legacy-reuse"; "--probe-model"; "reference" ] |> ignore
    audit [ "--legacy"; legacyPath; "--block-on-proven-provenance" ] |> ignore
    let after = files |> List.map (fun f -> File.ReadAllBytes f)
    List.iter2 (fun (b: byte[]) (a: byte[]) -> Assert.Equal<byte[]>(b, a)) before after
    Assert.Equal<System.DateTime list>(stamps, files |> List.map File.GetLastWriteTimeUtc)
    Assert.Equal("b6550313398d2872bc6484b14dea79128cf2e1dcdb42a76ac676764259957280", sha256File legacyPath)

[<Fact>]
let ``uncertain-authority findings never block, even with the proven-provenance flag`` () =
    let r = audit [ "--legacy"; legacyPath; "--block-on-proven-provenance" ]
    Assert.Equal(ExitCode.Continue, r.Exit)
    Assert.All(findings (json r), fun f -> Assert.False(f.GetProperty("blocking").GetBoolean()))

// ---------------------------------------------------------------- (b) constructed samples

let private constructed name extra =
    let dir = path ("fixtures/constructed/" + name)
    audit ([ "--root"; dir; "--source"; Path.Combine(dir, "InvoiceRules.fs") ] @ extra)

[<Fact>]
let ``clean constructed sample passes with and without its trace`` () =
    for extra in [ []; [ "--trace"; path "fixtures/constructed/clean/construction-trace.json" ] ] do
        let r = constructed "clean" extra
        Assert.Equal(ExitCode.Continue, r.Exit)
        let doc = json r
        Assert.Empty(findings doc)
        Assert.Equal("pass", str (doc.GetProperty "summary") "verdict")

let private single name =
    let r = constructed name []
    let fs = findings (json r)
    Assert.True((fs.Length = 1), $"{name}: expected one finding, got {fs.Length}: {r.Stdout}")
    r, fs.Head

[<Fact>]
let ``citing superseded DEC-FIN-000 is a proven superseded-authority finding`` () =
    let r, f = single "superseded"
    Assert.Equal("superseded-authority", str f "class")
    Assert.Equal("proven", str f "certainty")
    Assert.Equal<string list>([ "DEC-FIN-000"; "DEC-FIN-001" ], refs f)
    Assert.False(f.GetProperty("blocking").GetBoolean())
    Assert.Equal(ExitCode.Continue, r.Exit)

[<Fact>]
let ``an unmarked 25000 constant is proven missing-provenance and names the superseded value`` () =
    let _, f = single "unmarked-constant"
    Assert.Equal("missing-provenance", str f "class")
    Assert.Equal("proven", str f "certainty")
    Assert.Equal("Sample.Invoice.Rules.strategicLimit", str (f.GetProperty "location") "symbol")
    Assert.Contains("DEC-FIN-000", refs f)
    Assert.Contains("25000.00 equals DEC-FIN-000.limit", str f "message")

[<Fact>]
let ``encoding Q-FIN-001 as allowed is unresolved-question-encoded-as-rule`` () =
    let _, f = single "question-as-rule"
    Assert.Equal("unresolved-question-encoded-as-rule", str f "class")
    Assert.Equal<string list>([ "Q-FIN-001" ], refs f)
    Assert.Equal("Sample.Invoice.Rules.voidInvoice", str (f.GetProperty "location") "symbol")

[<Fact>]
let ``citing an observation id is proven missing-provenance`` () =
    let _, f = single "cites-observation"
    Assert.Equal("missing-provenance", str f "class")
    Assert.Equal("proven", str f "certainty")
    Assert.Equal<string list>([ "OBS-LEGACY-001" ], refs f)

[<Fact>]
let ``citing a proposed decision is self-authorized`` () =
    let dir = tempDir ()
    let handoff = File.ReadAllText(handoffPath)
    // DEC-FIN-002 turned into an agent proposal: no authorizedBy is required for proposed.
    let proposed = handoff.Replace("\"statement\": \"Only the payments-service role may write payments. Writes accept only validated, applied payments.\",\n      \"status\": \"accepted\"", "\"statement\": \"Only the payments-service role may write payments. Writes accept only validated, applied payments.\",\n      \"status\": \"proposed\"")
    Assert.NotEqual<string>(handoff, proposed)
    File.WriteAllText(Path.Combine(dir, "handoff.json"), proposed)
    File.WriteAllText(Path.Combine(dir, "Writes.fs"), "module W\n\n[<Authority(\"DEC-FIN-002\")>]\nlet canWrite (role: string) = role = \"payments-service\"\n")
    let r = cli [ "audit"; "--root"; dir; "--handoff"; Path.Combine(dir, "handoff.json"); "--source"; Path.Combine(dir, "Writes.fs") ]
    let fs = findings (json r)
    Assert.Single fs |> ignore
    Assert.Equal("self-authorized", str fs.Head "class")
    Assert.Equal("proven", str fs.Head "certainty")

[<Fact>]
let ``the proven-provenance flag makes proven findings blocking and the audit fail (exit 4)`` () =
    let r = constructed "superseded" [ "--block-on-proven-provenance" ]
    Assert.Equal(ExitCode.PolicyFailure, r.Exit)
    let doc = json r
    Assert.True((findings doc).Head.GetProperty("blocking").GetBoolean())
    Assert.Equal("fail", str (doc.GetProperty "summary") "verdict")

// ---------------------------------------------------------------- (c) VAC-002-P3 code-reuse errors in legacy mode

let private legacyReuse file =
    let r = audit [ "--legacy"; path ("fixtures/legacy-reuse/" + file) ]
    Assert.Equal(ExitCode.Continue, r.Exit)
    let fs = findings (json r)
    Assert.True((fs.Length = 1), r.Stdout)
    fs.Head

[<Fact>]
let ``P3 silent 0.00 default for an unlisted case is an owner question`` () =
    let f = legacyReuse "CurrencyLimits.fs"
    Assert.Equal("unverified-inherited-rule", str f "class")
    Assert.Equal("uncertain-authority", str f "certainty")
    Assert.False(f.GetProperty("blocking").GetBoolean())
    Assert.Equal("finance-controller", str f "owner")
    Assert.Contains("wildcard default", str f "message")

[<Fact>]
let ``P3 C06 exemption framed as operational fact inside the accepted-rule function is an owner question`` () =
    let f = legacyReuse "PaymentValidation.fs"
    Assert.Equal("unverified-inherited-rule", str f "class")
    Assert.Equal("uncertain-authority", str f "certainty")
    Assert.False(f.GetProperty("blocking").GetBoolean())
    Assert.Equal("finance-controller", str f "owner")
    Assert.Contains("DEC-FIN-001", refs f)
    Assert.Contains("\"PRT-\"", str f "message")
    Assert.Contains("exemption from DEC-FIN-001 needs its own decision record", str f "message")

[<Fact>]
let ``the same exemption in constructed mode is a proven missing-provenance finding, not an owner question`` () =
    let r = audit [ "--source"; path "fixtures/legacy-reuse/PaymentValidation.fs" ]
    let fs = findings (json r)
    Assert.Single fs |> ignore
    Assert.Equal("missing-provenance", str fs.Head "class")
    Assert.Equal("proven", str fs.Head "certainty")
