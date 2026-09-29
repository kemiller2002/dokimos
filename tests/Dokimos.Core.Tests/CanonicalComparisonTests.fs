namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Domain
open Dokimos.Core
open Fixtures

module CanonicalComparisonTests =
    let kindOf metricId (result: CanonicalComparison) =
        result.MetricChanges |> List.find (fun x -> x.MetricId = metricId) |> _.Kind

    [<Fact>]
    let ``directional metric can improve while contextual metric merely changes`` () =
        let before = snapshot "a" [ metric "source.mutable-bindings" 2M; metric "complexity.proxy-cyclomatic" 10M ] []
        let after = snapshot "b" [ metric "source.mutable-bindings" 1M; metric "complexity.proxy-cyclomatic" 12M ] []
        let result = CanonicalComparison.compare before after
        Assert.Equal(MetricImproved, kindOf "source.mutable-bindings" result)
        Assert.Equal(MetricChanged, kindOf "complexity.proxy-cyclomatic" result)

    [<Fact>]
    let ``every metric change state is reachable`` () =
        let failed = CanonicalMetric.failed "tests.failed" "A.fs" "count" { Code = "trx-malformed"; Message = "m" } "test"
        let unavailable = CanonicalMetric.unavailable "coverage.line-rate" "A.fs" "ratio" NotConfigured "test"
        let before =
            snapshot "a"
                [ metric "source.mutable-bindings" 2M
                  metric "quality.type-weakening-indicators" 1M
                  metric "source.lines" 10M
                  metric "complexity.proxy-cyclomatic" 4M
                  metric "source.broad-catch-indicators" 1M
                  { metric "source.nonblank-lines" 3M with MetricVersion = 1 }
                  metric "tests.failed" 0M
                  CanonicalMetric.available "coverage.line-rate" "A.fs" "ratio" 0.5M "test" ]
                []
        let after =
            snapshot "b"
                [ metric "source.mutable-bindings" 1M
                  metric "quality.type-weakening-indicators" 3M
                  metric "source.lines" 10M
                  metric "complexity.proxy-cyclomatic" 9M
                  { metric "source.nonblank-lines" 3M with MetricVersion = 2 }
                  failed
                  unavailable
                  metric "source.public-declarations" 2M ]
                []
        let result = CanonicalComparison.compare before after
        Assert.Equal(MetricImproved, kindOf "source.mutable-bindings" result)
        Assert.Equal(MetricDeteriorated, kindOf "quality.type-weakening-indicators" result)
        Assert.Equal(MetricUnchanged, kindOf "source.lines" result)
        Assert.Equal(MetricChanged, kindOf "complexity.proxy-cyclomatic" result)
        Assert.Equal(MetricRemoved, kindOf "source.broad-catch-indicators" result)
        Assert.Equal(MetricIncompatible, kindOf "source.nonblank-lines" result)
        Assert.Equal(MetricUnavailable, kindOf "tests.failed" result)
        Assert.Equal(MetricUnavailable, kindOf "coverage.line-rate" result)
        Assert.Equal(MetricAdded, kindOf "source.public-declarations" result)

    [<Fact>]
    let ``unavailable and failed evidence carry no delta and no number`` () =
        let before = snapshot "a" [ metric "tests.failed" 0M ] []
        let after = snapshot "b" [ CanonicalMetric.unavailable "tests.failed" "A.fs" "count" NotConfigured "test" ] []
        let change = (CanonicalComparison.compare before after).MetricChanges |> List.exactlyOne
        Assert.Equal(None, change.Delta)
        Assert.Equal(Some(Unavailable NotConfigured), change.After)

    [<Fact>]
    let ``finding lifecycle is derived from stable identity`` () =
        let result = CanonicalComparison.compare (snapshot "a" [] [ finding "one"; finding "same" ]) (snapshot "b" [] [ finding "two"; finding "same" ])
        let kind id = result.FindingChanges |> List.find (fun x -> x.FindingId = id) |> _.Kind
        Assert.Equal(FindingResolved, kind "one")
        Assert.Equal(FindingIntroduced, kind "two")
        Assert.Equal(FindingPersistent, kind "same")

    [<Fact>]
    let ``finding absence without temporal evidence is unavailable, not resolved`` () =
        let withoutHistory = Fixtures.producer (Capabilities.analyzers |> List.map (fun a -> if a.AnalyzerId = Capabilities.GitTemporal then notRun a.AnalyzerId else ran a.AnalyzerId))
        let before = snapshot "a" [] [ finding "hotspot" ]
        let after = snapshotWith (Some withoutHistory) "b" [] []
        let change = (CanonicalComparison.compare before after).FindingChanges |> List.exactlyOne
        Assert.Equal(FindingUnavailable, change.Kind)

    [<Fact>]
    let ``missing metric is removed rather than treated as zero`` () =
        let result = CanonicalComparison.compare (snapshot "a" [ metric "source.mutable-bindings" 1M ] []) (snapshot "b" [] [])
        let change = result.MetricChanges |> List.exactlyOne
        Assert.Equal(MetricRemoved, change.Kind)
        Assert.Equal(Some(Available(1M, "count")), change.Before)
        Assert.Equal(None, change.After)

    [<Fact>]
    let ``broad fallback indicator changes remain contextual`` () =
        let result = CanonicalComparison.compare (snapshot "a" [ metric "source.broad-catch-indicators" 2M ] []) (snapshot "b" [ metric "source.broad-catch-indicators" 11M ] [])
        Assert.Equal(MetricChanged, kindOf "source.broad-catch-indicators" result)
