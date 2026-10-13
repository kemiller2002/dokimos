namespace Dokimos.Core

open System
open System.Text
open System.Text.Json

/// One typed obligation (`ordo.construction-obligations/1`) as Dokimos reads
/// it from a prepared artifact. Only the fields the CONSTRUCT-008 contract
/// names are read; every other field is ignored.
type PreparedObligation =
    { Id: string
      Kind: string
      RequirementKeys: string list
      NodeIds: string list
      /// Extension contracts or ports the obligation touches, e.g.
      /// `ITimerPorts.Perform/Stop` or `port:transitionEffect/Stop`.
      ExtensionSurface: string list
      /// The open question, when the obligation states one.
      Question: string
      /// Ordo's optional `planSubject`, e.g. `Stop effect failure`.
      PlanSubject: string }

/// One generated path the constructor will emit, optionally with the SHA-256
/// of its bytes.
type LayoutFile = { Path: string; Sha256: string option }

/// The parts of `praxis.construction-prepared/1` that Dokimos checks.
type PreparedArtifact =
    { /// SHA-256 of the prepared artifact's bytes, as supplied.
      Digest: string
      CacheKey: string
      Plan: JsonElement
      DeclaredPlanDigest: string
      Obligations: PreparedObligation list
      GeneratedLayout: LayoutFile list
      GeneratorId: string
      GeneratorVersion: string
      VocabularyVersion: string
      /// Declared input digests by input: blueprint, sourceManifest, decisionRecords.
      InputDigests: Map<string, string>
      PermittedExtensionFiles: string list
      /// `permittedExtensionSlots` as (kind, path); every slot path is a
      /// permitted handwritten path (e.g. kind `acceptanceAdapter`).
      PermittedExtensionSlots: (string * string) list
      /// Obligation ids in the agent handoff, when the artifact carries one.
      HandoffObligationIds: string list option }

/// The constructing agent's disposition of one obligation.
type ObligationDisposition =
    { ObligationId: string
      Status: string
      Note: string
      Files: string list
      /// The decision the agent cites as resolving the obligation, if any.
      DecisionId: string }

/// A host refusal the agent says it received.
type ReportedRejection = { Path: string; Reason: string }

/// The agent's obligation dispositions (`dispositions.json`).
type DispositionRecord =
    { SchemaVersion: string
      Dispositions: ObligationDisposition list
      RejectedOperations: ReportedRejection list }

/// A commit's tree, read from Git objects.
type TreeAt = { Commit: string; Entries: Map<string, TreeEntry> }

/// Optional mechanical-first (arm C) inputs. Each is independently supplied;
/// none is derived from the candidate.
type MechanicalInputs =
    { PreparedBytes: byte[]
      DispositionsJson: string option
      DecisionRecordsBytes: byte[] option
      PreparedReplicaBytes: byte[] option
      /// The host's apply commit that carried only generated output.
      GeneratedCommit: TreeAt option
      /// A second candidate built from identical inputs.
      Replica: TreeAt option
      /// CONSTRUCT-010: the requirements ledger (`ordo.requirements-ledger/1`).
      LedgerBytes: byte[] option
      /// CONSTRUCT-010: the operator's decision approvals.
      ApprovalsBytes: byte[] option }

/// What Dokimos already established about the candidate, passed to the
/// mechanical-first checks so that nothing is recomputed twice.
type MechanicalContext =
    { SourceManifest: JsonElement
      SourceManifestText: string
      Blueprint: JsonElement
      BlueprintText: string
      Plan: PlanModel
      ExpectedPlanDigest: string
      ManifestPath: string
      Manifest: ConstructionManifest
      Ownership: OwnershipResult
      Candidate: CandidateSnapshot
      Base: BaseSnapshot option
      Host: HostEvidence option
      /// Findings of the existing construction checks.
      Existing: ConstructionFinding list
      /// The verified plan as JSON, for the CONSTRUCT-010 decision checks.
      PlanJson: JsonElement
      /// Requirement key -> Dokimos's own oracle observation (`passed`,
      /// `failed`, `none`, `unknown`).
      Acceptance: Map<string, string> }

/// One mechanical-first check: `passed`, `failed` or `unknown`.
type MechanicalCheck =
    { Name: string
      Status: string
      Detail: string
      Findings: string list }

type ObligationTrace =
    { Id: string
      Kind: string
      RequirementKeys: string list
      Disposition: string }

type MechanicalReport =
    { PreparedDigest: string
      CacheKey: string
      GeneratorId: string
      GeneratorVersion: string
      VocabularyVersion: string
      DecisionRecordsDigest: string
      GeneratedCommit: string
      ReplicaCommit: string
      GeneratedPaths: string list
      PermittedExtensionFiles: string list
      HandwrittenPaths: string list
      Obligations: ObligationTrace list
      Checks: MechanicalCheck list
      /// CONSTRUCT-010 decision completeness: every choice and its class.
      Decisions: DecisionReport }

[<RequireQualifiedAccess>]
module MechanicalCodes =
    let PreparedArtifactInvalid = "prepared-artifact-invalid"
    let UnresolvedObligationClaimedImplemented = "unresolved-obligation-claimed-implemented"
    let ProvenanceDigestMismatch = "provenance-digest-mismatch"
    let NondeterministicGeneration = "nondeterministic-generation"
    let ObligationDropped = "obligation-dropped"
    let ObligationDispositionInvalid = "obligation-disposition-invalid"
    let UnauthorizedFileMutation = "unauthorized-file-mutation"
    let GeneratedFileEdited = "generated-file-edited"
    let ExtensionBoundaryViolation = "extension-boundary-violation"
    let GeneratedLayoutMismatch = "generated-layout-mismatch"
    let RejectedOperationUnrecorded = "rejected-operation-unrecorded"
    let RejectedOperationApplied = "rejected-operation-applied"
    // CONSTRUCT-010 decision completeness.
    let FabricatedCitation = "fabricated-citation"
    let UnresolvableCitation = "unresolvable-citation"
    let UnapprovedDecisionCited = "unapproved-decision-cited"
    let UnresolvedChoiceClaimedImplemented = "unresolved-choice-claimed-implemented"
    let UnsupportedChoiceClaimedImplemented = "unsupported-choice-claimed-implemented"
    let RefusalByOmission = "refusal-by-omission"
    let UndecidedPairRefused = "undecided-pair-refused"
    let PairDispositionMismatch = "pair-disposition-mismatch"
    let UndecidedChoiceWithoutObligation = "undecided-choice-without-obligation"
    let UnsupportedOperationDropped = "unsupported-operation-dropped"
    let UnapprovedResolution = "unapproved-resolution"
    let LedgerInvalid = "ledger-invalid"
    let RequirementDropped = "requirement-dropped"
    let DuplicateLedgerRow = "duplicate-ledger-row"
    let InventedLedgerRow = "invented-ledger-row"
    let ImplementedWithoutIndependentAcceptance = "implemented-without-independent-acceptance"
    let ImplementedWithOpenObligation = "implemented-with-open-obligation"
    let UnknownOutcomeAssumed = "unknown-outcome-assumed"
    let UnapprovedCompensation = "unapproved-compensation"
    let EffectDispositionMismatch = "effect-disposition-mismatch"
    let ApprovalsInvalid = "approvals-invalid"
