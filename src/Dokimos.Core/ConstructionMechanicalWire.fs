namespace Dokimos.Core

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// One typed obligation (`ordo.construction-obligations/1`) as Dokimos reads
/// it from a prepared artifact. Only the fields the CONSTRUCT-008 contract
/// names are read; every other field is ignored.
type PreparedObligation =
    { Id: string
      Kind: string
      RequirementKeys: string list
      NodeIds: string list }

/// One generated path the constructor will emit, optionally with the SHA-256
/// of its bytes.
type LayoutFile = { Path: string; Sha256: string option }

/// The parts of `praxis.construction-prepared/1` that Dokimos checks.
type PreparedArtifact =
    { /// SHA-256 of the prepared artifact's bytes, as supplied.
      Digest: string
      CacheKey: string
      Plan: JsonElement
      DeclaredPlanDigest: string
      Obligations: PreparedObligation list
      GeneratedLayout: LayoutFile list
      GeneratorId: string
      GeneratorVersion: string
      VocabularyVersion: string
      /// Declared input digests by input: blueprint, sourceManifest, decisionRecords.
      InputDigests: Map<string, string>
      PermittedExtensionFiles: string list
      /// Obligation ids in the agent handoff, when the artifact carries one.
      HandoffObligationIds: string list option }

/// The constructing agent's disposition of one obligation.
type ObligationDisposition =
    { ObligationId: string
      Status: string
      Note: string
      Files: string list }

/// A host refusal the agent says it received.
type ReportedRejection = { Path: string; Reason: string }

/// The agent's obligation dispositions (`dispositions.json`).
type DispositionRecord =
    { SchemaVersion: string
      Dispositions: ObligationDisposition list
      RejectedOperations: ReportedRejection list }

/// A commit's tree, read from Git objects.
type TreeAt = { Commit: string; Entries: Map<string, TreeEntry> }

/// Optional mechanical-first (arm C) inputs. Each is independently supplied;
/// none is derived from the candidate.
type MechanicalInputs =
    { PreparedBytes: byte[]
      DispositionsJson: string option
      DecisionRecordsBytes: byte[] option
      PreparedReplicaBytes: byte[] option
      /// The host's apply commit that carried only generated output.
      GeneratedCommit: TreeAt option
      /// A second candidate built from identical inputs.
      Replica: TreeAt option }

/// What Dokimos already established about the candidate, passed to the
/// mechanical-first checks so that nothing is recomputed twice.
type MechanicalContext =
    { SourceManifest: JsonElement
      SourceManifestText: string
      Blueprint: JsonElement
      BlueprintText: string
      Plan: PlanModel
      ExpectedPlanDigest: string
      ManifestPath: string
      Manifest: ConstructionManifest
      Ownership: OwnershipResult
      Candidate: CandidateSnapshot
      Base: BaseSnapshot option
      Host: HostEvidence option
      /// Findings of the existing construction checks.
      Existing: ConstructionFinding list }

/// One mechanical-first check: `passed`, `failed` or `unknown`.
type MechanicalCheck =
    { Name: string
      Status: string
      Detail: string
      Findings: string list }

type ObligationTrace =
    { Id: string
      Kind: string
      RequirementKeys: string list
      Disposition: string }

type MechanicalReport =
    { PreparedDigest: string
      CacheKey: string
      GeneratorId: string
      GeneratorVersion: string
      VocabularyVersion: string
      DecisionRecordsDigest: string
      GeneratedCommit: string
      ReplicaCommit: string
      GeneratedPaths: string list
      PermittedExtensionFiles: string list
      HandwrittenPaths: string list
      Obligations: ObligationTrace list
      Checks: MechanicalCheck list }

[<RequireQualifiedAccess>]
module MechanicalCodes =
    let PreparedArtifactInvalid = "prepared-artifact-invalid"
    let ProvenanceDigestMismatch = "provenance-digest-mismatch"
    let NondeterministicGeneration = "nondeterministic-generation"
    let ObligationDropped = "obligation-dropped"
    let ObligationDispositionInvalid = "obligation-disposition-invalid"
    let UnauthorizedFileMutation = "unauthorized-file-mutation"
    let GeneratedFileEdited = "generated-file-edited"
    let ExtensionBoundaryViolation = "extension-boundary-violation"
    let GeneratedLayoutMismatch = "generated-layout-mismatch"
    let RejectedOperationUnrecorded = "rejected-operation-unrecorded"
    let RejectedOperationApplied = "rejected-operation-applied"

