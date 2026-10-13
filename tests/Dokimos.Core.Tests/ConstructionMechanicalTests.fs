/// CONSTRUCT-008 mechanical-first checks against in-memory candidates built
/// from the pinned Ordo timer fixture. Git-backed refusals are covered by
/// the CLI tests on the pilot-01 B1 bundle.
module Dokimos.Core.Tests.ConstructionMechanicalTests

open System.Text
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.Tests.ConstructionFixtures

let private cohortKey suffix =
    "kemiller2002/time-entry-state-machine/" + suffix

let private completionKeys =
    [ cohortKey "docs/architecture/business-activity-ledger-state-model.md#L131-L144"
      cohortKey "requirements-reconstruction/fsharp-business-requirements-beyond-csharp.md#L105-L107" ]

/// A valid prepared artifact for the Ordo golden timer plan.
let private prepared (edit: JsonObject -> unit) =
    let blueprint = read "time-entry-timer" "blueprint.json"
    let source = read "time-entry-timer" "source-manifest.json"
    let declaredSource =
        match JsonNode.Parse source with
        | null -> ""
        | n ->
            match n["digest"] with
            | null -> ""
            | d -> d.GetValue<string>()
    let blueprintDigest =
        ConstructionJson.parse "b" blueprint |> Result.map ConstructionCanonical.blueprintDigest |> Result.defaultValue ""
    let quoted (values: string list) = values |> List.map (fun v -> "\"" + v + "\"") |> String.concat ","
    let json =
        String.concat ""
            [ "{\"schemaVersion\":\"praxis.construction-prepared/1\",\"cacheKey\":\"key-1\","
              "\"permittedExtensionFiles\":[\"app/Extensions.fs\"],"
              "\"defaultPlan\":{\"plan\":" + read "time-entry-timer" "plan.json" + ","
              "\"planDigest\":\"" + timerPlanDigest + "\","
              "\"obligations\":[{\"id\":\"OBL-1\",\"kind\":\"extensionBehavior\",\"requirementKeys\":[" + quoted completionKeys + "],\"nodeIds\":[\"VER-TIMER-COMPLETION\"]}],"
              "\"generatedLayout\":[" + quoted [ "generated/Timer/Timer.Model.g.fs"; "generated/Timer/Timer.Transition.g.fs"; "generated/Timer/Timer.Orchestration.g.fs" ] + "],"
              "\"provenance\":{\"generatorId\":\"ordo.default-plan\",\"generatorVersion\":\"1\",\"vocabularyVersion\":\"ordo.construction-vocabulary/1\","
              "\"inputDigests\":{\"blueprint\":\"" + blueprintDigest + "\",\"sourceManifest\":\"" + declaredSource + "\","
              "\"decisionRecords\":\"sha256:" + System.String('0', 64) + "\"}}}}" ]
    let root =
        match JsonNode.Parse json with
        | null -> failwith "null"
        | n -> n.AsObject()
    edit root
    Encoding.UTF8.GetBytes(root.ToJsonString())

let private mechanical bytes =
    { PreparedBytes = bytes
      DispositionsJson = Some """{"dispositions":{"OBL-1":{"disposition":"escalated","note":"open"}}}"""
      DecisionRecordsBytes = None
      PreparedReplicaBytes = None
      GeneratedCommit = None
      Replica = None
      LedgerBytes = None
      ApprovalsBytes = None }

let private run (m: MechanicalInputs) inputs =
    match ConstructionVerification.verifyWith (Some m) inputs with
    | Ok report -> report
    | Error e -> failwithf "refused: %A" e

let private status name (report: ConstructionVerificationReport) =
    report.Mechanical |> Option.map (fun r -> r.Checks |> List.find (fun c -> c.Name = name) |> _.Status) |> Option.defaultValue "absent"

[<Fact>]
let ``a valid prepared artifact for the golden timer raises no mechanical-first finding`` () =
    let report = run (mechanical (prepared ignore)) (timerInputs ())
    // CONSTRUCT-010: vocabulary 1 states no pair rule, so the golden timer's
    // generated table refuses its seven unlisted pairs by omission. That is
    // the only finding; every CONSTRUCT-008 check is unaffected.
    Assert.Equal<(string * string) list>(
        [ for pair in [ "NoTimer/Pause"; "NoTimer/Resume"; "NoTimer/Stop"; "Paused/Pause"; "Paused/Start"; "Running/Resume"; "Running/Start" ] ->
            MechanicalCodes.RefusalByOmission, "pair:" + pair ],
        report.Findings |> List.map (fun f -> f.Code, f.Subject))
    Assert.Equal("failed", status DecisionSupport.Names.ClosedWorld report)
    Assert.Equal("passed", status ConstructionMechanical.Names.Coverage report)
    Assert.Equal("passed", status ConstructionMechanical.Names.Obligations report)
    // Decision records and replicas were not supplied: unknown, never passed.
    Assert.Equal("unknown", status ConstructionMechanical.Names.Provenance report)
    Assert.Equal("unknown", status ConstructionMechanical.Names.Determinism report)

