namespace Dokimos.Core.Verification

open System
open System.Text.Json
open Dokimos.Core

/// The input contract of the verification compiler: `dokimos.verification-obligations/1`.
/// Each obligation is bound to one or more exact test identities that must run in a named
/// suite. The compiler never invents expected behaviour: an obligation without a requirement,
/// a criterion or a bound identity is refused, not repaired.
type TestIdentity =
    /// `dotnet:<Namespace>.<Class or module>.<Method>` — xUnit's TestMethod className + name.
    | Dotnet of className: string * methodName: string
    /// `playwright:<file relative to the suite test root> > <title path> [<project>]`.
    | Playwright of file: string * titlePath: string list * project: string
    /// Evidence that is not a test (a review or process record, `praxis:...`); Dokimos
    /// reports it and never judges it.
    | Record of kind: string

type Obligation =
    { Id: string
      Requirement: string
      Priority: string
      Criterion: string
      Categories: string list
      Dependencies: string list }

type BoundTest =
    { Id: string
      Identity: TestIdentity
      Suite: string
      Kind: string
      Obligations: string list
      Categories: string list }

type SuiteRunner =
    | DotnetTest
    | PlaywrightTest
    | EvidenceRecord

type Suite =
    { Id: string
      Runner: SuiteRunner
      Command: string
      /// Directories (repository-relative) the runner executes. A test outside them is excluded.
      Paths: string list
      /// Root that Playwright identity files are relative to (e.g. `tests/browser`).
      TestRoot: string option
      Environment: string
      Fixture: string
      DependsOn: string list
      /// Symbols at least one of which every test in this suite must reference, so that a
      /// test exercising only a helper cannot satisfy an integrated obligation. Empty = no rule.
      RequiredBoundary: string list }

type ObligationSet =
    { PlanId: string
      PlanDigest: string
      Obligations: Obligation list
      Tests: BoundTest list
      Suites: Suite list
      /// Namespace prefix -> repository directory, for mapping a .NET identity to its file.
      NamespaceRoots: (string * string) list }

type Refusal =
    { Subject: string
      Reason: string }

module TestIdentity =
    let private projectSuffix (text: string) =
        let trimmed = text.TrimEnd()
        if trimmed.EndsWith "]" then
            match trimmed.LastIndexOf " [" with
            | -1 -> None
            | at -> Some(trimmed.Substring(0, at), trimmed.Substring(at + 2, trimmed.Length - at - 3))
        else
            None

    let parse (id: string) : Result<TestIdentity, string> =
        let after (prefix: string) = id.Substring(prefix.Length)

        if id.StartsWith "dotnet:" then
            let full = after "dotnet:"
            match full.LastIndexOf '.' with
            | at when at > 0 && at < full.Length - 1 -> Ok(Dotnet(full.Substring(0, at), full.Substring(at + 1)))
            | _ -> Error $"{id}: a .NET identity needs <class>.<method>"
        elif id.StartsWith "playwright:" then
            match projectSuffix (after "playwright:") with
            | Some(path, project) when project <> "" ->
                match path.Split(" > ") |> List.ofArray with
                | file :: title :: rest when file <> "" && title <> "" -> Ok(Playwright(file, title :: rest, project))
                | _ -> Error $"{id}: a Playwright identity needs <file> > <title>"
            | _ -> Error $"{id}: a Playwright identity needs a [project] suffix"
        elif id.Contains ":" && not (id.EndsWith ":") then
            Ok(Record(id))
        else
            Error $"{id}: unknown identity scheme (expected dotnet:, playwright: or a record kind)"

    let render =
        function
        | Dotnet(cls, m) -> $"dotnet:{cls}.{m}"
        | Playwright(file, titles, project) -> $"""playwright:{file} > {String.Join(" > ", titles)} [{project}]"""
        | Record kind -> kind

