namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module ArchitectureTests =
    [<Fact>]
    let ``forbidden dependency direction is explicit evidence`` () =
        let evidence = { ProjectReferences=[{FromProject="Domain";ToProject="Cli"}]; PackageReferences=[] }
        let violations = Architecture.boundaryViolations [{FromProject="Domain";ForbiddenTarget="Cli"}] evidence
        Assert.Single(violations) |> ignore

    [<Fact>]
    let ``new package dependency is distinguishable from existing dependency`` () =
        let before = { ProjectReferences=[]; PackageReferences=[] }
        let after = { ProjectReferences=[]; PackageReferences=[{Project="Core";Package="Example";Version=Some "1.0"}] }
        let added = Architecture.addedPackages before after
        Assert.Single(added) |> ignore
