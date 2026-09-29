namespace Dokimos.Core.Tests

open System
open System.IO
open Xunit
open Dokimos.Core

module EvidenceStoreTests =
    let request revision source : CollectionRequest =
        { Repository = "o/r"; Revision = revision; Ref = "main"; CollectedAt = DateTimeOffset.Parse "2026-09-01T00:00:00Z"
          AnalyzedScope = [ "src" ]; Sources = [ "A.fs", source ]; GitHistory = None; BuildLog = None; TestResults = None; Coverage = None }

    let snap revision source = Collection.snapshot (Timer.fixedDuration 1L) (request revision source)

    let freshStore () =
        let root = Path.Combine(Path.GetTempPath(), "dokimos-store-tests", Guid.NewGuid().ToString("N"))
        FileSystemStore.init root
        match FileSystemStore.openStore root with
        | Ok store -> root, store
        | Error e -> failwith (string e)

    [<Fact>]
    let ``an uninitialized directory is not silently used as a store`` () =
        let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
        Assert.Equal(Error(StoreNotInitialized root) |> Result.map ignore, FileSystemStore.openStore root |> Result.map ignore)

    [<Fact>]
    let ``stored snapshots round-trip`` () =
        let _, store = freshStore ()
        let s = snap "a" "let x = 1"
        Assert.Equal(Ok Stored, store.Put s)
        Assert.Equal(Ok(Some s), store.TryGet s.SnapshotId)

    [<Fact>]
    let ``re-storing identical evidence is idempotent`` () =
        let _, store = freshStore ()
        let s = snap "a" "let x = 1"
        store.Put s |> ignore
        let later = { s with CollectedAt = s.CollectedAt.AddHours 1.0; Performance = Some { s.Performance.Value with TotalMilliseconds = 99L } }
        Assert.Equal(Ok AlreadyStored, store.Put later)
        Assert.Equal(Ok(Some s), store.TryGet s.SnapshotId)

    [<Fact>]
    let ``different content under an existing identity is rejected and the original kept`` () =
        let root, store = freshStore ()
        let s = snap "a" "let x = 1"
        store.Put s |> ignore
        // A legacy (schema 1.0.0) record has no derived identity, so forge one.
        let legacy = { s with SchemaVersion = "1.0.0"; Producer = None; Performance = None; SnapshotId = "o/r:a" }
        Assert.Equal(Ok Stored, store.Put legacy)
        let forged = { legacy with Metrics = List.tail legacy.Metrics }
        Assert.Equal(Error(IdentityConflict "o/r:a"), store.Put forged)
        Assert.Equal(Ok(Some legacy), store.TryGet "o/r:a")
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "snapshots")).Length)

    [<Fact>]
    let ``a claimed identity that does not match the content is rejected`` () =
        let _, store = freshStore ()
        let s = snap "a" "let x = 1"
        let tampered = { s with Metrics = List.tail s.Metrics }
        match store.Put tampered with
        | Error (IdentityMismatch (claimed, _)) -> Assert.Equal(s.SnapshotId, claimed)
        | other -> failwith $"expected identity mismatch, got {other}"

    [<Fact>]
    let ``baselines are immutable acceptances and the latest wins`` () =
        let _, store = freshStore ()
        let first = snap "a" "let x = 1"
        let second = snap "b" "let y = 2"
        store.Put first |> ignore
        store.Put second |> ignore
        let at = DateTimeOffset.Parse "2026-09-02T00:00:00Z"
        Assert.Equal(Ok Stored, store.Accept { Name = "default"; SnapshotId = first.SnapshotId; AcceptedAt = at; Actor = "a"; Reason = "r" })
        Assert.Equal(Ok Stored, store.Accept { Name = "default"; SnapshotId = second.SnapshotId; AcceptedAt = at.AddDays 1.0; Actor = "a"; Reason = "r" })
        match EvidenceStore.currentBaseline store "default" with
        | Ok (Some (_, baseline)) -> Assert.Equal(second.SnapshotId, baseline.SnapshotId)
        | other -> failwith $"{other}"
        Assert.Equal(Ok 2, store.Acceptances "default" |> Result.map List.length)

    [<Fact>]
    let ``a baseline cannot reference evidence the store does not hold`` () =
        let _, store = freshStore ()
        Assert.Equal(Error(SnapshotNotFound "missing"), store.Accept { Name = "default"; SnapshotId = "missing"; AcceptedAt = DateTimeOffset.UnixEpoch; Actor = "a"; Reason = "r" })
