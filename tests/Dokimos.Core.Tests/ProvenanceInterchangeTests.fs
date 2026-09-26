namespace Dokimos.Core.Tests

open System
open System.IO
open System.Security.Cryptography
open Xunit
open Dokimos.Domain
open Dokimos.Core

/// Shared helpers for the provenance tests (R14).
module ProvenanceFixtures =
    let rec private findRoot (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "Dokimos.sln")) then directory.FullName
        else
            match directory.Parent with
            | null -> failwith "Could not locate the Dokimos repository root"
            | parent -> findRoot parent

    let root = findRoot (DirectoryInfo(AppContext.BaseDirectory))
    let path (relative: string) = Path.Combine(root, relative)
    let fixturePath name = path (Path.Combine("tests", "fixtures", "praxis-provenance", name))

    let unwrap result =
        match result with
        | Ok value -> value
        | Error error -> failwith $"{error}"

    let json (text: string) = Json.parse text |> unwrap
    let fixture name = json (File.ReadAllText(fixturePath name))

    let field name value =
        Json.tryField name value |> Option.defaultWith (fun () -> failwith $"missing field {name}")

    let text name value =
        field name value |> Json.tryString |> Option.defaultWith (fun () -> failwith $"{name} is not a string")

    let items value =
        match value with
        | Json.Array values -> values
        | _ -> failwith "expected an array"

    let sha256 (file: string) =
        use stream = File.OpenRead file
        SHA256.HashData(stream) |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()

    let supported verdict =
        match verdict with
        | ProvenanceVerdict.Supported(block, contributions, lineage, warnings) -> block, contributions, lineage, warnings
        | other -> failwith $"expected supported, got {other}"

    let codex = { Kind = ActorKind.Agent; Id = "openai/codex"; Provider = Some "openai"; Model = Some "gpt-5-codex"; Runtime = Some "codex" }
    let claude = { Kind = ActorKind.Agent; Id = "anthropic/claude-code"; Provider = Some "anthropic"; Model = Some "unknown"; Runtime = Some "claude-code" }
    let ci = { Kind = ActorKind.Automation; Id = "github/github-actions"; Provider = Some "github"; Model = Some "unknown"; Runtime = Some "github-actions" }
    let kevin = { Kind = ActorKind.Human; Id = "kevin"; Provider = None; Model = None; Runtime = None }

    let key text = ContributionKey.tryParse text |> Option.get

    let contribution keyText operations at actor : Contribution =
        { Key = key keyText
          Operations = operations
          At = at
          Last = None
          Actor = actor
          Reason = None
          Evidence = [] }

    let appendAll (contributions: Contribution list) =
        contributions
        |> List.fold (fun block item -> ProvenanceInterchange.append block item |> unwrap |> fst) ProvenanceInterchange.emptyBlock

    /// Replays `echelon-chain.json` with the Dokimos codec: one block per record.
    let replayChain () : Map<string, Json> =
        fixture "echelon-chain.json"
        |> field "steps"
        |> items
        |> List.fold
            (fun (blocks: Map<string, Json>) step ->
                let record = text "record" step
                let current = blocks |> Map.tryFind record |> Option.defaultValue ProvenanceInterchange.emptyBlock

                let next =
                    match Json.tryField "lineage" step with
                    | Some references -> ProvenanceInterchange.addLineage (items references |> List.choose Json.tryString) current
                    | None ->
                        let append = field "append" step
                        ProvenanceInterchange.appendJson current (text "key" append) (field "contribution" append) |> unwrap |> fst

                blocks |> Map.add record next)
            Map.empty

open ProvenanceFixtures

