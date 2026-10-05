namespace Dokimos.Cli

open System
open Dokimos.Core

/// The result of one CLI invocation. Canonical data goes to stdout,
/// diagnostics to stderr; the exit code is the machine-readable disposition.
type Output =
    { Stdout: string option
      Stderr: string option
      ExitCode: int }

module Output =
    let data (json: string) = { Stdout = Some json; Stderr = None; ExitCode = ExitCodes.Continue }

    let dataWithCode code (json: string) = { Stdout = Some json; Stderr = None; ExitCode = code }

    let error code diagnosticCode message =
        { Stdout = None
          Stderr = Some(Contracts.serialize (Contracts.diagnostic diagnosticCode message))
          ExitCode = code }

    let invalid diagnosticCode message = error ExitCodes.InvalidInvocation diagnosticCode message

    let unavailable diagnosticCode message = error ExitCodes.EvidenceUnavailable diagnosticCode message

/// `--name value` options (repeatable) and positional arguments.
type Arguments =
    { Positionals: string list
      Options: Map<string, string list> }

module Arguments =
    let parse (args: string list) =
        let rec go positionals options remaining =
            match remaining with
            | [] -> Ok { Positionals = List.rev positionals; Options = options |> Map.map (fun _ v -> List.rev v) }
            | (name: string) :: value :: rest when name.StartsWith("--", StringComparison.Ordinal) && not (value.StartsWith("--", StringComparison.Ordinal)) ->
                let key = name.Substring 2
                go positionals (options |> Map.change key (fun existing -> Some(value :: defaultArg existing []))) rest
            | name :: _ when name.StartsWith("--", StringComparison.Ordinal) -> Error $"option {name} requires a value"
            | value :: rest -> go (value :: positionals) options rest
        go [] Map.empty args

    let tryOne name args = args.Options |> Map.tryFind name |> Option.bind List.tryLast

    let many name args = args.Options |> Map.tryFind name |> Option.defaultValue []

    let required name args =
        match tryOne name args with
        | Some value -> Ok value
        | None -> Error $"missing required option --{name}"

    let allowOnly (known: string list) args =
        match args.Options |> Map.keys |> Seq.filter (fun k -> not (List.contains k known)) |> Seq.tryHead with
        | Some unknown -> Error $"unknown option --{unknown}"
        | None -> Ok args
