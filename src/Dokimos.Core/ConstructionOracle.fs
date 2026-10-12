namespace Dokimos.Core

open System
open System.Text.Json

type OracleExpect =
    { Outcome: string
      Next: string
      Effects: string list
      Members: string list }

type OracleStep = { Command: string; Expect: OracleExpect }

/// One frozen expectation, keyed by a stable oracle ID and the original
/// requirement keys it was derived from.
type OracleCase =
    { Id: string
      Kind: string
      State: string
      Steps: OracleStep list
      Expect: OracleExpect
      RequirementKeys: string list
      Citation: string }

type OracleSource =
    { RequirementKey: string
      Excerpt: string
      ContentDigest: string
      /// full | partial | none: how much of the requirement the oracle can observe.
      Coverage: string
      Remainder: string }

type OracleBinding =
    { Namespace: string
      StateType: string
      CommandType: string
      EffectType: string
      TransitionModule: string
      PortsInterface: string
      States: Map<string, string>
      Commands: Map<string, string>
      Effects: Map<string, string> }

/// The independent behavioral acceptance oracle (`dokimos.construction-oracle/1`).
type ConstructionOracle =
    { OracleId: string
      Digest: string
      FrozenAt: DateTimeOffset option
      ProducerKind: string
      ProducerSystem: string
      Workflow: string
      Binding: OracleBinding
      Sources: OracleSource list
      Cases: OracleCase list }

/// What the Dokimos-run harness observed for one step of a case, in the
/// candidate's own names.
type ObservedStep =
    { Outcome: string
      Next: string
      Effects: string list }

type CaseObservation =
    { CaseId: string
      /// observed | missing (a bound name does not exist) | unsupported | threw
      Status: string
      Steps: ObservedStep list
      Members: string list
      Reason: string }

/// The record of one oracle run that Dokimos itself started and watched.
type OracleRunRecord =
    { Runner: string
      RunnerVersion: string
      CommandLine: string
      StartedAt: DateTimeOffset
      FinishedAt: DateTimeOffset
      ExitCode: int
      StdoutDigest: string
      HarnessDigest: string
      CandidateCommit: string
      OracleDigest: string }

type OracleRun =
    | NotExecuted of reason: string
    | CompileFailed of run: OracleRunRecord * diagnostics: string
    | Executed of run: OracleRunRecord * observations: CaseObservation list

type CaseVerdict =
    | CasePassed
    | CaseFailed of reason: string
    | CaseUnknown of reason: string

