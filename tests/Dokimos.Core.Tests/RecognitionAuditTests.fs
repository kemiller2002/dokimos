/// The recognition audit re-derives Ordo's precision rules from the pinned
/// inputs. A clean Ordo report passes; every tampering below is caught.
module Dokimos.Core.Tests.RecognitionAuditTests

open System.IO
open System.Text
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Core.Tests.ConstructionFixtures

let private dir = Path.Combine(repositoryRoot, "tests", "fixtures", "recognition", "timer")
let private file name = File.ReadAllText(Path.Combine(dir, name))
let private packetFiles () =
    Directory.GetFiles(Path.Combine(dir, "packets"), "*.json")
    |> Array.sort
    |> Array.map (fun p -> Path.GetFileName p |> string, File.ReadAllBytes p)
    |> Array.toList

let private approved = [ "DEC-TIMER-STATES" ]

let private auditWith report packets approvals =
    RecognitionAudit.audit (file "source-manifest.json") (file "blueprint.json")
        (Some(File.ReadAllBytes(Path.Combine(dir, "construction-decisions.json")))) approvals report packets
    |> function Ok r -> r | Error e -> failwithf "%A" e

let private codes (r: RecognitionAuditReport) = r.Findings |> List.map _.Code

let private stopKey = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L131-L144"
let private sessionKey = "kemiller2002/time-entry-state-machine/docs/architecture/business-activity-ledger-state-model.md#L145-L148"

let private nn (x: JsonNode | null) : JsonNode =
    match x with
    | null -> failwith "unexpected JSON null"
    | v -> v

let private parse (text: string) = nn (JsonNode.Parse text)
let private at (name: string) (n: JsonNode) = nn n[name]
let private items (n: JsonNode) = n.AsArray() |> Seq.map nn |> Seq.toList
let private str (n: JsonNode) = n.GetValue<string>()

/// Rewrites one requirement's outcome in the report.
let private withOutcome (key: string) (outcome: string) =
    let root = parse (file "report.json")
    for r in items (at "requirements" root) do
        if str (at "key" r) = key then r["outcome"] <- parse outcome
    root.ToJsonString()

let private editPacket (containing: string) (edit: JsonNode -> unit) =
    packetFiles ()
    |> List.map (fun (name, bytes) ->
        let node = parse (Encoding.UTF8.GetString bytes)
        if items (at "requirementKeys" node) |> List.exists (fun k -> str k = containing) then
            edit node
            name, Encoding.UTF8.GetBytes(node.ToJsonString())
        else name, bytes)

[<Fact>]
let ``an Ordo report and its packets pass the independent audit`` () =
    let r = auditWith (file "report.json") (packetFiles ()) approved
    Assert.Empty r.Findings
    Assert.Equal(6, r.Assigned)
    Assert.Equal(3, r.Constructible)
    Assert.Equal("byte-identical", r.DecisionsBinding)

[<Fact>]
let ``calling an undecided effect requirement constructible is a false positive and a silent default`` () =
    let report = withOutcome stopKey """{"kind":"constructible","patterns":[],"delegatedPrerequisites":[]}"""
    let found = auditWith report (packetFiles ()) approved |> codes
    Assert.Contains(RecognitionAudit.Codes.FalseConstructible, found)
    Assert.Contains(RecognitionAudit.Codes.SilentDefault, found)

[<Fact>]
let ``calling an uncovered requirement constructible is a false positive`` () =
    let report = withOutcome sessionKey """{"kind":"constructible","patterns":[],"delegatedPrerequisites":[]}"""
    let r = auditWith report (packetFiles ()) approved
    Assert.Contains(r.Findings, fun f -> f.Code = RecognitionAudit.Codes.FalseConstructible && f.Detail.Contains "no approved state-model transition")

[<Fact>]
let ``constructible without the operator's approval is refused`` () =
    let found = auditWith (file "report.json") (packetFiles ()) [] |> codes
    Assert.Contains(RecognitionAudit.Codes.FalseConstructible, found)
    Assert.Contains(RecognitionAudit.Codes.SilentDefault, found)

