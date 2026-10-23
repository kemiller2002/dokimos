namespace RuleProvenance

/// Domain of the EXPERIMENTAL rule-provenance audit (VAC-002-P3-03-R1, VC-004).
/// Everything here is immutable data; analysis modules are pure functions over it.

// ---------------------------------------------------------------- handoff (input)

type RecordStatus =
    | Accepted
    | Superseded
    | Proposed
    | Rejected

type Requirement =
    { Id: string
      Status: RecordStatus
      Owner: string
      SupersededBy: string option
      /// The record's statement (optional in the handoff; empty when absent).
      Statement: string }

type Decision =
    { Id: string
      Status: RecordStatus
      Kind: string
      Owner: string
      SupersededBy: string option
      /// Decision parameters as raw strings (e.g. "limit" -> "10000.00").
      Parameters: (string * string) list
      /// The record's statement (optional in the handoff; empty when absent).
      Statement: string }

type PolicyQuestion =
    { Id: string
      Owner: string
      Affects: string list
      PermittedOutcome: string }

type Observation =
    { Id: string
      Description: string
      Kind: string
      Path: string
      Symbol: string option
      QuestionId: string option }

type Invariant =
    { Id: string
      Statement: string
      DerivedFrom: string list }

type Handoff =
    { HandoffId: string
      Requirements: Requirement list
      Decisions: Decision list
      PolicyQuestions: PolicyQuestion list
      Observations: Observation list
      Invariants: Invariant list }

// ---------------------------------------------------------------- construction trace (input)

type Disposition =
    | Authorized
    | DeferredPendingQuestion
    | Technical

type TraceAuthority = { Kind: string; Id: string }

type TraceConstruct =
    { Symbol: string
      Kind: string
      File: string
      Disposition: Disposition
      Authority: TraceAuthority list }

type TraceOutput = { Path: string; Sha256: string }

type Trace =
    { HandoffId: string
      HandoffSha256: string
      Constructs: TraceConstruct list
      ExcludedObservations: string list
      Outputs: TraceOutput list }

// ---------------------------------------------------------------- source files

/// Which side of the change a file is on. Constructed code must carry
/// provenance markers; legacy code is observed behaviour and is never modified.
type SourceRole =
    | Constructed
    | Legacy

/// A source file as data: repository-relative path ('/' separators) and text.
type SourceFile =
    { RelativePath: string
      Role: SourceRole
      Text: string }

// ---------------------------------------------------------------- findings (output)

type FindingClass =
    | InvariantViolation
    | MissingProvenance
    | SupersededAuthority
    | UnverifiedInheritedRule
    | SelfAuthorized
    | UnresolvedQuestionEncodedAsRule
    | TraceMismatch

type Certainty =
    | Proven
    | UncertainAuthority

type Location =
    { Path: string
      Line: int option
      Symbol: string option }

/// A finding before blocking policy is applied. Blocking is a policy decision
/// (VC-004 R4/R5), not a property of the analysis, so it is decided later.
type RawFinding =
    { Class: FindingClass
      Certainty: Certainty
      Location: Location
      AuthorityRefs: string list
      Message: string
      Owner: string
      /// True only when the finding comes from a formally specified invariant
      /// checked by an oracle that passed its counterexample validation.
      FromValidatedChecker: bool }

type Finding =
    { Id: string
      Class: FindingClass
      Certainty: Certainty
      Blocking: bool
      Location: Location
      AuthorityRefs: string list
      Message: string
      Owner: string }

type InvariantVerdict =
    | HoldsWithinBound
    | Violated of counterexample: string list
    | NotChecked

type InvariantResult =
    { Id: string
      Verdict: InvariantVerdict
      Method: string
      Bound: string option }

type Validity =
    { SourceDigest: string
      HandoffDigest: string
      TraceDigest: string
      ToolName: string
      ToolVersion: string
      ConfigDigest: string
      SdkVersion: string
      DependencyDigest: string option }

type Verdict =
    | Pass
    | Fail
    | PassWithOwnerQuestions

type Summary =
    { Verdict: Verdict
      Proven: int
      OwnerQuestions: int }

type Subject =
    { Repository: string
      Revision: string
      Paths: string list }

type AuditReport =
    { Subject: Subject
      Validity: Validity
      Invariants: InvariantResult list
      Findings: Finding list
      Summary: Summary }

// ---------------------------------------------------------------- configuration

/// Which provenance analysis runs. Both are kept so that results stay comparable;
/// the analyzer name and version are part of validity.configDigest, so a result
/// produced by one is never reused for the other.
type Analyzer =
    /// v1: the line-oriented lexical scan (Lexer + Scanner).
    | Lexical
    /// v2: untyped F# parse trees from FSharp.Compiler.Service (AstScanner) with
    /// authority matching against declared handoff parameters (AuthorityMatch).
    | Ast

/// The effective configuration. Its canonical form is digested into
/// validity.configDigest, so any change invalidates reuse of a prior result.
type AuditConfig =
    { /// VC-004 R5: default off until precision/recall are measured.
      BlockOnProvenProvenance: bool
      /// Owner of proven defects in constructed code (the constructing component).
      ConstructingOwner: string
      /// Owner for legacy rules that match no observation and no decision value.
      UnmatchedOwner: string
      /// Relative-path globs excluded from rule-bearing detection (not from digests).
      Excludes: string list
      Repository: string
      Revision: string
      /// The provenance analyzer (experiment default: Ast).
      Analyzer: Analyzer }

module Analyzer =
    let wire =
        function
        | Lexical -> "lexical"
        | Ast -> "ast"

    /// Bumped whenever an analyzer's classification or matching rules change.
    let version =
        function
        | Lexical -> "1"
        | Ast -> "2.0"

    let parse =
        function
        | "lexical" -> Some Lexical
        | "ast" -> Some Ast
        | _ -> None

module AuditConfig =
    let defaults =
        { BlockOnProvenProvenance = false
          ConstructingOwner = "ordo-construction"
          UnmatchedOwner = "praxis-intake"
          Excludes = []
          Repository = "unspecified"
          Revision = "unspecified"
          Analyzer = Ast }

module Tool =
    [<Literal>]
    let Name = "dokimos-rule-provenance-experiment"

    [<Literal>]
    let Version = "0.2.0-experimental"

/// Dokimos exit-code conventions (src/Dokimos.Core/Evaluation.fs ExitCodes).
module ExitCode =
    [<Literal>]
    let Continue = 0

    [<Literal>]
    let UnexpectedFault = 1

    [<Literal>]
    let InvalidInput = 2

    [<Literal>]
    let EvidenceUnavailable = 3

    [<Literal>]
    let PolicyFailure = 4

module FindingClass =
    let wire =
        function
        | InvariantViolation -> "invariant-violation"
        | MissingProvenance -> "missing-provenance"
        | SupersededAuthority -> "superseded-authority"
        | UnverifiedInheritedRule -> "unverified-inherited-rule"
        | SelfAuthorized -> "self-authorized"
        | UnresolvedQuestionEncodedAsRule -> "unresolved-question-encoded-as-rule"
        | TraceMismatch -> "trace-mismatch"

module Certainty =
    let wire =
        function
        | Proven -> "proven"
        | UncertainAuthority -> "uncertain-authority"

module Verdict =
    let wire =
        function
        | Pass -> "pass"
        | Fail -> "fail"
        | PassWithOwnerQuestions -> "pass-with-owner-questions"
