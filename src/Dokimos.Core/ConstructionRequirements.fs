namespace Dokimos.Core

open System
open System.Text.Json

/// Per original requirement. Each dimension is reported on its own; none is
/// folded into another, and nothing is reduced to a single percentage.
/// Tri-state values are yes, no, unknown or not-applicable; IndependentlyVerified
/// is verified, partial, failed, unknown or not-applicable.
type RequirementStatus =
    { RequirementKey: string
      Disposition: string
      InCohort: bool
      Represented: string
      Modeled: string
      Generated: string
      Implemented: string
      IndependentlyVerified: string
      Deferred: bool
      Unresolved: bool
      Waived: bool
      Artifacts: string list
      Operations: string list
      OracleCases: string list
      OpenObligations: string list
      FindingCodes: string list
      Notes: string }

type CaseResult =
    { Id: string
      RequirementKeys: string list
      Verdict: string
      Detail: string
      Citation: string }

type ReportedFinding =
    { Code: string
      Subject: string
      RequirementKeys: string list
      Detail: string
      /// open, or excepted by unexpired authorization evidence (still listed).
      Disposition: string
      WorkItem: string }

/// Separate counts. Unknown is counted as unknown, never as passed or failed.
type RequirementCounts =
    { Imported: int
      Represented: int
      Modeled: int
      Generated: int
      Implemented: int
      IndependentlyVerified: int
      PartiallyVerified: int
      VerificationFailed: int
      Deferred: int
      Unresolved: int
      Waived: int
      GeneratedUnknown: int
      ImplementedUnknown: int
      VerificationUnknown: int }

[<RequireQualifiedAccess>]
module ConstructionRequirements =
    let private s = ConstructionJson.text

    let statuses (sourceRows: JsonElement list) (blueprintRows: (string * JsonElement) list) (cohortKeys: Set<string>)
                 (plan: PlanModel) (manifest: ConstructionManifest) (ownership: OwnershipResult) (oracle: ConstructionOracle option)
                 (verdicts: Map<string, CaseVerdict>) (observed: bool) (authorization: ConstructionAuthorization)
                 (findings: ReportedFinding list) =
        let keys =
            (sourceRows |> List.map (s "key")) @ (blueprintRows |> List.map fst)
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> List.distinct
        let artifactStatus = ownership.Artifacts |> Map.ofList
        keys
        |> List.map (fun key ->
            let row = blueprintRows |> List.tryFind (fst >> (=) key) |> Option.map snd
            let disposition =
                row |> Option.map (fun r -> ConstructionJson.get "disposition" r |> Option.map (s "kind") |> Option.defaultValue "")
                |> Option.defaultValue "missing"
            let modeled = disposition = "modeled"
            let inCohort = Set.contains key cohortKeys
            let inScope = modeled && inCohort
            let artifacts =
                manifest.Artifacts |> List.filter (fun a -> List.contains key a.RequirementKeys)
                |> List.map (fun a -> ownership.Mount + a.Path)
            let statusesOf = artifacts |> List.map (fun p -> Map.tryFind p artifactStatus |> Option.defaultValue "missing")
            let generated =
                if not inScope then "not-applicable"
                elif artifacts.IsEmpty then "no"
                elif statusesOf |> List.forall ((=) "verified") then "yes"
                else "no"
            let obligations =
                manifest.Obligations
                |> List.filter (fun o -> (o.Kind = "implement" || o.Kind = "resolve") && List.contains key o.RequirementKeys)
                |> List.map _.Id
            let planUnresolved = plan.Unresolved |> List.filter (fun (_, ks) -> List.contains key ks) |> List.map (fun (subject, _) -> "RESOLVE:" + subject)
            let openObligations = obligations @ planUnresolved |> List.distinct |> List.sort
            let implemented =
                match generated with
                | "not-applicable" -> "not-applicable"
                | "no" -> "no"
                | _ when not openObligations.IsEmpty -> "unknown"
                | _ -> "yes"
            let cases = oracle |> Option.map (fun o -> o.Cases |> List.filter (fun c -> List.contains key c.RequirementKeys)) |> Option.defaultValue []
            let coverage =
                oracle |> Option.bind (fun o -> o.Sources |> List.tryFind (fun x -> x.RequirementKey = key))
            let caseVerdicts = cases |> List.map (fun c -> Map.tryFind c.Id verdicts)
            let verified =
                if caseVerdicts |> List.exists (function Some(CaseFailed _) -> true | _ -> false) then "failed"
                elif not inScope then "not-applicable"
                elif cases.IsEmpty || not observed then "unknown"
                elif caseVerdicts |> List.forall ((=) (Some CasePassed)) then
                    match coverage with
                    | Some c when c.Coverage = "full" -> "verified"
                    | _ -> "partial"
                else "unknown"
            let waived =
                authorization.Waivers
                |> List.exists (fun w -> w.RequirementKey = key && not (String.IsNullOrWhiteSpace w.Reason) && not (String.IsNullOrWhiteSpace w.Owner))
            { RequirementKey = key
              Disposition = disposition
              InCohort = inCohort
              Represented = (if row.IsSome then "yes" else "no")
              Modeled = (if modeled then "yes" else "no")
              Generated = generated
              Implemented = implemented
              IndependentlyVerified = verified
              Deferred = (disposition = "deferred")
              Unresolved = (disposition = "unresolved")
              Waived = waived
              Artifacts = artifacts
              Operations = plan.Traces |> List.filter (fun (_, ks) -> List.contains key ks) |> List.map fst |> List.distinct |> List.sort
              OracleCases = cases |> List.map _.Id
              OpenObligations = openObligations
              FindingCodes =
                findings
                |> List.filter (fun x -> x.Disposition = "open" && (List.contains key x.RequirementKeys || x.Subject = key))
                |> List.map _.Code |> List.distinct |> List.sort
              Notes = coverage |> Option.map (fun c -> c.Coverage + (if c.Remainder = "" then "" else ": " + c.Remainder)) |> Option.defaultValue "" })

    let counts (rows: RequirementStatus list) (imported: int) =
        let count predicate = rows |> List.filter predicate |> List.length
        { Imported = imported
          Represented = count (fun r -> r.Represented = "yes")
          Modeled = count (fun r -> r.Modeled = "yes")
          Generated = count (fun r -> r.Generated = "yes")
          Implemented = count (fun r -> r.Implemented = "yes")
          IndependentlyVerified = count (fun r -> r.IndependentlyVerified = "verified")
          PartiallyVerified = count (fun r -> r.IndependentlyVerified = "partial")
          VerificationFailed = count (fun r -> r.IndependentlyVerified = "failed")
          Deferred = count _.Deferred
          Unresolved = count _.Unresolved
          Waived = count _.Waived
          GeneratedUnknown = count (fun r -> r.Generated = "unknown")
          ImplementedUnknown = count (fun r -> r.Implemented = "unknown")
          VerificationUnknown = count (fun r -> r.IndependentlyVerified = "unknown") }
