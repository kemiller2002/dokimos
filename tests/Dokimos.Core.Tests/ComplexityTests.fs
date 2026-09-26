namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module ComplexityTests =
    [<Fact>]
    let ``complexity proxy exposes its contributing dimensions`` () =
        let source = "let f x =\n    if x > 0 && x < 10 then\n        match x with\n        | 1 -> true\n        | _ -> false"
        let result = Complexity.measure "A.fs" source
        Assert.Equal(1, result.DecisionPoints)
        Assert.Equal(1, result.BooleanOperators)
        Assert.Equal(2, result.MatchBranches)
        Assert.Equal(5, result.ProxyCyclomatic)

    [<Fact>]
    let ``duplicate blocks preserve locations rather than returning only a percentage`` () =
        let block = "let a = 1\nlet b = 2\nlet c = a + b"
        let result = Duplication.blocks 3 [("A.fs",block);("B.fs",block)]
        Assert.Single(result) |> ignore
        Assert.Equal(2, result.Head.Occurrences)
        Assert.Equal(2, result.Head.Paths.Length)