[<Fact>]
let ``a cohort requirement covered by neither a plan operation nor an obligation is refused`` () =
    // The golden plan traces every cohort key, so widen the cohort node with a
    // requirement the intake carries but the plan does not trace.
    let unresolved = cohortKey "requirements-reconstruction/fsharp-business-requirements-beyond-csharp.md#L113-L114"
    let node =
        match JsonNode.Parse(read "time-entry-timer" "blueprint.json") with
        | null -> failwith "null"
        | n -> n
    for n in (node["nodes"] |> Option.ofObj |> Option.get).AsArray() do
        match n with
        | null -> ()
        | n when (n["id"] |> Option.ofObj |> Option.map (fun v -> v.GetValue<string>())) = Some "COH-TIMER" ->
            (n["requirementKeys"] |> Option.ofObj |> Option.get).AsArray().Add(JsonValue.Create unresolved)
        | _ -> ()
    let inputs = { timerInputs () with BlueprintJson = node.ToJsonString() }
    let report = run (mechanical (prepared ignore)) inputs
    let hit =
        report.Findings
        |> List.tryFind (fun f -> f.Code = ConstructionCodes.RequirementSilentlyOmitted && f.Subject = unresolved && f.Detail.Contains "neither a plan operation nor an obligation")
    Assert.True(hit.IsSome, sprintf "%A" report.Findings)
    Assert.Equal("failed", status ConstructionMechanical.Names.Coverage report)
    // The same key covered by an obligation is accepted by the coverage check.
    let covered =
        prepared (fun root ->
            let obligations = (root["defaultPlan"] |> Option.ofObj |> Option.get)["obligations"] |> Option.ofObj |> Option.get
            obligations.AsArray().Add(JsonNode.Parse("{\"id\":\"OBL-2\",\"kind\":\"conflict\",\"requirementKeys\":[\"" + unresolved + "\"]}")))
    let m = { mechanical covered with DispositionsJson = Some """{"dispositions":{"OBL-1":{"disposition":"escalated","note":"open"},"OBL-2":{"disposition":"escalated","note":"conflict"}}}""" }
    Assert.Equal("passed", status ConstructionMechanical.Names.Coverage (run m inputs))

[<Fact>]
let ``an obligation citing a requirement outside the intake or the cohort is invented`` () =
    let bytes =
        prepared (fun root ->
            let obligations = (root["defaultPlan"] |> Option.ofObj |> Option.get)["obligations"] |> Option.ofObj |> Option.get
            obligations.AsArray().Add(JsonNode.Parse """{"id":"OBL-2","kind":"extensionBehavior","requirementKeys":["nowhere#L1"]}"""))
    let m = { mechanical bytes with DispositionsJson = Some """{"dispositions":{"OBL-1":"escalated","OBL-2":{"disposition":"implemented"}}}""" }
    let report = run m (timerInputs ())
    Assert.Contains(report.Findings, fun f -> f.Code = ConstructionCodes.InventedBehavior && f.Subject = "OBL-2@nowhere#L1")

[<Fact>]
let ``the prepared reader validates required fields strictly and reports each one`` () =
    let bytes =
        prepared (fun root ->
            root["schemaVersion"] <- JsonValue.Create "praxis.construction-prepared/9"
            root.Remove "cacheKey" |> ignore
            let dp = root["defaultPlan"] |> Option.ofObj |> Option.get
            dp["planDigest"] <- JsonValue.Create "not-a-digest"
            dp["obligations"] <- JsonNode.Parse """[{"id":"A","kind":"guess","requirementKeys":[]},{"id":"A","kind":"conflict","requirementKeys":[]}]"""
            root["unknownField"] <- JsonValue.Create 1)
    match MechanicalWire.readPrepared bytes with
    | Ok _ -> failwith "accepted an invalid prepared artifact"
    | Error errors ->
        Assert.Contains("schemaVersion is 'praxis.construction-prepared/9', not praxis.construction-prepared/1", errors)
        Assert.Contains("cacheKey is required", errors)
        Assert.Contains("defaultPlan.planDigest must be sha256:<64 lowercase hex>, not 'not-a-digest'", errors)
        Assert.Contains(errors, fun e -> e.StartsWith "defaultPlan.obligations[0].kind 'guess' is not one of")
        Assert.Equal(4, errors.Length)

[<Fact>]
let ``dispositions may be keyed by obligation id or listed`` () =
    let keyed = MechanicalWire.readDispositions """{"dispositions":{"A":"implemented","B":{"status":"escalated","note":"q"}}}"""
    let listed = MechanicalWire.readDispositions """{"dispositions":[{"id":"A","disposition":"implemented"},{"obligationId":"B","disposition":"escalated","note":"q","x":1}]}"""
    match keyed, listed with
    | Ok k, Ok l ->
        Assert.Equal<(string * string) list>(k.Dispositions |> List.map (fun d -> d.ObligationId, d.Status), l.Dispositions |> List.map (fun d -> d.ObligationId, d.Status))
    | other -> failwithf "%A" other
    Assert.True(Result.isError (MechanicalWire.readDispositions """{"dispositions":[{"disposition":"implemented"}]}"""))
