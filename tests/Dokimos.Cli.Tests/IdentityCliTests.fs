namespace Dokimos.Cli.Tests

open System.IO
open System.Text.Json
open Xunit

/// Issue #17 conformance: this repository's ros.json must identify Dokimos.
module IdentityCliTests =
    [<Fact>]
    let ``this repository's ros.json identity is Dokimos`` () =
        let result = Support.run [ "identity"; "--root"; Support.repositoryRoot; "--expected"; "dokimos" ]
        Assert.True((0 = result.ExitCode), defaultArg result.Stdout "" + defaultArg result.Stderr "")
        use doc = JsonDocument.Parse result.Stdout.Value
        Assert.Equal("consistent", doc.RootElement.GetProperty("Verdict").Text)
        Assert.Equal("dokimos", doc.RootElement.GetProperty("Declared").GetProperty("RepositoryId").Text)

    [<Fact>]
    let ``a copied identity fails adoption with exit 4`` () =
        let root = Support.tempDirectory ()
        Support.write root "ros.json" """{"name":"echelon-design-system","project":"Echelon Design System","repository":{"id":"echelon-design-system","type":"application"}}""" |> ignore
        Support.write root ".ros/installation.json" """{"project":"Dokimos","project_slug":"dokimos"}""" |> ignore
        let result = Support.run [ "identity"; "--root"; root; "--remote"; "https://github.com/kemiller2002/dokimos.git" ]
        Assert.Equal(4, result.ExitCode)
        Assert.Contains("\"mismatch\"", result.Stdout.Value)

    [<Fact>]
    let ``no external identity evidence is undetermined with exit 3`` () =
        let root = Support.tempDirectory ()
        Support.write root "ros.json" """{"name":"dokimos","project":"Dokimos","repository":{"id":"dokimos"}}""" |> ignore
        let result = Support.run [ "identity"; "--root"; Path.Combine(root) ]
        Assert.Equal(3, result.ExitCode)
