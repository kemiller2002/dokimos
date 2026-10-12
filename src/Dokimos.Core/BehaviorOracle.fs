namespace Dokimos.Core

open System
open System.Text.Json

/// What a recorded-minutes case expects: `Some n` whole minutes recorded.
type MinutesExpect = { Minutes: int }

/// One frozen adapter case. Decide and sequence cases drive
/// `decide`; recorded-minutes cases drive `recordedMinutes`.
type AdapterCaseBody =
    | DecideSteps of state: string * steps: OracleStep list
    | RecordedMinutes of segments: (int64 * int64) list * expect: MinutesExpect option

/// Whether a case counts. An unscored case is observed and reported, never
/// judged: its expected answer is not settled by the sources.
type CaseScoring =
    | Scored
    | Unscored of classification: string * conflict: string

type AdapterCase =
    { Id: string
      Kind: string
      Body: AdapterCaseBody
      Scoring: CaseScoring
      RequirementKeys: string list
      Citation: string }

type AdapterFunction = { Name: string; Signature: string }

/// The frozen adapter binding: the only surface a candidate must expose.
type AdapterBinding =
    { Kind: string
      Contract: string
      Namespace: string
      Module: string
      Functions: AdapterFunction list }

/// An adapter-bound behavioral oracle (`dokimos.adapter-oracle/1`).
type AdapterOracle =
    { OracleId: string
      Digest: string
      FrozenAt: DateTimeOffset option
      ProducerKind: string
      ProducerSystem: string
      Workflow: string
      Binding: AdapterBinding
      Sources: OracleSource list
      Excluded: (string * string) list
      Cases: AdapterCase list }

/// What the Dokimos-launched driver found when it looked for the adapter.
/// Each function is `ok`, `missing` or `signature-mismatch`.
type AdapterProbe =
    { Assembly: string
      Module: string
      Functions: (string * string) list
      Detail: string }

/// One case as the driver observed it, in the adapter's own terms.
/// Status: observed | adapter-missing | signature-mismatch | threw | timeout.
type AdapterObservation =
    { CaseId: string
      Status: string
      Steps: ObservedStep list
      Minutes: int option
      Reason: string }

