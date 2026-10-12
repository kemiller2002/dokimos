/// The frozen adapter oracle DOK-ORACLE-TIMER-ADAPTER-1 and the pure
/// judgment of driver observations. No candidate code runs here.
module Dokimos.Core.Tests.BehaviorOracleTests

open System
open System.IO
open Xunit
open Dokimos.Core
open Dokimos.Core.Tests.ConstructionFixtures

let private adapterOraclePath = Path.Combine(repositoryRoot, "oracles", "construction", "time-entry-timer", "oracle-adapter.json")
let private adapterOracle () = BehaviorOracle.read (File.ReadAllBytes adapterOraclePath) |> Result.defaultWith failwith
let private original () = ConstructionOracle.read (oracleBytes ()) |> Result.defaultWith failwith

let private seconds (segments: (int64 * int64) list) = segments |> List.sumBy (fun (a, b) -> b - a)

[<Fact>]
let ``the adapter oracle binds the frozen arm-neutral adapter contract`` () =
    let oracle = adapterOracle ()
    Assert.Equal("DOK-ORACLE-TIMER-ADAPTER-1", oracle.OracleId)
    Assert.Equal("adapter", oracle.Binding.Kind)
    Assert.Equal("dokimos.timer-oracle-adapter/1", oracle.Binding.Contract)
    Assert.Equal("TimerPilot", oracle.Binding.Namespace)
    Assert.Equal("TimerOracleAdapter", oracle.Binding.Module)
    Assert.Equal<string list>(
        [ "decide: state: string -> command: string -> Result<string * string list, string>"
          "recordedMinutes: segments: (int64 * int64) list -> int option" ],
        oracle.Binding.Functions |> List.map (fun f -> f.Name + ": " + f.Signature))
    Assert.Equal("independent-evaluator", oracle.ProducerKind)

[<Fact>]
let ``decide and sequence cases restate DOK-ORACLE-TIMER-1 with the same IDs, citations, keys and expectations`` () =
    let adapter = adapterOracle ()
    let restated = adapter.Cases |> List.filter (fun c -> match c.Body with DecideSteps _ -> true | RecordedMinutes _ -> false)
    let source = (original ()).Cases |> List.filter (fun c -> c.Kind <> "port-surface")
    Assert.Equal(14, restated.Length)
    for a, o in List.zip restated source do
        Assert.Equal(o.Id, a.Id)
        Assert.Equal(o.Kind, a.Kind)
        Assert.Equal(o.Citation, a.Citation)
        Assert.Equal<string list>(o.RequirementKeys, a.RequirementKeys)
        Assert.Equal(Scored, a.Scoring)
        match a.Body with
        | DecideSteps(state, steps) ->
            Assert.Equal(o.State, state)
            Assert.Equal<OracleStep list>(o.Steps, steps)
        | RecordedMinutes _ -> failwith "unreachable"
    Assert.Equal<string list>([ "TIMER-ORACLE-015" ], adapter.Excluded |> List.map fst)

[<Fact>]
let ``recorded-minutes cases test no exact half-minute tie and no stop the under-thirty rule could reach`` () =
    let oracle = adapterOracle ()
    let minutes = oracle.Cases |> List.choose (fun c -> match c.Body with RecordedMinutes(s, e) -> Some(c, s, e) | DecideSteps _ -> None)
    let scored = minutes |> List.filter (fun (c, _, _) -> c.Scoring = Scored)
    Assert.Equal(7, scored.Length)
    for c, segments, expect in scored do
        let total = seconds segments
        Assert.True(total % 60L <> 30L, c.Id + " is an exact half-minute tie; the source states no tie rule")
        Assert.True(total >= 30L, c.Id + " could be reached by the unresolved under-thirty-seconds rule")
        // Nearest whole minute of the summed working seconds, independently recomputed.
        Assert.Equal(Some { Minutes = int (Math.Round(float total / 60.0)) }, expect)
    // Paused time is excluded: at least one scored case has gaps that would change the answer.
    Assert.Contains(scored, fun (_, s, e) ->
        let wall = (s |> List.map snd |> List.max) - (s |> List.map fst |> List.min)
        Some { Minutes = int (Math.Round(float wall / 60.0)) } <> e)
    // Rounding to nearest, not truncation, is distinguished by at least one case.
    Assert.Contains(scored, fun (_, s, e) -> Some { Minutes = int (seconds s / 60L) } <> e)

