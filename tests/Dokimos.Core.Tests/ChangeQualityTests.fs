namespace Dokimos.Core.Tests

open Xunit
open Dokimos.Core

module ChangeQualityTests =
    [<Fact>]
    let ``large change and dependency growth remain decomposable indicators`` () =
        let summary =
            { FilesChanged=20; LinesAdded=900; LinesDeleted=200
              PublicSurfaceAdded=3; PublicSurfaceRemoved=2
              PackagesAdded=2; PackagesRemoved=0; TestsChanged=0 }
        let indicators = ChangeQuality.indicators 15 1000 summary
        Assert.Contains(LargeChange(20,1100), indicators)
        Assert.Contains(PublicSurfaceChurn(3,2), indicators)
        Assert.Contains(DependencyGrowth 2, indicators)
        Assert.Contains(ProductionWithoutTestChange, indicators)

    [<Fact>]
    let ``small tested change has no forced pathology label`` () =
        let summary =
            { FilesChanged=2; LinesAdded=20; LinesDeleted=4
              PublicSurfaceAdded=0; PublicSurfaceRemoved=0
              PackagesAdded=0; PackagesRemoved=0; TestsChanged=1 }
        Assert.Empty(ChangeQuality.indicators 15 1000 summary)
