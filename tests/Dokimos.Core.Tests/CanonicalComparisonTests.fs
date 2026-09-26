namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

module CanonicalComparisonTests =
    let snapshot id metrics findings =
        { SchemaVersion="1.0.0";SnapshotId=id;Repository="r";Revision=id;Ref="main";CollectedAt=DateTimeOffset.UnixEpoch
          Collector="test";Metrics=metrics;Findings=findings }
    let metric id value =
        { MetricId=id;MetricVersion=1;Scope="A.fs";State="available";Value=Some value;Unit="count";Source="test" }

    [<Fact>]
    let ``directional metric can improve while contextual metric merely changes`` () =
        let before = snapshot "a" [metric "source.mutable-bindings" 2M;metric "complexity.proxy-cyclomatic" 10M] []
        let after = snapshot "b" [metric "source.mutable-bindings" 1M;metric "complexity.proxy-cyclomatic" 12M] []
        let result = CanonicalComparison.compare before after
        Assert.Contains(result.MetricChanges, fun x -> x.MetricId="source.mutable-bindings" && x.Kind=MetricImproved)
        Assert.Contains(result.MetricChanges, fun x -> x.MetricId="complexity.proxy-cyclomatic" && x.Kind=MetricChanged)

    [<Fact>]
    let ``finding lifecycle is derived from stable identity`` () =
        let finding id = {FindingId=id;Scope="A.fs";Kind="k";State="present";Evidence=[];Explanation="e"}
        let result = CanonicalComparison.compare (snapshot "a" [] [finding "one"]) (snapshot "b" [] [finding "two"])
        Assert.Contains(result.FindingChanges, fun x -> x.FindingId="one" && x.Kind=FindingResolved)
        Assert.Contains(result.FindingChanges, fun x -> x.FindingId="two" && x.Kind=FindingIntroduced)
