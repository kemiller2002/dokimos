module Dokimos.Core.Tests.ConstructionEvidenceTests

open System
open Xunit
open Dokimos.Core
open Dokimos.Core.Tests.ConstructionFixtures

let private hostJson enforcement mechanism proofs observations =
    sprintf """{"schema":"dokimos.host-observations/1","host":{"id":"worker-host-1","recorder":"host","enforcement":"%s","mechanism":"%s","platformProofs":[%s]},"observations":[%s]}"""
        enforcement mechanism proofs observations

let private host json = ConstructionEvidence.readHost json |> Result.defaultWith failwith

let private claims json = ConstructionEvidence.readClaims json |> Result.defaultWith failwith

let private claimsJson confinement denials tests =
    sprintf """{"schema":"dokimos.construction-claims/1","source":"worker","confinement":"%s","denials":[%s],"testResults":[%s]}""" confinement denials tests

[<Fact>]
let ``a claimed denial with only a clean diff as evidence has no host evidence`` () =
    let c = claims (claimsJson "unknown" """{"attemptId":"ATT-1","path":"quality/baseline.json","evidence":"clean-diff"}""" "")
    let report = verify { timerInputs () with Claims = Some c }
    Assert.Contains("ATT-1", subjects ConstructionCodes.HostDenialWithoutEvidence report)

[<Fact>]
let ``a host-recorded denial satisfies the claim`` () =
    let observed =
        host (hostJson "enforced" "landlock" """{"name":"linux-landlock-abi","status":"available","detail":"ABI 4"}"""
                  """{"id":"OBS-1","attemptId":"ATT-1","kind":"write","path":"quality/baseline.json","outcome":"denied","recordedBy":"host","protected":true,"detail":"EACCES"}""")
    let c = claims (claimsJson "confined" """{"attemptId":"ATT-1","path":"quality/baseline.json","evidence":"host-observation"}""" "")
    let report = verify { timerInputs () with Host = Some observed; Claims = Some c }
    Assert.DoesNotContain(ConstructionCodes.HostDenialWithoutEvidence, codes report)
    Assert.Equal("confined", report.Containment.Status)
    Assert.Equal(1, report.Containment.HostRecordedDenials)

[<Fact>]
let ``a worker-recorded denial is not host evidence`` () =
    let observed =
        host (hostJson "enforced" "landlock" "" """{"id":"OBS-1","attemptId":"ATT-1","kind":"write","path":"quality/baseline.json","outcome":"denied","recordedBy":"worker"}""")
    let c = claims (claimsJson "unknown" """{"attemptId":"ATT-1","path":"quality/baseline.json","evidence":"checklist"}""" "")
    Assert.Contains(ConstructionCodes.HostDenialWithoutEvidence, codes (verify { timerInputs () with Host = Some observed; Claims = Some c }))

[<Fact>]
let ``write then revert of a protected rule leaves a clean diff that is never denial evidence`` () =
    let observed =
        host (hostJson "enforced" "landlock" """{"name":"linux-landlock-abi","status":"available","detail":""}"""
                  """{"id":"OBS-2","attemptId":"ATT-2","kind":"write","path":"app/App.fsproj","outcome":"allowed","recordedBy":"host","protected":true}""")
    let c = claims (claimsJson "confined" """{"attemptId":"ATT-2","path":"app/App.fsproj","evidence":"clean-diff"}""" "")
    let report = verify { timerInputs () with Host = Some observed; Claims = Some c }
    let found = codes report
    Assert.Contains(ConstructionCodes.CleanDiffNotDenial, found)
    Assert.Contains(ConstructionCodes.ProtectedWriteAllowed, found)
    Assert.Contains(ConstructionCodes.HostDenialWithoutEvidence, found)
    Assert.Contains(ConstructionCodes.ConfinementClaimUnsupported, found)
    Assert.Equal("not-confined", report.Containment.Status)

[<Fact>]
let ``a deliberately unprotected host is not reported as confined`` () =
    let observed = host (hostJson "unprotected" "" "" "")
    let c = claims (claimsJson "confined" "" "")
    let report = verify { timerInputs () with Host = Some observed; Claims = Some c }
    Assert.Equal("not-confined", report.Containment.Status)
    Assert.Contains(ConstructionCodes.ConfinementClaimUnsupported, codes report)

[<Fact>]
let ``unavailable OS-specific proofs leave containment unknown and are named`` () =
    let observed =
        host (hostJson "enforced" "seatbelt" """{"name":"macos-sandbox-profile","status":"unavailable","detail":"not exposed by this OS"}""" "")
    let report = verify { timerInputs () with Host = Some observed }
    Assert.Equal("unknown", report.Containment.Status)
    Assert.Equal<string list>([ "macos-sandbox-profile: unavailable" ], report.Containment.UnavailableProofs)
    let none = verify (timerInputs ())
    Assert.Equal("unknown", none.Containment.Status)

