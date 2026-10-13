/// CONSTRUCT-010 phase 4: Dokimos's independent decision-completeness checks.
/// Positive fixtures are the real timer cohort as ordo#78 classifies it (Ordo's
/// own output, copied as JSON) and the frozen expanded ECIR; every adversarial
/// case derives its input from one of them, so it can fail only for the one
/// thing it changed.
module Dokimos.Core.Tests.ConstructionDecisionTests

open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.Tests.ConstructionFixtures

let private v3 relative = read "time-entry-timer-v3" relative
let private expanded relative = read "time-entry-expanded-01" relative

let private node (json: string) : JsonNode =
    match JsonNode.Parse json with
    | null -> failwith "JSON null"
    | n -> n

let private child (n: JsonNode) (name: string) : JsonNode =
    match n[name] with
    | null -> failwithf "no %s" name
    | c -> c

let private element (json: string) =
    match ConstructionJson.parse "fixture" json with
    | Ok e -> e
    | Error e -> failwith e

/// Ordo's default plan wrapped as a prepared artifact, only to read its
/// obligations through Dokimos's strict reader.
let private obligationsOf (defaultPlan: string) =
    let dp = node defaultPlan
    let pv = child dp "provenance"
    let text (n: JsonNode) name = (child n name).GetValue<string>()
    let digests = JsonObject()
    digests["blueprint"] <- JsonValue.Create(text pv "blueprintDigest")
    digests["sourceManifest"] <- JsonValue.Create(text pv "sourceManifestDigest")
    digests["decisionRecords"] <- JsonValue.Create(text pv "decisionRecordsDigest")
    let provenance = JsonObject()
    for name in [ "generatorId"; "generatorVersion"; "vocabularyVersion" ] do
        provenance[name] <- JsonValue.Create(text pv name)
    provenance["inputDigests"] <- digests
    let defaultPlan = JsonObject()
    defaultPlan["plan"] <- node ((child dp "plan").ToJsonString())
    defaultPlan["planDigest"] <- JsonValue.Create(text pv "planDigest")
    defaultPlan["obligations"] <- node ((child dp "obligations").ToJsonString())
    defaultPlan["generatedLayout"] <- node ((child dp "generatedLayout").ToJsonString())
    defaultPlan["provenance"] <- provenance
    let wrapper = JsonObject()
    wrapper["schemaVersion"] <- JsonValue.Create "praxis.construction-prepared/1"
    wrapper["cacheKey"] <- JsonValue.Create "k"
    wrapper["permittedExtensionFiles"] <- JsonArray()
    wrapper["defaultPlan"] <- defaultPlan
    match MechanicalWire.readPrepared (Encoding.UTF8.GetBytes(wrapper.ToJsonString())) with
    | Ok p -> p.Obligations
    | Error e -> failwithf "%A" e

let private planOf (defaultPlan: string) = (child (node defaultPlan) "plan").ToJsonString()

let private keysOf (sourceManifest: string) =
    ConstructionJson.array "requirements" (element sourceManifest) |> List.map (ConstructionJson.text "key") |> Set.ofList

let private nodesOf (blueprint: string) =
    ConstructionJson.array "nodes" (element blueprint) |> List.map (fun n -> ConstructionJson.text "id" n, ConstructionJson.text "kind" n) |> Map.ofList

let private bytes (s: string) = Encoding.UTF8.GetBytes s

let private cohortKey suffix = "kemiller2002/time-entry-state-machine/" + suffix

/// The decision context of the real timer cohort (ordo#78, unlisted pairs refused).
let private timerContext (planJson: string) =
    let plan = DecisionModel.readPlan (element planJson)
    { Plan = plan
      Choices = DecisionModel.classify plan
      SourceKeys = keysOf (read "time-entry-timer" "source-manifest.json")
      BlueprintNodes = nodesOf (read "time-entry-timer" "blueprint.json")
      Obligations = obligationsOf (v3 "ordo-output/default-plan.json")
      Dispositions = None
      DecisionRecords = Some(element (v3 "ordo-golden/construction-decisions-v2.json"))
      Approvals = Some(DecisionInputs.readApprovals (bytes (v3 "synthetic/approvals.json")))
      Ledger = Some(DecisionInputs.readLedger (bytes (v3 "ordo-output/ledger.json")))
      Generated = None
      Acceptance = Map.empty
      RecordClosed = Set.empty
      Cohort = "COH-TIMER"
      BlueprintDigests = [ "sha256:28dd3964f9c49f3492f3c34174ab04d57f4ddcba298276b2a287b4a6f3fdc4cb" ]
      DecisionRecordsDigests = [] }

