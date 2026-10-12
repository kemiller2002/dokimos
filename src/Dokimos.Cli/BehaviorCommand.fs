namespace Dokimos.Cli

open System
open System.IO
open System.Text
open Dokimos.Core
open Dokimos.Cli.CliInputs

/// `dokimos behavior verify`: drives one candidate commit's frozen adapter
/// with an adapter-bound oracle. It builds and executes candidate code, so
/// it must run where that is acceptable (CI or a disposable environment).
module BehaviorCommand =
    let private known =
        [ "oracle"; "expected-oracle-digest"; "repository"; "commit"; "project"; "work-item"; "sources"
          "claims"; "claimed-results"; "runner"; "at" ]

    let usage =
        "usage: dokimos behavior verify --oracle FILE --expected-oracle-digest sha256:H --commit REV --project REL/PATH.fsproj "
        + "[--repository DIR] [--work-item ID] [--sources DIR] [--claims F] [--claimed-results TRX]... [--runner dotnet] [--at ISO-8601] [--json]"

    let private sources parsed =
        match Arguments.tryOne "sources" parsed with
        | None -> Ok None
        | Some dir when Directory.Exists dir ->
            Directory.EnumerateFiles dir
            |> Seq.map (fun p -> string (Path.GetFileName p), File.ReadAllBytes p)
            |> Map.ofSeq |> Some |> Ok
        | Some dir -> Error("sources directory not found: " + dir)

    let private claimed parsed =
        let trx =
            Arguments.many "claimed-results" parsed
            |> List.map (fun path ->
                if File.Exists path then ConstructionEvidence.readTrxClaims (File.ReadAllText path)
                else Error("claimed-results not found: " + path))
        let claims =
            match Arguments.tryOne "claims" parsed with
            | None -> Ok []
            | Some path when File.Exists path -> ConstructionEvidence.readClaims (File.ReadAllText path) |> Result.map _.Tests
            | Some path -> Error("claims not found: " + path)
        (claims :: trx)
        |> List.fold (fun acc r -> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ x)) acc) (Ok [])

    let private at parsed =
        match Arguments.tryOne "at" parsed with
        | None -> Ok DateTimeOffset.UtcNow
        | Some t ->
            match DateTimeOffset.TryParse(t, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
            | true, v -> Ok v
            | _ -> Error("--at is not a timestamp: " + t)

    let private committedAt repository commit =
        match ConstructionGit.run "git" [ "-C"; repository; "show"; "-s"; "--format=%cI"; commit ] None (TimeSpan.FromMinutes 1.0) with
        | Ok o when o.ExitCode = 0 ->
            let text = (Encoding.UTF8.GetString o.Stdout).Trim()
            match DateTimeOffset.TryParse(text, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
            | true, v -> Ok v
            | _ -> Error("unreadable commit time " + text)
        | Ok o -> Error("git show failed: " + o.Stderr.Trim())
        | Error e -> Error e

    let verify (args: string list) =
        // JSON is the only output format; --json is accepted for symmetry.
        withArgs known (args |> List.filter (fun a -> a <> "--json")) (fun parsed ->
            let req name = Arguments.required name parsed
            match req "oracle", req "expected-oracle-digest", req "commit", req "project" with
            | Ok oracleFile, Ok expected, Ok commitRev, Ok project ->
                let repository = Arguments.tryOne "repository" parsed |> Option.defaultValue "."
                let project = project.Replace('\\', '/').TrimStart('/')
                match (if File.Exists oracleFile then Ok(File.ReadAllBytes oracleFile) else Error("oracle not found: " + oracleFile)),
                      sources parsed, claimed parsed, at parsed with
                | Ok oracleBytes, Ok docs, Ok claims, Ok checkedAt ->
                    match BehaviorOracle.read oracleBytes with
                    | Error e -> Output.unavailable "behavior-oracle-invalid" e
                    | Ok oracle ->
                        let candidate =
                            ConstructionGit.resolve repository commitRev
                            |> Result.bind (fun commit ->
                                match ConstructionGit.tree repository commit, committedAt repository commit with
                                | Ok entries, Ok time -> Ok(commit, entries, time)
                                | Error e, _ | _, Error e -> Error e)
                        match candidate with
                        | Error e -> Output.unavailable "behavior-candidate-unavailable" e
                        | Ok(commit, entries, time) ->
                            let blob = BehaviorRunner.gitBlobId oracleBytes
                            let snapshot =
                                { Commit = commit
                                  CommittedAt = time
                                  OracleCopies = entries |> Map.toList |> List.filter (fun (_, e) -> e.Blob = blob) |> List.map fst }
                            let history = BehaviorRunner.oracleHistory oracleFile oracleBytes
                            let expectedDigest = ConstructionJson.normalizeDigest expected
                            let run =
                                match BehaviorVerification.integrity oracle expectedDigest history snapshot with
                                | [] ->
                                    let runner = Arguments.tryOne "runner" parsed |> Option.defaultValue "dotnet"
                                    BehaviorRunner.run runner oracle repository commit entries project
                                | blocking ->
                                    NotRun("refused: " + (blocking |> List.map _.Code |> List.distinct |> String.concat ", "))
                            let inputs =
                                { CheckedAt = checkedAt
                                  WorkItem = Arguments.tryOne "work-item" parsed |> Option.defaultValue ""
                                  OracleBytes = oracleBytes
                                  ExpectedOracleDigest = expectedDigest
                                  OracleHistory = history
                                  Candidate = snapshot
                                  Project = project
                                  SourceDocuments = docs
                                  Run = run
                                  ClaimedTests = claims }
                            match BehaviorVerification.verify inputs with
                            | Error e -> Output.unavailable "behavior-oracle-invalid" e
                            | Ok report -> Output.dataWithCode report.ExitCode (Contracts.serialize (BehaviorWire.toWire inputs report))
                | Error e, _, _, _ | _, Error e, _, _ | _, _, Error e, _ -> Output.unavailable "behavior-input-unavailable" e
                | _, _, _, Error e -> Output.invalid "invalid-arguments" e
            | _ -> Output.invalid "invalid-arguments" usage)
