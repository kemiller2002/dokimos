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