/// Readers for the mechanical-first wire inputs. Required fields are
/// validated strictly and every violation is reported, not only the first.
[<RequireQualifiedAccess>]
module MechanicalWire =
    [<Literal>]
    let PreparedSchema = "praxis.construction-prepared/1"

    [<Literal>]
    let ObligationsSchema = "ordo.construction-obligations/1"

    [<Literal>]
    let DecisionsSchema = "ordo.construction-decisions/1"

    [<Literal>]
    let DefaultPlanGenerator = "ordo.default-plan"

    let obligationKinds =
        set [ "extensionBehavior"; "undecidedEffectFailure"; "conflict"; "uncoveredRequirement"; "unsupportedVocabulary" ]

    let dispositionStatuses = set [ "implemented"; "escalated" ]

    let private apply (f: Result<'a -> 'b, string list>) (x: Result<'a, string list>) =
        match f, x with
        | Ok f, Ok x -> Ok(f x)
        | Error a, Error b -> Error(a @ b)
        | Error a, Ok _ -> Error a
        | Ok _, Error b -> Error b

    let private (<*>) = apply

    let private isDigest (value: string) = Regex.IsMatch(value, "^sha256:[0-9a-f]{64}$")

    let private kindIs kind (e: JsonElement) = e.ValueKind = kind

    let private member' (path: string) (names: string list) (e: JsonElement) =
        names |> List.tryPick (fun n -> ConstructionJson.get n e |> Option.map (fun v -> path + n, v))

    let private requiredText path names e =
        match member' path names e with
        | Some(_, v) when v.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(v.GetString())) -> Ok(v.GetString() |> string)
        | Some(p, _) -> Error [ p + " must be a non-empty string" ]
        | None -> Error [ path + List.head names + " is required" ]

    let private requiredDigest path names e =
        requiredText path names e
        |> Result.bind (fun v -> if isDigest v then Ok v else Error [ $"{path}{List.head names} must be sha256:<64 lowercase hex>, not '{v}'" ])

    let private requiredObject path names e =
        match member' path names e with
        | Some(_, v) when kindIs JsonValueKind.Object v -> Ok v
        | Some(p, _) -> Error [ p + " must be an object" ]
        | None -> Error [ path + List.head names + " is required" ]

    let private stringList path name (e: JsonElement) =
        match ConstructionJson.get name e with
        | None -> Ok []
        | Some v when kindIs JsonValueKind.Array v && v.EnumerateArray() |> Seq.forall (kindIs JsonValueKind.String) ->
            Ok(v.EnumerateArray() |> Seq.map (fun s -> s.GetString() |> string) |> Seq.toList)
        | Some _ -> Error [ path + name + " must be an array of strings" ]

    /// An array of strings, or of objects carrying `path`.
    let private paths path (v: JsonElement) =
        v.EnumerateArray()
        |> Seq.mapi (fun i item ->
            let at = $"{path}[{i}]"
            match item.ValueKind with
            | JsonValueKind.String when not (String.IsNullOrWhiteSpace(item.GetString())) ->
                Ok { Path = item.GetString() |> string; Sha256 = None }
            | JsonValueKind.Object ->
                Ok(fun p (d: string option) -> { Path = p; Sha256 = d })
                <*> requiredText (at + ".") [ "path" ] item
                <*> (match ConstructionJson.get "sha256" item with
                     | None -> Ok None
                     | Some d when d.ValueKind = JsonValueKind.String && isDigest (d.GetString() |> string) -> Ok(Some(d.GetString() |> string))
                     | Some _ -> Error [ at + ".sha256 must be sha256:<64 lowercase hex>" ])
            | _ -> Error [ at + " must be a non-empty path string or an object with path" ])
        |> Seq.fold (fun acc r -> Ok(fun xs x -> xs @ [ x ]) <*> acc <*> r) (Ok [])

    let private pathArray path names e =
        match member' path names e with
        | Some(p, v) when kindIs JsonValueKind.Array v -> paths p v
        | Some(p, v) when kindIs JsonValueKind.Object v ->
            match ConstructionJson.get "files" v with
            | Some files when kindIs JsonValueKind.Array files -> paths (p + ".files") files
            | _ -> Error [ p + " must be an array (or an object with a files array)" ]
        | Some(p, _) -> Error [ p + " must be an array" ]
        | None -> Error [ path + List.head names + " is required" ]

    let private obligation at (o: JsonElement) =
        if not (kindIs JsonValueKind.Object o) then Error [ at + " must be an object" ]
        else
            Ok(fun id kind keys nodes -> ({ Id = id; Kind = kind; RequirementKeys = keys; NodeIds = nodes }: PreparedObligation))
            <*> requiredText (at + ".") [ "id" ] o
            <*> (requiredText (at + ".") [ "kind" ] o
                 |> Result.bind (fun k ->
                     if Set.contains k obligationKinds then Ok k
                     else
                         let allowed = String.concat ", " obligationKinds
                         Error [ $"{at}.kind '{k}' is not one of {allowed}" ]))
            <*> (match ConstructionJson.get "requirementKeys" o with
                 | None -> Error [ at + ".requirementKeys is required" ]
                 | Some _ -> stringList (at + ".") "requirementKeys" o)
            <*> stringList (at + ".") "nodeIds" o

    let private obligations path (defaultPlan: JsonElement) =
        let list p (v: JsonElement) =
            v.EnumerateArray()
            |> Seq.mapi (fun i o -> obligation $"{p}[{i}]" o)
            |> Seq.fold (fun acc r -> Ok(fun xs x -> xs @ [ x ]) <*> acc <*> r) (Ok [])
        match ConstructionJson.get "obligations" defaultPlan with
        | Some v when kindIs JsonValueKind.Array v -> list (path + "obligations") v
        | Some v when kindIs JsonValueKind.Object v ->
            let schema = ConstructionJson.text "schemaVersion" v
            match ConstructionJson.get "obligations" v with
            | _ when schema <> "" && schema <> ObligationsSchema ->
                Error [ $"{path}obligations.schemaVersion is '{schema}', not {ObligationsSchema}" ]
            | Some items when kindIs JsonValueKind.Array items -> list (path + "obligations.obligations") items
            | _ -> Error [ path + "obligations.obligations must be an array" ]
        | Some _ -> Error [ path + "obligations must be an array or an " + ObligationsSchema + " object" ]
        | None -> Error [ path + "obligations is required" ]
        |> Result.bind (fun (xs: PreparedObligation list) ->
            match xs |> List.countBy _.Id |> List.filter (fun (_, n) -> n > 1) with
            | [] -> Ok xs
            | dups -> Error [ for id, _ in dups -> $"obligation id {id} is not unique" ])

    let private handoff (root: JsonElement) =
        match ConstructionJson.get "handoff" root with
        | None -> Ok None
        | Some h when kindIs JsonValueKind.Object h ->
            match ConstructionJson.get "obligationIds" h, ConstructionJson.get "obligations" h with
            | Some _, _ -> stringList "handoff." "obligationIds" h |> Result.map Some
            | None, Some items when kindIs JsonValueKind.Array items ->
                items.EnumerateArray()
                |> Seq.mapi (fun i o ->
                    if kindIs JsonValueKind.String o then Ok(o.GetString() |> string)
                    else requiredText $"handoff.obligations[{i}]." [ "id"; "obligationId" ] o)
                |> Seq.fold (fun acc r -> Ok(fun xs x -> xs @ [ x ]) <*> acc <*> r) (Ok [])
                |> Result.map Some
            | None, _ -> Ok None
        | Some _ -> Error [ "handoff must be an object" ]

    let private planOf (defaultPlan: JsonElement) =
        match member' "defaultPlan." [ "plan"; "draft" ] defaultPlan with
        | Some(_, v) when kindIs JsonValueKind.Object v ->
            let schema = ConstructionJson.text "schemaVersion" v
            if schema = "ordo.construction-plan/1" then Ok(v.Clone())
            else Error [ $"defaultPlan.plan.schemaVersion is '{schema}', not ordo.construction-plan/1" ]
        | Some(p, _) -> Error [ p + " must be an ordo.construction-plan/1 object" ]
        | None -> Error [ "defaultPlan.plan is required" ]

    let private inputDigests (provenance: JsonElement) =
        match requiredObject "defaultPlan.provenance." [ "inputDigests"; "inputs" ] provenance with
        | Error e -> Error e
        | Ok inputs ->
            let digest key = requiredDigest "defaultPlan.provenance.inputDigests." [ key; key + "Digest" ] inputs
            Ok(fun b s d -> Map.ofList [ "blueprint", b; "sourceManifest", s; "decisionRecords", d ])
            <*> digest "blueprint" <*> digest "sourceManifest" <*> digest "decisionRecords"

    /// Reads `praxis.construction-prepared/1`. Unknown fields are ignored;
    /// every missing or mistyped required field is listed.
    let readPrepared (bytes: byte[]) : Result<PreparedArtifact, string list> =
        match ConstructionJson.parse "prepared artifact" (Encoding.UTF8.GetString bytes) with
        | Error e -> Error [ e ]
        | Ok root when not (kindIs JsonValueKind.Object root) -> Error [ "prepared artifact must be a JSON object" ]
        | Ok root ->
            let schema =
                match ConstructionJson.text "schemaVersion" root with
                | PreparedSchema -> Ok()
                | other -> Error [ $"schemaVersion is '{other}', not {PreparedSchema}" ]
            match requiredObject "" [ "defaultPlan" ] root with
            | Error e -> Error((match schema with Error s -> s | Ok() -> []) @ e)
            | Ok dp ->
                let provenance = requiredObject "defaultPlan." [ "provenance" ] dp
                let fromProvenance f = provenance |> Result.bind f
                Ok(fun () cacheKey plan planDigest obligations layout generatorId generatorVersion vocabulary inputs permitted handoff ->
                    { Digest = ConstructionJson.sha256Bytes bytes
                      CacheKey = cacheKey
                      Plan = plan
                      DeclaredPlanDigest = planDigest
                      Obligations = obligations
                      GeneratedLayout = layout
                      GeneratorId = generatorId
                      GeneratorVersion = generatorVersion
                      VocabularyVersion = vocabulary
                      InputDigests = inputs
                      PermittedExtensionFiles = permitted |> List.map (fun (l: LayoutFile) -> l.Path)
                      HandoffObligationIds = handoff })
                <*> schema
                <*> requiredText "" [ "cacheKey" ] root
                <*> planOf dp
                <*> requiredDigest "defaultPlan." [ "planDigest" ] dp
                <*> obligations "defaultPlan." dp
                <*> pathArray "defaultPlan." [ "generatedLayout" ] dp
                <*> fromProvenance (requiredText "defaultPlan.provenance." [ "generatorId"; "generator" ])
                <*> fromProvenance (requiredText "defaultPlan.provenance." [ "generatorVersion" ])
                <*> fromProvenance (requiredText "defaultPlan.provenance." [ "vocabularyVersion" ])
                <*> fromProvenance inputDigests
                <*> pathArray "" [ "permittedExtensionFiles" ] root
                <*> handoff root

    /// Only the embedded plan of a prepared artifact, so that the plan can be
    /// verified even when other prepared fields are invalid.
    let preparedPlan (bytes: byte[]) : Result<JsonElement, string list> =
        match ConstructionJson.parse "prepared artifact" (Encoding.UTF8.GetString bytes) with
        | Error e -> Error [ e ]
        | Ok root -> requiredObject "" [ "defaultPlan" ] root |> Result.bind planOf

    let private disposition at (id: string option) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.String ->
            match id with
            | Some i -> Ok { ObligationId = i; Status = e.GetString() |> string; Note = ""; Files = [] }
            | None -> Error [ at + " must be an object" ]
        | JsonValueKind.Object ->
            Ok(fun i status files -> { ObligationId = i; Status = status; Note = ConstructionJson.text "note" e; Files = files })
            <*> (match id with Some i -> Ok i | None -> requiredText (at + ".") [ "obligationId"; "id" ] e)
            <*> requiredText (at + ".") [ "disposition"; "status" ] e
            <*> stringList (at + ".") "files" e
        | _ -> Error [ at + " must be an object" ]

    /// Reads the agent's dispositions: `dispositions` as an array of
    /// `{obligationId, disposition, note, files}` or as an object keyed by
    /// obligation id. Status values are judged by the checks, not here.
    let readDispositions (json: string) : Result<DispositionRecord, string list> =
        match ConstructionJson.parse "dispositions" json with
        | Error e -> Error [ e ]
        | Ok root ->
            let entries =
                match ConstructionJson.get "dispositions" root with
                | Some v when kindIs JsonValueKind.Array v ->
                    v.EnumerateArray() |> Seq.mapi (fun i d -> disposition $"dispositions[{i}]" None d) |> Seq.toList
                | Some v when kindIs JsonValueKind.Object v ->
                    v.EnumerateObject() |> Seq.map (fun p -> disposition ("dispositions." + p.Name) (Some p.Name) p.Value) |> Seq.toList
                | Some _ -> [ Error [ "dispositions must be an array or an object keyed by obligation id" ] ]
                | None -> [ Error [ "dispositions is required" ] ]
            let rejections =
                ConstructionJson.array "rejectedOperations" root
                |> List.mapi (fun i r ->
                    requiredText $"rejectedOperations[{i}]." [ "path" ] r
                    |> Result.map (fun p -> { Path = p; Reason = ConstructionJson.text "reason" r }))
            Ok(fun ds rs -> { SchemaVersion = ConstructionJson.text "schemaVersion" root; Dispositions = ds; RejectedOperations = rs })
            <*> (entries |> List.fold (fun acc r -> Ok(fun xs x -> xs @ [ x ]) <*> acc <*> r) (Ok []))
            <*> (rejections |> List.fold (fun acc r -> Ok(fun xs x -> xs @ [ x ]) <*> acc <*> r) (Ok []))
