namespace RuleProvenance

open System.IO
open System.Text
open System.Text.Encodings.Web
open System.Text.Json

/// Blocking policy, finding identity, summary and deterministic serialization of
/// echelon.provenance-audit/0.1.
module Report =
    /// VC-004 R4/R5. uncertain-authority never blocks. A proven finding blocks only
    /// when it comes from a validated checker of a formally specified invariant, or,
    /// for proven provenance findings, when the experiment flag opts in.
    let isBlocking (cfg: AuditConfig) (f: RawFinding) =
        match f.Certainty, f.Class with
        | UncertainAuthority, _ -> false
        | Proven, InvariantViolation -> f.FromValidatedChecker
        | Proven, _ -> f.FromValidatedChecker || cfg.BlockOnProvenProvenance

    let private sortKey (f: RawFinding) =
        f.Location.Path, (f.Location.Line |> Option.defaultValue 0), FindingClass.wire f.Class, (f.Location.Symbol |> Option.defaultValue ""), f.Message

    let finalize (cfg: AuditConfig) (raw: RawFinding list) : Finding list =
        raw
        |> List.sortWith (fun a b -> compare (sortKey a) (sortKey b))
        |> List.mapi (fun i f ->
            { Id = sprintf "RPA-%03d" (i + 1)
              Class = f.Class
              Certainty = f.Certainty
              Blocking = isBlocking cfg f
              Location = f.Location
              AuthorityRefs = f.AuthorityRefs
              Message = f.Message
              Owner = f.Owner })

    let summarize (findings: Finding list) =
        let proven = findings |> List.filter (fun f -> f.Certainty = Proven) |> List.length
        let questions = findings |> List.filter (fun f -> f.Certainty = UncertainAuthority) |> List.length
        let verdict =
            if findings |> List.exists (fun f -> f.Blocking) then Fail
            elif findings.IsEmpty then Pass
            else PassWithOwnerQuestions
        { Verdict = verdict; Proven = proven; OwnerQuestions = questions }

    let exitCode (r: AuditReport) =
        match r.Summary.Verdict with
        | Fail -> ExitCode.PolicyFailure
        | Pass
        | PassWithOwnerQuestions -> ExitCode.Continue

    // ------------------------------------------------------------------ serialization

    let private options =
        JsonWriterOptions(Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n")

    /// Write JSON deterministically: fixed property order, two-space indent, LF, trailing newline.
    let write (emit: Utf8JsonWriter -> unit) =
        use stream = new MemoryStream()
        do
            use w = new Utf8JsonWriter(stream, options)
            emit w
            w.Flush()
        Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    let private strings (w: Utf8JsonWriter) (name: string) (xs: string list) =
        w.WriteStartArray name
        xs |> List.iter w.WriteStringValue
        w.WriteEndArray()

    let writeValidity (w: Utf8JsonWriter) (v: Validity) =
        w.WriteStartObject "validity"
        w.WriteString("sourceDigest", v.SourceDigest)
        w.WriteString("handoffDigest", v.HandoffDigest)
        w.WriteString("traceDigest", v.TraceDigest)
        w.WriteString("toolName", v.ToolName)
        w.WriteString("toolVersion", v.ToolVersion)
        w.WriteString("configDigest", v.ConfigDigest)
        w.WriteString("sdkVersion", v.SdkVersion)
        v.DependencyDigest |> Option.iter (fun d -> w.WriteString("dependencyDigest", d))
        w.WriteEndObject()

    let toJson (r: AuditReport) =
        write (fun w ->
            w.WriteStartObject()
            w.WriteString("schema", "echelon.provenance-audit")
            w.WriteString("schemaVersion", "0.1")
            w.WriteStartObject "subject"
            w.WriteString("repository", r.Subject.Repository)
            w.WriteString("revision", r.Subject.Revision)
            strings w "paths" r.Subject.Paths
            w.WriteEndObject()
            writeValidity w r.Validity
            w.WriteStartArray "invariants"
            for i in r.Invariants do
                w.WriteStartObject()
                w.WriteString("id", i.Id)
                w.WriteString(
                    "verdict",
                    match i.Verdict with
                    | HoldsWithinBound -> "holds-within-bound"
                    | Violated _ -> "violated"
                    | NotChecked -> "not-checked"
                )
                w.WriteString("method", i.Method)
                i.Bound |> Option.iter (fun b -> w.WriteString("bound", b))
                match i.Verdict with
                | Violated cx -> strings w "counterexample" cx
                | _ -> ()
                w.WriteEndObject()
            w.WriteEndArray()
            w.WriteStartArray "findings"
            for f in r.Findings do
                w.WriteStartObject()
                w.WriteString("id", f.Id)
                w.WriteString("class", FindingClass.wire f.Class)
                w.WriteString("certainty", Certainty.wire f.Certainty)
                w.WriteBoolean("blocking", f.Blocking)
                w.WriteStartObject "location"
                w.WriteString("path", f.Location.Path)
                f.Location.Line |> Option.iter (fun l -> w.WriteNumber("line", l))
                f.Location.Symbol |> Option.iter (fun s -> w.WriteString("symbol", s))
                w.WriteEndObject()
                if not f.AuthorityRefs.IsEmpty then strings w "authorityRefs" f.AuthorityRefs
                w.WriteString("message", f.Message)
                w.WriteString("owner", f.Owner)
                w.WriteEndObject()
            w.WriteEndArray()
            w.WriteStartObject "summary"
            w.WriteString("verdict", Verdict.wire r.Summary.Verdict)
            w.WriteNumber("proven", r.Summary.Proven)
            w.WriteNumber("ownerQuestions", r.Summary.OwnerQuestions)
            w.WriteEndObject()
            w.WriteEndObject())

    /// Separate checker-validation report (experiment-local format).
    let validationToJson (v: OracleValidation.Validation) (bound: Bound) =
        write (fun w ->
            w.WriteStartObject()
            w.WriteString("schema", "dokimos.rule-provenance.checker-validation")
            w.WriteString("schemaVersion", "0.1-experimental")
            w.WriteString("bound", Oracle.describeBound bound)
            w.WriteBoolean("referenceHolds", v.ReferenceHolds)
            strings w "referenceViolations" v.ReferenceViolations
            w.WriteStartArray "mutants"
            for m in v.Mutants do
                w.WriteStartObject()
                w.WriteString("name", m.Name)
                w.WriteString("seeds", m.Seeds)
                w.WriteBoolean("killed", m.Killed)
                strings w "violatedChecks" m.ViolatedChecks
                w.WriteEndObject()
            w.WriteEndArray()
            w.WriteNumber("killed", v.Killed)
            w.WriteNumber("total", v.Total)
            w.WriteString("killRate", (if v.Total = 0 then "n/a" else $"{v.Killed}/{v.Total}"))
            w.WriteBoolean("validated", v.Validated)
            w.WriteString("note", v.Note)
            w.WriteEndObject())
