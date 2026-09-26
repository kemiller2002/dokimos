namespace Dokimos.Domain

open System
open System.Text.RegularExpressions

// Praxis agent identity and provenance, carried by Dokimos (R14).
//
// Praxis owns these meanings (DF-ROS-2026-A036, DF-ROS-2026-A037,
// RQ-ROS-2026-A001..A019). The types below are a typed view of the Praxis
// actor and contribution shapes so Dokimos code can reason about roles; the
// canonical JSON form is the `praxis.provenance/1` interchange block handled
// by `Dokimos.Core.ProvenanceInterchange`, which preserves anything this
// view does not model. Recorded identity is self-reported provenance. It is
// not authentication and never changes a threshold, gate, or evidence weight.

/// `agent`, `human`, `automation` (CI or another deterministic non-agent
/// process), `unknown`, or a namespaced `x-...` extension (RQ-ROS-2026-A001).
[<RequireQualifiedAccess>]
type ActorKind =
    | Agent
    | Human
    | Automation
    | Unknown
    | Extension of code: string

module ActorKind =
    let private extension = Regex("^x-[a-z0-9][a-z0-9-]*\\z", RegexOptions.CultureInvariant)

    let code kind =
        match kind with
        | ActorKind.Agent -> "agent"
        | ActorKind.Human -> "human"
        | ActorKind.Automation -> "automation"
        | ActorKind.Unknown -> "unknown"
        | ActorKind.Extension code -> code

    let tryParse (value: string) =
        match value with
        | "agent" -> Some ActorKind.Agent
        | "human" -> Some ActorKind.Human
        | "automation" -> Some ActorKind.Automation
        | "unknown" -> Some ActorKind.Unknown
        | other when not (String.IsNullOrEmpty other) && extension.IsMatch other -> Some(ActorKind.Extension other)
        | _ -> None

/// Who performed an action. `Provider`, `Model`, and `Runtime` are `None` for
/// a human (not applicable) and the literal `"unknown"` for a non-human actor
/// whose value is not known. "Unknown" and "not applicable" are never
/// conflated, and no value is ever invented.
type Actor =
    { Kind: ActorKind
      Id: string
      Provider: string option
      Model: string option
      Runtime: string option }

module Actor =
    [<Literal>]
    let UnknownValue = "unknown"

    /// The actor recorded when nobody declared one.
    let unknown =
        { Kind = ActorKind.Unknown
          Id = UnknownValue
          Provider = Some UnknownValue
          Model = Some UnknownValue
          Runtime = Some UnknownValue }

    let private isKnown (value: string option) =
        match value with
        | Some text -> not (String.IsNullOrWhiteSpace text) && text.Trim() <> UnknownValue
        | None -> false

    /// Same actor: kind, stable id, and every applicable known attribute
    /// agree; `unknown` never contradicts (mirrors the Praxis reference).
    let agrees (left: Actor) (right: Actor) =
        let compatible a b = not (isKnown a && isKnown b) || a = b

        left.Kind = right.Kind
        && (left.Id = right.Id || not (isKnown (Some left.Id)) || not (isKnown (Some right.Id)))
        && compatible left.Provider right.Provider
        && compatible left.Model right.Model
        && compatible left.Runtime right.Runtime

/// Praxis contribution operations (RQ-ROS-2026-A014). Only `Created`
/// establishes authorship. Measuring, discovering, reviewing, approving, and
/// validating are not modifications. `Other` holds an `x-...` extension or a
/// code a later Praxis 1.x release added; it is preserved verbatim.
[<RequireQualifiedAccess>]
type ContributionOperation =
    | Created
    | Modified
    | Reviewed
    | Approved
    | Superseded
    | Migrated
    | Discovered
    | Measured
    | Transformed
    | Remediated
    | Validated
    | Resolved
    | Other of code: string

