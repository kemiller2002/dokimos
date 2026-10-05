namespace Dokimos.Core

open System

/// The accepted quality baseline. Values per rule per scope are the limits;
/// nothing is hand-typed policy. Only `baseline update` may lower them.
type RatchetBaseline =
    { SchemaVersion: string
      Repository: string
      AcceptedAt: DateTimeOffset
      Configuration: RatchetConfiguration
      /// ruleId -> scope -> accepted value. A rule key present here is
      /// enforced; an absent scope means the rule's default (zero for counts,
      /// "not over the threshold" for size rules).
      Rules: Map<string, Map<string, decimal>> }

type ExceptionReview =
    | Expires of DateOnly
    | ReviewCondition of string
    | ExpiresOrReview of DateOnly * string

/// A first-class, reviewable quality exception. It accepts one named
/// regression in one scope up to an explicit value; it never modifies the
/// baseline.
type QualityException =
    { Id: string
      RuleId: string
      Scope: string
      Rationale: string
      Owner: string
      Created: DateOnly
      Review: ExceptionReview
      Evidence: string list
      AllowedValue: decimal option }

type InvalidException = { Id: string; Problems: string list }

type ExceptionSet =
    { Valid: QualityException list
      Invalid: InvalidException list }

type ExceptionStatus =
    | ExceptionActive
    | ExceptionExpired

type RatchetFindingKind =
    | Regression
    | ExceptedRegression
    | Improvement

type RatchetFinding =
    { RuleId: string
      Scope: string
      Kind: RatchetFindingKind
      Before: decimal
      After: decimal
      BaselineRecorded: bool
      Degraded: string
      Evidence: string list
      ExceptionId: string option }

type RuleState =
    | RuleMeasured
    | RuleNotMeasured of reason: string
    | RuleNotEnforced

type RatchetVerdict =
    | RatchetPass
    | RatchetRegression
    | RatchetInvalidExceptions
    | RatchetUnavailable

type RatchetCheck =
    { Verdict: RatchetVerdict
      Reasons: string list
      CheckedAt: DateTimeOffset
      Baseline: RatchetBaseline
      RuleStates: (string * RuleState) list
      Findings: RatchetFinding list
      ActiveExceptions: QualityException list
      ExpiredExceptions: QualityException list
      InvalidExceptions: InvalidException list
      UnusedExceptions: string list
      Measurement: QualityMeasurement }

type BaselineChangeKind =
    | ScopeTightened
    | ScopeRemoved

type BaselineChange =
    { RuleId: string
      Scope: string
      Before: decimal
      After: decimal option
      Kind: BaselineChangeKind }

type RefusedChange =
    { RuleId: string
      Scope: string
      Accepted: decimal option
      Current: decimal
      Reason: string }

type BaselineUpdate =
    { Updated: RatchetBaseline
      Changes: BaselineChange list
      Refused: RefusedChange list }

type BaselineDelta =
    { RuleId: string
      Scope: string
      From: decimal option
      To: decimal option }

type BaselineDiff =
    { Loosened: BaselineDelta list
      Tightened: BaselineDelta list
      ConfigurationLoosened: string list
      ConfigurationTightened: string list }
