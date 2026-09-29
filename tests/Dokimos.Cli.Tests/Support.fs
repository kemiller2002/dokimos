namespace Dokimos.Cli.Tests

open System
open System.Diagnostics
open System.IO
open System.Text.Json.Nodes
open Json.Schema
open Dokimos.Cli

/// Test harness for the CLI product boundary.
module Support =
    let repositoryRoot =
        let rec up (dir: DirectoryInfo) =
            if File.Exists(Path.Combine(dir.FullName, "Dokimos.sln")) then dir.FullName else up dir.Parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let repoPath (relative: string) = Path.Combine(repositoryRoot, relative)

    let tempDirectory () =
        let path = Path.Combine(Path.GetTempPath(), "dokimos-tests", Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory path |> ignore
        path

    let write (directory: string) (name: string) (content: string) =
        let path = Path.Combine(directory, name)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)
        path

    /// In-process invocation of the production dispatch and serialization path.
    let run (args: string list) = Program.run args

    type ProcessResult = { ExitCode: int; Stdout: string; Stderr: string }

    /// Out-of-process invocation of the built CLI, proving real exit codes and
    /// stream separation.
    let execute (args: string list) =
        let dll =
            [ Path.Combine(AppContext.BaseDirectory, "Dokimos.Cli.dll") ]
            |> List.tryFind File.Exists
            |> Option.defaultWith (fun () -> failwith "Dokimos.Cli.dll not found next to the tests")
        let info = ProcessStartInfo("dotnet", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
        info.ArgumentList.Add dll
        args |> List.iter info.ArgumentList.Add
        use proc = Process.Start info
        let stdout = proc.StandardOutput.ReadToEndAsync()
        let stderr = proc.StandardError.ReadToEndAsync()
        proc.WaitForExit()
        { ExitCode = proc.ExitCode; Stdout = stdout.Result; Stderr = stderr.Result }

    let private schemas = Collections.Concurrent.ConcurrentDictionary<string, JsonSchema>()

    /// Validates a document against a schema in schemas/. Returns the
    /// failing locations so assertion messages explain what is wrong.
    let schemaErrors (schemaFile: string) (json: string) =
        let schema = schemas.GetOrAdd(schemaFile, fun f -> JsonSchema.FromFile(repoPath (Path.Combine("schemas", f))))
        let result = schema.Evaluate(JsonNode.Parse json, EvaluationOptions(OutputFormat = OutputFormat.List))
        if result.IsValid then []
        else
            result.Details
            |> Seq.filter (fun d -> d.HasErrors)
            |> Seq.collect (fun d -> d.Errors |> Seq.map (fun kv -> $"{d.InstanceLocation} {kv.Key}: {kv.Value}"))
            |> Seq.toList

    let assertSchemaValid schemaFile json =
        match schemaErrors schemaFile json with
        | [] -> ()
        | errors -> failwith (String.Join("\n", errors))

    let buildLog warnings errors =
        $"  Dokimos.Core -> /tmp/Dokimos.Core.dll\n\nBuild succeeded.\n    {warnings} Warning(s)\n    {errors} Error(s)\n\nTime Elapsed 00:00:01.00\n"

    let trx total passed failed =
        $"""<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary outcome="Completed"><Counters total="{total}" executed="{total}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" /></ResultSummary></TestRun>"""
