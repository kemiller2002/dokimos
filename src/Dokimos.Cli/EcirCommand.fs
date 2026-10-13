namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core

/// ECIR's read-only source-conservation audit, with no execution authority.
module EcirCommand =
    /// Source-side ECIR audit runs on the independent pinned manifest, not a
    /// count or test pass claimed by the coding agent. Never grants
    /// implementation completion or authorization for a construction cohort.
    let ecirAudit args =
        match args with
        | [ "--manifest"; manifestPath; "--blueprint"; blueprintPath ]
        | [ "--blueprint"; blueprintPath; "--manifest"; manifestPath ] ->
            if not (File.Exists manifestPath) then
                Output.unavailable "ecir-manifest-not-found" manifestPath
            elif not (File.Exists blueprintPath) then
                Output.unavailable "ecir-blueprint-not-found" blueprintPath
            else
                match EcirAudit.audit (File.ReadAllText manifestPath) (File.ReadAllText blueprintPath) with
                | Error errors -> Output.invalid "ecir-invalid-input" (String.concat "; " errors)
                | Ok report when not report.Findings.IsEmpty ->
                    let description =
                        report.Findings
                        |> List.map (fun finding -> finding.Code + ":" + finding.RequirementKey + " (" + finding.Detail + ")")
                        |> String.concat "; "
                    Output.invalid "ecir-source-trace-failed" description
                | Ok report ->
                    Output.data (Contracts.serialize
                        {| Contract = "dokimos.ecir-audit/1"
                           Imported = report.Imported
                           Represented = report.Represented
                           Modeled = report.Modeled
                           Deferred = report.Deferred
                           Unresolved = report.Unresolved
                           Rejected = report.Rejected
                           Superseded = report.Superseded
                           IndependentlyVerified = report.IndependentlyVerified
                           Completeness = "source-trace-only; execution and tests unverified" |})
        | _ ->
            Output.invalid "invalid-arguments" "usage: dokimos ecir audit --manifest FILE --blueprint FILE"


    /// Independent audit of an Ordo pattern-recognition report and its
    /// delegation packets. Re-derives the precision rules from the pinned
    /// inputs; never grants construction or completion authority.
    let recognitionAudit args =
        let usage =
            "usage: dokimos ecir recognition-audit --manifest FILE --blueprint FILE --report FILE [--decisions FILE] [--approvals FILE] [--packets DIR]"
        let rec options (acc: Map<string, string>) (rest: string list) =
            match rest with
            | [] -> Ok acc
            | flag :: value :: tail when flag.StartsWith "--" && not (value.StartsWith "--") && not (Map.containsKey flag acc) ->
                options (Map.add flag value acc) tail
            | _ -> Error usage
        match options Map.empty args with
        | Error message -> Output.invalid "invalid-arguments" message
        | Ok values ->
            let known = set [ "--manifest"; "--blueprint"; "--report"; "--decisions"; "--approvals"; "--packets" ]
            let missing =
                [ "--manifest"; "--blueprint"; "--report"; "--decisions"; "--approvals" ]
                |> List.filter (fun flag -> Map.containsKey flag values && not (File.Exists values[flag]))
            match Map.tryFind "--manifest" values, Map.tryFind "--blueprint" values, Map.tryFind "--report" values with
            | _ when values |> Map.exists (fun k _ -> not (Set.contains k known)) -> Output.invalid "invalid-arguments" usage
            | _ when not (List.isEmpty missing) -> Output.unavailable "recognition-input-not-found" (String.concat ", " (missing |> List.map (fun f -> values[f])))
            | Some manifest, Some blueprint, Some report ->
                let approvals =
                    match Map.tryFind "--approvals" values with
                    | None -> Ok []
                    | Some path ->
                        try
                            use doc = Text.Json.JsonDocument.Parse(File.ReadAllText path)
                            Ok(doc.RootElement.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Seq.choose Option.ofObj |> Seq.toList)
                        with
                        | :? Text.Json.JsonException as e -> Error("approvals must be a JSON array of decision ids: " + e.Message)
                        | :? InvalidOperationException as e -> Error("approvals must be a JSON array of decision ids: " + e.Message)
                let packets =
                    match Map.tryFind "--packets" values with
                    | None -> []
                    | Some dir when Directory.Exists dir ->
                        Directory.GetFiles(dir, "*.json") |> Array.sort |> Array.map (fun (p: string) -> (Path.GetFileName p |> string), File.ReadAllBytes p) |> Array.toList
                    | Some _ -> []
                match approvals with
                | Error message -> Output.invalid "invalid-arguments" message
                | Ok approved ->
                    match RecognitionAudit.audit (File.ReadAllText manifest) (File.ReadAllText blueprint)
                                                 (Map.tryFind "--decisions" values |> Option.map File.ReadAllBytes)
                                                 approved (File.ReadAllText report) packets with
                    | Error errors -> Output.invalid "recognition-invalid-input" (String.concat "; " errors)
                    | Ok result when not result.Findings.IsEmpty ->
                        result.Findings
                        |> List.map (fun x -> x.Code + ":" + x.Subject + " (" + x.Detail + ")")
                        |> String.concat "; "
                        |> Output.invalid "recognition-audit-failed"
                    | Ok result ->
                        Output.data (Contracts.serialize
                            {| Contract = RecognitionAudit.Contract
                               CohortId = result.CohortId
                               Assigned = result.Assigned
                               Constructible = result.Constructible
                               DecisionRequired = result.DecisionRequired
                               Delegated = result.Delegated
                               Blocked = result.Blocked
                               Excluded = result.Excluded
                               Packets = result.Packets
                               DecisionsBinding = result.DecisionsBinding
                               Completeness = "classification and handoff bounds only; behavior unverified" |})
            | _ -> Output.invalid "invalid-arguments" usage