let private timer () = timerContext (planOf (v3 "ordo-output/default-plan.json"))

let private openTimer () =
    { timerContext (planOf (v3 "ordo-output-open/default-plan.json")) with
        Obligations = obligationsOf (v3 "ordo-output-open/default-plan.json")
        Ledger = Some(DecisionInputs.readLedger (bytes (v3 "ordo-output-open/ledger.json"))) }

let private manifest path =
    match ConstructionModels.readManifest (v3 path) with
    | Ok m -> m
    | Error e -> failwith e

let private generatedFor dir workflow (edit: string -> string) =
    let code tier = Some(edit (v3 $"{dir}/generated/{workflow}/{workflow}.{tier}.g.fs"))
    DecisionInputs.generated workflow (manifest $"{dir}/generated/{workflow}/construction-manifest.json") (code "Transition") (code "Orchestration")

let private run (check: DecisionContext -> MechanicalCheck * ConstructionFinding list) ctx = check ctx

let private codes (findings: ConstructionFinding list) = findings |> List.map (fun f -> f.Code, f.Subject)

let private has code subject (findings: ConstructionFinding list) =
    Assert.True(findings |> List.exists (fun f -> f.Code = code && f.Subject = subject), sprintf "expected %s on %s; got %A" code subject (codes findings))

/// Replaces the first operation matching `pick` in a plan JSON.
let private editPlan (planJson: string) (pick: JsonNode -> bool) (edit: JsonNode -> unit) =
    let plan = node planJson
    let op = (child plan "operations").AsArray() |> Seq.choose Option.ofObj |> Seq.find pick
    edit op
    plan.ToJsonString()

let private opIs name (o: JsonNode) = (child o "op").GetValue<string>() = name

// ---------------------------------------------------------------- canonical form

[<Theory>]
[<InlineData("ordo-golden/plan.v3-open.json", "sha256:dee18dc9cf73ef28699cfef6db577a6c6995d748cd3adbc41e52a2d8d0af2e38")>]
[<InlineData("ordo-golden/plan.v2-remain.json", "sha256:d0aa4619c6739dd185241031288acff0ecb155efbef689cd7fceb76b458a1a32")>]
[<InlineData("ordo-golden/plan.v2-proceed.json", "sha256:05c9f7b75b754082192cd77ca4f78aad464a6479b6d6187b7334066ab0804daf")>]
let ``Dokimos recomputes Ordo's vocabulary 2 and 3 plan digests independently`` (path: string, digest: string) =
    Assert.Equal(digest, ConstructionCanonical.planDigest (element (v3 path)))

[<Fact>]
let ``Dokimos recomputes the digests of Ordo's vocabulary 3 default plans and the expanded ECIR plan`` () =
    for dir in [ "ordo-output"; "ordo-output-open" ] do
        let dp = node (v3 (dir + "/default-plan.json"))
        Assert.Equal((child (child dp "provenance") "planDigest").GetValue<string>(), ConstructionCanonical.planDigest (element (planOf (v3 (dir + "/default-plan.json")))))
    let dp = node (expanded "generated/default-plan.json")
    Assert.Equal((child (child dp "provenance") "planDigest").GetValue<string>(), ConstructionCanonical.planDigest (element (expanded "generated/construction-plan.json")))

// ---------------------------------------------------------------- classification parity

[<Theory>]
[<InlineData("ordo-output")>]
[<InlineData("ordo-output-open")>]
let ``Dokimos's own classification equals the one ordo#78 publishes for the timer cohort`` (dir: string) =
    let published = element (v3 (dir + "/classification.json"))
    let expected =
        ConstructionJson.array "choices" published
        |> List.map (fun c ->
            ConstructionJson.text "subject" c, ConstructionJson.text "class" c, ConstructionJson.strings "decisionIds" c, ConstructionJson.text "rule" c)
    let ours =
        DecisionModel.classify (DecisionModel.readPlan (element (planOf (v3 (dir + "/default-plan.json")))))
        |> List.map DecisionModel.record
        |> List.map (fun r -> r.Subject, r.Class, r.DecisionIds, r.Rule)
    Assert.Equal<(string * string * string list * string) list>(expected, ours)

