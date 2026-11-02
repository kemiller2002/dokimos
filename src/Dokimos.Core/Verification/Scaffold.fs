namespace Dokimos.Core.Verification

open System
open Dokimos.Core

type Language =
    | FSharp
    | JavaScript

/// One bounded unit of test-writing work: one target file, the identities still missing or
/// invalid in it, and only the criteria those identities verify (a small agent context).
type WorkPackage =
    { Id: string
      Rank: int
      Suite: string
      File: string
      Language: Language
      Tests: BoundTest list
      Obligations: Obligation list
      Fixture: string
      DependsOn: string list
      /// Identities to write; the cost proxy used for scheduling.
      Cost: int }

type FileWrite =
    | CreateFile of path: string * content: string
    | AppendToFile of path: string * content: string

module Scaffold =
    [<Literal>]
    let Marker = "DOKIMOS-UNIMPLEMENTED"

    [<Literal>]
    let WorkSchema = "dokimos.verification-work/1"

    /// The repository file a bound identity lives in.
    let fileOf (set: ObligationSet) (suite: Suite) (identity: TestIdentity) =
        match identity with
        | Dotnet(cls, _) ->
            let segments = cls.Split('.') |> List.ofArray
            set.NamespaceRoots
            |> List.filter (fun (prefix, _) -> cls = prefix || cls.StartsWith(prefix + "."))
            |> List.sortByDescending (fst >> String.length)
            |> List.tryHead
            |> Option.map (fun (prefix, dir) ->
                let rest = segments |> List.skip (prefix.Split('.').Length)
                String.Join("/", dir :: rest) + ".fs")
        | Playwright(file, _, _) -> suite.TestRoot |> Option.map (fun root -> root.TrimEnd('/') + "/" + file)
        | Record _ -> None

    let private categoryRank (categories: string list) =
        [ "security", 0; "integration", 1; "live-provider", 2; "browser", 3; "unit", 4 ]
        |> List.choose (fun (c, r) -> if List.contains c categories then Some r else None)
        |> List.fold min 5

    let private suiteDepth (set: ObligationSet) =
        let rec depth seen (id: string) =
            match set.Suites |> List.tryFind (fun s -> s.Id = id) with
            | Some s when not (Set.contains id seen) -> 1 + (s.DependsOn |> List.map (depth (Set.add id seen)) |> List.fold max 0)
            | _ -> 0
        depth Set.empty

    let private needsWork =
        function
        | NotGenerated _ | Invalid _ -> true
        | _ -> false

    /// Work packages for every identity that is not generated or invalid, ranked: the named
    /// vertical-slice requirements first, then by obligation criticality, suite dependency depth
    /// and size. Incremental: run it again after each merge and only the remaining work returns.
    let plan (set: ObligationSet) (coverage: CoverageReport) (first: string list) : WorkPackage list =
        let status = coverage.Identities |> List.map (fun r -> (r.Identity, r.Suite), r.Status) |> Map.ofList
        let obligation id = set.Obligations |> List.tryFind (fun o -> o.Id = id)

        set.Tests
        |> List.filter (fun t -> status |> Map.tryFind (t.Id, t.Suite) |> Option.exists needsWork)
        |> List.choose (fun t ->
            let suite = set.Suites |> List.find (fun s -> s.Id = t.Suite)
            fileOf set suite t.Identity |> Option.map (fun file -> (suite, file), t))
        |> List.groupBy fst
        |> List.map (fun ((suite, file), group) ->
            let tests = group |> List.map snd |> List.sortBy _.Id
            let obligations = tests |> List.collect _.Obligations |> List.distinct |> List.choose obligation
            let slice = obligations |> List.exists (fun o -> List.contains o.Requirement first)
            let criticality = obligations |> List.map (fun o -> categoryRank (o.Categories @ (tests |> List.collect _.Categories))) |> List.fold min 5
            let key = (if slice then 0 else 1), criticality, suiteDepth set suite.Id, -tests.Length, file
            key,
            { Id = ""
              Rank = 0
              Suite = suite.Id
              File = file
              Language = (if file.EndsWith ".fs" then FSharp else JavaScript)
              Tests = tests
              Obligations = obligations
              Fixture = suite.Fixture
              DependsOn = suite.DependsOn
              Cost = tests.Length })
        |> List.sortBy fst
        |> List.mapi (fun i (_, p) -> { p with Id = $"WP-{i + 1:D3}"; Rank = i + 1 })

    /// Identities that need work but map to no file (no namespace root or test root). Reported,
    /// never silently dropped: someone must place them by hand.
    let unmapped (set: ObligationSet) (coverage: CoverageReport) =
        coverage.Identities
        |> List.filter (fun r -> needsWork r.Status)
        |> List.filter (fun r ->
            match set.Tests |> List.tryFind (fun t -> t.Id = r.Identity && t.Suite = r.Suite), set.Suites |> List.tryFind (fun s -> s.Id = r.Suite) with
            | Some t, Some suite -> (fileOf set suite t.Identity).IsNone
            | _ -> true)
        |> List.map _.Identity

    // ---------------------------------------------------------------- skeletons

    let private comment (set: ObligationSet) (test: BoundTest) =
        test.Obligations
        |> List.map (fun id ->
            let criterion = set.Obligations |> List.tryFind (fun o -> o.Id = id) |> Option.map _.Criterion |> Option.defaultValue ""
            let text = criterion.Replace("\r", " ").Replace("\n", " ")
            $"{id}: {text}")

    let private fsTest (set: ObligationSet) (indent: string) (test: BoundTest) (methodName: string) =
        (comment set test |> List.map (fun line -> $"{indent}/// {line}"))
        @ [ $"{indent}[<Fact>]"
            $"{indent}let ``{methodName}`` () : unit ="
            $"{indent}    failwith \"{Marker} {test.Id}\""
            "" ]

    let private jsString (text: string) = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""

    let private jsTest (set: ObligationSet) (test: BoundTest) (titles: string list) =
        let describes = titles |> List.take (titles.Length - 1)
        let pad n = String(' ', 2 * n)
        let depth = describes.Length
        let failure = jsString (Marker + " " + test.Id)
        (describes |> List.mapi (fun i d -> $"{pad i}test.describe({jsString d}, () => {{"))
        @ [ $"{pad depth}test({jsString (List.last titles)}, async ({{ page }}) => {{" ]
        @ (comment set test |> List.map (fun line -> $"{pad (depth + 1)}// {line}"))
        @ [ $"{pad (depth + 1)}throw new Error({failure});"; $"{pad depth}}});" ]
        @ (List.rev [ 0 .. depth - 1 ] |> List.map (fun i -> $"{pad i}}});"))
        @ [ "" ]

    let private header (set: ObligationSet) (prefix: string) =
        [ $"{prefix} Generated by `dokimos verification scaffold` from plan {set.PlanId}. Each body fails until"
          $"{prefix} it is written; `dokimos verification coverage` reports {Marker} as a placeholder." ]

    /// The file writes for a package. Never overwrites: an existing file only gains the missing
    /// tests (and only when `existing` is supplied, i.e. the caller opted into appending).
    let render (set: ObligationSet) (importFrom: string) (existing: string option) (package: WorkPackage) : FileWrite option =
        let missing =
            package.Tests
            |> List.filter (fun t ->
                match existing, t.Identity with
                | Some text, Dotnet(_, m) -> not (text.Contains $"``{m}``" || text.Contains $"let {m} ")
                | Some text, Playwright(_, titles, _) -> not (text.Contains(jsString (List.last titles)))
                | _ -> true)
            |> List.distinctBy (fun t ->
                match t.Identity with
                | Playwright(file, titles, _) -> file + "|" + String.Join(">", titles)
                | other -> TestIdentity.render other)

        match package.Language, existing, missing with
        | _, _, [] -> None
        | FSharp, None, tests ->
            let cls = match tests.Head.Identity with Dotnet(c, _) -> c | _ -> ""
            let ns, modName = cls.Substring(0, cls.LastIndexOf '.'), cls.Substring(cls.LastIndexOf '.' + 1)
            let body = tests |> List.collect (fun t -> match t.Identity with Dotnet(_, m) -> fsTest set "    " t m | _ -> [])
            Some(CreateFile(package.File, String.Join("\n", header set "//" @ [ $"namespace {ns}"; ""; "open Xunit"; ""; $"module {modName} =" ] @ body)))
        | FSharp, Some text, tests ->
            let indent =
                text.Split('\n')
                |> Array.tryPick (fun l -> if l.TrimStart().StartsWith "[<Fact" then Some(l.Substring(0, l.Length - l.TrimStart().Length)) else None)
                |> Option.defaultValue "    "
            let body = tests |> List.collect (fun t -> match t.Identity with Dotnet(_, m) -> fsTest set indent t m | _ -> [])
            Some(AppendToFile(package.File, String.Join("\n", "" :: body)))
        | JavaScript, None, tests ->
            let body = tests |> List.collect (fun t -> match t.Identity with Playwright(_, titles, _) -> jsTest set t titles | _ -> [])
            Some(CreateFile(package.File, String.Join("\n", header set "//" @ [ $"import {{ test, expect }} from {jsString importFrom};"; "" ] @ body)))
        | JavaScript, Some _, tests ->
            let body = tests |> List.collect (fun t -> match t.Identity with Playwright(_, titles, _) -> jsTest set t titles | _ -> [])
            Some(AppendToFile(package.File, String.Join("\n", "" :: body)))

    let workJson (set: ObligationSet) (packages: WorkPackage list) (unmappedIdentities: string list) =
        Contracts.serialize
            {| schema = WorkSchema
               planId = set.PlanId
               unmapped = unmappedIdentities
               packages =
                packages
                |> List.map (fun p ->
                    {| id = p.Id
                       rank = p.Rank
                       suite = p.Suite
                       file = p.File
                       language = (match p.Language with FSharp -> "fsharp" | JavaScript -> "javascript")
                       fixture = p.Fixture
                       dependsOn = p.DependsOn
                       cost = p.Cost
                       identities = p.Tests |> List.map _.Id
                       obligations = p.Obligations |> List.map (fun o -> {| id = o.Id; requirement = o.Requirement; criterion = o.Criterion; categories = o.Categories |}) |}) |}
