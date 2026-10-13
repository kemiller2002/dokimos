namespace Dokimos.Core

open System
open System.Text.RegularExpressions

/// Whether a citation resolves against the supplied inputs.
[<RequireQualifiedAccess>]
type Citation =
    | Resolves
    | Unobserved of string
    | Broken of ConstructionFinding

/// Shared helpers of the CONSTRUCT-010 decision checks: check results,
/// citation resolution and obligation matching. Opened by DecisionChecks.
module DecisionSupport =
    let private f = ConstructionChecks.finding
    let private c = MechanicalCodes.FabricatedCitation

    module Names =
        let Completeness = "decision-completeness"
        let Disposition = "requirement-disposition"
        let ClosedWorld = "closed-world"
        let Unsupported = "unsupported-operations"
        let Unresolved = "unresolved-obligations"
        let FailureSemantics = "failure-semantics"
        let all = [ Completeness; Disposition; ClosedWorld; Unsupported; Unresolved; FailureSemantics ]

    /// A check from its findings; `unknown` explains what was not observed.
    let make name (findings: ConstructionFinding list) (unknown: string option) (passed: string) =
        let status = if not findings.IsEmpty then "failed" elif unknown.IsSome then "unknown" else "passed"
        { Name = name
          Status = status
          Detail =
            match status, unknown with
            | "failed", _ -> $"{findings.Length} finding(s)" + (unknown |> Option.map (fun u -> "; also " + u) |> Option.defaultValue "")
            | "unknown", Some u -> u
            | _ -> passed
          Findings = findings |> List.map (fun x -> x.Code + ":" + x.Subject) |> List.distinct },
        findings

    // ------------------------------------------------------------ citations

    /// Kinds of blueprint node whose approval can fix a choice.
    let approvableKinds = set [ "decision"; "engineeringRationale" ]

    let keyCitation (ctx: DecisionContext) subject (keys: string list) what =
        match keys with
        | [] -> [ Citation.Broken(f MechanicalCodes.UnresolvableCitation subject [] $"The {what} cites no requirement location") ]
        | keys ->
            keys
            |> List.map (fun key ->
                if Set.contains key ctx.SourceKeys then Citation.Resolves
                else Citation.Broken(f c subject [ key ] $"The {what} cites requirement {key}, which is not in the original intake"))

    /// An approved decision id resolves when the approved blueprint has a
    /// decision (or engineering rationale) node with that id and the
    /// operator approved it.
    let decisionCitation (ctx: DecisionContext) subject (id: string) =
        match Map.tryFind id ctx.BlueprintNodes with
        | None -> Citation.Broken(f c subject [] $"Cites decision {id}, which the approved blueprint does not contain")
        | Some kind when not (Set.contains kind approvableKinds) ->
            Citation.Broken(f c subject [] $"Cites {id} as an approved decision, but it is a {kind} node, not a decision or rationale")
        | Some _ ->
            match ctx.Approvals with
            | None -> Citation.Unobserved "operator approvals were not supplied (--approvals); approved decisions are unverified"
            | Some(Error _) -> Citation.Unobserved "the operator approvals are invalid"
            | Some(Ok a) when a.Decisions |> List.exists (fun d -> d.Id = id) -> Citation.Resolves
            | Some(Ok a) ->
                Citation.Broken(f MechanicalCodes.UnapprovedDecisionCited subject [] $"Cites decision {id}, which {a.Approver} did not approve")

    let basisCitations (ctx: DecisionContext) subject (basis: ChoiceBasis) =
        match basis with
        | ChoiceBasis.Required keys -> keyCitation ctx subject keys "choice"
        | ChoiceBasis.Approved ids -> ids |> List.map (decisionCitation ctx subject)
        | ChoiceBasis.Implied(rule, keys) -> keyCitation ctx subject keys ("documented rule (" + rule + ")")
        | ChoiceBasis.Unresolved _ | ChoiceBasis.Unsupported _ -> []

    let broken citations = citations |> List.choose (function Citation.Broken x -> Some x | _ -> None)

    let unobserved citations =
        citations |> List.choose (function Citation.Unobserved u -> Some u | _ -> None) |> List.distinct |> List.tryHead

    // ------------------------------------------------------------ matching

    let openKinds =
        set [ "conflict"; "undecidedEffectFailure"; "uncoveredRequirement"; "undecidedTransition"; "undecidedExtensionDecision"; "unsupportedVocabulary" ]

    let private nameKey = ConstructionCanonical.nameKey

    /// The effect command an extension surface names (`IXPorts.Perform/Stop`,
    /// `port:transitionEffect/Stop`).
    let effectOfSurface (surface: string) =
        let m = Regex.Match(surface, @"(?:\.Perform|^port:transitionEffect)/(\w+)$")
        if m.Success then Some(nameKey m.Groups[1].Value) else None

    let touchesEffect command (o: PreparedObligation) =
        o.ExtensionSurface |> List.exists (fun s -> effectOfSurface s = Some command)

    /// Wording fallback for obligations without a structured `planSubject`
    /// (ordo#78 emits none for undecided pairs and decision points).
    let private mentions (needle: string) (o: PreparedObligation) =
        (nameKey o.Question).Contains(needle, StringComparison.Ordinal)

    /// An obligation matches a choice exactly by `planSubject` when it has
    /// one, and by Ordo's question wording only when it has none.
    let matchesSubject (subject: string) (wording: string) (o: PreparedObligation) =
        if o.PlanSubject <> "" then o.PlanSubject = subject else mentions wording o

    let pairObligations (ctx: DecisionContext) state command =
        ctx.Obligations
        |> List.filter (fun o ->
            o.Kind = "undecidedTransition" && matchesSubject $"pair:{state}/{command}" $"Command{command}DoesInState{state}" o)

    let private pointObligations (ctx: DecisionContext) command point =
        ctx.Obligations
        |> List.filter (fun o ->
            o.Kind = "undecidedExtensionDecision" && touchesEffect command o
            && matchesSubject $"extension:effect/{command}/point:{point}" point o)

    let private effectObligations (ctx: DecisionContext) command =
        ctx.Obligations |> List.filter (fun o -> o.Kind = "undecidedEffectFailure" && touchesEffect command o)

    let private questionObligations (ctx: DecisionContext) (subject: string) (keys: string list) =
        match ctx.Obligations |> List.filter (fun o -> Set.contains o.Kind openKinds && nameKey o.PlanSubject = subject) with
        | [] ->
            ctx.Obligations
            |> List.filter (fun o ->
                (o.Kind = "conflict" || o.Kind = "uncoveredRequirement") && o.RequirementKeys |> List.exists (fun k -> List.contains k keys))
        | named -> named

    /// The obligations that keep an open choice visible.
    let obligationsFor (ctx: DecisionContext) (choice: ClassifiedChoice) =
        let parts = choice.Subject.Split(':', 2)
        match choice.Kind, parts with
        | "pair", [| _; pair |] ->
            match pair.Split('/') with
            | [| state; command |] ->
                (pairObligations ctx state command |> List.map _.Id)
                @ (ctx.Generated |> Option.map (fun g -> [ "RESOLVE-PAIR:" + pair ] |> List.filter (fun id -> Set.contains id g.ObligationIds)) |> Option.defaultValue [])
            | _ -> []
        | "extensionDecision", _ ->
            let m = Regex.Match(choice.Subject, @"^extension:effect/(\w+)/point:(\w+)$")
            if m.Success then pointObligations ctx m.Groups[1].Value m.Groups[2].Value |> List.map _.Id else []
        | "effectFailure", [| _; command |] -> effectObligations ctx command |> List.map _.Id
        | "openQuestion", [| _; subject |] -> questionObligations ctx subject choice.RequirementKeys |> List.map _.Id
        | "extensionBehavior", _ ->
            let m = Regex.Match(choice.Subject, @"^extension:effect/(\w+)$")
            if m.Success then
                ctx.Obligations |> List.filter (fun o -> o.Kind = "unsupportedVocabulary" && touchesEffect m.Groups[1].Value o) |> List.map _.Id
            else []
        | _ -> []

    let dispositionOf (ctx: DecisionContext) id =
        ctx.Dispositions |> Option.bind (fun d -> d.Dispositions |> List.tryFind (fun x -> x.ObligationId = id))

    /// Obligations an agent claims resolved by citing an operator-approved
    /// decision that the plan did not already apply, and that may close it.
    let approvedClosures (ctx: DecisionContext) =
        match ctx.Approvals, ctx.Dispositions with
        | Some(Ok a), Some d ->
            d.Dispositions
            |> List.filter (fun x ->
                x.DecisionId <> ""
                && not (List.contains x.DecisionId ctx.Plan.DecisionIds)
                && Map.tryFind x.DecisionId ctx.BlueprintNodes |> Option.exists (fun k -> Set.contains k approvableKinds)
                && a.Decisions |> List.exists (fun ad -> ad.Id = x.DecisionId && (ad.Closes.IsEmpty || List.contains x.ObligationId ad.Closes)))
            |> List.map _.ObligationId
            |> Set.ofList
        | _ -> Set.empty

    let closed (ctx: DecisionContext) = Set.union ctx.RecordClosed (approvedClosures ctx)


    let contractsOf (ctx: DecisionContext) (kind: string) (name: string) =
        ctx.Generated
        |> Option.map (fun g ->
            g.Contracts
            |> List.filter (fun (contract, ops) ->
                match kind with
                | "effect" -> List.contains ("port:transitionEffect/" + name) ops || contract.EndsWith(".Perform/" + name, StringComparison.Ordinal)
                | "invariant" -> List.contains ("invariant:" + name) ops || contract.EndsWith("Invariant/" + name, StringComparison.Ordinal)
                | _ -> contract.EndsWith("Guard/" + name, StringComparison.Ordinal))
            |> List.map fst)
        |> Option.defaultValue []

