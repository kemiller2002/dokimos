namespace Dokimos.Core

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

// A local, dependency-free codec for the Praxis provenance interchange record
// (`praxis.provenance-record`, Praxis RQ-ROS-2026-A001/A004/A008/A013..A015).
// Dokimos takes no package or project reference on Praxis (R0.16). The JSON it
// reads and writes is exactly the contract's; the F# types below are an
// interpretation used for validation only. Every write goes through the raw
// JSON document, so fields this version does not model are never dropped.

/// Who (or what) performed an action. Recorded, never inferred (R0.13).
[<RequireQualifiedAccess>]
type ActorKind =
    | Agent
    | Human
    | Automation
    | Unknown
    | Extension of string

/// What a contribution did to a subject. `Created` establishes the originator.
[<RequireQualifiedAccess>]
type ProvenanceOperation =
    | Created
    | Modified
    | Reviewed
    | Approved
    | Superseded
    | Migrated
    | Extension of string

/// The portable actor. `Provider`/`Model`/`Runtime` are `None` when not
/// applicable (a human) and `Some "unknown"` when applicable but not known.
type ProvenanceActor =
    { ActorKind: ActorKind
      ActorId: string
      Provider: string option
      Model: string option
      Runtime: string option }

/// One actor's contribution, keyed by the execution (`EXE-...`) or
/// contribution (`CTB-...`) that produced it.
type ProvenanceContribution =
    { ContributionKey: string
      Operations: ProvenanceOperation list
      At: string
      Last: string option
      Actor: ProvenanceActor
      Reason: string option
      ContributionEvidence: string list }

type ContractVersion = { Major: int; Minor: int; Patch: int }

/// A lineage snapshot: a source's own record carried verbatim. An unsupported
/// major is opaque: preserved, never interpreted.
[<RequireQualifiedAccess>]
type SourceSnapshot =
    | Known of ProvenanceRecord
    | Opaque of version: string

and ProvenanceRecord =
    { ContractVersion: ContractVersion
      Subject: string option
      Contributions: ProvenanceContribution list
      DerivedFrom: string list
      LineageSources: (string * SourceSnapshot) list }

type ProvenanceProblem = { Field: string; Message: string }

/// What a reader found. `raw` is the document exactly as received.
[<RequireQualifiedAccess>]
type ProvenanceReading =
    | Current of record: ProvenanceRecord * raw: JsonObject
    | Unversioned of record: ProvenanceRecord * raw: JsonObject
    | Unsupported of version: string * raw: JsonObject

