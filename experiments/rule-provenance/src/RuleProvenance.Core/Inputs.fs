namespace RuleProvenance

open System
open System.Text.Json

/// Decoding of the handoff (echelon.authority-handoff/0.1) and construction trace
/// (echelon.construction-trace/0.1). Decoding is total: malformed input is an
/// Error listing every problem found, never an exception (exit code 2 upstream).
module Inputs =
    type private Decoder<'a> = JsonElement -> Result<'a, string list>

    let private text (e: JsonElement) : string =
        match e.GetString() with
        | null -> ""
        | s -> s

    let private prop (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private str path name (e: JsonElement) =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.String -> Ok((text v))
        | Some _ -> Error [ $"{path}.{name}: expected a string" ]
        | None -> Error [ $"{path}.{name}: required" ]

    let private optStr name (e: JsonElement) =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.String -> Some((text v))
        | _ -> None

    let private strList path name (e: JsonElement) =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.Array ->
            let items = v.EnumerateArray() |> List.ofSeq
            if items |> List.forall (fun i -> i.ValueKind = JsonValueKind.String) then
                Ok(items |> List.map (fun i -> (text i)))
            else Error [ $"{path}.{name}: expected an array of strings" ]
        | Some _ -> Error [ $"{path}.{name}: expected an array" ]
        | None -> Error [ $"{path}.{name}: required" ]

    let private arr path name (decode: string -> Decoder<'a>) (e: JsonElement) =
        match prop name e with
        | Some v when v.ValueKind = JsonValueKind.Array ->
            let results = v.EnumerateArray() |> List.ofSeq |> List.mapi (fun i item -> decode $"{path}.{name}[{i}]" item)
            let errors = results |> List.collect (function Error es -> es | Ok _ -> [])
            if errors.IsEmpty then Ok(results |> List.choose (function Ok x -> Some x | Error _ -> None)) else Error errors
        | Some _ -> Error [ $"{path}.{name}: expected an array" ]
        | None -> Error [ $"{path}.{name}: required" ]

    let private constant path name expected (e: JsonElement) =
        match str path name e with
        | Ok v when v = expected -> Ok()
        | Ok v -> Error [ $"{path}.{name}: expected '{expected}', found '{v}'" ]
        | Error es -> Error es

    /// Applicative combination: collect the errors of every field.
    let private (<!>) f r = Result.map f r

    let private (<*>) (rf: Result<'a -> 'b, string list>) (ra: Result<'a, string list>) =
        match rf, ra with
        | Ok f, Ok a -> Ok(f a)
        | Error e1, Error e2 -> Error(e1 @ e2)
        | Error e, _
        | _, Error e -> Error e

    let private status path (e: JsonElement) =
        match str path "status" e with
        | Ok "accepted" -> Ok Accepted
        | Ok "superseded" -> Ok Superseded
        | Ok "proposed" -> Ok Proposed
        | Ok "rejected" -> Ok Rejected
        | Ok other -> Error [ $"{path}.status: unknown status '{other}'" ]
        | Error es -> Error es

    let private requirement path (e: JsonElement) =
        (fun id s owner -> { Id = id; Status = s; Owner = owner; SupersededBy = optStr "supersededBy" e; Statement = optStr "statement" e |> Option.defaultValue "" } : Requirement)
        <!> str path "id" e
        <*> status path e
        <*> str path "owner" e

    let private parameters (e: JsonElement) =
        match prop "parameters" e with
        | Some p when p.ValueKind = JsonValueKind.Object ->
            p.EnumerateObject()
            |> Seq.map (fun kv ->
                let value =
                    match kv.Value.ValueKind with
                    | JsonValueKind.String -> (text kv.Value)
                    | _ -> kv.Value.GetRawText()
                kv.Name, value)
            |> Seq.sortBy fst
            |> List.ofSeq
        | _ -> []

    let private decision path (e: JsonElement) =
        (fun id s kind owner ->
            { Id = id
              Status = s
              Kind = kind
              Owner = owner
              SupersededBy = optStr "supersededBy" e
              Parameters = parameters e
              Statement = optStr "statement" e |> Option.defaultValue "" }
            : Decision)
        <!> str path "id" e
        <*> status path e
        <*> str path "kind" e
        <*> str path "owner" e

    let private question path (e: JsonElement) =
        (fun id owner affects outcome -> { Id = id; Owner = owner; Affects = affects; PermittedOutcome = outcome } : PolicyQuestion)
        <!> str path "id" e
        <*> str path "owner" e
        <*> strList path "affects" e
        <*> str path "permittedOutcome" e

    let private observation path (e: JsonElement) =
        let location = prop "location" e
        let locPath =
            match location with
            | Some l -> str (path + ".location") "path" l
            | None -> Error [ $"{path}.location: required" ]
        (fun id description kind p ->
            { Id = id
              Description = description
              Kind = kind
              Path = p
              Symbol = location |> Option.bind (optStr "symbol")
              QuestionId = optStr "questionId" e })
        <!> str path "id" e
        <*> str path "description" e
        <*> str path "kind" e
        <*> locPath

    let private invariant path (e: JsonElement) =
        (fun id statement derived -> { Id = id; Statement = statement; DerivedFrom = derived } : Invariant)
        <!> str path "id" e
        <*> str path "statement" e
        <*> strList path "derivedFrom" e

    let private parse (label: string) (bytes: byte[]) =
        try
            use doc = JsonDocument.Parse(ReadOnlyMemory bytes)
            Ok(doc.RootElement.Clone())
        with :? JsonException as ex ->
            Error [ $"{label}: not valid JSON ({ex.Message})" ]

    let decodeHandoff (bytes: byte[]) : Result<Handoff, string list> =
        parse "handoff" bytes
        |> Result.bind (fun root ->
            let p = "handoff"
            (fun () () id reqs decs qs obs invs ->
                { HandoffId = id
                  Requirements = reqs
                  Decisions = decs
                  PolicyQuestions = qs
                  Observations = obs
                  Invariants = invs })
            <!> constant p "schema" "echelon.authority-handoff" root
            <*> constant p "schemaVersion" "0.1" root
            <*> str p "handoffId" root
            <*> arr p "requirements" requirement root
            <*> arr p "decisions" decision root
            <*> arr p "policyQuestions" question root
            <*> arr p "observations" observation root
            <*> arr p "invariants" invariant root)

    let private disposition path (e: JsonElement) =
        match str path "disposition" e with
        | Ok "authorized" -> Ok Authorized
        | Ok "deferred-pending-question" -> Ok DeferredPendingQuestion
        | Ok "technical" -> Ok Technical
        | Ok other -> Error [ $"{path}.disposition: unknown '{other}'" ]
        | Error es -> Error es

    let private traceAuthority path (e: JsonElement) =
        (fun kind id -> { Kind = kind; Id = id }) <!> str path "kind" e <*> str path "id" e

    let private construct path (e: JsonElement) =
        (fun symbol kind file disp auth -> { Symbol = symbol; Kind = kind; File = file; Disposition = disp; Authority = auth })
        <!> str path "symbol" e
        <*> str path "kind" e
        <*> str path "file" e
        <*> disposition path e
        <*> arr path "authority" traceAuthority e

    let private excluded path (e: JsonElement) = str path "observationId" e

    let private output path (e: JsonElement) =
        (fun p sha -> { Path = p; Sha256 = sha }) <!> str path "path" e <*> str path "sha256" e

    let decodeTrace (bytes: byte[]) : Result<Trace, string list> =
        parse "trace" bytes
        |> Result.bind (fun root ->
            let p = "trace"
            let handoff =
                match prop "handoff" root with
                | Some h -> (fun id sha -> id, sha) <!> str (p + ".handoff") "handoffId" h <*> str (p + ".handoff") "sha256" h
                | None -> Error [ $"{p}.handoff: required" ]
            let outputs =
                match prop "outputs" root with
                | Some _ -> arr p "outputs" output root
                | None -> Ok []
            (fun () () (hid, hsha) constructs excl outs ->
                { HandoffId = hid
                  HandoffSha256 = hsha
                  Constructs = constructs
                  ExcludedObservations = excl
                  Outputs = outs })
            <!> constant p "schema" "echelon.construction-trace" root
            <*> constant p "schemaVersion" "0.1" root
            <*> handoff
            <*> arr p "constructs" construct root
            <*> arr p "excluded" excluded root
            <*> outputs)

    /// A prior audit's validity block, for the evidence-reuse check (A2).
    let decodePriorValidity (bytes: byte[]) : Result<Validity, string list> =
        parse "prior audit" bytes
        |> Result.bind (fun root ->
            let p = "audit"
            match prop "validity" root with
            | None -> Error [ $"{p}.validity: required" ]
            | Some v ->
                let p = p + ".validity"
                (fun () src h t tn tv c sdk ->
                    { SourceDigest = src
                      HandoffDigest = h
                      TraceDigest = t
                      ToolName = tn
                      ToolVersion = tv
                      ConfigDigest = c
                      SdkVersion = sdk
                      DependencyDigest = optStr "dependencyDigest" v })
                <!> constant "audit" "schema" "echelon.provenance-audit" root
                <*> str p "sourceDigest" v
                <*> str p "handoffDigest" v
                <*> str p "traceDigest" v
                <*> str p "toolName" v
                <*> str p "toolVersion" v
                <*> str p "configDigest" v
                <*> str p "sdkVersion" v)

