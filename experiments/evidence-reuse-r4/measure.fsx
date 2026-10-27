// Replays the Rehearsal 4 commit history through Dokimos evidence reuse
// (dokimos.evidence-reuse-plan/1 semantics) and counts how many evidence
// producers would have run versus the four that ran on every push.
//
// usage: dotnet fsi measure.fsx <r4-repo> <first-commit> <last-commit> [workflow|suite]
// (build src/Dokimos.Core first; the script loads its Debug net8.0 assembly)
// Each commit is treated as one push whose prior evidence is the previous
// commit's (every producer assumed to have passed). Source digests are
// content addresses of the committed blobs (git ls-tree), so the identity is
// computed from real tree contents; dependencies, configuration and tool
// versions are held constant (they are files inside the mapped sources here:
// lock files, project files and workflow definitions).
let args = fsi.CommandLineArgs |> Array.skip 1
#r "../../src/Dokimos.Core/bin/Debug/net8.0/Dokimos.Core.dll"
open System
open System.Diagnostics
open Dokimos.Core
open Dokimos.Core.EvidenceReuse

let repo, first, last = args.[0], args.[1], args.[2]

let git (a: string list) =
    let info = ProcessStartInfo("git", RedirectStandardOutput = true, UseShellExecute = false)
    "-C" :: repo :: a |> List.iter info.ArgumentList.Add
    use p = Process.Start info
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    out

let commits =
    git [ "rev-list"; "--first-parent"; "--reverse"; first + ".." + last ]
    |> fun s -> s.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

let tree (c: string) =
    git [ "ls-tree"; "-r"; c ]
    |> fun s -> s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun line ->
        let tab = line.IndexOf '\t'
        let blob = line.Substring(0, tab).Split(' ').[2]
        line.Substring(tab + 1), "sha256:" + Convert.ToHexString(Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes blob)).ToLowerInvariant())
    |> Map.ofArray

let changed (c: string) =
    git [ "diff-tree"; "--no-commit-id"; "--name-only"; "-r"; c + "^"; c ]
    |> fun s -> s.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.toList

// Declared producer-to-input mappings, derived from what each R4 workflow and
// suite reads. Mappings are deliberately broad (all of src/** for every
// application suite): an over-broad mapping costs reruns, never correctness.
let common = [ "App.slnx"; "Directory.Build.props"; "NuGet.config"; "package.json"; "package-lock.json"; ".conditor/**" ]
let ci = ".github/workflows/build-and-test.yml"
let foundations =
    "echelon-foundations", ContentAddressed, ".github/workflows/echelon-foundations.yml",
    common @ [ ".echelon/**"; "src/**"; "**/*.fsproj"; "vendor/**"; "limen.config.json"; "aegis-boundaries.json" ]
// praxis validate checks attribution of the pushed commit range, so its
// evidence is per-commit by construction.
let praxis = "praxis-validation", PerCommit, ".github/workflows/praxis-validation.yml", [ "**" ]
let live = "live-provider-suite", Live, ci, common @ [ "src/**"; "tests/browser/**" ]
let workflowLevel =
    [ ("build-and-test", ContentAddressed, ci,
       common @ [ "src/**"; "tests/**"; "scripts/**"; "vendor/**"; "acceptance/**"; "tools/acceptance/**"; "playwright.config.js"; "tsconfig.json"; "limen.config.json"; "aegis-boundaries.json" ])
      live; foundations; praxis ]
let suiteLevel =
    [ ("dotnet-tests", ContentAddressed, ci, common @ [ "src/**"; "tests/Causa.Core.Tests/**"; "tests/App.Engine.Tests/**"; "tests/fixtures/**" ])
      ("core-checks", ContentAddressed, ci, common @ [ "src/**"; "tests/Causa.Core.Checks/**"; "tests/fixtures/**" ])
      ("negative-control", ContentAddressed, "scripts/negative-control.mjs", common @ [ "src/**"; "tests/**" ])
      ("browser-suite", ContentAddressed, "tests/browser/app.spec.js",
       common @ [ "src/**"; "tests/browser/support/**"; "tests/fixtures/**"; "scripts/build-site.mjs"; "scripts/serve.mjs"; "playwright.config.js"; "vendor/**"; "limen.config.json"; "tsconfig.json"; ci ])
      ("lock-gate", ContentAddressed, "scripts/check-lock.mjs", common @ [ ci ])
      ("site-scan", ContentAddressed, "scripts/build-site.mjs", common @ [ "src/**"; "vendor/**"; "limen.config.json"; ci ])
      live; foundations; praxis ]
let producers = if args.Length > 3 && args.[3] = "suite" then suiteLevel else workflowLevel

let request (files: Map<string, string>) (id, freshness, contract, patterns) =
    let present = patterns |> List.filter (fun p -> files |> Map.exists (fun f _ -> matches [ p ] f))
    let sources = files |> Map.filter (fun f _ -> matches present f)
    { TestId = id
      Freshness = freshness
      Patterns = Some(contract :: patterns)
      Inputs =
        if sources.IsEmpty || not (files.ContainsKey contract) then Error "unresolved"
        else
            Ok { Sources = sources; Dependencies = Map.empty; TestContract = files.[contract]
                 Configuration = Map [ ("Configuration", "Release") ]; ToolVersion = "r4-ci" } }

let mutable priors: PriorEvidence list = []
let mutable rows = []
for c in commits do
    let files = tree c
    let p = plan c (Some(changed c)) [] [] (producers |> List.map (request files)) priors
    let ran = p.Tests |> List.filter isRun |> List.map _.TestId
    rows <- (c.Substring(0, 7), ran, (changed c).Length) :: rows
    priors <-
        p.Tests
        |> List.choose (fun t ->
            match t.Decision, t.CurrentInputs with
            | Reuse prior, _ -> Some prior
            | Run, Some inputs ->
                Some { EvidenceId = t.TestId + "@" + c; TestId = t.TestId; Outcome = OutcomePassed; ProducedAtRevision = Some c
                       Freshness = Some t.Freshness; EvidenceIdentity = Some(identity inputs); Inputs = Some inputs }
            | Run, None -> None)

let rows' = List.rev rows
for (c, ran, n) in rows' do printfn "%s files=%3d run=%s" c n (String.concat "," ran)
let total = rows'.Length * producers.Length
let selected = rows' |> List.sumBy (fun (_, ran, _) -> ran.Length)
printfn "commits=%d producer-runs-baseline=%d selected=%d reused=%d" rows'.Length total selected (total - selected)
for (id, _, _, _) in producers do
    printfn "  %-22s ran %d of %d" id (rows' |> List.filter (fun (_, ran, _) -> List.contains id ran) |> List.length) rows'.Length
