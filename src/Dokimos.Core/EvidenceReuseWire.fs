namespace Dokimos.Core

open System
open System.Text.Json
open Dokimos.Core.EvidenceReuse

/// Strict intake of `dokimos.evidence-reuse-request/1` and the
/// `dokimos.evidence-provenance/1` records it carries, the pure resolution of
/// declared mappings against a file listing, and the stable
/// `dokimos.evidence-reuse-plan/1` projection.
module EvidenceReuseWire =
    /// The declared test-to-input mapping, before file digests are resolved.
    type MappingSpec =
        { Sources: string list
          TestContract: string
          Dependencies: Map<string, string>
          Configuration: Map<string, string>
          ToolVersion: string }

    type TestSpec =
        { TestId: string
          Freshness: Freshness
          Mapping: MappingSpec option }

    type ReuseRequest =
        { Revision: string
          ChangedPaths: string list option
          GlobalInputs: string list
          IgnoredPaths: string list
          Tests: TestSpec list
          PriorEvidence: PriorEvidence list }

    let private exactWith required optional (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error $"expected object, found {element.ValueKind}"
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            match names |> List.groupBy id |> List.tryFind (fun (_, copies) -> copies.Length > 1) with
            | Some(name, _) -> Error $"duplicate JSON member '{name}'"
            | None ->
                let allowed = Set.ofList (required @ optional)
                match names |> List.tryFind (fun name -> not (Set.contains name allowed)) with
                | Some name -> Error $"unknown JSON member '{name}'"
                | None ->
                    match required |> List.tryFind (fun name -> not (List.contains name names)) with
                    | Some name -> Error $"missing required field '{name}'"
                    | None -> Ok()

    /// Absent means not asserted; JSON null is refused rather than read as absent.
    let private optional name (decoder: Json.Decoder<'a>) : Json.Decoder<'a option> =
        fun element ->
            match element.TryGetProperty(name: string) with
            | true, value -> decoder value |> Result.map Some |> Result.mapError (fun e -> $"{name}: {e}")
            | _ -> Ok None

    let private stringMap (value: Json.Decoder<string>) : Json.Decoder<Map<string, string>> =
        fun element ->
            if element.ValueKind <> JsonValueKind.Object then
                Error $"expected object, found {element.ValueKind}"
            else
                let members = element.EnumerateObject() |> Seq.toList
                match members |> List.groupBy _.Name |> List.tryFind (fun (_, copies) -> copies.Length > 1) with
                | Some(name, _) -> Error $"duplicate JSON member '{name}'"
                | None ->
                    members
                    |> List.fold
                        (fun state item ->
                            state
                            |> Result.bind (fun map ->
                                value item.Value
                                |> Result.map (fun v -> Map.add item.Name v map)
                                |> Result.mapError (fun e -> $"{item.Name}: {e}")))
                        (Ok Map.empty)

    let private digest: Json.Decoder<string> =
        Json.string
        >> Result.bind (fun text -> if isSha256 text then Ok text else Error $"expected sha256:<64 lowercase-or-uppercase hex>, found '{text}'")

    let private paths = Json.list Json.nonEmptyString

    let private mapping: Json.Decoder<MappingSpec> =
        fun element ->
            Json.result {
                do! exactWith [ "Sources"; "TestContract"; "ToolVersion" ] [ "Dependencies"; "Configuration" ] element
                let! sources = Json.field "Sources" paths element
                let! contract = Json.field "TestContract" Json.nonEmptyString element
                let! tool = Json.field "ToolVersion" Json.nonEmptyString element
                let! dependencies = optional "Dependencies" (stringMap Json.nonEmptyString) element
                let! configuration = optional "Configuration" (stringMap Json.string) element
                return
                    { Sources = sources; TestContract = contract; ToolVersion = tool
                      Dependencies = Option.defaultValue Map.empty dependencies
                      Configuration = Option.defaultValue Map.empty configuration }
            }

    let private test: Json.Decoder<TestSpec> =
        fun element ->
            Json.result {
                do! exactWith [ "TestId"; "Freshness" ] [ "Inputs" ] element
                let! id = Json.field "TestId" Json.nonEmptyString element
                let! freshness = Json.field "Freshness" (Json.oneOf freshnessCases) element
                let! inputs = optional "Inputs" mapping element
                return { TestId = id; Freshness = freshness; Mapping = inputs }
            }

    let private recordedInputs: Json.Decoder<IdentityInputs> =
        fun element ->
            Json.result {
                do! exactWith [] [ "Sources"; "TestContract"; "Dependencies"; "Configuration"; "ToolVersion" ] element
                let! sources = optional "Sources" (stringMap digest) element
                let! contract = optional "TestContract" digest element
                let! dependencies = optional "Dependencies" (stringMap Json.nonEmptyString) element
                let! configuration = optional "Configuration" (stringMap Json.string) element
                let! tool = optional "ToolVersion" Json.string element
                return
                    ({ Sources = Option.defaultValue Map.empty sources
                       TestContract = Option.defaultValue "" contract
                       Dependencies = Option.defaultValue Map.empty dependencies
                       Configuration = Option.defaultValue Map.empty configuration
                       ToolVersion = Option.defaultValue "" tool }: IdentityInputs)
            }

    /// A provenance record. Provenance members are optional at intake so a
    /// record without them is refused for reuse (REUSE.PROVENANCE_MISSING)
    /// rather than silently dropped.
    let provenanceRecord: Json.Decoder<PriorEvidence> =
        fun element ->
            Json.result {
                do! exactWith [ "SchemaVersion"; "EvidenceId"; "TestId"; "Outcome" ]
                        [ "ProducedAtRevision"; "Freshness"; "EvidenceIdentity"; "Inputs" ] element
                let! schema = Json.field "SchemaVersion" Json.nonEmptyString element
                do! if schema = ProvenanceSchemaVersion then Ok() else Error $"unsupported provenance schema '{schema}'"
                let! id = Json.field "EvidenceId" Json.nonEmptyString element
                let! testId = Json.field "TestId" Json.nonEmptyString element
                let! outcome = Json.field "Outcome" (Json.oneOf outcomeCases) element
                let! revision = optional "ProducedAtRevision" Json.nonEmptyString element
                let! freshness = optional "Freshness" (Json.oneOf freshnessCases) element
                let! identity = optional "EvidenceIdentity" digest element
                let! inputs = optional "Inputs" recordedInputs element
                return
                    { EvidenceId = id; TestId = testId; Outcome = outcome; ProducedAtRevision = revision
                      Freshness = freshness; EvidenceIdentity = identity; Inputs = inputs }
            }

    let private duplicates values =
        values |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

    let private request: Json.Decoder<ReuseRequest> =
        fun element ->
            Json.result {
                do! exactWith [ "SchemaVersion"; "Revision"; "Tests"; "PriorEvidence" ] [ "ChangedPaths"; "GlobalInputs"; "IgnoredPaths" ] element
                let! schema = Json.field "SchemaVersion" Json.nonEmptyString element
                do! if schema = RequestSchemaVersion then Ok() else Error $"unsupported request schema '{schema}'"
                let! revision = Json.field "Revision" Json.nonEmptyString element
                do! if isImmutableRevision revision then Ok() else Error $"Revision: expected a 40- or 64-hex revision, found '{revision}'"
                let! changed = optional "ChangedPaths" (Json.list Json.nonEmptyString) element
                let! globals = optional "GlobalInputs" paths element
                let! ignored = optional "IgnoredPaths" paths element
                let! tests = Json.field "Tests" (Json.list test) element
                do! match duplicates (tests |> List.map _.TestId) with
                    | [] -> Ok()
                    | ids -> Error("duplicate TestId: " + String.concat ", " ids)
                let! priors = Json.field "PriorEvidence" (Json.list provenanceRecord) element
                do! match duplicates (priors |> List.map _.EvidenceId) with
                    | [] -> Ok()
                    | ids -> Error("duplicate EvidenceId: " + String.concat ", " ids)
                return
                    { Revision = revision; ChangedPaths = changed
                      GlobalInputs = Option.defaultValue [] globals; IgnoredPaths = Option.defaultValue [] ignored
                      Tests = tests; PriorEvidence = priors }
            }

    let readRequest (text: string) =
        Json.parse request text |> Result.mapError (sprintf "%A")

    /// Resolves a declared mapping against the files present at the revision.
    /// `files` lists repository-relative '/' paths; `digestOf` hashes one.
    /// Any pattern matching nothing, or a missing test contract, leaves the
    /// mapping unknown (the test runs).
    let resolve (files: string list) (digestOf: string -> string) (globalInputs: string list) (spec: TestSpec) : TestRequest =
        let inputs =
            match spec.Mapping with
            | None -> Error "no test-to-input mapping is declared"
            | Some mapping ->
                let unmatched = mapping.Sources |> List.filter (fun pattern -> not (files |> List.exists (fun f -> matches [ pattern ] f)))
                if not unmatched.IsEmpty then
                    Error("source patterns match no file: " + String.concat ", " unmatched)
                elif not (List.contains mapping.TestContract files) then
                    Error("test contract not found: " + mapping.TestContract)
                else
                    let sources =
                        files
                        |> List.filter (matches (mapping.Sources @ globalInputs))
                        |> List.map (fun f -> f, digestOf f)
                        |> Map.ofList
                    let resolved: IdentityInputs =
                        { Sources = sources
                          Dependencies = mapping.Dependencies
                          TestContract = digestOf mapping.TestContract
                          Configuration = mapping.Configuration
                          ToolVersion = mapping.ToolVersion }
                    Ok resolved
        { TestId = spec.TestId
          Freshness = spec.Freshness
          Patterns = spec.Mapping |> Option.map (fun m -> m.TestContract :: m.Sources)
          Inputs = inputs }

    let planOf (files: string list) (digestOf: string -> string) (request: ReuseRequest) =
        let tests = request.Tests |> List.map (resolve files digestOf request.GlobalInputs)
        EvidenceReuse.plan request.Revision request.ChangedPaths request.GlobalInputs request.IgnoredPaths tests request.PriorEvidence

    let private inputsWire (inputs: IdentityInputs) =
        {| Sources = inputs.Sources
           Dependencies = inputs.Dependencies
           TestContract = inputs.TestContract
           Configuration = inputs.Configuration
           ToolVersion = inputs.ToolVersion |}

    let toWire (plan: Plan) =
        let reused = plan.Tests |> List.filter (isRun >> not)
        {| Contract = "dokimos.evidence-reuse-plan"
           SchemaVersion = PlanSchemaVersion
           DokimosVersion = DokimosInfo.version
           Revision = plan.Revision
           Acceptance =
            "Reuse is not acceptance. Each reused test carries evidence produced at an earlier revision under an identical identity; the acceptor must judge it independently and can see it here and in the completion receipt's ReusedEvidenceIds."
           Summary =
            {| Tests = plan.Tests.Length
               Run = plan.Tests.Length - reused.Length
               Reuse = reused.Length |}
           ChangedPaths = plan.ChangedPaths
           UnmappedChangedPaths = plan.UnmappedChangedPaths
           Tests =
            plan.Tests
            |> List.map (fun t ->
                {| TestId = t.TestId
                   Freshness = freshnessName t.Freshness
                   Decision = (if isRun t then "run" else "reuse")
                   PathAffected = t.PathAffected
                   CurrentIdentity = t.CurrentIdentity
                   Inputs = t.CurrentInputs |> Option.map inputsWire
                   Reused =
                    match t.Decision with
                    | Reuse prior ->
                        Some
                            {| EvidenceId = prior.EvidenceId
                               EvidenceIdentity = prior.EvidenceIdentity |> Option.defaultValue ""
                               ProducedAtRevision = prior.ProducedAtRevision |> Option.defaultValue ""
                               Freshness = prior.Freshness |> Option.map freshnessName |> Option.defaultValue "" |}
                    | Run -> None
                   Reasons = t.Reasons |}) |}

    /// The provenance record a host writes after running a planned test, so a
    /// later plan can reuse it. Built only from the plan's own resolved inputs.
    let provenanceOf (revision: string) (evidenceId: string) (outcome: Outcome) (decision: TestDecision) =
        decision.CurrentInputs
        |> Option.map (fun inputs ->
            {| SchemaVersion = ProvenanceSchemaVersion
               EvidenceId = evidenceId
               TestId = decision.TestId
               Outcome = outcomeCases |> List.find (fun (_, o) -> o = outcome) |> fst
               ProducedAtRevision = revision
               Freshness = freshnessName decision.Freshness
               EvidenceIdentity = identity inputs
               Inputs = inputsWire inputs |})
