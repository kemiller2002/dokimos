namespace Dokimos.Core

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography

/// Independently audits the original Conditor intake against an ECIR
/// proposal. Dokimos never accepts an agent's summary or test claims as
/// proof that behavioral requirements have passed.
type EcirFinding =
    { Code: string
      RequirementKey: string
      Detail: string }

type EcirAuditReport =
    { Imported: int
      Represented: int
      Modeled: int
      Deferred: int
      Unresolved: int
      Rejected: int
      Superseded: int
      IndependentlyVerified: int
      Findings: EcirFinding list }

[<RequireQualifiedAccess>]
module EcirAudit =
    let private get (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &property) then Some property else None

    let private text (name: string) element =
        match get name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            value.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let private array (name: string) element =
        match get name element with
        | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
        | _ -> []

    let private digest (message: string) =
        Encoding.UTF8.GetBytes message
        |> SHA256.HashData
        |> Convert.ToHexString
        |> fun hash -> "sha256:" + hash.ToLowerInvariant()

    let private frameManifest (rows: JsonElement list) =
        let builder = StringBuilder("ecir-source-manifest/1\n")
        let frame (value: string) =
            builder.Append(Encoding.UTF8.GetByteCount value).Append(':').Append(value) |> ignore
        rows
        |> List.sortBy (text "key")
        |> List.iter (fun entry ->
            for name in [ "key"; "originalId"; "document"; "location"; "revision"; "contentDigest" ] do
                frame (text name entry))
        digest (builder.ToString())

    let private sourceEquals (a: JsonElement) (b: JsonElement) =
        [ "key"; "originalId"; "document"; "location"; "revision"; "contentDigest" ]
        |> List.forall (fun name -> text name a = text name b)

    let private duplicateKeys xs =
        xs |> List.countBy id |> List.choose (fun (key, count) -> if count > 1 then Some key else None)

    /// Evidence here means a construction obligation, NOT proof that any test
    /// passed. Independent runtime observations require an additional
    /// verifier phase; IndependentlyVerified is necessarily zero in this
    /// report until that phase is implemented.
    let audit (manifestJson: string) (blueprintJson: string) : Result<EcirAuditReport, string list> =
        try
            use manifestDoc = JsonDocument.Parse manifestJson
            use blueprintDoc = JsonDocument.Parse blueprintJson
            let source = manifestDoc.RootElement
            let blueprint = blueprintDoc.RootElement
            let originals = array "requirements" source
            let modeled = array "requirements" blueprint
            let nodes = array "nodes" blueprint
            let findings = ResizeArray<EcirFinding>()
            let add code key detail = findings.Add { Code = code; RequirementKey = key; Detail = detail }

            if text "schemaVersion" blueprint <> "ecir/1" then
                add "unsupported-schema" "" "The construction artifact must use ecir/1"
            if List.isEmpty originals then
                add "empty-intake" "" "The independently supplied manifest contains no requirements"

            let sourceDigest = text "digest" source
            if sourceDigest <> frameManifest originals then
                add "source-manifest-digest-mismatch" "" "The manifest digest does not match the imported source identities and content"
            if sourceDigest <> text "sourceManifestDigest" blueprint then
                add "blueprint-stale" "" "The blueprint references a different source manifest"

            let originalKeys = originals |> List.map (text "key")
            let modeledKeys = modeled |> List.map (fun row -> get "source" row |> Option.map (text "key") |> Option.defaultValue "")
            for key in duplicateKeys originalKeys do add "duplicate-original" key "Source intake contains the same qualified key twice"
            for key in duplicateKeys modeledKeys do add "duplicate-blueprint" key "Blueprint repeats a qualified source requirement"

            let index = originals |> List.map (fun item -> text "key" item, item) |> Map.ofList
            let blueprintIndex =
                modeled
                |> List.choose (fun item -> get "source" item |> Option.map (fun src -> text "key" src, (src, item)))
                |> Map.ofList
            let nodeIndex = nodes |> List.map (fun node -> text "id" node, node) |> Map.ofList

            for original in originals do
                let key = text "key" original
                if String.IsNullOrWhiteSpace key then add "missing-key" key "Qualified original identity is blank"
                for field in [ "originalId"; "document"; "location"; "revision"; "contentDigest" ] do
                    if String.IsNullOrWhiteSpace(text field original) then add "missing-source-field" key field
                match Map.tryFind key blueprintIndex with
                | None -> add "missing-requirement" key "Blueprint dropped an imported requirement"
                | Some(src, _) when not (sourceEquals original src) ->
                    add "changed-source-requirement" key "The source identity, location, revision or content digest changed"
                | _ -> ()

            for key in modeledKeys do
                if not (Map.containsKey key index) then
                    add "invented-requirement" key "Blueprint names a source that was not in the independent intake"

            for id in nodes |> List.map (text "id") |> duplicateKeys do
                add "duplicate-node" id "Node identity occurs more than once"

            for row in modeled do
                let src = get "source" row
                let key = src |> Option.map (text "key") |> Option.defaultValue ""
                let disposition = get "disposition" row
                let kind = disposition |> Option.map (text "kind") |> Option.defaultValue ""
                let references = array "nodeIds" row |> List.map (fun node -> node.GetString() |> Option.ofObj |> Option.defaultValue "")
                if List.isEmpty references then add "unmapped-requirement" key "No construction nodes trace to this requirement"
                for nodeId in references do
                    match Map.tryFind nodeId nodeIndex with
                    | None -> add "dangling-node" key ("Missing node " + nodeId)
                    | Some node ->
                        let incoming = array "requirementKeys" node |> List.map (fun item -> item.GetString() |> Option.ofObj |> Option.defaultValue "")
                        if not (List.contains key incoming) then
                            add "nonreciprocal-link" key ("Node " + nodeId + " omits the source reference")
                let hasNodeOfKind expected =
                    references |> List.exists (fun id ->
                        Map.tryFind id nodeIndex
                        |> Option.exists (fun node ->
                            text "kind" node = expected
                            && (array "requirementKeys" node
                                |> List.exists (fun value -> value.ValueKind = JsonValueKind.String && value.GetString() = key))))
                match kind with
                | "modeled" ->
                    if not (hasNodeOfKind "verificationObligation") then
                        add "no-verification-obligation" key "The requirement has no declared independent verification obligation"
                    if not (hasNodeOfKind "cohort") then
                        add "no-construction-cohort" key "Modeled requirement is absent from every build cohort"
                | "deferred" | "unresolved" | "rejected" | "superseded" ->
                    if disposition |> Option.map (text "reason") |> Option.defaultValue "" |> String.IsNullOrWhiteSpace then
                        add "unjustified-disposition" key "Non-modeled requirement must preserve a reason"
                | _ -> add "unknown-disposition" key ("Unknown disposition: " + kind)

            for node in nodes do
                let nodeId = text "id" node
                let keys = array "requirementKeys" node |> List.map (fun item -> item.GetString() |> Option.ofObj |> Option.defaultValue "")
                if List.isEmpty keys && text "kind" node <> "engineeringRationale" then
                    add "unjustified-node" nodeId "No originating source requirement"
                for key in keys do
                    match Map.tryFind key blueprintIndex with
                    | None -> add "dangling-requirement" key ("Node " + nodeId + " names a missing source")
                    | Some (_, row) ->
                        if not (array "nodeIds" row |> List.exists (fun item -> item.GetString() = nodeId)) then
                            add "nonreciprocal-link" key ("Requirement omits node " + nodeId)

            let status name =
                modeled |> List.filter (fun item ->
                    get "disposition" item |> Option.map (text "kind") = Some name) |> List.length

            let result =
                { Imported = originals.Length
                  Represented = modeled.Length
                  Modeled = status "modeled"
                  Deferred = status "deferred"
                  Unresolved = status "unresolved"
                  Rejected = status "rejected"
                  Superseded = status "superseded"
                  IndependentlyVerified = 0
                  Findings = findings |> Seq.distinct |> Seq.sortBy (fun finding -> finding.Code, finding.RequirementKey) |> Seq.toList }
            Ok result
        with
        | :? JsonException as ex -> Error [ "Invalid ECIR JSON: " + ex.Message ]
        | :? InvalidOperationException as ex -> Error [ "Invalid ECIR shape: " + ex.Message ]
        | :? ArgumentException as ex -> Error [ "Invalid ECIR source: " + ex.Message ]
