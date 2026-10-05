namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

/// Issue #17: a repository identity copied from another project is detected.
module RepositoryIdentityTests =
    let dokimos = { Name = "dokimos"; Project = "Dokimos"; RepositoryId = "dokimos" }
    let copied = { Name = "echelon-design-system"; Project = "Echelon Design System"; RepositoryId = "echelon-design-system" }
    let noEvidence = { Expected = None; RemoteUrl = None; InstallationSlug = None }

    [<Theory>]
    [<InlineData("https://github.com/kemiller2002/dokimos.git")>]
    [<InlineData("https://github.com/kemiller2002/dokimos")>]
    [<InlineData("git@github.com:kemiller2002/dokimos.git")>]
    [<InlineData("http://127.0.0.1:1234/git/kemiller2002/dokimos/")>]
    let ``remote slug is the repository name`` (url: string) =
        Assert.Equal(Some "dokimos", RepositoryIdentity.slugOfRemote url)

    [<Fact>]
    let ``matching identity is consistent`` () =
        let checks = RepositoryIdentity.check dokimos { noEvidence with RemoteUrl = Some "https://github.com/kemiller2002/dokimos.git"; InstallationSlug = Some "dokimos" }
        Assert.Equal(IdentityConsistent, RepositoryIdentity.verdict checks)

    [<Fact>]
    let ``identity copied from another project is detected against the remote`` () =
        let checks = RepositoryIdentity.check copied { noEvidence with RemoteUrl = Some "https://github.com/kemiller2002/dokimos.git" }
        Assert.Equal(IdentityCopied, RepositoryIdentity.verdict checks)
        Assert.Contains(checks, fun c -> c.Check = "git-remote" && c.State = IdentityDisagrees)

    [<Fact>]
    let ``identity copied from another project is detected against the installation slug and expected id`` () =
        let checks = RepositoryIdentity.check copied { noEvidence with Expected = Some "dokimos"; InstallationSlug = Some "dokimos" }
        Assert.Equal(IdentityCopied, RepositoryIdentity.verdict checks)

    [<Fact>]
    let ``internally inconsistent declaration is a mismatch even when the id agrees`` () =
        let mixed = { dokimos with Project = "Echelon Design System" }
        Assert.Equal(IdentityCopied, RepositoryIdentity.verdict (RepositoryIdentity.check mixed { noEvidence with Expected = Some "dokimos" }))

    [<Fact>]
    let ``without external evidence the identity is undetermined, never consistent`` () =
        Assert.Equal(IdentityUndetermined, RepositoryIdentity.verdict (RepositoryIdentity.check dokimos noEvidence))
