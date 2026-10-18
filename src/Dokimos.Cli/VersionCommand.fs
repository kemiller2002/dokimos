namespace Dokimos.Cli

open Dokimos.Core

module VersionCommand =
    let report () =
        let releaseVersion, sourceCommit = Installation.releaseOf DokimosInfo.version
        Output.data (
            Contracts.serialize
                {| Contract = "dokimos.version"
                   SchemaVersion = "1.1.0"
                   SystemId = "dokimos"
                   Repository = "kemiller2002/dokimos"
                   Executable = "dokimos"
                   CompatibilityAliases = ([]: string list)
                   DokimosVersion = DokimosInfo.version
                   ReleaseVersion = releaseVersion
                   SourceCommit = sourceCommit
                   Contracts =
                    {| Snapshot = Contracts.supportedSnapshotSchemas
                       Comparison = [ Contracts.ComparisonSchemaVersion ]
                       Evaluation = [ Contracts.EvaluationSchemaVersion ]
                       Policy = Contracts.supportedPolicySchemas
                       CompletionVerification = [ CompletionVerification.ReceiptSchemaVersion ] |} |}
        )
