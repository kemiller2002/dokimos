namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Domain
open Dokimos.Core

module SnapshotTests =
    let unwrap = function Ok value -> value | Error error -> failwith error
    let metric = MetricId.tryCreate "build.compiler-errors" |> unwrap
    let version = MetricVersion.tryCreate 1 |> unwrap
    let provenance = { Collector = "test"; CollectorVersion = "1"; ConfigurationId = "test"; CollectedAt = DateTimeOffset.UtcNow }
    let observation value = { Metric = metric; MetricVersion = version; Scope = Repository; Measurement = Available(value,"count"); Provenance = provenance }
    let snapshot value = { Id = SnapshotId.create(); Revision = { Repository="r"; Commit=string value; Ref=None }; CreatedAt=DateTimeOffset.UtcNow; Observations=[observation value] }

    [<Fact>]
    let ``snapshot comparison derives trend only from declared metric semantics`` () =
        let definition = { Metric=metric; Version=version; Unit="count"; Preference=PreferLower }
        let result = Snapshots.compare [definition] (snapshot 12m) (snapshot 0m)
        Assert.Single(result.Trends) |> ignore
        let _,_,trend = result.Trends.Head
        Assert.Equal(Improved, trend.Direction)