// ---------------------------------------------------------------- positive fixtures

[<Fact>]
let ``the timer cohort as classified by ordo#78 passes every decision check and stays intentionally incomplete`` () =
    let ctx = { timer () with Generated = Some(generatedFor "ordo-output" "Timer" id) }
    let checks, _, block = DecisionChecks.assess ctx "" ""
    for check, findings in checks do
        Assert.True(findings.IsEmpty, sprintf "%s: %A" check.Name (codes findings))
        Assert.Equal("passed", check.Status)
    Assert.Equal("incomplete", block.Completeness)
    Assert.Equal<(string * int) list>(
        [ "required", 6; "approved", 8; "implied", 7; "unresolved", 3; "unsupported", 0 ],
        block.Summary |> List.map (fun c -> c.Class, c.Count))
    let subjects = block.OpenItems |> List.map _.Subject
    Assert.Equal<string list>(
        [ "effectFailure:Stop"; "extension:effect/Stop/point:RoundingTie"; "unresolved:UncoveredRequirementFfc2989b"
          "obligation:OBL-COH-TIMER-conflict-f569e9ac9d77" ],
        subjects)
    // Every open item keeps its obligation visible.
    for item in block.OpenItems do
        Assert.NotEmpty(item.ObligationIds)

[<Fact>]
let ``undecided pairs with their obligations pass the closed-world check`` () =
    let ctx = { openTimer () with Generated = Some(generatedFor "ordo-output-open" "Timer" id) }
    let check, findings = run DecisionChecks.closedWorld ctx
    Assert.Empty(findings)
    Assert.Equal("passed", check.Status)
    let block = DecisionChecks.openItems ctx
    let pairs = block |> List.filter (fun i -> i.Kind = "pair")
    Assert.Equal(7, pairs.Length)
    for p in pairs do
        Assert.Equal(2, p.ObligationIds.Length)

[<Fact>]
let ``the frozen expanded ECIR keeps every requirement in its ledger and records its unsupported pairs`` () =
    let plan = DecisionModel.readPlan (element (expanded "generated/construction-plan.json"))
    let ctx =
        { Plan = plan
          Choices = DecisionModel.classify plan
          SourceKeys = keysOf (expanded "source-manifest.json")
          BlueprintNodes = nodesOf (expanded "blueprint.json")
          Obligations = obligationsOf (expanded "generated/default-plan.json")
          Dispositions = None
          DecisionRecords = Some(element (expanded "construction-decisions.json"))
          Approvals = Some(DecisionInputs.readApprovals (bytes (expanded "operator-approval.json")))
          Ledger = Some(DecisionInputs.readLedger (bytes (expanded "generated/ledger.json")))
          Generated = None
          Acceptance = Map.empty
          RecordClosed = Set.empty
          Cohort = "COH-TIME-ENTRY-WEEK"
          BlueprintDigests = [ "sha256:5456d28901886a4ab49863c2b37131ebd3ccba12f97f50a429ff7b257afd198f" ]
          DecisionRecordsDigests = [ "sha256:79886081fe3a77df0d2ce4a6fc39432c90893e6561667124d5b9f5643c9b387f" ] }
    let checks, _, block = DecisionChecks.assess ctx "" ""
    let status name = checks |> List.find (fun (c, _) -> c.Name = name) |> fst
    Assert.Equal(46, ctx.SourceKeys.Count)
    for name in [ "decision-completeness"; "requirement-disposition"; "unsupported-operations"; "unresolved-obligations"; "failure-semantics" ] do
        Assert.Equal("passed", (status name).Status)
    // No generated code was read: refusals are judged on the plan alone.
    Assert.Equal("unknown", (status "closed-world").Status)
    // Every pair is decided: Recorded has a transition for each command, and
    // Superseded and Voided are terminal (implied by their role, LIFE-003).
    let pairs = ctx.Choices |> List.filter (fun c -> c.Kind = "pair")
    Assert.Equal(9, pairs.Length)
    Assert.Equal(6, pairs |> List.filter (fun c -> DecisionModel.className c.Basis = "implied") |> List.length)
    Assert.DoesNotContain(block.OpenItems, fun i -> i.Kind = "pair")
    Assert.Equal("incomplete", block.Completeness)
    // All 54 open obligations (40 uncovered requirements, 14 conflicts) stay visible.
    let visible = block.OpenItems |> List.collect _.ObligationIds |> Set.ofList
    Assert.Equal(54, (ctx.Obligations |> List.filter (fun o -> Set.contains o.Id visible)).Length)

