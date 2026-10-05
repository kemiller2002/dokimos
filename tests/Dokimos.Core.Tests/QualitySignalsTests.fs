namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

/// The independent F# signals behind DOK-R002..R008 (#16).
module QualitySignalsTests =
    let lexed (source: string) = SourceLexing.lex source

    let count select source = select (lexed source) |> List.length

    let configuration =
        { Sources = [ "src" ]
          Generated = [ "**/obj/**"; "src/Generated/**" ]
          LargeFileLines = 5
          ForbiddenReferences = [ { From = "App.Domain"; To = "App.Cli" } ] }

    let file path content : SourceFile = { Path = path; Content = content }

    let measured ruleId (m: QualityMeasurement) =
        match m.Rules |> Map.find ruleId with
        | Measured values -> values |> Map.map (fun _ v -> v.Value)
        | NotMeasured reason -> failwith reason

    [<Fact>]
    let ``markers count in comments but not in string literals`` () =
        let source =
            "// TODO: real\nlet s = \"TODO in a string\"\nlet t = \"\"\"FIXME in a triple-quoted\nstring HACK\"\"\"\n(* outer (* HACK nested *) still comment FIXME *)\nlet c = '\"' // HACK after a char literal\n"
        Assert.Equal(3, count QualitySignals.debtMarkerLines source)

    [<Fact>]
    let ``markers must be whole words`` () =
        Assert.Equal(0, count QualitySignals.debtMarkerLines "// TodoIndicators and HACKER are not markers\n")

    [<Fact>]
    let ``source suppressions are counted outside strings`` () =
        let source = "#nowarn \"57\"\nlet detector = \"#nowarn\"\n[<SuppressMessage(\"x\", \"y\")>]\nlet x = 1\n"
        Assert.Equal(2, count QualitySignals.suppressionLines source)

    [<Fact>]
    let ``project-file suppressions are counted, inherited NoWarn is not`` () =
        let project =
            "<Project>\n<NoWarn>$(NoWarn)</NoWarn>\n<NoWarn>$(NoWarn);FS0057</NoWarn>\n<WarningsNotAsErrors>FS0040</WarningsNotAsErrors>\n<TreatWarningsAsErrors>false</TreatWarningsAsErrors>\n<PackageReference Include=\"A\" NoWarn=\"NU1605\" />\n</Project>\n"
        Assert.Equal(4, QualitySignals.projectSuppressionLines project |> List.length)

    [<Fact>]
    let ``skipped and ignored tests are counted`` () =
        let source =
            "[<Fact(Skip = \"flaky\")>]\nlet a () = ()\n[<Ignore>]\nlet b () = ()\nptestCase \"c\" <| fun () -> ()\nlet note = \"Skip = \\\"not code\\\"\"\n[<Fact>]\nlet d () = ()\n"
        Assert.Equal(3, count QualitySignals.skippedTestLines source)

    [<Theory>]
    [<InlineData("let f () = try g () with _ -> None", 1)>]
    [<InlineData("let f () = try g () with e -> ()", 1)>]
    [<InlineData("let f () = try g () with ex -> Error ex.Message", 0)>]
    [<InlineData("let f () = try g () with :? System.Exception -> None", 1)>]
    [<InlineData("let f () = try g () with :? exn as e -> reraise ()", 1)>]
    [<InlineData("let f () = try g () with :? System.IO.IOException as e -> Error e.Message", 0)>]
    [<InlineData("let f x = match x with _ -> 0", 0)>]
    [<InlineData("let r = { x with Y = 1 }", 0)>]
    let ``broad discarding catches on one line`` (source: string, expected: int) =
        Assert.Equal(expected, count QualitySignals.broadCatchLines source)

    [<Fact>]
    let ``broad catches in multi-line handlers`` () =
        let source =
            "let f () =\n    try\n        g ()\n    with\n    | :? System.IO.IOException as e -> Error e.Message\n    | _ ->\n        None\nlet h () =\n    try g () with\n    | ex ->\n        log ex\n"
        Assert.Equal(1, count QualitySignals.broadCatchLines source)

    [<Fact>]
    let ``a handler whose body uses the exception on the next line is not discarding`` () =
        Assert.Equal(0, count QualitySignals.broadCatchLines "let f () =\n    try g () with ex ->\n        Error ex.Message\n")

    [<Fact>]
    let ``match cases after a standalone with are not try handlers when the with ends a match`` () =
        Assert.Equal(0, count QualitySignals.broadCatchLines "let f x =\n    match x with\n    | _ -> 0\n")

    [<Fact>]
    let ``measurement covers every rule and excludes declared generated files from all of them`` () =
        let files =
            [ file "src/App.Domain/App.Domain.fsproj" "<Project><ItemGroup><ProjectReference Include=\"../App.Cli/App.Cli.fsproj\" /><PackageReference Include=\"A\" Version=\"1\" /></ItemGroup></Project>"
              file "src/App.Domain/Big.fs" "1\n2\n3\n4\n5\n6\n7\n"
              file "src/App.Domain/Small.fs" "// TODO later\nlet x = 1\n"
              file "src/Generated/Parser.fs" "// TODO generated\n1\n2\n3\n4\n5\n6\n7\n8\n"
              file "src/App.Domain/obj/AssemblyInfo.fs" "#nowarn \"1\"\n" ]
        let m = QualitySignals.measure configuration { Files = files; BuildLog = Some "    2 Warning(s)\n    0 Error(s)\n" }
        Assert.Equal<string list>([ "src/App.Domain/obj/AssemblyInfo.fs"; "src/Generated/Parser.fs" ], m.GeneratedFiles)
        Assert.Equal(2m, (measured QualityRules.CompilerWarnings m)["repository"])
        Assert.Equal<Map<string, decimal>>(Map.ofList [ "src/App.Domain/Small.fs", 1m ], measured QualityRules.DebtMarkers m)
        Assert.True((measured QualityRules.Suppressions m).IsEmpty)
        Assert.Equal(7m, (measured QualityRules.LargeFileGrowth m)["src/App.Domain/Big.fs"])
        Assert.False((measured QualityRules.LargeFileGrowth m).ContainsKey "src/Generated/Parser.fs")
        Assert.Equal(1m, (measured QualityRules.PackageReferences m)["src/App.Domain/App.Domain.fsproj"])
        Assert.Equal(1m, (measured QualityRules.ArchitectureViolations m)["repository"])

    [<Fact>]
    let ``compiler warnings without a build log are not measured, never zero`` () =
        let m = QualitySignals.measure configuration { Files = []; BuildLog = None }
        match m.Rules |> Map.find QualityRules.CompilerWarnings with
        | NotMeasured _ -> ()
        | Measured _ -> failwith "warnings must not be measured without a build log"

    [<Theory>]
    [<InlineData("**/obj/**", "src/A/obj/Debug/x.fs", true)>]
    [<InlineData("**/obj/**", "obj/x.fs", true)>]
    [<InlineData("src/Generated/*.fs", "src/Generated/A.fs", true)>]
    [<InlineData("src/Generated/*.fs", "src/Generated/Sub/A.fs", false)>]
    [<InlineData("**/*.g.fs", "src/A/B.g.fs", true)>]
    [<InlineData("**/*.g.fs", "src/A/Bg.fs", false)>]
    let ``generated globs are explicit and segment-aware`` (glob: string, path: string, expected: bool) =
        Assert.Equal(expected, Glob.isMatch glob path)