[<RequireQualifiedAccess>]
module ConstructionOracle =
    [<Literal>]
    let Schema = "dokimos.construction-oracle/1"

    let private s = ConstructionJson.text
    let private strs = ConstructionJson.strings

    let private expectOf (element: JsonElement) =
        let e = ConstructionJson.get "expect" element |> Option.defaultValue element
        { Outcome = s "outcome" e; Next = s "next" e; Effects = strs "effects" e; Members = strs "members" e |> List.sort }

    let private map name element =
        ConstructionJson.members name element
        |> List.map (fun (k, v) -> k, (if v.ValueKind = JsonValueKind.String then v.GetString() |> Option.ofObj |> Option.defaultValue "" else ""))
        |> Map.ofList

    let read (bytes: byte[]) : Result<ConstructionOracle, string> =
        let digest = ConstructionJson.sha256Bytes bytes
        ConstructionJson.parse "oracle" (Text.Encoding.UTF8.GetString bytes)
        |> Result.bind (fun root ->
            if s "schema" root <> Schema then Error("oracle: unsupported schema " + s "schema" root)
            else
                let producer = ConstructionJson.get "producer" root |> Option.defaultValue root
                let subject = ConstructionJson.get "subject" root |> Option.defaultValue root
                let b = ConstructionJson.get "binding" root |> Option.defaultValue root
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
                        { Namespace = s "namespace" b; StateType = s "stateType" b; CommandType = s "commandType" b
                          EffectType = s "effectType" b; TransitionModule = s "transitionModule" b
                          PortsInterface = s "portsInterface" b
                          States = map "states" b; Commands = map "commands" b; Effects = map "effects" b }
                      Sources =
                        ConstructionJson.array "derivedFrom" root
                        |> List.map (fun d ->
                            { RequirementKey = s "requirementKey" d; Excerpt = s "excerpt" d
                              ContentDigest = s "contentDigest" d; Coverage = s "coverage" d; Remainder = s "remainder" d })
                      Cases =
                        ConstructionJson.array "cases" root
                        |> List.map (fun c ->
                            let steps =
                                match s "kind" c with
                                | "decide" -> [ { Command = s "command" c; Expect = expectOf c } ]
                                | _ -> ConstructionJson.array "steps" c |> List.map (fun st -> { Command = s "command" st; Expect = expectOf st })
                            { Id = s "id" c; Kind = s "kind" c; State = s "state" c; Steps = steps; Expect = expectOf c
                              RequirementKeys = strs "requirementKeys" c; Citation = s "citation" c }) })

    let private bound (table: Map<string, string>) name = Map.tryFind name table |> Option.defaultValue name

    /// The harness input: every case translated into the candidate's names
    /// through the oracle's own frozen binding. Expectations are not sent.
    let harnessInput (oracle: ConstructionOracle) =
        let b = oracle.Binding
        let cases =
            oracle.Cases
            |> List.map (fun c ->
                {| id = c.Id
                   kind = c.Kind
                   state = bound b.States c.State
                   commands = c.Steps |> List.map (fun st -> bound b.Commands st.Command) |})
        JsonSerializer.Serialize
            {| ``namespace`` = b.Namespace
               stateType = b.StateType
               commandType = b.CommandType
               transitionModule = b.TransitionModule
               portsInterface = b.PortsInterface
               cases = cases |}

    /// Reads the harness's JSON-lines output. Lines that are not harness
    /// records are ignored; an unparseable record is reported, not dropped.
    let readObservations (stdout: string) : CaseObservation list =
        stdout.Split('\n')
        |> Array.map _.Trim()
        |> Array.filter (fun line -> line.StartsWith("{\"caseId\"", StringComparison.Ordinal))
        |> Array.toList
        |> List.choose (fun line ->
            match ConstructionJson.parse "observation" line with
            | Error _ -> None
            | Ok o ->
                Some
                    { CaseId = s "caseId" o
                      Status = s "status" o
                      Steps = ConstructionJson.array "steps" o |> List.map (fun st -> { Outcome = s "outcome" st; Next = s "next" st; Effects = strs "effects" st })
                      Members = strs "members" o |> List.sort
                      Reason = s "reason" o })

    let private stepVerdict (b: OracleBinding) (index: int) (expected: OracleExpect) (observed: ObservedStep) =
        let expectedEffects = expected.Effects |> List.map (bound b.Effects) |> List.sort
        let at = sprintf "step %d: " (index + 1)
        match expected.Outcome, observed.Outcome with
        | "refused", "refused" -> None
        | "accepted", "accepted" when bound b.States expected.Next <> observed.Next ->
            Some(at + sprintf "expected next state %s, observed %s" (bound b.States expected.Next) observed.Next)
        | "accepted", "accepted" when expectedEffects <> List.sort observed.Effects ->
            Some(at + sprintf "expected effects [%s], observed [%s]" (String.concat "," expectedEffects) (String.concat "," observed.Effects))
        | "accepted", "accepted" -> None
        | e, o -> Some(at + sprintf "expected %s, observed %s" e o)

    /// Judges one case against what the Dokimos-run harness observed.
    let judge (oracle: ConstructionOracle) (case: OracleCase) (observation: CaseObservation option) =
        match observation with
        | None -> CaseUnknown "the harness produced no observation for this case"
        | Some o when o.Status = "unsupported" -> CaseUnknown("the harness could not drive the candidate: " + o.Reason)
        | Some o when o.Status = "missing" -> CaseFailed("the candidate lacks a bound element: " + o.Reason)
        | Some o when o.Status = "threw" -> CaseFailed("the candidate threw: " + o.Reason)
        | Some o when o.Status <> "observed" -> CaseUnknown("unrecognized observation status " + o.Status)
        | Some o when case.Kind = "port-surface" ->
            if o.Members = case.Expect.Members then CasePassed
            else CaseFailed(sprintf "expected port members [%s], observed [%s]" (String.concat "," case.Expect.Members) (String.concat "," o.Members))
        | Some o when o.Steps.Length <> case.Steps.Length ->
            CaseFailed(sprintf "expected %d observed steps, got %d" case.Steps.Length o.Steps.Length)
        | Some o ->
            List.zip case.Steps o.Steps
            |> List.mapi (fun i (step, observed) -> stepVerdict oracle.Binding i step.Expect observed)
            |> List.choose id
            |> function
                | [] -> CasePassed
                | reasons -> CaseFailed(String.concat "; " reasons)
