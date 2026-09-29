namespace Dokimos.Core

open System.Reflection

/// How far an analyzer's output can be reproduced from the same inputs.
type Determinism =
    | Deterministic
    | DeterministicGivenInputs of inputs: string

/// A declared analyzer: what it measures, where, and with what known limits.
/// Metrics an analyzer does not declare are never emitted by it, so a metric
/// with no declaring analyzer is represented as unavailable, never as zero.
type AnalyzerCapability =
    { AnalyzerId: string
      Version: string
      Language: string
      Metrics: string list
      Scopes: string list
      Limitations: string list
      RequiredTools: string list
      RequiredInputs: string list
      Determinism: Determinism }

type CanonicalMetricDefinition =
    { MetricId: string
      Version: int
      Unit: string
      Preference: MetricPreference
      Scopes: string list
      Analyzer: string option }

module DokimosInfo =
    type private Marker = class end

    /// The Dokimos release that produced evidence. Stamped by the build from
    /// Directory.Build.props (and SourceRevisionId when supplied).
    let version =
        let assembly = typeof<Marker>.Assembly
        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null ->
            match assembly.GetName().Version with
            | null -> "unversioned"
            | v -> v.ToString()
        | attribute -> attribute.InformationalVersion

module Capabilities =
    [<Literal>]
    let Structural = "dokimos.fsharp-structural"

    [<Literal>]
    let Complexity = "dokimos.fsharp-complexity-proxy"

    [<Literal>]
    let AgentQuality = "dokimos.fsharp-agent-quality"

    [<Literal>]
    let Duplication = "dokimos.text-duplication"

    [<Literal>]
    let GitTemporal = "dokimos.git-temporal"

    [<Literal>]
    let DotnetBuild = "dokimos.dotnet-build-diagnostics"

    [<Literal>]
    let DotnetTest = "dokimos.dotnet-test-results"

    [<Literal>]
    let Coverage = "dokimos.cobertura-coverage"

    [<Literal>]
    let Correlation = "dokimos.hotspot-correlation"

    let analyzers =
        [ { AnalyzerId = Structural
            Version = "1"
            Language = "fsharp"
            Metrics = [ "source.lines"; "source.nonblank-lines"; "source.public-declarations"; "source.mutable-bindings"; "source.broad-catch-indicators" ]
            Scopes = [ "file" ]
            Limitations = [ "Line-oriented heuristics, not a parser: declarations and wildcard arms are recognised by leading tokens."; "Only *.fs files are analyzed; *.fsi, *.fsx and non-F# sources are out of scope." ]
            RequiredTools = []
            RequiredInputs = [ "source directory" ]
            Determinism = Deterministic }
          { AnalyzerId = Complexity
            Version = "1"
            Language = "fsharp"
            Metrics = [ "complexity.proxy-cyclomatic" ]
            Scopes = [ "file" ]
            Limitations = [ "A line-based proxy (1 + decisions + boolean operators + match arms); not comparable with other tools' cyclomatic complexity." ]
            RequiredTools = []
            RequiredInputs = [ "source directory" ]
            Determinism = Deterministic }
          { AnalyzerId = AgentQuality
            Version = "1"
            Language = "fsharp"
            Metrics = [ "quality.type-weakening-indicators"; "quality.scaffolding-indicators" ]
            Scopes = [ "file" ]
            Limitations = [ "Counts textual indicators (obj-typed declarations, TODO/FIXME outside string literals); indicators are places to look, not defects." ]
            RequiredTools = []
            RequiredInputs = [ "source directory" ]
            Determinism = Deterministic }
          { AnalyzerId = Duplication
            Version = "1"
            Language = "fsharp"
            Metrics = [ "duplication.block-occurrences" ]
            Scopes = [ "repository" ]
            Limitations = [ "Exact matches of normalised six-line blocks; renamed or reordered duplication is not detected." ]
            RequiredTools = []
            RequiredInputs = [ "source directory" ]
            Determinism = Deterministic }
          { AnalyzerId = GitTemporal
            Version = "1"
            Language = "any"
            Metrics = [ "change.file-commits"; "change.file-churn" ]
            Scopes = [ "file" ]
            Limitations = [ "Depends on the fetched history depth; a shallow clone under-reports change."; "Rename chains are not followed: history before a rename is attributed to the old path." ]
            RequiredTools = [ "git" ]
            RequiredInputs = [ "git log --numstat --find-renames --format='commit %H %aI' output" ]
            Determinism = DeterministicGivenInputs "the same commit graph and history depth" }
          { AnalyzerId = DotnetBuild
            Version = "1"
            Language = "dotnet"
            Metrics = [ "build.compiler-errors"; "build.compiler-warnings" ]
            Scopes = [ "repository" ]
            Limitations = [ "Reads the MSBuild summary ('N Warning(s)', 'N Error(s)') from a captured build log; a log without that summary yields failed collection, not zero." ]
            RequiredTools = [ "dotnet" ]
            RequiredInputs = [ "dotnet build console log" ]
            Determinism = DeterministicGivenInputs "the same build log" }
          { AnalyzerId = DotnetTest
            Version = "1"
            Language = "dotnet"
            Metrics = [ "tests.total"; "tests.passed"; "tests.failed"; "tests.skipped" ]
            Scopes = [ "repository" ]
            Limitations = [ "Sums the Counters element of every supplied TRX file; tests that never ran are not counted." ]
            RequiredTools = [ "dotnet test --logger trx" ]
            RequiredInputs = [ "TRX result files" ]
            Determinism = DeterministicGivenInputs "the same TRX files" }
          { AnalyzerId = Coverage
            Version = "1"
            Language = "dotnet"
            Metrics = [ "coverage.line-rate" ]
            Scopes = [ "repository" ]
            Limitations = [ "Only reads the root line-rate of a supplied Cobertura report; unavailable unless coverage is explicitly supplied." ]
            RequiredTools = [ "a Cobertura-producing coverage collector" ]
            RequiredInputs = [ "Cobertura XML report" ]
            Determinism = DeterministicGivenInputs "the same coverage report" }
          { AnalyzerId = Correlation
            Version = "1"
            Language = "any"
            Metrics = []
            Scopes = [ "file" ]
            Limitations = [ "Emits findings only when independent signals coincide; each finding lists the contributing signals and their values."; "Temporal signals require the git-temporal analyzer; without it hotspot findings are unavailable, not absent." ]
            RequiredTools = []
            RequiredInputs = [ "structural analyzers"; "git-temporal analyzer" ]
            Determinism = DeterministicGivenInputs "the same structural and temporal evidence" } ]

    let private definition metricId unit preference scopes analyzer =
        { MetricId = metricId; Version = 1; Unit = unit; Preference = preference; Scopes = scopes; Analyzer = analyzer }

    /// Metric definitions Dokimos interprets. Preference drives comparison
    /// direction; contextual metrics change but never improve or deteriorate.
    let metrics =
        [ definition "build.compiler-errors" "count" PreferLower [ "repository" ] (Some DotnetBuild)
          definition "build.compiler-warnings" "count" PreferLower [ "repository" ] (Some DotnetBuild)
          definition "tests.total" "count" Contextual [ "repository" ] (Some DotnetTest)
          definition "tests.passed" "count" Contextual [ "repository" ] (Some DotnetTest)
          definition "tests.failed" "count" PreferLower [ "repository" ] (Some DotnetTest)
          definition "tests.skipped" "count" Contextual [ "repository" ] (Some DotnetTest)
          definition "coverage.line-rate" "ratio" PreferHigher [ "repository" ] (Some Coverage)
          definition "change.file-commits" "count" Contextual [ "file" ] (Some GitTemporal)
          definition "change.file-churn" "lines" Contextual [ "file" ] (Some GitTemporal)
          definition "change.region-commits" "count" Contextual [ "region" ] None
          definition "source.lines" "lines" Contextual [ "file" ] (Some Structural)
          definition "source.nonblank-lines" "lines" Contextual [ "file" ] (Some Structural)
          definition "source.public-declarations" "count" Contextual [ "file" ] (Some Structural)
          definition "source.mutable-bindings" "count" PreferLower [ "file" ] (Some Structural)
          definition "source.broad-catch-indicators" "count" Contextual [ "file" ] (Some Structural)
          definition "complexity.proxy-cyclomatic" "points" Contextual [ "file" ] (Some Complexity)
          definition "quality.type-weakening-indicators" "count" PreferLower [ "file" ] (Some AgentQuality)
          definition "quality.scaffolding-indicators" "count" PreferLower [ "file" ] (Some AgentQuality)
          definition "duplication.block-occurrences" "count" Contextual [ "repository" ] (Some Duplication)
          definition "debt.age-days" "days" Contextual [ "file"; "region" ] None
          definition "debt.staleness-days" "days" Contextual [ "file"; "region" ] None
          definition "api.added" "symbols" Contextual [ "project"; "repository" ] None
          definition "api.removed" "symbols" Contextual [ "project"; "repository" ] None
          definition "api.signature-changed" "symbols" Contextual [ "project"; "repository" ] None ]

    let tryDefinition metricId = metrics |> List.tryFind (fun d -> d.MetricId = metricId)

    /// Catalogued metrics that no analyzer in this release collects.
    let unsupportedMetrics = metrics |> List.filter (fun d -> d.Analyzer.IsNone)

    let tryAnalyzer analyzerId = analyzers |> List.tryFind (fun a -> a.AnalyzerId = analyzerId)

    /// Analyzers whose evidence a finding kind depends on. If any did not run,
    /// the finding's absence is unknown rather than a resolution.
    let requiredForFindings = [ Structural; Complexity; AgentQuality; GitTemporal; Correlation ]
