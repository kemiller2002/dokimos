namespace Dokimos.Core

/// The `dokimos.behavior-verification` 1.0.0 wire contract
/// (schemas/dokimos-behavior-verification.schema.json).
[<RequireQualifiedAccess>]
module BehaviorWire =
    [<Literal>]
    let Contract = "dokimos.behavior-verification"

    [<Literal>]
    let SchemaVersion = "1.0.0"

    let private stamp (t: System.DateTimeOffset) = t.ToString("O")

    let toWire (inputs: BehaviorInputs) (report: BehaviorReport) =
        let oracle = report.Oracle
        {| Contract = Contract
           SchemaVersion = SchemaVersion
           DokimosVersion = DokimosInfo.version
           CheckedAt = stamp inputs.CheckedAt
           WorkItem = inputs.WorkItem
           FirstPassVerdict = report.FirstPassVerdict
           ExitCode = report.ExitCode
           Reasons = report.Reasons
           Oracle =
            {| Id = oracle.OracleId
               Digest = oracle.Digest
               ExpectedDigest = inputs.ExpectedOracleDigest
               Producer = oracle.ProducerKind
               Binding =
                {| Kind = oracle.Binding.Kind
                   Contract = oracle.Binding.Contract
                   Namespace = oracle.Binding.Namespace
                   Module = oracle.Binding.Module
                   Functions = oracle.Binding.Functions |> List.map (fun x -> {| Name = x.Name; Signature = x.Signature |}) |}
               ExcludedCases = oracle.Excluded |> List.map (fun (id, reason) -> {| Id = id; Reason = reason |})
               Integrity =
                {| Usable = report.Integrity.Usable
                   DeclaredFrozenAt = report.Integrity.DeclaredFrozenAt
                   History = report.Integrity.History
                   HistoryCommit = report.Integrity.HistoryCommit
                   HistoryCommittedAt = report.Integrity.HistoryCommittedAt
                   SourceDocuments = report.Integrity.SourceDocuments |} |}
           Candidate =
            {| Commit = inputs.Candidate.Commit
               CommittedAt = stamp inputs.Candidate.CommittedAt
               Project = inputs.Project |}
           Compile = {| Status = report.Compile; Diagnostics = report.CompileDiagnostics |}
           Adapter =
            report.Probe
            |> Option.map (fun p ->
                {| Assembly = p.Assembly
                   Module = p.Module
                   Functions = p.Functions |> List.map (fun (name, status) -> {| Name = name; Status = status |})
                   Detail = p.Detail |})
            |> Option.toList
           Cases = report.Cases
           Counts = report.Counts
           ConflictObservations = report.ConflictObservations
           Requirements = report.Requirements
           Run =
            report.Run
            |> Option.map (fun r ->
                {| ObservedBy = "dokimos"
                   Sdk = r.Sdk
                   CandidateCommit = r.CandidateCommit
                   CandidateTree = r.CandidateTree
                   MaterializedFiles = r.MaterializedFiles
                   MaterializedDigest = r.MaterializedDigest
                   Project = r.Project
                   Assembly = r.Assembly
                   AssemblyDigest = r.AssemblyDigest
                   DriverDigest = r.DriverDigest
                   DriverInputDigest = r.DriverInputDigest
                   ObservationsDigest = r.ObservationsDigest
                   OracleDigest = r.OracleDigest
                   StartedAt = stamp r.StartedAt
                   FinishedAt = stamp r.FinishedAt
                   DurationMs = int64 (r.FinishedAt - r.StartedAt).TotalMilliseconds
                   Commands =
                    r.Commands
                    |> List.map (fun c ->
                        {| Purpose = c.Purpose
                           CommandLine = c.CommandLine
                           WorkingDirectory = c.WorkingDirectory
                           StartedAt = stamp c.StartedAt
                           FinishedAt = stamp c.FinishedAt
                           DurationMs = int64 (c.FinishedAt - c.StartedAt).TotalMilliseconds
                           Outcome = c.Outcome
                           ExitCode = c.ExitCode |> Option.toNullable
                           StdoutDigest = c.StdoutDigest
                           StderrDigest = c.StderrDigest |}) |})
            |> Option.toList
           Findings = report.Findings
           Limitations = report.Limitations |}
