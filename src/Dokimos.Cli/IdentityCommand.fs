namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core

/// `dokimos identity`: repository-identity conformance (issue #17).
module IdentityCommand =
    let private readRelative root (relative: string) =
        let path = Path.Combine(root, relative)
        if File.Exists path then Some(File.ReadAllText path) else None

    let private withArgs known args k =
        match Arguments.parse args |> Result.bind (Arguments.allowOnly known) with
        | Ok parsed -> k parsed
        | Error message -> Output.invalid "invalid-arguments" message

    /// Reads `git remote get-url origin` at the repository boundary. Any
    /// failure is absence of evidence, never agreement.
    let gitRemote (root: string) =
        try
            let info = Diagnostics.ProcessStartInfo("git", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
            [ "-C"; root; "remote"; "get-url"; "origin" ] |> List.iter info.ArgumentList.Add
            use proc =
                match Diagnostics.Process.Start info with
                | null -> raise (InvalidOperationException "git did not start")
                | started -> started
            let output = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit()
            if proc.ExitCode = 0 && not (String.IsNullOrWhiteSpace output) then Some(output.Trim()) else None
        with
        | :? ComponentModel.Win32Exception
        | :? InvalidOperationException -> None

    /// Repository-identity conformance (issue #17): `ros.json` must identify
    /// the repository it is in. Exit 0 consistent, 4 copied/mismatched,
    /// 3 undetermined (no external identity evidence).
    let identity args =
        withArgs [ "root"; "expected"; "remote" ] args (fun parsed ->
            let root = Arguments.tryOne "root" parsed |> Option.defaultValue "."
            match readRelative root "ros.json" with
            | None -> Output.unavailable "ros-json-not-found" (Path.Combine(root, "ros.json") + " does not exist")
            | Some text ->
                match RepositoryIdentity.readDeclared text with
                | Error reason -> Output.invalid "ros-json-invalid" reason
                | Ok declared ->
                    let evidence =
                        { Expected = Arguments.tryOne "expected" parsed
                          RemoteUrl = Arguments.tryOne "remote" parsed |> Option.orElse (gitRemote root)
                          InstallationSlug =
                            readRelative root ".ros/installation.json"
                            |> Option.bind (RepositoryIdentity.readInstallationSlug >> Result.toOption) }
                    let checks = RepositoryIdentity.check declared evidence
                    let verdict = RepositoryIdentity.verdict checks
                    let tag =
                        function
                        | IdentityAgrees -> "agrees"
                        | IdentityDisagrees -> "disagrees"
                        | IdentityUnavailable -> "unavailable"
                    let verdictTag, code =
                        match verdict with
                        | IdentityConsistent -> "consistent", ExitCodes.Continue
                        | IdentityCopied -> "mismatch", ExitCodes.PolicyFailure
                        | IdentityUndetermined -> "undetermined", ExitCodes.EvidenceUnavailable
                    Output.dataWithCode code (
                        Contracts.serialize
                            {| Contract = "dokimos.repository-identity"
                               SchemaVersion = "1.0.0"
                               DokimosVersion = DokimosInfo.version
                               Verdict = verdictTag
                               ExitCode = code
                               Declared = {| Name = declared.Name; Project = declared.Project; RepositoryId = declared.RepositoryId |}
                               Checks =
                                checks
                                |> List.map (fun c -> {| Check = c.Check; State = tag c.State; Detail = c.Detail; Remediation = c.Remediation |}) |}
                    ))
