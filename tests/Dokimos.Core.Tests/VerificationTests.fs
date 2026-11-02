namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core.Verification

/// The verification compiler (DOK-VC-001): plan intake, structural scanning, exact coverage,
/// scaffolds and fault probes. Every case is a defect class Rehearsal 8 hit or could have hit.
module VerificationTests =
    let sha = "5e2717665b696a0c70175c7cacfc5702c5b2058d"

    let obligation id req criterion : Obligation =
        { Id = id; Requirement = req; Priority = "P0"; Criterion = criterion; Categories = []; Dependencies = [] }

    let dotnetSuite =
        { Id = "SU-DET"; Runner = DotnetTest; Command = "dotnet test tests/App.Acceptance --filter FullyQualifiedName~App.Acceptance.Deterministic"
          Paths = [ "tests/App.Acceptance" ]; TestRoot = None; Environment = "ci"; Fixture = "fake"; DependsOn = []; RequiredBoundary = [] }

    let browserSuite =
        { Id = "SU-WEB"; Runner = PlaywrightTest; Command = "npx playwright test --project=chromium tests/browser/acceptance"
          Paths = [ "tests/browser/acceptance" ]; TestRoot = Some "tests/browser"; Environment = "ci"; Fixture = "page"; DependsOn = []
          RequiredBoundary = [ "page" ] }

    let bound (id: string) suite obligations =
        { Id = id; Identity = (match TestIdentity.parse id with Ok i -> i | Error e -> failwith e); Suite = suite; Kind = "deterministic"
          Obligations = obligations; Categories = [] }

    let set tests obligations =
        { PlanId = "VP-TEST"; PlanDigest = ""; Obligations = obligations; Tests = tests; Suites = [ dotnetSuite; browserSuite ]
          NamespaceRoots = [ "App.Acceptance", "tests/App.Acceptance" ] }

    let fsFile =
        "tests/App.Acceptance/Deterministic/SaveTests.fs"

    let fsSource body =
        $"""namespace App.Acceptance.Deterministic

open Xunit

module SaveTests =
    let private save x = x + 1

    [<Fact>]
    let ``SAVE-001-O1`` () =
{body}
"""

    let scanOf (tests: ScannedTest list) = { Tests = tests; Compiled = fun _ -> true }

    let noResults = Coverage.admit sha []

    let statusOf (report: CoverageReport) id =
        report.Obligations |> List.find (fun o -> o.Obligation = id) |> _.Status

    // ------------------------------------------------------------ identities and intake

    [<Fact>]
    let ``identities parse exactly and render back`` () =
        for id in [ "dotnet:Causa.Acceptance.Deterministic.AR014Tests.AR-014-O1"
                    "playwright:acceptance/AR-029.spec.js > AR-029-O1 [chromium]"
                    "playwright:acceptance/X.spec.js > group > case [webkit]"
                    "praxis:independent-review" ] do
            match TestIdentity.parse id with
            | Ok identity -> Assert.Equal(id, TestIdentity.render identity)
            | Error e -> failwith e
        Assert.True(Result.isError (TestIdentity.parse "playwright:acceptance/AR-029.spec.js > AR-029-O1"))
        Assert.True(Result.isError (TestIdentity.parse "dotnet:NoMethod"))

    [<Fact>]
    let ``ambiguous or incomplete obligations are refused, not repaired`` () =
        let s =
            set
                [ bound "dotnet:App.Acceptance.Deterministic.SaveTests.SAVE-001-O1" "SU-DET" [ "SAVE-001-O1"; "GHOST-O1" ] ]
                [ obligation "SAVE-001-O1" "SAVE-001" ""; obligation "SAVE-002-O1" "SAVE-002" "bound to nothing" ]
        let reasons = ObligationSet.refusals s |> List.map (fun r -> r.Subject, r.Reason)
        Assert.Contains(("SAVE-001-O1", "no criterion text: expected behaviour would have to be invented"), reasons)
        Assert.Contains(("SAVE-002-O1", "no bound test identity"), reasons)
        Assert.Contains(reasons, fun (_, r) -> r = "bound to unknown obligation GHOST-O1")

    [<Fact>]
    let ``an obligation set round-trips through its wire contract`` () =
        let s = set [ bound "dotnet:App.Acceptance.Deterministic.SaveTests.SAVE-001-O1" "SU-DET" [ "SAVE-001-O1" ] ] [ obligation "SAVE-001-O1" "SAVE-001" "saves" ]
        match ObligationSet.parse (ObligationSet.toJson s) with
        | Ok back ->
            Assert.Equal<Obligation list>(s.Obligations, back.Obligations)
            Assert.Equal<BoundTest list>(s.Tests, back.Tests)
            Assert.Equal<Suite list>(s.Suites, back.Suites)
        | Error e -> failwith e

    // ------------------------------------------------------------ F# scanning

    let scanFs body = TestScan.scanFSharp fsFile (fsSource body)

    [<Fact>]
    let ``F#: a real assertion is found with its exact container`` () =
        let t = scanFs "        Assert.Equal(2, save 1)" |> List.exactlyOne
        Assert.Equal("App.Acceptance.Deterministic.SaveTests", t.Container)
        Assert.Equal<string list>([ "SAVE-001-O1" ], t.TitlePath)
        Assert.True(TestScan.asserts t)
        Assert.False t.Placeholder

    [<Fact>]
    let ``F#: placeholder, no-assertion, trivial and skipped tests are classified`` () =
        Assert.True((scanFs "        failwith \"DOKIMOS-UNIMPLEMENTED x\"" |> List.exactlyOne).Placeholder)
        Assert.False(scanFs "        let fixture = save 1\n        ()" |> List.exactlyOne |> TestScan.asserts)
        let trivial = scanFs "        Assert.True(true)" |> List.exactlyOne
        Assert.True(trivial.Assertions <= trivial.TrivialAssertions)
        let skipped = (fsSource "        Assert.Equal(1, 1)").Replace("[<Fact>]", "[<Fact(Skip = \"later\")>]")
        Assert.True((TestScan.scanFSharp fsFile skipped |> List.exactlyOne).Skipped)

    [<Fact>]
    let ``F#: assertion strings and comments are not assertions`` () =
        let t = scanFs "        // Assert.Equal(1, save 0)\n        let note = \"Assert.True(save 1 > 0)\"\n        ()" |> List.exactlyOne
        Assert.False(TestScan.asserts t)

    [<Fact>]
    let ``F#: an assertion made through a file-local helper or a verb-named helper counts`` () =
        let source =
            "namespace N\n\nmodule M =\n    let private mustSave x =\n        Assert.Equal(2, x)\n\n    [<Fact>]\n    let ``A-O1`` () =\n        mustSave 2\n\n    [<Fact>]\n    let ``A-O2`` () =\n        Pat.assertNone \"pairs\" []\n"
        let tests = TestScan.scanFSharp "f.fs" source
        Assert.All(tests, fun t -> Assert.True(TestScan.asserts t, String.concat "." t.TitlePath))

    [<Fact>]
    let ``F#: class members, qualified attributes, theories, private modules and second namespaces`` () =
        let source =
            String.concat "\n"
                [ "namespace One"
                  "module private Helpers ="
                  "    let x = 1"
                  "type SaveTests() ="
                  "    [<Xunit.Fact>]"
                  "    member _.``S-O1``() = Assert.True(Helpers.x = 1)"
                  "namespace Two"
                  "module Inv ="
                  "    [<Theory>]"
                  "    [<InlineData(\"a\")>]"
                  "    [<InlineData(\"b\")>]"
                  "    let EachCase (case: string) = Assert.NotEmpty case" ]
        let found = TestScan.scanFSharp "f.fs" source |> List.map (fun t -> t.Container + "." + List.last t.TitlePath)
        Assert.Equal<string list>([ "One.SaveTests.S-O1"; "Two.Inv.EachCase" ], found)

    [<Fact>]
    let ``F#: compile membership and dotnet test filters decide execution`` () =
        let includes = TestScan.compiledFiles "<Project><ItemGroup><Compile Include=\"Deterministic/SaveTests.fs\" /></ItemGroup></Project>"
        Assert.True(TestScan.isCompiled includes "Deterministic/SaveTests.fs")
        Assert.False(TestScan.isCompiled includes "Deterministic/Other.fs")
        let filter = Some "FullyQualifiedName~Causa.Invariants.&FullyQualifiedName!~Live"
        Assert.True(TestScan.filterAdmits filter "Causa.Invariants.Property.INV04.RequestBounds")
        Assert.False(TestScan.filterAdmits filter "Causa.Invariants.Live.INV22.X")

    // ------------------------------------------------------------ Playwright scanning

    let spec =
        String.concat "\n"
            [ "import { test, expect } from \"../support/fixtures.js\";"
              "const re = /}\\{ not a brace/;"
              "const label = `a ${ { x: 1 }.x } }`;"
              "async function openApp(page) { await page.goto(\"/\"); }"
              "test(\"AR-029-O1\", async ({ page, failures }) => {"
              "  await openApp(page);"
              "  await expect(page.locator(\"[data-screen]\")).toHaveText(\"home\");"
              "});"
              "test.describe(\"group\", () => {"
              "  test(\"nested\", async ({ page }) => { expect(true).toBe(true); });"
              "  test.skip(\"skipped\", async ({ page }) => { await expect(page).toHaveTitle(/x/); });"
              "});"
              "test(\"helper only\", async () => { expect(sum(1, 1)).toBe(2); });"
              "test(\"placeholder\", async ({ page }) => { throw new Error(\"DOKIMOS-UNIMPLEMENTED x\"); });" ]

    [<Fact>]
    let ``Playwright: titles, describes, fixture destructuring, templates and regex literals`` () =
        let tests = TestScan.scanPlaywright "acceptance/AR-029.spec.js" spec
        let byTitle = tests |> List.map (fun t -> String.concat " > " t.TitlePath, t) |> Map.ofList
        Assert.Equal<string list>([ "AR-029-O1"; "group > nested"; "group > skipped"; "helper only"; "placeholder" ], tests |> List.map (fun t -> String.concat " > " t.TitlePath))
        Assert.True(TestScan.asserts byTitle["AR-029-O1"])
        Assert.True(byTitle["group > nested"].Assertions <= byTitle["group > nested"].TrivialAssertions)
        Assert.True(byTitle["group > skipped"].Skipped)
        Assert.True(byTitle["placeholder"].Placeholder)
        Assert.True(TestScan.reaches [ "page" ] byTitle["AR-029-O1"])
        Assert.False(TestScan.reaches [ "page" ] byTitle["helper only"])

    // ------------------------------------------------------------ results

    [<Fact>]
    let ``TRX identities come from TestMethod className and name`` () =
        let trx =
            "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
            + "<UnitTestResult testId=\"a\" testName=\"x\" outcome=\"Passed\"/><UnitTestResult testId=\"b\" testName=\"y\" outcome=\"Failed\"><Output><ErrorInfo><Message>boom</Message></ErrorInfo></Output></UnitTestResult>"
            + "</Results><TestDefinitions><UnitTest id=\"a\"><TestMethod className=\"App.T\" name=\"A-O1\"/></UnitTest><UnitTest id=\"b\"><TestMethod className=\"App.T\" name=\"A-O2\"/></UnitTest></TestDefinitions></TestRun>"
        match TestResults.parseTrx trx with
        | Ok results ->
            Assert.Contains(("dotnet:App.T.A-O1", Passed), results)
            Assert.Contains(("dotnet:App.T.A-O2", Failed "boom"), results)
        | Error e -> failwith e

    [<Fact>]
    let ``Playwright JSON: describe paths and a flaky test counts as failed`` () =
        let json =
            """{"suites":[{"title":"acceptance/a.spec.js","file":"acceptance/a.spec.js","specs":[{"title":"A-O1","tests":[{"projectName":"chromium","status":"expected"}]}],
               "suites":[{"title":"group","file":"acceptance/a.spec.js","specs":[{"title":"B-O1","tests":[{"projectName":"webkit","status":"flaky"}]}]}]}]}"""
        match TestResults.parsePlaywrightJson json with
        | Ok results ->
            Assert.Contains(("playwright:acceptance/a.spec.js > A-O1 [chromium]", Passed), results)
            Assert.Contains(results, fun (id, o) -> id = "playwright:acceptance/a.spec.js > group > B-O1 [webkit]" && o <> Passed)
        | Error e -> failwith e

    [<Fact>]
    let ``Playwright list logs: a retried title with differing outcomes is a failure`` () =
        let log = "  ✘  1 [webkit] › tests/browser/acceptance/a.spec.js:3:1 › A-O1 (1.3m)\n  ✓  2 [webkit] › tests/browser/acceptance/a.spec.js:3:1 › A-O1 (2s)\n"
        let results = TestResults.parsePlaywrightList "tests/browser" log
        Assert.Equal<(string * Outcome) list>([ "playwright:acceptance/a.spec.js > A-O1 [webkit]", Failed "retried: outcomes differed" ], results)

    // ------------------------------------------------------------ coverage

    let detId = "dotnet:App.Acceptance.Deterministic.SaveTests.SAVE-001-O1"
    let oneObligation = set [ bound detId "SU-DET" [ "SAVE-001-O1" ] ] [ obligation "SAVE-001-O1" "SAVE-001" "a save is confirmed by read-back" ]

    let resultsAt runSha outcome =
        Coverage.admit sha [ { Suite = "SU-DET"; Format = Trx; Path = "r.trx"; SourceSha = runSha; RunId = "1" }, [ detId, outcome ] ]

    let reconcile scan results = Coverage.reconcile oneObligation scan results [] []

    [<Fact>]
    let ``coverage: a missing test is not generated and never verified`` () =
        Assert.Equal(ObligationNotGenerated, statusOf (reconcile (scanOf []) (resultsAt sha Passed)) "SAVE-001-O1")

    [<Fact>]
    let ``coverage: a stub or placeholder cannot verify even when a result claims it passed`` () =
        let stub = scanFs "        failwith \"DOKIMOS-UNIMPLEMENTED\""
        Assert.Equal(ObligationInvalid, statusOf (reconcile (scanOf stub) (resultsAt sha Passed)) "SAVE-001-O1")
        let empty = scanFs "        ()"
        Assert.Equal(ObligationInvalid, statusOf (reconcile (scanOf empty) (resultsAt sha Passed)) "SAVE-001-O1")

    [<Fact>]
    let ``coverage: only a passing, structurally valid test at the candidate SHA verifies`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        Assert.Equal(Verified, statusOf (reconcile (scanOf real) (resultsAt sha Passed)) "SAVE-001-O1")
        Assert.Equal(ObligationFailed, statusOf (reconcile (scanOf real) (resultsAt sha (Failed "x"))) "SAVE-001-O1")
        Assert.Equal(ObligationNotRun, statusOf (reconcile (scanOf real) noResults) "SAVE-001-O1")

    [<Fact>]
    let ``coverage: results from another SHA are rejected, never carried forward`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        let report = reconcile (scanOf real) (resultsAt "c61bfe2c61bfe2c61bfe2c61bfe2c61bfe2c61bf" Passed)
        Assert.Equal(ObligationBlocked, statusOf report "SAVE-001-O1")
        Assert.Single(report.RejectedRuns) |> ignore

    [<Fact>]
    let ``coverage: a test excluded from execution or skipped cannot verify`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        let notCompiled = { Tests = real; Compiled = fun _ -> false }
        Assert.Equal(ObligationInvalid, statusOf (reconcile notCompiled (resultsAt sha Passed)) "SAVE-001-O1")
        Assert.Equal(ObligationInvalid, statusOf (reconcile (scanOf real) (resultsAt sha Skipped)) "SAVE-001-O1")

    [<Fact>]
    let ``coverage: a near-miss name is reported as identity drift`` () =
        let drifted = TestScan.scanFSharp fsFile ((fsSource "        Assert.Equal(2, save 1)").Replace("``SAVE-001-O1``", "``SAVE-001-O1 saves``"))
        let report = reconcile (scanOf drifted) noResults
        match (report.Identities |> List.exactlyOne).Status with
        | NotGenerated [ near ] -> Assert.Contains("SAVE-001-O1 saves", near)
        | other -> failwithf "expected a near miss, got %A" other

    [<Fact>]
    let ``coverage: an integrated obligation is not satisfied by a helper-only test`` () =
        let pwId = "playwright:acceptance/AR-029.spec.js > helper only [chromium]"
        let s = { oneObligation with Tests = [ bound pwId "SU-WEB" [ "SAVE-001-O1" ] ] }
        let scan = scanOf (TestScan.scanPlaywright "acceptance/AR-029.spec.js" spec)
        let results = Coverage.admit sha [ { Suite = "SU-WEB"; Format = PlaywrightJson; Path = "p"; SourceSha = sha; RunId = "" }, [ pwId, Passed ] ]
        let report = Coverage.reconcile s scan results [] []
        Assert.Equal(ObligationInvalid, statusOf report "SAVE-001-O1")
        Assert.Contains(report.Obligations.Head.Reasons, fun r -> r.Contains "exercises only helpers")

    [<Fact>]
    let ``coverage: record evidence is blocked for its producer, not judged`` () =
        let s = { oneObligation with Tests = [ bound "praxis:independent-review" "SU-DET" [ "SAVE-001-O1" ] ] }
        Assert.Equal(ObligationBlocked, statusOf (Coverage.reconcile s (scanOf []) noResults [] []) "SAVE-001-O1")

    [<Fact>]
    let ``coverage: a surviving fault probe withdraws verification; a detected one raises quality`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        let probe outcome = { ProbeId = "P1"; Obligations = [ "SAVE-001-O1" ]; SourceSha = sha; Outcome = outcome }
        let detected = Coverage.reconcile oneObligation (scanOf real) (resultsAt sha Passed) [ probe Detected ] []
        Assert.Equal("fault-sensitive", detected.Obligations.Head.Quality)
        let survived = Coverage.reconcile oneObligation (scanOf real) (resultsAt sha Passed) [ probe Survived ] []
        Assert.Equal(ObligationInvalid, statusOf survived "SAVE-001-O1")
        let unprobed = Coverage.reconcile oneObligation (scanOf real) (resultsAt sha Passed) [] []
        Assert.Equal("passing (fault sensitivity not demonstrated)", unprobed.Obligations.Head.Quality)

    [<Fact>]
    let ``coverage: a capability scope gates only its own requirements`` () =
        let s =
            set
                [ bound detId "SU-DET" [ "SAVE-001-O1" ]; bound "dotnet:App.Acceptance.Deterministic.SaveTests.LOAD-001-O1" "SU-DET" [ "LOAD-001-O1" ] ]
                [ obligation "SAVE-001-O1" "SAVE-001" "saves"; obligation "LOAD-001-O1" "LOAD-001" "loads" ]
        let real = scanFs "        Assert.Equal(2, save 1)"
        let report = Coverage.reconcile s (scanOf real) (resultsAt sha Passed) [] [ "SAVE-001" ]
        Assert.True(Coverage.allVerified report)
        Assert.False(Coverage.allVerified (Coverage.reconcile s (scanOf real) (resultsAt sha Passed) [] []))

    // ------------------------------------------------------------ scaffolds

    [<Fact>]
    let ``scaffold: identities map to files and work is ranked vertical slice first`` () =
        let s =
            set
                [ bound detId "SU-DET" [ "SAVE-001-O1" ]; bound "playwright:acceptance/UI-001.spec.js > UI-001-O1 [chromium]" "SU-WEB" [ "UI-001-O1" ] ]
                [ obligation "SAVE-001-O1" "SAVE-001" "saves"; obligation "UI-001-O1" "UI-001" "shows" ]
        let report = Coverage.reconcile s (scanOf []) noResults [] []
        let packages = Scaffold.plan s report [ "UI-001" ]
        Assert.Equal<string list>([ "tests/browser/acceptance/UI-001.spec.js"; fsFile ], packages |> List.map _.File)
        Assert.Equal<int list>([ 1; 2 ], packages |> List.map _.Rank)
        Assert.Empty(Scaffold.unmapped s report)
        let orphan = { s with NamespaceRoots = [] }
        Assert.Equal<string list>([ detId ], Scaffold.unmapped orphan (Coverage.reconcile orphan (scanOf []) noResults [] []))

    [<Fact>]
    let ``scaffold: generated skeletons carry exact identities and are classified as placeholders`` () =
        let s =
            set
                [ bound detId "SU-DET" [ "SAVE-001-O1" ]; bound "playwright:acceptance/UI-001.spec.js > group > UI-001-O1 [chromium]" "SU-WEB" [ "UI-001-O1" ] ]
                [ obligation "SAVE-001-O1" "SAVE-001" "saves"; obligation "UI-001-O1" "UI-001" "shows" ]
        let packages = Scaffold.plan s (Coverage.reconcile s (scanOf []) noResults [] []) []
        let written =
            packages
            |> List.choose (Scaffold.render s "@playwright/test" None)
            |> List.map (function
                | CreateFile(path, content) -> path, content
                | AppendToFile(path, content) -> path, content)
        let scanned =
            written
            |> List.collect (fun (path, content) ->
                if path.EndsWith ".fs" then TestScan.scanFSharp path content
                else TestScan.scanPlaywright (path.Substring "tests/browser/".Length) content)
        let report = Coverage.reconcile s (scanOf scanned) (resultsAt sha Passed) [] []
        // Every identity is now found (exact identity), and every one is a placeholder: no scaffold verifies.
        Assert.All(report.Identities, fun r ->
            match r.Status with
            | Invalid reasons -> Assert.Contains(reasons, fun x -> x.StartsWith "placeholder")
            | other -> failwithf "%s: %A" r.Identity other)

    [<Fact>]
    let ``scaffold: an existing file only gains the missing tests`` () =
        let s = set [ bound detId "SU-DET" [ "SAVE-001-O1" ]; bound "dotnet:App.Acceptance.Deterministic.SaveTests.SAVE-001-O2" "SU-DET" [ "SAVE-001-O2" ] ] [ obligation "SAVE-001-O1" "SAVE-001" "a"; obligation "SAVE-001-O2" "SAVE-001" "b" ]
        let existing = fsSource "        Assert.Equal(2, save 1)"
        let report = Coverage.reconcile s (scanOf (TestScan.scanFSharp fsFile existing)) noResults [] []
        match Scaffold.plan s report [] |> List.exactlyOne |> Scaffold.render s "" (Some existing) with
        | Some(AppendToFile(_, added)) ->
            Assert.Contains("``SAVE-001-O2``", added)
            Assert.DoesNotContain("``SAVE-001-O1``", added)
        | other -> failwithf "%A" other

    // ------------------------------------------------------------ fault probes

    [<Fact>]
    let ``fault probes apply exactly once and classify against a passing control`` () =
        let probe = { Id = "P1"; Description = ""; Obligations = [ "SAVE-001-O1" ]; File = "Save.fs"; Find = "readBack bytes"; Replace = "Ok ()"; Command = [ "dotnet"; "test" ] }
        Assert.Equal(Ok "let r = Ok ()", FaultProbe.apply probe "let r = readBack bytes")
        Assert.True(Result.isError (FaultProbe.apply probe "nothing here"))
        Assert.True(Result.isError (FaultProbe.apply probe "readBack bytes; readBack bytes"))
        Assert.Equal(Detected, FaultProbe.classify 0 1)
        Assert.Equal(Survived, FaultProbe.classify 0 0)
        Assert.True(match FaultProbe.classify 1 1 with ProbeError _ -> true | _ -> false)
