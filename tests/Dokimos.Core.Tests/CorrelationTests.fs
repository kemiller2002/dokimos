namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module CorrelationTests =
    [<Fact>]
    let ``complexity alone does not become a maintainability hotspot`` () =
        Assert.Empty(Correlation.evaluate "A.fs" [StructuralComplexity 14])

    [<Fact>]
    let ``complexity plus change pressure produces explainable hotspot evidence`` () =
        let result = Correlation.evaluate "A.fs" [StructuralComplexity 14; FrequentChange 9]
        Assert.Contains(result, fun x -> x.Kind = MaintainabilityHotspot)

    [<Fact>]
    let ``agent risk pattern requires multiple independent signals`` () =
        let result = Correlation.evaluate "A.fs" [TypeWeakening 2; MissingTestChange]
        Assert.Contains(result, fun x -> x.Kind = AgentGeneratedRiskPattern)
        Assert.All(result, fun x -> Assert.NotEmpty(x.Signals))
