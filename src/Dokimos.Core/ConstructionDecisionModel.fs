namespace Dokimos.Core

open System
open System.Text.Json

/// What a plan operation cites: requirement keys and blueprint node ids.
type DecisionTrace = { Keys: string list; NodeIds: string list }

/// One decision point of an extension inventory (vocabulary 3).
type DecisionPoint =
    { Point: string
      Decided: bool
      Question: string
      Trace: DecisionTrace }

/// A transition-effect port and what its failed or unknown outcome means.
type EffectPort =
    { Command: string
      /// `onFailure`: `remain`, `proceed`, `unresolved`, `compensate`, or absent.
      OnFailure: string option
      /// `onUnknown` (not yet in ordo#78's wire format): `reconcile` or `escalate`.
      OnUnknown: string option
      Trace: DecisionTrace }

type ExtensionInventory =
    { /// `guard`, `invariant` or `effect`.
      ExtensionKind: string
      Name: string
      Points: DecisionPoint list
      Trace: DecisionTrace }

type OpenQuestion =
    { Subject: string
      Effect: string option
      Question: string
      Trace: DecisionTrace }

type PairDeclaration =
    { State: string
      Command: string
      Question: string
      Trace: DecisionTrace }

type PlanState = { Name: string; Role: string; Trace: DecisionTrace }

type PlanTransitionChoice =
    { From: string
      Command: string
      To: string
      Guards: string list
      Trace: DecisionTrace }

/// Dokimos's own reading of every semantic choice a construction plan makes
/// or leaves open, in any of vocabularies 1, 2 and 3. Names are normalized.
type DecisionPlan =
    { VocabularyVersion: string
      Workflow: string
      DecisionIds: string list
      States: PlanState list
      Commands: string list
      Transitions: PlanTransitionChoice list
      Refusals: PairDeclaration list
      UndecidedPairs: PairDeclaration list
      /// `declarePairRule`: `refused` or `undecided`, with its trace.
      PairRule: (string * DecisionTrace) option
      Invariants: (string * DecisionTrace) list
      CurrentTime: DecisionTrace option
      Effects: EffectPort list
      Capabilities: (string * string * DecisionTrace) list
      Questions: OpenQuestion list
      Inventories: ExtensionInventory list
      /// Operations the declared vocabulary does not define, as `operations[i]:op`.
      UnsupportedOperations: string list }

/// Why a choice may or may not be generated.
[<RequireQualifiedAccess>]
type ChoiceBasis =
    | Required of requirementKeys: string list
    | Approved of decisionIds: string list
    | Implied of rule: string * requirementKeys: string list
    | Unresolved of question: string
    | Unsupported of reason: string

type ClassifiedChoice =
    { Subject: string
      Kind: string
      RequirementKeys: string list
      Basis: ChoiceBasis }

/// One classified choice as reported.
type ChoiceRecord =
    { Subject: string
      Kind: string
      Class: string
      RequirementKeys: string list
      DecisionIds: string list
      Rule: string
      Detail: string }

/// A choice that keeps the candidate intentionally incomplete.
type OpenItem =
    { Subject: string
      Kind: string
      Class: string
      Detail: string
      ObligationIds: string list }

type ChoiceCount = { Class: string; Count: int }

/// The CONSTRUCT-010 decision block of the mechanical-first report.
type DecisionReport =
    { VocabularyVersion: string
      /// `complete`, `incomplete` (open items, nothing violated) or `unknown`.
      Completeness: string
      Summary: ChoiceCount list
      Choices: ChoiceRecord list
      OpenItems: OpenItem list
      UnsupportedOperations: string list
      LedgerDigest: string
      ApprovalsDigest: string }

