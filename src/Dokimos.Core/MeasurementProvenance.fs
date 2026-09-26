namespace Dokimos.Core

open System.Text.Json.Nodes

/// The only environment Dokimos consults to identify the measuring actor
/// (R0.13). Every field is a whitelisted, non-secret variable read at the CLI
/// boundary; Git history, commit authorship, and code heuristics are never
/// inputs.
type IdentityEnvironment =
    { RosActorKind: string option
      RosActor: string option
      RosProvider: string option
      RosModel: string option
      RosRuntime: string option
      RosExecutionId: string option
      GitHubActions: bool
      GitHubRunId: string option
      GitHubRunAttempt: string option }

/// Explicit CLI declarations (`--actor-kind`, `--actor`, `--provider`,
/// `--model`, `--runtime`). They win over the environment.
type ActorDeclaration =
    { DeclaredKind: string option
      DeclaredId: string option
      DeclaredProvider: string option
      DeclaredModel: string option
      DeclaredRuntime: string option }

/// Who is acting in this Dokimos run and which run it is.
type ActingIdentity =
    { Actor: ProvenanceActor
      ExecutionKey: string
      /// How the actor was determined: `declared`, `github-actions`, or `none`.
      Mechanism: string }

[<RequireQualifiedAccess>]
module IdentityEnvironment =
    /// The whitelist. Nothing else is read.
    let names =
        [ "ROS_ACTOR_KIND"
          "ROS_ACTOR"
          "ROS_TELEMETRY_PROVIDER"
          "ROS_TELEMETRY_MODEL"
          "ROS_TELEMETRY_RUNTIME"
          "ROS_EXECUTION_ID"
          "GITHUB_ACTIONS"
          "GITHUB_RUN_ID"
          "GITHUB_RUN_ATTEMPT" ]

    let private clean (value: string option) =
        value |> Option.map (fun text -> text.Trim()) |> Option.filter (fun text -> text.Length > 0)

    let read (lookup: string -> string option) : IdentityEnvironment =
        let get name = lookup name |> clean

        { RosActorKind = get "ROS_ACTOR_KIND"
          RosActor = get "ROS_ACTOR"
          RosProvider = get "ROS_TELEMETRY_PROVIDER"
          RosModel = get "ROS_TELEMETRY_MODEL"
          RosRuntime = get "ROS_TELEMETRY_RUNTIME"
          RosExecutionId = get "ROS_EXECUTION_ID"
          GitHubActions = get "GITHUB_ACTIONS" |> Option.exists (fun text -> text.ToLowerInvariant() = "true")
          GitHubRunId = get "GITHUB_RUN_ID"
          GitHubRunAttempt = get "GITHUB_RUN_ATTEMPT" }

[<RequireQualifiedAccess>]
module ActorDeclaration =
    let none =
        { DeclaredKind = None
          DeclaredId = None
          DeclaredProvider = None
          DeclaredModel = None
          DeclaredRuntime = None }

