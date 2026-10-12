namespace Dokimos.Cli

open System
open System.IO
open System.Text
open Dokimos.Core

/// Runs the frozen oracle against the candidate's committed generated code.
/// Dokimos starts, watches and records the run itself; nothing a constructor
/// reports is accepted in its place. Executing candidate code is opt-in
/// (`--oracle-runner`), and the code runs in a fresh temporary directory.
module ConstructionOracleRunner =
    let private tierRank tier =
        match tier with
        | "semantic-model" -> 0
        | "transition" -> 1
        | "orchestration" -> 2
        | _ -> 3

    let harness () =
        let assembly = typeof<ConstructionInputs>.Assembly
        match assembly.GetManifestResourceStream "Dokimos.Core.ConstructionHarness.fsx" with
        | null -> Error "the Dokimos oracle harness resource is missing"
        | stream ->
            use reader = new StreamReader(stream, Encoding.UTF8)
            Ok(reader.ReadToEnd())

    let private stamp () = DateTimeOffset.UtcNow

    /// Best-effort removal of the run's scratch copy. A leftover directory
    /// changes no evidence, so a failure to remove it is not reported.
    let private remove (directory: string) =
        try Directory.Delete(directory, true) with
        | :? IOException as ex -> ignore ex.Message
        | :? UnauthorizedAccessException as ex -> ignore ex.Message

    let run (runner: string) (oracle: ConstructionOracle) (oracleDigest: string) (manifestPath: string) (candidate: CandidateSnapshot) =
        match harness (), candidate.Contents |> Map.tryFind manifestPath |> Option.map (Encoding.UTF8.GetString >> ConstructionModels.readManifest) with
        | Error reason, _ -> NotExecuted reason
        | _, None | _, Some(Error _) -> NotExecuted "the candidate manifest is unreadable, so no generated files can be loaded"
        | Ok script, Some(Ok manifest) ->
            let mount = manifestPath.Substring(0, max 0 (manifestPath.Length - (manifest.OwnershipRoot + "construction-manifest.json").Length))
            let sources =
                manifest.Artifacts
                |> List.filter (fun a -> a.Path.EndsWith(".fs", StringComparison.Ordinal))
                |> List.sortBy (fun a -> tierRank a.Tier, a.Path)
                |> List.choose (fun a -> Map.tryFind (mount + a.Path) candidate.Contents |> Option.map (fun bytes -> a.Path, bytes))
            if sources.IsEmpty then NotExecuted "the candidate commit contains none of the manifest's generated source files"
            else
                let work = Path.Combine(Path.GetTempPath(), "dokimos-oracle", Guid.NewGuid().ToString("N"))
                let write (relative: string) (bytes: byte[]) =
                    let path = Path.Combine(work, "candidate", relative)
                    Directory.CreateDirectory(Path.GetDirectoryName path |> string) |> ignore
                    File.WriteAllBytes(path, bytes)
                    "candidate/" + relative
                Directory.CreateDirectory work |> ignore
                let loads = sources |> List.map (fun (p, bytes) -> write p bytes)
                let harnessText =
                    "#load " + (loads |> List.map (fun p -> "\"" + p + "\"") |> String.concat " ") + "\n" + script
                File.WriteAllText(Path.Combine(work, "harness.fsx"), harnessText)
                File.WriteAllText(Path.Combine(work, "cases.json"), ConstructionOracle.harnessInput oracle)
                let version =
                    match ConstructionGit.run runner [ "--version" ] (Some work) (TimeSpan.FromMinutes 1.0) with
                    | Ok v when v.ExitCode = 0 -> (Encoding.UTF8.GetString v.Stdout).Trim()
                    | _ -> "unknown"
                let started = stamp ()
                let args = [ "fsi"; "--quiet"; "harness.fsx"; "cases.json" ]
                let outcome = ConstructionGit.run runner args (Some work) (TimeSpan.FromMinutes 5.0)
                let finished = stamp ()
                remove work
                match outcome with
                | Error reason -> NotExecuted("the oracle runner could not run: " + reason)
                | Ok o ->
                    let stdout = Encoding.UTF8.GetString o.Stdout
                    let record =
                        { Runner = runner
                          RunnerVersion = version
                          CommandLine = runner + " " + String.concat " " args
                          StartedAt = started
                          FinishedAt = finished
                          ExitCode = o.ExitCode
                          StdoutDigest = ConstructionJson.sha256Bytes o.Stdout
                          HarnessDigest = ConstructionJson.sha256Text harnessText
                          CandidateCommit = candidate.Commit
                          OracleDigest = oracleDigest }
                    let observations = ConstructionOracle.readObservations stdout
                    if o.ExitCode <> 0 && observations.IsEmpty then
                        let diagnostics = (o.Stderr + "\n" + stdout).Trim()
                        CompileFailed(record, (if diagnostics.Length > 4000 then diagnostics.Substring(0, 4000) else diagnostics))
                    else Executed(record, observations)
