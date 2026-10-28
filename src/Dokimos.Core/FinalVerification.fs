namespace Dokimos.Core

open Dokimos.Core.EvidenceCheckpoint
open Dokimos.Core.EvidenceReuse

/// Mandatory final verification: at the release checkpoint every test runs,
/// whatever evidence exists, and says why (`REUSE.FINAL_VERIFICATION_REQUIRED`).
/// Pure; it only ever turns a reuse into a run, never the reverse.
module FinalVerification =
    let atCheckpoint (checkpoint: Checkpoint) (plan: Plan) : Plan =
        match checkpoint with
        | Incremental -> plan
        | Release ->
            let full (test: TestDecision) =
                let required =
                    { Code = FinalVerificationRequired
                      Subject = test.TestId
                      Detail = "the release checkpoint requires a full run; no evidence is carried into final verification" }
                { test with
                    Decision = Run
                    Reasons = required :: test.Reasons |> List.sortBy (fun r -> r.Code, r.Subject, r.Detail) }
            { plan with Checkpoint = Release; Tests = plan.Tests |> List.map full }

    /// `EvidenceReuse.plan` at a delivery checkpoint.
    let planAt (checkpoint: Checkpoint) revision changedPaths globalPatterns ignoredPatterns tests priors : Plan =
        EvidenceReuse.plan revision changedPaths globalPatterns ignoredPatterns tests priors |> atCheckpoint checkpoint
