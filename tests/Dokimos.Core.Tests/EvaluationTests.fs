namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Domain
open Dokimos.Core
open Fixtures

module EvaluationTests =
    let asOf = DateTimeOffset.Parse "2026-09-29T00:00:00Z"

    let policy: QualityPolicy =
        { SchemaVersion = "1.1.0"
          Identity = "sha256:test"
          Baseline = "B"
          Ratchets =
            [ { MetricId = "build.compiler-warnings"; MetricVersion = 1; BestAccepted = 0M; Preference = LowerIsBetter; Disposition = Fail } ]
          Thresholds = [ { MetricId = "complexity.proxy-cyclomatic"; MetricVersion = 1; Maximum = 20M; Disposition = Warn } ]
          Regressions = Some Warn
          IntroducedFindings = Some Fail
          RequiredEvidence = [ "build.compiler-warnings" ]
          ObservedNotRatcheted = []
          Suppressions = [] }

    let healthy extra = snapshot "current" ([ repositoryMetric "build.compiler-warnings" 0M; metric "complexity.proxy-cyclomatic" 5M ] @ extra) []
    let baseline = snapshot "baseline" [ repositoryMetric "build.compiler-warnings" 0M; metric "complexity.proxy-cyclomatic" 5M; metric "source.mutable-bindings" 1M ] []

    let run p b c = Evaluation.evaluate asOf p b c

    let states (e: PolicyEvaluation) = e.Outcomes |> List.map (fun o -> Contracts.gateTag o.Result) |> Set.ofList

    [<Fact>]
    let ``healthy evidence passes and permits continuation`` () =
        let e = run policy baseline (healthy [ metric "source.mutable-bindings" 1M ])
        Assert.Equal(GatePassed, e.Disposition)
        Assert.Equal(ExitCodes.Continue, Evaluation.exitCode e.Disposition)

    [<Fact>]
    let ``ratchet deterioration fails with exit code 4`` () =
        let current = snapshot "current" [ repositoryMetric "build.compiler-warnings" 2M; metric "complexity.proxy-cyclomatic" 5M ] []
        let e = run policy baseline current
        Assert.Equal(GateFailed, e.Disposition)
        Assert.Equal(ExitCodes.PolicyFailure, Evaluation.exitCode e.Disposition)
        let failure = e.Outcomes |> List.find (fun o -> o.Rule = RatchetRuleKind)
        Assert.Equal(Failure(2M, 0M), failure.Result)
        Assert.Contains("best accepted", failure.Explanation)

    [<Fact>]
    let ``regression against the baseline warns without failing`` () =
        let e = run policy baseline (healthy [ metric "source.mutable-bindings" 3M ])
        Assert.Equal(GatePassedWithWarnings, e.Disposition)
        let regression = e.Outcomes |> List.find (fun o -> o.Rule = RegressionRuleKind)
        Assert.Equal(Warning(3M, 1M), regression.Result)
        Assert.Equal(Some 1M, regression.Baseline)

    [<Fact>]
    let ``improvement is reported in the summary`` () =
        let e = run policy baseline (healthy [ metric "source.mutable-bindings" 0M ])
        Assert.Equal(1, e.Summary.Improved)
        Assert.Equal(GatePassed, e.Disposition)

    [<Fact>]
    let ``missing required evidence yields exit code 3, not a pass`` () =
        let current = snapshot "current" [ CanonicalMetric.unavailable "build.compiler-warnings" CanonicalSnapshot.RepositoryScope "count" NotConfigured "test" ] []
        let e = run policy baseline current
        Assert.Equal(GateEvidenceUnavailable, e.Disposition)
        Assert.Equal(ExitCodes.EvidenceUnavailable, Evaluation.exitCode e.Disposition)
        Assert.Contains("unavailable", states e)

    [<Fact>]
    let ``failed collection and incompatible definitions are distinct states`` () =
        let failure = { Code = "trx-malformed"; Message = "m" }
        let current =
            snapshot "current"
                [ repositoryMetric "build.compiler-warnings" 0M
                  CanonicalMetric.failed "complexity.proxy-cyclomatic" "A.fs" "points" failure "test"
                  { metric "source.mutable-bindings" 1M with MetricVersion = 2 } ]
                []
        let e = run { policy with Thresholds = [ { MetricId = "complexity.proxy-cyclomatic"; MetricVersion = 1; Maximum = 20M; Disposition = Warn } ] } baseline current
        Assert.Contains("collection-failed", states e)
        Assert.Contains("incompatible", states e)
        Assert.Equal(GatePassed, e.Disposition)

    [<Fact>]
    let ``observe-only rules record exceedance without warning`` () =
        let observePolicy = { policy with Thresholds = [ { MetricId = "complexity.proxy-cyclomatic"; MetricVersion = 1; Maximum = 1M; Disposition = ObserveOnly } ] }
        let e = run observePolicy baseline (healthy [ metric "source.mutable-bindings" 1M ])
        Assert.Contains("observe-only", states e)
        Assert.Equal(GatePassed, e.Disposition)

    [<Fact>]
    let ``absent ratchet metric is unavailable rather than passing`` () =
        let e = run { policy with RequiredEvidence = [] } baseline (snapshot "current" [] [])
        let ratchet = e.Outcomes |> List.find (fun o -> o.Rule = RatchetRuleKind)
        Assert.Equal("unavailable", Contracts.gateTag ratchet.Result)
        Assert.Equal(GatePassed, e.Disposition)

    [<Fact>]
    let ``introduced finding follows the policy disposition`` () =
        let e = run policy baseline { healthy [ metric "source.mutable-bindings" 1M ] with Findings = [ finding "hotspot" ] }
        Assert.Equal(GateFailed, e.Disposition)
        let outcome = e.Outcomes |> List.find (fun o -> o.Rule = IntroducedFindingRuleKind)
        Assert.Contains("structural-complexity", outcome.Explanation)

    let suppression expires : Suppression =
        { FindingId = "hotspot"; Reason = "accepted during migration"; Scope = "A.fs"; Actor = Some "reviewer"
          Created = asOf.AddDays -10.0; Expires = expires; Status = SuppressionActive }

    [<Fact>]
    let ``active suppression keeps the finding but changes the judgment`` () =
        let p = { policy with Suppressions = [ suppression (Some(asOf.AddDays 5.0)) ] }
        let e = run p baseline { healthy [ metric "source.mutable-bindings" 1M ] with Findings = [ finding "hotspot" ] }
        Assert.Equal(GatePassed, e.Disposition)
        let outcome = e.Outcomes |> List.find (fun o -> o.Rule = IntroducedFindingRuleKind)
        Assert.Equal(ObservedOnly(1M, 0M), outcome.Result)
        Assert.True(match outcome.Suppression with Some (Suppressed _) -> true | _ -> false)
        Assert.Equal(1, e.Summary.FindingsIntroduced)

    [<Fact>]
    let ``expired suppression is visible and no longer waives policy`` () =
        let p = { policy with Suppressions = [ suppression (Some(asOf.AddDays -1.0)) ] }
        let e = run p baseline { healthy [ metric "source.mutable-bindings" 1M ] with Findings = [ finding "hotspot" ] }
        Assert.Equal(GateFailed, e.Disposition)
        let outcome = e.Outcomes |> List.find (fun o -> o.Rule = IntroducedFindingRuleKind)
        Assert.True(match outcome.Suppression with Some (SuppressionExpired _) -> true | _ -> false)

    [<Fact>]
    let ``snapshots of different repositories are incompatible`` () =
        let e = run policy baseline { healthy [] with Repository = "other" }
        Assert.Equal(ExitCodes.EvidenceUnavailable, Evaluation.exitCode e.Disposition)
        Assert.True(match e.Disposition with GateIncompatibleSnapshots _ -> true | _ -> false)
