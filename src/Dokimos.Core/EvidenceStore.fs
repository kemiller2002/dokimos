namespace Dokimos.Core

open System
open System.IO

/// A named, immutable baseline acceptance. A newer acceptance supersedes an
/// older one for the same name; neither is ever rewritten.
type BaselineAcceptance =
    { Name: string
      SnapshotId: string
      AcceptedAt: DateTimeOffset
      Actor: string
      Reason: string }

type PutOutcome =
    | Stored
    | AlreadyStored

/// Expected store outcomes are values. Only unexpected I/O escapes as an
/// exception and is handled at the operational fault boundary.
type StoreError =
    | IdentityConflict of snapshotId: string
    | IdentityMismatch of claimed: string * derived: string
    | SnapshotNotFound of snapshotId: string
    | InvalidStoredEvidence of location: string * reason: string
    | StoreNotInitialized of location: string

/// Storage boundary for longitudinal evidence. Comparison, evaluation and
/// history depend only on this record, so filesystem, Git, object storage or
/// a service can back it without changing domain logic.
type EvidenceStore =
    { Describe: string
      Put: CanonicalSnapshot -> Result<PutOutcome, StoreError>
      TryGet: string -> Result<CanonicalSnapshot option, StoreError>
      List: unit -> Result<CanonicalSnapshot list, StoreError>
      Accept: BaselineAcceptance -> Result<PutOutcome, StoreError>
      Acceptances: string -> Result<BaselineAcceptance list, StoreError> }

module EvidenceStore =
    /// Schema-2.0.0 identities are derived from content; a claimed identity
    /// that does not match its content is rejected. Schema-1.0.0 evidence
    /// predates derived identity and is stored under its recorded id.
    let verifyIdentity (snapshot: CanonicalSnapshot) =
        if snapshot.SchemaVersion = "1.0.0" then Ok snapshot
        else
            let derived = CanonicalSnapshot.identity snapshot.Repository snapshot.Revision (Contracts.evidenceDigest snapshot)
            if derived = snapshot.SnapshotId then Ok snapshot else Error(IdentityMismatch(snapshot.SnapshotId, derived))

    let currentBaseline (store: EvidenceStore) name =
        store.Acceptances name
        |> Result.bind (fun acceptances ->
            match acceptances |> List.sortBy _.AcceptedAt |> List.tryLast with
            | None -> Ok None
            | Some acceptance ->
                store.TryGet acceptance.SnapshotId
                |> Result.bind (function
                    | Some snapshot -> Ok(Some(acceptance, snapshot))
                    | None -> Error(SnapshotNotFound acceptance.SnapshotId)))

    let describeError =
        function
        | IdentityConflict id -> "store-identity-conflict", $"A different snapshot is already stored as {id}; stored evidence is immutable."
        | IdentityMismatch (claimed, derived) -> "store-identity-mismatch", $"Snapshot claims identity {claimed} but its content derives {derived}."
        | SnapshotNotFound id -> "snapshot-not-found", $"No snapshot {id} exists in the store."
        | InvalidStoredEvidence (location, reason) -> "store-invalid-evidence", $"{location}: {reason}"
        | StoreNotInitialized location -> "store-not-initialized", $"{location} is not a Dokimos evidence store; run `dokimos store init --store {location}`."

    let exitCode =
        function
        | IdentityConflict _
        | IdentityMismatch _ -> ExitCodes.StoreConflict
        | SnapshotNotFound _
        | InvalidStoredEvidence _
        | StoreNotInitialized _ -> ExitCodes.EvidenceUnavailable

