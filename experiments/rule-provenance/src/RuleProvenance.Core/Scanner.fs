namespace RuleProvenance

open System
open System.Globalization

/// What kind of rule-bearing construct the lexical scan recognised.
type ConstructKind =
    /// A decimal/money literal other than 0 and 1.
    | DecimalLiteral of value: decimal
    /// A comparison against a numeric literal, a string literal or a named constant.
    | ComparisonAgainstConstant of operator: string * operand: string
    /// A string prefix/suffix/containment or pattern check against a literal.
    | CategoryCheck of literal: string
    /// A match branch on a string literal (`| "USD" -> ...`).
    | CasePattern of literal: string
    /// An `if`/`elif`/`match`/`function`/`when` branch.
    | Branch of keyword: string
    /// A wildcard branch that returns a literal: a silent default for unlisted cases.
    | SilentDefault of literal: string
    /// `Error "CODE"`: a refusal.
    | Refusal of code: string

type Construct =
    { Kind: ConstructKind
      Line: int }

type DeclKind =
    | LetDecl
    | TypeDecl
    | MemberDecl
    | UnionCaseDecl
    | FieldDecl
    | ExceptionDecl

/// An `[<Authority("ID1", "ID2")>]` marker.
type Marker = { Ids: string list; Line: int }

type Decl =
    { Name: string
      /// Module path + enclosing type/let names + name, dot separated.
      Qualified: string
      Kind: DeclKind
      Line: int
      EndLine: int
      Indent: int
      Markers: Marker list
      /// Qualified name of the enclosing declaration, if any.
      Parent: string option
      /// Identifiers used in the declaration's body (for legacy closure).
      References: Set<string>
      /// Strings and identifiers in the body, for refusal-naming checks.
      BodyTexts: string list
      /// True when the declaration is `let name = <literal>` or carries [<Literal>].
      IsConstant: bool }

type ScannedFile =
    { File: SourceFile
      Decls: Decl list
      /// Constructs with the innermost declaration that contains them.
      Constructs: (Construct * Decl option) list
      /// Markers that precede no recognisable declaration.
      OrphanMarkers: Marker list }

module Construct =
    let private inv (d: decimal) = d.ToString(CultureInfo.InvariantCulture)

    let describe (c: Construct) =
        match c.Kind with
        | DecimalLiteral v -> $"decimal literal {inv v} (line {c.Line})"
        | ComparisonAgainstConstant(op, operand) -> $"comparison '{op} {operand}' (line {c.Line})"
        | CategoryCheck lit -> $"category/prefix check on \"{lit}\" (line {c.Line})"
        | CasePattern lit -> $"case pattern \"{lit}\" (line {c.Line})"
        | Branch kw -> $"'{kw}' branch (line {c.Line})"
        | SilentDefault lit -> $"wildcard default returning {lit} for unlisted cases (line {c.Line})"
        | Refusal code -> $"refusal \"{code}\" (line {c.Line})"

