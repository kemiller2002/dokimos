namespace Dokimos.Core

type EvidenceSignal =
    | StructuralComplexity of value: int
    | FrequentChange of commits: int
    | HighChurn of lines: int
    | RepeatedRegion of commits: int
    | ActiveFindings of count: int
    | AgedDebt of days: int
    | ApiInstability of changes: int
    | DependencyAdditions of count: int
    | MissingTestChange
    | TypeWeakening of count: int

type CorrelationKind =
    | MaintainabilityHotspot
    | UnstablePublicSurface
    | UntestedHighChange
    | AgentGeneratedRiskPattern
    | PersistentDebtHotspot

type CorrelatedEvidence =
    { Path: string
      Kind: CorrelationKind
      Signals: EvidenceSignal list
      Explanation: string }

module Correlation =
    let private has predicate signals = signals |> List.exists predicate

    let evaluate path signals =
        [ if has (function StructuralComplexity _ -> true | _ -> false) signals &&
             has (function FrequentChange _ | HighChurn _ -> true | _ -> false) signals then
              yield { Path=path; Kind=MaintainabilityHotspot; Signals=signals
                      Explanation="Structural complexity coincides with temporal change pressure." }

          if has (function ApiInstability _ -> true | _ -> false) signals &&
             has (function FrequentChange _ | RepeatedRegion _ -> true | _ -> false) signals then
              yield { Path=path; Kind=UnstablePublicSurface; Signals=signals
                      Explanation="Public API change coincides with repeated implementation change." }

          if has (function MissingTestChange -> true | _ -> false) signals &&
             has (function HighChurn _ | FrequentChange _ -> true | _ -> false) signals then
              yield { Path=path; Kind=UntestedHighChange; Signals=signals
                      Explanation="High-change production evidence has no corresponding test change evidence." }

          if has (function TypeWeakening _ | DependencyAdditions _ -> true | _ -> false) signals &&
             has (function MissingTestChange -> true | _ -> false) signals then
              yield { Path=path; Kind=AgentGeneratedRiskPattern; Signals=signals
                      Explanation="Type/dependency expansion coincides with production change lacking test change evidence." }

          if has (function AgedDebt _ -> true | _ -> false) signals &&
             has (function FrequentChange _ | RepeatedRegion _ -> true | _ -> false) signals then
              yield { Path=path; Kind=PersistentDebtHotspot; Signals=signals
                      Explanation="Old debt remains in an actively changing area." } ]
