namespace Dokimos.Core.Tests

open System.IO
open Xunit
open Dokimos.Domain
open Dokimos.Core
open ProvenanceFixtures

module SnapshotProvenanceTests =
    let private legacySnapshots () =
        Directory.GetFiles(path "observations", "*.json", SearchOption.AllDirectories)
        |> Array.sort
        |> Array.map (fun file -> file, File.ReadAllText file)
        |> Array.filter (fun (_, content) -> content.Contains "\"schemaVersion\": \"1.0.0\"")

    let private newSnapshot () =
        let _, content = legacySnapshots () |> Array.find (fun (file, _) -> file.EndsWith "baseline-0001-accepted.json")
        json content |> Json.setField "schemaVersion" (Json.String SnapshotProvenance.CurrentVersion)

    let private measurement =
        contribution "EXT-dokimos.ci-36186590248-1" [ ContributionOperation.Created; ContributionOperation.Measured ] "2026-09-26T08:45:00.000Z" ci

    let private commitProvenance =
        appendAll [ contribution "EXE-20260926T083000000Z-b2b2b2b2" [ ContributionOperation.Created ] "2026-09-26T08:30:00.000Z" claude ]

    [<Fact>]
    let ``legacy 1.0.0 snapshots stay valid, read as unattributed, and are never modified`` () =
        let snapshots = legacySnapshots ()
        Assert.NotEmpty(snapshots)

        for file, content in snapshots do
            let before = sha256 file
            let document = json content
            Assert.Equal(Ok SnapshotProvenance.LegacyVersion, SnapshotProvenance.schemaVersion document)
            Assert.Equal(Ok Unattributed, SnapshotProvenance.read document)
            Assert.True(SnapshotProvenance.subjects document |> unwrap |> List.forall ((=) Unattributed))

            let refused = SnapshotProvenance.recordMeasurement measurement [] document

            match refused with
            | Error message -> Assert.Contains("never backfilled", message)
            | Ok _ -> failwith $"{file}: a 1.0.0 snapshot must never be attributed"

            Assert.True(Result.isError (SnapshotProvenance.attachSubject 0 commitProvenance document))
            Assert.Equal(before, sha256 file)

    [<Fact>]
    let ``a 1.0.0 document carrying provenance is rejected`` () =
        let _, content = legacySnapshots () |> Array.head
        let invalid = json content |> Json.setField "provenance" commitProvenance
        Assert.True(Result.isError (SnapshotProvenance.read invalid))
        Assert.True(Result.isError (SnapshotProvenance.read (json content |> Json.setField "schemaVersion" (Json.String "2.0.0"))))

    [<Fact>]
    let ``a 1.1.0 snapshot records the measurement actor separately from the artifact author`` () =
        let document =
            newSnapshot ()
            |> SnapshotProvenance.recordMeasurement measurement [ "git:commit/90ec92ff153c936f29d5bf4e0a5d965a3e966da6" ]
            |> Result.bind (SnapshotProvenance.attachSubject 0 commitProvenance)
            |> unwrap

        let snapshot = SnapshotProvenance.read document |> unwrap
        let measurer = Assert.Single(Authorship.measuredBy snapshot)
        Assert.Equal(ci, measurer.Actor)

        let subject = SnapshotProvenance.subjects document |> unwrap |> List.head
        let author = Authorship.ofSubject subject |> Option.get
        Assert.Equal(claude, author.Actor)
        Assert.NotEqual(measurer.Actor, author.Actor)

        // The subject's author is not a contributor to the snapshot, and the measurer is not the subject's author.
        Assert.DoesNotContain(claude, ProvenanceInterchange.contributions (RecordAttribution.verdict snapshot |> Option.get) |> List.map _.Actor)
        Assert.Empty(RecordAttribution.withRole ProvenanceRole.MeasurementActor subject)
        Assert.Equal<string list>([ "git:commit/90ec92ff153c936f29d5bf4e0a5d965a3e966da6" ], RecordAttribution.lineage snapshot)

        // The document survives a text round trip unchanged.
        let text = Json.serializeIndented document
        Assert.Equal(Json.serialize document, Json.serialize (json text))

    [<Fact>]
    let ``a measured artifact without recorded provenance has no author`` () =
        let document = newSnapshot () |> SnapshotProvenance.recordMeasurement measurement [] |> unwrap
        let subject = SnapshotProvenance.subjects document |> unwrap |> List.head
        Assert.Equal(Unattributed, subject)
        Assert.Equal(None, Authorship.ofSubject subject)

    [<Fact>]
    let ``subject provenance of another major is carried verbatim; malformed is rejected; existing is never overwritten`` () =
        let future = json """{"schema":"praxis.provenance/2","contributions":[{"totally":"different"}]}"""
        let document = newSnapshot () |> SnapshotProvenance.attachSubject 0 future |> unwrap

        match SnapshotProvenance.subjects document |> unwrap |> List.head with
        | CarriedVerbatim(schema, block) ->
            Assert.Equal("praxis.provenance/2", schema)
            Assert.Equal(Json.serialize future, Json.serialize block)
        | other -> failwith $"expected verbatim, got {other}"

        Assert.True(Result.isError (SnapshotProvenance.attachSubject 0 commitProvenance document))
        Assert.True(Result.isOk (SnapshotProvenance.attachSubject 0 future document))

        let malformed = json """{"schema":"praxis.provenance/1"}"""
        Assert.True(Result.isError (newSnapshot () |> SnapshotProvenance.attachSubject 0 malformed))

        let withMalformed = newSnapshot () |> Json.setField "provenance" malformed
        Assert.True(Result.isError (SnapshotProvenance.read withMalformed))

        let withFuture = newSnapshot () |> Json.setField "provenance" future
        Assert.True(Result.isError (SnapshotProvenance.recordMeasurement measurement [] withFuture))

    [<Fact>]
    let ``a second measurement of the same snapshot is refused as a second creation`` () =
        let document = newSnapshot () |> SnapshotProvenance.recordMeasurement measurement [] |> unwrap
        let other = { measurement with Key = key "EXT-dokimos.ci-2"; At = "2026-09-26T09:00:00.000Z" }
        Assert.True(Result.isError (SnapshotProvenance.recordMeasurement other [] document))

    [<Fact>]
    let ``snapshot schema accepts 1.0.0 and 1.1.0 and declares the optional provenance fields`` () =
        let schema = json (File.ReadAllText(path "schemas/dokimos-snapshot.schema.json"))
        let properties = field "properties" schema
        Assert.Equal("""{"enum":["1.0.0","1.1.0"]}""", Json.serialize (field "schemaVersion" properties))
        Assert.True((Json.tryField "provenance" properties).IsSome)

        let observation = properties |> field "observations" |> field "items" |> field "properties"
        Assert.True((Json.tryField "subjectProvenance" observation).IsSome)
        Assert.Contains("\"1.0.0\"", Json.serialize (field "allOf" schema))
