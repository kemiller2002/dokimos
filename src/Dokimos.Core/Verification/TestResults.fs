namespace Dokimos.Core.Verification

open System
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq
open Dokimos.Core

type Outcome =
    | Passed
    | Failed of detail: string
    | Skipped
    | NotExecuted

type ResultFormat =
    | Trx
    | PlaywrightJson
    | PlaywrightList

/// One test run named by the results manifest: which suite it ran, at which source SHA.
type ResultRun =
    { Suite: string
      Format: ResultFormat
      Path: string
      SourceSha: string
      RunId: string }

type ResultsManifest =
    { Candidate: string
      Runs: ResultRun list }

/// Test outcomes keyed by rendered identity. Parsers are pure (text in, map out).
module TestResults =
    let private outcomeOfTrx (text: string) (message: string) =
        match text.ToLowerInvariant() with
        | "passed" -> Passed
        | "failed" | "error" | "timeout" | "aborted" -> Failed message
        | "notexecuted" | "inconclusive" | "pending" | "disconnected" | "warning" -> NotExecuted
        | _ -> Skipped

    /// TRX: identity `dotnet:<TestMethod className>.<TestMethod name>`.
    let parseTrx (xml: string) : Result<(string * Outcome) list, string> =
        try
            let doc = XDocument.Parse xml
            let local (e: XElement) = e.Name.LocalName
            let attr (name: string) (e: XElement) = match e.Attribute(XName.Get name) with | null -> "" | a -> a.Value

            let definitions =
                doc.Descendants()
                |> Seq.filter (fun e -> local e = "UnitTest")
                |> Seq.choose (fun e ->
                    e.Elements()
                    |> Seq.tryFind (fun c -> local c = "TestMethod")
                    |> Option.map (fun m -> attr "id" e, $"""dotnet:{attr "className" m}.{attr "name" m}"""))
                |> Map.ofSeq

            doc.Descendants()
            |> Seq.filter (fun e -> local e = "UnitTestResult")
            |> Seq.map (fun e ->
                let identity =
                    definitions |> Map.tryFind (attr "testId" e) |> Option.defaultValue ("dotnet:" + attr "testName" e)
                let message =
                    e.Descendants() |> Seq.tryFind (fun d -> local d = "Message") |> Option.map _.Value |> Option.defaultValue ""
                identity, outcomeOfTrx (attr "outcome" e) (message.Substring(0, min 300 message.Length)))
            |> List.ofSeq
            |> Ok
        with :? Xml.XmlException as ex ->
            Error $"malformed TRX: {ex.Message}"

    /// Playwright JSON reporter. A flaky test (failed, then passed on retry) is a failure.
    let parsePlaywrightJson (text: string) : Result<(string * Outcome) list, string> =
        let prop (name: string) (e: JsonElement) =
            match e.ValueKind with
            | JsonValueKind.Object ->
                match e.TryGetProperty name with
                | true, v -> Some v
                | _ -> None
            | _ -> None
        let str name e = prop name e |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String) |> Option.map (fun v -> v.GetString() |> nonNull) |> Option.defaultValue ""
        let arr name e = prop name e |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Array) |> Option.map (fun v -> v.EnumerateArray() |> List.ofSeq) |> Option.defaultValue []

        let rec suite (file: string) (titles: string list) (s: JsonElement) =
            let specs =
                arr "specs" s
                |> List.collect (fun spec ->
                    arr "tests" spec
                    |> List.map (fun t ->
                        let titlePath = titles @ [ str "title" spec ]
                        let identity = $"""playwright:{file} > {String.Join(" > ", titlePath)} [{str "projectName" t}]"""
                        let outcome =
                            match str "status" t with
                            | "expected" -> Passed
                            | "flaky" -> Failed "flaky: failed before passing on retry"
                            | "unexpected" -> Failed "unexpected result"
                            | "skipped" -> Skipped
                            | other -> Failed other
                        identity, outcome))
            specs @ (arr "suites" s |> List.collect (fun child -> suite file (titles @ [ str "title" child ]) child))

        try
            use doc = JsonDocument.Parse text
            Ok(arr "suites" doc.RootElement |> List.collect (fun s -> suite (str "file" s) [] s))
        with :? JsonException as ex ->
            Error $"malformed Playwright JSON: {ex.Message}"

    let private listLine =
        Regex(@"\s(✓|✘|-|ok|x)\s+\d+\s+\[([\w-]+)\]\s+›\s+(\S+?):\d+:\d+\s+›\s+(.*?)(?:\s+\(\d+(?:\.\d+)?(?:ms|s|m|h)\))?\s*$", RegexOptions.Compiled)

    /// Playwright `list` reporter lines in a CI log (`testRoot` is stripped from file paths).
    /// A title seen with two different outcomes (a retry) counts as a failure.
    let parsePlaywrightList (testRoot: string) (log: string) : (string * Outcome) list =
        let prefix = testRoot.TrimEnd('/') + "/"
        log.Split('\n')
        |> Array.choose (fun raw ->
            let line = Regex.Replace(raw.TrimEnd('\r'), @"\x1b\[[0-9;]*m", "")
            let m = listLine.Match line
            if not m.Success then None
            else
                let file = m.Groups[3].Value
                let file = if file.StartsWith prefix then file.Substring prefix.Length else file
                let title = m.Groups[4].Value.Replace(" › ", " > ")
                let outcome =
                    match m.Groups[1].Value with
                    | "✓" | "ok" -> Passed
                    | "-" -> Skipped
                    | _ -> Failed "failed in list reporter"
                Some($"playwright:{file} > {title} [{m.Groups[2].Value}]", outcome))
        |> Array.groupBy fst
        |> Array.map (fun (identity, seen) ->
            let outcomes = seen |> Array.map snd |> Array.distinct
            identity, (if outcomes.Length = 1 then outcomes[0] else Failed "retried: outcomes differed"))
        |> List.ofArray

    [<Literal>]
    let ManifestSchema = "dokimos.verification-results/1"

    let parseManifest (text: string) : Result<ResultsManifest, string> =
        let format = Json.oneOf [ "trx", Trx; "playwright-json", PlaywrightJson; "playwright-list", PlaywrightList ]
        let run: Json.Decoder<ResultRun> =
            fun e ->
                Json.result {
                    let! suite = Json.field "suite" Json.nonEmptyString e
                    let! fmt = Json.field "format" format e
                    let! path = Json.field "path" Json.nonEmptyString e
                    let! sha = Json.field "sourceSha" Json.nonEmptyString e
                    let! runId = Json.fieldOr "runId" "" Json.string e
                    return { Suite = suite; Format = fmt; Path = path; SourceSha = sha; RunId = runId }
                }
        let root: Json.Decoder<ResultsManifest> =
            fun e ->
                Json.result {
                    let! schema = Json.field "schema" Json.string e
                    do! if schema = ManifestSchema then Ok() else Error $"schema must be {ManifestSchema}"
                    let! candidate = Json.field "candidate" Json.nonEmptyString e
                    let! runs = Json.field "runs" (Json.list run) e
                    return { Candidate = candidate; Runs = runs }
                }
        Json.parse root text
        |> Result.mapError (function
            | Json.Malformed m -> $"malformed JSON: {m}"
            | Json.NullDocument -> "null document"
            | Json.Invalid m -> m)
