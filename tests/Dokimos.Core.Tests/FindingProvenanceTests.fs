namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Domain
open Dokimos.Core
open ProvenanceFixtures

module FindingProvenanceTests =
    let private acting keyText actor at =
        { Key = key keyText
          Actor = actor
          At = at
          Reason = None
          Evidence = [] }

    let private dokimosRun = acting "EXT-dokimos.snapshot-1" MeasurementAttribution.dokimos "2026-09-26T08:45:00.000Z"
    let private fixer = acting "EXE-20260926T100000000Z-a2a2a2a2" codex "2026-09-26T10:00:00.000Z"
    let private validator = acting "EXT-github-actions.run-777-1" ci "2026-09-26T10:20:00.000Z"
    let private reviewer = acting "CTB-20260926-5f2e19aa" kevin "2026-09-26T11:00:00.000Z"

    let private step previous current attribution block =
        FindingProvenance.transition previous current attribution block |> unwrap

    let private discovered () =
        step Absent (Present(Some 12m)) (Some([ FindingActivity.Discovered ], dokimosRun)) ProvenanceInterchange.emptyBlock

    [<Fact>]
    let ``discovery creates the finding record; remediation and validation are recorded as distinct actors`` () =
        let introduced = discovered ()
        Assert.Equal(Some Introduced, introduced.Transition)

        let remediated =
            FindingProvenance.record [ FindingActivity.Remediated ] fixer introduced.Provenance |> unwrap |> fst

        let resolved =
            step (Present(Some 12m)) Absent (Some([ FindingActivity.Validated; FindingActivity.Resolved ], validator)) remediated

        Assert.Equal(Some Resolved, resolved.Transition)
        Assert.True(resolved.Changed)

        let verdict = ProvenanceInterchange.classify resolved.Provenance
        let actors operation = ProvenanceInterchange.withOperation operation verdict |> List.map _.Actor.Id
        Assert.Equal<string list>([ "echelon/dokimos" ], actors ContributionOperation.Created)
        Assert.Equal<string list>([ "echelon/dokimos" ], actors ContributionOperation.Discovered)
        Assert.Equal<string list>([ "openai/codex" ], actors ContributionOperation.Remediated)
        Assert.Equal<string list>([ "github/github-actions" ], actors ContributionOperation.Validated)
        Assert.Equal<string list>([ "github/github-actions" ], actors ContributionOperation.Resolved)

        let reviewed = FindingProvenance.record [ FindingActivity.Reviewed ] reviewer resolved.Provenance |> unwrap |> fst
        Assert.Equal<string list>([ "kevin" ], ProvenanceInterchange.withOperation ContributionOperation.Reviewed (ProvenanceInterchange.classify reviewed) |> List.map _.Actor.Id)
        Assert.Empty(ProvenanceInterchange.preservationViolations resolved.Provenance reviewed)
        Assert.Empty(ProvenanceInterchange.preservationViolations introduced.Provenance reviewed)

    [<Fact>]
    let ``a transition without a declared actor records nothing`` () =
        let introduced = discovered ()
        let unattributed = step (Present(Some 12m)) (Present(Some 14m)) None introduced.Provenance
        Assert.Equal(Some Regressed, unattributed.Transition)
        Assert.False(unattributed.Changed)
        Assert.Equal(Json.serialize introduced.Provenance, Json.serialize unattributed.Provenance)

    [<Fact>]
    let ``resolved and discovered must match the observed transition`` () =
        let introduced = discovered ()
        Assert.True(Result.isError (FindingProvenance.transition (Present None) (Present None) (Some([ FindingActivity.Resolved ], validator)) introduced.Provenance))
        Assert.True(Result.isError (FindingProvenance.transition (Present None) Unknown (Some([ FindingActivity.Resolved ], validator)) introduced.Provenance))
        Assert.True(Result.isError (FindingProvenance.transition (Present None) Absent (Some([ FindingActivity.Discovered ], dokimosRun)) introduced.Provenance))

    [<Fact>]
    let ``the same agent remediating in two executions records two contributions`` () =
        let introduced = discovered ()
        let first = FindingProvenance.record [ FindingActivity.Remediated ] fixer introduced.Provenance |> unwrap |> fst
        let secondRun = acting "EXE-20260926T120000000Z-a3a3a3a3" codex "2026-09-26T12:00:00.000Z"
        let second = FindingProvenance.record [ FindingActivity.Remediated ] secondRun first |> unwrap |> fst

        let remediations = ProvenanceInterchange.withOperation ContributionOperation.Remediated (ProvenanceInterchange.classify second)
        Assert.Equal<string list>([ "EXE-20260926T100000000Z-a2a2a2a2"; "EXE-20260926T120000000Z-a3a3a3a3" ], remediations |> List.map (_.Key >> ContributionKey.value))
        Assert.All(remediations, fun item -> Assert.Equal(codex, item.Actor))

    [<Fact>]
    let ``an execution already attributed to one actor is never re-attributed`` () =
        let introduced = discovered ()
        let impostor = { fixer with Key = dokimosRun.Key; At = "2026-09-26T10:00:00.000Z" }
        Assert.True(Result.isError (FindingProvenance.record [ FindingActivity.Remediated ] impostor introduced.Provenance))

    [<Fact>]
    let ``an unknown actor is recorded as unknown, never replaced by the measurer or the author`` () =
        let introduced = discovered ()
        let anonymous = acting "EXT-dokimos.run-9" Actor.unknown "2026-09-26T13:00:00.000Z"
        let validated = FindingProvenance.record [ FindingActivity.Validated ] anonymous introduced.Provenance |> unwrap |> fst
        let validation = ProvenanceInterchange.withOperation ContributionOperation.Validated (ProvenanceInterchange.classify validated) |> List.exactlyOne
        Assert.Equal(Actor.unknown, validation.Actor)
        Assert.Equal(ActorKind.Unknown, validation.Actor.Kind)

    [<Fact>]
    let ``a finding with verbatim-carried provenance is never appended to`` () =
        let future = json """{"schema":"praxis.provenance/2","contributions":[]}"""
        Assert.True(Result.isError (FindingProvenance.record [ FindingActivity.Validated ] validator future))
        let unchanged = step (Present None) Absent None future
        Assert.Equal(Json.serialize future, Json.serialize unchanged.Provenance)
