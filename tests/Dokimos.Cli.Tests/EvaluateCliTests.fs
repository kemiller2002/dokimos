namespace Dokimos.Cli.Tests

open System.IO
open Xunit

/// DOK-OPS-002: exit codes and emitted JSON for `dokimos evaluate`.
module EvaluateCliTests =
    let fixture () =
        let dir = Support.tempDirectory ()
        let src = Path.Combine(dir, "src")
        Support.write src "A.fs" "let x = 1\n" |> ignore
        let log = Support.write dir "build.log" (Support.buildLog 0 0)
        let trx = Support.write dir "t.trx" (Support.trx 1 1 0)
        let snap = Support.run [ "snapshot"; src; "--repository"; "o/r"; "--revision"; "abc"; "--build-log"; log; "--test-results"; trx ]
        dir, Support.write dir "s.json" snap.Stdout.Value

    let policy = Support.repoPath "config/dokimos-policy.json"

    [<Fact>]
    let ``passing evaluation exits 0 with schema-valid output`` () =
        let _, s = fixture ()
        let output = Support.run [ "evaluate"; "--baseline"; s; "--current"; s; "--policy"; policy ]
        Assert.Equal(0, output.ExitCode)
        Support.assertSchemaValid "dokimos-evaluation.schema.json" output.Stdout.Value

    [<Fact>]
    let ``every outcome state is counted even when zero`` () =
        let _, s = fixture ()
        let output = Support.run [ "evaluate"; "--baseline"; s; "--current"; s; "--policy"; policy ]
        for state in [ "passed"; "warning"; "failed"; "observe-only"; "not-evaluated"; "unavailable"; "incompatible"; "collection-failed" ] do
            Assert.Contains($"\"{state}\": ", output.Stdout.Value)

    [<Fact>]
    let ``missing options are invalid invocations (exit 2)`` () =
        let output = Support.run [ "evaluate"; "--baseline"; "x" ]
        Assert.Equal(2, output.ExitCode)
        Support.assertSchemaValid "dokimos-diagnostic.schema.json" output.Stderr.Value

    [<Fact>]
    let ``invalid or missing policy is invalid configuration (exit 2)`` () =
        let dir, s = fixture ()
        let badPolicy = Support.write dir "p.json" """{"schemaVersion":"1.1.0"}"""
        Assert.Equal(2, (Support.run [ "evaluate"; "--baseline"; s; "--current"; s; "--policy"; badPolicy ]).ExitCode)
        Assert.Equal(2, (Support.run [ "evaluate"; "--baseline"; s; "--current"; s; "--policy"; Path.Combine(dir, "none.json") ]).ExitCode)

    [<Fact>]
    let ``unreadable baseline is unavailable evidence (exit 3)`` () =
        let dir, s = fixture ()
        let output = Support.run [ "evaluate"; "--baseline"; Path.Combine(dir, "missing.json"); "--current"; s; "--policy"; policy ]
        Assert.Equal(3, output.ExitCode)
        Assert.Contains("baseline-snapshot-unavailable", output.Stderr.Value)

    [<Fact>]
    let ``snapshot of a missing source directory is an invalid invocation`` () =
        let output = Support.run [ "snapshot"; "/definitely/not/here"; "--repository"; "o/r"; "--revision"; "abc" ]
        Assert.Equal(2, output.ExitCode)
