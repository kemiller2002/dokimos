namespace Dokimos.Core.Tests

open System.Text.Json
open Xunit
open Dokimos.Core

module WireTests =
    [<Fact>]
    let ``wire signal uses stable explicit discriminators`` () =
        let dto = Wire.signal (StructuralComplexity 12)
        Assert.Equal("structural-complexity",dto.Kind)
        Assert.Equal(Some 12,dto.Value)

    [<Fact>]
    let ``repository wire model serializes without FSharp union support`` () =
        let analysis = RepositoryAnalysis.analyze 3 Map.empty [("A.fs","let x = 1")]
        let dto = Wire.repository analysis
        let json = JsonSerializer.Serialize(dto)
        Assert.Contains("\"SchemaVersion\":\"1.0.0\"",json)
        Assert.Contains("\"Sources\"",json)

    [<Fact>]
    let ``valueless signals remain explicit and do not invent numeric zero`` () =
        let dto = Wire.signal MissingTestChange
        Assert.Equal("missing-test-change",dto.Kind)
        Assert.Equal(None,dto.Value)
