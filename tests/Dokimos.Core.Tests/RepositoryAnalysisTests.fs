namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

module RepositoryAnalysisTests =
    [<Fact>]
    let ``repository analysis correlates structural and temporal evidence`` () =
        let temporal =
            { Path="A.fs"; CommitCount=7; Additions=80; Deletions=40; Churn=120
              FirstChange=DateTimeOffset.UtcNow.AddDays(-10); LastChange=DateTimeOffset.UtcNow; RenameCount=0 }
        let source = "let f x =\n    if x > 0 then\n        match x with\n        | 1 -> 1\n        | 2 -> 2\n        | 3 -> 3\n        | 4 -> 4\n        | 5 -> 5\n        | 6 -> 6\n        | 7 -> 7\n        | _ -> 0"
        let result = RepositoryAnalysis.analyze 3 (Map.ofList [("A.fs",temporal)]) [("A.fs",source)]
        Assert.Single(result.Sources) |> ignore
        Assert.Contains(result.Sources.Head.Correlations, fun x -> x.Kind = MaintainabilityHotspot)

    [<Fact>]
    let ``absence of temporal evidence does not fabricate change pressure`` () =
        let result = RepositoryAnalysis.analyze 3 Map.empty [("A.fs","let x = 1")]
        Assert.DoesNotContain(result.Sources.Head.Signals, fun x -> match x with FrequentChange _ | HighChurn _ -> true | _ -> false)