module ObligationSet =
    [<Literal>]
    let Schema = "dokimos.verification-obligations/1"

    /// Every reason the set cannot be compiled. Empty means every obligation is
    /// unambiguous enough to generate verification work for.
    let refusals (set: ObligationSet) : Refusal list =
        let obligationIds = set.Obligations |> List.map _.Id |> Set.ofList
        let suiteIds = set.Suites |> List.map _.Id |> Set.ofList
        let bound = set.Tests |> List.collect _.Obligations |> Set.ofList

        let duplicates (ids: string list) label =
            ids
            |> List.countBy id
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun (dup, n) -> { Subject = dup; Reason = $"{label} declared {n} times" })

        [ yield! duplicates (set.Obligations |> List.map _.Id) "obligation"
          yield! duplicates (set.Tests |> List.map (fun t -> t.Id + " @" + t.Suite)) "test identity"
          for o in set.Obligations do
              if String.IsNullOrWhiteSpace o.Requirement then
                  yield { Subject = o.Id; Reason = "no requirement id" }
              if String.IsNullOrWhiteSpace o.Criterion then
                  yield { Subject = o.Id; Reason = "no criterion text: expected behaviour would have to be invented" }
              if not (bound.Contains o.Id) then
                  yield { Subject = o.Id; Reason = "no bound test identity" }
          for t in set.Tests do
              if not (suiteIds.Contains t.Suite) then
                  yield { Subject = t.Id; Reason = $"unknown suite {t.Suite}" }
              for o in t.Obligations do
                  if not (obligationIds.Contains o) then
                      yield { Subject = t.Id; Reason = $"bound to unknown obligation {o}" } ]

    // ----------------------------------------------------------------- wire

    let private runnerText =
        function
        | DotnetTest -> "dotnet-test"
        | PlaywrightTest -> "playwright"
        | EvidenceRecord -> "evidence-record"

    let private runnerOf =
        Json.oneOf [ "dotnet-test", DotnetTest; "playwright", PlaywrightTest; "evidence-record", EvidenceRecord ]

    let private strings name = Json.fieldOr name [] (Json.list Json.string)

    let private obligation: Json.Decoder<Obligation> =
        fun e ->
            Json.result {
                let! id = Json.field "id" Json.nonEmptyString e
                let! requirement = Json.fieldOr "requirement" "" Json.string e
                let! priority = Json.fieldOr "priority" "" Json.string e
                let! criterion = Json.fieldOr "criterion" "" Json.string e
                let! categories = strings "categories" e
                let! deps = strings "dependencies" e
                return { Id = id; Requirement = requirement; Priority = priority; Criterion = criterion; Categories = categories; Dependencies = deps }
            }

    let private test: Json.Decoder<BoundTest> =
        fun e ->
            Json.result {
                let! id = Json.field "id" Json.nonEmptyString e
                let! identity = TestIdentity.parse id
                let! suite = Json.field "suite" Json.nonEmptyString e
                let! kind = Json.fieldOr "kind" "" Json.string e
                let! obligations = strings "obligations" e
                let! categories = strings "categories" e
                return { Id = id; Identity = identity; Suite = suite; Kind = kind; Obligations = obligations; Categories = categories }
            }

    let private suite: Json.Decoder<Suite> =
        fun e ->
            Json.result {
                let! id = Json.field "id" Json.nonEmptyString e
                let! runner = Json.field "runner" runnerOf e
                let! command = Json.fieldOr "command" "" Json.string e
                let! paths = strings "paths" e
                let! root = Json.optionalField "testRoot" Json.string e
                let! environment = Json.fieldOr "environment" "" Json.string e
                let! fixture = Json.fieldOr "fixture" "" Json.string e
                let! dependsOn = strings "dependsOn" e
                let! boundary = strings "requiredBoundary" e
                return
                    { Id = id; Runner = runner; Command = command; Paths = paths; TestRoot = root; Environment = environment
                      Fixture = fixture; DependsOn = dependsOn; RequiredBoundary = boundary }
            }

    let private root: Json.Decoder<ObligationSet> =
        fun e ->
            Json.result {
                let! schema = Json.field "schema" Json.string e
                do! if schema = Schema then Ok() else Error $"schema must be {Schema}"
                let! planId = Json.fieldOr "planId" "" Json.string e
                let! digest = Json.fieldOr "planDigest" "" Json.string e
                let! obligations = Json.field "obligations" (Json.list obligation) e
                let! tests = Json.field "tests" (Json.list test) e
                let! suites = Json.field "suites" (Json.list suite) e
                let! roots =
                    Json.fieldOr "namespaceRoots" [] (Json.list (fun r ->
                        Json.result {
                            let! prefix = Json.field "namespace" Json.nonEmptyString r
                            let! dir = Json.field "directory" Json.nonEmptyString r
                            return prefix, dir
                        })) e
                return { PlanId = planId; PlanDigest = digest; Obligations = obligations; Tests = tests; Suites = suites; NamespaceRoots = roots }
            }

    let parse (text: string) =
        Json.parse root text
        |> Result.mapError (function
            | Json.Malformed m -> $"malformed JSON: {m}"
            | Json.NullDocument -> "null document"
            | Json.Invalid m -> m)

    let toJson (set: ObligationSet) =
        let value =
            {| schema = Schema
               planId = set.PlanId
               planDigest = set.PlanDigest
               namespaceRoots = set.NamespaceRoots |> List.map (fun (ns, dir) -> {| ``namespace`` = ns; directory = dir |})
               suites =
                set.Suites
                |> List.map (fun s ->
                    {| id = s.Id; runner = runnerText s.Runner; command = s.Command; paths = s.Paths
                       testRoot = s.TestRoot |> Option.toObj; environment = s.Environment; fixture = s.Fixture
                       dependsOn = s.DependsOn; requiredBoundary = s.RequiredBoundary |})
               obligations =
                set.Obligations
                |> List.map (fun o ->
                    {| id = o.Id; requirement = o.Requirement; priority = o.Priority; criterion = o.Criterion
                       categories = o.Categories; dependencies = o.Dependencies |})
               tests =
                set.Tests
                |> List.map (fun t -> {| id = t.Id; suite = t.Suite; kind = t.Kind; obligations = t.Obligations; categories = t.Categories |}) |}

        Contracts.serialize value

