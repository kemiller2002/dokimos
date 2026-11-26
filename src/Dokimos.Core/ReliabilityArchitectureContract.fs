namespace Dokimos.Core

open System
open Dokimos.Core.Json

type ArchitectureTenetDto =
    { Tenet: string
      Applicability: string
      Reason: string option }

type ArchitectureDeclarationDto =
    { Path: string
      State: string
      Reason: string option
      SystemClass: string option
      Tenets: ArchitectureTenetDto list }

type ArchitectureRuleStateDto =
    { RuleId: string
      Name: string
      Tenet: string
      State: string
      Reason: string option
      Violations: int
      Accepted: int }

type ArchitectureFindingDto =
    { RuleId: string
      RuleName: string
      Scope: string
      Kind: string
      File: string
      Line: int
      Subject: string
      Detail: string
      Description: string
      Remediation: string
      ExceptionProcess: string
      ExceptionId: string option
      DecisionRecords: string list }

type ArchitectureSummaryDto =
    { Violations: int
      Accepted: int
      RulesEvaluated: int
      RulesNotApplicable: int
      RulesNotConfigured: int
      RulesUnavailable: int
      ProjectFilesAnalyzed: int }

type ArchitectureReportDto =
    { Contract: string
      SchemaVersion: string
      DokimosVersion: string
      CheckedAt: DateTimeOffset
      Verdict: string
      ExitCode: int
      Reasons: string list
      Declaration: ArchitectureDeclarationDto
      Exceptions: RatchetExceptionsDto
      Summary: ArchitectureSummaryDto
      Rules: ArchitectureRuleStateDto list
      Findings: ArchitectureFindingDto list
      ProjectFiles: string list
      Limitations: string list }

