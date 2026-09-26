namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module GitHistoryTests =
    [<Fact>]
    let ``git numstat parser produces temporal evidence`` () =
        let text = "commit abc 2026-09-01T10:00:00+00:00\n10\t2\tsrc/A.fs\ncommit def 2026-09-02T10:00:00+00:00\n3\t1\tsrc/A.fs\n"
        let entries = GitHistory.parse text
        let history = GitHistory.summarize entries
        Assert.Equal(2, history["src/A.fs"].CommitCount)
        Assert.Equal(16, history["src/A.fs"].Churn)

    [<Fact>]
    let ``binary numstat remains unavailable to this parser rather than zero`` () =
        let entries = GitHistory.parse "commit abc 2026-09-01T10:00:00+00:00\n-\t-\tasset.bin\n"
        Assert.Empty(entries)
