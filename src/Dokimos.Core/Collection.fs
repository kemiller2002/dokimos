namespace Dokimos.Core

open System
open System.Text.RegularExpressions
open System.Xml.Linq
open Dokimos.Domain

/// Everything a snapshot is built from. Inputs are already-read contents;
/// reading files and Git stays at the CLI boundary.
type CollectionRequest =
    { Repository: string
      Revision: string
      Ref: string
      CollectedAt: DateTimeOffset
      AnalyzedScope: string list
      Sources: (string * string) list
      GitHistory: string option
      BuildLog: string option
      TestResults: string list option
      Coverage: string option }

/// Parsers for externally produced evidence. Each returns metrics or a typed
/// failure; none of them invents a zero when its input lacks the evidence.
module Collectors =
    let private repo = CanonicalSnapshot.RepositoryScope

    let private lastCount (pattern: string) (text: string) =
        Regex.Matches(text, pattern, RegexOptions.Multiline)
        |> Seq.tryLast
        |> Option.map (fun m -> decimal (int m.Groups[1].Value))

    let buildDiagnostics (log: string) =
        match lastCount @"^\s*(\d+) Warning\(s\)" log, lastCount @"^\s*(\d+) Error\(s\)" log with
        | Some warnings, Some errors ->
            Ok [ CanonicalMetric.available "build.compiler-errors" repo "count" errors Capabilities.DotnetBuild
                 CanonicalMetric.available "build.compiler-warnings" repo "count" warnings Capabilities.DotnetBuild ]
        | _ -> Error { Code = "build-summary-missing"; Message = "The build log has no MSBuild 'Warning(s)'/'Error(s)' summary." }

    let private trxCounters (xml: string) =
        try
            let doc = XDocument.Parse xml
            let counters = doc.Descendants() |> Seq.tryFind (fun e -> e.Name.LocalName = "Counters")
            match counters with
            | None -> Error "trx-counters-missing"
            | Some element ->
                let attr name =
                    match element.Attribute(XName.Get name) with
                    | null -> 0
                    | a -> int a.Value
                Ok(attr "total", attr "executed", attr "passed", attr "failed" + attr "error" + attr "timeout" + attr "aborted")
        with :? System.Xml.XmlException -> Error "trx-malformed"

    let testResults (trxFiles: string list) =
        match trxFiles with
        | [] -> Error { Code = "trx-none"; Message = "No TRX result files were supplied." }
        | files ->
            let parsed = files |> List.map trxCounters
            match parsed |> List.tryPick (function Error e -> Some e | Ok _ -> None) with
            | Some code -> Error { Code = code; Message = "A TRX result file could not be read." }
            | None ->
                let values = parsed |> List.choose (function Ok v -> Some v | Error _ -> None)
                let total = values |> List.sumBy (fun (t, _, _, _) -> t)
                let executed = values |> List.sumBy (fun (_, e, _, _) -> e)
                let passed = values |> List.sumBy (fun (_, _, p, _) -> p)
                let failed = values |> List.sumBy (fun (_, _, _, f) -> f)
                let m id (v: int) = CanonicalMetric.available id repo "count" (decimal v) Capabilities.DotnetTest
                Ok [ m "tests.total" total; m "tests.passed" passed; m "tests.failed" failed; m "tests.skipped" (total - executed) ]

    let coverage (xml: string) =
        try
            let doc = XDocument.Parse xml
            match doc.Root with
            | null -> Error { Code = "coverage-malformed"; Message = "Empty coverage document." }
            | root ->
                match root.Attribute(XName.Get "line-rate") with
                | null -> Error { Code = "coverage-line-rate-missing"; Message = "The Cobertura root has no line-rate." }
                | a ->
                    match Decimal.TryParse(a.Value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, v -> Ok [ CanonicalMetric.available "coverage.line-rate" repo "ratio" v Capabilities.Coverage ]
                    | _ -> Error { Code = "coverage-line-rate-invalid"; Message = $"Invalid line-rate '{a.Value}'." }
        with :? System.Xml.XmlException -> Error { Code = "coverage-malformed"; Message = "The coverage report is not valid XML." }

/// Measures a computation's wall-clock duration in milliseconds. Injected so
/// collection logic stays free of ambient clocks and is testable.
type Timer =
    abstract Time<'a> : (unit -> 'a) -> 'a * int64

module Timer =
    let stopwatch =
        { new Timer with
            member _.Time<'a>(work: unit -> 'a) =
                let watch = Diagnostics.Stopwatch.StartNew()
                let value = work ()
                value, watch.ElapsedMilliseconds }

    /// Deterministic timer for tests: every computation takes `ms`.
    let fixedDuration ms =
        { new Timer with
            member _.Time<'a>(work: unit -> 'a) = work (), ms }