/// `.echelon/reliability.json` decoding and the `dokimos.architecture`
/// report contract.
module ReliabilityArchitectureContract =
    [<Literal>]
    let ReportContract = "dokimos.architecture"

    [<Literal>]
    let ReportSchemaVersion = "1.0.0"

    [<Literal>]
    let DeclarationPath = ".echelon/reliability.json"

    [<Literal>]
    let DeclarationSchema = "echelon.reliability/v1"

    /// What a pass of these rules does not establish (RQ-DOKIMOS-ARCH-001).
    let limitations =
        [ "Static evidence only: a pass does not establish runtime reliability, availability or recovery."
          "Only direct PackageReference/GlobalPackageReference and ProjectReference elements are read; transitive packages, framework references (for example Microsoft.AspNetCore.App, which ships Microsoft.Extensions.Caching.Memory), paket and MSBuild property-valued includes are not analyzed."
          "An accepted exception proves that a decision record id is named, not that the decision is accepted." ]

    let private tenetDecoder: Decoder<TenetApplicability> =
        fun e ->
            result {
                // None stands for not-applicable, which needs its reason.
                let! applicable =
                    field "applicability" (oneOf [ "applicable", Some TenetApplicable; "partially-applicable", Some TenetPartiallyApplicable; "not-applicable", None ]) e
                let! reason = optionalField "reason" nonEmptyString e
                match applicable, reason with
                | Some a, _ -> return a
                | None, Some r -> return TenetNotApplicable r
                | None, None -> return! Error "a tenet that is not applicable requires a reason"
            }

    let private declarationDecoder: Decoder<string * Map<ReliabilityTenet, TenetApplicability>> =
        fun e ->
            result {
                let! schema = field "schema" string e
                do! if schema = DeclarationSchema then Ok() else Error $"schema must be {DeclarationSchema}, found '{schema}'"
                let! systemClass = field "systemClass" nonEmptyString e
                let! ca = field "tenets" (field "continuousAvailability" tenetDecoder) e
                let! cr = field "tenets" (field "cacheReadiness" tenetDecoder) e
                let! rbc = field "tenets" (field "reliabilityByConstruction" tenetDecoder) e
                return systemClass, Map.ofList [ ContinuousAvailability, ca; CacheReadiness, cr; ReliabilityByConstruction, rbc ]
            }

    /// `None` is an absent file; text that cannot be decoded is an invalid
    /// declaration, never "not applicable".
    let readDeclaration (text: string option) =
        match text with
        | None -> DeclarationAbsent
        | Some content ->
            match parse declarationDecoder content with
            | Ok (systemClass, tenets) -> Declared(systemClass, tenets)
            | Error NullDocument -> DeclarationInvalid "declaration is JSON null"
            | Error (Malformed m) -> DeclarationInvalid("malformed JSON: " + m)
            | Error (Invalid m) -> DeclarationInvalid m

    let private applicabilityTag =
        function
        | TenetApplicable -> "applicable", None
        | TenetPartiallyApplicable -> "partially-applicable", None
        | TenetNotApplicable reason -> "not-applicable", Some reason

    let private declarationDto path (d: ReliabilityDeclaration) : ArchitectureDeclarationDto =
        match d with
        | DeclarationAbsent -> { Path = path; State = "absent"; Reason = None; SystemClass = None; Tenets = [] }
        | DeclarationInvalid reason -> { Path = path; State = "invalid"; Reason = Some reason; SystemClass = None; Tenets = [] }
        | Declared (systemClass, tenets) ->
            { Path = path
              State = "declared"
              Reason = None
              SystemClass = Some systemClass
              Tenets =
                tenets
                |> Map.toList
                |> List.map (fun (t, a) ->
                    let tag, reason = applicabilityTag a
                    { Tenet = ReliabilityArchitecture.tenetName t; Applicability = tag; Reason = reason }) }

    let private stateOf =
        function
        | RuleNotApplicable r -> "not-applicable", Some r
        | RuleNotConfigured r -> "not-configured", Some r
        | RuleUnavailable r -> "unavailable", Some r
        | RuleEvaluated _ -> "evaluated", None

    let private findingsOf =
        function
        | RuleEvaluated f -> f
        | _ -> []

    let private isViolation (f: ArchitectureFinding) = f.Disposition = Violation

    let private findingDto (f: ArchitectureFinding) : ArchitectureFindingDto =
        let rule = QualityRules.tryFind f.RuleId
        let exceptionId, decisions =
            match f.Disposition with
            | Accepted (id, records) -> Some id, records
            | Violation -> None, []
        { RuleId = f.RuleId
          RuleName = rule |> Option.map _.Name |> Option.defaultValue f.RuleId
          Scope = f.Scope
          Kind = (if isViolation f then "violation" else "accepted")
          File = f.File
          Line = f.Line
          Subject = f.Subject
          Detail = f.Detail
          Description = rule |> Option.map _.Description |> Option.defaultValue ""
          Remediation = rule |> Option.map _.Remediation |> Option.defaultValue ""
          ExceptionProcess = rule |> Option.map QualityRules.exceptionProcess |> Option.defaultValue ""
          ExceptionId = exceptionId
          DecisionRecords = decisions }

    let private ruleDto (id, outcome) : ArchitectureRuleStateDto =
        let state, reason = stateOf outcome
        let findings = findingsOf outcome
        { RuleId = id
          Name = QualityRules.tryFind id |> Option.map _.Name |> Option.defaultValue id
          Tenet = ReliabilityArchitecture.tenetOf id |> Option.map ReliabilityArchitecture.tenetName |> Option.defaultValue ""
          State = state
          Reason = reason
          Violations = findings |> List.filter isViolation |> List.length
          Accepted = findings |> List.filter (isViolation >> not) |> List.length }

    let report declarationPath exceptionsPath (exceptionsDigest: string option) (c: ArchitectureCheck) : ArchitectureReportDto =
        let findings = c.Rules |> List.collect (snd >> findingsOf)
        let count state = c.Rules |> List.filter (fun (_, o) -> fst (stateOf o) = state) |> List.length
        { Contract = ReportContract
          SchemaVersion = ReportSchemaVersion
          DokimosVersion = DokimosInfo.version
          CheckedAt = c.CheckedAt
          Verdict = ReliabilityArchitecture.verdictTag c.Verdict
          ExitCode = ReliabilityArchitecture.exitCode c.Verdict
          Reasons = c.Reasons
          Declaration = declarationDto declarationPath c.Declaration
          Exceptions =
            { Path = exceptionsPath
              Digest = exceptionsDigest
              Active = c.ActiveExceptions |> List.map RatchetContract.exceptionDto
              Expired = c.ExpiredExceptions |> List.map RatchetContract.exceptionDto
              Invalid = c.InvalidExceptions |> List.map (fun i -> { Id = i.Id; Problems = i.Problems })
              Unused = c.UnusedExceptions }
          Summary =
            { Violations = findings |> List.filter isViolation |> List.length
              Accepted = findings |> List.filter (isViolation >> not) |> List.length
              RulesEvaluated = count "evaluated"
              RulesNotApplicable = count "not-applicable"
              RulesNotConfigured = count "not-configured"
              RulesUnavailable = count "unavailable"
              ProjectFilesAnalyzed = c.ProjectFiles.Length }
          Rules = c.Rules |> List.map ruleDto
          Findings = findings |> List.map findingDto
          ProjectFiles = c.ProjectFiles
          Limitations = limitations }