module ProvenanceInterchangeTests =
    [<Fact>]
    let ``vendored Praxis fixtures and schemas match the SHA-256 recorded in SOURCE.json`` () =
        for directory in [ Path.Combine("tests", "fixtures", "praxis-provenance"); "schemas/praxis" ] do
            let source = json (File.ReadAllText(path (Path.Combine(directory, "SOURCE.json"))))
            Assert.Equal("kemiller2002/praxis", text "repository" source)
            Assert.Equal("a42c44e8ae0e6e16fdd513141460b700e5fa6648", text "commit" source)
            let files = Json.members (field "files" source)
            Assert.NotEmpty(files)

            for name, expected in files do
                Assert.Equal(Json.tryString expected |> Option.get, sha256 (path (Path.Combine(directory, name))))

    [<Fact>]
    let ``every vendored conformance case reaches the reference verdict and warning count`` () =
        let cases = fixture "cases.json" |> field "cases" |> items
        Assert.True(cases.Length >= 40, "expected the full conformance set")

        for case in cases do
            let verdict = ProvenanceInterchange.classify (field "block" case)

            let actual =
                match verdict with
                | ProvenanceVerdict.Supported(_, _, _, warnings) -> "supported", warnings.Length
                | ProvenanceVerdict.Unsupported _ -> "unsupported", 0
                | ProvenanceVerdict.Malformed _ -> "malformed", 0

            let expected =
                text "expect" case,
                (match field "warnings" case with
                 | Json.Number raw -> int raw
                 | _ -> failwith "warnings must be a number")

            let name = text "name" case
            Assert.True((expected = actual), $"{name}: expected {expected}, got {actual} ({verdict})")

    [<Fact>]
    let ``supported blocks keep unknown fields exactly as received`` () =
        let case =
            fixture "cases.json" |> field "cases" |> items |> List.find (fun item -> text "name" item = "unknown-fields-preserved")

        let original = field "block" case
        let block, _, _, _ = supported (ProvenanceInterchange.classify original)
        Assert.Equal(Json.serialize original, Json.serialize block)

        let appended, changed =
            ProvenanceInterchange.append block (contribution "CTB-20260926-5f2e19aa" [ ContributionOperation.Reviewed ] "2026-09-26T11:00:00.000Z" kevin)
            |> unwrap

        Assert.True(changed)
        Assert.Empty(ProvenanceInterchange.preservationViolations original appended)

    [<Fact>]
    let ``unsupported major is carried verbatim and never appended to`` () =
        let original = json """{"schema":"praxis.provenance/2","contributions":[{"totally":"different"}],"whatever":true}"""

        match ProvenanceInterchange.classify original with
        | ProvenanceVerdict.Unsupported(schema, carried) ->
            Assert.Equal("praxis.provenance/2", schema)
            Assert.Equal(Json.serialize original, Json.serialize carried)
        | other -> failwith $"expected unsupported, got {other}"

        let attempt = ProvenanceInterchange.append original (contribution "EXT-dokimos.run-1" [ ContributionOperation.Measured ] "2026-09-26T08:00:00.000Z" ci)
        Assert.True(Result.isError attempt)
        Assert.Empty(ProvenanceInterchange.preservationViolations original original)
        Assert.NotEmpty(ProvenanceInterchange.preservationViolations original ProvenanceInterchange.emptyBlock)

    [<Fact>]
    let ``malformed blocks are rejected with a clear problem, never repaired`` () =
        let malformed = json """{"schema":"praxis.provenance/1","contributions":{"CTB-20260926-11111111":{"operations":["created"],"at":"2026-09-26T08:00:00.000Z","actor":{"kind":"agent","id":"openai/codex","provider":"openai","model":"gpt-5-codex","runtime":"codex"}}}}"""

        match ProvenanceInterchange.classify malformed with
        | ProvenanceVerdict.Malformed problems -> Assert.Contains(problems, fun problem -> problem.Contains "must be keyed by the execution")
        | other -> failwith $"expected malformed, got {other}"

        Assert.True(Result.isError (ProvenanceInterchange.append malformed (contribution "CTB-20260926-5f2e19aa" [ ContributionOperation.Reviewed ] "2026-09-26T11:00:00.000Z" kevin)))

        match ProvenanceInterchange.classifyText "{not json" with
        | ProvenanceVerdict.Malformed _ -> ()
        | other -> failwith $"expected malformed, got {other}"

    [<Fact>]
    let ``serialization round-trips: typed contributions to JSON text and back`` () =
        let contributions =
            [ { contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created; ContributionOperation.Discovered ] "2026-09-26T08:00:00.000Z" codex with
                  Reason = Some "found it"
                  Evidence = [ "aegis:evidence/EVD-0001" ] }
              contribution "EXT-dokimos.snapshot-1" [ ContributionOperation.Measured ] "2026-09-26T08:10:00.000Z" ci
              contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Remediated; ContributionOperation.Other "x-triaged" ] "2026-09-26T09:00:00.000Z" claude
              contribution "CTB-20260926-5f2e19aa" [ ContributionOperation.Reviewed ] "2026-09-26T10:00:00.000Z" kevin ]

        let block = appendAll contributions |> ProvenanceInterchange.addLineage [ "git:commit/5e1f0c2"; "RQ-APP-2026-A001" ]
        let text = Json.serializeIndented block
        let reparsed = json text
        let _, parsed, lineage, warnings = supported (ProvenanceInterchange.classify reparsed)
        Assert.Equal<Contribution list>(contributions, parsed)
        Assert.Equal<string list>([ "git:commit/5e1f0c2"; "RQ-APP-2026-A001" ], lineage)
        Assert.Empty(warnings)
        Assert.Equal(Json.serialize block, Json.serialize reparsed)

    [<Fact>]
    let ``append is append-only: no re-attribution, no second or late created, idempotent`` () =
        let first = contribution "EXE-20260926T080000000Z-a1a1a1a1" [ ContributionOperation.Created ] "2026-09-26T08:00:00.000Z" codex
        let block = appendAll [ first ]

        let reattributed = ProvenanceInterchange.append block { first with Actor = claude; Operations = [ ContributionOperation.Modified ] }
        Assert.True(Result.isError reattributed)

        let secondCreator = ProvenanceInterchange.append block (contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Created ] "2026-09-26T09:00:00.000Z" claude)
        Assert.True(Result.isError secondCreator)

        let again, changed = ProvenanceInterchange.append block first |> unwrap
        Assert.False(changed)
        Assert.Equal(Json.serialize block, Json.serialize again)

        let later, _ = ProvenanceInterchange.append block { first with Operations = [ ContributionOperation.Modified ]; At = "2026-09-26T08:30:00.000Z" } |> unwrap
        let _, parsed, _, _ = supported (ProvenanceInterchange.classify later)
        Assert.Equal(Some "2026-09-26T08:30:00.000Z", parsed.Head.Last)
        Assert.Empty(ProvenanceInterchange.preservationViolations block later)

        let lateCreated = appendAll [ contribution "EXE-20260926T090000000Z-b1b1b1b1" [ ContributionOperation.Modified ] "2026-09-26T09:00:00.000Z" claude ]
        Assert.True(Result.isError (ProvenanceInterchange.append lateCreated { first with At = "2026-09-26T10:00:00.000Z" }))

    [<Fact>]
    let ``credential-like values are refused`` () =
        for secret in [ "ghp_0123456789abcdefghijABCDEFGHIJ0123"; "sk-ant-api03-abcdefghijklmnopqrstuv"; "AKIAABCDEFGHIJKLMNOP"; "Bearer abcdefghijklmnopqrstuvwxyz" ] do
            Assert.True(ProvenanceInterchange.isCredentialLike secret, secret)

        for benign in [ "openai/codex"; "EXE-20260926T080000000Z-a1a1a1a1"; "echelon/dokimos"; "sk-short" ] do
            Assert.False(ProvenanceInterchange.isCredentialLike benign, benign)

    [<Fact>]
    let ``echelon chain replays with the Dokimos codec: every record keeps its own originator and roles`` () =
        let chain = fixture "echelon-chain.json"
        let blocks = replayChain ()
        let expect = field "expect" chain

        for record, expected in Json.members (field "originators" expect) do
            let verdict = ProvenanceInterchange.classify blocks[record]
            let origin = ProvenanceInterchange.originator verdict |> Option.get
            Assert.Equal(text "key" expected, ContributionKey.value origin.Key)
            Assert.Equal(text "actorId" expected, origin.Actor.Id)

        for record, roles in Json.members (field "roles" expect) do
            for operationCode, keys in Json.members roles do
                let operation = ContributionOperation.tryParse operationCode |> Option.get
                let actual = ProvenanceInterchange.withOperation operation (ProvenanceInterchange.classify blocks[record]) |> List.map (_.Key >> ContributionKey.value)
                Assert.Equal<string list>(items keys |> List.choose Json.tryString, actual)

        let originators =
            blocks |> Map.toList |> List.choose (fun (_, block) -> ProvenanceInterchange.originator (ProvenanceInterchange.classify block))

        Assert.Equal(5, originators.Length)
        Assert.True((originators |> List.map _.Actor.Id |> List.distinct).Length > 1, "no single actor authored the chain")

        let oneAgent = field "distinctExecutionsOfOneAgent" expect
        let agentKeys =
            blocks
            |> Map.toList
            |> List.collect (fun (_, block) -> ProvenanceInterchange.contributions (ProvenanceInterchange.classify block))
            |> List.filter (fun item -> item.Actor.Id = text "actorId" oneAgent)
            |> List.map (_.Key >> ContributionKey.value)
            |> List.distinct
            |> List.sort

        Assert.Equal<string list>(items (field "keys" oneAgent) |> List.choose Json.tryString |> List.sort, agentKeys)

    [<Fact>]
    let ``OBS-2026-0001: the measurer is Dokimos, the measured change's author is a different agent execution reached only through lineage`` () =
        let blocks = replayChain ()
        let observation = Attributed(ProvenanceInterchange.classify blocks["dokimos:observation/OBS-2026-0001"])

        let measurers = Authorship.measuredBy observation
        Assert.Equal<string list>([ "EXT-dokimos.snapshot-20260926-01" ], measurers |> List.map (_.Key >> ContributionKey.value))
        Assert.Equal("echelon/dokimos", measurers.Head.Actor.Id)
        Assert.Equal(ActorKind.Automation, measurers.Head.Actor.Kind)

        let authors = Authorship.viaLineage (fun reference -> Map.tryFind reference blocks) observation |> unwrap
        let reference, author = Assert.Single(authors)
        Assert.Equal("git:commit/5e1f0c2", reference)
        let author = author |> Option.get
        Assert.Equal("anthropic/claude-code", author.Actor.Id)
        Assert.Equal("EXE-20260926T083000000Z-b2b2b2b2", ContributionKey.value author.Key)
        Assert.NotEqual(measurers.Head.Actor, author.Actor)
        Assert.NotEqual(measurers.Head.Key, author.Key)

        // The observation's own originator is the measurement, not the change's author.
        Assert.Equal("echelon/dokimos", (RecordAttribution.originator observation |> Option.get).Actor.Id)

        // An unresolvable lineage reference yields no author rather than a guess.
        let unresolved = Authorship.viaLineage (fun _ -> None) observation |> unwrap
        Assert.Equal<(string * Contribution option) list>([ "git:commit/5e1f0c2", None ], unresolved)
