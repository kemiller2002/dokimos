namespace RuleProvenance.Cli

open System
open System.Globalization
open System.Runtime.InteropServices
open RuleProvenance

/// The CLI's disposition: stdout carries the JSON report, stderr diagnostics,
/// the exit code the machine-readable outcome (Dokimos conventions).
type CliResult = { Exit: int; Stdout: string; Stderr: string }

type private Args = { Values: Map<string, string list>; Flags: Set<string> }

module Cli =
    let private knownFlags = set [ "--block-on-proven-provenance"; "--help" ]

    let private knownValues =
        set
            [ "--handoff"; "--trace"; "--source"; "--legacy"; "--root"; "--dll"; "--probe-model"; "--depth"
              "--exclude"; "--repository"; "--revision"; "--sdk-version"; "--out"; "--validation-out"; "--prior"
              "--ordo-src"; "--constructing-owner"; "--unmatched-owner"; "--analyzer" ]

    let usage =
        String.concat
            "\n"
            [ "rule-provenance (EXPERIMENTAL; not part of the default Dokimos CLI or CI)"
              ""
              "  audit      --handoff F [--trace F] [--source P]... [--legacy P]... [--root DIR]"
              "             [--dll F | --probe-model reference|mutant:<name>] [--depth N]"
              "             [--block-on-proven-provenance] [--exclude GLOB]... [--repository R] [--revision R]"
              "             [--sdk-version V] [--out F] [--validation-out F] [--analyzer ast|lexical]"
              "             --analyzer: ast (default, v2: F# parse trees + authority matching) or"
              "             lexical (v1). The analyzer is part of validity.configDigest."
              "  integrate  --ordo-src DIR --trace F --dll F --handoff F [--legacy P]... [audit options]"
              "  reuse      --prior AUDIT.json <the same inputs as the audit>"
              "  validate-checker --handoff F [--depth N] [--out F]"
              ""
              "exit codes: 0 pass or pass-with-owner-questions (reuse: reusable), 2 invalid input,"
              "            3 evidence unavailable, 4 fail (reuse: not reusable), 1 unexpected fault" ]

    let private parse (xs: string list) : Result<Args, string> =
        let rec go xs (acc: Args) =
            match xs with
            | [] -> Ok acc
            | f :: rest when knownFlags.Contains f -> go rest { acc with Flags = acc.Flags.Add f }
            | k :: v :: rest when knownValues.Contains k ->
                let existing = acc.Values |> Map.tryFind k |> Option.defaultValue []
                go rest { acc with Values = acc.Values.Add(k, existing @ [ v ]) }
            | k :: [] when knownValues.Contains k -> Error $"{k} needs a value"
            | other :: _ -> Error $"unknown argument: {other}"
        go xs { Values = Map.empty; Flags = Set.empty }

    let private all (a: Args) k = a.Values |> Map.tryFind k |> Option.defaultValue []
    let private one (a: Args) k = all a k |> List.tryLast

    let private invalid msg = { Exit = ExitCode.InvalidInput; Stdout = ""; Stderr = $"rule-provenance: invalid input: {msg}\n{usage}\n" }
    let private unavailable msg = { Exit = ExitCode.EvidenceUnavailable; Stdout = ""; Stderr = $"rule-provenance: evidence unavailable: {msg}\n" }

    let private sequenceResults (rs: Result<'a list, string> list) =
        let errors = rs |> List.choose (function Error e -> Some e | Ok _ -> None)
        if errors.IsEmpty then Ok(rs |> List.collect (function Ok xs -> xs | Error _ -> [])) else Error(String.Join("; ", errors))

    type private Prepared =
        { Inputs: AuditInputs
          Handoff: Handoff }

    /// Read every input named by the arguments. Error carries the exit code.
    let private prepare (a: Args) : Result<Prepared, CliResult> =
        let root = one a "--root" |> Option.defaultValue "."
        let depth =
            match one a "--depth" with
            | None -> Ok Oracle.defaultBound.Depth
            | Some d ->
                match Int32.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, n when n >= 1 && n <= 8 -> Ok n
                | _ -> Error(invalid "--depth must be an integer from 1 to 8")
        let analyzer =
            match one a "--analyzer" with
            | None -> Ok AuditConfig.defaults.Analyzer
            | Some v ->
                match Analyzer.parse v with
                | Some x -> Ok x
                | None -> Error(invalid $"--analyzer must be 'ast' or 'lexical', not '{v}'")
        match one a "--handoff", depth, analyzer with
        | None, _, _ -> Error(invalid "--handoff is required")
        | _, Error e, _
        | _, _, Error e -> Error e
        | Some handoffPath, Ok depth, Ok analyzer ->
            let sources =
                [ for p in all a "--source" @ all a "--ordo-src" -> Io.collect root Constructed p ]
                @ [ for p in all a "--legacy" -> Io.collect root Legacy p ]
                |> sequenceResults
            let trace =
                match one a "--trace" with
                | None -> Ok None
                | Some t -> Io.readBytes t |> Result.map (fun b -> Some(Io.relative root t, b))
            match Io.readBytes handoffPath, sources, trace with
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error(invalid e)
            | Ok handoffBytes, Ok sources, Ok trace ->
                match Inputs.decodeHandoff handoffBytes with
                | Error es -> Error(invalid (String.Join("; ", es)))
                | Ok handoff ->
                    let spec = Oracle.specOf handoff
                    let probe =
                        match one a "--dll", one a "--probe-model" with
                        | Some _, Some _ -> Error(invalid "--dll and --probe-model are exclusive")
                        | Some dll, None ->
                            match Io.readBytes dll with
                            | Error e -> Error(unavailable e)
                            | Ok bytes ->
                                match ProbeLoader.load dll with
                                | Error es -> Error(unavailable (String.Join("; ", es)))
                                | Ok probe ->
                                    let rel = Io.relative root dll
                                    Ok(Some(ProbeTarget.ofProbe ("dll:" + rel) rel probe), Some(Digest.ofBytes bytes))
                        | None, Some "reference" ->
                            match OracleValidation.reference spec with
                            | Some p -> Ok(Some(ProbeTarget.ofProbe "builtin:reference" "builtin:reference" p), None)
                            | None -> Error(invalid "the handoff has no accepted policy-limit decision for the reference model")
                        | None, Some m when m.StartsWith "mutant:" ->
                            let name = m.Substring 7
                            match OracleValidation.mutant spec name with
                            | Some p -> Ok(Some(ProbeTarget.ofProbe ("builtin:" + m) ("builtin:" + m) p), None)
                            | None -> Error(invalid $"unknown mutant '{name}'")
                        | None, Some other -> Error(invalid $"unknown --probe-model '{other}'")
                        | None, None -> Ok(None, None)
                    probe
                    |> Result.map (fun (probe, dependency) ->
                        let cfg =
                            { AuditConfig.defaults with
                                BlockOnProvenProvenance = a.Flags.Contains "--block-on-proven-provenance"
                                Excludes = all a "--exclude"
                                Repository = one a "--repository" |> Option.defaultValue AuditConfig.defaults.Repository
                                Revision = one a "--revision" |> Option.defaultValue AuditConfig.defaults.Revision
                                ConstructingOwner = one a "--constructing-owner" |> Option.defaultValue AuditConfig.defaults.ConstructingOwner
                                UnmatchedOwner = one a "--unmatched-owner" |> Option.defaultValue AuditConfig.defaults.UnmatchedOwner
                                Analyzer = analyzer }
                        { Handoff = handoff
                          Inputs =
                            { Config = cfg
                              HandoffBytes = handoffBytes
                              Trace = trace
                              Sources = sources
                              Probe = probe
                              Bound = { Oracle.defaultBound with Depth = depth }
                              DependencyDigest = dependency
                              SdkVersion = one a "--sdk-version" |> Option.defaultValue RuntimeInformation.FrameworkDescription } })

    let private emit (a: Args) (json: string) =
        match one a "--out" with
        | Some path ->
            Io.writeText path json
            ""
        | None -> json

    let private summaryLine (r: AuditReport) =
        let blocking = r.Findings |> List.filter (fun f -> f.Blocking) |> List.length
        let invariants =
            r.Invariants
            |> List.map (fun i -> i.Id + "=" + (match i.Verdict with HoldsWithinBound -> "holds" | Violated _ -> "violated" | NotChecked -> "not-checked"))
        $"rule-provenance: {Verdict.wire r.Summary.Verdict}; findings={r.Findings.Length} proven={r.Summary.Proven} ownerQuestions={r.Summary.OwnerQuestions} blocking={blocking}; invariants: {String.Join(' ', invariants)}\n"

    let private audit (a: Args) =
        match prepare a with
        | Error e -> e
        | Ok p ->
            match Audit.run p.Inputs with
            | Error es -> invalid (String.Join("; ", es))
            | Ok(report, validation) ->
                validation
                |> Option.iter (fun v ->
                    one a "--validation-out" |> Option.iter (fun path -> Io.writeText path (Report.validationToJson v p.Inputs.Bound)))
                { Exit = Report.exitCode report
                  Stdout = emit a (Report.toJson report)
                  Stderr = summaryLine report }

    let private reuse (a: Args) =
        match one a "--prior" with
        | None -> invalid "--prior is required"
        | Some prior ->
            match Io.readBytes prior |> Result.mapError invalid |> Result.bind (fun b -> Inputs.decodePriorValidity b |> Result.mapError (fun es -> invalid (String.Join("; ", es)))) with
            | Error e -> e
            | Ok priorValidity ->
                match prepare a with
                | Error e -> e
                | Ok p ->
                    let result = Reuse.isReusable priorValidity (Audit.validity p.Inputs)
                    { Exit = (match result with Ok() -> ExitCode.Continue | Error _ -> ExitCode.PolicyFailure)
                      Stdout = emit a (Reuse.toJson result)
                      Stderr = (match result with Ok() -> "rule-provenance: prior audit reusable\n" | Error rs -> "rule-provenance: prior audit NOT reusable: " + String.Join("; ", rs |> List.map Reuse.describe) + "\n") }

    let private validateChecker (a: Args) =
        match one a "--handoff" |> Option.map Io.readBytes with
        | None -> invalid "--handoff is required"
        | Some(Error e) -> invalid e
        | Some(Ok bytes) ->
            match Inputs.decodeHandoff bytes with
            | Error es -> invalid (String.Join("; ", es))
            | Ok h ->
                let depth = one a "--depth" |> Option.bind (fun d -> match Int32.TryParse d with | true, n -> Some n | _ -> None) |> Option.defaultValue Oracle.defaultBound.Depth
                let bound = { Oracle.defaultBound with Depth = depth }
                let v = OracleValidation.validate (Oracle.specOf h) bound
                { Exit = (if v.Validated then ExitCode.Continue else ExitCode.PolicyFailure)
                  Stdout = emit a (Report.validationToJson v bound)
                  Stderr = $"rule-provenance: checker {v.Note}\n" }

    let run (argv: string list) : CliResult =
        let dispatch command rest =
            match parse rest with
            | Error e -> invalid e
            | Ok a when a.Flags.Contains "--help" -> { Exit = ExitCode.Continue; Stdout = usage + "\n"; Stderr = "" }
            | Ok a -> command a
        try
            match argv with
            | "audit" :: rest -> dispatch audit rest
            | "integrate" :: rest ->
                dispatch
                    (fun a ->
                        match one a "--ordo-src", one a "--trace", one a "--dll" with
                        | Some _, Some _, Some _ -> audit a
                        | _ -> invalid "integrate needs --ordo-src, --trace and --dll")
                    rest
            | "reuse" :: rest -> dispatch reuse rest
            | "validate-checker" :: rest -> dispatch validateChecker rest
            | [ "--help" ]
            | [] -> { Exit = ExitCode.Continue; Stdout = usage + "\n"; Stderr = "" }
            | other :: _ -> invalid $"unknown command '{other}'"
        with ex ->
            { Exit = ExitCode.UnexpectedFault; Stdout = ""; Stderr = $"rule-provenance: unexpected fault: {ex}\n" }

module Program =
    [<EntryPoint>]
    let main argv =
        let r = Cli.run (List.ofArray argv)
        if r.Stdout <> "" then Console.Out.Write r.Stdout
        if r.Stderr <> "" then Console.Error.Write r.Stderr
        r.Exit
