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
    let usage = "usage: dokimos evidence plan --request FILE [--root DIR]"

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

    let plan args =
        withArgs [ "request"; "root" ] args (fun parsed ->
            match Arguments.required "request" parsed with
            | Error _ -> Output.invalid "invalid-arguments" usage
            | Ok requestPath ->
                let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
                if not (File.Exists requestPath) then
                    Output.unavailable "evidence-request-unavailable" $"request not found: {requestPath}"
                else
                    match EvidenceReuseWire.readRequest (File.ReadAllText requestPath) with
                    | Error problem -> Output.unavailable "evidence-request-invalid" problem
                    | Ok request ->
                        match checkoutFiles root request.Revision with
                        | Error problem -> Output.unavailable "evidence-checkout-invalid" problem
                        | Ok files ->
                            let digestOf (path: string) = EvidenceReuse.sha256Bytes (File.ReadAllBytes(Path.Combine(root, path)))
                            let plan = EvidenceReuseWire.planOf files digestOf request
                            Output.data (Contracts.serialize (EvidenceReuseWire.toWire plan)))