module ContributionOperation =
    let private grammar = Regex("^[a-z][a-z0-9-]*\\z", RegexOptions.CultureInvariant)
    let private extension = Regex("^x-[a-z0-9][a-z0-9-]*\\z", RegexOptions.CultureInvariant)

    let known =
        [ ContributionOperation.Created
          ContributionOperation.Modified
          ContributionOperation.Reviewed
          ContributionOperation.Approved
          ContributionOperation.Superseded
          ContributionOperation.Migrated
          ContributionOperation.Discovered
          ContributionOperation.Measured
          ContributionOperation.Transformed
          ContributionOperation.Remediated
          ContributionOperation.Validated
          ContributionOperation.Resolved ]

    let code operation =
        match operation with
        | ContributionOperation.Created -> "created"
        | ContributionOperation.Modified -> "modified"
        | ContributionOperation.Reviewed -> "reviewed"
        | ContributionOperation.Approved -> "approved"
        | ContributionOperation.Superseded -> "superseded"
        | ContributionOperation.Migrated -> "migrated"
        | ContributionOperation.Discovered -> "discovered"
        | ContributionOperation.Measured -> "measured"
        | ContributionOperation.Transformed -> "transformed"
        | ContributionOperation.Remediated -> "remediated"
        | ContributionOperation.Validated -> "validated"
        | ContributionOperation.Resolved -> "resolved"
        | ContributionOperation.Other code -> code

    /// Any code matching the Praxis operation grammar; `None` otherwise.
    let tryParse (value: string) =
        if String.IsNullOrEmpty value || not (grammar.IsMatch value) then
            None
        else
            known
            |> List.tryFind (fun operation -> code operation = value)
            |> Option.orElse (Some(ContributionOperation.Other value))

    /// Neither a known operation nor an `x-...` extension: a newer Praxis
    /// vocabulary that a major-1 reader tolerates and reports.
    let isUnrecognised operation =
        match operation with
        | ContributionOperation.Other code -> not (extension.IsMatch code)
        | _ -> false

/// The key a contribution is recorded under: the execution that produced it.
[<RequireQualifiedAccess>]
type ContributionKey =
    /// `EXE-...`: a Praxis execution.
    | Execution of id: string
    /// `EXT-<system>.<run-id>`: an execution in another Echelon system.
    | ForeignExecution of system: string * runId: string
    /// `CTB-...`: a non-agent contributor outside any execution.
    | Contributor of id: string

module ContributionKey =
    let private execution = Regex("^EXE-[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)
    let private contributor = Regex("^CTB-[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)
    let private foreign = Regex("^EXT-([a-z][a-z0-9-]*)\\.([A-Za-z0-9._-]+)\\z", RegexOptions.CultureInvariant)

    let tryParse (value: string) =
        if String.IsNullOrEmpty value then
            None
        elif execution.IsMatch value then
            Some(ContributionKey.Execution value)
        else
            let found = foreign.Match value

            if found.Success then
                Some(ContributionKey.ForeignExecution(found.Groups[1].Value, found.Groups[2].Value))
            elif contributor.IsMatch value then
                Some(ContributionKey.Contributor value)
            else
                None

    let value key =
        match key with
        | ContributionKey.Execution id
        | ContributionKey.Contributor id -> id
        | ContributionKey.ForeignExecution(system, runId) -> $"EXT-{system}.{runId}"

    /// Builds `EXT-<system>.<run-id>`, refusing ids the key cannot carry.
    let foreignExecution (system: string) (runId: string) =
        match tryParse $"EXT-{system}.{runId}" with
        | Some(ContributionKey.ForeignExecution(parsedSystem, _) as key) when parsedSystem = system -> Ok key
        | _ -> Error $"cannot form a foreign execution key from system '{system}' and run '{runId}'"

    /// Agents must be keyed by an execution (`EXE-...` or `EXT-...`).
    let isExecution key =
        match key with
        | ContributionKey.Execution _
        | ContributionKey.ForeignExecution _ -> true
        | ContributionKey.Contributor _ -> false

/// One execution's contribution to a record. `At` and `Last` keep the exact
/// ISO-8601 UTC text that was recorded.
type Contribution =
    { Key: ContributionKey
      Operations: ContributionOperation list
      At: string
      Last: string option
      Actor: Actor
      Reason: string option
      Evidence: string list }

module Contribution =
    let has operation (contribution: Contribution) =
        contribution.Operations |> List.contains operation

/// The roles R14.1 keeps apart. Each is a Praxis operation on a specific
/// record: the artifact author is the `created` contribution of the measured
/// artifact's own provenance, while the measurement actor is recorded on the
/// snapshot, and remediation/validation/review/resolution on the finding.
[<RequireQualifiedAccess>]
type ProvenanceRole =
    | ArtifactAuthor
    | MeasurementActor
    | RemediationActor
    | ValidationActor
    | ReviewActor
    | ResolutionActor

module ProvenanceRole =
    let operation role =
        match role with
        | ProvenanceRole.ArtifactAuthor -> ContributionOperation.Created
        | ProvenanceRole.MeasurementActor -> ContributionOperation.Measured
        | ProvenanceRole.RemediationActor -> ContributionOperation.Remediated
        | ProvenanceRole.ValidationActor -> ContributionOperation.Validated
        | ProvenanceRole.ReviewActor -> ContributionOperation.Reviewed
        | ProvenanceRole.ResolutionActor -> ContributionOperation.Resolved