// ---------------------------------------------------------------- adversarial refusals

[<Fact>]
let ``a fabricated citation is refused`` () =
    let plan = planOf (v3 "ordo-output/default-plan.json")
    let keyed =
        editPlan plan (opIs "declarePort") (fun o -> (child (child o "trace") "requirementKeys").AsArray().Add(JsonValue.Create "invented/doc.md#L1"))
    let _, findings = run DecisionChecks.completeness (timerContext keyed)
    has MechanicalCodes.FabricatedCitation "port:currentTime" findings
    // A decision id the plan binds but the approved blueprint lacks.
    let bound =
        let p = node plan
        (child (child p "binding") "decisionIds").AsArray().Add(JsonValue.Create "DEC-TIMER-ROUNDING")
        p.ToJsonString()
    let cited = editPlan bound (opIs "declarePort") (fun o -> (child (child o "trace") "nodeIds").AsArray().Add(JsonValue.Create "DEC-TIMER-ROUNDING"))
    let _, findings = run DecisionChecks.completeness (timerContext cited)
    Assert.Contains(findings, fun f -> f.Code = MechanicalCodes.FabricatedCitation && f.Detail.Contains "DEC-TIMER-ROUNDING")
    // A real decision the operator did not approve.
    let unapproved = { timer () with Approvals = Some(DecisionInputs.readApprovals (bytes """{"approver":"op","decisions":[]}""")) }
    let _, findings = run DecisionChecks.completeness unapproved
    has MechanicalCodes.UnapprovedDecisionCited "state:NoTimer" findings
    // Without approvals, approved choices are unverified, never passed.
    let check, _ = run DecisionChecks.completeness { timer () with Approvals = None }
    Assert.Equal("unknown", check.Status)

[<Fact>]
let ``a refusal by omission is refused`` () =
    let plan = planOf (v3 "ordo-output/default-plan.json")
    let uncited =
        editPlan plan (opIs "declarePairRule") (fun o ->
            (child (child o "trace") "requirementKeys").AsArray().Clear()
            (child (child o "trace") "nodeIds").AsArray().Clear())
    let ctx = { timerContext uncited with Generated = Some(generatedFor "ordo-output" "Timer" id) }
    let check, findings = run DecisionChecks.closedWorld ctx
    Assert.Equal("failed", check.Status)
    has MechanicalCodes.RefusalByOmission "pair:NoTimer/Pause" findings
    Assert.Equal(7, findings |> List.filter (fun f -> f.Code = MechanicalCodes.RefusalByOmission) |> List.length)
    // M1: generated code refuses a pair the plan leaves undecided.
    let mutant (code: string) = code.Replace("| TimerState.NoTimer, TimerCommand.Pause -> true", "| TimerState.NoTimer, TimerCommand.Pause -> false")
    let ctx = { openTimer () with Generated = Some(generatedFor "ordo-output-open" "Timer" mutant) }
    has MechanicalCodes.UndecidedPairRefused "pair:NoTimer/Pause" (snd (run DecisionChecks.closedWorld ctx))
    // An undecided pair whose obligation was dropped from the default plan.
    let dropped = (openTimer ()).Obligations |> List.filter (fun o -> not (o.Kind = "undecidedTransition" && o.Question.Contains "command Pause does in state NoTimer"))
    let ctx = { openTimer () with Obligations = dropped; Generated = Some(generatedFor "ordo-output-open" "Timer" id) }
    has MechanicalCodes.UndecidedChoiceWithoutObligation "pair:NoTimer/Pause" (snd (run DecisionChecks.closedWorld ctx))

