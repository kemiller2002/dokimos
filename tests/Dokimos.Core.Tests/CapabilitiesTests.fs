namespace Dokimos.Core.Tests

open System.IO
open System.Text.Json
open Xunit
open Dokimos.Domain
open Dokimos.Core

module CapabilitiesTests =
    let repositoryRoot =
        let rec up (dir: DirectoryInfo) =
            if File.Exists(Path.Combine(dir.FullName, "Dokimos.sln")) then dir.FullName
            else
                match dir.Parent with
                | null -> failwith "Dokimos.sln not found above the test directory"
                | parent -> up parent
        up (DirectoryInfo(System.AppContext.BaseDirectory))

    [<Fact>]
    let ``every analyzer metric has a definition that names that analyzer`` () =
        for analyzer in Capabilities.analyzers do
            for metricId in analyzer.Metrics do
                let definition = Capabilities.tryDefinition metricId
                Assert.True(definition.IsSome, $"{metricId} has no definition")
                Assert.Equal(Some analyzer.AnalyzerId, definition.Value.Analyzer)

    [<Fact>]
    let ``every analyzer declares limitations and determinism`` () =
        for analyzer in Capabilities.analyzers do
            Assert.NotEmpty(analyzer.Limitations)
            Assert.False(System.String.IsNullOrWhiteSpace analyzer.Language)

    [<Fact>]
    let ``published metric catalog matches the interpreted definitions`` () =
        use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "config", "metric-catalog.json")))
        let published =
            doc.RootElement.GetProperty("metrics").EnumerateArray()
            |> Seq.filter (fun m -> not (m.GetProperty("id").Text.StartsWith "correlation."))
            |> Seq.map (fun m -> m.GetProperty("id").Text, m.GetProperty("version").GetInt32(), m.GetProperty("unit").Text, m.GetProperty("preference").Text)
            |> Set.ofSeq
        let interpreted =
            Capabilities.metrics
            |> List.map (fun d ->
                let preference = match d.Preference with PreferLower -> "lower-is-better" | PreferHigher -> "higher-is-better" | Contextual -> "contextual"
                d.MetricId, d.Version, d.Unit, preference)
            |> Set.ofList
        Assert.Equal<Set<_>>(interpreted, published)

    [<Fact>]
    let ``unsupported metrics are emitted as unavailable, never zero`` () =
        let metrics = CanonicalSnapshot.unsupportedMetrics ()
        Assert.NotEmpty(metrics)
        for m in metrics do
            Assert.Equal(Unavailable Unsupported, m.Measurement)
            Assert.Equal(None, CanonicalMetric.value m)
