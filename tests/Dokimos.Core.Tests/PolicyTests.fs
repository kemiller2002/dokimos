namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Domain
open Dokimos.Core

module PolicyHelpers =
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
              CollectedAt = System.DateTimeOffset.Parse("2026-09-24T00:00:00Z") } }

module PolicyTests =
    [<Fact>]
    let ``legacy threshold may pass while ratchet prevents deterioration`` () =
        let observation = PolicyHelpers.observation (Available(3m, "count"))
        let threshold =
            { Metric = PolicyHelpers.metric
              Maximum = 10m
              Disposition = Fail }
        let ratchet =
            { Metric = PolicyHelpers.metric
              BestAccepted = 2m
              Preference = LowerIsBetter
              Disposition = Fail }

        Assert.Equal(Pass, Policy.evaluateThreshold threshold observation)
        Assert.Equal(Failure(3m, 2m), Policy.evaluateRatchet ratchet observation)

    [<Fact>]
    let ``unavailable evidence never passes a ratchet by pretending to be zero`` () =
        let observation = PolicyHelpers.observation (Unavailable Unsupported)
        let ratchet =
            { Metric = PolicyHelpers.metric
              BestAccepted = 2m
              Preference = LowerIsBetter
              Disposition = Fail }

        match Policy.evaluateRatchet ratchet observation with
        | NotEvaluated _ -> ()
        | result -> failwith $"Expected NotEvaluated, got {result}."

    [<Fact>]
    let ``new best state passes ratchet`` () =
        let observation = PolicyHelpers.observation (Available(0m, "count"))
        let ratchet =
            { Metric = PolicyHelpers.metric
              BestAccepted = 2m
              Preference = LowerIsBetter
              Disposition = Fail }

        Assert.Equal(Pass, Policy.evaluateRatchet ratchet observation)
