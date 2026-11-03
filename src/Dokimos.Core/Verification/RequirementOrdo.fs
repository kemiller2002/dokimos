namespace Dokimos.Core.Verification

open System
open System.Text.Json
open System.Text.Json.Nodes

/// The requirement verification report in the shapes Ordo already reads: `indy-init.verification-plan/1`
/// and `causa.acceptance-evidence/1` results pinned by a `causa.acceptance-evidence-manifest/1`, so
/// `ordo verification schedule` turns failed requirement IDs into ranked, bounded corrective work.
[<RequireQualifiedAccess>]
module RequirementOrdo =
    let private node (value: string) : JsonNode = JsonValue.Create value |> nonNull :> JsonNode

    let private obj (members: (string * (JsonNode | null)) list) =
        let o = JsonObject()
        members |> List.iter (fun (k, v) -> o.Add(k, v))
        o

    let private arrayOf (items: JsonNode list) =
        let a = JsonArray()
        items |> List.iter a.Add
        a

    let private sha256Of (text: string) =
        text |> Text.Encoding.UTF8.GetBytes |> Security.Cryptography.SHA256.HashData |> Convert.ToHexString |> fun h -> h.ToLowerInvariant()

    let private indentedJson (node: JsonNode) =
        node.ToJsonString(JsonSerializerOptions(WriteIndented = true)).Replace("\r\n", "\n") + "\n"

    /// One `causa.acceptance-evidence/1` result per requirement with a verdict, the evidence shape
    /// `ordo verification schedule --evidence-manifest` already ingests (kemiller2002/ordo
    /// src/Ordo.Core/VerificationPlanWire.fs). PASS, FAIL and BLOCKED only: a NOT_RUN requirement has no
    /// evidence, which is exactly what Ordo schedules as unverified; NOT_APPLICABLE produces none.
    let ordoEvidence (report: VerificationReport) (runId: string) : (string * string) list =
        report.Requirements
        |> List.choose (fun r ->
            match r.Status with
            | Pass -> Some(r, "PASS")
            | Fail -> Some(r, "FAIL")
            | Blocked -> Some(r, "BLOCKED")
            | NotRun
            | NotApplicable -> None)
        |> List.map (fun (r, status) ->
            let body =
                obj
                    [ "schema", node "causa.acceptance-evidence/1"
                      "subject_type", node "requirement"
                      "subject_id", node r.Requirement.Id
                      "status", node status
                      "run_id", node runId
                      "source_sha", node (report.Candidate |> Option.defaultValue report.Manifest.SourceCommit)
                      "input_digest", node report.Manifest.ContentHash
                      "failed_obligations",
                      arrayOf (r.Obligations |> List.filter (fun o -> o.Status <> Pass && o.Status <> NotApplicable) |> List.map (fun o -> node o.Obligation.Id)) ]

            $"{r.Requirement.Id}.json", indentedJson body)

    /// The `causa.acceptance-evidence-manifest/1` pinning each evidence file by SHA-256.
    let ordoEvidenceManifest (files: (string * string) list) : string =
        obj
            [ "schema", node "causa.acceptance-evidence-manifest/1"
              "entries", arrayOf (files |> List.map (fun (path, text) -> obj [ "path", node path; "sha256", node (sha256Of text) ] :> JsonNode)) ]
        |> indentedJson

    /// The manifest's requirements as an `indy-init.verification-plan/1` that `ordo verification schedule`
    /// reads unchanged: one plan requirement per manifest requirement, one plan obligation per manifest
    /// obligation bound to the tests that exercised it (missing when none did), dependencies from the
    /// manifest, per-run freshness (evidence counts only for this run and candidate). A NOT_APPLICABLE
    /// requirement is excluded; an optional one is P1, so it never blocks acceptance.
    let ordoPlan (report: VerificationReport) (repository: string) : string =
        let requirements =
            report.Requirements
            |> List.map (fun r ->
                obj
                    [ "requirement", node r.Requirement.Id
                      "priority", node (if r.Requirement.Required then "P0" else "P1")
                      "title", node r.Requirement.Name
                      "exclusion", obj [ "status", node (if r.Status = NotApplicable then "excluded" else "in-scope") ]
                      "verificationMethod",
                      obj
                          [ "gates", arrayOf (r.Requirement.Obligations |> List.map _.Level |> List.distinct |> List.map node)
                            "rules", arrayOf [ obj [ "id", node "dokimos.requirement-verification/1" ] :> JsonNode ] ]
                      "obligations",
                      arrayOf (
                          r.Obligations
                          |> List.map (fun o ->
                              obj
                                  [ "id", node o.Obligation.Id
                                    "testIdentity",
                                    obj
                                        [ "status", node (if o.Tests.IsEmpty then "missing" else "bound")
                                          "tests", arrayOf (o.Tests |> List.map (fun t -> obj [ "id", node t ] :> JsonNode)) ] ]
                              :> JsonNode)
                      )
                      "dependencies", obj [ "requirements", arrayOf (r.Requirement.DependsOn |> List.map node) ]
                      "freshness", obj [ "policy", node "FP-PROCESS-THIS-RUN" ] ]
                :> JsonNode)

        let body = arrayOf requirements
        let digest = sha256Of (body.ToJsonString())

        obj
            [ "schema", node "indy-init.verification-plan/1"
              "planIdentity", obj [ "planId", node ("VP-" + digest.Substring(0, 12)); "digest", node digest ]
              "source",
              obj
                  [ "repository", node repository
                    "commit", node report.Manifest.SourceCommit
                    "baselineSha256", node report.Manifest.ContentHash ]
              "requirements", body ]
        |> indentedJson
