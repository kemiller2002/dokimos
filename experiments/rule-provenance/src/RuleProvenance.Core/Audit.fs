namespace RuleProvenance

open System

/// The probe target, already adapted to the Probe record. Existential over the
/// state type so callers can supply any implementation.
type ProbeTarget =
    { Label: string
      /// Relative path or `builtin:<model>` used as the finding location.
      LocationPath: string
      Run: OracleSpec -> Bound -> OracleRun }

module ProbeTarget =
    let ofProbe (label: string) (location: string) (probe: Probe<'S>) =
        { Label = label
          LocationPath = location
          Run = fun spec bound -> Oracle.run spec bound probe }

/// Everything the audit reads, as data. The CLI does the IO.
type AuditInputs =
    { Config: AuditConfig
      HandoffBytes: byte[]
      /// Relative trace path and its bytes, when a trace is supplied.
      Trace: (string * byte[]) option
      Sources: SourceFile list
      Probe: ProbeTarget option
      Bound: Bound
      /// sha256 of the probe assembly, when one was loaded.
      DependencyDigest: string option
      SdkVersion: string }

module Audit =
    /// Canonical, line-oriented form of the effective configuration. Its digest is
    /// validity.configDigest. Paths are excluded: they are covered by sourceDigest.
    let canonicalConfig (inputs: AuditInputs) =
        let c = inputs.Config
        let inv (d: decimal) = d.ToString(Globalization.CultureInfo.InvariantCulture)
        [ $"blockOnProvenProvenance={c.BlockOnProvenProvenance}"
          $"constructingOwner={c.ConstructingOwner}"
          $"unmatchedOwner={c.UnmatchedOwner}"
          $"excludes={String.Join(',', c.Excludes)}"
          $"repository={c.Repository}"
          $"analyzer={Analyzer.wire c.Analyzer}/{Analyzer.version c.Analyzer}"
          + (match c.Analyzer with
             | Ast -> $"; fcs={AstScanner.FcsVersion}"
             | Lexical -> "")
          $"revision={c.Revision}"
          "probe=" + (inputs.Probe |> Option.map (fun p -> p.Label) |> Option.defaultValue "none")
          $"bound.depth={inputs.Bound.Depth}"
          $"bound.totals={String.Join(',', inputs.Bound.Totals |> List.map inv)}"
          $"bound.amounts={String.Join(',', inputs.Bound.Amounts |> List.map inv)}"
          $"bound.customers={String.Join(',', inputs.Bound.Customers)}" ]
        |> String.concat "\n"

    let validity (inputs: AuditInputs) : Validity =
        { SourceDigest = Digest.ofSources inputs.Sources
          HandoffDigest = Digest.ofBytes inputs.HandoffBytes
          TraceDigest = inputs.Trace |> Option.map (snd >> Digest.ofBytes) |> Option.defaultValue Digest.empty
          ToolName = Tool.Name
          ToolVersion = Tool.Version
          ConfigDigest = Digest.ofString (canonicalConfig inputs)
          SdkVersion = inputs.SdkVersion
          DependencyDigest = inputs.DependencyDigest }

    let private invariantMethod = "bounded exhaustive exploration (breadth-first) of the Invoice.Slice.Probe API: every command sequence up to the depth bound from each start total; a refused command does not extend a sequence (a refusal leaves the state unchanged)"

    /// Oracle results as schema invariants plus invariant-violation findings.
    let private invariants (cfg: AuditConfig) (h: Handoff) (inputs: AuditInputs) =
        let spec = Oracle.specOf h
        match inputs.Probe with
        | None ->
            let results =
                h.Invariants
                |> List.map (fun i ->
                    { Id = i.Id
                      Verdict = NotChecked
                      Method = "not run: no probe target supplied (audit-only mode)"
                      Bound = None })
            results, [], None
        | Some target ->
            let validation = OracleValidation.validate spec inputs.Bound
            let run = target.Run spec inputs.Bound
            let boundText = $"{Oracle.describeBound inputs.Bound}; sequences explored={run.SequencesExplored}; states visited={run.StatesVisited}"
            let methodText = $"{invariantMethod}; target {target.Label}; checker {validation.Note}"
            let derived id =
                h.Invariants |> List.tryFind (fun i -> i.Id = id) |> Option.map (fun i -> i.DerivedFrom) |> Option.defaultValue []
            let results =
                run.Results
                |> List.map (fun r -> { Id = r.Id; Verdict = r.Verdict; Method = methodText; Bound = Some boundText })
            let findings =
                run.Results
                |> List.choose (fun r ->
                    match r.Verdict with
                    | Violated cx ->
                        let checkerNote =
                            if validation.Validated then "The checker is validated against counterexamples, so this violation blocks."
                            else "The checker is NOT validated, so this violation does not block."
                        Some
                            { Class = InvariantViolation
                              Certainty = Proven
                              Location = { Path = target.LocationPath; Line = None; Symbol = Some "Invoice.Slice.Probe.step" }
                              AuthorityRefs = (r.Id :: derived r.Id) |> List.distinct
                              Message = $"{r.Id} violated within the bound. Counterexample: {String.Join(' ', cx)}. {checkerNote}"
                              Owner = cfg.ConstructingOwner
                              FromValidatedChecker = validation.Validated }
                    | _ -> None)
            results, findings, Some validation

    /// The whole audit as a pure function of its inputs.
    let run (inputs: AuditInputs) : Result<AuditReport * OracleValidation.Validation option, string list> =
        let cfg = inputs.Config
        let trace =
            match inputs.Trace with
            | None -> Ok None
            | Some(path, bytes) -> Inputs.decodeTrace bytes |> Result.map (fun t -> Some(path, t))
        match Inputs.decodeHandoff inputs.HandoffBytes, trace with
        | Error e1, Error e2 -> Error(e1 @ e2)
        | Error e, _
        | _, Error e -> Error e
        | Ok h, Ok trace ->
            let v = validity inputs
            let ofRole role = inputs.Sources |> List.filter (fun f -> f.Role = role)
            let scanned =
                match cfg.Analyzer with
                | Lexical -> Ok(Scanner.scanAll (ofRole Constructed), Scanner.scanAll (ofRole Legacy), Provenance.legacy)
                | Ast ->
                    match AstScanner.scanAll (ofRole Constructed), AstScanner.scanAll (ofRole Legacy) with
                    | Ok c, Ok l -> Ok(c, l, Provenance.legacyAst)
                    | Error e1, Error e2 -> Error(e1 @ e2)
                    | Error e, _
                    | _, Error e -> Error e
            match scanned with
            | Error es -> Error es
            | Ok(constructed, legacy, legacyAnalysis) ->
                let invariantResults, invariantFindings, validation = invariants cfg h inputs
                let raw =
                    Provenance.constructed cfg h v.HandoffDigest trace constructed
                    @ legacyAnalysis cfg h legacy
                    @ invariantFindings
                let findings = Report.finalize cfg raw
                let report =
                    { Subject =
                        { Repository = cfg.Repository
                          Revision = cfg.Revision
                          Paths =
                            (inputs.Sources |> List.map (fun f -> f.RelativePath))
                            @ (inputs.Trace |> Option.map fst |> Option.toList)
                            |> List.distinct
                            |> List.sort }
                      Validity = v
                      Invariants = invariantResults
                      Findings = findings
                      Summary = Report.summarize findings }
                Ok(report, validation)
