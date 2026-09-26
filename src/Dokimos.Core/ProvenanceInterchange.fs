namespace Dokimos.Core

open System
open System.Globalization
open System.Text.RegularExpressions
open Dokimos.Domain

/// What Dokimos may do with a provenance block it received (R14.2,
/// RQ-ROS-2026-A015).
[<RequireQualifiedAccess>]
type ProvenanceVerdict =
    /// A `praxis.provenance/1` block. `Block` is exactly what was received,
    /// so unknown fields survive; `Contributions` is the typed view.
    /// `Warnings` name tolerated forward-compatible operation codes.
    | Supported of block: Json * contributions: Contribution list * derivedFrom: string list * warnings: string list
    /// Another major version: carry `Block` verbatim, never interpret or merge.
    | Unsupported of schema: string * block: Json
    /// Reject at the boundary; never drop or repair silently.
    | Malformed of problems: string list

/// Dokimos codec for the Praxis provenance interchange block
/// (`praxis.provenance/1`). It mirrors the receiving and appending rules of
/// the Praxis reference library (`lib/provenance-interchange.mjs` at the
/// commit recorded in `tests/fixtures/praxis-provenance/SOURCE.json`) and is
/// tested against every vendored conformance case. It works on whole JSON
/// values so fields it does not model are preserved. Every function is pure.
[<RequireQualifiedAccess>]
module ProvenanceInterchange =
    [<Literal>]
    let SchemaTag = "praxis.provenance/1"

    let private schemaPattern = Regex("^praxis\\.provenance/([1-9][0-9]*)\\z", RegexOptions.CultureInvariant)
    let private kindPattern = Regex("^(agent|human|automation|unknown|x-[a-z0-9][a-z0-9-]*)\\z", RegexOptions.CultureInvariant)
    let private operationGrammar = Regex("^[a-z][a-z0-9-]*\\z", RegexOptions.CultureInvariant)
    let private timestampPattern =
        Regex("^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\\.([0-9]{1,9}))?Z\\z", RegexOptions.CultureInvariant)

    let private credentialPatterns =
        [ "gh[pousr]_[A-Za-z0-9]{20,}"
          "github_pat_[A-Za-z0-9_]{20,}"
          "sk-[A-Za-z0-9_-]{20,}"
          "AKIA[0-9A-Z]{16}"
          "xox[abprs]-[A-Za-z0-9-]{10,}"
          "-----BEGIN [A-Z ]*PRIVATE KEY-----"
          "(?i)\\bbearer\\s+[A-Za-z0-9._~+/=-]{16,}"
          "eyJ[A-Za-z0-9_-]{10,}\\.[A-Za-z0-9_-]{10,}\\." ]
        |> List.map (fun pattern -> Regex(pattern, RegexOptions.CultureInvariant))

    /// A tripwire for authentication material (RQ-ROS-2026-A017), not a
    /// complete secret scanner.
    let isCredentialLike (value: string) =
        not (String.IsNullOrEmpty value) && credentialPatterns |> List.exists (fun pattern -> pattern.IsMatch value)

    /// Dotted paths of every member name or string value that looks like a credential.
    let credentialFindings (value: Json) : string list =
        let rec walk (path: string) (current: Json) =
            match current with
            | Json.Object members ->
                members
                |> List.collect (fun (name, item) ->
                    let child = if path.Length = 0 then name else $"{path}.{name}"
                    (if isCredentialLike name then [ child ] else []) @ walk child item)
            | Json.Array items -> items |> List.mapi (fun index item -> walk $"{path}[{index}]" item) |> List.concat
            | Json.String text when isCredentialLike text -> [ path ]
            | _ -> []

        walk "" value

    let private isNonEmpty (text: string) = not (String.IsNullOrWhiteSpace text)

    /// Milliseconds since 0001-01-01 for a calendar-valid UTC timestamp (year
    /// 0001-9999, no rollover such as Feb 30 or 24:00), or `None`. Ordering is
    /// at millisecond precision: extra fraction digits are truncated, never
    /// rounded (contract revision 1.1). Never throws.
    let parseTimestamp (text: string) : int64 option =
        if String.IsNullOrEmpty text then
            None
        else
            let found = timestampPattern.Match text

            if not found.Success then
                None
            else
                let part (index: int) = Int32.Parse(found.Groups[index].Value, CultureInfo.InvariantCulture)
                let year, month, day, hour, minute, second = part 1, part 2, part 3, part 4, part 5, part 6
                let fraction = found.Groups[7].Value.PadRight(3, '0').Substring(0, 3)
                let millisecond = Int32.Parse(fraction, CultureInfo.InvariantCulture)

                if year < 1 || month < 1 || month > 12 || hour > 23 || minute > 59 || second > 59 then None
                elif day < 1 || day > DateTime.DaysInMonth(year, month) then None
                else Some(DateTime(year, month, day, hour, minute, second, millisecond, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerMillisecond)

    let private parseInstant (text: string) = parseTimestamp text

    let private instantOf (value: Json option) =
        value |> Option.bind Json.tryString |> Option.bind parseInstant |> Option.defaultValue Int64.MaxValue

    let private isTimestamp (value: Json) =
        value |> Json.tryString |> Option.bind parseInstant |> Option.isSome

    /// Formats an instant the way Praxis records it (millisecond UTC).
    let timestamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

    let private describe (value: Json option) =
        match value with
        | None -> "undefined"
        | Some(Json.String text) -> text
        | Some other -> Json.serialize other

    let private actorProblems (prefix: string) (value: Json) =
        match value with
        | Json.Object _ ->
            let kindValue = Json.tryField "kind" value
            let kind = kindValue |> Option.bind Json.tryString

            [ if kind |> Option.forall (kindPattern.IsMatch >> not) then
                  $"{prefix}.kind '{describe kindValue}' is not agent, human, automation, unknown, or x-..."
              if Json.tryField "id" value |> Option.bind Json.tryString |> Option.forall (isNonEmpty >> not) then
                  $"{prefix}.id must not be empty; use 'unknown' when it is not known"
              for field in [ "provider"; "model"; "runtime" ] do
                  match Json.tryField field value with
                  | Some item when item |> Json.tryString |> Option.forall (isNonEmpty >> not) -> $"{prefix}.{field} must be a non-empty string"
                  | None when kind = Some "agent" -> $"{prefix}.{field} is required for an agent ('unknown' when not known)"
                  | _ -> () ]
        | _ -> [ $"{prefix} must be an object" ]

    let private stringListProblems (field: string) (value: Json option) =
        match value with
        | None -> []
        | Some(Json.Array items) when items |> List.forall (fun item -> item |> Json.tryString |> Option.exists isNonEmpty) -> []
        | Some _ -> [ $"{field} must be an array of non-empty strings" ]

    let private strings (value: Json option) =
        match value with
        | Some(Json.Array items) -> items |> List.choose Json.tryString
        | _ -> []

    let private operationCodes (entry: Json) = strings (Json.tryField "operations" entry)

    let private isAgent (actor: Json option) =
        match actor with
        | Some(Json.Object _ as item) -> Json.tryField "kind" item = Some(Json.String "agent")
        | _ -> false

    let private contributionProblems (key: string) (entry: Json) =
        let prefix = $"contributions.{key}"

        match entry with
        | Json.Object _ ->
            let keyKind = ContributionKey.tryParse key
            let actor = Json.tryField "actor" entry

            [ if keyKind.IsNone then
                  $"{prefix}: key must be EXE-..., EXT-<system>.<run-id>, or CTB-..."
              match Json.tryField "operations" entry with
              | Some(Json.Array []) -> $"{prefix}.operations must record at least one operation"
              | Some(Json.Array items) ->
                  for item in items do
                      match item with
                      | Json.String text when operationGrammar.IsMatch text -> ()
                      | other -> $"{prefix}.operations: '{describe (Some other)}' is not a valid operation code"

                  if (items |> List.distinct).Length <> items.Length then
                      $"{prefix}.operations must not repeat an operation"
              | _ -> $"{prefix}.operations must be an array"
              if Json.tryField "at" entry |> Option.forall (isTimestamp >> not) then
                  $"{prefix}.at must be an ISO-8601 UTC timestamp"
              match Json.tryField "last" entry with
              | None -> ()
              | Some last when not (isTimestamp last) -> $"{prefix}.last must be an ISO-8601 UTC timestamp"
              | Some last when instantOf (Some last) < instantOf (Json.tryField "at" entry) -> $"{prefix}.last must not precede at"
              | Some _ -> ()
              match actor with
              | None -> $"{prefix}.actor is required"
              | Some value ->
                  for problem in actorProblems $"{prefix}.actor" value do
                      problem
              if isAgent actor && not (keyKind |> Option.exists ContributionKey.isExecution) then
                  $"{prefix}: an agent contribution must be keyed by the execution (EXE-... or EXT-...) that produced it"
              match Json.tryField "reason" entry with
              | Some(Json.String _)
              | None -> ()
              | Some _ -> $"{prefix}.reason must be a string"
              for problem in stringListProblems $"{prefix}.evidence" (Json.tryField "evidence" entry) do
                  problem ]
        | _ -> [ $"{prefix} must be an object" ]

    let private isCreator (entry: Json) =
        operationCodes entry |> List.contains "created"

    let private creators (contributions: (string * Json) list) =
        contributions |> List.filter (snd >> isCreator)

    let private historyProblems (contributions: (string * Json) list) =
        match creators contributions with
        | [] -> []
        | [ creationKey, creation ] ->
            contributions
            |> List.filter (fun (key, entry) -> key <> creationKey && instantOf (Json.tryField "at" entry) < instantOf (Json.tryField "at" creation))
            |> List.map (fun (key, _) -> $"contributions.{key} precedes the recorded creation ({creationKey})")
        | many -> [ $"""more than one contribution claims 'created': {many |> List.map fst |> String.concat ", "}""" ]

    let private optionalString (name: string) (value: Json) =
        Json.tryField name value |> Option.bind Json.tryString

    /// Typed view of an actor; structural problems are returned, never repaired.
    let parseActor (value: Json) : Result<Actor, string list> =
        match actorProblems "actor" value with
        | [] ->
            Ok
                { Kind = optionalString "kind" value |> Option.bind ActorKind.tryParse |> Option.defaultValue ActorKind.Unknown
                  Id = optionalString "id" value |> Option.defaultValue Actor.UnknownValue
                  Provider = optionalString "provider" value
                  Model = optionalString "model" value
                  Runtime = optionalString "runtime" value }
        | problems -> Error problems

    let private typedContribution (key: ContributionKey) (entry: Json) : Result<Contribution, string list> =
        Json.tryField "actor" entry
        |> Option.map parseActor
        |> Option.defaultValue (Error [ "actor is required" ])
        |> Result.map (fun actor ->
            { Key = key
              Operations = operationCodes entry |> List.choose ContributionOperation.tryParse
              At = optionalString "at" entry |> Option.defaultValue String.Empty
              Last = optionalString "last" entry
              Actor = actor
              Reason = optionalString "reason" entry
              Evidence = strings (Json.tryField "evidence" entry) })

    let private typedContributions (contributions: (string * Json) list) =
        contributions
        |> List.fold
            (fun state (key, entry) ->
                match state, ContributionKey.tryParse key with
                | Ok items, Some parsedKey -> typedContribution parsedKey entry |> Result.map (fun item -> items @ [ item ])
                | Ok _, None -> Error [ $"contributions.{key}: invalid key" ]
                | error, _ -> error)
            (Ok [])

    /// Classifies a received block (R14.2). Credential-like content anywhere
    /// makes it malformed, even under another major version.
    let classify (block: Json) : ProvenanceVerdict =
        match block with
        | Json.Object _ ->
            match credentialFindings block with
            | [] ->
                match Json.tryField "schema" block with
                | Some(Json.String tag) when tag <> SchemaTag ->
                    if schemaPattern.IsMatch tag then
                        ProvenanceVerdict.Unsupported(tag, block)
                    else
                        ProvenanceVerdict.Malformed [ $"schema '{tag}' is not a valid praxis.provenance/<major> tag" ]
                | Some(Json.String _)
                | None ->
                    match Json.tryField "contributions" block with
                    | Some(Json.Object contributions) ->
                        let problems =
                            (contributions |> List.collect (fun (key, entry) -> contributionProblems key entry))
                            @ stringListProblems "derivedFrom" (Json.tryField "derivedFrom" block)

                        if not problems.IsEmpty then
                            ProvenanceVerdict.Malformed problems
                        else
                            match historyProblems contributions, typedContributions contributions with
                            | [], Ok typed ->
                                let warnings =
                                    typed
                                    |> List.collect (fun item ->
                                        item.Operations
                                        |> List.filter ContributionOperation.isUnrecognised
                                        |> List.map (fun operation ->
                                            $"contributions.{ContributionKey.value item.Key}.operations: '{ContributionOperation.code operation}' is not an operation this version knows; preserved verbatim"))

                                ProvenanceVerdict.Supported(block, typed, strings (Json.tryField "derivedFrom" block), warnings)
                            | [], Error typedProblems -> ProvenanceVerdict.Malformed typedProblems
                            | invariant, _ -> ProvenanceVerdict.Malformed invariant
                    | None -> ProvenanceVerdict.Malformed [ "contributions is required" ]
                    | Some _ -> ProvenanceVerdict.Malformed [ "contributions must be an object keyed by EXE-, EXT-, or CTB- keys" ]
                | Some _ -> ProvenanceVerdict.Malformed [ "schema must be a string" ]
            | secrets ->
                ProvenanceVerdict.Malformed(secrets |> List.map (fun path -> $"{path}: credential-like value; provenance must never carry authentication material"))
        | _ -> ProvenanceVerdict.Malformed [ "provenance must be a JSON object" ]

    /// Parses text and classifies it; text that is not JSON is malformed.
    let classifyText (text: string) =
        match Json.parse text with
        | Ok value -> classify value
        | Error message -> ProvenanceVerdict.Malformed [ $"provenance is {message}" ]

    /// A new, empty `praxis.provenance/1` block (no provenance recorded).
    let emptyBlock = Json.Object [ "schema", Json.String SchemaTag; "contributions", Json.Object [] ]

    /// Canonical JSON form of an actor: provider/model/runtime only when present.
    let actorJson (actor: Actor) =
        Json.Object(
            [ "kind", Json.String(ActorKind.code actor.Kind); "id", Json.String actor.Id ]
            @ ([ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ]
               |> List.choose (fun (name, value) -> value |> Option.map (fun text -> name, Json.String text)))
        )

    /// Canonical JSON form of a contribution entry (without its key).
    let contributionJson (contribution: Contribution) =
        Json.Object(
            [ yield "operations", Json.strings (contribution.Operations |> List.map ContributionOperation.code)
              yield "at", Json.String contribution.At
              match contribution.Last with
              | Some last -> yield "last", Json.String last
              | None -> ()
              yield "actor", actorJson contribution.Actor
              match contribution.Reason with
              | Some reason -> yield "reason", Json.String reason
              | None -> ()
              if not contribution.Evidence.IsEmpty then
                  yield "evidence", Json.strings contribution.Evidence ]
        )

    let private isKnownValue (value: string option) =
        value |> Option.exists (fun text -> not (String.IsNullOrWhiteSpace text) && text.Trim() <> Actor.UnknownValue)

    /// Same-key merge (contract revision 1.1): operations and evidence are
    /// unioned in order; the incoming entry's unknown fields are kept but the
    /// existing entry wins on conflict; `last` becomes the later of the
    /// existing (last or at) and incoming (last or at) times; a missing
    /// reason may be filled in.
    let private merge (existing: Json) (incoming: Json) =
        let existingOperations = operationCodes existing
        let operations = existingOperations @ (operationCodes incoming |> List.filter (fun op -> not (List.contains op existingOperations)))
        let existingEvidence = strings (Json.tryField "evidence" existing)
        let evidence = existingEvidence @ (strings (Json.tryField "evidence" incoming) |> List.filter (fun item -> not (List.contains item existingEvidence)))
        let latestOf entry = Json.tryField "last" entry |> Option.orElse (Json.tryField "at" entry)
        let existingLatest = latestOf existing
        let incomingLatest = latestOf incoming
        let latest = if instantOf incomingLatest > instantOf existingLatest then incomingLatest else existingLatest

        let incomingExtras =
            Json.members incoming |> List.filter (fun (name, _) -> (Json.tryField name existing).IsNone && name <> "last" && name <> "evidence" && name <> "reason")

        Json.Object(Json.members existing @ incomingExtras)
        |> Json.setField "operations" (Json.strings operations)
        |> (fun value -> if evidence.IsEmpty then value else Json.setField "evidence" (Json.strings evidence) value)
        |> (fun value ->
            match latest with
            | Some time when instantOf (Some time) > instantOf (Json.tryField "at" existing) -> Json.setField "last" time value
            | _ -> value)
        |> (fun value ->
            match Json.tryField "reason" existing, Json.tryField "reason" incoming with
            | None, Some reason -> Json.setField "reason" reason value
            | _ -> value)

    /// Appends one contribution entry (JSON form) under `key` without
    /// disturbing any other (RQ-ROS-2026-A004, contract revision 1.1): the
    /// same key merges operations and evidence and advances `last` only when
    /// the actor agrees and is not less known than the holder; a second or
    /// late `created` is refused; credential-like content is refused; unknown
    /// fields everywhere are preserved. Whatever it returns classifies as
    /// supported. Returns the new block and whether anything changed. Refuses
    /// to append to an unsupported or malformed block.
    let appendJson (block: Json) (key: string) (contribution: Json) : Result<Json * bool, string> =
        let finish (next: Json) (changed: bool) =
            match classify next with
            | ProvenanceVerdict.Supported _ -> Ok(next, changed)
            | ProvenanceVerdict.Malformed problems -> Error $"""the resulting history would be malformed: {String.concat "; " problems}"""
            | ProvenanceVerdict.Unsupported(schema, _) -> Error $"the resulting history would be unsupported ({schema})"

        match classify block with
        | ProvenanceVerdict.Unsupported(schema, _) -> Error $"refusing to append to an unsupported provenance block ({schema}); it is carried verbatim"
        | ProvenanceVerdict.Malformed problems -> Error $"""refusing to append to a malformed provenance block: {String.concat "; " problems}"""
        | ProvenanceVerdict.Supported _ ->
            match credentialFindings (Json.Object [ key, contribution ]), contributionProblems key contribution with
            | (_ :: _ as secrets), _ ->
                Error $"""{String.concat ", " secrets}: credential-like value; provenance must never carry authentication material"""
            | [], (_ :: _ as problems) -> Error(String.concat "; " problems)
            | [], [] ->
                let contributions = Json.tryField "contributions" block |> Option.map Json.members |> Option.defaultValue []
                let creates = isCreator contribution
                let hasOriginator = not (creators contributions).IsEmpty
                let withContributions items = Json.setField "contributions" (Json.Object items) block

                match contributions |> List.tryFind (fun (name, _) -> name = key) with
                | None ->
                    if creates && hasOriginator then
                        Error "the record already has an originator; record 'modified' instead of 'created'"
                    elif creates && (contributions |> List.exists (fun (_, entry) -> instantOf (Json.tryField "at" entry) < instantOf (Json.tryField "at" contribution))) then
                        Error "a 'created' contribution cannot follow existing contributions"
                    else
                        finish (withContributions (contributions @ [ key, contribution ])) true
                | Some(_, existing) ->
                    let actorOf entry = Json.tryField "actor" entry |> Option.map parseActor

                    match actorOf existing, actorOf contribution with
                    | Some(Ok existingActor), Some(Ok incomingActor) when not (Actor.agrees existingActor incomingActor) ->
                        Error $"contribution '{key}' is already attributed to {ActorKind.code existingActor.Kind}:{existingActor.Id}; refusing to re-attribute it"
                    | Some(Ok existingActor), Some(Ok incomingActor) when
                        (isKnownValue (Some existingActor.Id) && not (isKnownValue (Some incomingActor.Id)))
                        || (existingActor.Kind <> ActorKind.Unknown && incomingActor.Kind = ActorKind.Unknown)
                        ->
                        Error $"contribution '{key}' belongs to {ActorKind.code existingActor.Kind}:{existingActor.Id}; an actor with unknown identity cannot extend it"
                    | _ when
                        creates
                        && not (isCreator existing)
                        && (hasOriginator
                            || contributions
                               |> List.exists (fun (name, entry) -> name <> key && instantOf (Json.tryField "at" entry) < instantOf (Json.tryField "at" existing)))
                        ->
                        Error "the record's originator is already recorded or precedes this contribution; record 'modified' instead of 'created'"
                    | _ ->
                        let merged = merge existing contribution
                        let changed = Json.serialize merged <> Json.serialize existing
                        let items = contributions |> List.map (fun (name, entry) -> if name = key then name, merged else name, entry)
                        finish (withContributions items) changed

    /// Appends a typed contribution (see `appendJson`).
    let append (block: Json) (contribution: Contribution) =
        appendJson block (ContributionKey.value contribution.Key) (contributionJson contribution)

    /// Adds lineage references (never authorship), preserving existing order.
    let addLineage (references: string list) (block: Json) : Json =
        let current = strings (Json.tryField "derivedFrom" block)
        let additions = references |> List.distinct |> List.filter (fun reference -> not (List.contains reference current))

        if additions.IsEmpty then block else Json.setField "derivedFrom" (Json.strings (current @ additions)) block

    /// Checks that `after` preserved everything `before` held: no contribution
    /// removed, no actor or time rewritten, no operation, evidence, field, or
    /// lineage lost, no originator replaced. Empty when `after` is a faithful
    /// extension; an unsupported block must be unchanged.
    let preservationViolations (before: Json) (after: Json) : string list =
        match classify before with
        | ProvenanceVerdict.Unsupported _ ->
            if Json.serialize before = Json.serialize after then []
            else [ "unsupported provenance was altered instead of carried verbatim" ]
        | _ ->
            match Json.tryField "contributions" after with
            | Some(Json.Object afterContributions) ->
                let beforeContributions = Json.tryField "contributions" before |> Option.map Json.members |> Option.defaultValue []
                let field name entry = Json.tryField name entry |> Option.map Json.serialize

                [ for key, entry in beforeContributions do
                      match afterContributions |> List.tryFind (fun (name, _) -> name = key) with
                      | None -> $"contribution {key} was removed"
                      | Some(_, later) ->
                          if field "actor" later <> field "actor" entry then
                              $"contribution {key} actor was overwritten"

                          if field "at" later <> field "at" entry then
                              $"contribution {key} time was rewritten"

                          for op in operationCodes entry do
                              if not (List.contains op (operationCodes later)) then
                                  $"contribution {key} lost operation {op}"

                          for item in strings (Json.tryField "evidence" entry) do
                              if not (List.contains item (strings (Json.tryField "evidence" later))) then
                                  $"contribution {key} lost evidence {item}"

                          for name, _ in Json.members entry do
                              if (Json.tryField name later).IsNone then
                                  $"contribution {key} lost field {name}"
                  for name, _ in Json.members before do
                      if (Json.tryField name after).IsNone then
                          $"block lost field {name}"
                  for reference in strings (Json.tryField "derivedFrom" before) do
                      if not (List.contains reference (strings (Json.tryField "derivedFrom" after))) then
                          $"lineage {reference} was removed"
                  let creatorsBefore = creators beforeContributions |> List.map fst

                  if creatorsBefore.Length = 1 && creatorsBefore <> (creators afterContributions |> List.map fst) then
                      "the originator was replaced" ]
            | _ -> [ "provenance was removed" ]

    /// Contributions of a supported block, typed; empty otherwise.
    let contributions (verdict: ProvenanceVerdict) =
        match verdict with
        | ProvenanceVerdict.Supported(_, items, _, _) -> items
        | _ -> []

    /// The originating (`created`) contribution, or `None` when the origin is
    /// not recorded. Nothing is ever inferred from other contributions.
    let originator (verdict: ProvenanceVerdict) =
        match contributions verdict |> List.filter (Contribution.has ContributionOperation.Created) with
        | [ creator ] -> Some creator
        | _ -> None

    /// Contributions that played a role (for example `validated`), in time order.
    let withOperation (operation: ContributionOperation) (verdict: ProvenanceVerdict) =
        contributions verdict
        |> List.filter (Contribution.has operation)
        |> List.sortBy (fun item -> parseInstant item.At |> Option.defaultValue Int64.MaxValue)

    /// Lineage references of a supported block.
    let lineage (verdict: ProvenanceVerdict) =
        match verdict with
        | ProvenanceVerdict.Supported(_, _, derivedFrom, _) -> derivedFrom
        | _ -> []
