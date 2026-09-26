namespace Dokimos.Core.Tests

open System
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core

module MeasurementProvenanceTests =
    let environment (pairs: (string * string) list) =
        let values = Map.ofList pairs
        IdentityEnvironment.read (fun name -> values |> Map.tryFind name)

    let resolve pairs =
        match ActingIdentity.resolve ActorDeclaration.none (environment pairs) "local-test" with
        | Ok identity -> identity
        | Error message -> failwith message

    let at = DateTimeOffset(2026, 9, 26, 11, 0, 0, TimeSpan.Zero)

    let snapshot repository revision provenance =
        { SchemaVersion = CanonicalSnapshot.SchemaVersion
          SnapshotId = repository + ":" + revision
          Repository = repository
          Revision = revision
          Ref = "main"
          CollectedAt = at
          Collector = "dokimos-cli/1"
          Metrics = []
          Findings =
            [ { FindingId = "correlation:agent-generated-risk-pattern:A.fs"
                Scope = "A.fs"
                Kind = "agent-generated-risk-pattern"
                State = "present"
                Evidence = []
                Explanation = "e" } ]
          Provenance = provenance }

    let load = ProvenanceConformanceTests.load
    let get = ProvenanceConformanceTests.get
    let str = ProvenanceConformanceTests.str
    let obj = ProvenanceConformanceTests.obj

    let contributions (record: JsonObject) = obj [ "contributions" ] record
    let entry (record: JsonObject) key = get [ "contributions"; key ] record

    let normalizeReason (record: JsonObject) key =
        let copy = record.DeepClone().AsObject()
        let target = obj [ "contributions"; key ] copy
        target["reason"] <- JsonValue.Create "normalized"
        copy

    [<Fact>]
    let ``a declared agent is recorded exactly as declared`` () =
        let identity =
            resolve [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "openai"; "ROS_TELEMETRY_RUNTIME", "codex"; "ROS_EXECUTION_ID", "EXE-20260926T140000000Z-e5e5e5e5" ]

        Assert.Equal(ActorKind.Agent, identity.Actor.ActorKind)
        Assert.Equal("openai/codex", identity.Actor.ActorId)
        Assert.Equal(Some "unknown", identity.Actor.Model)
        Assert.Equal("EXE-20260926T140000000Z-e5e5e5e5", identity.ExecutionKey)

    [<Fact>]
    let ``GitHub Actions with nothing declared is automation keyed by its run`` () =
        let identity = resolve [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001"; "GITHUB_RUN_ATTEMPT", "2" ]
        Assert.Equal(ActingIdentity.gitHubActions, identity.Actor)
        Assert.Equal("EXE-dokimos.gh-run-9001-2", identity.ExecutionKey)
        Assert.Equal("github-actions", identity.Mechanism)

    [<Fact>]
    let ``nothing declared resolves to unknown, never a guess`` () =
        let identity = resolve []
        Assert.Equal(ProvenanceActor.unknown, identity.Actor)
        Assert.Equal("EXE-dokimos.local-test", identity.ExecutionKey)

    [<Fact>]
    let ``explicit declarations win over the environment and malformed declarations are refused`` () =
        let declared = { ActorDeclaration.none with DeclaredKind = Some "human"; DeclaredId = Some "kevin" }
        match ActingIdentity.resolve declared (environment [ "GITHUB_ACTIONS", "true"; "ROS_ACTOR_KIND", "agent" ]) "r" with
        | Ok identity ->
            Assert.Equal(ActorKind.Human, identity.Actor.ActorKind)
            Assert.Equal(None, identity.Actor.Provider)
        | Error message -> failwith message

        Assert.True(Result.isError (ActingIdentity.resolve { ActorDeclaration.none with DeclaredKind = Some "robot" } (environment []) "r"))
        Assert.True(Result.isError (ActingIdentity.resolve ActorDeclaration.none (environment [ "ROS_EXECUTION_ID", "not-an-execution" ]) "r"))
        Assert.True(Result.isError (ActingIdentity.resolve ActorDeclaration.none (environment [ "ROS_ACTOR", "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345" ]) "r"))

    [<Fact>]
    let ``measurement of e2e 03 reproduces e2e 04: automation measures, the code author is only a source`` () =
        let identity = resolve [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001" ]
        let implementation = load "e2e/03-implementation.json"
        let expected = load "e2e/04-dokimos-measurement.json"

        match MeasurementProvenance.forSnapshot identity (snapshot "app" "abc123" None) (Some implementation) with
        | Error problems -> failwithf "%A" problems
        | Ok record ->
            let key = "EXE-dokimos.gh-run-9001"
            Assert.True(JsonNode.DeepEquals(normalizeReason expected key, normalizeReason record key), record.ToJsonString())
            Assert.Equal<string list>([ key ], contributions record |> Seq.map _.Key |> Seq.toList)
            Assert.DoesNotContain("c3c3c3c3", (contributions record).ToJsonString())
            Assert.Contains("EXE-20260926T100000000Z-c3c3c3c3", (get [ "sources" ] record).ToJsonString())
            Assert.Equal("automation", str [ "actor"; "kind" ] (entry record key))

    [<Fact>]
    let ``a subject record for a different revision or a malformed one is refused, not dropped`` () =
        let identity = resolve []
        Assert.True(Result.isError (MeasurementProvenance.forSnapshot identity (snapshot "app" "fff999" None) (Some(load "e2e/03-implementation.json"))))
        Assert.True(Result.isError (MeasurementProvenance.forSnapshot identity (snapshot "app" "abc12" None) (Some(load "e2e/03-implementation.json"))))
        Assert.True(Result.isError (MeasurementProvenance.forSnapshot identity (snapshot "app" "abc123" None) (Some(load "invalid/two-created.json"))))

    [<Fact>]
    let ``an unsupported-major subject record is carried verbatim`` () =
        let future = load "unsupported/future-major.json"
        match MeasurementProvenance.forSnapshot (resolve []) (snapshot "app" "abc123" None) (Some future) with
        | Error problems -> failwithf "%A" problems
        | Ok record -> Assert.True(JsonNode.DeepEquals(future, get [ "sources"; "git:commit/abc123" ] record))

    let measured repository revision pairs =
        match MeasurementProvenance.forSnapshot (resolve pairs) (snapshot repository revision None) None with
        | Ok record -> snapshot repository revision (Some record)
        | Error problems -> failwithf "%A" problems

    [<Fact>]
    let ``comparison keeps both measuring actors separate and is created by the comparing actor`` () =
        let before = measured "app" "aaa111" [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ]
        let after = measured "app" "bbb222" [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "7" ]
        let comparer = resolve [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "anthropic"; "ROS_TELEMETRY_RUNTIME", "claude-code"; "ROS_EXECUTION_ID", "EXE-20260926T150000000Z-f6f6f6f6" ]

        match MeasurementProvenance.forComparison comparer at before after with
        | Error problems -> failwithf "%A" problems
        | Ok record ->
            Assert.Equal<string list>([ "EXE-20260926T150000000Z-f6f6f6f6" ], contributions record |> Seq.map _.Key |> Seq.toList)
            Assert.Equal("dokimos:comparison/app:aaa111..app:bbb222", str [ "subject" ] record)
            Assert.True(JsonNode.DeepEquals(before.Provenance.Value, get [ "sources"; "dokimos:snapshot/app:aaa111" ] record))
            Assert.True(JsonNode.DeepEquals(after.Provenance.Value, get [ "sources"; "dokimos:snapshot/app:bbb222" ] record))
            Assert.Equal("kevin", str [ "sources"; "dokimos:snapshot/app:aaa111"; "contributions"; "EXE-dokimos.local-test"; "actor"; "id" ] record)
            Assert.Equal("github/github-actions", str [ "sources"; "dokimos:snapshot/app:bbb222"; "contributions"; "EXE-dokimos.gh-run-7"; "actor"; "id" ] record)

    [<Fact>]
    let ``a legacy snapshot without provenance is lineage only; nothing is invented`` () =
        let before = snapshot "app" "aaa111" None
        let after = measured "app" "bbb222" []
        match MeasurementProvenance.forComparison (resolve []) at before after with
        | Error problems -> failwithf "%A" problems
        | Ok record ->
            Assert.Contains("dokimos:snapshot/app:aaa111", (get [ "derivedFrom" ] record).ToJsonString())
            Assert.Equal(None, ProvenanceConformanceTests.tryGet [ "sources"; "dokimos:snapshot/app:aaa111" ] record)
        Assert.True(Result.isError (MeasurementProvenance.forFinding before "correlation:agent-generated-risk-pattern:A.fs"))

    [<Fact>]
    let ``remediation and validation append to a finding without displacing the measurer`` () =
        let measuredSnapshot = measured "app" "abc123" [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001" ]
        let findingId = "correlation:agent-generated-risk-pattern:A.fs"
        let remediator = resolve [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "anthropic"; "ROS_TELEMETRY_RUNTIME", "claude-code"; "ROS_EXECUTION_ID", "EXE-20260926T150000000Z-f6f6f6f6" ]
        let validator = resolve [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9100" ]

        let result =
            MeasurementProvenance.forFinding measuredSnapshot findingId
            |> Result.bind (MeasurementProvenance.recordFollowUp remediator (at.AddHours 4.0) ProvenanceOperation.remediated None [ "git:commit/def456" ])
            |> Result.bind (MeasurementProvenance.recordFollowUp validator (at.AddHours 5.0) ProvenanceOperation.validated (Some "Regression test passed") [])

        match result with
        | Error problems -> failwithf "%A" problems
        | Ok record ->
            Assert.Equal("dokimos:finding/app:abc123/" + findingId, str [ "subject" ] record)
            let created = entry record "EXE-dokimos.gh-run-9001"
            Assert.Equal("created", str [ "operations"; "0" ] created)
            Assert.Equal("github/github-actions", str [ "actor"; "id" ] created)
            Assert.Equal("x-remediated", str [ "operations"; "0" ] (entry record "EXE-20260926T150000000Z-f6f6f6f6"))
            Assert.Equal("x-validated", str [ "operations"; "0" ] (entry record "EXE-dokimos.gh-run-9100"))
            Assert.True(JsonNode.DeepEquals(get [ "contributions"; "EXE-dokimos.gh-run-9001" ] measuredSnapshot.Provenance.Value, created))

    [<Fact>]
    let ``a follow-up cannot claim created or re-attribute the measuring execution`` () =
        let measuredSnapshot = measured "app" "abc123" [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001" ]
        let finding = MeasurementProvenance.forFinding measuredSnapshot "correlation:agent-generated-risk-pattern:A.fs" |> Result.defaultWith (failwithf "%A")
        let impostor = resolve [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "mallory"; "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001" ]
        Assert.True(Result.isError (MeasurementProvenance.recordFollowUp impostor (at.AddHours 1.0) ProvenanceOperation.validated None [] finding))
        Assert.True(Result.isError (MeasurementProvenance.recordFollowUp (resolve []) (at.AddHours 1.0) ProvenanceOperation.Created None [] finding))

    [<Fact>]
    let ``baseline acceptance is a separate record that leaves the snapshot untouched`` () =
        let measuredSnapshot = measured "app" "abc123" []
        let original = measuredSnapshot.Provenance.Value.DeepClone()
        match MeasurementProvenance.forBaselineAcceptance (resolve [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ]) (at.AddDays 1.0) None measuredSnapshot with
        | Error problems -> failwithf "%A" problems
        | Ok record ->
            Assert.Equal("dokimos:baseline/app:abc123", str [ "subject" ] record)
            Assert.Contains("approved", record.ToJsonString())
            Assert.True(JsonNode.DeepEquals(original, measuredSnapshot.Provenance.Value))

    [<Fact>]
    let ``two different measurements of one revision are refused rather than one being dropped`` () =
        let first = measured "app" "abc123" [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ]
        let second = measured "app" "abc123" [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "5" ]
        Assert.True(Result.isError (MeasurementProvenance.forComparison (resolve []) at first second))
        Assert.True(Result.isOk (MeasurementProvenance.forComparison (resolve []) at first first))
