namespace Dokimos.Core

type SourceAnalysis =
    { Path: string
      Structural: SourceMetrics
      Quality: FSharpQualityMetrics
      Complexity: ComplexityProxy
      Agent: AgentQualityIndicators
      Signals: EvidenceSignal list
      Correlations: CorrelatedEvidence list }

type RepositoryAnalysis =
    { Sources: SourceAnalysis list
      DuplicateBlocks: DuplicateBlock list }

module RepositoryAnalysis =
    let analyzeSource temporal path source =
        let structural = Structural.measure path source
        let quality = FSharpQuality.measure path source
        let complexity = Complexity.measure path source
        let agent = AgentQuality.fromMetrics structural quality

        let temporalSignals =
            match temporal with
            | None -> []
            | Some evidence ->
                [ if evidence.CommitCount >= 5 then FrequentChange evidence.CommitCount
                  if evidence.Churn >= 100 then HighChurn evidence.Churn ]

        let signals =
            [ if complexity.ProxyCyclomatic >= 10 then StructuralComplexity complexity.ProxyCyclomatic
              if agent.TypeWeakeningIndicators > 0 then TypeWeakening agent.TypeWeakeningIndicators
              if agent.UnresolvedScaffoldingIndicators > 0 then ActiveFindings agent.UnresolvedScaffoldingIndicators ]
            @ temporalSignals

        { Path = path
          Structural = structural
          Quality = quality
          Complexity = complexity
          Agent = agent
          Signals = signals
          Correlations = Correlation.evaluate path signals }

    let analyze duplicateBlockSize temporalByPath sources =
        let analyses =
            sources
            |> List.map (fun (path,source) ->
                let temporal = temporalByPath |> Map.tryFind path
                analyzeSource temporal path source)
        { Sources = analyses
          DuplicateBlocks = Duplication.blocks duplicateBlockSize sources }
