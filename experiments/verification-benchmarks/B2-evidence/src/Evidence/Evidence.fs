namespace Evidence

open System
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks

type EvidenceRecord =
    { Id: string; Source: string; CollectedBy: string; Content: byte[]; Sha256: string }

type EvidenceError =
    | MissingAttribution of field: string   // "Source" or "CollectedBy"
    | ProviderNotFound of id: string
    | ProviderUnauthorized
    | ProviderRateLimited
    | ProviderFailure of status: int        // any other non-2xx, or a network failure (status 0)
    | IntegrityFailure of id: string
    | NotStored of id: string

/// Pure helpers shared by the public types.
module internal Internal =

    let sha256Hex (content: byte[]) : string =
        SHA256.HashData(content) |> Convert.ToHexString |> fun s -> s.ToLowerInvariant()

    let isBlank (s: string) = String.IsNullOrWhiteSpace s

    /// Maps a non-success HTTP status to the contract's error.
    let classifyStatus (id: string) (status: int) : EvidenceError =
        match status with
        | 404 -> ProviderNotFound id
        | 401 | 403 -> ProviderUnauthorized
        | 429 -> ProviderRateLimited
        | other -> ProviderFailure other

    /// Removes every occurrence of the secret from a text (defensive redaction).
    let redact (secret: string) (text: string) : string =
        if String.IsNullOrEmpty secret || isNull (box text) then text
        else text.Replace(secret, "***", StringComparison.Ordinal)

    let describe (error: EvidenceError) : string =
        match error with
        | MissingAttribution field -> sprintf "missing attribution: %s" field
        | ProviderNotFound id -> sprintf "provider: not found (%s)" id
        | ProviderUnauthorized -> "provider: unauthorized"
        | ProviderRateLimited -> "provider: rate limited"
        | ProviderFailure status -> sprintf "provider: failure (status %d)" status
        | IntegrityFailure id -> sprintf "integrity failure (%s)" id
        | NotStored id -> sprintf "not stored (%s)" id

module Evidence =

    let create (id: string) (source: string) (collectedBy: string) (content: byte[]) : Result<EvidenceRecord, EvidenceError> =
        if Internal.isBlank source then Error(MissingAttribution "Source")
        elif Internal.isBlank collectedBy then Error(MissingAttribution "CollectedBy")
        else
            let content = if isNull (box content) then [||] else content
            Ok { Id = id
                 Source = source
                 CollectedBy = collectedBy
                 Content = content
                 Sha256 = Internal.sha256Hex content }

/// The HTTP provider client: GET {baseUrl}/evidence/{id}, header Authorization: Bearer {token}.
type ProviderClient(http: HttpClient, baseUrl: string, token: string) =

    let requestUri (id: string) =
        Uri(baseUrl.TrimEnd('/') + "/evidence/" + Uri.EscapeDataString id)

    /// Used by the collector for defensive redaction only; never exposed publicly.
    member internal _.Secret = token

    member _.Fetch(id: string) : Task<Result<byte[], EvidenceError>> =
        task {
            try
                use request = new HttpRequestMessage(HttpMethod.Get, requestUri id)
                request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
                use! response = http.SendAsync(request, HttpCompletionOption.ResponseContentRead)
                let status = int response.StatusCode
                if response.StatusCode = HttpStatusCode.OK then
                    let! body = response.Content.ReadAsByteArrayAsync()
                    return Ok body
                else
                    return Error(Internal.classifyStatus id status)
            with _ ->
                // Network failure, timeout or malformed URL: never thrown, never carries the token.
                return Error(ProviderFailure 0)
        }

/// Fetches from the provider and attributes the result (source = baseUrl + "/evidence/" + id).
type Collector(client: ProviderClient, baseUrl: string, collectedBy: string) =

    // The only mutable state: an immutable list of lines, newest first.
    let mutable lines: string list = []

    let log (line: string) =
        lines <- Internal.redact client.Secret line :: lines

    member _.Collect(id: string) : Task<Result<EvidenceRecord, EvidenceError>> =
        task {
            let source = baseUrl + "/evidence/" + id
            log (sprintf "fetch %s" source)
            let! fetched = client.Fetch id
            let result = fetched |> Result.bind (Evidence.create id source collectedBy)
            match result with
            | Ok record -> log (sprintf "fetched %s (%d bytes, sha256 %s)" id record.Content.Length record.Sha256)
            | Error error -> log (sprintf "failed %s: %s" id (Internal.describe error))
            return result
        }

    /// Diagnostic lines written so far (the collector logs each fetch and each failure).
    member _.Diagnostics: string list = List.rev lines

/// The metadata persisted in <id>.json (everything but the content).
type internal StoredMetadata =
    { Id: string; Source: string; CollectedBy: string; Sha256: string }

module internal Metadata =

    let serialize (m: StoredMetadata) : string =
        JsonSerializer.Serialize(
            dict [ "Id", m.Id; "Source", m.Source; "CollectedBy", m.CollectedBy; "Sha256", m.Sha256 ])

    /// Parses stored metadata; None when the JSON is malformed or incomplete.
    let tryParse (json: string) : StoredMetadata option =
        try
            use doc = JsonDocument.Parse json
            let field (name: string) =
                match doc.RootElement.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
                | _ -> None
            match field "Id", field "Source", field "CollectedBy", field "Sha256" with
            | Some id, Some source, Some collectedBy, Some sha ->
                Some { Id = id; Source = source; CollectedBy = collectedBy; Sha256 = sha }
            | _ -> None
        with
        | :? JsonException
        | :? InvalidOperationException -> None

/// One directory per store: <root>/<id>.json (metadata, including Sha256) and <root>/<id>.bin (content).
type EvidenceStore(root: string) =

    let jsonPath id = Path.Combine(root, id + ".json")
    let binPath id = Path.Combine(root, id + ".bin")

    let toMetadata (r: EvidenceRecord) : StoredMetadata =
        { Id = r.Id; Source = r.Source; CollectedBy = r.CollectedBy; Sha256 = r.Sha256 }

    /// Pure: verifies stored metadata against stored content.
    let verify (id: string) (meta: StoredMetadata) (content: byte[]) : Result<EvidenceRecord, EvidenceError> =
        if not (String.Equals(Internal.sha256Hex content, meta.Sha256, StringComparison.Ordinal)) then
            Error(IntegrityFailure id)
        else
            Ok { Id = meta.Id; Source = meta.Source; CollectedBy = meta.CollectedBy; Content = content; Sha256 = meta.Sha256 }

    member _.Save(record: EvidenceRecord) : Result<unit, EvidenceError> =
        Directory.CreateDirectory root |> ignore
        File.WriteAllBytes(binPath record.Id, record.Content)
        File.WriteAllText(jsonPath record.Id, Metadata.serialize (toMetadata record))
        Ok()

    /// Recomputes the SHA-256 of the stored content; a mismatch is IntegrityFailure.
    member _.Load(id: string) : Result<EvidenceRecord, EvidenceError> =
        let json, bin = jsonPath id, binPath id
        if not (File.Exists json && File.Exists bin) then Error(NotStored id)
        else
            match Metadata.tryParse (File.ReadAllText json) with
            | None -> Error(IntegrityFailure id)
            | Some meta -> verify id meta (File.ReadAllBytes bin)
