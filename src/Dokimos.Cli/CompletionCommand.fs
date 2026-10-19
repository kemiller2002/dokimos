namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core
open Dokimos.Cli.CliInputs

/// `dokimos completion verify`: read-only, independent verification of a
/// claimant packet against host-supplied authority and evidence observations.
/// Every input problem fails closed: absent, unreadable or malformed inputs
/// exit 3 and never produce a receipt.
module CompletionCommand =
    let usage = "usage: dokimos completion verify --packet FILE --authority FILE"

    let private readInput noun path =
        let label = "completion-" + noun
        if not (File.Exists path) then
            Error(label + "-unavailable", $"{noun} not found: {path}")
        else
            try
                Ok(File.ReadAllText path)
            with
            | :? IOException
            | :? UnauthorizedAccessException as problem -> Error(label + "-unreadable", $"{noun} unreadable: {path}: {problem.Message}")

    let verify args =
        withArgs [ "packet"; "authority" ] args (fun parsed ->
            match Arguments.required "packet" parsed, Arguments.required "authority" parsed with
            | Ok packetPath, Ok authorityPath ->
                match readInput "packet" packetPath, readInput "authority" authorityPath with
                | Error(code, problem), _
                | _, Error(code, problem) -> Output.unavailable code problem
                | Ok packetText, Ok authorityText ->
                    match CompletionWire.readPacket packetText, CompletionWire.readAuthority authorityText with
                    | Error problem, _ -> Output.unavailable "completion-packet-invalid" problem
                    | _, Error problem -> Output.unavailable "completion-authority-invalid" problem
                    | Ok packet, Ok authority ->
                        let receipt = CompletionVerification.verify authority packet
                        Output.dataWithCode receipt.ExitCode (Contracts.serialize (CompletionWire.toWire receipt))
            | _ -> Output.invalid "invalid-arguments" usage)
