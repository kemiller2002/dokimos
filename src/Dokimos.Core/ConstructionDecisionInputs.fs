namespace Dokimos.Core

open System
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

/// One decision the operator approved; `Closes`, when non-empty, limits the
/// obligations it may close.
type ApprovedDecision = { Id: string; Closes: string list }

/// The operator's decision approvals (`--approvals`). Dokimos checks their
/// consistency with the other inputs, not their authenticity.
type DecisionApprovals =
    { Approver: string
      Decisions: ApprovedDecision list
      Cohort: string
      BlueprintDigest: string
      DecisionRecordsDigest: string }

/// One row of `ordo.requirements-ledger/1`, as far as Dokimos reads it.
type LedgerRow =
    { Key: string
      Disposition: string
      ClosingDecisionIds: string list
      OpenObligationIds: string list }

/// What the candidate's generated output decides, read from Git objects.
type GeneratedDecisions =
    { /// Where pair dispositions were read from.
      PairSource: string
      /// (state, command) -> `allowed:<target>`, `refused` or `undecided`.
      Pairs: Map<string * string, string>
      /// Obligation ids of the construction manifest.
      ObligationIds: Set<string>
      /// Extension contracts of the construction manifest: name and operations.
      Contracts: (string * string list) list
      /// Effect command -> the failure disposition the generated code applies.
      EffectDispositions: Map<string, string> }

/// Everything the decision checks read. Nothing here is derived from the
/// candidate's own claims except where a claim is the thing being judged.
type DecisionContext =
    { Plan: DecisionPlan
      Choices: ClassifiedChoice list
      SourceKeys: Set<string>
      /// Blueprint node id -> kind.
      BlueprintNodes: Map<string, string>
      Obligations: PreparedObligation list
      Dispositions: DispositionRecord option
      /// The supplied decision records, only when their digest matches the declared one.
      DecisionRecords: JsonElement option
      Approvals: Result<DecisionApprovals, string list> option
      Ledger: Result<LedgerRow list, string list> option
      Generated: GeneratedDecisions option
      /// Requirement key -> Dokimos's own oracle observation:
      /// `passed`, `failed`, `none` (no case covers it) or `unknown` (not run).
      Acceptance: Map<string, string>
      /// Obligations an approved decision record already closes (CONSTRUCT-008).
      RecordClosed: Set<string>
      /// The plan's cohort and the digests the supplied inputs recompute to,
      /// against which the approvals' optional bindings are compared.
      Cohort: string
      BlueprintDigests: string list
      DecisionRecordsDigests: string list }

