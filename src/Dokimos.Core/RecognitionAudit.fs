namespace Dokimos.Core

open System
open System.Text
open System.Text.Json
open System.Security.Cryptography

/// One finding of the recognition audit. `Code` is the stable machine contract.
type RecognitionFinding =
    { Code: string
      Subject: string
      Detail: string }

type RecognitionAuditReport =
    { CohortId: string
      Assigned: int
      Constructible: int
      DecisionRequired: int
      Delegated: int
      Blocked: int
      Excluded: int
      Packets: int
      /// "byte-identical" when the decision file's SHA-256 equals the digest
      /// the report binds; otherwise why not. Ordo binds the canonical
      /// encoding, so a non-canonical file differs without being different
      /// records. Every content check below uses the supplied file either way.
      DecisionsBinding: string
      Findings: RecognitionFinding list }

/// Independently audits an Ordo pattern-recognition report
/// (`ordo.pattern-recognition/1`) and its delegation packets
/// (`ordo.delegation-contract/1`) against the pinned manifest, the blueprint,
/// the approved decision records and the operator's approvals. It re-derives
/// the precision rules from those inputs and never calls Ordo: a report that
/// calls something constructible must earn it from the inputs here.
///
/// The audit is a check of classification and handoff bounds, not proof that
/// any implementation is correct. Behavior is verified separately, with the
/// same oracle standard for generated and delegated code.
[<RequireQualifiedAccess>]
module RecognitionAudit =

    [<Literal>]
    let Contract = "dokimos.recognition-audit/1"

    /// Stable finding codes. Renaming one is a breaking contract change.
    module Codes =
        let UnsupportedReport = "recognition-unsupported-report"
        let ManifestMismatch = "recognition-manifest-mismatch"
        let DecisionsDigestMismatch = "recognition-decisions-digest-mismatch"
        let CohortMissing = "recognition-cohort-missing"
        let RequirementMissing = "recognition-requirement-missing"
        let RequirementDuplicated = "recognition-requirement-duplicated"
        let RequirementInvented = "recognition-requirement-invented"
        let FalseConstructible = "recognition-false-constructible"
        let SilentDefault = "recognition-silent-default"
        let ConflictIgnored = "recognition-conflict-ignored"
        let PacketMissing = "packet-missing"
        let PacketUnlisted = "packet-unlisted"
        let PacketDigestMismatch = "packet-digest-mismatch"
        let PacketKeysMismatch = "packet-keys-mismatch"
        let DelegationUncovered = "delegation-uncovered"
        let DelegationDuplicated = "delegation-duplicated"
        let PacketScopeViolation = "packet-scope-violation"
        let PacketBoundaryOverlap = "packet-boundary-overlap"
        let GeneratedRootUnprotected = "packet-generated-root-unprotected"
        let DecisionContextMissing = "packet-decision-context-missing"
        let EscalationMissing = "packet-escalation-missing"
        let VerificationMissing = "packet-verification-missing"

    let private get (name: string) (value: JsonElement) =
        let mutable property = Unchecked.defaultof<JsonElement>
        if value.ValueKind = JsonValueKind.Object && value.TryGetProperty(name, &property) then Some property else None

    let private text name element =
        match get name element with
        | Some v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let private array name element =
        match get name element with
        | Some v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toList
        | _ -> []

    let private strings name element =
        array name element |> List.choose (fun v -> if v.ValueKind = JsonValueKind.String then Option.ofObj (v.GetString()) else None)

    let private sha256 (bytes: byte[]) = "sha256:" + (SHA256.HashData bytes |> Convert.ToHexString).ToLowerInvariant()

    let private finding code subject detail = { Code = code; Subject = subject; Detail = detail }

    /// The literal prefix of a glob, up to its first wildcard segment.
    let private literalPrefix (glob: string) =
        glob.Split('/') |> Array.takeWhile (fun s -> not (s.Contains "*")) |> String.concat "/"

    let private overlaps (allowed: string) (forbidden: string) =
        let a, f = literalPrefix allowed, literalPrefix forbidden
        let under (x: string) (y: string) = y = "" || x = y || x.StartsWith(y + "/")
        under a f || under f a

    type private Facts =
        { Owners: Map<string, (string * string) list> // key -> (nodeId, kind)
          Disposition: Map<string, string>
          Transitions: Set<string>
          StateModelNodes: string list
          Ports: Map<string, (string * string * JsonElement option) list> } // key -> (nodeId, port, onFailure)

    let private facts (blueprint: JsonElement) (decisions: JsonElement option) =
        let nodes = array "nodes" blueprint
        let owners =
            nodes
            |> List.filter (fun n -> text "kind" n <> "cohort")
            |> List.collect (fun n -> strings "requirementKeys" n |> List.map (fun k -> k, (text "id" n, text "kind" n)))
            |> List.groupBy fst
            |> List.map (fun (k, xs) -> k, xs |> List.map snd)
            |> Map.ofList
        let disposition =
            array "requirements" blueprint
            |> List.map (fun r ->
                (get "source" r |> Option.map (text "key") |> Option.defaultValue ""),
                (get "disposition" r |> Option.map (text "kind") |> Option.defaultValue ""))
            |> Map.ofList
        let records = decisions |> Option.map (array "records") |> Option.defaultValue []
        let content r = get "content" r
        let stateModels = records |> List.filter (fun r -> content r |> Option.map (text "kind") = Some "stateModel")
        let transitions =
            stateModels
            |> List.collect (fun r -> content r |> Option.map (array "transitions") |> Option.defaultValue [])
            |> List.collect (strings "requirementKeys")
            |> Set.ofList
        let ports =
            records
            |> List.filter (fun r -> content r |> Option.map (text "kind") = Some "portContract")
            |> List.collect (fun r ->
                content r |> Option.map (array "ports") |> Option.defaultValue []
                |> List.collect (fun p -> strings "requirementKeys" p |> List.map (fun k -> k, (text "nodeId" r, text "port" p, get "onFailure" p))))
            |> List.groupBy fst
            |> List.map (fun (k, xs) -> k, xs |> List.map snd)
            |> Map.ofList
        { Owners = owners
          Disposition = disposition
          Transitions = transitions
          StateModelNodes = stateModels |> List.map (text "nodeId")
          Ports = ports }

    let private undecided (onFailure: JsonElement option) =
        match onFailure with
        | None -> true
        | Some v when v.ValueKind = JsonValueKind.Null -> true
        | Some v when v.ValueKind = JsonValueKind.String -> v.GetString() = "unresolved"
        | Some _ -> false

    /// Why a requirement may NOT be constructible, from the inputs alone.
    let private constructibleObjections (f: Facts) (approved: Set<string>) key =
        let owners = Map.tryFind key f.Owners |> Option.defaultValue []
        let ports = Map.tryFind key f.Ports |> Option.defaultValue []
        let kindOf id =
            owners |> List.tryFind (fst >> (=) id) |> Option.map snd
        [ if not (Set.contains key f.Transitions) then "no approved state-model transition cites it"
          if not (List.isEmpty ports) then "a port cites it, so part of its behavior is handwritten"
          for id, kind in owners do
              if kind = "conflict" then sprintf "conflict %s owns it" id
              if kind = "decision" && not (Set.contains id approved) then sprintf "decision %s is not approved" id
          match Map.tryFind key f.Disposition with
          | Some "modeled" -> ()
          | other -> sprintf "blueprint disposition is %s" (defaultArg other "missing")
          for id in f.StateModelNodes do
              if not (Set.contains id approved) then sprintf "state model record %s is not on an approved decision" id
              match kindOf id with
              | Some k when k <> "decision" -> sprintf "state model record %s is on a %s node" id k
              | _ -> () ]

    let private outcomeKind (requirement: JsonElement) =
        get "outcome" requirement |> Option.map (text "kind") |> Option.defaultValue ""

    let audit
        (manifestJson: string)
        (blueprintJson: string)
        (decisionsBytes: byte[] option)
        (approvedDecisionIds: string list)
        (reportJson: string)
        (packetFiles: (string * byte[]) list)
        : Result<RecognitionAuditReport, string list> =
        try
            use manifestDoc = JsonDocument.Parse manifestJson
            use blueprintDoc = JsonDocument.Parse blueprintJson
            use reportDoc = JsonDocument.Parse reportJson
            use decisionsDoc = decisionsBytes |> Option.map (fun b -> JsonDocument.Parse(ReadOnlyMemory b)) |> Option.toObj
            let manifest, blueprint, report = manifestDoc.RootElement, blueprintDoc.RootElement, reportDoc.RootElement
            let decisions = decisionsDoc |> Option.ofObj |> Option.map _.RootElement
            let approved = Set.ofList approvedDecisionIds
            let cohortId = text "cohortId" report
            let cohort = array "nodes" blueprint |> List.tryFind (fun n -> text "id" n = cohortId && text "kind" n = "cohort")
            let assigned = cohort |> Option.map (strings "requirementKeys") |> Option.defaultValue [] |> List.distinct
            let f = facts blueprint decisions
            let requirements = array "requirements" report
            let excluded = array "excluded" report
            let outcomes = requirements |> List.map (fun r -> text "key" r, outcomeKind r) |> Map.ofList
            let reported = (requirements |> List.map (text "key")) @ (excluded |> List.map (text "key"))
            let packets =
                packetFiles
                |> List.map (fun (name, bytes) ->
                    use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
                    let root = doc.RootElement.Clone()
                    name, sha256 bytes, root)
            let listed = array "packets" report |> List.map (fun p -> text "id" p, (text "digest" p, strings "requirementKeys" p))
            let packetKeys = packets |> List.collect (fun (_, _, p) -> strings "requirementKeys" p)

            let findings =
                [ if text "schemaVersion" report <> "ordo.pattern-recognition/1" then
                      finding Codes.UnsupportedReport "" ("unsupported report schema " + text "schemaVersion" report)
                  if text "sourceManifestDigest" report <> text "digest" manifest then
                      finding Codes.ManifestMismatch "" "the report is bound to another source manifest"
                  match decisionsBytes, get "decisionRecordsDigest" report with
                  | Some _, Some d when d.ValueKind = JsonValueKind.Null ->
                      finding Codes.DecisionsDigestMismatch "" "decision records were supplied but the report used none"
                  | _ -> ()
                  if Option.isNone cohort then finding Codes.CohortMissing cohortId "the report's cohort is not a cohort of the blueprint"

                  // Every assigned requirement exactly once.
                  for k in assigned do
                      if not (List.contains k reported) then finding Codes.RequirementMissing k "assigned requirement has no outcome"
                  for k, n in reported |> List.countBy id do
                      if n > 1 then finding Codes.RequirementDuplicated k "requirement has more than one outcome"
                      if not (List.contains k assigned) then finding Codes.RequirementInvented k "requirement is not assigned to the cohort"

                  // Precision: constructible must be earned from the inputs.
                  for k, kind in Map.toList outcomes do
                      let ports = Map.tryFind k f.Ports |> Option.defaultValue []
                      let owners = Map.tryFind k f.Owners |> Option.defaultValue []
                      if kind = "constructible" then
                          match constructibleObjections f approved k with
                          | [] -> ()
                          | reasons -> finding Codes.FalseConstructible k (String.concat "; " reasons)
                      if (kind = "constructible" || kind = "delegate")
                         && ports |> List.exists (fun (_, port, onFailure) -> port = "transitionEffect" && undecided onFailure) then
                          finding Codes.SilentDefault k "an effect failure disposition is undecided, but the outcome does not wait on a decision"
                      if (kind = "constructible" || kind = "delegate")
                         && owners |> List.exists (fun (id, nodeKind) -> nodeKind = "decision" && not (Set.contains id approved)) then
                          finding Codes.SilentDefault k "an owning decision is not approved, but the outcome does not wait on it"
                      if (kind = "constructible" || kind = "delegate") && owners |> List.exists (snd >> (=) "conflict") then
                          finding Codes.ConflictIgnored k "an open conflict owns the requirement"

                  // Packet integrity.
                  for id, (digest, keys) in listed do
                      match packets |> List.tryFind (fun (_, _, p) -> text "id" p = id) with
                      | None -> finding Codes.PacketMissing id "the report lists a packet that was not supplied"
                      | Some(_, actual, p) ->
                          if actual <> digest then finding Codes.PacketDigestMismatch id "packet bytes differ from the digest in the report"
                          if List.sort (strings "requirementKeys" p) <> List.sort keys then
                              finding Codes.PacketKeysMismatch id "packet requirements differ from the report"
                  for name, _, p in packets do
                      if not (listed |> List.exists (fst >> (=) (text "id" p))) then
                          finding Codes.PacketUnlisted name "a supplied packet is not in the report"

                  // Delegation coverage and scope.
                  for k, kind in Map.toList outcomes do
                      let n = packetKeys |> List.filter ((=) k) |> List.length
                      if kind = "delegate" && n = 0 then finding Codes.DelegationUncovered k "delegated requirement is in no packet"
                      if n > 1 then finding Codes.DelegationDuplicated k "requirement is in more than one packet"
                  for k in packetKeys |> List.distinct do
                      match Map.tryFind k outcomes with
                      | Some "delegate" -> ()
                      | Some "decisionRequired" when packets |> List.exists (fun (_, _, p) -> List.contains k (strings "escalationRequired" p)) -> ()
                      | other -> finding Codes.PacketScopeViolation k (sprintf "packet hands over a requirement whose outcome is %s" (defaultArg other "missing"))

                  // Packet bounds and context.
                  for _, _, p in packets do
                      let id = text "id" p
                      let keys = strings "requirementKeys" p
                      let forbidden = strings "forbiddenPaths" p
                      for a in strings "allowedPaths" p do
                          for g in forbidden do
                              if overlaps a g then finding Codes.PacketBoundaryOverlap id (sprintf "allowed %s overlaps forbidden %s" a g)
                      if not (List.isEmpty f.StateModelNodes) && List.isEmpty forbidden then
                          finding Codes.GeneratedRootUnprotected id "generated code exists but the packet forbids no path"
                      let listedNodes name = array name p |> List.map (text "nodeId") |> Set.ofList
                      let decisionsGiven = listedNodes "approvedDecisions"
                      let verificationGiven = listedNodes "verificationObligations"
                      let escalation = strings "escalationRequired" p
                      for k in keys do
                          for id', kind in Map.tryFind k f.Owners |> Option.defaultValue [] do
                              if kind = "decision" && Set.contains id' approved && not (Set.contains id' decisionsGiven) then
                                  finding Codes.DecisionContextMissing id (sprintf "approved decision %s owns %s but is not in the packet" id' k)
                              if kind = "verificationObligation" && not (Set.contains id' verificationGiven) then
                                  finding Codes.VerificationMissing id (sprintf "verification obligation %s for %s is not in the packet" id' k)
                              if kind = "conflict" && not (List.contains k escalation) then
                                  finding Codes.EscalationMissing id (sprintf "conflict %s owns %s but escalation is not required" id' k) ]

            let count kind = outcomes |> Map.filter (fun _ v -> v = kind) |> Map.count
            let binding =
                match decisionsBytes, get "decisionRecordsDigest" report with
                | None, _ -> "no decision records supplied"
                | Some bytes, Some d when d.ValueKind = JsonValueKind.String && d.GetString() = sha256 bytes -> "byte-identical"
                | Some _, Some d when d.ValueKind = JsonValueKind.String -> "not byte-identical (non-canonical file, or other records)"
                | Some _, _ -> "report binds no decision records"
            Ok
                { CohortId = cohortId
                  Assigned = assigned.Length
                  Constructible = count "constructible"
                  DecisionRequired = count "decisionRequired"
                  Delegated = count "delegate"
                  Blocked = count "blocked"
                  Excluded = excluded.Length
                  Packets = packets.Length
                  DecisionsBinding = binding
                  Findings = findings |> List.distinct |> List.sortBy (fun x -> x.Code, x.Subject, x.Detail) }
        with
        | :? JsonException as ex -> Error [ "Invalid JSON: " + ex.Message ]
        | :? InvalidOperationException as ex -> Error [ "Invalid shape: " + ex.Message ]
