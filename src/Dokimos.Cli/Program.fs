namespace Dokimos.Cli

open System
open System.IO
open System.Text.Json
open Dokimos.Core

module Program =
    [<EntryPoint>]
    let main args =
        match args |> Array.toList with
        | ["measure"; path] when File.Exists path ->
            let source = File.ReadAllText path
            let metrics = Structural.measure path source
            let options = JsonSerializerOptions(WriteIndented = true)
            Console.WriteLine(JsonSerializer.Serialize(metrics, options))
            0
        | _ ->
            Console.Error.WriteLine("Usage: dokimos measure <source-file>")
            2
