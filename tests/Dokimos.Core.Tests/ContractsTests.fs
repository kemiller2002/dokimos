namespace Dokimos.Core.Tests

open System
open System.Text.Json
open Xunit
open Dokimos.Domain
open Dokimos.Core
open Fixtures

module ContractsTests =
    let roundTrip snapshot =
        Contracts.snapshotDto snapshot |> Contracts.serialize |> Contracts.readSnapshot

    [<Fact>]
    let ``schema 2 snapshot round-trips through the wire contract`` () =
        let failure = { Code = "trx-malformed"; Message = "bad xml" }
        let original =
            { snapshot "a" [ metric "source.lines" 3M
                             CanonicalMetric.unavailable "coverage.line-rate" "repository" "ratio" NotConfigured "c"
                             CanonicalMetric.unavailable "change.file-churn" "A.fs" "lines" (InsufficientEvidence "no history") "g"
                             CanonicalMetric.failed "tests.total" "repository" "count" failure "t" ] [ finding "f" ]
                with Performance = Some { TotalMilliseconds = 5L; FileCount = 1; ObservationCount = 4; UnavailableCollectors = [ "x" ]; FailedCollectors = [] } }
        Assert.Equal(Ok original, roundTrip original)

    [<Fact>]
    let ``schema 1 snapshots remain readable without inventing provenance`` () =
        let json = """{"SchemaVersion":"1.0.0","SnapshotId":"r:x","Repository":"r","Revision":"x","Ref":"main","CollectedAt":"2026-09-26T20:16:28.4331831+00:00","Collector":"dokimos-cli/1","Metrics":[{"MetricId":"source.lines","MetricVersion":1,"Scope":"A.fs","State":"available","Value":74,"Unit":"lines","Source":"structural"}],"Findings":[]}"""
        match Contracts.readSnapshot json with
        | Ok s ->
            Assert.Equal(None, s.Producer)
            Assert.Equal(Available(74M, "lines"), (List.exactlyOne s.Metrics).Measurement)
        | Error e -> failwith e

    [<Fact>]
    let ``unsupported snapshot schema fails explicitly`` () =
        let json = """{"SchemaVersion":"9.0.0"}"""
        Assert.Equal(Error "unsupported-snapshot-schema:9.0.0", Contracts.readSnapshot json)

    [<Fact>]
    let ``an available metric without a value is rejected, not read as zero`` () =
        let json = """{"SchemaVersion":"1.0.0","SnapshotId":"r:x","Repository":"r","Revision":"x","Ref":"main","CollectedAt":"2026-09-26T20:16:28Z","Collector":"c","Metrics":[{"MetricId":"m","MetricVersion":1,"Scope":"A.fs","State":"available","Value":null,"Unit":"lines","Source":"s"}],"Findings":[]}"""
        match Contracts.readSnapshot json with
        | Error e -> Assert.Contains("available metrics require a numeric Value", e)
        | Ok _ -> failwith "expected rejection"

    [<Fact>]
    let ``comparison wire model uses stable tags for every state`` () =
        let kinds = [ MetricAdded; MetricRemoved; MetricImproved; MetricDeteriorated; MetricUnchanged; MetricChanged; MetricIncompatible; MetricUnavailable ]
        Assert.Equal<string list>([ "added"; "removed"; "improved"; "deteriorated"; "unchanged"; "changed"; "incompatible"; "unavailable" ], kinds |> List.map Contracts.metricChangeTag)
        let findings = [ FindingIntroduced; FindingResolved; FindingPersistent; FindingUnavailable ]
        Assert.Equal<string list>([ "introduced"; "resolved"; "persistent"; "unavailable" ], findings |> List.map Contracts.findingChangeTag)

    [<Fact>]
    let ``comparison serializes without F# union support`` () =
        let before = snapshot "a" [ metric "source.mutable-bindings" 2M ] [ finding "f" ]
        let after = snapshot "b" [ metric "source.mutable-bindings" 1M ] []
        let json = CanonicalComparison.compare before after |> Contracts.comparisonDto before after |> Contracts.serialize
        use doc = JsonDocument.Parse json
        let change = doc.RootElement.GetProperty("MetricChanges").[0]
        Assert.Equal("improved", change.GetProperty("Kind").Text)
        Assert.Equal("available", change.GetProperty("Before").GetProperty("State").Text)
        let findingChange = doc.RootElement.GetProperty("FindingChanges").[0]
        Assert.Equal("resolved", findingChange.GetProperty("Kind").Text)

    [<Fact>]
    let ``policy 1.2.0 takes ratchet limits from the baseline and rejects embedded suppressions`` () =
        let v12 = """{"schemaVersion":"1.2.0","baseline":"B","ratchets":[{"metricId":"m","metricVersion":1,"bestAccepted":"baseline","preference":"lower-is-better","disposition":"fail"}]}"""
        match Contracts.readPolicy v12 with
        | Ok p -> Assert.Equal(AcceptedBaseline, p.Ratchets.Head.Bound)
        | Error e -> failwith e
        Assert.True(Result.isError (Contracts.readPolicy (v12.Replace("\"1.2.0\"", "\"1.1.0\""))))
        match Contracts.readPolicy """{"schemaVersion":"1.2.0","baseline":"B","suppressions":[]}""" with
        | Error e -> Assert.Contains("quality/exceptions.json", e)
        | Ok _ -> failwith "1.2.0 must reject suppressions"

    [<Fact>]
    let ``policy 1.0.0 and 1.1.0 decode; unknown versions and dispositions are rejected`` () =
        let v10 = """{"schemaVersion":"1.0.0","baseline":"B","ratchets":[{"metricId":"m","metricVersion":1,"bestAccepted":0,"preference":"lower-is-better","disposition":"fail"}],"unavailableBehavior":"not-evaluated"}"""
        let v11 = """{"schemaVersion":"1.1.0","baseline":"B","regressions":{"disposition":"warn"},"requiredEvidence":["m"],"suppressions":[{"findingId":"f","reason":"r","scope":"A.fs","created":"2026-09-01T00:00:00Z","expires":null,"status":"active"}]}"""
        match Contracts.readPolicy v10, Contracts.readPolicy v11 with
        | Ok p10, Ok p11 ->
            Assert.Equal(1, p10.Ratchets.Length)
            Assert.Equal(None, p10.Regressions)
            Assert.Equal(Some Warn, p11.Regressions)
            Assert.Equal<string list>([ "m" ], p11.RequiredEvidence)
            Assert.Equal(1, p11.Suppressions.Length)
            Assert.StartsWith("sha256:", p11.Identity)
        | a, b -> failwith $"{a} {b}"
        Assert.Equal(Error "invalid-policy: unsupported-policy-schema:3.0.0", Contracts.readPolicy """{"schemaVersion":"3.0.0","baseline":"B"}""")
        match Contracts.readPolicy """{"schemaVersion":"1.1.0","baseline":"B","regressions":{"disposition":"maybe"}}""" with
        | Error e -> Assert.Contains("unknown value 'maybe'", e)
        | Ok _ -> failwith "expected rejection"
