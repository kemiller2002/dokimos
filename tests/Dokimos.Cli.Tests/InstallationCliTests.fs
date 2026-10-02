namespace Dokimos.Cli.Tests

open System.IO
open Xunit

/// DOK-OPS-011: Conditor lifecycle operations (init, verify, doctor).
module InstallationCliTests =
    let pinned root =
        [ "init"; "--root"; root; "--version"; "0.1.0"; "--action-ref"; String.replicate 40 "a"; "--source"; "src" ]

    [<Fact>]
    let ``init is idempotent and never overwrites existing files`` () =
        let root = Support.tempDirectory ()
        let first = Support.run (pinned root)
        Assert.Equal(0, first.ExitCode)
        Assert.Contains("\"created\"", first.Stdout.Value)
        let second = Support.run (pinned root)
        Assert.DoesNotContain("\"created\"", second.Stdout.Value)
        File.WriteAllText(Path.Combine(root, ".dokimos/policy.json"), """{"schemaVersion":"1.1.0","baseline":"custom"}""")
        let third = Support.run (pinned root)
        Assert.Contains("preserved-existing", third.Stdout.Value)
        Assert.Contains("custom", File.ReadAllText(Path.Combine(root, ".dokimos/policy.json")))

    [<Fact>]
    let ``verify passes on a fresh installation and the default policy is valid`` () =
        let root = Support.tempDirectory ()
        Support.run (pinned root) |> ignore
        let verify = Support.run [ "verify"; "--root"; root ]
        Assert.Equal(0, verify.ExitCode)
        Support.assertSchemaValid "dokimos-policy.schema.json" (File.ReadAllText(Path.Combine(root, ".dokimos/policy.json")))

    [<Fact>]
    let ``doctor reports an unpinned workflow with remediation and exit 3`` () =
        let root = Support.tempDirectory ()
        Support.run (pinned root) |> ignore
        let workflow = Path.Combine(root, ".github/workflows/dokimos.yml")
        File.WriteAllText(workflow, File.ReadAllText(workflow).Replace(String.replicate 40 "a", "main"))
        let doctor = Support.run [ "doctor"; "--root"; root ]
        Assert.Equal(3, doctor.ExitCode)
        Assert.Contains("Pin `uses:` to a full commit SHA", doctor.Stdout.Value)

    [<Fact>]
    let ``init refuses floating versions and branch refs`` () =
        let root = Support.tempDirectory ()
        let floating = Support.run [ "init"; "--root"; root; "--version"; "latest"; "--action-ref"; "main" ]
        Assert.Equal(2, floating.ExitCode)
        Assert.Contains("exact release version", floating.Stderr.Value)
        Assert.False(File.Exists(Path.Combine(root, ".dokimos/policy.json")))


    [<Fact>]
    let ``status is read-only and reports a healthy installation`` () =
        let root = Support.tempDirectory ()
        Support.run (pinned root) |> ignore
        let status = Support.run [ "status"; "--root"; root ]
        Assert.Equal(0, status.ExitCode)
        Assert.Contains("\"Command\": \"status\"", status.Stdout.Value)
        Assert.Contains("\"Healthy\": true", status.Stdout.Value)

    [<Fact>]
    let ``upgrade changes only Dokimos-owned files and preserves policy`` () =
        let root = Support.tempDirectory ()
        Support.run (pinned root) |> ignore
        let policy = Path.Combine(root, ".dokimos/policy.json")
        let custom = """{"schemaVersion":"1.1.0","baseline":"custom","requiredEvidence":[],"ratchets":[],"thresholds":[],"observedNotRatcheted":[],"unavailableBehavior":"not-evaluated","suppressions":[]}"""
        File.WriteAllText(policy, custom)

        let nextRef = String.replicate 40 "b"
        let first = Support.run [ "upgrade"; "--root"; root; "--version"; "0.2.0"; "--action-ref"; nextRef; "--source"; "src" ]
        Assert.Equal(0, first.ExitCode)
        Assert.Equal(custom, File.ReadAllText(policy))
        let workflow = File.ReadAllText(Path.Combine(root, ".github/workflows/dokimos.yml"))
        Assert.Contains("version: 0.2.0", workflow)
        Assert.Contains(nextRef, workflow)
        let record = File.ReadAllText(Path.Combine(root, ".dokimos/installation.json"))
        Assert.Contains("\"ConfigurationVersion\": 2", record)
        Assert.Contains("\"SchemaVersion\": \"1.1.0\"", record)

        let second = Support.run [ "upgrade"; "--root"; root; "--version"; "0.2.0"; "--action-ref"; nextRef; "--source"; "src" ]
        Assert.Equal(0, second.ExitCode)
        Assert.Contains("\"unchanged\"", second.Stdout.Value)
        Assert.Contains("\"preserved-user-owned\"", second.Stdout.Value)

    [<Fact>]
    let ``upgrade refuses a workflow outside Dokimos ownership`` () =
        let root = Support.tempDirectory ()
        Support.run (pinned root) |> ignore
        File.WriteAllText(Path.Combine(root, ".github/workflows/dokimos.yml"), "name: user-owned\n")
        let result = Support.run [ "upgrade"; "--root"; root; "--version"; "0.2.0"; "--action-ref"; String.replicate 40 "b" ]
        Assert.Equal(3, result.ExitCode)
        Assert.Contains("installation-ownership-conflict", result.Stderr.Value)
