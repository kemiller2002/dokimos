namespace Dokimos.Core

open Dokimos.Domain

/// The provenance a Dokimos record (snapshot, observation subject, finding)
/// carries after it passed the receiving boundary.
type RecordAttribution =
    /// No provenance was recorded (for example a legacy 1.0.0 snapshot).
    /// Nothing is inferred from its absence.
    | Unattributed
    /// A supported `praxis.provenance/1` block, preserved as received.
    | Attributed of ProvenanceVerdict
    /// Another major version: kept verbatim, never interpreted or merged into.
    | CarriedVerbatim of schema: string * block: Json

module RecordAttribution =
    let private collect (results: Result<'T, string> list) : Result<'T list, string> =
        results
        |> List.fold
            (fun state next ->
                match state, next with
                | Ok items, Ok item -> Ok(items @ [ item ])
                | Error message, _
                | _, Error message -> Error message)
            (Ok [])

    let internal all results = collect results

    /// Classifies an optional provenance value; malformed blocks are rejected.
    let receive (field: string) (value: Json option) : Result<RecordAttribution, string> =
        match value with
        | None -> Ok Unattributed
        | Some block ->
            match ProvenanceInterchange.classify block with
            | ProvenanceVerdict.Supported _ as verdict -> Ok(Attributed verdict)
            | ProvenanceVerdict.Unsupported(schema, carried) -> Ok(CarriedVerbatim(schema, carried))
            | ProvenanceVerdict.Malformed problems -> Error $"""{field} is malformed: {String.concat "; " problems}"""

    let verdict attribution =
        match attribution with
        | Attributed verdict -> Some verdict
        | _ -> None

    /// Contributions recorded with an operation, in time order (empty for
    /// unattributed or verbatim-carried provenance).
    let withOperation operation attribution =
        attribution |> verdict |> Option.map (ProvenanceInterchange.withOperation operation) |> Option.defaultValue []

    /// Contributions that played a role (R14.1).
    let withRole (role: ProvenanceRole) attribution =
        withOperation (ProvenanceRole.operation role) attribution

    /// The recorded originator, if any.
    let originator attribution =
        attribution |> verdict |> Option.bind ProvenanceInterchange.originator

    let lineage attribution =
        attribution |> verdict |> Option.map ProvenanceInterchange.lineage |> Option.defaultValue []

/// Provenance on Dokimos snapshot documents (`schemas/dokimos-snapshot.schema.json`,
/// R14.8, R14.9). Snapshot documents are the canonical JSON evidence; these
/// functions read them and build new ones, and never rewrite a historical one.
[<RequireQualifiedAccess>]
module SnapshotProvenance =
    [<Literal>]
    let LegacyVersion = "1.0.0"

    [<Literal>]
    let CurrentVersion = "1.1.0"

    let supportedVersions = [ LegacyVersion; CurrentVersion ]

    /// The document's schema version, when it is one this Dokimos reads.
    let schemaVersion (document: Json) : Result<string, string> =
        match document with
        | Json.Object _ ->
            match Json.tryField "schemaVersion" document with
            | Some(Json.String version) when List.contains version supportedVersions -> Ok version
            | Some(Json.String version) -> Error $"""snapshot schemaVersion '{version}' is not supported (expected {String.concat " or " supportedVersions})"""
            | Some _ -> Error "snapshot schemaVersion must be a string"
            | None -> Error "snapshot schemaVersion is required"
        | _ -> Error "a snapshot document must be a JSON object"

    /// The snapshot's own provenance: who measured, in which execution.
    /// A 1.0.0 document cannot carry it and reads as `Unattributed`.
    let read (document: Json) : Result<RecordAttribution, string> =
        schemaVersion document
        |> Result.bind (fun version ->
            match version, Json.tryField "provenance" document with
            | LegacyVersion, Some _ -> Error "a 1.0.0 snapshot cannot carry provenance; provenance requires schemaVersion 1.1.0"
            | _, block -> RecordAttribution.receive "snapshot provenance" block)

    let private observations (document: Json) =
        match Json.tryField "observations" document with
        | Some(Json.Array values) -> Ok values
        | _ -> Error "snapshot observations must be an array"

    /// Each observation's `subjectProvenance`: the measured artifact's own
    /// recorded provenance, in observation order.
    let subjects (document: Json) : Result<RecordAttribution list, string> =
        schemaVersion document
        |> Result.bind (fun _ -> observations document)
        |> Result.bind (fun values ->
            values
            |> List.mapi (fun index observation ->
                if Json.isObject observation then
                    RecordAttribution.receive $"observations[{index}].subjectProvenance" (Json.tryField "subjectProvenance" observation)
                else
                    Error $"observations[{index}] must be an object")
            |> RecordAttribution.all)

    let private requireCurrent (document: Json) =
        schemaVersion document
        |> Result.bind (fun version ->
            if version = LegacyVersion then
                Error "historical 1.0.0 snapshots are immutable evidence; provenance is never backfilled into them (R14.8)"
            else
                Ok document)

    /// Records a measurement contribution on a new 1.1.0 snapshot and returns
    /// the new document. Appends to supported provenance (append-only), refuses
    /// verbatim-carried or malformed provenance, and refuses 1.0.0 documents.
    let recordMeasurement (contribution: Contribution) (derivedFrom: string list) (document: Json) : Result<Json, string> =
        requireCurrent document
        |> Result.bind read
        |> Result.bind (fun attribution ->
            match attribution with
            | Unattributed -> MeasurementAttribution.block contribution derivedFrom
            | CarriedVerbatim(schema, _) -> Error $"snapshot provenance is {schema}; it is carried verbatim and never merged into"
            | Attributed(ProvenanceVerdict.Supported(block, _, _, _)) ->
                ProvenanceInterchange.append block contribution
                |> Result.map (fun (appended, _) -> ProvenanceInterchange.addLineage derivedFrom appended)
            | Attributed _ -> Error "snapshot provenance is not supported")
        |> Result.map (fun block -> Json.setField "provenance" block document)

    /// Attaches the measured artifact's own recorded provenance to one
    /// observation of a new 1.1.0 snapshot. The block is classified first:
    /// supported and unsupported blocks are kept exactly as received;
    /// malformed ones are rejected. An existing, different subject block is
    /// never overwritten.
    let attachSubject (index: int) (subject: Json) (document: Json) : Result<Json, string> =
        requireCurrent document
        |> Result.bind (fun _ -> RecordAttribution.receive "subjectProvenance" (Some subject))
        |> Result.bind (fun _ -> observations document)
        |> Result.bind (fun values ->
            match List.tryItem index values with
            | Some observation when Json.isObject observation ->
                match Json.tryField "subjectProvenance" observation with
                | Some existing when Json.serialize existing <> Json.serialize subject ->
                    Error $"observations[{index}] already carries different subject provenance; it is never overwritten"
                | _ ->
                    let updated = values |> List.mapi (fun position item -> if position = index then Json.setField "subjectProvenance" subject item else item)
                    Ok(Json.setField "observations" (Json.Array updated) document)
            | _ -> Error $"observations[{index}] does not exist")

/// Who authored a measured artifact, read only from recorded provenance
/// (R14.5). The measurement actor and heuristics never contribute.
[<RequireQualifiedAccess>]
module Authorship =
    /// The author of the measured artifact from its own recorded provenance
    /// (`subjectProvenance`), or `None` when no creation is recorded.
    let ofSubject (subject: RecordAttribution) = RecordAttribution.originator subject

    /// Follows the record's lineage one step: each `derivedFrom` reference is
    /// resolved through `resolve` to that record's own provenance and its
    /// recorded originator. References that cannot be resolved, or whose
    /// origin is not recorded, yield `None`; nothing is inferred.
    let viaLineage (resolve: string -> Json option) (record: RecordAttribution) : Result<(string * Contribution option) list, string> =
        RecordAttribution.lineage record
        |> List.map (fun reference ->
            RecordAttribution.receive $"provenance of {reference}" (resolve reference)
            |> Result.map (fun attribution -> reference, RecordAttribution.originator attribution))
        |> RecordAttribution.all

    /// The measurement actors recorded on a snapshot or observation.
    let measuredBy (record: RecordAttribution) =
        RecordAttribution.withRole ProvenanceRole.MeasurementActor record
