namespace Dokimos.Cli.Tests

open System
open System.IO
open System.Text.Json
open Xunit
open Dokimos.Domain
open Dokimos.Core

/// DOK-OPS-001: `dokimos compare` emits schema-valid JSON for every
/// comparison state through the production serialization path.
module CompareCliTests =
    let producer runs : SnapshotProducer =
        { DokimosVersion = "test"; ConfigurationId = "test"; AnalyzedScope = [ "src" ]; Analyzers = runs }

    let run id state : AnalyzerRun = { AnalyzerId = id; Version = "1"; State = state; DurationMilliseconds = Some 1L }

    let snapshot id runs metrics findings : CanonicalSnapshot =
        { SchemaVersion = "2.0.0"
          SnapshotId = id
          Repository = "owner/repo"
          Revision = id
          Ref = "main"
          CollectedAt = DateTimeOffset.Parse "2026-09-29T00:00:00Z"
          Collector = "test"
          Producer = Some(producer runs)
          Performance = None
          Metrics = metrics
          Findings = findings }

    let m id scope value = CanonicalMetric.available id scope "count" value "test"

    let finding id path : CanonicalFinding =
        { FindingId = id; Scope = path; Kind = "maintainability-hotspot"; Evidence = [ StructuralComplexity 14; HighChurn 150; MissingTestChange ]; Explanation = "e" }

    let allRan = Capabilities.analyzers |> List.map (fun a -> run a.AnalyzerId AnalyzerRan)

    let before =
        snapshot "before" allRan
            [ m "source.mutable-bindings" "A.fs" 2M
              m "quality.type-weakening-indicators" "A.fs" 1M
              m "source.lines" "A.fs" 10M
              m "complexity.proxy-cyclomatic" "A.fs" 4M
              m "source.broad-catch-indicators" "Gone.fs" 1M
              m "source.nonblank-lines" "A.fs" 3M
              m "tests.failed" "repository" 0M
              CanonicalMetric.available "coverage.line-rate" "repository" "ratio" 0.5M "test" ]
            [ finding "correlation:maintainability-hotspot:Gone.fs" "Gone.fs"; finding "correlation:maintainability-hotspot:A.fs" "A.fs" ]

    let after =
        snapshot "after" allRan
            [ m "source.mutable-bindings" "A.fs" 1M
              m "quality.type-weakening-indicators" "A.fs" 3M
              m "source.lines" "A.fs" 10M
              m "complexity.proxy-cyclomatic" "A.fs" 9M
              { m "source.nonblank-lines" "A.fs" 3M with MetricVersion = 2 }
              CanonicalMetric.failed "tests.failed" "repository" "count" { Code = "trx-malformed"; Message = "bad" } "test"
              CanonicalMetric.unavailable "coverage.line-rate" "repository" "ratio" NotConfigured "test"
              m "source.public-declarations" "New.fs" 2M ]
            [ finding "correlation:maintainability-hotspot:A.fs" "A.fs"; finding "correlation:maintainability-hotspot:New.fs" "New.fs" ]

    let writeSnapshot dir name s = Support.write dir name (Contracts.serialize (Contracts.snapshotDto s))

    let expectedKinds =
        [ "source.mutable-bindings", "improved"
          "quality.type-weakening-indicators", "deteriorated"
          "source.lines", "unchanged"
          "complexity.proxy-cyclomatic", "changed"
          "source.broad-catch-indicators", "removed"
          "source.nonblank-lines", "incompatible"
          "tests.failed", "unavailable"
          "coverage.line-rate", "unavailable"
          "source.public-declarations", "added" ]

    let assertComparison (json: string) =
        Support.assertSchemaValid "dokimos-comparison.schema.json" json
        use doc = JsonDocument.Parse json
        let changes = doc.RootElement.GetProperty("MetricChanges").EnumerateArray() |> Seq.map (fun c -> c.GetProperty("MetricId").GetString(), c.GetProperty("Kind").GetString()) |> Map.ofSeq
        for metricId, kind in expectedKinds do
            Assert.Equal(kind, changes[metricId])
        let findings = doc.RootElement.GetProperty("FindingChanges").EnumerateArray() |> Seq.map (fun c -> c.GetProperty("FindingId").GetString(), c.GetProperty("Kind").GetString()) |> Map.ofSeq
        Assert.Equal("resolved", findings["correlation:maintainability-hotspot:Gone.fs"])
        Assert.Equal("persistent", findings["correlation:maintainability-hotspot:A.fs"])
        Assert.Equal("introduced", findings["correlation:maintainability-hotspot:New.fs"])

    [<Fact>]
    let ``every comparison state serializes to schema-valid JSON with stable tags`` () =
        let dir = Support.tempDirectory ()
        let output = Support.run [ "compare"; writeSnapshot dir "before.json" before; writeSnapshot dir "after.json" after ]
        Assert.Equal(0, output.ExitCode)
        Assert.Equal(None, output.Stderr)
        assertComparison output.Stdout.Value

    [<Fact>]
    let ``finding unavailable state is emitted when temporal evidence is missing`` () =
        let dir = Support.tempDirectory ()
        let noHistory = allRan |> List.map (fun r -> if r.AnalyzerId = Capabilities.GitTemporal then run r.AnalyzerId (AnalyzerNotRun "git history not supplied") else r)
        let current = { after with Producer = Some(producer noHistory); Findings = [] }
        let output = Support.run [ "compare"; writeSnapshot dir "before.json" before; writeSnapshot dir "after.json" current ]
        Support.assertSchemaValid "dokimos-comparison.schema.json" output.Stdout.Value
        Assert.Contains("\"Kind\": \"unavailable\"", output.Stdout.Value)
        Assert.DoesNotContain("\"Kind\": \"resolved\"", output.Stdout.Value)

    [<Fact>]
    let ``comparison output is deterministic`` () =
        let dir = Support.tempDirectory ()
        let a = writeSnapshot dir "before.json" before
        let b = writeSnapshot dir "after.json" after
        let first = Support.run [ "compare"; a; b ]
        let second = Support.run [ "compare"; a; b ]
        Assert.Equal(first.Stdout, second.Stdout)

    [<Fact>]
    let ``the accepted schema 1 baseline compares against a schema 2 snapshot`` () =
        let dir = Support.tempDirectory ()
        let output = Support.run [ "compare"; Support.repoPath "baselines/accepted-snapshot.json"; writeSnapshot dir "after.json" after ]
        Assert.Equal(0, output.ExitCode)
        Support.assertSchemaValid "dokimos-comparison.schema.json" output.Stdout.Value

    [<Fact>]
    let ``the built CLI process emits the comparison without a serialization fault`` () =
        let dir = Support.tempDirectory ()
        let result = Support.execute [ "compare"; writeSnapshot dir "before.json" before; writeSnapshot dir "after.json" after ]
        Assert.Equal("", result.Stderr)
        Assert.Equal(0, result.ExitCode)
        assertComparison result.Stdout

    [<Fact>]
    let ``missing or invalid snapshots are unavailable evidence with exit code 3`` () =
        let dir = Support.tempDirectory ()
        let good = writeSnapshot dir "after.json" after
        let missing = Support.run [ "compare"; Path.Combine(dir, "nope.json"); good ]
        let malformed = Support.run [ "compare"; Support.write dir "bad.json" "{"; good ]
        for output in [ missing; malformed ] do
            Assert.Equal(3, output.ExitCode)
            Assert.Equal(None, output.Stdout)
            Support.assertSchemaValid "dokimos-diagnostic.schema.json" output.Stderr.Value
