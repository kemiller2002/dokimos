namespace Dokimos.Core

type ChangeSummary =
    { FilesChanged: int
      LinesAdded: int
      LinesDeleted: int
      PublicSurfaceAdded: int
      PublicSurfaceRemoved: int
      PackagesAdded: int
      PackagesRemoved: int
      TestsChanged: int }

type ChangeIndicator =
    | LargeChange of files: int * churn: int
    | PublicSurfaceChurn of added: int * removed: int
    | DependencyGrowth of added: int
    | ProductionWithoutTestChange

module ChangeQuality =
    let indicators fileThreshold churnThreshold summary =
        [ let churn = summary.LinesAdded + summary.LinesDeleted
          if summary.FilesChanged >= fileThreshold || churn >= churnThreshold then
              LargeChange(summary.FilesChanged, churn)
          if summary.PublicSurfaceAdded > 0 && summary.PublicSurfaceRemoved > 0 then
              PublicSurfaceChurn(summary.PublicSurfaceAdded, summary.PublicSurfaceRemoved)
          if summary.PackagesAdded > 0 then
              DependencyGrowth summary.PackagesAdded
          if summary.FilesChanged > 0 && summary.TestsChanged = 0 then
              ProductionWithoutTestChange ]