module Collection =
    [<Literal>]
    let DuplicateBlockSize = 6

    let configurationId = $"dokimos-collection/1;duplicate-block-size={DuplicateBlockSize}"

    type private CollectorResult =
        { Run: AnalyzerRun
          Metrics: CanonicalMetric list }

    let private version analyzerId =
        Capabilities.tryAnalyzer analyzerId |> Option.map _.Version |> Option.defaultValue "unknown"

    let private run analyzerId state duration metrics =
        { Run = ({ AnalyzerId = analyzerId; Version = version analyzerId; State = state; DurationMilliseconds = duration }: AnalyzerRun)
          Metrics = metrics }

    /// An optional external input: not supplied -> not run and every declared
    /// metric unavailable; unreadable -> failed and every metric failed.
    let private optionalCollector (timer: Timer) analyzerId (input: 'input option) (parse: 'input -> Result<CanonicalMetric list, Failure>) =
        let declared =
            Capabilities.metrics |> List.filter (fun d -> d.Analyzer = Some analyzerId)
        match input with
        | None ->
            let reason = "input not supplied"
            run analyzerId (AnalyzerNotRun reason) None
                (declared |> List.map (fun d -> CanonicalMetric.unavailable d.MetricId CanonicalSnapshot.RepositoryScope d.Unit NotConfigured analyzerId))
        | Some value ->
            let outcome, ms = timer.Time(fun () -> parse value)
            match outcome with
            | Ok metrics -> run analyzerId AnalyzerRan (Some ms) metrics
            | Error failure ->
                run analyzerId (AnalyzerFailed failure) (Some ms)
                    (declared |> List.map (fun d -> CanonicalMetric.failed d.MetricId CanonicalSnapshot.RepositoryScope d.Unit failure analyzerId))

    let private collect (timer: Timer) (request: CollectionRequest) =
        let temporalResult, temporalMs =
            match request.GitHistory with
            | None -> None, None
            | Some text ->
                let value, ms = timer.Time(fun () -> text |> GitHistory.parse |> GitHistory.summarize)
                Some value, Some ms
        let temporal = defaultArg temporalResult Map.empty
        let analysis, analysisMs = timer.Time(fun () -> RepositoryAnalysis.analyze DuplicateBlockSize temporal request.Sources)
        let sourceMetrics = CanonicalSnapshot.sourceMetrics temporalResult.IsSome temporal analysis
        let structuralRuns =
            [ Capabilities.Structural; Capabilities.Complexity; Capabilities.AgentQuality; Capabilities.Duplication; Capabilities.Correlation ]
            |> List.map (fun id -> run id AnalyzerRan (if id = Capabilities.Structural then Some analysisMs else None) [])
        let temporalRun =
            match temporalMs with
            | Some ms -> run Capabilities.GitTemporal AnalyzerRan (Some ms) []
            | None -> run Capabilities.GitTemporal (AnalyzerNotRun "git history not supplied") None []
        let external =
            [ optionalCollector timer Capabilities.DotnetBuild request.BuildLog Collectors.buildDiagnostics
              optionalCollector timer Capabilities.DotnetTest request.TestResults Collectors.testResults
              optionalCollector timer Capabilities.Coverage request.Coverage Collectors.coverage ]
        let runs = structuralRuns @ [ temporalRun ] @ external
        let metrics =
            (external |> List.collect _.Metrics)
            @ [ CanonicalSnapshot.duplicationMetric analysis ]
            @ CanonicalSnapshot.unsupportedMetrics ()
            @ sourceMetrics
        let producer: SnapshotProducer =
            { DokimosVersion = DokimosInfo.version
              ConfigurationId = configurationId
              AnalyzedScope = request.AnalyzedScope
              Analyzers = runs |> List.map _.Run }
        let draft: CanonicalSnapshot =
            { SchemaVersion = CanonicalSnapshot.CurrentSchemaVersion
              SnapshotId = ""
              Repository = request.Repository
              Revision = request.Revision
              Ref = request.Ref
              CollectedAt = request.CollectedAt
              Collector = "dokimos/" + DokimosInfo.version
              Producer = Some producer
              Performance = None
              Metrics = metrics
              Findings = CanonicalSnapshot.findings analysis }
        draft, runs |> List.map _.Run

    /// Builds a schema-2.0.0 snapshot with producer provenance, a content
    /// derived identity, and the collection's own performance evidence.
    let snapshot (timer: Timer) (request: CollectionRequest) =
        let (draft, runs), totalMs = timer.Time(fun () -> collect timer request)
        let digest = Contracts.evidenceDigest draft
        let performance: CollectionPerformance =
            { TotalMilliseconds = totalMs
              FileCount = request.Sources.Length
              ObservationCount = draft.Metrics.Length
              UnavailableCollectors = runs |> List.choose (fun r -> match r.State with AnalyzerNotRun _ -> Some r.AnalyzerId | _ -> None)
              FailedCollectors = runs |> List.choose (fun r -> match r.State with AnalyzerFailed _ -> Some r.AnalyzerId | _ -> None) }
        { draft with
            SnapshotId = CanonicalSnapshot.identity request.Repository request.Revision digest
            Performance = Some performance }
