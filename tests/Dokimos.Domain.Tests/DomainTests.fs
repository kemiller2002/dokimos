namespace Dokimos.Domain.Tests

open System
open Xunit
open Dokimos.Domain

module Helpers =
    let metric () =
        match MetricId.tryCreate "complexity.cyclomatic" with
        | Ok value -> value
        | Error error -> failwith error

    let version n =
        match MetricVersion.tryCreate n with
        | Ok value -> value
        | Error error -> failwith error

    let observation version measurement =
        { Metric = metric ()
          MetricVersion = version
          Scope = File "src/App.fs"
          Measurement = measurement
          Provenance =
            { Collector = "test"
              CollectorVersion = "1.0.0"
              ConfigurationId = "default"
              CollectedAt = DateTimeOffset.Parse("2026-09-24T00:00:00Z") } }

open Helpers

module DomainTests =
    [<Fact>]
    let ``zero is an available measurement and not unavailable`` () =
        let observation = observation (version 1) (Available(0m, "count"))
        match observation.Measurement with
        | Available (value, _) -> Assert.Equal(0m, value)
        | _ -> failwith "Zero must remain an available measurement."

    [<Fact>]
    let ``unavailable measurements do not produce a numeric delta`` () =
        let before = observation (version 1) (Available(10m, "count"))
        let after = observation (version 1) (Unavailable Unsupported)
        Assert.True(Comparison.delta before after |> Option.isNone)

    [<Fact>]
    let ``metric version changes prevent direct trend comparison`` () =
        let before = observation (version 1) (Available(10m, "count"))
        let after = observation (version 2) (Available(8m, "count"))
        match Comparison.compatibility before after with
        | IncompatibleMetricDefinition _ -> ()
        | Compatible -> failwith "Different metric definitions must not be silently compared."

    [<Fact>]
    let ``compatible measurements produce directional delta`` () =
        let before = observation (version 1) (Available(10m, "count"))
        let after = observation (version 1) (Available(7m, "count"))
        Assert.Equal(Some -3m, Comparison.delta before after)
