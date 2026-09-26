namespace Dokimos.Core

type ProjectReference =
    { FromProject: string
      ToProject: string }

type PackageReference =
    { Project: string
      Package: string
      Version: string option }

type ArchitectureEvidence =
    { ProjectReferences: ProjectReference list
      PackageReferences: PackageReference list }

type BoundaryRule =
    { FromProject: string
      ForbiddenTarget: string }

type BoundaryViolation =
    { FromProject: string
      ForbiddenTarget: string }

module Architecture =
    let boundaryViolations rules evidence =
        [ for reference in evidence.ProjectReferences do
            for rule in rules do
                if reference.FromProject = rule.FromProject && reference.ToProject = rule.ForbiddenTarget then
                    yield { FromProject = reference.FromProject; ForbiddenTarget = reference.ToProject } ]

    let addedPackages before after =
        let key x = x.Project, x.Package
        let existing = before.PackageReferences |> List.map key |> Set.ofList
        after.PackageReferences |> List.filter (key >> existing.Contains >> not)

    let removedPackages before after =
        let key x = x.Project, x.Package
        let current = after.PackageReferences |> List.map key |> Set.ofList
        before.PackageReferences |> List.filter (key >> current.Contains >> not)
