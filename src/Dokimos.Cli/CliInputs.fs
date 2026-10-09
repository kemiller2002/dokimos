namespace Dokimos.Cli

open System.IO

/// Shared CLI input helpers. Parsing and path existence are separate from
/// quality-domain decisions; a failed read never becomes a passed gate.
module CliInputs =
    let withArgs known args k =
        match Arguments.parse args |> Result.bind (Arguments.allowOnly known) with
        | Ok parsed -> k parsed
        | Error message -> Output.invalid "invalid-arguments" message

    let optionalFile name args k =
        match Arguments.tryOne name args with
        | None -> k None
        | Some path when File.Exists path -> k (Some(File.ReadAllText path))
        | Some path -> Output.unavailable $"{name}-not-found" $"--{name} {path} does not exist"

