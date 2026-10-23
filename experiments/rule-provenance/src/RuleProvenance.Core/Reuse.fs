namespace RuleProvenance

/// A2 prototype: a prior audit may be reused only while every assumption in its
/// validity block still holds. Any difference invalidates it, with a distinct reason.
type InvalidationReason =
    | SourceChanged of prior: string * current: string
    | HandoffChanged of prior: string * current: string
    | TraceChanged of prior: string * current: string
    | ToolNameChanged of prior: string * current: string
    | ToolVersionChanged of prior: string * current: string
    | ConfigChanged of prior: string * current: string
    | SdkChanged of prior: string * current: string
    | DependencyChanged of prior: string option * current: string option

type PriorAudit = Validity
type CurrentInputs = Validity

module Reuse =
    let isReusable (prior: PriorAudit) (current: CurrentInputs) : Result<unit, InvalidationReason list> =
        let check same reason = if same then [] else [ reason ]
        let reasons =
            List.concat
                [ check (prior.SourceDigest = current.SourceDigest) (SourceChanged(prior.SourceDigest, current.SourceDigest))
                  check (prior.HandoffDigest = current.HandoffDigest) (HandoffChanged(prior.HandoffDigest, current.HandoffDigest))
                  check (prior.TraceDigest = current.TraceDigest) (TraceChanged(prior.TraceDigest, current.TraceDigest))
                  check (prior.ToolName = current.ToolName) (ToolNameChanged(prior.ToolName, current.ToolName))
                  check (prior.ToolVersion = current.ToolVersion) (ToolVersionChanged(prior.ToolVersion, current.ToolVersion))
                  check (prior.ConfigDigest = current.ConfigDigest) (ConfigChanged(prior.ConfigDigest, current.ConfigDigest))
                  check (prior.SdkVersion = current.SdkVersion) (SdkChanged(prior.SdkVersion, current.SdkVersion))
                  check (prior.DependencyDigest = current.DependencyDigest) (DependencyChanged(prior.DependencyDigest, current.DependencyDigest)) ]
        if reasons.IsEmpty then Ok() else Error reasons

    let code =
        function
        | SourceChanged _ -> "source-changed"
        | HandoffChanged _ -> "handoff-changed"
        | TraceChanged _ -> "trace-changed"
        | ToolNameChanged _ -> "tool-name-changed"
        | ToolVersionChanged _ -> "tool-version-changed"
        | ConfigChanged _ -> "config-changed"
        | SdkChanged _ -> "sdk-version-changed"
        | DependencyChanged _ -> "dependency-changed"

    let describe (r: InvalidationReason) =
        let opt = Option.defaultValue "(none)"
        match r with
        | SourceChanged(p, c)
        | HandoffChanged(p, c)
        | TraceChanged(p, c)
        | ToolNameChanged(p, c)
        | ToolVersionChanged(p, c)
        | ConfigChanged(p, c)
        | SdkChanged(p, c) -> $"{code r}: {p} -> {c}"
        | DependencyChanged(p, c) -> $"{code r}: {opt p} -> {opt c}"

    let toJson (result: Result<unit, InvalidationReason list>) =
        Report.write (fun w ->
            w.WriteStartObject()
            w.WriteString("schema", "dokimos.rule-provenance.reuse-check")
            w.WriteString("schemaVersion", "0.1-experimental")
            match result with
            | Ok() ->
                w.WriteBoolean("reusable", true)
                w.WriteStartArray "invalidatedBy"
                w.WriteEndArray()
            | Error reasons ->
                w.WriteBoolean("reusable", false)
                w.WriteStartArray "invalidatedBy"
                for r in reasons do
                    w.WriteStartObject()
                    w.WriteString("code", code r)
                    w.WriteString("detail", describe r)
                    w.WriteEndObject()
                w.WriteEndArray()
            w.WriteEndObject())