[<Fact>]
let ``a dependency change is unauthorized unless authorization names it exactly`` () =
    let changed = projectFile.Replace("</ItemGroup>", """<PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>""")
    let inputs = timerInputs () |> withText "app/App.fsproj" changed
    Assert.Contains("app/App.fsproj:package:Newtonsoft.Json", subjects ConstructionCodes.UnauthorizedDependency (verify inputs))
    let authorization =
        ConstructionEvidence.readAuthorization
            """{"schema":"dokimos.construction-authorization/1","authority":"DEC-DEPS","dependencies":[{"project":"app/App.fsproj","kind":"package","include":"Newtonsoft.Json","change":"added","reason":"r","owner":"o"}]}"""
        |> Result.defaultWith failwith
    Assert.DoesNotContain(ConstructionCodes.UnauthorizedDependency, codes (verify { inputs with Authorization = authorization }))
    let projectRef = projectFile.Replace("</ItemGroup>", """<ProjectReference Include="../Other/Other.fsproj" /></ItemGroup>""")
    Assert.Contains(ConstructionCodes.UnauthorizedDependency, codes (verify (timerInputs () |> withText "app/App.fsproj" projectRef)))

[<Fact>]
let ``claimed passes are fake unless keyed to the oracle and observed by Dokimos`` () =
    let c = claims (claimsJson "unknown" "" """{"id":"TIMER-ORACLE-001","outcome":"passed"},{"id":"GeneratedTimerTests.allPairs","outcome":"passed"}""")
    let report = verify { timerInputs () with Claims = Some c }
    let fakes = subjects ConstructionCodes.FakeTestResult report
    Assert.Contains("TIMER-ORACLE-001", fakes)
    Assert.Contains("GeneratedTimerTests.allPairs", fakes)
    Assert.Equal(0, report.Counts.IndependentlyVerified)
    let trx =
        """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="StructuralChecks.pairs" outcome="Passed" /></Results></TestRun>"""
        |> ConstructionEvidence.readTrxClaims |> Result.defaultWith failwith
    Assert.Contains("StructuralChecks.pairs", subjects ConstructionCodes.FakeTestResult (verify { timerInputs () with ClaimedTests = trx }))

[<Fact>]
let ``oracle integrity: wrong digest, late freeze and generator authorship each disqualify it`` () =
    let inputs = timerInputs ()
    Assert.Contains(ConstructionCodes.OracleDigestMismatch, codes (verify { inputs with ExpectedOracleDigest = "sha256:" + String('1', 64) }))
    let early = { inputs with Candidate = { inputs.Candidate with CommittedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) } }
    Assert.Contains(ConstructionCodes.OracleNotFrozen, codes (verify early))
    let text = Text.Encoding.UTF8.GetString(oracleBytes ())
    let generated = Text.Encoding.UTF8.GetBytes(text.Replace("\"kind\": \"independent-evaluator\"", "\"kind\": \"generator\""))
    let report = verify { inputs with OracleBytes = Some generated; ExpectedOracleDigest = ConstructionJson.sha256Bytes generated }
    Assert.Contains(ConstructionCodes.OracleNotIndependent, codes report)

[<Fact>]
let ``an unexpired exception keeps the finding visible but not blocking; an expired one does not apply`` () =
    let path = "generated/Timer/Timer.Notes.md"
    let inputs = timerInputs () |> withText path "notes\n"
    let authorization expires =
        ConstructionEvidence.readAuthorization
            (sprintf """{"schema":"dokimos.construction-authorization/1","authority":"x","exceptions":[{"code":"undocumented-generated-file","subject":"%s","reason":"design notes","owner":"kevin","expires":"%s"}]}""" path expires)
        |> Result.defaultWith failwith
    let excepted = verify { inputs with Authorization = authorization "2099-01-01T00:00:00Z" }
    Assert.Equal(1, excepted.ExceptionsApplied)
    Assert.Contains(excepted.Findings, fun f -> f.Code = ConstructionCodes.UndocumentedGeneratedFile && f.Disposition = "excepted")
    Assert.Equal("incomplete", excepted.Verdict)
    let expired = verify { inputs with Authorization = authorization "2020-01-01T00:00:00Z" }
    Assert.Equal("rejected", expired.Verdict)

[<Fact>]
let ``a waiver is counted as waived, separately from verified`` () =
    let key = "kemiller2002/time-entry-state-machine/requirements-reconstruction/fsharp-business-requirements-beyond-csharp.md#L113-L114"
    let authorization =
        ConstructionEvidence.readAuthorization
            (sprintf """{"schema":"dokimos.construction-authorization/1","authority":"x","waivers":[{"requirementKey":"%s","reason":"conflict pending","owner":"kevin"}]}""" key)
        |> Result.defaultWith failwith
    let report = verify { timerInputs () with Authorization = authorization }
    Assert.Equal(1, report.Counts.Waived)
    Assert.Equal(1, report.Counts.Unresolved)
    Assert.Equal(0, report.Counts.IndependentlyVerified)
