namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

/// RQ-DOKIMOS-ARCH-001: DOK-ARCH-001/005/006 activation, detection,
/// decision-record exceptions and verdicts, on deterministic fixtures.
module ReliabilityArchitectureTests =
    let asOf = DateTimeOffset.Parse "2026-11-26T12:00:00Z"

    let file path content : SourceFile = { Path = path; Content = content }

    let project (references: string list) (packages: string list) =
        let refs = references |> List.map (fun r -> $"""    <ProjectReference Include="../{r}/{r}.fsproj" />""")
        let pkgs = packages |> List.map (fun p -> $"""    <PackageReference Include="{p}" Version="1.0.0" />""")
        String.concat "\n" ([ """<Project Sdk="Microsoft.NET.Sdk">"""; "  <ItemGroup>" ] @ refs @ pkgs @ [ "  </ItemGroup>"; "</Project>" ])

    /// Known-clean: the API reaches the domain only through its contract;
    /// cache abstractions (interfaces only) are allowed.
    let clean =
        [ file "src/App.Api/App.Api.fsproj" (project [ "App.Contracts" ] [ "FSharp.Core"; "Microsoft.Extensions.Caching.Abstractions" ])
          file "src/App.Contracts/App.Contracts.fsproj" (project [] [ "FSharp.Core" ])
          file "src/App.Domain/App.Domain.fsproj" (project [ "App.Contracts" ] [ "Npgsql"; "AWSSDK.SecretsManager" ]) ]

    /// Known-violating: the API references the domain directly, a cache
    /// provider, a broker and a distributed lock; a central-package-managed
    /// global reference to an in-process cache; a commented-out broker and
    /// a PackageVersion that adds no dependency.
    let violating =
        [ file "src/App.Api/App.Api.csproj" (project [ "App.Contracts"; "App.Domain" ] [ "StackExchange.Redis"; "Confluent.Kafka" ])
          file "src/App.Contracts/App.Contracts.fsproj" (project [] [])
          file "src/App.Domain/App.Domain.fsproj" (project [] [ "RedLock.net"; "Microsoft.Extensions.Caching.Abstractions" ])
          file
              "Directory.Packages.props"
              """<Project>
  <ItemGroup>
    <!-- <PackageReference Include="RabbitMQ.Client" /> -->
    <PackageVersion Include="EasyNetQ" Version="7.0.0" />
    <GlobalPackageReference Include="Microsoft.Extensions.Caching.Memory" Version="8.0.0" />
  </ItemGroup>
</Project>"""
          file "src/App.Api/Program.fs" "module Program" ]

    let tenets ca cr =
        Map.ofList [ ContinuousAvailability, ca; CacheReadiness, cr; ReliabilityByConstruction, TenetApplicable ]

    let applicable = Declared("transactional-application", tenets TenetApplicable TenetApplicable)

    let boundary = BoundariesConfigured [ { Api = "App.Api"; Contracts = [ "App.Contracts" ] } ]

    let inputs declaration files boundaries exceptions : ArchitectureInputs =
        { Declaration = declaration
          ProjectFiles = files
          ApiBoundaries = boundaries
          Exceptions = { Valid = exceptions; Invalid = [] } }

    let run declaration files = ReliabilityArchitecture.check asOf (inputs declaration files boundary [])

    let rule id (c: ArchitectureCheck) = c.Rules |> List.find (fst >> (=) id) |> snd

    let findings id c =
        match rule id c with
        | RuleEvaluated f -> f
        | other -> failwith $"{id} was not evaluated: {other}"

    let scopes id c = findings id c |> List.map _.Scope

    let decisionException ruleId scope evidence : QualityException =
        { Id = "EXC-ARCH-1"
          RuleId = ruleId
          Scope = scope
          Rationale = "Session store needs a shared cache until the migration completes."
          Owner = "kemiller2002"
          Created = DateOnly(2026, 11, 1)
          Review = Expires(DateOnly(2027, 1, 31))
          Evidence = evidence
          AllowedValue = None }

    // --- DOK-ARCH-005 -------------------------------------------------------

    [<Fact>]
    let ``DOK-ARCH-005 reports cache providers in the violating fixture, including global references`` () =
        let c = run applicable violating
        Assert.Equal<string list>(
            [ "Directory.Packages.props#Microsoft.Extensions.Caching.Memory"; "src/App.Api/App.Api.csproj#StackExchange.Redis" ],
            scopes QualityRules.CacheDependencyBan c
        )
        let redis = findings QualityRules.CacheDependencyBan c |> List.find (fun f -> f.Subject = "StackExchange.Redis")
        Assert.True(redis.Line > 0)
        Assert.Equal(ArchitectureViolation, c.Verdict)
        Assert.Equal(4, ReliabilityArchitecture.exitCode c.Verdict)

    [<Fact>]
    let ``DOK-ARCH-005 passes the clean fixture and allows cache abstractions`` () =
        let c = run applicable clean
        Assert.Empty(findings QualityRules.CacheDependencyBan c)
        Assert.Equal(ArchitecturePass, c.Verdict)
        Assert.Equal(0, ReliabilityArchitecture.exitCode c.Verdict)

    [<Fact>]
    let ``package matching is exact or by family and ignores case`` () =
        let isCache p = ReliabilityArchitecture.cacheProviders |> List.exists (fun b -> ReliabilityArchitecture.matches p b.Matcher)
        Assert.True(isCache "stackexchange.redis")
        Assert.True(isCache "StackExchange.Redis.Extensions.Core")
        Assert.True(isCache "ZiggyCreatures.FusionCache.Serialization.SystemTextJson")
        Assert.False(isCache "StackExchange.RedisLike")
        Assert.False(isCache "Microsoft.Extensions.Caching.Abstractions")
        Assert.False(isCache "Microsoft.Extensions.Caching.Memory.Extra")

    // --- DOK-ARCH-006 -------------------------------------------------------

    [<Fact>]
    let ``DOK-ARCH-006 reports brokers and distributed locks but not comments or PackageVersion`` () =
        let c = run applicable violating
        Assert.Equal<string list>(
            [ "src/App.Api/App.Api.csproj#Confluent.Kafka"; "src/App.Domain/App.Domain.fsproj#RedLock.net" ],
            scopes QualityRules.InfrastructureDependencyBan c
        )

    [<Fact>]
    let ``DOK-ARCH-006 passes the clean fixture`` () =
        Assert.Empty(findings QualityRules.InfrastructureDependencyBan (run applicable clean))

    // --- DOK-ARCH-001 -------------------------------------------------------

    [<Fact>]
    let ``DOK-ARCH-001 reports an API reference to a non-contract project`` () =
        let c = run applicable violating
        Assert.Equal<string list>([ "App.Api->App.Domain" ], scopes QualityRules.ThinApiBoundary c)

    [<Fact>]
    let ``DOK-ARCH-001 passes the clean fixture`` () =
        Assert.Empty(findings QualityRules.ThinApiBoundary (run applicable clean))

    [<Fact>]
    let ``DOK-ARCH-001 without configured boundaries is not-configured, not a pass`` () =
        let c = ReliabilityArchitecture.check asOf (inputs applicable clean (BoundariesConfigured []) [])
        Assert.True(match rule QualityRules.ThinApiBoundary c with RuleNotConfigured _ -> true | _ -> false)

    [<Fact>]
    let ``DOK-ARCH-001 naming an API project that does not exist is unavailable`` () =
        let missing = BoundariesConfigured [ { Api = "Other.Api"; Contracts = [] } ]
        let c = ReliabilityArchitecture.check asOf (inputs applicable clean missing [])
        Assert.True(match rule QualityRules.ThinApiBoundary c with RuleUnavailable _ -> true | _ -> false)
        Assert.Equal(ArchitectureUnavailable, c.Verdict)

    [<Fact>]
    let ``an unreadable baseline makes only DOK-ARCH-001 unavailable`` () =
        let c = ReliabilityArchitecture.check asOf (inputs applicable clean (BoundariesUnreadable "malformed") [])
        Assert.True(match rule QualityRules.ThinApiBoundary c with RuleUnavailable _ -> true | _ -> false)
        Assert.Empty(findings QualityRules.CacheDependencyBan c)

    // --- activation ---------------------------------------------------------

    [<Fact>]
    let ``without a declaration every rule is not-applicable and the verdict is not a pass`` () =
        let c = run DeclarationAbsent violating
        Assert.True(c.Rules |> List.forall (fun (_, o) -> match o with RuleNotApplicable _ -> true | _ -> false))
        Assert.Equal(ArchitectureNotEvaluated, c.Verdict)
        Assert.Equal("not-evaluated", ReliabilityArchitecture.verdictTag c.Verdict)

    [<Fact>]
    let ``a not-applicable tenet deactivates only its rules`` () =
        let declaration = Declared("stateless-service", tenets TenetApplicable (TenetNotApplicable "No server-side queries."))
        let c = run declaration violating
        Assert.True(match rule QualityRules.CacheDependencyBan c with RuleNotApplicable r -> r.Contains "cacheReadiness" | _ -> false)
        Assert.NotEmpty(findings QualityRules.InfrastructureDependencyBan c)

    [<Fact>]
    let ``continuous availability not applicable deactivates DOK-ARCH-001 and DOK-ARCH-006`` () =
        let declaration = Declared("static-website", tenets (TenetNotApplicable "Static content.") TenetPartiallyApplicable)
        let c = run declaration violating
        Assert.True(match rule QualityRules.ThinApiBoundary c with RuleNotApplicable _ -> true | _ -> false)
        Assert.True(match rule QualityRules.InfrastructureDependencyBan c with RuleNotApplicable _ -> true | _ -> false)
        Assert.NotEmpty(findings QualityRules.CacheDependencyBan c)

    [<Fact>]
    let ``an invalid declaration is unavailable, never not-applicable`` () =
        let c = run (DeclarationInvalid "malformed JSON") clean
        Assert.True(c.Rules |> List.forall (fun (_, o) -> match o with RuleUnavailable _ -> true | _ -> false))
        Assert.Equal(3, ReliabilityArchitecture.exitCode c.Verdict)

    [<Fact>]
    let ``malformed project XML and an empty project set are unavailable`` () =
        let broken = run applicable [ file "src/A/A.fsproj" "<Project><ItemGroup>" ]
        Assert.Equal(ArchitectureUnavailable, broken.Verdict)
        let empty = run applicable [ file "package.json" "{}" ]
        Assert.True(match rule QualityRules.CacheDependencyBan empty with RuleUnavailable r -> r.Contains "no MSBuild project files" | _ -> false)

    // --- declaration decoding -----------------------------------------------

    let declaration (ca: string) =
        """{"schema":"echelon.reliability/v1","systemClass":"transactional-application","tenets":{"continuousAvailability":"""
        + ca
        + ""","cacheReadiness":{"applicability":"applicable"},"reliabilityByConstruction":{"applicability":"partially-applicable"}}}"""

    [<Fact>]
    let ``the declaration decodes applicability per tenet`` () =
        match ReliabilityArchitectureContract.readDeclaration (Some(declaration """{"applicability":"not-applicable","reason":"No accepted work."}""")) with
        | Declared (systemClass, t) ->
            Assert.Equal("transactional-application", systemClass)
            Assert.Equal(TenetNotApplicable "No accepted work.", t[ContinuousAvailability])
            Assert.Equal(TenetApplicable, t[CacheReadiness])
            Assert.Equal(TenetPartiallyApplicable, t[ReliabilityByConstruction])
        | other -> failwith $"unexpected {other}"

    [<Fact>]
    let ``declarations that break the contract are invalid`` () =
        let invalid text = match ReliabilityArchitectureContract.readDeclaration (Some text) with DeclarationInvalid _ -> true | _ -> false
        Assert.True(invalid (declaration """{"applicability":"not-applicable"}"""))
        Assert.True(invalid (declaration """{"applicability":"sometimes"}"""))
        Assert.True(invalid ((declaration """{"applicability":"applicable"}""").Replace("echelon.reliability/v1", "echelon.reliability/v2")))
        Assert.True(invalid "{")
        Assert.Equal(DeclarationAbsent, ReliabilityArchitectureContract.readDeclaration None)

    // --- exceptions ---------------------------------------------------------

    [<Fact>]
    let ``an exception naming a decision record accepts exactly its finding`` () =
        let e = decisionException QualityRules.CacheDependencyBan "src/App.Api/App.Api.csproj#StackExchange.Redis" [ "DF-APP-2026-A001 accepted cache decision" ]
        let c = ReliabilityArchitecture.check asOf (inputs applicable violating boundary [ e ])
        let redis = findings QualityRules.CacheDependencyBan c |> List.find (fun f -> f.Subject = "StackExchange.Redis")
        Assert.Equal(Accepted("EXC-ARCH-1", [ "DF-APP-2026-A001" ]), redis.Disposition)
        let memory = findings QualityRules.CacheDependencyBan c |> List.find (fun f -> f.Subject = "Microsoft.Extensions.Caching.Memory")
        Assert.Equal(Violation, memory.Disposition)
        Assert.Empty(c.UnusedExceptions)

    [<Fact>]
    let ``accepting every finding of the only active rule passes`` () =
        let e = decisionException QualityRules.InfrastructureDependencyBan "src/App.Domain/App.Domain.fsproj#RedLock.net" [ "ADR-EAR-004" ]
        let only = [ violating[2] ]
        let declaration = Declared("background-processing-service", tenets TenetApplicable (TenetNotApplicable "No queries."))
        let c = ReliabilityArchitecture.check asOf (inputs declaration only (BoundariesConfigured []) [ e ])
        Assert.Equal(ArchitecturePass, c.Verdict)

    [<Fact>]
    let ``an architecture exception without a decision record is invalid`` () =
        let e = decisionException QualityRules.CacheDependencyBan "src/App.Api/App.Api.csproj#StackExchange.Redis" [ "https://example.com/issue/1" ]
        let c = ReliabilityArchitecture.check asOf (inputs applicable violating boundary [ e ])
        Assert.Equal(ArchitectureInvalidExceptions, c.Verdict)
        Assert.Equal(6, ReliabilityArchitecture.exitCode c.Verdict)
        Assert.Equal(Violation, (findings QualityRules.CacheDependencyBan c |> List.find (fun f -> f.Subject = "StackExchange.Redis")).Disposition)

    [<Fact>]
    let ``an expired architecture exception is reported and accepts nothing`` () =
        let e =
            { decisionException QualityRules.CacheDependencyBan "src/App.Api/App.Api.csproj#StackExchange.Redis" [ "DF-APP-2026-A001" ] with
                Review = Expires(DateOnly(2026, 11, 2)) }
        let c = ReliabilityArchitecture.check asOf (inputs applicable violating boundary [ e ])
        Assert.Equal(ArchitectureInvalidExceptions, c.Verdict)
        Assert.Single(c.ExpiredExceptions) |> ignore

    // --- catalog and configuration ------------------------------------------

    [<Fact>]
    let ``architecture rules are in the catalog and outside the ratchet`` () =
        Assert.Equal<string list>([ "DOK-ARCH-001"; "DOK-ARCH-005"; "DOK-ARCH-006" ], QualityRules.architectureRuleIds)
        Assert.True(QualityRules.architectureRuleIds |> List.forall (fun id -> not (List.contains id QualityRules.ratchetRuleIds)))

    [<Fact>]
    let ``removing an API boundary or widening its contracts loosens the baseline`` () =
        let configuration =
            { Sources = [ "src" ]
              Generated = []
              LargeFileLines = 400
              ForbiddenReferences = []
              ApiBoundaries = [ { Api = "App.Api"; Contracts = [ "App.Contracts" ] } ] }
        let b c : RatchetBaseline = { SchemaVersion = "1.0.0"; Repository = "example"; AcceptedAt = asOf; Configuration = c; Rules = Map.empty }
        let widened = { configuration with ApiBoundaries = [ { Api = "App.Api"; Contracts = [ "App.Contracts"; "App.Domain" ] } ] }
        Assert.Equal<string list>([ "api boundary App.Api contract added: App.Domain" ], (QualityRatchet.diff (b configuration) (b widened)).ConfigurationLoosened)
        let removed = QualityRatchet.diff (b configuration) (b { configuration with ApiBoundaries = [] })
        Assert.Equal<string list>([ "api boundary removed: App.Api" ], removed.ConfigurationLoosened)
        Assert.Equal<string list>([ "api boundary added: App.Api" ], (QualityRatchet.diff (b { configuration with ApiBoundaries = [] }) (b configuration)).ConfigurationTightened)

    [<Fact>]
    let ``a baseline without API boundaries is written without the field, keeping its digest`` () =
        let configuration = { Sources = [ "src" ]; Generated = []; LargeFileLines = 400; ForbiddenReferences = []; ApiBoundaries = [] }
        let written = RatchetContract.writeBaseline { SchemaVersion = "1.0.0"; Repository = "example"; AcceptedAt = asOf; Configuration = configuration; Rules = Map.empty }
        Assert.DoesNotContain("apiBoundaries", written)
        let withBoundary = { configuration with ApiBoundaries = [ { Api = "App.Api"; Contracts = [ "App.Contracts" ] } ] }
        let roundTrip =
            RatchetContract.writeBaseline { SchemaVersion = "1.0.0"; Repository = "example"; AcceptedAt = asOf; Configuration = withBoundary; Rules = Map.empty }
            |> RatchetContract.readBaseline
        match roundTrip with
        | Ok b -> Assert.Equal<ApiBoundary list>(withBoundary.ApiBoundaries, b.Configuration.ApiBoundaries)
        | Error e -> failwith e