[<Fact>]
let ``under-thirty-second cases are unscored unresolved conflicts with no expectation`` () =
    let oracle = adapterOracle ()
    let unscored = oracle.Cases |> List.filter (fun c -> c.Scoring <> Scored)
    Assert.Equal<string list>([ "TIMER-SHORT-001"; "TIMER-SHORT-002"; "TIMER-SHORT-003" ], unscored |> List.map _.Id)
    for c in unscored do
        Assert.Equal(Unscored("unresolved-conflict", "CONFLICT-TIMER-SHORT-STOP"), c.Scoring)
        match c.Body with
        | RecordedMinutes(segments, None) -> Assert.True(seconds segments < 30L)
        | _ -> failwith (c.Id + " must be an expectation-free recorded-minutes case")

[<Fact>]
let ``every case cites an excerpt whose digest the pinned source manifest also records`` () =
    let oracle = adapterOracle ()
    let excerpts = oracle.Sources |> List.map (fun s -> s.RequirementKey, s) |> Map.ofList
    for s in oracle.Sources do
        let bytes = File.ReadAllBytes(fixture "time-entry-timer" (Path.Combine("sources", s.Excerpt)))
        Assert.Equal(s.ContentDigest, ConstructionJson.sha256Bytes bytes)
    for c in oracle.Cases do
        Assert.NotEmpty c.RequirementKeys
        Assert.False(String.IsNullOrWhiteSpace c.Citation)
        for key in c.RequirementKeys do
            Assert.True(Map.containsKey key excerpts, c.Id + " cites an unknown requirement " + key)

let private observed caseId minutes = { CaseId = caseId; Status = "observed"; Steps = []; Minutes = minutes; Reason = "" }

let private minutesCase expect =
    { Id = "M"; Kind = "recorded-minutes"; Body = RecordedMinutes([ (0L, 460L) ], expect); Scoring = Scored; RequirementKeys = []; Citation = "" }

[<Fact>]
let ``judging recorded minutes never turns an absent or different answer into a pass`` () =
    let case = minutesCase (Some { Minutes = 8 })
    Assert.Equal(CasePassed, BehaviorOracle.judge case (Some(observed "M" (Some 8))))
    Assert.Equal(CaseFailed "expected Some 8 (whole minutes recorded), observed Some 7", BehaviorOracle.judge case (Some(observed "M" (Some 7))))
    Assert.Equal(CaseFailed "expected Some 8 (whole minutes recorded), observed None (nothing recorded)", BehaviorOracle.judge case (Some(observed "M" None)))
    Assert.Equal(CaseUnknown "the driver produced no observation for this case", BehaviorOracle.judge case None)
    let status s = BehaviorOracle.judge case (Some { observed "M" (Some 8) with Status = s; Reason = "r" })
    Assert.True(match status "adapter-missing" with CaseFailed _ -> true | _ -> false)
    Assert.True(match status "signature-mismatch" with CaseFailed _ -> true | _ -> false)
    Assert.True(match status "threw" with CaseFailed _ -> true | _ -> false)
    Assert.True(match status "timeout" with CaseUnknown _ -> true | _ -> false)
    Assert.True(match status "made-up" with CaseUnknown _ -> true | _ -> false)

[<Fact>]
let ``a case the driver reports twice is conflicting, not whichever line came last`` () =
    let lines =
        String.concat "\n"
            [ """{"probe":{"assembly":"loaded","module":"TimerPilot.TimerOracleAdapter","functions":{"decide":"ok","recordedMinutes":"ok"},"detail":""}}"""
              """{"caseId":"M","status":"observed","steps":[],"minutes":7,"reason":""}"""
              """{"caseId":"M","status":"observed","steps":[],"minutes":8,"reason":""}"""
              "not json" ]
    let probe, observations = BehaviorOracle.readDriverOutput lines
    Assert.True probe.IsSome
    let only = Assert.Single observations
    Assert.Equal("conflicting", only.Status)
    Assert.True(match BehaviorOracle.judge (minutesCase (Some { Minutes = 8 })) (Some only) with CaseUnknown _ -> true | _ -> false)

[<Fact>]
let ``the driver input carries no expectations`` () =
    let input = BehaviorOracle.driverInput (adapterOracle ()) "a.dll" "out.jsonl"
    Assert.DoesNotContain("expect", input)
    Assert.DoesNotContain("accepted", input)
    Assert.DoesNotContain("refused", input)
    Assert.DoesNotContain("\"minutes\"", input)
