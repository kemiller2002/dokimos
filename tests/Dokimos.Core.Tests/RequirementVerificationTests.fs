namespace Dokimos.Core.Tests

open System.IO
open Xunit
open Dokimos.Core.Verification

/// Manifest-driven requirement verification. Most cases are adversarial: each one gives the
/// verifier a deliberate omission or defect and requires that it is not reported as PASS.
module RequirementVerificationTests =
    let commit = "1111111111111111111111111111111111111111"
    let hash = "abababababababababababababababababababababababababababababababab"
    let test = "playwright:investigation.spec.js > INV-002 [chromium]"

    let obligation (requirement: string) (level: string) (check: string) : ManifestObligation =
        { Id = $"{requirement}/{level}/{check}"; Level = level; Check = check; Mandatory = true }

    /// INV-002: the required problem statement, a textarea of 1..20 characters.
    let statement: ManifestRequirement =
        { Id = "INV-002"; Kind = "ui-field"; Screen = "investigation-start"; Name = "Problem statement"
          Component = Some "textarea"; Required = true; MinLength = Some 1; MaxLength = Some 20; Pattern = None; Options = []
          AppliesWhen = None; Repeated = false; Mappings = [ "web/index.html" ]
          Obligations =
            [ obligation "INV-002" "V1" "mapped"; obligation "INV-002" "V1" "exists"; obligation "INV-002" "V2" "visible"
              obligation "INV-002" "V2" "accessible"; obligation "INV-002" "V3" "editable"; obligation "INV-002" "V3" "enter"
              obligation "INV-002" "V4" "validation"; obligation "INV-002" "V4" "rejects"; obligation "INV-002" "V5" "save"
              obligation "INV-002" "V5" "reload" ] }

    let manifest requirements : RequirementManifest =
        { CatalogId = "CAT-TEST"; SourceCommit = commit; ContentHash = hash; Intact = true; Requirements = requirements }

    let textarea: ElementFact =
        { Tag = "textarea"; InputType = ""; Role = "textbox"; AccessibleName = "What happened?"; Visible = true
          Disabled = false; ReadOnly = false; Instance = None; Options = [] }

    let attempt value observed accepted : Attempt = { Value = value; Observed = observed; Accepted = accepted }

    let probe check : Probe =
        let id = $"""INV-002/{(match check with
                               | "exists" -> "V1"
                               | "visible" | "accessible" -> "V2"
                               | "editable" | "enter" -> "V3"
                               | "validation" | "rejects" -> "V4"
                               | _ -> "V5")}/{check}"""

        let base' = { Obligation = id; Test = test; Elements = Some [ textarea ]; Attempts = []; Expected = None; Observed = None }

        match check with
        | "enter" -> { base' with Attempts = [ attempt "Disk full" "Disk full" true ] }
        | "validation" ->
            { base' with Attempts = [ attempt "Disk full" "Disk full" true; attempt "" "" false; attempt (String.replicate 21 "x") "" false ] }
        | "rejects" -> { base' with Attempts = [ attempt "" "" false; attempt (String.replicate 21 "x") "" false ] }
        | "save" -> { base' with Expected = Some "Disk full"; Observed = Some "Disk full" }
        | "reload" -> { base' with Expected = Some "Disk full"; Observed = Some "Disk full" }
        | _ -> base'

    let checks = [ "exists"; "visible"; "accessible"; "editable"; "enter"; "validation"; "rejects"; "save"; "reload" ]

    let observations probes : Observations =
        { RunId = "run-1"; SourceCommit = commit; ManifestHash = hash; Facts = Map.empty; Probes = probes }

    let source = fun (location: string) -> if location = "web/index.html" then Some """<textarea data-requirement-id="INV-002"></textarea>""" else None

    let evidence probes : Evidence =
        { Observations = Ok(observations probes)
          Results = Ok(Map [ test, Passed ])
          Source = source
          Candidate = Some commit
          Facts = Map.empty }

    let correct = evidence (checks |> List.map probe)

    let verify requirements e = RequirementVerification.verify (manifest requirements) [] e

    let statusOf (report: VerificationReport) (obligationId: string) =
        report.Requirements |> List.collect _.Obligations |> List.find (fun o -> o.Obligation.Id = obligationId) |> _.Status

    let replaceProbe check (f: Probe -> Probe) (e: Evidence) =
        { e with Observations = e.Observations |> Result.map (fun o -> { o with Probes = o.Probes |> List.map (fun p -> if p.Obligation.EndsWith("/" + check) then f p else p) }) }

    [<Fact>]
    let ``a correctly implemented, fully exercised field is verified and accepted`` () =
        let report = verify [ statement ] correct
        Assert.True(report.Accepted, RequirementVerification.toText report)
        Assert.Equal(Pass, report.Requirements.Head.Status)
        Assert.True report.Requirements.Head.Implemented

    [<Fact>]
    let ``adversarial 1: removing the required field fails structurally and blocks acceptance`` () =
        let removed =
            { correct with
                Source = fun _ -> Some "<p>no field</p>"
                Observations = correct.Observations |> Result.map (fun o -> { o with Probes = o.Probes |> List.map (fun p -> { p with Elements = Some [] }) }) }

        let report = verify [ statement ] removed
        Assert.False report.Accepted
        Assert.Equal(Fail, report.Requirements.Head.Status)
        Assert.Equal(Some "structural", report.Requirements.Head.FailureClass)
        Assert.Equal(Fail, statusOf report "INV-002/V1/mapped")
        Assert.Equal(Fail, statusOf report "INV-002/V1/exists")
        Assert.False report.Requirements.Head.Implemented

    [<Fact>]
    let ``adversarial 2: removing a field's test while keeping the requirement is NOT_RUN and blocks acceptance`` () =
        let withoutEnter = evidence (checks |> List.filter ((<>) "enter") |> List.map probe)
        let report = verify [ statement ] withoutEnter
        Assert.False report.Accepted
        Assert.Equal(NotRun, statusOf report "INV-002/V3/enter")
        Assert.Contains("INV-002/V3/enter", report.Blockers)

    [<Fact>]
    let ``adversarial 3: the ID rendered on an unrelated hidden element fails existence and visibility`` () =
        let hiddenSpan = { textarea with Tag = "span"; Role = ""; Visible = false }
        let report = verify [ statement ] (evidence (checks |> List.map (fun c -> { probe c with Elements = Some [ hiddenSpan ] })))
        Assert.False report.Accepted
        Assert.Equal(Fail, statusOf report "INV-002/V1/exists")
        Assert.Equal(Fail, statusOf report "INV-002/V2/visible")

    [<Fact>]
    let ``a hidden control of the right kind cannot pass a visibility check`` () =
        let hidden = { textarea with Visible = false }
        let report = verify [ statement ] (evidence (checks |> List.map (fun c -> { probe c with Elements = Some [ hidden ] })))
        Assert.Equal(Pass, statusOf report "INV-002/V1/exists")
        Assert.Equal(Fail, statusOf report "INV-002/V2/visible")

    [<Fact>]
    let ``adversarial 4: a disabled input fails interaction`` () =
        let disabled = { textarea with Disabled = true }
        let report = verify [ statement ] (evidence (checks |> List.map (fun c -> { probe c with Elements = Some [ disabled ] })))
        Assert.False report.Accepted
        Assert.Equal(Fail, statusOf report "INV-002/V3/editable")
        Assert.Equal(Fail, statusOf report "INV-002/V3/enter")
        Assert.Equal(Some "interaction", report.Requirements.Head.FailureClass)

    [<Fact>]
    let ``adversarial 5: a field that accepts input but does not save fails the lifecycle`` () =
        let report = verify [ statement ] (correct |> replaceProbe "save" (fun p -> { p with Observed = Some "" }) |> replaceProbe "reload" (fun p -> { p with Observed = Some "" }))
        Assert.False report.Accepted
        Assert.Equal(Pass, statusOf report "INV-002/V3/enter")
        Assert.Equal(Fail, statusOf report "INV-002/V5/save")
        Assert.Equal(Some "lifecycle", report.Requirements.Head.FailureClass)

    [<Fact>]
    let ``adversarial 6: saving the wrong value fails`` () =
        let report = verify [ statement ] (correct |> replaceProbe "reload" (fun p -> { p with Observed = Some "Disk ful" }))
        Assert.Equal(Fail, statusOf report "INV-002/V5/reload")
        Assert.False report.Accepted

    [<Fact>]
    let ``adversarial 7: a duplicated ID in one scope is ambiguous`` () =
        let report = verify [ statement ] (evidence (checks |> List.map (fun c -> { probe c with Elements = Some [ textarea; textarea ] })))
        Assert.Equal(Fail, statusOf report "INV-002/V1/exists")
        let reason = report.Requirements.Head.Obligations |> List.find (fun o -> o.Obligation.Check = "exists") |> _.Reason
        Assert.Contains("ambiguous", reason)

    [<Fact>]
    let ``repeated controls need distinct instance keys`` () =
        let repeated = { statement with Repeated = true }
        let keyed key = { textarea with Instance = Some key }
        let distinct = verify [ repeated ] (evidence (checks |> List.map (fun c -> { probe c with Elements = Some [ keyed "H1"; keyed "H2" ] })))
        Assert.Equal(Pass, statusOf distinct "INV-002/V1/exists")
        let shared = verify [ repeated ] (evidence (checks |> List.map (fun c -> { probe c with Elements = Some [ keyed "H1"; keyed "H1" ] })))
        Assert.Equal(Fail, statusOf shared "INV-002/V1/exists")

    [<Fact>]
    let ``adversarial 8: an invalid manifest is refused`` () =
        Assert.True(RequirementVerification.parseManifest """{"schema":"praxis.verification-manifest/2"}""" |> Result.isError)
        Assert.True(RequirementVerification.parseManifest """{"schema":"praxis.verification-manifest/1","requirements":[]}""" |> Result.isError)
        Assert.True(RequirementVerification.parseManifest "not json" |> Result.isError)

    [<Fact>]
    let ``adversarial 9: evidence from a test that did not execute is never PASS`` () =
        let skipped = { correct with Results = Ok(Map [ test, Skipped ]) }
        let absent = { correct with Results = Ok Map.empty }
        let failed = { correct with Results = Ok(Map [ test, Failed "timeout" ]) }

        for e, expected in [ skipped, NotRun; absent, NotRun; failed, Fail ] do
            let report = verify [ statement ] e
            Assert.False report.Accepted
            Assert.Equal(expected, statusOf report "INV-002/V3/enter")
            // The structural source check does not depend on the browser run.
            Assert.Equal(Pass, statusOf report "INV-002/V1/mapped")

    [<Fact>]
    let ``adversarial 10: observations recorded against another manifest are stale and BLOCKED`` () =
        let stale = { correct with Observations = correct.Observations |> Result.map (fun o -> { o with ManifestHash = "cd" + hash.Substring 2 }) }
        let report = verify [ statement ] stale
        Assert.False report.Accepted
        Assert.Equal(RequirementStatus.Blocked, statusOf report "INV-002/V3/enter")
        Assert.NotEmpty report.EvidenceProblems

    [<Fact>]
    let ``a manifest edited after compilation is never accepted`` () =
        let report = RequirementVerification.verify { manifest [ statement ] with Intact = false } [] correct
        Assert.False report.Accepted

    [<Fact>]
    let ``observations from another commit are BLOCKED`` () =
        let report = verify [ statement ] { correct with Candidate = Some "2222222222222222222222222222222222222222" }
        Assert.Equal(RequirementStatus.Blocked, statusOf report "INV-002/V2/visible")
        Assert.False report.Accepted

    [<Fact>]
    let ``test infrastructure failure is BLOCKED, never PASS`` () =
        let report = verify [ statement ] { correct with Results = Error "playwright.json does not exist"; Observations = Error "observations.json does not exist" }
        Assert.False report.Accepted
        Assert.Equal(RequirementStatus.Blocked, statusOf report "INV-002/V3/enter")
        Assert.Equal(Pass, statusOf report "INV-002/V1/mapped")

    [<Fact>]
    let ``validation that accepts an over-long value fails behaviourally`` () =
        let report =
            verify [ statement ] (correct |> replaceProbe "validation" (fun p -> { p with Attempts = [ attempt "ok" "ok" true; attempt "" "" false; attempt (String.replicate 21 "x") "" true ] }))
        Assert.Equal(Fail, statusOf report "INV-002/V4/validation")
        Assert.Equal(Some "behavioural", report.Requirements.Head.FailureClass)

    [<Fact>]
    let ``validation evidence that never exercises a rule is not run`` () =
        let report = verify [ statement ] (correct |> replaceProbe "validation" (fun p -> { p with Attempts = [ attempt "ok" "ok" true; attempt "" "" false ] }))
        Assert.Equal(NotRun, statusOf report "INV-002/V4/validation")

    [<Fact>]
    let ``NOT_APPLICABLE comes only from a declared fact with another value`` () =
        let conditional = { statement with AppliesWhen = Some [ "ai-provider-configured", true ] }
        let off = verify [ conditional ] { correct with Facts = Map [ "ai-provider-configured", false ] }
        Assert.Equal(NotApplicable, off.Requirements.Head.Status)
        Assert.Empty off.Blockers
        let unknown = verify [ conditional ] correct
        Assert.Equal(RequirementStatus.Blocked, unknown.Requirements.Head.Status)
        // A missing implementation never makes a requirement inapplicable.
        let missing = verify [ statement ] { correct with Source = fun _ -> None }
        Assert.Equal(Fail, missing.Requirements.Head.Status)

    [<Fact>]
    let ``an unmapped requirement fails its mapped check`` () =
        let report = verify [ { statement with Mappings = [] } ] correct
        Assert.Equal(Fail, statusOf report "INV-002/V1/mapped")

    [<Fact>]
    let ``a select offering other options than required fails`` () =
        let kind: ManifestRequirement =
            { statement with
                Id = "INV-003"; Component = Some "select"; MinLength = None; MaxLength = None; Options = [ "incident"; "defect" ]
                Obligations = [ obligation "INV-003" "V3" "select" ] }
        let element = { textarea with Tag = "select"; Role = "combobox"; Options = [ "incident" ] }
        let p = { Obligation = "INV-003/V3/select"; Test = test; Elements = Some [ element ]; Attempts = [ attempt "incident" "incident" true; attempt "defect" "defect" true ]; Expected = None; Observed = None }
        let report = verify [ kind ] (evidence [ p ])
        Assert.Equal(Fail, statusOf report "INV-003/V3/select")

    [<Fact>]
    let ``the report is reproducible: identical inputs give identical JSON`` () =
        let a = RequirementVerification.toJson (verify [ statement ] correct)
        let b = RequirementVerification.toJson (verify [ statement ] correct)
        Assert.Equal(a, b)

    [<Fact>]
    let ``summary distinguishes implemented coverage from verified coverage`` () =
        let enterMissing = evidence (checks |> List.filter ((<>) "enter") |> List.map probe)
        let s = RequirementVerification.summary (verify [ statement ] enterMissing)
        Assert.Equal(1, s.Implemented)
        Assert.Equal(0, s.Verified)
        Assert.Equal(1, s.NotRun)
        Assert.Equal(0.0, s.Percent)

    [<Fact>]
    let ``ordo evidence carries one requirement result in the shape ordo verification schedule ingests`` () =
        let files = RequirementVerification.ordoEvidence (verify [ statement ] correct) "run-1"
        let name, body = List.exactlyOne files
        Assert.Equal("INV-002.json", name)
        Assert.Contains("\"schema\": \"causa.acceptance-evidence/1\"", body)
        Assert.Contains("\"status\": \"PASS\"", body)

    [<Fact>]
    let ``a manifest compiled by Praxis parses and is intact`` () =
        let path = Path.Combine(__SOURCE_DIRECTORY__, "fixtures", "requirements", "verification-manifest.json")
        match RequirementVerification.parseManifest (File.ReadAllText path) with
        | Error e -> failwith e
        | Ok m ->
            Assert.True(m.Intact, "the Praxis contentHash must be reproducible by Dokimos")
            Assert.NotEmpty m.Requirements
            Assert.All(m.Requirements, fun r -> Assert.NotEmpty r.Obligations)
        // Any edit breaks the hash.
        let edited = (File.ReadAllText path).Replace("\"mandatory\": true", "\"mandatory\": false")
        match RequirementVerification.parseManifest edited with
        | Ok m -> Assert.False m.Intact
        | Error e -> failwith e

    [<Fact>]
    let ``a scoped run accepts only its scope and an unknown scoped ID is never accepted`` () =
        let other = { statement with Id = "INV-009"; Mappings = []; Obligations = [ obligation "INV-009" "V1" "mapped" ] }
        let scoped = RequirementVerification.verifyScoped (manifest [ statement; other ]) [] [ "INV-002" ] correct
        Assert.True(scoped.Accepted)
        Assert.Equal<string list>([ "INV-002" ], scoped.Requirements |> List.map _.Requirement.Id)
        let whole = RequirementVerification.verify (manifest [ statement; other ]) [] correct
        Assert.False whole.Accepted
        let unknown = RequirementVerification.verifyScoped (manifest [ statement ]) [] [ "INV-404" ] correct
        Assert.False unknown.Accepted
