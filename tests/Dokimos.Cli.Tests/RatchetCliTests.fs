namespace Dokimos.Cli.Tests

open System.IO
open System.Text.Json
open Xunit

/// Contract tests for `dokimos ratchet` (#16): exit codes, the
/// dokimos.ratchet report schema, the baseline and exceptions file schemas,
/// and Dokimos' own self-application.
module RatchetCliTests =
    let at = "2026-10-05T12:00:00Z"

    let repo () =
        let root = Support.tempDirectory ()
        Support.write root "src/App/App.fsproj" """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="A.fs" /></ItemGroup></Project>""" |> ignore
        Support.write root "src/App/A.fs" "module A\n\nlet x = 1\n" |> ignore
        Support.write root "build.log" (Support.buildLog 0 0) |> ignore
        root

    let init root =
        Support.run [ "ratchet"; "baseline"; "init"; "--root"; root; "--repository"; "example/app"; "--build-log"; "build.log"; "--at"; at; "--write" ]

    let check root extra =
        Support.run ([ "ratchet"; "check"; "--root"; root; "--build-log"; "build.log"; "--at"; at; "--json" ] @ extra)

    let verdict (output: Dokimos.Cli.Output) =
        use doc = JsonDocument.Parse output.Stdout.Value
        doc.RootElement.GetProperty("Verdict").Text

    let validReport (output: Dokimos.Cli.Output) =
        Support.assertSchemaValid "dokimos-ratchet.schema.json" output.Stdout.Value
        output

    let exceptionsFile (expires: string) =
        $$"""{"contract":"dokimos.quality-exceptions","schemaVersion":"1.0.0","exceptions":[{"id":"EXC-0001","ruleId":"DOK-R004","scope":"src/App/A.fs","rationale":"Tracked follow-up for the parser rewrite.","owner":"kemiller2002","created":"2026-10-01","expires":"{{expires}}","evidence":["https://github.com/kemiller2002/dokimos/issues/16"],"allowedValue":1}]}"""

    [<Fact>]
    let ``init writes a schema-valid baseline and refuses to overwrite it`` () =
        let root = repo ()
        Assert.Equal(0, (init root).ExitCode)
        Support.assertSchemaValid "dokimos-ratchet-baseline.schema.json" (File.ReadAllText(Path.Combine(root, "quality/baseline.json")))
        Assert.Equal(2, (init root).ExitCode)

    [<Fact>]
    let ``unchanged repository passes with exit 0`` () =
        let root = repo ()
        init root |> ignore
        let output = check root [] |> validReport
        Assert.Equal(0, output.ExitCode)
        Assert.Equal("pass", verdict output)

    [<Fact>]
    let ``new debt is a regression with exit 4 and a complete finding`` () =
        let root = repo ()
        init root |> ignore
        Support.write root "src/App/A.fs" "module A\n\n// TODO handle the empty case\nlet x = 1\n" |> ignore
        let output = check root [] |> validReport
        Assert.Equal(4, output.ExitCode)
        use doc = JsonDocument.Parse output.Stdout.Value
        let finding = doc.RootElement.GetProperty("Findings").EnumerateArray() |> Seq.head
        Assert.Equal("DOK-R004", finding.GetProperty("RuleId").Text)
        Assert.Equal("src/App/A.fs", finding.GetProperty("Scope").Text)
        Assert.Equal(0m, finding.GetProperty("Before").GetDecimal())
        Assert.Equal(1m, finding.GetProperty("After").GetDecimal())
        Assert.Contains("quality/exceptions.json", finding.GetProperty("ExceptionProcess").Text)
        Assert.Contains("src/App/A.fs:3", (finding.GetProperty("Evidence").EnumerateArray() |> Seq.head).Text)

    [<Fact>]
    let ``a valid exception accepts the named regression, an expired one fails with exit 6`` () =
        let root = repo ()
        init root |> ignore
        Support.write root "src/App/A.fs" "module A\n\n// TODO handle the empty case\nlet x = 1\n" |> ignore
        let exceptions = Support.write root "quality/exceptions.json" (exceptionsFile "2026-12-31")
        Support.assertSchemaValid "dokimos-quality-exceptions.schema.json" (File.ReadAllText exceptions)
        let accepted = check root [] |> validReport
        Assert.Equal(0, accepted.ExitCode)
        Assert.Contains("excepted-regression", accepted.Stdout.Value)
        Support.write root "quality/exceptions.json" (exceptionsFile "2026-10-04") |> ignore
        let expired = check root [] |> validReport
        Assert.Equal(6, expired.ExitCode)
        Assert.Equal("invalid-exceptions", verdict expired)

    [<Fact>]
    let ``an incomplete exception is invalid with exit 6`` () =
        let root = repo ()
        init root |> ignore
        Support.write root "quality/exceptions.json" """{"contract":"dokimos.quality-exceptions","schemaVersion":"1.0.0","exceptions":[{"id":"EXC-0002","ruleId":"DOK-R004","scope":"src/App/A.fs","created":"2026-10-01","evidence":["x"],"allowedValue":1}]}""" |> ignore
        let output = check root [] |> validReport
        Assert.Equal(6, output.ExitCode)
        Assert.Contains("owner", output.Stdout.Value)
        Assert.Contains("expires or reviewCondition", output.Stdout.Value)

    [<Fact>]
    let ``missing baseline or unmeasurable enforced rule is unavailable with exit 3`` () =
        let root = repo ()
        let missing = check root [] |> validReport
        Assert.Equal(3, missing.ExitCode)
        Assert.Equal("unavailable", verdict missing)
        init root |> ignore
        let noLog = Support.run [ "ratchet"; "check"; "--root"; root; "--at"; at; "--json" ] |> validReport
        Assert.Equal(3, noLog.ExitCode)
        Assert.Contains("DOK-R001", noLog.Stdout.Value)

    [<Fact>]
    let ``baseline update tightens only and diff rejects loosening`` () =
        let root = repo ()
        Support.write root "src/App/A.fs" "module A\n\n// TODO one\n// FIXME two\nlet x = 1\n" |> ignore
        init root |> ignore
        let baselinePath = Path.Combine(root, "quality/baseline.json")
        let original = Support.write root "original-baseline.json" (File.ReadAllText baselinePath)
        Support.write root "src/App/A.fs" "module A\n\n// TODO one\nlet x = 1\n" |> ignore
        let improved = check root [] |> validReport
        Assert.Equal(0, improved.ExitCode)
        Assert.Contains("\"improvement\"", improved.Stdout.Value)
        let update = Support.run [ "ratchet"; "baseline"; "update"; "--root"; root; "--build-log"; "build.log"; "--at"; at; "--write" ]
        Assert.Equal(0, update.ExitCode)
        Assert.Contains("\"tightened\"", update.Stdout.Value)
        Support.assertSchemaValid "dokimos-ratchet-baseline.schema.json" (File.ReadAllText baselinePath)
        Assert.Equal(0, (Support.run [ "ratchet"; "baseline"; "diff"; "--from"; original; "--to"; baselinePath ]).ExitCode)
        Assert.Equal(4, (Support.run [ "ratchet"; "baseline"; "diff"; "--from"; baselinePath; "--to"; original ]).ExitCode)

    [<Fact>]
    let ``human output names the verdict`` () =
        let root = repo ()
        init root |> ignore
        let output = Support.run [ "ratchet"; "check"; "--root"; root; "--build-log"; "build.log"; "--at"; at ]
        Assert.StartsWith("dokimos ratchet: pass", output.Stdout.Value)

    [<Fact>]
    let ``the rule catalog lists every stable rule id`` () =
        let output = Support.run [ "ratchet"; "rules" ]
        Assert.Equal(0, output.ExitCode)
        for id in [ "DOK-R001"; "DOK-R002"; "DOK-R003"; "DOK-R004"; "DOK-R005"; "DOK-R006"; "DOK-R007"; "DOK-R008"; "DOK-G001" ] do
            Assert.Contains(id, output.Stdout.Value)

    /// Self-application: Dokimos passes its own accepted baseline, and its
    /// checked-in baseline and exceptions files satisfy their schemas.
    [<Fact>]
    let ``Dokimos passes its own ratchet`` () =
        Support.assertSchemaValid "dokimos-ratchet-baseline.schema.json" (File.ReadAllText(Support.repoPath "quality/baseline.json"))
        Support.assertSchemaValid "dokimos-quality-exceptions.schema.json" (File.ReadAllText(Support.repoPath "quality/exceptions.json"))
        let log = Support.write (Support.tempDirectory ()) "build.log" (Support.buildLog 0 0)
        let output = Support.run [ "ratchet"; "check"; "--root"; Support.repositoryRoot; "--build-log"; log; "--json" ] |> validReport
        Assert.True((0 = output.ExitCode), output.Stdout.Value)
