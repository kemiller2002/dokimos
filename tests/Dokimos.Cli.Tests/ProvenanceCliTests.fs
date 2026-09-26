namespace Dokimos.Cli.Tests

open System
open System.IO
open System.Text.Json.Nodes
open Xunit
open Dokimos.Core
open Dokimos.Cli

module ProvenanceCliTests =
    let repositoryRoot =
        let rec up (directory: DirectoryInfo) =
            if File.Exists(Path.Combine(directory.FullName, "Dokimos.sln")) then directory.FullName
            else
                match directory.Parent with
                | null -> failwith "repository root not found"
                | parent -> up parent
        up (DirectoryInfo AppContext.BaseDirectory)

    let fixture relative = Path.Combine(repositoryRoot, "tests", "fixtures", "praxis-provenance-record", relative)

    let rec get (path: string list) (node: JsonNode) : JsonNode =
        match path with
        | [] -> node
        | name :: rest ->
            match node with
            | :? JsonObject as item ->
                match ProvenanceJson.property item name with
                | Some value -> get rest value
                | None -> failwithf "missing %s" name
            | _ -> failwithf "not an object at %s" name

    let str path node = (get path node).GetValue<string>()
    let parse (text: string) = (JsonNode.Parse text |> Option.ofObj |> Option.get).AsObject()

    /// A context whose environment is a fixed map and which records every
    /// variable name it is asked for.
    let context (pairs: (string * string) list) (asked: Collections.Generic.List<string>) =
        let values = Map.ofList pairs
        { Environment = fun name -> asked.Add name; values |> Map.tryFind name
          Now = fun () -> DateTimeOffset(2026, 9, 26, 11, 0, 0, TimeSpan.Zero)
          FreshRun = fun () -> "local-test" }

    let workspace () =
        let root = Path.Combine(Path.GetTempPath(), "dokimos-" + Guid.NewGuid().ToString("N"))
        let sources = Path.Combine(root, "src")
        Directory.CreateDirectory sources |> ignore
        File.WriteAllText(Path.Combine(sources, "A.fs"), "let f (x: obj) = x\n")
        // Author-looking lines are present on purpose: they must never become the actor.
        File.WriteAllText(Path.Combine(root, "history.txt"), "commit abc123 2026-09-26T10:00:00+00:00\nAuthor: Claude <noreply@anthropic.com>\nCo-Authored-By: Codex <codex@openai.com>\n3\t0\tsrc/A.fs\n")
        root

    let write (root: string) name (text: string) =
        let path = Path.Combine(root, name)
        File.WriteAllText(path, text)
        path

    /// Six changes of a complex file: a maintainability-hotspot finding.
    let hotspotWorkspace () =
        let root = workspace ()
        let source = Path.Combine(root, "src", "A.fs")
        File.WriteAllText(source, "let f x =\n    if x > 0 then\n        match x with\n        | 1 -> 1\n        | 2 -> 2\n        | 3 -> 3\n        | 4 -> 4\n        | 5 -> 5\n        | 6 -> 6\n        | 7 -> 7\n        | _ -> 0\n")
        let history = [ for day in 1..6 -> $"commit c{day} 2026-09-0{day}T10:00:00+00:00\n20\t0\t{source}\n" ] |> String.concat ""
        File.WriteAllText(Path.Combine(root, "history.txt"), history)
        root

    let snapshotIn root pairs revision extra =
        let asked = Collections.Generic.List<string>()
        let args =
            [ "snapshot"; Path.Combine(root, "src"); "--git-history"; Path.Combine(root, "history.txt"); "--repository"; "app"; "--revision"; revision; "--ref"; "main" ] @ extra
        Program.execute (context pairs asked) args

    let snapshot pairs revision extra =
        let root = workspace ()
        let asked = Collections.Generic.List<string>()
        let args =
            [ "snapshot"; Path.Combine(root, "src"); "--git-history"; Path.Combine(root, "history.txt"); "--repository"; "app"; "--revision"; revision; "--ref"; "main" ] @ extra
        root, asked, Program.execute (context pairs asked) args

    [<Fact>]
    let ``the accepted 1.0.0 baseline still loads, with provenance absent rather than invented`` () =
        let path = Path.Combine(repositoryRoot, "baselines", "accepted-snapshot.json")
        let bytes = File.ReadAllBytes path
        match Program.tryReadSnapshot path with
        | Ok snapshot ->
            Assert.Equal("1.0.0", snapshot.SchemaVersion)
            Assert.Equal(None, snapshot.Provenance)
        | Error reason -> failwith reason
        Assert.Equal<byte[]>(bytes, File.ReadAllBytes path)

    [<Fact>]
    let ``git history never populates the actor and only whitelisted variables are read`` () =
        let _, asked, result = snapshot [] "abc123" []
        Assert.Equal(0, result.ExitCode)
        let record = get [ "Provenance" ] (parse result.Output)
        Assert.Equal("1.1.0", str [ "SchemaVersion" ] (parse result.Output))
        Assert.Equal("unknown", str [ "contributions"; "EXE-dokimos.local-test"; "actor"; "kind" ] record)
        Assert.Equal("unknown", str [ "contributions"; "EXE-dokimos.local-test"; "actor"; "id" ] record)
        Assert.DoesNotContain("anthropic", result.Output)
        Assert.DoesNotContain("openai", result.Output)
        Assert.All(asked, fun name -> Assert.Contains(name, IdentityEnvironment.names))

    [<Fact>]
    let ``in GitHub Actions the measuring actor is automation and the code author appears only in sources`` () =
        let _, _, result =
            snapshot [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001" ] "abc123" [ "--subject-provenance"; fixture "e2e/03-implementation.json" ]
        Assert.Equal(0, result.ExitCode)
        let record = get [ "Provenance" ] (parse result.Output)
        Assert.Equal("dokimos:snapshot/app:abc123", str [ "subject" ] record)
        Assert.Equal("github/github-actions", str [ "contributions"; "EXE-dokimos.gh-run-9001"; "actor"; "id" ] record)
        Assert.DoesNotContain("c3c3c3c3", (get [ "contributions" ] record).ToJsonString())
        Assert.Equal("anthropic/claude-code", str [ "sources"; "git:commit/abc123"; "contributions"; "EXE-20260926T100000000Z-c3c3c3c3"; "actor"; "id" ] record)

    [<Fact>]
    let ``a declared agent measuring inside a Praxis execution is keyed by that execution`` () =
        let _, _, result =
            snapshot [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "openai"; "ROS_TELEMETRY_RUNTIME", "codex"; "ROS_EXECUTION_ID", "EXE-20260926T140000000Z-e5e5e5e5" ] "abc123" []
        let record = get [ "Provenance" ] (parse result.Output)
        Assert.Equal("openai/codex", str [ "contributions"; "EXE-20260926T140000000Z-e5e5e5e5"; "actor"; "id" ] record)

    let withUnknownFields () =
        let record = parse (File.ReadAllText(fixture "e2e/03-implementation.json"))
        record["x-envelope-extra"] <- JsonValue.Create "kept"
        let entry = (get [ "contributions"; "EXE-20260926T100000000Z-c3c3c3c3" ] record).AsObject()
        entry["attestation"] <- JsonValue.Create "opaque"
        (get [ "actor" ] entry).AsObject()["x-team"] <- JsonValue.Create "platform"
        record

    [<Fact>]
    let ``snapshot provenance round-trips through read and write with unknown fields intact`` () =
        let root = workspace ()
        let subject = write root "subject.json" ((withUnknownFields ()).ToJsonString())
        let _, _, result = snapshot [] "abc123" [ "--subject-provenance"; subject ]
        let path = write root "snapshot.json" result.Output
        match Program.tryReadSnapshot path with
        | Error reason -> failwith reason
        | Ok read ->
            let written = parse (Text.Json.JsonSerializer.Serialize(read, Program.options))
            Assert.True(JsonNode.DeepEquals(get [ "Provenance" ] (parse result.Output), get [ "Provenance" ] written))
            Assert.Contains("x-envelope-extra", written.ToJsonString())
            Assert.Contains("platform", written.ToJsonString())

    [<Fact>]
    let ``comparison keeps both measuring actors separate, and a legacy baseline is lineage only`` () =
        let root = workspace ()
        let _, _, first = snapshot [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ] "aaa111" []
        let _, _, second = snapshot [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "7" ] "bbb222" []
        let before = write root "before.json" first.Output
        let after = write root "after.json" second.Output
        let asked = Collections.Generic.List<string>()
        let result = Program.execute (context [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "8" ] asked) [ "compare"; before; after ]
        Assert.Equal(0, result.ExitCode)
        let record = get [ "Provenance" ] (parse result.Output)
        Assert.Equal("github/github-actions", str [ "contributions"; "EXE-dokimos.gh-run-8"; "actor"; "id" ] record)
        Assert.Equal("kevin", str [ "sources"; "dokimos:snapshot/app:aaa111"; "contributions"; "EXE-dokimos.local-test"; "actor"; "id" ] record)
        Assert.Equal("github/github-actions", str [ "sources"; "dokimos:snapshot/app:bbb222"; "contributions"; "EXE-dokimos.gh-run-7"; "actor"; "id" ] record)

        let legacy = Program.execute (context [] asked) [ "compare"; Path.Combine(repositoryRoot, "baselines", "accepted-snapshot.json"); after ]
        Assert.Equal(0, legacy.ExitCode)
        let legacyRecord = get [ "Provenance" ] (parse legacy.Output)
        Assert.Contains("dokimos:snapshot/kemiller2002/dokimos:", (get [ "derivedFrom" ] legacyRecord).ToJsonString())
        Assert.DoesNotContain("kemiller2002/dokimos", (get [ "sources" ] legacyRecord).ToJsonString())

    [<Fact>]
    let ``validation appends to a comparison without displacing the comparer; snapshots are refused`` () =
        let root = workspace ()
        let _, _, first = snapshot [] "aaa111" []
        let before = write root "before.json" first.Output
        let asked = Collections.Generic.List<string>()
        let comparison = Program.execute (context [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "8" ] asked) [ "compare"; before; before ]
        let comparisonPath = write root "comparison.json" comparison.Output
        let validated =
            Program.execute
                (context [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ] asked)
                [ "provenance"; "append"; comparisonPath; "--operation"; "x-validated"; "--reason"; "Reviewed the regression" ]
        Assert.Equal(0, validated.ExitCode)
        let document = parse validated.Output
        Assert.Equal("created", ((get [ "Provenance"; "contributions"; "EXE-dokimos.gh-run-8"; "operations" ] document).AsArray()[0] |> Option.ofObj |> Option.get).GetValue<string>())
        Assert.Equal("kevin", str [ "Provenance"; "contributions"; "EXE-dokimos.local-test"; "actor"; "id" ] document)
        Assert.True(JsonNode.DeepEquals(get [ "MetricChanges" ] (parse comparison.Output), get [ "MetricChanges" ] document))

        let refused = Program.execute (context [] asked) [ "provenance"; "append"; before; "--operation"; "x-validated" ]
        Assert.Equal(3, refused.ExitCode)
        Assert.Contains("snapshot-is-immutable-evidence", refused.ErrorOutput)

    [<Fact>]
    let ``finding provenance carries the measurer and accepts remediation`` () =
        let root = hotspotWorkspace ()
        let measured = snapshotIn root [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001" ] "abc123" []
        let snapshotPath = write root "snapshot.json" measured.Output
        let asked = Collections.Generic.List<string>()
        let findingId = str [ "FindingId" ] ((get [ "Findings" ] (parse measured.Output)).AsArray()[0] |> Option.ofObj |> Option.get)
        let finding = Program.execute (context [] asked) [ "provenance"; "finding"; snapshotPath; findingId ]
        Assert.Equal(0, finding.ExitCode)
        let findingPath = write root "finding.json" finding.Output
        let remediated =
            Program.execute
                (context [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "anthropic"; "ROS_TELEMETRY_RUNTIME", "claude-code"; "ROS_EXECUTION_ID", "EXE-20260926T150000000Z-f6f6f6f6" ] asked)
                [ "provenance"; "append"; findingPath; "--operation"; "x-remediated"; "--evidence"; "git:commit/def456" ]
        Assert.Equal(0, remediated.ExitCode)
        let record = parse remediated.Output
        Assert.Equal("github/github-actions", str [ "contributions"; "EXE-dokimos.gh-run-9001"; "actor"; "id" ] record)
        Assert.Equal("anthropic/claude-code", str [ "contributions"; "EXE-20260926T150000000Z-f6f6f6f6"; "actor"; "id" ] record)

    [<Fact>]
    let ``malformed snapshot provenance is rejected, an unsupported major is carried verbatim`` () =
        let root = workspace ()
        let _, _, measured = snapshot [] "abc123" []
        let document = parse measured.Output
        document["Provenance"] <- parse (File.ReadAllText(fixture "invalid/two-created.json"))
        let malformed = write root "malformed.json" (document.ToJsonString())
        match Program.tryReadSnapshot malformed with
        | Error reason -> Assert.StartsWith("malformed-snapshot-provenance:", reason)
        | Ok _ -> failwith "malformed provenance was accepted"

        let future = parse (File.ReadAllText(fixture "unsupported/future-major.json"))
        document["Provenance"] <- future.DeepClone()
        let futurePath = write root "future.json" (document.ToJsonString())
        let asked = Collections.Generic.List<string>()
        let result = Program.execute (context [] asked) [ "compare"; futurePath; futurePath ]
        Assert.Equal(0, result.ExitCode)
        Assert.True(JsonNode.DeepEquals(future, get [ "Provenance"; "sources"; "dokimos:snapshot/app:abc123" ] (parse result.Output)))

    [<Fact>]
    let ``baseline acceptance is recorded as its own record by the accepting actor`` () =
        let asked = Collections.Generic.List<string>()
        let result =
            Program.execute (context [ "ROS_ACTOR_KIND", "human"; "ROS_ACTOR", "kevin" ] asked) [ "provenance"; "accept-baseline"; Path.Combine(repositoryRoot, "baselines", "accepted-snapshot.json") ]
        Assert.Equal(0, result.ExitCode)
        let record = parse result.Output
        Assert.Equal("kevin", str [ "contributions"; "EXE-dokimos.local-test"; "actor"; "id" ] record)

    [<Fact>]
    let ``a provenance member that is not an object is rejected explicitly`` () =
        let root = workspace ()
        let _, _, measured = snapshot [] "abc123" []
        for replacement in [ "\"text\""; "[]"; "42" ] do
            let document = parse measured.Output
            document["Provenance"] <- JsonNode.Parse replacement
            let path = write root "bad.json" (document.ToJsonString())
            Assert.True(Result.isError (Program.tryReadSnapshot path))
