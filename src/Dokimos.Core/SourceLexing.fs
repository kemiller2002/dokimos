namespace Dokimos.Core

open System
open System.Text.RegularExpressions

/// One F# source line split into what the compiler reads as code (string
/// and character literal contents blanked, comments removed) and the text of
/// any comments on that line. Line-based heuristics then never mistake a
/// string literal for code or a comment.
type LexedLine =
    { Number: int
      Original: string
      Code: string
      Comment: string }

/// A lexical (not syntactic) view of F# source. Handles line and nested
/// block comments, regular, verbatim and triple-quoted strings, and
/// character literals. It is explicit about being lexical: it does not parse.
module SourceLexing =
    type private State =
        | InCode
        | InLineComment
        | InBlockComment of depth: int
        | InString
        | InVerbatimString
        | InTripleString

    /// (code char, comment char) emitted for one input char.
    let private blank = ' ', ' '

    let private step (text: string) (index: int) (state: State) =
        let at k = if index + k < text.Length then text[index + k] else '\000'
        let c = at 0
        if c = '\n' then
            [ '\n', '\n' ], (match state with InLineComment -> InCode | other -> other), 1
        else
            match state with
            | InCode ->
                if c = '/' && at 1 = '/' then [ (' ', '/'); (' ', '/') ], InLineComment, 2
                elif c = '(' && at 1 = '*' && at 2 <> ')' then [ (' ', '('); (' ', '*') ], InBlockComment 1, 2
                elif c = '"' && at 1 = '"' && at 2 = '"' then [ ('"', ' '); ('"', ' '); ('"', ' ') ], InTripleString, 3
                elif c = '@' && at 1 = '"' then [ ('@', ' '); ('"', ' ') ], InVerbatimString, 2
                elif c = '"' then [ ('"', ' ') ], InString, 1
                elif c = '\'' && at 1 = '\\' then
                    // Escaped character literal such as '\n' or 'A'.
                    let close = [ 3 .. 8 ] |> List.tryFind (fun k -> at k = '\'')
                    match close with
                    | Some k -> List.replicate (k + 1) blank, InCode, k + 1
                    | None -> [ (c, ' ') ], InCode, 1
                elif c = '\'' && at 2 = '\'' && at 1 <> '\n' then [ ('\'', ' '); blank; ('\'', ' ') ], InCode, 3
                else [ (c, ' ') ], InCode, 1
            | InLineComment -> [ (' ', c) ], InLineComment, 1
            | InBlockComment depth ->
                if c = '(' && at 1 = '*' then [ (' ', '('); (' ', '*') ], InBlockComment(depth + 1), 2
                elif c = '*' && at 1 = ')' then
                    [ (' ', '*'); (' ', ')') ], (if depth = 1 then InCode else InBlockComment(depth - 1)), 2
                else [ (' ', c) ], state, 1
            | InString ->
                if c = '\\' && at 1 <> '\n' then [ blank; blank ], InString, 2
                elif c = '"' then [ ('"', ' ') ], InCode, 1
                else [ blank ], InString, 1
            | InVerbatimString ->
                if c = '"' && at 1 = '"' then [ blank; blank ], InVerbatimString, 2
                elif c = '"' then [ ('"', ' ') ], InCode, 1
                else [ blank ], InVerbatimString, 1
            | InTripleString ->
                if c = '"' && at 1 = '"' && at 2 = '"' then [ ('"', ' '); ('"', ' '); ('"', ' ') ], InCode, 3
                else [ blank ], InTripleString, 1

    let private views (text: string) =
        let rec go index state (acc: (char * char) list list) =
            if index >= text.Length then List.rev acc |> List.concat
            else
                let emitted, next, consumed = step text index state
                go (index + consumed) next (emitted :: acc)
        let pairs = go 0 InCode []
        String(pairs |> List.map fst |> Array.ofList), String(pairs |> List.map snd |> Array.ofList)

    /// Lines of a source file; a trailing newline does not add a line.
    let lines (source: string) =
        let normalized = source.Replace("\r\n", "\n")
        let body = if normalized.EndsWith "\n" then normalized.Substring(0, normalized.Length - 1) else normalized
        if body.Length = 0 && normalized.Length = 0 then [||] else body.Split('\n')

    let lex (source: string) : LexedLine list =
        let original = lines source
        let code, comment = views (String.Join("\n", original))
        let codeLines = code.Split('\n')
        let commentLines = comment.Split('\n')
        original
        |> Array.mapi (fun i line ->
            { Number = i + 1
              Original = line
              Code = (if i < codeLines.Length then codeLines[i] else "")
              Comment = (if i < commentLines.Length then commentLines[i] else "") })
        |> List.ofArray

/// Repository-relative glob matching: `**` spans directories, `*` and `?`
/// stay within one path segment. Paths use '/'.
module Glob =
    let private toRegex (glob: string) =
        let rec go (chars: char list) (acc: string list) =
            match chars with
            | [] -> List.rev acc |> String.concat ""
            | '*' :: '*' :: '/' :: rest -> go rest ("(?:.*/)?" :: acc)
            | '*' :: '*' :: rest -> go rest (".*" :: acc)
            | '*' :: rest -> go rest ("[^/]*" :: acc)
            | '?' :: rest -> go rest ("[^/]" :: acc)
            | c :: rest -> go rest (Regex.Escape(string c) :: acc)
        Regex("^" + go (List.ofSeq (glob.Replace('\\', '/'))) [] + "$", RegexOptions.CultureInvariant)

    let isMatch (glob: string) (path: string) = (toRegex glob).IsMatch(path.Replace('\\', '/'))

    let anyMatch (globs: string list) (path: string) = globs |> List.exists (fun g -> isMatch g path)
