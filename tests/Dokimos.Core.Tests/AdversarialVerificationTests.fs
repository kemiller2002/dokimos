namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core.Verification
open Dokimos.Core.Tests.VerificationTests

/// DOK-VC-ADV-001: adversarial verification cases. A test identity recorded more than once
/// (xUnit theory rows share one TRX identity; retries and reruns at the same SHA repeat it)
/// must keep its worst outcome. Before this fix `Coverage.admit` kept the last outcome written,
/// so a failure followed by a pass reported the obligation as Verified.
module AdversarialVerificationTests =

    [<Fact>]
    let ``adversarial: a failed then passed result for one identity in one run is a failure, not a pass`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        let results =
            Coverage.admit sha [ { Suite = "SU-DET"; Format = Trx; Path = "r.trx"; SourceSha = sha; RunId = "1" }, [ detId, Failed "boom"; detId, Passed ] ]
        Assert.Equal(ObligationFailed, statusOf (reconcile (scanOf real) results) "SAVE-001-O1")

    [<Fact>]
    let ``adversarial: a rerun that passes at the same SHA does not erase an earlier failed run`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        let run id = { Suite = "SU-DET"; Format = Trx; Path = $"r{id}.trx"; SourceSha = sha; RunId = id }
        let results = Coverage.admit sha [ run "1", [ detId, Failed "boom" ]; run "2", [ detId, Passed ] ]
        Assert.Equal(ObligationFailed, statusOf (reconcile (scanOf real) results) "SAVE-001-O1")

    [<Fact>]
    let ``adversarial: a skipped duplicate cannot launder a failure and a passed duplicate cannot launder a skip`` () =
        let real = scanFs "        Assert.Equal(2, save 1)"
        let at outcomes = Coverage.admit sha [ { Suite = "SU-DET"; Format = Trx; Path = "r.trx"; SourceSha = sha; RunId = "1" }, outcomes |> List.map (fun o -> detId, o) ]
        Assert.Equal(ObligationFailed, statusOf (reconcile (scanOf real) (at [ Failed "x"; Skipped ])) "SAVE-001-O1")
        Assert.Equal(ObligationInvalid, statusOf (reconcile (scanOf real) (at [ Skipped; Passed ])) "SAVE-001-O1")
        Assert.Equal(ObligationNotRun, statusOf (reconcile (scanOf real) (at [ NotExecuted; Passed ])) "SAVE-001-O1")

    [<Fact>]
    let ``adversarial: TRX data-driven rows share one identity, so one failing row fails the identity`` () =
        let trx =
            "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
            + "<UnitTestResult testId=\"r1\" testName=\"SAVE-001-O1(x: 1)\" outcome=\"Failed\"><Output><ErrorInfo><Message>row 1 broke</Message></ErrorInfo></Output></UnitTestResult>"
            + "<UnitTestResult testId=\"r2\" testName=\"SAVE-001-O1(x: 2)\" outcome=\"Passed\"/>"
            + "</Results><TestDefinitions>"
            + "<UnitTest id=\"r1\"><TestMethod className=\"App.Acceptance.Deterministic.SaveTests\" name=\"SAVE-001-O1\"/></UnitTest>"
            + "<UnitTest id=\"r2\"><TestMethod className=\"App.Acceptance.Deterministic.SaveTests\" name=\"SAVE-001-O1\"/></UnitTest>"
            + "</TestDefinitions></TestRun>"
        let real = scanFs "        Assert.Equal(2, save 1)"
        match TestResults.parseTrx trx with
        | Error e -> failwith e
        | Ok outcomes ->
            let results = Coverage.admit sha [ { Suite = "SU-DET"; Format = Trx; Path = "r.trx"; SourceSha = sha; RunId = "1" }, outcomes ]
            Assert.Equal(ObligationFailed, statusOf (reconcile (scanOf real) results) "SAVE-001-O1")

    [<Fact>]
    let ``adversarial: worst outcome ordering is Failed, Skipped, NotExecuted, Passed`` () =
        Assert.Equal(Failed "a", TestResults.worst [ Passed; Failed "a"; Skipped ])
        Assert.Equal(Skipped, TestResults.worst [ Passed; Skipped; NotExecuted ])
        Assert.Equal(NotExecuted, TestResults.worst [ Passed; NotExecuted ])
        Assert.Equal(Passed, TestResults.worst [ Passed; Passed ])
        Assert.Equal(Failed "first", TestResults.worst [ Failed "first"; Failed "second" ])
