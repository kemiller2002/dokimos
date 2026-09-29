namespace Dokimos.Core

open System
open System.Globalization
open System.Text.Json

/// Explicit, total JSON decoding. Contracts are decoded field by field so a
/// missing or mistyped field is a named error, never a null inside a record.
module Json =
    type Decoder<'a> = JsonElement -> Result<'a, string>

    type ResultBuilder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return value = Ok value
        member _.ReturnFrom(value: Result<_, _>) = value

    let result = ResultBuilder()

    let succeed value : Decoder<'a> = fun _ -> Ok value

    let map (f: 'a -> 'b) (decoder: Decoder<'a>) : Decoder<'b> = decoder >> Result.map f

    let bind (f: 'a -> Decoder<'b>) (decoder: Decoder<'a>) : Decoder<'b> =
        fun element -> decoder element |> Result.bind (fun value -> f value element)

    let private kind (expected: JsonValueKind) name (read: JsonElement -> 'a) : Decoder<'a> =
        fun element ->
            if element.ValueKind = expected then Ok(read element)
            else Error $"expected {name}, found {element.ValueKind}"

    let string: Decoder<string> = kind JsonValueKind.String "string" (fun e -> e.GetString() |> Option.ofObj |> Option.defaultValue "")

    let nonEmptyString: Decoder<string> =
        string >> Result.bind (fun s -> if String.IsNullOrWhiteSpace s then Error "expected a non-empty string" else Ok s)

    let decimal: Decoder<decimal> =
        fun element ->
            match element.ValueKind with
            | JsonValueKind.Number ->
                match element.TryGetDecimal() with
                | true, value -> Ok value
                | _ -> Error "number out of decimal range"
            | other -> Error $"expected number, found {other}"

    let int: Decoder<int> =
        fun element ->
            match element.ValueKind with
            | JsonValueKind.Number ->
                match element.TryGetInt32() with
                | true, value -> Ok value
                | _ -> Error "expected an integer"
            | other -> Error $"expected integer, found {other}"

    let int64: Decoder<int64> =
        fun element ->
            match element.ValueKind with
            | JsonValueKind.Number ->
                match element.TryGetInt64() with
                | true, value -> Ok value
                | _ -> Error "expected an integer"
            | other -> Error $"expected integer, found {other}"

    let dateTimeOffset: Decoder<DateTimeOffset> =
        string
        >> Result.bind (fun text ->
            match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, value -> Ok value
            | _ -> Error $"expected an ISO-8601 timestamp, found '{text}'")

    let list (decoder: Decoder<'a>) : Decoder<'a list> =
        fun element ->
            if element.ValueKind <> JsonValueKind.Array then
                Error $"expected array, found {element.ValueKind}"
            else
                element.EnumerateArray()
                |> Seq.mapi (fun index item -> decoder item |> Result.mapError (fun e -> $"[{index}]: {e}"))
                |> Seq.fold
                    (fun state item ->
                        match state, item with
                        | Ok values, Ok value -> Ok(value :: values)
                        | Error e, _ -> Error e
                        | _, Error e -> Error e)
                    (Ok [])
                |> Result.map List.rev

    let field name (decoder: Decoder<'a>) : Decoder<'a> =
        fun element ->
            if element.ValueKind <> JsonValueKind.Object then
                Error $"expected object with '{name}', found {element.ValueKind}"
            else
                match element.TryGetProperty(name: string) with
                | true, value -> decoder value |> Result.mapError (fun e -> $"{name}: {e}")
                | _ -> Error $"missing required field '{name}'"

    let optionalField name (decoder: Decoder<'a>) : Decoder<'a option> =
        fun element ->
            if element.ValueKind <> JsonValueKind.Object then
                Error $"expected object with '{name}', found {element.ValueKind}"
            else
                match element.TryGetProperty(name: string) with
                | true, value when value.ValueKind <> JsonValueKind.Null ->
                    decoder value |> Result.map Some |> Result.mapError (fun e -> $"{name}: {e}")
                | _ -> Ok None

    let fieldOr name fallback decoder = optionalField name decoder |> map (Option.defaultValue fallback)

    let oneOf (cases: (string * 'a) list) : Decoder<'a> =
        string
        >> Result.bind (fun text ->
            match cases |> List.tryFind (fst >> (=) text) with
            | Some (_, value) -> Ok value
            | None ->
                let allowed = cases |> List.map fst |> String.concat ", "
                Error $"unknown value '{text}' (expected one of: {allowed})")

    type ParseError =
        | Malformed of string
        | NullDocument
        | Invalid of string

    let parse (decoder: Decoder<'a>) (text: string) =
        try
            use document = JsonDocument.Parse(text)
            if document.RootElement.ValueKind = JsonValueKind.Null then Error NullDocument
            else decoder document.RootElement |> Result.mapError Invalid
        with :? JsonException as e ->
            Error(Malformed e.Message)
