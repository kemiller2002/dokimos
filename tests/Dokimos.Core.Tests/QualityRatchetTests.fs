namespace Dokimos.Core.Tests

open System
open Xunit
open Dokimos.Core

/// Baseline-derived ratchet, exceptions and baseline lifecycle (#16).
module QualityRatchetTests =
    let asOf = DateTimeOffset.Parse "2026-10-05T12:00:00Z"
    let configuration = { Sources = [ "src" ]; Generated = []; LargeFileLines = 100; ForbiddenReferences = [] }

    let scopes (pairs: (string * decimal) list) =
        pairs |> List.map (fun (s, v) -> s, { Value = v; Evidence = [ $"{s}: evidence" ] }) |> Map.ofList

    let measurement (rules: (string * (string * decimal) list) list) : QualityMeasurement =
        { Rules = rules |> List.map (fun (id, values) -> id, Measured(scopes values)) |> Map.ofList
          AnalyzedFiles = []
          GeneratedFiles = [] }

    let baseline (rules: (string * (string * decimal) list) list) : RatchetBaseline =
        { SchemaVersion = QualityRatchet.BaselineSchemaVersion
          Repository = "example"
          AcceptedAt = asOf.AddDays -1.0
          Configuration = configuration
          Rules = rules |> List.map (fun (id, values) -> id, Map.ofList values) |> Map.ofList }

    let exceptionFor ruleId scope allowed : QualityException =
        { Id = "EXC-1"
          RuleId = ruleId
          Scope = scope
          Rationale = "parser is cohesive"
          Owner = "kemiller2002"
          Created = DateOnly(2026, 10, 1)
          Review = Expires(DateOnly(2026, 12, 31))
          Evidence = [ "https://github.com/kemiller2002/dokimos/issues/16" ]
          AllowedValue = Some allowed }

    let exceptions valid = { Valid = valid; Invalid = [] }

    let markers = QualityRules.DebtMarkers
    let accepted = baseline [ markers, [ "src/A.fs", 2m ] ]

    let run b e m = QualityRatchet.check asOf b e m

    [<Fact>]
    let ``unchanged measurement passes`` () =
        let result = run accepted (exceptions []) (measurement [ markers, [ "src/A.fs", 2m ] ])
        Assert.Equal(RatchetPass, result.Verdict)
        Assert.Empty(result.Findings)
        Assert.Equal(0, QualityRatchet.exitCode result.Verdict)

    [<Fact>]
    let ``growth in an existing scope is a regression with before, after and evidence`` () =
        let result = run accepted (exceptions []) (measurement [ markers, [ "src/A.fs", 3m ] ])
        Assert.Equal(RatchetRegression, result.Verdict)
        Assert.Equal(4, QualityRatchet.exitCode result.Verdict)
        let f = Assert.Single(result.Findings)
        Assert.Equal((markers, "src/A.fs", Regression, 2m, 3m), (f.RuleId, f.Scope, f.Kind, f.Before, f.After))
        Assert.NotEmpty(f.Evidence)

    [<Fact>]
    let ``debt in a new scope is a regression from zero`` () =
        let result = run accepted (exceptions []) (measurement [ markers, [ "src/A.fs", 2m; "src/B.fs", 1m ] ])
        let f = Assert.Single(result.Findings)
        Assert.Equal(("src/B.fs", 0m, false), (f.Scope, f.Before, f.BaselineRecorded))

    [<Fact>]
    let ``improvement is reported and passes`` () =
        let result = run accepted (exceptions []) (measurement [ markers, [ "src/A.fs", 1m ] ])
        Assert.Equal(RatchetPass, result.Verdict)
        Assert.Equal(Improvement, (Assert.Single result.Findings).Kind)

    [<Fact>]
    let ``large file growth and threshold crossing are regressions, small files are ignored`` () =
        let b = baseline [ QualityRules.LargeFileGrowth, [ "src/Big.fs", 150m ] ]
        let m = measurement [ QualityRules.LargeFileGrowth, [ "src/Big.fs", 151m; "src/New.fs", 120m; "src/Small.fs", 90m ] ]
        let result = run b (exceptions []) m
        Assert.Equal<string list>([ "src/Big.fs"; "src/New.fs" ], result.Findings |> List.map _.Scope)
        Assert.True(result.Findings |> List.forall (fun f -> f.Kind = Regression))
        Assert.Equal(100m, (result.Findings |> List.find (fun f -> f.Scope = "src/New.fs")).Before)

    [<Fact>]
    let ``an exception accepts only its rule and scope up to its allowed value`` () =
        let m = measurement [ markers, [ "src/A.fs", 3m; "src/B.fs", 1m ] ]
        let result = run accepted (exceptions [ exceptionFor markers "src/A.fs" 3m ]) m
        Assert.Equal(RatchetRegression, result.Verdict)
        let a = result.Findings |> List.find (fun f -> f.Scope = "src/A.fs")
        Assert.Equal((ExceptedRegression, Some "EXC-1"), (a.Kind, a.ExceptionId))
        Assert.Equal(Regression, (result.Findings |> List.find (fun f -> f.Scope = "src/B.fs")).Kind)

    [<Fact>]
    let ``a regression beyond the exception allowance still fails`` () =
        let result = run accepted (exceptions [ exceptionFor markers "src/A.fs" 3m ]) (measurement [ markers, [ "src/A.fs", 4m ] ])
        Assert.Equal(RatchetRegression, result.Verdict)

    [<Fact>]
    let ``a fully excepted change passes and the baseline is unchanged`` () =
        let result = run accepted (exceptions [ exceptionFor markers "src/A.fs" 3m ]) (measurement [ markers, [ "src/A.fs", 3m ] ])
        Assert.Equal(RatchetPass, result.Verdict)
        Assert.Equal<Map<string, decimal>>(Map.ofList [ "src/A.fs", 2m ], result.Baseline.Rules[markers])

    [<Fact>]
    let ``an expired exception fails verification`` () =
        let expired = { exceptionFor markers "src/A.fs" 3m with Review = Expires(DateOnly(2026, 10, 4)) }
        let result = run accepted (exceptions [ expired ]) (measurement [ markers, [ "src/A.fs", 2m ] ])
        Assert.Equal(RatchetInvalidExceptions, result.Verdict)
        Assert.Equal(6, QualityRatchet.exitCode result.Verdict)
        Assert.Single(result.ExpiredExceptions) |> ignore

    [<Fact>]
    let ``an exception is valid through its expiry date`` () =
        let lastDay = { exceptionFor markers "src/A.fs" 3m with Review = Expires(DateOnly(2026, 10, 5)) }
        Assert.Equal(RatchetPass, (run accepted (exceptions [ lastDay ]) (measurement [ markers, [ "src/A.fs", 3m ] ])).Verdict)

    [<Fact>]
    let ``exceptions missing owner, rationale, evidence or allowance are invalid`` () =
        let bad = { exceptionFor markers "src/A.fs" 3m with Owner = ""; Rationale = " "; Evidence = []; AllowedValue = None }
        let result = run accepted (exceptions [ bad ]) (measurement [ markers, [ "src/A.fs", 2m ] ])
        Assert.Equal(RatchetInvalidExceptions, result.Verdict)
        let invalid = Assert.Single(result.InvalidExceptions)
        Assert.Equal(4, invalid.Problems.Length)

    [<Fact>]
    let ``exceptions naming unknown rules or misscoped repository rules are invalid`` () =
        let unknown = { exceptionFor "DOK-R999" "src/A.fs" 1m with Id = "EXC-2" }
        let misscoped = { exceptionFor QualityRules.CompilerWarnings "src/A.fs" 1m with Id = "EXC-3" }
        let result = run accepted (exceptions [ unknown; misscoped ]) (measurement [ markers, [ "src/A.fs", 2m ] ])
        Assert.Equal(2, result.InvalidExceptions.Length)

    [<Fact>]
    let ``review-condition exceptions do not expire`` () =
        let reviewed = { exceptionFor markers "src/A.fs" 3m with Review = ReviewCondition "when the parser is replaced" }
        Assert.Equal(RatchetPass, (run accepted (exceptions [ reviewed ]) (measurement [ markers, [ "src/A.fs", 3m ] ])).Verdict)

    [<Fact>]
    let ``an enforced rule that could not be measured is unavailable, never a pass`` () =
        let b = baseline [ QualityRules.CompilerWarnings, [ "repository", 0m ] ]
        let m = { measurement [] with Rules = Map.ofList [ QualityRules.CompilerWarnings, NotMeasured "no build log" ] }
        let result = run b (exceptions []) m
        Assert.Equal(RatchetUnavailable, result.Verdict)
        Assert.Equal(3, QualityRatchet.exitCode result.Verdict)

    [<Fact>]
    let ``unavailable outranks invalid exceptions and regressions`` () =
        let b = baseline [ markers, []; QualityRules.CompilerWarnings, [ "repository", 0m ] ]
        let m = { measurement [ markers, [ "src/A.fs", 5m ] ] with Rules = Map.ofList [ markers, Measured(scopes [ "src/A.fs", 5m ]); QualityRules.CompilerWarnings, NotMeasured "x" ] }
        Assert.Equal(RatchetUnavailable, (run b (exceptions []) m).Verdict)

    [<Fact>]
    let ``rules absent from the baseline are reported as not enforced`` () =
        let result = run accepted (exceptions []) (measurement [ markers, [ "src/A.fs", 2m ]; QualityRules.SkippedTests, [ "src/T.fs", 9m ] ])
        Assert.Equal(RatchetPass, result.Verdict)
        Assert.Contains((QualityRules.SkippedTests, RuleNotEnforced), result.RuleStates)

    [<Fact>]
    let ``baseline update only tightens and refuses new or worse debt`` () =
        let b = baseline [ markers, [ "src/A.fs", 3m; "src/B.fs", 2m; "src/C.fs", 1m ] ]
        let m = measurement [ markers, [ "src/A.fs", 1m; "src/B.fs", 5m; "src/D.fs", 1m ] ]
        match QualityRatchet.update asOf b m with
        | Error e -> failwith (String.Join("; ", e))
        | Ok result ->
            Assert.Equal<Map<string, decimal>>(Map.ofList [ "src/A.fs", 1m; "src/B.fs", 2m ], result.Updated.Rules[markers])
            Assert.Equal<string list>([ "src/B.fs"; "src/D.fs" ], result.Refused |> List.map _.Scope)
            Assert.Equal<(string * BaselineChangeKind) list>([ "src/A.fs", ScopeTightened; "src/C.fs", ScopeRemoved ], result.Changes |> List.map (fun c -> c.Scope, c.Kind))
            Assert.False(QualityRatchet.isLoosening (QualityRatchet.diff b result.Updated))

    [<Fact>]
    let ``baseline update refuses when an enforced rule is unavailable`` () =
        let b = baseline [ QualityRules.CompilerWarnings, [ "repository", 0m ] ]
        let m = { measurement [] with Rules = Map.ofList [ QualityRules.CompilerWarnings, NotMeasured "no build log" ] }
        Assert.True(Result.isError (QualityRatchet.update asOf b m))

    [<Fact>]
    let ``baseline init records inherited debt for selected rules only`` () =
        let m = measurement [ markers, [ "src/A.fs", 2m ]; QualityRules.LargeFileGrowth, [ "src/Big.fs", 150m; "src/Small.fs", 10m ] ]
        match QualityRatchet.initialize asOf "example" configuration [ markers; QualityRules.LargeFileGrowth ] m with
        | Error e -> failwith (String.Join("; ", e))
        | Ok b ->
            Assert.Equal<Map<string, decimal>>(Map.ofList [ "src/Big.fs", 150m ], b.Rules[QualityRules.LargeFileGrowth])
            Assert.Equal(2, b.Rules.Count)

    [<Fact>]
    let ``diff flags raised values, added scopes, dropped rules and weakened configuration`` () =
        let older = baseline [ markers, [ "src/A.fs", 2m ]; QualityRules.SkippedTests, [] ]
        let newer =
            { baseline [ markers, [ "src/A.fs", 3m; "src/B.fs", 1m ] ] with
                Configuration = { configuration with Generated = [ "src/**" ]; LargeFileLines = 200 } }
        let d = QualityRatchet.diff older newer
        Assert.True(QualityRatchet.isLoosening d)
        Assert.Equal(3, d.Loosened.Length)
        Assert.Equal(2, d.ConfigurationLoosened.Length)

    [<Fact>]
    let ``the rule catalog has stable unique ids`` () =
        let ids = QualityRules.all |> List.map _.Id
        Assert.Equal<string list>(
            [ "DOK-R001"; "DOK-R002"; "DOK-R003"; "DOK-R004"; "DOK-R005"; "DOK-R006"; "DOK-R007"; "DOK-R008"; "DOK-G001" ],
            ids
        )
        Assert.True(QualityRules.all |> List.forall (fun r -> r.Description <> "" && r.Remediation <> "" && r.Rationale <> ""))
