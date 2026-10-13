/// Fixture helpers for the CONSTRUCT-010 decision tests: the real timer
/// cohort as ordo#78 generates it, and the frozen expanded ECIR.
module Dokimos.Core.Tests.ConstructionDecisionFixtures

open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.Tests.ConstructionFixtures

let v3 relative = read "time-entry-timer-v3" relative
let expanded relative = read "time-entry-expanded-01" relative

let node (json: string) : JsonNode =
    match JsonNode.Parse json with
    | null -> failwith "JSON null"
    | n -> n

let child (n: JsonNode) (name: string) : JsonNode =
    match n[name] with
    | null -> failwithf "no %s" name
    | c -> c

let element (json: string) =
    match ConstructionJson.parse "fixture" json with
    | Ok e -> e
    | Error e -> failwith e

/// Ordo's default plan wrapped as a prepared artifact, only to read its
/// obligations through Dokimos's strict reader.
let obligationsOf (defaultPlan: string) =
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

let planOf (defaultPlan: string) = (child (node defaultPlan) "plan").ToJsonString()

let keysOf (sourceManifest: string) =
    ConstructionJson.array "requirements" (element sourceManifest) |> List.map (ConstructionJson.text "key") |> Set.ofList

let nodesOf (blueprint: string) =
    ConstructionJson.array "nodes" (element blueprint) |> List.map (fun n -> ConstructionJson.text "id" n, ConstructionJson.text "kind" n) |> Map.ofList

let bytes (s: string) = Encoding.UTF8.GetBytes s

let cohortKey suffix = "kemiller2002/time-entry-state-machine/" + suffix

/// The decision context of the real timer cohort (ordo#78, unlisted pairs refused).
let timerContext (planJson: string) =
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

let timer () = timerContext (planOf (v3 "ordo-output/default-plan.json"))

let openTimer () =
    { timerContext (planOf (v3 "ordo-output-open/default-plan.json")) with
        Obligations = obligationsOf (v3 "ordo-output-open/default-plan.json")
        Ledger = Some(DecisionInputs.readLedger (bytes (v3 "ordo-output-open/ledger.json"))) }

let manifest path =
    match ConstructionModels.readManifest (v3 path) with
    | Ok m -> m
    | Error e -> failwith e

let generatedFor dir workflow (edit: string -> string) =
    let code tier = Some(edit (v3 $"{dir}/generated/{workflow}/{workflow}.{tier}.g.fs"))
    DecisionInputs.generated workflow (manifest $"{dir}/generated/{workflow}/construction-manifest.json") (code "Transition") (code "Orchestration")

let run (check: DecisionContext -> MechanicalCheck * ConstructionFinding list) ctx = check ctx

let codes (findings: ConstructionFinding list) = findings |> List.map (fun f -> f.Code, f.Subject)

let has code subject (findings: ConstructionFinding list) =
    Assert.True(findings |> List.exists (fun f -> f.Code = code && f.Subject = subject), sprintf "expected %s on %s; got %A" code subject (codes findings))

/// Replaces the first operation matching `pick` in a plan JSON.
let editPlan (planJson: string) (pick: JsonNode -> bool) (edit: JsonNode -> unit) =
    let plan = node planJson
    let op = (child plan "operations").AsArray() |> Seq.choose Option.ofObj |> Seq.find pick
    edit op
    plan.ToJsonString()

let opIs name (o: JsonNode) = (child o "op").GetValue<string>() = name

