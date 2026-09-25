namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module StructuralTests =
    [<Fact>]
    let ``source metrics are deterministic for identical source`` () =
        let source = "module A\n\nlet mutable x = 1\ntype T = T\n"
        let first = Structural.measure "A.fs" source
        let second = Structural.measure "A.fs" source
        Assert.Equal(first, second)
        Assert.Equal(4, first.NonBlankLines)
        Assert.Equal(2, first.PublicDeclarations)
        Assert.Equal(1, first.MutableBindings)

    [<Fact>]
    let ``broad catch is indicator not asserted defect`` () =
        let result = Structural.measure "A.fs" "match x with\n| _ -> fallback"
        Assert.Equal(1, result.BroadCatchIndicators)