[<Fact>]
let ``an unresolved choice claimed implemented is refused`` () =
    // M3: the manifest asks to IMPLEMENT an extension whose rounding tie is undecided.
    let g = generatedFor "ordo-output" "Timer" id
    let ctx = { timer () with Generated = Some { g with ObligationIds = Set.add "IMPLEMENT:ITimerPorts.Perform/Stop" g.ObligationIds } }
    has MechanicalCodes.UnresolvedChoiceClaimedImplemented "extension:effect/Stop/point:RoundingTie" (snd (run DecisionChecks.completeness ctx))
    // The orchestration applies a decided disposition the plan leaves unresolved.
    let decided (code: string) = code.Replace("TimerEffect.Stop -> TimerFailureDisposition.Unresolved", "TimerEffect.Stop -> TimerFailureDisposition.Remain")
    let ctx = { timer () with Generated = Some(generatedFor "ordo-output" "Timer" decided) }
    has MechanicalCodes.UnresolvedChoiceClaimedImplemented "effectFailure:Stop" (snd (run DecisionChecks.completeness ctx))
    // An agent resolution citing the decision the plan already applied closes nothing.
    let point = (timer ()).Obligations |> List.find (fun o -> o.Kind = "undecidedExtensionDecision")
    let claims = $"{{\"dispositions\":[{{\"obligationId\":\"{point.Id}\",\"disposition\":\"resolved\",\"decisionId\":\"DEC-TIMER-STATES\"}}]}}"
    let ctx = { timer () with Dispositions = MechanicalWire.readDispositions claims |> Result.toOption }
    let _, findings = run DecisionChecks.unresolved ctx
    has MechanicalCodes.UnapprovedResolution point.Id findings
    Assert.Contains("already applied", (findings |> List.find (fun f -> f.Subject = point.Id)).Detail)

[<Fact>]
let ``an operator-approved decision outside the plan may close an open obligation`` () =
    let point = (timer ()).Obligations |> List.find (fun o -> o.Kind = "undecidedExtensionDecision")
    let claims = $"{{\"dispositions\":[{{\"obligationId\":\"{point.Id}\",\"disposition\":\"resolved\",\"decisionId\":\"DEC-TIMER-ROUNDING\"}}]}}"
    let approvals = $"{{\"approver\":\"op\",\"decisions\":[\"DEC-TIMER-STATES\",{{\"id\":\"DEC-TIMER-ROUNDING\",\"closes\":[\"{point.Id}\"]}}]}}"
    let ctx =
        { timer () with
            Dispositions = MechanicalWire.readDispositions claims |> Result.toOption
            Approvals = Some(DecisionInputs.readApprovals (bytes approvals))
            BlueprintNodes = Map.add "DEC-TIMER-ROUNDING" "decision" (timer ()).BlueprintNodes }
    let check, findings = run DecisionChecks.unresolved ctx
    Assert.Empty(findings)
    Assert.Equal("passed", check.Status)
    Assert.Contains(point.Id, DecisionSupport.approvedClosures ctx)
    // The same claim is refused when the approval does not cover this obligation.
    let narrow = $"{{\"approver\":\"op\",\"decisions\":[\"DEC-TIMER-STATES\",{{\"id\":\"DEC-TIMER-ROUNDING\",\"closes\":[\"OTHER\"]}}]}}"
    has MechanicalCodes.UnapprovedResolution point.Id (snd (run DecisionChecks.unresolved { ctx with Approvals = Some(DecisionInputs.readApprovals (bytes narrow)) }))

