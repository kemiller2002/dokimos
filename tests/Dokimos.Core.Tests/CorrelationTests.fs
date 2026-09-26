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

    [<Fact>]
    let ``agent risk pattern is a heuristic and never produces provenance or authorship`` () =
        let result = Correlation.evaluate "A.fs" [TypeWeakening 2; DependencyAdditions 1; MissingTestChange]
        let pattern = result |> List.find (fun x -> x.Kind = AgentGeneratedRiskPattern)
        let fieldTypes =
            Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(typeof<CorrelatedEvidence>)
            |> Array.map (fun field -> field.PropertyType)
        for forbidden in [ typeof<Dokimos.Domain.Actor>; typeof<Dokimos.Domain.Contribution>; typeof<Json>; typeof<ProvenanceVerdict>; typeof<RecordAttribution> ] do
            Assert.DoesNotContain(forbidden, fieldTypes)
        Assert.DoesNotContain("author", pattern.Explanation.ToLowerInvariant())
        Assert.DoesNotContain("agent", pattern.Explanation.ToLowerInvariant())
