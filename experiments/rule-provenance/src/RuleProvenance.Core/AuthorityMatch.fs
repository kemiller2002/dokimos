namespace RuleProvenance

open System
open System.Globalization
open System.Text.RegularExpressions

/// Which matching rule decided a construct's authority (reported in messages).
type MatchRule =
    /// (a) an Authority marker citing accepted records.
    | MarkerRule
    /// (b) the value equals a declared parameter of an accepted record, or the
    /// category is named by an accepted record.
    | ParameterRule
    /// (c) a comment in the declaration cites an accepted record and the value
    /// matches that record's parameters, if it declares any.
    | CitationRule

/// The authority verdict for one rule-bearing construct (ast analyzer).
type ConstructAuthority =
    | Supported of rule: MatchRule * detail: string * refs: string list
    /// The value matches only superseded records' parameters (or names).
    | SupersededOnly of detail: string * refs: string list
    | Unsupported of detail: string * refs: string list

/// v2 authority matching: pure functions from a construct, the record ids cited in
/// its declaration's comments, and the handoff to a verdict. Nothing here knows any
/// corpus value; matching is by declared parameters, record statements and citations.
module AuthorityMatch =
    let private inv (d: decimal) = d.ToString(CultureInfo.InvariantCulture)

    let private idPattern = Regex(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+\b", RegexOptions.CultureInvariant)

    /// Record ids cited in comment text (any token shaped like a record id).
    let citedIds (comments: string list) =
        comments |> List.collect (fun c -> idPattern.Matches(c) |> Seq.map (fun m -> m.Value) |> List.ofSeq) |> List.distinct

    /// A record with what matching needs, decisions and requirements alike.
    type private Record =
        { Id: string
          Status: RecordStatus
          Kind: string
          SupersededBy: string option
          Parameters: (string * string) list
          Statement: string }

    let private records (h: Handoff) =
        (h.Decisions
         |> List.map (fun d ->
             { Id = d.Id
               Status = d.Status
               Kind = d.Kind
               SupersededBy = d.SupersededBy
               Parameters = d.Parameters
               Statement = d.Statement }))
        @ (h.Requirements
           |> List.map (fun r ->
               { Id = r.Id
                 Status = r.Status
                 Kind = "requirement"
                 SupersededBy = r.SupersededBy
                 Parameters = []
                 Statement = r.Statement }))

    let private statusText (r: Record) =
        match r.Status with
        | Accepted -> "accepted"
        | Superseded -> "superseded" + (r.SupersededBy |> Option.map (fun s -> " by " + s) |> Option.defaultValue "")
        | Proposed -> "proposed"
        | Rejected -> "rejected"

    let private refsOf (r: Record) = r.Id :: Option.toList r.SupersededBy

    /// How a parameter value is written: with a decimal point it is fractional.
    let private formOf (text: string) = if text.Contains '.' then Fractional else Integral

    type private ParamHit = { Record: Record; Key: string; SameForm: bool }

    let private paramHits (rs: Record list) (value: decimal) (form: NumericForm) =
        [ for r in rs do
              for (k, v) in r.Parameters do
                  match Scanner.literalValue v with
                  | Some x when x = value -> yield { Record = r; Key = k; SameForm = (formOf v = form) }
                  | _ -> () ]

    let private hitText (h: ParamHit) =
        let form = if h.SameForm then "same form" else "value only"
        $"{h.Record.Id}.{h.Key} ({statusText h.Record}, {form})"

    /// The record names the category: a parameter key segment equals it, or its
    /// statement contains it as a whole word. String literals match case-sensitively
    /// (they are data); union-case names case-insensitively (prose lowercases them).
    let private names (v: CategoryValue) (r: Record) =
        let n = CategoryValue.name v
        let keyHit = r.Parameters |> List.exists (fun (k, _) -> k.Split('.') |> Array.exists (fun seg -> seg = n))
        let options =
            match v with
            | StringValue _ -> RegexOptions.CultureInvariant
            | CaseValue _ -> RegexOptions.CultureInvariant ||| RegexOptions.IgnoreCase
        n.Trim().Length > 0
        && (keyHit || Regex.IsMatch(r.Statement, @"(?<![\w-])" + Regex.Escape n + @"(?![\w-])", options))

    /// What value a construct carries, for matching.
    type private Value =
        | Number of decimal * NumericForm
        | Category of CategoryValue * exemption: bool
        | Opaque

    let private valueOf (k: ConstructKind) =
        match k with
        | NumericLiteral(v, form) -> Number(v, form)
        | DecimalLiteral v -> Number(v, Fractional)
        | SilentDefault text when text.StartsWith "\"" -> Category(StringValue(text.Trim('"')), false)
        | SilentDefault text ->
            match Scanner.literalValue text with
            | Some v -> Number(v, (if text.Contains '.' || text.EndsWith "m" then Fractional else Integral))
            | None -> Opaque
        | CategoryComparison(_, v)
        | CategoryKey v -> Category(v, false)
        | CategoryExemption(v, _) -> Category(v, true)
        | CategoryCheck lit
        | CasePattern lit -> Category(StringValue lit, false)
        | ComparisonAgainstConstant _
        | Branch _
        | Refusal _ -> Opaque

    let private ids (rs: Record list) = rs |> List.collect refsOf |> List.distinct |> List.sort

    let private join (xs: string list) = String.Join(", ", xs)

    /// Assess one construct. `cited` are the record ids cited in its declaration's
    /// comments. Markers are handled by the caller (rule (a)).
    let assess (h: Handoff) (cited: string list) (k: ConstructKind) : ConstructAuthority =
        let all = records h
        let byId id = all |> List.tryFind (fun r -> r.Id = id)
        let citedRecords = cited |> List.choose byId
        let unknownCited = cited |> List.filter (fun id -> (byId id).IsNone)
        let citedAccepted = citedRecords |> List.filter (fun r -> r.Status = Accepted)
        let citedSuperseded = citedRecords |> List.filter (fun r -> r.Status = Superseded)
        let citedUnauthorized = citedRecords |> List.filter (fun r -> r.Status = Proposed || r.Status = Rejected)
        let accepted = all |> List.filter (fun r -> r.Status = Accepted)
        let superseded = all |> List.filter (fun r -> r.Status = Superseded)
        let citationNotes (extra: string list) =
            [ if not citedUnauthorized.IsEmpty then
                  yield
                      "the declaration's comment cites "
                      + join (citedUnauthorized |> List.map (fun r -> $"{r.Id} ({statusText r})"))
                      + ", which authorizes nothing"
              if not unknownCited.IsEmpty then
                  yield "its comment cites " + join unknownCited + ", which is not a record in the handoff"
              yield! extra ]
            |> String.concat "; "
        let unsupported detail refs =
            Unsupported(detail, (refs @ ids citedUnauthorized) |> List.distinct |> List.sort)
        match valueOf k with
        | Number(v, form) ->
            let shown = inv v
            let citedHit = paramHits citedAccepted v form
            let citedSupHit = paramHits citedSuperseded v form
            let accHits = paramHits accepted v form
            let supHits = paramHits superseded v form
            let mismatched =
                citedRecords
                |> List.filter (fun r -> not r.Parameters.IsEmpty && (paramHits [ r ] v form).IsEmpty)
                |> List.map (fun r ->
                    let declared = join (r.Parameters |> List.map (fun (k, x) -> k + "=" + x))
                    $"cited {r.Id} declares {declared}, not {shown}")
            match citedHit, citedSupHit with
            | h :: _, _ -> Supported(CitationRule, $"value {shown} = {hitText h}, cited in the declaration's comment", refsOf h.Record)
            | [], h :: _ -> SupersededOnly($"value {shown} = {hitText h}, which the declaration's comment cites", ids (citedSuperseded @ [ h.Record ]))
            | [], [] ->
                let sameAcc = accHits |> List.filter (fun x -> x.SameForm)
                let sameSup = supHits |> List.filter (fun x -> x.SameForm)
                let note = if mismatched.IsEmpty then "" else " (" + join mismatched + ")"
                match sameAcc, sameSup, accHits, supHits with
                | h :: _, _, _, _ -> Supported(ParameterRule, $"value {shown} = {hitText h}{note}", refsOf h.Record)
                | [], h :: _, _, _ -> SupersededOnly($"value {shown} = {hitText h} and no accepted parameter has the same form{note}", ids [ h.Record ])
                | [], [], h :: _, _ -> Supported(ParameterRule, $"value {shown} = {hitText h}{note}", refsOf h.Record)
                | [], [], [], h :: _ -> SupersededOnly($"value {shown} = {hitText h} and no accepted parameter{note}", ids [ h.Record ])
                | [], [], [], [] ->
                    match citedAccepted |> List.filter (fun r -> r.Parameters.IsEmpty) with
                    | r :: _ when mismatched.IsEmpty ->
                        Supported(CitationRule, $"the declaration's comment cites accepted {r.Id}, which declares no parameters", [ r.Id ])
                    | _ ->
                        let basis = $"value {shown} equals no declared parameter of an accepted or superseded record"
                        unsupported (citationNotes (basis :: mismatched)) (ids citedRecords)
        | Category(v, exemption) ->
            let shown = CategoryValue.show v
            let namedBy rs = rs |> List.filter (names v)
            let unauthorizedNaming = all |> List.filter (fun r -> r.Status = Proposed || r.Status = Rejected) |> namedBy
            // An exemption needs a record that grants it: a cited accepted record naming
            // the category, or an accepted exception-kind record naming it. A plain
            // category test is also supported by any accepted record naming the category.
            let grantors = if exemption then accepted |> List.filter (fun r -> r.Kind = "exception") else accepted
            match namedBy citedAccepted, namedBy citedSuperseded, namedBy grantors, namedBy superseded with
            | r :: _, _, _, _ -> Supported(CitationRule, $"{shown} is named by accepted {r.Id}, cited in the declaration's comment", [ r.Id ])
            | [], r :: _, _, _ -> SupersededOnly($"{shown} is named by {r.Id} ({statusText r}), which the declaration's comment cites", refsOf r)
            | [], [], r :: _, _ ->
                let what = if exemption then "accepted exception record" else "accepted"
                Supported(ParameterRule, $"{shown} is named by {what} {r.Id}", [ r.Id ])
            | [], [], [], r :: _ when not exemption -> SupersededOnly($"{shown} is named only by {r.Id} ({statusText r})", refsOf r)
            | _ ->
                let proposed =
                    if unauthorizedNaming.IsEmpty then []
                    else [ $"{shown} is named only by " + join (unauthorizedNaming |> List.map (fun r -> $"{r.Id} ({statusText r})")) ]
                let need =
                    if exemption then $"no cited record and no accepted exception record names {shown}"
                    else $"no accepted record names {shown}"
                unsupported (citationNotes (need :: proposed)) (ids (citedRecords @ unauthorizedNaming))
        | Opaque -> unsupported (citationNotes [ "no value to match" ]) (ids citedRecords)

    let ruleText =
        function
        | MarkerRule -> "(a) marker"
        | ParameterRule -> "(b) parameter"
        | CitationRule -> "(c) comment citation"
