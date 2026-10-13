namespace Dokimos.Core

open System.Text.Json

/// A plan-defined transition, with normalized names.
type PlanTransition =
    { From: string
      Command: string
      To: string
      Guards: string list
      RequirementKeys: string list }

/// What Dokimos itself reads out of the pinned construction plan. Every
/// expectation about the construction manifest and generated code is derived
/// from this, never from the manifest under test.
type PlanModel =
    { SchemaVersion: string
      VocabularyVersion: string
      SourceManifestDigest: string
      BlueprintDigest: string
      CohortId: string
      DecisionIds: string list
      RecomputedDigest: string
      Workflow: string
      States: string list
      Transitions: PlanTransition list
      Invariants: string list
      /// Operation labels in Ordo's derived form (`state:X`, `transition:A/B`, ...).
      Labels: Set<string>
      /// Labels a manifest may name without being required to (`unresolved:*`, `inventory:*`).
      OptionalLabels: Set<string>
      /// (state, command) pairs the plan leaves undecided (vocabulary 3).
      UndecidedPairs: Set<string * string>
      /// Unresolved subjects and the requirement keys they trace to.
      Unresolved: (string * string list) list
      /// Each labelled operation and the requirement keys it traces to.
      Traces: (string * string list) list
      /// Every requirement key any plan operation traces to.
      TracedKeys: Set<string>
      /// Requirement keys traced by a construct (not only by the workflow
      /// declaration or an unresolved question).
      ConstructedKeys: Set<string> }

/// One artifact as the candidate's construction manifest claims it.
type ManifestArtifact =
    { Path: string
      Tier: string
      Ownership: string
      Sha256: string
      Operations: string list
      RequirementKeys: string list }

type ManifestContract =
    { Name: string
      Kind: string
      Operations: string list
      RequirementKeys: string list }

type ManifestObligation =
    { Id: string
      Kind: string
      RequirementKeys: string list }

/// The candidate's `construction-manifest.json`: a claim to be verified.
type ConstructionManifest =
    { GeneratorVersion: string
      PlanDigest: string
      SourceManifestDigest: string
      BlueprintDigest: string
      CohortId: string
      DecisionIds: string list
      OwnershipRoot: string
      Artifacts: ManifestArtifact list
      ExtensionContracts: ManifestContract list
      Obligations: ManifestObligation list
      StructuralIndependence: string
      /// (state, command, expected) rows of the generator's own checks.
      StructuralPairs: (string * string * string) list
      CoveredKeys: string list
      Root: JsonElement }

