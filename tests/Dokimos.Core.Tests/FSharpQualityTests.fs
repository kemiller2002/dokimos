namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module FSharpQualityTests =
    [<Fact>]
    let ``quality analyzer preserves indicators instead of declaring defects`` () =
        let source = "let f (x: obj) =\n  match x with\n  | _ -> failwith \"TODO\"\n"
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
