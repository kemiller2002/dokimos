namespace Dokimos.Core

open System
open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// An immutable JSON value. Object members keep their order and numbers keep
/// their exact source text, so a document read and written again is
/// unchanged. Used at evidence boundaries where unknown fields must survive
/// (R10, R14.2).
[<RequireQualifiedAccess>]
type Json =
    | Null
    | Bool of bool
    | Number of raw: string
    | String of string
    | Array of Json list
    | Object of (string * Json) list

[<RequireQualifiedAccess>]
module Json =
    let rec private ofElement (element: JsonElement) : Json =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.map (fun property -> property.Name, ofElement property.Value)
            |> Seq.toList
            |> Json.Object
        | JsonValueKind.Array -> element.EnumerateArray() |> Seq.map ofElement |> Seq.toList |> Json.Array
        | JsonValueKind.String ->
            match element.GetString() with
            | null -> Json.Null
            | text -> Json.String text
        | JsonValueKind.Number -> Json.Number(element.GetRawText())
        | JsonValueKind.True -> Json.Bool true
        | JsonValueKind.False -> Json.Bool false
        | _ -> Json.Null

    let parse (text: string) : Result<Json, string> =
        try
            use document = JsonDocument.Parse text
            Ok(ofElement document.RootElement)
        with :? JsonException as error ->
            Error $"not valid JSON: {error.Message}"

    let rec private write (writer: Utf8JsonWriter) (value: Json) =
        match value with
        | Json.Null -> writer.WriteNullValue()
        | Json.Bool flag -> writer.WriteBooleanValue flag
        | Json.Number raw -> writer.WriteRawValue(raw, true)
        | Json.String text -> writer.WriteStringValue text
        | Json.Array items ->
            writer.WriteStartArray()
            items |> List.iter (write writer)
            writer.WriteEndArray()
        | Json.Object members ->
            writer.WriteStartObject()

            members
            |> List.iter (fun (name, item) ->
                writer.WritePropertyName name
                write writer item)

            writer.WriteEndObject()

    let private render (indented: bool) (value: Json) =
        use stream = new MemoryStream()
        let options = JsonWriterOptions(Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

        do
            use writer = new Utf8JsonWriter(stream, options)
            write writer value
            writer.Flush()

        Encoding.UTF8.GetString(stream.ToArray())

    /// Compact canonical text; equal values in equal member order render equally.
    let serialize value = render false value

    let serializeIndented value = render true value

    /// A member of an object (the last one when a name repeats, as in
    /// JavaScript); `None` for missing members and non-objects.
    let tryField (name: string) (value: Json) =
        match value with
        | Json.Object members -> members |> List.tryFindBack (fun (key, _) -> key = name) |> Option.map snd
        | _ -> None

    /// Sets a member, keeping its position when it exists and appending it otherwise.
    let setField (name: string) (item: Json) (value: Json) =
        match value with
        | Json.Object members when members |> List.exists (fun (key, _) -> key = name) ->
            members |> List.map (fun (key, existing) -> if key = name then key, item else key, existing) |> Json.Object
        | Json.Object members -> Json.Object(members @ [ name, item ])
        | other -> other

    let tryString value =
        match value with
        | Json.String text -> Some text
        | _ -> None

    let strings (values: string list) = values |> List.map Json.String |> Json.Array

    let isObject value =
        match value with
        | Json.Object _ -> true
        | _ -> false

    let members value =
        match value with
        | Json.Object members -> members
        | _ -> []
