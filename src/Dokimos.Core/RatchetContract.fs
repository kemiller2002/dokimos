namespace Dokimos.Core

open System
open System.Globalization
open System.Text
open System.Text.Json
open Dokimos.Core.Json

module RatchetContract =
    [<Literal>]
    let ReportContract = "dokimos.ratchet"

    [<Literal>]
    let ReportSchemaVersion = "1.0.0"

    [<Literal>]
    let BaselineContract = "dokimos.ratchet-baseline"

    [<Literal>]
    let ExceptionsContract = "dokimos.quality-exceptions"

    [<Literal>]
    let ExceptionsSchemaVersion = "1.0.0"

    let private fileOptions =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    let private compactFileOptions =
        JsonSerializerOptions(
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    let private dateText (d: DateOnly) = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let private timestampText (t: DateTimeOffset) =
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)

    // --- baseline file -------------------------------------------------------

    let baselineFileDto (b: RatchetBaseline) : RatchetBaselineFileDto =
        { Schema = "../schemas/dokimos-ratchet-baseline.schema.json"
          Contract = BaselineContract
          SchemaVersion = b.SchemaVersion
          Repository = b.Repository
          AcceptedAt = timestampText b.AcceptedAt
          Configuration =
            { Sources = b.Configuration.Sources
              Generated = b.Configuration.Generated
              LargeFileLines = b.Configuration.LargeFileLines
              ForbiddenReferences = b.Configuration.ForbiddenReferences |> List.map (fun r -> { From = r.From; To = r.To }) }
          Rules = b.Rules }

    let writeBaseline (b: RatchetBaseline) = JsonSerializer.Serialize(baselineFileDto b, fileOptions) + "\n"

    /// Content digest of the accepted baseline (canonical form, so
    /// formatting changes do not change it).
    let baselineDigest (b: RatchetBaseline) =
        "sha256:" + Contracts.sha256 (JsonSerializer.Serialize(baselineFileDto b, compactFileOptions))

    let private forbiddenDecoder: Decoder<ForbiddenReference> =
        fun e ->
            result {
                let! from = field "from" nonEmptyString e
                let! into = field "to" nonEmptyString e
                return { From = from; To = into }
            }

    let private configurationDecoder: Decoder<RatchetConfiguration> =
        fun e ->
            result {
                let! sources = field "sources" (list nonEmptyString) e
                let! generated = field "generated" (list nonEmptyString) e
                let! large = field "largeFileLines" int e
                do! if large > 0 then Ok() else Error "largeFileLines must be positive"
                let! forbidden = fieldOr "forbiddenReferences" [] (list forbiddenDecoder) e
                return { Sources = sources; Generated = generated; LargeFileLines = large; ForbiddenReferences = forbidden }
            }

    let private scopeValues: Decoder<Map<string, decimal>> =
        fun e ->
            if e.ValueKind <> JsonValueKind.Object then Error $"expected object, found {e.ValueKind}"
            else
                e.EnumerateObject()
                |> Seq.map (fun p -> decimal p.Value |> Result.map (fun v -> p.Name, v) |> Result.mapError (fun m -> $"{p.Name}: {m}"))
                |> Seq.fold (fun acc r -> match acc, r with | Ok xs, Ok x -> Ok(x :: xs) | Error m, _ | _, Error m -> Error m) (Ok [])
                |> Result.bind (fun xs ->
                    if xs |> List.exists (fun (_, v) -> v < 0m) then Error "accepted values must not be negative"
                    else Ok(Map.ofList xs))

    let private rulesDecoder: Decoder<Map<string, Map<string, decimal>>> =
        fun e ->
            if e.ValueKind <> JsonValueKind.Object then Error $"expected object, found {e.ValueKind}"
            else
                e.EnumerateObject()
                |> Seq.map (fun p -> scopeValues p.Value |> Result.map (fun v -> p.Name, v) |> Result.mapError (fun m -> $"{p.Name}: {m}"))
                |> Seq.fold (fun acc r -> match acc, r with | Ok xs, Ok x -> Ok(x :: xs) | Error m, _ | _, Error m -> Error m) (Ok [])
                |> Result.map Map.ofList

    let private baselineDecoder: Decoder<RatchetBaseline> =
        fun e ->
            result {
                let! contract = field "contract" string e
                do! if contract = BaselineContract then Ok() else Error $"contract must be {BaselineContract}"
                let! schema = field "schemaVersion" string e
                do! if schema = QualityRatchet.BaselineSchemaVersion then Ok() else Error("unsupported-baseline-schema:" + schema)
                let! repository = field "repository" nonEmptyString e
                let! acceptedAt = field "acceptedAt" dateTimeOffset e
                let! configuration = field "configuration" configurationDecoder e
                let! rules = field "rules" rulesDecoder e
                return
                    { SchemaVersion = schema
                      Repository = repository
                      AcceptedAt = acceptedAt
                      Configuration = configuration
                      Rules = rules }
            }

    let readBaseline (text: string) =
        match parse baselineDecoder text with
        | Ok b -> Ok b
        | Error NullDocument -> Error "baseline is JSON null"
        | Error (Malformed m) -> Error("malformed baseline JSON: " + m)
        | Error (Invalid m) -> Error("invalid baseline: " + m)

    // --- exceptions file -----------------------------------------------------

    let private date: Decoder<DateOnly> =
        string
        >> Result.bind (fun text ->
            match DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, d -> Ok d
            | _ -> Error $"expected a yyyy-MM-dd date, found '{text}'")

    /// Decodes one exception collecting every problem, so a reviewer sees all
    /// missing fields at once.
    let private exceptionEntry index (e: JsonElement) : Result<QualityException, InvalidException> =
        let problemsOf (r: Result<'a, string>) = match r with Error m -> [ m ] | Ok _ -> []
        let id = field "id" nonEmptyString e
        let ruleId = field "ruleId" nonEmptyString e
        let scope = field "scope" nonEmptyString e
        let rationale = field "rationale" nonEmptyString e
        let owner = field "owner" nonEmptyString e
        let created = field "created" date e
        let expires = optionalField "expires" date e
        let reviewCondition = optionalField "reviewCondition" nonEmptyString e
        let evidence = field "evidence" (list string) e
        let allowed = optionalField "allowedValue" decimal e
        let review =
            match expires, reviewCondition with
            | Ok (Some d), Ok (Some c) -> Ok(ExpiresOrReview(d, c))
            | Ok (Some d), Ok None -> Ok(Expires d)
            | Ok None, Ok (Some c) -> Ok(ReviewCondition c)
            | Ok None, Ok None -> Error "either expires or reviewCondition is required"
            | Error m, _
            | _, Error m -> Error m
        let problems =
            List.concat
                [ problemsOf id
                  problemsOf ruleId
                  problemsOf scope
                  problemsOf rationale
                  problemsOf owner
                  problemsOf created
                  problemsOf review
                  problemsOf evidence
                  problemsOf allowed ]
        match id, ruleId, scope, rationale, owner, created, review, evidence, allowed with
        | Ok id, Ok ruleId, Ok scope, Ok rationale, Ok owner, Ok created, Ok review, Ok evidence, Ok allowed ->
            Ok
                { Id = id
                  RuleId = ruleId
                  Scope = scope
                  Rationale = rationale
                  Owner = owner
                  Created = created
                  Review = review
                  Evidence = evidence
                  AllowedValue = allowed }
        | _ ->
            let label = match id with Ok text -> text | Error _ -> $"#{index}"
            Error { Id = label; Problems = problems }

    let private exceptionsDecoder: Decoder<ExceptionSet> =
        fun e ->
            result {
                let! contract = field "contract" string e
                do! if contract = ExceptionsContract then Ok() else Error $"contract must be {ExceptionsContract}"
                let! schema = field "schemaVersion" string e
                do! if schema = ExceptionsSchemaVersion then Ok() else Error("unsupported-exceptions-schema:" + schema)
                let! items = field "exceptions" (fun a -> if a.ValueKind = JsonValueKind.Array then Ok(List.ofSeq (a.EnumerateArray())) else Error "expected array") e
                let entries = items |> List.mapi exceptionEntry
                return
                    { Valid = entries |> List.choose (function Ok x -> Some x | Error _ -> None)
                      Invalid = entries |> List.choose (function Error x -> Some x | Ok _ -> None) }
            }

    /// A file that cannot be read as an exceptions document is itself an
    /// invalid exception set, never "no exceptions".
    let readExceptions (text: string) =
        match parse exceptionsDecoder text with
        | Ok set -> set
        | Error failure ->
            let message =
                match failure with
                | NullDocument -> "exceptions file is JSON null"
                | Malformed m -> "malformed exceptions JSON: " + m
                | Invalid m -> "invalid exceptions file: " + m
            { Valid = []; Invalid = [ { Id = "(file)"; Problems = [ message ] } ] }

    let emptyExceptions = { Valid = []; Invalid = [] }

    // --- output DTOs ---------------------------------------------------------

    let exceptionDto (e: QualityException) : RatchetExceptionDto =
        { Id = e.Id
          RuleId = e.RuleId
          Scope = e.Scope
          Rationale = e.Rationale
          Owner = e.Owner
          Created = dateText e.Created
          Expires = QualityRatchet.expiryOf e |> Option.map dateText
          ReviewCondition = (match e.Review with ReviewCondition c | ExpiresOrReview (_, c) -> Some c | Expires _ -> None)
          Evidence = e.Evidence
          AllowedValue = e.AllowedValue }

    let private kindTag =
        function
        | Regression -> "regression"
        | ExceptedRegression -> "excepted-regression"
        | Improvement -> "improvement"

    let findingDto (f: RatchetFinding) : RatchetFindingDto =
        let rule = QualityRules.tryFind f.RuleId
        { RuleId = f.RuleId
          RuleName = rule |> Option.map _.Name |> Option.defaultValue f.RuleId
          Scope = f.Scope
          Kind = kindTag f.Kind
          Degraded = f.Degraded
          Before = f.Before
          After = f.After
          BaselineRecorded = f.BaselineRecorded
          Description = rule |> Option.map _.Description |> Option.defaultValue ""
          Remediation = rule |> Option.map _.Remediation |> Option.defaultValue ""
          ExceptionProcess = rule |> Option.map QualityRules.exceptionProcess |> Option.defaultValue ""
          ExceptionId = f.ExceptionId
          Evidence = f.Evidence }

    let private ruleStateDto (id, state) : RatchetRuleStateDto =
        let name = QualityRules.tryFind id |> Option.map _.Name |> Option.defaultValue id
        match state with
        | RuleMeasured -> { RuleId = id; Name = name; State = "measured"; Reason = None }
        | RuleNotMeasured reason -> { RuleId = id; Name = name; State = "unavailable"; Reason = Some reason }
        | RuleNotEnforced -> { RuleId = id; Name = name; State = "not-enforced"; Reason = Some "the accepted baseline does not enforce this rule" }

    let report baselinePath exceptionsPath (exceptionsDigest: string option) (c: RatchetCheck) : RatchetReportDto =
        let count kind = c.Findings |> List.filter (fun f -> f.Kind = kind) |> List.length
        let states kind = c.RuleStates |> List.filter (snd >> kind) |> List.length
        { Contract = ReportContract
          SchemaVersion = ReportSchemaVersion
          DokimosVersion = DokimosInfo.version
          CheckedAt = c.CheckedAt
          Repository = Some c.Baseline.Repository
          Verdict = QualityRatchet.verdictTag c.Verdict
          ExitCode = QualityRatchet.exitCode c.Verdict
          Reasons = c.Reasons
          Baseline =
            { Path = baselinePath
              Digest = Some(baselineDigest c.Baseline)
              SchemaVersion = Some c.Baseline.SchemaVersion
              AcceptedAt = Some(timestampText c.Baseline.AcceptedAt) }
          Exceptions =
            { Path = exceptionsPath
              Digest = exceptionsDigest
              Active = c.ActiveExceptions |> List.map exceptionDto
              Expired = c.ExpiredExceptions |> List.map exceptionDto
              Invalid = c.InvalidExceptions |> List.map (fun i -> { Id = i.Id; Problems = i.Problems })
              Unused = c.UnusedExceptions }
          Summary =
            { Regressions = count Regression
              Excepted = count ExceptedRegression
              Improvements = count Improvement
              RulesMeasured = states (fun s -> s = RuleMeasured)
              RulesUnavailable = states (function RuleNotMeasured _ -> true | _ -> false)
              RulesNotEnforced = states (fun s -> s = RuleNotEnforced)
              FilesAnalyzed = c.Measurement.AnalyzedFiles.Length
              GeneratedFilesExcluded = c.Measurement.GeneratedFiles.Length }
          Rules = c.RuleStates |> List.map ruleStateDto
          Generated = { Globs = c.Baseline.Configuration.Generated; Files = c.Measurement.GeneratedFiles }
          Findings = c.Findings |> List.map findingDto }

    /// The report when the check could not run at all (baseline missing or
    /// unreadable). Always verdict "unavailable", never a pass.
    let unavailableReport (asOf: DateTimeOffset) baselinePath exceptionsPath (reasons: string list) : RatchetReportDto =
        { Contract = ReportContract
          SchemaVersion = ReportSchemaVersion
          DokimosVersion = DokimosInfo.version
          CheckedAt = asOf
          Repository = None
          Verdict = QualityRatchet.verdictTag RatchetUnavailable
          ExitCode = QualityRatchet.exitCode RatchetUnavailable
          Reasons = reasons
          Baseline = { Path = baselinePath; Digest = None; SchemaVersion = None; AcceptedAt = None }
          Exceptions = { Path = exceptionsPath; Digest = None; Active = []; Expired = []; Invalid = []; Unused = [] }
          Summary =
            { Regressions = 0
              Excepted = 0
              Improvements = 0
              RulesMeasured = 0
              RulesUnavailable = 0
              RulesNotEnforced = 0
              FilesAnalyzed = 0
              GeneratedFilesExcluded = 0 }
          Rules = []
          Generated = { Globs = []; Files = [] }
          Findings = [] }

    let updateDto operation written path (digestBefore: string option) (update: BaselineUpdate) : BaselineUpdateDto =
        { Contract = "dokimos.ratchet-baseline-update"
          SchemaVersion = "1.0.0"
          DokimosVersion = DokimosInfo.version
          Operation = operation
          Written = written
          Path = path
          DigestBefore = digestBefore
          DigestAfter = baselineDigest update.Updated
          Changes =
            update.Changes
            |> List.map (fun c ->
                { RuleId = c.RuleId
                  Scope = c.Scope
                  Change = (match c.Kind with ScopeTightened -> "tightened" | ScopeRemoved -> "removed")
                  Before = c.Before
                  After = c.After })
          Refused =
            update.Refused
            |> List.map (fun r -> { RuleId = r.RuleId; Scope = r.Scope; Accepted = r.Accepted; Current = r.Current; Reason = r.Reason })
          Baseline = baselineFileDto update.Updated }

    let diffDto (older: RatchetBaseline) (newer: RatchetBaseline) (d: BaselineDiff) : BaselineDiffDto =
        let delta (x: BaselineDelta) : BaselineDeltaDto = { RuleId = x.RuleId; Scope = x.Scope; From = x.From; To = x.To }
        let loosened = QualityRatchet.isLoosening d
        { Contract = "dokimos.ratchet-baseline-diff"
          SchemaVersion = "1.0.0"
          DokimosVersion = DokimosInfo.version
          Verdict = (if loosened then "loosened" else "tightened-or-unchanged")
          ExitCode = (if loosened then ExitCodes.PolicyFailure else ExitCodes.Continue)
          FromDigest = baselineDigest older
          ToDigest = baselineDigest newer
          Loosened = d.Loosened |> List.map delta
          Tightened = d.Tightened |> List.map delta
          ConfigurationLoosened = d.ConfigurationLoosened
          ConfigurationTightened = d.ConfigurationTightened }

    let catalogDto () : RatchetRuleCatalogDto =
        { Contract = "dokimos.ratchet-rules"
          SchemaVersion = "1.0.0"
          DokimosVersion = DokimosInfo.version
          Rules =
            QualityRules.all
            |> List.map (fun r ->
                { RuleId = r.Id
                  Name = r.Name
                  Engine = (match r.Engine with RatchetEngine _ -> "ratchet" | GateEngine -> "gate")
                  Semantics =
                    (match r.Engine with
                     | RatchetEngine CountMustNotGrow -> Some "count-must-not-grow"
                     | RatchetEngine SizeOverThresholdMustNotGrow -> Some "size-over-threshold-must-not-grow"
                     | GateEngine -> None)
                  Scope = (match r.ScopeKind with RepositoryScoped -> "repository" | FileScoped -> "file")
                  Signal = r.Signal
                  Description = r.Description
                  Rationale = r.Rationale
                  Remediation = r.Remediation
                  ExceptionProcess = QualityRules.exceptionProcess r }) }