[<RequireQualifiedAccess>]
module BehaviorOracle =
    [<Literal>]
    let Schema = "dokimos.adapter-oracle/1"

    [<Literal>]
    let AdapterKind = "adapter"

    let private s = ConstructionJson.text
    let private strs = ConstructionJson.strings

    let private expectOf (element: JsonElement) =
        let e = ConstructionJson.get "expect" element |> Option.defaultValue element
        { Outcome = s "outcome" e; Next = s "next" e; Effects = strs "effects" e; Members = [] }

    let private int64Of (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Number ->
            match e.TryGetInt64() with
            | true, v -> Ok v
            | _ -> Error "a segment bound is not a whole number of seconds"
        | _ -> Error "a segment bound is not a number"

    let private segmentsOf (c: JsonElement) =
        ConstructionJson.array "segments" c
        |> List.map (fun pair ->
            match pair.ValueKind with
            | JsonValueKind.Array when pair.GetArrayLength() = 2 ->
                match int64Of pair[0], int64Of pair[1] with
                | Ok a, Ok b -> Ok(a, b)
                | Error e, _ | _, Error e -> Error e
            | _ -> Error "a segment is not a [start, end] pair")
        |> List.fold (fun acc r -> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ [ x ])) acc) (Ok [])

    let private minutesExpect (c: JsonElement) =
        match ConstructionJson.get "expect" c with
        | None -> Ok None
        | Some e ->
            match ConstructionJson.get "recorded" e, ConstructionJson.get "minutes" e with
            | Some r, Some m when r.ValueKind = JsonValueKind.True && m.ValueKind = JsonValueKind.Number ->
                match m.TryGetInt32() with
                | true, minutes -> Ok(Some { Minutes = minutes })
                | _ -> Error "expected minutes are not an integer"
            | _ -> Error "a recorded-minutes expectation must be {recorded: true, minutes: n}"

    let private scoringOf (c: JsonElement) =
        match ConstructionJson.get "scored" c with
        | Some v when v.ValueKind = JsonValueKind.False -> Unscored(s "classification" c, s "conflict" c)
        | _ -> Scored

    let private caseOf (c: JsonElement) : Result<AdapterCase, string> =
        let id = s "id" c
        let body =
            match s "kind" c with
            | "decide" -> Ok(DecideSteps(s "state" c, [ { Command = s "command" c; Expect = expectOf c } ]))
            | "sequence" ->
                Ok(DecideSteps(s "state" c, ConstructionJson.array "steps" c |> List.map (fun st -> { Command = s "command" st; Expect = expectOf st })))
            | "recorded-minutes" ->
                match segmentsOf c, minutesExpect c with
                | Ok segments, Ok expect -> Ok(RecordedMinutes(segments, expect))
                | Error e, _ | _, Error e -> Error e
            | other -> Error("unsupported case kind " + other)
        let scoring = scoringOf c
        match body, scoring with
        | Error e, _ -> Error(id + ": " + e)
        | Ok(RecordedMinutes(_, None)), Scored -> Error(id + ": a scored recorded-minutes case needs an expectation")
        | Ok(RecordedMinutes(_, Some _)), Unscored _ -> Error(id + ": an unscored case must not carry an expectation")
        | Ok _, Unscored(classification, _) when String.IsNullOrWhiteSpace classification ->
            Error(id + ": an unscored case must state its classification")
        | Ok b, _ ->
            Ok { Id = id; Kind = s "kind" c; Body = b; Scoring = scoring; RequirementKeys = strs "requirementKeys" c; Citation = s "citation" c }

    let read (bytes: byte[]) : Result<AdapterOracle, string> =
        let digest = ConstructionJson.sha256Bytes bytes
        ConstructionJson.parse "oracle" (Text.Encoding.UTF8.GetString bytes)
        |> Result.bind (fun root ->
            let binding = ConstructionJson.get "binding" root |> Option.defaultValue root
            if s "schema" root <> Schema then Error("oracle: unsupported schema " + s "schema" root)
            elif s "kind" binding <> AdapterKind then Error("oracle: binding.kind is " + s "kind" binding + ", not " + AdapterKind)
            else
                let cases = ConstructionJson.array "cases" root |> List.map caseOf
                match cases |> List.choose (function Error e -> Some e | Ok _ -> None) with
                | _ :: _ as errors -> Error("oracle: " + String.concat "; " errors)
                | [] ->
                    let producer = ConstructionJson.get "producer" root |> Option.defaultValue root
                    let subject = ConstructionJson.get "subject" root |> Option.defaultValue root
                    let frozen =
                        match DateTimeOffset.TryParse(s "frozenAt" root, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
                        | true, value -> Some value
                        | _ -> None
                    Ok
                        { OracleId = s "oracleId" root
                          Digest = digest
                          FrozenAt = frozen
                          ProducerKind = s "kind" producer
                          ProducerSystem = s "system" producer
                          Workflow = s "workflow" subject
                          Binding =
                            { Kind = s "kind" binding
                              Contract = s "contract" binding
                              Namespace = s "namespace" binding
                              Module = s "module" binding
                              Functions =
                                ConstructionJson.members "functions" binding
                                |> List.map (fun (name, f) -> { Name = name; Signature = s "signature" f }) }
                          Sources =
                            ConstructionJson.array "derivedFrom" root
                            |> List.map (fun d ->
                                { RequirementKey = s "requirementKey" d; Excerpt = s "excerpt" d
                                  ContentDigest = s "contentDigest" d; Coverage = s "coverage" d; Remainder = s "remainder" d })
                          Excluded = ConstructionJson.array "excludedCases" root |> List.map (fun e -> s "id" e, s "reason" e)
                          Cases = cases |> List.choose Result.toOption })

    /// The driver input: what to call, never what to expect. Unscored cases
    /// are driven too, so their answers can be reported.
    let driverInput (oracle: AdapterOracle) (assemblyPath: string) (outputPath: string) =
        let cases =
            oracle.Cases
            |> List.map (fun c ->
                match c.Body with
                | DecideSteps(state, steps) ->
                    {| id = c.Id; kind = "decide"; state = state; commands = steps |> List.map _.Command; segments = ([]: int64 list list) |}
                | RecordedMinutes(segments, _) ->
                    {| id = c.Id; kind = "recorded-minutes"; state = ""; commands = ([]: string list)
                       segments = segments |> List.map (fun (a, b) -> [ a; b ]) |})
        JsonSerializer.Serialize
            {| ``namespace`` = oracle.Binding.Namespace
               ``module`` = oracle.Binding.Module
               assembly = assemblyPath
               output = outputPath
               cases = cases |}

    let private optionalInt (name: string) (e: JsonElement) =
        match ConstructionJson.get name e with
        | Some v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt32() with
            | true, n -> Some n
            | _ -> None
        | _ -> None

    /// Reads the driver's JSON-lines output: one probe record and one
    /// record per case. Unparseable lines are ignored; a case reported more
    /// than once is reported as conflicting rather than picking one.
    let readDriverOutput (text: string) : AdapterProbe option * AdapterObservation list =
        let records =
            text.Split('\n')
            |> Array.map _.Trim()
            |> Array.filter (fun l -> l.StartsWith("{", StringComparison.Ordinal))
            |> Array.choose (ConstructionJson.parse "driver" >> Result.toOption)
            |> Array.toList
        let probe =
            records
            |> List.tryPick (ConstructionJson.get "probe")
            |> Option.map (fun p ->
                { Assembly = s "assembly" p
                  Module = s "module" p
                  Functions = ConstructionJson.members "functions" p |> List.map (fun (k, v) -> k, (if v.ValueKind = JsonValueKind.String then string (v.GetString()) else ""))
                  Detail = s "detail" p })
        let observations =
            records
            |> List.filter (fun r -> s "caseId" r <> "")
            |> List.map (fun o ->
                { CaseId = s "caseId" o
                  Status = s "status" o
                  Steps = ConstructionJson.array "steps" o |> List.map (fun st -> { Outcome = s "outcome" st; Next = s "next" st; Effects = strs "effects" st })
                  Minutes = optionalInt "minutes" o
                  Reason = s "reason" o })
            |> List.groupBy _.CaseId
            |> List.map (fun (id, xs) ->
                match xs with
                | [ single ] -> single
                | _ -> { CaseId = id; Status = "conflicting"; Steps = []; Minutes = None; Reason = $"the driver reported this case {xs.Length} times" })
        probe, observations

    let private stepVerdict (index: int) (expected: OracleExpect) (observed: ObservedStep) =
        let at = sprintf "step %d: " (index + 1)
        match expected.Outcome, observed.Outcome with
        | "refused", "refused" -> None
        | "accepted", "accepted" when expected.Next <> observed.Next ->
            Some(at + sprintf "expected next state %s, observed %s" expected.Next observed.Next)
        | "accepted", "accepted" when List.sort expected.Effects <> List.sort observed.Effects ->
            Some(at + sprintf "expected effects [%s], observed [%s]" (String.concat "," expected.Effects) (String.concat "," observed.Effects))
        | "accepted", "accepted" -> None
        | e, o -> Some(at + sprintf "expected %s, observed %s" e o)

    let describeMinutes (minutes: int option) =
        match minutes with
        | None -> "None (nothing recorded)"
        | Some n -> $"Some {n}"

    /// Judges one scored case against what the Dokimos-launched driver
    /// observed. Missing, mis-signed or throwing adapters fail; anything the
    /// driver could not establish is unknown. Nothing defaults to passed.
    let judge (case: AdapterCase) (observation: AdapterObservation option) =
        match observation with
        | None -> CaseUnknown "the driver produced no observation for this case"
        | Some o ->
            match o.Status, case.Body with
            | "adapter-missing", _ -> CaseFailed("the adapter is missing: " + o.Reason)
            | "signature-mismatch", _ -> CaseFailed("the adapter function has the wrong signature: " + o.Reason)
            | "threw", _ -> CaseFailed("the adapter threw: " + o.Reason)
            | "timeout", _ -> CaseUnknown("the adapter did not return: " + o.Reason)
            | "conflicting", _ -> CaseUnknown o.Reason
            | "observed", DecideSteps(_, steps) when o.Steps.Length <> steps.Length ->
                CaseUnknown(sprintf "expected %d observed steps, got %d" steps.Length o.Steps.Length)
            | "observed", DecideSteps(_, steps) ->
                List.zip steps o.Steps
                |> List.mapi (fun i (step, observed) -> stepVerdict i step.Expect observed)
                |> List.choose id
                |> function
                    | [] -> CasePassed
                    | reasons -> CaseFailed(String.concat "; " reasons)
            | "observed", RecordedMinutes(_, Some expect) ->
                if o.Minutes = Some expect.Minutes then CasePassed
                else CaseFailed(sprintf "expected Some %d (whole minutes recorded), observed %s" expect.Minutes (describeMinutes o.Minutes))
            | "observed", RecordedMinutes(_, None) -> CaseUnknown "the case has no expectation"
            | status, _ -> CaseUnknown("unrecognized observation status " + status)
