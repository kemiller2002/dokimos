namespace Dokimos.Core.Tests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core

/// Runs the Dokimos provenance codec over the vendored Praxis conformance
/// fixtures (tests/fixtures/praxis-provenance-record, see SOURCE.json).
module ProvenanceConformanceTests =
    let repositoryRoot =
        let rec up (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Dokimos.sln")) then directory.FullName
            else
                match directory.Parent with
                | null -> failwith "repository root not found"
                | parent -> up parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let fixtureRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "praxis-provenance-record")
    let fixturePath (relative: string) = Path.Combine(fixtureRoot, relative)
    let text relative = File.ReadAllText(fixturePath relative)

    let load relative =
        match ProvenanceJson.parse (text relative) with
        | Ok node -> node
        | Error problems -> failwithf "%s is not a JSON object: %A" relative problems

    let manifest = load "manifest.json"

    /// Walks object properties / array indexes; fails on a missing step.
    let rec get (path: string list) (node: JsonNode) : JsonNode =
        match path with
        | [] -> node
        | name :: rest ->
            let next =
                match node with
                | :? JsonObject as item -> ProvenanceJson.property item name
                | :? JsonArray as items ->
                    match Int32.TryParse name with
                    | true, index when index < items.Count -> Option.ofObj items[index]
                    | _ -> None
                | _ -> None
            match next with
            | Some value -> get rest value
            | None -> failwithf "missing %s" name

    let tryGet path node = try Some(get path node) with _ -> None
    let str path node = (get path node).GetValue<string>()
    let obj path node = (get path node).AsObject()

    let entries (name: string) =
        match ProvenanceJson.property manifest name with
        | Some(:? JsonArray as items) -> items |> Seq.choose (Option.ofObj >> Option.map (fun item -> item.AsObject())) |> Seq.toList
        | _ -> []

    let stringField (item: JsonObject) name =
        match ProvenanceJson.property item name with
        | Some value -> value.GetValue<string>()
        | None -> ""

    [<Fact>]
    let ``vendored fixtures match the digests recorded in SOURCE.json`` () =
        let source = load "SOURCE.json"
        Assert.Equal("kemiller2002/praxis", stringField source "repository")
        Assert.Equal("58cf46a", stringField source "commit")

        let files =
            match ProvenanceJson.property source "files" with
            | Some(:? JsonObject as items) -> items |> Seq.map (fun pair -> pair.Key, (Option.ofObj pair.Value |> Option.map (fun v -> v.GetValue<string>()) |> Option.defaultValue "")) |> Seq.toList
            | _ -> []

        Assert.NotEmpty files

        for relative, digest in files do
            let actual = SHA256.HashData(File.ReadAllBytes(fixturePath relative)) |> Convert.ToHexString |> _.ToLowerInvariant()
            Assert.True((actual = digest), $"digest mismatch for {relative}")

        let onDisk =
            Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories)
            |> Seq.map (fun path -> Path.GetRelativePath(fixtureRoot, path).Replace('\\', '/'))
            |> Seq.filter ((<>) "SOURCE.json")
            |> Set.ofSeq

        Assert.Equal<Set<string>>(onDisk, files |> List.map fst |> Set.ofList)

    let classify (node: JsonObject) =
        match ProvenanceJson.validate node with
        | Ok(ProvenanceReading.Current _) -> "valid"
        | Ok(ProvenanceReading.Unversioned _) -> "valid-unversioned"
        | Ok(ProvenanceReading.Unsupported _) -> "unsupported-version"
        | Error _ -> "invalid"

    [<Fact>]
    let ``every manifest case is classified as the manifest expects`` () =
        let cases = entries "cases"
        Assert.Equal(28, cases.Length)

        for case in cases do
            let file = stringField case "file"
            let actual = match ProvenanceJson.parse (text file) with Ok node -> classify node | Error _ -> "invalid"
            let expected = stringField case "expect"
            Assert.True((actual = expected), $"{file}: expected {expected} but was {actual}")

    [<Fact>]
    let ``every successor pair is judged preserved or destructive as the manifest expects`` () =
        let pairs = entries "successors"
        Assert.NotEmpty pairs

        for pair in pairs do
            let before = load (stringField pair "before")
            let after = load (stringField pair "after")
            let actual = if List.isEmpty (ProvenanceJson.successorProblems before after) then "preserved" else "destructive"
            let expected = stringField pair "expect"
            let name = stringField pair "before"
            Assert.True((actual = expected), $"{name}: expected {expected} but was {actual}")

    [<Fact>]
    let ``every e2e step is valid and each successor step preserves its predecessor`` () =
        let steps =
            match ProvenanceJson.property manifest "e2e" with
            | Some(:? JsonObject as e2e) ->
                match ProvenanceJson.property e2e "steps" with
                | Some(:? JsonArray as items) -> items |> Seq.choose (Option.ofObj >> Option.map (fun item -> item.AsObject())) |> Seq.toList
                | _ -> []
            | _ -> []

        Assert.Equal(10, steps.Length)

        for step in steps do
            let file = stringField step "file"
            Assert.Equal("valid", classify (load file))

            match ProvenanceJson.property step "successorOf" with
            | Some previous -> Assert.Empty(ProvenanceJson.successorProblems (load (previous.GetValue<string>())) (load file))
            | None -> ()

    [<Fact>]
    let ``appending to a fixture keeps unknown fields and passes the codec's own successor check`` () =
        let before = load "valid/unknown-fields-preserved.json"

        let contribution =
            { ContributionKey = "EXE-dokimos.gh-run-1"
              Operations = [ ProvenanceOperation.validated ]
              At = "2026-09-27T00:00:00.000Z"
              Last = None
              Actor = ActingIdentity.gitHubActions
              Reason = Some "Regression test passed"
              ContributionEvidence = [] }

        match ProvenanceJson.append contribution before with
        | Error problems -> failwithf "%A" problems
        | Ok after ->
            Assert.Empty(ProvenanceJson.successorProblems before after)
            Assert.Contains("x-future-envelope", after.ToJsonString())
            Assert.Contains("\"x-team\":\"platform\"", after.ToJsonString())
            Assert.Contains("attestation", after.ToJsonString())

    [<Fact>]
    let ``an unsupported major is never extended`` () =
        let future = load "unsupported/future-major.json"
        let contribution =
            { ContributionKey = "EXE-dokimos.run"
              Operations = [ ProvenanceOperation.remediated ]
              At = "2026-09-27T00:00:00.000Z"
              Last = None
              Actor = ProvenanceActor.unknown
              Reason = None
              ContributionEvidence = [] }
        Assert.True(Result.isError (ProvenanceJson.append contribution future))

    // Rules the Praxis working tree adds after 58cf46a; asserted inline here
    // (not vendored) so the codec already enforces them.
    [<Fact>]
    let ``a lineage snapshot that describes another subject is invalid`` () =
        let record = load "valid/derived-with-sources.json"
        let reference = (obj [ "sources" ] record |> Seq.head).Key
        let tampered = record.DeepClone().AsObject()
        let source = obj [ "sources"; reference ] tampered
        source["subject"] <- JsonValue.Create "praxis:RQ-OTHER-2026-A001"
        Assert.Equal("invalid", classify tampered)

    [<Fact>]
    let ``lowering a record's minor version is destructive`` () =
        let before = load "valid/minor-version.json"
        let after = before.DeepClone().AsObject()
        after["version"] <- JsonValue.Create "1.0.0"
        Assert.NotEmpty(ProvenanceJson.successorProblems before after)