[<Fact>]
let ``an unknown outcome mapped to failure is refused`` () =
    // Ordo's own vocabulary-2 golden: onFailure remain, with no onUnknown,
    // so the generated orchestration treats an unknown outcome as a failure.
    let remain = timerContext (v3 "ordo-golden/plan.v2-remain.json")
    let check, findings = run DecisionChecks.failureSemantics remain
    Assert.Equal("failed", check.Status)
    has MechanicalCodes.UnknownOutcomeAssumed "effectFailure:Stop" findings
    Assert.Contains("assumed failure", findings.Head.Detail)
    let proceed = timerContext (v3 "ordo-golden/plan.v2-proceed.json")
    Assert.Contains("assumed success", (snd (run DecisionChecks.failureSemantics proceed)).Head.Detail)
    // A blind retry is refused; reconcile and escalate are accepted.
    let onUnknown value =
        let plan = node (v3 "ordo-golden/plan.v2-remain.json")
        for o in (child plan "operations").AsArray() |> Seq.choose Option.ofObj do
            if opIs "declarePort" o && (child o "port").GetValue<string>() = "transitionEffect" then o["onUnknown"] <- JsonValue.Create(value: string)
        timerContext (plan.ToJsonString())
    Assert.Contains("blind retry", (snd (run DecisionChecks.failureSemantics (onUnknown "retry"))).Head.Detail)
    Assert.Empty(snd (run DecisionChecks.failureSemantics (onUnknown "reconcile")))
    Assert.Empty(snd (run DecisionChecks.failureSemantics (onUnknown "escalate")))
    // ordo#78's open disposition escalates to the host: accepted.
    Assert.Empty(snd (run DecisionChecks.failureSemantics (timer ())))

[<Fact>]
let ``compensation without an approved decision is refused`` () =
    let compensate (nodeIds: string list) =
        let plan = node (v3 "ordo-golden/plan.v2-remain.json")
        for o in (child plan "operations").AsArray() |> Seq.choose Option.ofObj do
            if opIs "declarePort" o && (child o "port").GetValue<string>() = "transitionEffect" then
                o["onFailure"] <- JsonValue.Create "compensate"
                o["onUnknown"] <- JsonValue.Create "reconcile"
                let ids = (child (child o "trace") "nodeIds").AsArray()
                ids.Clear()
                for id in nodeIds do ids.Add(JsonValue.Create id)
        timerContext (plan.ToJsonString())
    let check, findings = run DecisionChecks.failureSemantics (compensate [ "IFC-TIMER-PORTS" ])
    Assert.Equal("failed", check.Status)
    has MechanicalCodes.UnapprovedCompensation "effectFailure:Stop" findings
    // Citing the approved decision (bound by the plan, approved by the operator) is accepted.
    Assert.Empty(snd (run DecisionChecks.failureSemantics (compensate [ "IFC-TIMER-PORTS"; "DEC-TIMER-STATES" ])))
    // The same citation without the operator's approval is not.
    let unapproved = { compensate [ "DEC-TIMER-STATES" ] with Approvals = Some(DecisionInputs.readApprovals (bytes """{"approver":"op","decisions":[]}""")) }
    has MechanicalCodes.UnapprovedCompensation "effectFailure:Stop" (snd (run DecisionChecks.failureSemantics unapproved))

[<Fact>]
let ``a dropped requirement and a duplicated ledger row are refused`` () =
    let ledger = node (v3 "ordo-output/ledger.json")
    let rows = (child ledger "rows").AsArray()
    let first = rows[0] |> Option.ofObj |> Option.get
    let firstKey = (child (child first "requirement") "key").GetValue<string>()
    let duplicated = node (ledger.ToJsonString())
    (child duplicated "rows").AsArray().Add(node (first.ToJsonString()))
    rows.RemoveAt 0
    let withLedger (json: string) = { timer () with Ledger = Some(DecisionInputs.readLedger (bytes json)) }
    let check, findings = run DecisionChecks.disposition (withLedger (ledger.ToJsonString()))
    Assert.Equal("failed", check.Status)
    has MechanicalCodes.RequirementDropped firstKey findings
    has MechanicalCodes.DuplicateLedgerRow firstKey (snd (run DecisionChecks.disposition (withLedger (duplicated.ToJsonString()))))
    // The unmodified ledger passes.
    Assert.Equal("passed", (fst (run DecisionChecks.disposition (timer ()))).Status)

