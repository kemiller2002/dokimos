namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module RegionTests =
    [<Fact>]
    let ``region history counts repeated modification across commits`` () =
        let identity = { Path = "A.fs"; Anchor = "module:A/function:f"; Confidence = 0.95m }
        let changes =
            [ { Identity = identity; Commit = "a"; AddedLines = 2; DeletedLines = 1 }
              { Identity = identity; Commit = "b"; AddedLines = 4; DeletedLines = 3 }
              { Identity = identity; Commit = "b"; AddedLines = 1; DeletedLines = 1 } ]
        let history = Regions.summarize identity changes |> Option.get
        Assert.Equal(2, history.DistinctCommits)
        Assert.Equal(3, history.Modifications)
        Assert.Equal(12, history.Churn)
        Assert.True(Regions.isRepeated 2 history)

    [<Fact>]
    let ``region identity preserves confidence rather than asserting certainty`` () =
        let identity = { Path = "A.fs"; Anchor = "context-hash:abc"; Confidence = 0.60m }
        Assert.True(identity.Confidence < 1m)
