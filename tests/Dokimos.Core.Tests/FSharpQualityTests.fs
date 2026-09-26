namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module FSharpQualityTests =
    [<Fact>]
    let ``quality analyzer preserves indicators instead of declaring defects`` () =
        let source = "let f (x: obj) = // TODO remove weak boundary\n  match x with\n  | _ -> failwith \"bad input\"\n"
        let structural = Structural.measure "A.fs" source
        let quality = FSharpQuality.measure "A.fs" source
        let agent = AgentQuality.fromMetrics structural quality
        Assert.Equal(1, quality.ObjTypeIndicators)
        Assert.Equal(1, quality.ExceptionRaiseIndicators)
        Assert.Equal(1, quality.TodoIndicators)
        Assert.Equal(1, agent.TypeWeakeningIndicators)
        Assert.Equal(1, agent.BroadCatchIndicators)

    [<Fact>]
    let ``result and option usage are observable without being scored`` () =
        let quality = FSharpQuality.measure "A.fs" "let x : Result<int,string> = Ok 1"
        Assert.True(quality.OptionResultIndicators > 0)


    [<Fact>]
    let ``quality indicators ignore analyzer vocabulary inside string literals`` () =
        let source = "let objPattern = \": obj\"\nlet todoPattern = \"TODO\""
        let quality = FSharpQuality.measure "Analyzer.fs" source
        Assert.Equal(0, quality.ObjTypeIndicators)
        Assert.Equal(0, quality.TodoIndicators)

    [<Fact>]
    let ``quality indicators still observe code outside string literals`` () =
        let source = "let f (value: obj) = value // TODO remove weak boundary"
        let quality = FSharpQuality.measure "A.fs" source
        Assert.Equal(1, quality.ObjTypeIndicators)
        Assert.Equal(1, quality.TodoIndicators)
