namespace Dokimos.Cli

open System
open System.IO
open System.Text.Json
open Dokimos.Core

module Program =
    let options = JsonSerializerOptions(WriteIndented = true)

    let sourceFiles root =
        Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun p -> not (p.Contains(string Path.DirectorySeparatorChar + "obj" + string Path.DirectorySeparatorChar)))
        |> Seq.filter (fun p -> not (p.Contains(string Path.DirectorySeparatorChar + "bin" + string Path.DirectorySeparatorChar)))
        |> Seq.sort
        |> Seq.toList

    [<EntryPoint>]
    let main args =
        match args |> Array.toList with
        | ["compare"; beforePath; afterPath] when File.Exists beforePath && File.Exists afterPath ->
            let before = JsonSerializer.Deserialize<CanonicalSnapshot>(File.ReadAllText beforePath, options)
            let after = JsonSerializer.Deserialize<CanonicalSnapshot>(File.ReadAllText afterPath, options)
            let comparison = CanonicalComparison.compare before after
            Console.WriteLine(JsonSerializer.Serialize(comparison, options))
            0
        | ["measure"; path] when File.Exists path ->
            let source = File.ReadAllText path
            let structural = Structural.measure path source
            let quality = FSharpQuality.measure path source
            let complexity = Complexity.measure path source
            let agent = AgentQuality.fromMetrics structural quality
            let result = {| path=path; structural=structural; quality=quality; complexity=complexity; agent=agent |}
            Console.WriteLine(JsonSerializer.Serialize(result, options))
            0
        | ["analyze"; root] when Directory.Exists root ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let result = RepositoryAnalysis.analyze 6 Map.empty sources
            Console.WriteLine(JsonSerializer.Serialize(Wire.repository result, options))
            0
        | ["snapshot"; root; "--git-history"; historyPath; "--repository"; repository; "--revision"; revision; "--ref"; refName] when Directory.Exists root && File.Exists historyPath ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let temporal = File.ReadAllText(historyPath) |> GitHistory.parse |> GitHistory.summarize
            let analysis = RepositoryAnalysis.analyze 6 temporal sources
            let snapshot = CanonicalSnapshot.fromAnalysis repository revision refName DateTimeOffset.UtcNow analysis
            Console.WriteLine(JsonSerializer.Serialize(snapshot, options))
            0
        | ["analyze"; root; "--git-history"; historyPath] when Directory.Exists root && File.Exists historyPath ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let temporal = File.ReadAllText(historyPath) |> GitHistory.parse |> GitHistory.summarize
            let result = RepositoryAnalysis.analyze 6 temporal sources
            Console.WriteLine(JsonSerializer.Serialize(Wire.repository result, options))
            0
        | _ ->
            Console.Error.WriteLine("Usage: dokimos compare <before-snapshot> <after-snapshot> | dokimos measure <source-file> | dokimos analyze <source-directory> [--git-history <numstat-file>] | dokimos snapshot <source-directory> --git-history <file> --repository <owner/repo> --revision <sha> --ref <ref>")
            2
