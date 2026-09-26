namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

module CanonicalSnapshotTests =
    [<Fact>]
    let ``canonical snapshot retains revision and decomposed source evidence`` () =
        let analysis = RepositoryAnalysis.analyze 3 Map.empty [("A.fs","let mutable x = 1")]
        let snapshot = CanonicalSnapshot.fromAnalysis "owner/repo" "abc" "main" DateTimeOffset.UnixEpoch analysis
        Assert.Equal("owner/repo:abc",snapshot.SnapshotId)
        Assert.Contains(snapshot.Metrics, fun x -> x.MetricId="source.mutable-bindings" && x.Value=Some 1M)

    [<Fact>]
    let ``correlations become stable finding identities`` () =
        let temporal =
            { Path="A.fs";CommitCount=6;Additions=70;Deletions=40;Churn=110
              FirstChange=DateTimeOffset.UnixEpoch;LastChange=DateTimeOffset.UnixEpoch;RenameCount=0 }
        let source = "let f x =\n    if x > 0 then\n        match x with\n        | 1 -> 1\n        | 2 -> 2\n        | 3 -> 3\n        | 4 -> 4\n        | 5 -> 5\n        | 6 -> 6\n        | 7 -> 7\n        | _ -> 0"
        let analysis = RepositoryAnalysis.analyze 3 (Map.ofList [("A.fs",temporal)]) [("A.fs",source)]
        let snapshot = CanonicalSnapshot.fromAnalysis "owner/repo" "abc" "main" DateTimeOffset.UnixEpoch analysis
        Assert.Contains(snapshot.Findings, fun x -> x.FindingId="correlation:maintainability-hotspot:A.fs")
