namespace RuleProvenance

open System
open System.Text

/// A lexical (not syntactic) tokenizer for F# source, modelled on
/// src/Dokimos.Core/SourceLexing.fs but producing tokens and keeping string
/// literal contents (the audit needs prefix strings, refusal codes and the IDs in
/// `[<Authority("...")>]`). Comments are dropped. It does not parse.
type TokenKind =
    | Ident
    | Keyword
    | Number
    | Str
    | Op
    | Punct
    | AttrOpen
    | AttrClose

type Token =
    { Kind: TokenKind
      /// For Str: the literal's contents (escapes left as written).
      Text: string
      Line: int
      Col: int
      SpaceBefore: bool
      SpaceAfter: bool }

module Lexer =
    let keywords =
        set
            [ "abstract"; "and"; "as"; "begin"; "default"; "do"; "done"; "elif"; "else"; "end"; "exception"
              "false"; "finally"; "for"; "fun"; "function"; "if"; "in"; "inherit"; "inline"; "interface"
              "internal"; "lazy"; "let"; "match"; "member"; "module"; "mutable"; "namespace"; "new"; "null"
              "of"; "open"; "override"; "private"; "public"; "rec"; "return"; "static"; "struct"; "then"
              "to"; "true"; "try"; "type"; "use"; "val"; "when"; "while"; "with"; "yield" ]

    let private opChars = set "!%&*+-./<=>?@^|~:$"
    let private punctChars = set "()[]{},;"

    let private isIdentStart (c: char) = Char.IsLetter c || c = '_'
    let private isIdentPart (c: char) = Char.IsLetterOrDigit c || c = '_' || c = '\''

    type private Raw = { Kind: TokenKind; Text: string; Start: int; End: int }

    /// Index just past a string literal opened at `i` (after any prefix), and its contents.
    let private scanString (s: string) (contentStart: int) (verbatim: bool) (triple: bool) =
        let sb = StringBuilder()
        let rec go j =
            if j >= s.Length then j
            elif triple then
                if j + 2 < s.Length && s[j] = '"' && s[j + 1] = '"' && s[j + 2] = '"' then j + 3
                else (sb.Append(s[j]) |> ignore; go (j + 1))
            elif verbatim then
                if s[j] = '"' && j + 1 < s.Length && s[j + 1] = '"' then (sb.Append('"') |> ignore; go (j + 2))
                elif s[j] = '"' then j + 1
                else (sb.Append(s[j]) |> ignore; go (j + 1))
            else if s[j] = '\\' && j + 1 < s.Length then (sb.Append(s[j]).Append(s[j + 1]) |> ignore; go (j + 2))
            elif s[j] = '"' then j + 1
            else (sb.Append(s[j]) |> ignore; go (j + 1))
        let finish = go contentStart
        finish, sb.ToString()

    let rec private skipBlockComment (s: string) (j: int) (depth: int) =
        if j >= s.Length then j
        elif j + 1 < s.Length && s[j] = '(' && s[j + 1] = '*' then skipBlockComment s (j + 2) (depth + 1)
        elif j + 1 < s.Length && s[j] = '*' && s[j + 1] = ')' then
            if depth = 1 then j + 2 else skipBlockComment s (j + 2) (depth - 1)
        else skipBlockComment s (j + 1) depth

    let private scanNumber (s: string) (i: int) =
        let rec digits j = if j < s.Length && (Char.IsDigit s[j] || s[j] = '_') then digits (j + 1) else j
        let afterInt =
            // Hex/octal/binary prefixes.
            if i + 1 < s.Length && s[i] = '0' && "xXoObB".Contains(s[i + 1]) then
                let rec hex j = if j < s.Length && (Uri.IsHexDigit s[j] || s[j] = '_') then hex (j + 1) else j
                hex (i + 2)
            else digits i
        let afterFrac =
            // `1..10` is a range, not a fraction.
            if afterInt + 1 < s.Length && s[afterInt] = '.' && Char.IsDigit s[afterInt + 1] then digits (afterInt + 1)
            else afterInt
        let afterExp =
            if afterFrac < s.Length && (s[afterFrac] = 'e' || s[afterFrac] = 'E') then
                let k = if afterFrac + 1 < s.Length && (s[afterFrac + 1] = '+' || s[afterFrac + 1] = '-') then afterFrac + 2 else afterFrac + 1
                if k < s.Length && Char.IsDigit s[k] then digits k else afterFrac
            else afterFrac
        let rec suffix j = if j < s.Length && Char.IsLetter s[j] then suffix (j + 1) else j
        suffix afterExp

    let private rawTokens (s: string) : Raw list =
        let rec go i (acc: Raw list) =
            if i >= s.Length then List.rev acc
            else
                let c = s[i]
                let at k = if i + k < s.Length then s[i + k] else '\000'
                if Char.IsWhiteSpace c then go (i + 1) acc
                elif c = '/' && at 1 = '/' then
                    let nl = s.IndexOf('\n', i)
                    go (if nl < 0 then s.Length else nl) acc
                elif c = '(' && at 1 = '*' && at 2 <> ')' then go (skipBlockComment s i 0) acc
                elif c = '"' && at 1 = '"' && at 2 = '"' then
                    let e, text = scanString s (i + 3) false true
                    go e ({ Kind = Str; Text = text; Start = i; End = e } :: acc)
                elif (c = '$' || c = '@') && at 1 = '"' && at 2 = '"' && at 3 = '"' then
                    let e, text = scanString s (i + 4) false true
                    go e ({ Kind = Str; Text = text; Start = i; End = e } :: acc)
                elif c = '@' && at 1 = '"' then
                    let e, text = scanString s (i + 2) true false
                    go e ({ Kind = Str; Text = text; Start = i; End = e } :: acc)
                elif c = '$' && at 1 = '"' then
                    let e, text = scanString s (i + 2) false false
                    go e ({ Kind = Str; Text = text; Start = i; End = e } :: acc)
                elif (c = '$' && at 1 = '@' || c = '@' && at 1 = '$') && at 2 = '"' then
                    let e, text = scanString s (i + 3) true false
                    go e ({ Kind = Str; Text = text; Start = i; End = e } :: acc)
                elif c = '"' then
                    let e, text = scanString s (i + 1) false false
                    go e ({ Kind = Str; Text = text; Start = i; End = e } :: acc)
                elif c = '\'' && at 1 = '\\' then
                    let close = [ 3..8 ] |> List.tryFind (fun k -> at k = '\'')
                    let e = match close with Some k -> i + k + 1 | None -> i + 1
                    go e ({ Kind = Str; Text = s.Substring(i, e - i); Start = i; End = e } :: acc)
                elif c = '\'' && at 2 = '\'' && at 1 <> '\n' then
                    go (i + 3) ({ Kind = Str; Text = string (at 1); Start = i; End = i + 3 } :: acc)
                elif c = '\'' && isIdentStart (at 1) then
                    // Generic type parameter such as 'T.
                    let rec id j = if j < s.Length && isIdentPart s[j] then id (j + 1) else j
                    let e = id (i + 1)
                    go e ({ Kind = Ident; Text = s.Substring(i, e - i); Start = i; End = e } :: acc)
                elif c = '`' && at 1 = '`' then
                    let close = s.IndexOf("``", i + 2, StringComparison.Ordinal)
                    let e = if close < 0 then s.Length else close + 2
                    go e ({ Kind = Ident; Text = s.Substring(i + 2, max 0 (e - i - 4)); Start = i; End = e } :: acc)
                elif Char.IsDigit c then
                    let e = scanNumber s i
                    go e ({ Kind = Number; Text = s.Substring(i, e - i); Start = i; End = e } :: acc)
                elif isIdentStart c then
                    let rec id j = if j < s.Length && isIdentPart s[j] then id (j + 1) else j
                    let e = id i
                    let text = s.Substring(i, e - i)
                    let kind = if keywords.Contains text then Keyword else Ident
                    go e ({ Kind = kind; Text = text; Start = i; End = e } :: acc)
                elif c = '[' && at 1 = '<' then go (i + 2) ({ Kind = AttrOpen; Text = "[<"; Start = i; End = i + 2 } :: acc)
                elif c = '>' && at 1 = ']' then go (i + 2) ({ Kind = AttrClose; Text = ">]"; Start = i; End = i + 2 } :: acc)
                elif punctChars.Contains c then go (i + 1) ({ Kind = Punct; Text = string c; Start = i; End = i + 1 } :: acc)
                elif opChars.Contains c then
                    let rec op j = if j < s.Length && opChars.Contains s[j] && not (s[j] = '>' && j + 1 < s.Length && s[j + 1] = ']') then op (j + 1) else j
                    let e = op i
                    go e ({ Kind = Op; Text = s.Substring(i, e - i); Start = i; End = e } :: acc)
                else go (i + 1) acc
        go 0 []

    /// Tokens with 1-based line and 0-based column. CRLF is normalized first.
    let tokenize (source: string) : Token list =
        let s = source.Replace("\r\n", "\n")
        let lineStarts =
            seq {
                yield 0
                for i in 0 .. s.Length - 1 do
                    if s[i] = '\n' then yield i + 1
            }
            |> Array.ofSeq
        let position (offset: int) =
            let idx = Array.BinarySearch(lineStarts, offset)
            let line = if idx >= 0 then idx else (~~~idx) - 1
            line + 1, offset - lineStarts[line]
        let space (k: int) = k < 0 || k >= s.Length || Char.IsWhiteSpace s[k]
        rawTokens s
        |> List.map (fun r ->
            let line, col = position r.Start
            { Kind = r.Kind
              Text = r.Text
              Line = line
              Col = col
              SpaceBefore = space (r.Start - 1)
              SpaceAfter = space r.End })
