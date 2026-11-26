namespace Dokimos.Cli.Tests

open System.IO
open System.Text.Json
open Xunit

/// Contract tests for `dokimos architecture check` (RQ-DOKIMOS-ARCH-001):
/// exit codes, the dokimos.architecture report schema, decision-record
/// exceptions and the apiBoundaries baseline configuration.
module ArchitectureCliTests =
    let at = "2026-11-26T12:00:00Z"

    let declaration cacheReadiness =
        """{"schema":"echelon.reliability/v1","systemClass":"transactional-application","tenets":{"continuousAvailability":{"applicability":"applicable"},"cacheReadiness":"""
        + cacheReadiness
        + ""","reliabilityByConstruction":{"applicability":"applicable"}}}"""

    let fsproj (body: string) = $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>{body}</ItemGroup></Project>"""

    let baseline =
        """{"contract":"dokimos.ratchet-baseline","schemaVersion":"1.0.0","repository":"example/app","acceptedAt":"2026-11-01T00:00:00Z","configuration":{"sources":["src"],"generated":["**/bin/**","**/obj/**"],"largeFileLines":400,"forbiddenReferences":[],"apiBoundaries":[{"api":"App.Api","contracts":["App.Contracts"]}]},"rules":{}}"""

    let repo (apiBody: string) (withDeclaration: string option) =
        let root = Support.tempDirectory ()
        Support.write root "src/App.Api/App.Api.fsproj" (fsproj apiBody) |> ignore
        Support.write root "src/App.Contracts/App.Contracts.fsproj" (fsproj "") |> ignore
        Support.write root "src/App.Domain/App.Domain.fsproj" (fsproj "") |> ignore
        // Build output is never analyzed.
        Support.write root "src/App.Api/obj/App.Api.fsproj" (fsproj """<PackageReference Include="RabbitMQ.Client" />""") |> ignore
        Support.write root "quality/baseline.json" baseline |> ignore
        withDeclaration |> Option.iter (fun d -> Support.write root ".echelon/reliability.json" d |> ignore)
        root

    let violatingApi =
        """<ProjectReference Include="../App.Domain/App.Domain.fsproj" /><PackageReference Include="StackExchange.Redis" Version="2.8.0" /><PackageReference Include="Confluent.Kafka" Version="2.5.0" />"""

    let cleanApi = """<ProjectReference Include="../App.Contracts/App.Contracts.fsproj" />"""

    let check root =
        let output = Support.run [ "architecture"; "check"; "--root"; root; "--at"; at; "--json" ]
        Support.assertSchemaValid "dokimos-architecture.schema.json" output.Stdout.Value
        output

    let field (name: string) (output: Dokimos.Cli.Output) =
        use doc = JsonDocument.Parse output.Stdout.Value
        doc.RootElement.GetProperty(name).Clone()

    let scopes output =
        (field "Findings" output).EnumerateArray()
        |> Seq.map (fun f -> f.GetProperty("RuleId").Text + " " + f.GetProperty("Scope").Text + " " + f.GetProperty("Kind").Text)
        |> List.ofSeq

    [<Fact>]
    let ``the violating fixture fails with exit 4 and one finding per rule edge`` () =
        let output = check (repo violatingApi (Some(declaration """{"applicability":"applicable"}""")))
        Assert.Equal(4, output.ExitCode)
        Assert.Equal("violation", (field "Verdict" output).Text)
        Assert.Equal<string list>(
            [ "DOK-ARCH-001 App.Api->App.Domain violation"
              "DOK-ARCH-005 src/App.Api/App.Api.fsproj#StackExchange.Redis violation"
              "DOK-ARCH-006 src/App.Api/App.Api.fsproj#Confluent.Kafka violation" ],
            scopes output
        )

    [<Fact>]
    let ``the clean fixture passes with exit 0`` () =
        let output = check (repo cleanApi (Some(declaration """{"applicability":"applicable"}""")))
        Assert.Equal(0, output.ExitCode)
        Assert.Equal("pass", (field "Verdict" output).Text)
        Assert.Equal(3, (field "Summary" output).GetProperty("RulesEvaluated").GetInt32())

    [<Fact>]
    let ``without a declaration nothing is evaluated and the verdict is not a pass`` () =
        let output = check (repo violatingApi None)
        Assert.Equal(0, output.ExitCode)
        Assert.Equal("not-evaluated", (field "Verdict" output).Text)
        Assert.Equal(3, (field "Summary" output).GetProperty("RulesNotApplicable").GetInt32())
        Assert.Empty(scopes output)

    [<Fact>]
    let ``a not-applicable tenet reports its rule as not-applicable`` () =
        let output = check (repo violatingApi (Some(declaration """{"applicability":"not-applicable","reason":"No server-side queries."}""")))
        let cache = (field "Rules" output).EnumerateArray() |> Seq.find (fun r -> r.GetProperty("RuleId").Text = "DOK-ARCH-005")
        Assert.Equal("not-applicable", cache.GetProperty("State").Text)
        Assert.Equal(4, output.ExitCode)

    [<Fact>]
    let ``a malformed declaration is unavailable with exit 3`` () =
        let output = check (repo cleanApi (Some "{ not json"))
        Assert.Equal(3, output.ExitCode)
        Assert.Equal("invalid", (field "Declaration" output).GetProperty("State").Text)

    [<Fact>]
    let ``a schema-valid decision-record exception accepts a dependency`` () =
        let root = repo """<ProjectReference Include="../App.Contracts/App.Contracts.fsproj" /><PackageReference Include="StackExchange.Redis" />""" (Some(declaration """{"applicability":"applicable"}"""))
        let exceptions =
            Support.write
                root
                "quality/exceptions.json"
                """{"contract":"dokimos.quality-exceptions","schemaVersion":"1.0.0","exceptions":[{"id":"EXC-ARCH-0001","ruleId":"DOK-ARCH-005","scope":"src/App.Api/App.Api.fsproj#StackExchange.Redis","rationale":"Shared session cache until the store migration completes.","owner":"kemiller2002","created":"2026-11-01","expires":"2027-01-31","evidence":["DF-APP-2026-A001"]}]}"""
        Support.assertSchemaValid "dokimos-quality-exceptions.schema.json" (File.ReadAllText exceptions)
        let output = check root
        Assert.Equal(0, output.ExitCode)
        Assert.Equal<string list>([ "DOK-ARCH-005 src/App.Api/App.Api.fsproj#StackExchange.Redis accepted" ], scopes output)

    [<Fact>]
    let ``the baseline with apiBoundaries is schema-valid`` () =
        Support.assertSchemaValid "dokimos-ratchet-baseline.schema.json" baseline

    [<Fact>]
    let ``Dokimos itself declares no reliability tenet and is not evaluated`` () =
        let output = Support.run [ "architecture"; "check"; "--root"; Support.repositoryRoot; "--at"; at; "--json" ]
        Support.assertSchemaValid "dokimos-architecture.schema.json" output.Stdout.Value
        Assert.Equal("not-evaluated", (field "Verdict" output).Text)
