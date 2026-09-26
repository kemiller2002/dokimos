namespace Dokimos.Core

type SignalDto =
    { Kind: string
      Value: int option }

type CorrelationDto =
    { Path: string
      Kind: string
      Signals: SignalDto list
      Explanation: string }

type SourceAnalysisDto =
    { Path: string
      Structural: SourceMetrics
      Quality: FSharpQualityMetrics
      Complexity: ComplexityProxy
      Agent: AgentQualityIndicators
      Signals: SignalDto list
      Correlations: CorrelationDto list }

type RepositoryAnalysisDto =
    { SchemaVersion: string
      Sources: SourceAnalysisDto list
      DuplicateBlocks: DuplicateBlock list }

module Wire =
    let signal = function
        | StructuralComplexity value -> { Kind="structural-complexity"; Value=Some value }
        | FrequentChange value -> { Kind="frequent-change"; Value=Some value }
        | HighChurn value -> { Kind="high-churn"; Value=Some value }
        | RepeatedRegion value -> { Kind="repeated-region"; Value=Some value }
        | ActiveFindings value -> { Kind="active-findings"; Value=Some value }
        | AgedDebt value -> { Kind="aged-debt"; Value=Some value }
        | ApiInstability value -> { Kind="api-instability"; Value=Some value }
        | DependencyAdditions value -> { Kind="dependency-additions"; Value=Some value }
        | MissingTestChange -> { Kind="missing-test-change"; Value=None }
        | TypeWeakening value -> { Kind="type-weakening"; Value=Some value }

    let correlationKind = function
        | MaintainabilityHotspot -> "maintainability-hotspot"
        | UnstablePublicSurface -> "unstable-public-surface"
        | UntestedHighChange -> "untested-high-change"
        | AgentGeneratedRiskPattern -> "agent-generated-risk-pattern"
        | PersistentDebtHotspot -> "persistent-debt-hotspot"

    let correlation (value: CorrelatedEvidence) =
        { Path=value.Path
          Kind=correlationKind value.Kind
          Signals=value.Signals |> List.map signal
          Explanation=value.Explanation }

    let source (value: SourceAnalysis) =
        { Path=value.Path
          Structural=value.Structural
          Quality=value.Quality
          Complexity=value.Complexity
          Agent=value.Agent
          Signals=value.Signals |> List.map signal
          Correlations=value.Correlations |> List.map correlation }

    let repository (value: RepositoryAnalysis) =
        { SchemaVersion="1.0.0"
          Sources=value.Sources |> List.map source
          DuplicateBlocks=value.DuplicateBlocks }
