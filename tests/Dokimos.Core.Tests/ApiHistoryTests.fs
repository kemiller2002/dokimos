namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module ApiHistoryTests =
    [<Fact>]
    let ``api history distinguishes additions removals and signature changes`` () =
        let before =
            [ {Project="Core";QualifiedName="A.f";Signature="int -> int"}
              {Project="Core";QualifiedName="A.old";Signature="unit -> unit"} ]
        let after =
            [ {Project="Core";QualifiedName="A.f";Signature="string -> int"}
              {Project="Core";QualifiedName="A.new";Signature="unit -> unit"} ]
        let result = Api.compare before after
        Assert.Equal(1,result.Added)
        Assert.Equal(1,result.Removed)
        Assert.Equal(1,result.Changed)
