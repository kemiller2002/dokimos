namespace Dokimos.Core

open Dokimos.Domain

/// What a declared actor did to a finding (R14.1, R14.7).
[<RequireQualifiedAccess>]
type FindingActivity =
    | Discovered
    | Remediated
    | Validated
    | Resolved
    | Reviewed

/// A declared actor acting within an execution at a point in time. Only
/// explicitly declared identities are recorded; see `ActorDeclaration`.
type ActingContributor =
    { Key: ContributionKey
      Actor: Actor
      At: string
      Reason: string option
      Evidence: string list }

/// A lifecycle transition together with the finding's (possibly extended)
/// provenance block.
type AttributedTransition =
    { Transition: FindingState option
      Provenance: Json
      Changed: bool }

[<RequireQualifiedAccess>]
module FindingProvenance =
    let operation activity =
        match activity with
        | FindingActivity.Discovered -> ContributionOperation.Discovered
        | FindingActivity.Remediated -> ContributionOperation.Remediated
        | FindingActivity.Validated -> ContributionOperation.Validated
        | FindingActivity.Resolved -> ContributionOperation.Resolved
        | FindingActivity.Reviewed -> ContributionOperation.Reviewed

    let private hasHistory (block: Json) =
        match ProvenanceInterchange.classify block with
        | ProvenanceVerdict.Supported(_, items, _, _) -> not items.IsEmpty
        | _ -> true

    /// Appends the activities as one contribution by `contributor`
    /// (append-only, RQ-ROS-2026-A004). The first discovery of a finding with
    /// no recorded history also records `created`: the discoverer created the
    /// finding record. Returns the new block and whether it changed.
    let record (activities: FindingActivity list) (contributor: ActingContributor) (block: Json) : Result<Json * bool, string> =
        if activities.IsEmpty then
            Error "record at least one finding activity"
        else
            let creates = activities |> List.contains FindingActivity.Discovered && not (hasHistory block)

            ProvenanceInterchange.append
                block
                { Key = contributor.Key
                  Operations = (if creates then [ ContributionOperation.Created ] else []) @ (activities |> List.distinct |> List.map operation)
                  At = contributor.At
                  Last = None
                  Actor = contributor.Actor
                  Reason = contributor.Reason
                  Evidence = contributor.Evidence }

    let private consistent (transition: FindingState option) (activity: FindingActivity) =
        match activity, transition with
        | FindingActivity.Resolved, Some Resolved -> None
        | FindingActivity.Resolved, _ -> Some "'resolved' can only be recorded for a Resolved transition"
        | FindingActivity.Discovered, Some Introduced -> None
        | FindingActivity.Discovered, _ -> Some "'discovered' can only be recorded for an Introduced transition"
        | _ -> None

    /// `Findings.transition` with optional attribution. Without a declared
    /// contributor the block is returned unchanged: no actor is invented.
    let transition
        (previous: FindingPresence)
        (current: FindingPresence)
        (attribution: (FindingActivity list * ActingContributor) option)
        (block: Json)
        : Result<AttributedTransition, string> =
        let state = Findings.transition previous current

        match attribution with
        | None ->
            match ProvenanceInterchange.classify block with
            | ProvenanceVerdict.Malformed problems -> Error(String.concat "; " problems)
            | _ ->
                Ok
                    { Transition = state
                      Provenance = block
                      Changed = false }
        | Some(activities, contributor) ->
            match activities |> List.tryPick (consistent state) with
            | Some message -> Error message
            | None ->
                record activities contributor block
                |> Result.map (fun (next, changed) ->
                    { Transition = state
                      Provenance = next
                      Changed = changed })
