namespace Dokimos.Core.Verification

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Manifest-driven requirement verification (`dokimos verification requirements`).
///
/// The obligations come from a Praxis `praxis.verification-manifest/1`, which is
/// compiled from requirements alone, so the application cannot shrink them. The
/// evidence comes from three independent places:
///
///  * the application source, for V1 `mapped` (the mapped file must carry the ID);
///  * browser observations (`dokimos.requirement-observations/1`): facts a probe
///    recorded about the rendered element (tag, visibility, disabled, accessible
///    name, values read back), never a verdict;
///  * the executed test results (Playwright JSON), proving the probe's test ran
///    and passed.
///
/// Dokimos alone turns facts into a verdict, against the rules in the manifest.
/// A probe's test that did not execute contributes nothing (NOT_RUN), whatever the
/// probe says; missing or stale evidence is BLOCKED, never PASS; NOT_APPLICABLE
/// is only the result of a declared manifest fact having another value.
type RequirementStatus =
    | Pass
    | Fail
    | Blocked
    | NotRun
    | NotApplicable

type ElementFact =
    { Tag: string
      InputType: string
      Role: string
      AccessibleName: string
      Visible: bool
      Disabled: bool
      ReadOnly: bool
      Instance: string option
      Options: string list }

/// One value a probe tried: what it entered, what the element (or app) then held,
/// and whether the application accepted it.
type Attempt =
    { Value: string
      Observed: string
      Accepted: bool }

type Probe =
    { Obligation: string
      /// The Dokimos identity of the test that recorded this probe
      /// (`playwright:<file> > <title> [<project>]`).
      Test: string
      Elements: ElementFact list option
      Attempts: Attempt list
      Expected: string option
      Observed: string option }

type Observations =
    { RunId: string
      SourceCommit: string
      ManifestHash: string
      Facts: Map<string, bool>
      Probes: Probe list }

type ManifestObligation =
    { Id: string
      Level: string
      Check: string
      Mandatory: bool }

type ManifestRequirement =
    { Id: string
      Kind: string
      Screen: string
      Name: string
      Component: string option
      Required: bool
      MinLength: int option
      MaxLength: int option
      Pattern: string option
      Options: string list
      /// None: always applicable. Some facts: applicable when every fact has the value.
      AppliesWhen: (string * bool) list option
      Repeated: bool
      DependsOn: string list
      Obligations: ManifestObligation list
      Mappings: string list }

type RequirementManifest =
    { CatalogId: string
      SourceCommit: string
      ContentHash: string
      /// Whether the manifest's content still hashes to its contentHash.
      Intact: bool
      Requirements: ManifestRequirement list }

type Evidence =
    { Observations: Result<Observations, string>
      /// Test outcomes by identity, or why they are unavailable.
      Results: Result<Map<string, Outcome>, string>
      /// The text of an application source file named by a mapping, if it exists.
      Source: string -> string option
      /// The commit under verification; observations from another commit are stale.
      Candidate: string option
      /// Facts supplied by the caller (environment), overriding none recorded by the run.
      Facts: Map<string, bool> }

type ObligationResult =
    { Obligation: ManifestObligation
      Status: RequirementStatus
      Reason: string
      Tests: string list }

type RequirementResult =
    { Requirement: ManifestRequirement
      Status: RequirementStatus
      /// For a failure: structural (V1-V2), interaction (V3), behavioural (V4) or lifecycle (V5).
      FailureClass: string option
      Implemented: bool
      Obligations: ObligationResult list }

type VerificationReport =
    { Manifest: RequirementManifest
      Candidate: string option
      RunId: string option
      Screens: string list
      /// Requirement IDs the run was limited to (empty: every requirement of the in-scope screens).
      /// A scoped run is acceptance of that scope only, never of the catalog.
      RequirementScope: string list
      Requirements: RequirementResult list
      /// Reasons the evidence as a whole is unusable (stale manifest, other commit).
      EvidenceProblems: string list
      Accepted: bool
      Blockers: string list }


