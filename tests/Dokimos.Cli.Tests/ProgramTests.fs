namespace Dokimos.Cli.Tests

open System.IO
open Xunit
open Dokimos.Cli

module ProgramTests =
    let withTemp content action =
        let path = Path.GetTempFileName()
        try File.WriteAllText(path, content); action path
        finally File.Delete path

    [<Fact>]
    let ``null snapshot is rejected explicitly`` () =
        withTemp "null" (fun path -> Assert.Equal(Error "snapshot-deserialized-to-null", Program.tryReadSnapshot path))

    [<Fact>]
    let ``malformed snapshot is rejected explicitly`` () =
        withTemp "{" (fun path -> Assert.Equal(Error "malformed-snapshot-json", Program.tryReadSnapshot path))

    [<Fact>]
    let ``unsupported snapshot schema is rejected explicitly`` () =
        let json = """{"SchemaVersion":"9.9.9","SnapshotId":"x","Repository":"r","Revision":"x","Ref":"main","CollectedAt":"1970-01-01T00:00:00+00:00","Collector":"test","Metrics":[],"Findings":[]}"""
        withTemp json (fun path -> Assert.Equal(Error "unsupported-snapshot-schema:9.9.9", Program.tryReadSnapshot path))

    [<Fact>]
    let ``structurally invalid snapshot names the offending field`` () =
        let json = """{"SchemaVersion":"2.0.0","Contract":"dokimos.snapshot","SnapshotId":"x","Repository":"r","Revision":"x","Ref":"main","CollectedAt":"1970-01-01T00:00:00+00:00","Collector":"test","Metrics":[],"Findings":[]}"""
        withTemp json (fun path ->
            match Program.tryReadSnapshot path with
            | Error e -> Assert.Contains("missing required field 'Producer'", e)
            | Ok _ -> failwith "expected rejection")

    [<Fact>]
    let ``unknown commands are invalid invocations with a diagnostic on stderr`` () =
        let output = Support.run [ "frobnicate" ]
        Assert.Equal(2, output.ExitCode)
        Assert.Equal(None, output.Stdout)
        Support.assertSchemaValid "dokimos-diagnostic.schema.json" output.Stderr.Value

    [<Fact>]
    let ``unknown options are rejected rather than ignored`` () =
        let output = Support.run [ "evaluate"; "--baseline"; "a"; "--current"; "b"; "--policy"; "c"; "--strict"; "yes" ]
        Assert.Equal(2, output.ExitCode)
        Assert.Contains("unknown option --strict", output.Stderr.Value)

    [<Fact>]
    let ``version identifies the release and supported contracts`` () =
        let output = Support.run [ "version" ]
        Assert.Equal(0, output.ExitCode)
        Assert.Contains("\"DokimosVersion\": \"" + Dokimos.Core.DokimosInfo.version + "\"", output.Stdout.Value)
        Assert.Contains("\"2.0.0\"", output.Stdout.Value)

    [<Fact>]
    let ``capabilities declare unsupported metrics as unavailable`` () =
        let output = Support.run [ "capabilities" ]
        Assert.Equal(0, output.ExitCode)
        use doc = System.Text.Json.JsonDocument.Parse output.Stdout.Value
        let apiAdded =
            doc.RootElement.GetProperty("Metrics").EnumerateArray()
            |> Seq.find (fun m -> m.GetProperty("MetricId").GetString() = "api.added")
        Assert.Equal("unavailable", apiAdded.GetProperty("Support").GetString())

    [<Fact>]
    let ``schema validation rejects a comparison with an unknown state tag`` () =
        let json = """{"Contract":"dokimos.comparison","SchemaVersion":"1.0.0","DokimosVersion":"x","Before":{},"After":{},"Summary":{},"MetricChanges":[{"Kind":"MetricImproved"}],"FindingChanges":[]}"""
        Assert.NotEmpty(Support.schemaErrors "dokimos-comparison.schema.json" json)