[<RequireQualifiedAccess>]
module ConstructionModels =
    let private s = ConstructionJson.text
    let private strs = ConstructionJson.strings
    let private key = ConstructionCanonical.nameKey

    let private traceKeys (op: JsonElement) =
        ConstructionJson.get "trace" op |> Option.map (strs "requirementKeys") |> Option.defaultValue []

    let private label (op: JsonElement) =
        match s "op" op with
        | "defineWorkflow" -> Some("workflow:" + key (s "name" op))
        | "defineState" -> Some("state:" + key (s "name" op))
        | "defineTransition" -> Some(sprintf "transition:%s/%s" (key (s "from" op)) (key (s "command" op)))
        | "defineInvariant" -> Some("invariant:" + key (s "name" op))
        | "declarePort" when s "port" op = "currentTime" -> Some "port:currentTime"
        | "declarePort" -> Some("port:transitionEffect/" + key (s "command" op))
        | "bindCapability" -> Some(sprintf "capability:%s/%s" (key (s "command" op)) (s "capability" op))
        // Vocabulary 3 (ordo#78).
        | "declarePairRule" -> Some "pairRule"
        | "defineRefusal" -> Some(sprintf "refusal:%s/%s" (key (s "from" op)) (key (s "command" op)))
        | "declareUndecidedTransition" -> Some(sprintf "undecided:%s/%s" (key (s "from" op)) (key (s "command" op)))
        | _ -> None

    /// Labels a manifest may claim but need not: open questions and inventories.
    let private optionalLabel (op: JsonElement) =
        match s "op" op with
        | "declareUnresolved" -> Some("unresolved:" + key (s "subject" op))
        | "declareExtensionInventory" ->
            let extension = ConstructionJson.get "extension" op |> Option.defaultValue op
            Some(sprintf "inventory:%s/%s" (s "kind" extension) (key (s "name" extension)))
        | _ -> None

    let readPlan (json: string) : Result<PlanModel, string> =
        ConstructionJson.parse "plan" json
        |> Result.map (fun plan ->
            let binding = ConstructionJson.get "binding" plan |> Option.defaultValue plan
            let ops = ConstructionJson.array "operations" plan
            let ofKind kind = ops |> List.filter (fun o -> s "op" o = kind)
            let constructs = ops |> List.filter (fun o -> s "op" o <> "defineWorkflow" && s "op" o <> "declareUnresolved")
            { SchemaVersion = s "schemaVersion" plan
              VocabularyVersion = s "vocabularyVersion" plan
              SourceManifestDigest = s "sourceManifestDigest" binding
              BlueprintDigest = s "blueprintDigest" binding
              CohortId = s "cohortId" binding
              DecisionIds = strs "decisionIds" binding |> List.distinct |> List.sort
              RecomputedDigest = ConstructionCanonical.planDigest plan
              Workflow = ofKind "defineWorkflow" |> List.tryHead |> Option.map (fun o -> key (s "name" o)) |> Option.defaultValue ""
              States = ofKind "defineState" |> List.map (fun o -> key (s "name" o)) |> List.sort
              Transitions =
                ofKind "defineTransition"
                |> List.map (fun o ->
                    { From = key (s "from" o)
                      Command = key (s "command" o)
                      To = key (s "to" o)
                      Guards = ConstructionJson.array "guards" o |> List.map (fun g -> key (s "name" g)) |> List.sort
                      RequirementKeys = traceKeys o })
              Invariants = ofKind "defineInvariant" |> List.map (fun o -> key (s "name" o)) |> List.sort
              Labels = ops |> List.choose label |> Set.ofList
              OptionalLabels = ops |> List.choose optionalLabel |> Set.ofList
              UndecidedPairs =
                let decision = DecisionModel.readPlan plan
                DecisionModel.pairs decision
                |> List.choose (fun (state, command, p) ->
                    match p with
                    | DecisionModel.PairPlan.Undecided _ | DecisionModel.PairPlan.RuleUndecided _ -> Some(state.Name, command)
                    | _ -> None)
                |> Set.ofList
              Unresolved = ofKind "declareUnresolved" |> List.map (fun o -> key (s "subject" o), traceKeys o)
              Traces = ops |> List.choose (fun o -> label o |> Option.map (fun l -> l, traceKeys o))
              TracedKeys = ops |> List.collect traceKeys |> Set.ofList
              ConstructedKeys = constructs |> List.collect traceKeys |> Set.ofList })

    let readManifest (json: string) : Result<ConstructionManifest, string> =
        ConstructionJson.parse "construction manifest" json
        |> Result.map (fun m ->
            let binding = ConstructionJson.get "binding" m |> Option.defaultValue m
            let checks = ConstructionJson.get "structuralChecks" m |> Option.defaultValue m
            { GeneratorVersion = s "generatorVersion" m
              PlanDigest = s "planDigest" m
              SourceManifestDigest = s "sourceManifestDigest" binding
              BlueprintDigest = s "blueprintDigest" binding
              CohortId = s "cohortId" binding
              DecisionIds = strs "decisionIds" binding |> List.distinct |> List.sort
              OwnershipRoot = s "ownershipRoot" m
              Artifacts =
                ConstructionJson.array "artifacts" m
                |> List.map (fun a ->
                    { Path = s "path" a; Tier = s "tier" a; Ownership = s "ownership" a
                      Sha256 = s "sha256" a; Operations = strs "operations" a; RequirementKeys = strs "requirementKeys" a })
              ExtensionContracts =
                ConstructionJson.array "extensionContracts" m
                |> List.map (fun c ->
                    { Name = s "name" c; Kind = s "kind" c; Operations = strs "operations" c; RequirementKeys = strs "requirementKeys" c })
              Obligations =
                ConstructionJson.array "obligations" m
                |> List.map (fun o -> { Id = s "id" o; Kind = s "kind" o; RequirementKeys = strs "requirementKeys" o })
              StructuralIndependence = s "independence" checks
              StructuralPairs =
                ConstructionJson.array "pairs" checks |> List.map (fun p -> s "state" p, s "command" p, s "expected" p)
              CoveredKeys = ConstructionJson.members "coverage" m |> List.map fst
              Root = m })

    /// The plan's commands: those of transitions, refusals and undecided pairs.
    let commands (plan: PlanModel) =
        (plan.Transitions |> List.map _.Command) @ (plan.UndecidedPairs |> Set.toList |> List.map snd)
        @ (plan.Labels |> Set.toList |> List.filter (fun l -> l.StartsWith "refusal:") |> List.map (fun l -> l.Split('/')[1]))
        |> List.distinct |> List.sort

    /// What the plan alone says every (state, command) pair must do.
    let expectedPairs (plan: PlanModel) =
        let commands = commands plan
        [ for state in plan.States do
            for command in commands do
                let target =
                    plan.Transitions
                    |> List.tryFind (fun t -> t.From = state && t.Command = command)
                    |> Option.map _.To
                    |> Option.defaultValue (if Set.contains (state, command) plan.UndecidedPairs then "undecided" else "invalid-transition")
                state, command, target ]

    /// Guard extension contracts the plan requires: name and guarded transitions.
    let requiredGuards (plan: PlanModel) =
        plan.Transitions
        |> List.collect (fun t -> t.Guards |> List.map (fun g -> g, sprintf "transition:%s/%s" t.From t.Command))
        |> List.groupBy fst
        |> List.map (fun (guard, rows) -> guard, rows |> List.map snd |> List.sort)
