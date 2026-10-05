namespace Dokimos.Core

open System
open System.Text.RegularExpressions

/// A repository file handed to the ratchet. Path is repository-relative
/// with '/' separators.
type SourceFile = { Path: string; Content: string }

type ForbiddenReference = { From: string; To: string }

/// What the ratchet measures and how. Part of the accepted baseline, so a
/// change to it is reviewed like a change to the baseline values.
type RatchetConfiguration =
    { Sources: string list
      /// Generated files are declared explicitly; nothing is inferred.
      Generated: string list
      LargeFileLines: int
      ForbiddenReferences: ForbiddenReference list }

type ScopeMeasure = { Value: decimal; Evidence: string list }

type RuleMeasurement =
    | Measured of Map<string, ScopeMeasure>
    | NotMeasured of reason: string

type MeasurementInputs =
    { Files: SourceFile list
      BuildLog: string option }

type QualityMeasurement =
    { Rules: Map<string, RuleMeasurement>
      AnalyzedFiles: string list
      GeneratedFiles: string list }

/// Independent, line-based quality signals for F# sources and MSBuild
/// project files. Each rule is measured on its own; none is derived from
/// another, and none is reported as zero when it could not be measured.
module QualitySignals =
    [<Literal>]
    let RepositoryScope = "repository"

    [<Literal>]
    let private MaxEvidence = 20

    let isFSharpSource (path: string) = path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)

    let isProjectFile (path: string) =
        let name = path.Split('/') |> Array.last
        path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || name = "Directory.Build.props"
        || name = "Directory.Packages.props"

    let private clip (text: string) =
        let t = text.Trim()
        if t.Length > 120 then t.Substring(0, 117) + "..." else t

    let private evidenceOf path (lines: LexedLine list) =
        lines |> List.truncate MaxEvidence |> List.map (fun l -> $"{path}:{l.Number}: {clip l.Original}")

    let private regex (pattern: string) = Regex(pattern, RegexOptions.CultureInvariant)

    let private codeMatches (patterns: Regex list) (lexed: LexedLine list) =
        lexed |> List.filter (fun l -> patterns |> List.exists (fun p -> p.IsMatch l.Code))

    // --- F# source signals ---------------------------------------------------

    let private suppressionPatterns =
        [ regex @"^\s*#nowarn\b"; regex @"^\s*#pragma\s+warning\s+disable\b"; regex @"\bSuppressMessage(Attribute)?\s*\(" ]

    let private skipPatterns =
        [ regex @"\bSkip\s*=\s*"""
          regex @"\[<\s*(Ignore|Explicit)\b"
          regex @"\bptest(Case|List|Property|Async|Task|CaseAsync|CaseTask)?\b"
          regex @"\bSkip\.(If|IfNot)\b"
          regex @"\bAssert\.Skip\b" ]

    let private markerPattern = regex @"\b(TODO|FIXME|HACK)\b"

    let suppressionLines lexed = codeMatches suppressionPatterns lexed

    let skippedTestLines lexed = codeMatches skipPatterns lexed

    let debtMarkerLines (lexed: LexedLine list) = lexed |> List.filter (fun l -> markerPattern.IsMatch l.Comment)

    // A handler pattern that catches every exception, optionally binding it.
    let private catchAll =
        @"(?<pat>_|(?<id>[a-z_][A-Za-z0-9_']*)|:\?\s*(?:System\.)?(?:Exception|exn)(?:\s+as\s+(?<as>[A-Za-z_][A-Za-z0-9_']*))?)\s*->(?<body>.*)$"

    let private sameLineHandler = regex (@"\bwith\s+" + catchAll)
    let private caseHandler = regex (@"^\s*\|\s*" + catchAll)
    let private matchOrFunction = regex @"\b(match|function)\b"
    let private indentOf (line: string) = line.Length - line.TrimStart().Length

    let private nextCodeLine (lexed: LexedLine list) (number: int) =
        lexed |> List.tryFind (fun l -> l.Number > number && not (String.IsNullOrWhiteSpace l.Code))

    /// True when the handler on `line` catches everything and discards it.
    let private discards (lexed: LexedLine list) (line: LexedLine) (m: Match) =
        let bound =
            if m.Groups["as"].Success then Some m.Groups["as"].Value
            elif m.Groups["id"].Success then Some m.Groups["id"].Value
            else None
        match bound with
        | None -> true
        | Some name ->
            let body =
                if String.IsNullOrWhiteSpace m.Groups["body"].Value then
                    nextCodeLine lexed line.Number |> Option.map _.Code |> Option.defaultValue ""
                else m.Groups["body"].Value
            not (Regex.IsMatch(body, @"(?<![A-Za-z0-9_'])" + Regex.Escape name + @"(?![A-Za-z0-9_'])"))

    let broadCatchLines (lexed: LexedLine list) =
        let sameLine =
            lexed
            |> List.filter (fun l ->
                let m = sameLineHandler.Match l.Code
                m.Success
                && not (matchOrFunction.IsMatch(l.Code.Substring(0, m.Index)))
                && discards lexed l m)
        let afterStandaloneWith =
            lexed
            |> List.filter (fun l -> l.Code.Trim() = "with")
            |> List.collect (fun withLine ->
                let following = lexed |> List.filter (fun l -> l.Number > withLine.Number && not (String.IsNullOrWhiteSpace l.Code))
                match following with
                | [] -> []
                | first :: _ ->
                    let caseIndent = indentOf first.Code
                    following
                    |> List.takeWhile (fun l -> indentOf l.Code >= caseIndent)
                    |> List.filter (fun l -> indentOf l.Code = caseIndent)
                    |> List.filter (fun l ->
                        let m = caseHandler.Match l.Code
                        m.Success && discards lexed l m))
        sameLine @ afterStandaloneWith |> List.distinctBy _.Number |> List.sortBy _.Number

    // --- project file signals -----------------------------------------------

    let private projectLines (content: string) =
        SourceLexing.lines content |> Array.mapi (fun i l -> { Number = i + 1; Original = l; Code = l; Comment = "" }) |> List.ofArray

    let private projectSuppression =
        [ regex @"<NoWarn>\s*(?!\$\(NoWarn\)\s*</NoWarn>)[^<\s]"
          regex @"<WarningsNotAsErrors>\s*[^<\s]"
          regex @"<TreatWarningsAsErrors>\s*false\s*<"
          regex @"\bNoWarn\s*=\s*""[^""]+""" ]

    let projectSuppressionLines content = codeMatches projectSuppression (projectLines content)

    let private packagePattern = regex @"<PackageReference\s+[^>]*\bInclude\s*=\s*""(?<name>[^""]+)""(?:[^>]*\bVersion\s*=\s*""(?<version>[^""]+)"")?"
    let private projectRefPattern = regex @"<ProjectReference\s+[^>]*\bInclude\s*=\s*""(?<path>[^""]+)"""

    let private projectName (path: string) =
        let name = path.Replace('\\', '/').Split('/') |> Array.last
        match name.LastIndexOf '.' with
        | -1 -> name
        | dot -> name.Substring(0, dot)

    let packageReferenceLines content =
        projectLines content |> List.filter (fun l -> packagePattern.IsMatch l.Code)

    /// Architecture evidence (project and package references) from project
    /// files, feeding Architecture.boundaryViolations.
    let architectureEvidence (projects: SourceFile list) : ArchitectureEvidence =
        let fsprojs = projects |> List.filter (fun p -> p.Path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
        { ProjectReferences =
            [ for p in fsprojs do
                  for m in projectRefPattern.Matches p.Content do
                      yield { FromProject = projectName p.Path; ToProject = projectName m.Groups["path"].Value } ]
          PackageReferences =
            [ for p in projects do
                  for m in packagePattern.Matches p.Content do
                      yield
                          { Project = projectName p.Path
                            Package = m.Groups["name"].Value
                            Version = if m.Groups["version"].Success then Some m.Groups["version"].Value else None } ] }

    // --- measurement ---------------------------------------------------------

    let private perFile (files: (string * LexedLine list) list) (select: LexedLine list -> LexedLine list) =
        files
        |> List.choose (fun (path, lexed) ->
            match select lexed with
            | [] -> None
            | hits -> Some(path, { Value = decimal hits.Length; Evidence = evidenceOf path hits }))
        |> Map.ofList

    let private compilerWarnings buildLog =
        match buildLog with
        | None -> NotMeasured "no build log supplied (--build-log); compiler warnings were not measured"
        | Some log ->
            match Collectors.buildDiagnostics log with
            | Error failure -> NotMeasured $"{failure.Code}: {failure.Message}"
            | Ok metrics ->
                match metrics |> List.tryFind (fun m -> m.MetricId = "build.compiler-warnings") |> Option.bind CanonicalMetric.value with
                | Some warnings -> Measured(Map.ofList [ RepositoryScope, { Value = warnings; Evidence = [ $"build log summary: {warnings} Warning(s)" ] } ])
                | None -> NotMeasured "the build log summary has no warning count"

    let measure (configuration: RatchetConfiguration) (inputs: MeasurementInputs) : QualityMeasurement =
        let generated, handwritten = inputs.Files |> List.partition (fun f -> Glob.anyMatch configuration.Generated f.Path)
        let sources = handwritten |> List.filter (fun f -> isFSharpSource f.Path) |> List.sortBy _.Path
        let projects = handwritten |> List.filter (fun f -> isProjectFile f.Path) |> List.sortBy _.Path
        let lexed = sources |> List.map (fun f -> f.Path, SourceLexing.lex f.Content)
        let projectSuppressions =
            projects
            |> List.choose (fun p ->
                match projectSuppressionLines p.Content with
                | [] -> None
                | hits -> Some(p.Path, { Value = decimal hits.Length; Evidence = evidenceOf p.Path hits }))
        // Every handwritten source's line count is measured; only files over
        // the threshold are recorded in a baseline or reported.
        let largeFiles =
            sources
            |> List.map (fun f ->
                let count = SourceLexing.lines f.Content |> Array.length
                let evidence = if count > configuration.LargeFileLines then [ $"{f.Path}: {count} lines (threshold {configuration.LargeFileLines})" ] else []
                f.Path, { Value = decimal count; Evidence = evidence })
            |> Map.ofList
        let packages =
            projects
            |> List.choose (fun p ->
                match packageReferenceLines p.Content with
                | [] -> None
                | hits -> Some(p.Path, { Value = decimal hits.Length; Evidence = evidenceOf p.Path hits }))
            |> Map.ofList
        let architecture =
            let rules = configuration.ForbiddenReferences |> List.map (fun r -> ({ FromProject = r.From; ForbiddenTarget = r.To }: BoundaryRule))
            let violations = Architecture.boundaryViolations rules (architectureEvidence projects)
            Map.ofList
                [ RepositoryScope,
                  { Value = decimal violations.Length
                    Evidence = violations |> List.map (fun v -> $"{v.FromProject} -> {v.ForbiddenTarget} is forbidden by the declared layer map") } ]
        let suppressions =
            Seq.append (perFile lexed suppressionLines |> Map.toSeq) projectSuppressions |> Map.ofSeq
        { Rules =
            Map.ofList
                [ QualityRules.CompilerWarnings, compilerWarnings inputs.BuildLog
                  QualityRules.Suppressions, Measured suppressions
                  QualityRules.SkippedTests, Measured(perFile lexed skippedTestLines)
                  QualityRules.DebtMarkers, Measured(perFile lexed debtMarkerLines)
                  QualityRules.LargeFileGrowth, Measured largeFiles
                  QualityRules.BroadCatches, Measured(perFile lexed broadCatchLines)
                  QualityRules.PackageReferences, Measured packages
                  QualityRules.ArchitectureViolations, Measured architecture ]
          AnalyzedFiles = (sources @ projects) |> List.map _.Path
          GeneratedFiles = generated |> List.map _.Path |> List.sort }
