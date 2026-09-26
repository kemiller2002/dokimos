namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Domain
open Dokimos.Core
open ProvenanceFixtures

module MeasurementAttributionTests =
    let private environment (values: (string * string) list) =
        let map = Map.ofList values
        fun name -> Map.tryFind name map

    let private resolve envValues flags runId =
        let declaration = ActorDeclaration.overriding (ActorDeclaration.fromEnvironment (environment envValues)) flags
        MeasurementAttribution.resolve declaration runId

    let private at = DateTimeOffset.Parse("2026-09-26T08:45:00Z")

    [<Fact>]
    let ``with nothing declared the measurement actor is unknown and keyed by the Dokimos run`` () =
        let actor, key = resolve [] ActorDeclaration.empty "run-1" |> unwrap
        Assert.Equal(Actor.unknown, actor)
        Assert.Equal("EXT-dokimos.run-1", ContributionKey.value key)

        let block = MeasurementAttribution.block (MeasurementAttribution.contribution actor key at None) [] |> unwrap
        let _, contributions, _, _ = supported (ProvenanceInterchange.classify block)
        let recorded = Assert.Single(contributions)
        Assert.Equal<ContributionOperation list>([ ContributionOperation.Created; ContributionOperation.Measured ], recorded.Operations)
        Assert.Equal("2026-09-26T08:45:00.000Z", recorded.At)

    [<Fact>]
    let ``the ROS propagation variables declare the actor and the invoking execution`` () =
        let actor, key =
            resolve
                [ "ROS_ACTOR_KIND", "agent"
                  "ROS_TELEMETRY_PROVIDER", "anthropic"
                  "ROS_TELEMETRY_RUNTIME", "claude-code"
                  "ROS_EXECUTION_ID", "EXE-20260926T081023859Z-75aea576" ]
                ActorDeclaration.empty
                "run-1"
            |> unwrap

        Assert.Equal(claude, actor)
        Assert.Equal(ContributionKey.Execution "EXE-20260926T081023859Z-75aea576", key)

    [<Fact>]
    let ``explicit flags win over the environment`` () =
        let flags =
            { ActorDeclaration.empty with
                Kind = Some "agent"
                Id = Some "openai/codex"
                Provider = Some "openai"
                Model = Some "gpt-5-codex"
                Runtime = Some "codex"
                Execution = Some "EXE-20260926T100000000Z-a2a2a2a2" }

        let actor, key =
            resolve [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin"; "ROS_EXECUTION_ID", "EXE-20260926T081023859Z-75aea576" ] flags "run-1" |> unwrap

        Assert.Equal(codex, actor)
        Assert.Equal("EXE-20260926T100000000Z-a2a2a2a2", ContributionKey.value key)

    [<Fact>]
    let ``actor JSON is accepted when valid and rejected when it is not`` () =
        let actor, _ =
            resolve [] { ActorDeclaration.empty with ActorJson = Some """{"kind":"automation","id":"github/github-actions","provider":"github","model":"unknown","runtime":"github-actions"}""" } "run-1"
            |> unwrap

        Assert.Equal(ci, actor)
        Assert.True(Result.isError (resolve [] { ActorDeclaration.empty with ActorJson = Some """{"kind":"agent","id":"x"}""" } "run-1"))
        Assert.True(Result.isError (resolve [] { ActorDeclaration.empty with ActorJson = Some "not json" } "run-1"))

    [<Fact>]
    let ``a human has no provider, model, or runtime; a non-human records unknown ones literally`` () =
        let human, _ = resolve [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin"; "ROS_TELEMETRY_PROVIDER", "anthropic" ] ActorDeclaration.empty "run-1" |> unwrap
        Assert.Equal(kevin, human)

        let agent, key = resolve [ "ROS_ACTOR_KIND", "agent" ] ActorDeclaration.empty "run-1" |> unwrap
        Assert.Equal({ Kind = ActorKind.Agent; Id = "unknown"; Provider = Some "unknown"; Model = Some "unknown"; Runtime = Some "unknown" }, agent)
        Assert.Equal("EXT-dokimos.run-1", ContributionKey.value key)

    [<Fact>]
    let ``two runs of the same agent get two keys that never merge`` () =
        let flags = { ActorDeclaration.empty with Kind = Some "agent"; Provider = Some "openai"; Runtime = Some "codex"; Model = Some "gpt-5-codex" }
        let firstActor, firstKey = resolve [] flags "run-1" |> unwrap
        let secondActor, secondKey = resolve [] flags "run-2" |> unwrap
        Assert.Equal(firstActor, secondActor)
        Assert.NotEqual(firstKey, secondKey)

        let block =
            [ MeasurementAttribution.contribution firstActor firstKey at None
              { MeasurementAttribution.contribution secondActor secondKey (at.AddHours 1.0) None with Operations = [ ContributionOperation.Measured ] } ]
            |> appendAll

        let _, contributions, _, _ = supported (ProvenanceInterchange.classify block)
        Assert.Equal(2, contributions.Length)
        Assert.All(contributions, fun item -> Assert.Equal("openai/codex", item.Actor.Id))

    [<Fact>]
    let ``declarations that are not identities are refused rather than guessed`` () =
        Assert.True(Result.isError (resolve [ "ROS_ACTOR_KIND", "robot" ] ActorDeclaration.empty "run-1"))
        Assert.True(Result.isError (resolve [ "ROS_ACTOR_KIND", "automation"; "ROS_EXECUTION_ID", "session-42" ] ActorDeclaration.empty "run-1"))
        Assert.True(Result.isError (resolve [ "ROS_ACTOR_KIND", "automation"; "ROS_EXECUTION_ID", "CTB-20260926-1" ] ActorDeclaration.empty "run-1"))
        Assert.True(Result.isError (resolve [] { ActorDeclaration.empty with Execution = Some "EXE-1\n" } "run-1"))
        Assert.True(Result.isError (resolve [] ActorDeclaration.empty "run with spaces"))
        Assert.True(Result.isError (resolve [ "ROS_ACTOR", "ghp_0123456789abcdefghijABCDEFGHIJ0123" ] ActorDeclaration.empty "run-1"))

    [<Fact>]
    let ``only the Praxis propagation variables are read`` () =
        let seen = Collections.Generic.List<string>()

        ActorDeclaration.fromEnvironment (fun name ->
            seen.Add name
            None)
        |> ignore

        Assert.Equal<string list>(ActorDeclaration.environmentVariables |> List.sort, seen |> Seq.toList |> List.sort)

    [<Fact>]
    let ``an identity-less process never inherits ROS_EXECUTION_ID from its environment (rule 8)`` () =
        let actor, key = resolve [ "ROS_EXECUTION_ID", "EXE-20260926T081023859Z-75aea576" ] ActorDeclaration.empty "run-1" |> unwrap
        Assert.Equal(Actor.unknown, actor)
        Assert.Equal("EXT-dokimos.run-1", ContributionKey.value key)

        // Provider/runtime alone are not an identity either.
        let _, key =
            resolve [ "ROS_TELEMETRY_PROVIDER", "anthropic"; "ROS_TELEMETRY_RUNTIME", "claude-code"; "ROS_EXECUTION_ID", "EXE-20260926T081023859Z-75aea576" ] ActorDeclaration.empty "run-1"
            |> unwrap

        Assert.Equal("EXT-dokimos.run-1", ContributionKey.value key)

        // A declared kind or id (environment or flag) makes it honoured; an explicit --execution always is.
        let _, byKind = resolve [ "ROS_ACTOR_KIND", "automation"; "ROS_EXECUTION_ID", "EXE-20260926T081023859Z-75aea576" ] ActorDeclaration.empty "run-1" |> unwrap
        Assert.Equal("EXE-20260926T081023859Z-75aea576", ContributionKey.value byKind)
        let _, byFlagId = resolve [ "ROS_EXECUTION_ID", "EXE-20260926T081023859Z-75aea576" ] { ActorDeclaration.empty with Id = Some "kevin"; Kind = Some "human" } "run-1" |> unwrap
        Assert.Equal("EXE-20260926T081023859Z-75aea576", ContributionKey.value byFlagId)
        let _, explicitExecution = resolve [] { ActorDeclaration.empty with Execution = Some "EXE-20260926T100000000Z-a2a2a2a2" } "run-1" |> unwrap
        Assert.Equal("EXE-20260926T100000000Z-a2a2a2a2", ContributionKey.value explicitExecution)

    [<Fact>]
    let ``every environment variable Dokimos reads is a Praxis identity variable from the vendored list (rule 7)`` () =
        let vendored = fixture "identity-environment.json" |> field "variables" |> items |> List.choose Json.tryString
        Assert.Equal(19, vendored.Length)

        for name in ActorDeclaration.environmentVariables do
            Assert.Contains(name, vendored)

        // Session/run hints that identify a different run are never read as an identity.
        let actor, key =
            resolve [ "CLAUDE_CODE_SESSION_ID", "s-1"; "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "99"; "OLLAMA_HOST", "localhost" ] ActorDeclaration.empty "run-1"
            |> unwrap

        Assert.Equal(Actor.unknown, actor)
        Assert.Equal("EXT-dokimos.run-1", ContributionKey.value key)