/// A directory-backed store. With the directory checked out from a dedicated
/// evidence branch (`dokimos-evidence`), it is the Git-backed store: Dokimos
/// writes immutable files, and the integration commits and pushes them.
///
/// Layout (store schema 1.0.0):
///   dokimos-store.json                       store marker
///   snapshots/<sha256(id)[0..24]>.json       one immutable file per snapshot
///   baselines/<name>/<acceptedAt>.json       immutable acceptance records
module FileSystemStore =
    [<Literal>]
    let MarkerFile = "dokimos-store.json"

    [<Literal>]
    let StoreSchemaVersion = "1.0.0"

    let marker =
        Contracts.serialize
            {| Contract = "dokimos.store"
               SchemaVersion = StoreSchemaVersion
               Layout = "snapshots/<sha256(snapshot-id)[0..24]>.json; baselines/<name>/<accepted-at>.json"
               Immutability = "Files are created once and never replaced. A different snapshot under an existing identity is rejected." |}

    let private snapshotFile root (id: string) =
        Path.Combine(root, "snapshots", (Contracts.sha256 id).Substring(0, 24) + ".json")

    let private safeName (name: string) =
        if String.IsNullOrWhiteSpace name || name |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-' || c = '_')) then
            Error(InvalidStoredEvidence(name, "baseline names may contain only letters, digits, '-' and '_'"))
        else Ok name

    /// Creates a file only if it does not exist. Returns false when it does.
    let private createNew (path: string) (content: string) =
        match Path.GetDirectoryName path with
        | null -> ()
        | directory -> Directory.CreateDirectory directory |> ignore
        try
            use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)
            use writer = new StreamWriter(stream)
            writer.Write content
            true
        with :? IOException when File.Exists path ->
            false

    let init root =
        Directory.CreateDirectory(Path.Combine(root, "snapshots")) |> ignore
        Directory.CreateDirectory(Path.Combine(root, "baselines")) |> ignore
        createNew (Path.Combine(root, MarkerFile)) marker |> ignore

    let private readSnapshotFile path =
        Contracts.readSnapshot (File.ReadAllText path)
        |> Result.mapError (fun reason -> InvalidStoredEvidence(path, reason))

    let private acceptanceJson (a: BaselineAcceptance) =
        Contracts.serialize
            {| Contract = "dokimos.baseline-acceptance"
               SchemaVersion = "1.0.0"
               Name = a.Name
               SnapshotId = a.SnapshotId
               AcceptedAt = a.AcceptedAt
               Actor = a.Actor
               Reason = a.Reason |}

    let private readAcceptance path =
        let decoder: Json.Decoder<BaselineAcceptance> =
            fun e ->
                Json.result {
                    let! schema = Json.field "SchemaVersion" Json.string e
                    do! if schema = "1.0.0" then Ok() else Error("unsupported-acceptance-schema:" + schema)
                    let! name = Json.field "Name" Json.nonEmptyString e
                    let! id = Json.field "SnapshotId" Json.nonEmptyString e
                    let! at = Json.field "AcceptedAt" Json.dateTimeOffset e
                    let! actor = Json.field "Actor" Json.string e
                    let! reason = Json.field "Reason" Json.string e
                    return { Name = name; SnapshotId = id; AcceptedAt = at; Actor = actor; Reason = reason }
                }
        Json.parse decoder (File.ReadAllText path)
        |> Result.mapError (fun e -> InvalidStoredEvidence(path, string e))

    let private sequence results =
        List.foldBack (fun item state -> Result.bind (fun values -> Result.map (fun v -> v :: values) item) state) results (Ok [])

    let openStore (root: string) : Result<EvidenceStore, StoreError> =
        if not (File.Exists(Path.Combine(root, MarkerFile))) then Error(StoreNotInitialized root)
        else
            let tryGet id =
                let path = snapshotFile root id
                if File.Exists path then readSnapshotFile path |> Result.map Some else Ok None

            let put (snapshot: CanonicalSnapshot) =
                EvidenceStore.verifyIdentity snapshot
                |> Result.bind (fun snapshot ->
                    let json = Contracts.serialize (Contracts.snapshotDto snapshot)
                    if createNew (snapshotFile root snapshot.SnapshotId) json then Ok Stored
                    else
                        tryGet snapshot.SnapshotId
                        |> Result.bind (function
                            | Some existing when Contracts.evidenceDigest existing = Contracts.evidenceDigest snapshot -> Ok AlreadyStored
                            | _ -> Error(IdentityConflict snapshot.SnapshotId)))

            let list () =
                let dir = Path.Combine(root, "snapshots")
                if not (Directory.Exists dir) then Ok []
                else Directory.EnumerateFiles(dir, "*.json") |> Seq.sort |> Seq.map readSnapshotFile |> Seq.toList |> sequence

            let acceptances name =
                safeName name
                |> Result.bind (fun name ->
                    let dir = Path.Combine(root, "baselines", name)
                    if not (Directory.Exists dir) then Ok []
                    else Directory.EnumerateFiles(dir, "*.json") |> Seq.sort |> Seq.map readAcceptance |> Seq.toList |> sequence)

            let accept (acceptance: BaselineAcceptance) =
                safeName acceptance.Name
                |> Result.bind (fun name ->
                    tryGet acceptance.SnapshotId
                    |> Result.bind (function
                        | None -> Error(SnapshotNotFound acceptance.SnapshotId)
                        | Some _ ->
                            let stamp = acceptance.AcceptedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'")
                            let path = Path.Combine(root, "baselines", name, stamp + ".json")
                            if createNew path (acceptanceJson acceptance) then Ok Stored
                            else
                                readAcceptance path
                                |> Result.bind (fun existing ->
                                    if existing = acceptance then Ok AlreadyStored
                                    else Error(IdentityConflict(name + "@" + stamp)))))

            Ok
                { Describe = "filesystem:" + root
                  Put = put
                  TryGet = tryGet
                  List = list
                  Accept = accept
                  Acceptances = acceptances }
