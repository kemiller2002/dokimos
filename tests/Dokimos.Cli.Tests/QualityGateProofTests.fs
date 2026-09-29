namespace Dokimos.Cli.Tests

open System.IO
open System.Text.Json
open Xunit

/// DOK-OPS-004: deterministic end-to-end proof through the real CLI:
/// real sources -> snapshot -> compare -> evaluate -> process exit code.
module QualityGateProofTests =
    let baselineSource =
        "module Calculator\n\nlet add x y = x + y\n\nlet mutable total = 0\n\nlet describe value =\n    match value with\n    | 0 -> \"zero\"\n    | _ -> \"other\"\n"

    let deterioratedSource =
        baselineSource + "\nlet mutable scratch = 0\nlet mutable cache: obj = null\n"

    let improvedSource =
        "module Calculator\n\nlet add x y = x + y\n\nlet describe value =\n    match value with\n    | 0 -> \"zero\"\n    | _ -> \"other\"\n"

    type Workspace =
        { Root: string
          Source: string
          Policy: string }

    let workspace policy =
        let root = Support.tempDirectory ()
        { Root = root
          Source = Path.Combine(root, "src")
          Policy = Support.write root "policy.json" policy }

    let snapshot (ws: Workspace) name (source: string) warnings failedTests =
        Support.write ws.Source "Calculator.fs" source |> ignore
        let log = Support.write ws.Root (name + ".build.log") (Support.buildLog warnings 0)
        let trx = Support.write ws.Root (name + ".trx") (Support.trx 10 (10 - failedTests) failedTests)
        let output =
            Support.run
                [ "snapshot"; ws.Source; "--repository"; "echelon/fixture"; "--revision"; name; "--ref"; "main"
                  "--build-log"; log; "--test-results"; trx ]
        Assert.Equal(0, output.ExitCode)
        Support.assertSchemaValid "dokimos-snapshot.schema.json" output.Stdout.Value
        Support.write ws.Root (name + ".snapshot.json") output.Stdout.Value

    let kind metricId (comparisonJson: string) =
        use doc = JsonDocument.Parse comparisonJson
        doc.RootElement.GetProperty("MetricChanges").EnumerateArray()
        |> Seq.find (fun c -> c.GetProperty("MetricId").Text = metricId && c.GetProperty("Scope").Text.EndsWith "Calculator.fs")
        |> fun c -> c.GetProperty("Kind").Text

    let property (name: string) (json: string) =
        use doc = JsonDocument.Parse json
        doc.RootElement.GetProperty(name).ToString()

    let repositoryPolicy = File.ReadAllText(Support.repoPath "config/dokimos-policy.json")

    let strictRegressionPolicy =
        """{"schemaVersion":"1.1.0","baseline":"fixture","requiredEvidence":["build.compiler-warnings","tests.failed"],"ratchets":[{"metricId":"build.compiler-warnings","metricVersion":1,"bestAccepted":0,"preference":"lower-is-better","disposition":"fail"}],"regressions":{"disposition":"fail"},"introducedFindings":{"disposition":"fail"}}"""

    [<Fact>]
    let ``deterioration is a regression, a policy failure, and exit code 4`` () =
        let ws = workspace strictRegressionPolicy
        let baseline = snapshot ws "baseline" baselineSource 0 0
        let current = snapshot ws "deteriorated" deterioratedSource 0 0

        let comparison = Support.run [ "compare"; baseline; current ]
        Assert.Equal("deteriorated", kind "source.mutable-bindings" comparison.Stdout.Value)
        Assert.Equal("deteriorated", kind "quality.type-weakening-indicators" comparison.Stdout.Value)

        let result = Support.execute [ "evaluate"; "--baseline"; baseline; "--current"; current; "--policy"; ws.Policy ]
        Assert.Equal(4, result.ExitCode)
        Assert.Equal("", result.Stderr)
        Support.assertSchemaValid "dokimos-evaluation.schema.json" result.Stdout
        Assert.Equal("failed", property "Disposition" result.Stdout)
        Assert.Contains("deteriorated from 1 to 3", result.Stdout)

    [<Fact>]
    let ``new compiler warnings fail the repository's own policy`` () =
        let ws = workspace repositoryPolicy
        let baseline = snapshot ws "baseline" baselineSource 0 0
        let current = snapshot ws "warnings" baselineSource 2 0
        let result = Support.execute [ "evaluate"; "--baseline"; baseline; "--current"; current; "--policy"; ws.Policy ]
        Assert.Equal(4, result.ExitCode)
        Assert.Contains("build.compiler-warnings is 2; the best accepted state is 0", result.Stdout)

    [<Fact>]
    let ``failing tests fail the repository's own policy`` () =
        let ws = workspace repositoryPolicy
        let baseline = snapshot ws "baseline" baselineSource 0 0
        let current = snapshot ws "red" baselineSource 0 1
        Assert.Equal(4, (Support.execute [ "evaluate"; "--baseline"; baseline; "--current"; current; "--policy"; ws.Policy ]).ExitCode)

    [<Fact>]
    let ``improvement is reported and the policy permits continuation`` () =
        let ws = workspace strictRegressionPolicy
        let baseline = snapshot ws "baseline" baselineSource 0 0
        let current = snapshot ws "improved" improvedSource 0 0

        let comparison = Support.run [ "compare"; baseline; current ]
        Assert.Equal("improved", kind "source.mutable-bindings" comparison.Stdout.Value)

        let result = Support.execute [ "evaluate"; "--baseline"; baseline; "--current"; current; "--policy"; ws.Policy ]
        Assert.Equal(0, result.ExitCode)
        Assert.Equal("passed", property "Disposition" result.Stdout)
        use doc = JsonDocument.Parse result.Stdout
        Assert.True(doc.RootElement.GetProperty("Summary").GetProperty("Improved").GetInt32() >= 1)

    [<Fact>]
    let ``missing required evidence stays unavailable and exits 3`` () =
        let ws = workspace repositoryPolicy
        let baseline = snapshot ws "baseline" baselineSource 0 0
        Support.write ws.Source "Calculator.fs" baselineSource |> ignore
        let bare = Support.run [ "snapshot"; ws.Source; "--repository"; "echelon/fixture"; "--revision"; "bare" ]
        let current = Support.write ws.Root "bare.snapshot.json" bare.Stdout.Value
        let result = Support.execute [ "evaluate"; "--baseline"; baseline; "--current"; current; "--policy"; ws.Policy ]
        Assert.Equal(3, result.ExitCode)
        Assert.Equal("required-evidence-unavailable", property "Disposition" result.Stdout)
        use doc = JsonDocument.Parse result.Stdout
        let required =
            doc.RootElement.GetProperty("Outcomes").EnumerateArray()
            |> Seq.filter (fun o -> o.GetProperty("Rule").Text = "required-evidence")
            |> Seq.map (fun o -> o.GetProperty("State").Text)
            |> Seq.toList
        Assert.NotEmpty(required)
        Assert.All(required, fun state -> Assert.Equal("unavailable", state))
