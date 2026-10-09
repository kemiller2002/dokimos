namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core

/// ECIR's read-only source-conservation audit, with no execution authority.
module EcirCommand =
    /// Source-side ECIR audit runs on the independent pinned manifest, not a
    /// count or test pass claimed by the coding agent. Never grants
    /// implementation completion or authorization for a construction cohort.
    let ecirAudit args =
        match args with
        | [ "--manifest"; manifestPath; "--blueprint"; blueprintPath ]
        | [ "--blueprint"; blueprintPath; "--manifest"; manifestPath ] ->
            if not (File.Exists manifestPath) then
                Output.unavailable "ecir-manifest-not-found" manifestPath
            elif not (File.Exists blueprintPath) then
                Output.unavailable "ecir-blueprint-not-found" blueprintPath
            else
                match EcirAudit.audit (File.ReadAllText manifestPath) (File.ReadAllText blueprintPath) with
                | Error errors -> Output.invalid "ecir-invalid-input" (String.concat "; " errors)
                | Ok report when not report.Findings.IsEmpty ->
                    let description =
                        report.Findings
                        |> List.map (fun finding -> finding.Code + ":" + finding.RequirementKey + " (" + finding.Detail + ")")
                        |> String.concat "; "
                    Output.invalid "ecir-source-trace-failed" description
                | Ok report ->
                    Output.data (Contracts.serialize
                        {| Contract = "dokimos.ecir-audit/1"
                           Imported = report.Imported
                           Represented = report.Represented
                           Modeled = report.Modeled
                           Deferred = report.Deferred
                           Unresolved = report.Unresolved
                           Rejected = report.Rejected
                           Superseded = report.Superseded
                           IndependentlyVerified = report.IndependentlyVerified
                           Completeness = "source-trace-only; execution and tests unverified" |})
        | _ ->
            Output.invalid "invalid-arguments" "usage: dokimos ecir audit --manifest FILE --blueprint FILE"

