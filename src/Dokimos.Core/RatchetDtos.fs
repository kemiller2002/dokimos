namespace Dokimos.Core

open System
open System.Text.Json.Serialization

// ---------------------------------------------------------------------------
// Files (camelCase, reviewed in pull requests): quality/baseline.json and
// quality/exceptions.json.
// ---------------------------------------------------------------------------

type ForbiddenReferenceFileDto = { From: string; To: string }

type RatchetConfigurationFileDto =
    { Sources: string list
      Generated: string list
      LargeFileLines: int
      ForbiddenReferences: ForbiddenReferenceFileDto list }

type RatchetBaselineFileDto =
    { [<JsonPropertyName("$schema")>]
      Schema: string
      Contract: string
      SchemaVersion: string
      Repository: string
      AcceptedAt: string
      Configuration: RatchetConfigurationFileDto
      Rules: Map<string, Map<string, decimal>> }

// ---------------------------------------------------------------------------
// Output contracts (PascalCase, like every other Dokimos contract).
// ---------------------------------------------------------------------------

type RatchetExceptionDto =
    { Id: string
      RuleId: string
      Scope: string
      Rationale: string
      Owner: string
      Created: string
      Expires: string option
      ReviewCondition: string option
      Evidence: string list
      AllowedValue: decimal option }

type RatchetInvalidExceptionDto = { Id: string; Problems: string list }

type RatchetFindingDto =
    { RuleId: string
      RuleName: string
      Scope: string
      Kind: string
      Degraded: string
      Before: decimal
      After: decimal
      BaselineRecorded: bool
      Description: string
      Remediation: string
      ExceptionProcess: string
      ExceptionId: string option
      Evidence: string list }

type RatchetRuleStateDto =
    { RuleId: string
      Name: string
      State: string
      Reason: string option }

type RatchetBaselineRefDto =
    { Path: string
      Digest: string option
      SchemaVersion: string option
      AcceptedAt: string option }

type RatchetExceptionsDto =
    { Path: string
      Digest: string option
      Active: RatchetExceptionDto list
      Expired: RatchetExceptionDto list
      Invalid: RatchetInvalidExceptionDto list
      Unused: string list }

type RatchetSummaryDto =
    { Regressions: int
      Excepted: int
      Improvements: int
      RulesMeasured: int
      RulesUnavailable: int
      RulesNotEnforced: int
      FilesAnalyzed: int
      GeneratedFilesExcluded: int }

type RatchetGeneratedDto = { Globs: string list; Files: string list }

type RatchetReportDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      CheckedAt: DateTimeOffset
      Repository: string option
      Verdict: string
      ExitCode: int
      Reasons: string list
      Baseline: RatchetBaselineRefDto
      Exceptions: RatchetExceptionsDto
      Summary: RatchetSummaryDto
      Rules: RatchetRuleStateDto list
      Generated: RatchetGeneratedDto
      Findings: RatchetFindingDto list }

type BaselineChangeDto =
    { RuleId: string
      Scope: string
      Change: string
      Before: decimal
      After: decimal option }

type RefusedChangeDto =
    { RuleId: string
      Scope: string
      Accepted: decimal option
      Current: decimal
      Reason: string }

type BaselineUpdateDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Operation: string
      Written: bool
      Path: string
      DigestBefore: string option
      DigestAfter: string
      Changes: BaselineChangeDto list
      Refused: RefusedChangeDto list
      Baseline: RatchetBaselineFileDto }

type BaselineDeltaDto =
    { RuleId: string
      Scope: string
      From: decimal option
      To: decimal option }

type BaselineDiffDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Verdict: string
      ExitCode: int
      FromDigest: string
      ToDigest: string
      Loosened: BaselineDeltaDto list
      Tightened: BaselineDeltaDto list
      ConfigurationLoosened: string list
      ConfigurationTightened: string list }

type RatchetRuleDto =
    { RuleId: string
      Name: string
      Engine: string
      Semantics: string option
      Scope: string
      Signal: string
      Description: string
      Rationale: string
      Remediation: string
      ExceptionProcess: string }

type RatchetRuleCatalogDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      Rules: RatchetRuleDto list }
