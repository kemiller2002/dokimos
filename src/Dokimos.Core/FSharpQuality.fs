namespace Dokimos.Core

open System

type FSharpQualityMetrics =
    { Path: string
      FunctionBindings: int
      MatchExpressions: int
      BranchIndicators: int
      ExceptionRaiseIndicators: int
      OptionResultIndicators: int
      ObjTypeIndicators: int
      StringMapIndicators: int
      TodoIndicators: int
      SuppressionIndicators: int }

module FSharpQuality =
    let measure path (source: string) =
        let lines = source.Replace("\r\n", "\n").Split('\n')
        let count predicate = lines |> Array.filter predicate |> Array.length
        let trimmedContains value (line: string) = line.Trim().Contains(value, StringComparison.Ordinal)
        { Path = path
          FunctionBindings = count (fun line -> line.TrimStart().StartsWith("let "))
          MatchExpressions = count (trimmedContains "match ")
          BranchIndicators = count (fun line ->
              let t = line.TrimStart()
              t.StartsWith("| ") || t.StartsWith("if ") || t.Contains(" then "))
          ExceptionRaiseIndicators = count (fun line ->
              trimmedContains "failwith" line || trimmedContains "raise " line || trimmedContains "invalidArg" line)
          OptionResultIndicators = count (fun line ->
              trimmedContains " option" line || trimmedContains "Result<" line || trimmedContains "Ok " line || trimmedContains "Error " line)
          ObjTypeIndicators = count (fun line ->
              trimmedContains ": obj" line || trimmedContains "<obj>" line)
          StringMapIndicators = count (fun line ->
              trimmedContains "Map<string" line || trimmedContains "IDictionary<string" line)
          TodoIndicators = count (fun line ->
              trimmedContains "TODO" line || trimmedContains "FIXME" line)
          SuppressionIndicators = count (fun line ->
              trimmedContains "nowarn" line || trimmedContains "NoWarn" line) }

type AgentQualityIndicators =
    { Path: string
      TypeWeakeningIndicators: int
      UnresolvedScaffoldingIndicators: int
      SuppressionIndicators: int
      BroadCatchIndicators: int }

module AgentQuality =
    let fromMetrics (structural: SourceMetrics) (quality: FSharpQualityMetrics) =
        { Path = structural.Path
          TypeWeakeningIndicators = quality.ObjTypeIndicators + quality.StringMapIndicators
          UnresolvedScaffoldingIndicators = quality.TodoIndicators
          SuppressionIndicators = quality.SuppressionIndicators
          BroadCatchIndicators = structural.BroadCatchIndicators }
