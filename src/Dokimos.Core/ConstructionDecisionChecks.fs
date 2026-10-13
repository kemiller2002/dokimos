namespace Dokimos.Core

open System
open System.Text.Json
open System.Text.RegularExpressions
open Dokimos.Core.DecisionSupport

/// The CONSTRUCT-010 decision-completeness checks. Each one is a pure
/// function of the context and reports `passed`, `failed` or `unknown`.
[<RequireQualifiedAccess>]
module DecisionChecks =
    let private f = ConstructionChecks.finding

    // ------------------------------------------------------------ 1. completeness
    /// Every choice that generated code relies on is required, approved or
    /// implied with a citation that resolves; no open choice is claimed
    /// implemented. Pairs are judged by the closed-world check.
    let completeness (ctx: DecisionContext) =
        let implementedBy (o: PreparedObligation) =
            dispositionOf ctx o.Id |> Option.exists (fun d -> d.Status = "implemented")
        let claimCode basis =
            match basis with
            | ChoiceBasis.Unsupported _ -> MechanicalCodes.UnsupportedChoiceClaimedImplemented
            | _ -> MechanicalCodes.UnresolvedChoiceClaimedImplemented
        let perChoice (choice: ClassifiedChoice) =
            let citations = basisCitations ctx choice.Subject choice.Basis
            let claims =
                if DecisionModel.generates choice.Basis then []
                else
                    let code = claimCode choice.Basis
                    let effect = Regex.Match(choice.Subject, @"^extension:(\w+)/(\w+)(?:/point:(\w+))?$")
                    [ match choice.Kind with
                      | "extensionDecision" when effect.Success ->
                          for contract in contractsOf ctx effect.Groups[1].Value effect.Groups[2].Value do
                              match ctx.Generated with
                              | Some g when Set.contains ("IMPLEMENT:" + contract) g.ObligationIds ->
                                  f code choice.Subject choice.RequirementKeys
                                      $"The construction manifest asks for IMPLEMENT:{contract} although this decision point is open; it may only be decided (DECIDE)"
                              | _ -> ()
                      | "extensionBehavior" when effect.Success && effect.Groups[1].Value = "effect" ->
                          for o in ctx.Obligations do
                              if o.Kind = "extensionBehavior" && touchesEffect effect.Groups[2].Value o && implementedBy o then
                                  f code choice.Subject choice.RequirementKeys
                                      $"Obligation {o.Id} is claimed implemented, but the {choice.Subject} implementation makes choices nobody stated ({DecisionModel.className choice.Basis})"
                      | "effectFailure" ->
                          let command = choice.Subject.Substring("effectFailure:".Length)
                          match ctx.Generated |> Option.bind (fun g -> Map.tryFind command g.EffectDispositions) with
                          | Some generated when generated <> "unresolved" ->
                              f code choice.Subject choice.RequirementKeys
                                  $"The generated orchestration applies '{generated}' to a failed or unknown {command} effect; the plan leaves it {DecisionModel.className choice.Basis}"
                          | _ -> ()
                      | _ -> () ]
            citations, claims
        let results = ctx.Choices |> List.filter (fun x -> x.Kind <> "pair") |> List.map perChoice
        let citations = results |> List.collect fst
        let approvals =
            match ctx.Approvals with
            | Some(Error e) -> [ f MechanicalCodes.ApprovalsInvalid "approvals" [] (String.concat "; " e) ]
            | Some(Ok a) ->
                [ if a.Cohort <> "" && a.Cohort <> ctx.Cohort then
                      f MechanicalCodes.ApprovalsInvalid "approvals.cohort" [] $"The approvals are for cohort {a.Cohort}; the plan is bound to {ctx.Cohort}"
                  if a.BlueprintDigest <> "" && not (List.contains a.BlueprintDigest ctx.BlueprintDigests) then
                      f MechanicalCodes.ApprovalsInvalid "approvals.blueprintDigest" [] $"The approvals bind blueprint {a.BlueprintDigest}, not the supplied blueprint"
                  if a.DecisionRecordsDigest <> "" && not ctx.DecisionRecordsDigests.IsEmpty
                     && not (List.contains a.DecisionRecordsDigest ctx.DecisionRecordsDigests) then
                      f MechanicalCodes.ApprovalsInvalid "approvals.decisionRecordsDigest" [] $"The approvals bind decision records {a.DecisionRecordsDigest}, not the supplied records" ]
            | None -> []
        let generating = ctx.Choices |> List.filter (fun x -> DecisionModel.generates x.Basis) |> List.length
        make Names.Completeness (approvals @ broken citations @ (results |> List.collect snd)) (unobserved citations)
            $"{ctx.Choices.Length} choice(s) classified; {generating} generate, each with a citation that resolves; no open choice is claimed implemented"

    // ------------------------------------------------------------ 2. ledger

    /// Every source requirement has exactly one ledger row; `implemented`
    /// needs Dokimos's own passing acceptance and no open obligation.
    let disposition (ctx: DecisionContext) =
        match ctx.Ledger with
        | None -> make Names.Disposition [] (Some "no requirements ledger was supplied (--ledger)") ""
        | Some(Error e) -> make Names.Disposition [ f MechanicalCodes.LedgerInvalid "ledger" [] (String.concat "; " e) ] None ""
        | Some(Ok rows) ->
            let byKey = rows |> List.countBy _.Key |> Map.ofList
            let isClosed = closed ctx
            let openFor key =
                ctx.Obligations
                |> List.filter (fun o -> Set.contains o.Kind openKinds && List.contains key o.RequirementKeys && not (Set.contains o.Id isClosed))
                |> List.map _.Id
            let unobservedAcceptance = Collections.Generic.HashSet<string>()
            let findings =
                [ for key in ctx.SourceKeys do
                    match Map.tryFind key byKey |> Option.defaultValue 0 with
                    | 0 -> f MechanicalCodes.RequirementDropped key [ key ] "The source requirement has no ledger row"
                    | 1 -> ()
                    | n -> f MechanicalCodes.DuplicateLedgerRow key [ key ] $"The source requirement has {n} ledger rows; exactly one is allowed"
                  for KeyValue(key, _) in byKey do
                    if not (Set.contains key ctx.SourceKeys) then
                        f MechanicalCodes.InventedLedgerRow key [ key ] "A ledger row names a requirement absent from the original intake"
                  for row in rows |> List.distinctBy _.Key do
                    match row.Disposition with
                    | "implemented" ->
                        match row.OpenObligationIds @ openFor row.Key |> List.distinct with
                        | [] -> ()
                        | ids ->
                            let named = String.concat ", " ids
                            f MechanicalCodes.ImplementedWithOpenObligation row.Key [ row.Key ] $"Claimed implemented with open obligation(s) {named}"
                        match Map.tryFind row.Key ctx.Acceptance |> Option.defaultValue "unknown" with
                        | "passed" -> ()
                        | "failed" ->
                            f MechanicalCodes.ImplementedWithoutIndependentAcceptance row.Key [ row.Key ] "Claimed implemented, but a frozen oracle case for it failed"
                        | "none" ->
                            f MechanicalCodes.ImplementedWithoutIndependentAcceptance row.Key [ row.Key ] "Claimed implemented, but no frozen oracle case observes it"
                        | _ -> unobservedAcceptance.Add row.Key |> ignore
                    | "rejected" | "superseded" ->
                        match row.ClosingDecisionIds with
                        | [] ->
                            f MechanicalCodes.UnapprovedResolution row.Key [ row.Key ] $"Claimed {row.Disposition} without a closing operator-approved decision"
                        | ids ->
                            for citation in ids |> List.map (decisionCitation ctx row.Key) |> broken do
                                f MechanicalCodes.UnapprovedResolution row.Key [ row.Key ] citation.Detail
                    | _ -> () ]
            let unknown =
                if unobservedAcceptance.Count = 0 then None
                else Some $"{unobservedAcceptance.Count} implemented row(s) have no Dokimos-observed oracle run"
            make Names.Disposition findings unknown $"{ctx.SourceKeys.Count} source requirement(s), each with exactly one ledger row; implemented rows are independently accepted"

    // ------------------------------------------------------------ 3. closed world

    /// Every (state, command) pair is an allowed transition, a refusal citing
    /// a rule or an approved decision, or undecided with an obligation.
    let closedWorld (ctx: DecisionContext) =
        let generatedPair state command =
            ctx.Generated |> Option.bind (fun g -> Map.tryFind (state, command) g.Pairs)
        let results =
            DecisionModel.pairs ctx.Plan
            |> List.map (fun (state, command, plan) ->
                let subject = $"pair:{state.Name}/{command}"
                let generated = generatedPair state.Name command
                let refusalCitations (t: DecisionTrace) what =
                    let approved = t.NodeIds |> List.filter (fun id -> List.contains id ctx.Plan.DecisionIds)
                    match t.Keys, approved with
                    | [], [] -> [ Citation.Broken(f MechanicalCodes.RefusalByOmission subject [] $"The {what} cites neither a requirement nor an approved decision") ]
                    | keys, ids -> (if keys.IsEmpty then [] else keyCitation ctx subject keys what) @ (ids |> List.map (decisionCitation ctx subject))
                let mismatch expected =
                    match generated with
                    | Some g when g <> expected && not (g.StartsWith "allowed:") ->
                        [ Citation.Broken(f MechanicalCodes.PairDispositionMismatch subject [] $"The {ctx.Generated.Value.PairSource} has '{g}'; the plan says '{expected}'") ]
                    | _ -> []
                match plan with
                | DecisionModel.PairPlan.Transition(_, t) ->
                    basisCitations ctx subject (match t.NodeIds |> List.filter (fun id -> List.contains id ctx.Plan.DecisionIds) with
                                                | [] -> ChoiceBasis.Required t.Keys
                                                | ids -> ChoiceBasis.Approved ids)
                | DecisionModel.PairPlan.Refused t -> refusalCitations t "refusal" @ mismatch "refused"
                | DecisionModel.PairPlan.RuleRefused t -> refusalCitations t "pair rule (refused)" @ mismatch "refused"
                | DecisionModel.PairPlan.Terminal t -> refusalCitations t "terminal state" @ mismatch "refused"
                | DecisionModel.PairPlan.Undecided _
                | DecisionModel.PairPlan.RuleUndecided _ ->
                    [ match generated with
                      | Some "refused" ->
                          Citation.Broken(f MechanicalCodes.UndecidedPairRefused subject []
                              $"The {ctx.Generated.Value.PairSource} refuses a pair the plan leaves undecided: refusal by omission")
                      | _ -> ()
                      match ctx.Generated with
                      | Some g when not (Set.contains ($"RESOLVE-PAIR:{state.Name}/{command}") g.ObligationIds) ->
                          Citation.Broken(f MechanicalCodes.UndecidedChoiceWithoutObligation subject []
                              "The construction manifest has no RESOLVE-PAIR obligation for this undecided pair")
                      | _ -> ()
                      if not ctx.Obligations.IsEmpty && (pairObligations ctx state.Name command).IsEmpty then
                          Citation.Broken(f MechanicalCodes.UndecidedChoiceWithoutObligation subject []
                              "The default plan has no undecidedTransition obligation for this undecided pair") ]
                | DecisionModel.PairPlan.Omitted ->
                    match generated with
                    | Some "refused" ->
                        [ Citation.Broken(f MechanicalCodes.RefusalByOmission subject []
                              $"The {ctx.Generated.Value.PairSource} refuses this pair, but {ctx.Plan.VocabularyVersion} states no pair rule, refusal or undecided pair for it") ]
                    | _ -> [])
            |> List.concat
        let unknown =
            match ctx.Generated, unobserved results with
            | None, _ -> Some "no generated transition code or structural rows were read; refusals are judged on the plan alone"
            | _, u -> u
        let count = ctx.Plan.States.Length * ctx.Plan.Commands.Length
        make Names.ClosedWorld (broken results) unknown
            $"All {count} (state, command) pair(s) are transitions, cited refusals or undecided pairs with obligations"

    // ------------------------------------------------------------ 4. unsupported

    /// Operations a vocabulary cannot carry are recorded, never silently
    /// dropped, and never claimed implemented.
    let unsupported (ctx: DecisionContext) =
        let v = ctx.Plan.VocabularyVersion
        let recorded (nodeId: string) =
            ctx.Obligations |> List.exists (fun o -> o.Kind = "unsupportedVocabulary" && List.contains nodeId o.NodeIds)
        let recordFindings =
            match ctx.DecisionRecords with
            | None -> []
            | Some records ->
                let schema = ConstructionJson.text "schemaVersion" records
                [ for record in ConstructionJson.array "records" records do
                    let node = ConstructionJson.text "nodeId" record
                    let content = ConstructionJson.get "content" record |> Option.defaultValue record
                    let carriedOnlyBy3 =
                        [ for name in [ "unlisted"; "refusals"; "undecided" ] do
                            match ConstructionJson.get name content with
                            | Some v when v.ValueKind <> JsonValueKind.Null && not (v.ValueKind = JsonValueKind.Array && v.GetArrayLength() = 0) -> name
                            | _ -> ()
                          for port in ConstructionJson.array "ports" content do
                            if not (ConstructionJson.array "decisionPoints" port).IsEmpty then "decisionPoints" ]
                    let decidedFailures =
                        [ for port in ConstructionJson.array "ports" content do
                            match ConstructionJson.get "onFailure" port with
                            | Some d when d.ValueKind = JsonValueKind.String -> ConstructionJson.text "command" port
                            | _ -> () ]
                    if v <> DecisionModel.Vocabulary3 && not carriedOnlyBy3.IsEmpty && not (recorded node) then
                        let names = String.concat ", " (List.distinct carriedOnlyBy3)
                        f MechanicalCodes.UnsupportedOperationDropped ("decisionRecords:" + node) []
                            $"Decision record {node} ({schema}) states {names}, which {v} cannot carry, and no unsupportedVocabulary obligation records it"
                    if v = DecisionModel.Vocabulary1 && not decidedFailures.IsEmpty && not (recorded node) then
                        for command in decidedFailures do
                            f MechanicalCodes.UnsupportedOperationDropped $"decisionRecords:{node}/onFailure:{command}" []
                                $"Decision record {node} decides the {command} effect's failure disposition, which {v} cannot carry, and no unsupportedVocabulary obligation records it" ]
        let isClosed = closed ctx
        let findings =
            [ for op in ctx.Plan.UnsupportedOperations do
                f MechanicalCodes.UnsupportedOperationDropped op [] $"The plan uses an operation that {v} does not define; nothing generated from it is accounted for"
              yield! recordFindings
              for o in ctx.Obligations do
                  if o.Kind = "unsupportedVocabulary" && not (Set.contains o.Id isClosed)
                     && dispositionOf ctx o.Id |> Option.exists (fun d -> d.Status = "implemented") then
                      f MechanicalCodes.UnsupportedChoiceClaimedImplemented o.Id o.RequirementKeys
                          "An unsupportedVocabulary obligation is claimed implemented; it can only be escalated or resolved by an approved decision" ]
        let unsupportedChoices = ctx.Choices |> List.filter (fun x -> DecisionModel.className x.Basis = "unsupported") |> List.length
        make Names.Unsupported findings None
            $"{unsupportedChoices} unsupported choice(s) and {ctx.Plan.UnsupportedOperations.Length} unsupported operation(s) are recorded as open items; none is dropped or claimed implemented"

    // ------------------------------------------------------------ 5. unresolved

    /// Open obligations stay visible; an agent's resolution must cite an
    /// operator-approved decision that the plan did not already apply.
    let unresolved (ctx: DecisionContext) =
        let resolutions =
            ctx.Dispositions
            |> Option.map (fun d -> d.Dispositions |> List.filter (fun x -> x.DecisionId <> "" || x.Status = "resolved"))
            |> Option.defaultValue []
        let keysOf id = ctx.Obligations |> List.tryFind (fun o -> o.Id = id) |> Option.map _.RequirementKeys |> Option.defaultValue []
        let approved = approvedClosures ctx
        let resolutionFindings, resolutionUnknown =
            match ctx.Approvals with
            | None when not resolutions.IsEmpty -> [], Some "operator approvals were not supplied (--approvals); claimed resolutions are unverified"
            | Some(Error _) -> [], None
            | _ ->
                [ for d in resolutions do
                    if not (Set.contains d.ObligationId approved) then
                        let why =
                            if d.DecisionId = "" then "cites no decision"
                            elif List.contains d.DecisionId ctx.Plan.DecisionIds then $"cites {d.DecisionId}, which the plan already applied; it cannot close what the generator left open"
                            else $"cites {d.DecisionId}, which is not an operator-approved decision (or not approved to close this obligation)"
                        f MechanicalCodes.UnapprovedResolution d.ObligationId (keysOf d.ObligationId) $"The agent claims this obligation resolved but {why}" ],
                None
        let visibility =
            [ for choice in ctx.Choices do
                // Pairs are judged by closed-world; open questions by the manifest's RESOLVE obligations.
                if DecisionModel.className choice.Basis = "unresolved" && choice.Kind <> "pair" && choice.Kind <> "openQuestion"
                   && not ctx.Obligations.IsEmpty
                   && (obligationsFor ctx choice).IsEmpty then
                    f MechanicalCodes.UndecidedChoiceWithoutObligation choice.Subject choice.RequirementKeys
                        "The plan leaves this choice open, but the default plan carries no obligation for it" ]
        let openCount = ctx.Obligations |> List.filter (fun o -> Set.contains o.Kind openKinds) |> List.length
        make Names.Unresolved (resolutionFindings @ visibility) resolutionUnknown
            $"{openCount} open obligation(s) stay visible; every claimed resolution cites an operator-approved decision"

    // ------------------------------------------------------------ 6. failure semantics

    let private outcomeAssumption (value: string) =
        match value with
        | "reconcile" | "escalate" -> None
        | "retry" -> Some "a blind retry"
        | "remain" | "fail" | "failed" | "failure" -> Some "an assumed failure"
        | "proceed" | "succeed" | "success" -> Some "an assumed success"
        | "compensate" -> Some "compensation for an effect that may not have happened"
        | other -> Some $"'{other}', which is neither reconcile nor escalate"

    /// An unknown effect outcome maps to reconcile or escalate, never to an
    /// assumed failure or a blind retry; compensate needs an approved decision.
    let failureSemantics (ctx: DecisionContext) =
        let judge subject (onFailure: string option) (onUnknown: string option) (approval: unit -> Citation list) keys =
            let unknownOutcome =
                match onUnknown, onFailure with
                | Some u, _ ->
                    outcomeAssumption u
                    |> Option.map (fun a ->
                        f MechanicalCodes.UnknownOutcomeAssumed subject keys
                            $"onUnknown '{u}' maps an unknown outcome to {a}; an unknown outcome must reconcile or escalate")
                    |> Option.toList
                | None, Some d when d <> "unresolved" ->
                    outcomeAssumption d
                    |> Option.map (fun a ->
                        f MechanicalCodes.UnknownOutcomeAssumed subject keys
                            $"onFailure '{d}' also governs an unknown outcome (no onUnknown), so an unknown outcome is mapped to {a}; it must reconcile or escalate. Fix: state the unknown outcome separately as onUnknown reconcile|escalate (Ordo vocabulary 5 declareEffectPolicy, ordo#83)")
                    |> Option.toList
                | _ -> []
            let compensation =
                if onFailure = Some "compensate" || onUnknown = Some "compensate" then
                    match approval () with
                    | [] -> [ Citation.Broken(f MechanicalCodes.UnapprovedCompensation subject keys "Compensation is declared without an approved decision") ]
                    | citations ->
                        citations
                        |> List.map (function
                            | Citation.Broken x -> Citation.Broken { x with Code = MechanicalCodes.UnapprovedCompensation; Detail = "Compensation: " + x.Detail }
                            | other -> other)
                else []
            (unknownOutcome |> List.map Citation.Broken) @ compensation
        let planResults =
            ctx.Plan.Effects
            |> List.collect (fun e ->
                let subject = "effectFailure:" + e.Command
                let approval () =
                    e.Trace.NodeIds |> List.filter (fun id -> List.contains id ctx.Plan.DecisionIds) |> List.map (decisionCitation ctx subject)
                let generatedMismatch =
                    match ctx.Generated |> Option.bind (fun g -> Map.tryFind e.Command g.EffectDispositions), e.OnFailure with
                    | Some g, Some p when g <> p ->
                        [ Citation.Broken(f MechanicalCodes.EffectDispositionMismatch subject e.Trace.Keys
                              $"The generated orchestration applies '{g}'; the plan declares '{p}'") ]
                    | _ -> []
                judge subject e.OnFailure e.OnUnknown approval e.Trace.Keys @ generatedMismatch)
        let recordResults =
            ctx.DecisionRecords
            |> Option.map (fun records ->
                [ for record in ConstructionJson.array "records" records do
                    let node = ConstructionJson.text "nodeId" record
                    let content = ConstructionJson.get "content" record |> Option.defaultValue record
                    for port in ConstructionJson.array "ports" content do
                        if ConstructionJson.text "port" port = "transitionEffect" then
                            let text name = ConstructionJson.get name port |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String) |> Option.map (fun v -> v.GetString() |> string)
                            let command = ConstructionJson.text "command" port
                            let subject = $"decisionRecords:{node}/{command}"
                            yield! judge subject (text "onFailure") (text "onUnknown") (fun () -> [ decisionCitation ctx subject node ]) (ConstructionJson.strings "requirementKeys" port) ])
            |> Option.defaultValue []
        let results = planResults @ recordResults
        make Names.FailureSemantics (broken results) (unobserved results)
            $"{ctx.Plan.Effects.Length} effect port(s): an unknown outcome reconciles or escalates; no compensation without an approved decision"

    // ------------------------------------------------------------ report

    /// The typed open items that keep a candidate intentionally incomplete.
    let openItems (ctx: DecisionContext) =
        let fromChoices =
            ctx.Choices
            |> List.filter (fun x -> not (DecisionModel.generates x.Basis))
            |> List.map (fun x ->
                let r = DecisionModel.record x
                { Subject = x.Subject; Kind = x.Kind; Class = r.Class; Detail = r.Detail; ObligationIds = obligationsFor ctx x |> List.distinct |> List.sort })
        let linked = fromChoices |> List.collect _.ObligationIds |> Set.ofList
        let isClosed = closed ctx
        let fromObligations =
            ctx.Obligations
            |> List.filter (fun o -> Set.contains o.Kind openKinds && not (Set.contains o.Id linked) && not (Set.contains o.Id isClosed))
            |> List.map (fun o ->
                { Subject = "obligation:" + o.Id; Kind = o.Kind
                  Class = (if o.Kind = "unsupportedVocabulary" then "unsupported" else "unresolved")
                  Detail = o.Question; ObligationIds = [ o.Id ] })
        let fromOperations =
            ctx.Plan.UnsupportedOperations
            |> List.map (fun op -> { Subject = op; Kind = "operation"; Class = "unsupported"; Detail = "Not in " + ctx.Plan.VocabularyVersion; ObligationIds = [] })
        fromChoices @ fromObligations @ fromOperations

    /// Runs the six decision checks. Returns each check with its findings,
    /// the approved closures, and the decision block of the report.
    let assess (ctx: DecisionContext) (ledgerDigest: string) (approvalsDigest: string) =
        let checks =
            [ completeness ctx; disposition ctx; closedWorld ctx; unsupported ctx; unresolved ctx; failureSemantics ctx ]
        let items = openItems ctx
        let completenessStatus =
            if checks |> List.exists (fun (x, _) -> x.Status = "failed") then "violated"
            elif not items.IsEmpty then "incomplete"
            elif checks |> List.exists (fun (x, _) -> x.Status = "unknown") then "unknown"
            else "complete"
        let counts = ctx.Choices |> List.countBy (_.Basis >> DecisionModel.className) |> Map.ofList
        checks,
        approvedClosures ctx,
        { VocabularyVersion = ctx.Plan.VocabularyVersion
          Completeness = completenessStatus
          Summary = DecisionModel.classes |> List.map (fun x -> { Class = x; Count = Map.tryFind x counts |> Option.defaultValue 0 })
          Choices = ctx.Choices |> List.map DecisionModel.record
          OpenItems = items
          UnsupportedOperations = ctx.Plan.UnsupportedOperations
          LedgerDigest = ledgerDigest
          ApprovalsDigest = approvalsDigest }