[<RequireQualifiedAccess>]
module ActorKind =
    let private extension = Regex("^x-[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)

    let code kind =
        match kind with
        | ActorKind.Agent -> "agent"
        | ActorKind.Human -> "human"
        | ActorKind.Automation -> "automation"
        | ActorKind.Unknown -> "unknown"
        | ActorKind.Extension value -> value

    let tryParse (value: string) =
        match value with
        | "agent" -> Some ActorKind.Agent
        | "human" -> Some ActorKind.Human
        | "automation" -> Some ActorKind.Automation
        | "unknown" -> Some ActorKind.Unknown
        | other when extension.IsMatch other -> Some(ActorKind.Extension other)
        | _ -> None

[<RequireQualifiedAccess>]
module ProvenanceOperation =
    let private extension = Regex("^x-[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)

    let code operation =
        match operation with
        | ProvenanceOperation.Created -> "created"
        | ProvenanceOperation.Modified -> "modified"
        | ProvenanceOperation.Reviewed -> "reviewed"
        | ProvenanceOperation.Approved -> "approved"
        | ProvenanceOperation.Superseded -> "superseded"
        | ProvenanceOperation.Migrated -> "migrated"
        | ProvenanceOperation.Extension value -> value

    let tryParse (value: string) =
        match value with
        | "created" -> Some ProvenanceOperation.Created
        | "modified" -> Some ProvenanceOperation.Modified
        | "reviewed" -> Some ProvenanceOperation.Reviewed
        | "approved" -> Some ProvenanceOperation.Approved
        | "superseded" -> Some ProvenanceOperation.Superseded
        | "migrated" -> Some ProvenanceOperation.Migrated
        | other when extension.IsMatch other -> Some(ProvenanceOperation.Extension other)
        | _ -> None

    /// Interchange extension operations a later actor records against a
    /// measurement or finding without displacing the measurer (R0.14).
    let remediated = ProvenanceOperation.Extension "x-remediated"
    let validated = ProvenanceOperation.Extension "x-validated"

/// A guard against accidentally recording authentication material. Not a
/// secret scanner: an unrecognized value is not thereby proven safe.
[<RequireQualifiedAccess>]
module Credentials =
    let private patterns =
        [ @"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{16,}"
          @"\bgh[pousr]_[A-Za-z0-9]{20,}"
          @"\bgithub_pat_[A-Za-z0-9_]{20,}"
          @"\bxox[abposr]-[A-Za-z0-9-]{10,}"
          @"\bAKIA[0-9A-Z]{16}\b"
          @"\bAIza[0-9A-Za-z_-]{30,}"
          @"-----BEGIN [A-Z ]*PRIVATE KEY-----"
          @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{16,}"
          @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"
          @"(?i)\b(?:api[_-]?key|access[_-]?token|secret|password|passwd)\s*[=:]\s*\S{8,}" ]
        |> List.map (fun pattern -> Regex(pattern, RegexOptions.CultureInvariant))

    let looksLikeCredential (value: string) =
        not (String.IsNullOrEmpty value) && patterns |> List.exists (fun pattern -> pattern.IsMatch value)

[<RequireQualifiedAccess>]
module ProvenanceActor =
    [<Literal>]
    let UnknownValue = "unknown"

    let unknown =
        { ActorKind = ActorKind.Unknown
          ActorId = UnknownValue
          Provider = Some UnknownValue
          Model = Some UnknownValue
          Runtime = Some UnknownValue }

    let private known (value: string) = value.Trim().Length > 0 && value.Trim() <> UnknownValue

    let problems (actor: ProvenanceActor) : (string * string) list =
        [ if actor.ActorId.Trim().Length = 0 then
              "id", "actor id must not be empty; use 'unknown' when it is not known"
          if actor.ActorKind = ActorKind.Agent then
              for field, value in [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ] do
                  match value with
                  | None -> field, $"agent actor must record {field} (use 'unknown' when it is not known)"
                  | Some text when text.Trim().Length = 0 -> field, $"agent actor {field} must not be empty"
                  | Some _ -> ()
          for field, value in [ "id", Some actor.ActorId; "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ] do
              match value with
              | Some text when Credentials.looksLikeCredential text ->
                  field, "value looks like a credential; identity must never carry secrets"
              | _ -> () ]

    /// Same kind and stable id, and no known attribute contradicts.
    let agrees (left: ProvenanceActor) (right: ProvenanceActor) =
        let compatible (a: string option) (b: string option) =
            match a, b with
            | Some x, Some y when known x && known y -> x = y
            | _ -> true

        left.ActorKind = right.ActorKind
        && (left.ActorId = right.ActorId || not (known left.ActorId) || not (known right.ActorId))
        && compatible left.Provider right.Provider
        && compatible left.Model right.Model
        && compatible left.Runtime right.Runtime

    let describe (actor: ProvenanceActor) =
        let detail =
            [ actor.Provider; actor.Model; actor.Runtime ]
            |> List.choose id
            |> function
                | [] -> ""
                | values -> " (" + String.concat ", " values + ")"

        $"{ActorKind.code actor.ActorKind}:{actor.ActorId}{detail}"

[<RequireQualifiedAccess>]
module ProvenanceContribution =
    let private executionPattern = Regex("^EXE-[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)
    let private contributionPattern = Regex("^CTB-[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)

    let private timestampPattern =
        Regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z$", RegexOptions.CultureInvariant)

    let isExecutionKey (value: string) = not (String.IsNullOrEmpty value) && executionPattern.IsMatch value

    let isTimestamp (value: string) =
        not (String.IsNullOrEmpty value)
        && timestampPattern.IsMatch value
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) |> fst

    /// The canonical ISO-8601 UTC form Dokimos writes.
    let timestamp (value: DateTimeOffset) =
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let isCreation (contribution: ProvenanceContribution) =
        contribution.Operations |> List.contains ProvenanceOperation.Created

    /// Invalid timestamps sort last so they never pose as the originator.
    let instant (value: string) =
        match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, parsed -> parsed
        | _ -> DateTimeOffset.MaxValue

    let problems (contribution: ProvenanceContribution) : (string * string) list =
        [ if not (executionPattern.IsMatch contribution.ContributionKey || contributionPattern.IsMatch contribution.ContributionKey) then
              "key", $"contribution key '{contribution.ContributionKey}' must be an execution ID (EXE-...) or a contribution ID (CTB-...)"
          if contribution.Operations.IsEmpty then
              "operations", "contribution must record at least one operation"
          if not (isTimestamp contribution.At) then
              "at", $"'{contribution.At}' is not an ISO-8601 UTC timestamp"
          match contribution.Last with
          | Some last when not (isTimestamp last) -> "last", $"'{last}' is not an ISO-8601 UTC timestamp"
          | Some last when instant last < instant contribution.At -> "last", "last must not precede at"
          | _ -> ()
          yield! ProvenanceActor.problems contribution.Actor |> List.map (fun (field, message) -> $"actor.{field}", message)
          if contribution.Actor.ActorKind = ActorKind.Agent && not (isExecutionKey contribution.ContributionKey) then
              "key", "an agent contribution must be keyed by the execution (EXE-...) that produced it"
          if contribution.ContributionEvidence |> List.exists (fun item -> item.Trim().Length = 0) then
              "evidence", "evidence references must not be empty"
          if contribution.Reason |> Option.exists Credentials.looksLikeCredential then
              "reason", "reason looks like it contains a credential; provenance must never carry secrets"
          if contribution.ContributionEvidence |> List.exists Credentials.looksLikeCredential then
              "evidence", "an evidence reference looks like a credential; provenance must never carry secrets" ]

[<RequireQualifiedAccess>]
module ContractVersion =
    let private pattern = Regex("^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant)

    /// The version Dokimos writes.
    let current = { Major = 1; Minor = 0; Patch = 0 }

    let code (version: ContractVersion) = $"{version.Major}.{version.Minor}.{version.Patch}"

    let tryParse (value: string) =
        let matched = if String.IsNullOrEmpty value then Match.Empty else pattern.Match value

        match matched.Success, Int32.TryParse matched.Groups[1].Value, Int32.TryParse matched.Groups[2].Value, Int32.TryParse matched.Groups[3].Value with
        | true, (true, major), (true, minor), (true, patch) -> Some { Major = major; Minor = minor; Patch = patch }
        | _ -> None

    let isSupported (version: ContractVersion) = version.Major = current.Major

[<RequireQualifiedAccess>]
module ProvenanceRecord =
    [<Literal>]
    let ContractName = "praxis.provenance-record"

    [<Literal>]
    let MaxSourceDepth = 16

    let private systemPattern = Regex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)
    let private runPattern = Regex("^[A-Za-z0-9_-][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)
    let private referencePattern = Regex("^\S+$", RegexOptions.CultureInvariant)

    /// `EXE-<system>.<run>`: a run of a system with no propagated Praxis
    /// execution (Praxis RQ-ROS-2026-A014). Never a Praxis-shaped id.
    let foreignExecutionKey (system: string) (run: string) : Result<string, string> =
        if String.IsNullOrEmpty system || not (systemPattern.IsMatch system) then
            Error $"system '{system}' must match ^[a-z][a-z0-9-]*$"
        elif String.IsNullOrEmpty run || not (runPattern.IsMatch run) then
            Error $"run '{run}' must match ^[A-Za-z0-9_-][A-Za-z0-9._-]*$"
        else
            Ok $"EXE-{system}.{run}"

    let isReference (value: string) = not (String.IsNullOrEmpty value) && referencePattern.IsMatch value

    let originator (record: ProvenanceRecord) =
        record.Contributions |> List.tryFind ProvenanceContribution.isCreation

    let private order (contributions: ProvenanceContribution list) =
        contributions
        |> List.sortWith (fun left right ->
            match compare (ProvenanceContribution.instant left.At) (ProvenanceContribution.instant right.At) with
            | 0 -> StringComparer.Ordinal.Compare(left.ContributionKey, right.ContributionKey)
            | byTime -> byTime)

    let private problem field message : ProvenanceProblem = { Field = field; Message = message }

    let rec private problemsAt (prefix: string) (depth: int) (record: ProvenanceRecord) : ProvenanceProblem list =
        let at field = if prefix = "" then field else $"{prefix}.{field}"

        let perContribution =
            record.Contributions
            |> List.collect (fun contribution ->
                ProvenanceContribution.problems contribution
                |> List.map (fun (field, message) -> problem (at $"contributions.{contribution.ContributionKey}.{field}") message))

        let creationProblems =
            match record.Contributions |> List.filter ProvenanceContribution.isCreation with
            | [] -> []
            | [ creation ] ->
                record.Contributions
                |> List.filter (fun item -> ProvenanceContribution.instant item.At < ProvenanceContribution.instant creation.At)
                |> List.map (fun item ->
                    problem (at $"contributions.{item.ContributionKey}.at") $"contribution precedes the recorded creation ({creation.ContributionKey})")
            | many ->
                [ problem (at "contributions") ("more than one contribution claims 'created': " + (many |> List.map _.ContributionKey |> String.concat ", ")) ]

        let referenceProblems =
            [ yield! record.Subject |> Option.toList |> List.map (fun value -> "subject", value)
              yield! record.DerivedFrom |> List.map (fun value -> "derivedFrom", value) ]
            |> List.collect (fun (field, value) ->
                [ if not (isReference value) then
                      problem (at field) $"reference '{value}' must be a non-empty token without whitespace"
                  if Credentials.looksLikeCredential value then
                      problem (at field) "value looks like a credential; provenance must never carry secrets" ])

        let lineageProblems =
            [ match record.Subject with
              | Some subject when List.contains subject record.DerivedFrom -> problem (at "derivedFrom") $"'{subject}' cannot be derived from itself"
              | _ -> ()
              if List.length (List.distinct record.DerivedFrom) <> List.length record.DerivedFrom then
                  problem (at "derivedFrom") "lineage references must be unique"
              for reference, snapshot in record.LineageSources do
                  if not (List.contains reference record.DerivedFrom) then
                      problem (at $"sources.{reference}") "a lineage snapshot must name a reference listed in derivedFrom"
                  match snapshot with
                  | SourceSnapshot.Known source when source.Subject.IsSome && source.Subject <> Some reference ->
                      problem (at $"sources.{reference}.subject") "a lineage snapshot must be the named source's own provenance"
                  | _ -> () ]

        let sourceProblems =
            record.LineageSources
            |> List.collect (fun (reference, snapshot) ->
                match snapshot with
                | SourceSnapshot.Opaque _ -> []
                | SourceSnapshot.Known _ when depth >= MaxSourceDepth -> [ problem (at $"sources.{reference}") "lineage snapshots nest too deeply" ]
                | SourceSnapshot.Known source -> problemsAt (at $"sources.{reference}") (depth + 1) source)

        perContribution @ creationProblems @ referenceProblems @ lineageProblems @ sourceProblems

    /// Structural problems; empty means well-formed (not that it is true).
    let problems (record: ProvenanceRecord) = problemsAt "" 0 record

    /// Append-only: adds an entry or extends the same execution's own entry;
    /// refuses re-attribution, a second `created`, and a late `created`.
    let record (contribution: ProvenanceContribution) (record: ProvenanceRecord) : Result<ProvenanceRecord, string> =
        let withContributions contributions = { record with Contributions = order contributions }

        match record.Contributions |> List.tryFind (fun item -> item.ContributionKey = contribution.ContributionKey) with
        | Some existing when not (ProvenanceActor.agrees existing.Actor contribution.Actor) ->
            Error
                $"contribution '{contribution.ContributionKey}' is attributed to {ProvenanceActor.describe existing.Actor}; refusing to re-attribute it to {ProvenanceActor.describe contribution.Actor}"
        | Some existing ->
            let latest = existing.Last |> Option.defaultValue existing.At

            let merged =
                { existing with
                    Operations = existing.Operations @ (contribution.Operations |> List.filter (fun item -> not (List.contains item existing.Operations)))
                    ContributionEvidence =
                        existing.ContributionEvidence
                        @ (contribution.ContributionEvidence |> List.filter (fun item -> not (List.contains item existing.ContributionEvidence)))
                    Last =
                        if ProvenanceContribution.instant contribution.At > ProvenanceContribution.instant latest then
                            Some contribution.At
                        else
                            existing.Last
                    Reason = existing.Reason |> Option.orElse contribution.Reason }

            record.Contributions
            |> List.map (fun item -> if item.ContributionKey = existing.ContributionKey then merged else item)
            |> withContributions
            |> Ok
        | None when ProvenanceContribution.isCreation contribution && (originator record).IsSome ->
            Error "the subject already has a recorded originator; a later contribution cannot claim 'created'"
        | None when
            ProvenanceContribution.isCreation contribution
            && record.Contributions |> List.exists (fun item -> ProvenanceContribution.instant item.At < ProvenanceContribution.instant contribution.At)
            ->
            Error "a 'created' contribution cannot follow existing contributions"
        | None -> record.Contributions @ [ contribution ] |> withContributions |> Ok

    /// One hop must keep every contribution (key, actor, `at`, operations,
    /// evidence, reason), the originator, every lineage reference and
    /// snapshot, the subject, the major, and must not lower the version.
    let successorProblems (before: ProvenanceRecord) (after: ProvenanceRecord) : ProvenanceProblem list =
        let afterByKey = after.Contributions |> List.map (fun item -> item.ContributionKey, item) |> Map.ofList

        let contributionProblems =
            before.Contributions
            |> List.collect (fun previous ->
                let field name = $"contributions.{previous.ContributionKey}{name}"

                match afterByKey |> Map.tryFind previous.ContributionKey with
                | None -> [ problem (field "") "contribution was removed; provenance history is append-only" ]
                | Some current ->
                    [ if current.Actor <> previous.Actor then
                          problem (field ".actor") $"actor changed from {ProvenanceActor.describe previous.Actor} to {ProvenanceActor.describe current.Actor}"
                      if current.At <> previous.At then
                          problem (field ".at") "the time of the first recorded operation changed"
                      for operation in previous.Operations do
                          if not (List.contains operation current.Operations) then
                              problem (field ".operations") $"operation '{ProvenanceOperation.code operation}' was removed"
                      for item in previous.ContributionEvidence do
                          if not (List.contains item current.ContributionEvidence) then
                              problem (field ".evidence") $"evidence '{item}' was removed"
                      match previous.Reason with
                      | Some reason when current.Reason <> Some reason -> problem (field ".reason") "reason was rewritten"
                      | _ -> () ])

        let originProblems =
            match originator before, originator after with
            | Some previous, Some current when previous.ContributionKey <> current.ContributionKey ->
                [ problem "contributions" $"originator changed from {previous.ContributionKey} to {current.ContributionKey}" ]
            | _ -> []

        let lineageProblems =
            [ for reference in before.DerivedFrom do
                  if not (List.contains reference after.DerivedFrom) then
                      problem "derivedFrom" $"lineage reference '{reference}' was removed"
              for reference, _ in before.LineageSources do
                  if not (after.LineageSources |> List.exists (fst >> (=) reference)) then
                      problem $"sources.{reference}" "lineage snapshot was removed"
              if before.Subject.IsSome && after.Subject <> before.Subject then
                  problem "subject" "subject changed; a different subject needs its own record"
              if after.ContractVersion.Major <> before.ContractVersion.Major then
                  problem "version" "major version changed in place"
              elif compare (after.ContractVersion.Minor, after.ContractVersion.Patch) (before.ContractVersion.Minor, before.ContractVersion.Patch) < 0 then
                  problem "version" "version was lowered; a newer record must not be relabelled as an older one" ]

        contributionProblems @ originProblems @ lineageProblems

/// The JSON codec. Reads interpret; writes clone the raw document and change
/// only what the operation requires, then prove the result is a preserving
/// successor of the input.
[<RequireQualifiedAccess>]
module ProvenanceJson =
    let private problem field message : ProvenanceProblem = { Field = field; Message = message }

    /// A property of an object, `None` when absent or JSON null.
    let property (item: JsonObject) (name: string) : JsonNode option =
        match item.TryGetPropertyValue name with
        | true, value -> Option.ofObj value
        | _ -> None

    let private members (item: JsonObject) : (string * JsonNode option) list =
        item |> Seq.map (fun pair -> pair.Key, Option.ofObj pair.Value) |> Seq.toList

    let private stringOf (node: JsonNode option) =
        match node with
        | Some(:? JsonValue as value) ->
            match value.TryGetValue<string>() with
            | true, text -> Option.ofObj text
            | _ -> None
        | _ -> None

    let private stringArray (field: string) (node: JsonNode option) : Result<string list, ProvenanceProblem list> =
        match node with
        | None -> Ok []
        | Some(:? JsonArray as items) ->
            let values = items |> Seq.map (Option.ofObj >> stringOf) |> Seq.toList

            if values |> List.forall Option.isSome then
                Ok(List.choose id values)
            else
                Error [ problem field "must be an array of strings" ]
        | Some _ -> Error [ problem field "must be an array of strings" ]

    let private collect (results: Result<'value, ProvenanceProblem list> list) : Result<'value list, ProvenanceProblem list> =
        match results |> List.collect (function Error problems -> problems | Ok _ -> []) with
        | [] -> results |> List.choose (function Ok value -> Some value | Error _ -> None) |> Ok
        | problems -> Error problems

    let private errorsOf (results: Result<unit, ProvenanceProblem list> list) =
        results |> List.collect (function Error problems -> problems | Ok () -> [])

    let private byTime (contributions: ProvenanceContribution list) =
        contributions
        |> List.sortWith (fun left right ->
            match compare (ProvenanceContribution.instant left.At) (ProvenanceContribution.instant right.At) with
            | 0 -> StringComparer.Ordinal.Compare(left.ContributionKey, right.ContributionKey)
            | order -> order)

    let private parseActor (field: string) (node: JsonNode option) : Result<ProvenanceActor, ProvenanceProblem list> =
        match node with
        | Some(:? JsonObject as item) ->
            match stringOf (property item "kind") with
            | None -> Error [ problem $"{field}.kind" "actor kind is required" ]
            | Some kindText ->
                match ActorKind.tryParse kindText with
                | None -> Error [ problem $"{field}.kind" $"unknown actor kind '{kindText}'" ]
                | Some kind ->
                    Ok
                        { ActorKind = kind
                          ActorId = stringOf (property item "id") |> Option.defaultValue ""
                          Provider = stringOf (property item "provider")
                          Model = stringOf (property item "model")
                          Runtime = stringOf (property item "runtime") }
        | None -> Error [ problem field "actor is required" ]
        | Some _ -> Error [ problem field "actor must be an object" ]

    let private parseContribution (key: string) (node: JsonNode option) : Result<ProvenanceContribution, ProvenanceProblem list> =
        let prefix = $"contributions.{key}"

        match node with
        | Some(:? JsonObject as entry) ->
            let operations = stringArray $"{prefix}.operations" (property entry "operations")
            let evidence = stringArray $"{prefix}.evidence" (property entry "evidence")
            let actor = parseActor $"{prefix}.actor" (property entry "actor")

            match operations, evidence, actor with
            | Ok texts, Ok evidenceItems, Ok parsedActor ->
                let unknown =
                    texts
                    |> List.filter (ProvenanceOperation.tryParse >> Option.isNone)
                    |> List.map (fun text -> problem $"{prefix}.operations" $"unknown operation '{text}'")

                let duplicates =
                    if List.length (List.distinct texts) <> List.length texts then
                        [ problem $"{prefix}.operations" "operations must be unique" ]
                    else
                        []

                match unknown @ duplicates with
                | [] ->
                    Ok
                        { ContributionKey = key
                          Operations = texts |> List.choose ProvenanceOperation.tryParse
                          At = stringOf (property entry "at") |> Option.defaultValue ""
                          Last = stringOf (property entry "last")
                          Actor = parsedActor
                          Reason = stringOf (property entry "reason")
                          ContributionEvidence = evidenceItems }
                | problems -> Error problems
            | _ -> Error(errorsOf [ Result.map ignore operations; Result.map ignore evidence; Result.map ignore actor ])
        | _ -> Error [ problem prefix "contribution must be an object" ]

    let rec private readAt (depth: int) (node: JsonNode option) : Result<ProvenanceReading, ProvenanceProblem list> =
        match node with
        | Some(:? JsonObject as raw) ->
            let body (version: ContractVersion) : Result<ProvenanceRecord, ProvenanceProblem list> =
                let contributions =
                    match property raw "contributions" with
                    | Some(:? JsonObject as entries) -> members entries |> List.map (fun (key, value) -> parseContribution key value) |> collect
                    | None -> Error [ problem "contributions" "contributions is required" ]
                    | Some _ -> Error [ problem "contributions" "contributions must be an object keyed by EXE-/CTB- id" ]

                let derivedFrom = stringArray "derivedFrom" (property raw "derivedFrom")

                let subject =
                    match property raw "subject" with
                    | None -> Ok None
                    | value ->
                        match stringOf value with
                        | Some text -> Ok(Some text)
                        | None -> Error [ problem "subject" "subject must be a string" ]

                let sources =
                    match property raw "sources" with
                    | None -> Ok []
                    | Some(:? JsonObject as snapshots) when depth >= ProvenanceRecord.MaxSourceDepth && snapshots.Count > 0 ->
                        Error [ problem "sources" "lineage snapshots nest too deeply" ]
                    | Some(:? JsonObject as snapshots) ->
                        members snapshots
                        |> List.map (fun (reference, value) ->
                            match readAt (depth + 1) value with
                            | Ok(ProvenanceReading.Current(source, _))
                            | Ok(ProvenanceReading.Unversioned(source, _)) -> Ok(reference, SourceSnapshot.Known source)
                            | Ok(ProvenanceReading.Unsupported(sourceVersion, _)) -> Ok(reference, SourceSnapshot.Opaque sourceVersion)
                            | Error problems -> Error(problems |> List.map (fun item -> { item with Field = $"sources.{reference}.{item.Field}" })))
                        |> collect
                    | Some _ -> Error [ problem "sources" "sources must be an object keyed by lineage reference" ]

                match contributions, derivedFrom, subject, sources with
                | Ok entries, Ok lineage, Ok subjectText, Ok snapshots ->
                    Ok
                        { ContractVersion = version
                          Subject = subjectText
                          Contributions = byTime entries
                          DerivedFrom = lineage
                          LineageSources = snapshots }
                | _ ->
                    Error(
                        errorsOf
                            [ Result.map ignore contributions
                              Result.map ignore derivedFrom
                              Result.map ignore subject
                              Result.map ignore sources ]
                    )

            match property raw "contract", property raw "version" with
            | None, None -> body ContractVersion.current |> Result.map (fun record -> ProvenanceReading.Unversioned(record, raw))
            | contract, _ when stringOf contract <> Some ProvenanceRecord.ContractName ->
                Error [ problem "contract" $"contract must be '{ProvenanceRecord.ContractName}'" ]
            | _, version ->
                match version |> stringOf |> Option.bind ContractVersion.tryParse with
                | None -> Error [ problem "version" "version must be a semantic version (MAJOR.MINOR.PATCH)" ]
                | Some parsed when not (ContractVersion.isSupported parsed) ->
                    Ok(ProvenanceReading.Unsupported(ContractVersion.code parsed, raw))
                | Some parsed -> body parsed |> Result.map (fun record -> ProvenanceReading.Current(record, raw))
        | _ -> Error [ problem "" "a provenance record must be a JSON object" ]

    /// Structure only; see `validate`.
    let read (node: JsonObject) = readAt 0 (Some(node :> JsonNode))

    let interpreted (reading: ProvenanceReading) =
        match reading with
        | ProvenanceReading.Current(record, _)
        | ProvenanceReading.Unversioned(record, _) -> Some record
        | ProvenanceReading.Unsupported _ -> None

    /// Reads and applies every structural rule. An unsupported major is not
    /// an error: the reading says so and the record must be carried verbatim.
    let validate (node: JsonObject) : Result<ProvenanceReading, ProvenanceProblem list> =
        match read node with
        | Ok reading ->
            match interpreted reading |> Option.map ProvenanceRecord.problems with
            | Some(_ :: _ as problems) -> Error problems
            | _ -> Ok reading
        | Error problems -> Error problems

    /// Parses text; a non-object or malformed document is a problem, not an exception.
    let parse (text: string) : Result<JsonObject, ProvenanceProblem list> =
        try
            match JsonNode.Parse text with
            | :? JsonObject as item -> Ok item
            | _ -> Error [ problem "" "a provenance record must be a JSON object" ]
        with :? JsonException as error ->
            Error [ problem "" $"not valid JSON: {error.Message}" ]

    let private strings (values: string list) =
        let array = JsonArray()
        values |> List.iter (fun value -> array.Add(JsonValue.Create value))
        array

    /// `{"kind","id","provider"?,"model"?,"runtime"?}` in contract key order.
    let actorNode (actor: ProvenanceActor) : JsonObject =
        let node = JsonObject()
        node["kind"] <- JsonValue.Create(ActorKind.code actor.ActorKind)
        node["id"] <- JsonValue.Create actor.ActorId

        [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ]
        |> List.iter (fun (name, value) -> value |> Option.iter (fun text -> node[name] <- JsonValue.Create text))

        node

    let contributionNode (contribution: ProvenanceContribution) : JsonObject =
        let node = JsonObject()
        node["operations"] <- strings (contribution.Operations |> List.map ProvenanceOperation.code)
        node["at"] <- JsonValue.Create contribution.At
        contribution.Last |> Option.iter (fun last -> node["last"] <- JsonValue.Create last)
        node["actor"] <- actorNode contribution.Actor
        contribution.Reason |> Option.iter (fun reason -> node["reason"] <- JsonValue.Create reason)

        if not contribution.ContributionEvidence.IsEmpty then
            node["evidence"] <- strings contribution.ContributionEvidence

        node

    let private same (left: JsonNode option) (right: JsonNode option) =
        match left, right with
        | Some a, Some b -> JsonNode.DeepEquals(a, b)
        | None, None -> true
        | _ -> false

    let private asObject (node: JsonNode option) =
        match node with
        | Some(:? JsonObject as item) -> Some item
        | _ -> None

    /// JSON-level preservation: every field `before` carried (including
    /// unmodelled ones on the record and each contribution/actor) survives
    /// unchanged, except the extendable `operations`/`evidence`/`last` and a
    /// previously absent `reason`; lineage snapshots stay verbatim.
    let private preservationProblems (before: JsonObject) (after: JsonObject) : ProvenanceProblem list =
        let envelope = set [ "contributions"; "derivedFrom"; "sources"; "version"; "contract" ]

        let topLevel =
            members before
            |> List.filter (fun (key, value) -> not (envelope.Contains key) && not (after.ContainsKey key && same value (property after key)))
            |> List.map (fun (key, _) -> problem key "field was removed or changed; fields a consumer does not model must be preserved")

        let extendable = set [ "operations"; "evidence"; "last"; "reason" ]

        let contributions =
            match asObject (property before "contributions"), asObject (property after "contributions") with
            | Some previous, Some current ->
                members previous
                |> List.collect (fun (key, value) ->
                    match asObject value, asObject (property current key) with
                    | Some entry, Some successor ->
                        members entry
                        |> List.filter (fun (name, field) -> not (extendable.Contains name) && not (successor.ContainsKey name && same field (property successor name)))
                        |> List.map (fun (name, _) -> problem $"contributions.{key}.{name}" "another contributor's entry must be preserved verbatim")
                    | _ -> [])
            | _ -> []

        let snapshots =
            match asObject (property before "sources"), asObject (property after "sources") with
            | Some previous, Some current ->
                members previous
                |> List.filter (fun (key, value) -> current.ContainsKey key && not (same value (property current key)))
                |> List.map (fun (key, _) -> problem $"sources.{key}" "lineage snapshot must be carried verbatim")
            | _ -> []

        topLevel @ contributions @ snapshots

    /// Whether `after` is a non-destructive successor of `before` (Praxis
    /// RQ-ROS-2026-A015). An unsupported `before` must be carried verbatim.
    let successorProblems (before: JsonObject) (after: JsonObject) : ProvenanceProblem list =
        match read before, read after with
        | Ok(ProvenanceReading.Unsupported _), _ ->
            if JsonNode.DeepEquals(before, after) then
                []
            else
                [ problem "" "a record in an unsupported major version must be carried verbatim" ]
        | Error _, _ -> [ problem "" "the previous record is malformed; refusing to judge a successor of it" ]
        | _, Error problems -> problems
        | Ok previous, Ok current ->
            match interpreted previous, interpreted current with
            | Some previousRecord, Some currentRecord ->
                ProvenanceRecord.successorProblems previousRecord currentRecord @ preservationProblems before after
            | _ -> [ problem "version" "a supported record was replaced by an unsupported major version" ]

    let private envelope (subject: string) =
        let node = JsonObject()
        node["contract"] <- JsonValue.Create ProvenanceRecord.ContractName
        node["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)
        node["subject"] <- JsonValue.Create subject
        node

    /// A new subject derived from other subjects: `creator` authors it; each
    /// source record is carried verbatim under `sources` and named in
    /// `derivedFrom`; `lineageOnly` names sources whose provenance is not held.
    /// A source's contributors are never copied into `contributions`.
    let derive
        (subject: string)
        (creator: ProvenanceContribution)
        (sources: (string * JsonObject) list)
        (lineageOnly: string list)
        : Result<JsonObject, ProvenanceProblem list> =
        let distinctSources = sources |> List.distinctBy fst

        let snapshots =
            distinctSources
            |> List.map (fun (reference, node) ->
                match read node with
                | Ok(ProvenanceReading.Unsupported(version, _)) -> Ok(reference, SourceSnapshot.Opaque version)
                | Ok(ProvenanceReading.Current(record, _))
                | Ok(ProvenanceReading.Unversioned(record, _)) -> Ok(reference, SourceSnapshot.Known record)
                | Error problems -> Error(problems |> List.map (fun item -> { item with Field = $"sources.{reference}.{item.Field}" })))
            |> collect

        match snapshots with
        | Error problems -> Error problems
        | Ok _ when not (ProvenanceContribution.isCreation creator) ->
            Error [ problem "contributions" "the first contribution to a derived subject must be 'created'" ]
        | Ok known ->
            let lineage = (List.map fst known @ lineageOnly) |> List.distinct

            let record =
                { ContractVersion = ContractVersion.current
                  Subject = Some subject
                  Contributions = [ creator ]
                  DerivedFrom = lineage
                  LineageSources = known }

            match ProvenanceRecord.problems record with
            | _ :: _ as problems -> Error problems
            | [] ->
                let node = envelope subject
                let contributions = JsonObject()
                contributions[creator.ContributionKey] <- contributionNode creator
                node["contributions"] <- contributions

                if not lineage.IsEmpty then
                    node["derivedFrom"] <- strings lineage

                if not distinctSources.IsEmpty then
                    let sourceNode = JsonObject()
                    distinctSources |> List.iter (fun (reference, source) -> sourceNode[reference] <- source.DeepClone())
                    node["sources"] <- sourceNode

                Ok node

    /// Appends (or merges into the contributing execution's own entry) one
    /// contribution to a raw record. Only the touched entry's
    /// `operations`/`last`/`evidence`/`reason` change; the result is proven
    /// to be a preserving successor before it is returned. Unsupported
    /// majors and malformed records are refused, never extended.
    let append (contribution: ProvenanceContribution) (raw: JsonObject) : Result<JsonObject, ProvenanceProblem list> =
        match validate raw with
        | Error problems -> Error(problem "" "refusing to extend malformed provenance" :: problems)
        | Ok(ProvenanceReading.Unsupported(version, _)) ->
            Error [ problem "version" $"version {version} is not supported; the record must be carried verbatim, not extended" ]
        | Ok(ProvenanceReading.Current(record, _))
        | Ok(ProvenanceReading.Unversioned(record, _)) ->
            match ProvenanceRecord.record contribution record with
            | Error message -> Error [ problem $"contributions.{contribution.ContributionKey}" message ]
            | Ok updated ->
                let merged = updated.Contributions |> List.find (fun item -> item.ContributionKey = contribution.ContributionKey)
                let result = raw.DeepClone().AsObject()

                if (property result "contract").IsNone then
                    result["contract"] <- JsonValue.Create ProvenanceRecord.ContractName
                    result["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)

                let canonical = contributionNode merged

                match asObject (property result "contributions") with
                | None -> Error [ problem "contributions" "contributions is required" ]
                | Some contributions ->
                    match asObject (property contributions contribution.ContributionKey) with
                    | Some existing ->
                        [ "operations"; "last"; "evidence"; "reason" ]
                        |> List.iter (fun name -> property canonical name |> Option.iter (fun value -> existing[name] <- value.DeepClone()))
                    | None -> contributions[contribution.ContributionKey] <- canonical

                    match validate result with
                    | Error problems -> Error problems
                    | Ok _ ->
                        match successorProblems raw result with
                        | [] -> Ok result
                        | problems -> Error(problem "" "the appended record failed its own preservation check" :: problems)
