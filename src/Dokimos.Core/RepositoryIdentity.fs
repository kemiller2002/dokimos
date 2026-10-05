namespace Dokimos.Core

open System
open Dokimos.Core.Json

/// The identity a governed repository declares about itself in `ros.json`
/// (the Praxis/ROS adoption record). Dokimos only reads it as evidence; it
/// never writes it and does not depend on Praxis to interpret it.
type DeclaredIdentity =
    { Name: string
      Project: string
      RepositoryId: string }

/// Independent sources of the repository's real identity. Each is optional:
/// an absent source is reported as unavailable, never as agreement.
type IdentityEvidence =
    { Expected: string option
      RemoteUrl: string option
      InstallationSlug: string option }

type IdentityCheckState =
    | IdentityAgrees
    | IdentityDisagrees
    | IdentityUnavailable

type IdentityCheck =
    { Check: string
      State: IdentityCheckState
      Detail: string
      Remediation: string option }

type IdentityVerdict =
    | IdentityConsistent
    | IdentityCopied
    | IdentityUndetermined

/// Detects a repository identity copied from another project (issue #17):
/// the declared `ros.json` identity must agree with the repository it sits in.
module RepositoryIdentity =
    let private normalize (text: string) =
        text.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-')

    /// The repository name from a Git remote URL (https, ssh or scp-style,
    /// with or without `.git` and a trailing slash).
    let slugOfRemote (url: string) =
        let trimmed = url.Trim().TrimEnd('/')
        let withoutGit = if trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) then trimmed.Substring(0, trimmed.Length - 4) else trimmed
        match withoutGit.Split([| '/'; ':' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryLast with
        | Some name when not (String.IsNullOrWhiteSpace name) -> Some(normalize name)
        | _ -> None

    let private declaredDecoder: Decoder<DeclaredIdentity> =
        fun e ->
            result {
                let! name = field "name" nonEmptyString e
                let! project = field "project" nonEmptyString e
                let! id = field "repository" (field "id" nonEmptyString) e
                return { Name = name; Project = project; RepositoryId = id }
            }

    let readDeclared (text: string) =
        match parse declaredDecoder text with
        | Ok declared -> Ok declared
        | Error NullDocument -> Error "ros.json is JSON null"
        | Error (Malformed m) -> Error("ros.json is malformed: " + m)
        | Error (Invalid m) -> Error("ros.json is invalid: " + m)

    let readInstallationSlug (text: string) =
        match parse (field "project_slug" nonEmptyString) text with
        | Ok slug -> Ok slug
        | Error _ -> Error ".ros/installation.json has no project_slug"

    let private compareWith label (declared: string) (source: string option) =
        match source with
        | None ->
            { Check = label
              State = IdentityUnavailable
              Detail = $"{label} is unavailable; agreement was not established."
              Remediation = Some $"Supply {label} (for example with --expected <id>) so the identity can be verified." }
        | Some actual when normalize actual = normalize declared ->
            { Check = label; State = IdentityAgrees; Detail = $"ros.json repository.id '{declared}' agrees with {label} '{actual}'."; Remediation = None }
        | Some actual ->
            { Check = label
              State = IdentityDisagrees
              Detail = $"ros.json repository.id '{declared}' disagrees with {label} '{actual}'. The identity was probably copied from another project."
              Remediation = Some "Correct name, project and repository.id in ros.json to this repository's identity; keep historical records unchanged and record a dated correction." }

    let private internalChecks (declared: DeclaredIdentity) =
        let nameCheck =
            if normalize declared.Name = normalize declared.RepositoryId then
                { Check = "name-matches-repository-id"; State = IdentityAgrees; Detail = $"name '{declared.Name}' matches repository.id."; Remediation = None }
            else
                { Check = "name-matches-repository-id"
                  State = IdentityDisagrees
                  Detail = $"name '{declared.Name}' differs from repository.id '{declared.RepositoryId}'."
                  Remediation = Some "Make ros.json name and repository.id the same repository slug." }
        let projectCheck =
            if normalize declared.Project = normalize declared.RepositoryId then
                { Check = "project-matches-repository-id"; State = IdentityAgrees; Detail = $"project '{declared.Project}' names repository.id."; Remediation = None }
            else
                { Check = "project-matches-repository-id"
                  State = IdentityDisagrees
                  Detail = $"project '{declared.Project}' does not name repository.id '{declared.RepositoryId}'."
                  Remediation = Some "Make ros.json project the display name of this repository." }
        [ nameCheck; projectCheck ]

    /// Pure: every check the declared identity must pass. At least one
    /// external source (expected id or Git remote) is required to conclude
    /// the identity is consistent.
    let check (declared: DeclaredIdentity) (evidence: IdentityEvidence) =
        let external =
            [ yield!
                  match evidence.Expected with
                  | Some _ -> [ compareWith "expected-id" declared.RepositoryId evidence.Expected ]
                  | None -> []
              compareWith "git-remote" declared.RepositoryId (evidence.RemoteUrl |> Option.bind slugOfRemote)
              yield!
                  match evidence.InstallationSlug with
                  | Some _ -> [ compareWith "installation-project-slug" declared.RepositoryId evidence.InstallationSlug ]
                  | None -> [] ]
        internalChecks declared @ external

    let verdict (checks: IdentityCheck list) =
        let externalAgreement =
            checks |> List.exists (fun c -> (c.Check = "expected-id" || c.Check = "git-remote") && c.State = IdentityAgrees)
        if checks |> List.exists (fun c -> c.State = IdentityDisagrees) then IdentityCopied
        elif externalAgreement then IdentityConsistent
        else IdentityUndetermined