[<Fact>]
let ``tampered packet bytes, missing packets and dropped requirements are caught`` () =
    let tampered = editPacket sessionKey (fun p -> p["interfaces"] <- JsonArray())
    Assert.Contains(RecognitionAudit.Codes.PacketDigestMismatch, auditWith (file "report.json") tampered approved |> codes)
    let missing = auditWith (file "report.json") (packetFiles () |> List.tail) approved |> codes
    Assert.Contains(RecognitionAudit.Codes.PacketMissing, missing)
    Assert.Contains(RecognitionAudit.Codes.DelegationUncovered, missing)
    let root = parse (file "report.json")
    (at "requirements" root).AsArray().RemoveAt 0
    Assert.Contains(RecognitionAudit.Codes.RequirementMissing, auditWith (root.ToJsonString()) (packetFiles ()) approved |> codes)

[<Fact>]
let ``a packet that drops its approved decision, its oracle or overlaps generated code is caught`` () =
    let noContext = editPacket sessionKey (fun p -> p["approvedDecisions"] <- JsonArray(); p["verificationObligations"] <- JsonArray())
    let found = auditWith (file "report.json") noContext approved |> codes
    Assert.Contains(RecognitionAudit.Codes.DecisionContextMissing, found)
    Assert.Contains(RecognitionAudit.Codes.VerificationMissing, found)
    let overlap = editPacket sessionKey (fun p -> p["allowedPaths"] <- JsonArray(JsonValue.Create "generated/**"))
    Assert.Contains(RecognitionAudit.Codes.PacketBoundaryOverlap, auditWith (file "report.json") overlap approved |> codes)
    let unprotected = editPacket sessionKey (fun p -> p["forbiddenPaths"] <- JsonArray())
    Assert.Contains(RecognitionAudit.Codes.GeneratedRootUnprotected, auditWith (file "report.json") unprotected approved |> codes)

[<Fact>]
let ``a packet that hands over undecided work is a scope violation`` () =
    let widened = editPacket sessionKey (fun p -> (at "requirementKeys" p).AsArray().Add(JsonValue.Create stopKey))
    Assert.Contains(RecognitionAudit.Codes.PacketScopeViolation, auditWith (file "report.json") widened approved |> codes)

// ---------------------------------------------------------------------------
// Conflicts settled by an approved decision (`conflictResolution`).
// ---------------------------------------------------------------------------

let private settledDir = Path.Combine(repositoryRoot, "tests", "fixtures", "recognition", "timer-settled")
let private settledFile name = File.ReadAllText(Path.Combine(settledDir, name))
let private settledPackets () =
    Directory.GetFiles(Path.Combine(settledDir, "packets"), "*.json")
    |> Array.sort
    |> Array.map (fun p -> Path.GetFileName p |> string, File.ReadAllBytes p)
    |> Array.toList

let private auditSettled report packets approvals =
    RecognitionAudit.audit (settledFile "source-manifest.json") (settledFile "blueprint.json")
        (Some(File.ReadAllBytes(Path.Combine(settledDir, "construction-decisions.json")))) approvals report packets
    |> function Ok r -> r | Error e -> failwithf "%A" e

let private settledApprovals = [ "DEC-TIMER-STATES"; "DEC-SESSION-SCOPE" ]

[<Fact>]
let ``a conflict settled by an approved decision is not an ignored conflict`` () =
    let r = auditSettled (settledFile "report.json") (settledPackets ()) settledApprovals
    Assert.Empty r.Findings
    Assert.Equal(2, r.Delegated)

[<Fact>]
let ``a settlement by an unapproved decision leaves the conflict open`` () =
    let found = auditSettled (settledFile "report.json") (settledPackets ()) [ "DEC-TIMER-STATES" ] |> codes
    Assert.Contains(RecognitionAudit.Codes.ConflictIgnored, found)

[<Fact>]
let ``a packet that hides which decision settles its conflict is caught`` () =
    let stripped =
        settledPackets ()
        |> List.map (fun (name, bytes) ->
            let node = parse (Encoding.UTF8.GetString bytes)
            for d in items (at "approvedDecisions" node) do d["resolves"] <- JsonArray()
            name, Encoding.UTF8.GetBytes(node.ToJsonString()))
    let found = auditSettled (settledFile "report.json") stripped settledApprovals |> codes
    Assert.Contains(RecognitionAudit.Codes.ResolutionMissing, found)
