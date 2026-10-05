namespace Dokimos.Core

/// Where a rule's measurements live.
type RuleScopeKind =
    | RepositoryScoped
    | FileScoped

/// How a rule's measured value is judged against the accepted baseline.
type RuleSemantics =
    /// A count that must not grow in any scope; absence means zero.
    | CountMustNotGrow
    /// A size signal: only scopes over the configured line threshold are
    /// recorded, and an over-threshold scope must not grow.
    | SizeOverThresholdMustNotGrow

/// Which engine consumes the rule. Ratchet rules compare a measurement with
/// the accepted ratchet baseline; gate rules are judged by `dokimos evaluate`.
type RuleEngine =
    | RatchetEngine of RuleSemantics
    | GateEngine

type QualityRule =
    { Id: string
      Name: string
      Engine: RuleEngine
      ScopeKind: RuleScopeKind
      Signal: string
      Description: string
      Rationale: string
      Remediation: string }

/// The stable rule catalog. Rule IDs are contract: they never change meaning
/// and are never reused. Documented in docs/quality/RATCHET-RULES.md.
module QualityRules =
    [<Literal>]
    let CompilerWarnings = "DOK-R001"

    [<Literal>]
    let Suppressions = "DOK-R002"

    [<Literal>]
    let SkippedTests = "DOK-R003"

    [<Literal>]
    let DebtMarkers = "DOK-R004"

    [<Literal>]
    let LargeFileGrowth = "DOK-R005"

    [<Literal>]
    let BroadCatches = "DOK-R006"

    [<Literal>]
    let PackageReferences = "DOK-R007"

    [<Literal>]
    let ArchitectureViolations = "DOK-R008"

    [<Literal>]
    let IntroducedFinding = "DOK-G001"

    [<Literal>]
    let ExceptionsPath = "quality/exceptions.json"

    [<Literal>]
    let BaselinePath = "quality/baseline.json"

    [<Literal>]
    let CatalogDocument = "docs/quality/RATCHET-RULES.md"

    let all: QualityRule list =
        [ { Id = CompilerWarnings
            Name = "compiler-warnings"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = RepositoryScoped
            Signal = "MSBuild 'N Warning(s)' summary of the supplied build log (--build-log)."
            Description = "The number of compiler warnings must not exceed the accepted baseline."
            Rationale = "Warnings are deferred defects; a growing count hides new ones among old ones."
            Remediation = "Fix the new warnings. Do not suppress them; suppressions are DOK-R002." }
          { Id = Suppressions
            Name = "warning-suppressions"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = FileScoped
            Signal = "F# '#nowarn', '#pragma warning disable' and [<SuppressMessage>] lines; project-file <NoWarn>, <WarningsNotAsErrors>, NoWarn attributes and TreatWarningsAsErrors=false."
            Description = "Warning suppressions per file must not exceed the accepted baseline."
            Rationale = "A suppression silences evidence for every future change to that file or project."
            Remediation = "Remove the suppression and fix the underlying warning, or record a scoped exception explaining why the warning is a false positive." }
          { Id = SkippedTests
            Name = "skipped-tests"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = FileScoped
            Signal = "Test skips: Skip = \"...\" attribute arguments, [<Ignore>]/[<Explicit>], Expecto ptest*, Skip.If/Skip.IfNot and Assert.Skip."
            Description = "Skipped or ignored tests per file must not exceed the accepted baseline."
            Rationale = "A skipped test reports success while verifying nothing."
            Remediation = "Make the test pass or delete it with a recorded reason; quarantine only through a dated exception." }
          { Id = DebtMarkers
            Name = "debt-markers"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = FileScoped
            Signal = "Comment lines containing the whole words TODO, FIXME or HACK (string literals are not comments)."
            Description = "Unresolved debt markers per file must not exceed the accepted baseline."
            Rationale = "Markers record known-incomplete work; growth means work is being declared done while incomplete."
            Remediation = "Finish the work, or track it in the work queue and remove the marker." }
          { Id = LargeFileGrowth
            Name = "large-file-growth"
            Engine = RatchetEngine SizeOverThresholdMustNotGrow
            ScopeKind = FileScoped
            Signal = "Line count of handwritten F# files over the baseline's largeFileLines threshold."
            Description = "A file over the size threshold must not grow, and no file may newly cross it."
            Rationale = "Growth of an already-large file concentrates responsibility; size is a review signal, not a semantic verdict."
            Remediation = "Extract a cohesive responsibility into its own module before adding to the file, or record an exception explaining why the file is cohesive." }
          { Id = BroadCatches
            Name = "broad-exception-catches"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = FileScoped
            Signal = "try/with handlers whose pattern catches everything (_, an identifier, :? exn or :? Exception) and either is a wildcard or never uses the caught exception."
            Description = "Broad, discarding exception handlers per file must not exceed the accepted baseline."
            Rationale = "A catch-all that discards the exception turns failures into silent defaults."
            Remediation = "Catch the specific exception types expected at the boundary and return a typed error, or let unexpected failures propagate to the fault boundary." }
          { Id = PackageReferences
            Name = "package-references"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = FileScoped
            Signal = "<PackageReference Include=...> elements per project file (.fsproj, Directory.Build.props, Directory.Packages.props)."
            Description = "Package dependencies per project file must not exceed the accepted baseline."
            Rationale = "Each dependency adds supply-chain, upgrade and boundary cost; additions should be deliberate."
            Remediation = "Remove the dependency, or record an exception with the dependency's justification and owner." }
          { Id = ArchitectureViolations
            Name = "architecture-violations"
            Engine = RatchetEngine CountMustNotGrow
            ScopeKind = RepositoryScoped
            Signal = "<ProjectReference> edges that the baseline's declared forbiddenReferences layer map prohibits."
            Description = "Forbidden project-reference edges must not exceed the accepted baseline."
            Rationale = "Dependency direction is the architecture; one forbidden edge invites more."
            Remediation = "Remove the forbidden reference by moving the shared concept to the lower layer or inverting the dependency." }
          { Id = IntroducedFinding
            Name = "introduced-finding"
            Engine = GateEngine
            ScopeKind = FileScoped
            Signal = "A correlation finding introduced since the baseline snapshot (dokimos evaluate introducedFindings)."
            Description = "Introduced findings are judged by the policy's introducedFindings disposition; an exception's scope is the finding id."
            Rationale = "Replaces policy-embedded suppressions so every waiver uses one validated mechanism."
            Remediation = "Reduce the contributing signals so the finding resolves." } ]

    let tryFind id = all |> List.tryFind (fun r -> r.Id = id)

    let ratchetRules =
        all |> List.filter (fun r -> match r.Engine with RatchetEngine _ -> true | GateEngine -> false)

    let ratchetRuleIds = ratchetRules |> List.map _.Id

    let semanticsOf id =
        tryFind id |> Option.bind (fun r -> match r.Engine with RatchetEngine s -> Some s | GateEngine -> None)

    let exceptionProcess (rule: QualityRule) =
        $"To accept this specific regression, add an entry to {ExceptionsPath} with id, ruleId \"{rule.Id}\", the exact scope, rationale, owner, created, expires (or reviewCondition), evidence and allowedValue, and have it reviewed in the pull request. An exception never changes the baseline. See {CatalogDocument}#exceptions."
