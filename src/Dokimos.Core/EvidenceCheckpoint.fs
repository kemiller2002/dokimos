namespace Dokimos.Core

/// Where in delivery an evidence reuse plan is made (Echelon development
/// speed optimization, Priority Four: mandatory final verification).
module EvidenceCheckpoint =
    /// - Incremental: during development; evidence may be carried forward
    ///   wherever every other reuse rule allows it.
    /// - Release: the final (release) verification checkpoint. Nothing is
    ///   reused: every test runs against the release candidate itself, so
    ///   incremental reuse can never stand in for the full run.
    type Checkpoint =
        | Incremental
        | Release

    let checkpointCases = [ "incremental", Incremental; "release", Release ]

    let checkpointName =
        function
        | Incremental -> "incremental"
        | Release -> "release"

    [<Literal>]
    let FinalVerificationRequired = "REUSE.FINAL_VERIFICATION_REQUIRED"