/// The CONSTRUCT-010 decision-completeness checks. Each one is a pure
/// function of the context and reports `passed`, `failed` or `unknown`.
/// Readers for the CONSTRUCT-010 inputs and the decision context.
[<RequireQualifiedAccess>]
module DecisionInputs =
    // ------------------------------------------------------------------ readers

    let private apply (fn: Result<'a -> 'b, string list>) (x: Result<'a, string list>) =
        match fn, x with
        | Ok fn, Ok x -> Ok(fn x)
        | Error a, Error b -> Error(a @ b)
        | Error a, Ok _ -> Error a
        | Ok _, Error b -> Error b

    let private sequence (results: Result<'a, string list> list) =
        results |> List.fold (fun acc r -> apply (apply (Ok(fun xs x -> xs @ [ x ])) acc) r) (Ok [])

    /// Reads the operator approvals: `approver` (non-empty) and `decisions`,
    /// an array of ids or of `{id, closes}`. The optional `cohort`,
    /// `blueprintDigest` and `decisionRecordsDigest` are compared by the checks.
    let readApprovals (bytes: byte[]) : Result<DecisionApprovals, string list> =
        match ConstructionJson.parse "approvals" (Encoding.UTF8.GetString bytes) with
        | Error e -> Error [ e ]
        | Ok root ->
            let approver =
                match ConstructionJson.text "approver" root with
                | "" -> Error [ "approvals.approver must name who approved" ]
                | a -> Ok a
            let decisions =
                match ConstructionJson.get "decisions" root with
                | Some v when v.ValueKind = JsonValueKind.Array ->
                    v.EnumerateArray()
                    |> Seq.mapi (fun i d ->
                        match d.ValueKind with
                        | JsonValueKind.String when not (String.IsNullOrWhiteSpace(d.GetString())) ->
                            Ok { Id = d.GetString() |> string; Closes = [] }
                        | JsonValueKind.Object when ConstructionJson.text "id" d <> "" ->
                            Ok { Id = ConstructionJson.text "id" d; Closes = ConstructionJson.strings "closes" d }
                        | _ -> Error [ $"approvals.decisions[{i}] must be a decision id or an object with id" ])
                    |> Seq.toList |> sequence
                | _ -> Error [ "approvals.decisions must be an array" ]
            apply (apply (Ok(fun a ds ->
                { Approver = a; Decisions = ds; Cohort = ConstructionJson.text "cohort" root
                  BlueprintDigest = ConstructionJson.text "blueprintDigest" root
                  DecisionRecordsDigest = ConstructionJson.text "decisionRecordsDigest" root })) approver) decisions

    /// Reads the rows of `ordo.requirements-ledger/1`.
    let readLedger (bytes: byte[]) : Result<LedgerRow list, string list> =
        match ConstructionJson.parse "requirements ledger" (Encoding.UTF8.GetString bytes) with
        | Error e -> Error [ e ]
        | Ok root ->
            match ConstructionJson.text "schemaVersion" root, ConstructionJson.get "rows" root with
            | schema, _ when schema <> "ordo.requirements-ledger/1" -> Error [ $"ledger schemaVersion is '{schema}', not ordo.requirements-ledger/1" ]
            | _, Some rows when rows.ValueKind = JsonValueKind.Array ->
                rows.EnumerateArray()
                |> Seq.mapi (fun i row ->
                    let key = ConstructionJson.get "requirement" row |> Option.map (ConstructionJson.text "key") |> Option.defaultValue ""
                    let disposition = ConstructionJson.get "disposition" row |> Option.defaultValue row
                    match key, ConstructionJson.text "kind" disposition with
                    | "", _ -> Error [ $"rows[{i}].requirement.key is required" ]
                    | _, "" -> Error [ $"rows[{i}].disposition.kind is required" ]
                    | k, kind ->
                        let decision = ConstructionJson.text "decisionId" disposition
                        Ok { Key = k; Disposition = kind
                             ClosingDecisionIds = (ConstructionJson.strings "closingDecisionIds" row @ (if decision = "" then [] else [ decision ])) |> List.distinct
                             OpenObligationIds = ConstructionJson.strings "openObligationIds" row })
                |> Seq.toList |> sequence
            | _ -> Error [ "ledger rows must be an array" ]

    // ------------------------------------------------------------ generated

    let private targetArms (w: string) (code: string) =
        Regex.Matches(code, $@"(?m)^\s*\|\s*{w}State\.(\w+),\s*{w}Command\.(\w+)\s*->\s*(?:None|Some\s+{w}State\.(\w+))\s*$")
        |> Seq.map (fun m -> (m.Groups[1].Value, m.Groups[2].Value), (if m.Groups[3].Success then Some m.Groups[3].Value else None))
        |> Map.ofSeq

    let private undecidedArms (w: string) (code: string) =
        Regex.Matches(code, $@"(?m)^\s*\|\s*{w}State\.(\w+),\s*{w}Command\.(\w+)\s*->\s*true\s*$")
        |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value)
        |> Set.ofSeq

    /// What the generated code (or, without it, the manifest's structural
    /// rows) decides about every pair and every effect failure.
    let generated (workflow: string) (manifest: ConstructionManifest) (transitionCode: string option) (orchestrationCode: string option) =
        let fromCode =
            transitionCode
            |> Option.map (fun code -> targetArms workflow code, undecidedArms workflow code)
            |> Option.filter (fun (arms, _) -> not arms.IsEmpty)
        let pairs, source =
            match fromCode with
            | Some(arms, undecided) ->
                arms
                |> Map.map (fun pair target ->
                    match target with
                    | Some t -> "allowed:" + t
                    | None when Set.contains pair undecided -> "undecided"
                    | None -> "refused"),
                "generated transition code"
            | None ->
                manifest.StructuralPairs
                |> List.map (fun (st, cmd, expected) ->
                    (st, cmd),
                    (match expected with
                     | "undecided" -> "undecided"
                     | "invalid-transition" -> "refused"
                     | target -> "allowed:" + target))
                |> Map.ofList,
                "construction manifest structural rows"
        { PairSource = source
          Pairs = pairs
          ObligationIds = manifest.Obligations |> List.map _.Id |> Set.ofList
          Contracts = manifest.ExtensionContracts |> List.map (fun x -> x.Name, x.Operations)
          EffectDispositions =
            orchestrationCode
            |> Option.map (fun code ->
                Regex.Matches(code, $@"(?m)^\s*\|\s*{workflow}Effect\.(\w+)\s*->\s*{workflow}FailureDisposition\.(\w+)\s*$")
                |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant())
                |> Map.ofSeq)
            |> Option.defaultValue Map.empty }

    /// The CONSTRUCT-010 decision context: the plan's own choices, the
    /// approved blueprint, the declared obligations and what the candidate's
    /// generated files decide, read from Git objects.
    let context (m: MechanicalInputs) (ctx: MechanicalContext) (p: PreparedArtifact option)
                                (dispositions: Result<DispositionRecord, string list> option) =
        let s = ConstructionJson.text
        let plan = DecisionModel.readPlan ctx.PlanJson
        let code tier =
            ctx.Manifest.Artifacts |> List.tryFind (fun a -> a.Tier = tier)
            |> Option.bind (fun a -> Map.tryFind (ctx.Ownership.Mount + a.Path) ctx.Candidate.Contents)
            |> Option.map Encoding.UTF8.GetString
        let declared = p |> Option.bind (fun p -> Map.tryFind "decisionRecords" p.InputDigests) |> Option.defaultValue ""
        let records, recordDigests =
            match m.DecisionRecordsBytes with
            | None -> None, []
            | Some bytes ->
                match ConstructionJson.parse "decision records" (Encoding.UTF8.GetString bytes) with
                | Error _ -> None, []
                | Ok d ->
                    let digests = [ ConstructionJson.sha256Bytes bytes; ConstructionJson.sha256Text (ConstructionCanonical.canonicalText d) ]
                    (if List.contains declared digests then Some d else None), digests
        { Plan = plan
          Choices = DecisionModel.classify plan
          SourceKeys = ConstructionJson.array "requirements" ctx.SourceManifest |> List.map (s "key") |> Set.ofList
          BlueprintNodes = ConstructionJson.array "nodes" ctx.Blueprint |> List.map (fun n -> s "id" n, s "kind" n) |> Map.ofList
          Obligations = p |> Option.map _.Obligations |> Option.defaultValue []
          Dispositions = (match dispositions with Some(Ok d) -> Some d | _ -> None)
          DecisionRecords = records
          Approvals = m.ApprovalsBytes |> Option.map readApprovals
          Ledger = m.LedgerBytes |> Option.map readLedger
          Generated = Some(generated plan.Workflow ctx.Manifest (code "transition") (code "orchestration"))
          Acceptance = ctx.Acceptance
          RecordClosed = p |> Option.map (MechanicalObligations.closedObligations m) |> Option.defaultValue Set.empty
          Cohort = ctx.Plan.CohortId
          BlueprintDigests = [ ConstructionCanonical.blueprintDigest ctx.Blueprint; ConstructionJson.sha256Text ctx.BlueprintText ]
          DecisionRecordsDigests = recordDigests }
