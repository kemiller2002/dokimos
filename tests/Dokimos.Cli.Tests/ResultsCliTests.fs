namespace Dokimos.Cli.Tests

open System.IO
open System.Text.Json
open Xunit

/// DOK-OPS-015/018: the results contract is a decomposable projection of
/// real evidence, and hotspots name the independent dimensions behind them.
module ResultsCliTests =
    let hotspotSource =
        "module Busy\n\nlet classify x =\n    if x > 0 && x < 10 then\n        match x with\n        | 1 -> \"a\"\n        | 2 -> \"b\"\n        | 3 -> \"c\"\n        | 4 -> \"d\"\n        | 5 -> \"e\"\n        | 6 -> \"f\"\n        | _ -> \"z\"\n    elif x < 0 || x > 100 then \"out\"\n    else \"mid\"\n"

    let history (path: string) =
        [ for i in 1..6 -> $"commit c{i} 2026-09-0{i}T00:00:00Z\n30\t10\t{path}" ] |> String.concat "\n"

    let setup () =
        let root = Support.tempDirectory ()
        let src = Path.Combine(root, "src")
        let file = Support.write src "Busy.fs" hotspotSource
        let scope = Path.GetRelativePath(System.IO.Directory.GetCurrentDirectory(), file).Replace('\\', '/')
        let git = Support.write root "git.txt" (history scope)
        let log = Support.write root "build.log" (Support.buildLog 0 0)
        let trx = Support.write root "t.trx" (Support.trx 3 3 0)
        let snap = Support.run [ "snapshot"; src; "--repository"; "o/r"; "--revision"; "r1"; "--git-history"; git; "--build-log"; log; "--test-results"; trx ]
        root, Support.write root "s.json" snap.Stdout.Value

    [<Fact>]
    let ``results are schema-valid and hotspots decompose into temporal and structural evidence`` () =
        let _, snapshot = setup ()
        let output = Support.run [ "results"; "--baseline"; snapshot; "--current"; snapshot; "--policy"; Support.repoPath "config/dokimos-policy.json" ]
        Assert.Equal(0, output.ExitCode)
        Support.assertSchemaValid "dokimos-results.schema.json" output.Stdout.Value
        use doc = JsonDocument.Parse output.Stdout.Value
        let hotspot = doc.RootElement.GetProperty("Hotspots").EnumerateArray() |> Seq.find (fun h -> h.GetProperty("Kind").Text = "maintainability-hotspot")
        let dimensions = hotspot.GetProperty("Dimensions").EnumerateArray() |> Seq.map _.Text |> Set.ofSeq
        Assert.Contains("structural", dimensions)
        Assert.Contains("temporal", dimensions)
        let signals = hotspot.GetProperty("Signals").EnumerateArray() |> Seq.map (fun s -> s.GetProperty("Kind").Text) |> Set.ofSeq
        Assert.Contains("structural-complexity", signals)
        Assert.Contains("frequent-change", signals)

    [<Fact>]
    let ``results explain every unavailable metric`` () =
        let _, snapshot = setup ()
        let output = Support.run [ "results"; "--baseline"; snapshot; "--current"; snapshot; "--policy"; Support.repoPath "config/dokimos-policy.json" ]
        use doc = JsonDocument.Parse output.Stdout.Value
        let unavailable = doc.RootElement.GetProperty("Unavailable").EnumerateArray() |> Seq.map (fun u -> u.GetProperty("Subject").Text, u.GetProperty("Explanation").Text) |> Map.ofSeq
        Assert.Contains("coverage.line-rate", unavailable.Keys)
        Assert.Contains("not supplied", unavailable["coverage.line-rate"])
        Assert.Contains("No analyzer", unavailable["api.added"])
        Assert.Equal(unavailable.Count, doc.RootElement.GetProperty("Overview").GetProperty("MetricsUnavailable").GetInt32())
