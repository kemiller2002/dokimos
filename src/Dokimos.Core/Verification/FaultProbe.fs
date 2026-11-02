namespace Dokimos.Core.Verification

open System
open Dokimos.Core

/// A deliberately introduced, relevant fault: replace exactly one occurrence of `Find` in `File`
/// with `Replace`, run `Command` (the bound tests), and require it to fail. Probes run on a
/// disposable copy of the repository; the working tree is never modified.
type FaultProbe =
    { Id: string
      Description: string
      Obligations: string list
      File: string
      Find: string
      Replace: string
      /// Optional: builds the faulted copy first. A fault that does not build is a probe error,
      /// never a detection (a compile failure says nothing about the tests).
      Build: string list
      Command: string list }

module FaultProbe =
    [<Literal>]
    let SpecSchema = "dokimos.fault-probes/1"

    [<Literal>]
    let ResultSchema = "dokimos.fault-probe-results/1"

    let parse (text: string) : Result<FaultProbe list, string> =
        let probe: Json.Decoder<FaultProbe> =
            fun e ->
                Json.result {
                    let! id = Json.field "id" Json.nonEmptyString e
                    let! description = Json.fieldOr "description" "" Json.string e
                    let! obligations = Json.field "obligations" (Json.list Json.nonEmptyString) e
                    let! file = Json.field "file" Json.nonEmptyString e
                    let! find = Json.field "find" Json.nonEmptyString e
                    let! replace = Json.field "replace" Json.string e
                    let! command = Json.field "command" (Json.list Json.nonEmptyString) e
                    do! if command.IsEmpty then Error "command must not be empty" else Ok()
                    let! build = Json.fieldOr "build" [] (Json.list Json.nonEmptyString) e
                    return { Id = id; Description = description; Obligations = obligations; File = file; Find = find; Replace = replace; Build = build; Command = command }
                }
        let root =
            fun e ->
                Json.result {
                    let! schema = Json.field "schema" Json.string e
                    do! if schema = SpecSchema then Ok() else Error $"schema must be {SpecSchema}"
                    return! Json.field "probes" (Json.list probe) e
                }
        Json.parse root text
        |> Result.mapError (function
            | Json.Malformed m -> $"malformed JSON: {m}"
            | Json.NullDocument -> "null document"
            | Json.Invalid m -> m)

    /// The faulted file content. A `Find` that is absent or ambiguous is an error: a probe that
    /// silently changes nothing would report a test as insensitive for the wrong reason.
    let apply (probe: FaultProbe) (content: string) : Result<string, string> =
        let first = content.IndexOf(probe.Find, StringComparison.Ordinal)
        if first < 0 then Error $"{probe.Id}: the fault anchor does not occur in {probe.File}"
        elif content.IndexOf(probe.Find, first + 1, StringComparison.Ordinal) >= 0 then
            Error $"{probe.Id}: the fault anchor occurs more than once in {probe.File}"
        else Ok(content.Substring(0, first) + probe.Replace + content.Substring(first + probe.Find.Length))

    /// Control must pass (the unfaulted copy), the faulted copy must build (when a build is
    /// declared), and the faulted run must fail.
    let classifyBuilt (controlExit: int) (faultedBuildExit: int) (faultedExit: int) =
        if controlExit <> 0 then ProbeError $"control run failed (exit {controlExit}): the probe proves nothing"
        elif faultedBuildExit <> 0 then ProbeError $"the faulted copy does not build (exit {faultedBuildExit}): a compile failure is not a detection"
        elif faultedExit = 0 then Survived
        else Detected

    let classify (controlExit: int) (faultedExit: int) = classifyBuilt controlExit 0 faultedExit

    let resultsJson (sourceSha: string) (results: ProbeResult list) =
        Contracts.serialize
            {| schema = ResultSchema
               sourceSha = sourceSha
               probes =
                results
                |> List.map (fun r ->
                    {| id = r.ProbeId
                       obligations = r.Obligations
                       outcome = (match r.Outcome with Detected -> "detected" | Survived -> "survived" | ProbeError _ -> "error")
                       detail = (match r.Outcome with ProbeError d -> Some d | _ -> None) |> Option.toObj |}) |}

    let parseResults (text: string) : Result<ProbeResult list, string> =
        let item sha: Json.Decoder<ProbeResult> =
            fun e ->
                Json.result {
                    let! id = Json.field "id" Json.nonEmptyString e
                    let! obligations = Json.field "obligations" (Json.list Json.string) e
                    let! outcome = Json.field "outcome" (Json.oneOf [ "detected", Detected; "survived", Survived; "error", ProbeError "error" ]) e
                    return { ProbeId = id; Obligations = obligations; SourceSha = sha; Outcome = outcome }
                }
        let root =
            fun e ->
                Json.result {
                    let! schema = Json.field "schema" Json.string e
                    do! if schema = ResultSchema then Ok() else Error $"schema must be {ResultSchema}"
                    let! sha = Json.field "sourceSha" Json.nonEmptyString e
                    return! Json.field "probes" (Json.list (item sha)) e
                }
        Json.parse root text |> Result.mapError (sprintf "%A")
