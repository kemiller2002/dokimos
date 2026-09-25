namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

module TemporalTests =
    let at day = DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero)

    [<Fact>]
    let ``temporal evidence aggregates churn and unique commits`` () =
        let changes =
            [ { Path = "A.fs"; Commit = "a"; ChangedAt = at 1; Additions = 30; Deletions = 10; PreviousPath = None }
              { Path = "A.fs"; Commit = "b"; ChangedAt = at 2; Additions = 50; Deletions = 20; PreviousPath = None } ]
        let evidence = Temporal.summarize "A.fs" changes |> Option.get
        Assert.Equal(2, evidence.CommitCount)
        Assert.Equal(110, evidence.Churn)

    [<Fact>]
    let ``rename evidence is retained`` () =
        let changes =
            [ { Path = "New.fs"; Commit = "b"; ChangedAt = at 2; Additions = 2; Deletions = 1; PreviousPath = Some "Old.fs" } ]
        let evidence = Temporal.summarize "Old.fs" changes |> Option.get
        Assert.Equal(1, evidence.RenameCount)

    [<Fact>]
    let ``hotspot explanation preserves contributing dimensions`` () =
        let temporal =
            { Path = "A.fs"; CommitCount = 8; Additions = 100; Deletions = 40; Churn = 140
              FirstChange = at 1; LastChange = at 9; RenameCount = 0 }
        let structural = { Complexity = Some 14m; Size = Some 200m; FindingCount = Some 2 }
        let hotspot = Hotspots.explain temporal structural
        Assert.Contains("frequently-changed", hotspot.Reasons)
        Assert.Contains("high-churn", hotspot.Reasons)
        Assert.Contains("high-complexity", hotspot.Reasons)
        Assert.Contains("active-findings", hotspot.Reasons)