/// Reads a plan's choices and classifies them. This is Dokimos's own
/// implementation of the classification ordo#78 documents
/// (`docs/architecture/decision-classification.md`); no Ordo code is used.
[<RequireQualifiedAccess>]
module DecisionModel =
    [<Literal>]
    let Vocabulary1 = "ordo.construction-vocabulary/1"

    [<Literal>]
    let Vocabulary2 = "ordo.construction-vocabulary/2"

    [<Literal>]
    let Vocabulary3 = "ordo.construction-vocabulary/3"

    let private baseOperations =
        set [ "defineWorkflow"; "defineState"; "defineTransition"; "defineInvariant"; "declarePort"; "bindCapability"; "declareUnresolved" ]

    /// The operations each vocabulary defines.
    let operationsOf vocabulary =
        if vocabulary = Vocabulary3 then
            Set.union baseOperations (set [ "declarePairRule"; "defineRefusal"; "declareUndecidedTransition"; "declareExtensionInventory" ])
        else baseOperations

    let isClassified vocabulary = vocabulary = Vocabulary3

    let private s = ConstructionJson.text
    let private key = ConstructionCanonical.nameKey

    let private optionalText name (e: JsonElement) =
        ConstructionJson.get name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.map (fun v -> v.GetString() |> Option.ofObj |> Option.defaultValue "")

    let private trace (op: JsonElement) =
        let t = ConstructionJson.get "trace" op |> Option.defaultValue op
        { Keys = ConstructionJson.strings "requirementKeys" t |> List.distinct |> List.sort
          NodeIds = ConstructionJson.strings "nodeIds" t |> List.distinct |> List.sort }

    let private pair (o: JsonElement) =
        { State = key (s "from" o); Command = key (s "command" o); Question = s "question" o; Trace = trace o }

    /// Reads every choice of a plan, whatever its vocabulary.
    let readPlan (plan: JsonElement) : DecisionPlan =
        let ops = ConstructionJson.array "operations" plan
        let ofKind kind = ops |> List.filter (fun o -> s "op" o = kind)
        let vocabulary = s "vocabularyVersion" plan
        let binding = ConstructionJson.get "binding" plan |> Option.defaultValue plan
        let transitions =
            ofKind "defineTransition"
            |> List.map (fun o ->
                { From = key (s "from" o); Command = key (s "command" o); To = key (s "to" o)
                  Guards = ConstructionJson.array "guards" o |> List.map (fun g -> key (s "name" g)) |> List.sort
                  Trace = trace o })
        let refusals = ofKind "defineRefusal" |> List.map pair
        let undecided = ofKind "declareUndecidedTransition" |> List.map pair
        let ports = ofKind "declarePort"
        { VocabularyVersion = vocabulary
          Workflow = ofKind "defineWorkflow" |> List.tryHead |> Option.map (fun o -> key (s "name" o)) |> Option.defaultValue ""
          DecisionIds = ConstructionJson.strings "decisionIds" binding |> List.distinct |> List.sort
          States =
            ofKind "defineState"
            |> List.map (fun o -> { Name = key (s "name" o); Role = s "role" o; Trace = trace o })
            |> List.sortBy (fun st -> (if st.Role = "initial" then 0 else 1), st.Name)
          Commands =
            (transitions |> List.map _.Command) @ (refusals @ undecided |> List.map _.Command)
            |> List.distinct |> List.sort
          Transitions = transitions
          Refusals = refusals
          UndecidedPairs = undecided
          PairRule = ofKind "declarePairRule" |> List.tryHead |> Option.map (fun o -> s "unlisted" o, trace o)
          Invariants = ofKind "defineInvariant" |> List.map (fun o -> key (s "name" o), trace o)
          CurrentTime = ports |> List.tryFind (fun o -> s "port" o = "currentTime") |> Option.map trace
          Effects =
            ports
            |> List.filter (fun o -> s "port" o = "transitionEffect")
            |> List.map (fun o ->
                { Command = key (s "command" o); OnFailure = optionalText "onFailure" o; OnUnknown = optionalText "onUnknown" o; Trace = trace o })
          Capabilities = ofKind "bindCapability" |> List.map (fun o -> key (s "command" o), s "capability" o, trace o)
          Questions =
            ofKind "declareUnresolved"
            |> List.map (fun o ->
                { Subject = key (s "subject" o); Effect = optionalText "effect" o |> Option.map key; Question = s "question" o; Trace = trace o })
          Inventories =
            ofKind "declareExtensionInventory"
            |> List.map (fun o ->
                let extension = ConstructionJson.get "extension" o |> Option.defaultValue o
                { ExtensionKind = s "kind" extension
                  Name = key (s "name" extension)
                  Points =
                    ConstructionJson.array "decisionPoints" o
                    |> List.map (fun p ->
                        { Point = key (s "point" p)
                          Decided = ConstructionJson.get "decided" p |> Option.exists (fun d -> d.ValueKind = JsonValueKind.True)
                          Question = s "question" p
                          Trace = trace p })
                  Trace = trace o })
          UnsupportedOperations =
            ops
            |> List.mapi (fun i o -> i, s "op" o)
            |> List.filter (fun (_, op) -> not (Set.contains op (operationsOf vocabulary)))
            |> List.map (fun (i, op) -> $"operations[{i}]:{op}") }

    let className basis =
        match basis with
        | ChoiceBasis.Required _ -> "required"
        | ChoiceBasis.Approved _ -> "approved"
        | ChoiceBasis.Implied _ -> "implied"
        | ChoiceBasis.Unresolved _ -> "unresolved"
        | ChoiceBasis.Unsupported _ -> "unsupported"

    /// Only required, approved and implied choices may be generated.
    let generates basis =
        match basis with
        | ChoiceBasis.Required _ | ChoiceBasis.Approved _ | ChoiceBasis.Implied _ -> true
        | ChoiceBasis.Unresolved _ | ChoiceBasis.Unsupported _ -> false

    let classes = [ "required"; "approved"; "implied"; "unresolved"; "unsupported" ]

    let extensionSubject kind name = $"extension:{kind}/{name}"

    /// Approved when the trace cites a decision the plan binds; otherwise
    /// required by the requirement text the trace cites.
    let private basisOf (decisionIds: string list) (t: DecisionTrace) =
        match t.NodeIds |> List.filter (fun id -> List.contains id decisionIds) with
        | [] -> ChoiceBasis.Required t.Keys
        | ids -> ChoiceBasis.Approved ids

    let private choice subject kind (keys: string list) basis : ClassifiedChoice =
        { Subject = subject; Kind = kind; RequirementKeys = keys |> List.distinct |> List.sort; Basis = basis }

    /// What a plan says about one (state, command) pair.
    [<RequireQualifiedAccess>]
    type PairPlan =
        | Transition of target: string * DecisionTrace
        | Refused of DecisionTrace
        | Undecided of question: string * DecisionTrace
        | Terminal of DecisionTrace
        | RuleRefused of DecisionTrace
        | RuleUndecided of DecisionTrace
        /// No vocabulary-3 pair rule: refused, if at all, by omission.
        | Omitted

    let pairPlan (plan: DecisionPlan) (state: PlanState) (command: string) =
        let at (p: PairDeclaration) = p.State = state.Name && p.Command = command
        match plan.Transitions |> List.tryFind (fun t -> t.From = state.Name && t.Command = command) with
        | Some t -> PairPlan.Transition(t.To, t.Trace)
        | None ->
            match plan.Refusals |> List.tryFind at, plan.UndecidedPairs |> List.tryFind at with
            | Some r, _ -> PairPlan.Refused r.Trace
            | None, Some u -> PairPlan.Undecided(u.Question, u.Trace)
            | None, None when state.Role = "terminal" -> PairPlan.Terminal state.Trace
            | None, None ->
                match plan.PairRule with
                | Some("refused", t) -> PairPlan.RuleRefused t
                | Some("undecided", t) -> PairPlan.RuleUndecided t
                | _ -> PairPlan.Omitted

    let pairs (plan: DecisionPlan) =
        [ for state in plan.States do
            for command in plan.Commands -> state, command, pairPlan plan state command ]

    /// Every semantic choice of the plan, classified, sorted by subject.
    let classify (plan: DecisionPlan) : ClassifiedChoice list =
        let basis = basisOf plan.DecisionIds
        let v = plan.VocabularyVersion
        let states = plan.States |> List.map (fun st -> choice ("state:" + st.Name) "state" st.Trace.Keys (basis st.Trace))
        let pairChoices =
            pairs plan
            |> List.map (fun (state, command, p) ->
                let subject = $"pair:{state.Name}/{command}"
                match p with
                | PairPlan.Transition(_, t) | PairPlan.Refused t -> choice subject "pair" t.Keys (basis t)
                | PairPlan.Undecided(q, t) -> choice subject "pair" t.Keys (ChoiceBasis.Unresolved q)
                | PairPlan.Terminal t -> choice subject "pair" t.Keys (ChoiceBasis.Implied("terminal state " + state.Name, t.Keys))
                | PairPlan.RuleRefused t -> choice subject "pair" t.Keys (ChoiceBasis.Implied("pairRule refused", t.Keys))
                | PairPlan.RuleUndecided t ->
                    choice subject "pair" t.Keys
                        (ChoiceBasis.Unresolved $"The requirements do not decide what command {command} does in state {state.Name} of workflow {plan.Workflow}.")
                | PairPlan.Omitted ->
                    choice subject "pair" []
                        (ChoiceBasis.Unsupported $"{v} refuses an unlisted pair by omission; no documented rule decides command {command} in state {state.Name}."))
        let invariants = plan.Invariants |> List.map (fun (name, t) -> choice ("invariant:" + name) "invariant" t.Keys (basis t))
        let effectQuestion command =
            plan.Questions |> List.tryFind (fun q -> q.Effect = Some command) |> Option.map _.Question
        let ports =
            (plan.CurrentTime |> Option.map (fun t -> choice "port:currentTime" "port" t.Keys (basis t)) |> Option.toList)
            @ (plan.Effects
               |> List.collect (fun e ->
                   let failure =
                       match e.OnFailure with
                       | Some "unresolved" ->
                           ChoiceBasis.Unresolved(effectQuestion e.Command |> Option.defaultValue $"The failure disposition of the {e.Command} effect is not decided.")
                       | Some _ -> basis e.Trace
                       | None -> ChoiceBasis.Unsupported $"{v} cannot state what a failed or unknown {e.Command} effect means for the workflow."
                   [ choice ("port:transitionEffect/" + e.Command) "port" e.Trace.Keys (basis e.Trace)
                     choice ("effectFailure:" + e.Command) "effectFailure" e.Trace.Keys failure ]))
        let capabilities =
            plan.Capabilities |> List.map (fun (command, capability, t) -> choice $"capability:{command}/{capability}" "capability" t.Keys (basis t))
        let extensions =
            (plan.Transitions |> List.collect (fun t -> t.Guards |> List.map (fun g -> ("guard", g), t.Trace)))
            @ (plan.Invariants |> List.map (fun (name, t) -> ("invariant", name), t))
            @ (plan.Effects |> List.map (fun e -> ("effect", e.Command), e.Trace))
            |> List.distinctBy fst
        let extensionChoices =
            extensions
            |> List.collect (fun ((kind, name), defining) ->
                let subject = extensionSubject kind name
                match plan.Inventories |> List.tryFind (fun i -> i.ExtensionKind = kind && i.Name = name) with
                | Some inventory ->
                    choice subject "extensionBehavior" inventory.Trace.Keys (basis inventory.Trace)
                    :: (inventory.Points
                        |> List.map (fun p ->
                            choice $"{subject}/point:{p.Point}" "extensionDecision" p.Trace.Keys
                                (if p.Decided then basis p.Trace else ChoiceBasis.Unresolved p.Question)))
                | None ->
                    [ choice subject "extensionBehavior" defining.Keys
                          (ChoiceBasis.Unsupported $"{v} has no decision inventory, so the choices the {subject} implementation must make are not stated.") ])
        let questions =
            plan.Questions
            |> List.filter (fun q -> q.Effect.IsNone)
            |> List.map (fun q -> choice ("unresolved:" + q.Subject) "openQuestion" q.Trace.Keys (ChoiceBasis.Unresolved q.Question))
        states @ pairChoices @ invariants @ ports @ capabilities @ extensionChoices @ questions
        |> List.sortWith (fun a b -> String.CompareOrdinal(a.Subject, b.Subject))

    let record (c: ClassifiedChoice) : ChoiceRecord =
        let ids, rule, detail =
            match c.Basis with
            | ChoiceBasis.Required _ -> [], "", ""
            | ChoiceBasis.Approved ids -> ids, "", ""
            | ChoiceBasis.Implied(rule, _) -> [], rule, ""
            | ChoiceBasis.Unresolved q -> [], "", q
            | ChoiceBasis.Unsupported r -> [], "", r
        { Subject = c.Subject; Kind = c.Kind; Class = className c.Basis; RequirementKeys = c.RequirementKeys
          DecisionIds = ids; Rule = rule; Detail = detail }
