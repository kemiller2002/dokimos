namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Domain
open Dokimos.Core

module CanonicalSnapshotTests =
    let request sources history =
        { Repository = "owner/repo"
          Revision = "abc"
          Ref = "main"
          CollectedAt = DateTimeOffset.UnixEpoch
          AnalyzedScope = [ "." ]
          Sources = sources
          GitHistory = history
          BuildLog = None
          TestResults = None
          Coverage = None }

    let hotspotSource = "let f x =\n    if x > 0 then\n        match x with\n        | 1 -> 1\n        | 2 -> 2\n        | 3 -> 3\n        | 4 -> 4\n        | 5 -> 5\n        | 6 -> 6\n        | 7 -> 7\n        | _ -> 0"

    let history path commits =
        [ for i in 1..commits -> $"commit c{i} 2026-09-2{i % 9}T00:00:00Z\n20\t5\t{path}" ] |> String.concat "\n"

    [<Fact>]
    let ``canonical snapshot retains revision and decomposed source evidence`` () =
        let snapshot = Collection.snapshot (Timer.fixedDuration 1L) (request [ "A.fs", "let mutable x = 1" ] None)
        Assert.StartsWith("owner/repo@abc:", snapshot.SnapshotId)
        Assert.Contains(snapshot.Metrics, fun x -> x.MetricId = "source.mutable-bindings" && CanonicalMetric.value x = Some 1M)

    [<Fact>]
    let ``correlations become stable finding identities with decomposable evidence`` () =
        let snapshot = Collection.snapshot (Timer.fixedDuration 1L) (request [ "A.fs", hotspotSource ] (Some(history "A.fs" 6)))
        let hotspot = snapshot.Findings |> List.find (fun x -> x.FindingId = "correlation:maintainability-hotspot:A.fs")
        Assert.Contains(hotspot.Evidence, fun s -> match s with StructuralComplexity _ -> true | _ -> false)
        Assert.Contains(hotspot.Evidence, fun s -> match s with FrequentChange 6 -> true | _ -> false)

    [<Fact>]
    let ``temporal metrics are unavailable without history, never zero`` () =
        let snapshot = Collection.snapshot (Timer.fixedDuration 1L) (request [ "A.fs", "let x = 1" ] None)
        let churn = snapshot.Metrics |> List.find (fun m -> m.MetricId = "change.file-churn")
        Assert.Equal(Unavailable(InsufficientEvidence "git history not supplied"), churn.Measurement)
        Assert.Equal(Some false, CanonicalSnapshot.analyzerRan snapshot Capabilities.GitTemporal)

    [<Fact>]
    let ``external evidence not supplied is unavailable and its analyzer is not run`` () =
        let snapshot = Collection.snapshot (Timer.fixedDuration 1L) (request [ "A.fs", "let x = 1" ] None)
        for id in [ "build.compiler-errors"; "build.compiler-warnings"; "tests.total"; "tests.failed"; "coverage.line-rate" ] do
            let m = snapshot.Metrics |> List.find (fun m -> m.MetricId = id)
            Assert.Equal(Unavailable NotConfigured, m.Measurement)
        Assert.Contains(Capabilities.DotnetBuild, snapshot.Performance.Value.UnavailableCollectors)

    [<Fact>]
    let ``identical evidence yields identical identity regardless of collection time`` () =
        let a = Collection.snapshot (Timer.fixedDuration 1L) (request [ "A.fs", "let x = 1" ] None)
        let b = Collection.snapshot (Timer.fixedDuration 99L) { request [ "A.fs", "let x = 1" ] None with CollectedAt = DateTimeOffset.UtcNow }
        let c = Collection.snapshot (Timer.fixedDuration 1L) (request [ "A.fs", "let mutable x = 1" ] None)
        Assert.Equal(a.SnapshotId, b.SnapshotId)
        Assert.NotEqual<string>(a.SnapshotId, c.SnapshotId)

    [<Fact>]
    let ``snapshot records producer provenance and its own performance`` () =
        let snapshot = Collection.snapshot (Timer.fixedDuration 7L) (request [ "A.fs", "let x = 1"; "B.fs", "let y = 2" ] None)
        let producer = snapshot.Producer.Value
        Assert.Equal(DokimosInfo.version, producer.DokimosVersion)
        Assert.Equal(Collection.configurationId, producer.ConfigurationId)
        Assert.Contains(producer.Analyzers, fun a -> a.AnalyzerId = Capabilities.Structural && a.State = AnalyzerRan)
        Assert.Equal(2, snapshot.Performance.Value.FileCount)
        Assert.Equal(snapshot.Metrics.Length, snapshot.Performance.Value.ObservationCount)
        Assert.Equal(7L, snapshot.Performance.Value.TotalMilliseconds)
