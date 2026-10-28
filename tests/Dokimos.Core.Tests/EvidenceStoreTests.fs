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

    // ---- EII-G: provenance records (dokimos.evidence-provenance/1) in the same store ---------------------------

    module Provenance =
        open Dokimos.Core.EvidenceReuse

        let private revA = String('a', 40)
        let private revB = String('b', 40)
        let private h (c: char) = "sha256:" + String(c, 64)

        let private inputs: IdentityInputs =
            { Sources = Map [ "src/Store.fs", h '1' ]
              Dependencies = Map [ "FSharp.Core", "9.0.100" ]
              TestContract = h '3'
              Configuration = Map [ "Configuration", "Release"; "runtime.os", "linux-x64" ]
              ToolVersion = "dotnet-sdk 10.0.402" }

        let private run id test outcome (i: IdentityInputs) : PriorEvidence =
            { EvidenceId = id; TestId = test; Outcome = outcome; ProducedAtRevision = Some revA
              Freshness = Some ContentAddressed; EvidenceIdentity = Some(identity i); Inputs = Some i }

        let private request test (i: IdentityInputs) : TestRequest =
            { TestId = test; Freshness = ContentAddressed; Category = Functional; Patterns = Some [ "src/**" ]; Inputs = Ok i }

        let private stored (store: EvidenceStore) test =
            match store.Provenance test with
            | Ok records -> records
            | Error e -> failwith (string e)

        [<Fact>]
        let ``passing and failing runs are published independently and read back`` () =
            let _, store = freshStore ()
            let pass = run "EV-1" "store" OutcomePassed inputs
            let fail = run "EV-2" "store" OutcomeFailed inputs
            let other = run "EV-3" "view" OutcomeFailed { inputs with Sources = Map [ "src/View.fs", h '5' ] }
            for r in [ pass; fail; other ] do
                Assert.Equal(Ok Stored, store.PutProvenance r)
            Assert.Equal<PriorEvidence list>([ pass; fail ], stored store "store" |> List.sortBy _.EvidenceId)
            Assert.Equal<PriorEvidence list>([ other ], stored store "view")
            Assert.Empty(stored store "never-run")

        [<Fact>]
        let ``a stored failure at the current identity fires CONFLICTING_PRIOR; another test's failure does not`` () =
            let _, store = freshStore ()
            let view = { inputs with Sources = Map [ "src/View.fs", h '5' ] }
            store.PutProvenance(run "EV-1" "store" OutcomePassed inputs) |> ignore
            store.PutProvenance(run "EV-v" "view" OutcomeFailed view) |> ignore
            let planNow () =
                let priors = stored store "store" @ stored store "view"
                let p = plan revB None [] [] [ request "store" inputs; { request "view" view with Patterns = Some [ "src/View.fs" ] } ] priors
                p.Tests |> List.map (fun t -> t.TestId, t) |> Map.ofList
            let before = planNow ()
            let storeBefore = before["store"]
            Assert.False(isRun storeBefore, $"%A{storeBefore.Reasons}")
            Assert.True(isRun before["view"])
            store.PutProvenance(run "EV-2" "store" OutcomeFailed inputs) |> ignore
            let after = planNow ()
            Assert.True(isRun after["store"])
            Assert.Contains(after["store"].Reasons, fun r -> r.Code = "REUSE.CONFLICTING_PRIOR" && r.Subject = "EV-2")

        [<Fact>]
        let ``published records are immutable`` () =
            let _, store = freshStore ()
            let pass = run "EV-1" "store" OutcomePassed inputs
            store.PutProvenance pass |> ignore
            Assert.Equal(Ok AlreadyStored, store.PutProvenance pass)
            // Rewriting a recorded pass as a failure (or the reverse) is refused.
            Assert.Equal(Error(IdentityConflict "EV-1") |> Result.map ignore, store.PutProvenance { pass with Outcome = OutcomeFailed } |> Result.map ignore)

        [<Fact>]
        let ``incomplete or tampered records are refused at publication`` () =
            let _, store = freshStore ()
            let pass = run "EV-1" "store" OutcomePassed inputs
            match store.PutProvenance { pass with Inputs = None } with
            | Error(InvalidStoredEvidence _) -> ()
            | other -> failwith $"%A{other}"
            match store.PutProvenance { pass with EvidenceIdentity = Some(h 'f') } with
            | Error(IdentityMismatch _) -> ()
            | other -> failwith $"%A{other}"
            Assert.Empty(stored store "store")

        [<Fact>]
        let ``a record whose inputs were edited on disk is refused when read`` () =
            let root, store = freshStore ()
            store.PutProvenance(run "EV-1" "store" OutcomePassed inputs) |> ignore
            let file = Directory.EnumerateFiles(Path.Combine(root, "provenance"), "*.json", SearchOption.AllDirectories) |> Seq.exactlyOne
            // Records are unsigned (docs/evidence-reuse.md, Limitations): an edit
            // to the inputs breaks the identity and is refused; an edit to the
            // outcome alone is not detectable without a signature.
            File.WriteAllText(file, File.ReadAllText(file).Replace(h (char 0x31), h (char 0x39)))
            match store.Provenance "store" with
            | Error(IdentityMismatch _) -> ()
            | other -> failwith $"expected refusal, got %A{other}"
            File.WriteAllText(file, "{ \"SchemaVersion\": \"dokimos.evidence-provenance/1\" }")
            match store.Provenance "store" with
            | Error(InvalidStoredEvidence _) -> ()
            | other -> failwith $"expected refusal, got %A{other}"

        [<Fact>]
        let ``a store created before provenance records existed accepts them`` () =
            let root = Path.Combine(Path.GetTempPath(), "dokimos-store-tests", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(Path.Combine(root, "snapshots")) |> ignore
            File.WriteAllText(Path.Combine(root, FileSystemStore.MarkerFile), "{ \"Contract\": \"dokimos.store\", \"SchemaVersion\": \"1.0.0\" }")
            match FileSystemStore.openStore root with
            | Ok store ->
                Assert.Equal(Ok Stored, store.PutProvenance(run "EV-1" "store" OutcomePassed inputs))
                Assert.Single(stored store "store") |> ignore
            | Error e -> failwith (string e)
