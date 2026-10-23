namespace RuleProvenance

open System
open System.Globalization
open System.Text.RegularExpressions

/// Repository-relative glob matching (same semantics as Dokimos.Core Glob):
/// `**` spans directories, `*` and `?` stay within one segment.
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

    let anyMatch (globs: string list) (path: string) = globs |> List.exists (fun g -> (toRegex g).IsMatch(path.Replace('\\', '/')))

/// How a cited ID resolves against the handoff.
type AuthorityResolution =
    | AcceptedRecord of owner: string
    | SupersededRecord of owner: string * supersededBy: string option
    | UnauthorizedDecision of status: string * owner: string
    | QuestionRef of PolicyQuestion
    | ObservationRef of Observation
    | InvariantRef of Invariant * derivedFromAccepted: bool
    | UnknownRef

/// VAC-002-P3-03-R1 / VC-004 R1-R3: static provenance analysis of constructed and
/// legacy code. Pure: scanned files and the handoff in, raw findings out.
module Provenance =
    let private inv (d: decimal) = d.ToString(CultureInfo.InvariantCulture)

    let resolve (h: Handoff) (id: string) : AuthorityResolution =
        let req = h.Requirements |> List.tryFind (fun r -> r.Id = id)
        let dec = h.Decisions |> List.tryFind (fun d -> d.Id = id)
        let isAccepted (x: string) =
            (h.Requirements |> List.exists (fun r -> r.Id = x && r.Status = Accepted))
            || (h.Decisions |> List.exists (fun d -> d.Id = x && d.Status = Accepted))
        match req, dec with
        | Some r, _ when r.Status = Accepted -> AcceptedRecord r.Owner
        | Some r, _ -> SupersededRecord(r.Owner, r.SupersededBy)
        | None, Some d ->
            match d.Status with
            | Accepted -> AcceptedRecord d.Owner
            | Superseded -> SupersededRecord(d.Owner, d.SupersededBy)
            | Proposed -> UnauthorizedDecision("proposed", d.Owner)
            | Rejected -> UnauthorizedDecision("rejected", d.Owner)
        | None, None ->
            match h.PolicyQuestions |> List.tryFind (fun q -> q.Id = id) with
            | Some q -> QuestionRef q
            | None ->
                match h.Observations |> List.tryFind (fun o -> o.Id = id) with
                | Some o -> ObservationRef o
                | None ->
                    match h.Invariants |> List.tryFind (fun i -> i.Id = id) with
                    | Some i -> InvariantRef(i, i.DerivedFrom |> List.forall isAccepted)
                    | None -> UnknownRef

    /// `a` and `b` name the same thing when one is a separator-aligned suffix of the other.
    let alignedSuffix (sep: char) (a: string) (b: string) =
        a = b || a.EndsWith(string sep + b, StringComparison.Ordinal) || b.EndsWith(string sep + a, StringComparison.Ordinal)

    let private normalizeId (s: string) = String(s.ToUpperInvariant() |> Seq.filter Char.IsLetterOrDigit |> Array.ofSeq)

    /// The declaration's body names the question in a string or identifier, directly or
    /// through one same-file declaration it references (e.g. `Error pendingVoidRefusal`).
    let private bodyNamesQuestion (fileDecls: Decl list) (d: Decl) (questionId: string) =
        let target = normalizeId questionId
        let names (x: Decl) = x.BodyTexts |> List.exists (fun t -> (normalizeId t).Contains(target, StringComparison.Ordinal))
        names d
        || fileDecls |> List.exists (fun x -> x.Qualified <> d.Qualified && d.References.Contains x.Name && x.Kind = LetDecl && names x)

    /// Decision parameters that are decimal values: (decision, parameter name, value).
    let decisionValues (h: Handoff) =
        [ for d in h.Decisions do
              for (k, v) in d.Parameters do
                  match Scanner.literalValue v with
                  | Some value -> yield d, k, value
                  | None -> () ]

    let private constructValues (cs: Construct list) =
        cs
        |> List.choose (fun c ->
            match c.Kind with
            | DecimalLiteral v -> Some v
            | ComparisonAgainstConstant(_, operand) -> Scanner.literalValue operand
            | _ -> None)
        |> List.distinct

    /// Decisions whose parameter equals a literal in the constructs, with a note.
    let private valueMatches (h: Handoff) (cs: Construct list) =
        let values = constructValues cs
        decisionValues h
        |> List.filter (fun (_, _, v) -> values |> List.contains v)
        |> List.sortBy (fun (d, k, _) -> d.Id, k)
        |> List.map (fun (d, k, v) ->
            let status =
                match d.Status with
                | Accepted -> "accepted"
                | Superseded -> "superseded" + (d.SupersededBy |> Option.map (fun s -> " by " + s) |> Option.defaultValue "")
                | Proposed -> "proposed"
                | Rejected -> "rejected"
            d, $"Value {inv v} equals {d.Id}.{k} ({status})")

    let private describeAll (cs: Construct list) =
        cs |> List.sortBy (fun c -> c.Line) |> List.map Construct.describe |> String.concat "; "

    let private finding cls certainty (loc: Location) refs message owner =
        { Class = cls
          Certainty = certainty
          Location = loc
          AuthorityRefs = refs |> List.distinct
          Message = message
          Owner = owner
          FromValidatedChecker = false }

    // ------------------------------------------------------------------ constructed code

    type private Located = { File: ScannedFile; Decl: Decl }

    let private chain (f: ScannedFile) (d: Decl) =
        let rec go (x: Decl) acc =
            let acc = acc @ [ x ]
            match x.Parent |> Option.bind (fun p -> f.Decls |> List.tryFind (fun y -> y.Qualified = p)) with
            | Some parent -> go parent acc
            | _ -> acc
        go d []

    let private findTraceDecl (all: Located list) (tc: TraceConstruct) =
        let candidates = all |> List.filter (fun l -> alignedSuffix '.' tc.Symbol l.Decl.Qualified)
        let preferred = candidates |> List.filter (fun l -> alignedSuffix '/' tc.File l.File.File.RelativePath)
        (if preferred.IsEmpty then candidates else preferred)
        |> List.sortBy (fun l -> l.File.File.RelativePath, l.Decl.Line)
        |> List.tryHead

    let private sameDecl (a: Located) (b: Located) =
        a.File.File.RelativePath = b.File.File.RelativePath && a.Decl.Qualified = b.Decl.Qualified && a.Decl.Line = b.Decl.Line

    /// Validate one cited ID. `disposition` is the trace's disposition for the symbol, if traced.
    let private checkCitation (cfg: AuditConfig) (h: Handoff) (loc: Location) (fileDecls: Decl list) (d: Decl) (disposition: Disposition option) (source: string) (id: string) =
        let proven cls refs msg = Some(finding cls Proven loc refs msg cfg.ConstructingOwner)
        match resolve h id with
        | AcceptedRecord _ -> None
        | InvariantRef(_, true) -> None
        | InvariantRef(i, false) ->
            proven MissingProvenance (id :: i.DerivedFrom) $"{source} cites invariant {id}, which derives from a record that is not accepted."
        | SupersededRecord(_, by) ->
            let byText = by |> Option.map (fun b -> $", superseded by {b}") |> Option.defaultValue ""
            proven SupersededAuthority (id :: Option.toList by) $"{source} cites {id}, which is not accepted{byText}. The rule must trace to the accepted record."
        | UnauthorizedDecision(status, _) ->
            proven SelfAuthorized [ id ] $"{source} cites {id}, a {status} decision: no human authorized it, so the rule authorizes itself."
        | ObservationRef o ->
            proven MissingProvenance [ id ] $"{source} cites observation {id} ({o.Kind}). Observed behaviour carries no authority (authority: unverified); it cannot justify constructed code."
        | UnknownRef ->
            proven MissingProvenance [ id ] $"{source} cites {id}, which is not a record in handoff {h.HandoffId}."
        | QuestionRef q ->
            let names = bodyNamesQuestion fileDecls d q.Id
            match disposition, names with
            | Some Authorized, _ ->
                proven UnresolvedQuestionEncodedAsRule [ id ] $"{source} cites unresolved question {id} but the trace marks the construct 'authorized'. Permitted outcome is {q.PermittedOutcome}; the question is not a rule."
            | _, false ->
                proven UnresolvedQuestionEncodedAsRule [ id ] $"{source} cites unresolved question {id} but its body never names {id} in a refusal or recorded code: the question is decided (allowed or forbidden) instead of {q.PermittedOutcome}."
            | _, true -> None

    let constructed (cfg: AuditConfig) (h: Handoff) (handoffDigest: string) (trace: (string * Trace) option) (files: ScannedFile list) : RawFinding list =
        let all = files |> List.collect (fun f -> f.Decls |> List.map (fun d -> { File = f; Decl = d }))
        let traceConstructs = trace |> Option.map (snd >> fun t -> t.Constructs) |> Option.defaultValue []
        let traced = traceConstructs |> List.map (fun tc -> tc, findTraceDecl all tc)
        let traceFor (l: Located) =
            traced |> List.tryPick (fun (tc, found) -> match found with Some x when sameDecl x l -> Some tc | _ -> None)
        let locOf (l: Located) = { Path = l.File.File.RelativePath; Line = Some l.Decl.Line; Symbol = Some l.Decl.Qualified }

        // 1. Every marker citation resolves to accepted authority.
        let markerFindings =
            all
            |> List.collect (fun l ->
                let disposition = traceFor l |> Option.map (fun tc -> tc.Disposition)
                l.Decl.Markers
                |> List.collect (fun m ->
                    if m.Ids.IsEmpty then
                        [ finding MissingProvenance Proven (locOf l) [] $"Authority marker on {l.Decl.Qualified} cites no record." cfg.ConstructingOwner ]
                    else
                        m.Ids |> List.choose (checkCitation cfg h (locOf l) l.File.Decls l.Decl disposition $"Authority marker on {l.Decl.Qualified}")))

        let orphanFindings =
            files
            |> List.collect (fun f ->
                f.OrphanMarkers
                |> List.map (fun m ->
                    finding MissingProvenance Proven { Path = f.File.RelativePath; Line = Some m.Line; Symbol = None } m.Ids
                        "Authority marker is not attached to a recognisable declaration, so it covers nothing." cfg.ConstructingOwner))

        // 2. Every rule-bearing construct is covered by a marker or a traced authority.
        let covered (f: ScannedFile) (d: Decl) =
            chain f d
            |> List.exists (fun x ->
                (x.Markers |> List.exists (fun m -> not m.Ids.IsEmpty))
                || (match traceFor { File = f; Decl = x } with
                    | Some tc -> tc.Disposition = Technical || not tc.Authority.IsEmpty
                    | None -> false))
        let coverageFindings =
            files
            |> List.filter (fun f -> not (Glob.anyMatch cfg.Excludes f.File.RelativePath))
            |> List.collect (fun f ->
                f.Constructs
                |> List.filter (fun (_, d) -> match d with Some d -> not (covered f d) | None -> true)
                |> List.groupBy (fun (_, d) -> d |> Option.map (fun d -> d.Qualified, d.Line))
                |> List.map (fun (key, group) ->
                    let cs = group |> List.map fst
                    let matches = valueMatches h cs
                    let refs = matches |> List.collect (fun (d, _) -> d.Id :: Option.toList d.SupersededBy)
                    let notes = matches |> List.map snd
                    let noteText = if notes.IsEmpty then "" else " " + String.Join("; ", notes) + "."
                    let symbol = key |> Option.map fst
                    let line = cs |> List.map (fun c -> c.Line) |> List.min
                    let where = symbol |> Option.defaultValue "module-level code"
                    finding MissingProvenance Proven { Path = f.File.RelativePath; Line = Some line; Symbol = symbol } refs
                        $"Rule-bearing construct(s) in {where} carry no Authority marker and no traced authority: {describeAll cs}.{noteText}"
                        cfg.ConstructingOwner))

        // 3. Trace and source agree (when a trace is supplied).
        let traceFindings =
            match trace with
            | None -> []
            | Some(tracePath, t) ->
                let at symbol = { Path = tracePath; Line = None; Symbol = symbol }
                let handoffChecks =
                    [ if t.HandoffSha256 <> handoffDigest then
                          yield finding TraceMismatch Proven (at None) []
                              $"Trace was built from handoff {t.HandoffSha256}, not the audited handoff {handoffDigest}: its provenance is stale." cfg.ConstructingOwner
                      if t.HandoffId <> h.HandoffId then
                          yield finding TraceMismatch Proven (at None) [] $"Trace names handoff {t.HandoffId}, the audited handoff is {h.HandoffId}." cfg.ConstructingOwner ]
                let constructChecks =
                    traced
                    |> List.collect (fun (tc, found) ->
                        let ids = tc.Authority |> List.map (fun a -> a.Id)
                        let dispositionChecks loc =
                            let hasQuestion = tc.Authority |> List.exists (fun a -> a.Kind = "question" || (match resolve h a.Id with QuestionRef _ -> true | _ -> false))
                            [ if tc.Disposition = DeferredPendingQuestion && not hasQuestion then
                                  yield finding TraceMismatch Proven loc ids $"Trace marks {tc.Symbol} deferred-pending-question but cites no policy question." cfg.ConstructingOwner ]
                        match found with
                        | None ->
                            [ finding TraceMismatch Proven (at (Some tc.Symbol)) ids
                                  $"Trace construct {tc.Symbol} ({tc.Kind}, {tc.File}) is not declared in the audited source." cfg.ConstructingOwner ]
                        | Some l ->
                            let loc = locOf l
                            let own = l.Decl.Markers |> List.collect (fun m -> m.Ids) |> Set.ofList
                            let inherited = chain l.File l.Decl |> List.collect (fun x -> x.Markers |> List.collect (fun m -> m.Ids)) |> Set.ofList
                            let traceIds = Set.ofList ids
                            let show (ids: Set<string>) = String.Join(", ", ids)
                            let markerCheck =
                                if not own.IsEmpty && own <> traceIds then
                                    [ finding TraceMismatch Proven loc (Set.toList (Set.union own traceIds))
                                          $"Source marker on {l.Decl.Qualified} cites [{show own}]; the trace cites [{show traceIds}]." cfg.ConstructingOwner ]
                                elif own.IsEmpty && tc.Disposition <> Technical && not (Set.isSubset traceIds inherited) then
                                    [ finding TraceMismatch Proven loc (Set.toList traceIds)
                                          $"Trace attributes [{show traceIds}] to {l.Decl.Qualified}, which no source marker supports (markers in scope: [{show inherited}])." cfg.ConstructingOwner ]
                                else []
                            let citationChecks =
                                ids |> List.choose (checkCitation cfg h loc l.File.Decls l.Decl (Some tc.Disposition) $"Trace entry for {tc.Symbol}")
                            markerCheck @ citationChecks @ dispositionChecks loc)
                let untraced =
                    all
                    |> List.filter (fun l -> not l.Decl.Markers.IsEmpty && (traceFor l).IsNone)
                    |> List.map (fun l ->
                        finding TraceMismatch Proven (locOf l) (l.Decl.Markers |> List.collect (fun m -> m.Ids))
                            $"{l.Decl.Qualified} carries an Authority marker but is absent from the construction trace." cfg.ConstructingOwner)
                let staleOutputs =
                    t.Outputs
                    |> List.choose (fun o ->
                        files
                        |> List.tryFind (fun f -> alignedSuffix '/' o.Path f.File.RelativePath)
                        |> Option.bind (fun f ->
                            let actual = Digest.ofString f.File.Text
                            if actual <> o.Sha256 then
                                Some(finding TraceMismatch Proven { Path = f.File.RelativePath; Line = None; Symbol = None } []
                                         $"Trace output digest {o.Sha256} for {o.Path} does not match the audited file ({actual}): the trace is stale." cfg.ConstructingOwner)
                            else None))
                handoffChecks @ constructChecks @ untraced @ staleOutputs

        markerFindings @ orphanFindings @ coverageFindings @ traceFindings
        |> List.distinctBy (fun f -> f.Class, f.Location, f.AuthorityRefs, f.Message)

    // ------------------------------------------------------------------ legacy code

    let private idPattern = Regex(@"\b[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+\b", RegexOptions.CultureInvariant)

    /// Owner of the affected authority: accepted decisions first, then superseded
    /// decisions, questions and requirements; otherwise the configured fallback.
    let private ownerOf (cfg: AuditConfig) (h: Handoff) (ids: string list) =
        let decisions = ids |> List.choose (fun id -> h.Decisions |> List.tryFind (fun d -> d.Id = id))
        let pick status = decisions |> List.filter (fun d -> d.Status = status) |> List.sortBy (fun d -> d.Id) |> List.tryHead |> Option.map (fun d -> d.Owner)
        [ pick Accepted
          pick Superseded
          ids |> List.tryPick (fun id -> h.PolicyQuestions |> List.tryFind (fun q -> q.Id = id) |> Option.map (fun q -> q.Owner))
          ids |> List.tryPick (fun id -> h.Requirements |> List.tryFind (fun r -> r.Id = id) |> Option.map (fun r -> r.Owner)) ]
        |> List.tryPick id
        |> Option.defaultValue cfg.UnmatchedOwner

    let private rootOf (f: ScannedFile) (d: Decl) = chain f d |> List.last

    let legacy (cfg: AuditConfig) (h: Handoff) (files: ScannedFile list) : RawFinding list =
        files
        |> List.collect (fun f ->
            let path = f.File.RelativePath
            // Constructs grouped by their outermost declaration (or module level).
            let groups =
                f.Constructs
                |> List.groupBy (fun (_, d) -> d |> Option.map (rootOf f))
                |> List.map (fun (root, cs) -> root, cs |> List.map fst)
            let ruleRoots = groups |> List.choose fst
            let observations =
                h.Observations
                |> List.filter (fun o -> alignedSuffix '/' o.Path path)
                |> List.sortBy (fun o -> o.Id)
            let symbolMatch (o: Observation) (d: Decl) =
                match o.Symbol with
                | Some s -> alignedSuffix '.' s d.Qualified
                | None -> false
            // Direct claims, then closure over referenced, unclaimed rule-bearing declarations.
            let direct =
                observations
                |> List.collect (fun o -> f.Decls |> List.filter (fun d -> d.Parent.IsNone && symbolMatch o d) |> List.map (fun d -> d.Qualified, o.Id))
                |> List.distinctBy fst
            let rec close (claims: (string * string) list) =
                let claimed = claims |> List.map fst |> Set.ofList
                let next =
                    observations
                    |> List.collect (fun o ->
                        let mine = claims |> List.filter (fun (_, oid) -> oid = o.Id) |> List.map fst
                        let refs = f.Decls |> List.filter (fun d -> mine |> List.contains d.Qualified) |> List.map (fun d -> d.References) |> Set.unionMany
                        ruleRoots
                        |> List.filter (fun r -> not (claimed.Contains r.Qualified) && refs.Contains r.Name)
                        |> List.map (fun r -> r.Qualified, o.Id))
                    |> List.distinctBy fst
                if next.IsEmpty then claims else close (claims @ next)
            let claims = close direct
            let claimOf (d: Decl) = claims |> List.tryFind (fun (q, _) -> q = d.Qualified) |> Option.map snd

            let observationFindings =
                observations
                |> List.map (fun o ->
                    let observed = f.Decls |> List.tryFind (fun d -> d.Parent.IsNone && symbolMatch o d)
                    let cs = groups |> List.filter (fun (r, _) -> match r with Some r -> claimOf r = Some o.Id | None -> false) |> List.collect snd
                    let matches = valueMatches h cs
                    let mentioned = idPattern.Matches(o.Description) |> Seq.map (fun m -> m.Value) |> Seq.filter (fun id -> id <> o.Id) |> List.ofSeq
                    let affected =
                        (mentioned @ (matches |> List.collect (fun (d, _) -> d.Id :: Option.toList d.SupersededBy)) @ Option.toList o.QuestionId)
                        |> List.filter (fun id -> resolve h id <> UnknownRef)
                        |> List.distinct
                        |> List.sort
                    let owner = ownerOf cfg h affected
                    let cls = if o.Kind = "superseded-rule" then SupersededAuthority else UnverifiedInheritedRule
                    let notes = matches |> List.map snd
                    let noteText = if notes.IsEmpty then "" else " " + String.Join("; ", notes) + "."
                    match observed with
                    | Some d ->
                        let body = if cs.IsEmpty then "no rule-bearing construct recognised" else describeAll cs
                        finding cls UncertainAuthority { Path = path; Line = Some d.Line; Symbol = o.Symbol } (o.Id :: affected)
                            $"Legacy {o.Kind} {o.Id} at {d.Qualified}: {body}.{noteText} No accepted requirement or decision authorizes it (observation authority: unverified). Question for {owner}: keep, change or retire it? New work must not reuse it until answered; the legacy code is left unchanged."
                            owner
                    | None ->
                        finding cls UncertainAuthority { Path = path; Line = None; Symbol = o.Symbol } (o.Id :: affected)
                            $"Observation {o.Id} names {o.Symbol |> Option.defaultValue path}, which the scan did not find in {path}; the observation may be stale. Question for {owner}."
                            owner)

            let unmatchedFindings =
                groups
                |> List.filter (fun (r, _) -> match r with Some r -> (claimOf r).IsNone | None -> true)
                |> List.map (fun (root, cs) ->
                    let matches = valueMatches h cs
                    let refs = matches |> List.collect (fun (d, _) -> d.Id :: Option.toList d.SupersededBy) |> List.distinct |> List.sort
                    let owner = ownerOf cfg h refs
                    let superseded = matches |> List.exists (fun (d, _) -> d.Status = Superseded)
                    let accepted = matches |> List.filter (fun (d, _) -> d.Status = Accepted) |> List.map (fun (d, _) -> d.Id) |> List.distinct
                    let hasDefault = cs |> List.exists (fun c -> match c.Kind with SilentDefault _ -> true | _ -> false)
                    let hasCategory = cs |> List.exists (fun c -> match c.Kind with CategoryCheck _ -> true | _ -> false)
                    let hints =
                        [ if hasDefault then
                              yield "A wildcard default silently decides every unlisted case; no record authorizes that default."
                          if hasCategory && not accepted.IsEmpty then
                              let ids = String.Join(", ", accepted)
                              yield $"A category/prefix branch sits inside the symbol that carries accepted {ids}: an exemption from {ids} needs its own decision record, whatever the code comments call it."
                          if superseded then yield "The code still carries a superseded decision's value." ]
                    let noteText = (matches |> List.map (fun (_, n) -> n + ".")) @ hints |> List.map (fun n -> " " + n) |> String.concat ""
                    let symbol = root |> Option.map (fun r -> r.Qualified)
                    let line = root |> Option.map (fun r -> r.Line) |> Option.defaultValue (cs |> List.map (fun c -> c.Line) |> List.min)
                    let where = symbol |> Option.defaultValue "module-level code"
                    finding (if superseded then SupersededAuthority else UnverifiedInheritedRule) UncertainAuthority
                        { Path = path; Line = Some line; Symbol = symbol } refs
                        $"Rule-bearing legacy code in {where} matches no observation and cites no authority: {describeAll cs}.{noteText} Question for {owner}: which accepted record, if any, authorizes it? New work must not reuse it until answered; the legacy code is left unchanged."
                        owner)
            observationFindings @ unmatchedFindings)
