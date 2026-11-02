namespace Dokimos.Core.Verification

open System
open System.Text.RegularExpressions

/// A lexical view of JavaScript/TypeScript test sources: comments removed and literal contents
/// blanked, so structural scanning never reads code inside a string, template or regex literal.
module JavaScriptLexing =
    /// A JavaScript view: `Code` has every comment and every string/template/regex literal's
    /// contents blanked (template `${...}` expressions stay code); `Literals` maps the offset of
    /// each string literal's opening quote to its decoded text.
    type JsView = { Code: string; Literals: Map<int, string> }

    let private regexPreceders = set [ '('; ','; '='; ':'; '['; '!'; '&'; '|'; '?'; '{'; '}'; ';'; '+'; '-'; '*'; '%'; '<'; '>'; '~'; '^' ]

    let lexJavaScript (source: string) : JsView =
        let code = Text.StringBuilder(source.Length)
        let literals = Collections.Generic.Dictionary<int, string>()
        let n = source.Length
        let at k = if k < n then source[k] else '\000'

        let previousSignificant () =
            let rec back k =
                if k < 0 then '\000'
                elif Char.IsWhiteSpace(code[k]) then back (k - 1)
                else code[k]
            back (code.Length - 1)

        let previousWord () =
            let text = code.ToString().TrimEnd()
            let m = Regex.Match(text, @"([A-Za-z_$][\w$]*)$")
            if m.Success then m.Value else ""

        let blankOut (c: char) = if c = '\n' then '\n' else ' '

        // Returns the index after the closing quote, appending blanked contents.
        let rec readString (start: int) (quote: char) =
            let value = Text.StringBuilder()
            code.Append(quote) |> ignore
            let rec go k =
                if k >= n then k
                else
                    let c = source[k]
                    if c = '\\' && k + 1 < n then
                        let decoded =
                            match source[k + 1] with
                            | 'n' -> '\n'
                            | 't' -> '\t'
                            | other -> other
                        value.Append(decoded) |> ignore
                        code.Append("  ") |> ignore
                        go (k + 2)
                    elif c = quote then
                        code.Append(quote) |> ignore
                        k + 1
                    elif c = '\n' && quote <> '`' then
                        code.Append('\n') |> ignore
                        k + 1
                    elif quote = '`' && c = '$' && at (k + 1) = '{' then
                        code.Append("${") |> ignore
                        let after = readCode (k + 2) true
                        value.Append("${…}") |> ignore
                        go after
                    else
                        value.Append(c) |> ignore
                        code.Append(blankOut c) |> ignore
                        go (k + 1)
            let finish = go (start + 1)
            literals[start] <- value.ToString()
            finish

        // Reads code until the brace depth returns to zero (for template expressions) or the end.
        and readCode (start: int) (stopAtClose: bool) =
            let rec go k depth =
                if k >= n then k
                else
                    let c = source[k]
                    if c = '/' && at (k + 1) = '/' then
                        let stop = source.IndexOf('\n', k)
                        let stop = if stop < 0 then n else stop
                        code.Append(String(' ', stop - k)) |> ignore
                        go stop depth
                    elif c = '/' && at (k + 1) = '*' then
                        let stop = source.IndexOf("*/", k + 2)
                        let stop = if stop < 0 then n else stop + 2
                        source.Substring(k, stop - k) |> Seq.iter (fun ch -> code.Append(blankOut ch) |> ignore)
                        go stop depth
                    elif c = '"' || c = '\'' || c = '`' then go (readString k c) depth
                    elif c = '/' && (regexPreceders.Contains(previousSignificant ()) || previousSignificant () = '\000'
                                     || List.contains (previousWord ()) [ "return"; "typeof"; "case"; "in"; "of" ]) then
                        // Regular-expression literal.
                        code.Append('/') |> ignore
                        let rec rx j inClass =
                            if j >= n || source[j] = '\n' then j
                            elif source[j] = '\\' then code.Append("  ") |> ignore; rx (j + 2) inClass
                            elif source[j] = '[' then code.Append(' ') |> ignore; rx (j + 1) true
                            elif source[j] = ']' then code.Append(' ') |> ignore; rx (j + 1) false
                            elif source[j] = '/' && not inClass then code.Append('/') |> ignore; j + 1
                            else code.Append(' ') |> ignore; rx (j + 1) inClass
                        go (rx (k + 1) false) depth
                    elif c = '{' then
                        code.Append(c) |> ignore
                        go (k + 1) (depth + 1)
                    elif c = '}' then
                        code.Append(c) |> ignore
                        if stopAtClose && depth = 0 then k + 1 else go (k + 1) (depth - 1)
                    else
                        code.Append(c) |> ignore
                        go (k + 1) depth
            go start 0

        readCode 0 false |> ignore
        { Code = code.ToString(); Literals = literals |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq }
