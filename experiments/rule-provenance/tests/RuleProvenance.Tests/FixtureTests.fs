module RuleProvenance.Tests.FixtureTests

open System.IO
open Xunit
open RuleProvenance.Tests.Support

[<Fact>]
let ``shared handoff fixture is the frozen fixture-v1 (sha256 pinned)`` () =
    Assert.Equal("ec5d0e6a4343b1774d40760c04af200a9b71147940305d1ddbabbc5c6db68bcd", sha256File handoffPath)

[<Fact>]
let ``shared legacy fixture is pinned`` () =
    Assert.Equal("b6550313398d2872bc6484b14dea79128cf2e1dcdb42a76ac676764259957280", sha256File legacyPath)

[<Fact>]
let ``copied handoff and traces conform to their contract schemas`` () =
    let code, output = validateSchema (path "contracts/schemas/echelon-authority-handoff-0.1.schema.json") [ handoffPath ]
    Assert.True((code = 0), output)
    let code, output =
        validateSchema
            (path "contracts/schemas/echelon-construction-trace-0.1.schema.json")
            [ path "fixtures/constructed/clean/construction-trace.json"; path "fixtures/probe-standin/construction-trace.json" ]
    Assert.True((code = 0), output)

[<Fact>]
let ``stand-in trace output digests match the stand-in sources`` () =
    let trace = File.ReadAllText(path "fixtures/probe-standin/construction-trace.json")
    for f in [ "src/Domain.fs"; "src/Probe.fs" ] do
        Assert.Contains("sha256:" + sha256File (path ("fixtures/probe-standin/" + f)), trace)
