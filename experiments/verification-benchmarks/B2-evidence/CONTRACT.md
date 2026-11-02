# Benchmark 2 interface contract (frozen before implementation)

Both agents build against this contract and nothing else. Test agents do not read `src/`.

## F# (project `src/Evidence/Evidence.fsproj`, net10.0)

```fsharp
namespace Evidence

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

module Evidence =
    val create: id: string -> source: string -> collectedBy: string -> content: byte[] -> Result<EvidenceRecord, EvidenceError>

/// The HTTP provider client: GET {baseUrl}/evidence/{id}, header Authorization: Bearer {token}.
type ProviderClient =
    new: http: System.Net.Http.HttpClient * baseUrl: string * token: string -> ProviderClient
    member Fetch: id: string -> System.Threading.Tasks.Task<Result<byte[], EvidenceError>>

/// Fetches from the provider and attributes the result (source = baseUrl + "/evidence/" + id).
type Collector =
    new: client: ProviderClient * baseUrl: string * collectedBy: string -> Collector
    member Collect: id: string -> System.Threading.Tasks.Task<Result<EvidenceRecord, EvidenceError>>
    /// Diagnostic lines written so far (the collector logs each fetch and each failure).
    member Diagnostics: string list

/// One directory per store: <root>/<id>.json (metadata, including Sha256) and <root>/<id>.bin (content).
type EvidenceStore =
    new: root: string -> EvidenceStore
    member Save: EvidenceRecord -> Result<unit, EvidenceError>
    /// Recomputes the SHA-256 of the stored content; a mismatch is IntegrityFailure.
    member Load: id: string -> Result<EvidenceRecord, EvidenceError>
```

Tests stand up their own provider with a real HTTP listener on 127.0.0.1 and a free port (for example `System.Net.HttpListener`, or an ASP.NET Core minimal app). Each route answers the status code the test needs. The provider records the requests it receives, so EVD-002-O3 can inspect them.
