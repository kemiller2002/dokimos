namespace Dokimos.Cli

open System
open System.IO
open System.Text
open Dokimos.Core
open Dokimos.Cli.CliInputs

/// `dokimos evidence plan`: decides, per test, whether recorded evidence can
/// be carried forward (identity unchanged, policy permits) or the test must
/// run. Source digests come from the checkout at the request's revision; the
/// checkout must be at that commit with no tracked modifications, so the
/// identity is of committed bytes, never of local edits.
module EvidenceCommand =
    let usage = "usage: dokimos evidence plan --request FILE [--root DIR] [--store DIR] [--checkpoint incremental|release]"

    let recordUsage =
        "usage: dokimos evidence record --store DIR (--record FILE | --plan FILE --test ID --outcome passed|failed|unavailable --evidence-id ID)"

    let private git root args =
        match ConstructionGit.run "git" ("-C" :: root :: args) None (TimeSpan.FromMinutes 2.0) with
        | Ok outcome when outcome.ExitCode = 0 -> Ok(Encoding.UTF8.GetString outcome.Stdout)
        | Ok outcome -> Error(outcome.Stderr.Trim())
        | Error problem -> Error problem

    /// Tracked files of a clean checkout at exactly `revision`.
    let private checkoutFiles root (revision: string) =
        git root [ "rev-parse"; "HEAD" ]
        |> Result.bind (fun head ->
            if not (String.Equals(head.Trim(), revision, StringComparison.OrdinalIgnoreCase)) then
                Error $"checkout {root} is at {head.Trim()}, not the request revision {revision}"
            else
                git root [ "status"; "--porcelain"; "--untracked-files=no" ]
                |> Result.bind (fun status ->
                    if status.Trim() <> "" then Error $"checkout {root} has tracked modifications; identities must be of committed bytes"
                    else
                        git root [ "ls-files"; "-z" ]
                        |> Result.map (fun listing -> listing.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Array.toList |> List.sort)))

    let private storeError error =
        let code, message = EvidenceStore.describeError error
        Output.error (EvidenceStore.exitCode error) code message

    let private sequence results =
        List.foldBack (fun item state -> Result.bind (fun values -> Result.map (fun v -> v :: values) item) state) results (Ok [])

    /// Adds the store's published records for the request's tests, if a
    /// store is named. Passing and failing records are both read, so a
    /// failure at the current identity contradicts an earlier pass.
    let private withStore (parsed: Arguments) (request: EvidenceReuseWire.ReuseRequest) k =
        match Arguments.tryOne "store" parsed with
        | None -> k request
        | Some root ->
            match FileSystemStore.openStore root with
            | Error e -> storeError e
            | Ok store ->
                match request.Tests |> List.map (fun t -> store.Provenance t.TestId) |> sequence with
                | Error e -> storeError e
                | Ok stored ->
                    match EvidenceReuseWire.withStoredPriors (List.concat stored) request with
                    | Error problem -> Output.unavailable "evidence-request-invalid" problem
                    | Ok request -> k request

    let plan args =
        withArgs [ "request"; "root"; "store"; "checkpoint" ] args (fun parsed ->
            let checkpoint =
                match Arguments.tryOne "checkpoint" parsed with
                | None -> Ok None
                | Some name ->
                    match EvidenceCheckpoint.checkpointCases |> List.tryFind (fst >> (=) name) with
                    | Some(_, c) -> Ok(Some c)
                    | None -> Error()
            match Arguments.required "request" parsed, checkpoint with
            | Error _, _
            | _, Error _ -> Output.invalid "invalid-arguments" usage
            | Ok requestPath, Ok checkpoint ->
                let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
                if not (File.Exists requestPath) then
                    Output.unavailable "evidence-request-unavailable" $"request not found: {requestPath}"
                else
                    match EvidenceReuseWire.readRequest (File.ReadAllText requestPath) with
                    | Error problem -> Output.unavailable "evidence-request-invalid" problem
                    | Ok request ->
                      // The release checkpoint can be raised from the command
                      // line (for a release workflow) but never lowered: a
                      // request that declares `release` stays a full run.
                      let request =
                          match checkpoint with
                          | Some EvidenceCheckpoint.Release -> { request with Checkpoint = EvidenceCheckpoint.Release }
                          | _ -> request
                      withStore parsed request (fun request ->
                        match checkoutFiles root request.Revision with
                        | Error problem -> Output.unavailable "evidence-checkout-invalid" problem
                        | Ok files ->
                            let digestOf (path: string) = EvidenceReuse.sha256Bytes (File.ReadAllBytes(Path.Combine(root, path)))
                            let plan = EvidenceReuseWire.planOf files digestOf request
                            Output.data (Contracts.serialize (EvidenceReuseWire.toWire plan))))

    let private stored (store: EvidenceStore) outcome (subject: string) =
        Output.data (
            Contracts.serialize
                {| Contract = "dokimos.store-result"
                   SchemaVersion = "1.0.0"
                   Store = store.Describe
                   Outcome = (match outcome with Stored -> "stored" | AlreadyStored -> "already-stored")
                   Subject = subject |}
        )

    /// `dokimos evidence record`: publishes one run's provenance, passing or
    /// failing, to the evidence store. The record comes either from a file
    /// (`--record`, a `dokimos.evidence-provenance/1` record) or from the plan
    /// that scheduled the run (`--plan`, built from the plan's own resolved
    /// inputs, so the host cannot misstate them).
    let record args =
        withArgs [ "store"; "record"; "plan"; "test"; "outcome"; "evidence-id" ] args (fun parsed ->
            let readFile name path k =
                if File.Exists path then k (File.ReadAllText path)
                else Output.unavailable $"evidence-{name}-unavailable" $"{name} not found: {path}"
            let fromArguments k =
                match Arguments.tryOne "record" parsed, Arguments.tryOne "plan" parsed with
                | Some path, None ->
                    if [ "test"; "outcome"; "evidence-id" ] |> List.exists (fun o -> (Arguments.tryOne o parsed).IsSome) then
                        Output.invalid "invalid-arguments" recordUsage
                    else
                        readFile "record" path (fun text ->
                            match Json.parse EvidenceReuseWire.provenanceRecord text with
                            | Ok record -> k record
                            | Error e -> Output.unavailable "evidence-record-invalid" (sprintf "%A" e))
                | None, Some path ->
                    match Arguments.required "test" parsed, Arguments.required "outcome" parsed, Arguments.required "evidence-id" parsed with
                    | Ok test, Ok outcome, Ok evidenceId ->
                        match EvidenceReuse.outcomeCases |> List.tryFind (fst >> (=) outcome) with
                        | None -> Output.invalid "invalid-arguments" recordUsage
                        | Some(_, outcome) ->
                            readFile "plan" path (fun text ->
                                match EvidenceReuseWire.recordFromPlan text test evidenceId outcome with
                                | Ok record -> k record
                                | Error problem -> Output.unavailable "evidence-record-invalid" problem)
                    | _ -> Output.invalid "invalid-arguments" recordUsage
                | _ -> Output.invalid "invalid-arguments" recordUsage
            match Arguments.required "store" parsed with
            | Error _ -> Output.invalid "invalid-arguments" recordUsage
            | Ok root ->
                fromArguments (fun record ->
                    match FileSystemStore.openStore root |> Result.bind (fun store -> store.PutProvenance record |> Result.map (fun o -> store, o)) with
                    | Ok(store, outcome) -> stored store outcome record.EvidenceId
                    | Error e -> storeError e))

    let dispatch args =
        match args with
        | "plan" :: rest -> plan rest
        | "record" :: rest -> record rest
        | _ -> Output.invalid "invalid-arguments" (usage + "\n" + recordUsage)
