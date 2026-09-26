namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module CorrelationLifecycleTests =
    let evidence =
        { Path="A.fs"; Kind=MaintainabilityHotspot
          Signals=[StructuralComplexity 12; FrequentChange 7]
          Explanation="test" }

    [<Fact>]
    let ``unknown evidence never fabricates resolution`` () =
        Assert.Equal(None, CorrelationLifecycle.transition (CorrelationPresent evidence) CorrelationUnknown)

    [<Fact>]
    let ``correlation resolves only when evidence is actually absent`` () =
        Assert.Equal(Some CorrelationResolved, CorrelationLifecycle.transition (CorrelationPresent evidence) CorrelationAbsent)
