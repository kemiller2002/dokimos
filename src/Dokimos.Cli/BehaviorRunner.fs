namespace Dokimos.Cli

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open Dokimos.Core

/// Builds one candidate commit and drives its frozen adapter. Every input
/// file is materialized from Git objects at the resolved commit into a fresh
/// temporary directory, never from a working tree. Dokimos starts, watches
/// and records every process itself. This executes candidate code: run it
/// only where that is acceptable (CI or a disposable environment).
module BehaviorRunner =
    let private now () = DateTimeOffset.UtcNow

    let private buildTimeout = TimeSpan.FromMinutes 10.0
    let private driverTimeout = TimeSpan.FromMinutes 5.0

    let driverScript () =
        let assembly = typeof<BehaviorInputs>.Assembly
        match assembly.GetManifestResourceStream "Dokimos.Core.BehaviorDriver.fsx" with
        | null -> Error "the Dokimos behavior driver resource is missing"
        | stream ->
            use reader = new StreamReader(stream, Encoding.UTF8)
            Ok(reader.ReadToEnd())

    let private quote (arg: string) =
        if arg = "" || arg |> Seq.exists (fun c -> Char.IsWhiteSpace c || c = '"') then "\"" + arg.Replace("\"", "\\\"") + "\"" else arg

    /// Runs one process to completion and records what Dokimos observed.
    let private recorded purpose label fileName (args: string list) directory timeout =
        let started = now ()
        let outcome = ConstructionGit.run fileName args (Some directory) timeout
        let finished = now ()
        let record =
            { Purpose = purpose
              CommandLine = fileName + " " + (args |> List.map quote |> String.concat " ")
              WorkingDirectory = label
              StartedAt = started
              FinishedAt = finished
              Outcome =
                match outcome with
                | Ok _ -> "completed"
                | Error reason when reason.Contains("timed out", StringComparison.Ordinal) -> "timeout"
                | Error _ -> "could-not-start"
              ExitCode = (match outcome with Ok o -> Some o.ExitCode | Error _ -> None)
              StdoutDigest = (match outcome with Ok o -> ConstructionJson.sha256Bytes o.Stdout | Error _ -> "")
              StderrDigest = (match outcome with Ok o -> ConstructionJson.sha256Text o.Stderr | Error _ -> "") }
        record, outcome

    /// Reads many blobs through one `git cat-file --batch` process.
    let private catFileBatch (repository: string) (blobs: string list) : Result<Map<string, byte[]>, string> =
        try
            let info =
                ProcessStartInfo("git", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
            [ "-C"; repository; "cat-file"; "--batch" ] |> List.iter info.ArgumentList.Add
            use proc =
                match Process.Start info with
                | null -> raise (InvalidOperationException "git cat-file did not start")
                | p -> p
            use buffer = new MemoryStream()
            let copy = proc.StandardOutput.BaseStream.CopyToAsync buffer
            let stderr = proc.StandardError.ReadToEndAsync()
            for blob in List.distinct blobs do
                proc.StandardInput.Write(blob + "\n")
            proc.StandardInput.Close()
            if not (proc.WaitForExit(int (TimeSpan.FromMinutes 5.0).TotalMilliseconds)) then
                proc.Kill true
                Error "git cat-file --batch timed out"
            else
                copy.Wait()
                if proc.ExitCode <> 0 then Error("git cat-file --batch failed: " + stderr.Result.Trim())
                else
                    let bytes = buffer.ToArray()
                    let rec parse position (acc: Map<string, byte[]>) =
                        if position >= bytes.Length then Ok acc
                        else
                            let eol = Array.IndexOf(bytes, byte '\n', position)
                            if eol < 0 then Error "git cat-file --batch: truncated header"
                            else
                                let header = Encoding.ASCII.GetString(bytes, position, eol - position).Split(' ')
                                match header with
                                | [| sha; _; size |] ->
                                    let length = int size
                                    parse (eol + 1 + length + 1) (Map.add sha (Array.sub bytes (eol + 1) length) acc)
                                | _ -> Error("git cat-file --batch: " + String.Join(" ", header))
                    parse 0 Map.empty
        with
        | :? ComponentModel.Win32Exception as ex -> Error("git could not start: " + ex.Message)
        | :? InvalidOperationException as ex -> Error ex.Message
        | :? IOException as ex -> Error ex.Message

    /// Writes the commit's regular files under `root`. Symlinks and
    /// submodules are not materialized. Returns the file count and a digest
    /// over every (path, SHA-256) pair written.
    let materialize (repository: string) (entries: Map<string, TreeEntry>) (root: string) =
        let regular = entries |> Map.toList |> List.filter (fun (_, e) -> e.Mode.StartsWith("100", StringComparison.Ordinal))
        let rootFull = Path.GetFullPath(root + string Path.DirectorySeparatorChar)
        catFileBatch repository (regular |> List.map (fun (_, e) -> e.Blob))
        |> Result.bind (fun blobs ->
            let written =
                regular
                |> List.map (fun (path, entry) ->
                    let target = Path.GetFullPath(Path.Combine(root, path))
                    match Map.tryFind entry.Blob blobs with
                    | _ when not (target.StartsWith(rootFull, StringComparison.Ordinal)) -> Error("refusing a tree path outside the materialization root: " + path)
                    | None -> Error("blob missing for " + path)
                    | Some bytes ->
                        Directory.CreateDirectory(Path.GetDirectoryName target |> string) |> ignore
                        File.WriteAllBytes(target, bytes)
                        Ok(path + "\000" + ConstructionJson.sha256Bytes bytes))
            match written |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | e :: _ -> Error e
            | [] ->
                let lines = written |> List.choose Result.toOption |> List.sort
                Ok(lines.Length, ConstructionJson.sha256Text (String.concat "\n" lines)))

    /// Best-effort removal of the scratch directory. A leftover directory
    /// changes no evidence, so failure to remove it is not reported.
    let private remove (directory: string) =
        try Directory.Delete(directory, true) with
        | :? IOException as ex -> ignore ex.Message
        | :? UnauthorizedAccessException as ex -> ignore ex.Message

    let private diagnosticsOf (o: ProcessOutcome) =
        let text = Encoding.UTF8.GetString o.Stdout + "\n" + o.Stderr
        let errors =
            text.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l.Contains(": error ", StringComparison.Ordinal))
            |> Array.distinct
        let chosen = if errors.Length > 0 then String.concat "\n" errors else text.Trim()
        if chosen.Length > 4000 then chosen.Substring(0, 4000) else chosen

    /// Materializes, builds and drives one candidate commit.
    let run (dotnet: string) (oracle: AdapterOracle) (repository: string) (commit: string) (entries: Map<string, TreeEntry>) (project: string) : BehaviorRun =
        match driverScript () with
        | Error reason -> NotRun reason
        | Ok script ->
            let work = Path.Combine(Path.GetTempPath(), "dokimos-behavior", Guid.NewGuid().ToString("N"))
            let candidateDir = Path.Combine(work, "candidate")
            let driverDir = Path.Combine(work, "driver")
            Directory.CreateDirectory candidateDir |> ignore
            Directory.CreateDirectory driverDir |> ignore
            let started = now ()
            let tree =
                match ConstructionGit.run "git" [ "-C"; repository; "rev-parse"; commit + "^{tree}" ] None (TimeSpan.FromMinutes 1.0) with
                | Ok o when o.ExitCode = 0 -> (Encoding.UTF8.GetString o.Stdout).Trim()
                | _ -> ""
            let baseRecord =
                { Sdk = ""
                  Commands = []
                  CandidateCommit = commit
                  CandidateTree = tree
                  MaterializedFiles = 0
                  MaterializedDigest = ""
                  Project = project
                  Assembly = ""
                  AssemblyDigest = ""
                  DriverDigest = ConstructionJson.sha256Text script
                  DriverInputDigest = ""
                  ObservationsDigest = ""
                  OracleDigest = oracle.Digest
                  StartedAt = started
                  FinishedAt = started }
            let finish (record: BehaviorRunRecord) = { record with FinishedAt = now () }
            let result =
                match Map.tryFind project entries with
                | Some entry when entry.Mode.StartsWith("100", StringComparison.Ordinal) ->
                    match materialize repository entries candidateDir with
                    | Error reason -> RunUnavailable(finish baseRecord, "could not materialize the candidate: " + reason)
                    | Ok(count, digest) ->
                        let record = { baseRecord with MaterializedFiles = count; MaterializedDigest = digest }
                        let sdkRecord, sdk = recorded "sdk-version" "<candidate>" dotnet [ "--version" ] candidateDir (TimeSpan.FromMinutes 1.0)
                        let sdkVersion = match sdk with Ok o when o.ExitCode = 0 -> (Encoding.UTF8.GetString o.Stdout).Trim() | _ -> "unknown"
                        let buildRecord, build =
                            recorded "build" "<candidate>" dotnet [ "build"; project; "-c"; "Release"; "--nologo"; "-nodeReuse:false" ] candidateDir buildTimeout
                        let record = { record with Sdk = sdkVersion; Commands = [ sdkRecord; buildRecord ] }
                        match build with
                        | Error reason -> RunUnavailable(finish record, "the build did not run: " + reason)
                        | Ok b when b.ExitCode <> 0 -> BuildFailed(finish record, diagnosticsOf b)
                        | Ok _ ->
                            let pathRecord, targetPath =
                                recorded "locate-assembly" "<candidate>" dotnet
                                    [ "msbuild"; project; "-getProperty:TargetPath"; "-p:Configuration=Release"; "-nologo" ] candidateDir (TimeSpan.FromMinutes 2.0)
                            let record = { record with Commands = record.Commands @ [ pathRecord ] }
                            match targetPath with
                            | Ok t when t.ExitCode = 0 && File.Exists((Encoding.UTF8.GetString t.Stdout).Trim()) ->
                                let assembly = (Encoding.UTF8.GetString t.Stdout).Trim()
                                let output = Path.Combine(driverDir, "observations.jsonl")
                                let input = BehaviorOracle.driverInput oracle assembly output
                                File.WriteAllText(Path.Combine(driverDir, "driver.fsx"), script)
                                File.WriteAllText(Path.Combine(driverDir, "input.json"), input)
                                let driveRecord, drive =
                                    recorded "drive-adapter" "<driver>" dotnet [ "fsi"; "--quiet"; "driver.fsx"; "input.json" ] driverDir driverTimeout
                                let observed = if File.Exists output then File.ReadAllText output else ""
                                let record =
                                    { record with
                                        Commands = record.Commands @ [ driveRecord ]
                                        Assembly = Path.GetRelativePath(candidateDir, assembly).Replace('\\', '/')
                                        AssemblyDigest = ConstructionJson.sha256Bytes (File.ReadAllBytes assembly)
                                        DriverInputDigest = ConstructionJson.sha256Text input
                                        ObservationsDigest = ConstructionJson.sha256Text observed }
                                match drive, BehaviorOracle.readDriverOutput observed with
                                | Error reason, _ -> RunUnavailable(finish record, "the driver did not complete: " + reason)
                                | Ok d, (None, _) ->
                                    RunUnavailable(finish record, $"the driver exited {d.ExitCode} without probing the adapter: " + (diagnosticsOf d))
                                | Ok _, (probe, observations) -> Driven(finish record, probe, observations)
                            | Ok t -> RunUnavailable(finish record, "the built assembly could not be located: " + (diagnosticsOf t))
                            | Error reason -> RunUnavailable(finish record, "the built assembly could not be located: " + reason)
                | _ -> ProjectMissing(finish baseRecord)
            remove work
            result

    /// The Git blob id of some bytes, to find copies of the oracle in a tree.
    let gitBlobId (bytes: byte[]) =
        let header = Encoding.ASCII.GetBytes($"blob {bytes.Length}\000")
        SHA1.HashData(Array.append header bytes) |> Convert.ToHexString |> _.ToLowerInvariant()

    /// The earliest commit in the oracle file's own repository whose copy of
    /// the file has exactly these bytes.
    let oracleHistory (oraclePath: string) (bytes: byte[]) =
        let full = Path.GetFullPath oraclePath
        let directory = Path.GetDirectoryName full |> string
        let gitIn at args = ConstructionGit.run "git" ("-C" :: at :: args) None (TimeSpan.FromMinutes 1.0)
        let text (o: ProcessOutcome) = (Encoding.UTF8.GetString o.Stdout).Trim()
        match gitIn directory [ "rev-parse"; "--show-toplevel" ] with
        | Ok top when top.ExitCode = 0 ->
            let root = text top
            // Paths below are relative to the repository root.
            let git = gitIn root
            let relative = Path.GetRelativePath(root, full).Replace('\\', '/')
            let blob = gitBlobId bytes
            match git [ "log"; "--format=%H %cI"; "--"; relative ] with
            | Ok log when log.ExitCode = 0 ->
                (text log).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                |> Array.rev
                |> Array.tryPick (fun line ->
                    match line.Split(' ') with
                    | [| commit; at |] ->
                        match git [ "rev-parse"; commit + ":" + relative ] with
                        | Ok b when b.ExitCode = 0 && text b = blob ->
                            match DateTimeOffset.TryParse(at, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
                            | true, when' -> Some(FirstCommitted(commit, when'))
                            | _ -> None
                        | _ -> None
                    | _ -> None)
                |> Option.defaultValue (HistoryUnavailable "these oracle bytes are not committed in the oracle's repository")
            | _ -> HistoryUnavailable "the oracle's Git history could not be read"
        | _ -> HistoryUnavailable "the oracle is not inside a Git repository"
