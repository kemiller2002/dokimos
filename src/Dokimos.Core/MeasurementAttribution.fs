namespace Dokimos.Core

open System
open Dokimos.Domain

/// Explicit identity declarations for the actor invoking Dokimos (R14.4,
/// RQ-ROS-2026-A016). Each field is what a flag or environment variable
/// declared; nothing here is discovered from ambient signals.
type ActorDeclaration =
    { ActorJson: string option
      Kind: string option
      Id: string option
      Provider: string option
      Model: string option
      Runtime: string option
      Execution: string option }

module ActorDeclaration =
    let empty =
        { ActorJson = None
          Kind = None
          Id = None
          Provider = None
          Model = None
          Runtime = None
          Execution = None }

    /// The environment variables Praxis defines for propagating the current
    /// actor and execution. Only these are read.
    let environmentVariables =
        [ "ROS_ACTOR_KIND"; "ROS_ACTOR"; "ROS_TELEMETRY_PROVIDER"; "ROS_TELEMETRY_MODEL"; "ROS_TELEMETRY_RUNTIME"; "ROS_EXECUTION_ID" ]

    let private declared (value: string option) =
        // Values are taken exactly as declared (contract revision 1.1: no silent
        // repair); only an ASCII-blank value counts as undeclared (revision 1.2).
        value |> Option.filter (ContractText.isBlank >> not)

    /// Reads the Praxis propagation variables through `lookup`.
    let fromEnvironment (lookup: string -> string option) =
        { ActorJson = None
          Kind = declared (lookup "ROS_ACTOR_KIND")
          Id = declared (lookup "ROS_ACTOR")
          Provider = declared (lookup "ROS_TELEMETRY_PROVIDER")
          Model = declared (lookup "ROS_TELEMETRY_MODEL")
          Runtime = declared (lookup "ROS_TELEMETRY_RUNTIME")
          Execution = declared (lookup "ROS_EXECUTION_ID") }

    /// Whether a declaration names an actor identity of its own: an
    /// `--actor-json`, or any actor field (kind, id, provider, model, runtime).
    let private declaresActorField (declaration: ActorDeclaration) =
        [ declaration.ActorJson; declaration.Kind; declaration.Id; declaration.Provider; declaration.Model; declaration.Runtime ]
        |> List.exists (declared >> Option.isSome)

    /// Whether an environment declares an identity (`ROS_ACTOR_KIND` or `ROS_ACTOR`).
    let private environmentDeclaresIdentity (environment: ActorDeclaration) =
        (declared environment.Kind).IsSome || (declared environment.Id).IsSome

    /// One identity source (RQ-ROS-2026-A016 revision 1.2, R14.4). Dokimos
    /// cannot verify a named execution against the Praxis execution record,
    /// so the actor comes wholly from one source:
    /// - explicit flags or `--actor-json` completely replace the environment's
    ///   identity; no field falls back to the environment and
    ///   `ROS_EXECUTION_ID` is never inherited (declare it with `--execution`);
    /// - otherwise the environment's identity is used, and its
    ///   `ROS_EXECUTION_ID` is honoured only when that environment declares an
    ///   identity (`ROS_ACTOR_KIND` or `ROS_ACTOR`); an environment that
    ///   declares only telemetry attributes yields no actor;
    /// - an explicit `--execution` flag is always used.
    let overriding (environment: ActorDeclaration) (flags: ActorDeclaration) =
        let explicitExecution = declared flags.Execution

        if declaresActorField flags then
            { ActorJson = declared flags.ActorJson
              Kind = declared flags.Kind
              Id = declared flags.Id
              Provider = declared flags.Provider
              Model = declared flags.Model
              Runtime = declared flags.Runtime
              Execution = explicitExecution }
        elif environmentDeclaresIdentity environment then
            { ActorJson = None
              Kind = declared environment.Kind
              Id = declared environment.Id
              Provider = declared environment.Provider
              Model = declared environment.Model
              Runtime = declared environment.Runtime
              Execution = explicitExecution |> Option.orElse (declared environment.Execution) }
        else
            { empty with Execution = explicitExecution }

    let private isKnown (value: string option) =
        value |> Option.exists (fun text -> text <> Actor.UnknownValue)

    let private credentialCheck (values: string list) =
        if values |> List.exists ContractText.hasLoneSurrogate then
            Error "the actor declaration holds an unpaired UTF-16 surrogate"
        elif values |> List.exists ProvenanceInterchange.isCredentialLike then
            Error "the actor declaration contains a credential-like value; provenance must never carry authentication material"
        else
            Ok()

    /// The declared actor, or `Actor.unknown` when nothing was declared.
    /// Mirrors Praxis: an explicit id wins; otherwise a non-human actor whose
    /// provider and runtime are both known is `provider/runtime`; otherwise
    /// `unknown`. Non-human attributes that were not declared are the literal
    /// `unknown`; a human has none.
    let actor (declaration: ActorDeclaration) : Result<Actor, string> =
        match declaration.ActorJson with
        | Some json ->
            Json.parse json
            |> Result.mapError (fun message -> $"--actor-json is {message}")
            |> Result.bind (fun node ->
                match ProvenanceInterchange.wellFormednessProblems node with
                | _ :: _ as problems -> Error $"""--actor-json is malformed: {String.concat "; " problems}"""
                | [] -> Ok node)
            |> Result.bind (fun node ->
                if not (ProvenanceInterchange.credentialFindings node).IsEmpty then
                    Error "the actor declaration contains a credential-like value; provenance must never carry authentication material"
                else
                    ProvenanceInterchange.parseActor node |> Result.mapError (String.concat "; "))
        | None ->
            let values = [ declaration.Kind; declaration.Id; declaration.Provider; declaration.Model; declaration.Runtime ] |> List.choose id

            credentialCheck values
            |> Result.bind (fun () ->
                if values.IsEmpty then
                    Ok Actor.unknown
                else
                    let kindText = declaration.Kind |> Option.defaultValue Actor.UnknownValue

                    match ActorKind.tryParse kindText with
                    | None -> Error $"actor kind '{kindText}' is not agent, human, automation, unknown, or x-..."
                    | Some ActorKind.Human ->
                        Ok
                            { Kind = ActorKind.Human
                              Id = declaration.Id |> Option.defaultValue Actor.UnknownValue
                              Provider = None
                              Model = None
                              Runtime = None }
                    | Some kind ->
                        let orUnknown = Option.defaultValue Actor.UnknownValue

                        let id =
                            match declaration.Id with
                            | Some id -> id
                            | None when isKnown declaration.Provider && isKnown declaration.Runtime ->
                                $"{declaration.Provider.Value}/{declaration.Runtime.Value}"
                            | None -> Actor.UnknownValue

                        Ok
                            { Kind = kind
                              Id = id
                              Provider = Some(orUnknown declaration.Provider)
                              Model = Some(orUnknown declaration.Model)
                              Runtime = Some(orUnknown declaration.Runtime) })

    /// The declared execution (`EXE-...` or `EXT-...`), if any. A value that
    /// is not an execution key is refused rather than guessed at.
    let execution (declaration: ActorDeclaration) : Result<ContributionKey option, string> =
        match declaration.Execution with
        | None -> Ok None
        | Some text when ProvenanceInterchange.isCredentialLike text -> Error "the declared execution looks like a credential"
        | Some text when ContractText.hasLoneSurrogate text -> Error "the declared execution holds an unpaired UTF-16 surrogate"
        | Some text ->
            match ContributionKey.tryParse text with
            | Some key when ContributionKey.isExecution key -> Ok(Some key)
            | _ -> Error $"declared execution '{text}' is not an EXE-... or EXT-<system>.<run-id> key"

