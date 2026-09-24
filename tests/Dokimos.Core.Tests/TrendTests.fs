namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Domain
open Dokimos.Core

module Helpers =
    let unwrap = function Ok value -> value | Error error -> failwith error
    let metric = MetricId.tryCreate "build.compiler-errors" |> unwrap
    let version = MetricVersion.tryCreate 1 |> unwrap

    let observation measurement =
        { Metric = metric
          MetricVersion = version
          Scope = Repository
          Measurement = measurement
          Provenance =
            { Collector = "test"
              CollectorVersion = "1"
              ConfigurationId = "test"
              CollectedAt = DateTimeOffset.Parse("2026-09-24T00:00:00Z") } }

module TrendTests =
    [<Fact>]
    let ``compiler errors falling from twelve to zero is improvement`` () =
        let before = Helpers.observation (Available(12m, "count"))
        let after = Helpers.observation (Available(0m, "count"))
        let trend = Trend.evaluate LowerIsBetter before after
        Assert.Equal(Some -12m, trend.Delta)
        Assert.Equal(Improved, trend.Direction)

    [<Fact>]
    let ``unavailable current evidence is not improvement`` () =
        let before = Helpers.observation (Available(12m, "count"))
        let after = Helpers.observation (Unavailable NotConfigured)
        let trend = Trend.evaluate LowerIsBetter before after
        Assert.Equal(NotComparable, trend.Direction)
