namespace Dokimos.Core

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography

/// Read-only JSON accessors for construction inputs. A missing or mistyped
/// member reads as empty; callers decide whether empty is a finding.
[<RequireQualifiedAccess>]
module ConstructionJson =
    let get (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &property) then Some property else None

    let text (name: string) element =
        match get name element with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let array (name: string) element =
        match get name element with
        | Some value when value.ValueKind = JsonValueKind.Array -> value.EnumerateArray() |> Seq.toList
        | _ -> []

    let strings (name: string) element =
        array name element
        |> List.choose (fun item -> if item.ValueKind = JsonValueKind.String then item.GetString() |> Option.ofObj else None)

    let members (name: string) element =
        match get name element with
        | Some value when value.ValueKind = JsonValueKind.Object ->
            value.EnumerateObject() |> Seq.map (fun p -> p.Name, p.Value) |> Seq.toList
        | _ -> []

    let sha256Bytes (bytes: byte[]) =
        "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()

    let sha256Text (value: string) = Encoding.UTF8.GetBytes value |> sha256Bytes

    /// Accepts `sha256:<hex>` or bare hex and returns the prefixed lowercase form.
    let normalizeDigest (value: string) =
        let trimmed = value.Trim().ToLowerInvariant()
        if trimmed.StartsWith("sha256:", StringComparison.Ordinal) then trimmed else "sha256:" + trimmed

    /// Parses JSON text into a detached root element, or a reason.
    let parse (label: string) (json: string) : Result<JsonElement, string> =
        try
            use document = JsonDocument.Parse json
            Ok(document.RootElement.Clone())
        with :? JsonException as ex -> Error(label + ": invalid JSON: " + ex.Message)

