namespace Dokimos.Core

open System
open System.Xml
open System.Xml.Linq

/// The INIT-012 tenets a repository declares in `.echelon/reliability.json`
/// (Praxis RQ-ROS-2026-A038, schema `echelon.reliability/v1`).
type ReliabilityTenet =
    | ContinuousAvailability
    | CacheReadiness
    | ReliabilityByConstruction

type TenetApplicability =
    | TenetApplicable
    | TenetPartiallyApplicable
    | TenetNotApplicable of reason: string

/// What the repository declared. Absent and invalid are distinct states;
/// neither activates a rule and neither is a pass.
type ReliabilityDeclaration =
    | DeclarationAbsent
    | DeclarationInvalid of reason: string
    | Declared of systemClass: string * tenets: Map<ReliabilityTenet, TenetApplicability>

/// A banned package id: exactly that id, or a family (the id itself and any
/// id that starts with "<id>."). Comparison ignores case, as NuGet does.
type PackageMatcher =
    | ExactPackage of string
    | PackageFamily of string

type BannedPackage = { Matcher: PackageMatcher; Category: string }

/// One dependency edge read from a project file, with its line for evidence.
type DependencyEdge = { Target: string; Line: int }

type ProjectDependencies =
    { File: string
      Project: string
      Packages: DependencyEdge list
      ProjectReferences: DependencyEdge list }

/// DOK-ARCH-001 configuration from the baseline. A baseline that cannot be
/// read is distinct from one that configures no boundary.
type ApiBoundaryConfiguration =
    | BoundariesConfigured of ApiBoundary list
    | BoundariesUnreadable of reason: string

type ArchitectureDisposition =
    | Violation
    /// Accepted by a valid, unexpired exception that names decision records.
    | Accepted of exceptionId: string * decisionRecords: string list

type ArchitectureFinding =
    { RuleId: string
      Scope: string
      File: string
      Line: int
      Subject: string
      Detail: string
      Disposition: ArchitectureDisposition }

type ArchitectureRuleOutcome =
    | RuleNotApplicable of reason: string
    | RuleNotConfigured of reason: string
    | RuleUnavailable of reason: string
    | RuleEvaluated of ArchitectureFinding list

type ArchitectureVerdict =
    | ArchitecturePass
    | ArchitectureViolation
    | ArchitectureInvalidExceptions
    | ArchitectureUnavailable
    /// No rule was evaluated (every rule not applicable or not configured).
    /// Never a pass.
    | ArchitectureNotEvaluated

type ArchitectureInputs =
    { Declaration: ReliabilityDeclaration
      ProjectFiles: SourceFile list
      ApiBoundaries: ApiBoundaryConfiguration
      Exceptions: ExceptionSet }

type ArchitectureCheck =
    { Verdict: ArchitectureVerdict
      Reasons: string list
      CheckedAt: DateTimeOffset
      Declaration: ReliabilityDeclaration
      Rules: (string * ArchitectureRuleOutcome) list
      ProjectFiles: string list
      ActiveExceptions: QualityException list
      ExpiredExceptions: QualityException list
      InvalidExceptions: InvalidException list
      UnusedExceptions: string list }