[<RequireQualifiedAccess>]
module ActingIdentity =
    [<Literal>]
    let SystemName = "dokimos"

    let private unknownText = ProvenanceActor.UnknownValue

    /// GitHub Actions with nothing declared is CI automation, recorded as such.
    let gitHubActions =
        { ActorKind = ActorKind.Automation
          ActorId = "github/github-actions"
          Provider = Some "github"
          Model = Some unknownText
          Runtime = Some "github-actions" }

    let private resolveActor (declaration: ActorDeclaration) (environment: IdentityEnvironment) : Result<ProvenanceActor * string, string> =
        let pick (flag: string option) (variable: string option) =
            flag |> Option.map (fun text -> text.Trim()) |> Option.filter (fun text -> text.Length > 0) |> Option.orElse variable

        let kindText = pick declaration.DeclaredKind environment.RosActorKind
        let idText = pick declaration.DeclaredId environment.RosActor
        let provider = pick declaration.DeclaredProvider environment.RosProvider
        let model = pick declaration.DeclaredModel environment.RosModel
        let runtime = pick declaration.DeclaredRuntime environment.RosRuntime
        let declared = [ kindText; idText; provider; model; runtime ] |> List.exists Option.isSome

        if not declared && environment.GitHubActions then
            Ok(gitHubActions, "github-actions")
        elif not declared then
            Ok(ProvenanceActor.unknown, "none")
        else
            let kind =
                match kindText with
                | None -> Ok ActorKind.Unknown
                | Some text ->
                    match ActorKind.tryParse text with
                    | Some parsed -> Ok parsed
                    | None -> Error $"unknown actor kind '{text}'; expected agent, human, automation, unknown, or x-<extension>"

            kind
            |> Result.map (fun kind ->
                let known (value: string option) = value |> Option.filter (fun text -> text <> unknownText)

                match kind with
                | ActorKind.Human ->
                    { ActorKind = kind
                      ActorId = idText |> Option.defaultValue unknownText
                      Provider = None
                      Model = None
                      Runtime = None },
                    "declared"
                | _ ->
                    let stableId =
                        match idText, known provider, known runtime with
                        | Some text, _, _ -> text
                        | None, Some p, Some r -> $"{p}/{r}"
                        | _ -> unknownText

                    { ActorKind = kind
                      ActorId = stableId
                      Provider = Some(provider |> Option.defaultValue unknownText)
                      Model = Some(model |> Option.defaultValue unknownText)
                      Runtime = Some(runtime |> Option.defaultValue unknownText) },
                    "declared")

    let private executionKey (environment: IdentityEnvironment) (fallbackRun: string) : Result<string, string> =
        match environment.RosExecutionId with
        | Some execution when ProvenanceContribution.isExecutionKey execution -> Ok execution
        | Some execution -> Error $"ROS_EXECUTION_ID '{execution}' is not an execution ID (EXE-...)"
        | None ->
            match environment.GitHubActions, environment.GitHubRunId with
            | true, Some run ->
                let attempt = environment.GitHubRunAttempt |> Option.map (fun value -> "-" + value) |> Option.defaultValue ""
                ProvenanceRecord.foreignExecutionKey SystemName $"gh-run-{run}{attempt}"
            | _ -> ProvenanceRecord.foreignExecutionKey SystemName fallbackRun

    /// Resolves the acting identity from explicit declarations, then the
    /// whitelisted environment, then GitHub Actions detection; otherwise the
    /// actor is `unknown`. The run is ROS_EXECUTION_ID when Praxis propagated
    /// one, else `EXE-dokimos.<run>` (the GitHub run, or `fallbackRun`).
    let resolve (declaration: ActorDeclaration) (environment: IdentityEnvironment) (fallbackRun: string) : Result<ActingIdentity, string> =
        match resolveActor declaration environment, executionKey environment fallbackRun with
        | Error message, _
        | _, Error message -> Error message
        | Ok(actor, mechanism), Ok key ->
            match ProvenanceActor.problems actor with
            | [] -> Ok { Actor = actor; ExecutionKey = key; Mechanism = mechanism }
            | (field, message) :: _ -> Error $"actor.{field}: {message}"