/// An independent re-implementation of the canonical forms Ordo publishes for
/// `ordo.construction-plan/1` and `ecir/1` (docs/architecture/construction-plan-v1.md,
/// "Validated plan, identity and authority"). Dokimos recomputes the plan and
/// blueprint digests from the bytes it was given instead of trusting a digest
/// that travels with them. No Ordo code is referenced.
[<RequireQualifiedAccess>]
module ConstructionCanonical =
    type private Node =
        | Str of string
        | Arr of Node list
        | Obj of (string * Node) list
        | Raw of string

    let private escape (sb: StringBuilder) (value: string) =
        sb.Append '"' |> ignore
        for ch in value do
            match ch with
            | '"' -> sb.Append "\\\"" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\b' -> sb.Append "\\b" |> ignore
            | '\f' -> sb.Append "\\f" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\t' -> sb.Append "\\t" |> ignore
            | c when c < ' ' -> sb.AppendFormat("\\u{0:x4}", int c) |> ignore
            | c -> sb.Append c |> ignore
        sb.Append '"' |> ignore

    /// Two-space indentation, ordinal-sorted object keys, LF separators.
    let private render (node: Node) =
        let sb = StringBuilder()
        let pad (n: int) = String(' ', n * 2)
        let rec go node indent =
            match node with
            | Str s -> escape sb s
            | Raw r -> sb.Append r |> ignore
            | Arr [] -> sb.Append "[]" |> ignore
            | Obj [] -> sb.Append "{}" |> ignore
            | Arr items ->
                sb.Append "[\n" |> ignore
                items |> List.iteri (fun i item ->
                    if i > 0 then sb.Append ",\n" |> ignore
                    sb.Append(pad (indent + 1)) |> ignore
                    go item (indent + 1))
                sb.Append('\n').Append(pad indent).Append ']' |> ignore
            | Obj fields ->
                sb.Append "{\n" |> ignore
                fields
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))
                |> List.iteri (fun i (name, item) ->
                    if i > 0 then sb.Append ",\n" |> ignore
                    sb.Append(pad (indent + 1)) |> ignore
                    escape sb name
                    sb.Append ": " |> ignore
                    go item (indent + 1))
                sb.Append('\n').Append(pad indent).Append '}' |> ignore
        go node 0
        sb.ToString()

    let rec private ofElement (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.String -> Str(element.GetString() |> Option.ofObj |> Option.defaultValue "")
        | JsonValueKind.Array -> Arr [ for item in element.EnumerateArray() -> ofElement item ]
        | JsonValueKind.Object -> Obj [ for p in element.EnumerateObject() -> p.Name, ofElement p.Value ]
        | _ -> Raw(element.GetRawText())

    let private texts values = Arr(values |> List.map Str)
    let private sortedSet values = values |> List.distinct |> List.sort |> texts

    /// A semantic name's identity: its ASCII words in PascalCase.
    let nameKey (raw: string) =
        raw.Split([| ' '; '-'; '_' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun word -> string (Char.ToUpperInvariant word[0]) + word.Substring 1)
        |> String.concat ""

    let private s name element = ConstructionJson.text name element

    let private trace (op: JsonElement) =
        let t = ConstructionJson.get "trace" op |> Option.defaultValue op
        Obj [ "requirementKeys", sortedSet (ConstructionJson.strings "requirementKeys" t)
              "nodeIds", sortedSet (ConstructionJson.strings "nodeIds" t) ]

    let private canonicalOperations (operations: JsonElement list) =
        let ofKind kind = operations |> List.filter (fun o -> s "op" o = kind)
        let key name o = nameKey (s name o)
        [ for o in ofKind "defineWorkflow" -> Obj [ "op", Str "defineWorkflow"; "name", Str(key "name" o); "trace", trace o ]
          for o in ofKind "defineState" |> List.sortBy (fun o -> (if s "role" o = "initial" then 0 else 1), key "name" o) ->
              Obj [ "op", Str "defineState"; "name", Str(key "name" o); "role", Str(s "role" o); "trace", trace o ]
          for o in ofKind "defineTransition" |> List.sortBy (fun o -> key "from" o, key "command" o) ->
              let guards =
                  ConstructionJson.array "guards" o
                  |> List.sortBy (key "name")
                  |> List.map (fun g -> Obj [ "name", Str(key "name" g); "predicate", Str(s "predicate" g) ])
              Obj [ "op", Str "defineTransition"; "command", Str(key "command" o); "from", Str(key "from" o)
                    "to", Str(key "to" o); "guards", Arr guards; "trace", trace o ]
          for o in ofKind "defineInvariant" |> List.sortBy (key "name") ->
              let holdsIn = ConstructionJson.strings "holdsIn" o |> List.map nameKey |> List.distinct |> List.sort
              Obj [ "op", Str "defineInvariant"; "name", Str(key "name" o); "predicate", Str(s "predicate" o)
                    "holdsIn", texts holdsIn; "trace", trace o ]
          for o in ofKind "declarePort" |> List.sortBy (fun o -> if s "port" o = "currentTime" then "" else key "command" o) ->
              let kind =
                  if s "port" o = "currentTime" then [ ("port", Str "currentTime") ]
                  else [ ("port", Str "transitionEffect"); ("command", Str(key "command" o)) ]
              Obj([ ("op", Str "declarePort") ] @ kind @ [ ("trace", trace o) ])
          for o in ofKind "bindCapability" |> List.sortBy (fun o -> key "command" o, s "capability" o) ->
              Obj [ "op", Str "bindCapability"; "command", Str(key "command" o); "capability", Str(s "capability" o); "trace", trace o ]
          for o in ofKind "declareUnresolved" |> List.sortBy (key "subject") ->
              Obj [ "op", Str "declareUnresolved"; "subject", Str(key "subject" o); "question", Str(s "question" o); "trace", trace o ] ]

    /// SHA-256 of the canonical plan: normalized names, initial state first,
    /// transitions by (from, command), other lists sorted, keys sorted.
    let planDigest (plan: JsonElement) =
        let binding = ConstructionJson.get "binding" plan |> Option.defaultValue plan
        Obj [ "schemaVersion", Str(s "schemaVersion" plan)
              "vocabularyVersion", Str(s "vocabularyVersion" plan)
              "binding",
              Obj [ "sourceManifestDigest", Str(s "sourceManifestDigest" binding)
                    "blueprintDigest", Str(s "blueprintDigest" binding)
                    "cohortId", Str(s "cohortId" binding)
                    "decisionIds", sortedSet (ConstructionJson.strings "decisionIds" binding) ]
              "operations", Arr(canonicalOperations (ConstructionJson.array "operations" plan)) ]
        |> render
        |> ConstructionJson.sha256Text

    /// SHA-256 of the canonical `ecir/1` blueprint: requirements by source key,
    /// nodes by id, identity reference lists sorted, keys sorted.
    let blueprintDigest (blueprint: JsonElement) =
        let requirements =
            ConstructionJson.array "requirements" blueprint
            |> List.sortBy (fun r -> ConstructionJson.get "source" r |> Option.map (s "key") |> Option.defaultValue "")
            |> List.map (fun r ->
                Obj [ "source", (ConstructionJson.get "source" r |> Option.map ofElement |> Option.defaultValue (Obj []))
                      "disposition", (ConstructionJson.get "disposition" r |> Option.map ofElement |> Option.defaultValue (Obj []))
                      "nodeIds", texts (ConstructionJson.strings "nodeIds" r |> List.sort) ])
        let nodes =
            ConstructionJson.array "nodes" blueprint
            |> List.sortBy (s "id")
            |> List.map (fun n ->
                Obj [ for p in n.EnumerateObject() ->
                        match p.Name with
                        | "requirementKeys" | "dependsOn" -> p.Name, texts (ConstructionJson.strings p.Name n |> List.sort)
                        | _ -> p.Name, ofElement p.Value ])
        Obj [ "schemaVersion", Str(s "schemaVersion" blueprint)
              "sourceManifestDigest", Str(s "sourceManifestDigest" blueprint)
              "requirements", Arr requirements
              "nodes", Arr nodes ]
        |> render
        |> ConstructionJson.sha256Text

    /// Order-independent rendering of any JSON value, for comparing two
    /// documents by meaning.
    let canonicalText (element: JsonElement) = render (ofElement element)
