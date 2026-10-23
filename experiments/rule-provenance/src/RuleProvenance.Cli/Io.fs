namespace RuleProvenance.Cli

open System
open System.IO
open RuleProvenance

/// The only module that touches the file system on the input side.
module Io =
    let relative (root: string) (path: string) =
        Path.GetRelativePath(Path.GetFullPath root, Path.GetFullPath path).Replace('\\', '/')

    let private isBuildOutput (root: string) (file: string) =
        let rel = relative root file
        rel.Split('/') |> Array.exists (fun seg -> seg = "bin" || seg = "obj")

    /// F# source files under a file or directory path, sorted by relative path.
    let collect (root: string) (role: SourceRole) (path: string) : Result<SourceFile list, string> =
        let read (file: string) =
            { RelativePath = relative root file
              Role = role
              Text = File.ReadAllText file }
        if File.Exists path then Ok [ read path ]
        elif Directory.Exists path then
            Directory.GetFiles(path, "*.fs", SearchOption.AllDirectories)
            |> Array.filter (fun f -> not (isBuildOutput path f))
            |> Array.sortWith (fun a b -> String.CompareOrdinal(relative root a, relative root b))
            |> Array.map read
            |> List.ofArray
            |> Ok
        else Error $"source path not found: {path}"

    let readBytes (path: string) : Result<byte[], string> =
        if File.Exists path then Ok(File.ReadAllBytes path) else Error $"file not found: {path}"

    /// Write text with LF line endings and no BOM.
    let writeText (path: string) (text: string) =
        Path.GetDirectoryName(Path.GetFullPath path) |> nonNull |> Directory.CreateDirectory |> ignore
        File.WriteAllText(path, text, Text.UTF8Encoding(false))
