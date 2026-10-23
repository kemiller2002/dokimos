namespace RuleProvenance

open System
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.SyntaxTrivia
open FSharp.Compiler.Text
open FSharp.Compiler.Xml

/// The v2 (`ast`) provenance scan: untyped F# parse trees from FSharp.Compiler.Service
/// instead of line-oriented tokens. It produces the same ScannedFile shape as the
/// lexical Scanner, so marker, trace and observation checks are shared; it differs in
/// which constructs count as rule-bearing and it keeps comments for citation matching.
///
/// Untyped by design: a typed check would need each audited file's whole compilation
/// closure (project order, sibling projects, packages), which an audit over individual
/// files does not have. Consequences, stated in LIMITATIONS.md: a union case is
/// recognised by shape (a capitalised identifier that is not an F# built-in), and a
/// call to a category predicate is resolved by name across the audited files.
///
/// Rule-bearing constructs (every rule is general; nothing names a corpus value):
/// * a numeric literal of any type, except 0 and 1 outside a value position. A value
///   position is a declaration's body, a local binding's right-hand side, or the result
///   of an if/match branch. Elsewhere (operands of comparisons and arithmetic, call
///   arguments, record fields, indexes) 0 and 1 act as identity, sign test, counter
///   start or index and are not counted. The digits argument of Math.Round/Decimal.Round
///   is representation, never counted;
/// * `x = V` / `x <> V` where V is a string/char literal or a union case, in any
///   expression (so multi-line conditions are seen);
/// * a match/function clause keyed on a literal or a union case;
/// * `StartsWith`/`EndsWith`/`Contains`/`Equals`/`IsMatch` against a string literal;
/// * an exemption shape: `cat || rest`, `cat-excluded && rest` or `if cat then ...`,
///   where `cat` is a category test or a call to a declaration whose body is one;
/// * a catch-all clause (`_` or a variable) returning a constant: a silent default.
module AstScanner =
    /// The pinned FSharp.Compiler.Service version (RuleProvenance.Core.fsproj).
    [<Literal>]
    let FcsVersion = "43.12.400"

    // One parser instance for the process; parsing itself is a deterministic
    // function of (file name, text).
    let private checker = lazy (FSharpChecker.Create())

    /// Parse one file. Error-severity diagnostics make the file unusable for the audit.
    let parse (file: SourceFile) : Result<ParsedInput, string list> =
        let name =
            if file.RelativePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
               || file.RelativePath.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) then file.RelativePath
            else file.RelativePath + ".fs"
        let opts = { FSharpParsingOptions.Default with SourceFiles = [| name |] }
        let result = checker.Force().ParseFile(name, SourceText.ofString file.Text, opts) |> Async.RunSynchronously
        let errors =
            result.Diagnostics
            |> Array.filter (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error)
            |> Array.map (fun d -> $"{file.RelativePath}({d.StartLine},{d.StartColumn + 1}): parse error: {d.Message}")
            |> List.ofArray
        if errors.IsEmpty then Ok result.ParseTree else Error errors

    // ------------------------------------------------------------------ expression shapes

    let private operatorText =
        Map
            [ "op_Equality", "="
              "op_Inequality", "<>"
              "op_LessThan", "<"
              "op_GreaterThan", ">"
              "op_LessThanOrEqual", "<="
              "op_GreaterThanOrEqual", ">="
              "op_BooleanOr", "||"
              "op_BooleanAnd", "&&" ]

    /// F# and BCL names that look like union cases but carry no business category:
    /// option/result/voption constructors and well-known sentinel members.
    let private builtinCases =
        set [ "None"; "Some"; "Ok"; "Error"; "ValueNone"; "ValueSome"; "Empty"; "MinValue"; "MaxValue"; "Zero"; "One"; "Null" ]

    let private capitalised (s: string) = s.Length > 0 && Char.IsUpper s[0]

    /// Every segment capitalised and the last not a built-in: `Gold`, `Tier.Gold`.
    let private caseLike (ids: string list) =
        not ids.IsEmpty && ids |> List.forall capitalised && not (builtinCases.Contains(List.last ids))

    let private idTexts (ids: Ident list) = ids |> List.map (fun i -> i.idText)

    let rec private strip (e: SynExpr) =
        match e with
        | SynExpr.Paren(expr = inner)
        | SynExpr.Typed(expr = inner)
        | SynExpr.DebugPoint(innerExpr = inner) -> strip inner
        | _ -> e

    /// `a op b` for the operators above.
    let private infix (e: SynExpr) =
        match strip e with
        | SynExpr.App(isInfix = false; funcExpr = SynExpr.App(isInfix = true; funcExpr = op; argExpr = a); argExpr = b) ->
            let name =
                match strip op with
                | SynExpr.Ident id -> Some id.idText
                | SynExpr.LongIdent(longDotId = SynLongIdent(id = [ id ])) -> Some id.idText
                | _ -> None
            name |> Option.bind operatorText.TryFind |> Option.map (fun o -> o, a, b)
        | _ -> None

    /// The innermost function of a (curried) application.
    let rec private appHead (e: SynExpr) =
        match strip e with
        | SynExpr.App(isInfix = false; funcExpr = f) when (infix e).IsNone -> appHead f
        | SynExpr.TypeApp(expr = f) -> appHead f
        | other -> other

    let private headName (e: SynExpr) =
        match appHead e with
        | SynExpr.Ident id -> Some [ id.idText ]
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> Some(idTexts ids)
        | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) -> Some(idTexts ids)
        | _ -> None

    let private isApp (e: SynExpr) =
        match strip e with
        | SynExpr.App _ -> (infix e).IsNone
        | _ -> false

    let private categoryValue (e: SynExpr) =
        match strip e with
        | SynExpr.Const(SynConst.String(text = s), _) -> Some(StringValue s)
        | SynExpr.Const(SynConst.Char c, _) -> Some(StringValue(string c))
        | SynExpr.Ident id when caseLike [ id.idText ] -> Some(CaseValue id.idText)
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) when caseLike (idTexts ids) -> Some(CaseValue(String.Join(".", idTexts ids)))
        | _ -> None

    let private stringMethods = set [ "StartsWith"; "EndsWith"; "Contains"; "Equals"; "IsMatch" ]

    /// `s.StartsWith "X"`, `s.StartsWith("X", ...)`: the literal.
    let private stringMethodLiteral (e: SynExpr) =
        match strip e with
        | SynExpr.App(isInfix = false; funcExpr = f; argExpr = arg) when (infix e).IsNone ->
            let name =
                match strip f with
                | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> ids |> List.tryLast |> Option.map (fun i -> i.idText)
                | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) -> ids |> List.tryLast |> Option.map (fun i -> i.idText)
                | _ -> None
            let literal =
                match strip arg with
                | SynExpr.Const(SynConst.String(text = s), _) -> Some s
                | SynExpr.Tuple(exprs = first :: _) ->
                    match strip first with
                    | SynExpr.Const(SynConst.String(text = s), _) -> Some s
                    | _ -> None
                | _ -> None
            match name, literal with
            | Some n, Some l when stringMethods.Contains n -> Some l
            | _ -> None
        | _ -> None

    /// Category values a boolean expression tests directly or through a predicate,
    /// each with whether the test excludes the category (`<>`, `not ...`).
    let rec private categoryTests (predicates: Map<string, CategoryValue list>) (negated: bool) (e: SynExpr) : (CategoryValue * bool) list =
        match infix e with
        | Some(("=" | "<>") as op, a, b) ->
            [ categoryValue a; categoryValue b ] |> List.choose id |> List.map (fun v -> v, (negated <> (op = "<>")))
        | Some _ -> []
        | None ->
            match strip e with
            | SynExpr.App(isInfix = false; funcExpr = f; argExpr = arg) when (match strip f with
                                                                             | SynExpr.Ident id -> id.idText = "not"
                                                                             | _ -> false) ->
                categoryTests predicates (not negated) arg
            | _ ->
                match stringMethodLiteral e with
                | Some l -> [ StringValue l, negated ]
                | None when isApp e ->
                    match headName e with
                    | Some ids -> predicates |> Map.tryFind (List.last ids) |> Option.defaultValue [] |> List.map (fun v -> v, negated)
                    | None -> []
                | None -> []

    /// Category values a predicate body decides on, through && / || / not.
    let rec private predicateValues (predicates: Map<string, CategoryValue list>) (e: SynExpr) : CategoryValue list =
        let values =
            match infix e with
            | Some(("&&" | "||"), a, b) -> predicateValues predicates a @ predicateValues predicates b
            | _ -> categoryTests predicates false e |> List.map fst
        List.distinct values

    // ------------------------------------------------------------------ literals

    let private numeric (c: SynConst) : (decimal * NumericForm) option =
        let integral (v: decimal) = Some(v, Integral)
        let rec go c =
            match c with
            | SynConst.Int32 v -> integral (decimal v)
            | SynConst.Int64 v -> integral (decimal v)
            | SynConst.Int16 v -> integral (decimal v)
            | SynConst.SByte v -> integral (decimal v)
            | SynConst.Byte v -> integral (decimal v)
            | SynConst.UInt16 v -> integral (decimal v)
            | SynConst.UInt32 v -> integral (decimal v)
            | SynConst.UInt64 v -> integral (decimal v)
            | SynConst.Decimal v -> Some(v, Fractional)
            | SynConst.Double v when not (Double.IsNaN v || Double.IsInfinity v) && abs v < 1e20 -> Some(decimal v, Fractional)
            | SynConst.Single v when not (Single.IsNaN v || Single.IsInfinity v) && abs v < 1e20f -> Some(decimal v, Fractional)
            | SynConst.UserNum(value = v) -> Scanner.literalValue v |> Option.map (fun d -> d, Integral)
            | SynConst.Measure(constant = inner) -> go inner
            | _ -> None
        go c

    let private inv (d: decimal) = d.ToString(Globalization.CultureInfo.InvariantCulture)

    /// Source-like text of a constant result (for SilentDefault).
    let private constText (c: SynConst) =
        match c with
        | SynConst.String(text = s) -> Some("\"" + s + "\"")
        | SynConst.Char ch -> Some("'" + string ch + "'")
        | SynConst.Bool b -> Some(if b then "true" else "false")
        | SynConst.Decimal v -> Some(inv v + "m")
        | SynConst.Unit -> None
        | other -> numeric other |> Option.map (fst >> inv)

    // ------------------------------------------------------------------ generic traversal

    let private bindingExprs (bs: SynBinding list) = bs |> List.map (fun (SynBinding(expr = e)) -> e)

    let private clauseParts (clauses: SynMatchClause list) =
        clauses |> List.collect (fun (SynMatchClause(whenExpr = w; resultExpr = r)) -> Option.toList w @ [ r ])

    /// Immediate sub-expressions (the default descent).
    let private children (e: SynExpr) : SynExpr list =
        match e with
        | SynExpr.Paren(expr = x)
        | SynExpr.Typed(expr = x)
        | SynExpr.DebugPoint(innerExpr = x)
        | SynExpr.Do(expr = x)
        | SynExpr.Assert(expr = x)
        | SynExpr.Lazy(expr = x)
        | SynExpr.ComputationExpr(expr = x)
        | SynExpr.ArrayOrListComputed(expr = x)
        | SynExpr.IndexFromEnd(expr = x)
        | SynExpr.YieldOrReturn(expr = x)
        | SynExpr.YieldOrReturnFrom(expr = x)
        | SynExpr.DoBang(expr = x)
        | SynExpr.DotGet(expr = x)
        | SynExpr.DotLambda(expr = x)
        | SynExpr.TypeApp(expr = x)
        | SynExpr.TypeTest(expr = x)
        | SynExpr.Upcast(expr = x)
        | SynExpr.Downcast(expr = x)
        | SynExpr.InferredUpcast(expr = x)
        | SynExpr.InferredDowncast(expr = x)
        | SynExpr.AddressOf(expr = x)
        | SynExpr.New(expr = x)
        | SynExpr.Fixed(expr = x)
        | SynExpr.Lambda(body = x)
        | SynExpr.Quote(quotedExpr = x)
        | SynExpr.LongIdentSet(expr = x) -> [ x ]
        | SynExpr.Tuple(exprs = xs)
        | SynExpr.ArrayOrList(exprs = xs) -> xs
        | SynExpr.Record(copyInfo = copy; recordFields = fields) ->
            (copy |> Option.map fst |> Option.toList)
            @ (fields |> List.choose (fun (SynExprRecordField(expr = x)) -> x))
        | SynExpr.AnonRecd(copyInfo = copy; recordFields = fields) ->
            (copy |> Option.map fst |> Option.toList) @ (fields |> List.map (fun (_, _, x) -> x))
        | SynExpr.ObjExpr(argOptions = args; bindings = bs; members = ms) ->
            (args |> Option.map fst |> Option.toList)
            @ bindingExprs bs
            @ (ms |> List.collect (function
                | SynMemberDefn.Member(memberDefn = b) -> bindingExprs [ b ]
                | _ -> []))
        | SynExpr.While(whileExpr = a; doExpr = b)
        | SynExpr.WhileBang(whileExpr = a; doExpr = b)
        | SynExpr.Sequential(expr1 = a; expr2 = b)
        | SynExpr.TryFinally(tryExpr = a; finallyExpr = b)
        | SynExpr.App(funcExpr = a; argExpr = b)
        | SynExpr.DotIndexedGet(objectExpr = a; indexArgs = b)
        | SynExpr.Set(targetExpr = a; rhsExpr = b)
        | SynExpr.DotSet(targetExpr = a; rhsExpr = b)
        | SynExpr.JoinIn(lhsExpr = a; rhsExpr = b)
        | SynExpr.NamedIndexedPropertySet(expr1 = a; expr2 = b)
        | SynExpr.Dynamic(funcExpr = a; argExpr = b)
        | SynExpr.ForEach(enumExpr = a; bodyExpr = b) -> [ a; b ]
        | SynExpr.DotIndexedSet(objectExpr = a; indexArgs = b; valueExpr = c)
        | SynExpr.DotNamedIndexedPropertySet(targetExpr = a; argExpr = b; rhsExpr = c)
        | SynExpr.SequentialOrImplicitYield(expr1 = a; expr2 = b; ifNotStmt = c)
        | SynExpr.For(identBody = a; toBody = b; doBody = c) -> [ a; b; c ]
        | SynExpr.IndexRange(expr1 = a; expr2 = b) -> Option.toList a @ Option.toList b
        | SynExpr.IfThenElse(ifExpr = a; thenExpr = b; elseExpr = c) -> [ a; b ] @ Option.toList c
        | SynExpr.Match(expr = x; clauses = cs)
        | SynExpr.MatchBang(expr = x; clauses = cs) -> x :: clauseParts cs
        | SynExpr.MatchLambda(matchClauses = cs) -> clauseParts cs
        | SynExpr.TryWith(tryExpr = x; withCases = cs) -> x :: clauseParts cs
        | SynExpr.LetOrUse lu -> bindingExprs lu.Bindings @ [ lu.Body ]
        | SynExpr.InterpolatedString(contents = parts) ->
            parts
            |> List.choose (function
                | SynInterpolatedStringPart.FillExpr(fillExpr = x) -> Some x
                | _ -> None)
        | _ -> []

    /// Identifiers and string literals anywhere in an expression (for references and
    /// the question-naming search shared with the lexical analyzer).
    let rec private texts (e: SynExpr) : string list * string list =
        let own =
            match e with
            | SynExpr.Ident id -> [ id.idText ], []
            | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) -> idTexts ids, []
            | SynExpr.DotGet(longDotId = SynLongIdent(id = ids)) -> idTexts ids, []
            | SynExpr.Const(SynConst.String(text = s), _) -> [], [ s ]
            | _ -> [], []
        children e |> List.map texts |> List.fold (fun (i, s) (i2, s2) -> i @ i2, s @ s2) own

    // ------------------------------------------------------------------ constructs

    type private Position =
        /// A declaration body, binding right-hand side or branch result.
        | ValuePos
        /// Anything else: operands, arguments, fields, indexes.
        | OperandPos

    let rec private patternKeys (p: SynPat) : ConstructKind list =
        match p with
        | SynPat.Const(SynConst.String(text = s), _) -> [ CategoryKey(StringValue s) ]
        | SynPat.Const(SynConst.Char c, _) -> [ CategoryKey(StringValue(string c)) ]
        | SynPat.Const(c, _) ->
            match numeric c with
            | Some(v, form) when v <> 0m && v <> 1m -> [ NumericLiteral(v, form) ]
            | _ -> []
        | SynPat.LongIdent(longDotId = SynLongIdent(id = ids); argPats = args) ->
            let own = if caseLike (idTexts ids) then [ CategoryKey(CaseValue(String.Join(".", idTexts ids))) ] else []
            let inner =
                match args with
                | SynArgPats.Pats ps -> ps |> List.collect patternKeys
                | SynArgPats.NamePatPairs(pats = ps) -> ps |> List.collect (fun (NamePatPairField(pat = p)) -> patternKeys p)
            own @ inner
        | SynPat.Named(ident = SynIdent(id, _)) when caseLike [ id.idText ] -> [ CategoryKey(CaseValue id.idText) ]
        | SynPat.Paren(pat = x)
        | SynPat.Typed(pat = x)
        | SynPat.Attrib(pat = x) -> patternKeys x
        | SynPat.Or(lhsPat = a; rhsPat = b)
        | SynPat.ListCons(lhsPat = a; rhsPat = b)
        | SynPat.As(lhsPat = a; rhsPat = b) -> patternKeys a @ patternKeys b
        | SynPat.Tuple(elementPats = ps)
        | SynPat.ArrayOrList(elementPats = ps)
        | SynPat.Ands(pats = ps) -> ps |> List.collect patternKeys
        | _ -> []

    let rec private isCatchAll (p: SynPat) =
        match p with
        | SynPat.Wild _ -> true
        | SynPat.Named(ident = SynIdent(id, _)) -> not (capitalised id.idText)
        | SynPat.Paren(pat = x)
        | SynPat.Typed(pat = x) -> isCatchAll x
        | _ -> false

    /// `Math.Round(x, 2, ...)` / `Decimal.Round(x, 2)`: the digits argument is skipped.
    let private isRoundCall (f: SynExpr) =
        match strip f with
        | SynExpr.LongIdent(longDotId = SynLongIdent(id = ids)) ->
            match idTexts ids |> List.rev with
            | "Round" :: owner :: _ -> owner = "Math" || owner = "Decimal" || owner = "decimal"
            | _ -> false
        | _ -> false

    /// Rule-bearing constructs of an expression, with their lines.
    let rec private constructs (predicates: Map<string, CategoryValue list>) (pos: Position) (e: SynExpr) : Construct list =
        let at (r: range) kind = { Kind = kind; Line = r.StartLine }
        let walk p x = constructs predicates p x
        let walkAll p xs = xs |> List.collect (walk p)
        let exemptions shape (tests: (CategoryValue * bool) list) (r: range) =
            tests |> List.map fst |> List.distinct |> List.map (fun v -> at r (CategoryExemption(v, shape)))
        match e with
        | SynExpr.Const(c, r) ->
            match numeric c with
            | Some(v, form) when (v <> 0m && v <> 1m) || pos = ValuePos -> [ at r (NumericLiteral(v, form)) ]
            | _ -> []
        | SynExpr.Paren(expr = x)
        | SynExpr.Typed(expr = x)
        | SynExpr.DebugPoint(innerExpr = x) -> walk pos x
        | _ when (infix e).IsSome ->
            let op, a, b = (infix e).Value
            let own =
                match op with
                | "="
                | "<>" -> [ categoryValue a; categoryValue b ] |> List.choose id |> List.map (fun v -> at e.Range (CategoryComparison(op, v)))
                | "||" -> exemptions "||" (categoryTests predicates false a @ categoryTests predicates false b) e.Range
                | "&&" -> exemptions "&& not" ((categoryTests predicates false a @ categoryTests predicates false b) |> List.filter snd) e.Range
                | _ -> []
            own @ walk OperandPos a @ walk OperandPos b
        | SynExpr.App(funcExpr = f; argExpr = arg) when isRoundCall f ->
            let args =
                match strip arg with
                | SynExpr.Tuple(exprs = x :: _digits :: rest) -> x :: rest
                | other -> [ other ]
            walk OperandPos f @ walkAll OperandPos args
        | SynExpr.App(funcExpr = f; argExpr = arg) ->
            let own =
                match stringMethodLiteral e with
                | Some l -> [ at e.Range (CategoryCheck l) ]
                | None -> []
            own @ walk OperandPos f @ walk OperandPos arg
        | SynExpr.IfThenElse(ifExpr = c; thenExpr = t; elseExpr = el; range = r) ->
            exemptions "if" (categoryTests predicates false c) r
            @ walk OperandPos c
            @ walk ValuePos t
            @ (el |> Option.map (walk ValuePos) |> Option.defaultValue [])
        | SynExpr.Match(expr = x; clauses = cs)
        | SynExpr.MatchBang(expr = x; clauses = cs) -> walk OperandPos x @ clauses predicates cs
        | SynExpr.MatchLambda(matchClauses = cs) -> clauses predicates cs
        | SynExpr.TryWith(tryExpr = x; withCases = cs) -> walk pos x @ clauses predicates cs
        | SynExpr.LetOrUse lu -> walkAll ValuePos (bindingExprs lu.Bindings) @ walk pos lu.Body
        | SynExpr.Sequential(expr1 = a; expr2 = b) -> walk OperandPos a @ walk pos b
        | _ -> children e |> walkAll OperandPos

    and private clauses predicates (cs: SynMatchClause list) : Construct list =
        cs
        |> List.collect (fun (SynMatchClause(pat = p; whenExpr = w; resultExpr = r; range = range)) ->
            let keys = patternKeys p |> List.map (fun k -> { Kind = k; Line = p.Range.StartLine })
            let silentDefault =
                match w, strip r with
                | None, SynExpr.Const(c, _) when isCatchAll p ->
                    constText c |> Option.map (fun t -> { Kind = SilentDefault t; Line = range.StartLine }) |> Option.toList
                | _ -> []
            // A silent default's constant is reported once, as the default.
            let result = if silentDefault.IsEmpty then constructs predicates ValuePos r else []
            keys @ silentDefault @ (w |> Option.map (constructs predicates OperandPos) |> Option.defaultValue []) @ result)

    // ------------------------------------------------------------------ declarations

    let private attributeName (a: SynAttribute) =
        let (SynLongIdent(id = ids)) = a.TypeName
        ids |> List.tryLast |> Option.map (fun i -> i.idText) |> Option.defaultValue ""

    let private markersOf (attrs: SynAttributes) : Marker list =
        attrs
        |> List.collect (fun l -> l.Attributes)
        |> List.filter (fun a -> attributeName a = "Authority" || attributeName a = "AuthorityAttribute")
        |> List.map (fun a -> { Ids = texts a.ArgExpr |> snd; Line = a.Range.StartLine })

    let private hasLiteralAttr (attrs: SynAttributes) =
        attrs |> List.collect (fun l -> l.Attributes) |> List.exists (fun a -> attributeName a = "Literal" || attributeName a = "LiteralAttribute")

    let rec private patName (p: SynPat) =
        match p with
        | SynPat.LongIdent(longDotId = SynLongIdent(id = ids)) -> ids |> List.tryLast |> Option.map (fun i -> i.idText)
        | SynPat.Named(ident = SynIdent(id, _)) -> Some id.idText
        | SynPat.Paren(pat = x)
        | SynPat.Typed(pat = x)
        | SynPat.Attrib(pat = x) -> patName x
        | _ -> None

    let private hasArgs (p: SynPat) =
        match p with
        | SynPat.LongIdent(argPats = SynArgPats.Pats(_ :: _)) -> true
        | _ -> false

    /// A declaration with the expressions that form its body.
    type private Found =
        { Decl: Decl
          Body: SynExpr list
          DocLines: string list
          /// First line of the declaration including its doc comment and attributes.
          StartLine: int }

    let private join (a: string) (b: string) = if a = "" then b else a + "." + b

    let private mkDecl name qualified kind (line: int) (endLine: int) (col: int) parent markers isConst =
        { Name = name
          Qualified = qualified
          Kind = kind
          Line = line
          EndLine = max line endLine
          Indent = col
          Markers = markers
          Parent = parent
          References = Set.empty
          BodyTexts = []
          IsConstant = isConst
          Comments = [] }

    let private docLines (doc: PreXmlDoc) = doc.ToXmlDoc(false, None).UnprocessedLines |> List.ofArray

    let private found decl body doc (start: int) =
        { Decl = decl
          Body = body
          DocLines = docLines doc
          StartLine = start }

    let private bindingDecl (prefix: string) (parent: string option) (kind: DeclKind) (b: SynBinding) : Found option =
        let (SynBinding(attributes = attrs; headPat = pat; expr = e; xmlDoc = doc; range = r)) = b
        patName pat
        |> Option.filter (fun n -> n <> "_" && n <> "__")
        |> Option.map (fun name ->
            let isConst =
                hasLiteralAttr attrs
                || (not (hasArgs pat)
                    && (match strip e with
                        | SynExpr.Const(c, _) -> (numeric c).IsSome
                        | _ -> false))
            let decl = mkDecl name (join prefix name) kind pat.Range.StartLine (max r.EndLine e.Range.EndLine) pat.Range.StartColumn parent (markersOf attrs) isConst
            found decl [ e ] doc (min r.StartLine pat.Range.StartLine))

    let rec private moduleDecls (prefix: string) (parent: string option) (decls: SynModuleDecl list) : Found list * SynExpr list * Marker list =
        let results =
            decls
            |> List.map (fun d ->
                match d with
                | SynModuleDecl.Let(bindings = bs) ->
                    // `let _ = ...` style bindings have no name: module-level code.
                    let pairs = bs |> List.map (fun b -> b, bindingDecl prefix parent LetDecl b)
                    let named = pairs |> List.choose snd
                    let unnamed = pairs |> List.filter (snd >> Option.isNone) |> List.map (fun (SynBinding(expr = e), _) -> e)
                    named, unnamed, []
                | SynModuleDecl.Expr(expr = e) -> [], [ e ], []
                | SynModuleDecl.NestedModule(moduleInfo = SynComponentInfo(attributes = attrs; longId = ids; xmlDoc = doc; range = r); decls = inner) ->
                    let name = String.Join(".", idTexts ids)
                    let qualified = join prefix name
                    let m = found (mkDecl name qualified ModuleDecl r.StartLine r.StartLine r.StartColumn parent (markersOf attrs) false) [] doc r.StartLine
                    let f, es, orphans = moduleDecls qualified (Some qualified) inner
                    m :: f, es, orphans
                | SynModuleDecl.Types(typeDefns = tds) -> tds |> List.collect (typeDecls prefix parent), [], []
                | SynModuleDecl.Exception(exnDefn = SynExceptionDefn(exnRepr = SynExceptionDefnRepr(attributes = attrs; caseName = SynUnionCase(ident = SynIdent(id, _)); xmlDoc = doc; range = r))) ->
                    [ found (mkDecl id.idText (join prefix id.idText) ExceptionDecl r.StartLine r.EndLine r.StartColumn parent (markersOf attrs) false) [] doc r.StartLine ], [], []
                | SynModuleDecl.Attributes(attributes = attrs) -> [], [], markersOf attrs
                | _ -> [], [], [])
        results |> List.collect (fun (a, _, _) -> a), results |> List.collect (fun (_, b, _) -> b), results |> List.collect (fun (_, _, c) -> c)

    and private typeDecls (prefix: string) (parent: string option) (td: SynTypeDefn) : Found list =
        let (SynTypeDefn(typeInfo = SynComponentInfo(attributes = attrs; longId = ids; xmlDoc = doc; range = info); typeRepr = repr; members = extra; range = r)) = td
        let name = String.Join(".", idTexts ids)
        let qualified = join prefix name
        let me = Some qualified
        let typeDecl = found (mkDecl name qualified TypeDecl info.StartLine r.EndLine info.StartColumn parent (markersOf attrs) false) [] doc info.StartLine
        let memberDecls (ms: SynMemberDefn list) =
            ms
            |> List.collect (fun m ->
                match m with
                | SynMemberDefn.Member(memberDefn = b) -> bindingDecl qualified me MemberDecl b |> Option.toList
                | SynMemberDefn.LetBindings(bindings = bs) -> bs |> List.choose (bindingDecl qualified me LetDecl)
                | SynMemberDefn.AutoProperty(attributes = a; ident = id; synExpr = e; xmlDoc = d; range = mr) ->
                    [ found (mkDecl id.idText (join qualified id.idText) MemberDecl mr.StartLine mr.EndLine mr.StartColumn me (markersOf a) false) [ e ] d mr.StartLine ]
                | _ -> [])
        let reprDecls =
            match repr with
            | SynTypeDefnRepr.Simple(simpleRepr = SynTypeDefnSimpleRepr.Union(unionCases = cases)) ->
                cases
                |> List.map (fun (SynUnionCase(attributes = a; ident = SynIdent(id, _); xmlDoc = d; range = cr)) ->
                    found (mkDecl id.idText (join qualified id.idText) UnionCaseDecl cr.StartLine cr.EndLine cr.StartColumn me (markersOf a) false) [] d cr.StartLine)
            | SynTypeDefnRepr.Simple(simpleRepr = SynTypeDefnSimpleRepr.Record(recordFields = fields)) ->
                fields
                |> List.choose (fun (SynField(attributes = a; idOpt = idOpt; xmlDoc = d; range = fr)) ->
                    idOpt
                    |> Option.map (fun id ->
                        found (mkDecl id.idText (join qualified id.idText) FieldDecl fr.StartLine fr.EndLine fr.StartColumn me (markersOf a) false) [] d fr.StartLine))
            | SynTypeDefnRepr.ObjectModel(members = ms) -> memberDecls ms
            | _ -> []
        typeDecl :: reprDecls @ memberDecls extra

    // ------------------------------------------------------------------ files

    type private Parsed =
        { File: SourceFile
          Found: Found list
          ModuleCode: SynExpr list
          Orphans: Marker list
          /// (start line, end line, text without the comment markers)
          Comments: (int * int * string) list }

    let private commentText (lines: string[]) (r: range) =
        let lineText n = if n >= 1 && n <= lines.Length then lines[n - 1] else ""
        let slice (l: string) (s: int) (e: int) =
            let s = min (max 0 s) l.Length
            l.Substring(s, max 0 (min e l.Length - s))
        let raw =
            if r.StartLine = r.EndLine then slice (lineText r.StartLine) r.StartColumn r.EndColumn
            else
                [ r.StartLine .. r.EndLine ]
                |> List.map (fun n ->
                    let l = lineText n
                    if n = r.StartLine then slice l r.StartColumn l.Length
                    elif n = r.EndLine then slice l 0 r.EndColumn
                    else l)
                |> String.concat "\n"
        raw.TrimStart('/').Trim()

    let private parsedOf (file: SourceFile) (tree: ParsedInput) : Parsed =
        let lines = file.Text.Replace("\r\n", "\n").Split('\n')
        match tree with
        | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules; trivia = trivia)) ->
            let parts =
                modules
                |> List.map (fun (SynModuleOrNamespace(longId = ids; kind = kind; decls = decls; attribs = attrs)) ->
                    let prefix = if kind = SynModuleOrNamespaceKind.AnonModule then "" else String.Join(".", idTexts ids)
                    let f, code, orphans = moduleDecls prefix None decls
                    f, code, orphans @ markersOf attrs)
            let comments =
                trivia.CodeComments
                |> List.map (function
                    | CommentTrivia.LineComment r
                    | CommentTrivia.BlockComment r -> r.StartLine, r.EndLine, commentText lines r)
            { File = file
              Found = parts |> List.collect (fun (f, _, _) -> f)
              ModuleCode = parts |> List.collect (fun (_, c, _) -> c)
              Orphans = parts |> List.collect (fun (_, _, o) -> o)
              Comments = comments }
        | ParsedInput.SigFile _ -> { File = file; Found = []; ModuleCode = []; Orphans = []; Comments = [] }

    /// Comments attached to a declaration: its doc comment, comments inside its extent,
    /// and the run of comments immediately above it.
    let private attachedComments (p: Parsed) (f: Found) =
        let inside =
            p.Comments |> List.filter (fun (s, _, _) -> s >= f.StartLine && s <= f.Decl.EndLine) |> List.map (fun (_, _, t) -> t)
        let rec above line acc =
            match p.Comments |> List.tryFind (fun (_, e, _) -> e = line - 1) with
            | Some(s, _, t) -> above s (t :: acc)
            | None -> acc
        above f.StartLine [] @ (f.DocLines |> List.map (fun l -> l.Trim())) @ inside

    let private generated (file: SourceFile) =
        let header = file.Text.Replace("\r\n", "\n").Split('\n') |> Array.truncate 5 |> String.concat "\n"
        file.RelativePath.EndsWith(".g.fs", StringComparison.OrdinalIgnoreCase)
        || header.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
        || header.Contains("@generated", StringComparison.Ordinal)

    /// Category predicates declared in the audited files: functions whose body is a
    /// category test (`let isX c = c.Kind = X`). Two rounds resolve a predicate that
    /// calls another. Name-based (untyped).
    let private predicatesOf (parsed: Parsed list) =
        let functions =
            parsed
            |> List.collect (fun p -> p.Found)
            |> List.filter (fun f -> f.Decl.Kind = LetDecl || f.Decl.Kind = MemberDecl)
            |> List.collect (fun f -> f.Body |> List.map (fun b -> f.Decl.Name, b))
        let round (known: Map<string, CategoryValue list>) =
            functions
            |> List.fold
                (fun (m: Map<string, CategoryValue list>) (name, body) ->
                    match predicateValues known body with
                    | [] -> m
                    | vs -> m.Add(name, (m.TryFind name |> Option.defaultValue []) @ vs |> List.distinct))
                Map.empty
        round (round Map.empty)

    let private scanParsed (predicates: Map<string, CategoryValue list>) (p: Parsed) : ScannedFile =
        let decls =
            p.Found
            |> List.map (fun f ->
                let ids, strings = f.Body |> List.map texts |> List.fold (fun (i, s) (i2, s2) -> i @ i2, s @ s2) ([], [])
                f,
                { f.Decl with
                    References = Set.ofList ids
                    BodyTexts = ids @ strings
                    Comments = attachedComments p f })
        let owned =
            decls |> List.collect (fun (f, d) -> f.Body |> List.collect (constructs predicates ValuePos) |> List.map (fun c -> c, Some d))
        let moduleLevel = p.ModuleCode |> List.collect (constructs predicates OperandPos) |> List.map (fun c -> c, None)
        { File = p.File
          Decls = decls |> List.map snd |> List.sortBy (fun d -> d.Line, d.Indent)
          Constructs = owned @ moduleLevel |> List.sortBy (fun (c, d) -> c.Line, (d |> Option.map (fun d -> d.Qualified) |> Option.defaultValue ""))
          OrphanMarkers = p.Orphans
          Generated = generated p.File }

    /// Scan a set of files of one role; category predicates are shared across the set.
    let scanAll (files: SourceFile list) : Result<ScannedFile list, string list> =
        let parsed = files |> List.map (fun f -> f, parse f)
        let errors = parsed |> List.collect (fun (_, r) -> match r with Error es -> es | Ok _ -> [])
        if not errors.IsEmpty then Error errors
        else
            let ps = parsed |> List.choose (fun (f, r) -> match r with Ok t -> Some(parsedOf f t) | Error _ -> None)
            let predicates = predicatesOf ps
            Ok(ps |> List.map (scanParsed predicates))
