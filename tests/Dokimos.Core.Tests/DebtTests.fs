namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

module DebtTests =
    [<Fact>]
    let ``debt age is historical evidence not current line count`` () =
        let first = DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero)
        let last = DateTimeOffset(2026,2,1,0,0,0,TimeSpan.Zero)
        let marker = { Path="A.fs";Kind=Todo;Anchor="TODO:x";FirstSeen=first;LastSeen=last;Present=true }
        let result = Debt.age (DateTimeOffset(2026,3,1,0,0,0,TimeSpan.Zero)) marker
        Assert.Equal(59, result.AgeDays)
        Assert.Equal(28, result.StalenessDays)

    [<Fact>]
    let ``production change without test change is an indicator`` () =
        let result = Tests.distribution 10 4 2 0
        Assert.True(result.UntestedChangeIndicator)