[<Fact>]
let ``an implemented ledger row needs independent acceptance and no open obligation`` () =
    let implement (key: string) =
        let ledger = node (v3 "ordo-output/ledger.json")
        for row in (child ledger "rows").AsArray() |> Seq.choose Option.ofObj do
            if (child (child row "requirement") "key").GetValue<string>() = key then
                row["disposition"] <- node """{"kind":"implemented"}"""
        { timer () with Ledger = Some(DecisionInputs.readLedger (bytes (ledger.ToJsonString()))) }
    let stateModel = cohortKey "docs/architecture/business-activity-ledger-state-model.md#L116-L129"
    let stop = cohortKey "docs/architecture/business-activity-ledger-state-model.md#L131-L144"
    // No oracle case observed it.
    let ctx = { implement stateModel with Acceptance = Map.ofList [ stateModel, "none" ] }
    has MechanicalCodes.ImplementedWithoutIndependentAcceptance stateModel (snd (run DecisionChecks.disposition ctx))
    // Observed and passed: accepted.
    Assert.Empty(snd (run DecisionChecks.disposition { ctx with Acceptance = Map.ofList [ stateModel, "passed" ] }))
    // Not observed at all: unknown, never passed.
    Assert.Equal("unknown", (fst (run DecisionChecks.disposition (implement stateModel))).Status)
    // Stop still has an undecided effect failure and rounding tie.
    let ctx = { implement stop with Acceptance = Map.ofList [ stop, "passed" ] }
    has MechanicalCodes.ImplementedWithOpenObligation stop (snd (run DecisionChecks.disposition ctx))

[<Fact>]
let ``unsupported operations are recorded and never claimed implemented`` () =
    // A vocabulary-2 plan that smuggles in a vocabulary-3 operation.
    let plan = node (v3 "ordo-golden/plan.v2-remain.json")
    (child plan "operations").AsArray().Add(node """{"op":"declarePairRule","unlisted":"refused","trace":{"requirementKeys":[],"nodeIds":[]}}""")
    let ctx = timerContext (plan.ToJsonString())
    let check, findings = run DecisionChecks.unsupported ctx
    Assert.Equal("failed", check.Status)
    Assert.Contains(findings, fun f -> f.Code = MechanicalCodes.UnsupportedOperationDropped && f.Subject.EndsWith ":declarePairRule")
    Assert.Contains(DecisionChecks.openItems ctx, fun i -> i.Kind = "operation" && i.Class = "unsupported")
    // A vocabulary-1 plan generated from records that decide what it cannot carry, with no unsupportedVocabulary obligation.
    let v1 = { timerContext (read "time-entry-timer" "plan.json") with Obligations = [] }
    Assert.Contains(snd (run DecisionChecks.unsupported v1), fun f -> f.Code = MechanicalCodes.UnsupportedOperationDropped && f.Subject = "decisionRecords:DEC-TIMER-STATES")
    // An unsupportedVocabulary obligation claimed implemented.
    let obligation =
        { Id = "OBL-UV"; Kind = "unsupportedVocabulary"; RequirementKeys = []; NodeIds = [ "IFC-TIMER-PORTS" ]
          ExtensionSurface = [ "ITimerPorts.Perform/Stop" ]; Question = "q"; PlanSubject = "" }
    let claimed =
        { timer () with
            Obligations = [ obligation ]
            Dispositions = MechanicalWire.readDispositions """{"dispositions":{"OBL-UV":"implemented"}}""" |> Result.toOption }
    has MechanicalCodes.UnsupportedChoiceClaimedImplemented "OBL-UV" (snd (run DecisionChecks.unsupported claimed))

[<Fact>]
let ``the ledger and approvals readers validate strictly`` () =
    Assert.True(Result.isError (DecisionInputs.readLedger (bytes """{"schemaVersion":"ordo.requirements-ledger/2","rows":[]}""")))
    Assert.True(Result.isError (DecisionInputs.readLedger (bytes """{"schemaVersion":"ordo.requirements-ledger/1","rows":[{"disposition":{"kind":"implemented"}}]}""")))
    Assert.True(Result.isError (DecisionInputs.readApprovals (bytes """{"decisions":["DEC"]}""")))
    match DecisionInputs.readApprovals (bytes (expanded "operator-approval.json")) with
    | Ok a -> Assert.Equal<string list>([ "DEC-ENTRY-LIFECYCLE" ], a.Decisions |> List.map _.Id)
    | Error e -> failwithf "%A" e