/// Constants and decoding of the requirement verifier's inputs: the Praxis manifest and the
/// browser observations. Pure: text in, typed value or a reason out.
[<RequireQualifiedAccess>]
module RequirementEvidence =
    [<Literal>]
    let ManifestSchema = "praxis.verification-manifest/1"

    [<Literal>]
    let ObservationsSchema = "dokimos.requirement-observations/1"

    [<Literal>]
    let ReportSchema = "dokimos.requirement-verification/1"

    [<Literal>]
    let Attribute = "data-requirement-id"

    let statusText status =
        match status with
        | Pass -> "PASS"
        | Fail -> "FAIL"
        | Blocked -> "BLOCKED"
        | NotRun -> "NOT_RUN"
        | NotApplicable -> "NOT_APPLICABLE"

    // ------------------------------------------------------------------ decoding

    let private prop (name: string) (e: JsonElement) =
        if e.ValueKind = JsonValueKind.Object then
            match e.TryGetProperty name with
            | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
            | _ -> None
        else None

    let private textOf (e: JsonElement) =
        match e.GetString() with
        | null -> ""
        | s -> s

    let private str name e =
        prop name e |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String) |> Option.map textOf

    let private strOr name fallback e = str name e |> Option.defaultValue fallback

    let private boolOr name fallback e =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.True -> true
        | Some v when v.ValueKind = JsonValueKind.False -> false
        | _ -> fallback

    let private intOpt name e =
        prop name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Number)
        |> Option.bind (fun v -> match v.TryGetInt32() with | true, n -> Some n | _ -> None)

    let private arr name e =
        prop name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Array)
        |> Option.map (fun v -> v.EnumerateArray() |> List.ofSeq)
        |> Option.defaultValue []

    let private strings name e =
        arr name e |> List.filter (fun v -> v.ValueKind = JsonValueKind.String) |> List.map textOf

    let private parseDocument (text: string) (schema: string) (decode: JsonElement -> Result<'T, string>) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement

            match str "schema" root with
            | Some s when s = schema -> decode root
            | Some s -> Error $"document schema is '{s}', expected '{schema}'"
            | None -> Error $"document has no schema; expected '{schema}'"
        with :? JsonException as ex ->
            Error $"malformed JSON: {ex.Message}"

    let private sha256 (text: string) =
        text
        |> Text.Encoding.UTF8.GetBytes
        |> Security.Cryptography.SHA256.HashData
        |> Convert.ToHexString
        |> fun h -> h.ToLowerInvariant()

    /// Recompute the Praxis manifest content hash: SHA-256 of the compact JSON with
    /// `contentHash` removed (praxis docs/requirement-verification-manifest.md).
    let private recomputeHash (text: string) =
        try
            match JsonNode.Parse text with
            | :? JsonObject as node ->
                node.Remove "contentHash" |> ignore
                let options = JsonSerializerOptions(Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
                Some(sha256 (node.ToJsonString options))
            | _ -> None
        with :? JsonException ->
            None

    let parseManifest (text: string) : Result<RequirementManifest, string> =
        parseDocument text ManifestSchema (fun root ->
            let requirement (e: JsonElement) : ManifestRequirement =
                let validation = prop "validation" e
                let applicability = prop "applicability" e

                { Id = strOr "id" "" e
                  Kind = strOr "type" "" e
                  Screen = strOr "screen" "" e
                  Name = strOr "name" "" e
                  Component = str "component" e
                  Required = boolOr "required" false e
                  MinLength = validation |> Option.bind (intOpt "minLength")
                  MaxLength = validation |> Option.bind (intOpt "maxLength")
                  Pattern = validation |> Option.bind (str "pattern")
                  Options = validation |> Option.map (strings "options") |> Option.defaultValue []
                  AppliesWhen =
                    match applicability |> Option.bind (str "rule") with
                    | Some "when" ->
                        applicability
                        |> Option.map (arr "facts" >> List.map (fun f -> strOr "fact" "" f, boolOr "equals" true f))
                    | _ -> None
                  Repeated = (prop "instancing" e |> Option.bind (str "scope")) = Some "repeated"
                  DependsOn = strings "dependsOn" e
                  Obligations =
                    arr "obligations" e
                    |> List.map (fun o ->
                        { Id = strOr "id" "" o
                          Level = strOr "level" "" o
                          Check = strOr "check" "" o
                          Mandatory = boolOr "mandatory" false o })
                  Mappings = strings "mappings" e }

            match str "contentHash" root, prop "source" root |> Option.bind (str "commit") with
            | Some hash, Some commit ->
                Ok
                    { CatalogId = strOr "catalogId" "" root
                      SourceCommit = commit
                      ContentHash = hash
                      Intact = recomputeHash text = Some hash
                      Requirements = arr "requirements" root |> List.map requirement }
            | _ -> Error "manifest has no contentHash or source.commit")

    let parseObservations (text: string) : Result<Observations, string> =
        parseDocument text ObservationsSchema (fun root ->
            let element (e: JsonElement) =
                { Tag = (strOr "tag" "" e).ToLowerInvariant()
                  InputType = (strOr "type" "" e).ToLowerInvariant()
                  Role = strOr "role" "" e
                  AccessibleName = strOr "accessibleName" "" e
                  Visible = boolOr "visible" false e
                  Disabled = boolOr "disabled" true e
                  ReadOnly = boolOr "readOnly" true e
                  Instance = str "instance" e
                  Options = strings "options" e }

            let probe (e: JsonElement) =
                { Obligation = strOr "obligation" "" e
                  Test = strOr "test" "" e
                  Elements = prop "elements" e |> Option.map (fun _ -> arr "elements" e |> List.map element)
                  Attempts =
                    arr "attempts" e
                    |> List.map (fun a ->
                        { Value = strOr "value" "" a
                          Observed = strOr "observed" "" a
                          Accepted = boolOr "accepted" false a })
                  Expected = str "expected" e
                  Observed = str "observed" e }

            let facts =
                match prop "facts" root with
                | Some f when f.ValueKind = JsonValueKind.Object ->
                    f.EnumerateObject()
                    |> Seq.choose (fun p ->
                        match p.Value.ValueKind with
                        | JsonValueKind.True -> Some(p.Name, true)
                        | JsonValueKind.False -> Some(p.Name, false)
                        | _ -> None)
                    |> Map.ofSeq
                | _ -> Map.empty

            Ok
                { RunId = strOr "runId" "" root
                  SourceCommit = strOr "sourceCommit" "" root
                  ManifestHash = strOr "manifestHash" "" root
                  Facts = facts
                  Probes = arr "probes" root |> List.map probe })

    /// Merge the per-test observation files of one run. Files from different runs, manifests or
    /// commits are never mixed: that is an error, not a merge.
    let mergeObservations (files: Observations list) : Result<Observations, string> =
        match files with
        | [] -> Error "no observation files were recorded"
        | first :: _ ->
            let differing (label: string) (key: Observations -> string) =
                match files |> List.map key |> List.distinct with
                | [ _ ] -> None
                | values -> Some $"""observation files disagree on {label}: {String.Join(", ", values)}"""

            match [ differing "runId" _.RunId; differing "manifestHash" _.ManifestHash; differing "sourceCommit" _.SourceCommit ] |> List.choose id with
            | problem :: _ -> Error problem
            | [] ->
                let facts = files |> List.collect (fun f -> Map.toList f.Facts)

                match facts |> List.groupBy fst |> List.tryFind (fun (_, values) -> (values |> List.map snd |> List.distinct).Length > 1) with
                | Some(fact, _) -> Error $"observation files disagree on fact '{fact}'"
                | None ->
                    Ok
                        { first with
                            Facts = Map.ofList facts
                            Probes = files |> List.collect _.Probes }

