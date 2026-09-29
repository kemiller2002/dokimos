namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Domain
open Dokimos.Core

module CollectorsTests =
    let value id metrics = metrics |> List.find (fun (m: CanonicalMetric) -> m.MetricId = id) |> CanonicalMetric.value

    [<Fact>]
    let ``build diagnostics come from the final MSBuild summary`` () =
        let log = "Build succeeded.\n    3 Warning(s)\n    0 Error(s)\n\nTime Elapsed 00:00:01\n  restore\n    0 Warning(s)\n    1 Error(s)\n"
        match Collectors.buildDiagnostics log with
        | Ok metrics ->
            Assert.Equal(Some 1M, value "build.compiler-errors" metrics)
            Assert.Equal(Some 0M, value "build.compiler-warnings" metrics)
        | Error e -> failwith e.Code

    [<Fact>]
    let ``a build log without a summary is failed collection, not zero`` () =
        match Collectors.buildDiagnostics "restored" with
        | Error failure -> Assert.Equal("build-summary-missing", failure.Code)
        | Ok _ -> failwith "expected failure"

    let trx total executed passed failed =
        $"""<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed"><Counters total="{total}" executed="{executed}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" /></ResultSummary></TestRun>"""

    [<Fact>]
    let ``test results sum every TRX file`` () =
        match Collectors.testResults [ trx 4 4 3 1; trx 10 9 9 0 ] with
        | Ok metrics ->
            Assert.Equal(Some 14M, value "tests.total" metrics)
            Assert.Equal(Some 12M, value "tests.passed" metrics)
            Assert.Equal(Some 1M, value "tests.failed" metrics)
            Assert.Equal(Some 1M, value "tests.skipped" metrics)
        | Error e -> failwith e.Code

    [<Fact>]
    let ``malformed TRX is a failed collection`` () =
        match Collectors.testResults [ "<TestRun" ] with
        | Error failure -> Assert.Equal("trx-malformed", failure.Code)
        | Ok _ -> failwith "expected failure"

    [<Fact>]
    let ``coverage reads the Cobertura root line rate`` () =
        match Collectors.coverage """<coverage line-rate="0.8125" branch-rate="0.5"></coverage>""" with
        | Ok metrics -> Assert.Equal(Some 0.8125M, value "coverage.line-rate" metrics)
        | Error e -> failwith e.Code

    [<Fact>]
    let ``coverage without a line rate is failed, not zero`` () =
        match Collectors.coverage "<coverage/>" with
        | Error failure -> Assert.Equal("coverage-line-rate-missing", failure.Code)
        | Ok _ -> failwith "expected failure"
