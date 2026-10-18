namespace Dokimos.Cli

open System.IO
open Dokimos.Core
open Dokimos.Cli.CliInputs

/// `dokimos completion verify`: read-only, independent verification of a
/// claimant packet against host-supplied authority and evidence observations.
module CompletionCommand =
    let usage = "usage: dokimos completion verify --packet FILE --authority FILE"

    let verify args =
        withArgs [ "packet"; "authority" ] args (fun parsed ->
            match Arguments.required "packet" parsed, Arguments.required "authority" parsed with
            | Ok packetPath, Ok authorityPath when not (File.Exists packetPath) ->
                Output.unavailable "completion-packet-unavailable" ("packet not found: " + packetPath)
            | Ok packetPath, Ok authorityPath when not (File.Exists authorityPath) ->
                Output.unavailable "completion-authority-unavailable" ("authority not found: " + authorityPath)
            | Ok packetPath, Ok authorityPath ->
                match CompletionWire.readPacket (File.ReadAllText packetPath), CompletionWire.readAuthority (File.ReadAllText authorityPath) with
                | Error problem, _ -> Output.unavailable "completion-packet-invalid" problem
                | _, Error problem -> Output.unavailable "completion-authority-invalid" problem
                | Ok packet, Ok authority ->
                    let receipt = CompletionVerification.verify authority packet
                    Output.dataWithCode receipt.ExitCode (Contracts.serialize (CompletionWire.toWire receipt))
            | _ -> Output.invalid "invalid-arguments" usage)
