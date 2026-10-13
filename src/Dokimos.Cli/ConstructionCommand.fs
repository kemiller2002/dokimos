namespace Dokimos.Cli

open System
open System.IO
open Dokimos.Core
open Dokimos.Cli.CliInputs

/// `dokimos construction verify`: independent verification of one
/// construction commit. Read-only towards the repository; it executes
/// candidate code only when `--oracle-runner` is supplied.
module ConstructionCommand =
    let private known =
        [ "source-manifest"; "sources"; "blueprint"; "plan"; "expected-plan-digest"; "expected-generator"
          "repository"; "commit"; "base"; "manifest-path"; "reference-manifest"; "oracle"; "expected-oracle-digest"
          "oracle-runner"; "host-observations"; "claims"; "claimed-results"; "authorization"; "work-item"; "at"
          // CONSTRUCT-008 mechanical-first (arm C) inputs; all optional.
          "prepared"; "dispositions"; "decisions"; "prepared-replica"; "generated-commit"; "replica-commit"; "replica-repository" ]

    let private mechanicalOnly =
        [ "dispositions"; "decisions"; "prepared-replica"; "generated-commit"; "replica-commit"; "replica-repository" ]

    let usage =
        "usage: dokimos construction verify --source-manifest F --blueprint F --plan F --expected-plan-digest sha256:H "
        + "--expected-generator V --commit REV --manifest-path P [--repository DIR] [--base REV] [--sources DIR] "
        + "[--reference-manifest F] [--oracle F --expected-oracle-digest sha256:H [--oracle-runner dotnet]] "
        + "[--host-observations F] [--claims F] [--claimed-results TRX]... [--authorization F] [--work-item ID] [--at ISO-8601] "
        + "[--prepared F [--dispositions F] [--decisions F] [--prepared-replica F] [--generated-commit REV] "
        + "[--replica-commit REV [--replica-repository DIR]]] (with --prepared, --plan defaults to the prepared plan)"

    let private readFile name path =
        if File.Exists path then Ok(File.ReadAllText path) else Error(name + " not found: " + path)

    let private optional name parsed reader =
        match Arguments.tryOne name parsed with
        | None -> Ok None
        | Some path -> readFile name path |> Result.bind reader |> Result.map Some

    let private sources parsed =
        match Arguments.tryOne "sources" parsed with
        | None -> Ok None
        | Some dir when Directory.Exists dir ->
            Directory.EnumerateFiles dir
            |> Seq.map (fun p -> string (Path.GetFileName p), File.ReadAllBytes p)
            |> Map.ofSeq |> Some |> Ok
        | Some dir -> Error("sources directory not found: " + dir)

    let private all (results: Result<unit, string> list) =
        match results |> List.choose (function Error e -> Some e | Ok () -> None) with
        | [] -> Ok()
        | errors -> Error(String.concat "; " errors)

    let private claimedResults parsed =
        Arguments.many "claimed-results" parsed
        |> List.map (fun path -> readFile "claimed-results" path |> Result.bind ConstructionEvidence.readTrxClaims)
        |> List.fold (fun acc r -> Result.bind (fun xs -> r |> Result.map (fun x -> xs @ x)) acc) (Ok [])

    let verify args =
        withArgs known args (fun parsed ->
            let req name = Arguments.required name parsed
            let preparedFile = Arguments.tryOne "prepared" parsed
            let planArgument =
                match Arguments.tryOne "plan" parsed, preparedFile with
                | Some plan, _ -> Ok(Some plan)
                | None, Some _ -> Ok None
                | None, None -> req "plan" |> Result.map Some
            let strayMechanical = mechanicalOnly |> List.filter (fun n -> (Arguments.tryOne n parsed).IsSome)
            match req "source-manifest", req "blueprint", planArgument, req "expected-plan-digest", req "expected-generator", req "commit", req "manifest-path" with
            | _ when preparedFile.IsNone && not strayMechanical.IsEmpty ->
                Output.invalid "invalid-arguments" ("--" + String.concat ", --" strayMechanical + " require --prepared")
            | Ok manifestFile, Ok blueprintFile, Ok planFile, Ok expectedPlan, Ok generator, Ok commitRev, Ok manifestPath ->
                let repository = Arguments.tryOne "repository" parsed |> Option.defaultValue "."
                let oracleFile = Arguments.tryOne "oracle" parsed
                let expectedOracle = Arguments.tryOne "expected-oracle-digest" parsed
                match oracleFile, expectedOracle with
                | Some _, None ->
                    Output.invalid "invalid-arguments" "--oracle requires --expected-oracle-digest: an unpinned oracle is not independent evidence"
                | _ ->
                    let at =
                        match Arguments.tryOne "at" parsed with
                        | None -> Ok DateTimeOffset.UtcNow
                        | Some t ->
                            match DateTimeOffset.TryParse(t, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal) with
                            | true, v -> Ok v
                            | _ -> Error("--at is not a timestamp: " + t)
                    let preparedBytes =
                        match preparedFile with
                        | None -> Ok None
                        | Some path when File.Exists path -> Ok(Some(File.ReadAllBytes path))
                        | Some path -> Error("prepared not found: " + path)
                    let planText =
                        match planFile, preparedBytes with
                        | Some path, _ -> readFile "plan" path
                        | None, Ok(Some bytes) ->
                            match MechanicalWire.preparedPlan bytes with
                            | Ok plan -> Ok(plan.GetRawText())
                            | Error errors -> Error("prepared artifact has no usable plan and --plan was not given: " + String.concat "; " errors)
                        | None, Ok None -> Error "--plan is required"
                        | None, Error e -> Error e
                    let files =
                        readFile "source-manifest" manifestFile, readFile "blueprint" blueprintFile, planText,
                        optional "reference-manifest" parsed Ok, optional "host-observations" parsed ConstructionEvidence.readHost,
                        optional "claims" parsed ConstructionEvidence.readClaims, optional "authorization" parsed ConstructionEvidence.readAuthorization
                    match files, sources parsed, claimedResults parsed, at with
                    | (Ok source, Ok blueprint, Ok plan, Ok reference, Ok host, Ok claims, Ok authorization), Ok docs, Ok trx, Ok checkedAt ->
                        let oracleBytes =
                            match oracleFile with
                            | Some path when File.Exists path -> Ok(Some(File.ReadAllBytes path))
                            | Some path -> Error("oracle not found: " + path)
                            | None -> Ok None
                        let gitInputs =
                            ConstructionGit.resolve repository commitRev
                            |> Result.bind (fun commit ->
                                ConstructionGit.candidate repository commit manifestPath
                                |> Result.map (fun candidate ->
                                    let baseRev = Arguments.tryOne "base" parsed |> Option.defaultValue (commit + "^")
                                    let baseline =
                                        ConstructionGit.resolve repository baseRev
                                        |> Result.bind (ConstructionGit.baseSnapshot repository)
                                        |> Result.toOption
                                    candidate, baseline))
                        let mechanical =
                            match preparedBytes with
                            | Error e -> Error e
                            | Ok None -> Ok None
                            | Ok(Some prepared) ->
                                let tree repo rev =
                                    ConstructionGit.resolve repo rev
                                    |> Result.bind (fun c -> ConstructionGit.tree repo c |> Result.map (fun entries -> { Commit = c; Entries = entries }))
                                let optionalTree repo name =
                                    match Arguments.tryOne name parsed with
                                    | None -> Ok None
                                    | Some rev -> tree repo rev |> Result.map Some |> Result.mapError (fun e -> $"--{name} {rev}: {e}")
                                let bytes name =
                                    match Arguments.tryOne name parsed with
                                    | None -> Ok None
                                    | Some path when File.Exists path -> Ok(Some(File.ReadAllBytes path))
                                    | Some path -> Error(name + " not found: " + path)
                                let replicaRepository = Arguments.tryOne "replica-repository" parsed |> Option.defaultValue repository
                                match optional "dispositions" parsed Ok, bytes "decisions", bytes "prepared-replica",
                                      optionalTree repository "generated-commit", optionalTree replicaRepository "replica-commit" with
                                | Ok dispositions, Ok decisions, Ok replicaPrepared, Ok generatedCommit, Ok replica ->
                                    Ok(Some { PreparedBytes = prepared; DispositionsJson = dispositions; DecisionRecordsBytes = decisions
                                              PreparedReplicaBytes = replicaPrepared; GeneratedCommit = generatedCommit; Replica = replica })
                                | Error e, _, _, _, _ | _, Error e, _, _, _ | _, _, Error e, _, _ | _, _, _, Error e, _ | _, _, _, _, Error e -> Error e
                        match oracleBytes, gitInputs, mechanical with
                        | Error e, _, _ -> Output.unavailable "construction-input-unavailable" e
                        | _, Error e, _ -> Output.unavailable "construction-candidate-unavailable" e
                        | _, _, Error e -> Output.unavailable "construction-input-unavailable" e
                        | Ok oracleBytes, Ok(candidate, baseline), Ok mechanical ->
                            let oracleDigest = expectedOracle |> Option.map ConstructionJson.normalizeDigest |> Option.defaultValue ""
                            let run =
                                match oracleBytes |> Option.map ConstructionOracle.read, Arguments.tryOne "oracle-runner" parsed with
                                | None, _ -> NotExecuted "No independent oracle was supplied"
                                | Some(Error e), _ -> NotExecuted("The oracle is unreadable: " + e)
                                | Some(Ok _), None -> NotExecuted "Candidate execution was not requested (--oracle-runner); behavior is unobserved"
                                | Some(Ok oracle), Some runner -> ConstructionOracleRunner.run runner oracle oracle.Digest manifestPath candidate
                            let inputs =
                                { CheckedAt = checkedAt
                                  WorkItem = Arguments.tryOne "work-item" parsed |> Option.defaultValue ""
                                  SourceManifestJson = source
                                  SourceDocuments = docs
                                  BlueprintJson = blueprint
                                  PlanJson = plan
                                  ExpectedPlanDigest = ConstructionJson.normalizeDigest expectedPlan
                                  ExpectedGenerator = generator
                                  ManifestPath = manifestPath
                                  Candidate = candidate
                                  Base = baseline
                                  ReferenceManifestJson = reference
                                  OracleBytes = oracleBytes
                                  ExpectedOracleDigest = oracleDigest
                                  OracleRun = run
                                  Host = host
                                  Claims = claims
                                  ClaimedTests = trx
                                  Authorization = authorization |> Option.defaultValue ConstructionEvidence.emptyAuthorization }
                            match ConstructionVerification.verifyWith mechanical inputs with
                            | Error errors -> Output.unavailable "construction-input-invalid" (String.concat "; " errors)
                            | Ok report ->
                                let baseCommit = baseline |> Option.map _.Commit |> Option.defaultValue ""
                                Output.dataWithCode report.ExitCode (Contracts.serialize (ConstructionWire.toWire inputs baseCommit report))
                    | (a, b, c, d, e, f, g), docs, trx, at ->
                        let errors =
                            [ a |> Result.map ignore; b |> Result.map ignore; c |> Result.map ignore; d |> Result.map ignore
                              e |> Result.map ignore; f |> Result.map ignore; g |> Result.map ignore; docs |> Result.map ignore
                              trx |> Result.map ignore; at |> Result.map ignore ]
                        match all errors with
                        | Error message -> Output.unavailable "construction-input-unavailable" message
                        | Ok() -> Output.invalid "invalid-arguments" usage
            | _ -> Output.invalid "invalid-arguments" usage)
