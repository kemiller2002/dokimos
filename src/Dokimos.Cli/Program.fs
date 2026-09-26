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
            Console.WriteLine(JsonSerializer.Serialize(result, options))
            0
        | ["analyze"; root; "--git-history"; historyPath] when Directory.Exists root && File.Exists historyPath ->
            let sources = sourceFiles root |> List.map (fun path -> path, File.ReadAllText path)
            let temporal = File.ReadAllText(historyPath) |> GitHistory.parse |> GitHistory.summarize
            let result = RepositoryAnalysis.analyze 6 temporal sources
            Console.WriteLine(JsonSerializer.Serialize(result, options))
            0
        | _ ->
            Console.Error.WriteLine("Usage: dokimos measure <source-file> | dokimos analyze <source-directory> [--git-history <numstat-file>]")
            2