/// Measurement provenance for snapshots, comparisons, findings, and baseline
/// acceptance (R0.11-R0.17). The measuring actor is the `created`
/// contribution of the measurement; the measured change (and its author) is
/// lineage only. Remediation and validation are appended later by their own
/// actors and never displace the measurer.
[<RequireQualifiedAccess>]
module MeasurementProvenance =
    [<Literal>]
    let MeasurementReason = "Measured by Dokimos; the measuring actor is not the author of the measured code"

    [<Literal>]
    let ComparisonReason = "Compared by Dokimos; each snapshot keeps its own measuring actor"

    [<Literal>]
    let BaselineReason = "Accepted as the Dokimos longitudinal baseline"

    let private problem field message : ProvenanceProblem = { Field = field; Message = message }

    let contribution (identity: ActingIdentity) (at: System.DateTimeOffset) (operations: ProvenanceOperation list) (reason: string option) (evidence: string list) =
        { ContributionKey = identity.ExecutionKey
          Operations = operations
          At = ProvenanceContribution.timestamp at
          Last = None
          Actor = identity.Actor
          Reason = reason
          ContributionEvidence = evidence }

    /// The default lineage reference of a measured revision.
    let revisionReference (revision: string) = "git:commit/" + revision

    /// The reference's last segment names the revision: equal, or an
    /// abbreviated/full SHA of it (at least seven characters).
    let private describesRevision (revision: string) (reference: string) =
        let tail = reference.Split([| '/'; '@'; ':' |]) |> Array.last

        tail = revision
        || (min tail.Length revision.Length >= 7
            && (revision.StartsWith(tail, System.StringComparison.Ordinal) || tail.StartsWith(revision, System.StringComparison.Ordinal)))

    /// The measured change's lineage reference and, when supplied, its own
    /// provenance record verbatim. A malformed record is refused, not dropped;
    /// a record for a different revision is refused; an unsupported major is
    /// carried verbatim under the default reference.
    let private measuredLineage (revision: string) (subjectProvenance: JsonObject option) =
        let fallback = revisionReference revision

        match subjectProvenance with
        | None -> Ok([], [ fallback ])
        | Some raw ->
            match ProvenanceJson.validate raw with
            | Error problems -> Error(problems |> List.map (fun item -> { item with Field = "subject-provenance." + item.Field }))
            | Ok reading ->
                match ProvenanceJson.interpreted reading |> Option.bind _.Subject with
                | Some reference when not (describesRevision revision reference) ->
                    Error [ problem "subject-provenance.subject" $"'{reference}' does not describe the measured revision '{revision}'" ]
                | Some reference -> Ok([ reference, raw ], [])
                | None -> Ok([ fallback, raw ], [])

    /// The snapshot's record: the measuring actor `created` it; the measured
    /// revision is `derivedFrom`; the change's own provenance (which is where
    /// the code author appears) is carried verbatim under `sources`.
    let forSnapshot (identity: ActingIdentity) (snapshot: CanonicalSnapshot) (subjectProvenance: JsonObject option) =
        measuredLineage snapshot.Revision subjectProvenance
        |> Result.bind (fun (sources, lineageOnly) ->
            let creator = contribution identity snapshot.CollectedAt [ ProvenanceOperation.Created ] (Some MeasurementReason) []
            ProvenanceJson.derive (CanonicalSnapshot.subject snapshot.SnapshotId) creator sources lineageOnly)

    /// Snapshot ids are `repository:revision`, so two measurements of one
    /// revision share a reference. The same record is carried once; two
    /// different records under one reference cannot both be carried, and
    /// dropping either would lose a measuring actor, so that is refused.
    let private snapshotLineage (snapshots: CanonicalSnapshot list) =
        let references = snapshots |> List.map (fun snapshot -> CanonicalSnapshot.subject snapshot.SnapshotId, snapshot.Provenance)
        let held = references |> List.choose (fun (reference, raw) -> raw |> Option.map (fun node -> reference, node))
        let lineageOnly = references |> List.filter (snd >> Option.isNone) |> List.map fst

        let conflicting =
            (held |> List.map fst) @ lineageOnly
            |> List.countBy id
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map fst
            |> List.filter (fun reference ->
                match references |> List.filter (fst >> (=) reference) |> List.map snd with
                | [ Some left; Some right ] -> not (JsonNode.DeepEquals(left, right))
                | [ None; None ] -> false
                | _ -> true)

        match conflicting with
        | [] -> Ok(held |> List.distinctBy fst, lineageOnly |> List.distinct)
        | reference :: _ ->
            Error
                [ problem "sources" $"two different measurements share '{reference}'; carrying one would drop the other's measuring actor" ]

    /// The comparison's record: the comparing actor `created` it; both
    /// snapshots are lineage and their records are carried verbatim, so the
    /// two measuring actors stay separate. A legacy snapshot without
    /// provenance is named in lineage only.
    let forComparison (identity: ActingIdentity) (at: System.DateTimeOffset) (before: CanonicalSnapshot) (after: CanonicalSnapshot) =
        snapshotLineage [ before; after ]
        |> Result.bind (fun (held, lineageOnly) ->
            let creator = contribution identity at [ ProvenanceOperation.Created ] (Some ComparisonReason) []
            ProvenanceJson.derive (CanonicalComparison.subject before after) creator held lineageOnly)

    /// A baseline acceptance is its own record: the accepting actor creates
    /// and approves it; the snapshot stays immutable and is lineage.
    let forBaselineAcceptance (identity: ActingIdentity) (at: System.DateTimeOffset) (reason: string option) (snapshot: CanonicalSnapshot) =
        snapshotLineage [ snapshot ]
        |> Result.bind (fun (held, lineageOnly) ->
            let creator =
                contribution identity at [ ProvenanceOperation.Created; ProvenanceOperation.Approved ] (reason |> Option.orElse (Some BaselineReason)) []

            ProvenanceJson.derive ("dokimos:baseline/" + snapshot.SnapshotId) creator held lineageOnly)

    let findingSubject (snapshot: CanonicalSnapshot) (findingId: string) =
        "dokimos:finding/" + snapshot.SnapshotId + "/" + findingId

    /// A finding's own record, to which remediation and validation are later
    /// appended. The finding was produced by the measuring execution in the
    /// same act as its snapshot, so the snapshot's `created` entry is carried
    /// verbatim (same key, actor, and time: not a re-attribution); the
    /// snapshot's lineage and sources are carried verbatim; `x-dokimos-part-of`
    /// names the snapshot. Nothing is invented for a snapshot without
    /// interpretable provenance.
    let forFinding (snapshot: CanonicalSnapshot) (findingId: string) : Result<JsonObject, ProvenanceProblem list> =
        match snapshot.Findings |> List.exists (fun finding -> finding.FindingId = findingId), snapshot.Provenance with
        | false, _ -> Error [ problem "finding" $"finding '{findingId}' is not in snapshot '{snapshot.SnapshotId}'" ]
        | true, None -> Error [ problem "provenance" "the snapshot records no measurement provenance; none is invented" ]
        | true, Some raw ->
            match ProvenanceJson.validate raw with
            | Error problems -> Error problems
            | Ok(ProvenanceReading.Unsupported(version, _)) ->
                Error [ problem "provenance" $"the snapshot's provenance is version {version}, which this Dokimos cannot interpret" ]
            | Ok reading ->
                match ProvenanceJson.interpreted reading |> Option.bind ProvenanceRecord.originator with
                | None -> Error [ problem "provenance" "the snapshot's provenance records no measuring (created) contribution" ]
                | Some creation ->
                    let node = JsonObject()
                    node["contract"] <- JsonValue.Create ProvenanceRecord.ContractName
                    node["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)
                    node["subject"] <- JsonValue.Create(findingSubject snapshot findingId)
                    let contributions = JsonObject()
                    ProvenanceJson.property raw "contributions"
                    |> Option.bind (function
                        | :? JsonObject as entries -> ProvenanceJson.property entries creation.ContributionKey
                        | _ -> None)
                    |> Option.iter (fun entry -> contributions[creation.ContributionKey] <- entry.DeepClone())
                    node["contributions"] <- contributions

                    [ "derivedFrom"; "sources" ]
                    |> List.iter (fun name -> ProvenanceJson.property raw name |> Option.iter (fun value -> node[name] <- value.DeepClone()))

                    node["x-dokimos-part-of"] <- JsonValue.Create(CanonicalSnapshot.subject snapshot.SnapshotId)

                    ProvenanceJson.validate node |> Result.map (fun _ -> node)

    /// Appends a later actor's contribution (remediation `x-remediated`,
    /// validation `x-validated`, review, approval, or another `x-` operation)
    /// to a finding's or comparison's record. `created` is refused: the
    /// measurer stays the originator.
    let recordFollowUp
        (identity: ActingIdentity)
        (at: System.DateTimeOffset)
        (operation: ProvenanceOperation)
        (reason: string option)
        (evidence: string list)
        (raw: JsonObject)
        : Result<JsonObject, ProvenanceProblem list> =
        match operation with
        | ProvenanceOperation.Created -> Error [ problem "operation" "a follow-up cannot claim 'created'; the measurer remains the originator" ]
        | _ -> ProvenanceJson.append (contribution identity at [ operation ] reason evidence) raw