/// Records who measured (R14.1, R14.3). The snapshot's own provenance gets a
/// `created` + `measured` contribution by the measurement actor, keyed by the
/// declared invoking execution or else by the Dokimos run itself. The
/// measured artifact's author is never recorded here.
[<RequireQualifiedAccess>]
module MeasurementAttribution =
    /// Dokimos's Echelon registry id, used in `EXT-dokimos.{run-id}` keys.
    [<Literal>]
    let System = "dokimos"

    /// Dokimos as an automation actor, for callers that record the tool's own
    /// contribution explicitly (it is not substituted for an undeclared actor).
    let dokimos =
        { Kind = ActorKind.Automation
          Id = "echelon/dokimos"
          Provider = Some "echelon"
          Model = Some Actor.UnknownValue
          Runtime = Some "dokimos" }

    /// `EXT-dokimos.{runId}`.
    let runKey (runId: string) = ContributionKey.foreignExecution System runId

    /// Resolves the measurement actor and execution key from declarations.
    let resolve (declaration: ActorDeclaration) (runId: string) : Result<Actor * ContributionKey, string> =
        ActorDeclaration.actor declaration
        |> Result.bind (fun actor ->
            ActorDeclaration.execution declaration
            |> Result.bind (fun execution ->
                match execution with
                | Some key -> Ok(actor, key)
                | None -> runKey runId |> Result.map (fun key -> actor, key)))

    let contribution (actor: Actor) (key: ContributionKey) (at: DateTimeOffset) (reason: string option) : Contribution =
        { Key = key
          Operations = [ ContributionOperation.Created; ContributionOperation.Measured ]
          At = ProvenanceInterchange.timestamp at
          Last = None
          Actor = actor
          Reason = reason
          Evidence = [] }

    /// A new snapshot/observation provenance block: the measurement
    /// contribution plus lineage (for example `git:commit/{sha}`). Lineage
    /// names what was measured; it is not authorship.
    let block (contribution: Contribution) (derivedFrom: string list) : Result<Json, string> =
        ProvenanceInterchange.append ProvenanceInterchange.emptyBlock contribution
        |> Result.bind (fun (block, _) -> ProvenanceInterchange.addLineage derivedFrom block |> Result.map fst)
        |> Result.bind (fun block ->
            match ProvenanceInterchange.classify block with
            | ProvenanceVerdict.Supported(checkedBlock, _, _, _) -> Ok checkedBlock
            | ProvenanceVerdict.Malformed problems -> Error(String.concat "; " problems)
            | ProvenanceVerdict.Unsupported(schema, _) -> Error $"unexpected provenance schema {schema}")
