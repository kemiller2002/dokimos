module RuleProvenance.Tests.Support

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text.Json
open RuleProvenance
open RuleProvenance.Cli

/// The experiment directory (holds contracts/ and fixtures/).
let root =
    let rec up (dir: DirectoryInfo | null) =
        match dir with
        | null -> failwith "experiment root not found"
        | d when File.Exists(Path.Combine(d.FullName, "contracts", "fixtures", "invoice-slice", "handoff.json")) -> d.FullName
        | d -> up d.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let path (rel: string) = Path.Combine(root, rel)
let fixture = path "contracts/fixtures/invoice-slice"
let handoffPath = Path.Combine(fixture, "handoff.json")
let legacyPath = Path.Combine(fixture, "legacy", "LegacyPaymentRules.fs")
let auditSchema = path "contracts/schemas/echelon-provenance-audit-0.1.schema.json"
let standinDll = Path.Combine(AppContext.BaseDirectory, "Invoice.Slice.dll")

let sha256File (p: string) = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes p)).ToLowerInvariant()

let tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "rule-provenance-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

/// Run the CLI in-process with `--root <experiment>` unless the caller sets one.
let cli (args: string list) =
    let withRoot = if args |> List.contains "--root" then args else args @ [ "--root"; root ]
    Cli.run withRoot

let audit (extra: string list) = cli ([ "audit"; "--handoff"; handoffPath; "--sdk-version"; "test-sdk" ] @ extra)

let json (r: CliResult) =
    if String.IsNullOrEmpty r.Stdout then failwith $"no stdout (exit {r.Exit}): {r.Stderr}"
    JsonDocument.Parse(r.Stdout).RootElement

let str (e: JsonElement) (name: string) =
    match e.GetProperty(name).GetString() with
    | null -> ""
    | s -> s

let findings (doc: JsonElement) = doc.GetProperty("findings").EnumerateArray() |> List.ofSeq

let invariantVerdicts (doc: JsonElement) =
    doc.GetProperty("invariants").EnumerateArray() |> Seq.map (fun i -> str i "id", str i "verdict") |> Map.ofSeq

let refs (f: JsonElement) =
    match f.TryGetProperty "authorityRefs" with
    | true, a -> a.EnumerateArray() |> Seq.map (fun x -> x.GetString() |> nonNull) |> List.ofSeq
    | _ -> []

/// Validate JSON files against a schema with python3 -I + jsonschema.
let validateSchema (schema: string) (files: string list) =
    let psi = ProcessStartInfo("python3")
    [ "-I"; path "tools/validate_json.py"; schema ] @ files |> List.iter psi.ArgumentList.Add
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    use p = Process.Start psi |> nonNull
    let out = p.StandardOutput.ReadToEnd()
    let err = p.StandardError.ReadToEnd()
    p.WaitForExit()
    p.ExitCode, out + err
