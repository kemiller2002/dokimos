namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Domain
open Dokimos.Core
open Fixtures

module HistoryTests =
    let at (day: int) (s: CanonicalSnapshot) = { s with CollectedAt = DateTimeOffset.UnixEpoch.AddDays(float day) }

    let series =
        [ snapshot "s1" [ metric "source.mutable-bindings" 3M; metric "complexity.proxy-cyclomatic" 4M ] [ finding "f" ] |> at 1
          snapshot "s2" [ metric "source.mutable-bindings" 1M; metric "complexity.proxy-cyclomatic" 6M ] [] |> at 2
          snapshot "s3" [ metric "source.mutable-bindings" 2M; CanonicalMetric.unavailable "complexity.proxy-cyclomatic" "A.fs" "points" NotConfigured "t" ] [ finding "f" ] |> at 3 ]

    [<Fact>]
    let ``metric history reports first, last, best and baseline distance`` () =
        let s = History.metricHistory (Some series[0]) "source.mutable-bindings" None series |> List.exactlyOne
        Assert.Equal(3, s.Points.Length)
        Assert.Equal("s1", s.FirstSeen.Value.SnapshotId)
        Assert.Equal("s3", s.LastSeen.Value.SnapshotId)
        Assert.Equal("s2", s.Best.Value.SnapshotId)
        Assert.Equal(Some -1M, s.BaselineDistance)

    [<Fact>]
    let ``contextual metrics claim no best state and unavailable points stay unavailable`` () =
        let s = History.metricHistory None "complexity.proxy-cyclomatic" None series |> List.exactlyOne
        Assert.Equal(None, s.Best)
        Assert.Equal("s2", s.LastSeen.Value.SnapshotId)
        Assert.Equal(Unavailable NotConfigured, s.Latest.Value.Measurement)
        Assert.Equal(None, s.BaselineDistance)

    [<Fact>]
    let ``incompatible definition versions are excluded from best state`` () =
        let versioned =
            [ snapshot "old" [ { metric "source.mutable-bindings" 0M with MetricVersion = 1 } ] [] |> at 1
              snapshot "new" [ { metric "source.mutable-bindings" 5M with MetricVersion = 2 } ] [] |> at 2 ]
        let s = History.metricHistory None "source.mutable-bindings" None versioned |> List.exactlyOne
        Assert.Equal<int list>([ 1 ], s.IncompatibleVersions)
        Assert.NotEqual(Some "old", s.Best |> Option.map _.SnapshotId)

    [<Fact>]
    let ``finding lifecycle survives snapshots and detects resurfacing`` () =
        let f = History.findingHistories series |> List.exactlyOne
        let states = f.Timeline |> List.map _.State
        Assert.Equal<FindingLifecycleState list>([ LifecycleIntroduced; LifecycleResolved; LifecycleResurfaced ], states)
        Assert.Equal(Some "s1", f.FirstSeen)
        Assert.Equal(Some "s3", f.LastSeen)

    [<Fact>]
    let ``persistent after resurfacing is persistent, not resurfaced again`` () =
        let longer = series @ [ snapshot "s4" [ metric "source.mutable-bindings" 2M ] [ finding "f" ] |> at 4 ]
        let f = History.findingHistories longer |> List.exactlyOne
        Assert.Equal(LifecyclePersistent, f.Current)

    [<Fact>]
    let ``absence without temporal evidence is unavailable, not resolved`` () =
        let noHistory = Fixtures.producer (Capabilities.analyzers |> List.map (fun a -> if a.AnalyzerId = Capabilities.GitTemporal then notRun a.AnalyzerId else ran a.AnalyzerId))
        let snaps = [ snapshot "s1" [] [ finding "f" ] |> at 1; snapshotWith (Some noHistory) "s2" [] [] |> at 2 ]
        let f = History.findingHistories snaps |> List.exactlyOne
        Assert.Equal(LifecycleUnavailable, f.Current)

    [<Fact>]
    let ``a finding that moves with its file is uncertain, never asserted`` () =
        let atPath path id : CanonicalFinding = { finding id with Scope = path }
        let snaps =
            [ snapshot "s1" [ metricAt "Old.fs" "source.lines" 5M ] [ atPath "Old.fs" "correlation:maintainability-hotspot:Old.fs" ] |> at 1
              snapshot "s2" [ metricAt "New.fs" "source.lines" 5M ] [ atPath "New.fs" "correlation:maintainability-hotspot:New.fs" ] |> at 2 ]
        let histories = History.findingHistories snaps
        for h in histories do
            match h.Current with
            | LifecycleUncertain _ -> ()
            | other -> failwith $"{h.FindingId}: expected uncertain, got {other}"
