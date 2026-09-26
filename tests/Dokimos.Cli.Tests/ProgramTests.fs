namespace Dokimos.Cli.Tests

open System
open System.IO
open Xunit
open Dokimos.Cli

module ProgramTests =
    let withTemp content action =
        let path = Path.GetTempFileName()
        try File.WriteAllText(path,content); action path
        finally File.Delete path

    [<Fact>]
    let ``null snapshot is rejected explicitly`` () =
        withTemp "null" (fun path ->
            Assert.Equal(Error "snapshot-deserialized-to-null", Program.tryReadSnapshot path))

    [<Fact>]
    let ``malformed snapshot is rejected explicitly`` () =
        withTemp "{" (fun path ->
            Assert.Equal(Error "malformed-snapshot-json", Program.tryReadSnapshot path))

    [<Fact>]
    let ``unsupported snapshot schema is rejected explicitly`` () =
        let json = """{"SchemaVersion":"2.0.0","SnapshotId":"x","Repository":"r","Revision":"x","Ref":"main","CollectedAt":"1970-01-01T00:00:00+00:00","Collector":"test","Metrics":[],"Findings":[]}"""
        withTemp json (fun path ->
            Assert.Equal(Error "unsupported-snapshot-schema:2.0.0", Program.tryReadSnapshot path))