/// Reads the frozen Indy Init plan as it is (`indy-init.v41-verification-execution-plan/1`),
/// joined with the v4 `indy-init.verification-plan/1` for each obligation's requirement,
/// priority and criterion text. Nothing in the frozen package is re-authored.
module IndyInitPlan =
    let private prop (name: string) (e: JsonElement) =
        match e.ValueKind with
        | JsonValueKind.Object ->
            match e.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        | _ -> None

    let private str name e =
        prop name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
        |> Option.map (fun v -> v.GetString() |> Option.ofObj |> Option.defaultValue "")
        |> Option.defaultValue ""

    let private items name e =
        prop name e
        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.Array)
        |> Option.map (fun v -> v.EnumerateArray() |> List.ofSeq)
        |> Option.defaultValue []

    let private strs name e =
        items name e |> List.filter (fun v -> v.ValueKind = JsonValueKind.String) |> List.map (fun v -> v.GetString() |> nonNull)

    let private runnerOf (command: string) =
        if command.StartsWith "dotnet test" then DotnetTest
        elif command.Contains "playwright" then PlaywrightTest
        else EvidenceRecord

    /// The repository directories a runner command names (`tests/...` tokens).
    let runnerPaths (command: string) =
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun token -> token.Trim('"'))
        |> Array.filter (fun token -> token.StartsWith "tests/")
        |> List.ofArray

    let private criteria (verificationPlan: JsonElement) =
        items "requirements" verificationPlan
        |> List.collect (fun requirement ->
            let reqId = str "requirement" requirement
            let priority = str "priority" requirement

            items "obligations" requirement
            |> List.map (fun o ->
                let contract = prop "acceptanceContract" o
                let deps = contract |> Option.bind (prop "dependencies") |> Option.map (strs "requirements") |> Option.defaultValue []
                let categories = contract |> Option.map (strs "requiredEvidenceCategories") |> Option.defaultValue []

                str "sourceObligation" o,
                { Id = str "sourceObligation" o; Requirement = reqId; Priority = priority; Criterion = str "text" o
                  Categories = categories; Dependencies = deps }))
        |> Map.ofList

    let read (executionPlan: string) (verificationPlan: string) : Result<ObligationSet, string> =
        try
            use exec = JsonDocument.Parse executionPlan
            use verif = JsonDocument.Parse verificationPlan
            let e = exec.RootElement

            if str "schema" e <> "indy-init.v41-verification-execution-plan/1" then
                Error "execution plan schema must be indy-init.v41-verification-execution-plan/1"
            elif str "schema" verif.RootElement <> "indy-init.verification-plan/1" then
                Error "verification plan schema must be indy-init.verification-plan/1"
            else
                let byObligation = criteria verif.RootElement
                let identity = prop "planIdentity" e

                let suites =
                    items "suites" e
                    |> List.map (fun s ->
                        let command = str "runner" s
                        let runner = runnerOf command
                        { Id = str "id" s; Runner = runner; Command = command; Paths = runnerPaths command
                          TestRoot = (if runner = PlaywrightTest then Some "tests/browser" else None)
                          Environment = str "environment" s; Fixture = str "fixture" s; DependsOn = strs "dependsOn" s
                          RequiredBoundary = [] })

                let tests =
                    items "suites" e
                    |> List.collect (fun s ->
                        items "tests" s
                        |> List.map (fun t ->
                            let id = str "id" t
                            id, { Id = id; Identity = Record id; Suite = str "id" s; Kind = str "kind" t
                                  Obligations = strs "obligations" t; Categories = strs "categories" t }))

                let parsed =
                    tests
                    |> List.map (fun (id, t) -> TestIdentity.parse id |> Result.map (fun identity -> { t with Identity = identity }))

                match parsed |> List.tryPick (function Error m -> Some m | Ok _ -> None) with
                | Some problem -> Error problem
                | None ->
                    let tests = parsed |> List.choose (function Ok t -> Some t | Error _ -> None)
                    let referenced = tests |> List.collect _.Obligations |> List.distinct

                    let obligations =
                        referenced
                        |> List.map (fun id ->
                            byObligation
                            |> Map.tryFind id
                            |> Option.defaultValue { Id = id; Requirement = ""; Priority = ""; Criterion = ""; Categories = []; Dependencies = [] })

                    Ok
                        { PlanId = identity |> Option.map (str "planId") |> Option.defaultValue ""
                          PlanDigest = identity |> Option.map (str "fileSha256") |> Option.defaultValue ""
                          Obligations = obligations
                          Tests = tests
                          Suites = suites
                          NamespaceRoots =
                            [ "Causa.Acceptance", "tests/Causa.Acceptance"
                              "Causa.Invariants", "tests/Causa.Invariants"
                              "Causa.Contract.Tests", "tests/Causa.Contract.Tests"
                              "Causa.Domain.Tests", "tests/Causa.Domain.Tests"
                              "Causa.Persistence.Tests", "tests/Causa.Persistence.Tests"
                              "Causa.App.Tests", "tests/Causa.App.Tests" ] }
        with :? JsonException as ex ->
            Error $"malformed plan JSON: {ex.Message}"
