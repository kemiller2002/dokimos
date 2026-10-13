namespace Dokimos.Core

open System
open System.Text
open System.Text.RegularExpressions

/// Which default-plan obligations an agent may claim as implemented.
[<RequireQualifiedAccess>]
module MechanicalObligations =
    /// Obligation kinds that record an open semantic question: the agent
    /// may only escalate them unless an approved decision record closes them.
    let unresolvedKinds = set [ "conflict"; "undecidedEffectFailure"; "uncoveredRequirement" ]

    /// Handoff work-item ids that are not default-plan obligations.
    let isAuxiliaryId (id: string) =
        id.StartsWith("IMPLEMENT:", StringComparison.Ordinal) || id.StartsWith("VERIFY:", StringComparison.Ordinal)

    /// The command an effect obligation is about, from its extension surface
    /// (`ITimerPorts.Perform/Stop` or `port:transitionEffect/Stop`).
    let private effectCommands (o: PreparedObligation) =
        o.ExtensionSurface
        |> List.choose (fun surface ->
            let m = Regex.Match(surface, @"(?:\.Perform|^port:transitionEffect)/(\w+)$")
            if m.Success then Some(ConstructionCanonical.nameKey m.Groups[1].Value) else None)
        |> List.distinct

    /// Unresolved obligations that the supplied, digest-matching decision
    /// records close. Conservative: an `undecidedEffectFailure` is closed only
    /// by a `portContract` record of one of its nodes whose `transitionEffect`
    /// port for that command has a non-null `onFailure` (when the surface names
    /// no command, every such port of the record must be decided). `conflict`
    /// and `uncoveredRequirement` are never closed: no approved record kind
    /// resolves them today.
    let closedObligations (m: MechanicalInputs) (p: PreparedArtifact) : Set<string> =
        let declared = Map.tryFind "decisionRecords" p.InputDigests |> Option.defaultValue ""
        match m.DecisionRecordsBytes with
        | None -> Set.empty
        | Some bytes ->
            match ConstructionJson.parse "decision records" (Encoding.UTF8.GetString bytes) with
            | Error _ -> Set.empty
            | Ok d ->
                let canonical = ConstructionJson.sha256Text (ConstructionCanonical.canonicalText d)
                if not (List.contains declared [ ConstructionJson.sha256Bytes bytes; canonical ]) then Set.empty
                else
                    let effects (record: Json.JsonElement) =
                        match ConstructionJson.get "content" record with
                        | Some content when ConstructionJson.text "kind" content = "portContract" ->
                            ConstructionJson.array "ports" content
                            |> List.filter (fun port -> ConstructionJson.text "port" port = "transitionEffect")
                            |> List.map (fun port ->
                                let decided =
                                    match ConstructionJson.get "onFailure" port with
                                    | Some v -> v.ValueKind <> Json.JsonValueKind.Null
                                    | None -> false
                                ConstructionCanonical.nameKey (ConstructionJson.text "command" port), decided)
                        | _ -> []
                    let records = ConstructionJson.array "records" d
                    p.Obligations
                    |> List.filter (fun o -> o.Kind = "undecidedEffectFailure")
                    |> List.filter (fun o ->
                        let ports =
                            records
                            |> List.filter (fun r -> List.contains (ConstructionJson.text "nodeId" r) o.NodeIds)
                            |> List.collect effects
                        match effectCommands o with
                        | [] -> not ports.IsEmpty && ports |> List.forall snd
                        | commands ->
                            commands |> List.forall (fun c -> ports |> List.exists (fun (command, decided) -> command = c && decided)))
                    |> List.map _.Id
                    |> Set.ofList
