namespace Dokimos.Core

open System

type ComplexityProxy =
    { Path: string
      DecisionPoints: int
      BooleanOperators: int
      MatchBranches: int
      NestingPeak: int
      ProxyCyclomatic: int }

module Complexity =
    let measure path (source: string) =
        let lines = source.Replace("\r\n", "\n").Split('\n')
        let mutable nesting = 0
        let mutable peak = 0
        let mutable decisions = 0
        let mutable booleanOps = 0
        let mutable branches = 0

        for line in lines do
            let t = line.Trim()
            if t.StartsWith("if ") || t.Contains(" if ") || t.StartsWith("for ") || t.StartsWith("while ") then decisions <- decisions + 1
            if t.StartsWith("| ") && not (t.StartsWith("|>")) then branches <- branches + 1
            booleanOps <- booleanOps + (t.Split("&&").Length - 1) + (t.Split("||").Length - 1)
            let leading = line.Length - line.TrimStart().Length
            nesting <- leading / 4
            peak <- max peak nesting

        { Path = path
          DecisionPoints = decisions
          BooleanOperators = booleanOps
          MatchBranches = branches
          NestingPeak = peak
          ProxyCyclomatic = 1 + decisions + booleanOps + branches }

type DuplicateBlock =
    { Fingerprint: string
      Occurrences: int
      Paths: string list
      LinesPerBlock: int }

module Duplication =
    let private normalize (line: string) = line.Trim()
    let blocks blockSize sources =
        sources
        |> List.collect (fun (path, source: string) ->
            let lines =
                source.Replace("\r\n", "\n").Split('\n')
                |> Array.map normalize
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
            if lines.Length < blockSize then []
            else
                [ for i in 0 .. lines.Length - blockSize ->
                    let block = lines[i .. i + blockSize] |> String.concat "\n"
                    block, path ])
        |> List.groupBy fst
        |> List.choose (fun (fingerprint, occurrences) ->
            let paths = occurrences |> List.map snd |> List.distinct
            if occurrences.Length > 1 then
                Some { Fingerprint = fingerprint; Occurrences = occurrences.Length; Paths = paths; LinesPerBlock = blockSize }
            else None)
