namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Domain
open Dokimos.Core

module FindingTests =
    [<Fact>]
    let ``present finding becoming absent is resolved`` () =
        Assert.Equal(Some Resolved, Findings.transition (Present(Some 12m)) Absent)

    [<Fact>]
    let ``unknown evidence does not invent a lifecycle transition`` () =
        Assert.True(Findings.transition (Present(Some 12m)) Unknown |> Option.isNone)

    [<Fact>]
    let ``return of resolved finding is resurfaced`` () =
        Assert.Equal(Some Resurfaced, Findings.resurfaced true (Present None))
