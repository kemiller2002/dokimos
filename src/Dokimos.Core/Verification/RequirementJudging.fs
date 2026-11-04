namespace Dokimos.Core.Verification

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

/// Turning recorded facts into a verdict for one check, against the manifest's own rules.
/// Pure; every failure carries the reason a person reads.
[<RequireQualifiedAccess>]
module RequirementJudging =
    let Attribute = RequirementEvidence.Attribute

    // ------------------------------------------------------------------ the component contract

    /// The elements that may carry a requirement ID for a component. An ID on any
    /// other element (a hidden span, a wrapper div) does not satisfy the requirement.
    /// Mirrors Forma.Workflow.RequirementIdentity (kemiller2002/forma).
    let componentMatches (comp: string option) (fact: ElementFact) =
        let textTypes = set [ ""; "text"; "search"; "email"; "url"; "tel"; "number"; "date"; "datetime-local"; "password" ]

        match comp with
        | Some "text-input" -> fact.Tag = "input" && textTypes.Contains fact.InputType
        // A URL field (Praxis catalogs use `url-input`, e.g. CAT-CAUSA-INVESTIGATION INV-029). Forma renders it as a text-type
        // input (RequirementIdentity.TextInput accepts type=url); an application may keep type=text so that its own URL
        // rule, not the browser's constraint check, decides. Any other element is not a URL field.
        | Some "url-input" -> fact.Tag = "input" && List.contains fact.InputType [ ""; "text"; "url" ]
        | Some "textarea" -> fact.Tag = "textarea"
        | Some "select" -> fact.Tag = "select"
        | Some "checkbox" -> fact.Tag = "input" && fact.InputType = "checkbox"
        | Some "radio-group" -> fact.Tag = "fieldset" || fact.Role = "radiogroup"
        | Some "button" -> fact.Tag = "button" || (fact.Tag = "input" && (fact.InputType = "submit" || fact.InputType = "button"))
        | Some "link" -> fact.Tag = "a"
        | Some "display" -> not (List.contains fact.Tag [ "input"; "textarea"; "select"; "button"; "script"; "style"; "template" ])
        | Some "region" -> List.contains fact.Tag [ "section"; "form"; "main"; "nav"; "aside"; "dialog"; "div"; "fieldset" ]
        | Some _ -> false
        | None -> true

    // ------------------------------------------------------------------ judging facts

    let private fail reason : Result<unit, RequirementStatus * string> = Error(Fail, reason)
    let private notRun reason : Result<unit, RequirementStatus * string> = Error(NotRun, reason)
    let private ok : Result<unit, RequirementStatus * string> = Ok()

    let private all (checks: (unit -> Result<unit, RequirementStatus * string>) list) =
        checks |> List.fold (fun acc next -> acc |> Result.bind next) ok

    let private describe (fact: ElementFact) =
        if fact.InputType = "" then $"<{fact.Tag}>" else $"<{fact.Tag} type={fact.InputType}>"

    /// V1 exists: exactly the right elements carry the ID in the probed state.
    let private identity (requirement: ManifestRequirement) (probe: Probe) =
        match probe.Elements with
        | None -> notRun "the probe recorded no element facts"
        | Some [] -> fail $"no element carries {Attribute}=\"{requirement.Id}\" in the probed state"
        | Some elements ->
            let componentName = requirement.Component |> Option.defaultValue "component"

            all
                [ fun () ->
                      match elements |> List.tryFind (componentMatches requirement.Component >> not) with
                      | Some wrong -> fail $"{Attribute}=\"{requirement.Id}\" is on {describe wrong}, which is not a {componentName}"
                      | None -> ok
                  fun () ->
                      if not requirement.Repeated && elements.Length > 1 then
                          fail $"ambiguous: {elements.Length} elements carry {Attribute}=\"{requirement.Id}\" in one scope"
                      elif requirement.Repeated then
                          let keys = elements |> List.map _.Instance
                          if keys |> List.exists Option.isNone then fail "a repeated control is missing data-requirement-instance"
                          elif (keys |> List.distinct).Length <> keys.Length then fail "ambiguous: two repeated controls share one instance key"
                          else ok
                      else ok ]

    let private elementsOf (probe: Probe) = probe.Elements |> Option.defaultValue []

    let private visible requirement probe =
        all
            [ fun () -> identity requirement probe
              fun () ->
                  match elementsOf probe |> List.tryFind (fun e -> not e.Visible) with
                  | Some e -> fail $"{describe e} carrying {requirement.Id} is not visible"
                  | None -> ok ]

    let private accessible requirement probe =
        all
            [ fun () -> visible requirement probe
              fun () ->
                  match elementsOf probe |> List.tryFind (fun e -> String.IsNullOrWhiteSpace e.AccessibleName) with
                  | Some e -> fail $"{describe e} carrying {requirement.Id} has no accessible name"
                  | None -> ok
              fun () ->
                  match elementsOf probe |> List.tryFind (fun e -> String.IsNullOrWhiteSpace e.Role) with
                  | Some e -> fail $"{describe e} carrying {requirement.Id} exposes no semantic role"
                  | None -> ok ]

    let private editable requirement probe =
        all
            [ fun () -> visible requirement probe
              fun () ->
                  match elementsOf probe |> List.tryFind (fun e -> e.Disabled || e.ReadOnly) with
                  | Some e -> fail $"""{describe e} carrying {requirement.Id} is {(if e.Disabled then "disabled" else "read-only")}"""
                  | None -> ok ]

    /// Does the manifest's rule set accept this value? `required` makes the requirement
    /// mandatory; whether an empty value is valid is decided by minLength alone (a value
    /// rule), so an empty value is valid unless minLength is at least 1.
    let accepts (requirement: ManifestRequirement) (value: string) =
        let length = value.Trim().Length

        if length = 0 then requirement.MinLength |> Option.forall (fun m -> m < 1)
        else
            (requirement.MinLength |> Option.forall (fun m -> length >= m))
            && (requirement.MaxLength |> Option.forall (fun m -> value.Length <= m))
            && (requirement.Pattern |> Option.forall (fun p -> Regex.IsMatch(value, p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds 1.0)))
            && (requirement.Options.IsEmpty || List.contains value requirement.Options)

    /// The rule violations a requirement defines, each with whether an attempt exercises it.
    let private violations (requirement: ManifestRequirement) : (string * (string -> bool)) list =
        [ match requirement.MinLength with
          | Some m when m >= 1 -> $"minLength {m}", (fun v -> v.Trim().Length < m)
          | _ -> ()
          match requirement.MaxLength with
          | Some m -> $"maxLength {m}", (fun v -> v.Length > m)
          | None -> ()
          match requirement.Pattern with
          | Some p -> $"pattern {p}", (fun v -> not (String.IsNullOrWhiteSpace v) && not (Regex.IsMatch(v, p)))
          | None -> ()
          if not requirement.Options.IsEmpty then "options", (fun v -> not (String.IsNullOrWhiteSpace v) && not (List.contains v requirement.Options)) ]

    let private enter requirement (probe: Probe) =
        all
            [ fun () -> editable requirement probe
              fun () ->
                  let valid = probe.Attempts |> List.filter (fun a -> accepts requirement a.Value && a.Value <> "")
                  if valid.IsEmpty then notRun "the probe entered no valid, non-empty value"
                  else
                      match valid |> List.tryFind (fun a -> a.Observed <> a.Value) with
                      | Some a -> fail $"entered '{a.Value}' but the element then held '{a.Observed}'"
                      | None -> ok ]

    let private select requirement (probe: Probe) =
        all
            [ fun () -> editable requirement probe
              fun () ->
                  match elementsOf probe |> List.tryFind (fun e -> e.Options <> requirement.Options) with
                  | Some e -> fail $"""the control offers [{String.Join(", ", e.Options)}], the requirement [{String.Join(", ", requirement.Options)}]"""
                  | None -> ok
              fun () ->
                  match requirement.Options |> List.tryFind (fun o -> not (probe.Attempts |> List.exists (fun a -> a.Value = o))) with
                  | Some o -> notRun $"the probe never selected '{o}'"
                  | None ->
                      match probe.Attempts |> List.tryFind (fun a -> a.Observed <> a.Value) with
                      | Some a -> fail $"selected '{a.Value}' but the control then held '{a.Observed}'"
                      | None -> ok ]

    let private expectedObserved (what: string) (probe: Probe) =
        match probe.Expected, probe.Observed with
        | Some expected, Some observed when expected = observed -> ok
        | Some expected, Some observed -> fail $"{what}: expected '{expected}', observed '{observed}'"
        | _ -> notRun $"the probe recorded no expected and observed {what}"

    let private activate requirement probe =
        all
            [ fun () -> visible requirement probe
              fun () ->
                  match elementsOf probe |> List.tryFind _.Disabled with
                  | Some e -> fail $"{describe e} carrying {requirement.Id} is disabled"
                  | None -> ok
              fun () -> expectedObserved "outcome of activation" probe ]

    let private validation requirement (probe: Probe) =
        let wrong = probe.Attempts |> List.tryFind (fun a -> a.Accepted <> accepts requirement a.Value)

        match wrong with
        | Some a when a.Accepted -> fail $"the application accepted '{a.Value}', which the requirement's rules reject"
        | Some a -> fail $"the application rejected '{a.Value}', which the requirement's rules accept"
        | None ->
            if not (probe.Attempts |> List.exists (fun a -> a.Accepted)) then notRun "the probe tried no valid value"
            else
                match violations requirement |> List.tryFind (fun (_, hits) -> not (probe.Attempts |> List.exists (fun a -> hits a.Value))) with
                | Some(rule, _) -> notRun $"the probe never exercised rule {rule}"
                | None -> ok

    let private rejects requirement (probe: Probe) =
        match probe.Attempts |> List.filter (fun a -> not (accepts requirement a.Value)) with
        | [] -> notRun "the probe tried no invalid value"
        | invalid ->
            match invalid |> List.tryFind _.Accepted with
            | Some a -> fail $"the application accepted invalid value '{a.Value}'"
            | None ->
                match violations requirement |> List.tryFind (fun (_, hits) -> not (invalid |> List.exists (fun a -> hits a.Value))) with
                | Some(rule, _) -> notRun $"the probe never tried a value violating {rule}"
                | None ->
                    match probe.Expected, probe.Observed with
                    | Some expected, Some observed when expected <> observed -> fail $"rejected input still changed state: expected '{expected}', observed '{observed}'"
                    | _ -> ok

    let judge (requirement: ManifestRequirement) (check: string) (probe: Probe) =
        match check with
        | "exists" -> identity requirement probe
        | "visible" -> visible requirement probe
        | "accessible" -> accessible requirement probe
        | "editable" -> editable requirement probe
        | "enter" -> enter requirement probe
        | "select" -> select requirement probe
        | "activate" -> activate requirement probe
        | "validation" -> validation requirement probe
        | "rejects" -> rejects requirement probe
        | "outcome" -> expectedObserved "outcome" probe
        | "save" -> expectedObserved "persisted value" probe
        | "reload" -> expectedObserved "value after reload" probe
        | "update" -> expectedObserved "updated value after reload" probe
        | "isolation" -> expectedObserved "value of the other entity" probe
        | other -> Error(Blocked, $"Dokimos has no judge for check '{other}'")