/// RQ-DOKIMOS-ARCH-001: objectively verifiable architectural rules
/// (DOK-ARCH-001, -005, -006). Pure: parsed project files, the reliability
/// declaration, configuration and exceptions in; typed outcomes out.
module ReliabilityArchitecture =
    /// Cache providers and distributed-cache packages (DOK-ARCH-005).
    /// Microsoft.Extensions.Caching.Abstractions is deliberately absent: it
    /// carries only the IMemoryCache/IDistributedCache interfaces, which is
    /// what "cache-ready" code depends on.
    let cacheProviders: BannedPackage list =
        [ PackageFamily "StackExchange.Redis", "redis client"
          ExactPackage "Microsoft.Extensions.Caching.StackExchangeRedis", "distributed cache (redis)"
          ExactPackage "Microsoft.Extensions.Caching.Redis", "distributed cache (redis)"
          ExactPackage "Microsoft.Extensions.Caching.Memory", "in-process cache"
          ExactPackage "Microsoft.Extensions.Caching.SqlServer", "distributed cache (sql server)"
          ExactPackage "Microsoft.Extensions.Caching.Cosmos", "distributed cache (cosmos db)"
          ExactPackage "Microsoft.Extensions.Caching.Hybrid", "hybrid cache"
          ExactPackage "Microsoft.AspNetCore.OutputCaching.StackExchangeRedis", "output cache (redis)"
          PackageFamily "Alachisoft.NCache", "ncache"
          PackageFamily "NCache", "ncache"
          PackageFamily "EasyCaching", "easycaching"
          PackageFamily "ZiggyCreatures.FusionCache", "fusioncache"
          PackageFamily "LazyCache", "lazycache"
          PackageFamily "CacheManager", "cachemanager"
          PackageFamily "EnyimMemcachedCore", "memcached client"
          PackageFamily "Enyim.Caching", "memcached client" ]
        |> List.map (fun (m, c) -> { Matcher = m; Category = c })

    /// Message-broker and distributed-lock clients (DOK-ARCH-006).
    let infrastructureClients: BannedPackage list =
        [ PackageFamily "Confluent.Kafka", "broker (kafka)"
          PackageFamily "Confluent.SchemaRegistry", "broker (kafka)"
          PackageFamily "RabbitMQ.Client", "broker (rabbitmq)"
          PackageFamily "RabbitMQ.Stream.Client", "broker (rabbitmq)"
          PackageFamily "EasyNetQ", "broker (rabbitmq)"
          PackageFamily "MassTransit.RabbitMQ", "broker (rabbitmq)"
          PackageFamily "MassTransit.Kafka", "broker (kafka)"
          PackageFamily "MassTransit.ActiveMQ", "broker (activemq)"
          PackageFamily "MassTransit.AmazonSQS", "broker (sqs)"
          PackageFamily "MassTransit.Azure.ServiceBus.Core", "broker (azure service bus)"
          PackageFamily "NServiceBus.RabbitMQ", "broker (rabbitmq)"
          PackageFamily "NServiceBus.Transport.RabbitMQ", "broker (rabbitmq)"
          PackageFamily "Rebus.RabbitMq", "broker (rabbitmq)"
          PackageFamily "Apache.NMS.ActiveMQ", "broker (activemq)"
          PackageFamily "Apache.NMS.AMQP", "broker (amqp)"
          PackageFamily "NATS.Client", "broker (nats)"
          PackageFamily "NATS.Net", "broker (nats)"
          PackageFamily "Azure.Messaging.ServiceBus", "broker (azure service bus)"
          PackageFamily "Microsoft.Azure.ServiceBus", "broker (azure service bus)"
          PackageFamily "Azure.Messaging.EventHubs", "broker (azure event hubs)"
          ExactPackage "AWSSDK.SQS", "broker (sqs)"
          PackageFamily "Google.Cloud.PubSub.V1", "broker (google pub/sub)"
          PackageFamily "RedLock.net", "distributed lock (redis)"
          ExactPackage "DistributedLock", "distributed lock"
          ExactPackage "DistributedLock.Redis", "distributed lock (redis)"
          ExactPackage "DistributedLock.ZooKeeper", "distributed lock (zookeeper)"
          ExactPackage "DistributedLock.Azure", "distributed lock (azure blob)"
          ExactPackage "DistributedLock.SqlServer", "distributed lock (sql server)"
          ExactPackage "DistributedLock.Postgres", "distributed lock (postgres)"
          ExactPackage "DistributedLock.MySql", "distributed lock (mysql)"
          ExactPackage "DistributedLock.Oracle", "distributed lock (oracle)"
          ExactPackage "DistributedLock.MongoDB", "distributed lock (mongodb)" ]
        |> List.map (fun (m, c) -> { Matcher = m; Category = c })

    /// Which tenet activates each implemented rule.
    let tenetOf ruleId =
        match ruleId with
        | QualityRules.CacheDependencyBan -> Some CacheReadiness
        | QualityRules.InfrastructureDependencyBan
        | QualityRules.ThinApiBoundary -> Some ContinuousAvailability
        | _ -> None

    let tenetName =
        function
        | ContinuousAvailability -> "continuousAvailability"
        | CacheReadiness -> "cacheReadiness"
        | ReliabilityByConstruction -> "reliabilityByConstruction"

    let private same (a: string) (b: string) = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    let matches (package: string) =
        function
        | ExactPackage id -> same package id
        | PackageFamily id -> same package id || package.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)

    let isProjectFile (path: string) =
        let name = path.Split('/') |> Array.last
        [ ".fsproj"; ".csproj"; ".vbproj" ] |> List.exists (fun ext -> name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        || [ "Directory.Build.props"; "Directory.Build.targets"; "Directory.Packages.props" ] |> List.contains name

    let private projectName (path: string) =
        let name = path.Replace('\\', '/').Split('/') |> Array.last
        match name.LastIndexOf '.' with
        | -1 -> name
        | dot -> name.Substring(0, dot)

    let private lineOf (x: XElement) =
        match box x with
        | :? IXmlLineInfo as info when info.HasLineInfo() -> info.LineNumber
        | _ -> 0

    let private edges (document: XDocument) (elementNames: string list) (target: string -> string) =
        document.Descendants()
        |> Seq.filter (fun e -> List.contains e.Name.LocalName elementNames)
        |> Seq.collect (fun e ->
            match e.Attribute(XName.Get "Include") with
            | null -> Seq.empty
            | attribute ->
                attribute.Value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                |> Seq.map (fun value -> { Target = target value; Line = lineOf e }))
        |> List.ofSeq

    /// Reads PackageReference/GlobalPackageReference and ProjectReference
    /// elements. XML comments are not elements, so commented-out references
    /// are not dependencies. `Update=` and `PackageVersion` add no dependency.
    let parseProject (file: SourceFile) : Result<ProjectDependencies, string> =
        try
            let document = XDocument.Parse(file.Content, LoadOptions.SetLineInfo)
            Ok
                { File = file.Path
                  Project = projectName file.Path
                  Packages = edges document [ "PackageReference"; "GlobalPackageReference" ] id
                  ProjectReferences = edges document [ "ProjectReference" ] projectName }
        with :? XmlException as e ->
            Error $"{file.Path} is not well-formed MSBuild XML: {e.Message}"

    let private banFindings ruleId (bans: BannedPackage list) (projects: ProjectDependencies list) =
        [ for p in projects do
              for edge in p.Packages do
                  match bans |> List.tryFind (fun b -> matches edge.Target b.Matcher) with
                  | Some ban ->
                      yield
                          { RuleId = ruleId
                            Scope = $"{p.File}#{edge.Target}"
                            File = p.File
                            Line = edge.Line
                            Subject = edge.Target
                            Detail = $"{p.File}:{edge.Line} references {edge.Target} ({ban.Category})"
                            Disposition = Violation }
                  | None -> () ]

    let private thinApiOutcome (boundaries: ApiBoundary list) (projects: ProjectDependencies list) =
        let missing =
            boundaries |> List.filter (fun b -> not (projects |> List.exists (fun p -> p.Project = b.Api))) |> List.map _.Api
        if not missing.IsEmpty then
            RuleUnavailable("configured API project(s) not found among the analyzed project files: " + String.Join(", ", missing))
        else
            RuleEvaluated
                [ for b in boundaries do
                      for p in projects |> List.filter (fun p -> p.Project = b.Api) do
                          for edge in p.ProjectReferences do
                              if not (List.contains edge.Target b.Contracts) then
                                  yield
                                      { RuleId = QualityRules.ThinApiBoundary
                                        Scope = $"{b.Api}->{edge.Target}"
                                        File = p.File
                                        Line = edge.Line
                                        Subject = edge.Target
                                        Detail = $"{p.File}:{edge.Line} API project {b.Api} references {edge.Target}, which is not a declared contract"
                                        Disposition = Violation } ]

    let private evaluateRule (inputs: ArchitectureInputs) (projects: Result<ProjectDependencies list, string list>) ruleId =
        match projects with
        | Error problems -> RuleUnavailable("project files could not be read: " + String.Join("; ", problems))
        | Ok [] -> RuleUnavailable "no MSBuild project files were found; dependencies of other ecosystems are not analyzed"
        | Ok parsed ->
            match ruleId with
            | QualityRules.CacheDependencyBan -> RuleEvaluated(banFindings ruleId cacheProviders parsed)
            | QualityRules.InfrastructureDependencyBan -> RuleEvaluated(banFindings ruleId infrastructureClients parsed)
            | QualityRules.ThinApiBoundary ->
                match inputs.ApiBoundaries with
                | BoundariesUnreadable reason -> RuleUnavailable $"the API boundary configuration could not be read: {reason}"
                | BoundariesConfigured [] ->
                    RuleNotConfigured "no configuration.apiBoundaries in quality/baseline.json names an API project and its contracts"
                | BoundariesConfigured boundaries -> thinApiOutcome boundaries parsed
            | other -> RuleUnavailable $"{other} has no analyzer"

    /// Activation by the declaration first; detection only for active rules.
    let outcome (inputs: ArchitectureInputs) projects ruleId =
        match inputs.Declaration, tenetOf ruleId with
        | _, None -> RuleUnavailable $"{ruleId} is not an implemented architecture rule"
        | DeclarationAbsent, Some _ -> RuleNotApplicable ".echelon/reliability.json is absent; no reliability tenet is declared"
        | DeclarationInvalid reason, Some _ -> RuleUnavailable $"the reliability declaration is invalid: {reason}"
        | Declared (_, tenets), Some tenet ->
            match tenets |> Map.tryFind tenet with
            | Some (TenetNotApplicable reason) -> RuleNotApplicable $"{tenetName tenet} is declared not-applicable: {reason}"
            | Some TenetApplicable
            | Some TenetPartiallyApplicable -> evaluateRule inputs projects ruleId
            | None -> RuleUnavailable $"the reliability declaration does not state {tenetName tenet}"

    let private accept (active: QualityException list) (finding: ArchitectureFinding) =
        match active |> List.tryFind (fun e -> e.RuleId = finding.RuleId && e.Scope = finding.Scope) with
        | Some e -> { finding with Disposition = Accepted(e.Id, QualityRules.decisionRecordsIn e.Evidence) }
        | None -> finding

    let private parseAll (files: SourceFile list) =
        let results = files |> List.filter (fun f -> isProjectFile f.Path) |> List.sortBy _.Path |> List.map parseProject
        match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
        | [] -> Ok(results |> List.choose Result.toOption)
        | problems -> Error problems

    let check (asOf: DateTimeOffset) (inputs: ArchitectureInputs) : ArchitectureCheck =
        let classified = QualityRatchet.classify asOf inputs.Exceptions
        let architectural = classified.Valid |> List.filter (fun e -> List.contains e.RuleId QualityRules.architectureRuleIds)
        let active, expired = architectural |> List.partition (fun e -> QualityRatchet.statusAt asOf e = ExceptionActive)
        let projects = parseAll inputs.ProjectFiles
        let rules =
            [ QualityRules.ThinApiBoundary; QualityRules.CacheDependencyBan; QualityRules.InfrastructureDependencyBan ]
            |> List.map (fun id ->
                id,
                match outcome inputs projects id with
                | RuleEvaluated findings -> RuleEvaluated(findings |> List.map (accept active) |> List.sortBy (fun f -> f.Scope))
                | other -> other)
        let findings = rules |> List.collect (function _, RuleEvaluated f -> f | _ -> [])
        let used = findings |> List.choose (fun f -> match f.Disposition with Accepted (id, _) -> Some id | Violation -> None) |> Set.ofList
        let unavailable = rules |> List.choose (function id, RuleUnavailable r -> Some $"{id}: {r}" | _ -> None)
        let invalid =
            (classified.Invalid |> List.map (fun i -> "exception " + i.Id + " is invalid: " + String.Join("; ", i.Problems)))
            @ (expired |> List.map (fun e -> $"exception {e.Id} has expired"))
        let violations = findings |> List.filter (fun f -> f.Disposition = Violation)
        let verdict, reasons =
            if not unavailable.IsEmpty then ArchitectureUnavailable, unavailable
            elif not invalid.IsEmpty then ArchitectureInvalidExceptions, invalid
            elif not violations.IsEmpty then ArchitectureViolation, violations |> List.map (fun f -> $"{f.RuleId} {f.Scope}")
            elif rules |> List.exists (function _, RuleEvaluated _ -> true | _ -> false) then ArchitecturePass, []
            else ArchitectureNotEvaluated, [ "no architecture rule is active for this repository; not-applicable is not a pass" ]
        { Verdict = verdict
          Reasons = reasons
          CheckedAt = asOf
          Declaration = inputs.Declaration
          Rules = rules
          ProjectFiles = inputs.ProjectFiles |> List.map _.Path |> List.filter isProjectFile |> List.sort
          ActiveExceptions = active
          ExpiredExceptions = expired
          InvalidExceptions = classified.Invalid
          UnusedExceptions = active |> List.map _.Id |> List.filter (fun id -> not (used.Contains id)) }

    let exitCode =
        function
        | ArchitecturePass
        | ArchitectureNotEvaluated -> ExitCodes.Continue
        | ArchitectureViolation -> ExitCodes.PolicyFailure
        | ArchitectureUnavailable -> ExitCodes.EvidenceUnavailable
        | ArchitectureInvalidExceptions -> ExitCodesRatchet.InvalidExceptions

    let verdictTag =
        function
        | ArchitecturePass -> "pass"
        | ArchitectureViolation -> "violation"
        | ArchitectureInvalidExceptions -> "invalid-exceptions"
        | ArchitectureUnavailable -> "unavailable"
        | ArchitectureNotEvaluated -> "not-evaluated"