module Scanner =
    type private Line = { Number: int; Indent: int; Tokens: Token list }

    let private accessOrModifier = set [ "private"; "internal"; "public"; "inline"; "rec"; "mutable"; "static" ]

    /// Parse a decimal literal's value: underscores and the type suffix removed.
    let literalValue (text: string) : decimal option =
        let body = text.Replace("_", "").TrimEnd([| 'm'; 'M'; 'f'; 'F'; 'L'; 'l'; 'u'; 'U'; 'y'; 's'; 'n'; 'I' |])
        match Decimal.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let private isDecimalText (text: string) =
        not (text.StartsWith "0x" || text.StartsWith "0X")
        && (text.EndsWith "m" || text.EndsWith "M" || text.Contains '.' || text.Contains 'e' || text.Contains 'E')

    let private lines (tokens: Token list) =
        tokens
        |> List.groupBy (fun t -> t.Line)
        |> List.map (fun (n, ts) -> { Number = n; Indent = (List.head ts).Col; Tokens = ts })
        |> List.sortBy (fun l -> l.Number)

    /// Strip leading attribute groups from a line; return their markers and the rest.
    let private splitAttributes (tokens: Token list) =
        let rec markersIn (ts: Token list) acc =
            match ts with
            | [] -> List.rev acc
            | t :: rest when t.Kind = Ident && (t.Text = "Authority" || t.Text = "AuthorityAttribute") ->
                match rest with
                | p :: args when p.Kind = Punct && p.Text = "(" ->
                    let inside = args |> List.takeWhile (fun a -> not (a.Kind = Punct && a.Text = ")"))
                    let ids = inside |> List.filter (fun a -> a.Kind = Str) |> List.map (fun a -> a.Text)
                    markersIn rest ({ Ids = ids; Line = t.Line } :: acc)
                | _ -> markersIn rest ({ Ids = []; Line = t.Line } :: acc)
            | _ :: rest -> markersIn rest acc
        let rec go (ts: Token list) markers literal =
            match ts with
            | a :: rest when a.Kind = AttrOpen ->
                let inside = rest |> List.takeWhile (fun t -> t.Kind <> AttrClose)
                let after = rest |> List.skipWhile (fun t -> t.Kind <> AttrClose) |> (function [] -> [] | _ :: r -> r)
                let isLiteral = inside |> List.exists (fun t -> t.Kind = Ident && t.Text = "Literal")
                go after (markers @ markersIn inside []) (literal || isLiteral)
            | _ -> markers, ts, literal
        go tokens [] false

    /// Tokens of a line that are outside every attribute group.
    let private codeTokens (tokens: Token list) =
        let rec go (ts: Token list) depth acc =
            match ts with
            | [] -> List.rev acc
            | t :: rest when t.Kind = AttrOpen -> go rest (depth + 1) acc
            | t :: rest when t.Kind = AttrClose && depth > 0 -> go rest (depth - 1) acc
            | t :: rest -> go rest depth (if depth = 0 then t :: acc else acc)
        go tokens 0 []

    type private DeclHead = { Name: string; Kind: DeclKind }

    let private skipModifiers (ts: Token list) =
        ts |> List.skipWhile (fun t -> t.Kind = Keyword && accessOrModifier.Contains t.Text)

    /// Recognise the declaration a line starts, given the kind of its enclosing declaration.
    let private declHead (enclosing: DeclKind option) (ts: Token list) : DeclHead option =
        match ts with
        | k :: rest when k.Kind = Keyword && (k.Text = "let" || k.Text = "and" || k.Text = "use") ->
            match skipModifiers rest with
            | n :: _ when n.Kind = Ident -> Some { Name = n.Text; Kind = LetDecl }
            | _ -> None
        | k :: rest when k.Kind = Keyword && (k.Text = "member" || k.Text = "override" || k.Text = "default" || k.Text = "abstract" || k.Text = "static") ->
            let afterKw = rest |> List.skipWhile (fun t -> t.Kind = Keyword)
            match afterKw with
            | self :: dot :: n :: _ when self.Kind = Ident && dot.Kind = Op && dot.Text = "." && n.Kind = Ident ->
                Some { Name = n.Text; Kind = MemberDecl }
            | n :: _ when n.Kind = Ident -> Some { Name = n.Text; Kind = MemberDecl }
            | _ -> None
        | k :: rest when k.Kind = Keyword && k.Text = "type" ->
            match skipModifiers rest with
            | n :: _ when n.Kind = Ident -> Some { Name = n.Text; Kind = TypeDecl }
            | _ -> None
        | k :: n :: _ when k.Kind = Keyword && k.Text = "exception" && n.Kind = Ident -> Some { Name = n.Text; Kind = ExceptionDecl }
        | bar :: n :: rest when enclosing = Some TypeDecl && bar.Kind = Op && bar.Text = "|" && n.Kind = Ident && Char.IsUpper n.Text[0]
                                && not (rest |> List.exists (fun t -> t.Kind = Op && t.Text = "->")) ->
            Some { Name = n.Text; Kind = UnionCaseDecl }
        | n :: colon :: _ when enclosing = Some TypeDecl && n.Kind = Ident && colon.Kind = Op && colon.Text = ":" ->
            Some { Name = n.Text; Kind = FieldDecl }
        | _ -> None

    /// `| [<Authority(..)>] Case` puts the marker after the bar.
    let private inlineCaseMarkers (ts: Token list) =
        match ts with
        | bar :: rest when bar.Kind = Op && bar.Text = "|" ->
            let markers, after, _ = splitAttributes rest
            if markers.IsEmpty then None else Some(markers, bar :: after)
        | _ -> None

    let private constructsOf (constants: Set<string>) (lineTokens: Token list) : Construct list =
        let ts = codeTokens lineTokens |> Array.ofList
        let at i = if i >= 0 && i < ts.Length then Some ts[i] else None
        let isLiteralTok (t: Token) =
            t.Kind = Number || t.Kind = Str || (t.Kind = Keyword && (t.Text = "true" || t.Text = "false"))
            || (t.Kind = Ident && (t.Text = "None" || t.Text = "true" || t.Text = "false"))
        let conditionContext =
            ts |> Array.exists (fun t -> (t.Kind = Keyword && (t.Text = "if" || t.Text = "elif" || t.Text = "when")) || (t.Kind = Op && (t.Text = "&&" || t.Text = "||")))
        let constantOperand (t: Token) =
            match t.Kind with
            | Number -> Some t.Text
            | Str -> Some("\"" + t.Text + "\"")
            | Ident when constants.Contains t.Text -> Some t.Text
            | _ -> None
        [ for i in 0 .. ts.Length - 1 do
              let t = ts[i]
              let line = t.Line
              match t.Kind with
              | Number when isDecimalText t.Text ->
                  match literalValue t.Text with
                  | Some v when v <> 0m && v <> 1m -> yield { Kind = DecimalLiteral v; Line = line }
                  | _ -> ()
              | Keyword when t.Text = "if" || t.Text = "elif" || t.Text = "match" || t.Text = "function" || t.Text = "when" ->
                  yield { Kind = Branch t.Text; Line = line }
              | Op when (t.Text = ">" || t.Text = "<" || t.Text = ">=" || t.Text = "<=" || t.Text = "<>" || (t.Text = "=" && conditionContext))
                        && (t.Text.Length = 2 || t.Text = "=" || (t.SpaceBefore && t.SpaceAfter)) ->
                  let operand = [ at (i + 1); at (i - 1) ] |> List.choose id |> List.tryPick constantOperand
                  match operand with
                  | Some o -> yield { Kind = ComparisonAgainstConstant(t.Text, o); Line = line }
                  | None -> ()
              | Ident when (t.Text = "StartsWith" || t.Text = "EndsWith" || t.Text = "Contains" || t.Text = "startsWith"
                            || t.Text = "endsWith" || t.Text = "IsMatch" || t.Text = "contains") ->
                  let lit = [ i + 1 .. i + 3 ] |> List.choose at |> List.tryFind (fun x -> x.Kind = Str)
                  match lit with
                  | Some l -> yield { Kind = CategoryCheck l.Text; Line = line }
                  | None -> ()
              | Op when t.Text = "|" ->
                  match at (i + 1), at (i + 2), at (i + 3) with
                  | Some u, Some arrow, Some result when u.Kind = Ident && u.Text = "_" && arrow.Kind = Op && arrow.Text = "->" && isLiteralTok result ->
                      let text = if result.Kind = Str then "\"" + result.Text + "\"" else result.Text
                      yield { Kind = SilentDefault text; Line = line }
                  | Some s, Some next, _ when s.Kind = Str && next.Kind = Op && (next.Text = "->" || next.Text = "|") ->
                      yield { Kind = CasePattern s.Text; Line = line }
                  | _ -> ()
              | Ident when t.Text = "Error" ->
                  match at (i + 1), at (i + 2) with
                  | Some s, _ when s.Kind = Str -> yield { Kind = Refusal s.Text; Line = line }
                  | Some p, Some s when p.Kind = Punct && p.Text = "(" && s.Kind = Str -> yield { Kind = Refusal s.Text; Line = line }
                  | _ -> ()
              | _ -> () ]

    type private Frame = { Decl: Decl; Indent: int }

    /// Scan one file. `globalConstants` are constant names declared in any file of the same role.
    let scanWith (globalConstants: Set<string>) (file: SourceFile) : ScannedFile =
        let ls = lines (Lexer.tokenize file.Text)
        // Namespace / top-level module prefix.
        let topPrefix =
            ls
            |> List.tryPick (fun l ->
                match l.Tokens with
                | k :: rest when k.Kind = Keyword && (k.Text = "namespace" || k.Text = "module") && not (rest |> List.exists (fun t -> t.Kind = Op && t.Text = "=")) ->
                    let name = rest |> List.filter (fun t -> not (t.Kind = Keyword && accessOrModifier.Contains t.Text) && not (t.Kind = Keyword && t.Text = "rec")) |> List.map (fun t -> t.Text) |> String.concat ""
                    Some name
                | _ -> None)
            |> Option.defaultValue ""
        let join (a: string) (b: string) = if a = "" then b else a + "." + b

        // Walk lines keeping an indentation stack of modules and declarations.
        let rec walk (remaining: Line list) (modules: (string * int) list) (frames: Frame list) (pending: Marker list)
                     (decls: Decl list) (orphans: Marker list) =
            match remaining with
            | [] -> List.rev decls, List.rev orphans @ pending
            | l :: rest ->
                let modules = modules |> List.filter (fun (_, ind) -> ind < l.Indent)
                let frames = frames |> List.filter (fun f -> f.Indent < l.Indent)
                let markers, afterAttrs, isLiteralAttr = splitAttributes l.Tokens
                let enclosingKind = frames |> List.tryHead |> Option.map (fun f -> f.Decl.Kind)
                let inlineCase = if markers.IsEmpty then inlineCaseMarkers afterAttrs else None
                let markers, afterAttrs =
                    match inlineCase with
                    | Some(ms, ts) -> ms, ts
                    | None -> markers, afterAttrs
                let allMarkers = pending @ markers
                match afterAttrs with
                | [] -> walk rest modules frames allMarkers decls orphans
                | k :: n :: tail when k.Kind = Keyword && k.Text = "module" && (tail |> List.exists (fun t -> t.Kind = Op && t.Text = "=")) ->
                    walk rest ((n.Text, l.Indent) :: modules) frames [] decls (orphans @ allMarkers)
                | k :: _ when k.Kind = Keyword && (k.Text = "module" || k.Text = "namespace" || k.Text = "open") ->
                    walk rest modules frames [] decls (orphans @ allMarkers)
                | _ ->
                    match declHead enclosingKind afterAttrs with
                    | Some head ->
                        let modulePath = modules |> List.rev |> List.map fst |> List.fold join topPrefix
                        let parent = frames |> List.tryHead |> Option.map (fun f -> f.Decl.Qualified)
                        let qualified = join (Option.defaultValue modulePath parent) head.Name
                        let isConst =
                            isLiteralAttr
                            || (head.Kind = LetDecl
                                && (match afterAttrs |> List.skipWhile (fun t -> not (t.Kind = Op && t.Text = "=")) with
                                    | _ :: [ v ] -> v.Kind = Number
                                    | _ :: [ minus; v ] -> minus.Kind = Op && minus.Text = "-" && v.Kind = Number
                                    | _ -> false))
                        let decl =
                            { Name = head.Name
                              Qualified = qualified
                              Kind = head.Kind
                              Line = l.Number
                              EndLine = l.Number
                              Indent = l.Indent
                              Markers = allMarkers
                              Parent = parent
                              References = Set.empty
                              BodyTexts = []
                              IsConstant = isConst }
                        walk rest modules ({ Decl = decl; Indent = l.Indent } :: frames) [] (decl :: decls) orphans
                    | None ->
                        walk rest modules frames [] decls (orphans @ allMarkers)

        let rawDecls, orphans = walk ls [] [] [] [] []

        // Extent of a declaration: until the next non-blank line indented at or left of it.
        let lineArray = ls |> Array.ofList
        let endLine (d: Decl) =
            let following = lineArray |> Array.filter (fun l -> l.Number > d.Line)
            match following |> Array.tryFind (fun l -> l.Indent <= d.Indent && not (List.head l.Tokens).Kind.IsAttrOpen) with
            | Some stop ->
                let before = lineArray |> Array.filter (fun l -> l.Number > d.Line && l.Number < stop.Number && l.Indent > d.Indent)
                if before.Length = 0 then d.Line else (Array.last before).Number
            | None ->
                let body = following |> Array.filter (fun l -> l.Indent > d.Indent)
                if body.Length = 0 then d.Line else (Array.last body).Number
        let localConstants = rawDecls |> List.filter (fun d -> d.IsConstant) |> List.map (fun d -> d.Name) |> Set.ofList
        let constants = Set.union globalConstants localConstants
        let decls =
            rawDecls
            |> List.map (fun d ->
                let e = endLine d
                let bodyTokens =
                    lineArray
                    |> Array.filter (fun l -> l.Number >= d.Line && l.Number <= e)
                    |> Array.collect (fun l -> codeTokens l.Tokens |> Array.ofList)
                { d with
                    EndLine = e
                    References = bodyTokens |> Array.filter (fun t -> t.Kind = Ident) |> Array.map (fun t -> t.Text) |> Set.ofArray
                    BodyTexts = bodyTokens |> Array.filter (fun t -> t.Kind = Ident || t.Kind = Str) |> Array.map (fun t -> t.Text) |> List.ofArray })

        // A construct belongs to the innermost declaration whose extent contains it.
        let owner (line: int) =
            decls
            |> List.filter (fun d -> d.Line <= line && line <= d.EndLine)
            |> List.sortByDescending (fun d -> d.Line, d.Indent)
            |> List.tryHead
        let constructs =
            ls
            |> List.collect (fun l -> constructsOf constants l.Tokens)
            |> List.map (fun c -> c, owner c.Line)
        { File = file; Decls = decls; Constructs = constructs; OrphanMarkers = orphans }

    /// Names of constants (`let x = <literal>` or [<Literal>]) declared in a file.
    let constantsOf (file: SourceFile) =
        (scanWith Set.empty file).Decls |> List.filter (fun d -> d.IsConstant) |> List.map (fun d -> d.Name) |> Set.ofList

    /// Scan a set of files of one role; constants are shared across the set.
    let scanAll (files: SourceFile list) =
        let constants = files |> List.map constantsOf |> Set.unionMany
        files |> List.map (scanWith constants)
