namespace Dokimos.Cli.Tests

open System.IO
open System.Text.Json
open Xunit

/// DOK-OPS-005/006: durable storage and historical queries through the CLI.
module StoreHistoryCliTests =
    let snapshotOf (root: string) (src: string) revision (source: string) =
        Support.write src "Calc.fs" source |> ignore
        let log = Support.write root (revision + ".log") (Support.buildLog 0 0)
        let output = Support.run [ "snapshot"; src; "--repository"; "o/r"; "--revision"; revision; "--build-log"; log ]
        Assert.Equal(0, output.ExitCode)
        Support.write root (revision + ".json") output.Stdout.Value

    let idOf (path: string) =
        use doc = JsonDocument.Parse(File.ReadAllText path)
        doc.RootElement.GetProperty("SnapshotId").GetString()

    let setup () =
        let root = Support.tempDirectory ()
        let src = Path.Combine(root, "src")
        let store = Path.Combine(root, "evidence")
        let init = Support.run [ "store"; "init"; "--store"; store ]
        Assert.Equal(0, init.ExitCode)
        Support.assertSchemaValid "dokimos-store-result.schema.json" init.Stdout.Value
        let a = snapshotOf root src "rev1" "let mutable a = 1\nlet mutable b = 2\n"
        let b = snapshotOf root src "rev2" "let mutable a = 1\n"
        let c = snapshotOf root src "rev3" "let mutable a = 1\nlet mutable b = 2\nlet mutable c = 3\n"
        for s in [ a; b; c ] do
            let put = Support.run [ "store"; "put"; "--store"; store; "--snapshot"; s ]
            Assert.Equal(0, put.ExitCode)
        store, a, b, c

    [<Fact>]
    let ``put is idempotent and a conflicting identity exits 5`` () =
        let store, a, _, _ = setup ()
        let again = Support.run [ "store"; "put"; "--store"; store; "--snapshot"; a ]
        Assert.Contains("already-stored", again.Stdout.Value)
        let tampered = File.ReadAllText(a).Replace("\"Value\": 2,", "\"Value\": 7,")
        let path = Support.write (Path.GetDirectoryName a) "tampered.json" tampered
        let conflict = Support.run [ "store"; "put"; "--store"; store; "--snapshot"; path ]
        Assert.Equal(5, conflict.ExitCode)
        Support.assertSchemaValid "dokimos-diagnostic.schema.json" conflict.Stderr.Value

    [<Fact>]
    let ``accepted baseline is retrievable and feeds history`` () =
        let store, a, _, _ = setup ()
        let missing = Support.run [ "store"; "baseline"; "--store"; store ]
        Assert.Equal(3, missing.ExitCode)
        let accept = Support.run [ "store"; "accept-baseline"; "--store"; store; "--snapshot-id"; idOf a; "--actor"; "test"; "--reason"; "first accepted state" ]
        Assert.Equal(0, accept.ExitCode)
        let baseline = Support.run [ "store"; "baseline"; "--store"; store ]
        Assert.Equal(0, baseline.ExitCode)
        Support.assertSchemaValid "dokimos-snapshot.schema.json" baseline.Stdout.Value
        Assert.Equal(idOf a, JsonDocument.Parse(baseline.Stdout.Value).RootElement.GetProperty("SnapshotId").GetString())

    [<Fact>]
    let ``history lists ordered snapshots and metric trends`` () =
        let store, a, b, _ = setup ()
        Support.run [ "store"; "accept-baseline"; "--store"; store; "--snapshot-id"; idOf a; "--actor"; "t"; "--reason"; "r" ] |> ignore
        let snapshots = Support.run [ "history"; "--store"; store ]
        Support.assertSchemaValid "dokimos-history.schema.json" snapshots.Stdout.Value
        use doc = JsonDocument.Parse snapshots.Stdout.Value
        Assert.Equal(3, doc.RootElement.GetProperty("Snapshots").GetArrayLength())

        let metric = Support.run [ "history"; "--store"; store; "--metric"; "source.mutable-bindings" ]
        Support.assertSchemaValid "dokimos-history.schema.json" metric.Stdout.Value
        use m = JsonDocument.Parse metric.Stdout.Value
        let series = m.RootElement.GetProperty("Series").[0]
        Assert.Equal(idOf b, series.GetProperty("Best").GetProperty("SnapshotId").GetString())
        Assert.Equal(1M, series.GetProperty("BaselineDistance").GetDecimal())

    [<Fact>]
    let ``file history covers every metric for the file`` () =
        let store, _, _, _ = setup ()
        let scope =
            let listing = Support.run [ "history"; "--store"; store; "--metric"; "source.lines" ]
            JsonDocument.Parse(listing.Stdout.Value).RootElement.GetProperty("Series").[0].GetProperty("Scope").GetString()
        let file = Support.run [ "history"; "--store"; store; "--file"; scope ]
        Support.assertSchemaValid "dokimos-history.schema.json" file.Stdout.Value
        Assert.Contains("\"MetricId\": \"change.file-churn\"", file.Stdout.Value)
        Assert.Contains("\"MetricId\": \"complexity.proxy-cyclomatic\"", file.Stdout.Value)

    [<Fact>]
    let ``store commands on an uninitialized directory are unavailable evidence`` () =
        let output = Support.run [ "history"; "--store"; Path.Combine(Support.tempDirectory (), "none") ]
        Assert.Equal(3, output.ExitCode)
        Assert.Contains("store-not-initialized", output.Stderr.Value)
