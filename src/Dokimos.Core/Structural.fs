namespace Dokimos.Core

open System

type SourceMetrics =
    { Path: string
      Lines: int
      NonBlankLines: int
      PublicDeclarations: int
      BroadCatchIndicators: int
      MutableBindings: int }

module Structural =
    let private trimmedLines (source: string) =
        source.Replace("\r\n", "\n").Split('\n')

    let measure path (source: string) =
        let lines = trimmedLines source
        let nonBlank = lines |> Array.filter (String.IsNullOrWhiteSpace >> not) |> Array.length
        let publicDeclarations =
            lines
            |> Array.filter (fun line ->
                let t = line.TrimStart()
                t.StartsWith("type ") || t.StartsWith("module ") || t.StartsWith("val "))
            |> Array.length
        let broadCatch =
            lines
            |> Array.filter (fun line ->
                let t = line.Trim()
                t = "| _ ->" || t.StartsWith("| _ -> "))
            |> Array.length
        let mutableBindings =
            lines
            |> Array.filter (fun line -> line.Contains("let mutable "))
            |> Array.length

        { Path = path
          Lines = lines.Length
          NonBlankLines = nonBlank
          PublicDeclarations = publicDeclarations
          BroadCatchIndicators = broadCatch
          MutableBindings = mutableBindings }
